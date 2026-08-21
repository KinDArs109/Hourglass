using System.Windows;
using Hourglass.Models;
using Hourglass.Services.Interfaces;
using Hourglass.ViewModels;
using Hourglass.Views;

namespace Hourglass.Services;

public sealed class DialogService : IDialogService
{
    private readonly SteamLoginService _loginService;
    private readonly IAppLogger _logger;
    private readonly IConfigStore _store;
    private readonly TelegramBotService _telegram;
    private readonly UpdateService _updates;

    public DialogService(
        SteamLoginService loginService,
        IAppLogger logger,
        IConfigStore store,
        TelegramBotService telegram,
        UpdateService updates)
    {
        _loginService = loginService;
        _logger = logger;
        _store = store;
        _telegram = telegram;
        _updates = updates;
    }

    public Task<LoginResult?> ShowLoginAsync(string? presetUsername, string? guardData, Uri? proxy, bool webSocketOnly)
    {
        var window = new LoginWindow(new LoginViewModel(_loginService, _logger, presetUsername, guardData, proxy, webSocketOnly))
        {
            Owner = ResolveOwner()
        };

        window.ShowDialog();
        return Task.FromResult(window.Result);
    }

    public Task<IReadOnlyList<GameConfig>?> ShowGamePickerAsync(AccountViewModel account)
    {
        var viewModel = new GamePickerViewModel(account);
        var window = new GamePickerWindow(viewModel)
        {
            Owner = ResolveOwner()
        };

        window.ShowDialog();
        return Task.FromResult(window.Result);
    }

    public void ShowSettings(IBoostController controller, IAccountDataManager data)
    {
        var window = new SettingsWindow(new SettingsViewModel(_store, _telegram, controller, data, this))
        {
            Owner = ResolveOwner()
        };

        window.ShowDialog();
    }

    private const string SettingsFilter = "Настройки Hourglass (*.json)|*.json|Все файлы (*.*)|*.*";

    public string? PickSaveFile(string title, string suggestedName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = title,
            FileName = suggestedName,
            DefaultExt = ".json",
            Filter = SettingsFilter,
            AddExtension = true,
            OverwritePrompt = true
        };

        return dialog.ShowDialog(ResolveOwner()) == true ? dialog.FileName : null;
    }

    public string? PickOpenFile(string title)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            DefaultExt = ".json",
            Filter = SettingsFilter,
            CheckFileExists = true
        };

        return dialog.ShowDialog(ResolveOwner()) == true ? dialog.FileName : null;
    }

    public void ShowLog(AccountViewModel account)
    {
        var window = new LogWindow(account) { Owner = ResolveOwner() };
        window.ShowDialog();
    }

    public void ShowProxy(AccountViewModel account)
    {
        var window = new ProxyWindow(account) { Owner = ResolveOwner() };
        window.ShowDialog();
    }

    public bool ShowUpdate(UpdateInfo update)
    {
        var window = new UpdateWindow(new UpdateViewModel(_updates, update))
        {
            Owner = ResolveOwner()
        };

        window.ShowDialog();
        return window.ShouldRestart;
    }

    public bool Confirm(string title, string message) =>
        MessageDialog.Confirm(ResolveOwner(), title, message);

    public void Error(string title, string message) =>
        MessageDialog.Notice(ResolveOwner(), title, message);

    private static Window? ResolveOwner()
    {
        var application = Application.Current;
        if (application is null)
            return null;

        return application.Windows
                   .OfType<Window>()
                   .FirstOrDefault(window => window.IsActive && window.IsVisible)
               ?? (application.MainWindow is { IsVisible: true } main ? main : null);
    }
}
