using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bitrix24.Desktop.Core;
using Bitrix24.Desktop.Core.Auth;

namespace Bitrix24.Desktop.Core.Rest;

public sealed class BitrixRestClient
{
    private readonly HttpClient _http;
    private readonly SessionHolder _sessions;
    private readonly BitrixOAuthClient _oauth;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _blockedUntil;

    public BitrixRestClient(HttpClient http, SessionHolder sessions, BitrixOAuthClient oauth)
    {
        _http = http;
        _sessions = sessions;
        _oauth = oauth;
    }

    public async Task<JsonDocument> CallAsync(string method, JsonObject? body, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var refreshed = false;
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                await WaitUntilUnblocked(cancellationToken);
                await EnsureFreshTokenAsync(cancellationToken);

                using var response = await SendAsync(method, body, cancellationToken);
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
                }
                catch (JsonException)
                {
                    throw new BitrixException(
                        $"Портал вернул не JSON (HTTP {(int)response.StatusCode}).",
                        null,
                        false,
                        false,
                        null);
                }

                var error = JsonRead.Str(document.RootElement, "error");
                var description = JsonRead.Str(document.RootElement, "error_description");
                if (IsRateLimit(response.StatusCode, error))
                {
                    var resetAt = ReadReset(document.RootElement);
                    document.Dispose();
                    var wait = Backoff(attempt, resetAt);
                    _blockedUntil = DateTimeOffset.UtcNow.Add(wait);
                    if (attempt == 5)
                        throw new BitrixException("Портал ограничил частоту запросов. Подождите немного.", error, true, false, wait);
                    await Task.Delay(wait, cancellationToken);
                    continue;
                }

                if (IsExpired(error) && !refreshed)
                {
                    document.Dispose();
                    await RefreshAsync(cancellationToken);
                    refreshed = true;
                    continue;
                }

                if (!string.IsNullOrEmpty(error) || !response.IsSuccessStatusCode)
                {
                    document.Dispose();
                    var auth = IsExpired(error) || error is "invalid_grant" or "INVALID_CREDENTIALS";
                    throw new BitrixException(Human(error, description, (int)response.StatusCode), error, false, auth, null);
                }

