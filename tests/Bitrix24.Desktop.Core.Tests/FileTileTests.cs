using Bitrix24.Desktop.Core;

namespace Bitrix24.Desktop.Core.Tests;

public class FileTileTests
{
    [Theory]
    [InlineData("договор.pdf", FileTileKind.Document)]
    [InlineData("таблица.xlsx", FileTileKind.Sheet)]
    [InlineData("отчёт.csv", FileTileKind.Sheet)]
    [InlineData("голос.mp3", FileTileKind.Audio)]
    [InlineData("ролик.mp4", FileTileKind.Video)]
    [InlineData("скрипт.py", FileTileKind.Code)]
    [InlineData("архив.zip", FileTileKind.Generic)]
    [InlineData("снимок.png", FileTileKind.Generic)]
    public void Extension_picks_the_librechat_tile(string name, FileTileKind kind)
    {
        Assert.Equal(kind, FileTile.FromName(name));
    }
}
