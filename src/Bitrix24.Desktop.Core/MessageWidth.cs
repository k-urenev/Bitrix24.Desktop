namespace Bitrix24.Desktop.Core;

public static class MessageWidth
{
    public const double InlineEmoji = 20;
    public const double QuoteEmoji = 18;
    public const double QuoteChrome = 20;

    public static bool IsSingleEmoji(IReadOnlyList<MessagePiece> pieces, Func<int, bool> isEmoji)
    {
        if (pieces.Count != 1 || pieces[0] is not MessageText text || string.IsNullOrWhiteSpace(text.Text))
            return false;

        var glyphs = EmojiPieces.Split(text.Text.Trim(), isEmoji);
        return glyphs.Count == 1 && glyphs[0].CodePoint != 0;
    }

    public static double EmojiSize(bool single, int nativePixels)
    {
        if (!single)
            return InlineEmoji;

        var doubled = InlineEmoji * 2;
        if (nativePixels <= 0)
            return doubled;

        return Math.Min(doubled, nativePixels);
    }
}
