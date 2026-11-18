namespace MeshMonitor.Native;

internal sealed record MessagePage(MessageRow[] Rows, bool HasMore);
internal sealed record OlderMessages(MessageRow[] Rows, bool HasMore, int LastOffset);

internal static class MessageHistory
{
    internal const int PageSize = 100;
    private const int MaxOffset = 50_000;
    private const int MaxSeekPages = 6;

    // These server endpoints page by offset. Locate the last displayed ID again
    // before appending, as arrivals or deletions can move an offset between calls.
    internal static async Task<OlderMessages> ReadOlder(
        string anchorId, int anchorOffset, Func<int, Task<MessagePage>> fetch)
    {
        if (string.IsNullOrWhiteSpace(anchorId)) throw Moved();
        int offset = Math.Max(0, anchorOffset - PageSize + 1);
        for (int seek = 0; seek < MaxSeekPages && offset <= MaxOffset; seek++)
        {
            var page = await fetch(offset);
            int anchor = Array.FindIndex(page.Rows, message => message.Id == anchorId);
            if (anchor >= 0)
            {
                if (anchor + 1 < page.Rows.Length || !page.HasMore)
                    return new(page.Rows[(anchor + 1)..], page.HasMore, offset + page.Rows.Length - 1);

                // Re-read with the anchor still in view. Reading beyond it
                // directly could skip a row if the head moved on this request.
                int next = offset + Math.Max(1, anchor - 1);
                if (next > MaxOffset) return new([], false, offset + anchor);
                offset = next;
                continue;
            }
            if (!page.HasMore) break;
            offset += PageSize;
        }
        throw Moved();
    }

    private static InvalidOperationException Moved() =>
        new("The message history changed. Choose Latest messages and try again.");
}
