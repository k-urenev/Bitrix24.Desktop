using System.Text.RegularExpressions;

namespace Bitrix24.Desktop.Core;

public readonly record struct TextStyle(
    bool Bold = false,
    bool Italic = false,
    bool Underline = false,
    bool Strike = false);

public readonly record struct StyledSegment(string Text, TextStyle Style);

public static partial class FormatText
{
    public static IEnumerable<StyledSegment> Split(string? text)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        foreach (var segment in SplitBlock(text, default))
            yield return segment;
    }

    private static IEnumerable<StyledSegment> SplitBlock(string text, TextStyle style)
    {
        var index = 0;
        while (index < text.Length)
        {
            var tag = Tag().Match(text, index);
            if (!tag.Success || tag.Index > text.Length)
            {
                var tail = text[index..];
                if (tail.Length > 0)
                    yield return new StyledSegment(tail, style);
                yield break;
            }

            if (tag.Index > index)
                yield return new StyledSegment(text[index..tag.Index], style);

            var name = tag.Groups["name"].Value.ToLowerInvariant();
            if (tag.Value.StartsWith("[/", StringComparison.Ordinal))
            {
                index = tag.Index + tag.Length;
                continue;
            }

            var openEnd = tag.Index + tag.Length;
            if (!TryReadClosed(text, openEnd, name, out var inner, out var after))
            {
                yield return new StyledSegment(tag.Value, style);
                index = openEnd;
                continue;
            }

            var innerStyle = ApplyOpen(style, name);
            foreach (var segment in SplitBlock(inner, innerStyle))
                yield return segment;

            index = after;
        }
    }

    private static TextStyle ApplyOpen(TextStyle style, string name) =>
        name switch
        {
            "b" => style with { Bold = true },
            "i" => style with { Italic = true },
            "u" => style with { Underline = true },
            "s" => style with { Strike = true },
            _ => style
        };

    private static bool TryReadClosed(string text, int start, string name, out string inner, out int after)
    {
        inner = "";
        after = start;
        var depth = 1;
        var index = start;
        while (index < text.Length && depth > 0)
        {
            var tag = Tag().Match(text, index);
            if (!tag.Success)
                return false;

            var tagName = tag.Groups["name"].Value.ToLowerInvariant();
            if (!string.Equals(tagName, name, StringComparison.Ordinal))
            {
                index = tag.Index + 1;
                continue;
            }

            if (tag.Groups["close"].Success)
            {
                depth--;
                if (depth == 0)
                {
                    inner = text[start..tag.Index];
                    after = tag.Index + tag.Length;
                    return true;
                }
            }
            else
            {
                depth++;
            }

            index = tag.Index + tag.Length;
        }

        return false;
    }

    [GeneratedRegex(@"\[(?<close>/)?(?<name>b|i|u|s)\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Tag();
}
