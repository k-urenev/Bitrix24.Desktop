using Bitrix24.Desktop.Core;

namespace Bitrix24.Desktop.ViewModels;

public sealed class TaskNoticeItem
{
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string When { get; init; } = "";
    public string Initials { get; init; } = "?";
    public string Color { get; init; } = "#2F80ED";
    public string? Url { get; init; }
    public int TaskId { get; init; }

    public static TaskNoticeItem Create(string title, string body, string? url, int taskId, DateTimeOffset at)
    {
        var heading = string.IsNullOrWhiteSpace(title) ? "Задача" : title;
        return new TaskNoticeItem
        {
            Title = heading,
            Body = body,
            Url = url,
            TaskId = taskId,
            When = NoticeWhen.Format(at, DateTimeOffset.Now),
            Initials = TextCleaner.Initials(heading),
            Color = NoticeWhen.Color(taskId)
        };
    }
}
