using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MeshMonitor.Native;

internal sealed record MqttSourceSettings(bool Bridge, string Url, string Username, string Subscription, bool AutoConnect)
{
    internal BridgeForwarding Forwarding { get; init; } = BridgeForwarding.New;
    internal static MqttSourceSettings From(bool bridge, JsonElement config)
    {
        var endpoint = bridge ? config.Property("upstream") : config;
        string url = endpoint.String(bridge ? "url" : "brokerUrl") ?? "";
        string subscription = config.String("region") ?? "";
        if (bridge)
        {
            var topics = config.Property("subscriptions");
            subscription = topics.ValueKind == JsonValueKind.Array
                ? FormatTopics(string.Join("\n", topics.EnumerateArray().Where(topic => topic.ValueKind == JsonValueKind.String)
                    .Select(topic => topic.GetString()))) : "msh/#";
        }
        return new(bridge, Regex.Replace(url, @"^([a-z]+://)[^/?#]*@", "$1", RegexOptions.IgnoreCase),
            endpoint.String("username") ?? "", subscription, config.Flag("autoConnect") != false)
        { Forwarding = bridge ? BridgeForwarding.From(config) : BridgeForwarding.New };
    }

    internal bool IsValidUrl()
    {
        string address = Url.Contains("://", StringComparison.Ordinal) ? Url : $"mqtt://{Url}";
        return !Url.Any(char.IsWhiteSpace) && Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
            uri.Scheme is "mqtt" or "mqtts" or "ws" or "wss" or "tcp" or "tls" &&
            uri.Host.Length > 0 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
            uri.Port is -1 or >= 1 and <= 65535;
    }

    internal bool HasValidSubscription() => Bridge
        ? TopicFilters().Length > 0 && TopicFilters().All(IsTopicFilter)
        : Subscription.Length > 0 && !Subscription.Any(c => char.IsWhiteSpace(c) || "/+#\0".Contains(c));

    internal JsonObject Apply(string password, JsonElement config = default)
    {
        bool editing = config.ValueKind == JsonValueKind.Object;
        var json = editing ? JsonNode.Parse(config.GetRawText())!.AsObject() : new JsonObject();
        var original = editing ? From(Bridge, config) : null;
        var endpoint = json;
        if (Bridge)
        {
            if (json["upstream"] is not JsonObject upstream)
            {
                upstream = new JsonObject();
                json["upstream"] = upstream;
            }
            endpoint = upstream;
            if (original?.Forwarding != Forwarding) Forwarding.Apply(json, original?.Forwarding);
            if (original?.Subscription != Subscription)
                json["subscriptions"] = new JsonArray(TopicFilters().Select(topic => (JsonNode?)JsonValue.Create(topic)).ToArray());
        }
        else
        {
            if (original?.Subscription != Subscription) json["region"] = Subscription.ToUpperInvariant();
            if (original?.AutoConnect != AutoConnect) json["autoConnect"] = AutoConnect;
        }

        endpoint.Remove("password");
        if (original?.Url != Url)
        {
            endpoint[Bridge ? "url" : "brokerUrl"] = Url;
            if (editing) endpoint["password"] = null;
            if (Bridge) endpoint.Remove("brokerPresetId");
        }
        if (original?.Username != Username)
        {
            if (Username.Length == 0) endpoint.Remove("username");
            else endpoint["username"] = Username;
        }
        if (password.Length > 0) endpoint["password"] = password;
        return json;
    }

    private string[] TopicFilters() => SplitTopics(Subscription);

    internal static string FormatTopics(string text) => string.Join(Environment.NewLine, SplitTopics(text));

    internal static string[] SplitTopics(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal).ToArray();

    internal static bool IsTopicFilter(string topic)
    {
        if (topic.Contains('\0')) return false;
        var levels = topic.Split('/');
        for (int i = 0; i < levels.Length; i++)
        {
            string level = levels[i];
            if (level.Contains('+') && level != "+") return false;
            if (level.Contains('#') && (level != "#" || i != levels.Length - 1)) return false;
        }
        return true;
    }
}
