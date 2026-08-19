using System.Globalization;

namespace Hourglass.Utilities;

public static class TimeFormat
{
    /// <summary>"12ч 04м" — compact accumulated-time text for cards and lists.</summary>
    public static string Compact(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;

        var hours = (long)span.TotalHours;
        if (hours >= 1)
            return $"{hours}ч {span.Minutes:00}м";

        return span.Minutes >= 1
            ? $"{span.Minutes}м {span.Seconds:00}с"
            : $"{span.Seconds}с";
    }

    /// <summary>"03:14:07" — running session clock.</summary>
    public static string Clock(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;

        return string.Create(CultureInfo.InvariantCulture,
            $"{(long)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}");
    }

    /// <summary>"1:05" — countdown until the next reconnect attempt.</summary>
    public static string Countdown(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;

        var total = (long)Math.Ceiling(span.TotalSeconds);
        return string.Create(CultureInfo.InvariantCulture, $"{total / 60}:{total % 60:00}");
    }
}
