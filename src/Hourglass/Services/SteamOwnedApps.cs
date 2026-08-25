using Hourglass.Models;
using SteamKit2;

namespace Hourglass.Services;

/// <summary>
/// What an account owns outright, and how many licences it merely borrows from a family.
/// </summary>
public sealed record OwnedApps(IReadOnlyList<OwnedGame> Games, int SharedLicenses);

/// <summary>
/// Works out everything an account can actually play, by walking its licences.
///
/// Steam's own list of games leaves out free titles that were never launched — an
/// account can hold a hundred and seventy licences and be told it owns one game. The
/// licences are the truth: each grants a package, each package names its apps, and the
/// app entries say which of them are games rather than tools, demos or soundtracks.
/// </summary>
public static class SteamOwnedApps
{
    /// <summary>Enough for any real account, and a stop against a runaway response.</summary>
    private const int MaxApps = 4000;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    /// <summary>Apps asked about at once. Steam answers a short list far more reliably.</summary>
    private const int BatchSize = 100;

    /// <summary>Stands in for an answer Steam declined to give.</summary>
    private static readonly IReadOnlyList<SteamApps.PICSProductInfoCallback> Empty =
        Array.Empty<SteamApps.PICSProductInfoCallback>();

    public static async Task<OwnedApps> FetchAsync(
        SteamClient client,
        IReadOnlyCollection<SteamApps.LicenseListCallback.License> licenses,
        ulong steamId,
        CancellationToken cancellationToken)
    {
        if (licenses.Count == 0)
            return new OwnedApps(Array.Empty<OwnedGame>(), 0);

        var apps = client.GetHandler<SteamApps>()
                   ?? throw new InvalidOperationException("SteamApps handler is unavailable.");

        // Licences held by somebody else are family shares. Idling a borrowed game takes
        // it away from the person who owns it — they get thrown out of their own game —
        // so those are left alone.
        var accountId = new SteamID(steamId).AccountID;
        var own = licenses.Where(license => license.OwnerAccountID == accountId).ToList();

        var shared = licenses.Count - own.Count;

        if (own.Count == 0)
            return new OwnedApps(Array.Empty<OwnedGame>(), shared);

        var packageRequests = own
            .GroupBy(license => license.PackageID)
            .Select(group => new SteamApps.PICSRequest(group.Key, group.First().AccessToken))
            .ToList();

        var appIds = await ReadPackageAppsAsync(apps, packageRequests, cancellationToken)
            .ConfigureAwait(false);

        if (appIds.Count == 0)
            return new OwnedApps(Array.Empty<OwnedGame>(), shared);

        var games = await ReadAppNamesAsync(apps, appIds, cancellationToken).ConfigureAwait(false);
        return new OwnedApps(games, shared);
    }

    private static async Task<IReadOnlyList<uint>> ReadPackageAppsAsync(
        SteamApps apps,
        IReadOnlyList<SteamApps.PICSRequest> packages,
        CancellationToken cancellationToken)
    {
        var found = new HashSet<uint>();

        var responses = await apps
            .PICSGetProductInfo(Array.Empty<SteamApps.PICSRequest>(), packages)
            .ToTask()
            .WaitAsync(Patience, cancellationToken)
            .ConfigureAwait(false);

        foreach (var response in responses.Results ?? Empty)
        {
            foreach (var package in response.Packages.Values)
            {
                foreach (var entry in package.KeyValues["appids"].Children)
                {
                    if (entry.Value is not null && uint.TryParse(entry.Value, out var appId) && appId != 0)
                        found.Add(appId);
                }
            }
        }

        return found.Take(MaxApps).ToList();
    }

    private static async Task<IReadOnlyList<OwnedGame>> ReadAppNamesAsync(
        SteamApps apps,
        IReadOnlyList<uint> appIds,
        CancellationToken cancellationToken)
    {
        var games = new List<OwnedGame>();

        // Asked for in batches: a single request for every app on a large account can
        // take longer than any sensible wait, and one slow answer would then cost the
        // whole list. A batch that fails costs only itself.
        foreach (var batch in appIds.Chunk(BatchSize))
        {
            try
            {
                Collect(await apps
                    .PICSGetProductInfo(
                        batch.Select(appId => new SteamApps.PICSRequest(appId)).ToList(),
                        Array.Empty<SteamApps.PICSRequest>())
                    .ToTask()
                    .WaitAsync(Patience, cancellationToken)
                    .ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is TimeoutException or AsyncJobFailedException)
            {
                // Keep what the other batches found.
            }
        }

        return games;

        void Collect(AsyncJobMultiple<SteamApps.PICSProductInfoCallback>.ResultSet responses)
        {
            foreach (var response in responses.Results ?? Empty)
                CollectFrom(response, games);
        }
    }

    private static void CollectFrom(
        SteamApps.PICSProductInfoCallback response, List<OwnedGame> games)
    {
        foreach (var app in response.Apps.Values)
        {
            var common = app.KeyValues["common"];

            // Packages carry demos, soundtracks, tools and DLC alongside the game
            // itself. Only games earn playtime, and only games have cards.
            if (!string.Equals(common["type"].Value, "game", StringComparison.OrdinalIgnoreCase))
                continue;

            var name = common["name"].Value;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            // Steam marks trading-card support as category 29. It is the only way to
            // know a never-launched game is worth farming: with no playtime there is no
            // badge page to read it from.
            var hasCards = common["category"]["category_29"].Value is not null;

            games.Add(new OwnedGame(app.ID, name, 0, hasCards));
        }
    }
}
