using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

/// <summary>
/// The one path from an eligible invocation to an immutable turn plan (§10.13).
/// </summary>
/// <remarks>
/// The order of the gates is the contract. Eligibility is decided from the invocation context before
/// anything touches configuration; the capability snapshot is read before anything takes a lease;
/// the lease is taken before anything opens a read. Reversing any pair would let an ineligible
/// caller learn something about Covenant state it has no authority to observe — even the shape of a
/// failure is an answer.
///
/// <para>A damaged Confirmed artifact fails the turn rather than degrading it. Authoritative
/// operator context that silently disappears is worse than an error, because the operator sees a
/// plausible answer produced without the instruction they wrote.</para>
/// </remarks>
public sealed class CovenantContextProvider(
    ICovenantAvailability availability,
    ICovenantOperationGate gate,
    ICovenantStore store,
    ICovenantLinker linker) : ICovenantContextProvider
{

    public async ValueTask<Result<CovenantTurnContext>> BeginTurnAsync(
        ArcanumInvocationContext invocation,
        Guid logicalTurnId,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(invocation);

        if (!invocation.CanReadCovenant)
        {
            return Absent(CovenantTurnAbsence.NotEligible);
        }

        CovenantAvailabilitySnapshot snapshot = availability.Current;

        if (!snapshot.FeatureEnabled)
        {
            return Absent(CovenantTurnAbsence.FeatureDisabled);
        }

        if (snapshot.Canonical is not CovenantCapabilityState.Healthy)
        {
            return Absent(CovenantTurnAbsence.CapabilityUnavailable);
        }

        if (invocation.Campaign is not { } campaign)
        {
            return Absent(CovenantTurnAbsence.NoCampaign);
        }

        Result<CovenantTurnLease> lease = await gate
            .AcquireTurnAsync(campaign, cancellationToken)
            .ConfigureAwait(false);

        if (lease.IsFailure)
        {

            // An unavailable gate is an absence, not a turn failure: the operator's profile is not
            // reachable right now, and refusing an ordinary conversation over that would make an
            // optional capability a hard dependency of inference.
            return lease.Error.Code == ErrorCodes.Covenant.Unavailable
                ? Absent(CovenantTurnAbsence.CapabilityUnavailable)
                : lease.Error;

        }

        bool retained = false;

        try
        {

            if (invocation.ReadAuthorityEpoch is not { } readEpoch
                || !readEpoch.Matches(lease.Value.Snapshot))
            {

                return new Error(
                    ErrorCodes.Covenant.StaleSnapshot,
                    "Covenant runtime authority changed between invocation admission and turn acquisition.");

            }

            Result<CovenantTurnContext> planned = await LoadAndLinkAsync(
                invocation,
                campaign,
                lease.Value,
                logicalTurnId,
                cancellationToken).ConfigureAwait(false);

            retained = planned.IsSuccess && planned.Value.HasPlan;

            return planned;

        }
        finally
        {

            if (!retained)
            {
                await lease.Value.DisposeAsync().ConfigureAwait(false);
            }

        }

    }

    private async ValueTask<Result<CovenantTurnContext>> LoadAndLinkAsync(
        ArcanumInvocationContext invocation,
        CanonicalCampaignContext campaign,
        CovenantTurnLease lease,
        Guid logicalTurnId,
        CancellationToken cancellationToken)
    {

        Result<CovenantTurnSnapshot> snapshot = await store
            .ReadTurnSnapshotAsync(campaign, lease, cancellationToken)
            .ConfigureAwait(false);

        if (snapshot.IsFailure)
        {
            return snapshot.Error;
        }

        Result<CovenantTurnPlan> plan = linker.Link(snapshot.Value);

        if (plan.IsFailure)
        {
            return plan.Error;
        }

        bool canStage = invocation.CanStageCovenantMutation;

        // An empty plan that cannot stage anything has no reason to keep a lease for the rest of the
        // turn, and holding one would delay every destructive operation behind a turn that was never
        // going to touch Covenant state.
        if (plan.Value.EligibleDecisions.IsEmpty && !canStage)
        {
            return Absent(CovenantTurnAbsence.Empty);
        }

        return Result<CovenantTurnContext>.Success(
            CovenantTurnContext.ForPlan(
                plan.Value,
                lease,
                canStage
                    ? new CovenantMutationCollector(logicalTurnId, plan.Value.Digest, Guid.NewGuid())
                    : null,
                logicalTurnId));

    }

    private static Result<CovenantTurnContext> Absent(CovenantTurnAbsence absence) =>
        Result<CovenantTurnContext>.Success(CovenantTurnContext.Absent(absence));

}
