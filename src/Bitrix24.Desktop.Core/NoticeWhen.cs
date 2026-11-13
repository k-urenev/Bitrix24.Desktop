using System.Globalization;

namespace Bitrix24.Desktop.Core;

public static class NoticeWhen
{
    private static readonly string[] Months =
    {
        "января", "февраля", "марта", "апреля", "мая", "июня",
        "июля", "августа", "сентября", "октября", "ноября", "декабря"
    };

    private static readonly string[] Colors =
    {
        "#2F80ED", "#00ACE3", "#1EAE7A", "#F7A700", "#E86666", "#A855C8"
    };

    public static string Format(DateTimeOffset at, DateTimeOffset now)
    {
        var local = at.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (local.Date == today)
            return "сегодня, " + time;
        if (local.Date == today.AddDays(-1))
            return "вчера, " + time;
        return local.Day.ToString(CultureInfo.InvariantCulture) + " " + Months[local.Month - 1] + ", " + time;
    }

    public static string Color(int taskId)
    {
        var index = taskId % Colors.Length;
        if (index < 0)
            index += Colors.Length;
        return Colors[index];
    }
}
