using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;

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
/// <para>The new generation is derived first and swapped second. A derivation failure therefore leaves
/// the previous generation intact and the gate closed, which is recoverable; a swap that half-succeeded
/// would not be.</para>
/// </remarks>
internal sealed class CovenantAuthorityTransitionPublisher(
    CovenantEnvelopeMasterKeyProvider keys,
    CovenantAuthoritySnapshotProvider authority) : ICovenantAuthorityTransitionPublisher
{

    /// <inheritdoc/>
    public async ValueTask<Result> PublishCommittedAsync(
        CovenantCommittedAuthorityTransition transition,
        ICovenantExclusiveOperationLease lease,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(transition);

        ArgumentNullException.ThrowIfNull(lease);

        cancellationToken.ThrowIfCancellationRequested();

        Result live = await lease.RevalidateAsync(cancellationToken).ConfigureAwait(false);

        if (!live.IsSuccess)
        {
            return live;
        }

        CovenantAuthoritySnapshot? current = authority.Current;

        if (current is not null
            && !string.Equals(current.InstallationIdentity, transition.InstallationIdentity, StringComparison.Ordinal))
        {
            return Result.Failure(
                new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "A committed transition cannot change the installation identity."));
        }

        // Monotonic or nothing. A transition that moved a counter backwards would make a replayed
        // token from an earlier generation authenticate again, which is the exact failure every one of
        // these counters exists to prevent.
        if (current is not null
            && (transition.AuthorityEpoch < current.AuthorityEpoch
                || transition.MasterKeyVersion < current.MasterKeyVersion
                || transition.RecoveryEnvelopeEpoch < current.RecoveryEnvelopeEpoch))
        {
            return Result.Failure(
                new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "A committed transition cannot move an authority counter backwards."));
        }

        Result rekeyed = keys.Rekey(transition);

        if (!rekeyed.IsSuccess)
        {
            return rekeyed;
        }

        authority.Publish(
            new CovenantAuthoritySnapshot(
                transition.InstallationIdentity,
                transition.AuthorityEpoch,
                transition.MasterKeyVersion,
                transition.RecoveryEnvelopeEpoch,
                current?.HostToolsState ?? CovenantHostToolsState.Clean,
                current?.TransitionId));

        return Result.Success();

    }

}
