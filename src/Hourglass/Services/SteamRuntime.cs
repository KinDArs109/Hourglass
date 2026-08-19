using System.IO;
using SteamKit2;
using SteamKit2.Discovery;

namespace Hourglass.Services;

/// <summary>
/// One Steam configuration shared by every session.
///
/// A fresh SteamClient starts with no idea which connection managers exist and has to
/// discover them. Letting each account do that on its own means several discoveries at
/// once, and the first connection attempt of each tends to die waiting. Sharing the
/// configuration shares the resolved server list, and the file cache carries it across
/// restarts so later launches connect immediately.
/// </summary>
public sealed class SteamRuntime
{
    public SteamRuntime()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppPaths.ProductName);

        Directory.CreateDirectory(directory);

        Configuration = SteamConfiguration.Create(builder =>
            builder.WithServerListProvider(
                new FileStorageServerListProvider(Path.Combine(directory, "steam-servers.bin"))));
    }

    public SteamConfiguration Configuration { get; }
}
