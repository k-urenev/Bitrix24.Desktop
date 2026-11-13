namespace Bitrix24.Desktop.Core;

public static class FeedTrace
{
    public static string Window(bool visible, bool minimized)
    {
        if (!visible)
            return "скрыто";
        return minimized ? "свёрнуто" : "на экране";
    }

    public static string Selected(string? dialogId, string? title)
    {
        if (string.IsNullOrEmpty(dialogId))
            return "нет";
        if (string.IsNullOrEmpty(title))
            return dialogId;
        return dialogId + " «" + Snippet(title) + "»";
    }

    public static string WindowLine(string state, string selected) =>
        "окно " + state + " выбран " + selected;

    public static string ChatLine(
        string dialogId,
        string title,
        int listedId,
        int feedMax,
        string window,
        string selected,
        bool refresh)
    {
        return string.Concat(
            "чат ",
            dialogId,
            " «",
            Snippet(title),
            "» последнее ",
            listedId.ToString(),
            " лента до ",
            feedMax.ToString(),
            " окно ",
            window,
            " выбран ",
            selected,
            " догрузка ",
            refresh ? "да" : "нет");
    }

    public static string LoadLine(string dialogId, int cached, int fresh, int feedMax, int listedId) =>
        "лента " + dialogId
        + " открытие кэш " + cached.ToString()
        + " портал " + fresh.ToString()
        + " хвост " + feedMax.ToString()
        + " в списке " + listedId.ToString();

    public static string TailLine(string dialogId, int fromId, int incoming, int feedMax, int listedId, int added) =>
        "лента " + dialogId
        + " догрузка с " + fromId.ToString()
        + " портал " + incoming.ToString()
        + " хвост " + feedMax.ToString()
        + " добавлено " + added.ToString()
        + " в списке " + listedId.ToString();

    public static string GapLine(string dialogId, int listedId, int feedMax, string when) =>
        "лента " + dialogId + " " + when + " в списке " + listedId.ToString() + " в ленте нет (хвост " + feedMax.ToString() + ")";

    public static string Snippet(string text)
    {
        var flat = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flat.Length <= 32 ? flat : flat[..32];
    }
}
