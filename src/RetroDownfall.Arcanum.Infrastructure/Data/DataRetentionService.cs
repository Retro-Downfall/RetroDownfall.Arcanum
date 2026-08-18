using System.Data;

using System.Data.Common;

using System.Globalization;

using System.Security.Cryptography;

using System.Text;

using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Daemons;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Infrastructure.Daemons;

using RetroDownfall.Arcanum.Infrastructure.Logging;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Shared read/plan/apply authority for Arcanum-owned persistent data. Every destructive path
/// rebuilds and fingerprints its plan immediately before mutation, then executes the same bounded
/// candidate set under a durable operation lease.
/// </summary>
internal sealed partial class DataRetentionService(
    ArcanumDbContext db,
    IOptionsMonitor<ArcanumSettings> settings,
    ILongRunningOperationStore operations,
    TimeProvider timeProvider,
    ILogger<DataRetentionService> logger,
    string? attachmentsRootOverride = null,
    string? filesRootOverride = null,
    string? logsRootOverride = null,
    IDataRetentionPolicyStore? policyStore = null,
    ISessionAttachmentStore? attachmentStore = null,
    IDaemonExecutionRepository? daemonExecutions = null,
    IDaemonExecutionMutationGate? daemonMutationGate = null,
    IManagedLogMutationGate? managedLogMutationGate = null) : IDataRetentionService
{

    private static readonly int[] ActiveOperationStates =
    [
        (int)LongRunningOperationState.Pending,
        (int)LongRunningOperationState.Running,
        (int)LongRunningOperationState.Waiting,
        (int)LongRunningOperationState.Cancelling,
        (int)LongRunningOperationState.ReconciliationRequired,
    ];

    private readonly string _attachmentsRoot = Path.GetFullPath(
        attachmentsRootOverride ?? ArcanumPaths.AttachmentsDirectory);

    private readonly string _filesRoot = Path.GetFullPath(
        filesRootOverride ?? ArcanumPaths.FilesDirectory);

    private readonly string _logsRoot = Path.GetFullPath(
        logsRootOverride ?? ArcanumPaths.GrimoireDirectory);

    private RetentionSettings CurrentRetention =>
        policyStore?.Current
        ?? settings.CurrentValue.Retention
        ?? new RetentionSettings();

    private readonly DataRetentionLeaseMaintainer _leaseMaintainer = new(
        operations.RenewLeaseAsync,
        timeProvider);

    public async Task<DataRetentionStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {

        RetentionSettings retention = CurrentRetention;

        List<DataRetentionStatusItem> items = [];

        await AddDatabaseStatusAsync(
            items,
            RetentionDataClass.ActiveSessions,
            "Sessions",
            "Status = 'active'",
            "Encrypted Grimoire session headers.",
            retention,
            cancellationToken).ConfigureAwait(false);

        await AddDatabaseStatusAsync(
            items,
            RetentionDataClass.ArchivedSessions,
            "Sessions",
            "Status = 'archived'",
            "Encrypted Grimoire session headers.",
            retention,
            cancellationToken).ConfigureAwait(false);

        await AddCompositeDatabaseStatusAsync(
            items,
            RetentionDataClass.Entries,
            ["Entries", "Entries_fts", "attachment_memory_consultations", "SessionContextPins"],
            "Session-owned transcript rows, full-text records, attachment-memory consultation provenance, and pinned context metadata.",
            retention,
            cancellationToken).ConfigureAwait(false);

        long attachmentRows = await CountTableAsync(
            "SessionAttachments",
            null,
            cancellationToken).ConfigureAwait(false);

        (long attachmentFiles, long attachmentBytes) = MeasureOwnedTree(
            _attachmentsRoot);

        AddStatus(
            items,
            RetentionDataClass.AttachmentVersions,
            attachmentRows,
            0,
            0,
            "SessionAttachments",
            "Versioned attachment metadata in the encrypted Grimoire.",
            retention);

        AddStatus(
            items,
            RetentionDataClass.AttachmentBytes,
            0,
            attachmentFiles,
            attachmentBytes,
            "attachments/",
            "Encrypted attachment envelopes under the Arcanum data root.",
            retention);

        await AddDatabaseStatusAsync(
            items,
            RetentionDataClass.AttachmentChunks,
            "session_attachment_chunks",
            null,
            "Derived attachment text chunks.",
            retention,
            cancellationToken).ConfigureAwait(false);

        await AddCompositeDatabaseStatusAsync(
            items,
            RetentionDataClass.AttachmentEmbeddings,
            [
                "session_attachment_embeddings",
                "session_attachment_embeddings_vec",
                "session_attachment_index_state",
            ],
            "Derived attachment embedding blobs, vector mirrors, and index state.",
            retention,
            cancellationToken).ConfigureAwait(false);

        long uploadedRows = await CountTableAsync(
            "UploadedFiles",
            null,
            cancellationToken).ConfigureAwait(false);

        (long uploadedFiles, long uploadedBytes) = MeasureOwnedTree(
            _filesRoot);

        AddStatus(
            items,
            RetentionDataClass.UploadedFiles,
            uploadedRows,
            uploadedFiles,
            uploadedBytes,
            "UploadedFiles + files/",
            "OpenAI-compatible uploaded-file metadata and encrypted bytes.",
            retention);

        await AddBatchFileStatusAsync(
            items,
            RetentionDataClass.BatchInputFiles,
            "InputFileId",
            retention,
            cancellationToken).ConfigureAwait(false);

        await AddBatchFileStatusAsync(
            items,
            RetentionDataClass.BatchOutputFiles,
            "OutputFileId",
            retention,
            cancellationToken).ConfigureAwait(false);

        await AddBatchFileStatusAsync(
            items,
            RetentionDataClass.BatchErrorFiles,
            "ErrorFileId",
            retention,
            cancellationToken).ConfigureAwait(false);

        await AddDatabaseStatusAsync(
            items,
            RetentionDataClass.CompletedBatches,
            "Batches",
            "Status IN ('completed', 'failed', 'cancelled', 'expired')",
            "Terminal batch rows; referenced file roles remain separately accounted.",
            retention,
            cancellationToken).ConfigureAwait(false);

        await AddCompositeDatabaseStatusAsync(
            items,
            RetentionDataClass.SagaMemories,
            [
                "saga_memories",
                "saga_memory_embeddings",
                "saga_memory_embeddings_vec",
                "saga_memory_attachment_provenance",
                "saga_extraction_watermarks",
            ],
            "Saga facts, embedding companions, typed provenance, and extraction watermarks remain independent from source attachment availability.",
            retention,
            cancellationToken).ConfigureAwait(false);

        await AddCompositeDatabaseStatusAsync(
            items,
            RetentionDataClass.LexiconEntries,
            [
                "lexicon_entries",
                "lexicon_fts",
                "lexicon_fact_attachment_provenance",
            ],
            "Lexicon facts, full-text records, and typed provenance remain independent from source attachment availability.",
            retention,
            cancellationToken).ConfigureAwait(false);

        await AddCompositeDatabaseStatusAsync(
            items,
            RetentionDataClass.WorkspaceChunks,
            ["workspace_file_chunks", "WorkspaceContexts"],
            "Derived workspace text chunks and retained workspace context snapshots.",
            retention,
            cancellationToken).ConfigureAwait(false);

        await AddCompositeDatabaseStatusAsync(
            items,
            RetentionDataClass.WorkspaceEmbeddings,
            ["workspace_file_embeddings", "workspace_file_embeddings_vec"],
            "Derived workspace embedding blobs and vector mirrors.",
            retention,
            cancellationToken).ConfigureAwait(false);

        await AddCompositeDatabaseStatusAsync(
            items,
            RetentionDataClass.SessionEntryEmbeddings,
            ["entry_embeddings", "entry_embeddings_vec"],
            "Derived session-entry embedding blobs and vector mirrors.",
            retention,
            cancellationToken).ConfigureAwait(false);

        await AddCompositeDatabaseStatusAsync(
            items,
            RetentionDataClass.Tapestry,
            [
                "tapestry_generations",
                "tapestry_nodes",
                "tapestry_node_embeddings",
                "tapestry_node_embeddings_vec",
            ],
            "Derived Tapestry generations, model-written summary nodes, embedding blobs, and vector mirrors.",
            retention,
            cancellationToken).ConfigureAwait(false);

        AddLogStatuses(items, retention);

        await AddCompositeDatabaseStatusAsync(
            items,
            RetentionDataClass.IdempotencyClaims,
            ["IdempotencyClaims", "IdempotencyKeys"],
            "Lease-aware idempotency claims and legacy completed-response keys.",
            retention,
            cancellationToken).ConfigureAwait(false);

        await AddDatabaseStatusAsync(
            items,
            RetentionDataClass.InferenceRuns,
            "InferenceRuns",
            null,
            "Accounting authority; Sessions.TotalCostUsd is not used.",
            retention,
            cancellationToken).ConfigureAwait(false);

        await AddDatabaseStatusAsync(
            items,
            RetentionDataClass.BillableOperations,
            "BillableOperations",
            null,
            "Provider/model/token/cost accounting rows.",
            retention,
            cancellationToken).ConfigureAwait(false);

        await AddDatabaseStatusAsync(
            items,
            RetentionDataClass.BudgetReservations,
            "BudgetReservations",
            null,
            "Outstanding reservations always block deletion.",
            retention,
            cancellationToken).ConfigureAwait(false);

        long adjustmentRows = await CountTableAsync(
            "CostAdjustments",
            null,
            cancellationToken).ConfigureAwait(false);

        adjustmentRows += await CountTableAsync(
            "BudgetAlerts",
            null,
            cancellationToken).ConfigureAwait(false);

        AddStatus(
            items,
            RetentionDataClass.CostAdjustments,
            adjustmentRows,
            0,
            0,
            "CostAdjustments + BudgetAlerts",
            "Accounting adjustments and durable budget-alert records.",
            retention);

        await AddDatabaseStatusAsync(
            items,
            RetentionDataClass.LongRunningOperations,
            "LongRunningOperations",
            null,
            "Durable operation and checkpoint history.",
            retention,
            cancellationToken).ConfigureAwait(false);

        await AddDatabaseStatusAsync(
            items,
            RetentionDataClass.SanctumBreaches,
            "SanctumBreaches",
            null,
            "Durable containment breach history.",
            retention,
            cancellationToken).ConfigureAwait(false);

        DaemonExecutionSummary[] daemonHistory = daemonExecutions is null
            ? []
            : await daemonExecutions.GetHistoryAsync(
                null,
                cancellationToken).ConfigureAwait(false);

        long daemonWatermarks = await CountTableAsync(
            "UnseenServantWatermarks",
            null,
            cancellationToken).ConfigureAwait(false);

        AddStatus(
            items,
            RetentionDataClass.DaemonExecutions,
            daemonHistory.LongLength + daemonWatermarks,
            0,
            0,
            "process memory + UnseenServantWatermarks",
            "Volatile daemon execution summaries and durable schedule watermarks; active executions are protected.",
            retention);

        DataRetentionStatusItem[] ordered =
            [.. items.OrderBy(static item => item.DataClass)];

        DataRetentionStatusItem[] physicallyOwned =
            [.. ordered.Where(static item => item.DataClass is not (
                RetentionDataClass.BatchInputFiles
                or RetentionDataClass.BatchOutputFiles
                or RetentionDataClass.BatchErrorFiles))];

        return new DataRetentionStatus(
            timeProvider.GetUtcNow(),
            ordered,
            physicallyOwned.Sum(static item => item.Rows),
            physicallyOwned.Sum(static item => item.Files),
            physicallyOwned.Sum(static item => item.EstimatedBytes),
            [
                "External backups and backup media",
                "OS credential and Data Protection stores",
                "Registered workspaces outside the Arcanum data root",
            ]);

    }

    public Task<DataRetentionPlan> PlanAsync(
        DataRetentionRequest request,
        CancellationToken cancellationToken = default) =>
        request.Operation switch
        {

            DataRetentionOperation.DeleteSession when request.TargetId is Guid sessionId =>
                BuildDeleteSessionPlanAsync(request, sessionId, cancellationToken),

            DataRetentionOperation.DeleteAttachment when request.TargetId is Guid attachmentId =>
                BuildDeleteAttachmentPlanAsync(request, attachmentId, cancellationToken),

            DataRetentionOperation.Prune =>
                BuildUnifiedPrunePlanAsync(request, cancellationToken),

            DataRetentionOperation.ResetMemory when request.MemoryScope is not null =>
                BuildResetMemoryPlanAsync(request, cancellationToken),

            DataRetentionOperation.ResetWorkspace when request.Workspace is not null =>
                BuildWorkspaceResetPlanAsync(
                    request,
                    request.Workspace,
                    cancellationToken),

            DataRetentionOperation.FactoryReset =>
                BuildFactoryResetPlanAsync(request, cancellationToken),

            _ => Task.FromResult(EmptyPlan(
                request,
                new DataRetentionBlocker(
                    RetentionDataClass.ActiveSessions,
                    request.TargetId?.ToString("D") ?? string.Empty,
                    ErrorCodes.Data.InvalidRequest,
                    "The requested data operation is missing its required target or memory scope."))),

        };

    public async Task<Result<DataRetentionApplyResult>> ApplyAsync(
        DataRetentionApplyRequest request,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(request);

        DataRetentionPlan current;

        try
        {

            current = await PlanAsync(
                request.Request,
                cancellationToken).ConfigureAwait(false);

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {

            logger.LogWarning(
                ex,
                "Data-retention apply refused a managed filesystem inventory that could not be proven safe.");

            return Result<DataRetentionApplyResult>.Failure(
                new Error(
                    ErrorCodes.Data.Conflict,
                    "Managed data changed or could not be safely inspected; request a new dry-run before applying."));

        }

        if (!string.IsNullOrWhiteSpace(request.ExpectedPlanId)
            && !string.Equals(
                request.ExpectedPlanId,
                current.PlanId,
                StringComparison.Ordinal))
        {

            return Result<DataRetentionApplyResult>.Failure(
                new Error(
                    ErrorCodes.Data.PlanChanged,
                    "The deletion plan changed after preview; request a new dry-run before applying."));

        }

        if (request.Request.Operation is DataRetentionOperation.DeleteSession
                or DataRetentionOperation.DeleteAttachment
            && current.Items.Length == 0)
        {

            return Result<DataRetentionApplyResult>.Failure(
                new Error(
                    ErrorCodes.Data.NotFound,
                    "The requested data source was not found."));

        }

        if (request.Request.Operation != DataRetentionOperation.Prune
            && current.Blockers.Length > 0)
        {

            return Result<DataRetentionApplyResult>.Failure(
                new Error(
                    ErrorCodes.Data.Blocked,
                    current.Blockers[0].Message));

        }

        if (request.Request.Operation != DataRetentionOperation.Prune
            && current.Conflicts.Length > 0)
        {

            return Result<DataRetentionApplyResult>.Failure(
                new Error(
                    ErrorCodes.Data.Conflict,
                    current.Conflicts[0].Message));

        }

        SessionPlanSnapshot? expectedSessionSnapshot =
            request.Request.Operation == DataRetentionOperation.DeleteSession
                ? await ReadSessionSnapshotAsync(
                    request.Request.TargetId!.Value,
                    cancellationToken).ConfigureAwait(false)
                : null;

        AttachmentPlanSnapshot? expectedAttachmentSnapshot =
            request.Request.Operation == DataRetentionOperation.DeleteAttachment
                ? await ReadAttachmentSnapshotAsync(
                    request.Request.TargetId!.Value,
                    cancellationToken).ConfigureAwait(false)
                : null;

        string ownerId = "data-retention:" + Guid.NewGuid().ToString("N");

        DateTimeOffset now = timeProvider.GetUtcNow();

        string operationKind = request.Request.Operation switch
        {

            DataRetentionOperation.Prune => LongRunningOperationKinds.DataRetentionPrune,

            DataRetentionOperation.FactoryReset =>
                LongRunningOperationKinds.DataRetentionFactoryReset,

            _ => LongRunningOperationKinds.DataRetentionMutation,

        };

        LongRunningOperationRecoveryPolicy recoveryPolicy =
            request.Request.Operation is DataRetentionOperation.Prune
                or DataRetentionOperation.FactoryReset
                ? LongRunningOperationRecoveryPolicy.RestartIdempotently
                : LongRunningOperationRecoveryPolicy.ReconcileAndComplete;

        LongRunningOperationCreateRequest operationRequest = new(
            operationKind,
            recoveryPolicy,
            $"Applying {request.Request.Operation} data-retention plan {current.PlanId}.",
            now,
            SessionId: request.Request.Operation == DataRetentionOperation.DeleteSession
                ? request.Request.TargetId
                : null);

        LongRunningOperation? started = await operations.TryStartSingleFlightAsync(
            operationRequest,
            ownerId,
            now,
            now.Add(DataRetentionLeaseMaintainer.DefaultLeaseDuration),
            cancellationToken).ConfigureAwait(false);

        if (started is null)
        {

            return Result<DataRetentionApplyResult>.Failure(
                new Error(
                    ErrorCodes.Data.Conflict,
                    await DescribeRetentionConflictAsync(cancellationToken).ConfigureAwait(false)));

        }

        LongRunningOperation operation = started;

        LongRunningOperationLeaseResult lease = new(true, operation);

        if (request.Request.Operation == DataRetentionOperation.FactoryReset)
        {

            DataRetentionConflict[] boundaryConflicts = await ReadGlobalConflictsAsync(
                cancellationToken,
                operation.Id).ConfigureAwait(false);

            if (boundaryConflicts.Length > 0)
            {

                LongRunningOperation latest = await operations.GetAsync(
                    operation.Id,
                    cancellationToken).ConfigureAwait(false)
                    ?? lease.Operation;

                bool terminalized = await operations.TryTransitionAsync(
                    operation.Id,
                    latest.Revision,
                    ownerId,
                    LongRunningOperationState.Failed,
                    timeProvider.GetUtcNow(),
                    ErrorCodes.Data.Conflict,
                    cancellationToken).ConfigureAwait(false);

                if (!terminalized)
                {

                    return Result<DataRetentionApplyResult>.Failure(
                        new Error(
                            ErrorCodes.Data.ReconciliationFailed,
                            "A factory-reset conflict appeared, but its durable marker could not be finalized."));

                }

                return Result<DataRetentionApplyResult>.Failure(
                    new Error(
                        ErrorCodes.Data.Conflict,
                        boundaryConflicts[0].Message));

            }

        }

        try
        {

            RetentionMutationJournal? mutationJournal =
                operationKind == LongRunningOperationKinds.DataRetentionMutation
                    ? await PrepareMutationJournalAsync(
                        operation,
                        ownerId,
                        request.Request,
                        expectedSessionSnapshot,
                        expectedAttachmentSnapshot,
                        cancellationToken).ConfigureAwait(false)
                    : null;

            DataRetentionApplyResult applied = request.Request.Operation switch
            {

                DataRetentionOperation.DeleteSession =>
                    await DeleteSessionAsync(
                        operation.Id,
                        current,
                        request.Request.TargetId!.Value,
                        expectedSessionSnapshot,
                        ageCutoff: null,
                        mutationJournal,
                        cancellationToken).ConfigureAwait(false),

                DataRetentionOperation.DeleteAttachment =>
                    await DeleteAttachmentAsync(
                        operation.Id,
                        current,
                        request.Request.TargetId!.Value,
                        expectedAttachmentSnapshot,
                        ageCutoff: null,
                        mutationJournal,
                        cancellationToken).ConfigureAwait(false),

                DataRetentionOperation.Prune =>
                    await ApplyUnifiedPruneAsync(
                        operation.Id,
                        ownerId,
                        current,
                        startIndex: 0,
                        checkpointVersion: 0,
                        saveCheckpoints: true,
                        frozenCutoffs: null,
                        cancellationToken).ConfigureAwait(false),

                DataRetentionOperation.ResetMemory =>
                    await ApplyMemoryResetAsync(
                        operation.Id,
                        current,
                        request.Request.MemoryScope!.Value,
                        cancellationToken).ConfigureAwait(false),

                DataRetentionOperation.ResetWorkspace =>
                    await ApplyWorkspaceResetAsync(
                        operation.Id,
                        current,
                        request.Request.Workspace!,
                        cancellationToken).ConfigureAwait(false),

                DataRetentionOperation.FactoryReset =>
                    await ApplyFactoryResetAsync(
                        operation.Id,
                        current,
                        cancellationToken).ConfigureAwait(false),

                _ => throw new InvalidOperationException("Unsupported data-retention operation."),

            };

            if (!applied.Reconciled)
            {

                throw new IOException(
                    "Post-delete reconciliation found retained owned data for the retention mutation.");

            }

            LongRunningOperation latest = await operations.GetAsync(
                operation.Id,
                cancellationToken).ConfigureAwait(false)
                ?? lease.Operation;

            bool completed = await operations.TryTransitionAsync(
                operation.Id,
                latest.Revision,
                ownerId,
                LongRunningOperationState.Completed,
                timeProvider.GetUtcNow(),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!completed)
            {

                return Result<DataRetentionApplyResult>.Failure(
                    new Error(
                        ErrorCodes.Data.ReconciliationFailed,
                        "Data was pruned, but the durable operation could not be finalized; retry is safe."));

            }

            return Result<DataRetentionApplyResult>.Success(applied);

        }
        catch (RetentionBlockedException ex)
        {

            LongRunningOperation latest = await operations.GetAsync(
                operation.Id,
                CancellationToken.None).ConfigureAwait(false)
                ?? lease.Operation;

            bool terminalized = await operations.TryTransitionAsync(
                operation.Id,
                latest.Revision,
                ownerId,
                LongRunningOperationState.Failed,
                timeProvider.GetUtcNow(),
                ErrorCodes.Data.Blocked,
                CancellationToken.None).ConfigureAwait(false);

            return Result<DataRetentionApplyResult>.Failure(
                terminalized
                    ? new Error(ErrorCodes.Data.Blocked, ex.Message)
                    : new Error(
                        ErrorCodes.Data.ReconciliationFailed,
                        "A retention blocker appeared, but its durable marker could not be finalized."));

        }
        catch (RetentionConflictException ex)
        {

            LongRunningOperation latest = await operations.GetAsync(
                operation.Id,
                CancellationToken.None).ConfigureAwait(false)
                ?? lease.Operation;

            bool terminalized = await operations.TryTransitionAsync(
                operation.Id,
                latest.Revision,
                ownerId,
                LongRunningOperationState.Failed,
                timeProvider.GetUtcNow(),
                ErrorCodes.Data.Conflict,
                CancellationToken.None).ConfigureAwait(false);

            return Result<DataRetentionApplyResult>.Failure(
                terminalized
                    ? new Error(ErrorCodes.Data.Conflict, ex.Message)
                    : new Error(
                        ErrorCodes.Data.ReconciliationFailed,
                        "A retention conflict appeared, but its durable marker could not be finalized."));

        }
        catch (OperationCanceledException)
        {

            // Deliberately non-terminal: a cancelled apply may be partly applied, so the durable row
            // has to stay recoverable. But it must surrender its lease — leaving a five-minute lease
            // on a row nobody is working keeps every retention command blocked for the whole window
            // with an "already active" error naming no owner. ReconciliationRequired carrying the
            // retention recovery code releases the lease and is exactly what FindExpiredAsync adopts
            // on its next pass.
            LongRunningOperation cancelled = await operations.GetAsync(
                operation.Id,
                CancellationToken.None).ConfigureAwait(false)
                ?? lease.Operation;

            _ = await operations.TryTransitionAsync(
                operation.Id,
                cancelled.Revision,
                ownerId,
                LongRunningOperationState.ReconciliationRequired,
                timeProvider.GetUtcNow(),
                ErrorCodes.Data.ReconciliationFailed,
                CancellationToken.None).ConfigureAwait(false);

            throw;

        }
        catch (RetentionQuarantineRecoveryRequiredException ex)
        {

            logger.LogWarning(
                ex,
                "Data-retention operation {OperationId} requires quarantine recovery.",
                operation.Id);

            LongRunningOperation latest = await operations.GetAsync(
                operation.Id,
                CancellationToken.None).ConfigureAwait(false)
                ?? lease.Operation;

            bool terminalized = await operations.TryTransitionAsync(
                operation.Id,
                latest.Revision,
                ownerId,
                LongRunningOperationState.ReconciliationRequired,
                timeProvider.GetUtcNow(),
                ErrorCodes.Data.ReconciliationFailed,
                CancellationToken.None).ConfigureAwait(false);

            return Result<DataRetentionApplyResult>.Failure(
                new Error(
                    ErrorCodes.Data.ReconciliationFailed,
                    terminalized
                        ? "The database mutation committed; quarantined bytes will be finalized by durable recovery."
                        : "The database mutation committed, but its quarantine recovery marker could not be finalized."));

        }
        catch (Exception ex)
        {

            logger.LogError(
                ex,
                "Data-retention operation {OperationId} failed while applying plan {PlanId}.",
                operation.Id,
                current.PlanId);

            LongRunningOperation latest = await operations.GetAsync(
                operation.Id,
                CancellationToken.None).ConfigureAwait(false)
                ?? lease.Operation;

            _ = await operations.TryTransitionAsync(
                operation.Id,
                latest.Revision,
                ownerId,
                LongRunningOperationState.Failed,
                timeProvider.GetUtcNow(),
                ErrorCodes.Data.ReconciliationFailed,
                CancellationToken.None).ConfigureAwait(false);

            return Result<DataRetentionApplyResult>.Failure(
                new Error(
                    ErrorCodes.Data.ReconciliationFailed,
                    "The retention operation failed; its durable history requires operator review."));

        }

    }

    private async Task<DataRetentionPlan> BuildDeleteSessionPlanAsync(
        DataRetentionRequest request,
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        SessionPlanSnapshot? snapshot = await ReadSessionSnapshotAsync(
            sessionId,
            cancellationToken).ConfigureAwait(false);

        if (snapshot is null)
        {

            return FinalizePlan(
                request,
                [],
                [],
                [],
                [],
                requiresConfirmation: true);

        }

        List<DataRetentionPlanItem> items =
        [
            new(
                string.Equals(snapshot.Status, "archived", StringComparison.OrdinalIgnoreCase)
                    ? RetentionDataClass.ArchivedSessions
                    : RetentionDataClass.ActiveSessions,
                1,
                0,
                0,
                0),
            new(
                RetentionDataClass.Entries,
                snapshot.EntryIds.Length,
                0,
                0,
                snapshot.EntryFtsCount
                    + snapshot.AttachmentMemoryConsultationCount),
            new(
                RetentionDataClass.AttachmentVersions,
                snapshot.Attachments.Length,
                0,
                0,
                0),
            new(
                RetentionDataClass.AttachmentBytes,
                0,
                snapshot.Attachments.LongCount(static item => item.FileExists),
                snapshot.Attachments.Sum(static item => item.ByteLength),
                0),
            new(
                RetentionDataClass.SessionEntryEmbeddings,
                0,
                0,
                0,
                snapshot.EntryEmbeddingCount),
            new(
                RetentionDataClass.AttachmentChunks,
                0,
                0,
                0,
                snapshot.AttachmentChunkCount),
            new(
                RetentionDataClass.AttachmentEmbeddings,
                0,
                0,
                0,
                snapshot.AttachmentEmbeddingCount
                    + snapshot.AttachmentVectorEmbeddingCount
                    + snapshot.AttachmentIndexStateCount),
            new(
                RetentionDataClass.SagaMemories,
                0,
                0,
                0,
                snapshot.SagaExtractionWatermarkCount),
            new(
                RetentionDataClass.SessionEntryEmbeddings,
                0,
                0,
                0,
                snapshot.EntryVectorEmbeddingCount),
        ];

        List<DataRetentionBlocker> blockers = [];

        foreach (Guid entryId in snapshot.PinnedEntryIds)
        {

            blockers.Add(
                new DataRetentionBlocker(
                    RetentionDataClass.Entries,
                    entryId.ToString("D"),
                    "Data.PinnedEntry",
                    "A pinned session entry protects this session from deletion."));

        }

        RetentionSettings retention = CurrentRetention;

        if ((retention.ProtectedSessionIds ?? []).Contains(sessionId))
        {

            blockers.Add(
                new DataRetentionBlocker(
                    RetentionDataClass.ArchivedSessions,
                    sessionId.ToString("D"),
                    "Data.SessionHold",
                    "The session is protected by an explicit operator retention hold."));

        }

        blockers.AddRange(
            await ReadContextPinBlockersAsync(
                sessionId,
                cancellationToken).ConfigureAwait(false));

        DataRetentionConflict[] conflicts = await ReadSessionConflictsAsync(
            sessionId,
            cancellationToken).ConfigureAwait(false);

        return FinalizePlan(
            request,
            items,
            blockers,
            conflicts,
            [sessionId.ToString("D")],
            requiresConfirmation: true);

    }

    private async Task<DataRetentionPlan> BuildDeleteAttachmentPlanAsync(
        DataRetentionRequest request,
        Guid attachmentId,
        CancellationToken cancellationToken)
    {

        AttachmentPlanSnapshot? attachment = await ReadAttachmentSnapshotAsync(
            attachmentId,
            cancellationToken).ConfigureAwait(false);

        if (attachment is null)
        {

            return FinalizePlan(
                request,
                [],
                [],
                [],
                [],
                requiresConfirmation: true);

        }

        if (!string.Equals(attachment.State, "Bound", StringComparison.OrdinalIgnoreCase))
        {

            return FinalizePlan(
                request,
                [],
                [
                    new DataRetentionBlocker(
                        RetentionDataClass.AttachmentVersions,
                        attachmentId.ToString("D"),
                        "Data.AttachmentInFlight",
                        "A pending attachment must finish or be reclaimed by attachment GC before deletion."),
                ],
                [],
                [],
                requiresConfirmation: true);

        }

        List<DataRetentionBlocker> blockers = [];

        if (attachment.SessionId is Guid sessionId)
        {

            if ((CurrentRetention.ProtectedSessionIds ?? []).Contains(sessionId))
            {

                blockers.Add(
                    new DataRetentionBlocker(
                        RetentionDataClass.AttachmentVersions,
                        sessionId.ToString("D"),
                        "Data.SessionHold",
                        "The attachment's owning session is protected by an explicit operator retention hold."));

            }

            blockers.AddRange(
                await ReadAttachmentPinBlockersAsync(
                    sessionId,
                    attachmentId,
                    cancellationToken).ConfigureAwait(false));

        }

        List<DataRetentionPlanItem> items =
        [
            new(RetentionDataClass.AttachmentVersions, 1, 0, 0, 0),
            new(
                RetentionDataClass.AttachmentBytes,
                0,
                attachment.FileExists ? 1 : 0,
                attachment.ByteLength,
                0),
            new(RetentionDataClass.AttachmentChunks, 0, 0, 0, attachment.ChunkCount),
            new(
                RetentionDataClass.AttachmentEmbeddings,
                0,
                0,
                0,
                attachment.EmbeddingCount
                    + attachment.VectorEmbeddingCount
                    + attachment.IndexStateCount),
        ];

        DataRetentionConflict[] conflicts = attachment.SessionId is Guid ownerSessionId
            ? await ReadSessionConflictsAsync(
                ownerSessionId,
                cancellationToken).ConfigureAwait(false)
            : [];

        return FinalizePlan(
            request,
            items,
            blockers,
            conflicts,
            [attachmentId.ToString("D")],
            requiresConfirmation: true);

    }

    private async Task AddSessionPruneCandidatesAsync(
        DataRetentionRequest request,
        RetentionSettings retention,
        int remaining,
        List<DataRetentionPlanItem> items,
        List<DataRetentionBlocker> blockers,
        List<DataRetentionConflict> conflicts,
        List<string> candidates,
        CancellationToken cancellationToken)
    {

        if (remaining <= 0)
        {

            return;

        }

        foreach ((string status, RetentionRuleSettings rule) in new[]
                 {
                     ("active", retention.ActiveSessions),
                     ("archived", retention.ArchivedSessions),
                 })
        {

            if (!rule.Enabled || remaining <= 0)
            {

                continue;

            }

            DateTimeOffset cutoff = PrunePlanningTimestamp.AddDays(
                -ArcanumSettingClamps.RetentionRuleDays(rule.Days));

            Guid[] diagnosticSessionIds = await ReadSessionIdsBeforeAsync(
                status,
                cutoff,
                remaining,
                cancellationToken).ConfigureAwait(false);

            foreach (Guid sessionId in diagnosticSessionIds)
            {

                DataRetentionPlan sessionPlan = await BuildDeleteSessionPlanAsync(
                    request with
                    {
                        Operation = DataRetentionOperation.DeleteSession,
                        TargetId = sessionId,
                    },
                    sessionId,
                    cancellationToken).ConfigureAwait(false);

                blockers.AddRange(sessionPlan.Blockers);

                conflicts.AddRange(sessionPlan.Conflicts);

            }

            Guid[] sessionIds = await ReadEligibleSessionIdsBeforeAsync(
                status,
                cutoff,
                remaining,
                retention.ProtectedSessionIds,
                cancellationToken).ConfigureAwait(false);

            foreach (Guid sessionId in sessionIds)
            {

                DataRetentionPlan sessionPlan = await BuildDeleteSessionPlanAsync(
                    request with
                    {
                        Operation = DataRetentionOperation.DeleteSession,
                        TargetId = sessionId,
                    },
                    sessionId,
                    cancellationToken).ConfigureAwait(false);

                if (sessionPlan.Blockers.Length == 0
                    && sessionPlan.Conflicts.Length == 0)
                {

                    candidates.Add("session:" + sessionId.ToString("D"));

                    items.AddRange(sessionPlan.Items);

                    remaining--;

                }

            }

        }

    }

    private async Task<DataRetentionPlan> BuildResetMemoryPlanAsync(
        DataRetentionRequest request,
        CancellationToken cancellationToken)
    {

        (RetentionDataClass dataClass, string[] tables) = request.MemoryScope switch
        {

            MemoryResetScope.Entry =>
                (RetentionDataClass.SessionEntryEmbeddings,
                    new[] { "entry_embeddings", "entry_embeddings_vec" }),

            MemoryResetScope.Attachments =>
                (RetentionDataClass.AttachmentEmbeddings,
                    new[]
                    {
                        "session_attachment_embeddings",
                        "session_attachment_embeddings_vec",
                        "session_attachment_chunks",
                        "session_attachment_index_state",
                    }),

            MemoryResetScope.Workspace =>
                (RetentionDataClass.WorkspaceEmbeddings,
                    new[]
                    {
                        "workspace_file_embeddings",
                        "workspace_file_embeddings_vec",
                        "workspace_file_chunks",
                    }),

            MemoryResetScope.Saga =>
                (RetentionDataClass.SagaMemories,
                    new[]
                    {
                        "saga_memory_embeddings",
                        "saga_memory_embeddings_vec",
                        "saga_memory_attachment_provenance",
                        "saga_extraction_watermarks",
                        "saga_memories",
                    }),

            MemoryResetScope.Lexicon =>
                (RetentionDataClass.LexiconEntries,
                    new[]
                    {
                        "lexicon_fact_attachment_provenance",
                        "lexicon_fts",
                        "lexicon_entries",
                    }),

            _ => throw new InvalidOperationException("Unsupported memory reset scope."),

        };

        long rows = 0;

        foreach (string table in tables)
        {

            rows += await CountTableAsync(
                table,
                null,
                cancellationToken).ConfigureAwait(false);

        }

        return FinalizePlan(
            request,
            rows == 0
                ? []
                : [new DataRetentionPlanItem(dataClass, 0, 0, 0, rows)],
            [],
            await ReadMemoryResetConflictsAsync(
                request.MemoryScope!.Value,
                cancellationToken).ConfigureAwait(false),
            rows == 0 ? [] : [request.MemoryScope!.Value.ToString()],
            requiresConfirmation: true);

    }

    private async Task<DataRetentionPlan> BuildFactoryResetPlanAsync(
        DataRetentionRequest request,
        CancellationToken cancellationToken)
        => await BuildFactoryResetPlanCoreAsync(
            request,
            excludedOperationId: null,
            cancellationToken).ConfigureAwait(false);

    private async Task<DataRetentionApplyResult> DeleteSessionAsync(
        Guid operationId,
        DataRetentionPlan plan,
        Guid sessionId,
        SessionPlanSnapshot? expectedSnapshot,
        DateTimeOffset? ageCutoff,
        RetentionMutationJournal? mutationJournal,
        CancellationToken cancellationToken)
    {

        using IDisposable? sessionGate = attachmentStore is null
            ? null
            : await attachmentStore.AcquireSessionGateAsync(
                sessionId,
                cancellationToken).ConfigureAwait(false);

        SessionPlanSnapshot? snapshot = await ReadSessionSnapshotAsync(
            sessionId,
            cancellationToken).ConfigureAwait(false);

        if (snapshot is null)
        {

            return EmptyApply(operationId, plan);

        }

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        await using DbTransaction transaction = await BeginMutationTransactionAsync(
            connection,
            cancellationToken).ConfigureAwait(false);

        long rowsDeleted = 0;

        long derivedDeleted = 0;

        long filesDeleted = 0;

        long bytesDeleted = 0;

        bool committed = false;

        List<IdentityOwnedFileSystemQuarantine> quarantinedFiles = [];

        try
        {

            SessionPlanSnapshot? transactionSnapshot =
                await ReadSessionSnapshotInTransactionAsync(
                    connection,
                    transaction,
                    sessionId,
                    cancellationToken).ConfigureAwait(false);

            if (transactionSnapshot is null)
            {

                throw new RetentionConflictException(
                    "Session data changed after preview; request a new dry-run before retrying.");

            }

            snapshot = transactionSnapshot;

            if (ageCutoff is DateTimeOffset cutoff
                && !await SessionCandidateOldEnoughInTransactionAsync(
                    connection,
                    transaction,
                    sessionId,
                    snapshot.Status,
                    cutoff,
                    cancellationToken).ConfigureAwait(false))
            {

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return EmptyApply(operationId, plan);

            }

            if (!SessionSnapshotMatchesPlan(snapshot, expectedSnapshot, plan))
            {

                throw new RetentionConflictException(
                    "Session data changed after preview; request a new dry-run before retrying.");

            }

            await RevalidateSessionDeletionBoundaryAsync(
                connection,
                transaction,
                operationId,
                sessionId,
                cancellationToken).ConfigureAwait(false);

            derivedDeleted += await DeleteEntryIndexesAsync(
                connection,
                transaction,
                snapshot.EntryIds,
                cancellationToken).ConfigureAwait(false);

            foreach (AttachmentPlanSnapshot attachment in snapshot.Attachments)
            {

                derivedDeleted += await DeleteAttachmentRowsAsync(
                    connection,
                    transaction,
                    attachment,
                    cancellationToken).ConfigureAwait(false);

            }

            derivedDeleted += await ExecuteAsync(
                connection,
                transaction,
                "DELETE FROM attachment_memory_consultations WHERE lower(replace(SessionId, '-', '')) = @id",
                cancellationToken,
                ("@id", sessionId.ToString("N"))).ConfigureAwait(false);

            derivedDeleted += await ExecuteAsync(
                connection,
                transaction,
                "DELETE FROM saga_extraction_watermarks WHERE lower(replace(SessionId, '-', '')) = @id",
                cancellationToken,
                ("@id", sessionId.ToString("N"))).ConfigureAwait(false);

            derivedDeleted += await ExecuteAsync(
                connection,
                transaction,
                "DELETE FROM Entries_fts WHERE lower(replace(SessionId, '-', '')) = @id",
                cancellationToken,
                ("@id", sessionId.ToString("N"))).ConfigureAwait(false);

            _ = await ExecuteAsync(
                connection,
                transaction,
                "DELETE FROM SessionContextPins WHERE lower(replace(SessionId, '-', '')) = @id",
                cancellationToken,
                ("@id", sessionId.ToString("N"))).ConfigureAwait(false);

            rowsDeleted += await ExecuteAsync(
                connection,
                transaction,
                "DELETE FROM SessionAttachments WHERE lower(replace(SessionId, '-', '')) = @id",
                cancellationToken,
                ("@id", sessionId.ToString("N"))).ConfigureAwait(false);

            rowsDeleted += await ExecuteAsync(
                connection,
                transaction,
                "DELETE FROM Entries WHERE lower(replace(SessionId, '-', '')) = @id",
                cancellationToken,
                ("@id", sessionId.ToString("N"))).ConfigureAwait(false);

            rowsDeleted += await DeleteSessionRowInTransactionAsync(
                connection,
                transaction,
                sessionId,
                cancellationToken).ConfigureAwait(false);

            foreach (AttachmentPlanSnapshot attachment in snapshot.Attachments)
            {

                if (TryQuarantineOwnedFile(
                        _attachmentsRoot,
                        attachment.RelativePath,
                        operationId,
                        mutationJournal,
                        out IdentityOwnedFileSystemQuarantine quarantine,
                        out long deletedBytes))
                {

                    quarantinedFiles.Add(quarantine);

                    filesDeleted++;

                    bytesDeleted += deletedBytes;

                }

            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            committed = true;

        }
        catch
        {

            if (!committed)
            {

                try
                {

                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

                }
                finally
                {

                    RestoreQuarantinedFactoryFiles(quarantinedFiles);

                }

            }

            throw;

        }

        FinalizeOperationQuarantines(quarantinedFiles);

        TryDeleteEmptySessionDirectory(sessionId);

        bool reconciled = snapshot.Attachments.All(
            attachment => !OwnedFileExists(
                _attachmentsRoot,
                attachment.RelativePath));

        reconciled &= await CountTableAsync(
            "Sessions",
            "lower(replace(Id, '-', '')) = @id",
            cancellationToken,
            ("@id", sessionId.ToString("N"))).ConfigureAwait(false) == 0;

        reconciled &= await CountTableAsync(
            "Entries",
            "lower(replace(SessionId, '-', '')) = @id",
            cancellationToken,
            ("@id", sessionId.ToString("N"))).ConfigureAwait(false) == 0;

        reconciled &= await CountTableAsync(
            "SessionAttachments",
            "lower(replace(SessionId, '-', '')) = @id",
            cancellationToken,
            ("@id", sessionId.ToString("N"))).ConfigureAwait(false) == 0;

        reconciled &= await CountTableAsync(
            "session_attachment_chunks",
            "lower(replace(SessionId, '-', '')) = @id",
            cancellationToken,
            ("@id", sessionId.ToString("N"))).ConfigureAwait(false) == 0;

        reconciled &= await CountTableAsync(
            "SessionContextPins",
            "lower(replace(SessionId, '-', '')) = @id",
            cancellationToken,
            ("@id", sessionId.ToString("N"))).ConfigureAwait(false) == 0;

        reconciled &= await CountTableAsync(
            "Entries_fts",
            "lower(replace(SessionId, '-', '')) = @id",
            cancellationToken,
            ("@id", sessionId.ToString("N"))).ConfigureAwait(false) == 0;

        reconciled &= await CountTableAsync(
            "attachment_memory_consultations",
            "lower(replace(SessionId, '-', '')) = @id",
            cancellationToken,
            ("@id", sessionId.ToString("N"))).ConfigureAwait(false) == 0;

        reconciled &= await CountTableAsync(
            "saga_extraction_watermarks",
            "lower(replace(SessionId, '-', '')) = @id",
            cancellationToken,
            ("@id", sessionId.ToString("N"))).ConfigureAwait(false) == 0;

        string[] normalizedEntryIds =
            [.. snapshot.EntryIds.Select(static entryId => entryId.ToString("N"))];

        reconciled &= await CountIdSetAsync(
            "entry_embeddings",
            "lower(replace(EntryId, '-', ''))",
            normalizedEntryIds,
            cancellationToken).ConfigureAwait(false) == 0;

        reconciled &= await CountIdSetAsync(
            "entry_embeddings_vec",
            "lower(replace(EntryId, '-', ''))",
            normalizedEntryIds,
            cancellationToken).ConfigureAwait(false) == 0;

        foreach (AttachmentPlanSnapshot attachment in snapshot.Attachments)
        {

            reconciled &= await CountTableAsync(
                "session_attachment_index_state",
                "lower(replace(AttachmentId, '-', '')) = @id",
                cancellationToken,
                ("@id", attachment.Id.ToString("N"))).ConfigureAwait(false) == 0;

        }

        string[] snapshotChunkIds =
            [.. snapshot.Attachments.SelectMany(static attachment => attachment.ChunkIds)];

        reconciled &= await CountIdSetAsync(
            "session_attachment_embeddings",
            "ChunkId",
            snapshotChunkIds,
            cancellationToken).ConfigureAwait(false) == 0;

        reconciled &= await CountIdSetAsync(
            "session_attachment_embeddings_vec",
            "ChunkId",
            snapshotChunkIds,
            cancellationToken).ConfigureAwait(false) == 0;

        return new DataRetentionApplyResult(
            operationId,
            plan.PlanId,
            rowsDeleted,
            filesDeleted,
            bytesDeleted,
            derivedDeleted,
            reconciled,
            plan.Blockers,
            plan.Conflicts);

    }

    /// <summary>
    /// Removes the Session row itself, under the retention authorization its cascade requires.
    /// </summary>
    /// <remarks>
    /// The Session owns its row in the turn capacity ledger, and that row leaves only through an
    /// authorized retention or capacity transaction. Its delete guard begins denied on every
    /// connection, including a pooled one handed back out, so the parent delete has to hold the
    /// scope itself. The scope covers the delete alone and is released before the caller commits,
    /// so no later statement in this transaction inherits it.
    /// </remarks>
    private static async Task<int> DeleteSessionRowInTransactionAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        using CovenantSqliteAuthorizationScope retention =
            CovenantSqliteConnectionInitializer.Instance.Authorize(
                (SqliteConnection)connection,
                CovenantSqliteAuthorizationKind.SessionRetention);

        return await ExecuteAsync(
            connection,
            transaction,
            "DELETE FROM Sessions WHERE lower(replace(Id, '-', '')) = @id",
            cancellationToken,
            ("@id", sessionId.ToString("N"))).ConfigureAwait(false);

    }

    private async Task<DataRetentionApplyResult> DeleteAttachmentAsync(
        Guid operationId,
        DataRetentionPlan plan,
        Guid attachmentId,
        AttachmentPlanSnapshot? expectedSnapshot,
        DateTimeOffset? ageCutoff,
        RetentionMutationJournal? mutationJournal,
        CancellationToken cancellationToken)
    {

        AttachmentPlanSnapshot? snapshot = await ReadAttachmentSnapshotAsync(
            attachmentId,
            cancellationToken).ConfigureAwait(false);

        if (snapshot is null)
        {

            return EmptyApply(operationId, plan);

        }

        using IDisposable? sessionGate = attachmentStore is null
            || snapshot.SessionId is not Guid sessionId
                ? null
                : await attachmentStore.AcquireSessionGateAsync(
                    sessionId,
                    cancellationToken).ConfigureAwait(false);

        snapshot = await ReadAttachmentSnapshotAsync(
            attachmentId,
            cancellationToken).ConfigureAwait(false);

        if (snapshot is null)
        {

            return EmptyApply(operationId, plan);

        }

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        await using DbTransaction transaction = await BeginMutationTransactionAsync(
            connection,
            cancellationToken).ConfigureAwait(false);

        long derivedDeleted;

        int rowsDeleted;

        bool fileDeleted;

        long bytesDeleted;

        bool committed = false;

        List<IdentityOwnedFileSystemQuarantine> quarantinedFiles = [];

        try
        {

            AttachmentPlanSnapshot? transactionSnapshot =
                await ReadAttachmentSnapshotInTransactionAsync(
                    connection,
                    transaction,
                    attachmentId,
                    cancellationToken).ConfigureAwait(false);

            if (transactionSnapshot is null)
            {

                throw new RetentionConflictException(
                    "Attachment data changed after preview; request a new dry-run before retrying.");

            }

            snapshot = transactionSnapshot;

            if (ageCutoff is DateTimeOffset cutoff
                && !await AttachmentCandidateOldEnoughInTransactionAsync(
                    connection,
                    transaction,
                    attachmentId,
                    cutoff,
                    cancellationToken).ConfigureAwait(false))
            {

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return EmptyApply(operationId, plan);

            }

            if (!AttachmentSnapshotMatchesPlan(snapshot, expectedSnapshot, plan))
            {

                throw new RetentionConflictException(
                    "Attachment data changed after preview; request a new dry-run before retrying.");

            }

            await RevalidateAttachmentDeletionBoundaryAsync(
                connection,
                transaction,
                operationId,
                snapshot,
                cancellationToken).ConfigureAwait(false);

            derivedDeleted = await DeleteAttachmentRowsAsync(
                connection,
                transaction,
                snapshot,
                cancellationToken).ConfigureAwait(false);

            rowsDeleted = await ExecuteAsync(
                connection,
                transaction,
                "DELETE FROM SessionAttachments WHERE lower(replace(Id, '-', '')) = @id",
                cancellationToken,
                ("@id", attachmentId.ToString("N"))).ConfigureAwait(false);

            fileDeleted = TryQuarantineOwnedFile(
                _attachmentsRoot,
                snapshot.RelativePath,
                operationId,
                mutationJournal,
                out IdentityOwnedFileSystemQuarantine quarantine,
                out bytesDeleted);

            if (fileDeleted)
            {

                quarantinedFiles.Add(quarantine);

            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            committed = true;

        }
        catch
        {

            if (!committed)
            {

                try
                {

                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

                }
                finally
                {

                    RestoreQuarantinedFactoryFiles(quarantinedFiles);

                }

            }

            throw;

        }

        FinalizeOperationQuarantines(quarantinedFiles);

        bool reconciled = !OwnedFileExists(_attachmentsRoot, snapshot.RelativePath);

        reconciled &= await CountTableAsync(
            "SessionAttachments",
            "lower(replace(Id, '-', '')) = @id",
            cancellationToken,
            ("@id", attachmentId.ToString("N"))).ConfigureAwait(false) == 0;

        reconciled &= await CountTableAsync(
            "session_attachment_chunks",
            "lower(replace(AttachmentId, '-', '')) = @id",
            cancellationToken,
            ("@id", attachmentId.ToString("N"))).ConfigureAwait(false) == 0;

        reconciled &= await CountTableAsync(
            "session_attachment_index_state",
            "lower(replace(AttachmentId, '-', '')) = @id",
            cancellationToken,
            ("@id", attachmentId.ToString("N"))).ConfigureAwait(false) == 0;

        reconciled &= await CountIdSetAsync(
            "session_attachment_embeddings",
            "ChunkId",
            snapshot.ChunkIds,
            cancellationToken).ConfigureAwait(false) == 0;

        reconciled &= await CountIdSetAsync(
            "session_attachment_embeddings_vec",
            "ChunkId",
            snapshot.ChunkIds,
            cancellationToken).ConfigureAwait(false) == 0;

        return new DataRetentionApplyResult(
            operationId,
            plan.PlanId,
            rowsDeleted,
            fileDeleted ? 1 : 0,
            bytesDeleted,
            derivedDeleted,
            reconciled,
            plan.Blockers,
            plan.Conflicts);

    }

    private async Task<DataRetentionApplyResult> ApplyMemoryResetAsync(
        Guid operationId,
        DataRetentionPlan plan,
        MemoryResetScope scope,
        CancellationToken cancellationToken)
    {

        string[] tables = scope switch
        {

            MemoryResetScope.Entry =>
                ["entry_embeddings_vec", "entry_embeddings"],

            MemoryResetScope.Attachments =>
                [
                    "session_attachment_embeddings_vec",
                    "session_attachment_embeddings",
                    "session_attachment_chunks",
                    "session_attachment_index_state",
                ],

            MemoryResetScope.Workspace =>
                [
                    "workspace_file_embeddings_vec",
                    "workspace_file_embeddings",
                    "workspace_file_chunks",
                ],

            MemoryResetScope.Saga =>
                [
                    "saga_memory_embeddings_vec",
                    "saga_memory_embeddings",
                    "saga_memory_attachment_provenance",
                    "saga_extraction_watermarks",
                    "saga_memories",
                ],

            MemoryResetScope.Lexicon =>
                [
                    "lexicon_fact_attachment_provenance",
                    "lexicon_fts",
                    "lexicon_entries",
                ],

            _ => [],

        };

        List<string> existingTables = [];

        foreach (string table in tables)
        {

            if (await TableExistsAsync(table, cancellationToken).ConfigureAwait(false))
            {

                existingTables.Add(table);

            }

        }

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        await using DbTransaction transaction = await BeginMutationTransactionAsync(
            connection,
            cancellationToken).ConfigureAwait(false);

        long deleted = 0;

        try
        {

            DataRetentionConflict[] conflicts =
                await ReadMemoryResetConflictsInTransactionAsync(
                    connection,
                    transaction,
                    scope,
                    cancellationToken).ConfigureAwait(false);

            if (conflicts.Length > 0)
            {

                throw new RetentionConflictException(conflicts[0].Message);

            }

            long currentRows = 0;

            foreach (string table in existingTables)
            {

                currentRows += await CountInTransactionAsync(
                    connection,
                    transaction,
                    table,
                    null,
                    cancellationToken).ConfigureAwait(false);

            }

            if (currentRows != plan.DerivedRecords
                || plan.Rows != 0
                || plan.Files != 0
                || plan.EstimatedBytes != 0
                || !plan.CandidateIds.SequenceEqual([scope.ToString()]))
            {

                throw new RetentionConflictException(
                    "Memory data changed after preview; request a new dry-run before retrying.");

            }

            foreach (string table in existingTables)
            {

                deleted += await ExecuteAsync(
                    connection,
                    transaction,
                    $"DELETE FROM \"{table}\"",
                    cancellationToken).ConfigureAwait(false);

            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        }
        catch
        {

            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

            throw;

        }

        bool reconciled = true;

        foreach (string table in existingTables)
        {

            reconciled &= await CountTableAsync(
                table,
                null,
                cancellationToken).ConfigureAwait(false) == 0;

        }

        return new DataRetentionApplyResult(
            operationId,
            plan.PlanId,
            0,
            0,
            0,
            deleted,
            reconciled,
            plan.Blockers,
            plan.Conflicts);

    }

    private async Task<SessionPlanSnapshot?> ReadSessionSnapshotAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        string? status = await ScalarStringAsync(
            connection,
            "SELECT Status FROM Sessions WHERE lower(replace(Id, '-', '')) = @id LIMIT 1",
            cancellationToken,
            ("@id", sessionId.ToString("N"))).ConfigureAwait(false);

        if (status is null)
        {

            return null;

        }

        List<Guid> entryIds = [];

        List<Guid> pinnedEntryIds = [];

        await using (DbCommand entries = connection.CreateCommand())
        {

            entries.CommandText =
                "SELECT Id, IsPinned FROM Entries WHERE lower(replace(SessionId, '-', '')) = @id";

            Add(entries, "@id", sessionId.ToString("N"));

            await using DbDataReader reader = await entries
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                Guid entryId = Guid.Parse(reader.GetString(0));

                entryIds.Add(entryId);

                if (Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture) != 0)
                {

                    pinnedEntryIds.Add(entryId);

                }

            }

        }

        List<AttachmentPlanSnapshot> attachments = [];

        await using (DbCommand attachmentCommand = connection.CreateCommand())
        {

            attachmentCommand.CommandText =
                """
                SELECT Id, SessionId, RelativePath, ByteLength, State
                FROM SessionAttachments
                WHERE lower(replace(SessionId, '-', '')) = @id
                """;

            Add(attachmentCommand, "@id", sessionId.ToString("N"));

            await using DbDataReader reader = await attachmentCommand
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                attachments.Add(
                    new AttachmentPlanSnapshot(
                        Guid.Parse(reader.GetString(0)),
                        Guid.Parse(reader.GetString(1)),
                        reader.GetString(2),
                        reader.GetInt64(3),
                        reader.GetString(4),
                        false,
                        [],
                        0,
                        0,
                        0,
                        0));

            }

        }

        for (int index = 0; index < attachments.Count; index++)
        {

            attachments[index] = await PopulateAttachmentDerivedCountsAsync(
                attachments[index],
                cancellationToken).ConfigureAwait(false);

        }

        long entryEmbeddings = await CountJoinedEntryEmbeddingsAsync(
            sessionId,
            cancellationToken).ConfigureAwait(false);

        long entryVectorEmbeddings = await CountJoinedEntryVectorEmbeddingsAsync(
            sessionId,
            cancellationToken).ConfigureAwait(false);

        long entryFts = await CountTableAsync(
            "Entries_fts",
            "lower(replace(SessionId, '-', '')) = @id",
            cancellationToken,
            ("@id", sessionId.ToString("N"))).ConfigureAwait(false);

        long consultations = await CountTableAsync(
            "attachment_memory_consultations",
            "lower(replace(SessionId, '-', '')) = @id",
            cancellationToken,
            ("@id", sessionId.ToString("N"))).ConfigureAwait(false);

        long sagaWatermarks = await CountTableAsync(
            "saga_extraction_watermarks",
            "lower(replace(SessionId, '-', '')) = @id",
            cancellationToken,
            ("@id", sessionId.ToString("N"))).ConfigureAwait(false);

        return new SessionPlanSnapshot(
            status,
            [.. entryIds],
            [.. pinnedEntryIds],
            [.. attachments],
            entryEmbeddings,
            entryVectorEmbeddings,
            entryFts,
            consultations,
            sagaWatermarks,
            attachments.Sum(static attachment => attachment.ChunkCount),
            attachments.Sum(static attachment => attachment.EmbeddingCount),
            attachments.Sum(static attachment => attachment.VectorEmbeddingCount),
            attachments.Sum(static attachment => attachment.IndexStateCount));

    }

    private async Task<AttachmentPlanSnapshot?> ReadAttachmentSnapshotAsync(
        Guid attachmentId,
        CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT Id, SessionId, RelativePath, ByteLength, State
            FROM SessionAttachments
            WHERE lower(replace(Id, '-', '')) = @id
            LIMIT 1
            """;

        Add(command, "@id", attachmentId.ToString("N"));

        await using DbDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            return null;

        }

        AttachmentPlanSnapshot snapshot = new(
            Guid.Parse(reader.GetString(0)),
            reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetString(4),
            false,
            [],
            0,
            0,
            0,
            0);

        return await PopulateAttachmentDerivedCountsAsync(
            snapshot,
            cancellationToken).ConfigureAwait(false);

    }

    private async Task<AttachmentPlanSnapshot> PopulateAttachmentDerivedCountsAsync(
        AttachmentPlanSnapshot snapshot,
        CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        List<string> chunkIds = [];

        await using (DbCommand command = connection.CreateCommand())
        {

            command.CommandText =
                "SELECT ChunkId FROM session_attachment_chunks WHERE lower(replace(AttachmentId, '-', '')) = @id ORDER BY ChunkId";

            Add(command, "@id", snapshot.Id.ToString("N"));

            await using DbDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                chunkIds.Add(reader.GetString(0));

            }

        }

        long embeddings = await CountAttachmentEmbeddingsAsync(
            snapshot.Id,
            cancellationToken).ConfigureAwait(false);

        long vectorEmbeddings = await CountAttachmentVectorEmbeddingsAsync(
            snapshot.Id,
            cancellationToken).ConfigureAwait(false);

        bool fileExists = TryGetOwnedFileLength(
            _attachmentsRoot,
            snapshot.RelativePath,
            out long fileBytes);

        long state = await CountTableAsync(
            "session_attachment_index_state",
            "lower(replace(AttachmentId, '-', '')) = @id",
            cancellationToken,
            ("@id", snapshot.Id.ToString("N"))).ConfigureAwait(false);

        return snapshot with
        {
            ByteLength = fileBytes,
            FileExists = fileExists,
            ChunkIds = [.. chunkIds],
            ChunkCount = chunkIds.Count,
            EmbeddingCount = embeddings,
            VectorEmbeddingCount = vectorEmbeddings,
            IndexStateCount = state,
        };

    }

    private async Task<SessionPlanSnapshot?> ReadSessionSnapshotInTransactionAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        string? status = await ScalarStringInTransactionAsync(
            connection,
            transaction,
            "SELECT Status FROM Sessions WHERE lower(replace(Id, '-', '')) = @id LIMIT 1",
            cancellationToken,
            ("@id", sessionId.ToString("N"))).ConfigureAwait(false);

        if (status is null)
        {

            return null;

        }

        List<Guid> entryIds = [];

        List<Guid> pinnedEntryIds = [];

        await using (DbCommand entries = connection.CreateCommand())
        {

            entries.Transaction = transaction;

            entries.CommandText =
                "SELECT Id, IsPinned FROM Entries WHERE lower(replace(SessionId, '-', '')) = @id ORDER BY Id";

            Add(entries, "@id", sessionId.ToString("N"));

            await using DbDataReader reader = await entries
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                Guid entryId = Guid.Parse(reader.GetString(0));

                entryIds.Add(entryId);

                if (Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture) != 0)
                {

                    pinnedEntryIds.Add(entryId);

                }

            }

        }

        List<Guid> attachmentIds = [];

        await using (DbCommand attachments = connection.CreateCommand())
        {

            attachments.Transaction = transaction;

            attachments.CommandText =
                "SELECT Id FROM SessionAttachments WHERE lower(replace(SessionId, '-', '')) = @id ORDER BY Id";

            Add(attachments, "@id", sessionId.ToString("N"));

            await using DbDataReader reader = await attachments
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                attachmentIds.Add(Guid.Parse(reader.GetString(0)));

            }

        }

        List<AttachmentPlanSnapshot> attachmentSnapshots = [];

        foreach (Guid attachmentId in attachmentIds)
        {

            AttachmentPlanSnapshot? attachment =
                await ReadAttachmentSnapshotInTransactionAsync(
                    connection,
                    transaction,
                    attachmentId,
                    cancellationToken).ConfigureAwait(false);

            if (attachment is null)
            {

                return null;

            }

            attachmentSnapshots.Add(attachment);

        }

        long entryEmbeddings = await CountInTransactionAsync(
            connection,
            transaction,
            "entry_embeddings",
            "lower(replace(EntryId, '-', '')) IN (SELECT lower(replace(Id, '-', '')) FROM Entries WHERE lower(replace(SessionId, '-', '')) = @id)",
            cancellationToken,
            ("@id", sessionId.ToString("N"))).ConfigureAwait(false);

        long entryVectorEmbeddings = await CountInTransactionAsync(
            connection,
            transaction,
            "entry_embeddings_vec",
            "lower(replace(EntryId, '-', '')) IN (SELECT lower(replace(Id, '-', '')) FROM Entries WHERE lower(replace(SessionId, '-', '')) = @id)",
            cancellationToken,
            ("@id", sessionId.ToString("N"))).ConfigureAwait(false);

        long entryFts = await CountInTransactionAsync(
            connection,
            transaction,
            "Entries_fts",
            "lower(replace(SessionId, '-', '')) = @id",
            cancellationToken,
            ("@id", sessionId.ToString("N"))).ConfigureAwait(false);

        long consultations = await CountInTransactionAsync(
            connection,
            transaction,
            "attachment_memory_consultations",
            "lower(replace(SessionId, '-', '')) = @id",
            cancellationToken,
            ("@id", sessionId.ToString("N"))).ConfigureAwait(false);

        long sagaWatermarks = await CountInTransactionAsync(
            connection,
            transaction,
            "saga_extraction_watermarks",
            "lower(replace(SessionId, '-', '')) = @id",
            cancellationToken,
            ("@id", sessionId.ToString("N"))).ConfigureAwait(false);

        return new SessionPlanSnapshot(
            status,
            [.. entryIds],
            [.. pinnedEntryIds],
            [.. attachmentSnapshots],
            entryEmbeddings,
            entryVectorEmbeddings,
            entryFts,
            consultations,
            sagaWatermarks,
            attachmentSnapshots.Sum(static attachment => attachment.ChunkCount),
            attachmentSnapshots.Sum(static attachment => attachment.EmbeddingCount),
            attachmentSnapshots.Sum(static attachment => attachment.VectorEmbeddingCount),
            attachmentSnapshots.Sum(static attachment => attachment.IndexStateCount));

    }

    private async Task<AttachmentPlanSnapshot?> ReadAttachmentSnapshotInTransactionAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid attachmentId,
        CancellationToken cancellationToken)
    {

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            """
            SELECT Id, SessionId, RelativePath, ByteLength, State
            FROM SessionAttachments
            WHERE lower(replace(Id, '-', '')) = @id
            LIMIT 1
            """;

        Add(command, "@id", attachmentId.ToString("N"));

        Guid id;

        Guid? sessionId;

        string relativePath;

        string state;

        await using (DbDataReader reader = await command
                         .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                return null;

            }

            id = Guid.Parse(reader.GetString(0));

            sessionId = reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1));

            relativePath = reader.GetString(2);

            state = reader.GetString(4);

        }

        List<string> chunkIds = [];

        await using (DbCommand chunks = connection.CreateCommand())
        {

            chunks.Transaction = transaction;

            chunks.CommandText =
                "SELECT ChunkId FROM session_attachment_chunks WHERE lower(replace(AttachmentId, '-', '')) = @id ORDER BY ChunkId";

            Add(chunks, "@id", attachmentId.ToString("N"));

            await using DbDataReader reader = await chunks
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                chunkIds.Add(reader.GetString(0));

            }

        }

        long embeddings = await CountInTransactionAsync(
            connection,
            transaction,
            "session_attachment_embeddings",
            "ChunkId IN (SELECT ChunkId FROM session_attachment_chunks WHERE lower(replace(AttachmentId, '-', '')) = @id)",
            cancellationToken,
            ("@id", attachmentId.ToString("N"))).ConfigureAwait(false);

        long vectorEmbeddings = await CountInTransactionAsync(
            connection,
            transaction,
            "session_attachment_embeddings_vec",
            "ChunkId IN (SELECT ChunkId FROM session_attachment_chunks WHERE lower(replace(AttachmentId, '-', '')) = @id)",
            cancellationToken,
            ("@id", attachmentId.ToString("N"))).ConfigureAwait(false);

        long indexState = await CountInTransactionAsync(
            connection,
            transaction,
            "session_attachment_index_state",
            "lower(replace(AttachmentId, '-', '')) = @id",
            cancellationToken,
            ("@id", attachmentId.ToString("N"))).ConfigureAwait(false);

        bool fileExists = TryGetOwnedFileLength(
            _attachmentsRoot,
            relativePath,
            out long fileBytes);

        return new AttachmentPlanSnapshot(
            id,
            sessionId,
            relativePath,
            fileBytes,
            state,
            fileExists,
            [.. chunkIds],
            chunkIds.Count,
            embeddings,
            vectorEmbeddings,
            indexState);

    }

    private async Task RevalidateSessionDeletionBoundaryAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid operationId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        if ((CurrentRetention.ProtectedSessionIds ?? []).Contains(sessionId))
        {

            throw new RetentionBlockedException(
                "The session became protected by an explicit operator retention hold.");

        }

        string? pinnedEntry = await ScalarStringInTransactionAsync(
            connection,
            transaction,
            """
            SELECT Id
            FROM Entries
            WHERE lower(replace(SessionId, '-', '')) = @sessionId
              AND IsPinned <> 0
            LIMIT 1
            """,
            cancellationToken,
            ("@sessionId", sessionId.ToString("N"))).ConfigureAwait(false);

        if (pinnedEntry is not null)
        {

            throw new RetentionBlockedException(
                "A pinned session entry appeared before deletion could begin.");

        }

        string? contextPin = await ScalarStringInTransactionAsync(
            connection,
            transaction,
            """
            SELECT Id
            FROM SessionContextPins
            WHERE lower(replace(SessionId, '-', '')) = @sessionId
            LIMIT 1
            """,
            cancellationToken,
            ("@sessionId", sessionId.ToString("N"))).ConfigureAwait(false);

        if (contextPin is not null)
        {

            throw new RetentionBlockedException(
                "Pinned context appeared before session deletion could begin.");

        }

        DataRetentionConflict[] conflicts = await ReadSessionConflictsInTransactionAsync(
            connection,
            transaction,
            sessionId,
            operationId,
            cancellationToken).ConfigureAwait(false);

        if (conflicts.Length > 0)
        {

            throw new RetentionConflictException(conflicts[0].Message);

        }

    }

    private async Task<bool> SessionCandidateOldEnoughInTransactionAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid sessionId,
        string status,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {

        RetentionRuleSettings? rule = status.ToLowerInvariant() switch
        {

            "active" => CurrentRetention.ActiveSessions,

            "archived" => CurrentRetention.ArchivedSessions,

            _ => null,

        };

        if (rule is null || !rule.Enabled)
        {

            return false;

        }

        return await CountInTransactionAsync(
            connection,
            transaction,
            "Sessions",
            "lower(replace(Id, '-', '')) = @id AND julianday(UpdatedAt) <= julianday(@cutoff)",
            cancellationToken,
            ("@id", sessionId.ToString("N")),
            ("@cutoff", FormatTimestamp(cutoff))).ConfigureAwait(false) > 0;

    }

    private async Task<bool> AttachmentCandidateOldEnoughInTransactionAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid attachmentId,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {

        RetentionRuleSettings rule = CurrentRetention.Attachments;

        if (!rule.Enabled)
        {

            return false;

        }

        return await CountInTransactionAsync(
            connection,
            transaction,
            "SessionAttachments",
            "lower(replace(Id, '-', '')) = @id AND julianday(CreatedAt) <= julianday(@cutoff)",
            cancellationToken,
            ("@id", attachmentId.ToString("N")),
            ("@cutoff", FormatTimestamp(cutoff))).ConfigureAwait(false) > 0;

    }

    private static bool SessionSnapshotMatchesPlan(
        SessionPlanSnapshot snapshot,
        SessionPlanSnapshot? expectedSnapshot,
        DataRetentionPlan plan)
    {

        long rows = 1
            + snapshot.EntryIds.LongLength
            + snapshot.Attachments.LongLength;

        long files = snapshot.Attachments.LongCount(static item => item.FileExists);

        long bytes = snapshot.Attachments.Sum(static item => item.ByteLength);

        long derived = snapshot.EntryEmbeddingCount
            + snapshot.EntryVectorEmbeddingCount
            + snapshot.EntryFtsCount
            + snapshot.AttachmentMemoryConsultationCount
            + snapshot.SagaExtractionWatermarkCount
            + snapshot.AttachmentChunkCount
            + snapshot.AttachmentEmbeddingCount
            + snapshot.AttachmentVectorEmbeddingCount
            + snapshot.AttachmentIndexStateCount;

        if (rows != plan.Rows
            || files != plan.Files
            || bytes != plan.EstimatedBytes
            || derived != plan.DerivedRecords)
        {

            return false;

        }

        if (expectedSnapshot is null)
        {

            return true;

        }

        return string.Equals(
                snapshot.Status,
                expectedSnapshot.Status,
                StringComparison.OrdinalIgnoreCase)
            && snapshot.EntryIds
                .Order()
                .SequenceEqual(expectedSnapshot.EntryIds.Order())
            && AttachmentSnapshotCollectionsMatch(
                snapshot.Attachments,
                expectedSnapshot.Attachments);

    }

    private static bool AttachmentSnapshotMatchesPlan(
        AttachmentPlanSnapshot snapshot,
        AttachmentPlanSnapshot? expectedSnapshot,
        DataRetentionPlan plan)
    {

        long derived = snapshot.ChunkCount
            + snapshot.EmbeddingCount
            + snapshot.VectorEmbeddingCount
            + snapshot.IndexStateCount;

        if (plan.Rows != 1
            || plan.Files != (snapshot.FileExists ? 1 : 0)
            || plan.EstimatedBytes != snapshot.ByteLength
            || plan.DerivedRecords != derived)
        {

            return false;

        }

        return expectedSnapshot is null
            || AttachmentSnapshotsMatch(snapshot, expectedSnapshot);

    }

    private static bool AttachmentSnapshotCollectionsMatch(
        IEnumerable<AttachmentPlanSnapshot> actual,
        IEnumerable<AttachmentPlanSnapshot> expected)
    {

        AttachmentPlanSnapshot[] actualItems =
            [.. actual.OrderBy(static item => item.Id)];

        AttachmentPlanSnapshot[] expectedItems =
            [.. expected.OrderBy(static item => item.Id)];

        return actualItems.Length == expectedItems.Length
            && actualItems.Zip(expectedItems).All(
                static pair => AttachmentSnapshotsMatch(
                    pair.First,
                    pair.Second));

    }

    private static bool AttachmentSnapshotsMatch(
        AttachmentPlanSnapshot actual,
        AttachmentPlanSnapshot expected) =>
        actual.Id == expected.Id
        && actual.SessionId == expected.SessionId
        && string.Equals(actual.RelativePath, expected.RelativePath, StringComparison.Ordinal)
        && actual.ByteLength == expected.ByteLength
        && string.Equals(actual.State, expected.State, StringComparison.OrdinalIgnoreCase)
        && actual.FileExists == expected.FileExists
        && actual.ChunkIds.SequenceEqual(expected.ChunkIds, StringComparer.Ordinal)
        && actual.ChunkCount == expected.ChunkCount
        && actual.EmbeddingCount == expected.EmbeddingCount
        && actual.VectorEmbeddingCount == expected.VectorEmbeddingCount
        && actual.IndexStateCount == expected.IndexStateCount;

    private async Task RevalidateAttachmentDeletionBoundaryAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid operationId,
        AttachmentPlanSnapshot attachment,
        CancellationToken cancellationToken)
    {

        string? state = await ScalarStringInTransactionAsync(
            connection,
            transaction,
            """
            SELECT State
            FROM SessionAttachments
            WHERE lower(replace(Id, '-', '')) = @attachmentId
            LIMIT 1
            """,
            cancellationToken,
            ("@attachmentId", attachment.Id.ToString("N"))).ConfigureAwait(false);

        if (state is null)
        {

            throw new RetentionConflictException(
                "The attachment changed after preview; request a new dry-run before retrying.");

        }

        if (!string.Equals(state, "Bound", StringComparison.OrdinalIgnoreCase))
        {

            throw new RetentionBlockedException(
                "The attachment became in-flight before deletion could begin.");

        }

        if (attachment.SessionId is not Guid sessionId)
        {

            return;

        }

        if ((CurrentRetention.ProtectedSessionIds ?? []).Contains(sessionId))
        {

            throw new RetentionBlockedException(
                "The attachment's owning session became protected by an explicit operator retention hold.");

        }

        string? pin = await ScalarStringInTransactionAsync(
            connection,
            transaction,
            """
            SELECT pin.Id
            FROM SessionContextPins pin
            WHERE pin.Kind = @kind
              AND lower(replace(pin.SessionId, '-', '')) = @sessionId
              AND (
                  lower(replace(pin.TargetIdentifier, '-', '')) = @attachmentId
                  OR pin.TargetIdentifier IN (
                      SELECT LogicalKey
                      FROM SessionAttachments
                      WHERE lower(replace(Id, '-', '')) = @attachmentId))
            LIMIT 1
            """,
            cancellationToken,
            ("@kind", (int)SessionContextPinKind.Attachment),
            ("@sessionId", sessionId.ToString("N")),
            ("@attachmentId", attachment.Id.ToString("N"))).ConfigureAwait(false);

        if (pin is not null)
        {

            throw new RetentionBlockedException(
                "A pinned attachment/context appeared before deletion could begin.");

        }

        DataRetentionConflict[] conflicts = await ReadSessionConflictsInTransactionAsync(
            connection,
            transaction,
            sessionId,
            operationId,
            cancellationToken).ConfigureAwait(false);

        if (conflicts.Length > 0)
        {

            throw new RetentionConflictException(conflicts[0].Message);

        }

    }

    private async Task<DataRetentionConflict[]> ReadSessionConflictsInTransactionAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid sessionId,
        Guid excludedOperationId,
        CancellationToken cancellationToken)
    {

        List<DataRetentionConflict> conflicts = [];

        conflicts.AddRange(
            await ReadConflictsInTransactionAsync(
                connection,
                transaction,
                $"""
                SELECT Id
                FROM LongRunningOperations
                WHERE lower(replace(SessionId, '-', '')) = @sessionId
                  AND lower(replace(Id, '-', '')) <> @excludedOperationId
                  AND State IN ({string.Join(",", ActiveOperationStates)})
                """,
                "Data.ActiveOperation",
                "An active durable operation protects this session.",
                cancellationToken,
                ("@sessionId", sessionId.ToString("N")),
                ("@excludedOperationId", excludedOperationId.ToString("N"))).ConfigureAwait(false));

        conflicts.AddRange(
            await ReadConflictsInTransactionAsync(
                connection,
                transaction,
                """
                SELECT Id
                FROM InferenceRuns
                WHERE lower(replace(SessionId, '-', '')) = @sessionId
                  AND Status = @running
                """,
                "Data.InferenceRunActive",
                "An active inference run protects this session.",
                cancellationToken,
                ("@sessionId", sessionId.ToString("N")),
                ("@running", (int)InferenceRunStatus.Running)).ConfigureAwait(false));

        conflicts.AddRange(
            await ReadConflictsInTransactionAsync(
                connection,
                transaction,
                """
                SELECT reservation.Id
                FROM BudgetReservations reservation
                JOIN InferenceRuns run
                  ON lower(replace(run.Id, '-', '')) = lower(replace(reservation.RunId, '-', ''))
                WHERE lower(replace(run.SessionId, '-', '')) = @sessionId
                  AND reservation.Status = @reserved
                """,
                "Data.BudgetReservationOutstanding",
                "An outstanding budget reservation protects the accounting chain.",
                cancellationToken,
                ("@sessionId", sessionId.ToString("N")),
                ("@reserved", (int)BudgetReservationStatus.Reserved)).ConfigureAwait(false));

        return [.. conflicts
            .DistinctBy(static conflict => (conflict.Code, conflict.ResourceId))
            .OrderBy(static conflict => conflict.Code, StringComparer.Ordinal)
            .ThenBy(static conflict => conflict.ResourceId, StringComparer.Ordinal)];

    }

    private async Task<DataRetentionConflict[]> ReadMemoryResetConflictsInTransactionAsync(
        DbConnection connection,
        DbTransaction transaction,
        MemoryResetScope scope,
        CancellationToken cancellationToken)
    {

        List<DataRetentionConflict> conflicts =
        [
            .. await ReadConflictsInTransactionAsync(
                connection,
                transaction,
                "SELECT Id FROM InferenceRuns WHERE Status = @running",
                "Data.InferenceRunActive",
                "An active inference run protects memory and accounting state.",
                cancellationToken,
                ("@running", (int)InferenceRunStatus.Running)).ConfigureAwait(false),
        ];

        string? operationKind = scope switch
        {

            MemoryResetScope.Attachments => LongRunningOperationKinds.AttachmentPromotion,

            MemoryResetScope.Workspace => LongRunningOperationKinds.WorkspaceIndex,

            _ => null,

        };

        if (operationKind is not null)
        {

            conflicts.AddRange(
                await ReadConflictsInTransactionAsync(
                    connection,
                    transaction,
                    $"""
                    SELECT Id
                    FROM LongRunningOperations
                    WHERE Kind = @kind
                      AND State IN ({string.Join(",", ActiveOperationStates)})
                    """,
                    "Data.ActiveOperation",
                    "An active derived-data operation protects this memory scope.",
                    cancellationToken,
                    ("@kind", operationKind)).ConfigureAwait(false));

        }

        return [.. conflicts
            .DistinctBy(static conflict => (conflict.Code, conflict.ResourceId))];

    }

    private static async Task<DataRetentionConflict[]> ReadConflictsInTransactionAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        string code,
        string message,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {

        List<DataRetentionConflict> conflicts = [];

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {

            Add(command, name, value);

        }

        await using DbDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

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

    private static async Task<string?> ScalarStringInTransactionAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
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

        object? result = await command.ExecuteScalarAsync(
            cancellationToken).ConfigureAwait(false);

        return result is null || result == DBNull.Value
            ? null
            : Convert.ToString(result, CultureInfo.InvariantCulture);

    }

    private static async Task<long> CountInTransactionAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        string? predicate,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {

        await using DbCommand tableCommand = connection.CreateCommand();

        tableCommand.Transaction = transaction;

        tableCommand.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @table";

        Add(tableCommand, "@table", table);

        object? tableResult = await tableCommand.ExecuteScalarAsync(
            cancellationToken).ConfigureAwait(false);

        if (Convert.ToInt64(tableResult, CultureInfo.InvariantCulture) == 0)
        {

            return 0;

        }

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = predicate is null
            ? $"SELECT COUNT(*) FROM \"{table}\""
            : $"SELECT COUNT(*) FROM \"{table}\" WHERE {predicate}";

        foreach ((string name, object value) in parameters)
        {

            Add(command, name, value);

        }

        object? result = await command.ExecuteScalarAsync(
            cancellationToken).ConfigureAwait(false);

        return Convert.ToInt64(result, CultureInfo.InvariantCulture);

    }

    private static async Task<DbTransaction> BeginMutationTransactionAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {

        if (connection is SqliteConnection sqliteConnection)
        {

            return sqliteConnection.BeginTransaction(deferred: false);

        }

        return await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);

    }

    private async Task<DataRetentionBlocker[]> ReadContextPinBlockersAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        if (!await TableExistsAsync(
                "SessionContextPins",
                cancellationToken).ConfigureAwait(false))
        {

            return [];

        }

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        List<DataRetentionBlocker> blockers = [];

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT Id, TargetIdentifier
            FROM SessionContextPins
            WHERE lower(replace(SessionId, '-', '')) = @id
            """;

        Add(command, "@id", sessionId.ToString("N"));

        await using DbDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            blockers.Add(
                new DataRetentionBlocker(
                    RetentionDataClass.Entries,
                    reader.GetString(0),
                    "Data.ContextPin",
                    $"Pinned context '{reader.GetString(1)}' protects this session from deletion."));

        }

        return [.. blockers];

    }

    private async Task<DataRetentionBlocker[]> ReadAttachmentPinBlockersAsync(
        Guid sessionId,
        Guid attachmentId,
        CancellationToken cancellationToken)
    {

        if (!await TableExistsAsync(
                "SessionContextPins",
                cancellationToken).ConfigureAwait(false))
        {

            return [];

        }

        long count = await CountTableAsync(
            "SessionContextPins",
            """
            Kind = @kind
            AND lower(replace(SessionId, '-', '')) = @sessionId
            AND (
                lower(replace(TargetIdentifier, '-', '')) = @attachmentId
                OR TargetIdentifier IN (
                    SELECT LogicalKey
                    FROM SessionAttachments
                    WHERE lower(replace(Id, '-', '')) = @attachmentId))
            """,
            cancellationToken,
            ("@kind", (int)SessionContextPinKind.Attachment),
            ("@sessionId", sessionId.ToString("N")),
            ("@attachmentId", attachmentId.ToString("N"))).ConfigureAwait(false);

        return count == 0
            ? []
            :
            [
                new DataRetentionBlocker(
                    RetentionDataClass.AttachmentVersions,
                    attachmentId.ToString("D"),
                    "Data.PinnedAttachment",
                    "A pinned attachment/context protects this attachment from deletion."),
            ];

    }

    private async Task<DataRetentionConflict[]> ReadSessionConflictsAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        List<DataRetentionConflict> conflicts = [];

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        if (await TableExistsAsync(
                "LongRunningOperations",
                cancellationToken).ConfigureAwait(false))
        {

            await using DbCommand command = connection.CreateCommand();

            command.CommandText =
                $"""
                SELECT Id
                FROM LongRunningOperations
                WHERE lower(replace(SessionId, '-', '')) = @sessionId
                  AND State IN ({string.Join(",", ActiveOperationStates)})
                """;

            Add(command, "@sessionId", sessionId.ToString("N"));

            await using DbDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                conflicts.Add(
                    new DataRetentionConflict(
                        "Data.ActiveOperation",
                        Guid.Parse(reader.GetString(0)).ToString("D"),
                        "An active durable operation protects this session."));

            }

        }

        if (await TableExistsAsync(
                "InferenceRuns",
                cancellationToken).ConfigureAwait(false))
        {

            await using DbCommand command = connection.CreateCommand();

            command.CommandText =
                """
                SELECT Id
                FROM InferenceRuns
                WHERE lower(replace(SessionId, '-', '')) = @sessionId
                  AND Status = @running
                """;

            Add(command, "@sessionId", sessionId.ToString("N"));

            Add(command, "@running", (int)InferenceRunStatus.Running);

            await using DbDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                conflicts.Add(
                    new DataRetentionConflict(
                        "Data.InferenceRunActive",
                        Guid.Parse(reader.GetString(0)).ToString("D"),
                        "An active inference run protects this session."));

            }

        }

        if (await TableExistsAsync(
                "BudgetReservations",
                cancellationToken).ConfigureAwait(false))
        {

            await using DbCommand command = connection.CreateCommand();

            command.CommandText =
                """
                SELECT reservation.Id
                FROM BudgetReservations reservation
                JOIN InferenceRuns run
                  ON lower(replace(run.Id, '-', '')) = lower(replace(reservation.RunId, '-', ''))
                WHERE lower(replace(run.SessionId, '-', '')) = @sessionId
                  AND reservation.Status = @reserved
                """;

            Add(command, "@sessionId", sessionId.ToString("N"));

            Add(command, "@reserved", (int)BudgetReservationStatus.Reserved);

            await using DbDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                conflicts.Add(
                    new DataRetentionConflict(
                        "Data.BudgetReservationOutstanding",
                        Guid.Parse(reader.GetString(0)).ToString("D"),
                        "An outstanding budget reservation protects the accounting chain."));

            }

        }

        return [.. conflicts
            .DistinctBy(static conflict => (conflict.Code, conflict.ResourceId))
            .OrderBy(static conflict => conflict.Code, StringComparer.Ordinal)
            .ThenBy(static conflict => conflict.ResourceId, StringComparer.Ordinal)];

    }

    private async Task<DataRetentionConflict[]> ReadGlobalConflictsAsync(
        CancellationToken cancellationToken,
        Guid? excludedOperationId = null)
    {

        List<DataRetentionConflict> conflicts = [];

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        if (await TableExistsAsync(
                "LongRunningOperations",
                cancellationToken).ConfigureAwait(false))
        {

            await using DbCommand command = connection.CreateCommand();

            string operationExclusion = excludedOperationId is null
                ? string.Empty
                : "AND lower(replace(Id, '-', '')) <> @excludedOperationId";

            command.CommandText =
                $"""
                SELECT Id
                FROM LongRunningOperations
                WHERE State IN ({string.Join(",", ActiveOperationStates)})
                  {operationExclusion}
                """;

            if (excludedOperationId is Guid excludedId)
            {

                Add(
                    command,
                    "@excludedOperationId",
                    excludedId.ToString("N"));

            }

            await using DbDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                conflicts.Add(
                    new DataRetentionConflict(
                        "Data.ActiveOperation",
                        Guid.Parse(reader.GetString(0)).ToString("D"),
                        "An active durable operation conflicts with this global reset."));

            }

        }

        if (await TableExistsAsync(
                "BudgetReservations",
                cancellationToken).ConfigureAwait(false))
        {

            await using DbCommand command = connection.CreateCommand();

            command.CommandText =
                "SELECT Id FROM BudgetReservations WHERE Status = @reserved";

            Add(command, "@reserved", (int)BudgetReservationStatus.Reserved);

            await using DbDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                conflicts.Add(
                    new DataRetentionConflict(
                        "Data.BudgetReservationOutstanding",
                        Guid.Parse(reader.GetString(0)).ToString("D"),
                        "An outstanding budget reservation conflicts with this global reset."));

            }

        }

        conflicts.AddRange(
            await ReadActiveInferenceConflictsAsync(
                cancellationToken).ConfigureAwait(false));

        conflicts.AddRange(
            await ReadActiveIdempotencyConflictsAsync(
                cancellationToken).ConfigureAwait(false));

        conflicts.AddRange(
            await ReadInProgressBatchConflictsAsync(
                cancellationToken).ConfigureAwait(false));

        conflicts.AddRange(
            await ReadActiveDaemonExecutionConflictsAsync(
                cancellationToken).ConfigureAwait(false));

        return [.. conflicts
            .DistinctBy(static conflict => (conflict.Code, conflict.ResourceId))
            .OrderBy(static conflict => conflict.Code, StringComparer.Ordinal)
            .ThenBy(static conflict => conflict.ResourceId, StringComparer.Ordinal)];

    }

    private async Task<DataRetentionConflict[]> ReadActiveDaemonExecutionConflictsAsync(
        CancellationToken cancellationToken)
    {

        if (daemonExecutions is null)
        {

            return [];

        }

        DaemonExecutionSummary[] history = await daemonExecutions.GetHistoryAsync(
            null,
            cancellationToken).ConfigureAwait(false);

        return
        [
            .. history
                .Where(static execution => execution.Status is
                    DaemonJobStatus.Pending or DaemonJobStatus.Running)
                .Select(static execution => new DataRetentionConflict(
                    "Data.DaemonExecutionActive",
                    execution.Id,
                    "An active daemon execution conflicts with this global reset.")),
        ];

    }

    private async Task<long> DeleteEntryIndexesAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid[] entryIds,
        CancellationToken cancellationToken)
    {

        long deleted = 0;

        if (entryIds.Length == 0)
        {

            return deleted;

        }

        // Batched, and the sqlite_master probe is hoisted: this runs inside the open mutation
        // transaction, so a statement per entry holds the write lock for the whole sweep.
        bool vectorTableExists = await TableExistsAsync(
            "entry_embeddings_vec",
            cancellationToken).ConfigureAwait(false);

        string[] normalizedEntryIds =
            [.. entryIds.Select(static entryId => entryId.ToString("N"))];

        foreach (IdSetBatch batch in BuildIdSetBatches(
            "lower(replace(EntryId, '-', ''))",
            normalizedEntryIds))
        {

            if (vectorTableExists)
            {

                deleted += await ExecuteAsync(
                    connection,
                    transaction,
                    $"DELETE FROM entry_embeddings_vec WHERE {batch.Predicate}",
                    cancellationToken,
                    batch.Parameters).ConfigureAwait(false);

            }

            deleted += await ExecuteAsync(
                connection,
                transaction,
                $"DELETE FROM entry_embeddings WHERE {batch.Predicate}",
                cancellationToken,
                batch.Parameters).ConfigureAwait(false);

        }

        return deleted;

    }

    private async Task<long> DeleteAttachmentRowsAsync(
        DbConnection connection,
        DbTransaction transaction,
        AttachmentPlanSnapshot attachment,
        CancellationToken cancellationToken)
    {

        long deleted = 0;

        if (await TableExistsAsync(
                "session_attachment_embeddings_vec",
                cancellationToken).ConfigureAwait(false))
        {

            deleted += await ExecuteAsync(
                connection,
                transaction,
                """
                DELETE FROM session_attachment_embeddings_vec
                WHERE ChunkId IN (
                    SELECT ChunkId
                    FROM session_attachment_chunks
                    WHERE lower(replace(AttachmentId, '-', '')) = @id)
                """,
                cancellationToken,
                ("@id", attachment.Id.ToString("N"))).ConfigureAwait(false);

        }

        deleted += await ExecuteAsync(
            connection,
            transaction,
            """
            DELETE FROM session_attachment_embeddings
            WHERE ChunkId IN (
                SELECT ChunkId
                FROM session_attachment_chunks
                WHERE lower(replace(AttachmentId, '-', '')) = @id)
            """,
            cancellationToken,
            ("@id", attachment.Id.ToString("N"))).ConfigureAwait(false);

        deleted += await ExecuteAsync(
            connection,
            transaction,
            "DELETE FROM session_attachment_chunks WHERE lower(replace(AttachmentId, '-', '')) = @id",
            cancellationToken,
            ("@id", attachment.Id.ToString("N"))).ConfigureAwait(false);

        deleted += await ExecuteAsync(
            connection,
            transaction,
            "DELETE FROM session_attachment_index_state WHERE lower(replace(AttachmentId, '-', '')) = @id",
            cancellationToken,
            ("@id", attachment.Id.ToString("N"))).ConfigureAwait(false);

        return deleted;

    }

    private Task<UploadedFileSnapshot[]> ReadEligibleUploadedFilesBeforeAsync(
        DateTimeOffset cutoff,
        int limit,
        HashSet<Guid> selectedBatches,
        CancellationToken cancellationToken) =>
        ReadUploadedFilesByReferenceEligibilityAsync(
            cutoff,
            limit,
            selectedBatches,
            requireBlockingReference: false,
            cancellationToken);

    private Task<UploadedFileSnapshot[]> ReadBlockedUploadedFilesBeforeAsync(
        DateTimeOffset cutoff,
        int limit,
        HashSet<Guid> selectedBatches,
        CancellationToken cancellationToken) =>
        ReadUploadedFilesByReferenceEligibilityAsync(
            cutoff,
            limit,
            selectedBatches,
            requireBlockingReference: true,
            cancellationToken);

    private async Task<UploadedFileSnapshot[]> ReadUploadedFilesByReferenceEligibilityAsync(
        DateTimeOffset cutoff,
        int limit,
        HashSet<Guid> selectedBatches,
        bool requireBlockingReference,
        CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        List<UploadedFileSnapshot> files = [];

        await using DbCommand command = connection.CreateCommand();

        Guid[] selectedBatchIds = [.. selectedBatches.Order()];

        string selectedClause = selectedBatchIds.Length == 0
            ? string.Empty
            : $"AND lower(replace(batch.Id, '-', '')) NOT IN ({string.Join(", ", selectedBatchIds.Select((_, index) => "@selectedBatch" + index.ToString(CultureInfo.InvariantCulture)))})";

        string blockingReference =
            $"""
            EXISTS (
                SELECT 1
                FROM Batches batch
                WHERE (
                    lower(replace(batch.InputFileId, '-', '')) = lower(replace(file.Id, '-', ''))
                    OR lower(replace(batch.OutputFileId, '-', '')) = lower(replace(file.Id, '-', ''))
                    OR lower(replace(batch.ErrorFileId, '-', '')) = lower(replace(file.Id, '-', '')))
                  {selectedClause})
            """;

        command.CommandText =
            $"""
            SELECT Id, Bytes
            FROM UploadedFiles file
            WHERE julianday(file.CreatedAt) < julianday(@cutoff)
              AND {(requireBlockingReference ? string.Empty : "NOT ")}{blockingReference}
            ORDER BY file.CreatedAt, file.Id
            LIMIT @limit
            """;

        Add(command, "@cutoff", cutoff.ToString("o", CultureInfo.InvariantCulture));

        Add(command, "@limit", limit);

        for (int index = 0; index < selectedBatchIds.Length; index++)
        {

            Add(
                command,
                "@selectedBatch" + index.ToString(CultureInfo.InvariantCulture),
                selectedBatchIds[index].ToString("N"));

        }

        await using DbDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            files.Add(
                new UploadedFileSnapshot(
                    Guid.Parse(reader.GetString(0)),
                    reader.GetInt64(1)));

        }

        return [.. files];

    }

    private async Task<UploadedFileSnapshot?> ReadUploadedFileAsync(
        Guid fileId,
        CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            "SELECT Id, Bytes FROM UploadedFiles WHERE lower(replace(Id, '-', '')) = @id LIMIT 1";

        Add(command, "@id", fileId.ToString("N"));

        await using DbDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new UploadedFileSnapshot(
                Guid.Parse(reader.GetString(0)),
                reader.GetInt64(1))
            : null;

    }

    private async Task<BatchReferenceSnapshot[]> ReadBlockingBatchReferencesAsync(
        Guid fileId,
        HashSet<Guid> selectedBatches,
        int limit,
        CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        List<BatchReferenceSnapshot> references = [];

        await using DbCommand command = connection.CreateCommand();

        Guid[] selectedBatchIds = [.. selectedBatches.Order()];

        string selectedClause = selectedBatchIds.Length == 0
            ? string.Empty
            : $"AND lower(replace(Id, '-', '')) NOT IN ({string.Join(", ", selectedBatchIds.Select((_, index) => "@selectedReference" + index.ToString(CultureInfo.InvariantCulture)))})";

        command.CommandText =
            $"""
            SELECT Id, Status
            FROM Batches
            WHERE (
                lower(replace(InputFileId, '-', '')) = @id
                OR lower(replace(OutputFileId, '-', '')) = @id
                OR lower(replace(ErrorFileId, '-', '')) = @id)
              {selectedClause}
            ORDER BY CASE
                         WHEN Status IN (@completed, @failed, @cancelled, @expired) THEN 1
                         ELSE 0
                     END,
                     Id
            LIMIT @limit
            """;

        Add(command, "@id", fileId.ToString("N"));

        Add(command, "@completed", BatchStatuses.Completed);

        Add(command, "@failed", BatchStatuses.Failed);

        Add(command, "@cancelled", BatchStatuses.Cancelled);

        Add(command, "@expired", BatchStatuses.Expired);

        Add(command, "@limit", limit);

        for (int index = 0; index < selectedBatchIds.Length; index++)
        {

            Add(
                command,
                "@selectedReference" + index.ToString(CultureInfo.InvariantCulture),
                selectedBatchIds[index].ToString("N"));

        }

        await using DbDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            references.Add(
                new BatchReferenceSnapshot(
                    Guid.Parse(reader.GetString(0)),
                    reader.GetString(1)));

        }

        return [.. references];

    }

    private async Task<Guid[]> ReadSessionIdsBeforeAsync(
        string status,
        DateTimeOffset cutoff,
        int limit,
        CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        List<Guid> ids = [];

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT Id
            FROM Sessions
            WHERE Status = @status
              AND julianday(UpdatedAt) < julianday(@cutoff)
            ORDER BY UpdatedAt, Id
            LIMIT @limit
            """;

        Add(command, "@status", status);

        Add(command, "@cutoff", cutoff.ToString("o", CultureInfo.InvariantCulture));

        Add(command, "@limit", limit);

        await using DbDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            ids.Add(Guid.Parse(reader.GetString(0)));

        }

        return [.. ids];

    }

    private async Task<Guid[]> ReadEligibleSessionIdsBeforeAsync(
        string status,
        DateTimeOffset cutoff,
        int limit,
        Guid[] protectedSessionIds,
        CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        List<Guid> ids = [];

        await using DbCommand command = connection.CreateCommand();

        string protectedClause = protectedSessionIds.Length == 0
            ? string.Empty
            : $"AND lower(replace(session.Id, '-', '')) NOT IN ({string.Join(", ", protectedSessionIds.Select((_, index) => "@protected" + index.ToString(CultureInfo.InvariantCulture)))})";

        command.CommandText =
            $"""
            SELECT session.Id
            FROM Sessions session
            WHERE session.Status = @status
              AND julianday(session.UpdatedAt) < julianday(@cutoff)
              AND NOT EXISTS (
                  SELECT 1
                  FROM Entries entry
                  WHERE lower(replace(entry.SessionId, '-', '')) = lower(replace(session.Id, '-', ''))
                    AND entry.IsPinned <> 0)
              AND NOT EXISTS (
                  SELECT 1
                  FROM SessionContextPins pin
                  WHERE lower(replace(pin.SessionId, '-', '')) = lower(replace(session.Id, '-', '')))
              AND NOT EXISTS (
                  SELECT 1
                  FROM LongRunningOperations operation
                  WHERE lower(replace(operation.SessionId, '-', '')) = lower(replace(session.Id, '-', ''))
                    AND operation.State IN ({string.Join(",", ActiveOperationStates)}))
              AND NOT EXISTS (
                  SELECT 1
                  FROM InferenceRuns run
                  WHERE lower(replace(run.SessionId, '-', '')) = lower(replace(session.Id, '-', ''))
                    AND run.Status = @running)
              AND NOT EXISTS (
                  SELECT 1
                  FROM BudgetReservations reservation
                  JOIN InferenceRuns run
                    ON lower(replace(run.Id, '-', '')) = lower(replace(reservation.RunId, '-', ''))
                  WHERE lower(replace(run.SessionId, '-', '')) = lower(replace(session.Id, '-', ''))
                    AND reservation.Status = @reserved)
              {protectedClause}
            ORDER BY session.UpdatedAt, session.Id
            LIMIT @limit
            """;

        Add(command, "@status", status);

        Add(command, "@cutoff", cutoff.ToString("o", CultureInfo.InvariantCulture));

        Add(command, "@running", (int)InferenceRunStatus.Running);

        Add(command, "@reserved", (int)BudgetReservationStatus.Reserved);

        Add(command, "@limit", limit);

        for (int index = 0; index < protectedSessionIds.Length; index++)
        {

            Add(
                command,
                "@protected" + index.ToString(CultureInfo.InvariantCulture),
                protectedSessionIds[index].ToString("N"));

        }

        await using DbDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            ids.Add(Guid.Parse(reader.GetString(0)));

        }

        return [.. ids];

    }

    private DataRetentionPlan FinalizePlan(
        DataRetentionRequest request,
        IEnumerable<DataRetentionPlanItem> items,
        IEnumerable<DataRetentionBlocker> blockers,
        IEnumerable<DataRetentionConflict> conflicts,
        IEnumerable<string> candidateIds,
        bool requiresConfirmation,
        string? planAuthority = null,
        DateTimeOffset? generatedAt = null)
    {

        DataRetentionPlanItem[] orderedItems =
            [.. items
                .GroupBy(static item => item.DataClass)
                .Select(static group => new DataRetentionPlanItem(
                    group.Key,
                    group.Sum(static item => item.Rows),
                    group.Sum(static item => item.Files),
                    group.Sum(static item => item.EstimatedBytes),
                    group.Sum(static item => item.DerivedRecords)))
                .OrderBy(static item => item.DataClass)];

        DataRetentionBlocker[] orderedBlockers =
            [.. blockers
                .DistinctBy(static blocker =>
                    (blocker.DataClass, blocker.ResourceId, blocker.ReasonCode))
                .OrderBy(static blocker => blocker.DataClass)
                .ThenBy(static blocker => blocker.ResourceId, StringComparer.Ordinal)
                .ThenBy(static blocker => blocker.ReasonCode, StringComparer.Ordinal)];

        DataRetentionConflict[] orderedConflicts =
            [.. conflicts
                .DistinctBy(static conflict =>
                    (conflict.Code, conflict.ResourceId))
                .OrderBy(static conflict => conflict.Code, StringComparer.Ordinal)
                .ThenBy(static conflict => conflict.ResourceId, StringComparer.Ordinal)];

        string[] orderedCandidates =
            [.. candidateIds
                .Distinct(StringComparer.Ordinal)];

        string planId = ComputePlanId(
            request,
            orderedItems,
            orderedBlockers,
            orderedConflicts,
            orderedCandidates,
            planAuthority);

        return new DataRetentionPlan(
            planId,
            request,
            generatedAt ?? timeProvider.GetUtcNow(),
            orderedItems,
            orderedBlockers,
            orderedConflicts,
            orderedItems.Sum(static item => item.Rows),
            orderedItems.Sum(static item => item.Files),
            orderedItems.Sum(static item => item.EstimatedBytes),
            orderedItems.Sum(static item => item.DerivedRecords),
            orderedCandidates,
            requiresConfirmation);

    }

    private DataRetentionPlan EmptyPlan(
        DataRetentionRequest request,
        DataRetentionBlocker blocker) =>
        FinalizePlan(
            request,
            [],
            [blocker],
            [],
            [],
            requiresConfirmation: true);

    private static string ComputePlanId(
        DataRetentionRequest request,
        DataRetentionPlanItem[] items,
        DataRetentionBlocker[] blockers,
        DataRetentionConflict[] conflicts,
        string[] candidates,
        string? planAuthority)
    {

        StringBuilder canonical = new();

        canonical.Append((int)request.Operation)
            .Append('|')
            .Append(request.TargetId?.ToString("N") ?? string.Empty)
            .Append('|')
            .Append(request.MemoryScope is null
                ? string.Empty
                : ((int)request.MemoryScope.Value).ToString(CultureInfo.InvariantCulture));

        foreach (DataRetentionPlanItem item in items)
        {

            canonical.Append("|i:")
                .Append((int)item.DataClass)
                .Append(':')
                .Append(item.Rows)
                .Append(':')
                .Append(item.Files)
                .Append(':')
                .Append(item.EstimatedBytes)
                .Append(':')
                .Append(item.DerivedRecords);

        }

        foreach (DataRetentionBlocker blocker in blockers)
        {

            canonical.Append("|b:")
                .Append((int)blocker.DataClass)
                .Append(':')
                .Append(blocker.ResourceId)
                .Append(':')
                .Append(blocker.ReasonCode);

        }

        foreach (DataRetentionConflict conflict in conflicts)
        {

            canonical.Append("|c:")
                .Append(conflict.Code)
                .Append(':')
                .Append(conflict.ResourceId);

        }

        foreach (string candidate in candidates)
        {

            canonical.Append("|r:").Append(candidate);

        }

        if (!string.IsNullOrEmpty(planAuthority))
        {

            canonical.Append("|a:").Append(planAuthority);

        }

        byte[] hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(canonical.ToString()));

        return Convert.ToHexString(hash);

    }

    private async Task AddDatabaseStatusAsync(
        List<DataRetentionStatusItem> items,
        RetentionDataClass dataClass,
        string table,
        string? predicate,
        string provenance,
        RetentionSettings retention,
        CancellationToken cancellationToken)
    {

        long rows = await CountTableAsync(
            table,
            predicate,
            cancellationToken).ConfigureAwait(false);

        AddStatus(
            items,
            dataClass,
            rows,
            0,
            0,
            table,
            provenance,
            retention);

    }

    private async Task AddCompositeDatabaseStatusAsync(
        List<DataRetentionStatusItem> items,
        RetentionDataClass dataClass,
        string[] tables,
        string provenance,
        RetentionSettings retention,
        CancellationToken cancellationToken)
    {

        long rows = 0;

        foreach (string table in tables)
        {

            rows += await CountTableAsync(
                table,
                null,
                cancellationToken).ConfigureAwait(false);

        }

        AddStatus(
            items,
            dataClass,
            rows,
            0,
            0,
            string.Join(" + ", tables),
            provenance,
            retention);

    }

    private static void AddStatus(
        List<DataRetentionStatusItem> items,
        RetentionDataClass dataClass,
        long rows,
        long files,
        long estimatedBytes,
        string store,
        string provenance,
        RetentionSettings retention)
    {

        RetentionRuleSettings? rule = DataRetentionSettingsCatalog.ResolveRule(
            retention,
            dataClass);

        int? days = rule is null
            ? null
            : ArcanumSettingClamps.RetentionRuleDays(rule.Days);

        if (dataClass is RetentionDataClass.InferenceRuns
            or RetentionDataClass.BillableOperations
            or RetentionDataClass.BudgetReservations
            or RetentionDataClass.CostAdjustments)
        {

            days = Math.Max(
                days ?? 0,
                ArcanumSettingClamps.RetentionAccountingMinimumDays(
                    retention.AccountingMinimumDays));

        }

        items.Add(
            new DataRetentionStatusItem(
                dataClass,
                rows,
                files,
                estimatedBytes,
                rule?.Enabled ?? false,
                days,
                store,
                provenance));

    }

    private void AddLogStatuses(
        List<DataRetentionStatusItem> items,
        RetentionSettings retention)
    {

        (long auditFiles, long auditBytes) = CountFiles(
            _logsRoot,
            "audit-????????.jsonl");

        (long guardrailFiles, long guardrailBytes) = CountFiles(
            _logsRoot,
            "guardrails-????????.jsonl");

        AddStatus(
            items,
            RetentionDataClass.AuditLogs,
            0,
            auditFiles,
            auditBytes,
            "dated audit JSONL",
            "Append-only inference audit logs; record bodies are not loaded for status.",
            retention);

        AddStatus(
            items,
            RetentionDataClass.GuardrailLogs,
            0,
            guardrailFiles,
            guardrailBytes,
            "dated guardrail JSONL",
            "Append-only guardrail audit logs; record bodies are not loaded for status.",
            retention);

    }

    private async Task<long> CountTableAsync(
        string table,
        string? predicate,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {

        if (!await TableExistsAsync(
                table,
                cancellationToken).ConfigureAwait(false))
        {

            return 0;

        }

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();

        command.CommandText = predicate is null
            ? $"SELECT COUNT(*) FROM \"{table}\""
            : $"SELECT COUNT(*) FROM \"{table}\" WHERE {predicate}";

        foreach ((string name, object value) in parameters)
        {

            Add(command, name, value);

        }

        object? result = await command.ExecuteScalarAsync(
            cancellationToken).ConfigureAwait(false);

        return Convert.ToInt64(result, CultureInfo.InvariantCulture);

    }

    /// <summary>
    /// Names the operation that currently owns the retention single-flight.
    /// </summary>
    /// <remarks>
    /// A bare "already active" gives the operator nothing to act on — the blocker can be a row an
    /// interrupted command left behind, and until it is named there is no way to tell that apart
    /// from a sweep genuinely in flight.
    /// </remarks>
    private async Task<string> DescribeRetentionConflictAsync(CancellationToken cancellationToken)
    {

        const string bare = "Another data-retention operation is already active.";

        string[] retentionKinds =
        [
            LongRunningOperationKinds.DataRetentionPrune,

            LongRunningOperationKinds.DataRetentionMutation,

            LongRunningOperationKinds.DataRetentionFactoryReset,
        ];

        try
        {

            foreach (string kind in retentionKinds)
            {

                IReadOnlyList<LongRunningOperation> active = await operations.ListAsync(
                    new LongRunningOperationQuery(Kind: kind),
                    cancellationToken).ConfigureAwait(false);

                foreach (LongRunningOperation blocker in active)
                {

                    if (blocker.State is not (LongRunningOperationState.Pending
                        or LongRunningOperationState.Running
                        or LongRunningOperationState.Waiting
                        or LongRunningOperationState.Cancelling
                        or LongRunningOperationState.ReconciliationRequired))
                    {

                        continue;

                    }

                    return "Another data-retention operation is already active: "
                        + $"{blocker.Kind} {blocker.Id:D} is {blocker.State}.";

                }

            }

        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {

            logger.LogDebug(
                ex,
                "Could not read the data-retention operation blocking this request.");

        }

        return bare;

    }

    /// <summary>
    /// Counts rows of <paramref name="table"/> whose <paramref name="keyExpression"/> falls inside an
    /// explicit id set, in batches rather than one statement per id.
    /// </summary>
    /// <remarks>
    /// The id set stays explicit on purpose. Post-commit reconciliation runs after the parent rows
    /// are already deleted and committed, so a count joined back through the parent would be
    /// unconditionally zero and would turn orphan/resurrection detection into a no-op.
    /// </remarks>
    private async Task<long> CountIdSetAsync(
        string table,
        string keyExpression,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken)
    {

        long total = 0;

        foreach (IdSetBatch batch in BuildIdSetBatches(keyExpression, ids))
        {

            total += await CountTableAsync(
                table,
                batch.Predicate,
                cancellationToken,
                batch.Parameters).ConfigureAwait(false);

        }

        return total;

    }

    /// <summary>
    /// Splits an id set into bounded <c>IN (...)</c> predicates so no statement exceeds SQLite's
    /// host-parameter limit.
    /// </summary>
    private static IEnumerable<IdSetBatch> BuildIdSetBatches(
        string keyExpression,
        IReadOnlyList<string> ids)
    {

        const int batchSize = 500;

        for (int start = 0; start < ids.Count; start += batchSize)
        {

            int length = Math.Min(batchSize, ids.Count - start);

            (string Name, object Value)[] parameters = new (string Name, object Value)[length];

            StringBuilder placeholders = new();

            for (int index = 0; index < length; index++)
            {

                string name = "@id" + index.ToString(CultureInfo.InvariantCulture);

                parameters[index] = (name, ids[start + index]);

                if (index > 0)
                {

                    _ = placeholders.Append(", ");

                }

                _ = placeholders.Append(name);

            }

            yield return new IdSetBatch(
                $"{keyExpression} IN ({placeholders})",
                parameters);

        }

    }

    private async Task<long> SumColumnAsync(
        string table,
        string column,
        CancellationToken cancellationToken)
    {

        if (!await TableExistsAsync(
                table,
                cancellationToken).ConfigureAwait(false))
        {

            return 0;

        }

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            $"SELECT COALESCE(SUM(\"{column}\"), 0) FROM \"{table}\"";

        object? result = await command.ExecuteScalarAsync(
            cancellationToken).ConfigureAwait(false);

        return Convert.ToInt64(result, CultureInfo.InvariantCulture);

    }

    private async Task<long> CountJoinedEntryEmbeddingsAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        if (!await TableExistsAsync(
                "entry_embeddings",
                cancellationToken).ConfigureAwait(false))
        {

            return 0;

        }

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT COUNT(*)
            FROM entry_embeddings embedding
            JOIN Entries entry
              ON lower(replace(entry.Id, '-', '')) = lower(replace(embedding.EntryId, '-', ''))
            WHERE lower(replace(entry.SessionId, '-', '')) = @sessionId
            """;

        Add(command, "@sessionId", sessionId.ToString("N"));

        object? result = await command.ExecuteScalarAsync(
            cancellationToken).ConfigureAwait(false);

        return Convert.ToInt64(result, CultureInfo.InvariantCulture);

    }

    private async Task<long> CountJoinedEntryVectorEmbeddingsAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        if (!await TableExistsAsync(
                "entry_embeddings_vec",
                cancellationToken).ConfigureAwait(false))
        {

            return 0;

        }

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT COUNT(*)
            FROM entry_embeddings_vec embedding
            JOIN Entries entry
              ON lower(replace(entry.Id, '-', '')) = lower(replace(embedding.EntryId, '-', ''))
            WHERE lower(replace(entry.SessionId, '-', '')) = @sessionId
            """;

        Add(command, "@sessionId", sessionId.ToString("N"));

        object? result = await command.ExecuteScalarAsync(
            cancellationToken).ConfigureAwait(false);

        return Convert.ToInt64(result, CultureInfo.InvariantCulture);

    }

    private async Task<long> CountAttachmentEmbeddingsAsync(
        Guid attachmentId,
        CancellationToken cancellationToken)
    {

        if (!await TableExistsAsync(
                "session_attachment_embeddings",
                cancellationToken).ConfigureAwait(false))
        {

            return 0;

        }

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT COUNT(*)
            FROM session_attachment_embeddings embedding
            JOIN session_attachment_chunks chunk
              ON chunk.ChunkId = embedding.ChunkId
            WHERE lower(replace(chunk.AttachmentId, '-', '')) = @id
            """;

        Add(command, "@id", attachmentId.ToString("N"));

        object? result = await command.ExecuteScalarAsync(
            cancellationToken).ConfigureAwait(false);

        return Convert.ToInt64(result, CultureInfo.InvariantCulture);

    }

    private async Task<long> CountAttachmentVectorEmbeddingsAsync(
        Guid attachmentId,
        CancellationToken cancellationToken)
    {

        if (!await TableExistsAsync(
                "session_attachment_embeddings_vec",
                cancellationToken).ConfigureAwait(false))
        {

            return 0;

        }

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT COUNT(*)
            FROM session_attachment_embeddings_vec embedding
            JOIN session_attachment_chunks chunk
              ON chunk.ChunkId = embedding.ChunkId
            WHERE lower(replace(chunk.AttachmentId, '-', '')) = @id
            """;

        Add(command, "@id", attachmentId.ToString("N"));

        object? result = await command.ExecuteScalarAsync(
            cancellationToken).ConfigureAwait(false);

        return Convert.ToInt64(result, CultureInfo.InvariantCulture);

    }

    private async Task<bool> TableExistsAsync(
        string table,
        CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT 1
            FROM sqlite_master
            WHERE name = @table AND type IN ('table', 'view')
            LIMIT 1
            """;

        Add(command, "@table", table);

        object? result = await command.ExecuteScalarAsync(
            cancellationToken).ConfigureAwait(false);

        return result is not null && result != DBNull.Value;

    }

    private async Task<DbConnection> OpenConnectionAsync(
        CancellationToken cancellationToken)
    {

        DbConnection connection = db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {

            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        }

        return connection;

    }

    private static async Task<string?> ScalarStringAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {

        await using DbCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {

            Add(command, name, value);

        }

        object? result = await command.ExecuteScalarAsync(
            cancellationToken).ConfigureAwait(false);

        return result is null or DBNull ? null : Convert.ToString(result, CultureInfo.InvariantCulture);

    }

    private static async Task<int> ExecuteAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
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

        return await command.ExecuteNonQueryAsync(
            cancellationToken).ConfigureAwait(false);

    }

    private async Task<int> ExecuteStandaloneAsync(
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {

            Add(command, name, value);

        }

        return await command.ExecuteNonQueryAsync(
            cancellationToken).ConfigureAwait(false);

    }

    private static void Add(
        DbCommand command,
        string name,
        object value)
    {

        DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        _ = command.Parameters.Add(parameter);

    }

    private static bool TryGetOwnedFileLength(
        string root,
        string relativePath,
        out long bytes)
    {

        bytes = 0;

        string fullRoot = Path.GetFullPath(root);

        string candidate = Path.GetFullPath(
            Path.Combine(fullRoot, relativePath));

        if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                fullRoot,
                candidate,
                out _)
            || !File.Exists(candidate))
        {

            return false;

        }

        bytes = new FileInfo(candidate).Length;

        return true;

    }

    private bool TryDeleteOwnedFile(
        string root,
        string relativePath,
        out long deletedBytes)
    {

        deletedBytes = 0;

        string fullRoot = Path.GetFullPath(root);

        string candidate = Path.GetFullPath(
            Path.Combine(fullRoot, relativePath));

        if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                fullRoot,
                candidate,
                out _))
        {

            logger.LogWarning(
                "Retention refused a file path outside its selected root: {RelativePath}",
                relativePath);

            return false;

        }

        if (!File.Exists(candidate))
        {

            return false;

        }

        if (!IdentityOwnedFileSystemCleanup.TryCapturePath(
                candidate,
                FileSystemObjectKind.RegularFile,
                out IdentityOwnedFileSystemArtifact artifact))
        {

            if (!File.Exists(candidate))
            {

                return false;

            }

            throw new IOException(
                "Retention refused a file whose no-follow identity could not be captured.");

        }

        FileInfo info = new(candidate);

        deletedBytes = info.Length;

        if (!IdentityOwnedFileSystemCleanup.TryDelete(artifact))
        {

            deletedBytes = 0;

            throw new IOException(
                "Retention refused a file whose identity changed before deletion.");

        }

        return true;

    }

    private async Task<RetentionMutationJournal> PrepareMutationJournalAsync(
        LongRunningOperation operation,
        string ownerId,
        DataRetentionRequest request,
        SessionPlanSnapshot? sessionSnapshot,
        AttachmentPlanSnapshot? attachmentSnapshot,
        CancellationToken cancellationToken)
    {

        string subtype;

        string target;

        IEnumerable<AttachmentPlanSnapshot> attachments;

        switch (request.Operation)
        {

            case DataRetentionOperation.DeleteSession:
                subtype = "delete-session";

                target = request.TargetId!.Value.ToString("D");

                attachments = sessionSnapshot?.Attachments ?? [];

                break;

            case DataRetentionOperation.DeleteAttachment:
                subtype = "delete-attachment";

                target = request.TargetId!.Value.ToString("D");

                attachments = attachmentSnapshot is null
                    ? []
                    : [attachmentSnapshot];

                break;

            case DataRetentionOperation.ResetMemory:
                subtype = "reset-memory";

                target = ((int)request.MemoryScope!.Value).ToString(
                    CultureInfo.InvariantCulture);

                attachments = [];

                break;

            case DataRetentionOperation.ResetWorkspace:
                subtype = "reset-workspace";

                target = request.Workspace!.CampaignId.ToString("N")
                    + ":"
                    + request.Workspace.WorkspaceRoot;

                attachments = [];

                break;

            default:
                throw new InvalidDataException(
                    "The durable mutation journal received an unsupported request subtype.");

        }

        List<RetentionMutationJournalEntry> entries = [];

        foreach (AttachmentPlanSnapshot attachment in attachments
                     .Where(static item => item.FileExists)
                     .OrderBy(static item => item.RelativePath, StringComparer.Ordinal))
        {

            string fullRoot = _attachmentsRoot;

            string path = Path.GetFullPath(
                Path.Combine(fullRoot, attachment.RelativePath));

            if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                    fullRoot,
                    path,
                    out _)
                || !IdentityOwnedFileSystemCleanup.TryCapturePath(
                    path,
                    FileSystemObjectKind.RegularFile,
                    out IdentityOwnedFileSystemArtifact artifact))
            {

                throw new RetentionConflictException(
                    "A selected attachment changed before its durable deletion journal was written.");

            }

            entries.Add(
                new RetentionMutationJournalEntry(
                    "attachments",
                    attachment.RelativePath,
                    artifact.Metadata));

        }

        RetentionMutationJournal journal = new(
            subtype,
            target,
            [.. entries]);

        byte[] payload = SerializeMutationJournal(journal);

        bool saved = await operations.SaveCheckpointAsync(
            operation.Id,
            ownerId,
            expectedCheckpointVersion: 0,
            checkpointVersion: 2,
            payload,
            checkpointReference: "retention-mutation:" + operation.Id.ToString("N"),
            operation.PublicSummary,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);

        if (!saved)
        {

            throw new InvalidOperationException(
                "The retention mutation journal could not be saved atomically.");

        }

        return journal;

    }

    private static byte[] SerializeMutationJournal(
        RetentionMutationJournal journal)
    {

        StringBuilder body = new();

        body.Append("ARCAMUT2\n")
            .Append(journal.Subtype)
            .Append('\n')
            .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(journal.Target)))
            .Append('\n')
            .Append(journal.Entries.Length.ToString(CultureInfo.InvariantCulture))
            .Append('\n');

        foreach (RetentionMutationJournalEntry entry in journal.Entries)
        {

            body.Append("E:")
                .Append(entry.RootRole)
                .Append(':')
                .Append(Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(entry.RelativePath)))
                .Append(':')
                .Append(entry.Metadata.Identity.VolumeId.ToString(
                    CultureInfo.InvariantCulture))
                .Append(':')
                .Append(entry.Metadata.Identity.FileId.ToString(
                    CultureInfo.InvariantCulture))
                .Append(':')
                .Append(entry.Metadata.HardLinkCount.ToString(
                    CultureInfo.InvariantCulture))
                .Append(':')
                .Append(((int)entry.Metadata.Kind).ToString(
                    CultureInfo.InvariantCulture))
                .Append('\n');

        }

        byte[] canonical = Encoding.UTF8.GetBytes(body.ToString());

        body.Append("H:").Append(Convert.ToHexString(SHA256.HashData(canonical))).Append('\n');

        return Encoding.UTF8.GetBytes(body.ToString());

    }

    private static RetentionMutationJournal ParseMutationJournal(byte[] payload)
    {

        string[] lines = Encoding.UTF8
            .GetString(payload)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        if (lines.Length < 5
            || !string.Equals(lines[0], "ARCAMUT2", StringComparison.Ordinal)
            || lines[1] is not (
                "delete-session"
                or "delete-attachment"
                or "reset-memory"
                or "reset-workspace"
                or "prune-candidate")
            || !int.TryParse(
                lines[3],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int entryCount)
            || entryCount < 0
            || lines.Length != entryCount + 5
            || !lines[^1].StartsWith("H:", StringComparison.Ordinal)
            || lines[^1].Length != 66)
        {

            throw new InvalidDataException("The retention mutation journal header is invalid.");

        }

        byte[] expectedDigest;

        try
        {

            expectedDigest = Convert.FromHexString(lines[^1][2..]);

        }
        catch (FormatException ex)
        {

            throw new InvalidDataException(
                "The retention mutation journal digest is invalid.",
                ex);

        }

        byte[] actualDigest = SHA256.HashData(
            Encoding.UTF8.GetBytes(
                string.Join('\n', lines[..^1]) + "\n"));

        if (!CryptographicOperations.FixedTimeEquals(expectedDigest, actualDigest))
        {

            throw new InvalidDataException("The retention mutation journal digest does not match.");

        }

        string target;

        try
        {

            target = Encoding.UTF8.GetString(Convert.FromBase64String(lines[2]));

        }
        catch (FormatException ex)
        {

            throw new InvalidDataException(
                "The retention mutation journal target is invalid.",
                ex);

        }

        List<RetentionMutationJournalEntry> entries = [];

        try
        {

            for (int index = 0; index < entryCount; index++)
            {

                string[] parts = lines[index + 4].Split(':');

                if (parts.Length != 7
                    || !string.Equals(parts[0], "E", StringComparison.Ordinal)
                    || parts[1] is not ("attachments" or "files" or "logs")
                    || !ulong.TryParse(parts[3], out ulong volumeId)
                    || !ulong.TryParse(parts[4], out ulong fileId)
                    || !ulong.TryParse(parts[5], out ulong hardLinkCount)
                    || !int.TryParse(parts[6], out int kindValue)
                    || kindValue != (int)FileSystemObjectKind.RegularFile)
                {

                    throw new InvalidDataException(
                        "The retention mutation journal entry is invalid.");

                }

                string relativePath = Encoding.UTF8.GetString(
                    Convert.FromBase64String(parts[2]));

                if (string.IsNullOrWhiteSpace(relativePath)
                    || Path.IsPathFullyQualified(relativePath))
                {

                    throw new InvalidDataException(
                        "The retention mutation journal path is invalid.");

                }

                entries.Add(
                    new RetentionMutationJournalEntry(
                        parts[1],
                        relativePath,
                        new FileHandleMetadata(
                            new FileHandleIdentity(volumeId, fileId),
                            hardLinkCount,
                            (FileSystemObjectKind)kindValue)));

            }

        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {

            throw new InvalidDataException(
                "The retention mutation journal entry list is invalid.",
                ex);

        }

        if (entries.DistinctBy(static entry =>
                (entry.RootRole, entry.RelativePath)).Count() != entries.Count)
        {

            throw new InvalidDataException(
                "The retention mutation journal contains duplicate paths.");

        }

        return new RetentionMutationJournal(lines[1], target, [.. entries]);

    }

    internal static bool MatchesWorkspaceResetMutation(
        LongRunningOperation operation,
        DataRetentionWorkspaceBinding binding)
    {

        if (!string.Equals(
                operation.Kind,
                LongRunningOperationKinds.DataRetentionMutation,
                StringComparison.Ordinal)
            || operation.RecoveryPolicy
                is not LongRunningOperationRecoveryPolicy.ReconcileAndComplete
            || operation.CheckpointVersion != 2
            || operation.CheckpointPayload is null
            || !string.Equals(
                operation.CheckpointReference,
                "retention-mutation:" + operation.Id.ToString("N"),
                StringComparison.Ordinal)
            || !TryGetCanonicalWorkspaceRoot(binding, out string workspaceRoot))
        {

            return false;

        }

        RetentionMutationJournal journal;

        try
        {

            journal = ParseMutationJournal(operation.CheckpointPayload);

        }
        catch (InvalidDataException)
        {

            return false;

        }

        return string.Equals(
                journal.Subtype,
                "reset-workspace",
                StringComparison.Ordinal)
            && journal.Entries.Length == 0
            && string.Equals(
                journal.Target,
                binding.CampaignId.ToString("N") + ":" + workspaceRoot,
                StringComparison.Ordinal);

    }

    private bool TryQuarantineOwnedFile(
        string root,
        string relativePath,
        Guid operationId,
        RetentionMutationJournal? mutationJournal,
        out IdentityOwnedFileSystemQuarantine quarantine,
        out long quarantinedBytes)
    {

        quarantine = default;

        quarantinedBytes = 0;

        string fullRoot = Path.GetFullPath(root);

        string candidate = Path.GetFullPath(
            Path.Combine(fullRoot, relativePath));

        if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                fullRoot,
                candidate,
                out _))
        {

            logger.LogWarning(
                "Retention refused a file path outside its selected root: {RelativePath}",
                relativePath);

            return false;

        }

        if (!File.Exists(candidate))
        {

            return false;

        }

        if (!IdentityOwnedFileSystemCleanup.TryCapturePath(
                candidate,
                FileSystemObjectKind.RegularFile,
                out IdentityOwnedFileSystemArtifact artifact))
        {

            if (!File.Exists(candidate))
            {

                return false;

            }

            throw new IOException(
                "Retention refused a file whose no-follow identity could not be captured.");

        }

        if (mutationJournal is not null)
        {

            string rootRole = ManagedRootRole(root);

            RetentionMutationJournalEntry? expected = mutationJournal.Entries
                .SingleOrDefault(entry =>
                    string.Equals(entry.RootRole, rootRole, StringComparison.Ordinal)
                    && string.Equals(
                        entry.RelativePath,
                        relativePath,
                        StringComparison.Ordinal));

            if (expected is null || expected.Metadata != artifact.Metadata)
            {

                throw new IOException(
                    "Retention refused a file that did not match its durable mutation journal.");

            }

        }

        quarantinedBytes = new FileInfo(candidate).Length;

        if (!IdentityOwnedFileSystemCleanup.TryQuarantine(
                artifact,
                OperationQuarantineDirectoryPrefix(operationId),
                out quarantine)
            || quarantine == default)
        {

            quarantinedBytes = 0;

            throw new IOException(
                "Retention refused a file whose identity changed before quarantine.");

        }

        return true;

    }

    private string ManagedRootRole(string root)
    {

        string fullRoot = Path.GetFullPath(root);

        if (string.Equals(fullRoot, _attachmentsRoot, StringComparison.Ordinal))
        {

            return "attachments";

        }

        if (string.Equals(fullRoot, _filesRoot, StringComparison.Ordinal))
        {

            return "files";

        }

        if (string.Equals(fullRoot, _logsRoot, StringComparison.Ordinal))
        {

            return "logs";

        }

        throw new InvalidDataException("The retention journal referenced an unknown managed root.");

    }

    private static string OperationQuarantineDirectoryPrefix(Guid operationId) =>
        $".arcanum-retention-{operationId:N}-";

    private static void FinalizeOperationQuarantines(
        IEnumerable<IdentityOwnedFileSystemQuarantine> quarantinedFiles)
    {

        foreach (IdentityOwnedFileSystemQuarantine quarantine in quarantinedFiles)
        {

            if (!IdentityOwnedFileSystemCleanup.TryDeleteQuarantined(quarantine))
            {

                throw new RetentionQuarantineRecoveryRequiredException(
                    "Retention committed its database mutation but could not finalize quarantined bytes.");

            }

        }

    }

    internal async Task<LongRunningOperationRecoveryResult> RecoverMutationAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken)
    {

        if (!string.Equals(
                operation.Kind,
                LongRunningOperationKinds.DataRetentionMutation,
                StringComparison.Ordinal))
        {

            return LongRunningOperationRecoveryResult.RequiresAttention(
                ErrorCodes.Data.ReconciliationFailed);

        }

        // The single-flight insert creates this row at checkpoint version 0, and
        // PrepareMutationJournalAsync lifts it to 2 before anything is captured, quarantined, or
        // deleted. A row still at 0 was interrupted inside that window, so no storage was touched
        // and there is nothing to reconcile. It has to close: ReconciliationRequired is re-selected
        // by the reconciler forever and blocks every later data-retention operation, so parking a
        // mutation that never began would wedge retention permanently.
        if (operation.CheckpointVersion == 0
            && operation.CheckpointPayload is null
            && operation.CheckpointReference is null)
        {

            return LongRunningOperationRecoveryResult.Abandoned(
                LongRunningOperationRecoveryOutcomes.RetentionMutationNeverStarted);

        }

        if (operation.CheckpointVersion != 2
            || operation.CheckpointPayload is null
            || !string.Equals(
                operation.CheckpointReference,
                "retention-mutation:" + operation.Id.ToString("N"),
                StringComparison.Ordinal))
        {

            return LongRunningOperationRecoveryResult.RequiresAttention(
                ErrorCodes.Data.ReconciliationFailed);

        }

        RetentionMutationJournal journal;

        try
        {

            journal = ParseMutationJournal(operation.CheckpointPayload);

        }
        catch (InvalidDataException)
        {

            return LongRunningOperationRecoveryResult.RequiresAttention(
                ErrorCodes.Data.ReconciliationFailed);

        }

        bool targetExists = await MutationTargetExistsAsync(
            journal,
            cancellationToken).ConfigureAwait(false);

        Dictionary<RetentionMutationJournalEntry, IdentityOwnedFileSystemQuarantine>
            quarantines;

        try
        {

            quarantines = DiscoverMutationQuarantines(
                operation.Id,
                journal,
                targetExists);

        }
        catch (InvalidDataException)
        {

            return LongRunningOperationRecoveryResult.RequiresAttention(
                ErrorCodes.Data.ReconciliationFailed);

        }

        foreach (RetentionMutationJournalEntry entry in journal.Entries)
        {

            string root = RootForRole(entry.RootRole);

            string originalPath = Path.GetFullPath(
                Path.Combine(root, entry.RelativePath));

            if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                    root,
                    originalPath,
                    out _))
            {

                return LongRunningOperationRecoveryResult.RequiresAttention(
                    ErrorCodes.Data.ReconciliationFailed);

            }

            if (quarantines.TryGetValue(entry, out IdentityOwnedFileSystemQuarantine quarantine))
            {

                bool recovered = targetExists
                    ? IdentityOwnedFileSystemCleanup.TryRestoreQuarantined(quarantine)
                    : IdentityOwnedFileSystemCleanup.TryDeleteQuarantined(quarantine);

                if (!recovered)
                {

                    return LongRunningOperationRecoveryResult.RequiresAttention(
                        ErrorCodes.Data.ReconciliationFailed);

                }

            }

            bool originalExists = IdentityOwnedFileSystemCleanup.TryCapturePath(
                originalPath,
                FileSystemObjectKind.RegularFile,
                out IdentityOwnedFileSystemArtifact original);

            if (targetExists)
            {

                if (!originalExists || original.Metadata != entry.Metadata)
                {

                    return LongRunningOperationRecoveryResult.RequiresAttention(
                        ErrorCodes.Data.ReconciliationFailed);

                }

            }
            else if (originalExists && original.Metadata == entry.Metadata)
            {

                return LongRunningOperationRecoveryResult.RequiresAttention(
                    ErrorCodes.Data.ReconciliationFailed);

            }

        }

        return targetExists
            ? LongRunningOperationRecoveryResult.Failed(
                ErrorCodes.Data.ReconciliationFailed)
            : LongRunningOperationRecoveryResult.Completed();

    }

    private Dictionary<RetentionMutationJournalEntry, IdentityOwnedFileSystemQuarantine>
        DiscoverMutationQuarantines(
            Guid operationId,
            RetentionMutationJournal journal,
            bool targetExists)
    {

        Dictionary<RetentionMutationJournalEntry, IdentityOwnedFileSystemQuarantine> found = [];

        string prefix = OperationQuarantineDirectoryPrefix(operationId);

        int maximumDirectories = Math.Max(16, journal.Entries.Length * 2 + 8);

        var parentScopes = journal.Entries
            .Select(entry =>
            {

                string root = RootForRole(entry.RootRole);

                string originalPath = Path.GetFullPath(
                    Path.Combine(root, entry.RelativePath));

                if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                        root,
                        originalPath,
                        out _))
                {

                    throw new InvalidDataException(
                        "A retention mutation journal path escaped its managed root.");

                }

                return new
                {

                    Root = root,

                    Parent = Path.GetDirectoryName(originalPath)
                        ?? throw new InvalidDataException(
                            "A retention mutation journal path has no parent."),

                };

            })
            .DistinctBy(static scope => (scope.Root, scope.Parent))
            .ToArray();

        foreach (var scope in parentScopes)
        {

            if (!Directory.Exists(scope.Parent))
            {

                continue;

            }

            string[] directories =
            [
                .. Directory.EnumerateDirectories(
                        scope.Parent,
                        prefix + "*",
                        SearchOption.TopDirectoryOnly)
                    .Take(maximumDirectories + 1),
            ];

            if (directories.Length > maximumDirectories)
            {

                throw new InvalidDataException(
                    "The retention mutation has an unbounded quarantine set.");

            }

            foreach (string directory in directories)
            {

                string name = Path.GetFileName(directory);

                string suffix = name[prefix.Length..];

                if (suffix.Length != 32 || !suffix.All(Uri.IsHexDigit))
                {

                    throw new InvalidDataException(
                        "A retention mutation quarantine name is malformed.");

                }

                if (!SecureFilePermissions.TryEnsureOwnerOnlyDirectoryExistsStrict(
                        directory))
                {

                    throw new InvalidDataException(
                        "A retention mutation quarantine is not strictly owner-only.");

                }

                if (!IdentityOwnedFileSystemCleanup.TryCapturePath(
                        directory,
                        FileSystemObjectKind.Directory,
                        out IdentityOwnedFileSystemArtifact directoryArtifact))
                {

                    throw new InvalidDataException(
                        "A retention mutation quarantine directory changed identity.");

                }

                string[] entries = Directory.GetFileSystemEntries(directory);

                if (entries.Length == 0)
                {

                    bool originalStillOwned = targetExists
                        && journal.Entries.Any(entry =>
                        {

                            string entryRoot = RootForRole(entry.RootRole);

                            string original = Path.GetFullPath(
                                Path.Combine(entryRoot, entry.RelativePath));

                            return string.Equals(
                                    Path.GetDirectoryName(original),
                                    scope.Parent,
                                    StringComparison.Ordinal)
                                && IdentityOwnedFileSystemCleanup.TryCapturePath(
                                    original,
                                    FileSystemObjectKind.RegularFile,
                                    out IdentityOwnedFileSystemArtifact artifact)
                                && artifact.Metadata == entry.Metadata;

                        });

                    if (!originalStillOwned
                        || !IdentityOwnedFileSystemCleanup.TryDelete(directoryArtifact))
                    {

                        throw new InvalidDataException(
                            "An empty retention mutation quarantine could not be reconciled safely.");

                    }

                    continue;

                }

                if (entries.Length != 1
                    || !IdentityOwnedFileSystemCleanup.TryCapturePath(
                        entries[0],
                        FileSystemObjectKind.RegularFile,
                        out IdentityOwnedFileSystemArtifact quarantinedArtifact))
                {

                    throw new InvalidDataException(
                        "A retention mutation quarantine has an unexpected shape.");

                }

                string originalPath = Path.Combine(
                    Path.GetDirectoryName(directory)!,
                    Path.GetFileName(entries[0]));

                string rootRole = ManagedRootRole(scope.Root);

                string relativePath = Path.GetRelativePath(scope.Root, originalPath);

                RetentionMutationJournalEntry? manifestEntry = journal.Entries
                    .SingleOrDefault(entry =>
                        string.Equals(entry.RootRole, rootRole, StringComparison.Ordinal)
                        && string.Equals(
                            entry.RelativePath,
                            relativePath,
                            StringComparison.Ordinal)
                        && entry.Metadata == quarantinedArtifact.Metadata);

                if (manifestEntry is null || found.ContainsKey(manifestEntry))
                {

                    throw new InvalidDataException(
                        "A retention mutation quarantine is not named by its durable journal.");

                }

                found.Add(
                    manifestEntry,
                    new IdentityOwnedFileSystemQuarantine(
                        new IdentityOwnedFileSystemArtifact(
                            originalPath,
                            manifestEntry.Metadata),
                        quarantinedArtifact,
                        directoryArtifact));

            }

        }

        return found;

    }

    private async Task<bool> MutationTargetExistsAsync(
        RetentionMutationJournal journal,
        CancellationToken cancellationToken)
    {

        if (journal.Subtype == "delete-session"
            && Guid.TryParse(journal.Target, out Guid sessionId))
        {

            return await CountTableAsync(
                "Sessions",
                "lower(replace(Id, '-', '')) = @id",
                cancellationToken,
                ("@id", sessionId.ToString("N"))).ConfigureAwait(false) > 0;

        }

        if (journal.Subtype == "delete-attachment"
            && Guid.TryParse(journal.Target, out Guid attachmentId))
        {

            return await CountTableAsync(
                "SessionAttachments",
                "lower(replace(Id, '-', '')) = @id",
                cancellationToken,
                ("@id", attachmentId.ToString("N"))).ConfigureAwait(false) > 0;

        }

        if (journal.Subtype == "reset-memory"
            && int.TryParse(
                journal.Target,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int scopeValue)
            && Enum.IsDefined((MemoryResetScope)scopeValue))
        {

            string[] tables = (MemoryResetScope)scopeValue switch
            {

                MemoryResetScope.Entry =>
                    ["entry_embeddings_vec", "entry_embeddings"],

                MemoryResetScope.Attachments =>
                    [
                        "session_attachment_embeddings_vec",
                        "session_attachment_embeddings",
                        "session_attachment_chunks",
                        "session_attachment_index_state",
                    ],

                MemoryResetScope.Workspace =>
                    [
                        "workspace_file_embeddings_vec",
                        "workspace_file_embeddings",
                        "workspace_file_chunks",
                    ],

                MemoryResetScope.Saga =>
                    [
                        "saga_memory_embeddings_vec",
                        "saga_memory_embeddings",
                        "saga_memory_attachment_provenance",
                        "saga_extraction_watermarks",
                        "saga_memories",
                    ],

                MemoryResetScope.Lexicon =>
                    [
                        "lexicon_fact_attachment_provenance",
                        "lexicon_fts",
                        "lexicon_entries",
                    ],

                _ => [],

            };

            foreach (string table in tables)
            {

                if (await CountTableAsync(
                        table,
                        null,
                        cancellationToken).ConfigureAwait(false) > 0)
                {

                    return true;

                }

            }

            return false;

        }

        if (journal.Subtype == "reset-workspace"
            && journal.Target.Length > 33
            && journal.Target[32] == ':'
            && Guid.TryParseExact(
                journal.Target[..32],
                "N",
                out _))
        {

            string workspaceRoot = journal.Target[33..];

            DbConnection connection = await OpenConnectionAsync(
                cancellationToken).ConfigureAwait(false);

            WorkspaceResetSnapshot snapshot = await ReadWorkspaceResetSnapshotAsync(
                connection,
                transaction: null,
                workspaceRoot,
                cancellationToken).ConfigureAwait(false);

            return snapshot.TotalOwnedRows > 0;

        }

        throw new InvalidDataException("The retention mutation journal target is invalid.");

    }

    private string RootForRole(string rootRole) => rootRole switch
    {

        "attachments" => _attachmentsRoot,

        "files" => _filesRoot,

        "logs" => _logsRoot,

        _ => throw new InvalidDataException(
            "The retention mutation journal root role is invalid."),

    };

    private void TryDeleteEmptySessionDirectory(Guid sessionId)
    {

        string directory = Path.GetFullPath(
            Path.Combine(_attachmentsRoot, sessionId.ToString("N")));

        if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                _attachmentsRoot,
                directory,
                out _)
            || !Directory.Exists(directory))
        {

            return;

        }

        if (!Directory.EnumerateFileSystemEntries(directory).Any())
        {

            Directory.Delete(directory);

        }

    }

    private static bool IsUnderRoot(string root, string candidate)
    {

        string normalizedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(root));

        string normalizedCandidate = Path.GetFullPath(candidate);

        return normalizedCandidate.StartsWith(
            normalizedRoot + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    }

    private static long CountExistingFiles(string root) =>
        MeasureOwnedTree(root).Files;

    private static (long Files, long Bytes) MeasureOwnedTree(string root)
    {

        if (!Directory.Exists(root))
        {

            return (0, 0);

        }

        FileInfo[] files =
            [.. Directory.EnumerateFiles(
                    root,
                    "*",
                    OwnedTreeEnumeration)
                .Select(static path => new FileInfo(path))];

        return (
            files.LongLength,
            files.Sum(static file => file.Length));

    }

    private static (long Files, long Bytes) CountFiles(
        string root,
        string pattern)
    {

        if (!Directory.Exists(root))
        {

            return (0, 0);

        }

        FileInfo[] files =
            [.. Directory.EnumerateFiles(root, pattern)
                .Select(static path => new FileInfo(path))];

        return (
            files.LongLength,
            files.Sum(static file => file.Length));

    }

    private static EnumerationOptions OwnedTreeEnumeration { get; } =
        new()
        {

            RecurseSubdirectories = true,

            AttributesToSkip = FileAttributes.ReparsePoint,

            IgnoreInaccessible = true,

            ReturnSpecialDirectories = false,

        };

    private static DataRetentionApplyResult EmptyApply(
        Guid operationId,
        DataRetentionPlan plan) =>
        new(
            operationId,
            plan.PlanId,
            0,
            0,
            0,
            0,
            Reconciled: true,
            plan.Blockers,
            plan.Conflicts);

    private sealed record SessionPlanSnapshot(
        string Status,
        Guid[] EntryIds,
        Guid[] PinnedEntryIds,
        AttachmentPlanSnapshot[] Attachments,
        long EntryEmbeddingCount,
        long EntryVectorEmbeddingCount,
        long EntryFtsCount,
        long AttachmentMemoryConsultationCount,
        long SagaExtractionWatermarkCount,
        long AttachmentChunkCount,
        long AttachmentEmbeddingCount,
        long AttachmentVectorEmbeddingCount,
        long AttachmentIndexStateCount);

    private readonly record struct IdSetBatch(
        string Predicate,
        (string Name, object Value)[] Parameters);

    private sealed record AttachmentPlanSnapshot(
        Guid Id,
        Guid? SessionId,
        string RelativePath,
        long ByteLength,
        string State,
        bool FileExists,
        string[] ChunkIds,
        long ChunkCount,
        long EmbeddingCount,
        long VectorEmbeddingCount,
        long IndexStateCount);

    private sealed record UploadedFileSnapshot(
        Guid Id,
        long Bytes);

    private sealed record RetentionMutationJournal(
        string Subtype,
        string Target,
        RetentionMutationJournalEntry[] Entries);

    private sealed record RetentionMutationJournalEntry(
        string RootRole,
        string RelativePath,
        FileHandleMetadata Metadata);

    private sealed record BatchReferenceSnapshot(
        Guid BatchId,
        string Status);

    private sealed class RetentionConflictException(string message) : Exception(message);

    private sealed class RetentionBlockedException(string message) : Exception(message);

    private sealed class RetentionQuarantineRecoveryRequiredException(
        string message,
        Exception? innerException = null)
        : Exception(message, innerException);

}
