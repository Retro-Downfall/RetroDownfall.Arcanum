namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Per-tool-call ambient for the live HITL emitter — (re-)established fresh before each tool
/// invocation (see <c>WizardIntelligenceProvider.ProcessWithLiveWardsAsync</c>) because an
/// <see cref="AsyncLocal{T}"/> write does not survive the <c>yield return</c> between one tool
/// call and the next, so nothing set here persists across a whole turn on its own; the channel it
/// wraps is still created once per turn. It is read at the tool-call site only: in-process tools
/// read it directly, and the MCP client wrapper hands it to its connection's elicitation sink for
/// the duration of each <c>tools/call</c>. Buffered and unattended turns leave it null, so an
/// elicitation raised on such a turn declines immediately instead of registering an invisible waiter.
/// </summary>
/// <remarks>
/// The MCP SDK runs client handlers, the <c>ElicitationHandler</c> included, on the connection's
/// receive loop, whose execution context was captured when the connection was made; this ambient is
/// never visible there. That is why the handler resolves the emitter from the per-connection sink
/// rather than from this <see cref="AsyncLocal{T}"/>.
/// </remarks>
public static class HumanPromptLiveEmitterAmbient
{
    private static readonly AsyncLocal<IHumanPromptLiveEmitter?> CurrentLocal = new();

    public static IHumanPromptLiveEmitter? Current
    {
        get => CurrentLocal.Value;
        set => CurrentLocal.Value = value;
    }
}
