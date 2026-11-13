using System.Net;
using System.Text.RegularExpressions;

namespace Bitrix24.Desktop.Core;

public sealed record OpenGraphCard(string Title, string Description, string? ImageUrl);

public static partial class OpenGraph
{
    public static OpenGraphCard? Read(string? html, Uri page)
    {
        if (string.IsNullOrEmpty(html))
            return null;

        var title = Clean(Meta(html, "og:title"));
        if (title.Length == 0)
            title = Clean(DocumentTitle(html));
        if (title.Length == 0)
            return null;

        var description = Clean(Meta(html, "og:description"));
        if (description.Length == 0)
            description = Clean(Meta(html, "description"));

        return new OpenGraphCard(title, description, Image(html, page));
    }

    private static string? Image(string html, Uri page)
    {
        var raw = WebUtility.HtmlDecode(Meta(html, "og:image")).Trim();
        if (raw.Length == 0)
            return null;
        if (!Uri.TryCreate(page, raw, out var image))
            return null;
        if (image.Scheme != Uri.UriSchemeHttp && image.Scheme != Uri.UriSchemeHttps)
            return null;
        return image.AbsoluteUri;
    }

    private static string Meta(string html, string key)
    {
        foreach (Match tag in MetaTag().Matches(html))
        {
            string? property = null;
            string? name = null;
            string? content = null;
            foreach (Match attribute in Attribute().Matches(tag.Value))
            {
                var attributeName = attribute.Groups["name"].Value;
                var value = attribute.Groups["value"].Value;
                if (attributeName.Equals("property", StringComparison.OrdinalIgnoreCase))
                    property = value;
                else if (attributeName.Equals("name", StringComparison.OrdinalIgnoreCase))
                    name = value;
                else if (attributeName.Equals("content", StringComparison.OrdinalIgnoreCase))
                    content = value;
            }

            if (content is null)
                continue;
            if (property is not null && property.Equals(key, StringComparison.OrdinalIgnoreCase))
                return content;
            if (name is not null && name.Equals(key, StringComparison.OrdinalIgnoreCase))
                return content;
        }

        return "";
    }

    private static string DocumentTitle(string html)
    {
        var title = TitleTag().Match(html);
        if (!title.Success)
            return "";
        return TitleInner().Replace(title.Groups["text"].Value, "");
    }

    private static string Clean(string value)
    {
        if (value.Length == 0)
            return "";

        var text = WebUtility.HtmlDecode(value);
        text = WebUtility.HtmlDecode(text);
        text = Breaks().Replace(text, " ").Trim();
        if (text.Length > 500)
            text = text[..500].Trim();
        return text;
    }

    [GeneratedRegex(@"<meta\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MetaTag();

    [GeneratedRegex(@"(?<name>property|name|content)\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>[^\s""'>]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Attribute();

    [GeneratedRegex(@"<title[^>]*>(?<text>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex TitleTag();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex TitleInner();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Breaks();
}
