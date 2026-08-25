using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using Hourglass.Models;

namespace Hourglass.Services;

/// <summary>One game's card-drop state, as the Steam badge page reports it.</summary>
/// <summary>
/// What Steam owes for one game. <c>DropsRemaining</c> below zero means nobody knows
/// yet: the game has cards but has never been started, so it has no badge to read.
/// </summary>
public sealed record CardBadge(uint AppId, string Name, int DropsRemaining, double HoursPlayed)
{
    /// <summary>True when the count is unknown because the game was never played.</summary>
    public bool IsUnstarted => DropsRemaining < 0;
}

/// <summary>
/// Reads the account's badge pages to find out where card drops are still waiting.
///
/// Steam exposes this nowhere but the community website, so the pages are fetched with
/// the account's own access token as a cookie and parsed. Pages are requested in
/// English on purpose: the "N card drops remaining" wording is what we match on, and it
/// would otherwise change with the account's language.
/// </summary>
public static partial class SteamBadges
{
    private const int MaxPages = 25;

    public static async Task<IReadOnlyList<CardBadge>> FetchAsync(
        IHttpClientFactory httpClientFactory,
        ulong steamId,
        string accessToken,
        IReadOnlyDictionary<uint, OwnedGame> known,
        CancellationToken cancellationToken)
    {
        var badges = new List<CardBadge>();
        var seen = new HashSet<uint>();

        var firstPage = await LoadPageAsync(httpClientFactory, steamId, accessToken, 1, cancellationToken)
            .ConfigureAwait(false);

        var pageCount = Math.Min(DetectPageCount(firstPage), MaxPages);
        Collect(firstPage, known, badges, seen);

        for (var page = 2; page <= pageCount; page++)
        {
            var html = await LoadPageAsync(httpClientFactory, steamId, accessToken, page, cancellationToken)
                .ConfigureAwait(false);
            Collect(html, known, badges, seen);
        }

        // An account with no badges at all is possible, but a badge page that yields
        // nothing usually means Steam changed the markup. Saying "no cards left" in
        // that case would quietly stop the farm, so treat it as a failure instead.
        if (badges.Count == 0 && !LooksEmpty(firstPage))
            throw new SteamBadgeException("Не удалось разобрать страницу значков — возможно, Steam изменил вёрстку.");

        return badges;
    }

    /// <summary>True when Steam itself says there is nothing on the page.</summary>
    private static bool LooksEmpty(string html) =>
        html.Contains("badges_sheet", StringComparison.Ordinal) &&
        !html.Contains("badge_row", StringComparison.Ordinal);

    private static async Task<string> LoadPageAsync(
        IHttpClientFactory httpClientFactory,
        ulong steamId,
        string accessToken,
        int page,
        CancellationToken cancellationToken)
    {
        var url = $"https://steamcommunity.com/profiles/{steamId}/badges/?l=english&p={page}";

        using var client = httpClientFactory.CreateClient(HttpClients.SteamApi);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        // Modern Steam accepts "steamid||accesstoken" as the web session cookie.
        request.Headers.Add("Cookie",
            $"steamLoginSecure={steamId}%7C%7C{Uri.EscapeDataString(accessToken)}");

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new SteamBadgeException("Steam не принял веб-сессию. Попробуйте позже.");

        if (!response.IsSuccessStatusCode)
            throw new SteamBadgeException($"Steam ответил {(int)response.StatusCode} на страницу значков.");

        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // An unauthenticated request lands on the sign-in page instead of the badges.
        if (html.Contains("g_steamID = false", StringComparison.Ordinal))
            throw new SteamBadgeException("Steam не признал вход на сайте — карточки посчитать не вышло.");

        return html;
    }

    private static void Collect(
        string html,
        IReadOnlyDictionary<uint, OwnedGame> known,
        List<CardBadge> badges,
        HashSet<uint> seen)
    {
        foreach (var block in html.Split("class=\"badge_row ", StringSplitOptions.None).Skip(1))
        {
            var appIdMatch = AppIdPattern().Match(block);
            if (!appIdMatch.Success || !uint.TryParse(appIdMatch.Groups[1].Value, out var appId))
                continue;

            if (!seen.Add(appId))
                continue;

            var dropsMatch = DropsPattern().Match(block);
            var drops = dropsMatch.Success ? int.Parse(dropsMatch.Groups[1].Value) : 0;

            var hours = 0d;
            var hoursMatch = PlaytimePattern().Match(block);
            if (hoursMatch.Success)
            {
                var raw = hoursMatch.Groups[1].Value.Replace(",", "", StringComparison.Ordinal);
                double.TryParse(raw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out hours);
            }

            known.TryGetValue(appId, out var owned);

            var name = owned is not null && owned.Name.Length > 0
                ? owned.Name
                : ExtractName(block) ?? $"AppID {appId}";

            // The badge page does not always print playtime; Steam's own figure from
            // the library is the better source when it does not.
            if (owned is not null)
                hours = Math.Max(hours, owned.PlaytimeMinutes / 60d);

            badges.Add(new CardBadge(appId, name, drops, hours));
        }
    }

    private static string? ExtractName(string block)
    {
        var match = TitlePattern().Match(block);
        if (!match.Success)
            return null;

        var name = WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
        return name.Length == 0 ? null : name;
    }

    private static int DetectPageCount(string html)
    {
        var highest = 1;
        foreach (Match match in PageLinkPattern().Matches(html))
        {
            if (int.TryParse(match.Groups[1].Value, out var page) && page > highest)
                highest = page;
        }

        return highest;
    }

    [GeneratedRegex(@"/gamecards/(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex AppIdPattern();

    [GeneratedRegex(@"(\d+)\s+card drops? remaining", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DropsPattern();

    [GeneratedRegex(@"badge_title_stats_playtime"">\s*([\d.,]+)\s*hrs on record", RegexOptions.CultureInvariant)]
    private static partial Regex PlaytimePattern();

    [GeneratedRegex(@"badge_title"">\s*([^<]+?)\s*<", RegexOptions.CultureInvariant)]
    private static partial Regex TitlePattern();

    [GeneratedRegex(@"[?&]p=(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex PageLinkPattern();
}

public sealed class SteamBadgeException : Exception
{
    public SteamBadgeException(string message) : base(message)
    {
    }
}
