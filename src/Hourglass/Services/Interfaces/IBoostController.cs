namespace Hourglass.Services.Interfaces;

/// <summary>
/// What the Telegram bot is allowed to do with the app. Keeps the bot free of any
/// knowledge about view models, and keeps the remote surface deliberately small.
/// </summary>
public interface IBoostController
{
    Task<string> DescribeStatusAsync();

    Task<string> StartAllAsync();

    Task<string> StopAllAsync();

    /// <summary>Starts one account matched by login or profile name.</summary>
    Task<string> StartAccountAsync(string query);

    Task<string> StopAccountAsync(string query);
}
