namespace Hourglass.Models;

public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error
}

public sealed record LogEntry(DateTime Timestamp, LogLevel Level, string Message)
{
    public string TimeText => Timestamp.ToString("HH:mm:ss");
}
