using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;

/// <summary>
/// Phase 1 seam: TurnEngine producer delegates into the Master implementation's
/// <c>RunInferenceAttemptAsync</c> (single tool-loop, mode-parameterized) via thin buffered-drain /
/// streaming-map adapters.
/// </summary>
internal interface ITurnPipelineRunner
{

    Task RunBufferedIntoEmitterAsync(
        TurnExecutionRequest request,
        TurnEventEmitter emitter,
        InferenceAuditContext? auditContext,
        CancellationToken cancellationToken);

    Task RunStreamingIntoEmitterAsync(
        TurnExecutionRequest request,
        TurnEventEmitter emitter,
        InferenceAuditContext? auditContext,
        CancellationToken cancellationToken);

}
