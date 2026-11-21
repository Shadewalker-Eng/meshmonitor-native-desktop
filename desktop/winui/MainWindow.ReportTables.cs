using System.Text.Json;
using Microsoft.UI.Xaml.Controls;

namespace MeshMonitor.Native;

public sealed partial class MainWindow
{
    private async Task ReportTable(MeshApi client, string title, JsonElement response, string? request = null)
    {
        var page = response.Property("data");
        if (page.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object)) page = response;
        var groups = new Dictionary<string, JsonElement[]>();
        if (page.ValueKind == JsonValueKind.Array) groups["Records"] = Records(page);
        else if (page.ValueKind == JsonValueKind.Object)
        {
            foreach (var field in page.EnumerateObject())
                if (field.Value.ValueKind == JsonValueKind.Array) groups[field.Name] = Records(field.Value);
            if (groups.Count == 0) groups["Summary"] = [page];
        }
        if (groups.Count == 0) { await ShowResult(title, "No records."); return; }
        var section = new ComboBox { Header = "Results", ItemsSource = groups.Keys.ToArray(), SelectedIndex = 0 };
        var sort = new ComboBox { Header = "Sort by" };
        var descending = new CheckBox { Content = "Descending" };
        var list = new ListView { MaxHeight = 200, SelectionMode = ListViewSelectionMode.Single };
        var details = Wrapped("");
        details.IsTextSelectionEnabled = true;
        var panel = new StackPanel { Spacing = 5 };
        var chart = new StackPanel { Spacing = 2 };
        var header = new StackPanel();
        Grid Cells(IEnumerable<string> values, bool heading = false)
        {
            var grid = new Grid { Width = 365, ColumnSpacing = 6 };
            int index = 0;
            foreach (var value in values)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                var label = Wrapped(value, 2, heading);
                Grid.SetColumn(label,index++);
                grid.Children.Add(label);
            }
            return grid;
        }
        foreach (var control in new Microsoft.UI.Xaml.UIElement[] { section, chart, sort, descending, header, list, details })
            panel.Children.Add(control);
        JsonElement[] sorted = [];
        void Refresh()
        {
            var source = groups[(string)section.SelectedItem];
            var key = sort.SelectedItem as string;
            var ordered = source.OrderBy(row => key == null ? null : row.Number(key)).ThenBy(
                row => key == null ? "" : row.Property(key).ToString(), StringComparer.CurrentCultureIgnoreCase);
            sorted = (descending.IsChecked == true ? ordered.Reverse() : ordered.AsEnumerable()).ToArray();
            var columns = source.Where(r => r.ValueKind == JsonValueKind.Object).SelectMany(r => r.EnumerateObject())
                .Where(p => p.Value.ValueKind is JsonValueKind.Number or JsonValueKind.String)
                .Select(p => p.Name).Distinct().OrderByDescending(name => name == key)
                .ThenByDescending(name => name is "sourceName" or "longName" or "senderId" or "hops" or "count" or "fixCount")
                .Take(3).ToArray();
            header.Children.Clear();
            header.Children.Add(Cells(columns.Length > 0 ? columns : ["Value"], true));
            list.ItemsSource = sorted.Select(row => Cells(columns.Length > 0 ?
                columns.Select(name => row.Property(name).ToString()) : [row.ToString()])).ToArray();
            details.Text = sorted.Length == 0 ? "No records." : "";
        }
        void SelectSection()
        {
            var rows = groups[(string)section.SelectedItem];
            sort.ItemsSource = rows.Where(r => r.ValueKind == JsonValueKind.Object).SelectMany(r => r.EnumerateObject())
                .Where(p => p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number).Select(p => p.Name).Distinct().ToArray();
            sort.SelectedIndex = sort.Items.Count == 0 ? -1 : 0;
            chart.Children.Clear();
            var bins = rows.Where(r => r.Number("hops").HasValue).GroupBy(r => r.Number("hops")).OrderBy(g => g.Key).ToArray();
            foreach (var bin in bins)
            {
                chart.Children.Add(Wrapped($"{bin.Key} hops · {bin.Count()} nodes"));
                chart.Children.Add(new ProgressBar { Minimum = 0, Maximum = bins.Max(g => g.Count()), Value = bin.Count() });
            }
            Refresh();
        }
        section.SelectionChanged += (_, _) => SelectSection();
        sort.SelectionChanged += (_, _) => Refresh();
        descending.Click += (_, _) => Refresh();
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedIndex < 0 || list.SelectedIndex >= sorted.Length) return;
            var row = sorted[list.SelectedIndex];
            details.Text = row.ValueKind != JsonValueKind.Object ? row.ToString() :
                string.Join(Environment.NewLine, row.EnumerateObject().Select(p => p.Name + "   " + p.Value));
        };
        SelectSection();
        var dialog = DetailDialog(title, new ScrollViewer { MaxHeight = 485, Content = panel });
        string? next = page.String("nextCursor");
        if (request != null && page.Flag("hasMore") == true && !string.IsNullOrEmpty(next))
            dialog.SecondaryButtonText = "Next page";
        bool receivers = request?.Contains("/coverage/receptions?") == true;
        if (request?.Contains("/coverage/senders?") == true) dialog.PrimaryButtonText = "Show receptions";
        if (receivers) dialog.PrimaryButtonText = "Sender packets";
        dialog.IsPrimaryButtonEnabled = false;
        list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedIndex >= 0 && list.SelectedIndex < sorted.Length &&
            (!receivers || sorted[list.SelectedIndex].UInt32("senderNodeNum").HasValue);
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Secondary && request != null && next != null)
        {
            string basePath = request.Split("&cursor=")[0];
            await ReportTable(client, title, await client.RequestAsync(basePath + "&cursor=" + Esc(next)), basePath);
        }
        if (result == ContentDialogResult.Primary && list.SelectedIndex >= 0 && request != null)
        {
            var row = sorted[list.SelectedIndex];
            if (receivers && row.UInt32("senderNodeNum") is uint node && row.String("sourceId") is string source)
            {
                string since = request.Split("since=").Last().Split('&')[0];
                string query = "api/packets?sourceId=" + Esc(source) + "&from_node=" + node + "&since=" + Esc(since);
                int offset = 0;
                while (true)
                {
                    var packets = await client.RequestAsync(query + "&limit=100&offset=" + offset);
                    await ReportTable(client, "Sender packets · " + node, packets.Property("packets"));
                    int limit = (int)(packets.Number("limit") ?? 100);
                    offset += limit;
                    if (limit < 1 || offset >= (packets.Number("total") ?? 0) ||
                        await Form("Sender packets", "Older packets", Wrapped("Load the next page?")).ShowAsync() != ContentDialogResult.Primary) return;
                }
            }
            string sender = row.String("senderId") ?? row.Property("senderNodeNum").ToString();
            if (sender.Length == 0) { await ShowResult(title, "No sender identifier in this record."); return; }
            string path = request.Replace("/coverage/senders?", "/coverage/receptions?") + "&sender=" + Esc(sender);
            await ReportTable(client, "Receptions · " + sender, await client.RequestAsync(path), path);
        }
    }
}
