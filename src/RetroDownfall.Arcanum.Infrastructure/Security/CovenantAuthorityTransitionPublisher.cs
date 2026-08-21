using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// Swaps the process onto the key material of an already-committed authority transition.
/// </summary>
/// <remarks>
/// Reset, restore, family reinitialize, and envelope counter rollover all end the same way: a durable
/// transition has committed, the exclusive gate is still closed, and the live process is still holding
/// keys the transition invalidated. This publisher is the one place that closes that gap (§10.12).
///
/// <para>The caller presents the still-held exclusive lease that protected the durable transition. It
/// is not decoration: it is the proof that admission is still closed, which is what makes deriving and
/// swapping safe. Publishing while readers were admitted would let a request that started under the old
/// generation finish under the new one.</para>
///
/// <para>The new generation is derived first and swapped second. Any failure conditionally retires
/// the observed runtime generation while preserving the newest availability and exact recovery owner;
/// a competing complete authority winner is left untouched. Recovery can then derive again from the
/// resident root while the same gate closure remains closed.</para>
/// </remarks>
internal sealed class CovenantAuthorityTransitionPublisher(
    CovenantRuntimeGenerationProvider runtime,
    CovenantEnvelopeMasterKeyProvider keys,
    CovenantAvailability availability)
    : ICovenantAuthorityTransitionPublisher, ICovenantCommittedTransitionPublisher
{

    /// <inheritdoc/>
    public ValueTask<Result> PublishCommittedAsync(
        CovenantCommittedAuthorityTransition transition,
        ICovenantExclusiveOperationLease lease,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(transition);

        return PublishCommittedAsync(
            Result<CovenantCommittedAuthorityTransition>.Success(transition),
            lease,
            runtime.Current,
            cancellationToken);

    }

    /// <summary>
    /// Publishes against the exact runtime tuple the erasure adapter captured before projection.
    /// </summary>
    public async ValueTask<Result> PublishCommittedAsync(
        Result<CovenantCommittedAuthorityTransition> transition,
        ICovenantExclusiveOperationLease lease,
        CovenantRuntimeGenerationState expected,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(transition);

        ArgumentNullException.ThrowIfNull(lease);

        ArgumentNullException.ThrowIfNull(expected);

        long observedRuntimeGeneration = lease.Snapshot.RuntimeAuthorityGeneration;

        if (lease.Snapshot.RecoveryOwner is not { } recoveryOwner)
        {

            return Result.Failure(
                new Error(
                    ErrorCodes.Covenant.ForbiddenAuthority,
                    "A committed authority transition requires an exact exclusive recovery owner."));

        }

        if (expected.RuntimeAuthorityGeneration != observedRuntimeGeneration)
        {
            _ = runtime.RetireAuthorityGeneration(observedRuntimeGeneration, recoveryOwner);

            return Result.Failure(
                new Error(
                    ErrorCodes.Covenant.StaleSnapshot,
                    "The exclusive lease belongs to an older Covenant runtime generation."));
        }

        if (transition.IsFailure)
        {

            _ = runtime.RetireAuthorityGeneration(observedRuntimeGeneration, recoveryOwner);

            return Result.Failure(transition.Error);

        }

        CovenantCommittedAuthorityTransition committed = transition.Value;

        CovenantPreparedEnvelopeKeyGeneration? prepared = null;

        try
        {

            cancellationToken.ThrowIfCancellationRequested();

            Result live = await lease.RevalidateAsync(cancellationToken).ConfigureAwait(false);

            if (!live.IsSuccess)
            {
                _ = runtime.RetireAuthorityGeneration(observedRuntimeGeneration, recoveryOwner);

                return live;
            }

            CovenantAuthoritySnapshot? current = expected.ActiveAuthority
                ?? (expected.AuthorityRetired && expected.RecoveryOwner == recoveryOwner
                    ? expected.AuthoritySlot
                    : null);

            if (current is not null
                && !string.Equals(current.InstallationIdentity, committed.InstallationIdentity, StringComparison.Ordinal))
            {
                Result failure = Result.Failure(
                    new Error(
                        ErrorCodes.Covenant.IntegrityFailure,
                        "A committed transition cannot change the installation identity."));

                _ = runtime.RetireAuthorityGeneration(observedRuntimeGeneration, recoveryOwner);

                return failure;
            }

        // Monotonic or nothing. A transition that moved a counter backwards would make a replayed
        // token from an earlier generation authenticate again, which is the exact failure every one of
        // these counters exists to prevent.
            if (current is not null
                && (committed.AuthorityEpoch < current.AuthorityEpoch
                    || committed.MasterKeyVersion < current.MasterKeyVersion
                    || (expected.CanonicalEnvelopeEpoch is { } canonicalEnvelopeEpoch
                        && committed.CanonicalEnvelopeEpoch < canonicalEnvelopeEpoch)
                    || committed.RecoveryEnvelopeEpoch < current.RecoveryEnvelopeEpoch))
            {
                Result failure = Result.Failure(
                    new Error(
                        ErrorCodes.Covenant.IntegrityFailure,
                        "A committed transition cannot move an authority counter backwards."));

                _ = runtime.RetireAuthorityGeneration(observedRuntimeGeneration, recoveryOwner);

                return failure;
            }

            Result<CovenantHealthTransition> healthTransition = ResolveHealthTransition(
                recoveryOwner.Operation);

            if (!healthTransition.IsSuccess)
            {
                _ = runtime.RetireAuthorityGeneration(observedRuntimeGeneration, recoveryOwner);

                return healthTransition.Error;
            }

            Result<CovenantAvailabilitySnapshot> built = availability.BuildCommittedTransition(
                expected.Availability,
                committed.Capability,
                healthTransition.Value);

            if (!built.IsSuccess)
            {
                _ = runtime.RetireAuthorityGeneration(observedRuntimeGeneration, recoveryOwner);

                return built.Error;
            }

            Result<CovenantPreparedEnvelopeKeyGeneration> derived = keys.PrepareRekey(committed);

            if (!derived.IsSuccess)
            {
                _ = runtime.RetireAuthorityGeneration(observedRuntimeGeneration, recoveryOwner);

                return derived.Error;
            }

            prepared = derived.Value;

            Result published = lease.ExecuteWhileHeld(
                () => runtime.PublishCommitted(expected, prepared!, committed, built.Value));

            if (!published.IsSuccess)
            {
                _ = runtime.RetireAuthorityGeneration(observedRuntimeGeneration, recoveryOwner);
            }

            return published;

        }
        catch
        {

            _ = runtime.RetireAuthorityGeneration(observedRuntimeGeneration, recoveryOwner);

            throw;

        }
        finally
        {

            prepared?.Dispose();

        }

    }

    private static Result<CovenantHealthTransition> ResolveHealthTransition(
        CovenantExclusiveOperation operation) =>
        operation switch
        {
            CovenantExclusiveOperation.CovenantReset => CovenantHealthTransition.Reset,
            CovenantExclusiveOperation.HealthyCatalogFactoryErasure => CovenantHealthTransition.Reset,
            CovenantExclusiveOperation.BackupRestore => CovenantHealthTransition.Restore,
            CovenantExclusiveOperation.CovenantFamilyReinitialize => CovenantHealthTransition.FamilyReinitialize,
            CovenantExclusiveOperation.SchemaRepair => CovenantHealthTransition.SchemaRepair,
            _ => Result<CovenantHealthTransition>.Failure(
                new Error(
                    ErrorCodes.Covenant.ForbiddenAuthority,
                    "This exclusive operation cannot publish a Covenant authority transition.")),
        };

}
