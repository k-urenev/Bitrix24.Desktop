namespace Bitrix24.Desktop.Core;

public static class PresenceText
{
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(10);

    public static bool IsCurrent(string? status, DateTimeOffset? idle, DateTimeOffset? lastSeen, DateTimeOffset now)
    {
        if (idle is not null)
            return false;
        if (!string.Equals(status, "online", StringComparison.OrdinalIgnoreCase))
            return false;
        if (lastSeen is null)
            return true;
        var age = now - lastSeen.Value;
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;
        return age <= OnlineWindow;
    }

    public static string Format(bool online, DateTimeOffset? lastSeen, string? gender, DateTimeOffset now)
    {
        if (online)
            return "Онлайн";
        if (lastSeen is null)
            return "";

        var elapsed = now - lastSeen.Value;
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        var verb = string.Equals(gender, "F", StringComparison.OrdinalIgnoreCase) ? "была" : "был";
        if (elapsed.TotalHours < 1)
        {
            var minutes = Math.Max(1, (int)elapsed.TotalMinutes);
            return $"{verb} {minutes} {Unit(minutes, "минуту", "минуты", "минут")} назад";
        }

        if (elapsed.TotalDays < 1)
        {
            var hours = Math.Max(1, (int)elapsed.TotalHours);
            return $"{verb} {hours} {Unit(hours, "час", "часа", "часов")} назад";
        }

        if (elapsed.TotalDays < 30)
        {
            var days = Math.Max(1, (int)elapsed.TotalDays);
            return $"{verb} {days} {Unit(days, "день", "дня", "дней")} назад";
        }

        var months = Math.Max(1, (int)(elapsed.TotalDays / 30));
        return $"{verb} {months} {Unit(months, "месяц", "месяца", "месяцев")} назад";
    }

    private static string Unit(int value, string one, string few, string many)
    {
        var mod100 = value % 100;
        if (mod100 is >= 11 and <= 14)
            return many;
        return (value % 10) switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many
        };
    }
}