                return document;
            }

            throw new BitrixException("Портал не ответил.", null, false, false, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<JsonDocument> CallReleasedAsync(string method, JsonObject? body, CancellationToken cancellationToken)
    {
        var refreshed = false;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            string url;
            await _gate.WaitAsync(cancellationToken);
            try
            {
                await WaitUntilUnblocked(cancellationToken);
                await EnsureFreshTokenAsync(cancellationToken);
                url = EndpointUrl(method);
            }
            finally
            {
                _gate.Release();
            }

            using var response = await PostAsync(url, body, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            }
            catch (JsonException)
            {
                throw new BitrixException(
                    $"Портал вернул не JSON (HTTP {(int)response.StatusCode}).",
                    null,
                    false,
                    false,
                    null);
            }

            var error = JsonRead.Str(document.RootElement, "error");
            var description = JsonRead.Str(document.RootElement, "error_description");
            if (IsRateLimit(response.StatusCode, error))
            {
                var resetAt = ReadReset(document.RootElement);
                document.Dispose();
                var wait = Backoff(attempt, resetAt);
                await RememberBlock(wait, cancellationToken);
                if (attempt == 5)
                    throw new BitrixException("Портал ограничил частоту запросов. Подождите немного.", error, true, false, wait);
                await Task.Delay(wait, cancellationToken);
                continue;
            }

            if (IsExpired(error) && !refreshed)
            {
                document.Dispose();
                await _gate.WaitAsync(cancellationToken);
                try
                {
                    await RefreshAsync(cancellationToken);
                }
                finally
                {
                    _gate.Release();
                }

                refreshed = true;
                continue;
            }

            if (!string.IsNullOrEmpty(error) || !response.IsSuccessStatusCode)
            {
                document.Dispose();
                var auth = IsExpired(error) || error is "invalid_grant" or "INVALID_CREDENTIALS";
                throw new BitrixException(Human(error, description, (int)response.StatusCode), error, false, auth, null);
            }

            return document;
        }

        throw new BitrixException("Портал не ответил.", null, false, false, null);
    }

    public async Task<byte[]> DownloadAuthorizedAsync(string url, string portalHost, int maxBytes, CancellationToken cancellationToken)
    {
        if (!PortalAddress.IsPortalHttps(portalHost, url))
            throw new BitrixException("Ссылка на файл ведёт не на портал.", null, false, false, null);

        string token;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await WaitUntilUnblocked(cancellationToken);
            await EnsureFreshTokenAsync(cancellationToken);
            token = _sessions.Current.AccessToken ?? "";
        }
        finally
        {
            _gate.Release();
        }

        if (token.Length == 0)
            throw new BitrixException("Нет активной сессии. Войдите снова.", "NO_AUTH_FOUND", false, true, null);

        using var response = await _http.GetAsync(WithAuth(url, token), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var announced = response.Content.Headers.ContentLength;
        if (announced is long length && length > maxBytes)
            throw new BitrixException("Файл слишком большой для превью.", null, false, false, null);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var bytes = await StreamLimit.ReadAsync(stream, maxBytes, cancellationToken);

        if (LooksLikeJson(response, bytes))
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(bytes);
            }
            catch (JsonException)
            {
                return bytes;
            }

            using (document)
            {
                var error = JsonRead.Str(document.RootElement, "error");
                var description = JsonRead.Str(document.RootElement, "error_description");
                if (!string.IsNullOrEmpty(error) || !response.IsSuccessStatusCode)
                {
                    var auth = IsExpired(error) || error is "invalid_grant" or "INVALID_CREDENTIALS";
                    throw new BitrixException(Human(error, description, (int)response.StatusCode), error, IsRateLimit(response.StatusCode, error), auth, null);
                }
            }

            return bytes;
        }

        if (!response.IsSuccessStatusCode)
            throw new BitrixException($"Портал ответил HTTP {(int)response.StatusCode}.", null, false, false, null);

        return bytes;
    }

    public async Task<byte[]> DownloadPublicAsync(string url, int maxBytes, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var announced = response.Content.Headers.ContentLength;
        if (announced is long length && length > maxBytes)
            throw new BitrixException("Файл слишком большой для превью.", null, false, false, null);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var bytes = await StreamLimit.ReadAsync(stream, maxBytes, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new BitrixException($"Портал ответил HTTP {(int)response.StatusCode}.", null, false, false, null);

        return bytes;
    }

    private static bool LooksLikeJson(HttpResponseMessage response, byte[] bytes)
    {
        var media = response.Content.Headers.ContentType?.MediaType ?? "";
        if (media.Contains("json", StringComparison.OrdinalIgnoreCase))
            return true;
        return bytes.Length > 0 && bytes[0] == (byte)'{';
    }

    private static string WithAuth(string url, string token)
    {
        if (HasAuth(url))
            return url;

        var separator = url.Contains('?') ? "&" : "?";
        return url + separator + "auth=" + Uri.EscapeDataString(token);
    }

    private static bool HasAuth(string url)
    {
        var query = url.IndexOf('?', StringComparison.Ordinal);
        if (query < 0)
            return false;

        foreach (var part in url[(query + 1)..].Split('&'))
        {
            var eq = part.IndexOf('=');
            var key = eq < 0 ? part : part[..eq];
            if (key.Equals("auth", StringComparison.OrdinalIgnoreCase) && eq >= 0 && eq < part.Length - 1)
                return true;
        }

        return false;
    }

    private async Task EnsureFreshTokenAsync(CancellationToken cancellationToken)
    {
        var session = _sessions.Current;
        if (!session.HasRefreshToken)
            throw new BitrixException("Нет активной сессии. Войдите снова.", "NO_AUTH_FOUND", false, true, null);

        if (!string.IsNullOrWhiteSpace(session.AccessToken)
            && session.AccessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
            return;

        await RefreshAsync(cancellationToken);
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var updated = await _oauth.RefreshAsync(_sessions.Current, cancellationToken);
        _sessions.Replace(updated);
    }

    private async Task<HttpResponseMessage> SendAsync(string method, JsonObject? body, CancellationToken cancellationToken) =>
        await PostAsync(EndpointUrl(method), body, cancellationToken);

    private string EndpointUrl(string method)
    {
        var session = _sessions.Current;
        if (string.IsNullOrWhiteSpace(session.AccessToken) || string.IsNullOrWhiteSpace(session.ClientEndpoint))
            throw new BitrixException("Нет активной сессии. Войдите снова.", "NO_AUTH_FOUND", false, true, null);

        var endpoint = session.ClientEndpoint.EndsWith('/') ? session.ClientEndpoint : session.ClientEndpoint + "/";
        return endpoint + method + "?auth=" + Uri.EscapeDataString(session.AccessToken);
    }

    private Task<HttpResponseMessage> PostAsync(string url, JsonObject? body, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body?.ToJsonString() ?? "{}", Encoding.UTF8, "application/json")
        };
        return _http.SendAsync(request, cancellationToken);
    }

    private async Task RememberBlock(TimeSpan wait, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _blockedUntil = DateTimeOffset.UtcNow.Add(wait);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WaitUntilUnblocked(CancellationToken cancellationToken)
    {
        var wait = _blockedUntil - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait, cancellationToken);
    }

    private static bool IsRateLimit(HttpStatusCode status, string error) =>
        status == HttpStatusCode.ServiceUnavailable
        || (int)status == 429
        || error is "QUERY_LIMIT_EXCEEDED" or "OPERATION_TIME_LIMIT" or "OVERLOAD_LIMIT";

    private static bool IsExpired(string error) =>
        error is "expired_token" or "invalid_token" or "NO_AUTH_FOUND";

    private static DateTimeOffset? ReadReset(JsonElement root)
    {
        var time = JsonRead.Prop(root, "time");
        var reset = JsonRead.Int(time, "operating_reset_at");
        if (reset <= 0)
            return null;
        return DateTimeOffset.FromUnixTimeSeconds(reset);
    }

    private static TimeSpan Backoff(int attempt, DateTimeOffset? resetAt)
    {
        if (resetAt is DateTimeOffset reset && reset > DateTimeOffset.UtcNow)
        {
            var until = reset - DateTimeOffset.UtcNow;
            if (until < TimeSpan.FromMinutes(2))
                return until + TimeSpan.FromSeconds(1);
        }

        var seconds = Math.Min(32, 2 << Math.Max(0, attempt - 1));
        return TimeSpan.FromSeconds(seconds);
    }

    private static string Human(string error, string description, int status)
    {
        if (error == "insufficient_scope")
            return "У приложения не хватает прав на этот запрос.";
        if (!string.IsNullOrWhiteSpace(description) && description.Length < 180)
            return description;
        if (!string.IsNullOrWhiteSpace(error))
            return "Битрикс24 отклонил запрос.";
        return $"Портал ответил HTTP {status}.";
    }
}
