using System.Text;

namespace MeshMonitor.Native;

internal static class MessageReactions
{
    // Unicode Extended_Pictographic and Emoji_Presentation ranges used by the bundled backend.
    private static readonly (int First, int Last)[] EmojiRanges =
    [
        (0xa9, 0xa9), (0xae, 0xae), (0x203c, 0x203c), (0x2049, 0x2049), (0x2122, 0x2122),
        (0x2139, 0x2139), (0x2194, 0x2199), (0x21a9, 0x21aa), (0x231a, 0x231b), (0x2328, 0x2328),
        (0x2388, 0x2388), (0x23cf, 0x23cf), (0x23e9, 0x23f3), (0x23f8, 0x23fa), (0x24c2, 0x24c2),
        (0x25aa, 0x25ab), (0x25b6, 0x25b6), (0x25c0, 0x25c0), (0x25fb, 0x25fe), (0x2600, 0x2605),
        (0x2607, 0x2612), (0x2614, 0x2685), (0x2690, 0x2705), (0x2708, 0x2712), (0x2714, 0x2714),
        (0x2716, 0x2716), (0x271d, 0x271d), (0x2721, 0x2721), (0x2728, 0x2728), (0x2733, 0x2734),
        (0x2744, 0x2744), (0x2747, 0x2747), (0x274c, 0x274c), (0x274e, 0x274e), (0x2753, 0x2755),
        (0x2757, 0x2757), (0x2763, 0x2767), (0x2795, 0x2797), (0x27a1, 0x27a1), (0x27b0, 0x27b0),
        (0x27bf, 0x27bf), (0x2934, 0x2935), (0x2b05, 0x2b07), (0x2b1b, 0x2b1c), (0x2b50, 0x2b50),
        (0x2b55, 0x2b55), (0x3030, 0x3030), (0x303d, 0x303d), (0x3297, 0x3297), (0x3299, 0x3299),
        (0x1f000, 0x1f0ff), (0x1f10d, 0x1f10f), (0x1f12f, 0x1f12f), (0x1f16c, 0x1f171), (0x1f17e, 0x1f17f),
        (0x1f18e, 0x1f18e), (0x1f191, 0x1f19a), (0x1f1ad, 0x1f1ff), (0x1f201, 0x1f20f), (0x1f21a, 0x1f21a),
        (0x1f22f, 0x1f22f), (0x1f232, 0x1f23a), (0x1f23c, 0x1f23f), (0x1f249, 0x1f53d), (0x1f546, 0x1f64f),
        (0x1f680, 0x1f6ff), (0x1f774, 0x1f77f), (0x1f7d5, 0x1f7ff), (0x1f80c, 0x1f80f), (0x1f848, 0x1f84f),
        (0x1f85a, 0x1f85f), (0x1f888, 0x1f88f), (0x1f8ae, 0x1f8ff), (0x1f90c, 0x1f93a), (0x1f93c, 0x1f945),
        (0x1f947, 0x1faff), (0x1fc00, 0x1fffd),
    ];

    private static bool EmojiOnly(string text)
    {
        var runes = text.EnumerateRunes().Select(rune => rune.Value).ToArray();
        bool seen = false;
        for (int i = 0; i < runes.Length; i++)
        {
            int value = runes[i];
            if (value is >= '0' and <= '9' or '#' or '*')
            {
                if (i + 1 < runes.Length && runes[i + 1] == 0xfe0f) i++;
                if (++i >= runes.Length || runes[i] != 0x20e3) return false;
            }
            else if (seen && value is 0x200d or >= 0xfe00 and <= 0xfe0f or >= 0xe0020 and <= 0xe007f) continue;
            else if (!EmojiRanges.Any(range => value >= range.First && value <= range.Last)) return false;
            seen = true;
        }
        return seen;
    }

    internal static MessageRow[] Attach(MessageRow[] rows)
    {
        var parents = rows.Where(row => row.PacketId.HasValue)
            .ToLookup(row => (row.PacketId, row.Channel, Conversation.DirectPair(row)));
        var attached = new HashSet<MessageRow>();
        var summaries = new Dictionary<MessageRow, List<string>>();
        foreach (var row in rows)
        {
            if (row.ReplyId is null || row.Channel is null || !(row.EmojiFlag || EmojiOnly(row.Text))) continue;
            string? pair = Conversation.DirectPair(row);
            if (row.Channel == -1 && pair is null) continue;
            var matches = parents[(row.ReplyId, row.Channel, pair)].Take(2).ToArray();
            if (matches.Length != 1 || matches[0] == row) continue;
            if (!summaries.TryGetValue(matches[0], out var labels)) summaries[matches[0]] = labels = [];
            labels.Add($"{row.Text} {row.Sender}");
            attached.Add(row);
        }
        return rows.Where(row => !attached.Contains(row)).Select(row =>
            summaries.TryGetValue(row, out var labels) ? row with { Reactions = string.Join(" · ", labels) } : row).ToArray();
    }
}
