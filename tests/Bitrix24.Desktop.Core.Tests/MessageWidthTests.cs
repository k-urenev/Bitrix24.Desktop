using Bitrix24.Desktop.Core;

namespace Bitrix24.Desktop.Core.Tests;

public class MessageWidthTests
{
    private static bool Known(int codePoint) => codePoint is 0x1F600 or 0x2764;

    [Fact]
    public void A_lone_picture_uses_the_smaller_of_double_size_and_the_file()
    {
        Assert.Equal(40, MessageWidth.EmojiSize(single: true, nativePixels: 160));
        Assert.Equal(32, MessageWidth.EmojiSize(single: true, nativePixels: 32));
        Assert.Equal(40, MessageWidth.EmojiSize(single: true, nativePixels: 0));
        Assert.Equal(20, MessageWidth.EmojiSize(single: false, nativePixels: 160));
    }

    [Fact]
    public void Only_a_message_of_one_picture_counts()
    {
        Assert.True(MessageWidth.IsSingleEmoji(new MessagePiece[] { new MessageText("  \U0001F600  ") }, Known));
        Assert.False(MessageWidth.IsSingleEmoji(new MessagePiece[] { new MessageText("\U0001F600!") }, Known));
        Assert.False(MessageWidth.IsSingleEmoji(new MessagePiece[] { new MessageText("\U0001F600\U0001F600") }, Known));
        Assert.False(MessageWidth.IsSingleEmoji(new MessagePiece[] { new MessageQuote("А", "12:00", "\U0001F600") }, Known));
    }
}
