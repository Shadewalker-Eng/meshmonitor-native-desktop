using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace MeshMonitor.Native;

public sealed partial class MeshMap : Grid
{
    private const int MaxZoom = 16;
    private const string TileRoot = "https://server.arcgisonline.com/ArcGIS/rest/services/";
    private static readonly SolidColorBrush White = new(Colors.White);
    private static readonly SolidColorBrush TrailBlue = new(ColorHelper.FromArgb(255, 0, 91, 171));
    private static readonly (string Label, SolidColorBrush Color)[] MarkerStyles =
    [
        ("Heard within 1 hour", new(ColorHelper.FromArgb(255, 22, 101, 52))),
        ("Heard 1–24 hours ago", new(ColorHelper.FromArgb(255, 146, 64, 14))),
        ("Heard over 24 hours ago", new(ColorHelper.FromArgb(255, 71, 85, 105))),
        ("Last heard unknown", new(ColorHelper.FromArgb(255, 60, 66, 74))),
        ("Node cluster", new(ColorHelper.FromArgb(255, 91, 49, 151)))
    ];
    private readonly Canvas tiles = new() { IsHitTestVisible = false };
    private readonly Canvas trail = new() { IsHitTestVisible = false };
    private readonly Canvas linkCanvas = new() { IsHitTestVisible = false };
    private MapLink[] links = [];
    private string linkSource = "";
    private readonly Dictionary<string, (double X, double Y)> linkLocations = new(StringComparer.OrdinalIgnoreCase);
    private int playbackIndex = -1;
    private readonly Canvas playbackLayer = new() { IsHitTestVisible = false };
    private readonly Border playbackCursor = new()
    {
        Width = 24, Height = 24, CornerRadius = new CornerRadius(12),
        BorderThickness = new Thickness(4), BorderBrush = new SolidColorBrush(Colors.DarkOrange),
        Child = new Border { CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(2), BorderBrush = White },
        Visibility = Visibility.Collapsed
    };
    private readonly Canvas markers = new();
    private readonly RectangleGeometry clip = new();
    private readonly Dictionary<(int Zoom, int X, int Y), FrameworkElement> visibleTiles = [];
    private (NodeRow Node, double X, double Y)[] positions = [];
    private (double X, double Y)[] trailPoints = [];
    private double centerX;
    private double centerY;
    private int zoom = 2;
    private Point? dragPoint;
    private bool fitPending;
    private bool fitLinksPending;
    private bool showNeighbors = true;
    private bool showRoutes = true;

    internal void SetLayers(bool showMarkers, bool showTrails, bool neighbors, bool routes)
    {
        markers.Visibility = showMarkers ? Visibility.Visible : Visibility.Collapsed;
        playbackLayer.Visibility = showTrails ? Visibility.Visible : Visibility.Collapsed;
        trail.Visibility = showTrails ? Visibility.Visible : Visibility.Collapsed;
        showNeighbors = neighbors;
        showRoutes = routes;
        Render();
    }
    private bool grayBasemap;
    private NodeRow? selectedNode;

    public MeshMap()
    {
        Background = new SolidColorBrush(ColorHelper.FromArgb(255, 214, 230, 239));
        Clip = clip;
        (centerX, centerY) = MapProjection.Project(38, -98);
        Children.Add(tiles);
        Children.Add(trail);
        Children.Add(linkCanvas);
        Children.Add(annotations);
        Children.Add(markers);
        playbackLayer.Children.Add(playbackCursor);
        Children.Add(playbackLayer);
        SizeChanged += (_, _) =>
        {
            if (fitLinksPending) SetLinks(linkSource, links);
            else if (fitPending) Fit();
            else Render();
        };
        PointerPressed += BeginPan;
        PointerMoved += Pan;
        PointerReleased += EndPan;
        PointerCaptureLost += (_, _) => dragPoint = null;
        PointerWheelChanged += (_, args) =>
        {
            var current = args.GetCurrentPoint(this);
            ZoomAt(Math.Sign(current.Properties.MouseWheelDelta), current.Position);
            args.Handled = true;
        };
    }

