using Hourglass.Models;
using SteamKit2;
using SteamKit2.Internal;

namespace Hourglass.Services;

/// <summary>
/// Reads an account's game library over an already signed-in Steam connection.
///
/// The Web API route (GenerateAccessTokenForApp + IPlayerService) is deliberately
/// not used: Steam answers AccessDenied when the token is minted outside a signed-in
/// session, and opening an extra session just to list games risks invalidating the
/// stored refresh token.
/// </summary>
public static class SteamLibrary
{
    public static async Task<IReadOnlyList<OwnedGame>> FetchAsync(
        SteamClient client, ulong steamId, CancellationToken cancellationToken)
    {
        if (steamId == 0)
            return Array.Empty<OwnedGame>();

        var unified = client.GetHandler<SteamUnifiedMessages>()
                      ?? throw new InvalidOperationException("SteamUnifiedMessages handler is unavailable.");

        var player = unified.CreateService<Player>();
        var response = await player
            .GetOwnedGames(new CPlayer_GetOwnedGames_Request
            {
                steamid = steamId,
                include_appinfo = true,
                include_played_free_games = true,
                include_free_sub = true
            })
            .ToTask()
            .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);

        if (response.Result != EResult.OK)
            throw new SteamLibraryException($"Steam ответил {response.Result}.");

        return response.Body.games
            .Where(game => game.appid != 0)
            .Select(game => new OwnedGame(
                (uint)game.appid,
                string.IsNullOrWhiteSpace(game.name) ? $"AppID {game.appid}" : game.name,
                game.playtime_forever))
            .OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}

public sealed class SteamLibraryException : Exception
{
    public SteamLibraryException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
