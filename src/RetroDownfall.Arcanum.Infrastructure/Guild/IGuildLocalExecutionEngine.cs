using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildLocalEngineContinuation(GuildLocalInferenceExecution Execution,
    GuildContinuationStored Continuation, string Answer, ArtifactSensitivityLabel? SourceLabel = null);

internal interface IGuildLocalEngineWait
{
    Task<Result<GuildLocalEngineContinuation>> WaitAsync(GuildLocalInferenceExecution execution,
        GuildLocalWaitCheckpointWrite checkpoint, CancellationToken cancellationToken);
}

/// <summary>One awaited entry into the existing Apprentice units. The Quest owns its operation and lease.</summary>
internal interface IGuildLocalExecutionEngine
{
    Task<Result<PromptTurnResult>> ExecuteGuildAsync(GuildLocalExecutionBinding binding,
        GuildLocalExecutionInput input, GuildLocalInferenceExecution execution, IGuildLocalInferenceRuntime runtime,
        IGuildLocalEngineWait waitForInput, CancellationToken cancellationToken);
}
