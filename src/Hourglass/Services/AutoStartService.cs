using Hourglass.Services.Interfaces;
using Microsoft.Win32;

namespace Hourglass.Services;

/// <summary>Registers the app in the per-user Run key so it can start with Windows.</summary>
public sealed class AutoStartService
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    private readonly IAppLogger _logger;

    public AutoStartService(IAppLogger logger)
    {
        _logger = logger;
    }

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(AppPaths.ProductName) is string value && value.Length > 0;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                _logger.Warn(AppLogScopes.App, $"Нет доступа к автозапуску: {ex.Message}");
                return false;
            }
        }
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
                return;

            if (!enabled)
            {
                key.DeleteValue(AppPaths.ProductName, throwOnMissingValue: false);
                return;
            }

            var executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable))
            {
                _logger.Warn(AppLogScopes.App, "Не удалось определить путь к программе для автозапуска");
                return;
            }

            key.SetValue(AppPaths.ProductName, $"\"{executable}\" --minimized");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logger.Error(AppLogScopes.App, "Не удалось изменить автозапуск", ex);
        }
    }
}
