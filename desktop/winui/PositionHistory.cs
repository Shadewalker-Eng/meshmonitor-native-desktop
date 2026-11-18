using System.Text.Json;

namespace MeshMonitor.Native;

internal sealed record PositionFix(DateTimeOffset Time, double Latitude, double Longitude)
{
    internal static PositionFix? From(JsonElement row) =>
        row.Timestamp("timestamp") is { } time &&
        row.Number("latitude") is double latitude && latitude is >= -90 and <= 90 &&
        row.Number("longitude") is double longitude && longitude is >= -180 and <= 180
            ? new(time, latitude, longitude) : null;
}

internal sealed record PositionHistory(PositionFix[] Fixes, bool Limited);
