using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Hourglass.Services.Interfaces;
using SteamKit2.Discovery;

namespace Hourglass.Services;

/// <summary>
/// Serves only the connection managers that listen on 443.
///
/// Steam spreads its WebSocket servers across a range of ports — of twenty on offer,
/// barely a handful sit on 443 — and a tunnel or a company network that passes ordinary
/// web traffic and nothing else can reach only those. SteamKit's own discovery hands out
/// whatever the directory returns, so the list is fetched here and cut down to the ones
/// that will actually go through.
/// </summary>
public sealed class HttpsServerList : IServerListProvider
{
    private const string Directory =
        "https://api.steampowered.com/ISteamDirectory/GetCMListForConnect/v1/" +
        "?cellid=0&cmtype=websockets&maxcount=64";

    private const int HttpsPort = 443;

    private readonly IAppLogger _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private IReadOnlyList<ServerRecord> _cached = Array.Empty<ServerRecord>();

    public HttpsServerList(IAppLogger logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Never stale by SteamKit's reckoning: the list is fetched once per run and the
    /// three or four servers on 443 do not move around.
    /// </summary>
    public DateTime LastServerListRefresh => DateTime.UtcNow;

    public async Task<IEnumerable<ServerRecord>> FetchServerListAsync()
    {
        if (_cached.Count > 0)
            return _cached;

        try
        {
            using var client = _httpClientFactory.CreateClient(HttpClients.SteamApi);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            var payload = await client
                .GetFromJsonAsync<DirectoryResponse>(Directory, timeout.Token)
                .ConfigureAwait(false);

            var servers = payload?.Response?.ServerList ?? new List<DirectoryServer>();

            var onHttps = servers
                .Select(server => server.Endpoint ?? "")
                .Where(endpoint => endpoint.EndsWith($":{HttpsPort}", StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(ServerRecord.CreateWebSocketServer)
                .ToList();

            if (onHttps.Count == 0)
            {
                _logger.Warn(AppLogScopes.App,
                    "Steam не предложил ни одного сервера на 443 — подключаемся как обычно");
                return Array.Empty<ServerRecord>();
            }

            _logger.Info(AppLogScopes.App, $"Серверов Steam на 443: {onHttps.Count}");
            _cached = onHttps;
            return _cached;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NotSupportedException)
        {
            _logger.Warn(AppLogScopes.App, $"Список серверов Steam получить не удалось: {ex.Message}");
            return Array.Empty<ServerRecord>();
        }
    }

    /// <summary>
    /// SteamKit offers its own findings back for storage. They are the wide list this
    /// class exists to narrow, so they are dropped on the floor.
    /// </summary>
    public Task UpdateServerListAsync(IEnumerable<ServerRecord> endpoints) => Task.CompletedTask;

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
