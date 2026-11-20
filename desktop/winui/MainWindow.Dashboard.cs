using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

namespace MeshMonitor.Native;

internal sealed record PinnedChart(string Source, string Node, string Name, string Metric, string Unit,
    int Hours = 24, double? Minimum = null, double? Maximum = null, string[]? HiddenSources = null, bool Scatter = false)
{
    internal bool Matches(PinnedChart other) =>
        (Source, Node, Metric, Unit) == (other.Source, other.Node, other.Metric, other.Unit);
}

internal sealed record DesktopPreferences
{
    public int OpeningTab { get; init; }
    public double SidebarWidth { get; init; } = 265;
    public PinnedChart[] Charts { get; init; } = [];
    public string? TilePackage { get; init; }
    public string? TileLabel { get; init; }
    public bool Offline { get; init; }
    public bool Markers { get; init; } = true;
    public bool Trails { get; init; } = true;
    public bool Neighbors { get; init; } = true;
    public bool Routes { get; init; } = true;
}

public sealed partial class MainWindow
{
    private DesktopPreferences desktop = new();
    private string preferenceFile = "";

    private void LoadDesktopPreferences(string file)
    {
        preferenceFile = file;
        desktop = new();
        if (File.Exists(file))
        {
            try { desktop = JsonSerializer.Deserialize<DesktopPreferences>(File.ReadAllText(file)) ?? new(); }
            catch (JsonException) { ShowNotice("Desktop preferences could not be read. Using defaults.", InfoBarSeverity.Warning); }
        }
        desktop = desktop with { Charts = (desktop.Charts ?? []).Where(chart =>
            chart is { Source: not null, Node.Length: > 0, Metric.Length: > 0, Unit: not null, Hours: 1 or 6 or 24 or 168 } &&
            ((chart.Minimum is null && chart.Maximum is null) ||
             (chart.Minimum is double low && chart.Maximum is double high && low < high && double.IsFinite(high - low)))).ToArray() };
        Workspace.ColumnDefinitions[0].Width=new GridLength(Math.Clamp(desktop.SidebarWidth,180,420));
        Activity.SelectedIndex=Math.Clamp(desktop.OpeningTab,0,Activity.Items.Count-1);
        RestoreTiles();
        ApplyLayerSettings();
    }

