namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Correlates in-flight human prompts (e.g. MCP <c>ask_human</c>) with HTTP or CLI responses.
/// </summary>
public interface IHumanPromptRegistry
{
    /// <summary>
    /// Registers a wait for <paramref name="promptId"/> and returns when <see cref="TrySubmitResponse"/> supplies text or <paramref name="ct"/> is canceled.
    /// </summary>
    Task<string> WaitForResponseAsync(string promptId, CancellationToken ct);

    /// <summary>
    /// Completes the wait for <paramref name="promptId"/> when one is registered. Returns <see langword="false"/> if no waiter exists.
    /// </summary>
    bool TrySubmitResponse(string promptId, string response);
}
