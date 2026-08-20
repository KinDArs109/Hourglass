using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;
using System.Windows.Threading;
using Hourglass.Models;
using Hourglass.Services;
using Hourglass.Services.Interfaces;
using Hourglass.Utilities;

namespace Hourglass.ViewModels;

public sealed class MainViewModel : ViewModelBase, IBoostController, IDisposable
{
    private readonly IConfigStore _store;
    private readonly IAppLogger _logger;
    private readonly IDialogService _dialogs;
    private readonly CapsuleCache _capsules;
    private readonly CardFarmService _cardFarm;
    private readonly SteamClientWatcher _watcher;
    private readonly SystemTrayService _tray;
    private readonly AutoStartService _autoStart;
    private readonly TelegramBotService _telegram;
    private readonly UpdateService _updates;
    private readonly Dictionary<string, SessionState> _notifiedStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, SteamBoostSession> _sessionFactory;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _clockTimer;

    private static readonly TimeSpan StartStagger = TimeSpan.FromSeconds(3);

    private AccountViewModel? _selectedAccount;
    private DateTime _lastTickUtc = DateTime.UtcNow;
    private string _statusText = "";
    private bool _disposed;

    public MainViewModel(
        IConfigStore store,
        IAppLogger logger,
        IDialogService dialogs,
        CapsuleCache capsules,
        CardFarmService cardFarm,
        SteamClientWatcher watcher,
        SystemTrayService tray,
        AutoStartService autoStart,
        TelegramBotService telegram,
        UpdateService updates,
        Func<string, SteamBoostSession> sessionFactory)
    {
        _store = store;
        _logger = logger;
        _dialogs = dialogs;
        _capsules = capsules;
        _cardFarm = cardFarm;
        _watcher = watcher;
        _tray = tray;
        _autoStart = autoStart;
        _telegram = telegram;
        _updates = updates;
        _sessionFactory = sessionFactory;

        AddAccountCommand = new AsyncRelayCommand(_ => AddAccountAsync());
        OpenSettingsCommand = new RelayCommand(_ => _dialogs.ShowSettings(this));
        OpenUpdateCommand = new RelayCommand(_ => OfferUpdate(), _ => HasUpdate);
        StartAllCommand = new AsyncRelayCommand(_ => StartAllAsync(), _ => Accounts.Any(a => !a.IsRunning));
        StopAllCommand = new AsyncRelayCommand(_ => StopAllAsync(), _ => Accounts.Any(a => a.IsRunning));

        Accounts.CollectionChanged += OnAccountsChanged;
        _logger.EntryWritten += OnLogEntryWritten;

        _tray.StartAllRequested += (_, _) => AsyncHelper.FireAndForget(StartAllAsync, nameof(StartAllAsync));
        _tray.StopAllRequested += (_, _) => AsyncHelper.FireAndForget(StopAllAsync, nameof(StopAllAsync));

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += OnTick;

        // Counters advance once a second; the clock is redrawn five times as often so
        // it never sits on a stale second long enough to skip one when the second hand
        // rolls over between two ticks.
        _clockTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _clockTimer.Tick += OnClockTick;
    }

    public ObservableCollection<AccountViewModel> Accounts { get; } = new();

    public ICommand AddAccountCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand OpenUpdateCommand { get; }
    public ICommand StartAllCommand { get; }
    public ICommand StopAllCommand { get; }

    public AccountViewModel? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (SetProperty(ref _selectedAccount, value))
                OnPropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasSelection => SelectedAccount is not null;

    public bool HasAccounts => Accounts.Count > 0;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool MinimizeToTray
    {
        get => _store.Config.MinimizeToTray;
        set
        {
            if (_store.Config.MinimizeToTray == value)
                return;

            _store.Config.MinimizeToTray = value;
            OnPropertyChanged();
            _store.Save();
        }
    }

    public bool PauseWhenSteamClientRuns
    {
        get => _store.Config.PauseWhenSteamClientRuns;
        set
        {
            if (_store.Config.PauseWhenSteamClientRuns == value)
                return;

            _store.Config.PauseWhenSteamClientRuns = value;
            OnPropertyChanged();
            _store.Save();

            foreach (var account in Accounts)
                account.PushPlan();
        }
    }

    public bool LaunchWithWindows
    {
        get => _store.Config.LaunchWithWindows;
        set
        {
            if (_store.Config.LaunchWithWindows == value)
                return;

            _store.Config.LaunchWithWindows = value;
            _autoStart.SetEnabled(value);
            OnPropertyChanged();
            _store.Save();
        }
    }

