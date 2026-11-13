using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Bitrix24.Desktop.Core;

namespace Bitrix24.Desktop.Controls;

static class ClipboardText
{
    private const uint Moveable = 0x0002;
    private const uint UnicodeText = 13;

    public static void Set(string text)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            if (TryPut(text))
                return;

            PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            if (attempt >= 2)
                Thread.Sleep(15);
        }

        FileLog.Write("Не удалось положить текст в буфер: " + Holder());
    }

    private static bool TryPut(string text)
    {
        if (!OpenClipboard(IntPtr.Zero))
            return false;

        var handle = IntPtr.Zero;
        var owned = false;
        try
        {
            if (!EmptyClipboard())
                return false;

            var payload = new byte[Encoding.Unicode.GetByteCount(text) + 2];
            Encoding.Unicode.GetBytes(text, 0, text.Length, payload, 0);
            handle = GlobalAlloc(Moveable, (UIntPtr)payload.Length);
            if (handle == IntPtr.Zero)
                return false;

            var locked = GlobalLock(handle);
            if (locked == IntPtr.Zero)
                return false;

            try
            {
                Marshal.Copy(payload, 0, locked, payload.Length);
            }
            finally
            {
                GlobalUnlock(handle);
            }

            owned = SetClipboardData(UnicodeText, handle) != IntPtr.Zero;
            return owned;
        }
        finally
        {
            if (handle != IntPtr.Zero && !owned)
                GlobalFree(handle);
            CloseClipboard();
        }
    }

    private static string Holder()
    {
        var window = GetOpenClipboardWindow();
        if (window == IntPtr.Zero)
            window = GetClipboardOwner();
        if (window == IntPtr.Zero)
            return "окно не найдено";

        var title = new StringBuilder(256);
        GetWindowText(window, title, title.Capacity);
        GetWindowThreadProcessId(window, out var pid);
        var name = pid.ToString();
        try
        {
            name = Process.GetProcessById((int)pid).ProcessName;
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        return $"{name} pid={pid} «{title}»";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("user32.dll")]
    private static extern IntPtr GetOpenClipboardWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint filterMin, uint filterMax, uint remove);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr memory);
}
