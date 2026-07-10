using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace RetroDownfall.TheForge.Ux;

/// <summary>
/// Observes fire-and-forget tasks so exceptions are logged instead of becoming unobserved.
/// </summary>
internal static class TaskUtilities
{

    public static void FireAndForget(
        Task task,
        ILogger? logger = null,
        [CallerMemberName] string? caller = null)
    {

        ArgumentNullException.ThrowIfNull(task);

        if (task.IsCompletedSuccessfully)
        {

            return;

        }

        _ = ObserveAsync(task, logger, caller);

    }

    private static async Task ObserveAsync(Task task, ILogger? logger, string? caller)
    {

        try
        {

            await task.ConfigureAwait(false);

        }
        catch (OperationCanceledException)
        {

            // Expected when a panel or document is disposed mid-flight.

        }
        catch (Exception ex)
        {

            logger?.LogError(ex, "Fire-and-forget task failed ({Caller}).", caller ?? "unknown");

        }

    }

}
