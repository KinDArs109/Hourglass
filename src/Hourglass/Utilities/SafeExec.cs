using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Hourglass.Utilities;

internal static class SafeExec
{
    public static void Try(Action action, [CallerMemberName] string? caller = null)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SafeExec:{caller}] {ex.Message}");
        }
    }

    public static async Task TryAsync(Func<Task> action, [CallerMemberName] string? caller = null)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SafeExec:{caller}] {ex.Message}");
        }
    }
}
