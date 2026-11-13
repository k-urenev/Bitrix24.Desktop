namespace Bitrix24.Desktop.Core;

public sealed class AppPaths
{
    public string Directory { get; }
    public string DatabaseFile { get; }
    public string AuthFile { get; }
    public string LogFile { get; }
    public string WebViewFolder { get; }

    private AppPaths(string directory)
    {
        Directory = directory;
        DatabaseFile = Path.Combine(directory, "app.db");
        AuthFile = Path.Combine(directory, "auth.bin");
        LogFile = Path.Combine(directory, "app.log");
        WebViewFolder = Path.Combine(directory, "WebView2");
    }

    public static AppPaths Create()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Bitrix24.Desktop");
        System.IO.Directory.CreateDirectory(directory);
        return new AppPaths(directory);
    }
}
