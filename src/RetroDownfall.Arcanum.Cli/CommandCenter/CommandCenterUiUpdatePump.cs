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
