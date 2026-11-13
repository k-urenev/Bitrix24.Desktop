using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;
using Bitrix24.Desktop.Core;
using Bitrix24.Desktop.Core.Storage;

namespace Bitrix24.Desktop.Core.Tests;

public class SupportArchiveTests
{
    [Fact]
    public void Zip_has_log_db_and_gap_and_skips_auth()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "b24-pack-" + Guid.NewGuid().ToString("N")));
        try
        {
            var log = Path.Combine(root.FullName, "app.log");
            File.WriteAllText(log, "строка журнала" + Environment.NewLine, Encoding.UTF8);
            File.WriteAllText(Path.Combine(root.FullName, "auth.bin"), "secret");
            var database = new LocalDatabase(Path.Combine(root.FullName, "app.db"));
            database.Initialize();
            database.SaveDialogs(new[]
            {
                new DialogSummary("chat5", "Андрей", null, "#00ACE3", "привет", 100, 2, DateTimeOffset.Parse("2026-11-12T11:00:00+03:00"), 1, "chat")
            });
            database.SaveMessages("chat5", new[]
            {
                new ChatMessage(90, "chat5", 2, "Андрей", "старое", null, false, Array.Empty<ChatFile>())
            });

            var zip = Path.Combine(root.FullName, SupportArchive.FileName(DateTimeOffset.Parse("2026-11-12T11:43:00+03:00")));
            SupportArchive.Write(zip, "версия 1.0.1", log, database);

            Assert.Equal("Bitrix24-поддержка-20261112-1143.zip", Path.GetFileName(zip));
            using var archive = ZipFile.OpenRead(zip);
            var names = archive.Entries.Select(entry => entry.FullName.Replace('\\', '/')).ToArray();
            Assert.Contains("info.txt", names);
            Assert.Contains("app.log", names);
            Assert.Contains("app.db", names);
            Assert.Contains("gaps.txt", names);
            Assert.DoesNotContain(names, name => name.Contains("auth", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("версия 1.0.1", Read(archive, "info.txt").Trim());
            Assert.Contains("строка журнала", Read(archive, "app.log"));
            Assert.Contains("chat5", Read(archive, "gaps.txt"));
            Assert.Contains("100", Read(archive, "gaps.txt"));
            Assert.Contains("90", Read(archive, "gaps.txt"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root.FullName, true);
        }
    }

    [Fact]
    public void Window_and_chat_lines_name_the_gap()
    {
        Assert.Equal("скрыто", FeedTrace.Window(false, false));
        Assert.Equal("свёрнуто", FeedTrace.Window(true, true));
        Assert.Equal("на экране", FeedTrace.Window(true, false));
        Assert.Equal("нет", FeedTrace.Selected(null, null));
        Assert.Equal(
            "чат chat5 «Андрей» последнее 100 лента до 90 окно скрыто выбран нет догрузка нет",
            FeedTrace.ChatLine("chat5", "Андрей", 100, 90, "скрыто", "нет", false));
        Assert.Equal(
            "лента chat5 после появления окна в списке 100 в ленте нет (хвост 90)",
            FeedTrace.GapLine("chat5", 100, 90, "после появления окна"));
    }

    private static string Read(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        Assert.NotNull(entry);
        using var stream = entry!.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
