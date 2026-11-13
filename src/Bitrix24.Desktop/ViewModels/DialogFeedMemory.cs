using Bitrix24.Desktop.Core;

namespace Bitrix24.Desktop.ViewModels;

internal sealed class DialogFeedMemory
{
    private readonly Dictionary<string, List<ChatMessage>> _pages = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public List<ChatMessage>? Try(string dialogId)
    {
        lock (_gate)
            return _pages.TryGetValue(dialogId, out var page) ? page : null;
    }

    public List<ChatMessage> Fold(string dialogId, List<ChatMessage> page, List<ChatMessage>? remembered)
    {
        lock (_gate)
        {
            if (remembered is { Count: > 0 })
                page = Merge(page, remembered);
            if (_pages.TryGetValue(dialogId, out var latest))
                page = Merge(page, latest);
            if (page.Count > 0)
                _pages[dialogId] = page;
        }

        return page;
    }

    public void RememberPage(string dialogId, IReadOnlyList<ChatMessage> messages)
    {
        var page = FeedDiff.Page(messages);
        lock (_gate)
        {
            if (page.Count == 0)
            {
                _pages.Remove(dialogId);
                return;
            }

            if (_pages.TryGetValue(dialogId, out var existing))
                page = KeepReactions(existing, page);
            _pages[dialogId] = page;
        }
    }

    public void RememberMore(string dialogId, IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0)
            return;

        lock (_gate)
        {
            if (!_pages.TryGetValue(dialogId, out var existing))
            {
                var page = FeedDiff.Page(messages);
                if (page.Count > 0)
                    _pages[dialogId] = page;
                return;
            }

            _pages[dialogId] = Merge(existing, messages);
        }
    }

    public void Warm(IEnumerable<string> dialogIds, Func<string, IReadOnlyList<ChatMessage>> load)
    {
        List<string> missing;
        lock (_gate)
            missing = dialogIds.Where(id => !_pages.ContainsKey(id)).Distinct(StringComparer.Ordinal).ToList();
        if (missing.Count == 0)
            return;

        _ = Task.Run(() =>
        {
            foreach (var id in missing)
            {
                List<ChatMessage> page;
                try
                {
                    page = FeedDiff.Page(load(id));
                }
                catch (Exception exception)
                {
                    FileLog.Write(exception);
                    continue;
                }

                if (page.Count == 0)
                    continue;

                lock (_gate)
                {
                    if (!_pages.ContainsKey(id))
                        _pages[id] = page;
                }
            }
        });
    }

    public void ApplyPage(string dialogId, ReactionPage page)
    {
        var covered = new HashSet<int>(page.MessageIds);
        lock (_gate)
        {
            if (!_pages.TryGetValue(dialogId, out var stored))
                return;
            for (var i = 0; i < stored.Count; i++)
            {
                if (!covered.Contains(stored[i].Id))
                    continue;
                stored[i] = stored[i] with { Reactions = page.For(stored[i].Id) };
            }
        }
    }

    public void Rewrite(string dialogId, int messageId, string text)
    {
        lock (_gate)
        {
            if (!_pages.TryGetValue(dialogId, out var stored))
                return;
            for (var i = 0; i < stored.Count; i++)
            {
                if (stored[i].Id != messageId)
                    continue;
                stored[i] = stored[i] with { Text = text, IsEdited = true };
                return;
            }
        }
    }

    public void RememberReaction(string dialogId, int messageId, IReadOnlyList<ChatReaction> reactions)
    {
        lock (_gate)
        {
            if (!_pages.TryGetValue(dialogId, out var stored))
                return;
            for (var i = 0; i < stored.Count; i++)
            {
                if (stored[i].Id != messageId)
                    continue;
                stored[i] = stored[i] with { Reactions = reactions };
                return;
            }
        }
    }

    public List<ChatMessage> Copy(string dialogId)
    {
        lock (_gate)
            return _pages.TryGetValue(dialogId, out var stored) ? stored.ToList() : new List<ChatMessage>();
    }

    private static List<ChatMessage> Merge(IReadOnlyList<ChatMessage> older, IReadOnlyList<ChatMessage> newer)
    {
        var map = new Dictionary<int, ChatMessage>(older.Count + newer.Count);
        foreach (var message in older)
            map[message.Id] = message;
        foreach (var message in newer)
        {
            if (message.Reactions is null
                && map.TryGetValue(message.Id, out var previous)
                && previous.Reactions is not null)
                map[message.Id] = message with { Reactions = previous.Reactions };
            else
                map[message.Id] = message;
        }

        return FeedDiff.Page(map.Values.ToList());
    }

    private static List<ChatMessage> KeepReactions(IReadOnlyList<ChatMessage> previous, List<ChatMessage> next)
    {
        Dictionary<int, IReadOnlyList<ChatReaction>>? kept = null;
        foreach (var message in previous)
        {
            if (message.Reactions is null)
                continue;
            kept ??= new Dictionary<int, IReadOnlyList<ChatReaction>>();
            kept[message.Id] = message.Reactions;
        }

        if (kept is null)
            return next;

        for (var i = 0; i < next.Count; i++)
        {
            if (next[i].Reactions is null && kept.TryGetValue(next[i].Id, out var reactions))
                next[i] = next[i] with { Reactions = reactions };
        }

        return next;
    }
}
