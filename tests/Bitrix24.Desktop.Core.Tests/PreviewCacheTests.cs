using Bitrix24.Desktop.Core;

namespace Bitrix24.Desktop.Core.Tests;

public class PreviewCacheTests
{
    [Fact]
    public void Prune_deletes_the_oldest_files_until_the_folder_fits()
    {
        var folder = NewFolder();
        try
        {
            Write(folder, "1", 100, minutesAgo: 3);
            Write(folder, "2", 100, minutesAgo: 2);
            Write(folder, "3", 100, minutesAgo: 1);

            PreviewCache.Prune(folder, 150);

            Assert.False(File.Exists(Path.Combine(folder, "1")));
            Assert.False(File.Exists(Path.Combine(folder, "2")));
            Assert.True(File.Exists(Path.Combine(folder, "3")));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Touch_keeps_an_opened_preview_ahead_of_newer_downloads()
    {
        var folder = NewFolder();
        try
        {
            var older = Write(folder, "older", 100, minutesAgo: 5);
            Write(folder, "newer", 100, minutesAgo: 1);
            PreviewCache.Touch(older);

            PreviewCache.Prune(folder, 100);

            Assert.True(File.Exists(older));
            Assert.False(File.Exists(Path.Combine(folder, "newer")));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static string NewFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "b24-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string Write(string folder, string name, int bytes, int minutesAgo)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-minutesAgo));
        return path;
    }
}
