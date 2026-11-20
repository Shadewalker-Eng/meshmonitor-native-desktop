using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeshMonitor.Native;

internal sealed partial class MeshApi
{
    internal static bool TryRemoteNodeNumber(string id, out uint number)
    {
        number = 0;
        return id.Length == 9 && id[0] == '!' &&
            uint.TryParse(id.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out number) &&
            number is > 0 and < uint.MaxValue;
    }

    private async Task<uint> RemoteNodeNumber(SourceRow source, string nodeId)
    {
        if (!TryRemoteNodeNumber(nodeId, out uint target))
            throw new InvalidOperationException("The selected node has no valid Meshtastic address.");
        var local = RadioSection.NodeId(await ReadRadioConfiguration(source));
        if (local is null || !TryRemoteNodeNumber(local, out uint localNumber) || target == localNumber)
            throw new InvalidOperationException("Select a remote node distinct from the connected local radio.");
        return target;
    }

    internal async Task<JsonElement> ReadRemoteRadio(SourceRow source, string nodeId, RadioSection section)
    {
        uint nodeNum = await RemoteNodeNumber(source, nodeId);
        var request = new { sourceId = source.Id, nodeNum, configType = section.Key,
            channelIndex = section.ChannelIndex, desktopRemoteOnly = true };
        string route = section.ChannelIndex is not null ? "get-channel" : section.Key == "owner" ? "load-owner" : "load-config";
        var reply = await RequestAsync($"api/admin/{route}", request, timeout: TimeSpan.FromSeconds(95));
        var settings = reply.Property(route switch { "get-channel" => "channel", "load-owner" => "owner", _ => "config" });
        if (settings.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The selected radio did not return settings for this section.");
        var values = JsonNode.Parse(settings.GetRawText())!.AsObject();
        if (section.Key == "security" && values["adminKeys"] is { } keys)
        {
            values.Remove("adminKeys");
            values["adminKey"] = keys;
        }
        var snapshot = new JsonObject { ["localNodeInfo"] = new JsonObject { ["nodeId"] = nodeId } };
        if (section.Key == "owner")
        {
            values["nodeId"] = nodeId;
            snapshot["localNodeInfo"] = values;
        }
        else if (section.ChannelIndex is not null)
            snapshot["channel"] = values;
        else
            snapshot[section.Module ? "moduleConfig" : "deviceConfig"] = new JsonObject { [section.Field] = values };
        return JsonSerializer.SerializeToElement(snapshot);
    }

    internal async Task<JsonElement> StartRemoteRadioSave(SourceRow source, string nodeId, RadioSection section, JsonObject settings)
    {
        string command = section.AdminCommand ?? throw new InvalidOperationException("This section is read-only.");
        if (section.ReadOnly) throw new InvalidOperationException("This section is read-only.");
        uint nodeNum = await RemoteNodeNumber(source, nodeId);
        var body = new JsonObject
        {
            ["sourceId"] = source.Id, ["nodeNum"] = nodeNum, ["command"] = command, ["desktopRemoteOnly"] = true,
            ["verify"] = section.RequestType is not null && section.Key != "security", ["retryAttempts"] = 1
        };
        if (section.Key == "owner")
        {
            foreach (var (key, value) in settings) body[key] = value?.DeepClone();
        }
        else
        {
            if (section.ChannelIndex is int index) body["channelIndex"] = index;
            body["config"] = settings;
        }
        return await RequestAsync("api/admin/commands", body);
    }

    internal async Task TrackRemoteRadioSave(JsonElement accepted, Action<string> report, CancellationToken stopWatching,
        bool follow = true)
    {
        string? job = accepted.String("verifyJobId");
        string? operation = accepted.String("operationId");
        if (job is null && operation is null)
            throw new InvalidOperationException("The backend did not return a remote operation ID.");
        while (!stopWatching.IsCancellationRequested)
        {
            var state = job is null
                ? (await RequestAsync($"api/admin/operations/{Uri.EscapeDataString(operation!)}")).Property("data")
                : (await RequestAsync("api/admin/verify-jobs")).Property("data").EnumerateArray()
                    .FirstOrDefault(item => item.String("id") == job);
            if (stopWatching.IsCancellationRequested) return;
            string? status = state.String("status");
            string? detail = state.Property("error").String("message");
            string? progress = status switch
            {
                "pending" => "Remote save queued…",
                "awaiting_passkey" => "Requesting a session passkey…",
                "sending" => "Sending settings to the remote radio…",
                "awaiting_ack" => "Waiting for a routing acknowledgement…",
                "waiting" or "checking" => "Waiting for remote settings readback…",
                "resending" => "The backend is retrying the save after readback…",
                _ => null
            };
            if (progress is null)
            {
                report(status switch
                {
                    "verified" => "The remote radio confirmed the saved settings.",
                    "succeeded" when state.Property("result").Property("ack").Flag("acked") == true =>
                        "The remote radio acknowledged the command. Settings readback is unavailable for this command.",
                    "succeeded" => "The command was sent. Radio confirmation is unavailable for this command.",
                    "differs" => "The remote radio returned different settings.",
                    "refused" or "rejected" => detail ?? "The remote radio refused the save.",
                    "gave_up" or "timed_out" => "The remote radio did not confirm the save in time.",
                    "cancelled" => "The backend cancelled the remote save.",
                    "failed" => detail ?? "The remote save failed.",
                    _ => "The remote operation status is unavailable."
                });
                return;
            }
            report(progress);
            if (!follow) return;
            await Task.Delay(TimeSpan.FromSeconds(2), stopWatching);
        }
    }
}
