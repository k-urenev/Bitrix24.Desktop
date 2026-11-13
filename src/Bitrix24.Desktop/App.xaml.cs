using System.Windows;
using System.Windows.Threading;
using Bitrix24.Desktop.Core;
using Bitrix24.Desktop.Services;
using Bitrix24.Desktop.ViewModels;

namespace Bitrix24.Desktop;

public partial class App : Application
{
    private Mutex? _mutex;
    private AppHost? _host;
    private bool _exitRequested;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (SingleInstance.IsExitRequest(e.Args))
        {
            SingleInstance.SignalExit();
            Shutdown();
            return;
        }

        var openWindow = !SingleInstance.IsTrayRequest(e.Args);
        var paths = AppPaths.Create();
        FileLog.Initialize(paths.LogFile);
        FileLog.Write("запуск " + AppRelease.Version);
        WindowsStartup.RepairIfTargetMissing();
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                FileLog.Write(exception);
        };

        if (!SingleInstance.TryAcquire(
                out _mutex,
                () => Dispatcher.BeginInvoke(() => _host?.ShowMain()),
                () => Dispatcher.BeginInvoke(RequestRemoteExit)))
        {
            Shutdown();
            return;
        }

        try
        {
            _host = new AppHost(paths, Dispatcher);
            if (_exitRequested)
            {
                _host.RequestExit();
                return;
            }

            await _host.StartAsync(openWindow);
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
            MessageBox.Show(exception.Message, "Битрикс24");
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.DisposeTray();
        if (_mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }

            _mutex.Dispose();
        }

        base.OnExit(e);
    }

    private void RequestRemoteExit()
    {
        if (_host is null)
        {
            _exitRequested = true;
            return;
        }

        _host.RequestExit();
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        FileLog.Write(e.Exception);
        if (e.Exception is OutOfMemoryException or AccessViolationException)
        {
            e.Handled = false;
            return;
        }

        e.Handled = true;
        foreach (Window window in Windows)
        {
            if (window.DataContext is MainViewModel viewModel)
            {
                viewModel.Error = "В интерфейсе произошла ошибка. Подробности записаны в журнал.";
                break;
            }
        }
    }
}
