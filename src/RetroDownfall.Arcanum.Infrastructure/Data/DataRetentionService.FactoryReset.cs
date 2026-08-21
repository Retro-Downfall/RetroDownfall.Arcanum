using System.Data.Common;

using System.Globalization;

using Microsoft.Data.Sqlite;

using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Daemons;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal sealed partial class DataRetentionService
{

    private static readonly FactoryTablePlan[] FactoryPlanTables =
    [
        new("Sessions", RetentionDataClass.ActiveSessions, FactoryRecordKind.Physical, "lower(Status) <> 'archived'"),
        new("Sessions", RetentionDataClass.ArchivedSessions, FactoryRecordKind.Physical, "lower(Status) = 'archived'"),
        new("Entries", RetentionDataClass.Entries, FactoryRecordKind.Physical),
        new("Entries_fts", RetentionDataClass.Entries, FactoryRecordKind.Derived),
        new("SessionContextPins", RetentionDataClass.Entries, FactoryRecordKind.Derived),
        new("attachment_memory_consultations", RetentionDataClass.Entries, FactoryRecordKind.Derived),
        new("SessionAttachments", RetentionDataClass.AttachmentVersions, FactoryRecordKind.Physical),
        new("session_attachment_chunks", RetentionDataClass.AttachmentChunks, FactoryRecordKind.Derived),
        new("session_attachment_embeddings", RetentionDataClass.AttachmentEmbeddings, FactoryRecordKind.Derived),
        new("session_attachment_embeddings_vec", RetentionDataClass.AttachmentEmbeddings, FactoryRecordKind.Derived),
        new("session_attachment_index_state", RetentionDataClass.AttachmentEmbeddings, FactoryRecordKind.Derived),
        new("UploadedFiles", RetentionDataClass.UploadedFiles, FactoryRecordKind.Physical),
        new(
            "Batches",
            RetentionDataClass.CompletedBatches,
            FactoryRecordKind.Physical,
            "Status IN ('completed', 'failed', 'cancelled', 'expired')"),
        new("saga_memories", RetentionDataClass.SagaMemories, FactoryRecordKind.Physical),
        new("saga_memory_embeddings", RetentionDataClass.SagaMemories, FactoryRecordKind.Derived),
        new("saga_memory_embeddings_vec", RetentionDataClass.SagaMemories, FactoryRecordKind.Derived),
        new("saga_memory_attachment_provenance", RetentionDataClass.SagaMemories, FactoryRecordKind.Derived),
        new("saga_extraction_watermarks", RetentionDataClass.SagaMemories, FactoryRecordKind.Derived),
        new("lexicon_entries", RetentionDataClass.LexiconEntries, FactoryRecordKind.Physical),
        new("lexicon_fts", RetentionDataClass.LexiconEntries, FactoryRecordKind.Derived),
        new("lexicon_fact_attachment_provenance", RetentionDataClass.LexiconEntries, FactoryRecordKind.Derived),
        new("WorkspaceContexts", RetentionDataClass.WorkspaceChunks, FactoryRecordKind.Physical),
        new("workspace_file_chunks", RetentionDataClass.WorkspaceChunks, FactoryRecordKind.Derived),
        new("workspace_file_embeddings", RetentionDataClass.WorkspaceEmbeddings, FactoryRecordKind.Derived),
        new("workspace_file_embeddings_vec", RetentionDataClass.WorkspaceEmbeddings, FactoryRecordKind.Derived),
        new("entry_embeddings", RetentionDataClass.SessionEntryEmbeddings, FactoryRecordKind.Derived),
        new("entry_embeddings_vec", RetentionDataClass.SessionEntryEmbeddings, FactoryRecordKind.Derived),
        new("tapestry_generations", RetentionDataClass.Tapestry, FactoryRecordKind.Derived),
        new("tapestry_nodes", RetentionDataClass.Tapestry, FactoryRecordKind.Derived),
        new("tapestry_node_embeddings", RetentionDataClass.Tapestry, FactoryRecordKind.Derived),
        new("tapestry_node_embeddings_vec", RetentionDataClass.Tapestry, FactoryRecordKind.Derived),
        new("IdempotencyClaims", RetentionDataClass.IdempotencyClaims, FactoryRecordKind.Physical),
        new("IdempotencyKeys", RetentionDataClass.IdempotencyClaims, FactoryRecordKind.Physical),
        new("InferenceRuns", RetentionDataClass.InferenceRuns, FactoryRecordKind.Physical),
        new("BillableOperations", RetentionDataClass.BillableOperations, FactoryRecordKind.Physical),
        new("BudgetReservations", RetentionDataClass.BudgetReservations, FactoryRecordKind.Physical),
        new("CostAdjustments", RetentionDataClass.CostAdjustments, FactoryRecordKind.Physical),
        new("BudgetAlerts", RetentionDataClass.CostAdjustments, FactoryRecordKind.Physical),
        new("SanctumBreaches", RetentionDataClass.SanctumBreaches, FactoryRecordKind.Physical),
        new("UnseenServantWatermarks", RetentionDataClass.DaemonExecutions, FactoryRecordKind.Physical),
    ];

    private static readonly FactoryTablePlan[] FactoryDeletionTables =
    [
        new("session_attachment_embeddings_vec", RetentionDataClass.AttachmentEmbeddings, FactoryRecordKind.Derived),
        new("entry_embeddings_vec", RetentionDataClass.SessionEntryEmbeddings, FactoryRecordKind.Derived),
        new("workspace_file_embeddings_vec", RetentionDataClass.WorkspaceEmbeddings, FactoryRecordKind.Derived),
        new("saga_memory_embeddings_vec", RetentionDataClass.SagaMemories, FactoryRecordKind.Derived),
        new("tapestry_node_embeddings_vec", RetentionDataClass.Tapestry, FactoryRecordKind.Derived),
        new("Entries_fts", RetentionDataClass.Entries, FactoryRecordKind.Derived),
        new("lexicon_fts", RetentionDataClass.LexiconEntries, FactoryRecordKind.Derived),
        new("session_attachment_embeddings", RetentionDataClass.AttachmentEmbeddings, FactoryRecordKind.Derived),
        new("session_attachment_chunks", RetentionDataClass.AttachmentChunks, FactoryRecordKind.Derived),
        new("session_attachment_index_state", RetentionDataClass.AttachmentEmbeddings, FactoryRecordKind.Derived),
        new("entry_embeddings", RetentionDataClass.SessionEntryEmbeddings, FactoryRecordKind.Derived),
        new("tapestry_node_embeddings", RetentionDataClass.Tapestry, FactoryRecordKind.Derived),
        new("tapestry_nodes", RetentionDataClass.Tapestry, FactoryRecordKind.Derived),
        new("tapestry_generations", RetentionDataClass.Tapestry, FactoryRecordKind.Derived),
        new("workspace_file_embeddings", RetentionDataClass.WorkspaceEmbeddings, FactoryRecordKind.Derived),
        new("workspace_file_chunks", RetentionDataClass.WorkspaceChunks, FactoryRecordKind.Derived),
        new("saga_memory_embeddings", RetentionDataClass.SagaMemories, FactoryRecordKind.Derived),
        new("saga_memory_attachment_provenance", RetentionDataClass.SagaMemories, FactoryRecordKind.Derived),
        new("saga_extraction_watermarks", RetentionDataClass.SagaMemories, FactoryRecordKind.Derived),
        new("attachment_memory_consultations", RetentionDataClass.Entries, FactoryRecordKind.Derived),
        new("lexicon_fact_attachment_provenance", RetentionDataClass.LexiconEntries, FactoryRecordKind.Derived),
        new("SessionContextPins", RetentionDataClass.Entries, FactoryRecordKind.Derived),
        new("lexicon_entries", RetentionDataClass.LexiconEntries, FactoryRecordKind.Physical),
        new("saga_memories", RetentionDataClass.SagaMemories, FactoryRecordKind.Physical),
        new("SessionAttachments", RetentionDataClass.AttachmentVersions, FactoryRecordKind.Physical),
        new("Entries", RetentionDataClass.Entries, FactoryRecordKind.Physical),
        new("Sessions", RetentionDataClass.ActiveSessions, FactoryRecordKind.Physical),
        new("Batches", RetentionDataClass.CompletedBatches, FactoryRecordKind.Physical),
        new("UploadedFiles", RetentionDataClass.UploadedFiles, FactoryRecordKind.Physical),
        new("IdempotencyClaims", RetentionDataClass.IdempotencyClaims, FactoryRecordKind.Physical),
        new("IdempotencyKeys", RetentionDataClass.IdempotencyClaims, FactoryRecordKind.Physical),
        new("CostAdjustments", RetentionDataClass.CostAdjustments, FactoryRecordKind.Physical),
        new("BudgetReservations", RetentionDataClass.BudgetReservations, FactoryRecordKind.Physical),
        new("BillableOperations", RetentionDataClass.BillableOperations, FactoryRecordKind.Physical),
        new("InferenceRuns", RetentionDataClass.InferenceRuns, FactoryRecordKind.Physical),
        new("SanctumBreaches", RetentionDataClass.SanctumBreaches, FactoryRecordKind.Physical),
        new("BudgetAlerts", RetentionDataClass.CostAdjustments, FactoryRecordKind.Physical),
        new("UnseenServantWatermarks", RetentionDataClass.DaemonExecutions, FactoryRecordKind.Physical),
        new("WorkspaceContexts", RetentionDataClass.WorkspaceChunks, FactoryRecordKind.Physical),
    ];

    private async Task<DataRetentionPlan> BuildFactoryResetPlanCoreAsync(
        DataRetentionRequest request,
        Guid? excludedOperationId,
        CancellationToken cancellationToken)
    {

        List<DataRetentionPlanItem> items = [];

        foreach (FactoryTablePlan source in FactoryPlanTables)
        {

            long count = await CountTableAsync(
                source.Table,
                source.Predicate,
                cancellationToken).ConfigureAwait(false);

            AddFactoryPlanItem(items, source, count);

        }

        string operationPredicate = excludedOperationId is null
            ? $"State IN ({FactoryTerminalOperationStates})"
            : $"State IN ({FactoryTerminalOperationStates}) AND lower(replace(Id, '-', '')) <> @currentId";

        long operationRows = excludedOperationId is null
            ? await CountTableAsync(
                "LongRunningOperations",
                operationPredicate,
                cancellationToken).ConfigureAwait(false)
            : await CountTableAsync(
                "LongRunningOperations",
                operationPredicate,
                cancellationToken,
                ("@currentId", excludedOperationId.Value.ToString("N"))).ConfigureAwait(false);

        AddFactoryPlanItem(
            items,
            new FactoryTablePlan(
                "LongRunningOperations",
                RetentionDataClass.LongRunningOperations,
                FactoryRecordKind.Physical),
            operationRows);

        (long attachmentFiles, long attachmentBytes) = CountManagedTreeContents(
            _attachmentsRoot);

        AddFactoryFilePlanItem(
            items,
            RetentionDataClass.AttachmentBytes,
            attachmentFiles,
            attachmentBytes);

        (long uploadedFiles, long uploadedBytes) = CountManagedTreeContents(
            _filesRoot);

        AddFactoryFilePlanItem(
            items,
            RetentionDataClass.UploadedFiles,
            uploadedFiles,
            uploadedBytes);

        (long auditFiles, long auditBytes) = CountFactoryLogFiles(
            _logsRoot,
            "audit-????????.jsonl");

        AddFactoryFilePlanItem(
            items,
            RetentionDataClass.AuditLogs,
            auditFiles,
            auditBytes);

        (long guardrailFiles, long guardrailBytes) = CountFactoryLogFiles(
            _logsRoot,
            "guardrails-????????.jsonl");

        AddFactoryFilePlanItem(
            items,
            RetentionDataClass.GuardrailLogs,
            guardrailFiles,
            guardrailBytes);

        if (daemonExecutions is not null)
        {

            DaemonExecutionSummary[] history = await daemonExecutions.GetHistoryAsync(
                null,
                cancellationToken).ConfigureAwait(false);

            long terminalHistory = history.LongCount(IsTerminalDaemonExecution);

            AddFactoryPlanItem(
                items,
                new FactoryTablePlan(
                    "daemon execution history",
                    RetentionDataClass.DaemonExecutions,
                    FactoryRecordKind.Physical),
                terminalHistory);

        }

        DataRetentionConflict[] conflicts = await ReadGlobalConflictsAsync(
            cancellationToken,
            excludedOperationId).ConfigureAwait(false);

        return FinalizePlan(
            request,
            items,
            [],
            conflicts,
            items.Count == 0 ? [] : ["factory-reset"],
            requiresConfirmation: true);

    }

    private async Task<DataRetentionApplyResult> ApplyFactoryResetAsync(
        Guid operationId,
        DataRetentionPlan plan,
        CancellationToken cancellationToken)
    {

        // Lock ordering is managed-log gate -> SQLite writer transaction. Log publication never
        // enters SQLite, so the reset cannot hold the database writer while waiting on an append.
        await using IAsyncDisposable? managedLogLease = managedLogMutationGate is null
            ? null
            : await managedLogMutationGate.AcquireExclusiveAsync(
                cancellationToken).ConfigureAwait(false);

        SqliteConnection connection = (SqliteConnection)await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        await using SqliteTransaction transaction = connection.BeginTransaction(
            deferred: false);

        bool committed = false;

        List<IdentityOwnedFileSystemQuarantine> quarantinedFiles = [];

        try
        {

            await using IAsyncDisposable? daemonLease = daemonMutationGate is null
                ? null
                : await daemonMutationGate.AcquireExclusiveAsync(
                    cancellationToken).ConfigureAwait(false);

            DataRetentionConflict[] conflicts = await ReadFactoryConflictsInTransactionAsync(
                connection,
                transaction,
                operationId,
                cancellationToken).ConfigureAwait(false);

            DataRetentionConflict[] daemonConflicts =
                await ReadActiveDaemonExecutionConflictsAsync(
                    cancellationToken).ConfigureAwait(false);

            if (conflicts.Length > 0 || daemonConflicts.Length > 0)
            {

                string message = conflicts.FirstOrDefault()?.Message
                    ?? daemonConflicts[0].Message;

                throw new RetentionConflictException(message);

            }

            DaemonExecutionSummary[] terminalDaemonHistory = daemonExecutions is null
                ? []
                : [.. (await daemonExecutions.GetHistoryAsync(
                        null,
                        cancellationToken).ConfigureAwait(false))
                    .Where(IsTerminalDaemonExecution)];

            FactoryFileSystemSnapshot fileSystem = CaptureFactoryFileSystemSnapshot();

            long physicalDeleted = 0;

            long derivedDeleted = 0;

            foreach (FactoryTablePlan table in FactoryDeletionTables)
            {

                if (!await FactoryTableExistsAsync(
                        connection,
                        transaction,
                        table.Table,
                        cancellationToken).ConfigureAwait(false))
                {

                    continue;

                }

                int deleted = await ClearFactoryTableAsync(
                    connection,
                    transaction,
                    table,
                    cancellationToken).ConfigureAwait(false);

                if (table.Kind == FactoryRecordKind.Physical)
                {

                    physicalDeleted += deleted;

                }
                else
                {

                    derivedDeleted += deleted;

                }

            }

            int deletedOperations;

            do
            {

                deletedOperations = await ExecuteAsync(
                    connection,
                    transaction,
                    $"""
                    DELETE FROM LongRunningOperations
                    WHERE lower(replace(Id, '-', '')) <> @currentId
                      AND State IN ({FactoryTerminalOperationStates})
                      AND NOT EXISTS (
                          SELECT 1 FROM LongRunningOperations child
                          WHERE child.ParentOperationId = LongRunningOperations.Id
                             OR child.RootOperationId = LongRunningOperations.Id)
                    """,
                    cancellationToken,
                    ("@currentId", operationId.ToString("N"))).ConfigureAwait(false);

                physicalDeleted += deletedOperations;

            }

            while (deletedOperations > 0);

            long plannedDaemonRows = terminalDaemonHistory.LongLength;

            if (physicalDeleted + plannedDaemonRows != plan.Rows
                || derivedDeleted != plan.DerivedRecords
                || fileSystem.Files.LongLength != plan.Files
                || fileSystem.Files.Sum(static file => file.Bytes) != plan.EstimatedBytes)
            {

                throw new RetentionConflictException(
                    "Factory-reset data changed after preview; request a new dry-run before retrying.");

            }

            ValidateFactoryFileSystemSnapshot(fileSystem);

            DateTimeOffset leaseRenewedAt = timeProvider.GetUtcNow();

            int renewed = await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE LongRunningOperations
                SET HeartbeatAt = @now,
                    LeaseExpiresAt = @expiresAt,
                    Revision = Revision + 1
                WHERE lower(replace(Id, '-', '')) = @currentId
                  AND State = @running
                """,
                cancellationToken,
                ("@now", leaseRenewedAt.ToString("o", CultureInfo.InvariantCulture)),
                ("@expiresAt", leaseRenewedAt
                    .Add(DataRetentionLeaseMaintainer.DefaultLeaseDuration)
                    .ToString("o", CultureInfo.InvariantCulture)),
                ("@currentId", operationId.ToString("N")),
                ("@running", (int)LongRunningOperationState.Running)).ConfigureAwait(false);

            if (renewed != 1)
            {

                throw new RetentionConflictException(
                    "Factory reset lost its durable operation lease before file cleanup.");

            }

            QuarantineFactoryFiles(
                fileSystem,
                quarantinedFiles);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            committed = true;

            if (daemonExecutions is not null)
            {

                foreach (DaemonExecutionSummary execution in terminalDaemonHistory)
                {

                    if (await daemonExecutions.TryDeleteTerminalAsync(
                            execution.Id,
                            cancellationToken).ConfigureAwait(false))
                    {

                        physicalDeleted++;

                    }

                }

            }

            DeleteQuarantinedFactoryFiles(quarantinedFiles);

            DeleteFactoryDirectories(fileSystem.Directories);

            long files = fileSystem.Files.LongLength;

            long bytes = fileSystem.Files.Sum(static file => file.Bytes);

            bool reconciled = await ReconcileFactoryResetAsync(
                operationId,
                cancellationToken).ConfigureAwait(false);

            if (!reconciled)
            {

                throw new RetentionQuarantineRecoveryRequiredException(
                    "Factory reset committed, but post-commit reconciliation found retained owned data.");

            }

            return new DataRetentionApplyResult(
                operationId,
                plan.PlanId,
                physicalDeleted,
                files,
                bytes,
                derivedDeleted,
                reconciled,
                plan.Blockers,
                plan.Conflicts);

        }
        catch (Exception failure)
        {

            if (committed)
            {

                if (failure is RetentionQuarantineRecoveryRequiredException)
                {

                    throw;

                }

                throw new RetentionQuarantineRecoveryRequiredException(
                    "Factory reset committed, but its post-commit cleanup did not finish.",
                    failure);

            }

            if (!committed && transaction.Connection is not null)
            {

                Exception? cleanupFailure = null;

                try
                {

                    RestoreQuarantinedFactoryFiles(quarantinedFiles);

                }
                catch (Exception ex)
                {

                    cleanupFailure = ex;

                }

                try
                {

                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

                }
                catch (Exception ex)
                {

                    cleanupFailure = cleanupFailure is null
                        ? ex
                        : new AggregateException(cleanupFailure, ex);

                }

                if (cleanupFailure is not null)
                {

                    throw new AggregateException(
                        "Factory reset could not fully restore its pre-commit state.",
                        failure,
                        cleanupFailure);

                }

            }

            throw;

        }

    }

    internal async Task<LongRunningOperationRecoveryResult> RecoverFactoryResetAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken)
    {

        if (!string.Equals(
                operation.Kind,
                LongRunningOperationKinds.DataRetentionFactoryReset,
                StringComparison.Ordinal)
            || operation.RecoveryPolicy != LongRunningOperationRecoveryPolicy.RestartIdempotently)
        {

            return LongRunningOperationRecoveryResult.Failed(
                LongRunningOperationErrorCodes.InvalidRecoveryResult);

        }

        // Version 0 is the documented legacy arm: no payload, nothing to resume from, restarted
        // idempotently from durable quarantine state below. Version 1 is a healthy-catalog Covenant
        // erasure, which cannot be restarted — a factory erasure that already applied its canonical
        // deletion cannot prove that by inspecting the result, so it is resumed from the exact phase
        // its checkpoint recorded (§10.20.3).
        if (operation.CheckpointVersion == DataRetentionFactoryResetCheckpointV1.CurrentVersion)
        {

            return await RecoverCovenantFactoryErasureAsync(
                operation,
                cancellationToken).ConfigureAwait(false);

        }

        if (operation.CheckpointVersion != 0)
        {

            return LongRunningOperationRecoveryResult.RequiresAttention(
                ErrorCodes.Covenant.ManualRecoveryRequired);

        }

        try
        {

            DataRetentionRequest request = new(DataRetentionOperation.FactoryReset);

            DataRetentionPlan plan = await BuildFactoryResetPlanCoreAsync(
                request,
                operation.Id,
                cancellationToken).ConfigureAwait(false);

            if (plan.Conflicts.Length > 0)
            {

                return LongRunningOperationRecoveryResult.Failed(
                    ErrorCodes.Data.Conflict);

            }

            DataRetentionApplyResult result = await ApplyFactoryResetAsync(
                operation.Id,
                plan,
                cancellationToken).ConfigureAwait(false);

            return result.Reconciled
                ? LongRunningOperationRecoveryResult.Completed()
                : LongRunningOperationRecoveryResult.Failed(
                    ErrorCodes.Data.ReconciliationFailed);

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            throw;

        }
        catch (RetentionConflictException)
        {

            return LongRunningOperationRecoveryResult.Failed(
                ErrorCodes.Data.Conflict);

        }
        catch (RetentionQuarantineRecoveryRequiredException ex)
        {

            logger.LogWarning(
                ex,
                "Factory-reset recovery still requires post-commit cleanup for durable operation {OperationId}.",
                operation.Id);

            return LongRunningOperationRecoveryResult.RequiresAttention(
                ErrorCodes.Data.ReconciliationFailed);

        }
        catch (Exception ex)
        {

            logger.LogError(
                ex,
                "Factory-reset recovery failed for durable operation {OperationId}.",
                operation.Id);

            return LongRunningOperationRecoveryResult.RequiresAttention(
                ErrorCodes.Data.ReconciliationFailed);

        }

    }

    /// <summary>
    /// Reconciles a version-1 healthy-catalog factory erasure.
    /// </summary>
    /// <remarks>
    /// Rebuilds the identical exclusive owner from the checkpoint alone and parks the operation.
    /// This build has no erasure coordinator to resume it with, and restarting it down the ordinary
    /// idempotent path would run the factory contract's deletion over a family whose canonical arm
    /// an interrupted erasure may already have replaced — while every reader that erasure closed
    /// admission against is still waiting (§10.20.3).
    /// </remarks>
    private async Task<LongRunningOperationRecoveryResult> RecoverCovenantFactoryErasureAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken)
    {

        if (operation.CheckpointPayload is null)
        {

            return LongRunningOperationRecoveryResult.RequiresAttention(
                ErrorCodes.Covenant.ManualRecoveryRequired);

        }

        // The same projection the reset arm uses and the erasure coordinator resumes from. The two
        // journals differ only in their headers, and one reader is what keeps them agreeing about
        // what a phase means.
        Result<CovenantErasureCheckpointState> state =
            CovenantErasureCheckpointState.FromFactoryResetCheckpoint(
                operation.Id,
                operation.CheckpointPayload);

        if (state.IsFailure)
        {

            return LongRunningOperationRecoveryResult.RequiresAttention(
                ErrorCodes.Covenant.ManualRecoveryRequired);

        }

        if (string.IsNullOrWhiteSpace(operation.LeaseOwner)
            || _covenantErasureCoordinator is null)
        {

            return LongRunningOperationRecoveryResult.RequiresAttention(
                ErrorCodes.Covenant.MaintenanceFailed);

        }

        logger.LogWarning(
            "A healthy-catalog Covenant factory erasure was interrupted at phase {ResetPhase} for "
            + "durable operation {OperationId}; recovery is resuming the recorded owner.",
            state.Value.Phase,
            operation.Id);

        Result<CovenantErasureCompletion> recovered = await _covenantErasureCoordinator
            .RunAsync(
                operation,
                state.Value,
                operation.LeaseOwner,
                cancellationToken)
            .ConfigureAwait(false);

        return MapCovenantErasureRecovery(recovered);

    }

    private async Task<bool> ReconcileFactoryResetAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {

        bool reconciled = true;

        foreach (FactoryTablePlan table in FactoryDeletionTables)
        {

            reconciled &= await CountTableAsync(
                table.Table,
                null,
                cancellationToken).ConfigureAwait(false) == 0;

        }

        reconciled &= CountManagedTreeContents(_attachmentsRoot).Files == 0;

        reconciled &= CountManagedTreeContents(_filesRoot).Files == 0;

        (long remainingAuditLogs, _) = CountFactoryLogFiles(
            _logsRoot,
            "audit-????????.jsonl");

        (long remainingGuardrailLogs, _) = CountFactoryLogFiles(
            _logsRoot,
            "guardrails-????????.jsonl");

        reconciled &= remainingAuditLogs == 0;

        reconciled &= remainingGuardrailLogs == 0;

        reconciled &= await CountTableAsync(
            "LongRunningOperations",
            $"State IN ({FactoryTerminalOperationStates}) AND lower(replace(Id, '-', '')) <> @currentId",
            cancellationToken,
            ("@currentId", operationId.ToString("N"))).ConfigureAwait(false) == 0;

        reconciled &= await CountTableAsync(
            "LongRunningOperations",
            "lower(replace(Id, '-', '')) = @currentId",
            cancellationToken,
            ("@currentId", operationId.ToString("N"))).ConfigureAwait(false) == 1;

        if (daemonExecutions is not null)
        {

            DaemonExecutionSummary[] remaining = await daemonExecutions.GetHistoryAsync(
                null,
                cancellationToken).ConfigureAwait(false);

            reconciled &= remaining.All(static execution => !IsTerminalDaemonExecution(execution));

        }

        return reconciled;

    }

    private async Task<DataRetentionConflict[]> ReadFactoryConflictsInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid excludedOperationId,
        CancellationToken cancellationToken)
    {

        List<DataRetentionConflict> conflicts = [];

        conflicts.AddRange(
            await ReadFactoryConflictRowsAsync(
                connection,
                transaction,
                $"""
                SELECT Id
                FROM LongRunningOperations
                WHERE State IN ({string.Join(",", ActiveOperationStates)})
                  AND lower(replace(Id, '-', '')) <> @excludedOperationId
                """,
                "Data.ActiveOperation",
                "An active durable operation conflicts with this global reset.",
                cancellationToken,
                ("@excludedOperationId", excludedOperationId.ToString("N"))).ConfigureAwait(false));

        conflicts.AddRange(
            await ReadFactoryConflictRowsAsync(
                connection,
                transaction,
                "SELECT Id FROM BudgetReservations WHERE Status = @status",
                "Data.BudgetReservationOutstanding",
                "An outstanding budget reservation conflicts with this global reset.",
                cancellationToken,
                ("@status", (int)BudgetReservationStatus.Reserved)).ConfigureAwait(false));

        conflicts.AddRange(
            await ReadFactoryConflictRowsAsync(
                connection,
                transaction,
                "SELECT Id FROM InferenceRuns WHERE Status = @status",
                "Data.InferenceRunActive",
                "An active inference run conflicts with this global reset.",
                cancellationToken,
                ("@status", (int)InferenceRunStatus.Running)).ConfigureAwait(false));

        conflicts.AddRange(
            await ReadFactoryConflictRowsAsync(
                connection,
                transaction,
                """
                SELECT Id
                FROM IdempotencyClaims
                WHERE State IN (@claimed, @running)
                  AND LeaseExpiresAt > @now
                """,
                "Data.IdempotencyLeaseActive",
                "An active idempotency lease conflicts with this global reset.",
                cancellationToken,
                ("@claimed", (int)IdempotencyClaimState.Claimed),
                ("@running", (int)IdempotencyClaimState.Running),
                ("@now", timeProvider.GetUtcNow().ToString("o", CultureInfo.InvariantCulture))).ConfigureAwait(false));

        conflicts.AddRange(
            await ReadFactoryConflictRowsAsync(
                connection,
                transaction,
                """
                SELECT Id
                FROM Batches
                WHERE Status NOT IN (@completed, @failed, @cancelled, @expired)
                """,
                "Data.BatchInProgress",
                "An in-progress batch conflicts with this global reset.",
                cancellationToken,
                ("@completed", BatchStatuses.Completed),
                ("@failed", BatchStatuses.Failed),
                ("@cancelled", BatchStatuses.Cancelled),
                ("@expired", BatchStatuses.Expired)).ConfigureAwait(false));

        return [.. conflicts
            .DistinctBy(static conflict => (conflict.Code, conflict.ResourceId))
            .OrderBy(static conflict => conflict.Code, StringComparer.Ordinal)
            .ThenBy(static conflict => conflict.ResourceId, StringComparer.Ordinal)];

    }

    private static async Task<DataRetentionConflict[]> ReadFactoryConflictRowsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        string code,
        string message,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {

            Add(command, name, value);

        }

        List<DataRetentionConflict> conflicts = [];

        await using DbDataReader reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            conflicts.Add(
                new DataRetentionConflict(
                    code,
                    Guid.Parse(reader.GetString(0)).ToString("D"),
                    message));

        }

        return [.. conflicts];

    }

    /// <summary>
    /// Empties one factory-reset table, holding the retention authorization for Sessions alone.
    /// </summary>
    /// <remarks>
    /// Emptying Sessions cascades into the per-Session turn capacity ledger, whose delete guard
    /// begins denied on every connection, including a pooled one handed back out, so the parent
    /// delete has to hold the scope itself. Only that one table needs the grant, so the scope opens
    /// and closes around its own statement rather than spanning the sweep or the commit.
    /// </remarks>
    private static async Task<int> ClearFactoryTableAsync(
        DbConnection connection,
        DbTransaction transaction,
        FactoryTablePlan table,
        CancellationToken cancellationToken)
    {

        using CovenantSqliteAuthorizationScope? retention =
            string.Equals(table.Table, "Sessions", StringComparison.Ordinal)
                ? CovenantSqliteConnectionInitializer.Instance.Authorize(
                    (SqliteConnection)connection,
                    CovenantSqliteAuthorizationKind.SessionRetention)
                : null;

        return await ExecuteAsync(
            connection,
            transaction,
            $"DELETE FROM \"{table.Table}\"",
            cancellationToken).ConfigureAwait(false);

    }

    private static async Task<bool> FactoryTableExistsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        CancellationToken cancellationToken)
    {

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            "SELECT 1 FROM sqlite_master WHERE name = @table AND type IN ('table', 'view') LIMIT 1";

        Add(command, "@table", table);

        object? result = await command.ExecuteScalarAsync(
            cancellationToken).ConfigureAwait(false);

        return result is not null && result != DBNull.Value;

    }

    private static void AddFactoryPlanItem(
        List<DataRetentionPlanItem> items,
        FactoryTablePlan source,
        long count)
    {

        if (count == 0)
        {

            return;

        }

        items.Add(
            new DataRetentionPlanItem(
                source.DataClass,
                source.Kind == FactoryRecordKind.Physical ? count : 0,
                0,
                0,
                source.Kind == FactoryRecordKind.Derived ? count : 0));

    }

    private static void AddFactoryFilePlanItem(
        List<DataRetentionPlanItem> items,
        RetentionDataClass dataClass,
        long files,
        long bytes)
    {

        if (files == 0)
        {

            return;

        }

        items.Add(new DataRetentionPlanItem(dataClass, 0, files, bytes, 0));

    }

    private static (long Files, long Bytes) CountManagedTreeContents(string root)
    {

        List<FactoryFileCandidate> files = [];

        List<IdentityOwnedFileSystemArtifact> directories = [];

        CaptureManagedTree(root, files, directories);

        return (
            files.Count,
            files.Sum(static file => file.Bytes));

    }

    private FactoryFileSystemSnapshot CaptureFactoryFileSystemSnapshot()
    {

        List<FactoryFileCandidate> files = [];

        List<IdentityOwnedFileSystemArtifact> directories = [];

        CaptureManagedTree(_attachmentsRoot, files, directories);

        CaptureManagedTree(_filesRoot, files, directories);

        CaptureFactoryLogFiles(
            _logsRoot,
            "audit-????????.jsonl",
            files,
            directories);

        CaptureFactoryLogFiles(
            _logsRoot,
            "guardrails-????????.jsonl",
            files,
            directories);

        return new FactoryFileSystemSnapshot(
            [.. files],
            [.. directories
                .OrderByDescending(static artifact => artifact.Path.Length)]);

    }

    private static void CaptureManagedTree(
        string root,
        List<FactoryFileCandidate> files,
        List<IdentityOwnedFileSystemArtifact> directories)
    {

        if (!TryEnsureStrictFactoryRoot(root))
        {

            return;

        }

        Stack<string> pending = new();

        pending.Push(Path.GetFullPath(root));

        while (pending.TryPop(out string? directory))
        {

            foreach (string path in EnumerateStrictFactoryEntries(directory))
            {

                if (!FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                        path,
                        out FileHandleMetadata metadata)
                    || IsFactoryReparsePoint(path))
                {

                    throw new IOException(
                        "Factory reset refused a managed entry whose no-follow identity could not be captured.");

                }

                if (metadata.Kind == FileSystemObjectKind.RegularFile)
                {

                    FactoryFileCandidate file = CaptureFactoryFile(path, files);

                    if (file.Artifact.Metadata != metadata)
                    {

                        throw new IOException(
                            "Factory reset refused a managed file whose identity changed during inventory.");

                    }

                    continue;

                }

                if (metadata.Kind != FileSystemObjectKind.Directory
                    || !IdentityOwnedFileSystemCleanup.TryCapturePath(
                        path,
                        FileSystemObjectKind.Directory,
                        out IdentityOwnedFileSystemArtifact artifact)
                    || artifact.Metadata != metadata)
                {

                    throw new IOException(
                        "Factory reset refused a managed entry that was not an owned regular file or directory.");

                }

                directories.Add(artifact);

                pending.Push(artifact.Path);

            }

        }

    }

    private static void CaptureFactoryLogFiles(
        string root,
        string pattern,
        List<FactoryFileCandidate> files,
        List<IdentityOwnedFileSystemArtifact> directories)
    {

        if (!TryEnsureStrictFactoryRoot(root))
        {

            return;

        }

        foreach (string path in Directory.EnumerateFiles(
                     root,
                     pattern,
                     FactoryLogEnumeration))
        {

            CaptureFactoryFile(path, files);

        }

        foreach (string directory in Directory.EnumerateDirectories(
                     root,
                     ".arcanum-cleanup-*",
                     FactoryLogEnumeration))
        {

            _ = CaptureFactoryQuarantinedLogFiles(
                directory,
                pattern,
                files,
                directories);

        }

    }

    private static bool CaptureFactoryQuarantinedLogFiles(
        string directory,
        string pattern,
        List<FactoryFileCandidate> files,
        List<IdentityOwnedFileSystemArtifact> directories)
    {

        EnsureStrictFactoryDirectory(directory);

        bool captured = false;

        foreach (string path in Directory.EnumerateFiles(
                     directory,
                     pattern,
                     FactoryLogEnumeration))
        {

            CaptureFactoryFile(path, files);

            captured = true;

        }

        foreach (string child in Directory.EnumerateDirectories(
                     directory,
                     ".arcanum-cleanup-*",
                     FactoryLogEnumeration))
        {

            captured |= CaptureFactoryQuarantinedLogFiles(
                child,
                pattern,
                files,
                directories);

        }

        if (!captured)
        {

            return false;

        }

        if (!IdentityOwnedFileSystemCleanup.TryCapturePath(
                directory,
                FileSystemObjectKind.Directory,
                out IdentityOwnedFileSystemArtifact artifact))
        {

            throw new IOException(
                "Factory reset refused an interrupted log-cleanup directory whose no-follow identity could not be captured.");

        }

        directories.Add(artifact);

        return true;

    }

    private static (long Files, long Bytes) CountFactoryLogFiles(
        string root,
        string pattern)
    {

        List<FactoryFileCandidate> files = [];

        foreach (string path in EnumerateFactoryLogFiles(root, pattern))
        {

            CaptureFactoryFile(path, files);

        }

        return (
            files.Count,
            files.Sum(static file => file.Bytes));

    }

    private static IEnumerable<string> EnumerateFactoryLogFiles(
        string root,
        string pattern)
    {

        if (!TryEnsureStrictFactoryRoot(root))
        {

            yield break;

        }

        foreach (string path in Directory.EnumerateFiles(
                     root,
                     pattern,
                     FactoryLogEnumeration))
        {

            yield return path;

        }

        foreach (string directory in Directory.EnumerateDirectories(
                     root,
                     ".arcanum-cleanup-*",
                     FactoryLogEnumeration))
        {

            foreach (string path in EnumerateFactoryQuarantinedLogFiles(
                         directory,
                         pattern))
            {

                yield return path;

            }

        }

    }

    private static IEnumerable<string> EnumerateFactoryQuarantinedLogFiles(
        string directory,
        string pattern)
    {

        EnsureStrictFactoryDirectory(directory);

        foreach (string path in Directory.EnumerateFiles(
                     directory,
                     pattern,
                     FactoryLogEnumeration))
        {

            yield return path;

        }

        foreach (string child in Directory.EnumerateDirectories(
                     directory,
                     ".arcanum-cleanup-*",
                     FactoryLogEnumeration))
        {

            foreach (string path in EnumerateFactoryQuarantinedLogFiles(
                         child,
                         pattern))
            {

                yield return path;

            }

        }

    }

    private static FactoryFileCandidate CaptureFactoryFile(
        string path,
        List<FactoryFileCandidate> files)
    {

        if (!IdentityOwnedFileSystemCleanup.TryCapturePath(
                path,
                FileSystemObjectKind.RegularFile,
                out IdentityOwnedFileSystemArtifact artifact))
        {

            throw new IOException(
                "Factory reset refused a managed file whose no-follow identity could not be captured.");

        }

        FactoryFileCandidate candidate = new(
            artifact,
            new FileInfo(path).Length);

        files.Add(candidate);

        return candidate;

    }

    private static string[] EnumerateStrictFactoryEntries(string directory)
    {

        try
        {

            return
            [
                .. Directory.EnumerateFileSystemEntries(
                        directory,
                        "*",
                        FactoryManagedTreeEnumeration)
                    .OrderBy(static path => path, StringComparer.Ordinal),
            ];

        }
        catch (UnauthorizedAccessException ex)
        {

            throw new IOException(
                "Factory reset could not enumerate every entry in a managed directory.",
                ex);

        }

    }

    private static void EnsureStrictFactoryDirectory(string directory)
    {

        if (!IdentityOwnedFileSystemCleanup.TryCapturePath(
                directory,
                FileSystemObjectKind.Directory,
                out _)
            || IsFactoryReparsePoint(directory))
        {

            throw new IOException(
                "Factory reset refused a managed directory whose no-follow identity could not be captured.");

        }

    }

    private static bool TryEnsureStrictFactoryRoot(string root)
    {

        if (FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                root,
                out FileHandleMetadata metadata))
        {

            if (metadata.Kind != FileSystemObjectKind.Directory
                || IsFactoryReparsePoint(root)
                || !IdentityOwnedFileSystemCleanup.TryCapturePath(
                    root,
                    FileSystemObjectKind.Directory,
                    out IdentityOwnedFileSystemArtifact rootArtifact)
                || rootArtifact.Metadata != metadata)
            {

                throw new IOException(
                    "Factory reset refused a managed root that was not an owned ordinary directory.");

            }

            return true;

        }

        try
        {

            _ = File.GetAttributes(root);

        }
        catch (FileNotFoundException)
        {

            return false;

        }
        catch (DirectoryNotFoundException)
        {

            return false;

        }
        catch (UnauthorizedAccessException ex)
        {

            throw new IOException(
                "Factory reset could not inspect a managed root.",
                ex);

        }

        throw new IOException(
            "Factory reset could not prove the no-follow identity of an existing managed root.");

    }

    private static bool IsFactoryReparsePoint(string path)
    {

        try
        {

            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

        }
        catch (UnauthorizedAccessException ex)
        {

            throw new IOException(
                "Factory reset could not inspect a managed entry without following it.",
                ex);

        }

    }

    private static void ValidateFactoryFileSystemSnapshot(
        FactoryFileSystemSnapshot snapshot)
    {

        foreach (FactoryFileCandidate file in snapshot.Files)
        {

            if (!IdentityOwnedFileSystemCleanup.TryCapturePath(
                    file.Artifact.Path,
                    FileSystemObjectKind.RegularFile,
                    out IdentityOwnedFileSystemArtifact current)
                || current.Metadata != file.Artifact.Metadata)
            {

                throw new RetentionConflictException(
                    "A managed file changed identity after the factory-reset preview.");

            }

        }

        foreach (IdentityOwnedFileSystemArtifact directory in snapshot.Directories)
        {

            if (!IdentityOwnedFileSystemCleanup.TryCapturePath(
                    directory.Path,
                    FileSystemObjectKind.Directory,
                    out IdentityOwnedFileSystemArtifact current)
                || current.Metadata != directory.Metadata)
            {

                throw new RetentionConflictException(
                    "A managed directory changed identity after the factory-reset preview.");

            }

        }

    }

    private static void QuarantineFactoryFiles(
        FactoryFileSystemSnapshot snapshot,
        List<IdentityOwnedFileSystemQuarantine> quarantined)
    {

        foreach (FactoryFileCandidate file in snapshot.Files)
        {

            bool succeeded = IdentityOwnedFileSystemCleanup.TryQuarantine(
                file.Artifact,
                out IdentityOwnedFileSystemQuarantine quarantine);

            if (quarantine != default)
            {

                quarantined.Add(quarantine);

            }

            if (!succeeded || quarantine == default)
            {

                throw new IOException(
                    "Factory reset refused a managed file whose identity changed before quarantine.");

            }

        }

    }

    private static void DeleteQuarantinedFactoryFiles(
        IEnumerable<IdentityOwnedFileSystemQuarantine> quarantinedFiles)
    {

        foreach (IdentityOwnedFileSystemQuarantine quarantine in quarantinedFiles)
        {

            if (!IdentityOwnedFileSystemCleanup.TryDeleteQuarantined(quarantine))
            {

                throw new RetentionQuarantineRecoveryRequiredException(
                    "Factory reset could not finalize a quarantined managed file after the database commit.");

            }

        }

    }

    private static void RestoreQuarantinedFactoryFiles(
        IEnumerable<IdentityOwnedFileSystemQuarantine> quarantinedFiles)
    {

        foreach (IdentityOwnedFileSystemQuarantine quarantine in quarantinedFiles.Reverse())
        {

            if (!IdentityOwnedFileSystemCleanup.TryRestoreQuarantined(quarantine))
            {

                throw new IOException(
                    "Factory reset could not restore a managed file after the database transaction rolled back.");

            }

        }

    }

    private static void DeleteFactoryDirectories(
        IEnumerable<IdentityOwnedFileSystemArtifact> directories)
    {

        foreach (IdentityOwnedFileSystemArtifact directory in directories)
        {

            if (!Directory.Exists(directory.Path)
                || Directory.EnumerateFileSystemEntries(directory.Path).Any())
            {

                continue;

            }

            if (!IdentityOwnedFileSystemCleanup.TryDelete(directory))
            {

                throw new IOException(
                    "Factory reset refused an empty managed directory whose identity changed before deletion.");

            }

        }

    }

    private static bool IsTerminalDaemonExecution(DaemonExecutionSummary execution) =>
        execution.Status is DaemonJobStatus.Completed
            or DaemonJobStatus.Failed
            or DaemonJobStatus.Cancelled;

    private static EnumerationOptions FactoryLogEnumeration { get; } =
        new()
        {

            RecurseSubdirectories = false,

            AttributesToSkip = 0,

            IgnoreInaccessible = false,

            ReturnSpecialDirectories = false,

        };

    private static EnumerationOptions FactoryManagedTreeEnumeration { get; } =
        new()
        {

            RecurseSubdirectories = false,

            AttributesToSkip = 0,

            IgnoreInaccessible = false,

            ReturnSpecialDirectories = false,

        };

    private static string FactoryTerminalOperationStates =>
        $"{(int)LongRunningOperationState.Completed},"
        + $"{(int)LongRunningOperationState.Failed},"
        + $"{(int)LongRunningOperationState.Abandoned}";

    private sealed record FactoryTablePlan(
        string Table,
        RetentionDataClass DataClass,
        FactoryRecordKind Kind,
        string? Predicate = null);

    private sealed record FactoryFileCandidate(
        IdentityOwnedFileSystemArtifact Artifact,
        long Bytes);

    private sealed record FactoryFileSystemSnapshot(
        FactoryFileCandidate[] Files,
        IdentityOwnedFileSystemArtifact[] Directories);

    private enum FactoryRecordKind
    {

        Physical,

        Derived,

    }

}
