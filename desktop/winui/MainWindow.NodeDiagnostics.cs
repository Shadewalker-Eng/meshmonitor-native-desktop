using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeshMonitor.Native;

public sealed partial class MainWindow
{
    private async void OnNodeDiagnostics(object sender, RoutedEventArgs args) => await RunAsync(async client =>
    {
        if (SourcesList.SelectedItem is not SourceRow { Type: "meshtastic_tcp" } source ||
            NodesList.SelectedItem is not NodeRow node ||
            (!string.IsNullOrEmpty(node.SourceId) && node.SourceId != source.Id)) return;
        if (!MeshApi.TryRemoteNodeNumber(node.Id, out uint number) &&
            (!uint.TryParse(node.Id, out number) || number is 0 or uint.MaxValue))
            throw new InvalidOperationException("This node has no valid Meshtastic address.");

        var picker = new ComboBox { Header = "View", ItemsSource = new[] { "Name history", "Neighbors", "Traceroute history" }, SelectedIndex = 0 };
        var refresh = new Button { Content = "Refresh records" };
        var request = new Button();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(refresh);
        buttons.Children.Add(request);
        var status = Wrapped("");
        var records = new StackPanel { Spacing = 12 };
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(Wrapped($"{node.Name} ({node.Id}) · {source.Name}", strong: true));
        body.Children.Add(picker);
        body.Children.Add(buttons);
        body.Children.Add(status);
        body.Children.Add(new ScrollViewer { Content = records, MaxHeight = 250 });

        var dialog = DetailDialog("Node diagnostics", body);
        NodeRecord? chosenRoute = null;
        bool open = true;
        bool busy = false;
        bool canSend = radioAdmin || radioPermissions.Property(source.Id).Property("traceroute").Flag("write") == true;
        async Task Load(bool send = false)
        {
            if (!open || busy || closed) return;
            busy = true;
            var report = (NodeReport)picker.SelectedIndex;
            request.Visibility = canSend && report != NodeReport.Names ? Visibility.Visible : Visibility.Collapsed;
            request.Content = report == NodeReport.Neighbors ? "Request neighbor report" : "Send traceroute";
            picker.IsEnabled = refresh.IsEnabled = request.IsEnabled = false;
            status.Text = send ? "Sending request…" : "Loading records…";
            try
            {
                if (send)
                {
                    string reply = await client.RequestNodeReport(source, number, report);
                    if (open && !closed) status.Text = reply + " Refresh records to check for a reply.";
                }
                else
                {
                    records.Children.Clear();
                    var entries = await client.NodeRecords(source, number, report);
                    if (!open || closed) return;
                    foreach (var entry in entries)
                    {
                        records.Children.Add(Wrapped(entry.Description));
                        if (report != NodeReport.Traceroutes) continue;
                        var show = new Button { Content = "Show on map" };
                        show.Click += (_, _) => { chosenRoute = entry; dialog.Hide(); };
                        records.Children.Add(show);
                    }
                    status.Text = entries.Length == 0 ? "No records for this node on this source." : $"Records shown: {entries.Length}";
                }
            }
            catch (Exception ex) { if (open && !closed) status.Text = ex.Message; }
            finally
            {
                busy = false;
                if (open && !closed) picker.IsEnabled = refresh.IsEnabled = request.IsEnabled = true;
            }
        }
        picker.SelectionChanged += async (_, _) => await Load();
        refresh.Click += async (_, _) => await Load();
        request.Click += async (_, _) => await Load(true);
        var shown = dialog.ShowAsync();
        _ = Load();
        try { await shown; }
        finally { open = false; }
        if (chosenRoute is null || closed) return;
        Activity.SelectedItem = MapTab;
        await LoadSourceAsync();
        if (closed) return;
        if (overlaySource != source.Id) HideMapLinks();
        tracerouteReports = [chosenRoute];
        UpdateLinks(source);
    });
}
