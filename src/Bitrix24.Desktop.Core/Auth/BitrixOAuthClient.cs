using System.Globalization;
using System.Text.Json;
using Bitrix24.Desktop.Core;
using Bitrix24.Desktop.Core.Rest;

namespace Bitrix24.Desktop.Core.Auth;

public sealed class BitrixOAuthClient
{
    private static readonly TimeSpan ResponseWait = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http;

    public BitrixOAuthClient(HttpClient http) => _http = http;

    public static string AuthorizeUrl(string portalDomain, string clientId, string state)
    {
        var query = string.Join("&", new[]
        {
            "client_id=" + Uri.EscapeDataString(clientId),
            "state=" + Uri.EscapeDataString(state)
        });
        return $"https://{portalDomain}/oauth/authorize/?{query}";
    }

    public async Task<StoredSession> ExchangeCodeAsync(
        StoredSession session,
        string code,
        string? serverDomain,
        CancellationToken cancellationToken)
    {
        var endpoint = TokenEndpoints.From(serverDomain, session.TokenEndpoint);
        var draft = session.Clone();
        draft.TokenEndpoint = endpoint;
        var url = TokenEndpoints.Build(endpoint, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = draft.ClientId,
            ["client_secret"] = draft.ClientSecret,
            ["code"] = code
        });
        return await RequestAsync(draft, url, cancellationToken);
    }

    public async Task<StoredSession> RefreshAsync(StoredSession session, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.RefreshToken))
            throw new BitrixException("Сессия не найдена. Войдите снова.", "invalid_grant", isRateLimit: false, isAuthFailure: true, retryAfter: null);

        var endpoint = TokenEndpoints.From(null, session.TokenEndpoint);
        var url = TokenEndpoints.Build(endpoint, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = session.ClientId,
            ["client_secret"] = session.ClientSecret,
            ["refresh_token"] = session.RefreshToken
        });
        var draft = session.Clone();
        draft.TokenEndpoint = endpoint;
        return await RequestAsync(draft, url, cancellationToken);
    }

    private async Task<StoredSession> RequestAsync(StoredSession session, string url, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ResponseWait);
        var response = await AwaitAuth(_http.GetAsync(url, timeout.Token), cancellationToken);
        using (response)
        {
            var body = await AwaitAuth(response.Content.ReadAsStringAsync(timeout.Token), cancellationToken);
            return ReadSession(session, response, body);
        }
    }

    private static async Task<T> AwaitAuth<T>(Task<T> task, CancellationToken caller)
    {
        try
        {
            return await task;
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            throw Unanswered(timedOut: true);
        }
        catch (HttpRequestException)
        {
            throw Unanswered(timedOut: false);
        }
    }

    private static BitrixException Unanswered(bool timedOut)
    {
        var seconds = ((int)ResponseWait.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        FileLog.Write(timedOut
            ? "Сервер авторизации не ответил за " + seconds + " секунд."
            : "Сервер авторизации не ответил.");
        return new BitrixException("Сервер авторизации не ответил.", null, false, false, null);
    }

    private static StoredSession ReadSession(StoredSession session, HttpResponseMessage response, string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        }
        catch (JsonException)
        {
            throw Unreadable("Сервер авторизации вернул не JSON. ", response, body);
        }

        using (document)
        {
            var root = document.RootElement;
            var error = JsonRead.Str(root, "error");
            var access = JsonRead.Str(root, "access_token");
            if (string.IsNullOrEmpty(error) && string.IsNullOrEmpty(access))
                throw Unreadable("Сервер авторизации вернул JSON без токена. ", response, body);

            if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(access))
            {
                var description = JsonRead.Str(root, "error_description");
                throw new BitrixException(Human(error, description), error, false, IsAuth(error), null);
            }

            return WithToken(session, root, access);
        }
    }

    private static BitrixException Unreadable(string reason, HttpResponseMessage response, string? body)
    {
        FileLog.Write(reason + Describe(response, body));
        return new BitrixException("Сервер авторизации вернул непонятный ответ.", null, false, false, null);
    }

    private static StoredSession WithToken(StoredSession session, JsonElement root, string access)
    {
        var updated = session.Clone();
        updated.AccessToken = access;
        var refresh = JsonRead.Str(root, "refresh_token");
        if (!string.IsNullOrEmpty(refresh))
            updated.RefreshToken = refresh;

        var expiresIn = JsonRead.Int(root, "expires_in");
        if (expiresIn <= 0)
            expiresIn = 3600;
        updated.AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);

        var clientEndpoint = JsonRead.Str(root, "client_endpoint");
        if (!string.IsNullOrEmpty(clientEndpoint))
            updated.ClientEndpoint = clientEndpoint.EndsWith('/') ? clientEndpoint : clientEndpoint + "/";

        var serverEndpoint = JsonRead.Str(root, "server_endpoint");
        if (!string.IsNullOrEmpty(serverEndpoint))
            updated.TokenEndpoint = TokenEndpoints.FromServerEndpoint(serverEndpoint);

        var memberId = JsonRead.Str(root, "member_id");
        if (!string.IsNullOrEmpty(memberId))
            updated.MemberId = memberId;

        return updated;
    }

    private static string Describe(HttpResponseMessage response, string? body)
    {
        var kind = response.Content.Headers.ContentType?.MediaType;
        return "HTTP "
            + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)
            + (string.IsNullOrEmpty(kind) ? "" : ", " + kind)
            + ". "
            + Preview(body);
    }

    private static string Preview(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "Тело пустое.";

        var text = body.Trim();
        if (text.Contains("access_token", StringComparison.OrdinalIgnoreCase)
            || text.Contains("refresh_token", StringComparison.OrdinalIgnoreCase)
            || text.Contains("client_secret", StringComparison.OrdinalIgnoreCase))
            return "Длина " + text.Length.ToString(CultureInfo.InvariantCulture) + ".";

        var compact = new System.Text.StringBuilder();
        foreach (var ch in text)
        {
            if (compact.Length >= 160)
                break;
            compact.Append(char.IsControl(ch) ? ' ' : ch);
        }

        return "«" + compact + "»";
    }

    private static bool IsAuth(string error) =>
        error is "invalid_grant" or "invalid_client" or "invalid_token" or "expired_token";

    private static string Human(string error, string description)
    {
        return error switch
        {
            "invalid_client" => "Код приложения или секретный ключ не подошли.",
            "invalid_grant" => "Код входа устарел. Повторите вход.",
            "invalid_request" => "Сервер авторизации отклонил запрос.",
            _ when !string.IsNullOrWhiteSpace(description) => description,
            _ => "Не удалось войти в Битрикс24."
        };
    }
}

public static class TokenEndpoints
{
    public const string DefaultHost = "oauth.bitrix.info";

    public static string From(string? serverDomain, string? currentEndpoint)
    {
        if (!string.IsNullOrWhiteSpace(serverDomain))
            return Host(serverDomain);
        if (!string.IsNullOrWhiteSpace(currentEndpoint))
            return currentEndpoint;
        return Host(DefaultHost);
    }

    public static string FromServerEndpoint(string serverEndpoint)
    {
        if (Uri.TryCreate(serverEndpoint, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            return Host(uri.Host);
        return Host(DefaultHost);
    }

    public static string Build(string endpoint, IReadOnlyDictionary<string, string> query)
    {
        var parts = string.Join("&", query.Select(pair =>
            Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        var separator = endpoint.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return endpoint + separator + parts;
    }

    private static string Host(string domain)
    {
        var host = domain.Trim().TrimEnd('/');
        if (host.Contains("://", StringComparison.Ordinal))
        {
            if (Uri.TryCreate(host, UriKind.Absolute, out var uri))
                host = uri.Host;
        }

        return string.Format(CultureInfo.InvariantCulture, "https://{0}/oauth/token/", host);
    }
}
