using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Bitrix24.Desktop.Core;
using Size = System.Windows.Size;

namespace Bitrix24.Desktop.ViewModels;

internal static class BubbleMeasure
{
    private static readonly System.Windows.Media.FontFamily BodyFont = new("Segoe UI");
    private static readonly System.Windows.Media.FontFamily CodeFont = new("Consolas");

    public static double Inner(MessageItem item)
    {
        var width = Content(item.Text);
        if (item.ShowAuthor)
            width = Math.Max(width, Run(item.Author, 13, bold: true));
        width = Math.Max(width, Time(item));
        return width;
    }

    public static double Content(string? text)
    {
        var pieces = QuoteText.Parse(text);
        if (pieces.Count == 0)
            return 0;

        var single = MessageWidth.IsSingleEmoji(pieces, EmojiCatalog.Shared.Has);
        var width = 0d;
        foreach (var piece in pieces)
        {
            var block = piece switch
            {
                MessageText plain => TextWidth(single ? plain.Text.Trim() : plain.Text, single),
                MessageCode code => CodeWidth(code.Text),
                MessageQuote quote => QuoteWidth(quote),
                _ => 0d
            };
            width = Math.Max(width, block);
        }

        return width <= 0 ? 0 : width + 12;
    }

    public static double Time(MessageItem item)
    {
        if (item.TimeLabel.Length == 0)
            return 0;

        var width = Run(item.TimeLabel, 10, bold: false);
        if (item.IsEdited)
            width += Run("изменено", 10, bold: false) + 6;
        if (item.ShowReceipt)
            width += 4 + (item.IsViewed ? 14 : 11);
        return width;
    }

    private static double TextWidth(string text, bool single)
    {
        var emoji = single
            ? MessageWidth.EmojiSize(true, NativePixels(text))
            : MessageWidth.InlineEmoji;
        return WidestLine(text, emoji, 15, bold: false);
    }

    private static double QuoteWidth(MessageQuote quote)
    {
        var header = 0d;
        if (quote.Author.Length > 0)
            header += Run(quote.Author, 13, bold: true);
        if (quote.When.Length > 0)
            header += Run((quote.Author.Length > 0 ? "  " : "") + quote.When, 11, bold: false);

        var body = 0d;
        foreach (var segment in CodeText.Split(quote.Body))
        {
            var width = segment.IsCode
                ? CodeWidth(segment.Text)
                : WidestLine(segment.Text, MessageWidth.QuoteEmoji, 13, bold: false);
            body = Math.Max(body, width);
        }

        var content = Math.Max(header, body);
        return content > 0 ? content + MessageWidth.QuoteChrome : 0;
    }

    private static double WidestLine(string text, double emojiSize, double fontSize, bool bold)
    {
        var line = 0d;
        var max = 0d;
        foreach (var piece in EmojiPieces.Split(text, EmojiCatalog.Shared.Has))
        {
            if (piece.CodePoint != 0)
            {
                line += emojiSize;
                continue;
            }

            var parts = LinkText.Visible(piece.Text).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (var index = 0; index < parts.Length; index++)
            {
                if (index > 0)
                {
                    max = Math.Max(max, line);
                    line = 0;
                }

                if (parts[index].Length > 0)
                    line += Run(parts[index], fontSize, bold);
            }
        }

        return Math.Max(max, line);
    }

    private static double CodeWidth(string text)
    {
        var max = 0d;
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (line.Length > 0)
                max = Math.Max(max, Run(line, 13, bold: false, CodeFont));
        }

        return max > 0 ? max + 16 : 0;
    }

    private static double Run(string text, double size, bool bold, System.Windows.Media.FontFamily? family = null)
    {
        var block = new TextBlock
        {
            FontFamily = family ?? BodyFont,
            FontSize = size,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Text = text,
            TextWrapping = TextWrapping.NoWrap
        };
        block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return block.DesiredSize.Width;
    }

    private static int NativePixels(string text)
    {
        var glyphs = EmojiPieces.Split(text, EmojiCatalog.Shared.Has);
        if (glyphs.Count != 1 || glyphs[0].CodePoint == 0)
            return 0;

        return EmojiCatalog.Shared.PixelSize(glyphs[0].CodePoint);
    }
}
