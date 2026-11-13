namespace Bitrix24.Desktop.Core;

public enum NotificationKind
{
    Message,
    Task
}

public sealed record AppNotification(
    NotificationKind Kind,
    string Title,
    string Body,
    string? DialogId,
    int? TaskId,
    string? ExternalUrl,
    bool UpdateExisting = false);

public sealed record DialogSummary(
    string DialogId,
    string Title,
    string? AvatarUrl,
    string Color,
    string LastMessageText,
    int LastMessageId,
    int LastMessageAuthorId,
    DateTimeOffset? LastMessageAt,
    int UnreadCounter,
    string Type,
    bool LastMessageDelivered = false,
    int OthersReadMessageId = 0,
    bool HasPresence = false,
    bool IsOnline = false,
    DateTimeOffset? LastSeenAt = null,
    string Gender = "",
    int ChatId = 0,
    bool AvatarKnown = false);

public sealed record ChatFile(string Name, string? Url, long Size = 0, string? Preview = null, int Id = 0);

public sealed record ChatReactionFace(int Id, string Name, string? AvatarUrl, string Color);

public sealed record ChatReaction(string Code, int Count, bool Mine, IReadOnlyList<ChatReactionFace> Faces);

public readonly record struct FeedGap(string DialogId, string Title, int ListedId, int FeedMax);

public sealed record ChatMessage(
    int Id,
    string DialogId,
    int AuthorId,
    string AuthorName,
    string Text,
    DateTimeOffset? SentAt,
    bool IsSystem,
    IReadOnlyList<ChatFile> Files,
    string? AuthorAvatarUrl = null,
    int ChatId = 0,
    IReadOnlyList<ChatReaction>? Reactions = null,
    bool IsEdited = false);

public sealed record UserProfile(int Id, string Name, string? AvatarUrl = null);

public sealed record TaskSnapshot(
    int Id,
    string Title,
    int Status,
    string? Deadline,
    int ResponsibleId,
    int CommentCount,
    int NewCommentCount,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? ChangedAt,
    int CreatedBy = 0,
    bool Accomplice = false,
    bool Auditor = false,
    bool Muted = false);

public sealed record TaskChange(TaskSnapshot Task, string Summary, bool Comment = false);

public sealed record TaskNoticeRecord(
    long Id,
    int TaskId,
    string Title,
    string Body,
    string? Url,
    DateTimeOffset At);
