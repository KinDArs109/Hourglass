using System.Net.Http;
using Hourglass.Models;
using Hourglass.Services.Interfaces;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;

namespace Hourglass.Services;

public sealed record LoginResult(
    string AccountName,
    ulong SteamId,
    string RefreshToken,
    string? GuardData,
    string? PersonaName,
    IReadOnlyList<OwnedGame> Library);

/// <summary>
/// Runs the interactive credential sign-in once, to obtain a long-lived refresh
/// token. The password stays in memory for the duration of this call and is never
/// written anywhere.
///
/// The same connection is then signed in once to read the persona name and the game
/// library, so the rest of the app never has to open an extra Steam session for that.
/// </summary>
public sealed class SteamLoginService
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SignOnTimeout = TimeSpan.FromSeconds(45);

    private readonly IAppLogger _logger;
    private readonly SteamRuntime _runtime;

    public SteamLoginService(IAppLogger logger, SteamRuntime runtime)
    {
        _logger = logger;
        _runtime = runtime;
    }

    public async Task<LoginResult> SignInAsync(
        string username,
        string password,
        string? guardData,
        IAuthenticator authenticator,
        CancellationToken cancellationToken)
    {
        var client = new SteamClient(_runtime.Configuration, username);
        var callbacks = new CallbackManager(client);
        var steamUser = client.GetHandler<SteamUser>()
                        ?? throw new InvalidOperationException("SteamUser handler is unavailable.");

        var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var signedOn = new TaskCompletionSource<SteamUser.LoggedOnCallback>(TaskCreationOptions.RunContinuationsAsynchronously);
        var personaName = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var connectedSubscription = callbacks.Subscribe<SteamClient.ConnectedCallback>(
            _ => connected.TrySetResult(true));
        using var disconnectedSubscription = callbacks.Subscribe<SteamClient.DisconnectedCallback>(
            _ => connected.TrySetResult(false));
        using var signedOnSubscription = callbacks.Subscribe<SteamUser.LoggedOnCallback>(
            callback => signedOn.TrySetResult(callback));
        using var accountInfoSubscription = callbacks.Subscribe<SteamUser.AccountInfoCallback>(
            callback => personaName.TrySetResult(callback.PersonaName));

        using var pumpCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pumpToken = pumpCancellation.Token;
        var pump = Task.Factory.StartNew(() =>
            {
                while (!pumpToken.IsCancellationRequested)
                {
                    try
                    {
                        callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(200));
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(AppLogScopes.App, "Ошибка обработчика событий при входе", ex);
                        Thread.Sleep(300);
                    }
                }
            },
            pumpToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        try
        {
            client.Connect();

            var isConnected = await connected.Task
                .WaitAsync(ConnectTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (!isConnected)
                throw new SteamLoginException("Не удалось подключиться к серверам Steam.");

            var details = new AuthSessionDetails
            {
                Username = username,
                Password = password,
                IsPersistentSession = true,
                GuardData = guardData,
                Authenticator = authenticator,
                DeviceFriendlyName = $"{AppPaths.ProductName} ({Environment.MachineName})",
                PlatformType = EAuthTokenPlatformType.k_EAuthTokenPlatformType_SteamClient,
                ClientOSType = EOSType.Win11
            };

            var session = await client.Authentication
                .BeginAuthSessionViaCredentialsAsync(details)
                .ConfigureAwait(false);

            var poll = await session.PollingWaitForResultAsync(cancellationToken).ConfigureAwait(false);

            var accountName = string.IsNullOrWhiteSpace(poll.AccountName) ? username : poll.AccountName;
            var steamId = session.SteamID?.ConvertToUInt64() ?? 0UL;
            _logger.Success(accountName, "Аккаунт авторизован");

            // Sign in on this very connection to pick up the persona name and the
            // library while we are already here.
            var (persona, library) = await ReadAccountDetailsAsync(
                client, steamUser, accountName, poll.RefreshToken, steamId,
                signedOn.Task, personaName.Task, cancellationToken).ConfigureAwait(false);

            return new LoginResult(
                accountName,
                steamId,
                poll.RefreshToken,
                string.IsNullOrWhiteSpace(poll.NewGuardData) ? guardData : poll.NewGuardData,
                persona,
                library);
        }
        catch (AuthenticationException ex)
        {
            _logger.Error(username, $"Steam отклонил вход ({ex.Result}): {ex.Message}");
            throw new SteamLoginException(Describe(ex.Result), ex);
        }
        catch (TimeoutException ex)
        {
            throw new SteamLoginException("Steam не ответил вовремя. Попробуйте ещё раз.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new SteamLoginException("Нет связи с серверами Steam.", ex);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotImplementedException)
        {
            // SteamKit raises these when the account offers no confirmation method
            // this app can drive — for example only a QR-code login.
            _logger.Error(username, "Неподдерживаемый способ подтверждения входа", ex);
            throw new SteamLoginException(
                "Этот аккаунт требует способ подтверждения, который программа не умеет. " +
                "Попробуйте войти кодом из приложения Steam.", ex);
        }
        finally
        {
            pumpCancellation.Cancel();

            try
            {
                client.Disconnect();
            }
            catch (InvalidOperationException)
            {
                // Already torn down.
            }

            try
            {
                await pump.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }
        }
    }

    /// <summary>
    /// Best effort: a failure here must not cost the user their sign-in, so the token
    /// is returned regardless and the library simply stays empty.
    /// </summary>
    private async Task<(string? Persona, IReadOnlyList<OwnedGame> Library)> ReadAccountDetailsAsync(
        SteamClient client,
        SteamUser steamUser,
        string accountName,
        string refreshToken,
        ulong steamId,
        Task<SteamUser.LoggedOnCallback> signedOn,
        Task<string> personaName,
        CancellationToken cancellationToken)
    {
        try
        {
            steamUser.LogOn(new SteamUser.LogOnDetails
            {
                Username = accountName,
                AccessToken = refreshToken,
                ShouldRememberPassword = true,
                LoginID = SteamLoginIds.For(accountName),
                ClientOSType = EOSType.Win11,
                MachineName = AppPaths.ProductName
            });

            var logon = await signedOn.WaitAsync(SignOnTimeout, cancellationToken).ConfigureAwait(false);
            if (logon.Result != EResult.OK)
            {
                _logger.Warn(accountName, $"Не удалось прочитать библиотеку: вход вернул {logon.Result}");
                return (null, Array.Empty<OwnedGame>());
            }

            var library = await SteamLibrary
                .FetchAsync(client, steamId, cancellationToken)
                .ConfigureAwait(false);

            _logger.Info(accountName, $"Библиотека прочитана: игр {library.Count}");

            // AccountInfo lands moments after the logon; give it a beat.
            string? persona = null;
            try
            {
                persona = await personaName.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The boost session will pick the persona up on its next sign-in.
            }

            steamUser.LogOff();
            return (persona, library);
        }
        catch (Exception ex) when (ex is SteamLibraryException or TimeoutException
                                       or OperationCanceledException or AsyncJobFailedException
                                       or InvalidOperationException)
        {
            _logger.Warn(accountName, $"Библиотеку прочитать не удалось: {ex.Message}");
            return (null, Array.Empty<OwnedGame>());
        }
    }

    private static string Describe(EResult result) => result switch
    {
        EResult.InvalidPassword => "Неверный логин или пароль.",

        // Steam revokes a saved token when it is used from a different device.
        EResult.AccessDenied or EResult.Revoked =>
            "Сохранённый вход больше не действителен — Steam его отозвал. Войдите заново.",

        // Steam uses FileNotFound for "this login request no longer exists":
        // it was declined in the mobile app, or nobody confirmed it in time.
        EResult.FileNotFound or EResult.Expired =>
            "Запрос на вход не подтверждён: его отклонили в приложении Steam или он истёк. " +
            "Нажмите «Войти» ещё раз и подтвердите вход на телефоне в течение пары минут.",

        EResult.AccountLoginDeniedThrottle or EResult.RateLimitExceeded =>
            "Слишком много попыток входа. Подождите ~30 минут.",
        EResult.AccountDisabled => "Аккаунт отключён.",
        EResult.AccountLockedDown => "Аккаунт заблокирован.",
        EResult.TwoFactorCodeMismatch or EResult.InvalidLoginAuthCode =>
            "Неверный код Steam Guard.",
        EResult.Timeout or EResult.ServiceUnavailable or EResult.TryAnotherCM =>
            "Серверы Steam не отвечают. Попробуйте через минуту.",
        _ => $"Steam отклонил вход: {result}."
    };
}

public sealed class SteamLoginException : Exception
{
    public SteamLoginException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A stable, non-zero login id per account. Steam uses it to tell instances apart,
/// which is what lets this app coexist with the user's own Steam client.
/// </summary>
public static class SteamLoginIds
{
    public static uint For(string username)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var character in username)
            {
                hash ^= character;
                hash *= 16777619u;
            }

            return hash | 0x8000_0000u;
        }
    }
}
