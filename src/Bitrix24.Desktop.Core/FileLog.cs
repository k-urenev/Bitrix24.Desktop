namespace Bitrix24.Desktop.Core;

public static class FileLog
{
    private static readonly object Gate = new();
    private static string? _path;

    public static void Initialize(string path) => _path = path;

    public static void Write(string message)
    {
        if (_path is null)
            return;

        try
        {
            lock (Gate)
            {
                File.AppendAllText(_path, $"{DateTimeOffset.Now:o} {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static void Write(Exception exception) => Write(exception.ToString());
}
