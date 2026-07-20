using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Shared recovery for stranded <see cref="BatchStatuses.InProgress"/> batches — host startup
/// reconcile and <c>POST /v1/batches/{id}/reset</c>. Transitions use compare-and-set so concurrent
/// workers cannot clobber a status that already moved on.
/// </summary>
internal interface IBatchRecoveryService
{

    /// <summary>
    /// At host start: for every DB-stranded <see cref="BatchStatuses.InProgress"/> batch, either
    /// re-queue to <see cref="BatchStatuses.Validating"/> (input still present) or mark
    /// <see cref="BatchStatuses.Failed"/> (input missing). Completes before the dispatch loop runs.
    /// </summary>
    Task ReconcileStrandedAsync(CancellationToken cancellationToken = default);

    /// <summary>Operator reset path for a single stuck batch (same input-exists / CAS rules as startup).</summary>
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
    ILogger<BatchRecoveryService> logger) : IBatchRecoveryService
{

    public async Task ReconcileStrandedAsync(CancellationToken cancellationToken = default)
    {

        using IServiceScope scope = scopeFactory.CreateScope();

        IBatchRepository batches = scope.ServiceProvider.GetRequiredService<IBatchRepository>();

        IUploadedFileRepository files = scope.ServiceProvider.GetRequiredService<IUploadedFileRepository>();

        IReadOnlyList<BatchRecord> stranded = await batches
            .ListByStatusAsync(BatchStatuses.InProgress, cancellationToken)
            .ConfigureAwait(false);

        foreach (BatchRecord batch in stranded)
        {

            cancellationToken.ThrowIfCancellationRequested();

            if (await InputExistsAsync(batch, files, cancellationToken).ConfigureAwait(false))
            {

                await ClearOutputAndErrorArtifactsAsync(batch, files, cancellationToken).ConfigureAwait(false);

                bool reset = await batches.TryCompareAndSetStatusAsync(
                    batch.Id,
                    BatchStatuses.InProgress,
                    BatchStatuses.Validating,
                    completedAt: null,
                    outputFileId: null,
                    errorFileId: null,
                    cancellationToken).ConfigureAwait(false);

                if (reset)
                {

                    logger.LogInformation(
                        "Reconciled stranded batch {BatchId}: in_progress → validating (input present).",
                        batch.Id);

                }

            }
            else
            {

                string reason = await DescribeMissingInputAsync(batch, files, cancellationToken).ConfigureAwait(false);

                bool failed = await batches.TryCompareAndSetStatusAsync(
                    batch.Id,
                    BatchStatuses.InProgress,
                    BatchStatuses.Failed,
                    DateTimeOffset.UtcNow,
                    outputFileId: null,
                    errorFileId: null,
                    cancellationToken).ConfigureAwait(false);

                if (failed)
                {

                    logger.LogWarning(
                        "Reconciled stranded batch {BatchId}: in_progress → failed ({Reason}).",
                        batch.Id,
                        reason);

                }

            }

        }

    }

    public async Task<BatchRecoveryResult> ResetStuckBatchAsync(Guid batchId, CancellationToken cancellationToken = default)
    {

        using IServiceScope scope = scopeFactory.CreateScope();

        IBatchRepository batches = scope.ServiceProvider.GetRequiredService<IBatchRepository>();

        IUploadedFileRepository files = scope.ServiceProvider.GetRequiredService<IUploadedFileRepository>();

        BatchRecord? record = await batches.GetByIdAsync(batchId, cancellationToken).ConfigureAwait(false);

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

        if (!await InputExistsAsync(record, files, cancellationToken).ConfigureAwait(false))
        {

            return new BatchRecoveryResult(BatchRecoveryStatus.InputMissing, record);

        }

        await ClearOutputAndErrorArtifactsAsync(record, files, cancellationToken).ConfigureAwait(false);

        bool cas = await batches.TryCompareAndSetStatusAsync(
            batchId,
            BatchStatuses.InProgress,
            BatchStatuses.Validating,
            completedAt: null,
            outputFileId: null,
            errorFileId: null,
            cancellationToken).ConfigureAwait(false);

        if (!cas)
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

    private static async Task<bool> InputExistsAsync(
        BatchRecord batch,
        IUploadedFileRepository files,
        CancellationToken cancellationToken)
    {

        UploadedFileRecord? inputFile = await files.GetByIdAsync(batch.InputFileId, cancellationToken).ConfigureAwait(false);

        if (inputFile is null)
        {

            return false;

        }

        return File.Exists(UploadedFileStorage.ResolvePath(batch.InputFileId));

    }

    private static async Task<string> DescribeMissingInputAsync(
        BatchRecord batch,
        IUploadedFileRepository files,
        CancellationToken cancellationToken)
    {

        UploadedFileRecord? inputFile = await files.GetByIdAsync(batch.InputFileId, cancellationToken).ConfigureAwait(false);

        if (inputFile is null)
        {

            return "UploadedFiles metadata missing for input file";

        }

        if (!File.Exists(UploadedFileStorage.ResolvePath(batch.InputFileId)))
        {

            return "input file missing on disk";

        }

        return "input file unavailable";

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
