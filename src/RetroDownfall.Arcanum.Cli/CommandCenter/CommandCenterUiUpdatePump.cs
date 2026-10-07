using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// Collapses a burst of queued UI updates before any of them reaches the Terminal.Gui thread.
/// Streaming emits a refresh per flush interval and per newline, so a code-heavy answer can queue
/// dozens of refreshes while the UI thread is still applying the first one; applying each in turn
/// repeats the same full-pane work. Consecutive refresh kinds therefore fold into a single apply
/// (mixed kinds widen to <see cref="CommandCenterUiUpdateKind.RefreshAll"/>, which is a superset),
/// while focus kinds are one-shot side effects and always survive, in order.
/// </summary>
internal static class CommandCenterUiUpdatePump
{
    /// <summary>
    /// Carries queued updates to <paramref name="apply"/>, through <paramref name="marshal"/>, until the
    /// channel completes or <paramref name="cancellationToken"/> is cancelled. Whatever arrived while the
    /// previous update was being handed over is drained and folded first, so a burst costs one apply per kind.
    /// </summary>
    /// <remarks>
    /// An apply that throws is logged and the loop carries on: one failed refresh must neither freeze the
    /// screen for the rest of the run nor vanish without a trace. <paramref name="marshal"/> is the UI
    /// loop's hand-off (Terminal.Gui's <c>Invoke</c>), which from this thread queues the work and runs it
    /// later on the UI thread, so the guard is part of the work handed over rather than a wrapper around the
    /// hand-off: an apply that throws on the UI loop is logged there instead of unwinding the loop and
    /// ending the session.
    /// </remarks>
    public static async Task RunAsync(
        ChannelReader<CommandCenterUiUpdate> updates,
        Action<Action> marshal,
        Action<CommandCenterUiUpdateKind> apply,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(marshal);
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            List<CommandCenterUiUpdateKind> queued = [];
            await foreach (CommandCenterUiUpdate update in updates
                               .ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                queued.Clear();
                queued.Add(update.Kind);
                while (updates.TryRead(out CommandCenterUiUpdate? pending))
                {
                    queued.Add(pending.Kind);
                }

                foreach (CommandCenterUiUpdateKind kind in Coalesce(queued))
                {
                    try
                    {
                        marshal(() => ApplyLogged(apply, kind, logger));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogError(ex, "Handing a Command Center UI update ({UpdateKind}) to the UI loop failed.", kind);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The run ended: the host cancels this loop's token once the terminal UI has closed.
        }
    }

    private static void ApplyLogged(
        Action<CommandCenterUiUpdateKind> apply,
        CommandCenterUiUpdateKind kind,
        ILogger logger)
    {
        try
        {
            apply(kind);
        }
        catch (Exception ex)
        {
            // This runs on the UI loop with no token of its own, so nothing here is a cancellation to pass
            // on: anything that escaped would unwind the loop and end the session.
            logger.LogError(ex, "Applying a Command Center UI update ({UpdateKind}) failed.", kind);
        }
    }

    public static bool IsRefreshKind(CommandCenterUiUpdateKind kind) =>
        kind is not (CommandCenterUiUpdateKind.FocusInput
            or CommandCenterUiUpdateKind.FocusSessions
            or CommandCenterUiUpdateKind.FocusTranscript);

    /// <summary>
    /// Folds <paramref name="queued"/> into the smallest equivalent sequence of applies.
    /// </summary>
    public static List<CommandCenterUiUpdateKind> Coalesce(IReadOnlyList<CommandCenterUiUpdateKind> queued)
    {
        ArgumentNullException.ThrowIfNull(queued);

        List<CommandCenterUiUpdateKind> result = new();
        CommandCenterUiUpdateKind? pendingRefresh = null;
        foreach (CommandCenterUiUpdateKind kind in queued)
        {
            if (IsRefreshKind(kind))
            {
                pendingRefresh = pendingRefresh is { } existing ? Widen(existing, kind) : kind;
                continue;
            }

            if (pendingRefresh is { } buffered)
            {
                result.Add(buffered);
                pendingRefresh = null;
            }

            result.Add(kind);
        }

        if (pendingRefresh is { } tail)
        {
            result.Add(tail);
        }

        return result;
    }

    private static CommandCenterUiUpdateKind Widen(
        CommandCenterUiUpdateKind first,
        CommandCenterUiUpdateKind second) =>
        first == second ? first : CommandCenterUiUpdateKind.RefreshAll;
}
