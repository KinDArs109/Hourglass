namespace Hourglass.Models;

/// <summary>Root of the on-disk configuration file.</summary>
public sealed class AppConfig
{
    public List<AccountConfig> Accounts { get; set; } = new();

    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool LaunchWithWindows { get; set; }

    /// <summary>Yield the account when the local Steam client signs in with it.</summary>
    public bool PauseWhenSteamClientRuns { get; set; } = true;

    public TelegramConfig Telegram { get; set; } = new();
}

public sealed class TelegramConfig
{
    public bool IsEnabled { get; set; }

    /// <summary>DPAPI-protected bot token from BotFather.</summary>
    public string? ProtectedToken { get; set; }

    /// <summary>The one chat allowed to command the app. Zero means not paired yet.</summary>
    public long ChatId { get; set; }

    /// <summary>Send a message when an account needs attention.</summary>
    public bool NotifyProblems { get; set; } = true;
}

public sealed class AccountConfig
{
    /// <summary>Steam login name. Used as the identity key across the app.</summary>
    public string Username { get; set; } = "";

    /// <summary>Persona name, filled in once the account signs in successfully.</summary>
    public string DisplayName { get; set; } = "";

    public ulong SteamId { get; set; }

    /// <summary>DPAPI-protected Steam refresh token. No password is ever stored.</summary>
    public string? ProtectedRefreshToken { get; set; }

    /// <summary>DPAPI-protected Steam Guard machine token, avoids repeat prompts.</summary>
    public string? ProtectedGuardData { get; set; }

    /// <summary>
    /// DPAPI-protected proxy address this account connects through, if any. Protected
    /// because these usually come with a password baked into the address.
    /// </summary>
    public string? ProtectedProxy { get; set; }

    public bool ShowOnline { get; set; } = true;

    /// <summary>Optional free-text status shown instead of the game name.</summary>
    public string? CustomStatus { get; set; }

    /// <summary>Start boosting this account as soon as the app launches.</summary>
    public bool AutoStart { get; set; }

    public long BoostedSeconds { get; set; }

    /// <summary>
    /// Idle whatever still has card drops instead of the hand-picked list.
    /// </summary>
    public bool FarmCards { get; set; }

    /// <summary>Optional time window and daily cap for unattended boosting.</summary>
    public ScheduleConfig Schedule { get; set; } = new();

    /// <summary>Seconds boosted during <see cref="DailyDate"/>, for the daily cap.</summary>
    public long DailySeconds { get; set; }

    /// <summary>Local date the daily counter belongs to, as yyyy-MM-dd.</summary>
    public string? DailyDate { get; set; }

    /// <summary>
    /// Finished days, oldest first, so the history page has something to draw. Today is
    /// not in here — it is still <see cref="DailySeconds"/> until midnight closes it.
    /// </summary>
    public List<DayStat> History { get; set; } = new();

    /// <summary>Games chosen for boosting.</summary>
    public List<GameConfig> Games { get; set; } = new();

    /// <summary>
    /// Last known library, cached at sign-in so the picker has something to show
    /// without opening another Steam session.
    /// </summary>
    public List<LibraryEntry> Library { get; set; } = new();
}

/// <summary>One finished day of boosting, for the history page.</summary>
public sealed class DayStat
{
    /// <summary>Local date, as yyyy-MM-dd.</summary>
    public string Date { get; set; } = "";

    public long Seconds { get; set; }
}

public sealed class LibraryEntry
{
    public uint AppId { get; set; }
    public string Name { get; set; } = "";
    public long PlaytimeMinutes { get; set; }
}

public sealed class GameConfig
{
    public uint AppId { get; set; }
    public string Name { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public long BoostedSeconds { get; set; }

    /// <summary>Stop boosting this game after this many hours. Zero means no goal.</summary>
    public int GoalHours { get; set; }

    /// <summary>
    /// Total playtime Steam reported for this game at the last sign-in. Steam commits
    /// playtime in chunks, so this trails the app's own counter for a while.
    /// </summary>
    public long SteamMinutes { get; set; }
}

public sealed class ScheduleConfig
{
    public bool IsEnabled { get; set; }

    /// <summary>Minutes past local midnight. Start == End means the whole day.</summary>
    public int StartMinute { get; set; }

    public int EndMinute { get; set; }

    /// <summary>Hours per day after which the account stands down. Zero means no cap.</summary>
    public int DailyLimitHours { get; set; }

    /// <summary>
    /// Shifts the window start by up to this many minutes, differently each day, so the
    /// account does not connect at exactly the same second forever.
    /// </summary>
    public int JitterMinutes { get; set; } = 10;
}
