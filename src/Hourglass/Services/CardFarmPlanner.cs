namespace Hourglass.Services;

/// <summary>What to idle next in order to keep card drops coming, and why.</summary>
public sealed record FarmPlan(IReadOnlyList<uint> AppIds, string Status, bool IsFinished);

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
        var pending = badges.Where(badge => badge.DropsRemaining > 0).ToList();

        if (pending.Count == 0)
            return new FarmPlan(Array.Empty<uint>(), "Карточек для фарма не осталось", IsFinished: true);

        var totalDrops = pending.Sum(badge => badge.DropsRemaining);

        var ready = pending
            .Where(badge => badge.HoursPlayed >= HoursBeforeDropsBegin)
            .OrderByDescending(badge => badge.DropsRemaining)
            .ToList();

        if (ready.Count > 0)
        {
            var selected = ready.Take(BoostPlan.MaxGames).ToList();
            var readyDrops = selected.Sum(badge => badge.DropsRemaining);

            var queued = pending.Count - selected.Count;
            var tail = queued > 0 ? $" · в очереди ещё {queued} игр, карточек всего {totalDrops}" : "";

            var status = selected.Count == 1
                ? $"Фарм «{selected[0].Name}» · карточек осталось {readyDrops}{tail}"
                : $"Фарм: игр {selected.Count}, карточек осталось {readyDrops}{tail}";

            return new FarmPlan(selected.Select(badge => badge.AppId).ToList(), status, IsFinished: false);
        }

        // Nothing is past the threshold yet. Below it Steam ignores a game that is idled
        // alongside others, so the closest one is warmed up on its own and the rest wait.
        var warmup = pending.OrderByDescending(badge => badge.HoursPlayed).First();
        var hoursLeft = Math.Max(0, HoursBeforeDropsBegin - warmup.HoursPlayed);

        return new FarmPlan(
            new[] { warmup.AppId },
            $"Разогрев «{warmup.Name}»: ещё {hoursLeft:0.#} ч до первых карточек · " +
            $"в очереди игр {pending.Count}, карточек всего {totalDrops}",
            IsFinished: false);
    }
}
