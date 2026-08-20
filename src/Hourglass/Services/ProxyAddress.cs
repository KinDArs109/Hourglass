using System.Net;

namespace Hourglass.Services;

/// <summary>
/// Parses what a person types into the proxy box. People paste
/// <c>host:port</c>, <c>user:pass@host:port</c> and full URLs in about equal measure,
/// so all of them are accepted and normalised to a URI .NET can route through.
/// </summary>
public static class ProxyAddress
{
    private static readonly string[] Schemes = { "http", "https", "socks4", "socks4a", "socks5" };

    /// <summary>Returns false with a reason the user can act on.</summary>
    public static bool TryParse(string? text, out Uri? proxy, out string error)
    {
        proxy = null;
        error = "";

        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0)
            return true;

        // No scheme means an ordinary HTTP proxy, which is what most sellers hand out.
        if (!trimmed.Contains("://", StringComparison.Ordinal))
            trimmed = "http://" + trimmed;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed))
        {
            error = "Не разобрать адрес. Ожидается host:port или socks5://host:port";
            return false;
        }

        if (!Schemes.Contains(parsed.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            error = $"Такой тип прокси не поддерживается: {parsed.Scheme}. " +
                    "Можно http, https, socks4, socks4a, socks5";
            return false;
        }

        if (parsed.IsDefaultPort && parsed.Authority.IndexOf(':') < 0)
        {
            error = "Не указан порт";
            return false;
        }

        proxy = parsed;
        return true;
    }

    /// <summary>Builds the proxy .NET will use, carrying over any login from the address.</summary>
    public static WebProxy ToWebProxy(Uri proxy)
    {
        var result = new WebProxy(proxy) { BypassProxyOnLocal = false };

        // WebProxy ignores the user:pass part of the URI, so it is lifted out by hand.
        if (string.IsNullOrEmpty(proxy.UserInfo))
            return result;

        var parts = Uri.UnescapeDataString(proxy.UserInfo).Split(':', 2);
        result.Credentials = new NetworkCredential(parts[0], parts.Length > 1 ? parts[1] : "");
        return result;
    }

    /// <summary>Address without the password, for logs and the window title.</summary>
    public static string Describe(Uri proxy) =>
        string.IsNullOrEmpty(proxy.UserInfo)
            ? $"{proxy.Scheme}://{proxy.Authority}"
            : $"{proxy.Scheme}://{proxy.UserInfo.Split(':')[0]}@{proxy.Host}:{proxy.Port}";
}