    public event Action<NodeRow>? NodeSelected;
    public int PositionedCount => positions.Length;

    private OfflineTilePackage? offlineTiles;

    internal void UseOffline(OfflineTilePackage? package)
    {
        if (offlineTiles == package) return;
        offlineTiles = package;
        tiles.Children.Clear();
        visibleTiles.Clear();
        if (package is not null)
        {
            (zoom, centerX, centerY) = package.InitialView;
            fitPending = false;
        }
        else zoom = Math.Clamp(zoom, 1, MaxZoom);
        Render();
    }

    public void SetGrayBasemap(bool useGray)
    {
        if (grayBasemap == useGray) return;
        grayBasemap = useGray;
        Background = new SolidColorBrush(useGray
            ? ColorHelper.FromArgb(255, 232, 235, 237)
            : ColorHelper.FromArgb(255, 214, 230, 239));
        tiles.Children.Clear();
        visibleTiles.Clear();
        Render();
    }

    public static void PopulateLegend(Panel legend)
    {
        foreach (var (label, color) in MarkerStyles)
        {
            var entry = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            entry.Children.Add(new Border
            {
                Width = 10, Height = 10, CornerRadius = new CornerRadius(3),
                Background = color, VerticalAlignment = VerticalAlignment.Center
            });
            entry.Children.Add(new TextBlock { Text = label, FontSize = 12 });
            legend.Children.Add(entry);
        }
    }

    public void SetNodes(IEnumerable<NodeRow> nodes, bool fit, NodeRow? selected = null)
    {
        selectedNode = selected;
        positions = nodes.Where(node => node.Latitude is >= -90 and <= 90 &&
            node.Longitude is >= -180 and <= 180)
            .Select(node =>
            {
                var location = MapProjection.Project(node.Latitude!.Value, node.Longitude!.Value);
                return (node, location.X, location.Y);
            }).ToArray();
        UpdateLinkLocations();
        if (fit) Fit();
        else Render();
    }

    public void SelectNode(NodeRow node, bool center = false)
    {
        selectedNode = node;
        if (center && node.Latitude is { } latitude && node.Longitude is { } longitude)
        {
            (centerX, centerY) = MapProjection.Project(latitude, longitude);
            zoom = offlineTiles?.ClosestZoom(14) ?? Math.Max(zoom, 14);
            fitPending = false;
        }
        Render();
    }

    public void Fit()
    {
        if (positions.Length == 0)
        {
            fitPending = false;
            Render();
            return;
        }
        if (ActualWidth < 10 || ActualHeight < 10)
        {
            fitPending = true;
            return;
        }
        double anchor = positions[0].X;
        var x = positions.Select(point => MapProjection.NearestWorld(point.X, anchor)).ToArray();
        FitBounds(x.Min(), x.Max(), positions.Min(point => point.Y), positions.Max(point => point.Y), 12);
    }

    internal void SetTrail(PositionFix[] fixes)
    {
        playbackIndex = -1;
        trailPoints = new (double X, double Y)[fixes.Length];
        for (int i = 0; i < fixes.Length; i++)
        {
            var fix = fixes[i];
            var point = MapProjection.Project(fix.Latitude, fix.Longitude);
            if (i != 0) point.X = MapProjection.NearestWorld(point.X, trailPoints[i - 1].X);
            trailPoints[i] = point;
        }
        if (trailPoints.Length > 0 && ActualWidth >= 10 && ActualHeight >= 10)
            FitBounds(trailPoints.Min(point => point.X), trailPoints.Max(point => point.X),
                trailPoints.Min(point => point.Y), trailPoints.Max(point => point.Y), 14);
        else
            Render();
    }

