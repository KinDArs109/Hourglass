using System.Net;
using System.Net.Http;

namespace Hourglass.Services;

public sealed record ProxyCheckResult(bool IsWorking, string Message);

/// <summary>
/// Answers the only question worth asking before saving a proxy: does Steam actually
/// answer through it? Asks Steam's own endpoint, so nothing is sent to a third party.
/// </summary>
public static class ProxyCheck
{
    private const string Probe = "https://api.steampowered.com/ISteamWebAPIUtil/GetServerInfo/v1/";

    public static async Task<ProxyCheckResult> RunAsync(Uri proxy, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler
        {
            Proxy = ProxyAddress.ToWebProxy(proxy),
            UseProxy = true
        };

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

        try
        {
            using var response = await client.GetAsync(Probe, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                return new ProxyCheckResult(false, "Прокси требует логин и пароль — впишите их в адрес: user:pass@host:port");

            if (!response.IsSuccessStatusCode)
                return new ProxyCheckResult(false, $"Прокси отвечает, но Steam через него вернул {(int)response.StatusCode}");

            return new ProxyCheckResult(true, "Прокси работает, Steam через него отвечает");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProxyCheckResult(false, "Прокси не ответил за 20 секунд");
        }
        catch (HttpRequestException ex)
        {
            return new ProxyCheckResult(false, $"Через прокси не подключиться: {ex.Message}");
        }
    }
}
