using System.Text.Json;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeshMonitor.Native;

public sealed partial class MainWindow
{
    private JsonElement radioPermissions;
    private bool radioAdmin;

    private bool HasRadioGrant(SourceRow source, string action) =>
        radioAdmin || radioPermissions.Property(source.Id).Property("configuration").Flag(action) == true;

    private async void OnRadioConfiguration(object sender, RoutedEventArgs args) =>
        await RunAsync(client => ShowRadioConfiguration(client));

    private async void OnRemoteRadio(object sender, RoutedEventArgs args) => await RunAsync(client =>
    {
        if (!radioAdmin || SourcesList.SelectedItem is not SourceRow { Type: "meshtastic_tcp" } source ||
            NodesList.SelectedItem is not NodeRow node || !MeshApi.TryRemoteNodeNumber(node.Id, out _) ||
            (node.SourceId.Length > 0 && node.SourceId != source.Id))
            throw new InvalidOperationException("Sign in as an administrator, select a Meshtastic TCP source, and select the target node.");
        return ShowRadioConfiguration(client, node);
    });

    private async Task ShowRadioConfiguration(MeshApi client, NodeRow? target = null)
    {
        if (SourcesList.SelectedItem is not SourceRow { Type: "meshtastic_tcp" or "meshcore" } source ||
            !HasRadioGrant(source, "read"))
            return;
        bool canWrite = HasRadioGrant(source, "write");
        bool meshCore = source.Type == "meshcore";
        bool remote = target is not null;
        var status = Wrapped("");
        var fields = new StackPanel { Spacing = 8 };
        RadioSection[] sections = remote ? RadioSection.Remote : meshCore
            ? RadioSection.MeshCore.Where(section => section.Key != "ble-pin" || canWrite).ToArray()
            : radioAdmin ? [.. RadioSection.All, RadioSection.FixedPosition, .. RadioSection.Channels()] : RadioSection.All;
        var selector = new ComboBox
        {
            Header = "Section", ItemsSource = sections.Select(section => section.Label).ToArray(), SelectedIndex = 0
        };
        var save = new Button { Content = "Save to radio", IsEnabled = false,
            Visibility = canWrite ? Visibility.Visible : Visibility.Collapsed };
        var refresh = new Button { Content = "Refresh settings" };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(refresh);
        actions.Children.Add(save);
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(Wrapped(source.Name, strong: true));
        if (target is not null) content.Children.Add(Wrapped($"Target radio  {target.Name} ({target.Id})", strong: true));
        content.Children.Add(selector);
        content.Children.Add(fields);
        content.Children.Add(actions);
        content.Children.Add(status);
        if (remote) content.Children.Add(Wrapped("Queued saves continue on the backend if you close this window."));
        var dialog = DetailDialog(remote ? "Remote radio administration" : "Radio configuration",
            new ScrollViewer { Content = content, MaxHeight = 430 });

        JsonElement snapshot = default;
        var inputs = new Dictionary<RadioField, Control>();
        bool showing = true;
        using var stopWatching = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var trackingToken = stopWatching.Token;
        bool working = false;
        bool online = false;
        RadioSection Section() => sections[Math.Max(0, selector.SelectedIndex)];
        Task<JsonElement> Read(RadioSection section) => target is not null
            ? client.ReadRemoteRadio(source, target.Id, section)
            : meshCore
            ? client.ReadMeshCoreRadioSection(source, section)
            : client.ReadRadioSection(source, section, radioAdmin);
        bool Editable(RadioSection section) => canWrite && online && !section.ReadOnly &&
            (section.AdminCommand is null || radioAdmin);
        bool EditableField(RadioSection section, RadioField field) => Editable(section) && section.CanSave(snapshot) && !field.ReadOnly &&
            (field.Key != "useAead" || snapshot.Property("supportedModules").Flag("aeadChannels") != false);

        void Display()
        {
            fields.Children.Clear();
            inputs.Clear();
            var section = Section();
            var current = section.Settings(snapshot);
            save.IsEnabled = !working && Editable(section) && section.CanSave(snapshot);
            if (current.ValueKind != JsonValueKind.Object)
            {
                fields.Children.Add(Wrapped("No settings for this section have been received."));
                return;
            }
            foreach (var field in section.Fields)
            {
                object value = RadioSection.Read(current, field);
                string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                Control input;
                if (field.Kind == "enum" && field.Options is { } options)
                {
                    var choices = options.Select(option => new ComboBoxItem
                        { Tag = option.Value, Content = option.Key.Replace('_', ' ') }).ToList();
                    var selected = choices.FirstOrDefault(choice => Equals(choice.Tag, value));
                    if (selected is null)
                    {
                        selected = new ComboBoxItem { Tag = value, Content = $"Current value ({value})" };
                        choices.Add(selected);
                    }
                    input = new ComboBox { Header = field.Label, ItemsSource = choices, SelectedItem = selected };
                }
                else if (field.Choices is { } choices)
                    input = new ComboBox { Header = field.Label, ItemsSource = choices,
                        SelectedItem = choices.Contains(text) ? text : null };
                else if (value is bool enabled)
                    input = new ToggleSwitch { Header = field.Label, IsOn = enabled };
                else if (field.Secret)
                    input = new PasswordBox { Header = field.Label, Password = text, PasswordRevealMode = PasswordRevealMode.Hidden };
                else
                    input = new TextBox { Header = field.Label, Text = text, MaxLength = field.Maximum,
                        AcceptsReturn = field.Kind == "json", TextWrapping = TextWrapping.Wrap };
                input.IsEnabled = EditableField(section, field);
                inputs[field] = input;
                fields.Children.Add(input);
                string? hint = (section.Key, field.Key) switch
                {
                    ("device", "isManaged" or "serialEnabled") => "Configure this setting under Security.",
                    ("position", "gpsEnabled") => "Configure GPS using GPS mode.",
                    ("position", "gpsAttemptTime") => "Firmware uses the smart and regular broadcast intervals.",
                    ("lora", "transparentMode") => "The backend preserves the mode reported by the radio.",
                    ("lora", "serialHalOnly") => "Enabling this switches the radio from its onboard LoRa driver to a serial HAL connection.",
                    _ => null
                };
                if (hint is not null) fields.Children.Add(Wrapped(hint));
            }
        }

        void SetWorking(bool value)
        {
            working = value;
            selector.IsEnabled = refresh.IsEnabled = !value;
            foreach (var (field, input) in inputs) input.IsEnabled = !value && EditableField(Section(), field);
            save.IsEnabled = !value && Editable(Section()) && Section().CanSave(snapshot);
        }

        async Task Load(bool request = false)
        {
            if (working) return;
            SetWorking(true);
            try
            {
                if (!meshCore) online = (await client.SourceStatus(source)).Flag("connected") == true;
                if (!remote && !meshCore && request && online && canWrite && Section().RequestType is int configType)
                {
                    await client.RequestRadioConfiguration(source, configType, Section().Module);
                    await Task.Delay(1500, lifetime.Token);
                }
                if (remote && !online)
                    throw new InvalidOperationException("Connect the local radio to reach the remote node.");
                snapshot = await Read(Section());
                if (meshCore) online = snapshot.Flag("connected") == true;
                if (!showing || closed) return;
                status.Text = !online ? "Connect the source before saving radio settings."
                    : Section().Key == "owner" && !Section().CanSave(snapshot)
                        ? "The backend has yet to provide the owner flags required for a save."
                        : remote && Section().ReadOnly ? "This section is read-only in remote administration."
                        : remote ? "Settings received from the selected radio." : "Showing the latest settings available to the backend.";
            }
            catch (Exception error)
            {
                if (!showing || closed) return;
                online = false;
                status.Text = error.Message;
            }
            finally
            {
                if (showing && !closed)
                {
                    SetWorking(false);
                    Display();
                }
            }
        }

        selector.SelectionChanged += async (_, _) =>
        {
            if (remote || meshCore || Section().Key is "owner" or "fixed-position" || Section().ChannelIndex is not null ||
                snapshot.ValueKind != JsonValueKind.Object)
            {
                snapshot = default;
                Display();
                await Load();
                return;
            }
            Display();
            status.Text = Section().ReadOnly ? "This section can be viewed. The backend has no save operation for it."
                : Section().AdminCommand is not null && !radioAdmin ? "Saving this section requires administrator access." : "";
        };
        refresh.Click += async (_, _) => await Load(request: true);
        save.Click += async (_, _) =>
        {
            var section = Section();
            if (working || !Editable(section)) return;
            var changes = new Dictionary<string, object>();
            foreach (var (field, input) in inputs)
            {
                if (!EditableField(section, field)) continue;
                object value;
                if (input is ToggleSwitch toggle) value = toggle.IsOn;
                else if (input is ComboBox choice)
                {
                    if (choice.SelectedItem is null) continue;
                    value = choice.SelectedItem is ComboBoxItem item ? item.Tag : choice.SelectedItem;
                }
                else
                {
                    string text = input is PasswordBox credential ? credential.Password : ((TextBox)input).Text;
                    if (Equals(text, RadioSection.Read(section.Settings(snapshot), field))) continue;
                    if (field.Kind is "" or "string" or "bytes")
                    {
                        if ((section.Key is "owner" or "name" && string.IsNullOrWhiteSpace(text)) ||
                            (field.Maximum > 0 && System.Text.Encoding.UTF8.GetByteCount(text) > field.Maximum))
                        {
                            status.Text = $"Enter a value for {field.Label} of up to {field.Maximum} UTF-8 bytes.";
                            return;
                        }
                        value = text;
                        if (section.ChannelIndex is not null && field.Key == "psk")
                        {
                            byte[] key = new byte[32];
                            if (text.Length == 0) value = "AA==";
                            else if (Convert.TryFromBase64String(text, key, out int length) && length is 1 or 16 or 32)
                                value = Convert.ToBase64String(key, 0, length);
                            else
                            {
                                status.Text = "Enter a Base64 key of 1, 16, or 32 bytes. Clear the field to turn off encryption.";
                                return;
                            }
                        }
                    }
                    else if (!TryParseRadioValue(field, text, out value))
                    {
                        status.Text = $"Enter a valid value for {field.Label}.";
                        return;
                    }
                }
                if (!Equals(RadioSection.ReadValue(JsonSerializer.SerializeToElement(value), field),
                    RadioSection.Read(section.Settings(snapshot), field))) changes[field.Key] = value;
            }
            if (changes.Count == 0)
            {
                status.Text = "No changes to save.";
                return;
            }
            SetWorking(true);
            status.Text = "Sending settings to the radio…";
            bool sent = false;
            try
            {
                var current = await Read(section);
                if (meshCore && current.Flag("connected") != true)
                    throw new InvalidOperationException("Connect the source before saving radio settings.");
                var payload = section.Prepare(snapshot, current, changes);
                if (!showing || closed) return;
                if (target is not null)
                {
                    var operation = await client.StartRemoteRadioSave(source, target.Id, section, payload);
                    sent = true;
                    snapshot = default;
                    status.Text = "Remote save queued…";
                    await client.TrackRemoteRadioSave(operation,
                        message => { if (showing && !closed) status.Text = message; }, trackingToken);
                    if (showing && !closed)
                    {
                        snapshot = default;
                        Display();
                    }
                    return;
                }
                await client.SaveRadioConfiguration(source, section, payload);
                sent = true;
                if (section.Module)
                {
                    string message = "The radio confirmed the status message.";
                    if (section.Key != "statusmessage")
                    {
                        var reply = await client.ReadModuleFromRadio(source, section.RequestType!.Value, RadioSection.NodeId(snapshot)!);
                        var live = reply.Property("section");
                        if (live.ValueKind != JsonValueKind.Object || reply.String("nodeId") != RadioSection.NodeId(snapshot))
                            throw new InvalidOperationException("The selected radio did not return this section.");
                        var mismatches = section.Fields.Where(field => changes.TryGetValue(field.Key, out var expected) &&
                            !Equals(RadioSection.ReadValue(JsonSerializer.SerializeToElement(expected), field),
                                RadioSection.Read(live, field))).Select(field => field.Label).ToArray();
                        message = mismatches.Length == 0 ? "The radio confirmed the requested module settings."
                            : $"The radio reported different values for {string.Join(", ", mismatches)}.";
                    }
                    if (showing && !closed)
                    {
                        await SetModuleResult(message);
                    }
                    return;
                }
                if (section.RequestType is int configType && showing && !closed)
                    await client.RequestRadioConfiguration(source, configType, section.Module);
                if (!showing || closed) return;
                await Task.Delay(1500, lifetime.Token);
                if (!showing || closed) return;
                var next = await Read(section);
                if (!showing || closed) return;
                if (RadioSection.NodeId(next) != RadioSection.NodeId(snapshot))
                {
                    online = false;
                    status.Text = "Settings were sent. Refresh after the radio reconnects to check them.";
                    return;
                }
                var returned = section.Settings(next);
                var differences = section.Fields.Where(field => changes.TryGetValue(field.Key, out var expected) &&
                    !Equals(RadioSection.ReadValue(JsonSerializer.SerializeToElement(expected), field),
                        RadioSection.Read(returned, field))).Select(field => field.Label).ToArray();
                snapshot = next;
                status.Text = section.Key == "ble-pin"
                    ? "Bluetooth PIN updated. The new PIN takes effect after the radio restarts."
                    : returned.ValueKind != JsonValueKind.Object
                    ? "Settings were sent. The backend has no values to compare yet."
                    : differences.Length == 0
                        ? "Settings were sent. The backend shows the requested values. Radio confirmation is unavailable."
                        : $"Settings were sent. The backend shows different values for {string.Join(", ", differences)}.";
                Display();
            }
            catch (Exception error)
            {
                if (showing && !closed)
                    status.Text = sent ? $"Settings were sent. Readback failed. {error.Message}" : error.Message;
            }
            finally
            {
                if (showing && !closed) SetWorking(false);
            }
        };

        async Task SetModuleResult(string message)
        {
            try { snapshot = await Read(Section()); }
            catch (Exception error) { message += $" Reload failed. {error.Message}"; }
            if (!showing || closed) return;
            Display();
            status.Text = message;
        }

        var opened = dialog.ShowAsync();
        _ = Load();
        try { await opened; }
        finally { showing = false; stopWatching.Cancel(); }
    }

