using System.Net.Http;
using System.Windows.Input;
using Hourglass.Services;
using Hourglass.Services.Interfaces;
using Hourglass.Utilities;

namespace Hourglass.ViewModels;

/// <summary>Backs the settings window. Currently everything here is about the Telegram bot.</summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly IConfigStore _store;
    private readonly TelegramBotService _telegram;
    private readonly IBoostController _controller;

    private string _token;
    private string _statusMessage = "";
    private bool _isBusy;
    private bool _isStatusBad;

    public SettingsViewModel(IConfigStore store, TelegramBotService telegram, IBoostController controller)
    {
        _store = store;
        _telegram = telegram;
        _controller = controller;

        _token = SecretProtector.Unprotect(store.Config.Telegram.ProtectedToken) ?? "";

        SaveTokenCommand = new AsyncRelayCommand(_ => SaveTokenAsync(), _ => Token.Trim().Length > 0 && !IsBusy);
        UnpairCommand = new RelayCommand(_ => Unpair(), _ => IsPaired);
        SendTestCommand = new RelayCommand(_ => SendTest(), _ => IsPaired);

        _telegram.StateChanged += OnBotStateChanged;
    }

    public ICommand SaveTokenCommand { get; }
    public ICommand UnpairCommand { get; }
    public ICommand SendTestCommand { get; }

    public string Token
    {
        get => _token;
        set => SetProperty(ref _token, value);
    }

    public bool TelegramEnabled
    {
        get => _store.Config.Telegram.IsEnabled;
        set
        {
            if (_store.Config.Telegram.IsEnabled == value)
                return;

            _store.Config.Telegram.IsEnabled = value;
            _store.Save();
            OnPropertyChanged();

            AsyncHelper.FireAndForget(RestartBotAsync, nameof(TelegramEnabled));
        }
    }

    public bool NotifyProblems
    {
        get => _store.Config.Telegram.NotifyProblems;
        set
        {
            if (_store.Config.Telegram.NotifyProblems == value)
                return;

            _store.Config.Telegram.NotifyProblems = value;
            _store.Save();
            OnPropertyChanged();
        }
    }

    public bool IsPaired => _store.Config.Telegram.ChatId != 0;

    public string PairingInstruction => IsPaired
        ? "Чат привязан. Команды: /status, /run, /stop."
        : $"Отправьте боту в Telegram: /link {_telegram.PairingCode}";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
                OnPropertyChanged(nameof(HasStatusMessage));
        }
    }

    public bool HasStatusMessage => StatusMessage.Length > 0;

    public bool IsStatusBad
    {
        get => _isStatusBad;
        private set => SetProperty(ref _isStatusBad, value);
    }

    public void Detach() => _telegram.StateChanged -= OnBotStateChanged;

    private async Task SaveTokenAsync()
    {
        var token = Token.Trim();
        IsBusy = true;
        SetStatus("Проверяем токен…", isBad: false);

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var botName = await _telegram.VerifyTokenAsync(token, timeout.Token).ConfigureAwait(true);

            _store.Config.Telegram.ProtectedToken = SecretProtector.Protect(token);
            _store.Config.Telegram.IsEnabled = true;
            _store.Save();

            OnPropertyChanged(nameof(TelegramEnabled));
            await RestartBotAsync().ConfigureAwait(true);

            SetStatus($"Бот {botName} на связи. {PairingInstruction}", isBad: false);
        }
        catch (TelegramException ex)
        {
            SetStatus(ex.Message, isBad: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            SetStatus("Не удалось связаться с Telegram. Проверьте интернет.", isBad: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Unpair()
    {
        _store.Config.Telegram.ChatId = 0;
        _store.Save();
        OnPropertyChanged(nameof(IsPaired));
        OnPropertyChanged(nameof(PairingInstruction));
        SetStatus("Чат отвязан. Привяжите заново кодом выше.", isBad: false);
    }

    private void SendTest()
    {
        _telegram.Notify("Проверка связи из Hourglass — всё работает.");
        SetStatus("Сообщение отправлено.", isBad: false);
    }

    private Task RestartBotAsync() => _telegram.RestartAsync(_controller);

    private void OnBotStateChanged(object? sender, EventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            RaisePairing();
        else
            dispatcher.InvokeAsync(RaisePairing);
    }

    private void RaisePairing()
    {
        OnPropertyChanged(nameof(IsPaired));
        OnPropertyChanged(nameof(PairingInstruction));
        CommandManager.InvalidateRequerySuggested();
    }

    private void SetStatus(string message, bool isBad)
    {
        IsStatusBad = isBad;
        StatusMessage = message;
    }
}
