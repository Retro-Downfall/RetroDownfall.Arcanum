using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;

public interface IMemoryEvaluationProvider
{
    Task<Result<MemoryEvaluationExecutionPlan>> PrepareAsync(MemoryEvaluationValidatedRequest request, MemoryEvaluationSourceSnapshot snapshot, MemoryEvaluationPromptPair prompts, CancellationToken ct);

    Task<Result> ValidateAsync(MemoryEvaluationExecutionPlan plan, CancellationToken ct);

    Task<Result<MemoryEvaluationArmResult>> SendAsync(MemoryEvaluationExecutionPlan plan, int pairIndex, MemoryEvaluationArm arm, ITurnBudget budget, Func<MemoryEvaluationSendEvidence, CancellationToken, Task> beforeSend, CancellationToken ct);
}
