namespace Hourglass.Services.Interfaces;

/// <summary>
/// Whole-app operations on the saved data, for the settings window. Separate from
/// <see cref="IBoostController"/> on purpose: the Telegram bot has no business
/// wiping counters or swapping the configuration out from under itself.
/// </summary>
public interface IAccountDataManager
{
    /// <summary>Zeroes every counter on every account. Games and settings stay put.</summary>
    void ResetCounters();

    /// <summary>Writes a copy of the settings. Sign-in tokens are left out.</summary>
    bool ExportSettings(string path);

    /// <summary>
    /// Replaces the settings with the ones in the file and rebuilds the account list.
    /// Everything running is stopped first.
    /// </summary>
    Task<bool> ImportSettingsAsync(string path);
}
