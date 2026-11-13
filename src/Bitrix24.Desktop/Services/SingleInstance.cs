namespace Bitrix24.Desktop.Services;

public static class SingleInstance
{
    private const string MutexName = @"Local\Bitrix24.Desktop.SingleInstance";
    private const string ShowEventName = @"Local\Bitrix24.Desktop.Show";
    private const string ExitEventName = @"Local\Bitrix24.Desktop.Exit";

    public static bool IsExitRequest(IReadOnlyList<string> args)
    {
        foreach (var arg in args)
        {
            if (arg.Equals("--exit", StringComparison.OrdinalIgnoreCase)
                || arg.Equals("/exit", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static bool IsTrayRequest(IReadOnlyList<string> args)
    {
        foreach (var arg in args)
        {
            if (arg.Equals("--tray", StringComparison.OrdinalIgnoreCase)
                || arg.Equals("/tray", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static bool SignalExit() => Signal(ExitEventName);

    public static bool TryAcquire(out Mutex? mutex, Action onShow, Action onExit)
    {
        mutex = null;
        try
        {
            mutex = new Mutex(false, MutexName);
            try
            {
                if (!mutex.WaitOne(0))
                {
                    mutex.Dispose();
                    mutex = null;
                    Signal(ShowEventName);
                    return false;
                }
            }
            catch (AbandonedMutexException)
            {
            }
        }
        catch (Exception)
        {
            mutex?.Dispose();
            mutex = null;
            return true;
        }

        var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var exit = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        var handles = new WaitHandle[] { show, exit };
        var thread = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    if (WaitHandle.WaitAny(handles) == 1)
                    {
                        onExit();
                        return;
                    }

                    onShow();
                }
            }
            catch (ObjectDisposedException)
            {
            }
        })
        {
            IsBackground = true,
            Name = "Bitrix24.SingleInstance"
        };
        thread.Start();
        return true;
    }

    private static bool Signal(string name)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var handle = EventWaitHandle.OpenExisting(name);
                handle.Set();
                return true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        return false;
    }
}
