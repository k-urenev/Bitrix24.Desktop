using System.IO;
using Bitrix24.Desktop.Core;
using Microsoft.Win32;

namespace Bitrix24.Desktop.Services;

public static class WindowsStartup
{
    public const string ValueName = "Битрикс24";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return StartupCommand.SameExecutable(key?.GetValue(ValueName) as string, CurrentPath());
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("Не удалось открыть автозагрузку Windows.");
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var path = CurrentPath();
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Не удалось определить файл программы.");

        key.SetValue(ValueName, StartupCommand.Format(path));
    }

    public static bool CommandHidesWindow()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            var command = key?.GetValue(ValueName) as string;
            return StartupCommand.SameExecutable(command, CurrentPath()) && StartupCommand.HidesWindow(command);
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
            return false;
        }
    }

    public static void RepairIfTargetMissing()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            var registered = StartupCommand.Executable(key?.GetValue(ValueName) as string);
            if (string.IsNullOrWhiteSpace(registered) || File.Exists(registered))
                return;

            SetEnabled(true);
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
        }
    }

    private static string CurrentPath() => Environment.ProcessPath ?? "";
}
