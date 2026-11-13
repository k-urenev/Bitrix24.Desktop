using Bitrix24.Desktop.Core;
using Bitrix24.Desktop.Core.Polling;

namespace Bitrix24.Desktop.Core.Tests;

public class MessageNoticePlannerTests
{
    [Fact]
    public void First_poll_seeds_cursors_without_cards()
    {
        var dialogs = new[]
        {
            Dialog(id: "1", messageId: 10, authorId: 2),
            Dialog(id: "chat8", messageId: 4, authorId: 3)
        };

        var decision = MessageNoticePlanner.Plan(dialogs, new Dictionary<string, int>(), currentUserId: 1, quietDialogId: null, doNotDisturb: false, baselineEstablished: false, maxCards: 3);

        Assert.Empty(decision.Cards);
        Assert.Equal(2, decision.AdvanceCursors.Count);
        Assert.Contains(("1", 10), decision.AdvanceCursors);
        Assert.Contains(("chat8", 4), decision.AdvanceCursors);
    }

    [Fact]
    public void New_message_from_someone_else_becomes_a_card()
    {
        var dialogs = new[] { Dialog(id: "5", messageId: 12, authorId: 8, title: "Анна", text: "Привет") };
        var cursors = new Dictionary<string, int> { ["5"] = 11 };

        var decision = MessageNoticePlanner.Plan(dialogs, cursors, 1, null, false, true, 3);

        var card = Assert.Single(decision.Cards);
        Assert.Equal(NotificationKind.Message, card.Kind);
        Assert.Equal("Анна", card.Title);
        Assert.Equal("Привет", card.Body);
        Assert.Equal("5", card.DialogId);
        Assert.Contains(("5", 12), decision.AdvanceCursors);
    }

    [Fact]
    public void Own_system_and_dnd_stay_silent_open_dialog_still_notifies()
    {
        var dialogs = new[]
        {
            Dialog("1", 3, authorId: 7, title: "Я"),
            Dialog("2", 4, authorId: 0, title: "Система"),
            Dialog("3", 5, authorId: 9, title: "Открытый"),
            Dialog("4", 6, authorId: 9, title: "Тихий")
        };
        var cursors = new Dictionary<string, int>();

        var own = MessageNoticePlanner.Plan(dialogs.Take(1).ToArray(), cursors, currentUserId: 7, null, false, true, 3);
        var system = MessageNoticePlanner.Plan(dialogs.Skip(1).Take(1).ToArray(), cursors, 7, null, false, true, 3);
        var quiet = MessageNoticePlanner.Plan(dialogs.Skip(2).Take(1).ToArray(), cursors, 7, quietDialogId: "3", false, true, 3);
        var dnd = MessageNoticePlanner.Plan(dialogs.Skip(3).ToArray(), cursors, 7, null, doNotDisturb: true, true, 3);

        Assert.Empty(own.Cards);
        Assert.Empty(system.Cards);
        var open = Assert.Single(quiet.Cards);
        Assert.Equal("3", open.DialogId);
        Assert.Equal("3", quiet.DialogToRefresh);
        Assert.Contains(("3", 5), quiet.AdvanceCursors);
        Assert.Empty(dnd.Cards);
        Assert.Contains(("4", 6), dnd.AdvanceCursors);
    }

    [Fact]
    public void Open_dialog_does_not_take_a_card_slot()
    {
        var dialogs = Enumerable.Range(1, 4)
            .Select(index => Dialog(index.ToString(), messageId: 20 + index, authorId: 9, title: "Чат " + index))
            .ToArray();

        var decision = MessageNoticePlanner.Plan(dialogs, new Dictionary<string, int>(), 1, quietDialogId: "1", false, true, 3);

        Assert.Equal(new[] { "1", "2", "3", "4" }, decision.Cards.Select(card => card.DialogId).ToArray());
        Assert.Equal("1", decision.DialogToRefresh);
    }

    [Fact]
    public void Only_three_dialogs_are_carded_per_poll()
    {
        var dialogs = Enumerable.Range(1, 4)
            .Select(index => Dialog(index.ToString(), messageId: 20 + index, authorId: 9, title: "Чат " + index))
            .ToArray();

        var decision = MessageNoticePlanner.Plan(dialogs, new Dictionary<string, int>(), 1, null, false, true, 3);

        Assert.Equal(3, decision.Cards.Count);
        Assert.DoesNotContain(decision.AdvanceCursors, cursor => cursor.DialogId == "4");
        Assert.Contains(decision.AdvanceCursors, cursor => cursor.DialogId == "1");
    }

    private static DialogSummary Dialog(string id, int messageId, int authorId, string title = "Чат", string text = "текст") =>
        new(id, title, null, "#2F80ED", text, messageId, authorId, DateTimeOffset.Parse("2026-10-29T12:00:00+03:00"), 1, "user");
}
