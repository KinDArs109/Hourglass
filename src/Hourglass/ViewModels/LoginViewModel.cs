using System.Windows.Input;
using Hourglass.Services;
using Hourglass.Services.Interfaces;
using SteamKit2.Authentication;

namespace Hourglass.ViewModels;

public enum LoginStep
{
    Credentials,
    Working,
    Guard
}

public enum GuardMode
{
    None,

    /// <summary>Steam is waiting for the user to approve the sign-in in the mobile app.</summary>
    Choice,

    /// <summary>A code from the Steam mobile authenticator.</summary>
    DeviceCode,

    /// <summary>A code emailed to the account address.</summary>
    EmailCode
}

/// <summary>
/// Drives the sign-in window and doubles as the SteamKit authenticator, turning
/// Steam's Guard callbacks into UI prompts.
/// </summary>
public sealed class LoginViewModel : ViewModelBase, IAuthenticator
{
    private const string DefaultWorkingText = "Связываемся со Steam…";

    private readonly SteamLoginService _loginService;
    private readonly IAppLogger _logger;
    private readonly string? _guardData;

    /// <summary>Sign in the same way the account will boost, or Steam sees two addresses.</summary>
    private readonly Uri? _proxy;

    /// <summary>Same reason: the sign-in must take the route the boost will take.</summary>
    private readonly bool _webSocketOnly;

    private readonly object _promptGate = new();
    private TaskCompletionSource<string>? _codePrompt;
    private TaskCompletionSource<bool>? _choicePrompt;

    private CancellationTokenSource? _cts;

    private string _username = "";
    private string _guardCode = "";
    private string _errorMessage = "";
    private string _guardHint = "";
    private string _workingText = DefaultWorkingText;
    private string _emailDomain = "";
    private LoginStep _step = LoginStep.Credentials;
    private GuardMode _guardMode = GuardMode.None;
    private bool _isUsernameLocked;

    public LoginViewModel(
        SteamLoginService loginService,
        IAppLogger logger,
        string? presetUsername,
        string? guardData,
        Uri? proxy,
        bool webSocketOnly)
    {
        _loginService = loginService;
        _logger = logger;
        _guardData = guardData;
        _proxy = proxy;
        _webSocketOnly = webSocketOnly;

        _username = presetUsername ?? "";
        _isUsernameLocked = !string.IsNullOrEmpty(presetUsername);

        SubmitGuardCommand = new RelayCommand(_ => SubmitGuardCode(), _ => GuardCode.Trim().Length > 0);
        UseCodeInsteadCommand = new RelayCommand(_ => ResolveChoice(false));
        ConfirmedInAppCommand = new RelayCommand(_ => ResolveChoice(true));
        CancelCommand = new RelayCommand(_ => Cancel());
    }

    /// <summary>Fires with the result on success, or null when the user gave up.</summary>
    public event EventHandler<LoginResult?>? Completed;

