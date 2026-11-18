using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace MeshMonitor.Native;

public sealed partial class MainWindow
{
    private void OnMessageSelection(object sender, SelectionChangedEventArgs args)
    {
        UpdateDetailActions();
        UpdateConversationActions();
    }
    private void OnTelemetrySelection(object sender, SelectionChangedEventArgs args) => UpdateDetailActions();

    private void UpdateDetailActions()
    {
        if (DeliveryDetails is null || TelemetryHistory is null) return;
        DeliveryDetails.IsEnabled = !busy && SourcesList.SelectedItem is SourceRow { IsUnified: false }
            && MessagesList.SelectedItem is MessageRow { Id.Length: > 0 };
        TelemetryHistory.IsEnabled = !busy && TelemetryList.SelectedItem is TelemetryRow { HasHistory: true };
        UpdateFavoriteActions();
    }

    private async void OnDeliveryDetails(object sender, RoutedEventArgs args) => await RunAsync(async client =>
    {
        if (SourcesList.SelectedItem is not SourceRow { IsUnified: false } source ||
            MessagesList.SelectedItem is not MessageRow { Id.Length: > 0 } message) return;

        var list = new StackPanel { Spacing = 12 };
        var status = Wrapped("Loading recorded delivery events…");
        var refresh = new Button { Content = "Refresh events" };
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(Wrapped($"{source.Name} · {message.Summary}"));
        if (message.Delivery.Length > 0) body.Children.Add(Wrapped(message.Delivery));
        body.Children.Add(Wrapped(message.Text, 4));
        body.Children.Add(refresh);
        body.Children.Add(status);
        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 290 });

        bool showing = true;
        async Task LoadAsync()
        {
            if (!refresh.IsEnabled) return;
            refresh.IsEnabled = false;
            status.Text = "Loading recorded delivery events…";
            try
            {
                var events = await client.DeliveryHistory(source, message);
                if (!showing || closed) return;
                list.Children.Clear();
                status.Text = events.Length == 0 ? "No delivery events have been recorded." : "";
                foreach (var entry in events)
                {
                    var item = new StackPanel { Spacing = 3 };
                    item.Children.Add(Wrapped(entry.Description, strong: true));
                    string origin = string.IsNullOrWhiteSpace(entry.Provenance) ? "" : $" · {entry.Provenance}";
                    item.Children.Add(Wrapped(ValueFormat.Time(entry.Time) + origin));
                    if (!string.IsNullOrWhiteSpace(entry.Detail)) item.Children.Add(Wrapped(entry.Detail));
                    list.Children.Add(item);
                }
            }
            catch (Exception error)
            {
                if (showing && !closed) status.Text = error.Message;
            }
            finally { refresh.IsEnabled = true; }
        }

