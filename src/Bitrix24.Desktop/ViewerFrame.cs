using System.Runtime.InteropServices;
using System.Windows;
using Bitrix24.Desktop.Core;
using WinFormsScreen = System.Windows.Forms.Screen;

namespace Bitrix24.Desktop;

internal static class ViewerFrame
{
    public static (int Width, int Height) Pixels { get; private set; } = (1, 1);

    public static void Measure()
    {
        var screens = WinFormsScreen.AllScreens;
        if (screens.Length == 0)
            return;

        long bestResolution = -1;
        long bestArea = -1;
        var best = (Width: 1, Height: 1);
        foreach (var screen in screens)
        {
            var resolution = (long)screen.Bounds.Width * screen.Bounds.Height;
            var box = For(screen);
            var area = (long)box.Width * box.Height;
            if (resolution > bestResolution || (resolution == bestResolution && area > bestArea))
            {
                bestResolution = resolution;
                bestArea = area;
                best = box;
            }
        }

        Pixels = best;
    }

    private static (int Width, int Height) For(WinFormsScreen screen)
    {
        var scale = Dpi(screen) / 96d;
        var side = (int)Math.Ceiling(24 * scale);
        var top = (int)Math.Ceiling(56 * scale);
        var bottom = (int)Math.Ceiling(24 * scale);
        var caption = (int)Math.Ceiling(SystemParameters.WindowCaptionHeight * scale);
        var insetWidth = Math.Max(0, screen.Bounds.Width - screen.WorkingArea.Width) + side * 2;
        var insetHeight = Math.Max(0, screen.Bounds.Height - screen.WorkingArea.Height) + caption + top + bottom;
        return PreviewFit.ViewerBox(screen.Bounds.Width, screen.Bounds.Height, insetWidth, insetHeight);
    }

    private static int Dpi(WinFormsScreen screen)
    {
        var point = new NativePoint
        {
            X = screen.Bounds.X + Math.Max(0, screen.Bounds.Width / 2),
            Y = screen.Bounds.Y + Math.Max(0, screen.Bounds.Height / 2)
        };
        var monitor = MonitorFromPoint(point, 2);
        if (monitor == IntPtr.Zero || GetDpiForMonitor(monitor, 0, out var dpiX, out _) != 0 || dpiX == 0)
            return 96;
        return (int)dpiX;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}
