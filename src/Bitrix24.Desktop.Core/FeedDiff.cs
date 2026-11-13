namespace Bitrix24.Desktop.Core;

public static class FeedDiff
{
    public const int InstantCount = 15;
    public const int PageCount = 50;

    public static List<ChatMessage> Page(IReadOnlyList<ChatMessage> messages)
    {
        var ordered = messages.OrderBy(message => message.Id).ToList();
        if (ordered.Count <= PageCount)
            return ordered;
        return ordered.GetRange(ordered.Count - PageCount, PageCount);
    }

    public static List<ChatMessage> Instant(IReadOnlyList<ChatMessage> messages)
    {
        var ordered = messages.OrderBy(message => message.Id).ToList();
        if (ordered.Count <= InstantCount)
            return ordered;
        return ordered.GetRange(ordered.Count - InstantCount, InstantCount);
    }

    public readonly record struct Result(
        IReadOnlyList<int> RemoveIds,
        IReadOnlyList<ChatMessage> Above,
        IReadOnlyList<ChatMessage> Tail);

    public static Result Plan(
        IReadOnlyList<int> currentIds,
        IReadOnlyList<ChatMessage> incoming,
        bool dropMissing,
        int keepBelowId)
    {
        var present = new HashSet<int>(incoming.Count);
        foreach (var message in incoming)
            present.Add(message.Id);

        var remove = new List<int>();
        var previousMax = 0;
        foreach (var id in currentIds)
        {
            if (id > previousMax)
                previousMax = id;
            if (!dropMissing || present.Contains(id) || id < keepBelowId)
                continue;
            remove.Add(id);
        }

        var current = new HashSet<int>(currentIds);
        var above = new List<ChatMessage>();
        var tail = new List<ChatMessage>();
        foreach (var message in incoming.OrderBy(item => item.Id))
        {
            if (current.Contains(message.Id))
                continue;
            if (previousMax > 0 && message.Id <= previousMax)
                above.Add(message);
            else
                tail.Add(message);
        }

        return new Result(remove, above, tail);
    }
}
