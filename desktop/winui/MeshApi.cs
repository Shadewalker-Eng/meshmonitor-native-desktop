using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeshMonitor.Native;

internal sealed partial class MeshApi(Uri endpoint, CancellationToken cancellation) : IDisposable
{
    private readonly HttpClient client = new(new HttpClientHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        CookieContainer = new CookieContainer()
    }) { BaseAddress = endpoint, Timeout = Timeout.InfiniteTimeSpan };

    internal Task<JsonElement> AuthStatus() => RequestAsync("api/auth/status");
    internal Task<JsonElement> SignIn(string username, string password) =>
        RequestAsync("api/auth/login", new { username, password });
    internal Task<JsonElement> VerifyCode(string token) =>
        RequestAsync("api/auth/verify-mfa", new { token });
    internal Task<JsonElement> SignOut() => RequestAsync("api/auth/logout", new { });
    internal Task<JsonElement> ChangePassword(string currentPassword, string newPassword) =>
        RequestAsync("api/auth/change-password", new { currentPassword, newPassword });
    internal async Task<SourceRow[]> Sources() =>
        (await RequestAsync("api/sources")).EnumerateArray().Select(SourceRow.From).ToArray();
    internal Task<JsonElement> SourceStatus(SourceRow source) =>
        RequestAsync(SourcePath(source, "status"));
    internal Task<JsonElement> ReadRadioConfiguration(SourceRow source) =>
        RequestAsync($"api/config/current?sourceId={Uri.EscapeDataString(source.Id)}");
    internal async Task<JsonElement> ReadRadioSection(SourceRow source, RadioSection section, bool administrator = false)
    {
        var snapshot = await ReadRadioConfiguration(source);
        if (section.Key == "owner" && RadioSection.NodeId(snapshot) is { } nodeId)
        {
            JsonElement owner;
            if (administrator)
                owner = (await RequestAsync("api/admin/load-owner", new { sourceId = source.Id })).Property("owner");
            else
            {
                try
                {
                    var nodes = await RequestAsync(SourcePath(source, "nodes"));
                    owner = nodes.EnumerateArray().FirstOrDefault(node => node.String("nodeId") == nodeId);
                }
                catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                {
                    return snapshot;
                }
            }
            var configuration = JsonNode.Parse(snapshot.GetRawText())!.AsObject();
            var local = configuration["localNodeInfo"]!.AsObject();
            foreach (string key in new[] { "isUnmessagable", "isLicensed" })
                if (owner.Flag(key) is bool value) local[key] = value;
            return JsonSerializer.SerializeToElement(configuration);
        }
        if (section.Key == "fixed-position" && RadioSection.NodeId(snapshot) is { } localId)
        {
            var configuration = JsonNode.Parse(snapshot.GetRawText())!.AsObject();
            var position = new JsonObject();
            if (snapshot.Property("deviceConfig").Property("position").Flag("fixedPosition") == true)
            {
                var nodes = await RequestAsync(SourcePath(source, "nodes"));
                var local = nodes.EnumerateArray().FirstOrDefault(node => node.String("nodeId") == localId);
                foreach (string key in new[] { "latitude", "longitude", "altitude" })
                {
                    var coordinate = local.Property(key);
                    if (coordinate.ValueKind == JsonValueKind.Number) position[key] = JsonNode.Parse(coordinate.GetRawText());
                }
            }
            configuration["fixedPosition"] = position;
            return JsonSerializer.SerializeToElement(configuration);
        }
        if (section.ChannelIndex is not int index) return snapshot;
        var result = await RequestAsync("api/admin/get-channel", new { sourceId = source.Id, channelIndex = index });
        var merged = JsonNode.Parse(snapshot.GetRawText())!.AsObject();
        var channel = result.Property("channel");
        if (channel.ValueKind == JsonValueKind.Object)
            merged["channel"] = JsonNode.Parse(channel.GetRawText());
        return JsonSerializer.SerializeToElement(merged);
    }
    internal async Task<JsonElement> ReadMeshCoreRadioSection(SourceRow source, RadioSection section)
    {
        if ((await SourceStatus(source)).Flag("connected") != true)
            return JsonSerializer.SerializeToElement(new { connected = false });

        var status = (await RequestAsync(SourcePath(source, "meshcore/status"))).Property("data");
        var node = status.Property("localNode");
        var values = new Dictionary<string, JsonElement>();
        if (status.Flag("connected") == true && node.String("publicKey") is not null)
        {
            var reported = node;
            if (section.MeshCoreReadPath is { } route)
            {
                var reply = await RequestAsync(SourcePath(source, $"meshcore/config/{route}"));
                reported = reply.Property("data").ValueKind == JsonValueKind.Object ? reply.Property("data") : reply;
            }
            foreach (var field in section.Fields)
            {
                string key = field.Key switch
                {
                    "freq" => "radioFreq", "bw" => "radioBw", "sf" => "radioSf", "cr" => "radioCr",
                    "power" => "txPower", "lat" => "latitude", "lon" => "longitude", "policy" => "advLocPolicy",
                    "value" => "multiAcks",
                    "mode" when section.Key == "telemetry-mode-base" => "telemetryModeBase",
                    "mode" when section.Key == "telemetry-mode-loc" => "telemetryModeLoc",
                    "mode" => "telemetryModeEnv",
                    _ => field.Key
                };
                var value = reported.Property(key);
                if (section.Key == "auto-add-contacts" && reported.Number("manualAddContacts") is double bits)
                    value = JsonSerializer.SerializeToElement(((int)bits & 1) == 0);
                if (value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)) values.Add(field.Key, value);
            }
        }
        return JsonSerializer.SerializeToElement(new
        {
            connected = status.Flag("connected") == true,
            localNodeInfo = new { nodeId = node.String("publicKey") },
            deviceConfig = values.Count == section.Fields.Length
                ? new Dictionary<string, object> { [section.Key] = values } : null
        });
    }

    internal Task<JsonElement> SaveRadioConfiguration(SourceRow source, RadioSection section, JsonObject settings)
    {
        if (source.Type == "meshcore")
            return RequestAsync(SourcePath(source, $"meshcore/config/{section.Key}"), settings);
        if (section.Key == "fixed-position")
        {
            settings["sourceId"] = source.Id;
            settings["command"] = section.AdminCommand;
            return RequestAsync("api/admin/commands", settings);
        }
        if (section.ChannelIndex is int index)
            return RequestAsync("api/admin/commands",
                new { sourceId = source.Id, command = "setChannel", channelIndex = index, config = settings });
        if (section.AdminCommand is { } command)
            return RequestAsync("api/admin/commands", new { sourceId = source.Id, command, config = settings });
        settings["sourceId"] = source.Id;
        return RequestAsync($"api/config/{section.SavePath}", settings);
    }
    internal Task<JsonElement> ReadModuleFromRadio(SourceRow source, int configType, string nodeId) =>
        RequestAsync("api/config/local-module-readback", new { sourceId = source.Id, configType, nodeId });

    internal Task<JsonElement> RequestRadioConfiguration(SourceRow source, int configType, bool module = false) =>
        RequestAsync(module ? "api/config/module/request" : "api/config/request", new { sourceId = source.Id, configType });
    internal Task<JsonElement> SourceConfiguration(SourceRow source) =>
        RequestAsync(SourcePath(source));
    internal Task<JsonElement> UpdateSource(SourceRow source, Dictionary<string, object> changes) =>
        RequestAsync(SourcePath(source), changes, HttpMethod.Put);
    internal Task<JsonElement> SetConnected(SourceRow source, bool connect) =>
        RequestAsync(SourcePath(source, connect ? "connect" : "disconnect"), new { });
    internal Task<JsonElement> AddMeshtastic(string name, string host, int port) =>
        RequestAsync("api/sources", new { name, type = "meshtastic_tcp", config = new { host, port }, enabled = true });
    internal Task<JsonElement> AddMeshCore(string name, MeshCoreConnection connection) =>
        RequestAsync("api/sources", new { name, type = "meshcore", config = connection.Apply(), enabled = true });
    internal Task<JsonElement> AddMqttSource(string name, bool bridge, JsonObject config, bool enabled) =>
        RequestAsync("api/sources", new { name, type = bridge ? "mqtt_bridge" : "meshcore_mqtt", config, enabled });
    internal Task<JsonElement> AddEmbeddedBroker(string name, EmbeddedBrokerSettings settings, string password,
        bool enabled) => RequestAsync("api/sources", new
        {
            name, type = settings.MeshCore ? "meshcore_mqtt_broker" : "mqtt_broker",
            config = settings.Apply(name, password), enabled
        });

    internal async Task<NodeRow[]> MapNodes(SourceRow source)
    {
        string path = source.IsUnified ? "api/unified/dashboard" : SourcePath(source, "dashboard");
        return (await DashboardNodes(path + "?ageWindow=settings", source.IsUnified))
            .Where(node => node.Latitude is >= -90 and <= 90 && node.Longitude is >= -180 and <= 180)
            .Select(node => node.SourceId.Length == 0 && !source.IsUnified ? node with { SourceId = source.Id } : node)
            .ToArray();
    }

    private async Task<NodeRow[]> DashboardNodes(string path, bool unified)
    {
        var snapshot = await RequestAsync(path);
        IEnumerable<JsonElement> bundles = unified ? snapshot.EnumerateArray() : [snapshot];
        return bundles.SelectMany(bundle => Rows(bundle.Property("nodes")).Select(row =>
            NodeRow.From(row) with { SourceId = bundle.String("sourceId") ?? row.String("sourceId") ?? "" })).ToArray();
    }

    internal async Task<PositionHistory> NodePositionHistory(SourceRow source, NodeRow node, int hours)
    {
        const int maxFixes = 4000;
        const int maxPages = 20;
        if (source.IsUnified || source.Id != node.SourceId)
            throw new InvalidOperationException("Position history requires the node's source.");

        var fixes = new List<PositionFix>();
        bool limited = false;
        if (source.IsMeshCore)
        {
            var key = Uri.EscapeDataString(node.MeshCoreKey ?? node.Id);
            long since = DateTimeOffset.UtcNow.AddHours(-hours).ToUnixTimeMilliseconds();
            var history = await RequestAsync(SourcePath(source, $"meshcore/nodes/{key}/position-history?since={since}"));
            fixes.AddRange(Rows(history.Property("data")).Select(PositionFix.From).OfType<PositionFix>());
        }
        else
        {
            string id = Uri.EscapeDataString(node.Id);
            string path = $"api/nodes/{id}/position-history?sourceId={Uri.EscapeDataString(source.Id)}&hours={hours}";
            long? before = null;
            for (int page = 0; page < maxPages; page++)
            {
                var rows = Rows(await RequestAsync(before is { } cursor ? $"{path}&before={cursor}" : path)).ToArray();
                if (rows.Length == 0) break;
                fixes.AddRange(rows.Select(PositionFix.From).OfType<PositionFix>());
                if (fixes.Count >= maxFixes || page == maxPages - 1 ||
                    rows[0].Timestamp("timestamp") is not { } oldest || oldest.ToUnixTimeMilliseconds() >= before)
                {
                    limited = true;
                    break;
                }
                before = oldest.ToUnixTimeMilliseconds();
            }
        }
        return new(fixes.Distinct().OrderBy(fix => fix.Time).TakeLast(maxFixes).ToArray(),
            limited || fixes.Count > maxFixes);
    }

    internal async Task<TelemetryRow[]> Telemetry(SourceRow source)
    {
        var readings = await RequestAsync("api/unified/telemetry?hours=24");
        return Rows(readings).Where(item => source.IsUnified || item.String("sourceId") == source.Id)
            .Select(TelemetryRow.From).ToArray();
    }

    internal async Task<TelemetryPoint[]> TelemetryHistory(TelemetryRow reading, int hours)
    {
        var rows = await NodeTelemetryRows(reading.SourceId, reading.NodeId, hours);
        return TelemetryComparison.ParsePoints(Rows(rows).Where(row => row.String("telemetryType") == reading.Type &&
            (row.String("unit") is not { Length: > 0 } unit || unit == reading.Unit)));
    }

    private Task<JsonElement> NodeTelemetryRows(string source, string node, int hours) =>
        RequestAsync($"api/telemetry/{Uri.EscapeDataString(node)}?sourceId={Uri.EscapeDataString(source)}&hours={hours}");

    internal async Task<TelemetryRow[]> NodeMetrics(string source, NodeRow node)
    {
        var rows = await NodeTelemetryRows(source, node.Id, 168);
        return Rows(rows).Select(TelemetryRow.From).Where(row => row.Type.Length > 0)
            .GroupBy(row => (row.Type, row.Unit)).Select(group => group.First() with
                { SourceId = source, NodeId = node.Id, Node = node.Name, Reading = group.Key.Type, Context = "" })
            .OrderBy(row => row.Type).ToArray();
    }

    internal async Task SetTelemetryFavorite(TelemetryRow reading, bool enabled)
    {
        if (!reading.HasHistory || !reading.CanEditFavorite)
            throw new InvalidOperationException("This reading's source does not allow editing its favorites.");
        string path = $"api/settings?sourceId={Uri.EscapeDataString(reading.SourceId)}";
        var settings = await RequestAsync(path);
        string updated = TelemetryComparison.UpdateFavorites(
            settings.String("telemetryFavorites"), reading.NodeId, reading.Type, enabled);
        await RequestAsync(path, new { telemetryFavorites = updated });
    }

    internal async Task<TelemetryComparison[]> TelemetryComparisons(int hours)
    {
        var result = await RequestAsync($"api/unified/telemetry/series?hours={hours}");
        return Rows(result.Property("data").Property("series")).SelectMany(TelemetryComparison.From)
            .OrderBy(chart => chart.NodeName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(chart => chart.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(chart => chart.Unit, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal async Task<DeliveryEvent[]> DeliveryHistory(SourceRow source, MessageRow message)
    {
        string id = Uri.EscapeDataString(message.Id);
        var result = await RequestAsync(SourcePath(source, $"messages/{id}/events"));
        return Rows(result.Property("data").Property("events"))
            .Select(DeliveryEvent.From).OrderBy(item => item.Time).ToArray();
    }

    internal Task<JsonElement> Send(SourceRow source, string text, int channel, string? destination, uint? replyId = null) => source.Type switch
    {
        "meshtastic_tcp" => RequestAsync("api/messages/send", MeshtasticSendBody(source, text, channel, destination, replyId)),
        "meshcore" when replyId is null => RequestAsync(SourcePath(source, "meshcore/messages/send"),
            new { text, channelIdx = channel, toPublicKey = destination }),
        _ => throw new InvalidOperationException("This source does not have a supported native send path.")
    };

    internal static Dictionary<string, object?> MeshtasticSendBody(SourceRow source, string text, int channel,
        string? destination, uint? replyId)
    {
        var body = new Dictionary<string, object?>
        {
            ["sourceId"] = source.Id, ["text"] = text, ["channel"] = channel, ["destination"] = destination
        };
        if (replyId is { } packet) body["replyId"] = packet;
        return body;
    }

    internal Task<JsonElement> SendReaction(SourceRow source, Conversation thread, uint packet, string emoji)
    {
        var body = MeshtasticSendBody(source, emoji, thread.Channel ?? 0, thread.Peer, packet);
        body["emoji"] = 1;
        return RequestAsync("api/messages/send", body);
    }

    internal async Task<Conversation[]> Channels(SourceRow source)
    {
        var result = await RequestAsync(SourcePath(source, "channels"));
        return Rows(result).Where(row => row.Number("id") is >= 0 and <= 7 && row.Number("role") != 0)
            .Select(row =>
            {
                int index = (int)row.Number("id")!.Value;
                string? name = row.String("displayName") ?? row.String("name");
                return Conversation.ForChannel(index, name);
            }).ToArray();
    }

    internal async Task<Conversation[]> MeshCorePeers(SourceRow source)
    {
        var result = await RequestAsync(SourcePath(source, "meshcore/messages/dm-peers"));
        return Rows(result).Select(row => row.String("publicKey")).OfType<string>()
            .Where(key => Conversation.ValidPeer(key, meshCore: true))
            .Select(key => Conversation.ForPeer(key)).ToArray();
    }

    internal Task<JsonElement> UnreadCounts(SourceRow source) =>
        RequestAsync($"api/messages/unread-counts?sourceId={Uri.EscapeDataString(source.Id)}");

    internal async Task MarkDirectRead(SourceRow source, Conversation conversation, string local)
    {
        // The bundled peer-wide mark operation omits sourceId in its database query.
        // Use only IDs returned by the source-scoped direct conversation.
        string path = MessagePath(source, conversation, local).Replace("limit=100", "limit=500");
        for (int offset = 0; offset <= 50000; offset += 500)
        {
            var page = await RequestAsync($"{path}&offset={offset}");
            var ids = Rows(page.Property("messages")).Where(row =>
                string.Equals(row.String("fromNodeId"), conversation.Peer, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(row.String("toNodeId"), local, StringComparison.OrdinalIgnoreCase))
                .Select(row => row.String("id")).OfType<string>().Where(id => id.Length > 0).Distinct().ToArray();
            if (ids.Length > 0)
                await RequestAsync("api/messages/mark-read", new { sourceId = source.Id, messageIds = ids });
            if (page.Flag("hasMore") != true) return;
            var remaining = (await UnreadCounts(source)).Property("directMessages");
            if (remaining.ValueKind == JsonValueKind.Object && remaining.Number(conversation.Peer!) is null or 0) return;
        }
        throw new InvalidOperationException("Reached the backend history limit. Older messages may still be unread.");
    }

    internal Task<JsonElement> MarkChannelRead(SourceRow source, int channel) =>
        RequestAsync("api/messages/mark-read", new { sourceId = source.Id, channelId = channel });
    internal async Task<NodeRow[]> Nodes(SourceRow source)
    {
        if (source.IsUnified)
            return (await DashboardNodes("api/unified/dashboard", unified: true))
                .OrderByDescending(row => row.Timestamp).ToArray();
        var result = await RequestAsync(SourcePath(source, source.IsMeshCore ? "meshcore/nodes" : "nodes"));
        return Rows(result).Select(NodeRow.From).OrderByDescending(row => row.Timestamp).ToArray();
    }

    internal async Task<MessageRow[]> Messages(SourceRow source, Conversation? conversation = null, string? localNode = null)
        => FormatMessages(source, (await ReadMessagePage(source, conversation ?? Conversation.Recent, localNode)).Rows);

    internal async Task<MessagePage> ReadMessagePage(SourceRow source, Conversation conversation, string? localNode,
        int offset = 0)
    {
        string path = MessagePath(source, conversation, localNode);
        if (offset > 0) path += $"&offset={offset}";
        var result = await RequestAsync(path);
        var rows = result.Property("messages");
        var entries = Rows(rows.ValueKind == JsonValueKind.Array ? rows : result).Select(MessageRow.From).ToArray();
        // MeshCore channel/direct endpoints return their newest page oldest first.
        if (source.IsMeshCore && !conversation.IsRecent) Array.Reverse(entries);
        return new(entries, !conversation.IsRecent && result.Flag("hasMore") == true);
    }

    internal static MessageRow[] FormatMessages(SourceRow source, IEnumerable<MessageRow> rows)
    {
        var messages = rows
            .Where(message => !string.IsNullOrWhiteSpace(message.Text))
            .OrderByDescending(message => message.Timestamp).ToArray();
        bool meshtastic = source.Type == "meshtastic_tcp";
        if (meshtastic) messages = MessageReactions.Attach(messages);
        var quoted = messages.Where(message => message.PacketId is not null)
            .ToLookup(message => (message.PacketId!.Value, message.Channel, Conversation.DirectPair(message)));
        return messages.Select(message =>
        {
            if (message.ReplyId is not { } reply) return message;
            string? pair = Conversation.DirectPair(message);
            var candidates = source.IsUnified || (message.Channel == -1 && pair is null)
                ? Array.Empty<MessageRow>() : quoted[(reply, message.Channel, pair)].Take(2).ToArray();
            return message with { ReplyPreview = candidates.Length == 1
                ? $"Reply to {candidates[0].Sender} · {Conversation.Quote(candidates[0].Text)}"
                : $"Reply to packet {reply}" };
        }).ToArray();
    }

    internal static string MessagePath(SourceRow source, Conversation conversation, string? localNode)
    {
        if (source.IsUnified) return "api/unified/messages?channel=__all__&limit=100";
        string scope = $"?sourceId={Uri.EscapeDataString(source.Id)}&limit=100";
        if (conversation.Peer is { } peer)
        {
            string recipient = Uri.EscapeDataString(peer);
            if (source.IsMeshCore) return SourcePath(source, $"meshcore/messages/dm/{recipient}?limit=100");
            if (localNode is null || !Conversation.ValidPeer(localNode, meshCore: false))
                throw new InvalidOperationException("The source must report its local node ID before opening a direct conversation.");
            return $"api/messages/direct/{Uri.EscapeDataString(localNode)}/{recipient}{scope}";
        }
        if (conversation.Channel is int channel)
            return source.IsMeshCore ? SourcePath(source, $"meshcore/messages/channel/{channel}?limit=100") :
                $"api/messages/channel/{channel}{scope}";
        if (source.IsMeshCore) return SourcePath(source, "meshcore/messages?limit=100");
        return "api/messages" + scope;
    }

    internal async Task<JsonElement> RequestAsync(string path, object? body = null, HttpMethod? method = null,
        TimeSpan? timeout = null)
    {
        using var request = new HttpRequestMessage(method ?? (body is null ? HttpMethod.Get : HttpMethod.Post), path);
        if (body is not null)
        {
            string token = (await RequestAsync("api/csrf-token")).String("csrfToken")
                ?? throw new InvalidOperationException("MeshMonitor did not issue a request token.");
            request.Headers.Add("X-CSRF-Token", token);
            request.Content = JsonContent.Create(body);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(12));
        using var response = await client.SendAsync(request, deadline.Token);
        JsonElement result = default;
        var content = await response.Content.ReadAsByteArrayAsync(deadline.Token);
        if (content.Length != 0)
        {
            try
            {
                using var document = JsonDocument.Parse(content);
                result = document.RootElement.Clone();
            }
            catch (JsonException) when (!response.IsSuccessStatusCode) { }
        }

        if (!response.IsSuccessStatusCode || result.Flag("success") == false)
        {
            string explanation = result.String("message") ?? result.String("error")
                ?? $"MeshMonitor returned HTTP {(int)response.StatusCode}.";
            throw new HttpRequestException(explanation, null, response.StatusCode);
        }
        return result;
    }

    private static string SourcePath(SourceRow source, string? action = null) =>
        $"api/sources/{Uri.EscapeDataString(source.Id)}" + (action is null ? "" : "/" + action);

    private static JsonElement.ArrayEnumerator Rows(JsonElement value) =>
        (value.ValueKind == JsonValueKind.Array ? value : value.Property("data")).EnumerateArray();

    public void Dispose() => client.Dispose();
}

public sealed record SourceRow(string Id, string Name, string Type, bool Enabled)
{
    public static SourceRow Unified { get; } = new("", "Unified", "unified", true);
    public bool IsUnified => Type == "unified";
    public bool CanTransmit => Type is "meshtastic_tcp" or "meshcore";
    internal bool CanConnect => Type is "meshtastic_tcp" or "meshcore" or "meshcore_mqtt" or
        "meshcore_mqtt_broker" or "mqtt_bridge" or "reticulum";
    public bool IsMeshCore => Type.StartsWith("meshcore", StringComparison.Ordinal);
    internal bool IsEmbeddedBroker => Type is "mqtt_broker" or "meshcore_mqtt_broker";
    private string? Transport { get; init; }
    public string Subtitle => Type switch
    {
        "meshtastic_tcp" => "Meshtastic · TCP",
        "mqtt_bridge" => "MQTT bridge",
        "mqtt_broker" => "MQTT broker",
        "meshcore" => Transport is "tcp" ? "MeshCore · TCP" :
            Transport is "usb" or "serial" ? "MeshCore · USB / serial" : "MeshCore",
        "meshcore_mqtt" => "MeshCore · MQTT",
        "meshcore_mqtt_broker" => "MeshCore · MQTT broker",
        "reticulum" => "Reticulum",
        "unified" => "All sources",
        _ => Type
    };
    public string Description => Enabled ? Subtitle : $"{Subtitle} · Disabled";

    internal static SourceRow From(JsonElement row) => new(
        row.String("id") ?? throw new JsonException("Source ID missing."),
        row.String("name") ?? "Unnamed source", row.String("type") ?? "", row.Flag("enabled") == true)
        { Transport = row.Property("config").String("transport") };
}

public sealed record NodeRow(string Id, string Name, string ShortName, DateTimeOffset? Timestamp,
    double? Snr, double? Rssi, double? Battery, double? Latitude, double? Longitude)
{
    public string SourceId { get; init; } = "";
    internal string? MeshCoreKey { get; init; }
    public string Summary => $"{Id} · {ValueFormat.Time(Timestamp)}";
    public string Signal => Snr is { } snr ? $"{snr:g} dB SNR" : "—";

    internal static NodeRow From(JsonElement row)
    {
        var user = row.Property("user");
        var position = row.Property("position");
        string id = row.String("nodeId") ?? row.String("publicKey") ?? user.String("id")
            ?? row.Number("nodeNum")?.ToString() ?? "Unknown";
        return new NodeRow(id, row.String("longName") ?? row.String("name") ?? user.String("longName") ?? id,
            row.String("shortName") ?? user.String("shortName") ?? "",
            row.Timestamp("lastHeard"), row.Number("snr"), row.Number("rssi"), row.Number("batteryLevel"),
            row.Number("latitude") ?? position.Number("latitude"),
            row.Number("longitude") ?? position.Number("longitude"))
        { SourceId = row.String("sourceId") ?? "", MeshCoreKey = row.String("publicKey") };
    }
}

public sealed record MessageRow(string Sender, string Text, DateTimeOffset? Timestamp)
{
    public string Id { get; init; } = "";
    internal string? DeliveryState { get; init; }
    internal string? ProtocolDeliveryStatus { get; init; }
    internal bool RoutingFailed { get; init; }
    internal bool WantsAcknowledgment { get; init; }
    internal uint? AcknowledgmentSender { get; init; }
    public string Delivery => MessageAcknowledgment.Summary(this);
    public string? FromId { get; init; }
    public string? ToId { get; init; }
    public int? Channel { get; init; }
    public uint? PacketId { get; init; }
    public uint? ReplyId { get; init; }
    public string Reactions { get; init; } = "";
    public Microsoft.UI.Xaml.Visibility ReactionVisibility => Reactions.Length == 0 ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    internal bool EmojiFlag { get; init; }
    public string ReplyPreview { get; init; } = "";
    public string Summary => $"{Sender} · {ValueFormat.Time(Timestamp)}";

    internal static MessageRow From(JsonElement row) => new MessageRow(
        row.String("senderName") ?? row.String("fromNodeLongName") ?? row.String("name") ?? row.String("fromNodeId")
            ?? row.String("senderPublicKey") ?? "Unknown sender",
        row.String("text") ?? "", row.Timestamp("timestamp") ?? row.Timestamp("createdAt"))
    {
        Id = row.String("id") ?? "",
        FromId = row.String("fromNodeId") ?? row.String("fromPublicKey"),
        ToId = row.String("toNodeId") ?? row.String("toPublicKey"),
        Channel = Conversation.MessageChannel(row),
        PacketId = row.UInt32("packetId") is uint packet && packet > 0
            ? packet : Conversation.PacketFromStoredId(row.String("id")),
        ReplyId = row.UInt32("replyId") is uint reply && reply > 0 ? reply : null,
        EmojiFlag = row.Number("emoji") == 1 || row.Flag("emoji") == true,
        DeliveryState = row.String("deliveryState"),
        ProtocolDeliveryStatus = row.String("deliveryStatus"),
        RoutingFailed = row.Flag("ackFailed") == true || row.Flag("routingErrorReceived") == true,
        WantsAcknowledgment = row.Flag("wantAck") == true,
        AcknowledgmentSender = row.UInt32("ackFromNode")
    };
}

public sealed record TelemetryRow(string Node, string Reading, string Context)
{
    public string NodeId { get; init; } = "";
    public string SourceId { get; init; } = "";
    public string Type { get; init; } = "";
    public string Unit { get; init; } = "";
    public bool Favorited { get; init; }
    public bool CanEditFavorite { get; init; }
    public string FavoriteLabel => Favorited ? "★ Favorite" : "";
    public bool HasHistory => NodeId.Length > 0 && SourceId.Length > 0 && Type.Length > 0;

    internal static TelemetryRow From(JsonElement row)
    {
        string type = row.String("telemetryType") ?? "Reading";
        string value = row.Number("value") is { } number ? number.ToString("G6") : "—";
        string unit = row.String("unit") ?? "";
        string source = row.String("sourceName") ?? "Source";
        return new TelemetryRow(row.String("nodeLongName") ?? row.String("nodeShortName") ?? row.String("nodeId") ?? "Node",
            $"{type} · {value} {unit}".TrimEnd(),
            $"{source} · {ValueFormat.Time(row.Timestamp("timestamp"))}")
        {
            NodeId = row.String("nodeId") ?? "",
            SourceId = row.String("sourceId") ?? "",
            Type = row.String("telemetryType") ?? "",
            Unit = unit,
            Favorited = row.Flag("favorited") == true,
            CanEditFavorite = row.Flag("canEditFavorite") == true
        };
    }
}

internal readonly record struct TelemetryPoint(DateTimeOffset Time, double Value);

internal sealed record DeliveryEvent(DateTimeOffset? Time, string Type, string Provenance, string? Detail)
{
    internal string Description => Type switch
    {
        "submitted" => "Submitted",
        "sent_to_radio" => "Sent to radio",
        "delivered" => "Delivery reported",
        "confirmed" => "Recipient acknowledgment",
        "routing_error" => "Routing error",
        "timeout" => "Acknowledgment timed out",
        "retry" => "Retry",
        _ => Type.Replace('_', ' ')
    };

    internal static DeliveryEvent From(JsonElement row) => new(row.Timestamp("timestamp"),
        row.String("eventType") ?? "Event", row.String("provenance") ?? "", row.String("detail"));
}

internal static class ValueFormat
{
    internal static string Time(DateTimeOffset? value) =>
        value?.ToLocalTime().ToString("g") ?? "Time unavailable";

    internal static JsonElement Property(this JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
            ? property : default;
    internal static string? String(this JsonElement value, string name)
    {
        var property = value.Property(name);
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }
    internal static bool? Flag(this JsonElement value, string name) => value.Property(name).ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };
    internal static double? Number(this JsonElement value, string name)
    {
        var property = value.Property(name);
        return property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out double number)
            ? number : null;
    }
    internal static DateTimeOffset? Timestamp(this JsonElement value, string name)
    {
        if (value.String(name) is { } text && DateTimeOffset.TryParse(text,
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind,
            out var date))
            return date;
        if (value.Number(name) is not { } number || number <= 0 || number > 253402300799999)
            return null;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)(number < 100_000_000_000 ? number * 1000 : number));
    }

    internal static uint? UInt32(this JsonElement value, string name) =>
        value.Number(name) is { } number && number is >= 0 and <= uint.MaxValue && number == Math.Truncate(number)
            ? (uint)number : null;
}
