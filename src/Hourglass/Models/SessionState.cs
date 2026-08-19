namespace Hourglass.Models;

public enum SessionState
{
    /// <summary>Not running; the user has not started it or has stopped it.</summary>
    Stopped,

    Connecting,
    SigningIn,

    /// <summary>Signed in and reporting the configured games as being played.</summary>
    Boosting,

    /// <summary>Deliberately standing down so the owner can use the account.</summary>
    Paused,

    /// <summary>Waiting out a backoff before the next connection attempt.</summary>
    Reconnecting,

    /// <summary>The stored refresh token is gone or rejected; a fresh sign-in is required.</summary>
    NeedsLogin,

    /// <summary>Stopped on an error that retrying will not fix.</summary>
    Failed
}
