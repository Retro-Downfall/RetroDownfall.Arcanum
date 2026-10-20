using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;

namespace RetroDownfall.Arcanum.Core.Intelligence.MemoryDelegation;

public interface IMemoryDelegationSourceReader
{
    Task<Result<MemoryEvaluationSourceSnapshot>> ReadAsync(MemoryDelegationGrant grant, ArcanumInvocationContext parent, CovenantTurnLease lease, CancellationToken ct);

    Task<Result> ValidateAsync(MemoryEvaluationSourceSnapshot snapshot, CovenantTurnLease lease, CancellationToken ct);
}
