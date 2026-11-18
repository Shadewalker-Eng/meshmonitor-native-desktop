using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeshMonitor.Native;

public sealed partial class MainWindow
{
    private bool canReadSourceSettings;
    private bool canWriteSources;

    private async void OnAddSource(object sender, RoutedEventArgs args) => await RunAsync(async client =>
    {
        if (!canWriteSources) return;
        var name = new TextBox { Header = "Source name" };
        var protocol = new ComboBox { Header = "Source type", ItemsSource = new[]
            { "Meshtastic TCP", "MeshCore", "Meshtastic MQTT bridge", "MeshCore MQTT",
                "Embedded Meshtastic MQTT broker", "Embedded MeshCore MQTT broker" }, SelectedIndex = 0 };
        var host = new TextBox { Header = "Radio hostname or IP address" };
        var port = new TextBox { Header = "TCP port", Text = "4403" };
        var meshtastic = new StackPanel { Spacing = 12 };
        meshtastic.Children.Add(host);
        meshtastic.Children.Add(port);
        var meshCore = new MeshCoreFields();
        var bridge = new MqttFields(bridge: true, sources: SourcesList.ItemsSource as SourceRow[]);
        var ingest = new MqttFields(bridge: false);
        var meshtasticBroker = new BrokerFields(meshCore: false);
        var meshCoreBroker = new BrokerFields(meshCore: true);
        var panels = new[] { meshtastic, meshCore.View, bridge.View, ingest.View, meshtasticBroker.View, meshCoreBroker.View };
        for (int i = 1; i < panels.Length; i++) panels[i].Visibility = Visibility.Collapsed;
        protocol.SelectionChanged += (_, _) =>
        {
            for (int i = 0; i < panels.Length; i++)
                panels[i].Visibility = protocol.SelectedIndex == i ? Visibility.Visible : Visibility.Collapsed;
        };
        var error = DialogError();
        var dialog = Form("Add source", "Add source", [name, protocol, .. panels, error]);
        int portNumber = 0;
        MeshCoreConnection connection = MeshCoreConnection.New;
        MqttSourceSettings? mqtt = null;
        EmbeddedBrokerSettings? embedded = null;
        dialog.PrimaryButtonClick += (_, action) =>
        {
            error.Text = string.IsNullOrWhiteSpace(name.Text) ? "Enter a source name." : protocol.SelectedIndex switch
            {
                1 => meshCore.Read(out connection),
                2 => bridge.Read(out mqtt),
                3 => ingest.Read(out mqtt),
                4 => meshtasticBroker.Read(out embedded),
                5 => meshCoreBroker.Read(out embedded),
                _ => TryTcpEndpoint(host.Text, port.Text, out portNumber) ? "" : "Enter a valid hostname or IP address and TCP port."
            };
            action.Cancel = error.Text.Length > 0;
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || closed) return;
        string label = name.Text.Trim();
        var created = protocol.SelectedIndex switch
        {
            1 => await client.AddMeshCore(label, connection),
            2 or 3 when mqtt is not null => await client.AddMqttSource(label, mqtt.Bridge,
                mqtt.Apply(mqtt.Bridge ? bridge.Password : ingest.Password), !mqtt.Bridge || bridge.Enabled),
            4 or 5 when embedded is not null => await client.AddEmbeddedBroker(label, embedded,
                embedded.MeshCore ? meshCoreBroker.Password : meshtasticBroker.Password, enabled: true),
            _ => await client.AddMeshtastic(label, NormalizeHost(host.Text), portNumber)
        };
        await LoadSourcesAsync(created.String("id") ?? created.Property("data").String("id"));
    });

