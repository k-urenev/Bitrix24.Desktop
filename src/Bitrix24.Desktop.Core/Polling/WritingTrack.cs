namespace Bitrix24.Desktop.Core.Polling;

public readonly record struct WritingSignal(string DialogId, int UserId);

public sealed class WritingTrack
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Tail = TimeSpan.FromSeconds(2);

    private readonly Dictionary<int, DateTimeOffset> _last = new();
    private readonly Dictionary<int, TimeSpan> _interval = new();

    public TimeSpan HoldAfter(int userId, DateTimeOffset now)
    {
        var interval = _interval.TryGetValue(userId, out var known) ? known : DefaultInterval;
        if (_last.TryGetValue(userId, out var previous))
        {
            var gap = now - previous;
            if (gap >= TimeSpan.FromSeconds(1) && gap <= TimeSpan.FromSeconds(40))
            {
                interval = gap;
                _interval[userId] = gap;
            }
        }

        _last[userId] = now;
        return interval + Tail;
    }

    public static bool Belongs(string openDialogId, int openChatId, bool isGroup, int selfUserId, int writerUserId, string eventDialogId)
    {
        if (writerUserId <= 0 || writerUserId == selfUserId || openDialogId.Length == 0)
            return false;

        if (eventDialogId.Length > 0 && string.Equals(eventDialogId, openDialogId, StringComparison.OrdinalIgnoreCase))
            return true;

        if (openChatId > 0)
        {
            var chatDialog = "chat" + openChatId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (string.Equals(eventDialogId, chatDialog, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return !isGroup
            && string.Equals(writerUserId.ToString(System.Globalization.CultureInfo.InvariantCulture), openDialogId, StringComparison.Ordinal);
    }
}
