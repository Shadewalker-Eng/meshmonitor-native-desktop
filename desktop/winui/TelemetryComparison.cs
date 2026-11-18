using System.Text.Json;

namespace MeshMonitor.Native;

internal sealed record TelemetryLine(string SourceId, string SourceName, string Unit, TelemetryPoint[] Points);

internal sealed record TelemetryComparison(
    string NodeId, string NodeName, string Type, string Unit, bool ObserverRelative, TelemetryLine[] Lines)
{
    public string Label => $"{NodeName} · {Type}" + (Unit.Length == 0 ? "" : $" ({Unit})");
    internal (string NodeId, string Type, string Unit) Key => (NodeId, Type, Unit);

    internal static IEnumerable<TelemetryComparison> From(JsonElement row)
    {
        var lines = row.Property("lines");
        if (lines.ValueKind != JsonValueKind.Array) yield break;
        string nodeId = row.String("subjectKey") ?? "";
        string nodeName = row.String("nodeName") ?? nodeId;
        string type = row.String("type") ?? "Reading";
        bool observerRelative = row.Flag("observerRelative") == true;
        var available = lines.EnumerateArray().Select(line =>
        {
            var points = line.Property("points");
            return new TelemetryLine(line.String("sourceId") ?? "", line.String("sourceName") ?? "Source",
                line.String("unit")?.Trim() ?? "",
                points.ValueKind == JsonValueKind.Array ? ParsePoints(points.EnumerateArray()) : []);
        }).Where(line => line.Points.Length > 0);

        // A common Y axis is only meaningful if each source uses the same unit.
        foreach (var group in available.GroupBy(line => line.Unit, StringComparer.Ordinal))
            yield return new(nodeId, nodeName, type, group.Key, observerRelative,
                group.OrderBy(line => line.SourceName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(line => line.SourceId, StringComparer.Ordinal).ToArray());
    }

    internal static TelemetryPoint[] ParsePoints(IEnumerable<JsonElement> rows) => rows
        .Select(row => (Time: row.Timestamp("timestamp"), Value: row.Number("value")))
        .Where(point => point.Time is not null && point.Value is { } value && double.IsFinite(value))
        .Select(point => new TelemetryPoint(point.Time!.Value, point.Value!.Value))
        .OrderBy(point => point.Time).ToArray();

    internal static string UpdateFavorites(string? raw, string node, string type, bool enabled)
    {
        using var original = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "[]" : raw);
        if (original.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("The stored telemetry favorites are not a list.");
        var entries = original.RootElement.EnumerateArray()
            .Where(row => row.String("nodeId") != node || row.String("telemetryType") != type).ToList();
        if (enabled) entries.Add(JsonSerializer.SerializeToElement(new { nodeId = node, telemetryType = type }));
        return JsonSerializer.Serialize(entries);
    }
}
