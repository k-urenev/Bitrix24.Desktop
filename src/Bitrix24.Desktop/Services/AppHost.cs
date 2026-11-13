using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Bitrix24.Desktop.Core;
using Bitrix24.Desktop.Core.Auth;
using Bitrix24.Desktop.Core.Polling;
using Bitrix24.Desktop.Core.Rest;
using Bitrix24.Desktop.Core.Storage;
using Bitrix24.Desktop.ViewModels;

namespace Bitrix24.Desktop.Services;

public sealed class AppHost
{
    private readonly AppPaths _paths;
    private readonly Dispatcher _dispatcher;
    private readonly DpapiSecretStore _store;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(3) };
    private readonly SessionHolder _sessions;
    private readonly BitrixOAuthClient _oauth;
    private readonly BitrixRestClient _rest;
    private readonly BitrixApi _api;
    private readonly LocalDatabase _database;
    private readonly PortalPoller _poller;
    private readonly NotificationWindow _notifications = new();
    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Windows.Forms.ToolStripMenuItem? _doNotDisturbItem;
    private System.Drawing.Icon? _baseIcon;
    private System.Drawing.Icon? _badgedIcon;
    private int _badgeCount = -1;
    private MainWindow? _main;
    private LoginWindow? _login;
    private volatile string? _quietDialogId;
    private bool _awaitingLogin = true;
    private bool _exiting;
    private bool _signingOut;

    public AppHost(AppPaths paths, Dispatcher dispatcher)
    {
        _paths = paths;
        _dispatcher = dispatcher;
        _store = new DpapiSecretStore(paths.AuthFile);
        var session = _store.Load() ?? new StoredSession();
        _sessions = new SessionHolder(_store, session);
        _oauth = new BitrixOAuthClient(_http);
        _rest = new BitrixRestClient(_http, _sessions, _oauth);
        _api = new BitrixApi(_rest, _sessions);
        _database = new LocalDatabase(paths.DatabaseFile);
        _database.Initialize();
        Directory.CreateDirectory(paths.WebViewFolder);
        _poller = new PortalPoller(_api, _database, _sessions, () => _quietDialogId, WindowStateLabel);
        _poller.DialogsRefreshed += (_, dialogs) => _dispatcher.InvokeAsync(() => _main?.ViewModel.ApplyDialogs(dialogs));
        _poller.ReactionsChanged += (_, _) => _dispatcher.InvokeAsync(() => _main?.ViewModel.ApplyReactionMarks());
        _poller.NotificationsReady += (_, notifications) => _dispatcher.InvokeAsync(() =>
        {
            var active = MainWindowIsActive();
            var selectedId = _main?.ViewModel.SelectedDialog?.DialogId;
            _main?.ViewModel.AddTaskNotices(notifications);
            foreach (var notification in notifications)
            {
                var openChat = notification.Kind == NotificationKind.Message
                    && notification.DialogId is not null
                    && notification.DialogId == selectedId;
                var cue = notification.Kind == NotificationKind.Task
                    ? NoticeCue.Reminder
                    : active && openChat ? NoticeCue.OpenChat : NoticeCue.Incoming;
                NoticeSounds.Play(cue);
                if (!active)
                    _notifications.Push(notification);
            }
        });
        _poller.Writing += (_, signal) => _dispatcher.InvokeAsync(() => _main?.ViewModel.NoteWriting(signal));
        _poller.OpenDialogNeedsRefresh += (_, dialogId) => _dispatcher.InvokeAsync(async () =>
        {
            if (_main is null)
                return;
            try
            {
                await _main.ViewModel.RefreshOpenAsync(dialogId);
            }
            catch (Exception exception)
            {
                FileLog.Write("лента " + dialogId + " догрузка не удалась");
                FileLog.Write(exception);
            }
        });
        _poller.StatusChanged += (_, status) => _dispatcher.InvokeAsync(() =>
        {
            if (_main is not null)
                _main.ViewModel.Status = status;
        });
        _poller.AuthenticationLost += (_, _) => _dispatcher.InvokeAsync(HandleAuthenticationLostAsync);
        _notifications.CardChosen += (_, card) =>
        {
            if (card.Kind == NotificationKind.Message && !string.IsNullOrWhiteSpace(card.DialogId))
                FocusDialog(card.DialogId);
            else if (!string.IsNullOrWhiteSpace(card.ExternalUrl))
                OpenLink(card.ExternalUrl);
        };
    }

    public async Task StartAsync(bool openWindow)
    {
        var session = _sessions.Current;
        var showWindow = openWindow && !session.StartInTray;
        AdoptTrayAutostart();
        CreateTray();
        if (!session.HasRefreshToken)
        {
            await ShowLoginAsync(null);
            return;
        }

        try
        {
            await RightsRenewal.TryAsync(_sessions, _oauth, CancellationToken.None);
            session = _sessions.Current;
            if (string.IsNullOrWhiteSpace(session.AccessToken) || session.AccessTokenExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2))
            {
                var refreshed = await _oauth.RefreshAsync(session, CancellationToken.None);
                _sessions.Replace(refreshed);
            }

            if (_sessions.Current.UserId == 0)
                await LoadProfileAsync(CancellationToken.None);

            if (showWindow)
                ShowMainWindow();
            else
                EnsureMainWindow();
            _poller.Start();
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
            _sessions.ClearTokens();
            await ShowLoginAsync("Сессия истекла. Войдите снова.");
        }
    }

    private void AdoptTrayAutostart()
    {
        try
        {
            if (!WindowsStartup.CommandHidesWindow())
                return;

            if (!_sessions.Current.StartInTray)
                _sessions.SetStartInTray(true);
            WindowsStartup.SetEnabled(true);
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
        }
    }

    private void ShowFromTray()
    {
        if (_dispatcher.CheckAccess())
            ShowMain();
        else
            _dispatcher.Invoke(ShowMain, DispatcherPriority.Send);
    }

    public void ShowMain()
    {
        if (_main is not null)
        {
            ShowMainWindow();
            return;
        }

        if (_login is not null)
        {
            _login.Show();
            _login.WindowState = WindowState.Normal;
            _login.Activate();
        }
    }

    public void DisposeTray()
    {
        if (_tray is null)
            return;
        _tray.Visible = false;
        _tray.Icon = null;
        _tray.Dispose();
        _tray = null;
        _badgedIcon?.Dispose();
        _badgedIcon = null;
        _baseIcon?.Dispose();
        _baseIcon = null;
    }

    private async Task ShowLoginAsync(string? error)
    {
        _awaitingLogin = true;
        var login = new LoginWindow(_sessions, error, CompleteLoginAsync);
        _login = login;
        login.Closed += (_, _) =>
        {
            if (_awaitingLogin && !_exiting)
                System.Windows.Application.Current.Shutdown();
        };
        login.Show();
        await Task.CompletedTask;
    }

    private async Task CompleteLoginAsync(
        string portal,
        string clientId,
        string clientSecret,
        string code,
        string? serverDomain,
        CancellationToken cancellationToken)
    {
        var draft = _sessions.Current.Clone();
        draft.PortalDomain = portal;
        draft.ClientId = clientId;
        draft.ClientSecret = clientSecret;
        var session = await _oauth.ExchangeCodeAsync(draft, code, serverDomain, cancellationToken);
        _sessions.Replace(session);
        await LoadProfileAsync(cancellationToken);
        _awaitingLogin = false;
        ShowMainWindow();
        _main?.ViewModel.LoadTaskNotices();
        _poller.Start();
        _login?.Close();
        _login = null;
    }

    private async Task LoadProfileAsync(CancellationToken cancellationToken)
    {
        var profile = await _api.GetCurrentUserAsync(cancellationToken);
        var session = _sessions.Current;
        session.UserId = profile.Id;
        session.UserName = profile.Name;
        _sessions.Replace(session);
    }

    private void EnsureMainWindow()
    {
        if (_main is not null)
        {
            _main.ViewModel.UserName = _sessions.Current.UserName;
            return;
        }

        var viewModel = new MainViewModel(_api, _database, _sessions, _paths);
        viewModel.UserName = _sessions.Current.UserName;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MainViewModel.SelectedDialog) or null)
                RememberQuietDialog();
            if (args.PropertyName is nameof(MainViewModel.UnreadTotal) or null)
                UpdateTrayBadge();
        };
        viewModel.ApplyDialogs(_database.GetDialogs());
        viewModel.LoadTaskNotices();
        _main = new MainWindow(viewModel);
        _main.SignOutRequested += (_, _) => _ = SignOutAsync();
        _main.StateChanged += (_, _) => RememberQuietDialog();
        _main.IsVisibleChanged += (_, _) => RememberQuietDialog();
        _main.Closing += OnMainClosing;
        UpdateTrayBadge();
    }

    private void ShowMainWindow()
    {
        EnsureMainWindow();
        WindowForeground.Bring(_main!);
        RememberQuietDialog();
    }

    private void OnMainClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting)
            return;
        e.Cancel = true;
        _main?.Hide();
        RememberQuietDialog();
    }

    private bool MainWindowIsActive() =>
        _main is { IsVisible: true, IsActive: true }
        && _main.WindowState is WindowState.Normal or WindowState.Maximized;

    private void RememberQuietDialog()
    {
        _windowLabel = FeedTrace.Window(
            _main is { IsVisible: true },
            _main?.WindowState == WindowState.Minimized);

        if (_main is null || !_main.IsVisible || _main.WindowState == WindowState.Minimized)
        {
            _quietDialogId = null;
            NoteWindow();
            return;
        }

        _quietDialogId = _main.ViewModel.SelectedDialog?.DialogId;
        if (_quietDialogId is not null)
            _notifications.DismissDialog(_quietDialogId);
        NoteWindow();
    }

    private string _windowLog = "";
    private string _windowLabel = "скрыто";

    private string WindowStateLabel() => _windowLabel;

    private void NoteWindow()
    {
        if (_main is null)
            return;

        var selected = _main.ViewModel.SelectedDialog;
        var line = FeedTrace.WindowLine(
            WindowStateLabel(),
            FeedTrace.Selected(selected?.DialogId, selected?.Title));
        if (line == _windowLog)
            return;

        _windowLog = line;
        FileLog.Write(line);
        if (selected is null || !_main.IsVisible || _main.WindowState == WindowState.Minimized)
            return;

        var feed = _main.ViewModel.NewestShownId();
        if (selected.LastMessageId <= feed)
            return;
        FileLog.Write(FeedTrace.GapLine(selected.DialogId, selected.LastMessageId, feed, "после появления окна"));
    }

    private void FocusDialog(string dialogId)
    {
        ShowMainWindow();
        if (_main is null)
            return;

        _main.ViewModel.SelectDialog(dialogId);
        _main.RevealDialog(dialogId);
    }

    private static void OpenLink(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async Task SignOutAsync()
    {
        if (_signingOut || _exiting)
            return;

        _signingOut = true;
        try
        {
            try
            {
                await _poller.StopAsync();
            }
            catch (Exception exception)
            {
                FileLog.Write(exception);
            }

            _sessions.ClearTokens();
            _notifications.DismissAll();
            _main?.ViewModel.ClearForSignOut();
            _main?.Hide();
            RememberQuietDialog();
            await ShowLoginAsync(null);
        }
        finally
        {
            _signingOut = false;
        }
    }

    private async Task HandleAuthenticationLostAsync()
    {
        await _poller.StopAsync();
        _sessions.ClearTokens();
        _main?.Hide();
        RememberQuietDialog();
        await ShowLoginAsync("Сессия истекла. Войдите снова.");
    }

    private void CreateTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        var open = new System.Windows.Forms.ToolStripMenuItem("Открыть");
        open.Click += (_, _) => ShowFromTray();
        _doNotDisturbItem = new System.Windows.Forms.ToolStripMenuItem("Не беспокоить")
        {
            CheckOnClick = true,
            Checked = _sessions.Current.DoNotDisturb
        };
        _doNotDisturbItem.CheckedChanged += (_, _) =>
        {
            if (_doNotDisturbItem is null)
                return;
            _sessions.SetDoNotDisturb(_doNotDisturbItem.Checked);
            UpdateTooltip();
        };
        var exit = new System.Windows.Forms.ToolStripMenuItem("Выход");
        exit.Click += (_, _) => _dispatcher.InvokeAsync(ExitAsync);
        menu.Items.Add(open);
        menu.Items.Add(_doNotDisturbItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(exit);
        var trayButton = System.Windows.Forms.MouseButtons.None;
        menu.Opening += (_, args) =>
        {
            if (trayButton != System.Windows.Forms.MouseButtons.Right)
                args.Cancel = true;
        };

        _baseIcon = LoadIcon();
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = _baseIcon,
            Visible = true,
            Text = "Битрикс24",
            ContextMenuStrip = menu
        };
        _tray.MouseDown += (_, args) => trayButton = args.Button;
        _tray.DoubleClick += (_, _) => ShowFromTray();
        UpdateTooltip();
    }

    private void UpdateTooltip()
    {
        if (_tray is null)
            return;
        var text = _sessions.Current.DoNotDisturb ? "Битрикс24 — не беспокоить" : "Битрикс24";
        var unread = _main?.ViewModel.UnreadTotal ?? 0;
        if (unread > 0)
            text += unread > 99 ? " — 99+ непрочитанных" : " — " + unread + " непрочитанных";
        _tray.Text = text.Length <= 63 ? text : text[..63];
    }

    private void UpdateTrayBadge()
    {
        if (_tray is null || _baseIcon is null)
            return;

        var count = Math.Max(0, _main?.ViewModel.UnreadTotal ?? 0);
        if (count == _badgeCount)
            return;

        _badgeCount = count;
        UpdateTooltip();
        if (count == 0)
        {
            _tray.Icon = _baseIcon;
            return;
        }

        var next = DrawBadge(_baseIcon, count);
        var previous = _badgedIcon;
        _badgedIcon = next;
        _tray.Icon = next;
        previous?.Dispose();
    }

    private static System.Drawing.Icon DrawBadge(System.Drawing.Icon source, int count)
    {
        var label = count > 99 ? "99+" : count.ToString();
        using var bitmap = new System.Drawing.Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.Clear(System.Drawing.Color.Transparent);
            graphics.DrawIcon(source, new System.Drawing.Rectangle(0, 0, 32, 32));

            using var font = new System.Drawing.Font("Segoe UI", label.Length > 2 ? 8f : 9f, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
            var text = graphics.MeasureString(label, font);
            var height = 16f;
            var width = label.Length == 1 ? height : Math.Max(height, text.Width + 5f);
            var x = 32f - width;
            var y = 32f - height;
            using var ring = new System.Drawing.SolidBrush(System.Drawing.Color.White);
            using var fill = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 245, 180, 0));
            using var ink = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 26, 26, 26));
            FillBadge(graphics, ring, x - 1f, y - 1f, width + 2f, height + 2f);
            FillBadge(graphics, fill, x, y, width, height);
            var format = new System.Drawing.StringFormat
            {
                Alignment = System.Drawing.StringAlignment.Center,
                LineAlignment = System.Drawing.StringAlignment.Center
            };
            graphics.DrawString(label, font, ink, new System.Drawing.RectangleF(x, y - 0.5f, width, height), format);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var icon = System.Drawing.Icon.FromHandle(handle);
            return (System.Drawing.Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static void FillBadge(System.Drawing.Graphics graphics, System.Drawing.Brush brush, float x, float y, float width, float height)
    {
        if (Math.Abs(width - height) < 0.5f)
        {
            graphics.FillEllipse(brush, x, y, width, height);
            return;
        }

        var radius = height / 2f;
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(x, y, height, height, 90, 180);
        path.AddArc(x + width - height, y, height, height, 270, 180);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }

    public void RequestExit() => _ = ExitAsync();

    private async Task ExitAsync()
    {
        if (_exiting)
            return;

        _exiting = true;
        _awaitingLogin = false;
        DisposeTray();
        try
        {
            await _poller.StopAsync();
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
        }

        _notifications.Close();
        _main?.Close();
        _login?.Close();
        System.Windows.Application.Current.Shutdown();
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
            {
                var associated = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath);
                if (associated is not null)
                    return associated;
            }
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
        }

        using var bitmap = new System.Drawing.Bitmap(32, 32);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(System.Drawing.Color.FromArgb(47, 128, 237));
        var handle = bitmap.GetHicon();
        try
        {
            using var icon = System.Drawing.Icon.FromHandle(handle);
            return (System.Drawing.Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
