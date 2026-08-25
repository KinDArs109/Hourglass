namespace Hourglass.Services;

/// <summary>
/// What to idle next in order to keep card drops coming, why, and everything the
/// badge pages reported — the page shows the whole picture, not just the choice.
/// </summary>
public sealed record FarmPlan(
    IReadOnlyList<uint> AppIds,
    string Status,
    bool IsFinished,
    IReadOnlyList<CardBadge> Pending);

/// <summary>
/// Turns badge data into an idling plan.
///
/// Steam only starts dropping cards for a game after a couple of hours on record, and
/// while a game is below that line it has to be idled on its own — running a pile of
/// games together does nothing for it. So games past the line are farmed in bulk, and
/// everything else is warmed up one at a time.
/// </summary>
public static class CardFarmPlanner
{
    /// <summary>Hours on record before Steam begins dropping cards for a game.</summary>
    public const double HoursBeforeDropsBegin = 3.0;

    public static FarmPlan Plan(IReadOnlyList<CardBadge> badges)
    {
        // Two kinds of work: games with drops still owed, and games that have cards but
        // were never launched, so Steam has not started counting for them at all.
        var pending = badges
            .Where(badge => badge.DropsRemaining > 0 || badge.IsUnstarted)
            .ToList();

        if (pending.Count == 0)
            return new FarmPlan(
                Array.Empty<uint>(), "Карточек для фарма не осталось", IsFinished: true, pending);

        var totalDrops = pending.Sum(badge => Math.Max(0, badge.DropsRemaining));
        var unstarted = pending.Count(badge => badge.IsUnstarted);

        // Ready games first, then the ones still short of the threshold: that is the
        // order the farm will actually work through them.
        var ordered = pending
            .OrderByDescending(badge => badge.DropsRemaining > 0 &&
                                        badge.HoursPlayed >= HoursBeforeDropsBegin)
            .ThenByDescending(badge => badge.DropsRemaining)
            .ThenByDescending(badge => badge.HoursPlayed)
            .ToList();

        var ready = ordered
            .Where(badge => badge.DropsRemaining > 0 && badge.HoursPlayed >= HoursBeforeDropsBegin)
            .ToList();

        if (ready.Count > 0)
        {
            var selected = ready.Take(BoostPlan.MaxGames).ToList();
            var readyDrops = selected.Sum(badge => badge.DropsRemaining);

            var queued = pending.Count - selected.Count;
            var waiting = unstarted > 0 ? $", не начато {unstarted}" : "";
            var tail = queued > 0
                ? $" · в очереди ещё {queued} игр{waiting}, карточек всего {totalDrops}"
                : "";

            var status = selected.Count == 1
                ? $"Фарм «{selected[0].Name}» · карточек осталось {readyDrops}{tail}"
                : $"Фарм: игр {selected.Count}, карточек осталось {readyDrops}{tail}";

            return new FarmPlan(
                selected.Select(badge => badge.AppId).ToList(), status, IsFinished: false, ordered);
        }

        // Nothing is past the threshold yet. Below it Steam ignores a game that is idled
        // alongside others, so the closest one is warmed up on its own and the rest wait.
        var warmup = ordered.First();
        var hoursLeft = Math.Max(0, HoursBeforeDropsBegin - warmup.HoursPlayed);

        var totals = totalDrops > 0
            ? $"в очереди игр {pending.Count}, карточек всего {totalDrops}"
            : $"в очереди игр {pending.Count}";

        return new FarmPlan(
            new[] { warmup.AppId },
            $"Разогрев «{warmup.Name}»: ещё {hoursLeft:0.#} ч до первых карточек · {totals}",
            IsFinished: false,
            ordered);
    }
}
