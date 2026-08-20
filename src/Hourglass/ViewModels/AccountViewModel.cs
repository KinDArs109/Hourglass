using System.Collections.ObjectModel;
using System.Windows.Input;
using Hourglass.Models;
using Hourglass.Services;
using Hourglass.Services.Interfaces;
using Hourglass.Utilities;

namespace Hourglass.ViewModels;

/// <summary>Why a session was stopped. Drives what the journal says about it.</summary>
public enum StopReason
{
    Manual,
    Schedule,
    CardsFinished
}

public sealed class AccountViewModel : ViewModelBase, IDisposable
{
    private const int MaxLogEntries = 250;

    private readonly AccountConfig _config;
    private readonly SteamBoostSession _session;
    private readonly IConfigStore _store;
    private readonly IAppLogger _logger;
    private readonly IDialogService _dialogs;
    private readonly CardFarmService _cardFarm;
    private readonly Func<bool> _pauseWhenClientRuns;

    private static readonly TimeSpan FarmCheckInterval = TimeSpan.FromMinutes(12);

    /// <summary>Back-off after a failed badge read, so a broken token cannot spin.</summary>
    private static readonly TimeSpan FarmRetryInterval = TimeSpan.FromMinutes(2);

    private double _pendingSeconds;
    private IReadOnlyList<uint> _accruingTo = Array.Empty<uint>();
    private HashSet<uint> _accruingIds = new();
    private IReadOnlyList<uint> _farmAppIds = Array.Empty<uint>();
    private string _farmStatus = "";
    private DateTime _nextFarmCheckUtc = DateTime.MinValue;
    private bool _isRefreshingFarm;
    private bool _isFarmTab;
    private DateTime? _scheduleSuppressedUntil;
    private string _scheduleHint = "";
    private bool _isApplyingSchedule;
    private bool _disposed;

    public AccountViewModel(
        AccountConfig config,
        SteamBoostSession session,
        IConfigStore store,
        IAppLogger logger,
        IDialogService dialogs,
        CapsuleCache capsules,
        CardFarmService cardFarm,
        Func<bool> pauseWhenClientRuns)
    {
        _config = config;
        _session = session;
        _store = store;
        _logger = logger;
        _dialogs = dialogs;
        Capsules = capsules;
        _cardFarm = cardFarm;
        _pauseWhenClientRuns = pauseWhenClientRuns;

        Games = new ObservableCollection<GameViewModel>(
            config.Games.Select(game => new GameViewModel(game, capsules, OnGameToggled)));

        _session.StateChanged += OnSessionStateChanged;
        _session.PersonaResolved += OnPersonaResolved;
        _session.LibraryResolved += OnLibraryResolved;
        _session.SteamIdResolved += OnSteamIdResolved;
        _session.TokenRejected += OnTokenRejected;

        StartCommand = new AsyncRelayCommand(_ => StartAsync(), _ => !IsRunning);
        StopCommand = new AsyncRelayCommand(_ => StopAsync(), _ => IsRunning);
        SignInCommand = new AsyncRelayCommand(_ => SignInAsync());
        AddGamesCommand = new AsyncRelayCommand(_ => AddGamesAsync());
        RemoveGameCommand = new RelayCommand(parameter => RemoveGame(parameter as GameViewModel));
        RefreshFarmCommand = new AsyncRelayCommand(_ => RefreshFarmNowAsync(), _ => FarmCards && !_isRefreshingFarm);
        RemoveAccountCommand = new RelayCommand(_ => RemoveRequested?.Invoke(this, EventArgs.Empty));
        ShowLogCommand = new RelayCommand(_ => _dialogs.ShowLog(this));
    }

    public event EventHandler? RemoveRequested;
    public event EventHandler? StateChanged;

    public AccountConfig Config => _config;

    /// <summary>Shared store-art cache, handed to child view models.</summary>
    public CapsuleCache Capsules { get; }

    public string Username => _config.Username;

    public ObservableCollection<GameViewModel> Games { get; }

    public ObservableCollection<LogEntry> Log { get; } = new();

    public bool HasLogEntries => Log.Count > 0;

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand SignInCommand { get; }
    public ICommand AddGamesCommand { get; }
    public ICommand RemoveGameCommand { get; }
    public ICommand RefreshFarmCommand { get; }
    public ICommand RemoveAccountCommand { get; }
    public ICommand ShowLogCommand { get; }

