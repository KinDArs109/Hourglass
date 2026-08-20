using System.IO;
using System.Net.Http;
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
    private readonly Dictionary<string, SteamConfiguration> _proxied =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _gate = new();

    public SteamRuntime()
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppPaths.ProductName);

        Directory.CreateDirectory(_directory);

        Configuration = SteamConfiguration.Create(builder =>
            builder.WithServerListProvider(
                new FileStorageServerListProvider(Path.Combine(_directory, "steam-servers.bin"))));
    }

    /// <summary>Shared by every account that goes straight out.</summary>
    public SteamConfiguration Configuration { get; }

    /// <summary>What this account should connect with, proxy included when it has one.</summary>
    public SteamConfiguration ResolveFor(string username, Uri? proxy)
    {
        if (proxy is null)
            return Configuration;

        var key = $"{username}|{proxy}";

        lock (_gate)
        {
            if (_proxied.TryGetValue(key, out var existing))
                return existing;

            var created = BuildProxied(username, proxy);
            _proxied[key] = created;
            return created;
        }
    }

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