        refresh.Click += async (_, _) => await LoadAsync();
        var dialog = DetailDialog("Delivery details", body);
        var opened = dialog.ShowAsync();
        _ = LoadAsync();
        try { await opened; }
        finally { showing = false; }
    });

    private async void OnTelemetryHistory(object sender, RoutedEventArgs args) => await RunAsync(async client =>
    {
        if (TelemetryList.SelectedItem is not TelemetryRow { HasHistory: true } reading) return;
        (int Hours, string Label)[] windows = [(1, "1 hour"), (6, "6 hours"), (24, "24 hours"), (168, "7 days")];
        var range = new ComboBox { Header = "Time range", ItemsSource = windows.Select(item => item.Label).ToArray(), SelectedIndex = 2 };
        var status = Wrapped("Loading history…");
        var labels = new Grid();
        labels.ColumnDefinitions.Add(new ColumnDefinition());
        labels.ColumnDefinitions.Add(new ColumnDefinition());
        var from = Wrapped("");
        var to = Wrapped("");
        to.TextAlignment = TextAlignment.Right;
        Grid.SetColumn(to, 1);
        labels.Children.Add(from);
        labels.Children.Add(to);

        var chart = new Canvas { Height = 176, Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetName(chart, $"{reading.Type} chart for {reading.Node}");
        var points = Array.Empty<TelemetryPoint>();
        chart.SizeChanged += (_, _) => DrawChart(chart, points);
        var content = new StackPanel { Spacing = 9 };
        content.Children.Add(Wrapped(reading.Node, strong: true));
        content.Children.Add(Wrapped(reading.Reading));
        content.Children.Add(Wrapped(reading.Context));
        content.Children.Add(range);
        content.Children.Add(chart);
        content.Children.Add(labels);
        content.Children.Add(status);

        bool showing = true;
        int request = 0;
        async Task LoadAsync()
        {
            int generation = ++request;
            status.Text = "Loading history…";
            try
            {
                var loaded = await client.TelemetryHistory(reading, windows[range.SelectedIndex].Hours);
                if (!showing || closed || generation != request) return;
                points = loaded;
                DrawChart(chart, points);
                from.Text = points.Length == 0 ? "" : ValueFormat.Time(points[0].Time);
                to.Text = points.Length < 2 ? "" : ValueFormat.Time(points[^1].Time);
                string unit = string.IsNullOrWhiteSpace(reading.Unit) ? "" : $" {reading.Unit}";
                status.Text = points.Length == 0 ? "No readings in this time range." :
                    $"{points.Length} samples · Low {points.Min(p => p.Value):G6}{unit} · High {points.Max(p => p.Value):G6}{unit}";
                AutomationProperties.SetHelpText(chart, status.Text);
            }
            catch (Exception error)
            {
                if (!showing || closed || generation != request) return;
                points = [];
                DrawChart(chart, points);
                from.Text = to.Text = "";
                status.Text = error.Message;
            }
        }

        range.SelectionChanged += async (_, _) => await LoadAsync();
        var dialog = DetailDialog("Telemetry history", content);
        var opened = dialog.ShowAsync();
        _ = LoadAsync();
        try { await opened; }
        finally { showing = false; }
    });

    private ContentDialog DetailDialog(string title, FrameworkElement content) => new()
    {
        XamlRoot = ((FrameworkElement)Content).XamlRoot,
        Title = title,
        Content = content,
        CloseButtonText = "Close",
        DefaultButton = ContentDialogButton.Close
    };

    private static TextBlock Wrapped(string text, int maxLines = 0, bool strong = false)
    {
        var block = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = maxLines,
            TextTrimming = maxLines > 0 ? TextTrimming.CharacterEllipsis : TextTrimming.None
        };
        if (strong) block.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        return block;
    }

    private static void DrawChart(Canvas canvas, TelemetryPoint[] points)
        => DrawTelemetryLines(canvas, [new("", "", "", points)]);

    private static void DrawTelemetryLines(Canvas canvas, TelemetryLine[] lines)
    {
        canvas.Children.Clear();
        const double inset = 8;
        double width = canvas.ActualWidth - 2 * inset;
        double height = canvas.ActualHeight - 2 * inset;
        var points = lines.SelectMany(line => line.Points).ToArray();
        if (width <= 0 || height <= 0 || points.Length == 0) return;

        for (int row = 0; row <= 4; row++)
        {
            double y = inset + row * height / 4;
            canvas.Children.Add(new Line
            {
                X1 = inset, X2 = inset + width, Y1 = y, Y2 = y,
                Stroke = new SolidColorBrush(Colors.Gray), StrokeThickness = 1, Opacity = .4
            });
        }

        double low = points.Min(point => point.Value), high = points.Max(point => point.Value);
        var start = points.Min(point => point.Time);
        double seconds = (points.Max(point => point.Time) - start).TotalSeconds;
        for (int index = 0; index < lines.Length; index++)
        {
            var series = new Polyline
            {
                Points = new PointCollection(), Stroke = TelemetryBrush(index), StrokeThickness = 2.5
            };
            if (index % 2 == 1) series.StrokeDashArray = new DoubleCollection { 3, 2 };
            foreach (var point in lines[index].Points)
            {
                double x = seconds > 0 ? (point.Time - start).TotalSeconds / seconds : .5;
                double y = high > low ? (high - point.Value) / (high - low) : .5;
                series.Points.Add(new Point(inset + x * width, inset + y * height));
            }
            if (series.Points.Count == 1)
            {
                var marker = new Ellipse { Fill = series.Stroke, Width = 8, Height = 8 };
                Canvas.SetLeft(marker, series.Points[0].X - 4);
                Canvas.SetTop(marker, series.Points[0].Y - 4);
                canvas.Children.Add(marker);
            }
            else canvas.Children.Add(series);
        }
    }
}