    private async void OnEditSource(object sender, RoutedEventArgs args) => await RunAsync(async client =>
    {
        if (!canWriteSources || SourcesList.SelectedItem is not SourceRow { IsUnified: false } selected) return;
        bool showEndpoint = canReadSourceSettings && selected.Type == "meshtastic_tcp";
        var settings = canReadSourceSettings ? await client.SourceConfiguration(selected) : default;
        if (closed) return;
        var source = settings.ValueKind == JsonValueKind.Object ? SourceRow.From(settings) : selected;
        var endpoint = settings.Property("config");
        var meshCore = canReadSourceSettings && source.Type == "meshcore" && endpoint.ValueKind == JsonValueKind.Object
            ? new MeshCoreFields(MeshCoreConnection.From(endpoint)) : null;
        var mqtt = canReadSourceSettings && (source.Type is "mqtt_bridge" or "meshcore_mqtt") &&
            endpoint.ValueKind == JsonValueKind.Object
            ? new MqttFields(source.Type == "mqtt_bridge", endpoint, SourcesList.ItemsSource as SourceRow[]) : null;
        var broker = canReadSourceSettings && source.IsEmbeddedBroker && endpoint.ValueKind == JsonValueKind.Object
            ? new BrokerFields(source.IsMeshCore, endpoint) : null;
        string originalHost = endpoint.String("host") ?? "";
        int originalPort = ReadTcpPort(endpoint) ?? 4403;
        var name = new TextBox { Header = "Source name", Text = source.Name };
        var enabled = new ToggleSwitch { Header = "Source enabled", IsOn = source.Enabled };
        var host = new TextBox { Header = "Radio hostname or IP address", Text = originalHost };
        var port = new TextBox { Header = "TCP port", Text = originalPort.ToString() };
        var error = DialogError();
        FrameworkElement[] fields = meshCore is not null ? [meshCore.View] : mqtt is not null ? [mqtt.View] :
            broker is not null ? [broker.View] :
            showEndpoint ? [host, port] : [];
        var dialog = Form("Edit source", "Save", [name, enabled, .. fields, error]);
        int portNumber = originalPort;
        MeshCoreConnection? connection = null;
        MqttSourceSettings? mqttSettings = null;
        EmbeddedBrokerSettings? brokerSettings = null;
        dialog.PrimaryButtonClick += (_, action) =>
        {
            bool validName = !string.IsNullOrWhiteSpace(name.Text);
            if (validName && meshCore is not null)
            {
                error.Text = meshCore.Read(out var edited);
                connection = edited;
                action.Cancel = error.Text.Length > 0;
                return;
            }
            if (validName && mqtt is not null)
            {
                error.Text = mqtt.Read(out mqttSettings);
                action.Cancel = error.Text.Length > 0;
                return;
            }
            if (validName && broker is not null)
            {
                error.Text = broker.Read(out brokerSettings);
                action.Cancel = error.Text.Length > 0;
                return;
            }
            bool endpointUntouched = NormalizeHost(host.Text) == NormalizeHost(originalHost) &&
                port.Text == originalPort.ToString();
            bool validEndpoint = !showEndpoint || endpointUntouched || TryTcpEndpoint(host.Text, port.Text, out portNumber);
            if (validName && validEndpoint) return;
            action.Cancel = true;
            error.Text = validName ? "Enter a valid hostname or IP address and TCP port." : "Enter a source name.";
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || closed) return;

        var changes = new Dictionary<string, object>();
        if (name.Text.Trim() != source.Name) changes["name"] = name.Text.Trim();
        if (enabled.IsOn != source.Enabled) changes["enabled"] = enabled.IsOn;
        if (brokerSettings is not null && broker is not null &&
            (brokerSettings != broker.Initial || broker.Password.Length > 0))
        {
            var latest = (await client.SourceConfiguration(source)).Property("config");
            if (latest.ValueKind != JsonValueKind.Object || EmbeddedBrokerSettings.From(source.IsMeshCore, latest) != broker.Initial)
                throw new InvalidOperationException("The broker settings changed while you were editing. Reopen the editor to try again.");
            changes["config"] = brokerSettings.Apply(source.Name, broker.Password, latest);
        }
        if (mqttSettings is not null && mqtt is not null &&
            (mqttSettings != mqtt.Initial || mqtt.Password.Length > 0))
        {
            var latest = (await client.SourceConfiguration(source)).Property("config");
            if (latest.ValueKind != JsonValueKind.Object || MqttSourceSettings.From(mqttSettings.Bridge, latest) != mqtt.Initial)
                throw new InvalidOperationException("The MQTT settings changed while you were editing. Reopen the editor to try again.");
            changes["config"] = mqttSettings.Apply(mqtt.Password, latest);
        }
        if (connection is not null && connection != MeshCoreConnection.From(endpoint))
        {
            var latest = (await client.SourceConfiguration(source)).Property("config");
            if (latest.ValueKind != JsonValueKind.Object || MeshCoreConnection.From(latest) != MeshCoreConnection.From(endpoint))
                throw new InvalidOperationException("The MeshCore connection settings changed while you were editing. Reopen the editor to try again.");
            changes["config"] = connection.Apply(latest);
        }
        string updatedHost = NormalizeHost(host.Text);
        if (showEndpoint && (updatedHost != NormalizeHost(originalHost) || portNumber != originalPort))
        {
            var latest = (await client.SourceConfiguration(source)).Property("config");
            if (latest.ValueKind != JsonValueKind.Object ||
                (latest.String("host") ?? "") != originalHost || (ReadTcpPort(latest) ?? 4403) != originalPort)
                throw new InvalidOperationException("The source endpoint changed while you were editing. Reopen the editor to try again.");
            var config = JsonNode.Parse(latest.GetRawText())!.AsObject();
            config["host"] = updatedHost;
            config["port"] = portNumber;
            changes["config"] = config;
        }
        if (changes.Count == 0 || closed) return;
        await client.UpdateSource(source, changes);
        await LoadSourcesAsync(source.Id);
        ShowNotice("Source settings saved.", InfoBarSeverity.Success);
    });

