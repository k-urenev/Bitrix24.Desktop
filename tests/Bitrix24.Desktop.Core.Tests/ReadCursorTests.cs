using System.Text.Json;
using Bitrix24.Desktop.Core;

namespace Bitrix24.Desktop.Core.Tests;

public class ReadCursorTests
{
    [Fact]
    public void Unread_message_stays_behind_the_other_persons_cursor()
    {
        using var document = JsonDocument.Parse("""
            {
              "readed_list": [
                { "user_id": 632, "user_name": "Андрей Просветов", "message_id": 2995268, "date": null }
              ]
            }
            """);

        var read = ReadCursor.FromDialog(document.RootElement, userId: 520);

        Assert.Equal(2995268, read);
        Assert.True(read.GetValueOrDefault() >= 2995268);
        Assert.False(read.GetValueOrDefault() >= 2995652);
        Assert.Equal(0, ReadCursor.FromDialog(document.RootElement, userId: 632));
        using var empty = JsonDocument.Parse("{}");
        Assert.Null(ReadCursor.FromDialog(empty.RootElement, userId: 520));
    }

    [Fact]
    public void Delivered_means_the_last_message_was_read()
    {
        Assert.True(ReadCursor.IsDelivered("delivered"));
        Assert.False(ReadCursor.IsDelivered("received"));
        Assert.False(ReadCursor.IsDelivered(""));
    }
}
