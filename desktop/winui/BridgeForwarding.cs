using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeshMonitor.Native;

internal sealed record BridgeForwarding(string Mode, string? ParentId, string PublisherMode,
    string DownlinkBlock, string UplinkAllow)
{
    internal static BridgeForwarding New { get; } = new("subscribe_only", null, "per_gateway", "", "");

    internal static BridgeForwarding From(JsonElement config) => config.ValueKind != JsonValueKind.Object ? New : new(
        config.String("mode") ?? "bidirectional", config.String("brokerSourceId"),
        config.String("forwardingMode") ?? "per_gateway", ReadTopics(config, "downlinkFilters", "block"),
        ReadTopics(config, "uplinkFilters", "allow"));

    internal void Apply(JsonObject config, BridgeForwarding? original)
    {
        if (original?.Mode != Mode) config["mode"] = Mode;
        if (original?.ParentId != ParentId)
        {
            if (ParentId is null) config.Remove("brokerSourceId");
            else config["brokerSourceId"] = ParentId;
        }
        if (original?.PublisherMode != PublisherMode) config["forwardingMode"] = PublisherMode;
        if (original?.DownlinkBlock != DownlinkBlock)
            WriteTopics(config, "downlinkFilters", "block", DownlinkBlock);
        if (original?.UplinkAllow != UplinkAllow)
            WriteTopics(config, "uplinkFilters", "allow", UplinkAllow);
    }

    private static string ReadTopics(JsonElement config, string direction, string list)
    {
        var values = config.Property(direction).Property("topics").Property(list);
        return values.ValueKind == JsonValueKind.Array
            ? MqttSourceSettings.FormatTopics(string.Join("\n", values.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()))) : "";
    }

    private static void WriteTopics(JsonObject config, string direction, string list, string text)
    {
        var rules = MqttSourceSettings.SplitTopics(text);
        if (rules.Length == 0)
        {
            if (config[direction] is JsonObject filters && filters["topics"] is JsonObject topics)
            {
                topics.Remove(list);
                if (topics.Count == 0) filters.Remove("topics");
                if (filters.Count == 0) config.Remove(direction);
            }
            return;
        }
        var next = new JsonArray(rules.Select(rule => (JsonNode?)JsonValue.Create(rule)).ToArray());
        if (config[direction] is not JsonObject existingFilters)
        {
            existingFilters = new JsonObject();
            config[direction] = existingFilters;
        }
        if (existingFilters["topics"] is not JsonObject existingTopics)
        {
            existingTopics = new JsonObject();
            existingFilters["topics"] = existingTopics;
        }
        existingTopics[list] = next;
    }
}
