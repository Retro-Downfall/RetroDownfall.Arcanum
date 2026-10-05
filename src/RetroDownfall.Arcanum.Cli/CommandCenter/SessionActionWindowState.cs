namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// The window reads a gated session action decides from. Kept behind an interface so a test can see on
/// which thread each read happens without a Terminal.Gui view.
/// </summary>
internal interface ICommandCenterSessionActionWindow
{
    bool ComposerHasText { get; }

    int GetSelectedLogIndex();

    IReadOnlyList<string> GetLogLinesSnapshot();

    Guid? GetSelectedSessionId(CommandCenterState state);
}

/// <summary>
/// What a gated session action (resume the selected session, start a new one) needs to know about the
/// window, read once and held as plain values.
/// </summary>
/// <remarks>
/// Terminal.Gui views belong to the UI thread. A gated action first waits on the action gate, and when
/// the gate is contended the continuation that follows runs on a thread-pool thread — so a read taken
/// after the wait is a cross-thread read of a view. The reads are taken here instead, in the key
/// handler's synchronous prefix (before anything is awaited), and the action decides from the values.
/// That also pins the question to what the operator saw when they pressed the key, rather than to
/// whatever the selection has become by the time the gate frees up.
/// </remarks>
internal sealed record SessionActionWindowState(
    bool ComposerHasText,
    Guid? SelectedSessionId,
    int SelectedLogIndex,
    IReadOnlyList<string> LogLines)
{
    /// <summary>
    /// Reads the window. Call it on the UI thread. The transcript lines are copied only when a transcript
    /// pick is what the action will consult, because the copy is proportional to the transcript.
    /// </summary>
    public static SessionActionWindowState Capture(ICommandCenterSessionActionWindow window, CommandCenterState state)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(state);

        bool transcriptPick = state.FocusRegion == CommandCenterFocusRegion.Transcript
            && state.Overlay == CommandCenterOverlayKind.None;

        return new SessionActionWindowState(
            window.ComposerHasText,
            window.GetSelectedSessionId(state),
            transcriptPick ? window.GetSelectedLogIndex() : -1,
            transcriptPick ? window.GetLogLinesSnapshot() : []);
    }
}
