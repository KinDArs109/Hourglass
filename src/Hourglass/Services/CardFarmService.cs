using System.Net.Http;
using Hourglass.Models;
using Hourglass.Services.Interfaces;

namespace Hourglass.Services;

/// <summary>
/// Works out what an account should idle to collect its remaining trading cards.
/// Stateless on purpose: the caller decides how often to ask.
/// </summary>
public sealed class CardFarmService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAppLogger _logger;

    public CardFarmService(IHttpClientFactory httpClientFactory, IAppLogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Returns null when the account is not signed in yet, or Steam would not answer.
    /// A failure here must never stop ordinary boosting.
    /// </summary>
    public async Task<FarmPlan?> PlanAsync(
        SteamBoostSession session,
        IReadOnlyDictionary<uint, OwnedGame> known,
        CancellationToken cancellationToken)
    {
        if (!session.IsSignedOn || session.SteamId == 0)
            return null;

        var token = await session.GetWebTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null)
            return null;

        try
        {
            var badges = await SteamBadges
                .FetchAsync(_httpClientFactory, session.SteamId, token, known, cancellationToken)
                .ConfigureAwait(false);

            var plan = CardFarmPlanner.Plan(badges);
            _logger.Info(session.Username,
                $"Значки прочитаны: игр с карточками {badges.Count(badge => badge.DropsRemaining > 0)}");

            return plan;
        }
        catch (SteamBadgeException ex)
        {
            _logger.Warn(session.Username, $"Фарм карточек: {ex.Message}");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.Warn(session.Username, "Фарм карточек: Steam не ответил, попробую позже");
            return null;
        }
    }
}