    public string DisplayName => string.IsNullOrWhiteSpace(_config.DisplayName)
        ? _config.Username
        : _config.DisplayName;

    public string Initial => DisplayName.Length > 0 ? DisplayName[..1].ToUpperInvariant() : "?";

    public SessionState State => _session.State;

    public bool IsRunning => _session.IsActive;

    public bool NeedsSignIn => string.IsNullOrEmpty(_config.ProtectedRefreshToken)
                               || _session.State == SessionState.NeedsLogin;

    public string StateText => _session.State switch
    {
        SessionState.Stopped => "Остановлено",
        SessionState.Connecting => "Подключение",
        SessionState.SigningIn => "Вход",
        SessionState.Boosting => "Накрутка идёт",
        SessionState.Paused => "Пауза",
        SessionState.Reconnecting => "Переподключение",
        SessionState.NeedsLogin => "Нужен вход",
        SessionState.Failed => "Ошибка",
        _ => "—"
    };

    public string StatusDetail
    {
        get
        {
            var detail = _session.StatusDetail;
            if (_session.NextRetryUtc is not { } retryAt)
                return detail;

            var remaining = TimeFormat.Countdown(retryAt - DateTime.UtcNow);
            return string.IsNullOrEmpty(detail)
                ? $"Повтор через {remaining}"
                : $"{detail} · повтор через {remaining}";
        }
    }

    public string SessionClockText => _session.BoostingSinceUtc is { } since
        ? TimeFormat.Clock(DateTime.UtcNow - since)
        : "00:00:00";

    public bool HasSessionClock => _session.BoostingSinceUtc is not null;

    public string TotalBoostedText => TimeFormat.Compact(TimeSpan.FromSeconds(_config.BoostedSeconds));

    public int ActiveGameCount => Games.Count(game => game.IsEnabled);

    public string GamesHeader => FarmCards && FarmStatus.Length > 0
        ? FarmStatus
        : $"Игры · выбрано {ActiveGameCount} из {Games.Count}";

    public bool HasGames => Games.Count > 0;

    public bool IsOverGameLimit => ActiveGameCount > BoostPlan.MaxGames;

    public string GameLimitWarning =>
        $"Steam учитывает максимум {BoostPlan.MaxGames} игр одновременно — лишние {ActiveGameCount - BoostPlan.MaxGames} не считаются.";

    public bool ShowOnline
    {
        get => _config.ShowOnline;
        set
        {
            if (_config.ShowOnline == value)
                return;

            _config.ShowOnline = value;
            OnPropertyChanged();
            PushPlan();
            _store.Save();
        }
    }

    public bool AutoStart
    {
        get => _config.AutoStart;
        set
        {
            if (_config.AutoStart == value)
                return;

            _config.AutoStart = value;
            OnPropertyChanged();
            _store.Save();
        }
    }

    // ------------------------------------------------------------ card farming

    public bool FarmCards
    {
        get => _config.FarmCards;
        set
        {
            if (_config.FarmCards == value)
                return;

            _config.FarmCards = value;
            _farmAppIds = Array.Empty<uint>();
            _nextFarmCheckUtc = DateTime.MinValue;
            FarmStatus = value
                ? "Фарм включён — список игр программа выберет сама после входа в Steam"
                : "";

            OnPropertyChanged();
            OnPropertyChanged(nameof(GamesHeader));
            PushPlan();
            _store.Save();
        }
    }

    /// <summary>Everything the last badge read found, newest choice first.</summary>
    public ObservableCollection<FarmGameViewModel> FarmGames { get; } = new();

    public bool HasFarmGames => FarmGames.Count > 0;

    public string FarmSummary => FarmGames.Count == 0
        ? "Список появится после первого чтения значков"
        : $"{Plural.Games(FarmGames.Count)} с карточками · всего {FarmGames.Sum(game => game.DropsRemaining)} шт.";

    /// <summary>Which half of the account pane is on screen.</summary>
    public bool IsGamesTab
    {
        get => !_isFarmTab;
        set
        {
            if (value)
                IsFarmTab = false;
        }
    }

    public bool IsFarmTab
    {
        get => _isFarmTab;
        set
        {
            if (!SetProperty(ref _isFarmTab, value))
                return;

            OnPropertyChanged(nameof(IsGamesTab));
        }
    }

