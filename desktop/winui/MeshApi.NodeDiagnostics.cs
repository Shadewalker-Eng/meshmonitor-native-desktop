using System.Text.Json;

namespace MeshMonitor.Native;

internal enum NodeReport { Names, Neighbors, Traceroutes }

internal sealed record MapLink(string From, string To, bool Return = false, bool Traceroute = false);
internal sealed record NodeRecord(string Description, MapLink[] Links);

internal sealed partial class MeshApi
{
    internal async Task<NodeRecord[]> NodeRecords(SourceRow source, uint node, NodeReport report)
    {
        switch (report)
        {
            case NodeReport.Names:
                var history = await RequestAsync($"api/nodes/{node}/name-history");
                return history.Property("data").Property("entries").EnumerateArray()
                    .Where(row => row.Property("sourceIds").EnumerateArray().Any(id => id.GetString() == source.Id))
                    .Select(row => new NodeRecord($"{ValueFormat.Time(row.Timestamp("changedAt"))}\n" +
                        $"{NodeNames(row, "old")} → {NodeNames(row, "new")}", [])).ToArray();
            case NodeReport.Neighbors:
                var neighbors = await RequestAsync(SourcePath(source, "neighbor-info"));
                return neighbors.EnumerateArray()
                    .Where(row => row.UInt32("nodeNum") == node || row.UInt32("neighborNodeNum") == node)
                    .OrderByDescending(row => row.Timestamp("timestamp"))
                    .Select(row =>
                    {
                        var first = row.String("nodeName") ?? row.String("nodeId");
                        var second = row.String("neighborName") ?? row.String("neighborNodeId");
                        string link = $"{first} → {second} · {Signal(row.Number("snr"))}";
                        if (row.Flag("bidirectional") == true)
                            link += $"\n{second} → {first} · {Signal(row.Number("reverseSnr"))} · {ValueFormat.Time(row.Timestamp("reverseTimestamp"))}";
                        string from = NodeAddress(row.UInt32("nodeNum")), to = NodeAddress(row.UInt32("neighborNodeNum"));
                        return new NodeRecord($"{ValueFormat.Time(row.Timestamp("timestamp"))}\n{link}",
                            row.Flag("bidirectional") == true ? [new(from, to), new(to, from)] : [new(from, to)]);
                    }).ToArray();
            case NodeReport.Traceroutes:
                var traces = await RequestAsync($"api/traceroutes/participation/{node}?sourceId={Uri.EscapeDataString(source.Id)}&limit=100");
                return traces.Property("data").Property("entries").EnumerateArray().Select(row =>
                {
                    string from = row.String("fromNodeId") ?? NodeAddress(row.UInt32("fromNodeNum"));
                    string to = row.String("toNodeId") ?? NodeAddress(row.UInt32("toNodeNum"));
                    var links = new List<MapLink>();
                    string forward = Route(row.Property("route"), from, to, links, false);
                    string back = Route(row.Property("routeBack"), to, from, links, true);
                    return new NodeRecord($"{ValueFormat.Time(row.Timestamp("timestamp"))} · {from} / {to}\n" +
                        $"Forward: {forward}\nReturn: {back}", links.ToArray());
                }).ToArray();
            default: throw new ArgumentOutOfRangeException(nameof(report));
        }
    }

    internal async Task<string> RequestNodeReport(SourceRow source, uint node, NodeReport report)
    {
        string path = report switch
        {
            NodeReport.Traceroutes => "api/traceroute",
            NodeReport.Neighbors => "api/neighborinfo/request",
            _ => throw new ArgumentOutOfRangeException(nameof(report))
        };
        var reply = await RequestAsync(path, new { sourceId = source.Id, destination = node });
        return reply.String("message") ?? "Request sent.";
    }

    private static string NodeNames(JsonElement row, string prefix) =>
        string.Join(" / ", new[] { row.String(prefix + "LongName"), row.String(prefix + "ShortName") }
            .Where(name => !string.IsNullOrWhiteSpace(name)).DefaultIfEmpty("Unnamed"));

    private static string Signal(double? snr) => snr is double value ? $"{value:g} dB SNR" : "SNR unavailable";
    private static string NodeAddress(uint? number) => number is null or 0 or uint.MaxValue ? "Unknown node" : $"!{number:x8}";

    private static string Route(JsonElement hops, string from, string to, List<MapLink> links, bool returning)
    {
        if (hops.ValueKind == JsonValueKind.String)
        {
            try { using var doc = JsonDocument.Parse(hops.GetString()!); hops = doc.RootElement.Clone(); }
            catch (JsonException) { return "Unavailable"; }
        }
        if (hops.ValueKind != JsonValueKind.Array) return "Unavailable";
        var path = hops.EnumerateArray().Select(hop =>
            NodeAddress(hop.ValueKind == JsonValueKind.Number && hop.TryGetUInt32(out uint value) ? value : null));
        var nodes = path.Prepend(from).Append(to).ToArray();
        for (int i = 1; i < nodes.Length; i++) links.Add(new(nodes[i - 1], nodes[i], returning, true));
        return string.Join(" → ", nodes);
    }
}
