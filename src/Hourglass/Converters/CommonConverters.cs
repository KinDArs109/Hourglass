using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Hourglass.Models;

namespace Hourglass.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public bool UseHidden { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (Invert)
            flag = !flag;

        return flag ? Visibility.Visible : UseHidden ? Visibility.Hidden : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible != Invert;
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}

/// <summary>Maps a session state onto the palette used by pills, dots and borders.</summary>
public sealed class SessionStateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = (value as SessionState?) switch
        {
            SessionState.Boosting => "SuccessBrush",
            SessionState.Connecting or SessionState.SigningIn => "AccentBrush",
            SessionState.Paused or SessionState.Reconnecting => "WarningBrush",
            SessionState.NeedsLogin or SessionState.Failed => "DangerBrush",
            _ => "TextMutedBrush"
        };

        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class LogLevelToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = (value as LogLevel?) switch
        {
            LogLevel.Success => "SuccessBrush",
            LogLevel.Warning => "WarningBrush",
            LogLevel.Error => "DangerBrush",
            _ => "TextSecondaryBrush"
        };

        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
