using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeshMonitor.Native;

public sealed partial class MainWindow
{
    private bool canReadSourceSettings;
    private bool canWriteSources;

    private async void OnEditSource(object sender, RoutedEventArgs args) => await RunAsync(async client =>
    {
        if (!canWriteSources || SourcesList.SelectedItem is not SourceRow { IsUnified: false } selected) return;
        bool showEndpoint = canReadSourceSettings && selected.Type == "meshtastic_tcp";
        var settings = canReadSourceSettings ? await client.SourceConfiguration(selected) : default;
        if (closed) return;
        var source = settings.ValueKind == JsonValueKind.Object ? SourceRow.From(settings) : selected;
        var endpoint = settings.Property("config");
        string originalHost = endpoint.String("host") ?? "";
        int originalPort = ReadTcpPort(endpoint) ?? 4403;
        var name = new TextBox { Header = "Source name", Text = source.Name };
        var enabled = new ToggleSwitch { Header = "Source enabled", IsOn = source.Enabled };
        var host = new TextBox { Header = "Radio hostname or IP address", Text = originalHost };
        var port = new TextBox { Header = "TCP port", Text = originalPort.ToString() };
        var error = DialogError();
        var dialog = showEndpoint
            ? Form("Edit source", "Save", name, enabled, host, port, error)
            : Form("Edit source", "Save", name, enabled, error);
        int portNumber = originalPort;
        dialog.PrimaryButtonClick += (_, action) =>
        {
            bool validName = !string.IsNullOrWhiteSpace(name.Text);
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
        status.Flag("connected") == true ? "Connected" : source.Enabled ? "Disconnected" : "Disabled";

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
}