    public void Initialize()
    {
        foreach (var config in _store.Config.Accounts)
            Accounts.Add(CreateAccount(config));

        SelectedAccount = Accounts.FirstOrDefault();

        // Keep the registry and the config in agreement after a move or reinstall.
        if (_store.Config.LaunchWithWindows != _autoStart.IsEnabled)
            _autoStart.SetEnabled(_store.Config.LaunchWithWindows);

        _lastTickUtc = DateTime.UtcNow;
        _timer.Start();
        _clockTimer.Start();
        UpdateStatus();

        AsyncHelper.FireAndForget(StartAutoStartAccountsAsync, nameof(StartAutoStartAccountsAsync));

        _telegram.Start(this);

        AsyncHelper.FireAndForget(CheckForUpdatesAsync, nameof(CheckForUpdatesAsync));
    }

    /// <summary>Raised when a staged update needs the app to close and come back.</summary>
    public event EventHandler? RestartRequested;

    private UpdateInfo? _availableUpdate;

    public bool HasUpdate => _availableUpdate is not null;

    public string UpdateButtonText => _availableUpdate is { } update
        ? $"Обновить до {update.Version.Major}.{update.Version.Minor}"
        : "Обновить";

    /// <summary>Looks for a newer build. Silent when there is none or GitHub is unreachable.</summary>
    public async Task CheckForUpdatesAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var update = await _updates.CheckAsync(timeout.Token).ConfigureAwait(true);

        if (update is null)
            return;

