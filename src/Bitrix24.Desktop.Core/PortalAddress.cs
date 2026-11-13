namespace Bitrix24.Desktop.Core;

public static class PortalAddress
{
    public static bool TryNormalize(string input, out string host, out string error)
    {
        host = "";
        error = "";
        var value = input.Trim();
        if (value.Length == 0)
        {
            error = "Укажите адрес портала.";
            return false;
        }

        if (!value.Contains("://", StringComparison.Ordinal))
            value = "https://" + value;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !uri.Host.Contains('.', StringComparison.Ordinal))
        {
            error = "Адрес портала выглядит неполным. Пример: company.bitrix24.ru.";
            return false;
        }

        host = uri.Host;
        return true;
    }

    public static string? Absolute(string portalDomain, string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url == "/bitrix/js/im/images/blank.gif")
            return null;

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return url;

        if (url.StartsWith('/'))
            return "https://" + portalDomain + url;

        return null;
    }

    public static bool IsPortalHttps(string portalDomain, string? url)
    {
        if (string.IsNullOrWhiteSpace(portalDomain) || string.IsNullOrWhiteSpace(url))
            return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        return uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && uri.IdnHost.Equals(portalDomain, StringComparison.OrdinalIgnoreCase);
    }
}
