using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace MeshMonitor.Native;

public sealed partial class MainWindow
{
    private TelemetryRow[] telemetryRows = [];

    private async Task LoadTelemetryAsync(SourceRow source)
    {
        var readings = await api!.Telemetry(source);
        if (closed) return;
        telemetryRows = readings;
        ApplyTelemetryFilter();
    }

    private void OnFavoritesFilter(object sender, RoutedEventArgs args) => ApplyTelemetryFilter();

    private void ApplyTelemetryFilter()
    {
        var selected = TelemetryList.SelectedItem as TelemetryRow;
        bool favorites = FavoritesOnly.IsChecked == true;
        var visible = favorites ? telemetryRows.Where(item => item.Favorited).ToArray() : telemetryRows;
        TelemetryList.ItemsSource = visible;
        TelemetryList.SelectedItem = selected is null ? null : visible.FirstOrDefault(row => SameReading(row, selected));
        TelemetryStatus.Text = visible.Length == 0
            ? favorites ? "No favorite readings in the past 24 hours." : "No readings in the past 24 hours."
            : $"{visible.Length} {(favorites ? "favorite" : "latest")} readings from the past 24 hours";
        UpdateDetailActions();
    }

    private static bool SameReading(TelemetryRow left, TelemetryRow right) =>
        left.NodeId == right.NodeId && left.SourceId == right.SourceId && left.Type == right.Type;

    private void UpdateFavoriteActions()
    {
        if (FavoriteReading is null || CompareFavorites is null) return;
        var row = TelemetryList.SelectedItem as TelemetryRow;
        PinReading.IsEnabled = !busy && row is { HasHistory: true };
        FavoriteReading.Content = row?.Favorited == true ? "Remove favorite" : "Add favorite";
        FavoriteReading.IsEnabled = !busy && row is { HasHistory: true, CanEditFavorite: true };
        CompareFavorites.IsEnabled = !busy;
    }

    private async void OnToggleFavorite(object sender, RoutedEventArgs args) => await RunAsync(async client =>
    {
        if (TelemetryList.SelectedItem is not TelemetryRow { HasHistory: true, CanEditFavorite: true } row) return;
        bool favorite = !row.Favorited;
        await client.SetTelemetryFavorite(row, favorite);
        if (closed) return;
        telemetryRows = telemetryRows.Select(item => SameReading(row, item)
            ? item with { Favorited = favorite } : item).ToArray();
        ApplyTelemetryFilter();
    });

