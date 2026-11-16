using System.Net;
using System.Net.WebSockets;
using Bitrix24.Desktop.Core.Rest;

namespace Bitrix24.Desktop.Core.Polling;

public sealed class PullListener
{
    public static readonly TimeSpan Silence = TimeSpan.FromSeconds(25);

    private readonly BitrixApi _api;
    private int _loggedConnect;
    private int _restores;
    private bool _held;
    private bool _down;

    public PullListener(BitrixApi api) => _api = api;

    public event EventHandler? ChatsChanged;
    public event EventHandler<PullNotice>? Reaction;
    public event EventHandler<WritingSignal>? Writing;
    public event EventHandler? AuthenticationLost;
    public event EventHandler<string>? Unavailable;
    public event EventHandler? Restored;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(75) };
        var route = new ChannelRoute();
        string? mid = null;

        if (!await ScopeAllowsPullAsync(cancellationToken))
            return;

        while (!cancellationToken.IsCancellationRequested)
        {
            var loaded = await LoadConfigAsync(cancellationToken);
            if (loaded.Stop)
                break;
            if (loaded.Session is null)
                continue;

            var step = await FollowAsync(http, loaded.Session, mid, route, cancellationToken);
            mid = step.Mid;
            if (step.Stop)
                break;
        }
    }

    private async Task<ConfigLoad> LoadConfigAsync(CancellationToken cancellationToken)
    {
        PullSession? session;
        try
        {
            session = await _api.GetPullSessionAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ConfigLoad(true, null);
        }
        catch (BitrixException exception) when (exception.IsAuthFailure)
        {
            FileLog.Write(exception.Message);
            AuthenticationLost?.Invoke(this, EventArgs.Empty);
            return new ConfigLoad(true, null);
        }
        catch (BitrixException exception) when (IsPermanent(exception))
        {
            var hint = exception.IsAccessDenied
                ? "нет права pull"
                : exception.Message;
            FileLog.Write("Push&Pull: " + hint + ". Канал не поднимается.");
            SignalUnavailable("Мгновенные сообщения недоступны.");
            return new ConfigLoad(true, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            FileLog.Write("Push&Pull: не удалось получить конфигурацию (" + exception.GetType().Name + ").");
            return new ConfigLoad(await RetryLaterAsync("конфигурация не получена", cancellationToken), null);
        }

        if (session is not null)
            return new ConfigLoad(false, session);

        if (!_down)
            FileLog.Write("На портале не настроен Push&Pull. Новые сообщения приходят опросом.");
        return new ConfigLoad(await RetryLaterAsync("на портале нет Push&Pull", cancellationToken), null);
    }

    private async Task<ChannelStep> FollowAsync(
        HttpClient http,
        PullSession session,
        string? mid,
        ChannelRoute route,
        CancellationToken cancellationToken)
    {
        var longPoll = session.HasLongPoll && (!session.WebSocketEnabled || DateTimeOffset.UtcNow < route.LongPollUntil);
        try
        {
            var outcome = longPoll
                ? await ReadLongPollAsync(http, session, mid, cancellationToken)
                : await ReadSocketAsync(session, mid, cancellationToken);
            mid = outcome.Mid ?? mid;
            if (await SettleAsync(session, outcome, route, cancellationToken))
                return new ChannelStep(true, mid);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ChannelStep(true, mid);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            FileLog.Write("Push&Pull: соединение прервано (" + exception.GetType().Name + ").");
            if (await RetryLaterAsync("соединение прервано", cancellationToken))
                return new ChannelStep(true, mid);
        }

        return new ChannelStep(false, mid);
    }

    private async Task<bool> SettleAsync(
        PullSession session,
        PullOutcome outcome,
        ChannelRoute route,
        CancellationToken cancellationToken)
    {
        if (outcome.Kind is PullStop.ConfigExpired or PullStop.Reconnect)
        {
            if (_held)
            {
                _restores = 0;
                _held = false;
            }
        }
        else if (outcome.Kind == PullStop.Dropped
            || (outcome.Kind == PullStop.SocketBlocked && !session.HasLongPoll))
        {
            if (await RetryLaterAsync("соединение прервано", cancellationToken))
                return true;
        }

        if (outcome.Kind == PullStop.SocketBlocked)
        {
            route.Blocked++;
            if (route.Blocked >= 3 && session.HasLongPoll)
            {
                route.LongPollUntil = DateTimeOffset.UtcNow.AddMinutes(30);
                route.Blocked = 0;
                FileLog.Write("Push&Pull: WebSocket недоступен, перехожу на длинный опрос.");
            }
        }
        else if (outcome.Kind != PullStop.ConfigExpired)
        {
            route.Blocked = 0;
        }

        var pause = outcome.Kind switch
        {
            PullStop.ConfigExpired => TimeSpan.FromSeconds(3),
            PullStop.Dropped or PullStop.SocketBlocked => TimeSpan.FromMilliseconds(500),
            _ => TimeSpan.Zero
        };
        return pause > TimeSpan.Zero && !await PauseAsync(pause, cancellationToken);
    }

    private async Task<PullOutcome> ReadSocketAsync(PullSession session, string? mid, CancellationToken cancellationToken)
    {
        var drops = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (session.NeedsRefresh(DateTimeOffset.UtcNow))
                return new PullOutcome(PullStop.ConfigExpired, mid);

            var url = session.BuildUrl(longPolling: false, mid);
            if (url is null)
                return new PullOutcome(PullStop.ConfigExpired, mid);

            using var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            try
            {
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connect.CancelAfter(TimeSpan.FromSeconds(15));
                await socket.ConnectAsync(new Uri(url), connect.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new PullOutcome(PullStop.Dropped, mid);
            }
            catch (WebSocketException)
            {
                return new PullOutcome(PullStop.Dropped, mid);
            }

            MarkHeld("Push&Pull: WebSocket подключён.", traffic: true);

            var outcome = await ReceiveSocketAsync(socket, session, mid, cancellationToken);
            mid = outcome.Mid ?? mid;
            if (outcome.Kind is PullStop.ConfigExpired or PullStop.SocketBlocked)
                return outcome;
            if (outcome.Kind == PullStop.Reconnect)
            {
                drops = 0;
                continue;
            }

            drops++;
            if (drops >= 3)
                return new PullOutcome(PullStop.Dropped, mid);
            if (!await PauseAsync(TimeSpan.FromMilliseconds(500), cancellationToken))
                break;
        }

        return new PullOutcome(PullStop.Dropped, mid);
    }

    private async Task<PullOutcome> ReceiveSocketAsync(
        ClientWebSocket socket,
        PullSession session,
        string? mid,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var body = new MemoryStream();
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            body.SetLength(0);
            var frame = await ReadFrameAsync(socket, buffer, body, mid, cancellationToken);
            if (frame.Outcome is PullOutcome stopped)
                return stopped;

            var result = frame.Result;
            var text = result.MessageType == WebSocketMessageType.Binary
                ? PullFrame.ToJson(body.GetBuffer().AsSpan(0, (int)body.Length))
                : System.Text.Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length);
            var outcome = Apply(session, text, mid);
            mid = outcome.Mid ?? mid;
            if (outcome.Kind != PullStop.Continue)
                return outcome;
        }

        return new PullOutcome(PullStop.Dropped, mid);
    }

    private async Task<SocketFrame> ReadFrameAsync(
        ClientWebSocket socket,
        byte[] buffer,
        MemoryStream body,
        string? mid,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var silence = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            silence.CancelAfter(Silence);
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer, silence.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return SocketFrame.Stop(new PullOutcome(PullStop.Dropped, mid));
            }
            catch (WebSocketException exception) when (!cancellationToken.IsCancellationRequested)
            {
                var blocked = Blocked(socket) || exception.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely;
                return SocketFrame.Stop(Closed(blocked, mid));
            }

            if (result.MessageType == WebSocketMessageType.Close)
                return SocketFrame.Stop(Closed(Blocked(socket), mid));

            body.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return SocketFrame.Message(result);
            if (body.Length > 1_000_000)
                return SocketFrame.Stop(new PullOutcome(PullStop.Dropped, mid));
        }
    }

    private static PullOutcome Closed(bool blocked, string? mid) =>
        new(blocked ? PullStop.SocketBlocked : PullStop.Dropped, mid);

    private async Task<PullOutcome> ReadLongPollAsync(
        HttpClient http,
        PullSession session,
        string? mid,
        CancellationToken cancellationToken)
    {
        MarkHeld("Push&Pull: длинный опрос.", traffic: false);

        while (!cancellationToken.IsCancellationRequested)
        {
            if (session.NeedsRefresh(DateTimeOffset.UtcNow))
                return new PullOutcome(PullStop.ConfigExpired, mid);

            var url = session.BuildUrl(longPolling: true, mid);
            if (url is null)
                return new PullOutcome(PullStop.ConfigExpired, mid);

            HttpResponseMessage response;
            try
            {
                response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                continue;
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    NoticeTraffic();
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    return new PullOutcome(PullStop.Dropped, mid);

                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                var text = TextFrame(bytes) ? System.Text.Encoding.UTF8.GetString(bytes) : PullFrame.ToJson(bytes);
                var outcome = Apply(session, text, mid);
                mid = outcome.Mid ?? mid;
                if (outcome.Kind == PullStop.ConfigExpired)
                    return outcome;
            }
        }

        return new PullOutcome(PullStop.Dropped, mid);
    }

    private static bool TextFrame(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
            return true;
        var first = bytes[0];
        return first is (byte)'{' or (byte)'[' or (byte)'"' or (byte)'#' or (byte)'p' or (byte)'P';
    }

    private PullOutcome Apply(PullSession session, string text, string? mid)
    {
        NoticeTraffic();
        var reload = false;
        var replaced = false;
        foreach (var notice in PullSession.ReadNotices(text))
        {
            if (!string.IsNullOrEmpty(notice.Mid))
                mid = notice.Mid;
            if (notice.Kind == PullNoticeKind.Chat)
                ChatsChanged?.Invoke(this, EventArgs.Empty);
            else if (notice.Kind == PullNoticeKind.Reaction)
                Reaction?.Invoke(this, notice);
            else if (notice.Kind == PullNoticeKind.Writing)
                Writing?.Invoke(this, new WritingSignal(notice.DialogId ?? "", notice.UserId));
            else if (notice.Kind == PullNoticeKind.ReplaceChannel)
            {
                session.ReplaceChannel(notice.ChannelType, notice.ChannelId);
                replaced = true;
            }
            else if (notice.Kind == PullNoticeKind.ReloadConfig)
            {
                reload = true;
            }
        }

        if (reload)
            return new PullOutcome(PullStop.ConfigExpired, mid);
        if (replaced)
            return new PullOutcome(PullStop.Reconnect, mid);
        return new PullOutcome(PullStop.Continue, mid);
    }

    private static bool Blocked(ClientWebSocket socket)
    {
        var code = (int?)socket.CloseStatus;
        return code is 1006 or 1008;
    }

    private async Task<bool> ScopeAllowsPullAsync(CancellationToken cancellationToken)
    {
        try
        {
            var scopes = await _api.GetScopesAsync(cancellationToken);
            if (scopes.Any(scope => scope.Equals("pull", StringComparison.OrdinalIgnoreCase)))
                return true;

            FileLog.Write("Push&Pull: при открытии в правах нет pull.");
            Unavailable?.Invoke(this, "В правах нет pull.");
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (BitrixException exception) when (exception.IsAuthFailure)
        {
            FileLog.Write(exception.Message);
            AuthenticationLost?.Invoke(this, EventArgs.Empty);
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            FileLog.Write("Push&Pull: список прав не прочитан (" + exception.GetType().Name + "), пробую канал.");
            return true;
        }
    }

    private void MarkHeld(string connected, bool traffic)
    {
        _held = true;
        if (Interlocked.Exchange(ref _loggedConnect, 1) == 0)
            FileLog.Write(connected);
        if (traffic)
            NoticeTraffic();
    }

    private void NoticeTraffic()
    {
        if (!_down)
            return;

        _down = false;
        _restores = 0;
        FileLog.Write("Push&Pull: соединение восстановлено.");
        Restored?.Invoke(this, EventArgs.Empty);
    }

    private void SignalUnavailable(string message)
    {
        if (_down)
            return;

        _down = true;
        Unavailable?.Invoke(this, message);
    }

    private async Task<bool> RetryLaterAsync(string why, CancellationToken cancellationToken)
    {
        SignalUnavailable("Мгновенные сообщения переподключаются.");
        if (_held)
        {
            _restores = 0;
            _held = false;
            return false;
        }

        _restores++;
        FileLog.Write("Push&Pull: " + why + ". Попытка " + _restores.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
        return !await PauseAsync(PullReconnect.Delay(_restores), cancellationToken);
    }

    private static bool IsPermanent(BitrixException exception)
    {
        if (exception.IsAccessDenied)
            return true;

        var code = exception.ErrorCode ?? "";
        return code.Contains("METHOD_NOT_FOUND", StringComparison.OrdinalIgnoreCase)
            || code.Contains("UNKNOWN_METHOD", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> PauseAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private readonly record struct ConfigLoad(bool Stop, PullSession? Session);

    private readonly record struct ChannelStep(bool Stop, string? Mid);

    private sealed class ChannelRoute
    {
        public int Blocked;
        public DateTimeOffset LongPollUntil = DateTimeOffset.MinValue;
    }

    private readonly struct SocketFrame
    {
        private SocketFrame(WebSocketReceiveResult result, PullOutcome stop, bool stopped)
        {
            Result = result;
            _stop = stop;
            _stopped = stopped;
        }

        public WebSocketReceiveResult Result { get; }
        public PullOutcome? Outcome => _stopped ? _stop : null;

        public static SocketFrame Message(WebSocketReceiveResult result) => new(result, default, false);
        public static SocketFrame Stop(PullOutcome outcome) =>
            new(new WebSocketReceiveResult(0, WebSocketMessageType.Text, true), outcome, true);

        private readonly PullOutcome _stop;
        private readonly bool _stopped;
    }

    private readonly record struct PullOutcome(PullStop Kind, string? Mid);

    private enum PullStop
    {
        Continue,
        Reconnect,
        Dropped,
        SocketBlocked,
        ConfigExpired
    }
}
