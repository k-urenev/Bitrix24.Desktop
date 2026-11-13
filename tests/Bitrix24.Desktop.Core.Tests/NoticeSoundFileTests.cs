using Bitrix24.Desktop.Core;

namespace Bitrix24.Desktop.Core.Tests;

public class NoticeSoundFileTests
{
    [Fact]
    public void Missing_choice_uses_the_built_in_file()
    {
        var fallback = Path.GetTempFileName();
        try
        {
            Assert.Equal(fallback, NoticeSoundFile.Resolve("", fallback));
            Assert.Equal(fallback, NoticeSoundFile.Resolve(@"C:\missing\tone.mp3", fallback));
        }
        finally
        {
            File.Delete(fallback);
        }
    }

    [Fact]
    public void Existing_file_replaces_the_built_in_one()
    {
        var chosen = Path.GetTempFileName();
        try
        {
            Assert.Equal(chosen, NoticeSoundFile.Resolve(chosen, @"C:\built-in.mp3"));
        }
        finally
        {
            File.Delete(chosen);
        }
    }
}
