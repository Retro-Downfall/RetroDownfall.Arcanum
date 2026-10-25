using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Shared recovery for stranded <see cref="BatchStatuses.InProgress"/> batches — host startup
/// reconcile and <c>POST /v1/batches/{id}/reset</c>. A private durable recovery claim excludes
/// workers and public status mutation while checkpoint, artifact, and accounting cleanup runs.
/// </summary>
internal interface IBatchRecoveryService
{
    /// <summary>
    /// At host start: for every DB-stranded <see cref="BatchStatuses.InProgress"/> batch, either
    /// re-queue to <see cref="BatchStatuses.Validating"/> (input still present) or mark
    /// <see cref="BatchStatuses.Failed"/> (input missing). Completes before the dispatch loop runs.
    /// </summary>
    Task ReconcileStrandedAsync(CancellationToken cancellationToken = default);

    /// <summary>Operator reset path for a single stuck batch (same input and recovery-claim rules as startup).</summary>
    Task<BatchRecoveryResult> ResetStuckBatchAsync(Guid batchId, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of <see cref="IBatchRecoveryService.ResetStuckBatchAsync"/>.</summary>
internal enum BatchRecoveryStatus
{
    Succeeded,

    NotFound,

    NotStuck,

    InFlight,

    InputMissing,

    InputUnreadable,

    ConcurrentModification,
}

/// <summary>
/// Result of an operator reset. <see cref="Record"/> is set only when
/// <see cref="Status"/> is <see cref="BatchRecoveryStatus.Succeeded"/>.
/// </summary>
internal sealed record BatchRecoveryResult(BatchRecoveryStatus Status, BatchRecord? Record = null);

internal sealed class BatchRecoveryService(
    IServiceScopeFactory scopeFactory,
    BatchProcessingService batchProcessing,
    IEncryptedBlobStore blobStore,
    ILogger<BatchRecoveryService> logger) : IBatchRecoveryService
{
    private const int CheckpointPageSize = 64;

    private enum BatchInputAvailability
    {
        Available,

        Missing,

        Unreadable,
    }

    private readonly ConcurrentDictionary<Guid, byte> _operatorRecoveries = new();

    public async Task ReconcileStrandedAsync(CancellationToken cancellationToken = default)
    {
        using IServiceScope scope = scopeFactory.CreateScope();

        IBatchRepository batches = scope.ServiceProvider.GetRequiredService<IBatchRepository>();

        IUploadedFileRepository files = scope.ServiceProvider.GetRequiredService<IUploadedFileRepository>();

        IBatchAccountingRecoveryStore accountingRecovery =
            scope.ServiceProvider.GetRequiredService<IBatchAccountingRecoveryStore>();

        IReadOnlyList<BatchRecord> stranded = await batches
            .ListByStatusAsync(BatchStatuses.InProgress, cancellationToken)
            .ConfigureAwait(false);

        foreach (BatchRecord batch in stranded)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // InProgress is the live state of a batch this process is dispatching, not evidence that
            // it was stranded; sealing its lines would cut off the running worker.
            if (batchProcessing.IsBatchInFlight(batch.Id))
            {
                continue;
            }

            await RecoverStrandedBatchAsync(
                batch,
                batches,
                files,
                accountingRecovery,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<BatchRecoveryResult> ResetStuckBatchAsync(Guid batchId, CancellationToken cancellationToken = default)
    {
        using IServiceScope scope = scopeFactory.CreateScope();

        IBatchRepository batches = scope.ServiceProvider.GetRequiredService<IBatchRepository>();

        IUploadedFileRepository files = scope.ServiceProvider.GetRequiredService<IUploadedFileRepository>();

        IBatchAccountingRecoveryStore accountingRecovery =
            scope.ServiceProvider.GetRequiredService<IBatchAccountingRecoveryStore>();

        BatchRecord? record = await batches.GetByIdAsync(batchId, cancellationToken).ConfigureAwait(false);

        if (record is null)
        {
            return new BatchRecoveryResult(BatchRecoveryStatus.NotFound);
        }

        if (!BatchStatuses.IsStuck(record.Status))
        {
            return new BatchRecoveryResult(BatchRecoveryStatus.NotStuck, record);
        }

        if (batchProcessing.IsBatchInFlight(batchId)
            || !_operatorRecoveries.TryAdd(batchId, 0))
        {
            return new BatchRecoveryResult(BatchRecoveryStatus.InFlight, record);
        }

        try
        {
            record = await batches.GetByIdAsync(batchId, cancellationToken).ConfigureAwait(false);

            if (record is null)
            {
                return new BatchRecoveryResult(BatchRecoveryStatus.NotFound);
            }

            if (!BatchStatuses.IsStuck(record.Status))
            {
                return new BatchRecoveryResult(BatchRecoveryStatus.NotStuck, record);
            }

            if (batchProcessing.IsBatchInFlight(batchId))
            {
                return new BatchRecoveryResult(BatchRecoveryStatus.InFlight, record);
            }

            BatchInputAvailability input = await InspectInputAsync(record, files, cancellationToken).ConfigureAwait(false);

            if (input != BatchInputAvailability.Available)
            {
                return new BatchRecoveryResult(input == BatchInputAvailability.Missing
                    ? BatchRecoveryStatus.InputMissing : BatchRecoveryStatus.InputUnreadable, record);
            }

            BatchAccountingRecoveryClaimStatus claim = await accountingRecovery
                .ClaimRecoveryAsync(batchId, cancellationToken)
                .ConfigureAwait(false);

            if (claim == BatchAccountingRecoveryClaimStatus.NotRecoverable)
            {
                return new BatchRecoveryResult(BatchRecoveryStatus.ConcurrentModification, record);
            }

            await SealInterruptedLinesAsync(record.Id, batches, cancellationToken).ConfigureAwait(false);

            await ClearOutputAndErrorArtifactsAsync(record, files, cancellationToken).ConfigureAwait(false);

            bool completed = await accountingRecovery.TryCompleteRecoveryAsync(
                batchId,
                BatchAccountingRecoveryTarget.Requeue,
                cancellationToken).ConfigureAwait(false);

            if (!completed)
            {
                return new BatchRecoveryResult(BatchRecoveryStatus.ConcurrentModification, record);
            }

            BatchRecord? updated = await batches.GetByIdAsync(batchId, cancellationToken).ConfigureAwait(false);

            return new BatchRecoveryResult(BatchRecoveryStatus.Succeeded, updated ?? record with
            {
                Status = BatchStatuses.Validating,
                CompletedAt = null,
                OutputFileId = null,
                ErrorFileId = null,
            });
        }
        finally
        {
            _ = _operatorRecoveries.TryRemove(batchId, out _);
        }
    }

    private async Task RecoverStrandedBatchAsync(
        BatchRecord batch,
        IBatchRepository batches,
        IUploadedFileRepository files,
        IBatchAccountingRecoveryStore accountingRecovery,
        CancellationToken cancellationToken)
    {
        BatchInputAvailability input = await InspectInputAsync(batch, files, cancellationToken).ConfigureAwait(false);

        bool inputExists = input == BatchInputAvailability.Available;

        BatchAccountingRecoveryClaimStatus claim = await accountingRecovery
            .ClaimRecoveryAsync(batch.Id, cancellationToken)
            .ConfigureAwait(false);

        if (claim == BatchAccountingRecoveryClaimStatus.NotRecoverable)
        {
            return;
        }

        await SealInterruptedLinesAsync(batch.Id, batches, cancellationToken).ConfigureAwait(false);

        await ClearOutputAndErrorArtifactsAsync(batch, files, cancellationToken).ConfigureAwait(false);

        BatchAccountingRecoveryTarget target = inputExists
            ? BatchAccountingRecoveryTarget.Requeue
            : BatchAccountingRecoveryTarget.Fail;

        if (!await accountingRecovery.TryCompleteRecoveryAsync(batch.Id, target, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Batch '{batch.Id:D}' lost its durable accounting recovery claim before publication.");
        }

        if (inputExists)
        {
            logger.LogInformation(
                "Reconciled stranded batch {BatchId}: in_progress → validating (input present).",
                batch.Id);
        }
        else
        {
            logger.LogWarning(
                "Reconciled stranded batch {BatchId}: in_progress → failed ({Reason}).",
                batch.Id,
                input == BatchInputAvailability.Missing ? "input file missing" : "input file unreadable or undecryptable");
        }
    }

    private static async Task SealInterruptedLinesAsync(
        Guid batchId,

        IBatchRepository batches,

        CancellationToken cancellationToken)

    {
        long afterLine = 0;

        while (true)

        {
            List<BatchLineCheckpoint> page =
            [
                .. await batches.ListLineCheckpointsAsync(
                    batchId,

                    BatchLineCheckpointState.Dispatched,

                    afterLine,

                    CheckpointPageSize,

                    cancellationToken).ConfigureAwait(false),
            ];

            if (page.Count == 0)

            {
                return;
            }

            foreach (BatchLineCheckpoint checkpoint in page)

            {
                cancellationToken.ThrowIfCancellationRequested();

                await BatchProcessingService.CompleteInterruptedLineAsync(
                    checkpoint,

                    batches,

                    cancellationToken).ConfigureAwait(false);
            }

            afterLine = page[^1].LineNumber;
        }
    }

    private async Task<BatchInputAvailability> InspectInputAsync(
        BatchRecord batch,
        IUploadedFileRepository files,
        CancellationToken cancellationToken)
    {
        UploadedFileRecord? inputFile = await files.GetByIdAsync(batch.InputFileId, cancellationToken).ConfigureAwait(false);

        if (inputFile is null)
        {
            return BatchInputAvailability.Missing;
        }

        string path = UploadedFileStorage.ResolvePath(batch.InputFileId);

        try
        {
            // File.Exists also returns false for access failures. Only a genuine not-found
            // exception establishes absence; present but inaccessible data needs a different remedy.
            _ = File.GetAttributes(path);

            await using Stream input = await blobStore.OpenCompatibleReadAsync(
                    path,
                    EncryptedBlobPurpose.UploadedFile,
                    inputFile.EncryptionVersion,
                    cancellationToken)
                .ConfigureAwait(false);

            await input.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);

            return BatchInputAvailability.Available;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
            && ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return BatchInputAvailability.Missing;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
            && ex is System.Security.Cryptography.CryptographicException
                or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // Only the input read is contained. Recovery-claim, accounting and database failures
            // still propagate rather than allowing startup with unverifiable durable authority.
            logger.LogWarning(ex, "Batch {BatchId} input file {InputFileId} cannot be read or decrypted.",
                batch.Id, batch.InputFileId);

            return BatchInputAvailability.Unreadable;
        }
    }

    private static async Task ClearOutputAndErrorArtifactsAsync(
        BatchRecord batch,
        IUploadedFileRepository files,
        CancellationToken cancellationToken)
    {
        if (batch.OutputFileId is { } outputFileId)
        {
            BatchProcessingService.TryDeleteFile(UploadedFileStorage.ResolvePath(outputFileId));

            await files.DeleteAsync(outputFileId, cancellationToken).ConfigureAwait(false);
        }

        if (batch.ErrorFileId is { } errorFileId)
        {
            BatchProcessingService.TryDeleteFile(UploadedFileStorage.ResolvePath(errorFileId));

            await files.DeleteAsync(errorFileId, cancellationToken).ConfigureAwait(false);
        }
    }
}
