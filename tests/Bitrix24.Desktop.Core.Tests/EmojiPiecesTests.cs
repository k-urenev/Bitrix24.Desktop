using Bitrix24.Desktop.Core;

namespace Bitrix24.Desktop.Core.Tests;

public class EmojiPiecesTests
{
    private static bool Known(int codePoint) => codePoint is 0x1F600 or 0x2764;

    [Fact]
    public void Splits_a_smiley_out_of_the_sentence()
    {
        var pieces = EmojiPieces.Split("Привет \U0001F600!", Known);

        Assert.Equal(3, pieces.Count);
        Assert.Equal("Привет ", pieces[0].Text);
        Assert.Equal(0, pieces[0].CodePoint);
        Assert.Equal("\U0001F600", pieces[1].Text);
        Assert.Equal(0x1F600, pieces[1].CodePoint);
        Assert.Equal("!", pieces[2].Text);
    }

    [Fact]
    public void Keeps_the_variation_selector_on_the_picture()
    {
        var pieces = EmojiPieces.Split("\u2764\uFE0F", Known);

        Assert.Single(pieces);
        Assert.Equal(0x2764, pieces[0].CodePoint);
        Assert.Equal("\u2764\uFE0F", pieces[0].Text);
    }

    [Fact]
    public void Keeps_a_joiner_between_pictures()
    {
        var text = "\U0001F600\u200D\U0001F600";
        var pieces = EmojiPieces.Split(text, Known);

        Assert.Equal(2, pieces.Count);
        Assert.Equal(text, string.Concat(pieces.Select(piece => piece.Text)));
    }

    [Fact]
    public void Keeps_digits_as_text_when_every_code_is_known()
    {
        var pieces = EmojiPieces.Split("1\U0001F6002", _ => true);

        Assert.Equal(3, pieces.Count);
        Assert.Equal("1", pieces[0].Text);
        Assert.Equal(0, pieces[0].CodePoint);
        Assert.Equal(0x1F600, pieces[1].CodePoint);
        Assert.Equal("2", pieces[2].Text);
    }

    [Fact]
    public void Leaves_an_unknown_symbol_in_the_text()
    {
        var pieces = EmojiPieces.Split("A\u2603B", Known);

        Assert.Single(pieces);
        Assert.Equal("A\u2603B", pieces[0].Text);
        Assert.Equal(0, pieces[0].CodePoint);
    }
}
