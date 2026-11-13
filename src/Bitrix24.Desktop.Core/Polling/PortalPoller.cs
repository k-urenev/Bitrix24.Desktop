using Bitrix24.Desktop.Core;
using Bitrix24.Desktop.Core.Auth;
using Bitrix24.Desktop.Core.Rest;
using Bitrix24.Desktop.Core.Storage;

namespace Bitrix24.Desktop.Core.Polling;

public sealed class PortalPoller
{
    public const int MaxCards = 3;
    public static readonly TimeSpan ChatInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan TaskInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan TaskOverlap = TimeSpan.FromMinutes(10);

    private readonly BitrixApi _api;
    private readonly LocalDatabase _database;
    private readonly SessionHolder _sessions;
    private readonly Func<string?> _quietDialogId;
    private readonly Func<string> _windowState;
    private readonly PullListener _pull;
    private readonly object _schedule = new();
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _wait;
    private Task? _loop;
    private Task? _pullTask;
    private DateTimeOffset _nextChats = DateTimeOffset.MinValue;
    private DateTimeOffset _nextTasks = DateTimeOffset.MinValue;
    private bool _taskAccessDenied;
    private volatile bool _pullUnavailable;
    private bool _authenticationLost;
    private int _authNotified;

    public PortalPoller(
        BitrixApi api,
        LocalDatabase database,
        SessionHolder sessions,
        Func<string?> quietDialogId,
        Func<string>? windowState = null)
    {
        _api = api;
        _database = database;
        _sessions = sessions;
        _quietDialogId = quietDialogId;
        _windowState = windowState ?? (() => quietDialogId() is null ? "скрыто" : "на экране");
        _pull = new PullListener(api);
        _pull.ChatsChanged += (_, _) => RequestChatsNow();
        _pull.Reaction += (_, notice) => NoteReaction(notice);
        _pull.Writing += (_, signal) => Writing?.Invoke(this, signal);
        _pull.AuthenticationLost += (_, _) => NotifyAuthLost();
        _pull.Unavailable += (_, _) =>
        {
            _pullUnavailable = true;
            StatusChanged?.Invoke(this, LinkedStatus());
        };
    }

    public event EventHandler<IReadOnlyList<DialogSummary>>? DialogsRefreshed;
    public event EventHandler<IReadOnlyList<AppNotification>>? NotificationsReady;
    public event EventHandler<string>? OpenDialogNeedsRefresh;
    public event EventHandler<WritingSignal>? Writing;
    public event EventHandler? ReactionsChanged;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler? AuthenticationLost;

