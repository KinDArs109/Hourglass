using System.Globalization;
using Hourglass.Utilities;

namespace Hourglass.ViewModels;

/// <summary>
/// One column of the history chart. The pixel height is worked out here rather than in
/// a converter, so the whole row scales against the same peak in one place.
/// </summary>
public sealed class DayBarViewModel
{
    /// <summary>Height of the plot area. Bars are measured against it.</summary>
    public const double ChartHeight = 96;

    /// <summary>Enough to stay visible on a day with only a couple of minutes on it.</summary>
    private const double MinimumVisibleHeight = 3;

    public DayBarViewModel(DateTime date, long seconds, long peakSeconds, bool isToday)
    {
        Date = date;
        Seconds = seconds;
        IsToday = isToday;

        Height = seconds <= 0 || peakSeconds <= 0
            ? 0
            : Math.Max(MinimumVisibleHeight, ChartHeight * seconds / peakSeconds);
    }

    public DateTime Date { get; }

    public long Seconds { get; }

    public bool IsToday { get; }

    public double Height { get; }

    public bool HasTime => Seconds > 0;

    /// <summary>Day of the month, under every few columns.</summary>
    public string DayLabel => Date.Day.ToString(CultureInfo.InvariantCulture);

    public string Tooltip => Seconds > 0
        ? $"{Date:d MMMM} — {TimeFormat.Compact(TimeSpan.FromSeconds(Seconds))}"
        : $"{Date:d MMMM} — накрутки не было";
}
