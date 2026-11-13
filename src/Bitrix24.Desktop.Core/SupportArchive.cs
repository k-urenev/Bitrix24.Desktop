using System.IO.Compression;
using System.Text;
using Bitrix24.Desktop.Core.Storage;

namespace Bitrix24.Desktop.Core;

public static class SupportArchive
{
    public static string FileName(DateTimeOffset at) =>
        "Bitrix24-поддержка-" + at.ToString("yyyyMMdd-HHmm") + ".zip";

    public static void Write(string zipPath, string info, string logPath, LocalDatabase database)
    {
        var folder = Path.Combine(Path.GetTempPath(), "b24-support-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "info.txt"), info, Encoding.UTF8);
            WriteGaps(Path.Combine(folder, "gaps.txt"), database);
            CopyLog(logPath, Path.Combine(folder, "app.log"));
            database.BackupTo(Path.Combine(folder, "app.db"));
            if (File.Exists(zipPath))
                File.Delete(zipPath);
            ZipFile.CreateFromDirectory(folder, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static void WriteGaps(string path, LocalDatabase database)
    {
        var gaps = database.ListGaps();
        if (gaps.Count == 0)
        {
            File.WriteAllText(path, "расхождений нет" + Environment.NewLine, Encoding.UTF8);
            return;
        }

        var text = new StringBuilder();
        foreach (var gap in gaps)
        {
            text.Append(gap.DialogId)
                .Append('\t')
                .Append(gap.Title)
                .Append('\t')
                .Append(gap.ListedId)
                .Append('\t')
                .Append(gap.FeedMax)
                .AppendLine();
        }

        File.WriteAllText(path, text.ToString(), Encoding.UTF8);
    }

    private static void CopyLog(string source, string dest)
    {
        if (!File.Exists(source))
        {
            File.WriteAllText(dest, "", Encoding.UTF8);
            return;
        }

        try
        {
            File.Copy(source, dest, overwrite: true);
        }
        catch (IOException)
        {
            File.WriteAllText(dest, File.ReadAllText(source), Encoding.UTF8);
        }
        catch (UnauthorizedAccessException)
        {
            File.WriteAllText(dest, File.ReadAllText(source), Encoding.UTF8);
        }
    }
}
