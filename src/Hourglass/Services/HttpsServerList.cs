using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Hourglass.Services.Interfaces;
using SteamKit2.Discovery;

namespace Hourglass.Services;

/// <summary>
/// Serves the connection managers that listen on 443.
///
/// Steam spreads its WebSocket servers across a range of ports — of the three dozen on
/// offer, only a handful sit on 443 — and a tunnel or a company network that passes
/// ordinary web traffic and nothing else can reach only those. SteamKit's own discovery
/// hands out whatever the directory returns, so the list is fetched here and cut down.
///
/// The list is also kept on disk. Learning it needs a working connection, and the whole
/// point of this mode is that connections are the problem: one timed-out request must
/// not leave the app with nowhere to connect.
/// </summary>
public sealed class HttpsServerList : IServerListProvider
{
    private const string Directory =
        "https://api.steampowered.com/ISteamDirectory/GetCMListForConnect/v1/" +
        "?cellid=0&cmtype=websockets&maxcount=64";

    private const int HttpsPort = 443;

    private readonly IAppLogger _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _cacheFile;
    private readonly IServerListProvider _fallback;
    private IReadOnlyList<string> _known = Array.Empty<string>();

    public HttpsServerList(
        IAppLogger logger,
        IHttpClientFactory httpClientFactory,
        string cacheFile,
        IServerListProvider fallback)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _cacheFile = cacheFile;
        _fallback = fallback;
    }

    /// <summary>
    /// Never stale by SteamKit's reckoning: the handful of servers on 443 do not move
    /// around, and refreshing is this class's own business.
    /// </summary>
    public DateTime LastServerListRefresh => DateTime.UtcNow;

    public async Task<IEnumerable<ServerRecord>> FetchServerListAsync()
    {
        if (_known.Count > 0)
            return Records(_known);

        // A fresh answer is best, the remembered one will do, and an empty result is
        // never remembered — that is what left the app spinning with nowhere to go.
        var fetched = await FetchFromSteamAsync().ConfigureAwait(false);
        if (fetched.Count > 0)
        {
            _known = fetched;
            Remember(fetched);
            return Records(_known);
        }

        var remembered = Recall();
        if (remembered.Count > 0)
        {
            _logger.Info(AppLogScopes.App,
                $"Серверы Steam на 443 взяты из прошлого запуска: {remembered.Count}");

            _known = remembered;
            return Records(_known);
        }

        // Last resort: whatever the ordinary mode learned earlier. Wrong port beats no
        // connection, and the journal names the port it settled on.
        var ordinary = (await _fallback.FetchServerListAsync().ConfigureAwait(false)).ToList();
        if (ordinary.Count > 0)
        {
            _logger.Warn(AppLogScopes.App,
                "Серверы на 443 узнать не удалось — подключаемся по обычному списку");

            return ordinary;
        }

        _logger.Warn(AppLogScopes.App,
            "Не удалось узнать серверы Steam ни на 443, ни из прошлых запусков. " +
            "Если так и останется — снимите галочку «Steam через 443» в настройках");

        return Array.Empty<ServerRecord>();
    }

    /// <summary>
    /// SteamKit offers its own findings back for storage. They are the wide list this
    /// class exists to narrow, so they are dropped on the floor.
    /// </summary>
    public Task UpdateServerListAsync(IEnumerable<ServerRecord> endpoints) => Task.CompletedTask;

    private static IEnumerable<ServerRecord> Records(IEnumerable<string> endpoints) =>
        endpoints.Select(ServerRecord.CreateWebSocketServer);

    private async Task<IReadOnlyList<string>> FetchFromSteamAsync()
    {
        try
        {
            using var client = _httpClientFactory.CreateClient(HttpClients.SteamApi);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            var payload = await client
                .GetFromJsonAsync<DirectoryResponse>(Directory, timeout.Token)
                .ConfigureAwait(false);

            var all = (payload?.Response?.ServerList ?? new List<DirectoryServer>())
                .Select(server => server.Endpoint ?? "")
                .Where(endpoint => endpoint.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var onHttps = all
                .Where(endpoint => endpoint.EndsWith($":{HttpsPort}", StringComparison.Ordinal))
                .ToList();

            if (onHttps.Count > 0)
            {
                _logger.Info(AppLogScopes.App, $"Серверов Steam на 443: {onHttps.Count}");
                return onHttps;
            }

            // Steam offered nothing on 443 this time. Connected on an odd port beats
            // not connected, and the journal says which it was.
            if (all.Count > 0)
                _logger.Warn(AppLogScopes.App,
                    "Steam не предложил серверов на 443 — берём остальные, порт будет виден в журнале");

            return all;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NotSupportedException)
        {
            _logger.Warn(AppLogScopes.App, $"Список серверов Steam получить не удалось: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    private void Remember(IReadOnlyList<string> endpoints)
    {
        try
        {
            File.WriteAllLines(_cacheFile, endpoints);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn(AppLogScopes.App, $"Список серверов не сохранился: {ex.Message}");
        }
    }

    private IReadOnlyList<string> Recall()
    {
        try
        {
            return File.Exists(_cacheFile)
                ? File.ReadAllLines(_cacheFile).Where(line => line.Trim().Length > 0).ToList()
                : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn(AppLogScopes.App, $"Сохранённый список серверов не прочитался: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    private sealed class DirectoryResponse
    {
        [JsonPropertyName("response")]
        public DirectoryPayload? Response { get; set; }
    }

    private sealed class DirectoryPayload
    {
        [JsonPropertyName("serverlist")]
        public List<DirectoryServer>? ServerList { get; set; }
    }

    private sealed class DirectoryServer
    {
        [JsonPropertyName("endpoint")]
        public string? Endpoint { get; set; }
    }
}
