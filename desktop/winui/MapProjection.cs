namespace MeshMonitor.Native;

internal static class MapProjection
{
    internal const int TileSize = 256;

    internal static (double X, double Y) Project(double latitude, double longitude)
    {
        double radians = Math.Clamp(latitude, -85.05112878, 85.05112878) * Math.PI / 180;
        return ((longitude + 180) / 360, Math.Clamp((1 - Math.Asinh(Math.Tan(radians)) / Math.PI) / 2, 0, 1));
    }

    internal static double Wrap(double value) => value - Math.Floor(value);
    internal static double NearestWorld(double value, double reference) => value - Math.Round(value - reference);
    internal static int WrapTile(int index, int count) => (index % count + count) % count;
    internal static double WorldSize(int zoom) => TileSize * (double)(1 << zoom);
}
