using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hourglass.Services.Interfaces;
using Hourglass.Utilities;

namespace Hourglass.Services;

/// <summary>
/// Lets the user check on and steer the app from their phone.
///
/// Only one chat is ever served: the one that proved it knows the pairing code shown
/// in the app. Anything from any other chat is answered with a flat refusal, so a bot
/// token that leaks does not hand over control.
/// </summary>
public sealed class TelegramBotService : IDisposable
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan ErrorBackoff = TimeSpan.FromSeconds(15);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAppLogger _logger;
    private readonly IConfigStore _store;

    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private IBoostController? _controller;
    private string _token = "";
    private long _updateOffset;
    private bool _disposed;

    public TelegramBotService(IHttpClientFactory httpClientFactory, IAppLogger logger, IConfigStore store)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _store = store;
    }

    /// <summary>Shown in settings; the user sends it to the bot once to pair their chat.</summary>
    public string PairingCode { get; } = Random.Shared.Next(100_000, 999_999).ToString();

    public bool IsRunning => _pollTask is { IsCompleted: false };

    public bool IsPaired => _store.Config.Telegram.ChatId != 0;

    public event EventHandler? StateChanged;

    public void Start(IBoostController controller)
    {
        if (_disposed || IsRunning)
            return;

        var telegram = _store.Config.Telegram;
        var token = SecretProtector.Unprotect(telegram.ProtectedToken);

        if (!telegram.IsEnabled || string.IsNullOrWhiteSpace(token))
            return;

        _controller = controller;
        _token = token;
        _cts = new CancellationTokenSource();

        var cancellationToken = _cts.Token;
        _pollTask = Task.Run(() => PollLoopAsync(cancellationToken), CancellationToken.None);

        _logger.Info(AppLogScopes.App, "Telegram-бот запущен");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        var poll = _pollTask;
        _cts = null;
        _pollTask = null;

        if (cts is null)
            return;

        cts.Cancel();

        if (poll is not null)
        {
            try
            {
                await poll.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }
        }

        cts.Dispose();
        _logger.Info(AppLogScopes.App, "Telegram-бот остановлен");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Restarts the bot so a changed token or switch takes effect immediately.</summary>
    public async Task RestartAsync(IBoostController controller)
    {
        await StopAsync().ConfigureAwait(true);
        Start(controller);
    }

    /// <summary>Pushes a message to the paired chat. Quietly does nothing when unpaired.</summary>
    public void Notify(string text)
    {
        var chatId = _store.Config.Telegram.ChatId;
        if (chatId == 0 || string.IsNullOrEmpty(_token))
            return;

        AsyncHelper.FireAndForget(
            () => SendAsync(chatId, text, CancellationToken.None),
            nameof(Notify));
    }

    /// <summary>Verifies a token without saving it, returning the bot's @name.</summary>
    public async Task<string> VerifyTokenAsync(string token, CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient(HttpClients.Telegram);
        using var response = await client
            .GetAsync($"https://api.telegram.org/bot{token}/getMe", cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new TelegramException("Telegram не принял токен. Проверьте, что скопировали его целиком.");

        if (!response.IsSuccessStatusCode)
            throw new TelegramException($"Telegram ответил {(int)response.StatusCode}.");

        var payload = await response.Content
            .ReadFromJsonAsync<TelegramResponse<TelegramUser>>(cancellationToken)
            .ConfigureAwait(false);

        var username = payload?.Result?.Username;
        if (string.IsNullOrWhiteSpace(username))
            throw new TelegramException("Telegram вернул неожиданный ответ.");

        return "@" + username;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    // ------------------------------------------------------------------ polling

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var updates = await FetchUpdatesAsync(cancellationToken).ConfigureAwait(false);

                foreach (var update in updates)
                {
                    _updateOffset = Math.Max(_updateOffset, update.UpdateId + 1);

                    var message = update.Message;
                    if (message?.Chat is null || string.IsNullOrWhiteSpace(message.Text))
                        continue;

                    await HandleMessageAsync(message.Chat.Id, message.Text.Trim(), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (TelegramException ex)
            {
                _logger.Error(AppLogScopes.App, $"Telegram: {ex.Message}");
                await DelayAsync(ErrorBackoff, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException)
            {
                // Ordinary connectivity noise; keep quiet and retry.
                await DelayAsync(ErrorBackoff, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<IReadOnlyList<TelegramUpdate>> FetchUpdatesAsync(CancellationToken cancellationToken)
    {
        var url = $"https://api.telegram.org/bot{_token}/getUpdates" +
                  $"?timeout={(int)PollTimeout.TotalSeconds}&offset={_updateOffset}&allowed_updates=%5B%22message%22%5D";

        using var client = _httpClientFactory.CreateClient(HttpClients.Telegram);
        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new TelegramException("токен отклонён, бот остановлен");

        if (!response.IsSuccessStatusCode)
            throw new TelegramException($"getUpdates вернул {(int)response.StatusCode}");

        var payload = await response.Content
            .ReadFromJsonAsync<TelegramResponse<List<TelegramUpdate>>>(cancellationToken)
            .ConfigureAwait(false);

        return payload?.Result ?? new List<TelegramUpdate>();
    }

    private async Task HandleMessageAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        var telegram = _store.Config.Telegram;

        // Pairing is the only thing an unknown chat may do.
        if (telegram.ChatId == 0)
        {
            if (text.StartsWith("/link", StringComparison.OrdinalIgnoreCase) &&
                text.Contains(PairingCode, StringComparison.Ordinal))
            {
                telegram.ChatId = chatId;
                _store.Save();
                _logger.Success(AppLogScopes.App, "Telegram: чат привязан");
                StateChanged?.Invoke(this, EventArgs.Empty);

                await SendAsync(chatId,
                    "Готово, чат привязан.\n\n" + Help,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await SendAsync(chatId,
                "Этот бот не привязан. Откройте Hourglass → Настройки → Telegram и отправьте сюда: /link ваш-код",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (chatId != telegram.ChatId)
        {
            await SendAsync(chatId, "Доступ запрещён.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var reply = await ExecuteCommandAsync(text).ConfigureAwait(false);
        await SendAsync(chatId, reply, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ExecuteCommandAsync(string text)
    {
        var controller = _controller;
        if (controller is null)
            return "Программа ещё запускается, попробуйте через минуту.";

        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var command = parts[0].ToLowerInvariant();
        var argument = parts.Length > 1 ? parts[1] : "";

        // Telegram appends @botname when several bots share a group.
        var at = command.IndexOf('@');
        if (at > 0)
            command = command[..at];

        try
        {
            return command switch
            {
                "/start" or "/help" => Help,
                "/status" => await controller.DescribeStatusAsync().ConfigureAwait(false),
                "/run" when argument.Length > 0 => await controller.StartAccountAsync(argument).ConfigureAwait(false),
                "/run" => await controller.StartAllAsync().ConfigureAwait(false),
                "/stop" when argument.Length > 0 => await controller.StopAccountAsync(argument).ConfigureAwait(false),
                "/stop" => await controller.StopAllAsync().ConfigureAwait(false),
                _ => "Не понял команду. /help — список."
            };
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogScopes.App, "Telegram: команда не выполнена", ex);
            return "Команда не выполнилась: " + ex.Message;
        }
    }

    private async Task SendAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient(HttpClients.Telegram);
            using var response = await client
                .PostAsJsonAsync(
                    $"https://api.telegram.org/bot{_token}/sendMessage",
                    new
                    {
                        chat_id = chatId,
                        text,
                        parse_mode = "HTML",
                        disable_web_page_preview = true,
                        disable_notification = false
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                _logger.Warn(AppLogScopes.App, $"Telegram: сообщение не доставлено ({(int)response.StatusCode})");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // The next notification will try again; nothing is lost that matters.
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private const string Help =
        "<b>Hourglass</b>\n\n" +
        "<code>/status</code> — что сейчас происходит\n" +
        "<code>/run</code> — запустить все аккаунты\n" +
        "<code>/run имя</code> — запустить один\n" +
        "<code>/stop</code> — остановить все\n" +
        "<code>/stop имя</code> — остановить один";

    /// <summary>
    /// Messages go out as HTML, so anything coming from Steam — persona names, error
    /// text — has to be tamed before it lands in the markup.
    /// </summary>
    public static string Escape(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);

    // ------------------------------------------------------------------- wire

    private sealed class TelegramResponse<T>
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("result")]
        public T? Result { get; set; }
    }

    private sealed class TelegramUser
    {
        [JsonPropertyName("username")]
        public string? Username { get; set; }
    }

    private sealed class TelegramUpdate
    {
        [JsonPropertyName("update_id")]
        public long UpdateId { get; set; }

        [JsonPropertyName("message")]
        public TelegramMessage? Message { get; set; }
    }

    private sealed class TelegramMessage
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("chat")]
        public TelegramChat? Chat { get; set; }
    }

    private sealed class TelegramChat
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }
    }
}

public sealed class TelegramException : Exception
{
    public TelegramException(string message) : base(message)
    {
    }
}
