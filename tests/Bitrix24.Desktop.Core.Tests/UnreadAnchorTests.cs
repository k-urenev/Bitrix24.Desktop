using Bitrix24.Desktop.Core;

namespace Bitrix24.Desktop.Core.Tests;

public class UnreadAnchorTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Single_or_none_stays_at_the_end(int unread)
    {
        var rows = new[]
        {
            new UnreadAnchor.Row(1, false),
            new UnreadAnchor.Row(2, false)
        };

        Assert.Null(UnreadAnchor.Find(rows, unread));
    }

    [Fact]
    public void Several_unread_skip_own_messages_from_the_tail()
    {
        var rows = new[]
        {
            new UnreadAnchor.Row(1, false),
            new UnreadAnchor.Row(2, true),
            new UnreadAnchor.Row(3, false),
            new UnreadAnchor.Row(4, false),
            new UnreadAnchor.Row(5, true)
        };

        Assert.Equal(3, UnreadAnchor.Find(rows, 2));
        Assert.Equal(1, UnreadAnchor.Find(rows, 3));
    }

    [Fact]
    public void Counter_past_the_page_anchors_the_oldest_foreign_message()
    {
        var rows = new[]
        {
            new UnreadAnchor.Row(7, true),
            new UnreadAnchor.Row(8, false),
            new UnreadAnchor.Row(9, false)
        };

        Assert.Equal(8, UnreadAnchor.Find(rows, 10));
    }

    [Fact]
    public void Only_own_messages_stay_at_the_end()
    {
        var rows = new[]
        {
            new UnreadAnchor.Row(1, true),
            new UnreadAnchor.Row(2, true)
        };

        Assert.Null(UnreadAnchor.Find(rows, 4));
    }
}
