using System.IO.Compression;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;

namespace MeshMonitor.Native;

internal sealed class OfflineTilePackage : IDisposable
{
    private readonly ZipArchive archive;
    private readonly Dictionary<(int Z, int X, int Y), ZipArchiveEntry> entries = [];
    private readonly int[] levels;
    internal (int Zoom, double X, double Y) InitialView { get; }
    internal string Coverage { get; }

    internal OfflineTilePackage(string path)
    {
        archive = ZipFile.OpenRead(path);
        try
        {
            double west = 1, east = 0, north = 1, south = 0;
            foreach (var entry in archive.Entries)
            {
                string[] parts = entry.FullName.Split('/');
                string ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                if (parts.Length != 3 || ext is not (".png" or ".jpg" or ".jpeg") ||
                    !int.TryParse(parts[0], out int z) || z is < 0 or > 16 ||
                    !int.TryParse(parts[1], out int x) || !int.TryParse(Path.GetFileNameWithoutExtension(parts[2]), out int y)) continue;
                int size = 1 << z;
                if (x < 0 || y < 0 || x >= size || y >= size || entry.Length is <= 0 or > 16777216) continue;
                entries.TryAdd((z, x, y), entry);
                west = Math.Min(west, (double)x / size);
                east = Math.Max(east, (double)(x + 1) / size);
                north = Math.Min(north, (double)y / size);
                south = Math.Max(south, (double)(y + 1) / size);
            }
            if (entries.Count == 0) throw new InvalidDataException("Choose a ZIP containing raster tiles at z/x/y.png or z/x/y.jpg (zoom 0–16).");
            levels = entries.Keys.Select(k => k.Z).Distinct().Order().ToArray();
            var first = entries.Keys.First(k => k.Z == levels[0]);
            double scale = 1 << first.Z;
            InitialView = (first.Z, (first.X + .5) / scale, (first.Y + .5) / scale);
            static double Latitude(double y) => Math.Atan(Math.Sinh(Math.PI * (1 - 2 * y))) * 180 / Math.PI;
            Coverage = $"{entries.Count:N0} tiles · Zoom {string.Join(", ", levels)}\nBounds {Latitude(south):F3}° to {Latitude(north):F3}° latitude, {west * 360 - 180:F3}° to {east * 360 - 180:F3}° longitude";
        }
        catch { archive.Dispose(); throw; }
    }

    internal int ClosestZoom(int level) => levels.MinBy(z => Math.Abs(z - level));
    internal int NextZoom(int level, int delta) => levels[Math.Clamp(Array.IndexOf(levels, ClosestZoom(level)) + delta, 0, levels.Length - 1)];

    internal Image? Image(int zoom, int x, int y)
    {
        if (!entries.TryGetValue((zoom, x, y), out var entry)) return null;
        var image = new Image { Stretch = Stretch.Fill };
        _ = Decode(image, entry);
        return image;
    }

    private static async Task Decode(Image image, ZipArchiveEntry entry)
    {
        try
        {
            using var buffer = new MemoryStream();
            using (var tile = entry.Open()) tile.CopyTo(buffer);
            buffer.Position = 0;
            using var stream = buffer.AsRandomAccessStream();
            var bitmap = new BitmapImage { DecodePixelWidth = 256 };
            await bitmap.SetSourceAsync(stream);
            image.Source = bitmap;
        }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine(error.Message); }
    }

    public void Dispose() => archive.Dispose();
}

public sealed partial class MainWindow
{
    private OfflineTilePackage? importedTiles;
    private bool restoringTiles;

    private void RestoreTiles()
    {
        restoringTiles = true;
        var old = importedTiles;
        try
        {
            importedTiles = null;
            if (Guid.TryParseExact(desktop.TilePackage, "N", out var key))
            {
                var file = Path.Combine(backend!.DataDirectory, key.ToString("N") + ".tiles.zip");
                importedTiles = new OfflineTilePackage(file);
            }
        }
        catch (Exception ex) { ShowNotice(ex.Message, InfoBarSeverity.Warning); }
        finally
        {
            OfflineChoice.IsEnabled = importedTiles is not null;
            OfflineCoverage.Text = importedTiles is null ? "Import a ZIP of XYZ raster tiles." : desktop.TileLabel + "\n" + importedTiles.Coverage;
            BasemapStyle.SelectedIndex = desktop.Offline && importedTiles is not null ? 2 : 0;
            ApplyBasemap(BasemapStyle.SelectedIndex == 2);
            old?.Dispose();
            restoringTiles = false;
        }
    }

    private async void OnImportTiles(object sender, RoutedEventArgs args) => await RunAsync(async _ =>
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".zip");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var selected = await picker.PickSingleFileAsync();
        if (selected is null || closed) return;
        string id = Guid.NewGuid().ToString("N");
        string path = Path.Combine(backend!.DataDirectory, id + ".tiles.zip");
        try
        {
            using (var source = await selected.OpenStreamForReadAsync())
            using (var target = File.Create(path)) await source.CopyToAsync(target);
            using var package = await Task.Run(() => new OfflineTilePackage(path));
            if (closed) { return; }
            var confirm = Form("Use offline basemap?", "Use package", Wrapped(selected.Name), Wrapped(package.Coverage));
            if (await confirm.ShowAsync() != ContentDialogResult.Primary || closed) return;
            var oldId = desktop.TilePackage;
            SaveDesktop(desktop with { TilePackage = id, TileLabel = selected.Name, Offline = true });
            RestoreTiles();
            if (Guid.TryParseExact(oldId, "N", out var old))
                File.Delete(Path.Combine(backend!.DataDirectory, old.ToString("N") + ".tiles.zip"));
        }
        finally { if (desktop.TilePackage != id) File.Delete(path); }
    });
}