    private void FitBounds(double minX, double maxX, double minY, double maxY, int maximumZoom)
    {
        fitPending = false;
        centerX = MapProjection.Wrap((minX + maxX) / 2);
        centerY = (minY + maxY) / 2;
        zoom = maximumZoom;
        while (zoom > 1 && ((maxX - minX) * MapProjection.WorldSize(zoom) > ActualWidth * .8 ||
            (maxY - minY) * MapProjection.WorldSize(zoom) > ActualHeight * .8))
            zoom--;
        if (offlineTiles is not null) zoom = offlineTiles.ClosestZoom(zoom);
        Render();
    }

    public void ChangeZoom(int delta) => ZoomAt(delta, new Point(ActualWidth / 2, ActualHeight / 2));

    private void ZoomAt(int delta, Point pivot)
    {
        int next = offlineTiles?.NextZoom(zoom, delta) ?? Math.Clamp(zoom + delta, 1, MaxZoom);
        if (next == zoom) return;
        double x = pivot.X - ActualWidth / 2, y = pivot.Y - ActualHeight / 2;
        double before = MapProjection.WorldSize(zoom), after = MapProjection.WorldSize(next);
        centerX = MapProjection.Wrap(centerX + x / before - x / after);
        centerY = Math.Clamp(centerY + y / before - y / after, 0, 1);
        zoom = next;
        Render();
    }

