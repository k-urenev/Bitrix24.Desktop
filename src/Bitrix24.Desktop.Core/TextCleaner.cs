using System.Text.RegularExpressions;

namespace Bitrix24.Desktop.Core;

public static partial class TextCleaner
{
    public static string ToPlain(string? text, bool singleLine, string? portalDomain = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var codes = Array.Empty<string>();
        var value = text;
        if (!singleLine)
            value = CodeText.Mark(text, out codes);

        value = UserTag().Replace(value, match => UserLink(match, portalDomain));
        value = OtherFormattingTag().Replace(value, "");
        if (singleLine)
        {
            value = BasicFormattingTag().Replace(value, "");
            value = LinkText.Visible(value);
        }

        value = value.Replace('\u00A0', ' ');
        if (!singleLine)
            value = CodeText.Restore(value, codes);
        else
        {
            value = value.Replace("\r", " ").Replace("\n", " ");
            value = Spaces().Replace(value, " ");
        }

        return value.Trim();
    }

    public static string UserProfileUrl(string portalDomain, int userId) =>
        $"https://{portalDomain}/company/personal/user/{userId}/";

    private static string UserLink(Match match, string? portalDomain)
    {
        var name = match.Groups["name"].Value;
        if (string.IsNullOrWhiteSpace(portalDomain))
            return name;

        if (!int.TryParse(match.Groups["id"].Value, out var userId) || userId <= 0)
            return name;

        var url = UserProfileUrl(portalDomain, userId);
        return $"[URL={url}]{name}[/URL]";
    }

    public static string Initials(string title)
    {
        var parts = title.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return "?";

        if (parts.Length == 1)
            return char.ToUpperInvariant(parts[0][0]).ToString();

        return string.Concat(char.ToUpperInvariant(parts[0][0]), char.ToUpperInvariant(parts[1][0]));
    }

    public static string ShortTime(DateTimeOffset? value)
    {
        if (value is null)
            return "";

        var local = value.Value.ToLocalTime();
        var today = DateTime.Today;
        if (local.Date == today)
            return local.ToString("HH:mm");
        if (local.Date == today.AddDays(-1))
            return "вчера";
        return local.ToString("dd.MM");
    }

    public static string MessageTime(DateTimeOffset? value)
    {
        if (value is null)
            return "";

        return value.Value.ToLocalTime().ToString("HH:mm");
    }

    [GeneratedRegex(@"\[USER=(?<id>\d+)[^\]]*\](?<name>.*?)\[/USER\]", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex UserTag();

    [GeneratedRegex(@"\[/?(?:br|img|size|color|quote|code|font|p)[^\]]*\]", RegexOptions.IgnoreCase)]
    private static partial Regex OtherFormattingTag();

    [GeneratedRegex(@"\[/?(?:b|i|u(?!rl)|s)\]", RegexOptions.IgnoreCase)]
    private static partial Regex BasicFormattingTag();

    [GeneratedRegex(@"[ ]{2,}")]
    private static partial Regex Spaces();
}