    private static bool TryParseRadioValue(RadioField field, string text, out object value)
    {
        value = "";
        if (field.Kind == "json")
        {
            try
            {
                using var parsed = JsonDocument.Parse(text);
                var expected = Equals(field.Default, "[]") ? JsonValueKind.Array : JsonValueKind.Object;
                if (parsed.RootElement.ValueKind != expected) return false;
                value = parsed.RootElement.Clone();
                return true;
            }
            catch (JsonException) { return false; }
        }
        if (field.Kind is "float" or "double")
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ||
                !double.IsFinite(number) || field.Kind == "float" && Math.Abs(number) > float.MaxValue) return false;
            if ((field.Key is "latitude" or "lat") && Math.Abs(number) > 90 ||
                (field.Key is "longitude" or "lon") && Math.Abs(number) > 180) return false;
            value = number;
            return true;
        }
        if (field.Kind is "uint64" or "fixed64")
        {
            if (!ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong number)) return false;
            value = number.ToString(CultureInfo.InvariantCulture);
            return true;
        }
        bool signed = field.Kind is "int32" or "sint32" or "sfixed32";
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer) ||
            integer < (signed ? int.MinValue : 0L) || integer > (signed ? int.MaxValue : (long)uint.MaxValue))
            return false;
        int floor = field.Key switch { "nodeInfoBroadcastSecs" => 3600, "positionBroadcastSecs" => 32, _ => 0 };
        if (integer != 0 && floor > 0 && integer < floor || field.Key == "hopLimit" && integer > 7 ||
            field.Key == "positionPrecision" && integer > 32) return false;
        value = integer;
        return true;
    }

}
