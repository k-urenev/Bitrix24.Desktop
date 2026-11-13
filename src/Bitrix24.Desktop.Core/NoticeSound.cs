namespace Bitrix24.Desktop.Core;

public enum NoticeSound
{
    Incoming,
    OpenChat,
    Send,
    Reminder
}

public static class NoticeSoundFile
{
    public static string Resolve(string? chosen, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(chosen) && File.Exists(chosen))
            return chosen;

        return fallback;
    }
}
