using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hourglass.Services.Interfaces;

namespace Hourglass.Services;

/// <summary>
/// Fetches Steam store capsules and keeps them on disk, so the game lists show art
/// immediately on later runs and stay quiet when there is no network.
///
/// WPF's own remote image loading is bypassed on purpose: it gives no way to cache,
/// retry or fail silently, and a missing capsule must never disturb the UI.
/// </summary>
public sealed class CapsuleCache
{
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppPaths.ProductName, "capsules");

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAppLogger _logger;
    private readonly ConcurrentDictionary<uint, Task<ImageSource?>> _requests = new();

    public CapsuleCache(IHttpClientFactory httpClientFactory, IAppLogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public Task<ImageSource?> GetAsync(uint appId) =>
        appId == 0
            ? Task.FromResult<ImageSource?>(null)
            : _requests.GetOrAdd(appId, id => Task.Run(() => LoadAsync(id)));

    private async Task<ImageSource?> LoadAsync(uint appId)
    {
        var path = Path.Combine(CacheDirectory, $"{appId}.jpg");

        try
        {
            if (File.Exists(path))
            {
                var cached = Decode(await File.ReadAllBytesAsync(path).ConfigureAwait(false));
                if (cached is not null)
                    return cached;

                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fall through to a fresh download.
        }

        var bytes = await DownloadAsync(appId).ConfigureAwait(false);
        if (bytes is null)
            return null;

        var image = Decode(bytes);
        if (image is null)
            return null;

        try
        {
            Directory.CreateDirectory(CacheDirectory);
            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn(AppLogScopes.App, $"Не удалось сохранить обложку {appId}: {ex.Message}");
        }

        return image;
    }

    private async Task<byte[]?> DownloadAsync(uint appId)
    {
        var url = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/capsule_184x69.jpg";

        try
        {
            using var client = _httpClientFactory.CreateClient(HttpClients.SteamApi);
            using var response = await client.GetAsync(url).ConfigureAwait(false);

            // Titles without store art are ordinary, not worth logging.
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>Decodes into a frozen bitmap so it can be handed straight to the UI thread.</summary>
    private static ImageSource? Decode(byte[] bytes)
    {
        if (bytes.Length == 0)
            return null;

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.DecodePixelWidth = 184;
            image.EndInit();
            image.Freeze();

            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or IOException)
        {
            return null;
        }
    }
}