    private void BeginPan(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed ||
            args.OriginalSource is FrameworkElement { DataContext: NodeRow }) return;
        if (CapturePointer(args.Pointer))
        {
            dragPoint = point.Position;
            args.Handled = true;
        }
    }

    private void Pan(object sender, PointerRoutedEventArgs args)
    {
        if (dragPoint is not { } previous) return;
        var point = args.GetCurrentPoint(this).Position;
        double scale = MapProjection.WorldSize(zoom);
        centerX = MapProjection.Wrap(centerX - (point.X - previous.X) / scale);
        centerY = Math.Clamp(centerY - (point.Y - previous.Y) / scale, 0, 1);
        dragPoint = point;
        Render();
    }

    private void EndPan(object sender, PointerRoutedEventArgs args)
    {
        if (dragPoint is null) return;
        dragPoint = null;
        ReleasePointerCapture(args.Pointer);
    }

    private void Render()
    {
        if (ActualWidth < 10 || ActualHeight < 10) return;
        clip.Rect = new Rect(0, 0, ActualWidth, ActualHeight);
        double world = MapProjection.WorldSize(zoom);
        double left = centerX * world - ActualWidth / 2;
        double top = centerY * world - ActualHeight / 2;
        RenderTiles(left, top);
        RenderTrail(left, top, world);
        DrawLinks(left, top, world);
        DrawOverlays(left, top, world);
        RenderMarkers(left, top, world);
        foreach (var marker in groupMarkers) PlaceGroupMarker(marker.Dot, marker.Fix);
        PlacePlaybackCursor();
    }

    private readonly List<(Border Dot, PositionFix? Fix)> groupMarkers = [];

    internal void ClearGroupReplay()
    {
        foreach (var marker in groupMarkers) playbackLayer.Children.Remove(marker.Dot);
        groupMarkers.Clear();
    }

    internal void StartGroupReplay(IEnumerable<PositionFix> fixes, int count)
    {
        ClearGroupReplay();
        var points = fixes.Select(fix => MapProjection.Project(fix.Latitude, fix.Longitude)).ToArray();
        if (points.Length > 0)
        {
            var xs = points.Select(point => MapProjection.NearestWorld(point.X, points[0].X)).ToArray();
            FitBounds(xs.Min(), xs.Max(), points.Min(p => p.Y), points.Max(p => p.Y), 14);
        }
        for (int i = 0; i < count; i++)
        {
            var dot = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(5,1,5,1),
                BorderThickness = new Thickness(2), BorderBrush = White, Background = MarkerStyles[i % 5].Color,
                Visibility = Visibility.Collapsed, Child = new TextBlock { Text = (i + 1).ToString(), Foreground = White } };
            playbackLayer.Children.Add(dot);
            groupMarkers.Add((dot, null));
        }
    }

    internal void SetGroupFix(int index, PositionFix? fix)
    {
        var marker = groupMarkers[index];
        if (marker.Fix == fix) return;
        groupMarkers[index] = (marker.Dot, fix);
        PlaceGroupMarker(marker.Dot, fix);
    }

    private void PlaceGroupMarker(Border dot, PositionFix? fix)
    {
        dot.Visibility = fix is null ? Visibility.Collapsed : Visibility.Visible;
        if (fix is null) return;
        var point = MapProjection.Project(fix.Latitude, fix.Longitude);
        double world = MapProjection.WorldSize(zoom);
        Canvas.SetLeft(dot, (MapProjection.NearestWorld(point.X, centerX) - centerX) * world + ActualWidth / 2 - 10);
        Canvas.SetTop(dot, (point.Y - centerY) * world + ActualHeight / 2 - 12);
    }

    internal void SetPlaybackFix(int index)
    {
        playbackIndex = index;
        PlacePlaybackCursor();
    }

    private void PlacePlaybackCursor()
    {
        bool visible = playbackIndex >= 0 && playbackIndex < trailPoints.Length;
        playbackCursor.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        var point = trailPoints[playbackIndex];
        double world = MapProjection.WorldSize(zoom);
        double x = (MapProjection.NearestWorld(point.X, centerX) - centerX) * world + ActualWidth / 2;
        double y = (point.Y - centerY) * world + ActualHeight / 2;
        Canvas.SetLeft(playbackCursor, x - playbackCursor.Width / 2);
        Canvas.SetTop(playbackCursor, y - playbackCursor.Height / 2);
    }

    private void UpdateLinkLocations()
    {
        linkLocations.Clear();
        foreach (var item in positions)
        {
            if (item.Node.SourceId != linkSource) continue;
            string id = uint.TryParse(item.Node.Id, out uint number) ? $"!{number:x8}" : item.Node.Id;
            linkLocations[id] = (item.X, item.Y);
        }
    }

    internal int SetLinks(string source, MapLink[] selected)
    {
        links = selected;
        linkSource = source;
        UpdateLinkLocations();
        var mapped = links.Where(link => linkLocations.ContainsKey(link.From) && linkLocations.ContainsKey(link.To)).ToArray();
        fitLinksPending = mapped.Length > 0 && (ActualWidth <= 10 || ActualHeight <= 10);
        if (mapped.Length > 0 && !fitLinksPending)
        {
            var points = mapped.SelectMany(link => new[] { linkLocations[link.From], linkLocations[link.To] }).ToArray();
            var xs = points.Select(point => MapProjection.NearestWorld(point.X, points[0].X)).ToArray();
            FitBounds(xs.Min(), xs.Max(), points.Min(p => p.Y), points.Max(p => p.Y), 14);
        }
        else Render();
        return mapped.Length;
    }

    private void DrawLinks(double left, double top, double world)
    {
        linkCanvas.Children.Clear();
        foreach (var link in links)
        {
            if (link.Traceroute ? !showRoutes : !showNeighbors) continue;
            if (!linkLocations.TryGetValue(link.From, out var a) || !linkLocations.TryGetValue(link.To, out var b)) continue;
            double ax = MapProjection.NearestWorld(a.X, centerX);
            double bx = MapProjection.NearestWorld(b.X, ax);
            double x = ax * world - left, y = a.Y * world - top;
            double dx = (bx - ax) * world, dy = (b.Y - a.Y) * world;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1) continue;
            double ux = dx / length, uy = dy / length;
            x -= uy * 3;
            y += ux * 3;
            var brush = !link.Traceroute ? new SolidColorBrush(Colors.Teal) : link.Return ? new SolidColorBrush(Colors.DarkMagenta) : TrailBlue;
            linkCanvas.Children.Add(new Line { X1 = x, Y1 = y, X2 = x + dx, Y2 = y + dy,
                Stroke = brush, StrokeThickness = 3 });
            var tip = new Point(x + dx * .6, y + dy * .6);
            linkCanvas.Children.Add(new Polygon { Fill = brush, Points = new PointCollection {
                tip, new(tip.X - ux * 11 - uy * 5, tip.Y - uy * 11 + ux * 5),
                new(tip.X - ux * 11 + uy * 5, tip.Y - uy * 11 - ux * 5) } });
        }
    }

    private void RenderTrail(double left, double top, double world)
    {
        trail.Children.Clear();
        if (trailPoints.Length == 0) return;

        double minX = trailPoints.Min(point => point.X);
        double maxX = trailPoints.Max(point => point.X);
        double middle = (minX + maxX) / 2;
        double wrap = MapProjection.NearestWorld(middle, centerX) - middle;
        for (int copy = -1; copy <= 1; copy++)
        {
            double offset = (wrap + copy) * world - left;
            if (maxX * world + offset < -8 || minX * world + offset > ActualWidth + 8) continue;
            var points = new PointCollection();
            foreach (var point in trailPoints)
                points.Add(new Point(point.X * world + offset, point.Y * world - top));

            if (points.Count > 1)
            {
                trail.Children.Add(new Polyline { Points = points, Stroke = White, StrokeThickness = 7, StrokeLineJoin = PenLineJoin.Round });
                var bluePoints = new PointCollection();
                foreach (var point in points) bluePoints.Add(point);
                trail.Children.Add(new Polyline { Points = bluePoints, Stroke = TrailBlue, StrokeThickness = 4, StrokeLineJoin = PenLineJoin.Round });
                AddTrailDot(points[0], White, TrailBlue);
            }
            Point latest = points[^1];
            bool overlapsSelection = selectedNode is { Latitude: { } latitude, Longitude: { } longitude } &&
                IsNearMarker(latest, latitude, longitude, left, top, world);
            if (overlapsSelection)
                AddTrailDot(latest, null, TrailBlue, size: 42);
            else
                AddTrailDot(latest, TrailBlue, White);
        }
    }

    private bool IsNearMarker(Point location, double latitude, double longitude, double left, double top, double world)
    {
        var node = MapProjection.Project(latitude, longitude);
        double x = MapProjection.NearestWorld(node.X, centerX) * world - left;
        double y = node.Y * world - top;
        return Math.Abs(location.X - x) <= 10 && Math.Abs(location.Y - y) <= 10;
    }

    private void AddTrailDot(Point location, Brush? fill, Brush stroke, double size = 12)
    {
        var dot = new Ellipse { Width = size, Height = size, Stroke = stroke, StrokeThickness = 2, Fill = fill };
        Canvas.SetLeft(dot, location.X - size / 2);
        Canvas.SetTop(dot, location.Y - size / 2);
        trail.Children.Add(dot);
    }

    private void RenderTiles(double left, double top)
    {
        int count = 1 << zoom;
        int size = MapProjection.TileSize;
        int firstX = (int)Math.Floor(left / size);
        int lastX = (int)Math.Floor((left + ActualWidth) / size);
        int firstY = Math.Max(0, (int)Math.Floor(top / size));
        int lastY = Math.Min(count - 1, (int)Math.Floor((top + ActualHeight) / size));
        var needed = new HashSet<(int, int, int)>();
        for (int y = firstY; y <= lastY; y++)
        for (int x = firstX; x <= lastX; x++)
        {
            var key = (zoom, x, y);
            needed.Add(key);
            if (!visibleTiles.TryGetValue(key, out var tile))
            {
                int column = MapProjection.WrapTile(x, count);
                tile = new Grid { Width = size, Height = size };
                var layers = ((Grid)tile).Children;
                if (offlineTiles is not null)
                {
                    if (offlineTiles.Image(zoom, column, y) is { } local) layers.Add(local);
                }
                else
                {
                    layers.Add(TileImage(grayBasemap ? "Canvas/World_Light_Gray_Base" : "World_Topo_Map", zoom, y, column));
                    if (grayBasemap)
                        layers.Add(TileImage("Canvas/World_Light_Gray_Reference", zoom, y, column));
                }
                visibleTiles.Add(key, tile);
                tiles.Children.Add(tile);
            }
            Canvas.SetLeft(tile, x * size - left);
            Canvas.SetTop(tile, y * size - top);
        }
        foreach (var key in visibleTiles.Keys.Where(key => !needed.Contains(key)).ToArray())
        {
            tiles.Children.Remove(visibleTiles[key]);
            visibleTiles.Remove(key);
        }
    }

    private static Image TileImage(string layer, int zoom, int row, int column) => new()
    {
        Source = new BitmapImage(new Uri($"{TileRoot}{layer}/MapServer/tile/{zoom}/{row}/{column}")),
        Stretch = Stretch.Fill
    };

    private void RenderMarkers(double left, double top, double world)
    {
        markers.Children.Clear();
        var visible = positions.Select(point => (
            point.Node, X: MapProjection.NearestWorld(point.X, centerX) * world - left, Y: point.Y * world - top))
            .Where(point => point.X >= 0 && point.X <= ActualWidth && point.Y >= 0 && point.Y <= ActualHeight);
        var now = DateTimeOffset.UtcNow;

        foreach (var group in visible.GroupBy(point => ((int)(point.X / 35), (int)(point.Y / 35))))
        {
            var first = group.First();
            int count = group.Count();
            bool selected = false;
            foreach (var point in group)
            {
                if (selectedNode?.Id != point.Node.Id || selectedNode.SourceId != point.Node.SourceId)
                    continue;
                first = point;
                selected = true;
                break;
            }
            var (status, color) = MarkerStyles[count > 1 ? 4 : AgeStyle(first.Node.Timestamp, now)];
            var button = new Button
            {
                Content = count > 1 ? count.ToString() : first.Node.ShortName is { Length: > 0 } label ? label : "•",
                Padding = new Thickness(6, 3, 6, 3),
                MinWidth = 30, MinHeight = 28, FontSize = 12,
                Background = color, Foreground = White, BorderBrush = White,
                BorderThickness = new Thickness(selected ? 3 : 1), CornerRadius = new CornerRadius(7),
                DataContext = first.Node
            };
            bool canZoom = (offlineTiles?.NextZoom(zoom, 1) ?? Math.Min(zoom + 1, MaxZoom)) > zoom;
            string tooltip = count == 1 ? $"{first.Node.Name} · {status}" :
                canZoom ? $"{count} nodes. Zoom in to inspect." : $"Select from {count} nodes.";
            if (selected && count > 1) tooltip = $"{first.Node.Name} selected · {tooltip}";
            AutomationProperties.SetName(button, tooltip);
            if (selected) AutomationProperties.SetItemStatus(button, "Selected");
            ToolTipService.SetToolTip(button, tooltip);
            button.Click += (_, _) =>
            {
                if (count == 1) NodeSelected?.Invoke(first.Node);
                else if (canZoom) ZoomAt(2, new Point(first.X, first.Y));
                else
                {
                    var choices = new MenuFlyout();
                    foreach (var entry in group)
                    {
                        var node = entry.Node;
                        var choice = new MenuFlyoutItem { Text = $"{node.Name} · {node.Id}" };
                        choice.Click += (_, _) => NodeSelected?.Invoke(node);
                        choices.Items.Add(choice);
                    }
                    choices.ShowAt(button);
                }
            };
            Canvas.SetLeft(button, first.X - 14);
            Canvas.SetTop(button, first.Y - 14);
            markers.Children.Add(button);
        }
    }

    private static int AgeStyle(DateTimeOffset? timestamp, DateTimeOffset now)
    {
        if (timestamp is null) return 3;
        var age = now - timestamp.Value;
        if (age <= TimeSpan.FromHours(1)) return 0;
        return age <= TimeSpan.FromHours(24) ? 1 : 2;
    }
}
