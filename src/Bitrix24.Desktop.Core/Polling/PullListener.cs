using System.Net;
using System.Net.WebSockets;
using Bitrix24.Desktop.Core.Rest;

namespace Bitrix24.Desktop.Core.Polling;

public sealed class PullListener
{
    public const int RestoreLimit = 3;
    public static readonly TimeSpan Silence = TimeSpan.FromSeconds(25);

    private readonly BitrixApi _api;
    private int _loggedConnect;
    private int _restores;
    private bool _held;

    public PullListener(BitrixApi api) => _api = api;

    public event EventHandler? ChatsChanged;
    public event EventHandler<PullNotice>? Reaction;
    public event EventHandler<WritingSignal>? Writing;
    public event EventHandler? AuthenticationLost;
    public event EventHandler<string>? Unavailable;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(75) };
        var blocked = 0;
        var longPollUntil = DateTimeOffset.MinValue;
        string? mid = null;

        if (!await ScopeAllowsPullAsync(cancellationToken))
            return;

        while (!cancellationToken.IsCancellationRequested)
        {
            PullSession? session;
            try
            {
                session = await _api.GetPullSessionAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (BitrixException exception) when (exception.IsAuthFailure)
            {
                FileLog.Write(exception.Message);
                AuthenticationLost?.Invoke(this, EventArgs.Empty);
                break;
            }
            catch (BitrixException exception) when (IsPermanent(exception))
            {
                var hint = exception.IsAccessDenied
                    ? "нет права pull"
                    : exception.Message;
                if (await GiveUpAsync(hint, cancellationToken))
                    break;
                continue;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                FileLog.Write("Push&Pull: не удалось получить конфигурацию (" + exception.GetType().Name + ").");
                if (await GiveUpAsync("конфигурация не получена", cancellationToken))
                    break;
                continue;
            }

            if (session is null)
            {
                FileLog.Write("На портале не настроен Push&Pull. Новые сообщения приходят опросом.");
                if (await GiveUpAsync("на портале нет Push&Pull", cancellationToken))
                    break;
                continue;
            }

            var longPoll = session.HasLongPoll && (!session.WebSocketEnabled || DateTimeOffset.UtcNow < longPollUntil);
            try
            {
                var outcome = longPoll
                    ? await ReadLongPollAsync(http, session, mid, cancellationToken)
                    : await ReadSocketAsync(session, mid, cancellationToken);
                mid = outcome.Mid ?? mid;
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
                    if (await GiveUpAsync("соединение прервано", cancellationToken))
                        break;
                }

                if (outcome.Kind == PullStop.SocketBlocked)
                {
                    blocked++;
                    if (blocked >= 3 && session.HasLongPoll)
                    {
                        longPollUntil = DateTimeOffset.UtcNow.AddMinutes(30);
                        blocked = 0;
                        FileLog.Write("Push&Pull: WebSocket недоступен, перехожу на длинный опрос.");
                    }
                }
                else if (outcome.Kind != PullStop.ConfigExpired)
                {
                    blocked = 0;
                }

                var pause = outcome.Kind switch
                {
                    PullStop.ConfigExpired => TimeSpan.FromSeconds(3),
                    PullStop.Dropped or PullStop.SocketBlocked => TimeSpan.FromMilliseconds(500),
                    _ => TimeSpan.Zero
                };
                if (pause > TimeSpan.Zero && !await PauseAsync(pause, cancellationToken))
                    break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                FileLog.Write("Push&Pull: соединение прервано (" + exception.GetType().Name + ").");
                if (await GiveUpAsync("соединение прервано", cancellationToken))
                    break;
            }
        }
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

            _held = true;
            if (Interlocked.Exchange(ref _loggedConnect, 1) == 0)
                FileLog.Write("Push&Pull: WebSocket подключён.");

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
            WebSocketReceiveResult result;
            while (true)
            {
                using var silence = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                silence.CancelAfter(Silence);
                try
                {
                    result = await socket.ReceiveAsync(buffer, silence.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return new PullOutcome(PullStop.Dropped, mid);
                }
                catch (WebSocketException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    var blocked = Blocked(socket) || exception.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely;
                    return new PullOutcome(blocked ? PullStop.SocketBlocked : PullStop.Dropped, mid);
                }

                if (result.MessageType == WebSocketMessageType.Close)
                    return new PullOutcome(Blocked(socket) ? PullStop.SocketBlocked : PullStop.Dropped, mid);

                body.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                    break;
                if (body.Length > 1_000_000)
                    return new PullOutcome(PullStop.Dropped, mid);
            }

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

    private async Task<PullOutcome> ReadLongPollAsync(
        HttpClient http,
        PullSession session,
        string? mid,
        CancellationToken cancellationToken)
    {
        _held = true;
        if (Interlocked.Exchange(ref _loggedConnect, 1) == 0)
            FileLog.Write("Push&Pull: длинный опрос.");

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
                    continue;
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

    private async Task<bool> GiveUpAsync(string why, CancellationToken cancellationToken)
    {
        if (_held)
        {
            _restores = 0;
            _held = false;
            return false;
        }

        _restores++;
        if (_restores >= RestoreLimit)
        {
            FileLog.Write("Push&Pull: " + why + ". Три попытки не удались, право pull снято до следующего открытия.");
            Unavailable?.Invoke(this, "Мгновенные сообщения недоступны.");
            return true;
        }

        FileLog.Write("Push&Pull: " + why + ". Попытка " + _restores.ToString(System.Globalization.CultureInfo.InvariantCulture) + " из 3.");
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
