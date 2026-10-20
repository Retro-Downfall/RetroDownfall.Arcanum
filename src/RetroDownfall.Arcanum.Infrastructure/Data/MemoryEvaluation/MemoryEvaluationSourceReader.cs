using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data.MemoryEvaluation;

internal sealed class MemoryEvaluationSourceReader(ICovenantConnectionSource connections, ICovenantOperationGate gate, TimeProvider timeProvider, ICovenantStore? covenantStore = null) : IMemoryEvaluationSourceReader
{
    private readonly MemorySourceProofReader _proof = new(connections, gate, timeProvider, covenantStore);

    public async Task<Result<MemoryEvaluationSourceSnapshot>> ReadAsync(MemoryEvaluationValidatedRequest request, ArcanumInvocationContext invocation, CovenantTurnLease lease, CancellationToken ct)
    {
        Result authority = await gate.ValidateTurnLeaseAsync(lease, ct).ConfigureAwait(false);

        if (authority.IsFailure || !invocation.CanReadCovenant || invocation.ToolPolicy != ToolPolicy.NoTools
            || invocation.Surface != ArcanumExecutionSurface.StatelessOperatorTurn
            || invocation.ReadAuthorityEpoch?.Matches(lease.Snapshot) != true
            || invocation.Campaign is not { } campaign || campaign.CampaignId != request.Request.Scope.CampaignId
            || lease.Snapshot.Scope?.CampaignId != campaign.CampaignId)
        {
            return new Error(ErrorCodes.Covenant.ForbiddenAuthority, "Evaluation requires its genuine live, scope-matched operator Turn lease.");
        }

        return await _proof.ReadAsync(request.Request.Memory, campaign, lease, ct).ConfigureAwait(false);
    }

    public Task<Result> ValidateAsync(MemoryEvaluationSourceSnapshot snapshot, CovenantTurnLease lease, CancellationToken ct) => _proof.ValidateAsync(snapshot, lease, ct);

    internal static string CovenantMetadataProjection(string sql) => MemorySourceProofReader.CovenantMetadataProjection(sql);
}
