using Bitrix24.Desktop.Core;

namespace Bitrix24.Desktop.Core.Tests;

public class FeedDiffTests
{
    [Fact]
    public void Page_and_instant_keep_the_newest_messages()
    {
        var messages = Enumerable.Range(1, 80).Select(Message).ToArray();

        var page = FeedDiff.Page(messages);
        var instant = FeedDiff.Instant(page);

        Assert.Equal(50, page.Count);
        Assert.Equal(31, page[0].Id);
        Assert.Equal(80, page[^1].Id);
        Assert.Equal(15, instant.Count);
        Assert.Equal(66, instant[0].Id);
        Assert.Equal(80, instant[^1].Id);
    }

    [Fact]
    public void Plan_drops_messages_missing_from_the_page_and_appends_the_new_tail()
    {
        var current = new[] { 10, 40, 41, 42 };
        var incoming = new[] { 40, 41, 43 }.Select(Message).ToArray();

        var plan = FeedDiff.Plan(current, incoming, dropMissing: true, keepBelowId: 40);

        Assert.Equal(new[] { 42 }, plan.RemoveIds);
        Assert.Empty(plan.Above);
        Assert.Equal(43, Assert.Single(plan.Tail).Id);
    }

    [Fact]
    public void Plan_keeps_history_below_the_open_page_and_inserts_gaps_above()
    {
        var current = new[] { 10, 48, 49, 50 };
        var incoming = Enumerable.Range(40, 11).Select(Message).ToArray();

        var plan = FeedDiff.Plan(current, incoming, dropMissing: true, keepBelowId: 48);

        Assert.Empty(plan.RemoveIds);
        Assert.Equal(Enumerable.Range(40, 8), plan.Above.Select(message => message.Id));
        Assert.Empty(plan.Tail);
    }

    [Fact]
    public void Plan_without_dropping_only_inserts_what_is_missing()
    {
        var current = new[] { 48, 49, 50 };
        var incoming = Enumerable.Range(40, 11).Select(Message).ToArray();

        var plan = FeedDiff.Plan(current, incoming, dropMissing: false, keepBelowId: 0);

        Assert.Empty(plan.RemoveIds);
        Assert.Equal(Enumerable.Range(40, 8), plan.Above.Select(message => message.Id));
        Assert.Empty(plan.Tail);
    }

    private static ChatMessage Message(int id) =>
        new(id, "chat", 1, "Автор", "текст", null, false, Array.Empty<ChatFile>());
}