    /// <summary>Re-reads the badge pages right now instead of waiting out the interval.</summary>
    public async Task RefreshFarmNowAsync()
    {
        if (!FarmCards)
            return;

        _nextFarmCheckUtc = DateTime.MinValue;
        await RefreshCardFarmAsync().ConfigureAwait(true);
    }

    /// <summary>What the card farmer is doing, shown instead of the games counter.</summary>
    public string FarmStatus
    {
        get => _farmStatus;
        private set
        {
            if (SetProperty(ref _farmStatus, value))
                OnPropertyChanged(nameof(GamesHeader));
        }
    }

    /// <summary>
    /// Re-reads the badge pages now and then and hands the session a fresh set of games.
    /// Runs only while the account is actually signed in.
    /// </summary>
    private async Task RefreshCardFarmAsync()
    {
        if (!FarmCards || _isRefreshingFarm)
            return;

        if (!_session.IsSignedOn || _session.State != SessionState.Boosting)
            return;

        if (DateTime.UtcNow < _nextFarmCheckUtc)
            return;

        _isRefreshingFarm = true;

        // Claim the slot before the request: a failure must back off too, otherwise the
        // one-second tick would retry Steam every single second.
        _nextFarmCheckUtc = DateTime.UtcNow + FarmRetryInterval;

        try
        {
            var known = _config.Library.ToDictionary(
                entry => entry.AppId,
                entry => new OwnedGame(entry.AppId, entry.Name, entry.PlaytimeMinutes));

            var plan = await _cardFarm
                .PlanAsync(_session, known, CancellationToken.None)
                .ConfigureAwait(true);

            if (plan is null)
                return;

            _nextFarmCheckUtc = DateTime.UtcNow + FarmCheckInterval;

            var changed = !_farmAppIds.SequenceEqual(plan.AppIds);
            _farmAppIds = plan.AppIds;
            FarmStatus = plan.Status;
            ApplyFarmGames(plan);

            if (plan.IsFinished)
            {
                if (ActiveGameCount > 0)
                {
                    FarmStatus = "Карточек не осталось — кручу отмеченные игры";
                    _logger.Success(Username, "Карточек не осталось — перехожу на отмеченные игры");
                    PushPlan();
                }
                else
                {
                    _logger.Success(Username, "Карточек не осталось — фарм завершён");
                    await StopAsync(StopReason.CardsFinished).ConfigureAwait(true);
                }

                return;
            }

            if (changed)
            {
                _logger.Info(Username, plan.Status);
                PushPlan();
            }
        }
        finally
        {
            _isRefreshingFarm = false;
        }
    }

    // --------------------------------------------------------------- schedule

