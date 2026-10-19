using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;

public interface IMemoryEvaluationSourceReader
{
    Task<Result<MemoryEvaluationSourceSnapshot>> ReadAsync(MemoryEvaluationValidatedRequest request, ArcanumInvocationContext invocation, CovenantTurnLease lease, CancellationToken ct);

    Task<Result> ValidateAsync(MemoryEvaluationSourceSnapshot snapshot, CovenantTurnLease lease, CancellationToken ct);
}
