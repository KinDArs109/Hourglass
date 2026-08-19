using Hourglass.Models;

namespace Hourglass.Services.Interfaces;

public interface IAppLogger
{
    /// <summary>Raised for every entry. May fire on any thread.</summary>
    event EventHandler<AppLogEventArgs>? EntryWritten;

    void Info(string scope, string message);
    void Success(string scope, string message);
    void Warn(string scope, string message);
    void Error(string scope, string message, Exception? exception = null);
}

public sealed class AppLogEventArgs : EventArgs
{
    public AppLogEventArgs(string scope, LogEntry entry)
    {
        Scope = scope;
        Entry = entry;
    }

    /// <summary>Account name the entry belongs to, or <see cref="AppLogScopes.App"/>.</summary>
    public string Scope { get; }

    public LogEntry Entry { get; }
}

public static class AppLogScopes
{
    public const string App = "@app";
}
