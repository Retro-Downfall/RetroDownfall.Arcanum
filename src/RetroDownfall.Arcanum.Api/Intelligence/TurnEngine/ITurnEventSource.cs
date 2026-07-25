using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;

/// <summary>Internal entry for OpenAI and native surfaces to obtain the same semantic turn stream.</summary>
internal interface ITurnEventSource
{

    IAsyncEnumerable<TurnEvent> RunTurnAsync(
        TurnExecutionRequest request,
        CancellationToken executionToken);

}

/// <summary>
/// Tracks provider-attempt commitment from raw <see cref="ModelCallUpdate"/>s before projection.
/// </summary>
internal static class ProviderAttemptCommitTracker
{

    public static bool CommitsProviderAttempt(ModelCallUpdate update) =>
        update is ModelCallTextDelta { Text.Length: > 0 }
            or ModelCallReasoningUpdate;

    public static bool CommitsProviderAttempt(ModelCallReasoningResult reasoning) =>
        reasoning.HasProviderContent;

    public static bool CommitsOnCompleteToolProposal(bool hasActionableToolCalls) => hasActionableToolCalls;

    public static bool CommitsOnEmptySuccessfulRound(bool hasText, bool hasToolCalls) =>
        !hasText && !hasToolCalls;

}
