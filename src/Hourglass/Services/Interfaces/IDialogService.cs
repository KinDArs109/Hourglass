using Hourglass.Models;
using Hourglass.ViewModels;

namespace Hourglass.Services.Interfaces;

public interface IDialogService
{
    /// <summary>Runs the sign-in window. Returns null when the user cancels.</summary>
    Task<LoginResult?> ShowLoginAsync(string? presetUsername, string? guardData);

    /// <summary>Runs the game picker. Returns null when the user cancels.</summary>
    Task<IReadOnlyList<GameConfig>?> ShowGamePickerAsync(AccountViewModel account);

    void ShowSettings(IBoostController controller);

    /// <summary>Opens the journal for one account in its own window.</summary>
    void ShowLog(AccountViewModel account);

    bool Confirm(string title, string message);

    void Error(string title, string message);
}
