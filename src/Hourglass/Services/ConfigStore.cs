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

    /// <summary>
    /// Writes a copy of the settings for another machine or a rainy day. Sign-in tokens
    /// are deliberately left out: Windows ties them to this user on this machine, so they
    /// would be useless in the copy and are a secret not worth scattering into files.
    /// </summary>
    public bool Export(string path)
    {
        lock (_gate)
        {
            try
            {
                var copy = JsonSerializer.Deserialize<AppConfig>(
                    JsonSerializer.Serialize(Config, SerializerOptions), SerializerOptions);

                if (copy is null)
                    return false;

                foreach (var account in copy.Accounts)
                {
                    account.ProtectedRefreshToken = null;
                    account.ProtectedGuardData = null;
                    account.ProtectedProxy = null;
                }

                copy.Telegram.ProtectedToken = null;
                copy.Telegram.ProtectedProxy = null;

                File.WriteAllText(path, JsonSerializer.Serialize(copy, SerializerOptions));
                _logger.Success(AppLogScopes.App, $"Копия настроек сохранена: {Path.GetFileName(path)}");
                return true;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _logger.Error(AppLogScopes.App, "Не удалось сохранить копию настроек", ex);
                return false;
            }
        }
    }

    /// <summary>
    /// Replaces the settings with the ones in the file. Sign-ins already on this machine
    /// are carried across by login name, so restoring a copy does not log anything out.
    /// </summary>
    public bool Import(string path)
    {
        lock (_gate)
        {
            try
            {
                var incoming = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), SerializerOptions);

                // A file with no accounts is either the wrong file or a broken one, and
                // swallowing it would silently wipe everything.
                if (incoming is null || incoming.Accounts.Count == 0)
                    return false;

                Normalize(incoming);

                var known = Config.Accounts.ToDictionary(
                    account => account.Username, StringComparer.OrdinalIgnoreCase);

                foreach (var account in incoming.Accounts)
                {
                    if (!known.TryGetValue(account.Username, out var existing))
                        continue;

                    account.ProtectedRefreshToken ??= existing.ProtectedRefreshToken;
                    account.ProtectedGuardData ??= existing.ProtectedGuardData;
                    account.ProtectedProxy ??= existing.ProtectedProxy;
                }

                incoming.Telegram.ProtectedToken ??= Config.Telegram.ProtectedToken;
                incoming.Telegram.ProtectedProxy ??= Config.Telegram.ProtectedProxy;

                // Left unsaved on purpose: the caller has to stop the sessions and swap
                // the account list over first, and only then is the new state worth
                // writing down.
                Config = incoming;
                return true;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _logger.Error(AppLogScopes.App, $"Не удалось прочитать {Path.GetFileName(path)}", ex);
                return false;
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
