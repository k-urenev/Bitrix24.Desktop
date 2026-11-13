using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Bitrix24.Desktop.Core;

public sealed record LinkPreview(string Url, string Title, string Description, string ImagePath);

public static partial class LinkPreviewClient
{
    private const int MaxHtmlBytes = 512 * 1024;
    private const int MaxImageBytes = 4 * 1024 * 1024;
    private const int MaxRedirects = 5;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    });

    public static async Task<LinkPreview?> FetchAsync(string url, string folder, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var start))
            return null;

        var page = await ReadPageAsync(start, cancellationToken);
        if (page is null)
            return null;

        var card = OpenGraph.Read(page.Value.Html, page.Value.Address);
        if (card is null)
            return null;

        var imagePath = "";
        if (card.ImageUrl is not null)
            imagePath = await SaveImageAsync(card.ImageUrl, folder, cancellationToken) ?? "";

        return new LinkPreview(url, card.Title, card.Description, imagePath);
    }

    private static async Task<(string Html, Uri Address)?> ReadPageAsync(Uri start, CancellationToken cancellationToken)
    {
        var address = start;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            var loaded = await LoadAsync(address, "text/html,application/xhtml+xml", MaxHtmlBytes, cancellationToken);
            if (loaded is null)
                return null;

            if (loaded.Value.Redirect is Uri next)
            {
                if (hop == MaxRedirects)
                    return null;
                address = next;
                continue;
            }

            if (loaded.Value.Body is null || loaded.Value.Body.Length == 0)
                return null;

            var html = EncodingOf(loaded.Value.Charset, loaded.Value.Body).GetString(loaded.Value.Body);
            return (html, address);
        }

        return null;
    }

    private static async Task<string?> SaveImageAsync(string url, string folder, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address))
            return null;

        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            var loaded = await LoadAsync(address, "image/*,*/*", MaxImageBytes, cancellationToken);
            if (loaded is null)
                return null;

            if (loaded.Value.Redirect is Uri next)
            {
                if (hop == MaxRedirects)
                    return null;
                address = next;
                continue;
            }

            if (loaded.Value.TextBody || loaded.Value.Body is null || loaded.Value.Body.Length == 0)
                return null;

            var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + ".img";
            PreviewCache.Store(folder, name, loaded.Value.Body, 200L * 1024 * 1024);
            return Path.Combine(folder, name);
        }

        return null;
    }

    private readonly record struct Loaded(Uri? Redirect, byte[]? Body, string? Charset, bool TextBody);

    private static async Task<Loaded?> LoadAsync(Uri address, string accept, int maxBytes, CancellationToken cancellationToken)
    {
        if (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps)
            return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        request.Headers.TryAddWithoutValidation("Accept", accept);
        try
        {
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (IsRedirect(response.StatusCode))
                return new Loaded(Next(address, response), null, null, false);
            if (response.StatusCode != HttpStatusCode.OK)
                return null;

            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            var bytes = await ReadLimitedAsync(response.Content, maxBytes, timeout.Token);
            return new Loaded(
                null,
                bytes,
                response.Content.Headers.ContentType?.CharSet,
                type.StartsWith("text/", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
                throw;
            return null;
        }
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static Uri? Next(Uri current, HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Location", out var values))
            return null;
        var location = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(location))
            return null;
        if (!Uri.TryCreate(current, location.Trim(), out var next))
            return null;
        if (next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps)
            return null;
        return next;
    }

    private static async Task<byte[]?> ReadLimitedAsync(HttpContent content, int max, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[8192];
        using var memory = new MemoryStream();
        while (memory.Length < max)
        {
            var take = (int)Math.Min(buffer.Length, max - memory.Length);
            var read = await stream.ReadAsync(buffer.AsMemory(0, take), cancellationToken);
            if (read == 0)
                break;
            memory.Write(buffer, 0, read);
        }

        return memory.ToArray();
    }

    private static Encoding EncodingOf(string? charset, byte[] bytes)
    {
        if (string.IsNullOrWhiteSpace(charset))
        {
            var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 2048));
            var found = CharsetMeta().Match(head);
            if (found.Success)
                charset = found.Groups["charset"].Value;
        }

        if (string.IsNullOrWhiteSpace(charset))
            return Encoding.UTF8;

        try
        {
            return Encoding.GetEncoding(charset.Trim().Trim('"').Trim('\''));
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    [GeneratedRegex(@"charset\s*=\s*[""']?(?<charset>[\w-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CharsetMeta();
}
