namespace Bitrix24.Desktop.Core;

public static class AvatarCachePolicy
{
    public const int WarmCount = 20;
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

    public static bool NeedsFetch(
        DateTimeOffset? savedUtc,
        string? savedUrl,
        string? url,
        DateTimeOffset now,
        bool force,
        bool complete)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        if (force || !complete || savedUtc is null)
            return true;
        if (!string.Equals(url, savedUrl, StringComparison.Ordinal))
            return true;
        return now - savedUtc.Value >= Lifetime;
    }

    public static List<int> FirstEmployees(IEnumerable<(bool IsGroup, int UserId)> dialogs, int count = WarmCount)
    {
        var ids = new List<int>(Math.Max(count, 0));
        if (count <= 0)
            return ids;

        foreach (var dialog in dialogs)
        {
            if (dialog.IsGroup || dialog.UserId <= 0 || ids.Contains(dialog.UserId))
                continue;
            ids.Add(dialog.UserId);
            if (ids.Count == count)
                break;
        }

        return ids;
    }
}
