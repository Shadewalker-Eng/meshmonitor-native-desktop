using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeshMonitor.Native;

public sealed record RecordedPacket(string Label, string Detail);
internal sealed record PacketPage(RecordedPacket[] Rows, int Limit, bool More);

internal sealed partial class MeshApi
{
    internal async Task<PacketPage> PacketPageAsync(SourceRow source, long since, uint? sender, int? type, int offset)
    {
        string path = $"api/packets?sourceId={Uri.EscapeDataString(source.Id)}&since={since}&limit=100&offset={offset}";
        if (sender is uint node) path += $"&from_node={node}";
        if (type is int port) path += $"&portnum={port}";
        var response = await RequestAsync(path);
        int limit = (int)(response.Number("limit") ?? 100);
        var packets = Rows(response.Property("packets")).Select(row =>
        {
            string from = row.String("from_node_longName") ?? row.String("from_node_id") ?? row.Property("from_node").ToString();
            string portName = row.String("portnum_name") ?? $"Port {row.Number("portnum")}";
            string label = $"{ValueFormat.Time(row.Timestamp("timestamp"))} · {from} · {portName}";
            return new RecordedPacket(label, JsonSerializer.Serialize(row, new JsonSerializerOptions { WriteIndented = true }));
        }).ToArray();
        return new(packets, limit, limit > 0 && offset + (long)limit < response.Number("total"));
    }

    internal Task<JsonElement> PacketDistribution(SourceRow source, long since) =>
        RequestAsync($"api/packets/stats/distribution?sourceId={Uri.EscapeDataString(source.Id)}&since={since}");
}

public sealed partial class MainWindow
{
    private int packetHours = 24, packetOffset, packetPageSize = 100;
    private bool packetsPaused;
    private uint? packetNode;
    private int? packetType;
    private long packetSince;

    private void ResetPackets()
    {
        packetOffset = 0;
        packetHours = 24;
        packetNode = null;
        packetType = null;
        packetSince = 0;
        packetsPaused = false;
        PacketList.ItemsSource = null;
        PacketStatus.Text = "";
        PacketWindow.SelectedIndex = 1;
        PacketSender.Text = PacketPort.Text = "";
    }

    private long PacketWindowStart() => DateTimeOffset.UtcNow.AddHours(-packetHours).ToUnixTimeMilliseconds();

    private async Task LoadPacketsAsync(MeshApi client, SourceRow source)
    {
        bool permitted = source.Type == "meshtastic_tcp" &&
            (radioAdmin || radioPermissions.Property(source.Id).Property("packetmonitor").Flag("read") == true);
        PacketFilters.Visibility = PacketActions.Visibility = permitted ? Visibility.Visible : Visibility.Collapsed;
        if (!permitted)
        {
            PacketList.ItemsSource = null;
            PacketStatus.Text = source.Type == "meshtastic_tcp" ? "Packet monitor read access is required for this source." :
                "Select a Meshtastic TCP source to view its packet log.";
            return;
        }
        if (!packetsPaused || packetSince == 0) packetSince = PacketWindowStart();
        PacketPause.Content = packetsPaused ? "Resume live" : "Pause";
        PacketPage page;
        try { page = await client.PacketPageAsync(source, packetSince, packetNode, packetType, packetOffset); }
        catch
        {
            if (!closed)
            {
                PacketList.ItemsSource = null;
                PacketStatus.Text = "Packet log could not be loaded.";
                PacketNewer.IsEnabled = PacketOlder.IsEnabled = false;
            }
            throw;
        }
        if (closed) return;
        PacketList.ItemsSource = page.Rows;
        packetPageSize = page.Limit;
        PacketNewer.IsEnabled = packetOffset > 0;
        PacketOlder.IsEnabled = page.More;
        PacketStatus.Text = $"{source.Name} · {(packetsPaused ? "Paused" : "Live")} · {page.Rows.Length} visible packets on page {1 + packetOffset / Math.Max(1, page.Limit)}";
    }