    private async void OnCompareFavorites(object sender, RoutedEventArgs args) => await RunAsync(async client =>
    {
        (int Hours, string Label)[] windows = [(1, "1 hour"), (6, "6 hours"), (24, "24 hours"), (168, "7 days")];
        var range = new ComboBox
        {
            Header = "Time range", ItemsSource = windows.Select(item => item.Label).ToArray(), SelectedIndex = 2
        };
        var metric = new ComboBox { Header = "Favorite metric", HorizontalAlignment = HorizontalAlignment.Stretch };
        var chart = new Canvas { Height = 176, MinWidth = 300 };
        AutomationProperties.SetName(chart, "Cross-source telemetry chart");
        var dates = Wrapped("");
        var status = Wrapped("Loading favorite metrics…");
        var legend = new StackPanel { Spacing = 4 };
        var layout = new StackPanel { Spacing = 10 };
        layout.Children.Add(range);
        layout.Children.Add(metric);
        var pin = new Button { Content = "Pin comparison", IsEnabled = false };
        layout.Children.Add(pin);
        layout.Children.Add(chart);
        layout.Children.Add(dates);
        layout.Children.Add(status);
        layout.Children.Add(new ScrollViewer { Content = legend, MaxHeight = 110 });

        TelemetryComparison[] options = [];
        TelemetryComparison? selected = null;
        bool showing = true;
        bool updating = false;
        int generation = 0;
        chart.SizeChanged += (_, _) => DrawTelemetryLines(chart, selected?.Lines ?? []);
        void ShowSelection()
        {
            selected = metric.SelectedIndex >= 0 ? options[metric.SelectedIndex] : null;
            pin.IsEnabled = selected is not null;
            DrawTelemetryLines(chart, selected?.Lines ?? []);
            legend.Children.Clear();
            dates.Text = "";
            if (selected is not { } comparison)
            {
                status.Text = "No favorite metrics have data in this time range.";
                AutomationProperties.SetHelpText(chart, status.Text);
                return;
            }
            var points = comparison.Lines.SelectMany(line => line.Points).ToArray();
            dates.Text = $"{ValueFormat.Time(points.Min(point => point.Time))} – {ValueFormat.Time(points.Max(point => point.Time))}";
            string unit = comparison.Unit.Length == 0 ? "" : $" {comparison.Unit}";
            string note = comparison.ObserverRelative ? " · Measured at each source" : "";
            status.Text = $"Low {points.Min(point => point.Value):G6}{unit} · High {points.Max(point => point.Value):G6}{unit}{note}";
            for (int index = 0; index < comparison.Lines.Length; index++)
            {
                var line = comparison.Lines[index];
                var key = new Line
                {
                    X2 = 24, Stroke = TelemetryBrush(index), StrokeThickness = 2.5,
                    VerticalAlignment = VerticalAlignment.Center
                };
                if (index % 2 == 1) key.StrokeDashArray = new DoubleCollection { 3, 2 };
                var legendRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                legendRow.Children.Add(key);
                legendRow.Children.Add(Wrapped($"{line.SourceName} · {line.Points.Length} samples"));
                legend.Children.Add(legendRow);
            }
            AutomationProperties.SetHelpText(chart, $"{comparison.Label}. {status.Text}. " +
                string.Join(", ", comparison.Lines.Select(line => line.SourceName)));
        }

        async Task LoadAsync()
        {
            if (range.SelectedIndex < 0) return;
            int request = ++generation;
            var key = selected?.Key;
            metric.IsEnabled = pin.IsEnabled = false;
            status.Text = "Loading favorite metrics…";
            try
            {
                var loaded = await client.TelemetryComparisons(windows[range.SelectedIndex].Hours);
                if (!showing || closed || request != generation) return;
                options = loaded;
                updating = true;
                try
                {
                    metric.ItemsSource = options.Select(item => item.Label).ToArray();
                    int previous = Array.FindIndex(options, item => item.Key == key);
                    metric.SelectedIndex = previous >= 0 ? previous : options.Length > 0 ? 0 : -1;
                }
                finally { updating = false; }
                ShowSelection();
            }
            catch (Exception error)
            {
                if (!showing || closed || request != generation) return;
                selected = null;
                pin.IsEnabled = false;
                DrawTelemetryLines(chart, []);
                dates.Text = "";
                legend.Children.Clear();
                status.Text = error.Message;
            }
            finally
            {
                if (showing && !closed && request == generation) metric.IsEnabled = true;
            }
        }

        pin.Click += (_, _) =>
        {
            if (selected is null) return;
            try
            {
                PinChart(new("", selected.NodeId, selected.NodeName, selected.Type, selected.Unit,
                    windows[range.SelectedIndex].Hours));
                status.Text = "Comparison pinned. Open Dashboard under Unified to view it.";
            }
            catch (Exception error) { status.Text = error.Message; }
        };
        metric.SelectionChanged += (_, _) => { if (!updating) ShowSelection(); };
        range.SelectionChanged += async (_, _) => await LoadAsync();
        var dialog = DetailDialog("Compare favorite telemetry", layout);
        var opened = dialog.ShowAsync();
        _ = LoadAsync();
        try { await opened; }
        finally { showing = false; }
    });

    private static SolidColorBrush TelemetryBrush(int index)
    {
        (byte R, byte G, byte B)[] palette =
            [(0, 95, 184), (146, 64, 14), (0, 109, 87), (110, 58, 164), (171, 42, 75), (47, 82, 115)];
        var (r, g, b) = palette[index % palette.Length];
        return new SolidColorBrush(ColorHelper.FromArgb(255, r, g, b));
    }
}
