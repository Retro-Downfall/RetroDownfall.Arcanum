using System.Data;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

internal interface ILongRestSweepRequestAdmission
{
    /// <param name="scope">The exact sweep scope the request is for.</param>
    /// <param name="fence">The independently captured request fence.</param>
    /// <param name="facts">The live fence reader the transaction re-proves the fence with.</param>
    /// <param name="ownership">The in-process ownership claims.</param>
    /// <param name="currentRecipe">
    /// The recipe the installation runs now. A protected park whose generation was captured under another recipe (a code-owned version
    /// moved with an upgrade) can never resume, and is judged by this.
    /// </param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="proveAuthority">
    /// Present only when the request carries the live turn authority of a Session turn for a protected partition: it re-proves that
    /// authority at the moment a protected park that can never resume would be retired, and a request without it never retires one.
    /// </param>
    /// <param name="replayRecipe">
    /// Present when the caller looked for a completed sweep before it reached this transaction: admission looks once more, inside the
    /// transaction, before it would create a generation, because another request may have completed its replacement in between, or
    /// completed its generation and not yet settled its operation.
    /// </param>
    Task<Result<LongRunningOperation>> AdmitRequestAsync(LongRestSweepScope scope, LongRestSweepFence fence,
        LongRestLiveFenceReader facts, LongRunningOperationOwnership ownership, LongRestRecipeIdentity currentRecipe, CancellationToken ct,
        Func<CancellationToken, ValueTask<Result>>? proveAuthority = null, LongRestRecipeIdentity? replayRecipe = null);
}

internal sealed partial class LongRestSweepStore : ILongRestSweepRequestAdmission
{
    private const string StaleRequestConfiguration = LongRestParkCodes.StaleConfiguration;

    public async Task<Result<LongRunningOperation>> AdmitRequestAsync(LongRestSweepScope scope, LongRestSweepFence fence,
        LongRestLiveFenceReader facts, LongRunningOperationOwnership ownership, LongRestRecipeIdentity currentRecipe, CancellationToken ct,
        Func<CancellationToken, ValueTask<Result>>? proveAuthority = null, LongRestRecipeIdentity? replayRecipe = null)
    {
        try
        {
            if (!scope.IsInitialized || fence.Configuration is not { IsValid: true } || currentRecipe is null
                || scope.CampaignId != fence.CanonicalScope.CampaignId
                || (scope.Kind == LongRestSweepScopeKind.Campaign) != fence.CanonicalScope.IsCampaignBound) throw new InvalidDataException();

            return await SqliteBusyRetry.ExecuteAsync(() => AdmitOnceAsync(scope, fence, facts, ownership, currentRecipe, proveAuthority, replayRecipe, ct), ct).ConfigureAwait(false);
        }
        catch (OperationBusyException)
        {
            // Another executor in this process owns the sweep this request would have to replace. Nothing was written.
            return new Error(ErrorCodes.LongRest.OperationBusy, "Another executor in this process owns the active sweep for this scope right now.");
        }
        catch (AuthorityRefusedException refused)
        {
            // The turn authority the request carried is not live for this exact scope: nothing was written, and the park stays.
            return refused.Error;
        }
        catch (SweepParkedException parked)
        {
            // The identifier is content-free and is what `operation retry` and `operation cancel` take. A park under the Unknown-Usage
            // Cap is the one a retry cannot clear: the cap is per work unit, so a retried sweep re-trips it before any send, and only a
            // cancel (which starts a new generation with fresh units) resolves it. The message names only a remedy that works.
            string remedy = parked.TerminalErrorCode == ErrorCodes.LongRest.TransportUnknown ? "cancel it" : "retry or cancel it";

            return new Error(ErrorCodes.LongRest.SweepParked,
                $"The active sweep for this scope (operation {parked.OperationId:D}) is parked and needs an operator to {remedy}.");
        }
        catch (Exception exception) when (exception is SqliteException or InvalidDataException or ArgumentException
            or FormatException or OverflowException or JsonException or InvalidOperationException)
        {
            return new Error(ErrorCodes.LongRest.StaleInput, "The sweep request could not prove its exact current admission.");
        }
    }

