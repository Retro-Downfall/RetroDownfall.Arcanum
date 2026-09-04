using System.Security.Cryptography;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// The one way a Covenant reset or a healthy-catalog factory erasure reaches its exclusive gate.
/// </summary>
/// <remarks>
/// The ordering this type enforces is the whole reason it exists. An erasure that closed admission
/// before committing <c>InventoryPrepared</c> would leave a crash window in which the family is
/// closed, the operator is waiting, and nothing durable says which destructive plan is in flight —
/// so the next start could neither resume it nor prove it never began. Making that ordering a rule
/// somebody has to remember is how it eventually gets forgotten, so the admission a caller needs to
/// acquire the gate cannot be constructed at all except by a successful checkpoint commit.
///
/// <para>The gate identity is always the durable server <c>LongRunningOperation.OperationId</c>. An
/// optional caller-supplied requested id remains only the normalized replay key: when one is
/// present the digest derived here must equal the one already written to that identity row, and
/// when it is absent no row exists and this checkpoint is the sole durable effect-digest source.
/// Reading a row that was never written would turn every ordinary server-generated reset into a
/// recovery escalation (§10.20.3).</para>
/// </remarks>
internal sealed class CovenantResetCheckpointInitiator(
    ILongRunningOperationStore operations,
    ICovenantErasureEffectDigestCalculator effectDigests,
    CovenantHealthyCatalogErasureGuard healthyCatalog,
    ICovenantOperationGate operationGate,
    ICovenantErasureInventorySource inventory,
    TimeProvider timeProvider)
{

    /// <summary>
    /// Proof that the first phase committed, and the only carrier of the owner it committed.
    /// </summary>
    /// <remarks>
    /// Its constructor is private and <see cref="CommitInventoryAsync"/> is the only code that can
    /// reach it — a nested type's private members are not visible to its enclosing type, so not even
    /// <see cref="CovenantResetCheckpointInitiator"/> can mint one another way. That method commits
    /// the checkpoint first and returns a failure rather than an admission when the commit is lost,
    /// so holding one of these is holding evidence rather than an intention.
    ///
    /// <para>An <c>internal</c> factory taking an already-built owner would have left the ordering
    /// documented rather than enforced: anything else in this assembly — the erasure coordinator
    /// included — could then acquire the exclusive gate with no checkpoint behind it, which is the
    /// exact crash window this type exists to close.</para>
    /// </remarks>
    internal sealed class GateAdmission
    {

        private GateAdmission(GrimoireOfflineTransitionLaunchBinding launch)
        {

            Launch = launch;

        }

        /// <summary>
        /// The launch this admission was issued against, projected from the exact bytes that won.
        /// </summary>
        /// <remarks>
        /// The binding rather than the owner, because the owner is only three of its eleven fields.
        /// What the admission now asserts is not merely that a checkpoint row was written but that a
        /// launch this build can bind a journal to was written — which is the strongest statement
        /// available at this point in the authority order, and the one the closed period later has to
        /// be able to check its journal against.
        /// </remarks>
        internal GrimoireOfflineTransitionLaunchBinding Launch { get; }

        /// <summary>The exact owner the exclusive gate must be acquired with.</summary>
        /// <remarks>
        /// Derived rather than stored. Two copies of the same three fields could disagree, and the
        /// one that decides which closed scope is adopted must be the one the launch records.
        /// </remarks>
        internal CovenantExclusiveRecoveryOwner Owner =>
            new(Launch.OperationId, Launch.Operation, Launch.EffectDigest);

        /// <summary>The phase the committed checkpoint records.</summary>
        internal CovenantResetPhase Phase => CovenantResetPhaseMachine.First;

        /// <summary>
        /// Commits the first checkpoint and, only if that commit wins, issues the admission.
        /// </summary>
        /// <remarks>
        /// Expected version 0: this is the operation's first checkpoint, so a row that already
        /// carries one is either a resume — which belongs to recovery rather than to a fresh
        /// preparation — or a second caller under the same lease.
        /// </remarks>
        internal static async Task<Result<GateAdmission>> CommitInventoryAsync(
            ILongRunningOperationStore operations,
            LongRunningOperation operation,
            string ownerId,
            GrimoireOfflineTransitionLaunchBinding launch,
            string kind,
            int checkpointVersion,
            byte[] payload,
            DateTimeOffset utcNow,
            CancellationToken cancellationToken)
        {

            bool committed = await operations.SaveCheckpointAsync(
                operation.Id,
                ownerId,
                expectedCheckpointVersion: 0,
                checkpointVersion,
                payload,
                checkpointReference: CheckpointReference(kind, operation.Id),
                operation.PublicSummary,
                utcNow,
                cancellationToken).ConfigureAwait(false);

            return committed
                ? Result<GateAdmission>.Success(new GateAdmission(launch))
                : new Error(
                    ErrorCodes.Covenant.ManualRecoveryRequired,
                    "The Covenant erasure inventory checkpoint could not be committed, so no "
                        + "exclusive owner was issued.");

        }

    }

    /// <summary>The preselected half of a launch: the one target this transition may stamp.</summary>
    private readonly record struct CovenantOfflineTransitionTarget(
        Guid DatasetGeneration,
        CovenantOfflineTransitionEpochsV1 Epochs);

    /// <summary>Everything a launch shape needs, gathered once so the two arms differ only in shape.</summary>
    private readonly record struct CovenantOfflineTransitionLaunchInputs(
        Guid OperationId,
        CovenantDigest EffectDigest,
        LongRunningOperationRecoveryPolicy RecoveryPolicy,
        CovenantOfflineTransitionSourceState Source,
        CovenantOfflineTransitionTarget Target,
        long StartingRevision)
    {

        internal Guid TargetDatasetGeneration => Target.DatasetGeneration;

        internal CovenantOfflineTransitionEpochsV1 TargetEpochs => Target.Epochs;

    }

    /// <summary>
    /// Commits the <c>InventoryPrepared</c> checkpoint of a Covenant memory reset.
    /// </summary>
    internal Task<Result<GateAdmission>> PrepareCovenantResetInventoryAsync(
        LongRunningOperation operation,
        string ownerId,
        CovenantErasureEffectDigestInput effect,
        Guid? requestedOperationId,
        MemoryResetScope memoryScope,
        CancellationToken cancellationToken) =>
        memoryScope is MemoryResetScope.Covenant
            ? PrepareAsync(
                operation,
                ownerId,
                effect,
                requestedOperationId,
                CovenantExclusiveOperation.CovenantReset,
                LongRunningOperationKinds.DataRetentionMutation,
                CovenantOfflineTransitionLaunchV4.CurrentVersion,
                static launch => Launchable(
                    new CovenantOfflineTransitionLaunchV4(
                        CovenantOfflineTransitionLaunchV4.CurrentVersion,
                        launch.OperationId,
                        LongRunningOperationKinds.DataRetentionMutation,
                        launch.RecoveryPolicy.ToString(),
                        CovenantExclusiveOperation.CovenantReset,
                        CovenantRecoveryCheckpointCodec.EncodeEffectDigest(launch.EffectDigest),
                        launch.Source.DatasetGeneration,
                        launch.TargetDatasetGeneration,
                        Epochs(launch.Source),
                        launch.TargetEpochs,
                        launch.StartingRevision),
                    CovenantRecoveryCheckpointCodec.IsLaunchable,
                    CovenantRecoveryCheckpointCodec.Encode),
                cancellationToken)
            : Task.FromResult(
                Result<GateAdmission>.Failure(
                    new Error(
                        ErrorCodes.Covenant.InvalidScope,
                        "Only the Covenant memory-reset scope prepares a Covenant reset inventory.")));

    /// <summary>
    /// Commits the <c>InventoryPrepared</c> checkpoint of a healthy-catalog factory erasure.
    /// </summary>
    internal async Task<Result<GateAdmission>> PrepareFactoryErasureInventoryAsync(
        LongRunningOperation operation,
        string ownerId,
        CovenantErasureEffectDigestInput effect,
        Guid? requestedOperationId,
        CancellationToken cancellationToken)
    {

        Result<CovenantInstallationReadLease> acquired = await operationGate
            .AcquireInstallationReadAsync(cancellationToken)
            .ConfigureAwait(false);

        if (acquired.IsFailure)
        {

            return Result<GateAdmission>.Failure(acquired.Error);

        }

        await using CovenantInstallationReadLease readLease = acquired.Value;

        return await PrepareFactoryErasureInventoryCoreAsync(
            operation,
            ownerId,
            effect,
            requestedOperationId,
            readLease,
            requireExactSnapshot: false,
            cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Commits a factory-erasure inventory under the caller's exact installation planning lease.
    /// </summary>
    /// <remarks>
    /// Ownership stays with the caller. This overload neither acquires nor disposes a capability, so
    /// the snapshot used to build the confirmed plan remains live through catalog proof, lease
    /// revalidation, and the V1 checkpoint commit.
    /// </remarks>
    internal async Task<Result<GateAdmission>> PrepareFactoryErasureInventoryAsync(
        LongRunningOperation operation,
        string ownerId,
        CovenantErasureEffectDigestInput effect,
        Guid? requestedOperationId,
        CovenantInstallationReadLease readLease,
        CancellationToken cancellationToken) =>
        await PrepareFactoryErasureInventoryCoreAsync(
            operation,
            ownerId,
            effect,
            requestedOperationId,
            readLease,
            requireExactSnapshot: true,
            cancellationToken).ConfigureAwait(false);

    private async Task<Result<GateAdmission>> PrepareFactoryErasureInventoryCoreAsync(
        LongRunningOperation operation,
        string ownerId,
        CovenantErasureEffectDigestInput effect,
        Guid? requestedOperationId,
        CovenantInstallationReadLease readLease,
        bool requireExactSnapshot,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(effect);

        ArgumentNullException.ThrowIfNull(readLease);

        if (requireExactSnapshot
            && (readLease.Snapshot.Kind is not CovenantLeaseKind.InstallationRead
                || readLease.Snapshot.Coverage is not CovenantLeaseCoverage.Installation
                || readLease.Snapshot.DatasetGeneration is not { } datasetGeneration
                || datasetGeneration == Guid.Empty
                || datasetGeneration != effect.DatasetGeneration))
        {

            return Result<GateAdmission>.Failure(
                new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "Healthy-catalog factory erasure requires its exact installation planning snapshot."));

        }

        Result healthy = await healthyCatalog
            .RequireHealthyAsync(cancellationToken)
            .ConfigureAwait(false);

        if (healthy.IsFailure)
        {

            return Result<GateAdmission>.Failure(healthy.Error);

        }

        Result current = await readLease.RevalidateAsync(cancellationToken).ConfigureAwait(false);

        if (current.IsFailure)
        {

            return Result<GateAdmission>.Failure(current.Error);

        }

        // The installation read lease stays live through digest derivation and the successful
        // checkpoint commit. A competing exclusive acquisition closes admission and waits for this
        // method to return, so it cannot replace the catalog between proof and durable preparation.
        return await PrepareAsync(
            operation,
            ownerId,
            effect,
            requestedOperationId,
            CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
            LongRunningOperationKinds.DataRetentionFactoryReset,
            DataRetentionFactoryTransitionLaunchV2.CurrentVersion,
            static launch => Launchable(
                new DataRetentionFactoryTransitionLaunchV2(
                    DataRetentionFactoryTransitionLaunchV2.CurrentVersion,
                    launch.OperationId,
                    LongRunningOperationKinds.DataRetentionFactoryReset,
                    launch.RecoveryPolicy.ToString(),
                    CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
                    CovenantRecoveryCheckpointCodec.EncodeEffectDigest(launch.EffectDigest),
                    launch.Source.DatasetGeneration,
                    launch.TargetDatasetGeneration,
                    Epochs(launch.Source),
                    launch.TargetEpochs,
                    launch.StartingRevision),
                CovenantRecoveryCheckpointCodec.IsLaunchable,
                CovenantRecoveryCheckpointCodec.Encode),
            cancellationToken).ConfigureAwait(false);

    }

    private async Task<Result<GateAdmission>> PrepareAsync(
        LongRunningOperation operation,
        string ownerId,
        CovenantErasureEffectDigestInput effect,
        Guid? requestedOperationId,
        CovenantExclusiveOperation exclusiveOperation,
        string kind,
        int checkpointVersion,
        Func<CovenantOfflineTransitionLaunchInputs, Result<byte[]>> encode,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(operation);

        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        ArgumentNullException.ThrowIfNull(effect);

        // The effect input names the operation it is for. Accepting the other one's would mint an
        // owner whose code and whose digest domain disagreed about what the operation is. The
        // identity check is defensive rather than expected: a ledger row with no id is corruption,
        // and a typed refusal surfaces it where the recovery paths already refuse the same shape
        // rather than throwing out of a planner.
        if (operation.Id == Guid.Empty
            || effect.Operation != exclusiveOperation
            || !string.Equals(operation.Kind, kind, StringComparison.Ordinal))
        {

            return new Error(
                ErrorCodes.Covenant.InvalidScope,
                "This erasure plan does not belong to the operation it was asked to prepare.");

        }

        Result<CovenantDigest> derived = effectDigests.Compute(effect);

        if (derived.IsFailure)
        {

            return Result<GateAdmission>.Failure(derived.Error);

        }

        if (requestedOperationId is { } requested)
        {

            Result verified = await VerifyRequestIdentityAsync(
                operation.Id,
                requested,
                derived.Value,
                cancellationToken).ConfigureAwait(false);

            if (verified.IsFailure)
            {

                return Result<GateAdmission>.Failure(verified.Error);

            }

        }

        // Read under the caller's still-live planning lease, so the tuple cannot move between the
        // read and the commit: a competing exclusive acquisition has to close admission and wait for
        // that lease before it can touch the canonical singleton.
        Result<CovenantOfflineTransitionSourceState> read = await inventory
            .ReadOfflineTransitionSourceStateAsync(cancellationToken)
            .ConfigureAwait(false);

        if (read.IsFailure)
        {

            return Result<GateAdmission>.Failure(read.Error);

        }

        // The source tuple and the effect digest describe the same plan or they describe two, and a
        // launch that bound one generation while its digest was computed over another would verify a
        // replaced family against a plan nobody made.
        if (read.Value.DatasetGeneration != effect.DatasetGeneration)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "This erasure plan and the canonical source state it would launch against name "
                    + "different datasets.");

        }

        // Read immediately before the commit rather than trusting the instance the caller passed in:
        // a lease renewal advances the revision, and a launch that recorded a stale one would name a
        // revision the journal could never be bound past.
        LongRunningOperation? current = await operations
            .GetAsync(operation.Id, cancellationToken)
            .ConfigureAwait(false);

        if (current is null || !string.Equals(current.LeaseOwner, ownerId, StringComparison.Ordinal))
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "This erasure no longer holds the durable operation it was asked to prepare.");

        }

        Result<byte[]> payload = encode(
            new CovenantOfflineTransitionLaunchInputs(
                operation.Id,
                derived.Value,
                current.RecoveryPolicy,
                read.Value,
                Preselect(read.Value),
                current.Revision));

        if (payload.IsFailure)
        {

            return Result<GateAdmission>.Failure(payload.Error);

        }

        // Projected from the exact bytes about to be committed rather than assembled beside them. A
        // hand-built owner and a decoded launch could disagree about the same row, and the admission
        // is what later proves a journal belongs to this launch - so it has to be the row's own
        // reading of itself, refused here if this build cannot make one.
        Result<GrimoireOfflineTransitionLaunchBinding> launch =
            GrimoireOfflineTransitionLaunch.FromCommittedCheckpoint(checkpointVersion, payload.Value);

        if (launch.IsFailure)
        {

            return Result<GateAdmission>.Failure(launch.Error);

        }

        if (launch.Value.Operation != exclusiveOperation)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "The encoded launch does not describe the exclusive operation it was prepared for.");

        }

        return await GateAdmission.CommitInventoryAsync(
            operations,
            operation,
            ownerId,
            launch.Value,
            kind,
            checkpointVersion,
            payload.Value,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// The one target this transition will ever stamp, chosen before it can close anything.
    /// </summary>
    /// <remarks>
    /// Preselected rather than generated inside the canonical transaction because that transaction
    /// stamps a generation and advances all three epochs in one statement: a recovery pass that found
    /// the family already replaced could not otherwise tell its own commit from an unrelated one.
    ///
    /// <para>The generation is cryptographically random for the same reason the canonical installer's
    /// is — it is compared by equality across processes and snapshots, and a value predictable from
    /// the last one would let a stale reader guess its way past that comparison. Each epoch is the
    /// successor of its own source rather than of some source, so a transposed pair cannot satisfy a
    /// rule that only asked whether every target was one more than something.</para>
    /// </remarks>
    private static CovenantOfflineTransitionTarget Preselect(CovenantOfflineTransitionSourceState source) =>
        new(
            new Guid(RandomNumberGenerator.GetBytes(16)),
            new CovenantOfflineTransitionEpochsV1(
                source.AcceleratorEpoch + 1,
                source.KeyReclamationEpoch + 1,
                source.EnvelopeKeyEpoch + 1));

    private static CovenantOfflineTransitionEpochsV1 Epochs(CovenantOfflineTransitionSourceState source) =>
        new(source.AcceleratorEpoch, source.KeyReclamationEpoch, source.EnvelopeKeyEpoch);

    /// <summary>
    /// Encodes a launch only if it is one the decoder would accept back.
    /// </summary>
    /// <remarks>
    /// The predicate the codec already uses is the predicate applied here, rather than a second list
    /// of the same rules. Two rules about what a launch is would agree on the day they were written
    /// and diverge on the first change, and the half that disagreed would be the half that committed
    /// a destructive plan the other half could not read back.
    /// </remarks>
    private static Result<byte[]> Launchable<TLaunch>(
        TLaunch launch,
        Func<TLaunch, bool> isLaunchable,
        Func<TLaunch, byte[]> encode) =>
        isLaunchable(launch)
            ? Result<byte[]>.Success(encode(launch))
            : new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "The preselected offline-transition launch is not one this build would read back.");

    /// <summary>
    /// The requested arm's digest has to equal the normalized identity row's.
    /// </summary>
    /// <remarks>
    /// Fixed-time, because the comparison decides whether a replayed request may adopt an owner it
    /// did not name. A missing row under a supplied name is a refusal rather than an absence: the
    /// caller asserted a durable identity that the ledger does not have.
    /// </remarks>
    private async Task<Result> VerifyRequestIdentityAsync(
        Guid operationId,
        Guid requestedOperationId,
        CovenantDigest derived,
        CancellationToken cancellationToken)
    {

        LongRunningOperationRequestIdentity? identity = await operations
            .FindRequestIdentityAsync(operationId, cancellationToken)
            .ConfigureAwait(false);

        if (identity is null || identity.RequestedOperationId != requestedOperationId)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "This operation carries no request identity under the name the caller supplied.");

        }

        return CryptographicOperations.FixedTimeEquals(identity.EffectDigest.Bytes, derived.Bytes)
            ? Result.Success()
            : new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "The derived erasure effect digest does not match the one this operation was named "
                    + "under.");

    }

    internal static string CheckpointReference(string kind, Guid operationId) =>
        kind switch
        {

            LongRunningOperationKinds.DataRetentionMutation =>
                "retention-mutation:" + operationId.ToString("N"),

            _ => "retention-factory-reset:" + operationId.ToString("N"),

        };

}