    private async void OnConnectionDiagnostics(object sender, RoutedEventArgs args) => await RunAsync(async client =>
    {
        if (SourcesList.SelectedItem is not SourceRow { IsUnified: false } source) return;
        var details = Wrapped("");
        var refresh = new Button { Content = "Refresh status" };
        var logs = new Button { Content = "Open logs folder" };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(refresh);
        if (canReadSourceSettings) actions.Children.Add(logs);
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(Wrapped($"{source.Name} · {source.Subtitle}", strong: true));
        content.Children.Add(details);
        content.Children.Add(actions);

        bool showing = true;
        async Task RefreshStatus()
        {
            if (!refresh.IsEnabled) return;
            refresh.IsEnabled = false;
            details.Text = "Reading source status…";
            try
            {
                var status = await client.SourceStatus(source);
                if (!showing || closed) return;
                var lines = new List<string> { ConnectionState(source, status) };
                if (status.String("nodeId") is { Length: > 0 } id) lines.Add($"Local node  {id}");
                if (status.Number("nodeCount") is { } count) lines.Add($"Known nodes  {count:N0}");
                if (status.Number("activeNodeCount") is { } active) lines.Add($"Heard in the last 2 hours  {active:N0}");
                if (source.IsEmbeddedBroker && status.Number("clientCount") is { } clients)
                    lines.Add($"MQTT clients  {clients:N0}");
                if (source.Type == "mqtt_bridge" && status.String("mode") is { } mode)
                {
                    lines.Add(mode switch
                    {
                        "subscribe_only" => "Receive only",
                        "publish_only" => "Send only",
                        _ => "Send and receive"
                    });
                    if (status.Flag("parentBrokerAttached") == true) lines.Add("Local broker attached");
                    if (status.Number("downlinkIngested") is { } received) lines.Add($"Messages ingested  {received:N0}");
                    if (status.Number("uplinkOut") is { } sent) lines.Add($"Messages sent upstream  {sent:N0}");
                }
                if (status.Flag("mqttProxyUnlinked") == true) lines.Add("The MQTT client proxy has no linked source.");
                if ((status.String("lastError") ?? status.String("error")) is { Length: > 0 } issue)
                    lines.Add(issue);
                lines.Add($"Checked {ValueFormat.Time(DateTimeOffset.Now)}");
                details.Text = string.Join(Environment.NewLine, lines);
            }
            catch (Exception error)
            {
                if (showing && !closed) details.Text = error.Message;
            }
            finally { refresh.IsEnabled = true; }
        }

        refresh.Click += async (_, _) => await RefreshStatus();
        logs.Click += async (_, _) => await OpenFolderAsync(logs: true);
        var opened = DetailDialog("Connection diagnostics", content).ShowAsync();
        _ = RefreshStatus();
        try { await opened; }
        finally { showing = false; }
    });

