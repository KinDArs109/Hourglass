using System.Windows.Media;
using Hourglass.Models;
using Hourglass.Services;
using Hourglass.Utilities;

namespace Hourglass.ViewModels;

public sealed class GameViewModel : ViewModelBase
{
    private readonly GameConfig _config;
    private readonly Action _onChanged;

    private ImageSource? _capsule;
    private bool _isWaiting;

    public GameViewModel(GameConfig config, CapsuleCache capsules, Action onChanged)
    {
        _config = config;
        _onChanged = onChanged;

        AsyncHelper.FireAndForget(async () =>
        {
            var image = await capsules.GetAsync(config.AppId).ConfigureAwait(true);
            if (image is not null)
                Capsule = image;
        }, $"Capsule:{config.AppId}");
    }

    public GameConfig Config => _config;

    public uint AppId => _config.AppId;

    public string Name => _config.Name;

    /// <summary>Store art, or null while it loads or when the title has none.</summary>
    public ImageSource? Capsule
    {
        get => _capsule;
        private set => SetProperty(ref _capsule, value);
    }

    public bool IsEnabled
    {
        get => _config.IsEnabled;
        set
        {
            if (_config.IsEnabled == value)
                return;

            _config.IsEnabled = value;
            OnPropertyChanged();
            _onChanged();
        }
    }

    public string BoostedText => TimeFormat.Compact(TimeSpan.FromSeconds(_config.BoostedSeconds));

    /// <summary>
    /// True when the account is boosting but Steam was not told about this game — it
    /// happens while card farming runs its own picks. Without saying so, a counter
    /// standing still next to a ticked box just looks broken.
    /// </summary>
    public bool IsWaiting
    {
        get => _isWaiting;
        set
        {
            if (SetProperty(ref _isWaiting, value))
                OnPropertyChanged(nameof(BoostedCaption));
        }
    }

    public string BoostedCaption => IsWaiting ? "ждёт очереди" : "накручено";

    /// <summary>What Steam itself counted, as of the last sign-in.</summary>
    public string SteamTotalText => _config.SteamMinutes > 0
        ? $"в Steam {TimeFormat.Compact(TimeSpan.FromMinutes(_config.SteamMinutes))}"
        : "в Steam — нет данных";

    public void UpdateSteamMinutes(long minutes)
    {
        if (minutes <= 0 || _config.SteamMinutes == minutes)
            return;

        _config.SteamMinutes = minutes;
        OnPropertyChanged(nameof(SteamTotalText));
    }

    /// <summary>Hours to boost before this game switches itself off. Empty means no goal.</summary>
    public string GoalText
    {
        get => _config.GoalHours > 0 ? _config.GoalHours.ToString() : "";
        set
        {
            var trimmed = value.Trim();
            var hours = trimmed.Length == 0
                ? 0
                : int.TryParse(trimmed, out var parsed) ? Math.Clamp(parsed, 0, 100000) : -1;

            if (hours < 0 || hours == _config.GoalHours)
            {
                OnPropertyChanged();
                return;
            }

            _config.GoalHours = hours;
            OnPropertyChanged();
            RaiseGoalProperties();
            _onChanged();
        }
    }

    public bool HasGoal => _config.GoalHours > 0;

    public bool IsGoalReached => HasGoal && _config.BoostedSeconds >= _config.GoalHours * 3600L;

    /// <summary>0..1, for the thin progress bar under the title.</summary>
    public double GoalProgress => HasGoal
        ? Math.Clamp(_config.BoostedSeconds / (_config.GoalHours * 3600d), 0d, 1d)
        : 0d;

    public string GoalProgressText => HasGoal
        ? $"{TimeFormat.Compact(TimeSpan.FromSeconds(_config.BoostedSeconds))} из {_config.GoalHours} ч"
        : "";

    /// <summary>Back to zero. Steam's own figure is left alone — it is not ours to reset.</summary>
    public void ResetCounter()
    {
        if (_config.BoostedSeconds == 0)
            return;

        _config.BoostedSeconds = 0;
        OnPropertyChanged(nameof(BoostedText));
        RaiseGoalProperties();
    }

    public void Accrue(long seconds)
    {
        if (seconds <= 0)
            return;

        _config.BoostedSeconds += seconds;
        OnPropertyChanged(nameof(BoostedText));

        if (HasGoal)
            RaiseGoalProperties();
    }

    private void RaiseGoalProperties()
    {
        OnPropertyChanged(nameof(HasGoal));
        OnPropertyChanged(nameof(IsGoalReached));
        OnPropertyChanged(nameof(GoalProgress));
        OnPropertyChanged(nameof(GoalProgressText));
    }
}
