using System.Text;
using System.Text.RegularExpressions;

namespace Bitrix24.Desktop.Core;

public sealed record TextLink(string Text, string? Target);

public static partial class LinkText
{
    public static List<TextLink> Split(string? text)
    {
        var pieces = new List<TextLink>();
        if (string.IsNullOrEmpty(text))
            return pieces;

        var index = 0;
        while (index < text.Length)
        {
            var tag = UrlTag().Match(text, index);
            var bare = BareUrl().Match(text, index);
            var tagAt = tag.Success ? tag.Index : int.MaxValue;
            var bareAt = bare.Success ? bare.Index : int.MaxValue;
            if (tagAt == int.MaxValue && bareAt == int.MaxValue)
            {
                AddPlain(pieces, text[index..]);
                break;
            }

            if (tagAt <= bareAt)
            {
                if (tag.Index > index)
                    AddPlain(pieces, text[index..tag.Index]);
                AddTag(pieces, tag);
                var next = tag.Index + tag.Length;
                index = next > index ? next : index + 1;
                continue;
            }

            if (bare.Index > index)
                AddPlain(pieces, text[index..bare.Index]);
            var consumed = AppendBare(pieces, bare);
            var nextBare = bare.Index + Math.Max(consumed, 1);
            index = nextBare > index ? nextBare : index + 1;
        }

        return pieces;
    }

    public static string? FirstTarget(string? text)
    {
        foreach (var piece in QuoteText.Parse(text))
        {
            if (piece is MessageText plain)
            {
                var target = FirstIn(plain.Text);
                if (target is not null)
                    return target;
                continue;
            }

            if (piece is not MessageQuote quote)
                continue;

            foreach (var segment in CodeText.Split(quote.Body))
            {
                if (segment.IsCode)
                    continue;
                var target = FirstIn(segment.Text);
                if (target is not null)
                    return target;
            }
        }

        return null;
    }

    private static string? FirstIn(string text)
    {
        foreach (var piece in Split(text))
        {
            if (piece.Target is not null)
                return piece.Target;
        }

        return null;
    }

    public static string Visible(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var builder = new StringBuilder();
        foreach (var piece in Split(text))
            builder.Append(piece.Text);
        return builder.ToString();
    }

    private static void AddTag(List<TextLink> pieces, Match tag)
    {
        var href = tag.Groups["href"].Success ? tag.Groups["href"].Value.Trim().Trim('"').Trim('\'') : "";
        var label = tag.Groups["label"].Value.Trim();
        var visible = label.Length > 0 ? label : href;
        if (visible.Length == 0)
            return;

        if (TryHttp(href, out var target) || TryHttp(visible, out target))
            pieces.Add(new TextLink(visible, target));
        else
            AddPlain(pieces, visible);
    }

    private static int AppendBare(List<TextLink> pieces, Match bare)
    {
        var raw = bare.Value;
        var visible = TrimTail(raw);
        if (visible.Length > 0 && TryHttp(visible, out var target))
        {
            pieces.Add(new TextLink(visible, target));
            return visible.Length;
        }

        AddPlain(pieces, raw);
        return raw.Length;
    }

    private static bool TryHttp(string value, out string target)
    {
        target = "";
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var candidate = value.Trim();
        if (candidate.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            candidate = "https://" + candidate;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;
        if (uri.Host.Length == 0)
            return false;
        if (!uri.Host.Contains('.') && !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return false;

        target = candidate;
        return true;
    }

    private static string TrimTail(string url)
    {
        var end = url.Length;
        while (end > 0 && ShouldTrim(url, end - 1))
            end--;
        return url[..end];
    }

    private static bool ShouldTrim(string url, int index)
    {
        switch (url[index])
        {
            case '.':
            case ',':
            case ';':
            case ':':
            case '!':
            case '?':
            case '"':
            case '\'':
            case '»':
            case '…':
            case '>':
            case ']':
            case '}':
            case '。':
            case '，':
            case '、':
                return true;
            case ')':
                var open = 0;
                var close = 0;
                for (var i = 0; i <= index; i++)
                {
                    if (url[i] == '(')
                        open++;
                    else if (url[i] == ')')
                        close++;
                }

                return close > open;
            default:
                return false;
        }
    }

    private static void AddPlain(List<TextLink> pieces, string text)
    {
        if (text.Length == 0)
            return;
        if (pieces.Count > 0 && pieces[^1].Target is null)
        {
            pieces[^1] = pieces[^1] with { Text = pieces[^1].Text + text };
            return;
        }

        pieces.Add(new TextLink(text, null));
    }

    [GeneratedRegex(@"\[url(?:=(?<href>[^\]]*))?\](?<label>.*?)\[/url\]", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex UrlTag();

    [GeneratedRegex(@"(?<![\w@])(?:https?://|www\.)[^\s<>\[\]""'{}|\\^`]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BareUrl();
}