    public bool ScheduleEnabled
    {
        get => _config.Schedule.IsEnabled;
        set
        {
            if (_config.Schedule.IsEnabled == value)
                return;

            _config.Schedule.IsEnabled = value;
            _scheduleSuppressedUntil = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ScheduleSummary));
            _store.Save();
        }
    }

    public string ScheduleStartText
    {
        get => BoostScheduler.Format(_config.Schedule.StartMinute);
        set => SetScheduleMinute(value, isStart: true);
    }

    public string ScheduleEndText
    {
        get => BoostScheduler.Format(_config.Schedule.EndMinute);
        set => SetScheduleMinute(value, isStart: false);
    }

    public string DailyLimitText
    {
        get => _config.Schedule.DailyLimitHours > 0 ? _config.Schedule.DailyLimitHours.ToString() : "";
        set
        {
            var trimmed = value.Trim();
            var hours = trimmed.Length == 0 ? 0 : int.TryParse(trimmed, out var parsed) ? Math.Clamp(parsed, 0, 24) : -1;

            if (hours < 0)
            {
                OnPropertyChanged();
                return;
            }

            _config.Schedule.DailyLimitHours = hours;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ScheduleSummary));
            _store.Save();
        }
    }

    public string ScheduleSummary =>
        $"сегодня накручено {TimeFormat.Compact(TimeSpan.FromSeconds(_config.DailySeconds))}";

    /// <summary>What the schedule is doing right now, shown under the schedule row.</summary>
    public string ScheduleHint
    {
        get => _scheduleHint;
        private set => SetProperty(ref _scheduleHint, value);
    }

    public string CustomStatus
    {
        get => _config.CustomStatus ?? "";
        set
        {
            var trimmed = value.Trim();
            if ((_config.CustomStatus ?? "") == trimmed)
                return;

            _config.CustomStatus = trimmed.Length == 0 ? null : trimmed;
            OnPropertyChanged();
            PushPlan();
            _store.Save();
        }
    }

    // ------------------------------------------------------------- lifecycle

    public Task StartAsync() => StartAsync(silent: false);

    /// <summary>Silent starts come from the schedule and must never open a dialog.</summary>
    public async Task StartAsync(bool silent)
    {
        if (IsRunning)
            return;

        var token = SecretProtector.Unprotect(_config.ProtectedRefreshToken);
        if (token is null)
        {
            if (silent)
                return;

            await SignInAsync().ConfigureAwait(true);
            token = SecretProtector.Unprotect(_config.ProtectedRefreshToken);
            if (token is null)
                return;
        }

        if (!FarmCards && ActiveGameCount == 0 && string.IsNullOrWhiteSpace(CustomStatus))
        {
            if (!silent)
                _dialogs.Error("Нечего запускать",
                    "Отметьте хотя бы одну игру, включите фарм карточек или задайте свой статус.");
            return;
        }

        if (!silent)
        {
            _scheduleSuppressedUntil = null;
            _logger.Info(Username, "Запуск сессии");
        }

        _session.Start(token, BuildPlan());
        RaiseSessionProperties();
    }

    public async Task StopAsync() => await StopAsync(StopReason.Manual).ConfigureAwait(true);

    /// <summary>
    /// A manual stop also holds the schedule off until its next window, so the app does
    /// not immediately restart what the user just switched off.
    /// </summary>
    public async Task StopAsync(StopReason reason)
    {
        if (!IsRunning)
            return;

        if (reason == StopReason.Manual && ScheduleEnabled)
        {
            var decision = EvaluateSchedule();
            _scheduleSuppressedUntil = decision.NextChangeLocal;
        }

        // Through the logger so it lands in the file too: the journal in the window
        // is the first place anyone looks, but the file is what survives a restart.
        _logger.Info(Username, reason switch
        {
            StopReason.Schedule => "Остановка по расписанию",
            StopReason.CardsFinished => "Карточек не осталось — останавливаю",
            _ => "Остановка сессии"
        });

        await _session.StopAsync().ConfigureAwait(true);
        FlushCounters();
        RaiseSessionProperties();
    }

    /// <summary>
    /// Applies the schedule. Returns silently when the account has nothing to boost or
    /// needs a fresh sign-in — the schedule must never spam Steam with doomed logins.
    /// </summary>
    public async Task ApplyScheduleAsync()
    {
        if (!ScheduleEnabled)
        {
            ScheduleHint = "";
            return;
        }

        // The tick fires every second; a start or stop takes longer than that.
        if (_isApplyingSchedule)
            return;

        _isApplyingSchedule = true;
        try
        {
            await ApplyScheduleCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            _isApplyingSchedule = false;
        }
    }

    private async Task ApplyScheduleCoreAsync()
    {
        var decision = EvaluateSchedule();

        if (_scheduleSuppressedUntil is { } until)
        {
            if (DateTime.Now < until)
            {
                ScheduleHint = $"Остановлено вручную, расписание возобновит в {until:HH:mm}";
                return;
            }

            _scheduleSuppressedUntil = null;
        }

        if (decision.ShouldRun)
        {
            ScheduleHint = decision.NextChangeLocal is { } endsAt
                ? $"По расписанию работает до {endsAt:HH:mm}"
                : "По расписанию работает";

            var canStart = !IsRunning
                           && State is not (SessionState.NeedsLogin or SessionState.Failed)
                           && !string.IsNullOrEmpty(_config.ProtectedRefreshToken)
                           && (ActiveGameCount > 0 || !string.IsNullOrWhiteSpace(CustomStatus));

            if (canStart)
            {
                _logger.Info(Username, "Запуск по расписанию");
                await StartAsync(silent: true).ConfigureAwait(true);
            }

            return;
        }

        ScheduleHint = decision.NextChangeLocal is { } resumesAt
            ? $"{Capitalize(decision.Reason)} · возобновит в {resumesAt:HH:mm}"
            : Capitalize(decision.Reason);

        if (IsRunning)
            await StopAsync(StopReason.Schedule).ConfigureAwait(true);
    }

    private void ApplyFarmGames(FarmPlan plan)
    {
        var active = plan.AppIds.ToHashSet();

        FarmGames.Clear();
        foreach (var badge in plan.Pending)
            FarmGames.Add(new FarmGameViewModel(badge, active.Contains(badge.AppId), Capsules));

        OnPropertyChanged(nameof(HasFarmGames));
        OnPropertyChanged(nameof(FarmSummary));
    }

    private ScheduleDecision EvaluateSchedule() =>
        BoostScheduler.Evaluate(_config.Schedule, _config.Username, DateTime.Now, _config.DailySeconds);

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0]) + text[1..];

    private void SetScheduleMinute(string value, bool isStart)
    {
        if (!TryParseTimeOfDay(value, out var minutes))
        {
            // Reject silently and put the stored value back in the box.
            OnPropertyChanged(isStart ? nameof(ScheduleStartText) : nameof(ScheduleEndText));
            return;
        }

        if (isStart)
            _config.Schedule.StartMinute = minutes;
        else
            _config.Schedule.EndMinute = minutes;

        OnPropertyChanged(isStart ? nameof(ScheduleStartText) : nameof(ScheduleEndText));
        OnPropertyChanged(nameof(ScheduleSummary));
        _store.Save();
    }

    /// <summary>Accepts "23:00", "23.00", "2300" and "23".</summary>
    private static bool TryParseTimeOfDay(string text, out int minuteOfDay)
    {
        minuteOfDay = 0;

        var cleaned = new string(text.Where(char.IsDigit).ToArray());
        if (cleaned.Length is 0 or > 4)
            return false;

        var hours = cleaned.Length <= 2 ? int.Parse(cleaned) : int.Parse(cleaned[..^2]);
        var minutes = cleaned.Length <= 2 ? 0 : int.Parse(cleaned[^2..]);

        if (hours > 23 || minutes > 59)
            return false;

        minuteOfDay = hours * 60 + minutes;
        return true;
    }

    public async Task SignInAsync()
    {
        var result = await _dialogs
            .ShowLoginAsync(_config.Username, SecretProtector.Unprotect(_config.ProtectedGuardData))
            .ConfigureAwait(true);

        if (result is null)
            return;

        _config.ProtectedRefreshToken = SecretProtector.Protect(result.RefreshToken);
        _config.ProtectedGuardData = SecretProtector.Protect(result.GuardData);
        if (result.SteamId != 0)
            _config.SteamId = result.SteamId;
        if (!string.IsNullOrWhiteSpace(result.PersonaName))
            _config.DisplayName = result.PersonaName;

        ApplyLibrary(result.Library);
        _store.Save();

        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Initial));

        AppendLog(new LogEntry(DateTime.Now, LogLevel.Success, "Аккаунт заново авторизован"));
        OnPropertyChanged(nameof(NeedsSignIn));
        RaiseSessionProperties();
    }

    /// <summary>
    /// Redraws the session clock. Called several times a second, so the seconds change
    /// on their real boundary: at one call per second the timer runs a hair late, the
    /// lateness piles up, and every so often a whole second gets skipped on screen.
    /// </summary>
    public void TickClock()
    {
        if (_session.BoostingSinceUtc is not null)
            OnPropertyChanged(nameof(SessionClockText));
    }

    /// <summary>Called once per second by the shell to advance clocks and counters.</summary>
    public void Tick(double elapsedSeconds)
    {
        RollDailyCounter();

        if (_session.State == SessionState.Boosting)
        {
            _pendingSeconds += elapsedSeconds;
            var whole = (long)_pendingSeconds;
            if (whole > 0)
            {
                _pendingSeconds -= whole;
                _config.BoostedSeconds += whole;
                _config.DailySeconds += whole;

                AccrueToRunningGames(whole);
                RetireGamesThatReachedTheirGoal();

                OnPropertyChanged(nameof(TotalBoostedText));
                OnPropertyChanged(nameof(ScheduleSummary));
                _store.SaveDeferred();
            }

            OnPropertyChanged(nameof(SessionClockText));
        }
        else if (_pendingSeconds > 0)
        {
            _pendingSeconds = 0;
        }

        if (_session.NextRetryUtc is not null)
            OnPropertyChanged(nameof(StatusDetail));

        if (FarmCards)
            AsyncHelper.FireAndForget(RefreshCardFarmAsync, $"CardFarm:{Username}");
    }

    /// <summary>
    /// Credits the seconds to the games Steam is actually running, not to every ticked
    /// row. They are the same list most of the time, but not while card farming: there
    /// the ticked games sit idle, and crediting them would run their counters far ahead
    /// of the hours Steam will ever hand out.
    /// </summary>
    private void AccrueToRunningGames(long seconds)
    {
        var running = _session.ActiveAppIds;

        // The plan keeps the same list until it changes, so the set is rebuilt rarely.
        if (!ReferenceEquals(_accruingTo, running))
        {
            _accruingTo = running;
            _accruingIds = running.ToHashSet();
        }

        if (_accruingIds.Count == 0)
            return;

        foreach (var game in Games)
        {
            if (_accruingIds.Contains(game.AppId))
                game.Accrue(seconds);
        }
    }

    private void RollDailyCounter()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (_config.DailyDate == today)
            return;

        _config.DailyDate = today;
        _config.DailySeconds = 0;
        _scheduleSuppressedUntil = null;
        OnPropertyChanged(nameof(ScheduleSummary));
        _store.SaveDeferred();
    }

    /// <summary>Unticks any game that has reached its hour goal, so the rest keep going.</summary>
    private void RetireGamesThatReachedTheirGoal()
    {
        var finished = Games
            .Where(game => game.IsEnabled && game.HasGoal && game.IsGoalReached)
            .ToList();

        if (finished.Count == 0)
            return;

        foreach (var game in finished)
        {
            game.IsEnabled = false;
            _logger.Success(Username, $"Цель достигнута: {game.Name} — {game.Config.GoalHours} ч");
        }

        // IsEnabled already saved and pushed the plan through OnGameToggled.
        RaiseGameProperties();
    }

    /// <summary>
    /// The library is read by Steam itself at every sign-in and cached here, so the
    /// picker never has to open a session of its own.
    /// </summary>
    /// <summary>Asks Steam for the owned games again. False when the account is not on.</summary>
    public Task<bool> RefreshLibraryNowAsync(CancellationToken cancellationToken) =>
        _session.RefreshLibraryNowAsync(cancellationToken);

    public IReadOnlyList<OwnedGame> Library => _config.Library
        .Select(entry => new OwnedGame(entry.AppId, entry.Name, entry.PlaytimeMinutes))
        .ToList();

    public bool HasLibrary => _config.Library.Count > 0;

    public void AddGames(IEnumerable<GameConfig> games)
    {
        var existing = Games.Select(game => game.AppId).ToHashSet();
        var added = 0;

        foreach (var game in games)
        {
            if (game.AppId == 0 || !existing.Add(game.AppId))
                continue;

            _config.Games.Add(game);
            Games.Add(new GameViewModel(game, Capsules, OnGameToggled));
            added++;
        }

        if (added == 0)
            return;

        _store.Save();
        PushPlan();
        RaiseGameProperties();
        AppendLog(new LogEntry(DateTime.Now, LogLevel.Info, $"Добавлено игр: {added}"));
    }

    public void AppendLog(LogEntry entry)
    {
        var wasEmpty = Log.Count == 0;

        Log.Insert(0, entry);
        while (Log.Count > MaxLogEntries)
            Log.RemoveAt(Log.Count - 1);

        if (wasEmpty)
            OnPropertyChanged(nameof(HasLogEntries));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _session.StateChanged -= OnSessionStateChanged;
        _session.PersonaResolved -= OnPersonaResolved;
        _session.LibraryResolved -= OnLibraryResolved;
        _session.SteamIdResolved -= OnSteamIdResolved;
        _session.TokenRejected -= OnTokenRejected;
        _session.Dispose();
    }

    /// <summary>Stops the session and disposes it, used when shutting the app down.</summary>
    public async Task ShutdownAsync()
    {
        if (IsRunning)
            await _session.StopAsync().ConfigureAwait(false);

        FlushCounters();
        Dispose();
    }

    // -------------------------------------------------------------- internals

    private BoostPlan BuildPlan()
    {
        var manual = Games.Where(game => game.IsEnabled).Select(game => game.AppId).ToList();

        var appIds = FarmCards && _farmAppIds.Count > 0
            ? _farmAppIds
            : manual;

        return new BoostPlan(
            appIds.Take(BoostPlan.MaxGames).ToList(),
            string.IsNullOrWhiteSpace(CustomStatus) ? null : CustomStatus,
            ShowOnline,
            _pauseWhenClientRuns());
    }

    public void PushPlan()
    {
        if (IsRunning)
            _session.UpdatePlan(BuildPlan());
    }

    private void RemoveGame(GameViewModel? game)
    {
        if (game is null)
            return;

        if (!_dialogs.Confirm("Убрать игру", $"Убрать «{game.Name}» из списка?"))
            return;

        Games.Remove(game);
        _config.Games.RemoveAll(item => item.AppId == game.AppId);
        _store.Save();
        PushPlan();
        RaiseGameProperties();
    }

    private void OnGameToggled()
    {
        _store.Save();
        PushPlan();
        RaiseGameProperties();
    }

    private async Task AddGamesAsync()
    {
        var picked = await _dialogs.ShowGamePickerAsync(this).ConfigureAwait(true);
        if (picked is { Count: > 0 })
            AddGames(picked);
    }

    private void FlushCounters()
    {
        _pendingSeconds = 0;
        _store.Save();
    }

    private void RaiseGameProperties()
    {
        OnPropertyChanged(nameof(ActiveGameCount));
        OnPropertyChanged(nameof(GamesHeader));
        OnPropertyChanged(nameof(HasGames));
        OnPropertyChanged(nameof(IsOverGameLimit));
        OnPropertyChanged(nameof(GameLimitWarning));
    }

    private void RaiseSessionProperties()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(NeedsSignIn));
        OnPropertyChanged(nameof(HasSessionClock));
        OnPropertyChanged(nameof(SessionClockText));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSessionStateChanged(object? sender, EventArgs e) =>
        Dispatch(() =>
        {
            RaiseSessionProperties();

            var level = _session.State switch
            {
                SessionState.Boosting => LogLevel.Success,
                SessionState.Failed or SessionState.NeedsLogin => LogLevel.Error,
                SessionState.Paused or SessionState.Reconnecting => LogLevel.Warning,
                _ => LogLevel.Info
            };

            // Through the logger, so the file records how a session actually progressed.
            // Without this a stalled connect leaves no trace at all.
            var detail = _session.StatusDetail;
            var message = string.IsNullOrEmpty(detail) ? StateText : $"{StateText} — {detail}";

            switch (level)
            {
                case LogLevel.Success:
                    _logger.Success(Username, message);
                    break;
                case LogLevel.Warning:
                    _logger.Warn(Username, message);
                    break;
                case LogLevel.Error:
                    _logger.Error(Username, message);
                    break;
                default:
                    _logger.Info(Username, message);
                    break;
            }
        });

    private void OnLibraryResolved(object? sender, IReadOnlyList<OwnedGame> library) =>
        Dispatch(() => ApplyLibrary(library));

    public void ApplyLibrary(IReadOnlyList<OwnedGame> library)
    {
        if (library.Count == 0)
            return;

        _config.Library = library
            .Select(game => new LibraryEntry
            {
                AppId = game.AppId,
                Name = game.Name,
                PlaytimeMinutes = game.PlaytimeMinutes
            })
            .ToList();

        // Refresh what Steam says about the games already on the boost list, so the
        // user can watch Steam's own counter catch up.
        var byAppId = library.ToDictionary(game => game.AppId, game => game.PlaytimeMinutes);
        foreach (var game in Games)
        {
            if (byAppId.TryGetValue(game.AppId, out var minutes))
                game.UpdateSteamMinutes(minutes);
        }

        _store.Save();
        OnPropertyChanged(nameof(HasLibrary));
    }

    private void OnPersonaResolved(object? sender, string persona) =>
        Dispatch(() =>
        {
            if (string.IsNullOrWhiteSpace(persona) || _config.DisplayName == persona)
                return;

            _config.DisplayName = persona;
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(Initial));
            _store.SaveDeferred();
        });

    private void OnSteamIdResolved(object? sender, ulong steamId) =>
        Dispatch(() =>
        {
            if (steamId == 0 || _config.SteamId == steamId)
                return;

            _config.SteamId = steamId;
            _store.SaveDeferred();
        });

    private void OnTokenRejected(object? sender, EventArgs e) =>
        Dispatch(() =>
        {
            OnPropertyChanged(nameof(NeedsSignIn));
            _logger.Warn(Username, "Steam не принял сохранённый вход — нажмите «Войти заново»");
        });

    private static void Dispatch(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.InvokeAsync(action);
    }
}