    private static string ConnectionState(SourceRow source, JsonElement status) =>
        status.Flag("connected") == true ? (source.IsEmbeddedBroker ? "Listening" : "Connected") : !source.Enabled ? "Disabled" :
        status.Flag("authStopped") == true ? "Broker authentication stopped" : "Disconnected";

    private static string NormalizeHost(string host) => host.Trim().Trim('[', ']');

    private static bool TryTcpEndpoint(string host, string port, out int portNumber) =>
        int.TryParse(port, out portNumber) && portNumber is > 0 and <= 65535 &&
        Uri.CheckHostName(NormalizeHost(host)) != UriHostNameType.Unknown;

    private static int? ReadTcpPort(JsonElement config)
    {
        if (config.Number("port") is { } number && number is > 0 and <= 65535 && number == Math.Truncate(number))
            return (int)number;
        return int.TryParse(config.String("port"), out int parsed) && parsed is > 0 and <= 65535 ? parsed : null;
    }

    private sealed class MeshCoreFields
    {
        private readonly MeshCoreConnection initial;
        private readonly bool adding;
        private readonly ComboBox transport = new() { Header = "Transport", ItemsSource = new[] { "USB / serial", "TCP" } };
        private readonly ComboBox device = new() { Header = "Device type", ItemsSource = new[] { "Companion", "Repeater" } };
        private readonly TextBox host = new() { Header = "Radio hostname or IP address" };
        private readonly TextBox port = new() { Header = "TCP port" };
        private readonly TextBox serial = new() { Header = "Windows COM port", PlaceholderText = "COM3" };
        private readonly ToggleSwitch autoConnect = new() { Header = "Connect automatically" };
        public StackPanel View { get; } = new() { Spacing = 12 };

        internal MeshCoreFields(MeshCoreConnection? settings = null)
        {
            adding = settings is null;
            initial = settings ?? MeshCoreConnection.New;
            transport.SelectedIndex = initial.Tcp ? 1 : 0;
            device.SelectedIndex = initial.Repeater ? 1 : 0;
            host.Text = initial.Host;
            port.Text = (initial.Tcp || initial.Host.Length > 0 ? initial.Port : 5000).ToString();
            serial.Text = initial.SerialPort;
            autoConnect.IsOn = initial.AutoConnect;
            foreach (var control in new Control[] { transport, device, host, port, serial, autoConnect })
                View.Children.Add(control);
            transport.SelectionChanged += (_, _) => ShowEndpointFields();
            ShowEndpointFields();
        }

        internal string Read(out MeshCoreConnection settings)
        {
            bool tcp = transport.SelectedIndex == 1;
            settings = initial with { Tcp = tcp, Repeater = device.SelectedIndex == 1, AutoConnect = autoConnect.IsOn };
            if (tcp)
            {
                if (!adding && initial.Tcp && host.Text == initial.Host && port.Text == initial.Port.ToString()) return "";
                if (!TryTcpEndpoint(host.Text, port.Text, out int number))
                    return "Enter a valid hostname or IP address and TCP port.";
                settings = settings with { Host = NormalizeHost(host.Text), Port = number };
                return "";
            }

            string path = serial.Text.Trim().ToUpperInvariant();
            if (!adding && !initial.Tcp && serial.Text == initial.SerialPort) return "";
            if (!path.StartsWith("COM", StringComparison.Ordinal) ||
                !int.TryParse(path.AsSpan(3), System.Globalization.NumberStyles.None, null, out int index) || index <= 0)
                return "Enter a Windows COM port such as COM3.";
            settings = settings with { SerialPort = $"COM{index}" };
            return "";
        }

        private void ShowEndpointFields()
        {
            bool tcp = transport.SelectedIndex == 1;
            host.Visibility = port.Visibility = tcp ? Visibility.Visible : Visibility.Collapsed;
            serial.Visibility = tcp ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
