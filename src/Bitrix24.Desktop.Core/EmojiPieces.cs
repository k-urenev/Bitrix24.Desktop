using System.Text;

namespace Bitrix24.Desktop.Core;

public readonly record struct EmojiPiece(string Text, int CodePoint);

public static class EmojiPieces
{
    public static List<EmojiPiece> Split(string? text, Func<int, bool> isEmoji)
    {
        var pieces = new List<EmojiPiece>();
        if (string.IsNullOrEmpty(text))
            return pieces;

        var plain = new StringBuilder();
        var index = 0;
        while (index < text.Length)
        {
            int codePoint;
            int length;
            if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                codePoint = char.ConvertToUtf32(text, index);
                length = 2;
            }
            else
            {
                codePoint = text[index];
                length = 1;
            }

            var raw = text.Substring(index, length);
            if (codePoint is 0x200D or 0xFE0E or 0xFE0F)
            {
                if (plain.Length == 0 && pieces.Count > 0 && pieces[^1].CodePoint != 0)
                    pieces[^1] = pieces[^1] with { Text = pieces[^1].Text + raw };
                else
                    plain.Append(raw);
            }
            else if (isEmoji(codePoint) && !IsPlainSymbol(codePoint))
            {
                Flush();
                pieces.Add(new EmojiPiece(raw, codePoint));
            }
            else
            {
                plain.Append(raw);
            }

            index += length;
        }

        Flush();
        return pieces;

        void Flush()
        {
            if (plain.Length == 0)
                return;

            pieces.Add(new EmojiPiece(plain.ToString(), 0));
            plain.Clear();
        }
    }

    public static bool IsPlainSymbol(int codePoint) =>
        codePoint is (>= '0' and <= '9') or '#' or '*';
}
