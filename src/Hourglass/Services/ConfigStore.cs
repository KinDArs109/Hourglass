using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hourglass.Models;
using Hourglass.Services.Interfaces;
using Hourglass.Utilities;

namespace Hourglass.Services;

public sealed class ConfigStore : IConfigStore, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IAppLogger _logger;
    private readonly object _gate = new();
    private readonly System.Timers.Timer _deferredTimer;

    private static string BackupFile => AppPaths.ConfigFile + ".bak";

    public ConfigStore(IAppLogger logger)
    {
        _logger = logger;
        _deferredTimer = new System.Timers.Timer(5000) { AutoReset = false };
        _deferredTimer.Elapsed += (_, _) => Save();
    }

    public AppConfig Config { get; private set; } = new();

    public void Load()
    {
        lock (_gate)
        {
            // Losing this file means losing every saved sign-in, so a damaged main
            // file falls back to the copy kept by the previous successful save.
            if (TryLoadFrom(AppPaths.ConfigFile) || TryLoadFrom(BackupFile))
            {
                _logger.Info(AppLogScopes.App, $"Загружено аккаунтов: {Config.Accounts.Count}");
                return;
            }

            Config = new AppConfig();
        }
    }

    private bool TryLoadFrom(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;

            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions);
            if (loaded is null)
                return false;

            Normalize(loaded);
            Config = loaded;

            if (path == BackupFile)
                _logger.Warn(AppLogScopes.App, "Основной файл настроек повреждён — взята резервная копия");

            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.Error(AppLogScopes.App, $"Не удалось прочитать {Path.GetFileName(path)}", ex);
            return false;
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);

                // Write beside the target and swap, so a crash mid-write cannot
                // leave a truncated config behind. The swap also keeps the previous
                // good version as a backup.
                var temporary = AppPaths.ConfigFile + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(Config, SerializerOptions));

                if (File.Exists(AppPaths.ConfigFile))
                    File.Replace(temporary, AppPaths.ConfigFile, BackupFile);
                else
                    File.Move(temporary, AppPaths.ConfigFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Error(AppLogScopes.App, "Не удалось сохранить настройки", ex);
            }
        }
    }

    public void SaveDeferred()
    {
        if (_deferredTimer.Enabled)
            return;

        _deferredTimer.Start();
    }

    public void Dispose()
    {
        _deferredTimer.Stop();
        _deferredTimer.Dispose();
        Save();
    }

    private static void Normalize(AppConfig config)
    {
        config.Accounts.RemoveAll(account => string.IsNullOrWhiteSpace(account.Username));

        foreach (var account in config.Accounts)
        {
            if (string.IsNullOrWhiteSpace(account.DisplayName))
                account.DisplayName = account.Username;

            account.Games.RemoveAll(game => game.AppId == 0);

            // Duplicate app ids would be sent twice and waste one of the 32 slots.
            var seen = new HashSet<uint>();
            account.Games.RemoveAll(game => !seen.Add(game.AppId));

            // Backfill Steam's own playtime for games chosen before it was recorded.
            var libraryPlaytime = account.Library
                .GroupBy(entry => entry.AppId)
                .ToDictionary(group => group.Key, group => group.First().PlaytimeMinutes);

            foreach (var game in account.Games)
            {
                if (game.SteamMinutes == 0 && libraryPlaytime.TryGetValue(game.AppId, out var minutes))
                    game.SteamMinutes = minutes;
            }
        }
    }
}
