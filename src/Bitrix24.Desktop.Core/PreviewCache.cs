namespace Bitrix24.Desktop.Core;

public static class PreviewCache
{
    public const long MaxBytes = 1024L * 1024 * 1024;

    private static readonly object Gate = new();

    public static void Touch(string path)
    {
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
    }

    public static void Store(string folder, string name, byte[] bytes, long maxBytes = MaxBytes)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        var temporary = path + ".part";
        lock (Gate)
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
            RemoveOldest(folder, maxBytes);
        }
    }

    public static void Prune(string folder, long maxBytes = MaxBytes)
    {
        if (maxBytes < 0 || !Directory.Exists(folder))
            return;

        lock (Gate)
            RemoveOldest(folder, maxBytes);
    }

    private static void RemoveOldest(string folder, long maxBytes)
    {
        foreach (var part in Directory.EnumerateFiles(folder, "*.part"))
            TryDelete(part);

        var files = new DirectoryInfo(folder)
            .EnumerateFiles()
            .Select(file => (file.FullName, file.Length, file.LastWriteTimeUtc))
            .OrderBy(file => file.LastWriteTimeUtc)
            .ToList();
        var total = files.Sum(file => file.Length);
        foreach (var file in files)
        {
            if (total <= maxBytes)
                return;

            if (TryDelete(file.FullName))
                total -= file.Length;
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
