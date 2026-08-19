using System.Diagnostics;

namespace Hourglass.Utilities;

internal static class AsyncHelper
{
    /// <summary>Runs a task without awaiting it, but never lets an exception escape unnoticed.</summary>
    public static async void FireAndForget(Func<Task> operation, string context)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{context}] background task failed: {ex}");
        }
    }
}
