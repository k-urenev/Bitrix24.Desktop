using Bitrix24.Desktop.Core;
using Bitrix24.Desktop.Core.Polling;

namespace Bitrix24.Desktop.Core.Tests;

public class TaskDiffTests
{
    private static readonly DateTimeOffset AssignedAfter = DateTimeOffset.Parse("2026-10-29T10:00:00+03:00");

    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified)));

    [Fact]
    public void Recent_unseen_task_is_an_assignment()
    {
        var task = Sample(id: 1, created: AssignedAfter.AddMinutes(5));

        var change = Assert.Single(TaskDiff.Compare(new Dictionary<int, TaskSnapshot>(), new[] { task }, AssignedAfter));

        Assert.Equal("Вам назначена задача", change.Summary);
    }

    [Fact]
    public void Older_unseen_task_is_an_update()
    {
        var task = Sample(id: 2, created: AssignedAfter.AddDays(-3));

        var change = Assert.Single(TaskDiff.Compare(new Dictionary<int, TaskSnapshot>(), new[] { task }, AssignedAfter));

        Assert.Equal("Задача обновлена", change.Summary);
    }

    [Fact]
    public void Status_deadline_responsible_and_comment_changes_are_listed()
    {
        var older = Sample(id: 3, status: 2, deadline: "2026-10-30T09:00:00+03:00", responsible: 5, comments: 1, fresh: 0);
        var newer = Sample(id: 3, status: 3, deadline: "2026-10-31T09:00:00+03:00", responsible: 8, comments: 2, fresh: 1);
        var previous = new Dictionary<int, TaskSnapshot> { [3] = older };

        var change = Assert.Single(TaskDiff.Compare(previous, new[] { newer }, AssignedAfter));

        Assert.Contains("Статус: Ждёт выполнения → Выполняется", change.Summary);
        Assert.Contains("Крайний срок:", change.Summary);
        Assert.Contains("Изменился ответственный", change.Summary);
        Assert.Contains("Оставлен комментарий", change.Summary);
        Assert.True(change.Comment);
    }

    [Fact]
    public void Recent_task_uses_the_role_in_the_summary()
    {
        var created = Sample(id: 8, created: AssignedAfter.AddMinutes(1), responsible: 4, createdBy: 9);
        var accomplice = Sample(id: 9, created: AssignedAfter.AddMinutes(1), responsible: 4, accomplice: true);
        var auditor = Sample(id: 10, created: AssignedAfter.AddMinutes(1), responsible: 4, auditor: true);
        var current = new[] { created, accomplice, auditor };

        var changes = TaskDiff.Compare(new Dictionary<int, TaskSnapshot>(), current, AssignedAfter, userId: 9);

        Assert.Equal("Вы поставили задачу", changes[0].Summary);
        Assert.Equal("Вы соисполнитель", changes[1].Summary);
        Assert.Equal("Вы наблюдатель", changes[2].Summary);
    }

    [Fact]
    public void Muted_task_is_stored_without_a_card()
    {
        var older = Sample(id: 7, status: 2);
        var newer = Sample(id: 7, status: 5, muted: true);
        var previous = new Dictionary<int, TaskSnapshot> { [7] = older };

        var decision = TaskNoticePlanner.Plan(previous, new[] { newer }, AssignedAfter, false, "portal.bitrix24.ru", 9, 3);

        Assert.Empty(decision.Cards);
        Assert.Single(decision.SnapshotsToStore);
        Assert.Equal(5, decision.SnapshotsToStore[0].Status);
    }

    [Fact]
    public void Notice_time_uses_today_yesterday_or_the_month_name()
    {
var now = Local(2026, 11, 6, 12, 0);

        Assert.Equal("сегодня, 10:18", NoticeWhen.Format(Local(2026, 11, 6, 10, 18), now));
        Assert.Equal("вчера, 17:57", NoticeWhen.Format(Local(2026, 11, 5, 17, 57), now));
        Assert.Equal("2 ноября, 16:55", NoticeWhen.Format(Local(2026, 11, 2, 16, 55), now));
    }

    [Fact]
    public void Unchanged_task_produces_nothing()
    {
        var task = Sample(id: 4);
        var previous = new Dictionary<int, TaskSnapshot> { [4] = task };

        Assert.Empty(TaskDiff.Compare(previous, new[] { task }, AssignedAfter));
    }

    [Fact]
    public void Do_not_disturb_stores_every_snapshot_and_shows_no_cards()
    {
        var older = Sample(id: 5, status: 2);
        var newer = Sample(id: 5, status: 5);
        var previous = new Dictionary<int, TaskSnapshot> { [5] = older };

        var decision = TaskNoticePlanner.Plan(previous, new[] { newer }, AssignedAfter, doNotDisturb: true, "portal.bitrix24.ru", 5, 3);

        Assert.Empty(decision.Cards);
        Assert.Single(decision.SnapshotsToStore);
    }

    [Fact]
    public void Only_three_task_cards_are_stored_as_seen()
    {
        var previous = new Dictionary<int, TaskSnapshot>();
        var current = Enumerable.Range(1, 4)
            .Select(id => Sample(id, created: AssignedAfter.AddMinutes(id), title: "Задача " + id))
            .ToArray();

        var decision = TaskNoticePlanner.Plan(previous, current, AssignedAfter, false, "portal.bitrix24.ru", 9, 3);

        Assert.Equal(3, decision.Cards.Count);
        Assert.Equal(3, decision.SnapshotsToStore.Count);
        Assert.DoesNotContain(decision.SnapshotsToStore, task => task.Id == 4);
        Assert.Contains("/tasks/task/view/1/", decision.Cards[0].ExternalUrl);
        Assert.False(decision.Cards[0].UpdateExisting);
    }

    [Fact]
    public void A_comment_card_updates_the_existing_notice()
    {
        var older = Sample(id: 3, comments: 1);
        var newer = Sample(id: 3, comments: 2, title: "С комментарием");
        var previous = new Dictionary<int, TaskSnapshot> { [3] = older };

        var card = Assert.Single(TaskNoticePlanner.Plan(previous, new[] { newer }, AssignedAfter, false, "portal.bitrix24.ru", 9, 3).Cards);

        Assert.Equal("Оставлен комментарий", card.Body);
        Assert.True(card.UpdateExisting);
    }

    private static TaskSnapshot Sample(
        int id,
        int status = 2,
        string? deadline = null,
        int responsible = 5,
        int comments = 0,
        int fresh = 0,
        DateTimeOffset? created = null,
        string title = "Задача",
        int createdBy = 0,
        bool accomplice = false,
        bool auditor = false,
        bool muted = false) =>
        new(id, title, status, deadline, responsible, comments, fresh, created ?? AssignedAfter.AddDays(-1), AssignedAfter, createdBy, accomplice, auditor, muted);
}
