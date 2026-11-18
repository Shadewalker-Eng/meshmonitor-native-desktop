using System.Text.Json;

namespace MeshMonitor.Native;

public sealed record Conversation(string Label, int? Channel = null, string? Peer = null, int Unread = 0)
{
    public static Conversation Recent { get; } = new("Recent messages");
    public bool IsRecent => Channel is null && Peer is null;
    public string Key => Peer is { } peer ? $"peer:{peer.ToLowerInvariant()}" : Channel is int channel ? $"channel:{channel}" : "recent";
    public string Display => Unread > 0 ? $"{Label} · {Unread} unread" : Label;

    internal static Conversation ForChannel(int channel, string? name = null) =>
        new(string.IsNullOrWhiteSpace(name) ? $"Channel {channel}" : $"{name} · Channel {channel}", Channel: channel);

    internal static Conversation ForPeer(string peer, string? name = null) =>
        new(string.IsNullOrWhiteSpace(name) || name == peer ? $"Direct · {ShortId(peer)}" : $"Direct · {name} ({ShortId(peer)})",
            Peer: peer.ToLowerInvariant());

    internal static bool ValidPeer(string value, bool meshCore) =>
        meshCore ? value.Length == 64 && value.All(Uri.IsHexDigit) :
        value.Length == 9 && value[0] == '!' && value.Skip(1).All(Uri.IsHexDigit);

    internal static string? PeerFor(MessageRow message, string localNode)
    {
        if (message.Channel != -1) return null;
        if (string.Equals(message.FromId, localNode, StringComparison.OrdinalIgnoreCase))
            return message.ToId;
        if (string.Equals(message.ToId, localNode, StringComparison.OrdinalIgnoreCase))
            return message.FromId;
        return null;
    }

    internal static string? DirectPair(MessageRow message)
    {
        if (message.Channel != -1 || message.FromId is not { } from || message.ToId is not { } to)
            return null;
        return string.Compare(from, to, StringComparison.OrdinalIgnoreCase) <= 0
            ? $"{from.ToLowerInvariant()}:{to.ToLowerInvariant()}"
            : $"{to.ToLowerInvariant()}:{from.ToLowerInvariant()}";
    }

    internal static uint? PacketFromStoredId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var parts = id.Split('_');
        return parts.Length >= 2 && uint.TryParse(parts[^2], out _) &&
            uint.TryParse(parts[^1], out uint packet) && packet > 0 ? packet : null;
    }

    internal static int? MessageChannel(JsonElement row)
    {
        if ((row.Number("channel") ?? row.Number("channelIdx")) is { } channel &&
            channel is >= -1 and <= 65535 && channel == Math.Truncate(channel))
            return (int)channel;
        foreach (var address in new[] { row.String("toPublicKey"), row.String("fromPublicKey") })
            if (address is not null && address.StartsWith("channel-", StringComparison.Ordinal) &&
                int.TryParse(address.AsSpan(8), out int index) && index is >= 0 and <= 255)
                return index;
        return null;
    }

    internal static string Quote(string text)
    {
        string line = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        return line.Length <= 96 ? line : line[..96] + "…";
    }

    private static string ShortId(string id) => id.Length > 12 ? id[..12] + "…" : id;
}