    private async Task<LongRunningOperation> AdmitOnceAsync(LongRestSweepScope scope, LongRestSweepFence fence,
        LongRestLiveFenceReader facts, LongRunningOperationOwnership ownership, LongRestRecipeIdentity currentRecipe,
        Func<CancellationToken, ValueTask<Result>>? proveAuthority, LongRestRecipeIdentity? replayRecipe, CancellationToken ct)
    {
        Guid claimed = Guid.Empty;

        Guid token = Guid.Empty;

        bool retiredByAuthority = false;

        try
        {
            SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();

            if (connection.State != ConnectionState.Open) await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);

            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            LongRestVerificationCounters.Transaction(deferred: false);

            if (!await facts.ValidateRequestFenceWithinAsync(connection, transaction, fence, ct).ConfigureAwait(false)) throw new InvalidDataException();

            string stamp = JsonSerializer.Serialize(fence.Configuration, LongRestJsonContext.Default.LongRestConfigurationStamp);

            ActiveRequest? active = await ReadActiveRequestAsync(connection, transaction, scope, ct).ConfigureAwait(false);

            LongRunningOperation admitted;

            if (active is not null)
            {
                LongRunningOperation operation = await LongRunningOperationStore.ReadWithinAsync(connection, transaction, active.OperationId, ct).ConfigureAwait(false)
                    ?? throw new InvalidDataException();

                if (operation.Kind != LongRunningOperationKinds.LongRestSweep
                    || operation.State == LongRunningOperationState.Completed) throw new InvalidDataException();

                LongRestConfigurationStamp stored = JsonSerializer.Deserialize(active.ConfigurationJson, LongRestJsonContext.Default.LongRestConfigurationStamp)
                    ?? throw new InvalidDataException();

                if (!stored.IsValid) throw new InvalidDataException();

                if (operation.State == LongRunningOperationState.ReconciliationRequired)
                {
                    // Parked-Is-Active: a parked sweep still owns its scope. A request never claims, resets or runs it. It keeps that
                    // scope while its generation can still revalidate against the current stamp: the stamp is identical, or only the
                    // boot differs (a restart is why it parked). The same rule retires a never-started sweep, and it is the one
                    // historical validation applies to the generation's own fence.
                    ParkedReplacement? replacement = ReplacementOfPark(scope, operation, active, stored, fence, proveAuthority is not null);

                    // A protected park that waits for a fresh Session request waits for one that can readmit it. When its generation can no
                    // longer resume under the live installation, no turn ever will, and every turn that needs the partition would end: the
                    // recipe it was captured under is not the one in force (a code-owned version moved), the fence moved (a maintenance
                    // window drained inside the boot that parked it, recovered authority, a change of Covenant capability, a moved dataset
                    // or Campaign root), or a source it froze can no longer be proved (the suppressed source of an earlier consolidation
                    // was erased). A request that carries live authority retires it exactly as it retires one parked under a fault no
                    // revalidation survives, and admits a new generation.
                    if (replacement is null && proveAuthority is not null && scope.ProtectedPartitionDigest is not null
                        && await FindResumeDriftAsync(connection, transaction, operation.Id, fence, currentRecipe, ct).ConfigureAwait(false) is not null)
                    {
                        replacement = new(ErrorCodes.LongRest.StaleInput, true);
                    }

                    if (replacement is null)
                    {
                        // The sweep is returned unchanged for its own authority class to readmit.
                        admitted = operation;
                    }
                    else
                    {
                        // The generation can never resume: its value digest changed, or only the epoch did inside one boot (a configuration
                        // flip A to B to A bumps it twice), or a protected sweep faulted in a way no revalidation survives and this request
                        // carries live authority for it. Retire it as a terminal sweep and replace it, evidence preserved.
                        if (operation.RecoveryPolicy != LongRunningOperationRecoveryPolicy.ResumeFromCheckpoint
                            || operation.LeaseOwner is not null || operation.LeaseExpiresAt is not null) throw new InvalidDataException();

                        if (replacement.ByAuthority)
                        {
                            Result proof = await proveAuthority!(ct).ConfigureAwait(false);

                            if (proof.IsFailure) throw new AuthorityRefusedException(proof.Error);

                            retiredByAuthority = true;
                        }

                        if (!ownership.TryClaim(operation.Id, out token)) throw new OperationBusyException();

                        claimed = operation.Id;

                        LongRunningOperation abandoned = await AbandonParkedWithinAsync(connection, transaction, operation, replacement.TerminalCode, ct).ConfigureAwait(false);

                        await RetireTerminalRequestWithinAsync(connection, transaction, scope, active, abandoned, ct).ConfigureAwait(false);

                        admitted = await CreateRequestWithinAsync(connection, transaction, scope, stamp, ct).ConfigureAwait(false);
                    }
                }
                else if (operation.State is LongRunningOperationState.Failed or LongRunningOperationState.Abandoned)
                {
                    if (operation.RecoveryPolicy != LongRunningOperationRecoveryPolicy.ResumeFromCheckpoint
                        || operation.CompletedAt is null || operation.LeaseOwner is not null || operation.LeaseExpiresAt is not null) throw new InvalidDataException();

                    if (!ownership.TryClaim(operation.Id, out token)) throw new OperationBusyException();

                    claimed = operation.Id;

                    await RetireTerminalRequestWithinAsync(connection, transaction, scope, active, operation, ct).ConfigureAwait(false);

                    admitted = await CreateRequestWithinAsync(connection, transaction, scope, stamp, ct).ConfigureAwait(false);
                }
                else if (stored == fence.Configuration)
                {
                    admitted = operation;
                }
                else
                {
                    if (scope.ProtectedPartitionDigest is not null || stored.ValueDigest != fence.Configuration.ValueDigest) throw new InvalidDataException();

                    if (!ownership.TryClaim(operation.Id, out token)) throw new OperationBusyException();

                    claimed = operation.Id;

                    // The immediate transaction and exact process claim enclose proof, writes, commit and disposal.
                    if (!await IsNeverStartedCleanAsync(connection, transaction, operation.Id, operation.Revision, active.Revision,
                        active.ConfigurationJson, ct).ConfigureAwait(false)) throw new InvalidDataException();

                    if (!await facts.ValidateRequestFenceWithinAsync(connection, transaction, fence, ct).ConfigureAwait(false)) throw new InvalidDataException();

                    if (stored.BootId != fence.Configuration.BootId)
                    {
                        await ChangeSweepAdmissionAsync(connection, transaction, active, stamp, LongRestSweepPhase.Capture, ct).ConfigureAwait(false);

                        admitted = operation;
                    }
                    else
                    {
                        await ChangeSweepAdmissionAsync(connection, transaction, active, active.ConfigurationJson, LongRestSweepPhase.Invalidated, ct).ConfigureAwait(false);

                        await using SqliteCommand retire = Command(connection, transaction, """
                            UPDATE LongRunningOperations SET State = $abandoned, CompletedAt = $at,
                                TerminalErrorCode = $error, Revision = Revision + 1
                            WHERE Id = $operation AND Revision = $revision AND State = $pending
                                AND AttemptCount = 0 AND CheckpointVersion = 0 AND LeaseOwner IS NULL AND LeaseExpiresAt IS NULL;
                            """, ("$operation", operation.Id.ToString("N")), ("$revision", operation.Revision),
                            ("$pending", (int)LongRunningOperationState.Pending), ("$abandoned", (int)LongRunningOperationState.Abandoned),
                            ("$at", UtcInstantText.Format(timeProvider.GetUtcNow())), ("$error", StaleRequestConfiguration));

                        if (await retire.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) throw new InvalidDataException();

                        admitted = await CreateRequestWithinAsync(connection, transaction, scope, stamp, ct).ConfigureAwait(false);
                    }
                }
            }
            else
            {
                // Nothing is active, so this transaction would create a generation, which is paid work. The caller's lookup preceded the
                // transaction, and a sweep may have completed since: the replacement of one it found stale, or one whose generation is
                // complete while its operation has not yet reached Completed (the run completes the generation, and settles the operation
                // in a later write). A sweep that now describes the installation is the answer, whichever step its operation is on, and
                // no second generation is created beside it.
                LongRunningOperation? joined = null;

                if (replayRecipe is { } recipe)
                {
                    joined = await FindCurrentCompletedWithinAsync(connection, transaction, scope, fence, recipe, ct).ConfigureAwait(false);

                    // A completed generation that does not describe the installation, parked for the authority class that would readmit
                    // it, is one nothing can readmit: it is settled here, so it does not hold its scope degraded for ever.
                    if (joined is null && await FindStrandedCompletedParkAsync(connection, transaction, scope, ct).ConfigureAwait(false) is { } stranded)
                    {
                        if (!ownership.TryClaim(stranded.Id, out token)) throw new OperationBusyException();

                        claimed = stranded.Id;

                        _ = await AbandonParkedWithinAsync(connection, transaction, stranded, StaleRequestConfiguration, ct).ConfigureAwait(false);
                    }
                }

                admitted = joined ?? await CreateRequestWithinAsync(connection, transaction, scope, stamp, ct).ConfigureAwait(false);
            }

            if (!await facts.ValidateRequestFenceWithinAsync(connection, transaction, fence, ct).ConfigureAwait(false)
                || !facts.IsRequestFenceCurrent(fence)) throw new InvalidDataException();

            if (retiredByAuthority)
            {
                // The authority that freed the partition must still be live at the commit, like the fence it was proved against.
                Result proof = await proveAuthority!(ct).ConfigureAwait(false);

                if (proof.IsFailure) throw new AuthorityRefusedException(proof.Error);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);

            return admitted;
        }
        finally
        {
            if (claimed != Guid.Empty) ownership.Release(claimed, token);
        }
    }

    /// <summary>
    /// Whether the parked sweep is one its own authority class will readmit. A protected sweep waits for a fresh Session
    /// request. A clean sweep waits for the worker, unless its fault is not resumable or its failure budget is spent.
    /// </summary>
    private static bool IsResumablePark(LongRestSweepScope scope, LongRunningOperation operation, ActiveRequest active)
    {
        if (scope.ProtectedPartitionDigest is not null) return operation.TerminalErrorCode == LongRestParkCodes.AuthorizedRequestRequired;

        if (LongRestFaultPolicy.Classify(operation.TerminalErrorCode) != LongRestFaultClass.ResumeLater) return false;

        return LongRestFaultPolicy.IsFreePark(operation.TerminalErrorCode) || LongRestFaultPolicy.CanReadmit(active.ReadmitCount);
    }

    /// <summary>
    /// What a request does with the parked sweep that owns its scope: nothing (<see langword="null"/>, the park is returned unchanged
    /// for its own authority class to readmit), or replacement by a new sweep. A stored configuration that can no longer revalidate is
    /// replaced as <c>long_rest.stale_configuration</c>. A protected sweep parked with a restart-generation fault (its captured
    /// generation can never complete, and nothing automatic may readmit a protected sweep) is replaced under that fault's own code, and
    /// only by a request that carries live turn authority (Fresh-Authority Retire); any other park is refused as a sweep only an operator
    /// can clear.
    /// </summary>
    private static ParkedReplacement? ReplacementOfPark(LongRestSweepScope scope, LongRunningOperation operation, ActiveRequest active,
        LongRestConfigurationStamp stored, LongRestSweepFence fence, bool carriesAuthority)
    {
        if (stored.ValueDigest != fence.Configuration.ValueDigest
            || stored != fence.Configuration && stored.BootId == fence.Configuration.BootId)
        {
            return new(StaleRequestConfiguration, false);
        }

        if (IsResumablePark(scope, operation, active)) return null;

        if (carriesAuthority && scope.ProtectedPartitionDigest is not null && operation.TerminalErrorCode is { } code
            && LongRestFaultPolicy.Classify(code) == LongRestFaultClass.RestartGeneration)
        {
            return new(code, true);
        }

        throw new SweepParkedException(operation.Id, operation.TerminalErrorCode);
    }

    /// <summary>Why a parked sweep's captured generation can never resume under the live installation.</summary>
    private enum ResumeDrift
    {
        /// <summary>It was captured under a recipe other than the one in force: a code-owned version moved.</summary>
        Recipe,

        /// <summary>The live fence moved where <see cref="LongRestFenceCurrency.CanResume"/> does not allow it to.</summary>
        Fence,

        /// <summary>A source the generation froze can no longer be proved against its stored facts.</summary>
        FrozenSources,
    }

    /// <summary>
    /// What, if anything, keeps the parked sweep's captured generation from ever resuming under the live installation: the questions a
    /// maintenance capability is created under, asked in the order it asks them, so the park is retired exactly when the turn that would
    /// have readmitted it would have been refused as stale. All three read metadata and never a protected source's content. A sweep
    /// with no generation yet has nothing to have drifted.
    /// </summary>
    /// <remarks>
    /// The recipe comes first, from the stored digest alone, because a recipe the current code owns differently may be one the stored
    /// generation can no longer be parsed under at all (a fixed bound moved), and nothing else about such a generation can be asked.
    /// A version that still parses (a scorer or an adjudication version) is the same question, and the same answer.
    /// </remarks>
    private static async Task<ResumeDrift?> FindResumeDriftAsync(SqliteConnection connection, SqliteTransaction transaction, Guid operationId,
        LongRestSweepFence live, LongRestRecipeIdentity currentRecipe, CancellationToken ct)
    {
        await using (SqliteCommand digest = Command(connection, transaction,
            "SELECT RecipeDigest FROM long_rest_discovery_generations WHERE OperationId = $operation;", ("$operation", operationId.ToString("N"))))
        {
            if (await digest.ExecuteScalarAsync(ct).ConfigureAwait(false) is not byte[] stored)
            {
                return null;
            }

            Result<CovenantDigest> current = LongRestDiscoveryHash.Recipe(currentRecipe);

            if (current.IsFailure)
            {
                throw new InvalidDataException();
            }

            if (!stored.AsSpan().SequenceEqual(current.Value.Bytes))
            {
                return ResumeDrift.Recipe;
            }
        }

        LongRestDiscoveryGeneration generation = await LongRestDiscoveryStore.ReadSweepGenerationAsync(connection, transaction, operationId, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException();

        if (!LongRestFenceCurrency.CanResume(generation.Fence, live))
        {
            return ResumeDrift.Fence;
        }

        try
        {
            // The seam question a capability over a retained generation is issued under.
            await LongRestDiscoveryStore.VerifyFrozenMetadataAsync(connection, transaction, generation, LongRestVerificationDepth.Seam, ct).ConfigureAwait(false);

            return null;
        }
        catch (InvalidDataException)
        {
            return ResumeDrift.FrozenSources;
        }
    }

    /// <summary>
    /// Whether the park is one its own authority class readmits: a protected sweep waits for a fresh Session request, and a clean sweep
    /// for the worker, unless its fault is one only an operator can clear.
    /// </summary>
    private static bool IsReadmittablePark(LongRestSweepScope scope, LongRunningOperation operation) => scope.ProtectedPartitionDigest is not null
        ? operation.TerminalErrorCode == LongRestParkCodes.AuthorizedRequestRequired
        : LongRestFaultPolicy.Classify(operation.TerminalErrorCode) == LongRestFaultClass.ResumeLater;

    // A sweep that completed its generation is joined whichever step its operation has reached, except one an operator has already
    // started to cancel or one that ended: those do not answer a request.
    private static readonly int JoinableOperationStates = StateMask(LongRunningOperationState.Pending, LongRunningOperationState.Running,
        LongRunningOperationState.Waiting, LongRunningOperationState.ReconciliationRequired, LongRunningOperationState.Completed);

    private static int StateMask(params LongRunningOperationState[] states)
    {
        int mask = 0;

        foreach (LongRunningOperationState state in states)
        {
            mask |= 1 << (int)state;
        }

        return mask;
    }

    /// <summary>
    /// The sweep of this exact partition that completed its generation and describes the installation, though its operation may not have
    /// reached Completed yet (the generation and the operation are settled in separate writes). A parked one is joined only while its
    /// own authority class would readmit it.
    /// </summary>
    private static async Task<LongRunningOperation?> FindCurrentCompletedWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestSweepScope scope, LongRestSweepFence live, LongRestRecipeIdentity recipe, CancellationToken ct)
    {
        Guid? completed = await FindCompletedWithinAsync(connection, transaction, live.CanonicalScope, scope, recipe, JoinableOperationStates,
            generation => generation.Phase == LongRestSweepPhase.Completed && LongRestFenceCurrency.Describes(generation.Fence, live), ct).ConfigureAwait(false);

        LongRunningOperation? operation = completed is { } found
            ? await LongRunningOperationStore.ReadWithinAsync(connection, transaction, found, ct).ConfigureAwait(false) : null;

        return operation is { State: LongRunningOperationState.ReconciliationRequired } && !IsReadmittablePark(scope, operation) ? null : operation;
    }

    /// <summary>
    /// The operation of this exact partition whose generation is complete and which is parked for the authority class that would readmit
    /// it: when nothing joins it, nothing can readmit it either.
    /// </summary>
    private static async Task<LongRunningOperation?> FindStrandedCompletedParkAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestSweepScope scope, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT sweep.OperationId FROM long_rest_sweeps sweep
            JOIN LongRunningOperations operation ON operation.Id = sweep.OperationId AND operation.Kind = $kind
            WHERE sweep.PhaseCode = $completed AND sweep.ScopeKindCode = $scope AND sweep.CampaignId IS $campaign
                AND sweep.PartitionDigest IS $partition AND operation.State = $attention LIMIT 1;
            """, ("$kind", LongRunningOperationKinds.LongRestSweep), ("$completed", (int)LongRestSweepPhase.Completed), ("$scope", (int)scope.Kind),
            ("$campaign", scope.CampaignId is { } campaign ? Format(campaign) : null), ("$partition", scope.ProtectedPartitionDigest?.Bytes),
            ("$attention", (int)LongRunningOperationState.ReconciliationRequired));

        if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string id)
        {
            return null;
        }

        LongRunningOperation operation = await LongRunningOperationStore.ReadWithinAsync(connection, transaction, Guid.Parse(id), ct).ConfigureAwait(false)
            ?? throw new InvalidDataException();

        return operation.State == LongRunningOperationState.ReconciliationRequired && IsReadmittablePark(scope, operation) ? operation : null;
    }

    private async Task<LongRunningOperation> AbandonParkedWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRunningOperation operation, string terminalCode, CancellationToken ct)
    {
        await using SqliteCommand abandon = Command(connection, transaction, """
            UPDATE LongRunningOperations SET State = $abandoned, CompletedAt = $at, TerminalErrorCode = $error, Revision = Revision + 1
            WHERE Id = $operation AND Revision = $revision AND Kind = $kind AND RecoveryPolicy = $policy AND State = $attention
                AND LeaseOwner IS NULL AND LeaseExpiresAt IS NULL;
            """, ("$operation", operation.Id.ToString("N")), ("$revision", operation.Revision), ("$kind", LongRunningOperationKinds.LongRestSweep),
            ("$policy", (int)LongRunningOperationRecoveryPolicy.ResumeFromCheckpoint), ("$attention", (int)LongRunningOperationState.ReconciliationRequired),
            ("$abandoned", (int)LongRunningOperationState.Abandoned), ("$at", UtcInstantText.Format(timeProvider.GetUtcNow())), ("$error", terminalCode));

        if (await abandon.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) throw new InvalidDataException();

        return await LongRunningOperationStore.ReadWithinAsync(connection, transaction, operation.Id, ct).ConfigureAwait(false) ?? throw new InvalidDataException();
    }

    /// <remarks>
    /// Retirement reads only the columns of the generation's row, never the generation whole: it needs the identity, scope, phase and
    /// revision, and every statement below is guarded by them. A generation stored before an upgrade that moved a version, a fixed bound
    /// or the recipe preimage cannot be parsed under the code in force, and must still be retirable by whichever request finds it.
    /// </remarks>
    private async Task RetireTerminalRequestWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestSweepScope scope, ActiveRequest active, LongRunningOperation operation, CancellationToken ct)
    {
        // Retirement releases reservations only. Invalidation would delete accepted/Sent evidence.
        RetiringGeneration? generation = await ReadRetiringGenerationAsync(connection, transaction, scope, operation.Id, ct).ConfigureAwait(false);

        if (active.OperationId != operation.Id || active.GenerationId != generation?.GenerationId
            || generation is not null && (generation.OperationId != operation.Id
                || generation.Phase is LongRestSweepPhase.Completed or LongRestSweepPhase.Invalidated or LongRestSweepPhase.Retired))
        {
            throw new InvalidDataException();
        }

        if (generation is not null)
        {
            await using SqliteCommand retireGeneration = Command(connection, transaction, """
                UPDATE long_rest_discovery_generations SET PhaseCode = $retired, Revision = Revision + 1
                WHERE GenerationId = $generation AND OperationId = $operation AND Revision = $generationRevision AND PhaseCode = $generationPhase
                    AND ScopeKindCode = $scope AND CampaignId IS $campaign AND PartitionDigest IS $partition
                    AND EXISTS (SELECT 1 FROM LongRunningOperations operation WHERE operation.Id = $operation
                        AND operation.Kind = $kind AND operation.RecoveryPolicy = $policy AND operation.Revision = $operationRevision
                        AND operation.State IN ($failed, $abandoned) AND operation.State = $terminalState AND operation.CompletedAt IS NOT NULL
                        AND operation.LeaseOwner IS NULL AND operation.LeaseExpiresAt IS NULL);
                """, ("$retired", (int)LongRestSweepPhase.Retired), ("$generation", Format(generation.GenerationId)),
                ("$operation", operation.Id.ToString("N")), ("$generationRevision", generation.Revision), ("$generationPhase", (int)generation.Phase),
                ("$scope", (int)scope.Kind), ("$campaign", scope.CampaignId is { } campaign ? Format(campaign) : null),
                ("$partition", scope.ProtectedPartitionDigest?.Bytes), ("$kind", LongRunningOperationKinds.LongRestSweep),
                ("$policy", (int)LongRunningOperationRecoveryPolicy.ResumeFromCheckpoint), ("$operationRevision", operation.Revision),
                ("$terminalState", (int)operation.State), ("$failed", (int)LongRunningOperationState.Failed),
                ("$abandoned", (int)LongRunningOperationState.Abandoned));

            if (await retireGeneration.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) throw new InvalidDataException();
        }

        await using SqliteCommand retireSweep = Command(connection, transaction, """
            UPDATE long_rest_sweeps SET PhaseCode = $retired, Revision = Revision + 1, UpdatedAtUtc = $at
            WHERE OperationId = $operation AND Revision = $sweepRevision AND ConfigurationJson = $stamp AND PhaseCode = $sweepPhase
                AND GenerationId IS $generation AND ScopeKindCode = $scope AND CampaignId IS $campaign AND PartitionDigest IS $partition
                AND EXISTS (SELECT 1 FROM LongRunningOperations operation WHERE operation.Id = $operation
                    AND operation.Kind = $kind AND operation.RecoveryPolicy = $policy AND operation.Revision = $operationRevision
                    AND operation.State IN ($failed, $abandoned) AND operation.State = $terminalState AND operation.CompletedAt IS NOT NULL
                    AND operation.LeaseOwner IS NULL AND operation.LeaseExpiresAt IS NULL);
            """, ("$retired", (int)LongRestSweepPhase.Retired), ("$at", UtcInstantText.Format(timeProvider.GetUtcNow())),
            ("$operation", operation.Id.ToString("N")), ("$sweepRevision", active.Revision), ("$stamp", active.ConfigurationJson),
            ("$sweepPhase", (int)active.Phase), ("$generation", active.GenerationId is { } generationId ? Format(generationId) : null),
            ("$scope", (int)scope.Kind), ("$campaign", scope.CampaignId is { } campaignId ? Format(campaignId) : null),
            ("$partition", scope.ProtectedPartitionDigest?.Bytes), ("$kind", LongRunningOperationKinds.LongRestSweep),
            ("$policy", (int)LongRunningOperationRecoveryPolicy.ResumeFromCheckpoint), ("$operationRevision", operation.Revision),
            ("$terminalState", (int)operation.State), ("$failed", (int)LongRunningOperationState.Failed),
            ("$abandoned", (int)LongRunningOperationState.Abandoned));

        if (await retireSweep.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) throw new InvalidDataException();
    }

    /// <summary>What retiring a request's generation needs of its row: the identity, phase and revision the retirement is guarded by.</summary>
    private sealed record RetiringGeneration(Guid GenerationId, Guid OperationId, LongRestSweepPhase Phase, long Revision);

    private static async Task<RetiringGeneration?> ReadRetiringGenerationAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestSweepScope scope, Guid operationId, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT GenerationId, ScopeKindCode, CampaignId, PartitionDigest, PhaseCode, Revision
            FROM long_rest_discovery_generations WHERE OperationId = $operation;
            """, ("$operation", operationId.ToString("N")));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        Guid? campaign = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2));

        byte[]? partition = reader.IsDBNull(3) ? null : (byte[])reader[3];

        bool samePartition = partition is null
            ? scope.ProtectedPartitionDigest is null
            : scope.ProtectedPartitionDigest is { } expected && partition.AsSpan().SequenceEqual(expected.Bytes);

        if ((LongRestSweepScopeKind)reader.GetInt32(1) != scope.Kind || campaign != scope.CampaignId || !samePartition)
        {
            throw new InvalidDataException();
        }

        return new(Guid.Parse(reader.GetString(0)), operationId, (LongRestSweepPhase)reader.GetInt32(4), reader.GetInt64(5));
    }

    private async Task<LongRunningOperation> CreateRequestWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestSweepScope scope, string stamp, CancellationToken ct)
    {
        LongRunningOperation operation = await LongRunningOperationStore.CreateWithinAsync(connection, transaction,
            new(LongRunningOperationKinds.LongRestSweep, LongRunningOperationRecoveryPolicy.ResumeFromCheckpoint,
                "Long Rest sweep queued.", timeProvider.GetUtcNow()), ct).ConfigureAwait(false);

        await using SqliteCommand insert = Command(connection, transaction, """
            INSERT INTO long_rest_sweeps (OperationId, GenerationId, ScopeKindCode, CampaignId, PartitionDigest,
                ConfigurationJson, PhaseCode, Revision, UpdatedAtUtc)
            VALUES ($operation, NULL, $scope, $campaign, $partition, $stamp, $capture, 0, $at);
            """, ("$operation", operation.Id.ToString("N")), ("$scope", (int)scope.Kind),
            ("$campaign", scope.CampaignId is { } campaign ? Format(campaign) : null), ("$partition", scope.ProtectedPartitionDigest?.Bytes),
            ("$stamp", stamp), ("$capture", (int)LongRestSweepPhase.Capture), ("$at", UtcInstantText.Format(timeProvider.GetUtcNow())));

        if (await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) throw new InvalidDataException();

        return operation;
    }

    private static async Task<ActiveRequest?> ReadActiveRequestAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestSweepScope scope, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT OperationId, ConfigurationJson, Revision, GenerationId, PhaseCode, ReadmitCount FROM long_rest_sweeps
            WHERE ScopeKindCode = $scope AND CampaignId IS $campaign AND PartitionDigest IS $partition
                AND PhaseCode NOT IN (6, 7, 9) LIMIT 1;
            """, ("$scope", (int)scope.Kind), ("$campaign", scope.CampaignId is { } campaign ? Format(campaign) : null),
            ("$partition", scope.ProtectedPartitionDigest?.Bytes));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetInt64(2),
                reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)), (LongRestSweepPhase)reader.GetInt32(4), checked((int)reader.GetInt64(5))) : null;
    }

    private static async Task<bool> IsNeverStartedCleanAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid operationId, long operationRevision, long sweepRevision, string stamp, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT 1 FROM LongRunningOperations operation JOIN long_rest_sweeps sweep ON sweep.OperationId = operation.Id
            WHERE operation.Id = $operation AND operation.Kind = $kind AND operation.RecoveryPolicy = $policy
                AND operation.Revision = $operationRevision AND operation.State = $pending
                AND operation.AttemptCount = 0 AND operation.CheckpointVersion = 0
                AND operation.CheckpointPayload IS NULL AND operation.CheckpointReference IS NULL
                AND operation.StartedAt IS NULL AND operation.HeartbeatAt IS NULL AND operation.CompletedAt IS NULL
                AND operation.LeaseOwner IS NULL AND operation.LeaseExpiresAt IS NULL AND operation.TerminalErrorCode IS NULL
                AND operation.RootOperationId IS NULL AND operation.ParentOperationId IS NULL
                AND operation.SessionId IS NULL AND operation.RunId IS NULL AND operation.InferenceRunId IS NULL
                AND operation.BudgetReservationId IS NULL AND operation.IdempotencyClaimId IS NULL
                AND sweep.Revision = $sweepRevision AND sweep.ConfigurationJson = $stamp
                AND sweep.PartitionDigest IS NULL AND sweep.GenerationId IS NULL AND sweep.PhaseCode = $capture
                AND sweep.CommittedItemCursor IS NULL AND sweep.CommittedWorkDigest IS NULL
                AND NOT EXISTS (SELECT 1 FROM long_running_operation_request_identities identity WHERE identity.OperationId = operation.Id)
                AND NOT EXISTS (SELECT 1 FROM long_rest_discovery_generations WHERE OperationId = operation.Id)
                AND NOT EXISTS (SELECT 1 FROM long_rest_work_items WHERE OperationId = operation.Id)
                AND NOT EXISTS (SELECT 1 FROM LongRunningOperations child WHERE child.ParentOperationId = operation.Id OR child.RootOperationId = operation.Id);
            """, ("$operation", operationId.ToString("N")), ("$kind", LongRunningOperationKinds.LongRestSweep),
            ("$policy", (int)LongRunningOperationRecoveryPolicy.ResumeFromCheckpoint), ("$operationRevision", operationRevision),
            ("$pending", (int)LongRunningOperationState.Pending), ("$sweepRevision", sweepRevision), ("$stamp", stamp),
            ("$capture", (int)LongRestSweepPhase.Capture));

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    private async Task ChangeSweepAdmissionAsync(SqliteConnection connection, SqliteTransaction transaction,
        ActiveRequest active, string stamp, LongRestSweepPhase phase, CancellationToken ct)
    {
        await using SqliteCommand update = Command(connection, transaction, """
            UPDATE long_rest_sweeps SET ConfigurationJson = $stamp, PhaseCode = $phase, Revision = Revision + 1, UpdatedAtUtc = $at
            WHERE OperationId = $operation AND Revision = $revision AND ConfigurationJson = $old;
            """, ("$stamp", stamp), ("$phase", (int)phase), ("$at", UtcInstantText.Format(timeProvider.GetUtcNow())),
            ("$operation", active.OperationId.ToString("N")), ("$revision", active.Revision), ("$old", active.ConfigurationJson));

        if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) throw new InvalidDataException();
    }

    private sealed record ActiveRequest(Guid OperationId, string ConfigurationJson, long Revision, Guid? GenerationId, LongRestSweepPhase Phase,
        int ReadmitCount);

    private sealed class OperationBusyException : Exception;

    /// <summary>The replacement of a parked sweep: the terminal code it is abandoned under, and whether live request authority freed it.</summary>
    private sealed record ParkedReplacement(string TerminalCode, bool ByAuthority);

    private sealed class AuthorityRefusedException(Error error) : Exception(error.Code)
    {
        internal Error Error { get; } = error;
    }

    private sealed class SweepParkedException(Guid operationId, string? terminalErrorCode) : Exception
    {
        internal Guid OperationId { get; } = operationId;

        internal string? TerminalErrorCode { get; } = terminalErrorCode;
    }
}