    private void SaveDesktop(DesktopPreferences next)
    {
        string temporary = preferenceFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(next));
        File.Move(temporary, preferenceFile, true);
        desktop = next;
    }

    private void PinChart(PinnedChart chart)
    {
        if (!desktop.Charts.Any(chart.Matches))
            SaveDesktop(desktop with { Charts = [.. desktop.Charts, chart] });
    }

    private async void OnPinReading(object sender, RoutedEventArgs args) => await RunAsync(async client =>
    {
        if (TelemetryList.SelectedItem is not TelemetryRow { HasHistory: true } row ||
            SourcesList.SelectedItem is not SourceRow source) return;
        PinChart(new(row.SourceId, row.NodeId, row.Node, row.Type, row.Unit));
        Activity.SelectedItem = DashboardTab;
        await LoadDashboardAsync(client, source);
    });

    private async Task LoadDashboardAsync(MeshApi client, SourceRow source)
    {
        var readable = (SourcesList.ItemsSource as SourceRow[] ?? []).ToDictionary(item => item.Id);
        var visible = desktop.Charts.Where(chart => chart.Source.Length == 0 ? source.IsUnified :
            readable.ContainsKey(chart.Source) && (source.IsUnified || chart.Source == source.Id)).ToArray();
        var cards = new List<StackPanel>();
        var comparisons = new Dictionary<int, TelemetryComparison[]>();
        foreach (var chart in visible)
        {
            var card = new StackPanel { Spacing = 6 };
            cards.Add(card);
            string scope = chart.Source.Length == 0 ? "Favorite sources" : readable[chart.Source].Name;
            card.Children.Add(Wrapped($"{chart.Name} · {chart.Metric} · {scope}", strong: true));
            try
            {
                TelemetryLine[] lines;
                if (chart.Source.Length == 0)
                {
                    if (!comparisons.TryGetValue(chart.Hours, out var available))
                        comparisons[chart.Hours] = available = await client.TelemetryComparisons(chart.Hours);
                    lines = available.FirstOrDefault(item => item.Key == (chart.Node, chart.Metric, chart.Unit))?.Lines
                        .Where(line => chart.HiddenSources?.Contains(line.SourceId) != true).ToArray() ?? [];
                }
                else
                {
                    var row = new TelemetryRow(chart.Name, "", "") { SourceId = chart.Source, NodeId = chart.Node, Type = chart.Metric, Unit = chart.Unit };
                    lines = [new(chart.Source, scope, chart.Unit, await client.TelemetryHistory(row, chart.Hours))];
                }
                if (closed) return;
                var points = lines.SelectMany(line => line.Points).ToArray();
                if (points.Length > 0)
                {
                    var plot = new Canvas { Height = 130 };
                    AutomationProperties.SetName(plot, $"{chart.Name} {chart.Metric}");
                    plot.SizeChanged += (_, _) => DrawTelemetryLines(plot, lines, chart.Minimum, chart.Maximum, chart.Scatter);
                    card.Children.Add(plot);
                    card.Children.Add(Wrapped($"Last {chart.Hours} h · Y axis {chart.Minimum ?? points.Min(p => p.Value):G6} to {chart.Maximum ?? points.Max(p => p.Value):G6} {chart.Unit} · {points.Length} samples"));
                    if (lines.Length > 1)
                    {
                        var legend = new StackPanel();
                        for (int i = 0; i < lines.Length; i++)
                        {
                            var label = Wrapped(lines[i].SourceName);
                            label.Foreground = TelemetryBrush(i);
                            legend.Children.Add(label);
                        }
                        card.Children.Add(legend);
                    }
                }
                else card.Children.Add(Wrapped($"No samples in the last {chart.Hours} hours."));
            }
            catch (HttpRequestException error) { card.Children.Add(Wrapped(error.Message)); }
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            card.Children.Add(actions);
            void Action(string title, bool enabled, Func<Task> change)
            {
                var button = new Button { Content = title, IsEnabled = enabled };
                button.Click += async (_, _) => await RunAsync(async current =>
                {
                    await change();
                    if (!closed) await LoadDashboardAsync(current, source);
                });
                actions.Children.Add(button);
            }
            int position = Array.IndexOf(visible, chart);
            Task Move(int direction)
            {
                var charts = desktop.Charts.ToArray();
                int index = Array.IndexOf(charts, chart), other = Array.IndexOf(charts, visible[position + direction]);
                (charts[index], charts[other]) = (charts[other], charts[index]);
                SaveDesktop(desktop with { Charts = charts });
                return Task.CompletedTask;
            }
            Action("Chart settings", true, () => EditChart(client, chart));
            Action("Move up", position > 0, () => Move(-1));
            Action("Move down", position + 1 < visible.Length, () => Move(1));
            Action("Unpin", true, () =>
            {
                SaveDesktop(desktop with { Charts = desktop.Charts.Where(item => item != chart).ToArray() });
                return Task.CompletedTask;
            });
        }
        if (closed) return;
        DashboardCards.Children.Clear();
        if (cards.Count == 0) DashboardCards.Children.Add(Wrapped("Select a reading in Telemetry and choose Pin chart."));
        foreach (var card in cards) DashboardCards.Children.Add(card);
    }

    private async Task EditChart(MeshApi client, PinnedChart chart)
    {
        int[] hours = [1, 6, 24, 168];
        var range = new ComboBox { Header = "History window", ItemsSource = new[] { "1 hour", "6 hours", "24 hours", "7 days" },
            SelectedIndex = Array.IndexOf(hours, chart.Hours) };
        var auto = new CheckBox { Content = "Automatic Y axis", IsChecked = chart.Minimum is null };
        var min = new NumberBox { Header = $"Minimum ({chart.Unit})", Value = chart.Minimum ?? double.NaN };
        var max = new NumberBox { Header = $"Maximum ({chart.Unit})", Value = chart.Maximum ?? double.NaN };
        void Toggle() => min.IsEnabled = max.IsEnabled = auto.IsChecked != true;
        auto.Click += (_, _) => Toggle();
        Toggle();
        var error = DialogError();
        var sources = new List<CheckBox>();
        var scatter = new CheckBox { Content = "Show individual sample points", IsChecked = chart.Scatter };
        var fields = new List<FrameworkElement> { range, scatter, auto, min, max };
        if (chart.Source.Length == 0)
        {
            var options = await client.TelemetryComparisons(168);
            if (closed) return;
            var available = options.FirstOrDefault(option => option.Key == (chart.Node, chart.Metric, chart.Unit));
            if (available is not null)
            {
                fields.Add(Wrapped("Comparison sources", strong: true));
                var choices = new StackPanel();
                foreach (var line in available.Lines)
                {
                    var check = new CheckBox { Content = line.SourceName, Tag = line.SourceId,
                        IsChecked = chart.HiddenSources?.Contains(line.SourceId) != true };
                    sources.Add(check);
                    choices.Children.Add(check);
                }
                fields.Add(new ScrollViewer { Content = choices, MaxHeight = 110 });
            }
        }
        fields.Add(error);
        var dialog = Form("Chart settings", "Save", fields.ToArray());
        dialog.PrimaryButtonClick += (_, action) =>
        {
            if (sources.Count > 0 && sources.All(check => check.IsChecked != true))
            {
                action.Cancel = true;
                error.Text = "Select at least one comparison source.";
                return;
            }
            if (auto.IsChecked == true || double.IsFinite(max.Value - min.Value) && min.Value < max.Value) return;
            action.Cancel = true;
            error.Text = "Enter two finite bounds with the maximum above the minimum.";
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || closed) return;
        var updated = chart with { Scatter = scatter.IsChecked == true, Hours = hours[range.SelectedIndex], Minimum = auto.IsChecked == true ? null : min.Value,
            Maximum = auto.IsChecked == true ? null : max.Value,
            HiddenSources = (chart.HiddenSources ?? []).Except(sources.Select(check => (string)check.Tag))
                .Concat(sources.Where(check => check.IsChecked != true).Select(check => (string)check.Tag)).ToArray() };
        SaveDesktop(desktop with { Charts = desktop.Charts.Select(item => item == chart ? updated : item).ToArray() });
    }
}
