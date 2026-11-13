using System.Text;
using System.Text.RegularExpressions;

namespace Bitrix24.Desktop.Core;

public readonly record struct CodeSegment(string Text, bool IsCode);

public static partial class CodeText
{
    public static string Mark(string text, out string[] bodies)
    {
        var list = new List<string>();
        var marked = Block().Replace(text, match =>
        {
            var body = Normalize(match.Groups["body"].Value);
            if (body.Length == 0)
                return "";

            var index = list.Count;
            list.Add(body);
            return "\uE000" + index.ToString() + "\uE000";
        });
        bodies = list.ToArray();
        return marked;
    }

    public static string Restore(string text, IReadOnlyList<string> bodies)
    {
        if (bodies.Count == 0 || text.Length == 0)
            return text;

        return Token().Replace(text, match =>
        {
            if (!int.TryParse(match.Groups["i"].Value, out var index) || index < 0 || index >= bodies.Count)
                return match.Value;

            return "[code]" + bodies[index] + "[/code]";
        });
    }

    public static IReadOnlyList<CodeSegment> Split(string? text)
    {
        var result = new List<CodeSegment>();
        if (string.IsNullOrEmpty(text))
            return result;

        var marked = Mark(text, out var bodies);
        var index = 0;
        foreach (Match match in Token().Matches(marked))
        {
            if (match.Index > index)
                result.Add(new CodeSegment(marked[index..match.Index], false));

            if (int.TryParse(match.Groups["i"].Value, out var slot) && slot >= 0 && slot < bodies.Length)
                result.Add(new CodeSegment(bodies[slot], true));

            index = match.Index + match.Length;
        }

        if (index < marked.Length)
            result.Add(new CodeSegment(marked[index..], false));

        return result;
    }

    public static string Visible(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var builder = new StringBuilder();
        foreach (var segment in Split(text))
            builder.Append(segment.Text);
        return builder.ToString();
    }

    private static string Normalize(string body)
    {
        body = body.Replace("\r\n", "\n").Replace('\r', '\n');
        if (body.StartsWith('\n'))
            body = body[1..];
        if (body.EndsWith('\n'))
            body = body[..^1];
        return body;
    }

    [GeneratedRegex(@"\[code(?<attr>[^\]]*)\](?<body>.*?)\[/code\]", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Block();

    [GeneratedRegex("\uE000(?<i>\\d+)\uE000", RegexOptions.CultureInvariant)]
    private static partial Regex Token();
}