    public ICommand SubmitGuardCommand { get; }
    public ICommand UseCodeInsteadCommand { get; }
    public ICommand ConfirmedInAppCommand { get; }
    public ICommand CancelCommand { get; }

    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value);
    }

    public bool IsUsernameLocked
    {
        get => _isUsernameLocked;
        private set => SetProperty(ref _isUsernameLocked, value);
    }

    public string GuardCode
    {
        get => _guardCode;
        set => SetProperty(ref _guardCode, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => ErrorMessage.Length > 0;

    public string GuardHint
    {
        get => _guardHint;
        private set => SetProperty(ref _guardHint, value);
    }

    /// <summary>Caption under the spinner; changes once we are waiting on the phone.</summary>
    public string WorkingText
    {
        get => _workingText;
        private set => SetProperty(ref _workingText, value);
    }

    public string EmailDomain
    {
        get => _emailDomain;
        private set => SetProperty(ref _emailDomain, value);
    }

    public LoginStep Step
    {
        get => _step;
        private set
        {
            if (!SetProperty(ref _step, value))
                return;

            OnPropertyChanged(nameof(IsCredentialsStep));
            OnPropertyChanged(nameof(IsWorkingStep));
            OnPropertyChanged(nameof(IsGuardStep));
        }
    }

    public bool IsCredentialsStep => Step == LoginStep.Credentials;
    public bool IsWorkingStep => Step == LoginStep.Working;
    public bool IsGuardStep => Step == LoginStep.Guard;

    public GuardMode GuardMode
    {
        get => _guardMode;
        private set
        {
            if (!SetProperty(ref _guardMode, value))
                return;

            OnPropertyChanged(nameof(IsChoicePrompt));
            OnPropertyChanged(nameof(IsCodePrompt));
        }
    }

    public bool IsChoicePrompt => GuardMode == GuardMode.Choice;
    public bool IsCodePrompt => GuardMode is GuardMode.DeviceCode or GuardMode.EmailCode;

    public async Task SubmitAsync(string password)
    {
        if (Step != LoginStep.Credentials)
            return;

        var username = Username.Trim();
        if (username.Length == 0)
        {
            ErrorMessage = "Введите логин Steam.";
            return;
        }

        if (password.Length == 0)
        {
            ErrorMessage = "Введите пароль.";
            return;
        }

        ErrorMessage = "";
        WorkingText = DefaultWorkingText;
        Step = LoginStep.Working;
        _cts = new CancellationTokenSource();

        try
        {
            var result = await _loginService
                .SignInAsync(username, password, _guardData, _proxy, _webSocketOnly, this, _cts.Token)
                .ConfigureAwait(true);

            Completed?.Invoke(this, result);
        }
        catch (OperationCanceledException)
        {
            Completed?.Invoke(this, null);
        }
        catch (SteamLoginException ex)
        {
            Fail(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.Error(username, "Неожиданная ошибка входа", ex);
            Fail("Непредвиденная ошибка: " + ex.Message);
        }
    }

    public void Cancel()
    {
        _cts?.Cancel();

        lock (_promptGate)
        {
            _codePrompt?.TrySetCanceled();
            _choicePrompt?.TrySetCanceled();
        }

        Completed?.Invoke(this, null);
    }

    // ------------------------------------------------------------ IAuthenticator

    Task<string> IAuthenticator.GetDeviceCodeAsync(bool previousCodeWasIncorrect) =>
        RequestCodeAsync(GuardMode.DeviceCode, null, previousCodeWasIncorrect);

    Task<string> IAuthenticator.GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) =>
        RequestCodeAsync(GuardMode.EmailCode, email, previousCodeWasIncorrect);

    Task<bool> IAuthenticator.AcceptDeviceConfirmationAsync()
    {
        var prompt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_promptGate)
            _choicePrompt = prompt;

        Post(() =>
        {
            ErrorMessage = "";
            GuardMode = GuardMode.Choice;
            GuardHint = "Откройте приложение Steam на телефоне — там появился запрос на вход. " +
                        "Сначала подтвердите его там, и только потом нажмите кнопку ниже. " +
                        "На это есть пара минут, иначе запрос истечёт.";
            Step = LoginStep.Guard;
        });

        return prompt.Task;
    }

    private Task<string> RequestCodeAsync(GuardMode mode, string? email, bool previousCodeWasIncorrect)
    {
        var prompt = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_promptGate)
            _codePrompt = prompt;

        Post(() =>
        {
            ErrorMessage = previousCodeWasIncorrect ? "Код неверный, попробуйте ещё раз." : "";
            GuardCode = "";
            GuardMode = mode;
            EmailDomain = email ?? "";
            GuardHint = mode == GuardMode.EmailCode
                ? $"Введите код, отправленный на {email}."
                : "Введите код из приложения Steam Guard.";
            Step = LoginStep.Guard;
        });

        return prompt.Task;
    }

    private void SubmitGuardCode()
    {
        var code = GuardCode.Trim();
        if (code.Length == 0)
            return;

        TaskCompletionSource<string>? prompt;
        lock (_promptGate)
        {
            prompt = _codePrompt;
            _codePrompt = null;
        }

        if (prompt is null)
            return;

        WorkingText = "Проверяем код…";
        GuardMode = GuardMode.None;
        Step = LoginStep.Working;
        prompt.TrySetResult(code);
    }

    private void ResolveChoice(bool waitForMobileApproval)
    {
        TaskCompletionSource<bool>? prompt;
        lock (_promptGate)
        {
            prompt = _choicePrompt;
            _choicePrompt = null;
        }

        if (prompt is null)
            return;

        if (waitForMobileApproval)
        {
            WorkingText = "Проверяем подтверждение в приложении Steam…";
            GuardMode = GuardMode.None;
            Step = LoginStep.Working;
        }

        prompt.TrySetResult(waitForMobileApproval);
    }

    private void Fail(string message)
    {
        ErrorMessage = message;
        WorkingText = DefaultWorkingText;
        GuardMode = GuardMode.None;
        Step = LoginStep.Credentials;
    }

    private static void Post(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.InvokeAsync(action);
    }
}
