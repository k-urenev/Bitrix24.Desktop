namespace Bitrix24.Desktop.Core;

public static class StartupCommand
{
    public static string Format(string executablePath) =>
        "\"" + executablePath + "\"";

    public static bool HidesWindow(string? command)
    {
        foreach (var part in Tail(command).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals("--tray", StringComparison.OrdinalIgnoreCase)
                || part.Equals("/tray", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string Tail(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return "";

        var text = command.Trim();
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end < 0 ? "" : text[(end + 1)..];
        }

        var space = text.IndexOf(' ');
        return space < 0 ? "" : text[(space + 1)..];
    }

    public static string? Executable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var text = command.Trim();
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }

        var space = text.IndexOf(' ');
        return space < 0 ? text : text[..space];
    }

    public static bool SameExecutable(string? command, string executablePath)
    {
        var registered = Executable(command);
        if (string.IsNullOrWhiteSpace(registered) || string.IsNullOrWhiteSpace(executablePath))
            return false;

        try
        {
            return string.Equals(
                Path.GetFullPath(registered),
                Path.GetFullPath(executablePath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return string.Equals(registered, executablePath, StringComparison.OrdinalIgnoreCase);
        }
    }
}
