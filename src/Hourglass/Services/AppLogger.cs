using System.Diagnostics;
using System.IO;
using Hourglass.Models;
using Hourglass.Services.Interfaces;

namespace Hourglass.Services;

public sealed class AppLogger : IAppLogger
{
    private const long MaxLogBytes = 4 * 1024 * 1024;

    private readonly object _fileGate = new();
    private readonly string _logPath;

    public AppLogger()
    {
        var directory = AppPaths.DataDirectory;
        Directory.CreateDirectory(directory);
        _logPath = Path.Combine(directory, "hourglass.log");
        RotateIfNeeded(directory);
    }

    public event EventHandler<AppLogEventArgs>? EntryWritten;

    public void Info(string scope, string message) => Write(scope, LogLevel.Info, message);

    public void Success(string scope, string message) => Write(scope, LogLevel.Success, message);

    public void Warn(string scope, string message) => Write(scope, LogLevel.Warning, message);

    public void Error(string scope, string message, Exception? exception = null) =>
        Write(scope, LogLevel.Error, exception is null ? message : $"{message} — {exception.Message}");

    private void Write(string scope, LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);

        var line = $"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss}] [{level,-7}] [{scope}] {message}";
        lock (_fileGate)
        {
            try
            {
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
            catch (IOException ex)
            {
                Debug.WriteLine($"[AppLogger] could not write log: {ex.Message}");
            }
        }

        Debug.WriteLine(line);
        EntryWritten?.Invoke(this, new AppLogEventArgs(scope, entry));
    }

    private void RotateIfNeeded(string directory)
    {
        SafeExecRotate(() =>
        {
            var info = new FileInfo(_logPath);
            if (!info.Exists || info.Length <= MaxLogBytes)
                return;

            var backup = Path.Combine(directory, "hourglass.log.old");
            if (File.Exists(backup))
                File.Delete(backup);
            File.Move(_logPath, backup);
        });
    }

    private static void SafeExecRotate(Action action)
    {
        try
        {
            action();
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"[AppLogger] rotation failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Debug.WriteLine($"[AppLogger] rotation denied: {ex.Message}");
        }
    }
}
