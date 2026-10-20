using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.MemoryDelegation;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.MemoryEvaluation;

namespace RetroDownfall.Arcanum.Infrastructure.Data.MemoryDelegation;

internal sealed class MemoryDelegationSourceReader(ICovenantConnectionSource connections, ICovenantOperationGate gate, TimeProvider timeProvider, ICovenantStore? covenantStore = null) : IMemoryDelegationSourceReader
{
    private readonly MemorySourceProofReader _proof = new(connections, gate, timeProvider, covenantStore, MemorySourceProofPolicy.Delegation);

    public async Task<Result<MemoryEvaluationSourceSnapshot>> ReadAsync(MemoryDelegationGrant grant, ArcanumInvocationContext parent, CovenantTurnLease lease, CancellationToken ct)
    {
        if (!MemoryDelegationDigests.IsValid(grant))
        {
            return new Error(ErrorCodes.MemoryDelegation.InvalidGrant, "The memory delegation grant violates policy v1.");
        }

        Result authority = await gate.ValidateTurnLeaseAsync(lease, ct).ConfigureAwait(false);

        if (authority.IsFailure || !parent.CanReadCovenant
            || parent.Surface is not (ArcanumExecutionSurface.StatelessOperatorTurn or ArcanumExecutionSurface.SessionBackedOperatorTurn)
            || parent.ToolPolicy is not (ToolPolicy.AllTools or ToolPolicy.ReadOnlyTools or ToolPolicy.NoForbiddenArts)
            || parent.ReadAuthorityEpoch?.Matches(lease.Snapshot) != true
            || parent.Campaign is not { } campaign || campaign.CampaignId != grant.Scope.CampaignId
            || lease.Snapshot.Scope?.CampaignId != campaign.CampaignId)
        {
            return new Error(ErrorCodes.Covenant.ForbiddenAuthority, "Delegation requires its genuine live, scope-matched operator parent Turn lease.");
        }

        Result<MemoryEvaluationSourceSnapshot> result = await _proof.ReadAsync(grant.Memory, campaign, lease, ct).ConfigureAwait(false);

        return result.IsFailure ? Translate(result.Error) : result;
    }

    public async Task<Result> ValidateAsync(MemoryEvaluationSourceSnapshot snapshot, CovenantTurnLease lease, CancellationToken ct)
    {
        Result result = await _proof.ValidateAsync(snapshot, lease, ct).ConfigureAwait(false);

        return result.IsFailure ? Translate(result.Error) : result;
    }

    private static Error Translate(Error error) => error.Code == ErrorCodes.MemoryEvaluation.StaleSource
        ? new(ErrorCodes.MemoryDelegation.StaleSource, "The exact delegation source evidence is unavailable or no longer eligible.")
        : error;
}
