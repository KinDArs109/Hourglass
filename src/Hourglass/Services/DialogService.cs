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

    public DialogService(
        SteamLoginService loginService,
        IAppLogger logger,
        IConfigStore store,
        TelegramBotService telegram)
    {
        _loginService = loginService;
        _logger = logger;
        _store = store;
        _telegram = telegram;
    }

    public Task<LoginResult?> ShowLoginAsync(string? presetUsername, string? guardData)
    {
        var window = new LoginWindow(new LoginViewModel(_loginService, _logger, presetUsername, guardData))
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

    public void ShowSettings(IBoostController controller)
    {
        var window = new SettingsWindow(new SettingsViewModel(_store, _telegram, controller))
        {
            Owner = ResolveOwner()
        };

        window.ShowDialog();
    }

    public void ShowLog(AccountViewModel account)
    {
        var window = new LogWindow(account) { Owner = ResolveOwner() };
        window.ShowDialog();
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
