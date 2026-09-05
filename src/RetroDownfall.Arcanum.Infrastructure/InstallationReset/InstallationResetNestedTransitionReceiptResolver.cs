using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

/// <summary>
/// Reads the installation-reset record and decides whether this transition is its nested arm.
/// </summary>
/// <remarks>
/// The whole decision comes out of durable evidence: the outer record either carries a claim naming a
/// nested transition or it does not, and a journal either committed to a binding or it did not. That
/// is why recovery in a fresh process reaches the same answer as first entry — there is nothing here
/// that only a live caller could have supplied.
/// </remarks>
internal sealed class InstallationResetNestedTransitionReceiptResolver(
    IInstallationResetActiveStore store)
    : IGrimoireOfflineTransitionParentReceiptResolver
{

    private readonly IInstallationResetActiveStore _store =
        store ?? throw new ArgumentNullException(nameof(store));

    public async Task<Result<IGrimoireOfflineTransitionParentReceiptSink?>> ResolveAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        GrimoireOfflineTransitionKind kind,
        CovenantDigest nestedEffectDigest,
        CovenantDigest? committedBindingDigest,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        heldInstallationLock.AssertHeldFor(_store.GuardedRoot);

        Result<InstallationResetActiveRecoveryState> inspected = await _store
            .InspectAsync(cancellationToken).ConfigureAwait(false);

        if (inspected.IsFailure)
        {

            return Result<IGrimoireOfflineTransitionParentReceiptSink?>.Failure(inspected.Error);

        }

        if (inspected.Value.Publication is not { } publication
            || publication.Payload.NestedTransitionReceipt is not { } receipt)
        {

            // No outer record, or one that never claimed a nested transition. On first entry that is
            // a standalone erasure. On a resume it is the state §3.6 fails closed on: the journal
            // says it is somebody's nested arm and the record that would say the same is not there.
            return committedBindingDigest is null
                ? Result<IGrimoireOfflineTransitionParentReceiptSink?>.Success(null)
                : Refused();

        }

        // A direct Covenant reset is never nested. The broader workflow's database arm is a
        // healthy-catalog factory erasure and nothing else, so a claim standing beside a reset is two
        // records describing different work under one identity.
        if (kind is not GrimoireOfflineTransitionKind.HealthyCatalogFactoryErasure)
        {

            return Refused();

        }

        if (receipt.Phase is InstallationResetNestedTransitionPhase.Completed
            && receipt.NestedEffectDigest != nestedEffectDigest)
        {

            return Refused();

        }

        Result<CovenantDigest> binding = GrimoireOfflineTransitionParentReceipt.BindingDigest(
            publication.Payload.OperationId,
            receipt.NestedOperationId,
            nestedEffectDigest);

        if (binding.IsFailure)
        {

            return Result<IGrimoireOfflineTransitionParentReceiptSink?>.Failure(binding.Error);

        }

        // A resumed journal names the binding it was published under. The outer record has to
        // reproduce that exact value from its own contents, or the two are not halves of one
        // transition and nothing here may repair the difference.
        return committedBindingDigest is { } committed && committed != binding.Value
            ? Refused()
            : Result<IGrimoireOfflineTransitionParentReceiptSink?>.Success(
                new InstallationResetNestedTransitionReceiptSink(
                    _store,
                    heldInstallationLock,
                    binding.Value,
                    receipt.NestedOperationId,
                    nestedEffectDigest));

    }

    private static Result<IGrimoireOfflineTransitionParentReceiptSink?> Refused() =>
        Result<IGrimoireOfflineTransitionParentReceiptSink?>.Failure(
            new Error(
                ErrorCodes.Covenant.ManualRecoveryRequired,
                "The nested offline transition and its broader workflow record do not agree."));

}

/// <summary>
/// Publishes exactly one completion receipt into the outer record, then proves what it left there.
/// </summary>
/// <remarks>
/// The digest it answers with is recomputed from the record it read back, never from the value it was
/// constructed with. That is the difference between a proof and a restatement: the journal already
/// holds the binding this is compared against, so an answer derived from the same place would assert
/// equality with itself.
/// </remarks>
internal sealed class InstallationResetNestedTransitionReceiptSink(
    IInstallationResetActiveStore store,
    ArcanumMaintenanceLock heldInstallationLock,
    CovenantDigest bindingDigest,
    Guid nestedOperationId,
    CovenantDigest nestedEffectDigest)
    : IGrimoireOfflineTransitionParentReceiptSink
{

    private readonly IInstallationResetActiveStore _store =
        store ?? throw new ArgumentNullException(nameof(store));

    private readonly ArcanumMaintenanceLock _heldInstallationLock =
        heldInstallationLock ?? throw new ArgumentNullException(nameof(heldInstallationLock));

    public CovenantDigest BindingDigest { get; } = bindingDigest;

    public async Task<Result<CovenantDigest>> PublishAndRereadAsync(
        CovenantDigest terminalWinnerDigest,
        CancellationToken cancellationToken)
    {

        _heldInstallationLock.AssertHeldFor(_store.GuardedRoot);

        if (!terminalWinnerDigest.IsValid)
        {

            return Refused<CovenantDigest>();

        }

        InstallationResetNestedTransitionReceiptV1 completed = new(
            Version: 1,
            nestedOperationId,
            InstallationResetNestedTransitionPhase.Completed,
            nestedEffectDigest,
            terminalWinnerDigest);

        Result<InstallationResetActivePublication> current = await ReadAsync(cancellationToken)
            .ConfigureAwait(false);

        if (current.IsFailure)
        {

            return Result<CovenantDigest>.Failure(current.Error);

        }

        if (current.Value.Payload.NestedTransitionReceipt != completed)
        {

            Result<InstallationResetActivePublication> advanced = await _store.AdvanceAsync(
                _heldInstallationLock,
                current.Value,
                current.Value.Payload.ToRecord() with { NestedTransitionReceipt = completed },
                cancellationToken).ConfigureAwait(false);

            if (advanced.IsFailure)
            {

                return Result<CovenantDigest>.Failure(advanced.Error);

            }

        }

        // Reread rather than trust the publication just returned. The value that matters is what a
        // later process would find, and only a fresh authenticated read answers that question.
        Result<InstallationResetActivePublication> reread = await ReadAsync(cancellationToken)
            .ConfigureAwait(false);

        if (reread.IsFailure)
        {

            return Result<CovenantDigest>.Failure(reread.Error);

        }

        return reread.Value.Payload.NestedTransitionReceipt == completed
            ? GrimoireOfflineTransitionParentReceipt.BindingDigest(
                reread.Value.Payload.OperationId,
                reread.Value.Payload.NestedTransitionReceipt.NestedOperationId,
                reread.Value.Payload.NestedTransitionReceipt.NestedEffectDigest!.Value)
            : Refused<CovenantDigest>();

    }

    private async Task<Result<InstallationResetActivePublication>> ReadAsync(
        CancellationToken cancellationToken)
    {

        Result<InstallationResetActiveRecoveryState> inspected = await _store
            .InspectAsync(cancellationToken).ConfigureAwait(false);

        return inspected.IsFailure
            ? Result<InstallationResetActivePublication>.Failure(inspected.Error)
            : inspected.Value.Publication is { } publication
                ? Result<InstallationResetActivePublication>.Success(publication)
                : Refused<InstallationResetActivePublication>();

    }

    private static Result<T> Refused<T>() =>
        Result<T>.Failure(
            new Error(
                ErrorCodes.Covenant.ManualRecoveryRequired,
                "The nested completion receipt could not be published and proved in the broader record."));

}
