using Hourglass.Models;

namespace Hourglass.Services;

/// <summary>Whether an account should be boosting right now, and why.</summary>
public sealed record ScheduleDecision(bool ShouldRun, string Reason, DateTime? NextChangeLocal);

/// <summary>
/// Pure decision logic for the unattended schedule: a daily time window plus a cap on
/// hours per day. Kept free of Steam and UI concerns so it can be reasoned about — and
/// checked — on its own.
/// </summary>
public static class BoostScheduler
{
    public const int MinutesPerDay = 24 * 60;

    public static ScheduleDecision Evaluate(
        ScheduleConfig schedule, string seed, DateTime nowLocal, long dailySeconds)
    {
        var midnightTomorrow = nowLocal.Date.AddDays(1);

        if (schedule.DailyLimitHours > 0 && dailySeconds >= schedule.DailyLimitHours * 3600L)
        {
            return new ScheduleDecision(
                false,
                $"дневной лимит {schedule.DailyLimitHours} ч выбран",
                midnightTomorrow);
        }

        var (start, end) = ResolveWindow(schedule, seed, nowLocal.Date);

        // Start == End is read as "all day", which is also the default.
        if (start == end)
            return new ScheduleDecision(true, "", midnightTomorrow);

        var minuteOfDay = nowLocal.Hour * 60 + nowLocal.Minute;
        var isInside = start < end
            ? minuteOfDay >= start && minuteOfDay < end
            : minuteOfDay >= start || minuteOfDay < end;   // window wraps past midnight

        var nextChange = NextBoundary(nowLocal, minuteOfDay, start, end, isInside);

        return isInside
            ? new ScheduleDecision(true, "", nextChange)
            : new ScheduleDecision(false, $"вне расписания, начало в {Format(start)}", nextChange);
    }

    /// <summary>Window start shifted by a per-day jitter so connections are not clockwork.</summary>
    public static (int Start, int End) ResolveWindow(ScheduleConfig schedule, string seed, DateTime day)
    {
        var start = Normalize(schedule.StartMinute);
        var end = Normalize(schedule.EndMinute);

        if (schedule.JitterMinutes <= 0 || start == end)
            return (start, end);

        var length = start < end ? end - start : MinutesPerDay - start + end;

        // Never let the jitter eat a short window.
        var jitterCap = Math.Min(schedule.JitterMinutes, Math.Max(0, (length - 30) / 2));
        if (jitterCap <= 0)
            return (start, end);

        var offset = (int)(StableHash($"{seed}|{day:yyyy-MM-dd}") % (uint)(jitterCap + 1));
        return (Normalize(start + offset), end);
    }

    public static string Format(int minuteOfDay)
    {
        var minutes = Normalize(minuteOfDay);
        return $"{minutes / 60:00}:{minutes % 60:00}";
    }

    public static int Normalize(int minuteOfDay)
    {
        var value = minuteOfDay % MinutesPerDay;
        return value < 0 ? value + MinutesPerDay : value;
    }

    private static DateTime NextBoundary(DateTime nowLocal, int minuteOfDay, int start, int end, bool isInside)
    {
        var target = isInside ? end : start;
        var minutesAhead = target - minuteOfDay;
        if (minutesAhead <= 0)
            minutesAhead += MinutesPerDay;

        return nowLocal.Date.AddMinutes(minuteOfDay + minutesAhead);
    }

    private static uint StableHash(string value)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var character in value)
            {
                hash ^= character;
                hash *= 16777619u;
            }

            return hash;
        }
    }
}
