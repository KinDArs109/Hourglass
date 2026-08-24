using System.IO;
using System.Net.Http;
using Hourglass.Services.Interfaces;
using SteamKit2;
using SteamKit2.Discovery;

namespace Hourglass.Services;

/// <summary>
/// Hands out the Steam configuration a session should connect with.
///
/// A fresh SteamClient starts with no idea which connection managers exist and has to
/// discover them. Letting each account do that on its own means several discoveries at
/// once, and the first connection attempt of each tends to die waiting. Accounts that
/// go out the same way therefore share one configuration, and the file cache carries
/// the resolved list across restarts so later launches connect immediately.
///
/// An account with its own proxy cannot share any of that: it reaches Steam by another
/// route and may well be handed different servers, so it gets its own configuration and
/// its own cache file.
/// </summary>
public sealed class SteamRuntime
{
    private readonly string _directory;
    private readonly IServerListProvider _sharedServers;
    private readonly Dictionary<string, SteamConfiguration> _variants = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly IAppLogger _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public SteamRuntime(IAppLogger logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;

        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppPaths.ProductName);

        Directory.CreateDirectory(_directory);

        _sharedServers = new FileStorageServerListProvider(Path.Combine(_directory, "steam-servers.bin"));
        Configuration = SteamConfiguration.Create(builder => builder.WithServerListProvider(_sharedServers));
    }

    /// <summary>Shared by every account that goes straight out on Steam's own ports.</summary>
    public SteamConfiguration Configuration { get; }

    /// <summary>
    /// What this account should connect with. Same object for accounts that share the
    /// route, so they also share the discovered server list.
    /// </summary>
    /// <param name="webSocketOnly">
    /// Talk to Steam over 443 instead of its own ports (27015–27050). Tunnels and
    /// company networks routinely pass the first and drop the rest.
    /// </param>
    public SteamConfiguration ResolveFor(string username, Uri? proxy, bool webSocketOnly)
    {
        if (proxy is null && !webSocketOnly)
            return Configuration;

        var key = proxy is null ? "ws" : $"{username}|{proxy}";

        lock (_gate)
        {
            if (_variants.TryGetValue(key, out var existing))
                return existing;

            var created = proxy is null ? BuildWebSocketOnly() : BuildProxied(username, proxy);
            _variants[key] = created;
            return created;
        }
    }

    private SteamConfiguration BuildWebSocketOnly() =>
        SteamConfiguration.Create(builder => builder
            .WithServerListProvider(new HttpsServerList(
                _logger,
                _httpClientFactory,
                Path.Combine(_directory, "steam-servers-443.txt"),
                _sharedServers))

            // Discovery is off on purpose: it would merge back the servers on unusual
            // ports, which is exactly what this mode exists to avoid.
            .WithDirectoryFetch(false)
            .WithProtocolTypes(ProtocolTypes.WebSocket));

    private SteamConfiguration BuildProxied(string username, Uri proxy)
    {
        var servers = new FileStorageServerListProvider(
            Path.Combine(_directory, $"steam-servers-{Sanitize(username)}.bin"));

        return SteamConfiguration.Create(builder => builder
            .WithServerListProvider(servers)

            // The plain TCP transport opens its own socket and would ignore the proxy
            // entirely, so the WebSocket one — the only transport that runs on the
            // HttpClient below — is the only one left available.
            .WithProtocolTypes(ProtocolTypes.WebSocket)
            .WithHttpClientFactory(purpose => new HttpClient(
                new HttpClientHandler
                {
                    Proxy = ProxyAddress.ToWebProxy(proxy),
                    UseProxy = true
                })
            {
                // The CM connection is a socket that stays open for hours. A request
                // timeout would cut it, so only the short-lived calls get one.
                Timeout = purpose == HttpClientPurpose.CMWebSocket
                    ? Timeout.InfiniteTimeSpan
                    : TimeSpan.FromSeconds(30)
            }));
    }

    private static string Sanitize(string username) =>
        string.Concat(username.Select(letter =>
            char.IsLetterOrDigit(letter) || letter is '-' or '_' ? letter : '_'));
}