    public void Start()
    {
        if (_loop is { IsCompleted: false })
            return;

        _authenticationLost = false;
        _pullUnavailable = false;
        _authNotified = 0;
        lock (_schedule)
        {
            _nextChats = DateTimeOffset.MinValue;
            _nextTasks = DateTimeOffset.MinValue;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        _pullTask = Task.Run(() => _pull.RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        var loop = _loop;
        var pull = _pullTask;
        if (cts is null || loop is null)
            return;

        cts.Cancel();
        try
        {
            if (pull is null)
                await loop;
            else
                await Task.WhenAll(loop, pull);
        }
        catch (OperationCanceledException)
        {
        }

        cts.Dispose();
        _cts = null;
        _loop = null;
        _pullTask = null;
    }

    private void NoteReaction(PullNotice notice)
    {
        var dialogId = notice.DialogId ?? "";
        var chatId = notice.ChatId;
        if (dialogId.Length == 0 && chatId > 0)
            dialogId = _database.FindDialogId(chatId);
        if (chatId <= 0 && dialogId.Length > 0)
            chatId = _database.FindChatId(dialogId);

        var self = _sessions.Current.UserId;
        if (notice.ReactionActive && notice.UserId > 0 && notice.UserId == self)
            return;

        var open = _quietDialogId();
        var visible = open is not null && SameDialog(open, dialogId, chatId);
        if (!notice.ReactionActive || visible)
        {
            if (notice.WholeChat || notice.MessageId <= 0)
                _database.ForgetChatReactions(dialogId, chatId);
            else
                _database.ForgetReaction(dialogId, chatId, notice.MessageId);
            if (visible && chatId > 0)
                _ = ReadAnchorsAsync(chatId);
        }
        else
        {
            _database.RememberReaction(dialogId, chatId, notice.MessageId);
        }

        ReactionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ReadAnchorsAsync(int chatId)
    {
        try
        {
            await _api.ReadReactionAnchorsAsync(chatId, CancellationToken.None);
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
        }
    }

    private static bool SameDialog(string open, string dialogId, int chatId) =>
        (dialogId.Length > 0 && string.Equals(open, dialogId, StringComparison.Ordinal))
        || (chatId > 0 && string.Equals(open, "chat" + chatId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));

    private void RequestChatsNow()
    {
        lock (_schedule)
            _nextChats = DateTimeOffset.MinValue;

        var wait = _wait;
        try
        {
            wait?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void CompleteChatPoll(TimeSpan after, bool force = false)
    {
        lock (_schedule)
        {
            if (!force && _nextChats == DateTimeOffset.MinValue)
                return;
            _nextChats = DateTimeOffset.UtcNow.Add(after);
        }
    }

    private void NotifyAuthLost()
    {
        if (Interlocked.Exchange(ref _authNotified, 1) == 1)
            return;

        _authenticationLost = true;
        StatusChanged?.Invoke(this, "Нужно войти снова");
        AuthenticationLost?.Invoke(this, EventArgs.Empty);
        var wait = _wait;
        try
        {
            wait?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_authenticationLost)
                break;

            DateTimeOffset nextChats;
            DateTimeOffset nextTasks;
            lock (_schedule)
            {
                nextChats = _nextChats;
                nextTasks = _nextTasks;
            }

            var now = DateTimeOffset.UtcNow;
            if (now >= nextChats)
                await PollSafelyAsync(chats: true, cancellationToken);
            else if (now >= nextTasks)
                await PollSafelyAsync(chats: false, cancellationToken);

            if (_authenticationLost || cancellationToken.IsCancellationRequested)
                break;

            lock (_schedule)
            {
                nextChats = _nextChats;
                nextTasks = _nextTasks;
            }

            now = DateTimeOffset.UtcNow;
            if (now >= nextChats || now >= nextTasks)
                continue;

            var until = nextChats < nextTasks ? nextChats : nextTasks;
            var delay = until - now;
            if (delay > TimeSpan.FromSeconds(1))
                delay = TimeSpan.FromSeconds(1);

            var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _wait = wait;
            try
            {
                await Task.Delay(delay, wait.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                if (ReferenceEquals(_wait, wait))
                    _wait = null;
                wait.Dispose();
            }
        }
    }

    private string LinkedStatus()
    {
        if (_pullUnavailable && _taskAccessDenied)
            return "На связи. Нет мгновенных сообщений. Нет прав на задачи";
        if (_pullUnavailable)
            return "На связи. Нет мгновенных сообщений";
        if (_taskAccessDenied)
            return "На связи. Нет прав на задачи";
        return "На связи";
    }

    private async Task<TimeSpan> PollSafelyAsync(bool chats, CancellationToken cancellationToken)
    {
        try
        {
            if (chats)
            {
                lock (_schedule)
                    _nextChats = DateTimeOffset.MaxValue;
                await PollChatsAsync(cancellationToken);
                CompleteChatPoll(ChatInterval);
                StatusChanged?.Invoke(this, LinkedStatus());
            }
            else
            {
                await PollTasksAsync(cancellationToken);
                _taskAccessDenied = false;
                _nextTasks = DateTimeOffset.UtcNow.Add(TaskInterval);
            }

            return TimeSpan.FromSeconds(1);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (BitrixException exception) when (exception.IsAuthFailure)
        {
            FileLog.Write(exception);
            NotifyAuthLost();
            return TimeSpan.Zero;
        }
        catch (BitrixException exception) when (exception.IsRateLimit)
        {
            FileLog.Write(exception.Message);
            StatusChanged?.Invoke(this, "Портал ограничил запросы, ждём");
            var pause = exception.RetryAfter ?? TimeSpan.FromSeconds(30);
            var at = DateTimeOffset.UtcNow.Add(pause);
            lock (_schedule)
            {
                _nextChats = at;
                _nextTasks = at;
            }

            return pause;
        }
        catch (BitrixException exception) when (!chats && exception.IsAccessDenied)
        {
            FileLog.Write(exception.Message);
            _taskAccessDenied = true;
            _nextTasks = DateTimeOffset.UtcNow.Add(TimeSpan.FromMinutes(10));
            StatusChanged?.Invoke(this, LinkedStatus());
            return TimeSpan.FromSeconds(1);
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
            StatusChanged?.Invoke(this, "Нет связи с порталом");
            if (chats)
                CompleteChatPoll(ChatInterval);
            else
                _nextTasks = DateTimeOffset.UtcNow.Add(TaskInterval);
            return TimeSpan.FromSeconds(1);
        }
    }

    private async Task PollChatsAsync(CancellationToken cancellationToken)
    {
        var dialogs = await _api.GetRecentDialogsAsync(cancellationToken);
        _database.SaveDialogs(dialogs);
        var session = _sessions.Current;
        var cursors = _database.GetCursors();
        var baseline = _database.HasFlag("messages_baseline");
        var decision = MessageNoticePlanner.Plan(
            dialogs,
            cursors,
            session.UserId,
            _quietDialogId(),
            session.DoNotDisturb,
            baseline,
            MaxCards);

        if (baseline)
            NoteNewChats(dialogs, cursors, decision.DialogToRefresh);

        foreach (var (dialogId, messageId) in decision.AdvanceCursors)
            _database.SetLastNotified(dialogId, messageId);

        if (!_database.HasFlag("messages_baseline"))
            _database.SetFlag("messages_baseline", "1");

        DialogsRefreshed?.Invoke(this, dialogs);
        if (decision.DialogToRefresh is not null)
            OpenDialogNeedsRefresh?.Invoke(this, decision.DialogToRefresh);
        if (decision.Cards.Count > 0)
            NotificationsReady?.Invoke(this, decision.Cards);
    }

    private async Task PollTasksAsync(CancellationToken cancellationToken)
    {
        var session = _sessions.Current;
        if (session.UserId == 0)
            return;

        var baseline = _database.HasFlag("tasks_baseline");
        DateTimeOffset? since = null;
        var assignedAfter = DateTimeOffset.UtcNow.AddMinutes(-2);
        if (baseline && DateTimeOffset.TryParse(_database.GetFlag("tasks_polled_at"), out var polled))
        {
            since = polled.Subtract(TaskOverlap);
            assignedAfter = polled;
        }

        var tasks = await _api.GetMemberTasksAsync(session.UserId, since, cancellationToken);
        if (!baseline)
        {
            _database.ReplaceTaskSnapshots(tasks);
            _database.SetFlag("tasks_baseline", "1");
        }
        else
        {
            var decision = TaskNoticePlanner.Plan(
                _database.GetTaskSnapshots(),
                tasks,
                assignedAfter,
                session.DoNotDisturb,
                session.PortalDomain,
                session.UserId,
                MaxCards);
            _database.UpsertTaskSnapshots(decision.SnapshotsToStore);
            _database.AddTaskNotices(session.UserId, decision.Cards, DateTimeOffset.Now);
            if (decision.Cards.Count > 0)
                NotificationsReady?.Invoke(this, decision.Cards);
        }

        _database.SetFlag("tasks_polled_at", DateTimeOffset.UtcNow.ToString("o"));
    }

    private void NoteNewChats(
        IReadOnlyList<DialogSummary> dialogs,
        IReadOnlyDictionary<string, int> cursors,
        string? refresh)
    {
        var selected = _quietDialogId();
        var window = _windowState();
        var who = selected ?? "нет";
        foreach (var dialog in dialogs)
        {
            cursors.TryGetValue(dialog.DialogId, out var notified);
            if (dialog.LastMessageId <= notified)
                continue;

            FileLog.Write(
                FeedTrace.ChatLine(
                    dialog.DialogId,
                    dialog.Title,
                    dialog.LastMessageId,
                    _database.NewestMessageId(dialog.DialogId),
                    window,
                    who,
                    refresh == dialog.DialogId));
        }
    }
}
