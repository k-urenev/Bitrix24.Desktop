using System.Globalization;
using Bitrix24.Desktop.Core;

namespace Bitrix24.Desktop.Core.Polling;

public sealed record TaskNoticeDecision(
    IReadOnlyList<AppNotification> Cards,
    IReadOnlyList<TaskSnapshot> SnapshotsToStore);

public static class TaskDiff
{
    public static IReadOnlyList<TaskChange> Compare(
        IReadOnlyDictionary<int, TaskSnapshot> previous,
        IReadOnlyList<TaskSnapshot> current,
        DateTimeOffset assignedAfter,
        int userId = 0)
    {
        var changes = new List<TaskChange>();
        foreach (var task in current)
        {
            if (task.Muted)
                continue;

            if (!previous.TryGetValue(task.Id, out var older))
            {
                changes.Add(new TaskChange(task, NewSummary(task, assignedAfter, userId)));
                continue;
            }

            var lines = new List<string>();
            if (older.Status != task.Status)
                lines.Add($"Статус: {StatusName(older.Status)} → {StatusName(task.Status)}");
            if (!SameDeadline(older.Deadline, task.Deadline))
                lines.Add($"Крайний срок: {FormatDeadline(older.Deadline)} → {FormatDeadline(task.Deadline)}");
            if (older.ResponsibleId != task.ResponsibleId)
                lines.Add("Изменился ответственный");
            var comment = task.CommentCount > older.CommentCount || task.NewCommentCount > older.NewCommentCount;
            if (comment)
                lines.Add("Оставлен комментарий");
            if (lines.Count > 0)
                changes.Add(new TaskChange(task, string.Join("\n", lines), comment));
        }

        return changes;
    }

    public static string StatusName(int status) => status switch
    {
        2 => "Ждёт выполнения",
        3 => "Выполняется",
        4 => "Ждёт контроля",
        5 => "Завершена",
        6 => "Отложена",
        7 => "Отклонена",
        _ => "Статус " + status.ToString(CultureInfo.InvariantCulture)
    };

    public static string NewSummary(TaskSnapshot task, DateTimeOffset assignedAfter, int userId)
    {
        var recent = task.CreatedAt is DateTimeOffset created && created >= assignedAfter;
        if (!recent)
            return "Задача обновлена";
        if (userId <= 0 || task.ResponsibleId == userId)
            return "Вам назначена задача";
        if (task.CreatedBy == userId)
            return "Вы поставили задачу";
        if (task.Accomplice)
            return "Вы соисполнитель";
        if (task.Auditor)
            return "Вы наблюдатель";
        return "Новая задача";
    }

    public static string FormatDeadline(string? deadline)
    {
        if (string.IsNullOrWhiteSpace(deadline))
            return "не задан";
        if (DateTimeOffset.TryParse(deadline, out var parsed))
            return parsed.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
        return deadline;
    }

    private static bool SameDeadline(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right))
            return true;
        if (DateTimeOffset.TryParse(left, out var a) && DateTimeOffset.TryParse(right, out var b))
            return a.UtcDateTime == b.UtcDateTime;
        return string.Equals(left, right, StringComparison.Ordinal);
    }
}

public static class TaskNoticePlanner
{
    public static TaskNoticeDecision Plan(
        IReadOnlyDictionary<int, TaskSnapshot> previous,
        IReadOnlyList<TaskSnapshot> current,
        DateTimeOffset assignedAfter,
        bool doNotDisturb,
        string portalDomain,
        int userId,
        int maxCards)
    {
        if (doNotDisturb)
            return new TaskNoticeDecision(Array.Empty<AppNotification>(), current);

        var changes = TaskDiff.Compare(previous, current, assignedAfter, userId);
        var cards = new List<AppNotification>();
        var shown = new HashSet<int>();
        foreach (var change in changes)
        {
            if (cards.Count >= maxCards)
                break;
            shown.Add(change.Task.Id);
            var title = string.IsNullOrWhiteSpace(change.Task.Title) ? "Задача" : change.Task.Title;
            cards.Add(new AppNotification(
                NotificationKind.Task,
                title,
                change.Summary,
                null,
                change.Task.Id,
                TaskLinks.For(portalDomain, userId, change.Task.Id),
                change.Comment));
        }

        var changedIds = changes.Select(change => change.Task.Id).ToHashSet();
        var store = current.Where(task => !changedIds.Contains(task.Id) || shown.Contains(task.Id)).ToArray();
        return new TaskNoticeDecision(cards, store);
    }
}

public static class TaskLinks
{
    public static string For(string portalDomain, int userId, int taskId) =>
        $"https://{portalDomain}/company/personal/user/{userId}/tasks/task/view/{taskId}/";
}
