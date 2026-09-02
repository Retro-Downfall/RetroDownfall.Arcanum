namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Per-tool-call ambient for the live HITL emitter — (re-)established fresh before each tool
/// invocation (see <c>WizardIntelligenceProvider.ProcessWithLiveWardsAsync</c>) because an
/// <see cref="AsyncLocal{T}"/> write does not survive the <c>yield return</c> between one tool
/// call and the next, so nothing set here persists across a whole turn on its own; the channel it
/// wraps is still created once per turn. The MCP client <c>ElicitationHandler</c> stays a
/// singleton registration; it reads this <see cref="AsyncLocal{T}"/> so buffered/unattended turns
/// (no emitter) decline immediately instead of registering an invisible waiter.
/// </summary>
/// <remarks>
/// Spike assumption: MCP SDK elicitation callbacks run on the same async/<see cref="ExecutionContext"/>
/// as the in-flight tool invoke, so <see cref="AsyncLocal{T}"/> flows. If a future SDK version
/// marshals the handler onto a detached context, this ambient will be null and elicitation will
/// decline — document that failure mode before inventing a second ambient.
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
