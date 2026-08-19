using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using Hourglass.Services.Interfaces;

namespace Hourglass.Services;

public sealed record UpdateInfo(Version Version, string Tag, string Notes, string DownloadUrl, long Size);

/// <summary>
/// Checks the project's own GitHub releases and swaps the executable in place.
///
/// Only that one repository over HTTPS is ever consulted, and the downloaded file has
/// to look like a Windows executable of the advertised size before it is allowed to
/// replace anything.
/// </summary>
public sealed class UpdateService
{
    private const string Repository = "KinDArs109/Hourglass";
    private const string AssetName = "Hourglass.exe";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAppLogger _logger;

    public UpdateService(IHttpClientFactory httpClientFactory, IAppLogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public static Version CurrentVersion { get; } =
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);

    public static string CurrentVersionText => $"{CurrentVersion.Major}.{CurrentVersion.Minor}";

    /// <summary>Null when there is nothing newer, or when GitHub could not be reached.</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient(HttpClients.GitHub);
            var release = await client
                .GetFromJsonAsync<GitHubRelease>(
                    $"https://api.github.com/repos/{Repository}/releases/latest", cancellationToken)
                .ConfigureAwait(false);

            if (release?.TagName is not { Length: > 0 } tag || release.Draft)
                return null;

            if (!TryParseVersion(tag, out var version) || version <= CurrentVersion)
                return null;

            var asset = release.Assets?.FirstOrDefault(item =>
                string.Equals(item.Name, AssetName, StringComparison.OrdinalIgnoreCase));

            if (asset?.DownloadUrl is not { Length: > 0 })
                return null;

            return new UpdateInfo(version, tag, release.Body ?? "", asset.DownloadUrl, asset.Size);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NotSupportedException)
        {
            _logger.Warn(AppLogScopes.App, $"Проверка обновлений не удалась: {ex.Message}");
            return null;
        }
    }

    /// <summary>Downloads the new build to a temporary file and returns its path.</summary>
    public async Task<string> DownloadAsync(
        UpdateInfo update, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var target = Path.Combine(Path.GetTempPath(), $"Hourglass-{update.Tag}.exe");

        using var client = _httpClientFactory.CreateClient(HttpClients.GitHub);
        using var response = await client
            .GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? update.Size;

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var destination = File.Create(target))
        {
            var buffer = new byte[128 * 1024];
            long copied = 0;
            int read;

            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied += read;

                if (total > 0)
                    progress?.Report(Math.Clamp((double)copied / total, 0d, 1d));
            }
        }

        Verify(target, update.Size);
        return target;
    }

    /// <summary>
    /// Hands the swap to a throwaway script: a running executable cannot overwrite
    /// itself, so the script waits for this process to exit, replaces the file and
    /// starts the new build.
    /// </summary>
    public void ApplyAndRestart(string downloadedPath)
    {
        var current = Environment.ProcessPath
                      ?? throw new UpdateException("Не удалось определить путь к программе.");

        var scriptPath = Path.Combine(Path.GetTempPath(), $"hourglass-update-{Environment.ProcessId}.cmd");
        var script = $"""
            @echo off
            :wait
            tasklist /FI "PID eq {Environment.ProcessId}" 2>nul | find "{Environment.ProcessId}" >nul
            if not errorlevel 1 (
                ping -n 2 127.0.0.1 >nul
                goto wait
            )
            copy /y "{downloadedPath}" "{current}" >nul
            if errorlevel 1 goto done
            del /q "{downloadedPath}" >nul 2>&1
            start "" "{current}"
            :done
            del /q "%~f0" >nul 2>&1
            """;

        File.WriteAllText(scriptPath, script);

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{scriptPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false
        });

        _logger.Info(AppLogScopes.App, "Обновление скачано, программа перезапустится");
    }

    /// <summary>Refuses anything that is not a plausible Windows executable.</summary>
    private static void Verify(string path, long expectedSize)
    {
        var info = new FileInfo(path);

        if (!info.Exists || info.Length == 0)
            throw new UpdateException("Файл обновления не скачался.");

        if (expectedSize > 0 && info.Length != expectedSize)
            throw new UpdateException("Размер файла не совпал с заявленным — обновление отменено.");

        using var stream = File.OpenRead(path);
        var header = new byte[2];

        if (stream.Read(header, 0, 2) != 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
            throw new UpdateException("Скачанный файл не похож на программу — обновление отменено.");
    }

    private static bool TryParseVersion(string tag, out Version version)
    {
        var cleaned = tag.TrimStart('v', 'V');

        // "1.1" alone is not a Version, so it is padded out.
        if (cleaned.Count(character => character == '.') == 1)
            cleaned += ".0";

        return Version.TryParse(cleaned, out version!);
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? DownloadUrl { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}

public sealed class UpdateException : Exception
{
    public UpdateException(string message) : base(message)
    {
    }
}
