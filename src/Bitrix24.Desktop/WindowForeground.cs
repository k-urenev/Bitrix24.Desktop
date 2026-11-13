using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Bitrix24.Desktop.Core;

namespace Bitrix24.Desktop;

internal static class WindowForeground
{
    private const int SwShow = 5;
    private const int SwRestore = 9;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpShowWindow = 0x0040;
    private const byte VkMenu = 0x12;
    private const uint KeyeventfKeyup = 0x0002;

    public static void Bring(Window window)
    {
        try
        {
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            if (!window.IsVisible)
                window.Show();

            var handle = new WindowInteropHelper(window).EnsureHandle();
            ShowWindow(handle, IsIconic(handle) ? SwRestore : SwShow);
            SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
            BringWindowToTop(handle);
            SetForegroundWindow(handle);
            if (GetForegroundWindow() != handle)
                SwitchToThisWindow(handle, true);
            if (GetForegroundWindow() != handle)
            {
                keybd_event(VkMenu, 0, 0, UIntPtr.Zero);
                try
                {
                    SetForegroundWindow(handle);
                }
                finally
                {
                    keybd_event(VkMenu, 0, KeyeventfKeyup, UIntPtr.Zero);
                }
            }

            window.Activate();
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
            try
            {
                if (window.WindowState == WindowState.Minimized)
                    window.WindowState = WindowState.Normal;
                if (!window.IsVisible)
                    window.Show();
                window.Activate();
            }
            catch (Exception nested)
            {
                FileLog.Write(nested);
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr handle, int command);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr handle, bool altTab);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
}
