using Bitrix24.Desktop.Core;

namespace Bitrix24.Desktop.Core.Tests;

public class StartupCommandTests
{
    [Fact]
    public void Command_quotes_the_executable()
    {
        var command = StartupCommand.Format(@"C:\Users\Admin\Desktop\Bitrix24\Bitrix24.Desktop.exe");

        Assert.Equal(
            "\"C:\\Users\\Admin\\Desktop\\Bitrix24\\Bitrix24.Desktop.exe\"",
            command);
        Assert.False(StartupCommand.HidesWindow(command));
        Assert.True(StartupCommand.SameExecutable(
            command,
            @"C:\Users\Admin\Desktop\Bitrix24\Bitrix24.Desktop.exe"));
    }

    [Fact]
    public void Old_tray_command_still_points_at_the_same_program()
    {
        var command = "\"C:\\Users\\Admin\\Desktop\\Bitrix24\\Bitrix24.Desktop.exe\" --tray";

        Assert.True(StartupCommand.HidesWindow(command));
        Assert.True(StartupCommand.SameExecutable(
            command,
            @"C:\Users\Admin\Desktop\Bitrix24\Bitrix24.Desktop.exe"));
    }

    [Fact]
    public void Different_folder_is_not_the_same_program()
    {
        Assert.False(StartupCommand.SameExecutable(
            "\"C:\\Old\\Bitrix24.Desktop.exe\" --tray",
            @"C:\Users\Admin\Desktop\Bitrix24\Bitrix24.Desktop.exe"));
    }
}
