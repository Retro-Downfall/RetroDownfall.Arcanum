using System.Globalization;
using System.Security.Cryptography;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;

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

        private GateAdmission(CovenantExclusiveRecoveryOwner owner)
        {

            Owner = owner;

        }

        /// <summary>The exact owner the exclusive gate must be acquired with.</summary>
        internal CovenantExclusiveRecoveryOwner Owner { get; }

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
            CovenantExclusiveRecoveryOwner owner,
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
                ? Result<GateAdmission>.Success(new GateAdmission(owner))
                : new Error(
                    ErrorCodes.Covenant.ManualRecoveryRequired,
                    "The Covenant erasure inventory checkpoint could not be committed, so no "
                        + "exclusive owner was issued.");

        }

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
                DataRetentionMutationCheckpointV3.CurrentVersion,
                (operationId, digest) => CovenantRecoveryCheckpointCodec.Encode(
                    new DataRetentionMutationCheckpointV3(
                        DataRetentionMutationCheckpointV3.CurrentVersion,
                        Subtype: "reset-memory",
                        Target: ((int)memoryScope).ToString(CultureInfo.InvariantCulture),
                        new CovenantResetEffectArmV1(
                            operationId,
                            CovenantRecoveryCheckpointCodec.EncodeEffectDigest(digest),
                            CovenantExclusiveOperation.CovenantReset,
                            CovenantResetPhaseMachine.First))),
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
            DataRetentionFactoryResetCheckpointV1.CurrentVersion,
            static (operationId, digest) => CovenantRecoveryCheckpointCodec.Encode(
                new DataRetentionFactoryResetCheckpointV1(
                    DataRetentionFactoryResetCheckpointV1.CurrentVersion,
                    operationId,
                    CovenantRecoveryCheckpointCodec.EncodeEffectDigest(digest),
                    CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
                    CovenantResetPhaseMachine.First)),
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
        Func<Guid, CovenantDigest, byte[]> encode,
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

        return await GateAdmission.CommitInventoryAsync(
            operations,
            operation,
            ownerId,
            new CovenantExclusiveRecoveryOwner(operation.Id, exclusiveOperation, derived.Value),
            kind,
            checkpointVersion,
            encode(operation.Id, derived.Value),
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);

    }

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
