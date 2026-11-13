using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Bitrix24.Desktop.Core;

public abstract record MessagePiece;

public sealed record MessageText(string Text) : MessagePiece;

public sealed record MessageQuote(string Author, string When, string Body) : MessagePiece;

public sealed record MessageCode(string Text) : MessagePiece;

public static class QuoteText
{
    private static readonly string[] Months =
    [
        "января", "февраля", "марта", "апреля", "мая", "июня",
        "июля", "августа", "сентября", "октября", "ноября", "декабря"
    ];

    public const string PreviewMark = "[Цитата]";

    private static readonly Regex Header = new(
        @"^(?<name>.+?) \[(?<when>[^\]]+)\](?: #\S+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex InlineQuote = new(
        @"-{20,} .*? -{20,}|={20,} .*? ={20,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RepeatedMark = new(
        @"(?:\[Цитата\]\s*){2,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Compose(string author, DateTimeOffset? sentAt, string? dialogId, int userId, int messageId, string body)
    {
        var text = (body ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n');
        var when = QuoteWhen(sentAt);
        var head = when.Length == 0 ? author : author + " [" + when + "]";
        var bar = new string('-', 54);
        return bar + "\n" + head + " " + QuoteMark(dialogId, userId, messageId) + "\n" + text + "\n" + bar + "\n";
    }

    public static string QuoteWhen(DateTimeOffset? value) => QuoteWhen(value, DateTime.Today);

    public static string QuoteWhen(DateTimeOffset? value, DateTime today)
    {
        if (value is null)
            return "";

        var local = value.Value.ToLocalTime();
        var clock = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (local.Date == today.Date)
            return "сегодня, " + clock;
        if (local.Date == today.Date.AddDays(-1))
            return "вчера, " + clock;

        var day = local.Day.ToString(CultureInfo.InvariantCulture) + " " + Months[local.Month - 1];
        if (local.Year == today.Year)
            return day + ", " + clock;
        return day + " " + local.Year.ToString(CultureInfo.InvariantCulture) + ", " + clock;
    }

    public static string QuoteMark(string? dialogId, int userId, int messageId)
    {
        var id = messageId.ToString(CultureInfo.InvariantCulture);
        if (string.IsNullOrEmpty(dialogId))
            return "#" + id;
        if (int.TryParse(dialogId, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            return "#" + dialogId + ":" + userId.ToString(CultureInfo.InvariantCulture) + "/" + id;
        return "#" + dialogId + "/" + id;
    }

    public static IReadOnlyList<MessagePiece> Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<MessagePiece>();

        var marked = CodeText.Mark(text, out var bodies);
        return Expand(ParseBars(marked), bodies);
    }

    private static List<MessagePiece> ParseBars(string text)
    {
        var pieces = new List<MessagePiece>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var plain = new StringBuilder();
        for (var index = 0; index < lines.Length; index++)
        {
            if (!IsBar(lines[index], out var mark) || !TryClose(lines, index + 1, mark, out var close))
            {
                AppendLine(plain, lines[index]);
                continue;
            }

            Flush(pieces, plain);
            var inner = new string[close - index - 1];
            Array.Copy(lines, index + 1, inner, 0, inner.Length);
            pieces.Add(ReadQuote(inner));
            index = close;
        }

        Flush(pieces, plain);
        return pieces;
    }

    private static List<MessagePiece> Expand(IReadOnlyList<MessagePiece> pieces, IReadOnlyList<string> bodies)
    {
        var result = new List<MessagePiece>();
        foreach (var piece in pieces)
        {
            switch (piece)
            {
                case MessageText plain:
                    AppendExpanded(result, plain.Text, bodies);
                    break;
                case MessageQuote quote:
                    result.Add(quote with { Body = CodeText.Restore(quote.Body, bodies) });
                    break;
            }
        }

        return result;
    }

    private static void AppendExpanded(List<MessagePiece> pieces, string text, IReadOnlyList<string> bodies)
    {
        if (bodies.Count == 0 || text.IndexOf('\uE000') < 0)
        {
            if (text.Length > 0)
                pieces.Add(new MessageText(text));
            return;
        }

        var restored = CodeText.Restore(text, bodies);
        foreach (var segment in CodeText.Split(restored))
        {
            if (segment.IsCode)
            {
                if (segment.Text.Length > 0)
                    pieces.Add(new MessageCode(segment.Text));
                continue;
            }

            var plain = segment.Text.Trim('\n');
            if (plain.Length > 0)
                pieces.Add(new MessageText(plain));
        }
    }

    public static string Flatten(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var pieces = Parse(text);
        if (pieces.Count == 1 && pieces[0] is MessageText only)
            return MarkInline(only.Text);

        var builder = new StringBuilder();
        foreach (var piece in pieces)
        {
            var chunk = piece switch
            {
                MessageText plain => MarkInline(plain.Text),
                MessageCode code => Collapse(code.Text),
                MessageQuote => PreviewMark,
                _ => ""
            };
            if (chunk.Length == 0)
                continue;
            if (chunk == PreviewMark && builder.ToString().EndsWith(PreviewMark, StringComparison.Ordinal))
                continue;
            if (builder.Length > 0)
                builder.Append(' ');
            builder.Append(chunk);
        }

        return RepeatedMark.Replace(builder.ToString(), PreviewMark + " ").Trim();
    }

    private static string MarkInline(string text)
    {
        var collapsed = Collapse(text);
        if (collapsed.IndexOf("---", StringComparison.Ordinal) < 0
            && collapsed.IndexOf("===", StringComparison.Ordinal) < 0)
            return collapsed;

        var marked = InlineQuote.Replace(collapsed, PreviewMark);
        return RepeatedMark.Replace(marked, PreviewMark + " ").Trim();
    }

    private static MessageQuote ReadQuote(string[] lines)
    {
        var start = 0;
        var author = "";
        var when = "";
        if (lines.Length > 0 && Header.Match(lines[0].Trim()) is { Success: true } header)
        {
            author = header.Groups["name"].Value.Trim();
            when = header.Groups["when"].Value.Trim();
            start = 1;
        }

        var body = new StringBuilder();
        for (var index = start; index < lines.Length; index++)
            AppendLine(body, lines[index]);
        return new MessageQuote(author, when, body.ToString().Trim());
    }

    private static bool TryClose(string[] lines, int from, char mark, out int close)
    {
        for (var index = from; index < lines.Length; index++)
        {
            if (IsBar(lines[index], out var next) && next == mark)
            {
                close = index;
                return true;
            }
        }

        close = -1;
        return false;
    }

    private static bool IsBar(string line, out char mark)
    {
        var trimmed = line.Trim();
        mark = trimmed.Length > 0 ? trimmed[0] : '\0';
        if (trimmed.Length < 20 || mark is not ('-' or '='))
            return false;

        foreach (var character in trimmed)
        {
            if (character != mark)
                return false;
        }

        return true;
    }

    private static void AppendLine(StringBuilder builder, string line)
    {
        if (builder.Length > 0)
            builder.Append('\n');
        builder.Append(line);
    }

    private static void Flush(List<MessagePiece> pieces, StringBuilder plain)
    {
        var text = plain.ToString().Trim('\n');
        plain.Clear();
        if (text.Length > 0)
            pieces.Add(new MessageText(text));
    }

    private static string Collapse(string text) =>
        text.Replace("\r", " ").Replace("\n", " ").Trim();
}