    private async void OnPacketApply(object sender, RoutedEventArgs args) => await RunAsync(async client =>
    {
        if (SourcesList.SelectedItem is not SourceRow source) return;
        string address = PacketSender.Text.Trim(), port = PacketPort.Text.Trim();
        uint? node = null;
        int? type = null;
        if (address.Length > 0)
        {
            bool hex = address.StartsWith('!');
            if (!uint.TryParse(hex ? address[1..] : address, hex ? NumberStyles.HexNumber : NumberStyles.None,
                    CultureInfo.InvariantCulture, out uint value)) throw new InvalidOperationException("Enter a decimal sender number or a hexadecimal !node ID.");
            node = value;
        }
        if (port.Length > 0)
        {
            if (!int.TryParse(port, out int value) || value < 0) throw new InvalidOperationException("Enter a nonnegative port number.");
            type = value;
        }
        packetNode = node;
        packetType = type;
        packetHours = PacketWindow.SelectedIndex switch { 0 => 1, 2 => 168, _ => 24 };
        packetOffset = 0;
        packetSince = PacketWindowStart();
        await LoadPacketsAsync(client, source);
    });

    private async void OnPacketPause(object sender, RoutedEventArgs args) => await RunAsync(async client =>
    {
        packetsPaused = !packetsPaused;
        if (!packetsPaused)
        {
            packetOffset = 0;
            if (SourcesList.SelectedItem is SourceRow source) await LoadPacketsAsync(client, source);
        }
        else
        {
            PacketPause.Content = "Resume live";
            PacketStatus.Text = PacketStatus.Text.Replace(" · Live · ", " · Paused · ");
        }
    });

    private async Task MovePacketPage(int direction) => await RunAsync(async client =>
    {
        if (SourcesList.SelectedItem is not SourceRow source) return;
        packetsPaused = true;
        packetOffset = Math.Max(0, packetOffset + direction * packetPageSize);
        await LoadPacketsAsync(client, source);
    });

    private async void OnPacketNewer(object sender, RoutedEventArgs args) => await MovePacketPage(-1);
    private async void OnPacketOlder(object sender, RoutedEventArgs args) => await MovePacketPage(1);

    private async void OnPacketDetails(object sender, ItemClickEventArgs args) => await RunAsync(async _ =>
    {
        if (args.ClickedItem is not RecordedPacket packet) return;
        var detail = Wrapped(packet.Detail);
        detail.IsTextSelectionEnabled = true;
        await DetailDialog("Packet details", new ScrollViewer { Content = detail, MaxHeight = 420 }).ShowAsync();
    });

    private async void OnPacketReport(object sender, RoutedEventArgs args) => await RunAsync(async client =>
    {
        if (SourcesList.SelectedItem is not SourceRow source) return;
        int hours = PacketWindow.SelectedIndex switch { 0 => 1, 2 => 168, _ => 24 };
        long since = DateTimeOffset.UtcNow.AddHours(-hours).ToUnixTimeMilliseconds();
        var data = await client.PacketDistribution(source, since);
        if (closed) return;
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Wrapped($"{source.Name} · Last {hours} hours · {data.Number("total") ?? 0:N0} packets"));
        var dialog = DetailDialog("Packet activity", new ScrollViewer { Content = panel, MaxHeight = 420 });
        uint? node = null;
        int? type = null;
        if (data.Flag("enabled") == false) panel.Children.Add(Wrapped("Packet logging is disabled."));
        else
        {
            void AddCounts(string property, string heading, bool senders)
            {
                panel.Children.Add(Wrapped(heading, strong: true));
                foreach (var row in data.Property(property).EnumerateArray())
                {
                    uint? key = row.UInt32(senders ? "from_node" : "portnum");
                    if (key is null) continue;
                    string label = senders ? row.String("from_node_longName") ?? $"!{key:x8}" :
                        row.String("portnum_name") ?? $"Port {key}";
                    var button = new Button { Content = $"{label} · {row.Number("count"):N0} packets" };
                    button.Click += (_, _) =>
                    {
                        if (senders) node = key; else type = (int)key.Value;
                        dialog.Hide();
                    };
                    panel.Children.Add(button);
                }
            }
            AddCounts("byType", "Packet types", false);
            AddCounts("byDevice", "Top senders (up to 10)", true);
        }
        await dialog.ShowAsync();
        if (closed || (node is null && type is null)) return;
        packetHours = hours;
        packetSince = since;
        packetNode = node;
        packetType = type;
        PacketSender.Text = node is null ? "" : $"!{node:x8}";
        PacketPort.Text = type?.ToString() ?? "";
        packetsPaused = true;
        packetOffset = 0;
        await LoadPacketsAsync(client, source);
    });
}
