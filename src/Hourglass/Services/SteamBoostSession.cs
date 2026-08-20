using System.IO;
using System.Threading.Channels;
using Hourglass.Models;
using Hourglass.Services.Interfaces;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;

namespace Hourglass.Services;

/// <summary>
/// Keeps one Steam account signed in and reporting games as played.
///
/// Steam callbacks are pushed onto a channel and consumed by a single state-machine
/// loop, so connect / sign-in / boost / retry never race each other.
/// </summary>
public sealed class SteamBoostSession : IDisposable
{
    private static readonly TimeSpan[] Backoff =
    {
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5)
    };

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan BoostPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ClientReleaseGrace = TimeSpan.FromSeconds(20);

    /// <summary>Steam commits playtime in chunks, so re-reading it often buys nothing.</summary>
    private static readonly TimeSpan LibraryRefreshInterval = TimeSpan.FromMinutes(10);

    private readonly IAppLogger _logger;
    private readonly SteamClientWatcher _watcher;
    private readonly SteamClient _client;
    private readonly CallbackManager _callbacks;
    private readonly SteamUser _steamUser;
    private readonly SteamFriends _steamFriends;
    private readonly List<IDisposable> _subscriptions = new();
    private readonly Channel<SessionEvent> _events =
        Channel.CreateUnbounded<SessionEvent>(new UnboundedChannelOptions { SingleReader = true });

    private readonly object _lifecycleGate = new();

    private CancellationTokenSource? _cts;
    private Task? _pumpTask;
    private Task? _runTask;
    private string _refreshToken = "";
    private BoostPlan _plan = new(Array.Empty<uint>(), null, true, true);
    private volatile bool _isRunning;
    private volatile bool _isSignedOn;
    private DateTime _lastLibraryRefreshUtc = DateTime.MinValue;
    private ulong _steamId;
    private bool _isReportingGames;
    private bool _disposed;

    public SteamBoostSession(
        string username, IAppLogger logger, SteamClientWatcher watcher, SteamConfiguration configuration)
    {
        Username = username;
        _logger = logger;
        _watcher = watcher;

        // The identifier only shows up in SteamKit's own debug output, but it makes a
        // two-account log readable when something goes wrong.
        _client = new SteamClient(configuration, username);
        _callbacks = new CallbackManager(_client);
        _steamUser = _client.GetHandler<SteamUser>()
                     ?? throw new InvalidOperationException("SteamUser handler is unavailable.");
        _steamFriends = _client.GetHandler<SteamFriends>()
                        ?? throw new InvalidOperationException("SteamFriends handler is unavailable.");

        _subscriptions.Add(_callbacks.Subscribe<SteamClient.ConnectedCallback>(
            _ => Publish(new SessionEvent(SessionEventKind.Connected))));
        _subscriptions.Add(_callbacks.Subscribe<SteamClient.DisconnectedCallback>(
            callback => Publish(new SessionEvent(SessionEventKind.Disconnected, UserInitiated: callback.UserInitiated))));
        _subscriptions.Add(_callbacks.Subscribe<SteamUser.LoggedOnCallback>(
            callback => Publish(new SessionEvent(SessionEventKind.SignedOn, callback.Result, SteamId: callback.ClientSteamID))));
        _subscriptions.Add(_callbacks.Subscribe<SteamUser.LoggedOffCallback>(
            callback => Publish(new SessionEvent(SessionEventKind.SignedOff, callback.Result))));
        _subscriptions.Add(_callbacks.Subscribe<SteamUser.AccountInfoCallback>(
            callback => PersonaResolved?.Invoke(this, callback.PersonaName)));
    }

    public string Username { get; }

    public SessionState State { get; private set; } = SessionState.Stopped;

    public string StatusDetail { get; private set; } = "";

    /// <summary>When set, the session is counting down to its next attempt.</summary>
    public DateTime? NextRetryUtc { get; private set; }

    /// <summary>UTC time the current uninterrupted boost started, if any.</summary>
    public DateTime? BoostingSinceUtc { get; private set; }

    /// <summary>
    /// The games Steam is being told about right now, empty when nothing is running.
    /// This is what actually earns playtime, which is not always the ticked list: with
    /// card farming on it is the farmer's picks, and a long list is cut to the limit.
    /// </summary>
    public IReadOnlyList<uint> ActiveAppIds => _isReportingGames && State == SessionState.Boosting
        ? _plan.AppIds
        : Array.Empty<uint>();

    /// <summary>
    /// True between <see cref="Start"/> and the moment the state machine settles.
    /// Flipped before the final state change so the UI never sees "running" and
    /// a terminal state at the same time.
    /// </summary>
    public bool IsActive => _isRunning;

    public event EventHandler? StateChanged;
    public event EventHandler<string>? PersonaResolved;
    public event EventHandler<IReadOnlyList<OwnedGame>>? LibraryResolved;
    public event EventHandler<ulong>? SteamIdResolved;
    public event EventHandler? TokenRejected;

    public void Start(string refreshToken, BoostPlan plan)
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_isRunning)
                return;

            _isRunning = true;
            _refreshToken = refreshToken;
            _plan = plan;

            // A previous run may have ended on its own without StopAsync.
            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            var token = _cts.Token;
            _pumpTask = Task.Factory.StartNew(
                () => PumpCallbacks(token), token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            _runTask = Task.Run(() => RunAsync(token), CancellationToken.None);
        }
    }

    public void UpdatePlan(BoostPlan plan)
    {
        _plan = plan;

        if (State == SessionState.Boosting && _isReportingGames)
            ApplyPlanToSteam(plan);
    }

    public async Task StopAsync()
    {
        Task? runTask;
        Task? pumpTask;
        CancellationTokenSource? cts;

        lock (_lifecycleGate)
        {
            cts = _cts;
            runTask = _runTask;
            pumpTask = _pumpTask;
            _cts = null;
            _runTask = null;
            _pumpTask = null;
            _isRunning = false;
        }

        if (cts is null)
        {
            SetState(SessionState.Stopped, "");
            return;
        }

        // Drop the playing state before tearing the connection down, otherwise
        // Steam keeps showing the account in-game for a while.
        if (_isReportingGames)
            TrySend(BuildGamesPlayedMessage(Array.Empty<uint>(), null));

        _isReportingGames = false;
        SafeCancel(cts);
        TryDisconnect();

        if (runTask is not null)
            await WaitWithTimeoutAsync(runTask, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        if (pumpTask is not null)
            await WaitWithTimeoutAsync(pumpTask, TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        cts.Dispose();
        SetState(SessionState.Stopped, "");
    }

    /// <summary>True while Steam has this session signed in and answering.</summary>
    public bool IsSignedOn => _isSignedOn;

    /// <summary>
    /// Re-reads the owned games straight away, for when the user has just bought
    /// something and does not want to wait for the periodic refresh.
    /// </summary>
    public async Task<bool> RefreshLibraryNowAsync(CancellationToken cancellationToken)
    {
        if (!_isSignedOn || _steamId == 0)
            return false;

        await RefreshLibraryAsync(_steamId, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public ulong SteamId => _steamId;

    /// <summary>
    /// Mints a Web API access token. Steam only issues one to a signed-in session, so
    /// this returns null until the account is actually on.
    /// </summary>
    public async Task<string?> GetWebTokenAsync(CancellationToken cancellationToken)
    {
        if (!_isSignedOn || _steamId == 0 || string.IsNullOrEmpty(_refreshToken))
            return null;

        try
        {
            var result = await _client.Authentication
                .GenerateAccessTokenForAppAsync(_steamId, _refreshToken, allowRenewal: false)
                .WaitAsync(TimeSpan.FromSeconds(20), cancellationToken)
                .ConfigureAwait(false);

            return result.AccessToken;
        }
        catch (Exception ex) when (ex is AuthenticationException or TimeoutException or OperationCanceledException)
        {
            _logger.Warn(Username, $"Не удалось получить веб-токен Steam: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        foreach (var subscription in _subscriptions)
            subscription.Dispose();
        _subscriptions.Clear();

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    // ---------------------------------------------------------------- pumping

    private void PumpCallbacks(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(250));
            }
            catch (Exception ex)
            {
                _logger.Error(Username, "Ошибка в обработчике событий Steam", ex);
                Thread.Sleep(500);
            }
        }
    }

    private void Publish(SessionEvent sessionEvent) => _events.Writer.TryWrite(sessionEvent);

    // ----------------------------------------------------------- state machine

    private static readonly TimeSpan[] PauseBackoff =
    {
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15)
    };

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var failedAttempts = 0;
        var consecutivePauses = 0;
        var finalState = SessionState.Stopped;
        var finalDetail = "";
        var tokenRejected = false;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await WaitForLocalClientToReleaseAccountAsync(cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                    break;

                var outcome = await RunAttemptAsync(cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                    break;

                if (outcome.IsFatal)
                {
                    finalState = outcome.FatalState;
                    finalDetail = outcome.Reason;
                    tokenRejected = outcome.FatalState == SessionState.NeedsLogin;
                    break;
                }

                if (outcome.ResetBackoff)
                    failedAttempts = 0;

                var delay = outcome.IsPause
                    ? PauseBackoff[Math.Min(consecutivePauses++, PauseBackoff.Length - 1)]
                    : outcome.Delay ?? Backoff[Math.Min(failedAttempts++, Backoff.Length - 1)];

                if (!outcome.IsPause)
                    consecutivePauses = 0;
                await CountDownAsync(delay, outcome.Reason, outcome.IsPause, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.Error(Username, "Сессия завершилась с ошибкой", ex);
            finalState = SessionState.Failed;
            finalDetail = ex.Message;
        }
        finally
        {
            // Stop the callback pump too: without this a session that gave up
            // would keep a background thread spinning until the app closes.
            StopPump();

            NextRetryUtc = null;
            BoostingSinceUtc = null;
            _isRunning = false;
            SetState(finalState, finalDetail);

            if (tokenRejected)
                TokenRejected?.Invoke(this, EventArgs.Empty);
        }
    }

    private void StopPump()
    {
        lock (_lifecycleGate)
        {
            if (!_isRunning)
                return;

            SafeCancel(_cts);
        }
    }

    private static void SafeCancel(CancellationTokenSource? source)
    {
        try
        {
            source?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down by StopAsync.
        }
    }

    private async Task<AttemptOutcome> RunAttemptAsync(CancellationToken cancellationToken)
    {
        DrainEvents();

        SetState(SessionState.Connecting, "Подключение к Steam…");
        _client.Connect();

        var connected = await ReadEventAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
        if (connected is null)
        {
            TryDisconnect();
            return AttemptOutcome.Retry("Сервер Steam не ответил");
        }

        if (connected.Kind != SessionEventKind.Connected)
        {
            TryDisconnect();
            return AttemptOutcome.Retry("Соединение оборвалось");
        }

        SetState(SessionState.SigningIn, "Вход в аккаунт…");
        _steamUser.LogOn(new SteamUser.LogOnDetails
        {
            Username = Username,
            AccessToken = _refreshToken,
            ShouldRememberPassword = true,
            LoginID = SteamLoginIds.For(Username),
            ClientOSType = EOSType.Win11,
            MachineName = AppPaths.ProductName
        });

        var signedOn = await ReadEventAsync(SignInTimeout, cancellationToken).ConfigureAwait(false);
        if (signedOn is null)
        {
            TryDisconnect();
            return AttemptOutcome.Retry("Steam не ответил на вход");
        }

        if (signedOn.Kind != SessionEventKind.SignedOn)
        {
            TryDisconnect();
            return AttemptOutcome.Retry("Соединение оборвалось при входе");
        }

        if (signedOn.Result != EResult.OK)
        {
            TryDisconnect();
            return MapSignInFailure(signedOn.Result);
        }

        if (signedOn.SteamId is { } steamId && steamId.IsValid)
            SteamIdResolved?.Invoke(this, steamId.ConvertToUInt64());

        _logger.Success(Username, "Вход выполнен");

        _steamId = signedOn.SteamId?.ConvertToUInt64() ?? 0UL;
        _isSignedOn = true;

        if (_steamId != 0)
            await RefreshLibraryAsync(_steamId, cancellationToken).ConfigureAwait(false);

        return await BoostAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the library while signed in. Never fatal: boosting must continue even if
    /// Steam declines to answer.
    /// </summary>
    private async Task RefreshLibraryAsync(ulong steamId, CancellationToken cancellationToken)
    {
        _lastLibraryRefreshUtc = DateTime.UtcNow;

        try
        {
            var library = await SteamLibrary.FetchAsync(_client, steamId, cancellationToken).ConfigureAwait(false);
            if (library.Count > 0)
            {
                _logger.Info(Username, $"Библиотека обновлена: игр {library.Count}");
                LibraryResolved?.Invoke(this, library);
            }
        }
        catch (Exception ex) when (ex is SteamLibraryException or TimeoutException
                                       or AsyncJobFailedException or InvalidOperationException)
        {
            _logger.Warn(Username, $"Список игр получить не удалось: {ex.Message}");
        }
    }

    private async Task<AttemptOutcome> BoostAsync(CancellationToken cancellationToken)
    {
        var plan = _plan;
        _isReportingGames = true;
        BoostingSinceUtc = DateTime.UtcNow;
        NextRetryUtc = null;
        SetState(SessionState.Boosting, DescribePlan(plan));
        ApplyPlanToSteam(plan);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var next = await ReadEventAsync(BoostPollInterval, cancellationToken).ConfigureAwait(false);

                if (next is null)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    // Stand down before Steam kicks us, so the owner never sees a
                    // "logged in elsewhere" prompt on their own client.
                    if (_plan.PauseWhenClientRuns && _watcher.IsPlayingAs(Username))
                        return AttemptOutcome.Pause("вы играете сами на этом аккаунте");

                    // Keep Steam's own playtime figures fresh while we idle.
                    if (_steamId != 0 && DateTime.UtcNow - _lastLibraryRefreshUtc >= LibraryRefreshInterval)
                        await RefreshLibraryAsync(_steamId, cancellationToken).ConfigureAwait(false);

                    continue;
                }

                switch (next.Kind)
                {
                    case SessionEventKind.SignedOff when next.Result == EResult.LoggedInElsewhere
                                                      || next.Result == EResult.LogonSessionReplaced:
                        return AttemptOutcome.Pause("Вход в аккаунт выполнен с другого устройства");

                    case SessionEventKind.SignedOff:
                        return AttemptOutcome.Retry($"Steam завершил сессию ({Describe(next.Result)})", resetBackoff: true);

                    case SessionEventKind.Disconnected when next.UserInitiated:
                        return AttemptOutcome.Retry("Соединение закрыто");

                    case SessionEventKind.Disconnected:
                        return AttemptOutcome.Retry("Соединение с Steam потеряно", resetBackoff: true);
                }
            }
        }
        finally
        {
            _isReportingGames = false;
            _isSignedOn = false;
            BoostingSinceUtc = null;
            TryDisconnect();
        }

        return AttemptOutcome.Retry("");
    }

    private void ApplyPlanToSteam(BoostPlan plan)
    {
        _steamFriends.SetPersonaState(plan.ShowOnline ? EPersonaState.Online : EPersonaState.Invisible);
        TrySend(BuildGamesPlayedMessage(plan.AppIds, plan.CustomStatus));

        if (State == SessionState.Boosting)
            SetState(SessionState.Boosting, DescribePlan(plan));
    }

    private static ClientMsgProtobuf<CMsgClientGamesPlayed> BuildGamesPlayedMessage(
        IReadOnlyList<uint> appIds, string? customStatus)
    {
        var message = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);

        // A shortcut entry with no app id is what Steam renders as free-text status.
        if (!string.IsNullOrWhiteSpace(customStatus))
        {
            message.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed
            {
                game_id = new GameID
                {
                    AppID = 0,
                    AppType = GameID.GameType.Shortcut,
                    ModID = uint.MaxValue
                },
                game_extra_info = customStatus
            });
        }

        foreach (var appId in appIds.Take(BoostPlan.MaxGames))
        {
            message.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed
            {
                game_id = new GameID(appId)
            });
        }

        return message;
    }

    private async Task WaitForLocalClientToReleaseAccountAsync(CancellationToken cancellationToken)
    {
        if (!_plan.PauseWhenClientRuns || !_watcher.IsPlayingAs(Username))
            return;

        SetState(SessionState.Paused, "Ждём, пока вы доиграете");
        NextRetryUtc = null;

        while (!cancellationToken.IsCancellationRequested && _watcher.IsPlayingAs(Username))
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);

        if (cancellationToken.IsCancellationRequested)
            return;

        // Steam keeps the session for a moment after the client closes.
        await Task.Delay(ClientReleaseGrace, cancellationToken).ConfigureAwait(false);
    }

    private async Task CountDownAsync(TimeSpan delay, string reason, bool isPause, CancellationToken cancellationToken)
    {
        NextRetryUtc = DateTime.UtcNow + delay;
        SetState(isPause ? SessionState.Paused : SessionState.Reconnecting, reason);

        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            NextRetryUtc = null;
        }
    }

    private AttemptOutcome MapSignInFailure(EResult result)
    {
        switch (result)
        {
            case EResult.InvalidPassword:
            case EResult.Expired:
            case EResult.Revoked:
            case EResult.AccessDenied:
                _logger.Warn(Username, "Сохранённый вход больше не действует — нужно войти заново");
                return AttemptOutcome.Fatal(SessionState.NeedsLogin, "Нужно войти заново");

            case EResult.AccountDisabled:
            case EResult.AccountLockedDown:
            case EResult.Suspended:
                _logger.Error(Username, $"Аккаунт недоступен: {Describe(result)}");
                return AttemptOutcome.Fatal(SessionState.Failed, Describe(result));

            case EResult.RateLimitExceeded:
            case EResult.AccountLoginDeniedThrottle:
                _logger.Warn(Username, "Слишком много попыток входа — пауза на 30 минут");
                return AttemptOutcome.Retry("Ограничение Steam на частые входы", TimeSpan.FromMinutes(30));

            case EResult.TryAnotherCM:
            case EResult.ServiceUnavailable:
            case EResult.Busy:
                return AttemptOutcome.Retry("Сервер Steam занят", TimeSpan.FromSeconds(15), resetBackoff: true);

            default:
                _logger.Warn(Username, $"Вход не удался: {Describe(result)}");
                return AttemptOutcome.Retry(Describe(result));
        }
    }

    private async Task<SessionEvent?> ReadEventAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            return await _events.Reader.ReadAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private void DrainEvents()
    {
        while (_events.Reader.TryRead(out _))
        {
        }
    }

    private void TrySend(IClientMsg message)
    {
        try
        {
            if (_client.IsConnected)
                _client.Send(message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
        {
            _logger.Warn(Username, $"Не удалось отправить состояние игр: {ex.Message}");
        }
    }

    private void TryDisconnect()
    {
        try
        {
            _client.Disconnect();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            _logger.Warn(Username, $"Ошибка при отключении: {ex.Message}");
        }
    }

    private void SetState(SessionState state, string detail)
    {
        if (State == state && StatusDetail == detail)
            return;

        State = state;
        StatusDetail = detail;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string DescribePlan(BoostPlan plan) => plan.AppIds.Count switch
    {
        0 => "Ожидание списка игр",
        1 => "Идёт накрутка: 1 игра",
        _ => $"Идёт накрутка: {plan.AppIds.Count} игр"
    };

    private static string Describe(EResult result) => result switch
    {
        EResult.LoggedInElsewhere => "вход с другого устройства",
        EResult.LogonSessionReplaced => "сессия заменена",
        EResult.ServiceUnavailable => "сервис недоступен",
        EResult.Timeout => "таймаут",
        EResult.InvalidPassword => "неверные данные входа",
        EResult.AccountDisabled => "аккаунт отключён",
        EResult.AccountLockedDown => "аккаунт заблокирован",
        EResult.Suspended => "аккаунт приостановлен",
        EResult.RateLimitExceeded => "слишком много попыток",
        _ => result.ToString()
    };

    private static async Task WaitWithTimeoutAsync(Task task, TimeSpan timeout)
    {
        try
        {
            await task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
        }
    }

    private enum SessionEventKind
    {
        Connected,
        Disconnected,
        SignedOn,
        SignedOff
    }

    private sealed record SessionEvent(
        SessionEventKind Kind,
        EResult Result = EResult.OK,
        bool UserInitiated = false,
        SteamID? SteamId = null);

    private sealed record AttemptOutcome
    {
        private AttemptOutcome(string reason)
        {
            Reason = reason;
        }

        public string Reason { get; }
        public TimeSpan? Delay { get; private init; }
        public bool IsFatal { get; private init; }
        public bool IsPause { get; private init; }
        public SessionState FatalState { get; private init; }
        public bool ResetBackoff { get; private init; }

        public static AttemptOutcome Retry(string reason, TimeSpan? delay = null, bool resetBackoff = false) =>
            new(reason) { Delay = delay, ResetBackoff = resetBackoff };

        public static AttemptOutcome Pause(string reason) =>
            new(reason) { Delay = TimeSpan.FromSeconds(30), ResetBackoff = true, IsPause = true };

        public static AttemptOutcome Fatal(SessionState state, string reason) =>
            new(reason) { IsFatal = true, FatalState = state };
    }
}