        _availableUpdate = update;
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(UpdateButtonText));
        CommandManager.InvalidateRequerySuggested();

        _logger.Info(AppLogScopes.App, $"Доступна версия {update.Tag}");
    }

    private void OfferUpdate()
    {
        if (_availableUpdate is not { } update)
            return;

        if (_dialogs.ShowUpdate(update))
            RestartRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Spaces out the opening handshakes so Steam is not hit by all at once.</summary>
    private async Task StartAutoStartAccountsAsync()
    {
        var pending = Accounts.Where(account => account.Config.AutoStart).ToList();

        for (var index = 0; index < pending.Count; index++)
        {
            if (index > 0)
                await Task.Delay(StartStagger).ConfigureAwait(true);

            await pending[index].StartAsync().ConfigureAwait(true);
        }
    }

    public async Task ShutdownAsync()
    {
        _timer.Stop();
        await _telegram.StopAsync().ConfigureAwait(true);

        foreach (var account in Accounts.ToList())
            await account.ShutdownAsync().ConfigureAwait(true);

        _store.Save();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _clockTimer.Stop();
        _clockTimer.Tick -= OnClockTick;
        _logger.EntryWritten -= OnLogEntryWritten;
        Accounts.CollectionChanged -= OnAccountsChanged;

        foreach (var account in Accounts)
            account.Dispose();
    }

    // ------------------------------------------------------- remote control

    public Task<string> DescribeStatusAsync() => OnUiAsync(() =>
    {
        if (Accounts.Count == 0)
            return "Аккаунтов нет. Добавьте их в программе на компьютере.";

        var boosting = Accounts.Count(account => account.State == SessionState.Boosting);

        var header = boosting == Accounts.Count
            ? $"<b>Всё работает</b> · {Plural.Accounts(Accounts.Count)}"
            : $"<b>Накрутка идёт у {boosting} из {Accounts.Count}</b>";

        var blocks = Accounts.Select(account =>
        {
            var name = TelegramBotService.Escape(account.DisplayName);
            var block = $"{StateMark(account.State)} <b>{name}</b> — {account.StateText.ToLowerInvariant()}";

            var facts = new List<string>();
            if (account.HasSessionClock)
                facts.Add(account.SessionClockText);
            facts.Add($"всего {account.TotalBoostedText}");
            if (account.State == SessionState.Boosting)
                facts.Add(Plural.Games(account.ActiveGameCount));

            block += "\n" + string.Join(" · ", facts);

            // The detail line only earns its place when it says something the state does not.
            if (!string.IsNullOrWhiteSpace(account.StatusDetail) && account.State != SessionState.Boosting)
                block += $"\n<i>{TelegramBotService.Escape(account.StatusDetail)}</i>";

            return block;
        });

        var footer = _watcher.SignedInAccount is { } signedIn
            ? $"\n\n<i>Клиент Steam открыт под {TelegramBotService.Escape(signedIn)}</i>"
            : "";

        return header + "\n\n" + string.Join("\n\n", blocks) + footer;
    });

    /// <summary>A coloured dot reads far faster on a phone than the word does.</summary>
    private static string StateMark(SessionState state) => state switch
    {
        SessionState.Boosting => "\U0001F7E2",
        SessionState.Connecting or SessionState.SigningIn => "\U0001F535",
        SessionState.Paused or SessionState.Reconnecting => "\U0001F7E1",
        SessionState.NeedsLogin or SessionState.Failed => "\U0001F534",
        _ => "⚪"
    };

    Task<string> IBoostController.StartAllAsync() => OnUiFlatAsync(async () =>
    {
        await StartAllAsync().ConfigureAwait(true);
        return await DescribeStatusAsync().ConfigureAwait(true);
    });

    Task<string> IBoostController.StopAllAsync() => OnUiFlatAsync(async () =>
    {
        await StopAllAsync().ConfigureAwait(true);
        return await DescribeStatusAsync().ConfigureAwait(true);
    });

    Task<string> IBoostController.StartAccountAsync(string query) => OnUiFlatAsync(async () =>
    {
        var account = FindAccount(query);
        if (account is null)
            return $"Аккаунт «{query}» не найден.";

        await account.StartAsync(silent: true).ConfigureAwait(true);
        return $"{account.DisplayName}: {account.StateText}";
    });

    Task<string> IBoostController.StopAccountAsync(string query) => OnUiFlatAsync(async () =>
    {
        var account = FindAccount(query);
        if (account is null)
            return $"Аккаунт «{query}» не найден.";

        await account.StopAsync().ConfigureAwait(true);
        return $"{account.DisplayName}: остановлен.";
    });

    private AccountViewModel? FindAccount(string query) =>
        Accounts.FirstOrDefault(account =>
            account.Username.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            account.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>Reports only transitions worth waking a phone for.</summary>
    private void NotifyIfStateWorthReporting(AccountViewModel account)
    {
        if (!_store.Config.Telegram.NotifyProblems)
            return;

        _notifiedStates.TryGetValue(account.Username, out var previous);
        var current = account.State;

        if (previous == current)
            return;

        _notifiedStates[account.Username] = current;

        var wasBroken = previous is SessionState.NeedsLogin or SessionState.Failed;

        var name = TelegramBotService.Escape(account.DisplayName);

        var message = current switch
        {
            SessionState.NeedsLogin =>
                $"\U0001F534 <b>{name}</b>\nSteam не принял сохранённый вход — нужно войти заново.",
            SessionState.Failed =>
                $"\U0001F534 <b>{name}</b>\n{TelegramBotService.Escape(account.StatusDetail)}",
            SessionState.Boosting when wasBroken =>
                $"\U0001F7E2 <b>{name}</b>\nСнова работает.",
            _ => null
        };

        if (message is not null)
            _telegram.Notify(message);
    }

    private static Task<T> OnUiAsync<T>(Func<T> work)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        return dispatcher is null || dispatcher.CheckAccess()
            ? Task.FromResult(work())
            : dispatcher.InvokeAsync(work).Task;
    }

    /// <summary>Marshals work that is itself asynchronous, without double-wrapping the task.</summary>
    private static Task<T> OnUiFlatAsync<T>(Func<Task<T>> work)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        return dispatcher is null || dispatcher.CheckAccess()
            ? work()
            : dispatcher.InvokeAsync(work).Task.Unwrap();
    }

    // -------------------------------------------------------------- internals

    private AccountViewModel CreateAccount(AccountConfig config)
    {
        var session = _sessionFactory(config.Username);
        var account = new AccountViewModel(
            config, session, _store, _logger, _dialogs, _capsules, _cardFarm,
            () => _store.Config.PauseWhenSteamClientRuns);

        account.RemoveRequested += OnAccountRemoveRequested;
        account.StateChanged += (_, _) =>
        {
            UpdateStatus();
            NotifyIfStateWorthReporting(account);
        };
        return account;
    }

    private async Task AddAccountAsync()
    {
        var result = await _dialogs.ShowLoginAsync(null, null).ConfigureAwait(true);
        if (result is null)
            return;

        var existing = Accounts.FirstOrDefault(account =>
            string.Equals(account.Username, result.AccountName, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.Config.ProtectedRefreshToken = SecretProtector.Protect(result.RefreshToken);
            existing.Config.ProtectedGuardData = SecretProtector.Protect(result.GuardData);
            if (result.SteamId != 0)
                existing.Config.SteamId = result.SteamId;

            existing.ApplyLibrary(result.Library);
            _store.Save();
            SelectedAccount = existing;
            _dialogs.Error("Аккаунт уже добавлен", $"Вход для «{result.AccountName}» обновлён.");
            return;
        }

        var config = new AccountConfig
        {
            Username = result.AccountName,
            DisplayName = string.IsNullOrWhiteSpace(result.PersonaName)
                ? result.AccountName
                : result.PersonaName,
            SteamId = result.SteamId,
            ProtectedRefreshToken = SecretProtector.Protect(result.RefreshToken),
            ProtectedGuardData = SecretProtector.Protect(result.GuardData)
        };

        _store.Config.Accounts.Add(config);
        _store.Save();

        var account = CreateAccount(config);
        account.ApplyLibrary(result.Library);
        Accounts.Add(account);
        SelectedAccount = account;

        _store.Save();
        _logger.Success(config.Username, "Аккаунт добавлен");
    }

    private void OnAccountRemoveRequested(object? sender, EventArgs e)
    {
        if (sender is not AccountViewModel account)
            return;

        if (!_dialogs.Confirm("Удалить аккаунт",
                $"Убрать «{account.DisplayName}» из программы? Сохранённый вход будет удалён."))
            return;

        AsyncHelper.FireAndForget(async () =>
        {
            await account.ShutdownAsync().ConfigureAwait(true);

            account.RemoveRequested -= OnAccountRemoveRequested;
            Accounts.Remove(account);
            _store.Config.Accounts.RemoveAll(config =>
                string.Equals(config.Username, account.Username, StringComparison.OrdinalIgnoreCase));
            _store.Save();

            SelectedAccount = Accounts.FirstOrDefault();
            UpdateStatus();
        }, $"RemoveAccount:{account.Username}");
    }

    private async Task StartAllAsync()
    {
        var pending = Accounts.Where(account => !account.IsRunning).ToList();

        for (var index = 0; index < pending.Count; index++)
        {
            if (index > 0)
                await Task.Delay(StartStagger).ConfigureAwait(true);

            await pending[index].StartAsync().ConfigureAwait(true);
        }
    }

    private async Task StopAllAsync()
    {
        foreach (var account in Accounts.Where(account => account.IsRunning).ToList())
            await account.StopAsync().ConfigureAwait(true);
    }

    private void OnClockTick(object? sender, EventArgs e)
    {
        foreach (var account in Accounts)
            account.TickClock();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastTickUtc).TotalSeconds;
        _lastTickUtc = now;

        // A machine waking from sleep can hand us a huge delta; do not credit it.
        if (elapsed is <= 0 or > 10)
            elapsed = 0;

        foreach (var account in Accounts)
            account.Tick(elapsed);

        ApplySchedules();
        UpdateStatus();
    }

    /// <summary>
    /// Runs the schedule for every account. Start and stop are awaited off the timer
    /// tick, so a slow Steam handshake never stalls the UI clock.
    /// </summary>
    private void ApplySchedules()
    {
        foreach (var account in Accounts.Where(account => account.ScheduleEnabled))
        {
            var current = account;
            AsyncHelper.FireAndForget(current.ApplyScheduleAsync, $"Schedule:{current.Username}");
        }
    }

    private void OnAccountsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasAccounts));
        UpdateStatus();
    }

    private void OnLogEntryWritten(object? sender, AppLogEventArgs e)
    {
        if (e.Scope == AppLogScopes.App)
            return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;

        dispatcher.InvokeAsync(() =>
        {
            var account = Accounts.FirstOrDefault(item =>
                string.Equals(item.Username, e.Scope, StringComparison.OrdinalIgnoreCase));
            account?.AppendLog(e.Entry);
        });
    }

    private void UpdateStatus()
    {
        var boosting = Accounts.Count(account => account.State == SessionState.Boosting);
        var waiting = Accounts.Count(account =>
            account.State is SessionState.Paused or SessionState.Reconnecting
                or SessionState.Connecting or SessionState.SigningIn);
        var attention = Accounts.Count(account =>
            account.State is SessionState.NeedsLogin or SessionState.Failed);

        var clientText = _watcher.SignedInAccount is { } signedIn
            ? $"клиент Steam: {signedIn}"
            : _watcher.IsClientRunning
                ? "клиент Steam: запущен"
                : "клиент Steam: не запущен";

        StatusText = Accounts.Count == 0
            ? $"Аккаунтов нет · {clientText}"
            : $"Аккаунтов: {Accounts.Count} · накрутка: {boosting} · {clientText}";

        var trayStatus = attention > 0 ? TrayStatus.Attention
            : boosting > 0 ? TrayStatus.Active
            : waiting > 0 ? TrayStatus.Waiting
            : TrayStatus.Idle;

        _tray.UpdateStatus(trayStatus, boosting > 0
            ? $"{AppPaths.ProductName} — накрутка: {boosting}"
            : AppPaths.ProductName);
    }
}
