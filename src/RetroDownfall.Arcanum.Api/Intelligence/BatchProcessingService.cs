using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Background processor for <c>/v1/batches</c> (<c>docs/Arcanum.DESIGN.md</c> §11.21) — modeled on
/// <c>EntryWeavingService</c>'s poll-and-process shape, but lives in the Api project (not
/// Infrastructure) because it needs the <c>/v1</c> OpenAI DTOs and
/// <see cref="OpenAiV1Endpoints.ExecuteChatRequestForBatchAsync"/>, which Infrastructure must not
/// depend on (Api → Infrastructure is the only allowed direction).
/// </summary>
[ExcludeFromCodeCoverage] // Reason: IHostedService background poller; behavior covered via BatchProcessingServiceTests using a directly-constructed instance + fakes.
internal sealed class BatchProcessingService(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<ArcanumSettings> optionsMonitor,
    IServiceProvider services,
    IGrimoireConnectionAdmissionGate admissionGate,
    ILogger<BatchProcessingService> logger) : BackgroundService
{
    private readonly IGrimoireConnectionAdmissionGate _admissionGate = admissionGate;

    private const int RequestPageSize = 64;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan CancelWatchInterval = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<Guid, Task> _inFlight = new();

    private readonly object _dispatchSync = new();

    private bool _stopping;

    internal Func<ValueTask>? AfterBatchRegisteredTestSeam { get; set; }

    private sealed record PreparedBatchRequestLine(
        long Line,
        BatchJsonlRequestLine? Request,
        string? ParseError);

    // Best-effort early-rejection guard, not a correctness guarantee — there is a race window between
    // this check and the status reset. The real double-processing guard is the worker's
    // _inFlight registration of its stable completion task before processing, which prevents
    // a second worker from picking the same batch up. This endpoint check just gives the operator a
    // clear 409 instead of a confusing reset-while-running.
    public bool IsBatchInFlight(Guid batchId) => _inFlight.ContainsKey(batchId);

    /// <summary>
    /// Reconcile DB-stranded <see cref="BatchStatuses.InProgress"/> rows before Kestrel accepts and
    /// before <see cref="ExecuteAsync"/>'s poll loop starts picking up <see cref="BatchStatuses.Validating"/> work.
    /// </summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Resolved here (not via ctor) to avoid a singleton cycle with BatchRecoveryService.
        IBatchRecoveryService recovery = services.GetRequiredService<IBatchRecoveryService>();

        await recovery.ReconcileStrandedAsync(cancellationToken).ConfigureAwait(false);

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        using PeriodicTimer timer = new(PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    break;
                }

                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Batch processing tick failed; continuing.");
            }
        }
    }

    internal async Task TickAsync(CancellationToken stoppingToken)
    {
        lock (_dispatchSync)
        {
            if (_stopping)
            {
                return;
            }
        }

        if (!_admissionGate.TryAcquireWorkLease(GrimoireWorkKind.BatchProcessing, out IGrimoireWorkLease? admitted))
        {
            return;
        }

        await using IGrimoireWorkLease lease = admitted!;

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        IBatchRepository batches = scope.ServiceProvider.GetRequiredService<IBatchRepository>();

        BatchesSettings settings = optionsMonitor.CurrentValue.ResolveBatches();

        int maxConcurrentBatches = ArcanumSettingClamps.BatchesMaxConcurrentBatches(settings.MaxConcurrentBatches);

        int availableSlots = maxConcurrentBatches - _inFlight.Count;

        if (availableSlots <= 0)
        {
            return;
        }

        IReadOnlyList<BatchRecord> pending = await batches.ListPendingPageAsync(
                availableSlots,
                stoppingToken)
            .ConfigureAwait(false);

        foreach (BatchRecord batch in pending)
        {
            TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_dispatchSync)
            {
                if (_stopping || _inFlight.Count >= maxConcurrentBatches)
                {
                    break;
                }

                if (!_inFlight.TryAdd(batch.Id, completion.Task))
                {
                    continue;
                }
            }

            try
            {
                if (AfterBatchRegisteredTestSeam is { } afterRegistered)
                {
                    await afterRegistered().ConfigureAwait(false);
                }
                // The exact completion task is visible before launch, including to StopAsync.
                _ = Task.Run(
                    async () =>
                    {
                        try
                        {
                            await ProcessBatchWithCleanupAsync(batch, stoppingToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            completion.TrySetResult();
                        }
                    },
                    CancellationToken.None);
            }
            catch
            {
                _ = _inFlight.TryRemove(batch.Id, out _);

                completion.TrySetResult();

                throw;
            }
        }
    }

    /// <summary>
    /// Drains in-flight batch workers before the host disposes the container underneath them. A
    /// worker that has already received (and been billed for) a provider response is inside
    /// <c>CompleteLineAsync</c> with <see cref="CancellationToken.None"/> precisely so the result is
    /// persisted; losing the scope mid-write would strand that line as
    /// <c>batch_interrupted_after_dispatch</c> and discard a paid-for response. Batches that do not
    /// drain within the shutdown budget still fall back to startup reconciliation.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_dispatchSync)
        {
            _stopping = true;
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        int drainSeconds = ArcanumSettingClamps.DaemonShutdownDrainTimeoutSeconds(
            ArcanumRuntimeDefaults.DaemonShutdownDrainTimeoutSeconds);

        if (drainSeconds <= 0)
        {
            return;
        }

        Task[] snapshot = [.. _inFlight.Values];

        if (snapshot.Length == 0)
        {
            return;
        }

        using CancellationTokenSource drainCts = new(TimeSpan.FromSeconds(drainSeconds));

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            drainCts.Token);

        try
        {
            await Task.WhenAll(snapshot).WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation(
                "Batch shutdown drain elapsed before {Count} batch(es) completed; they remain durable for startup reconciliation.",
                snapshot.Length);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Batch shutdown drain observed an unhandled exception.");
        }
    }

    private async Task ProcessBatchWithCleanupAsync(BatchRecord batch, CancellationToken stoppingToken)
    {
        try
        {
            await ProcessBatchAsync(batch, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation(
                "Batch {BatchId} stopped with the host and remains durable for startup reconciliation.",
                batch.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Batch {BatchId} processing failed unexpectedly and remains durable for startup reconciliation.",
                batch.Id);
        }
        finally
        {
            _ = _inFlight.TryRemove(batch.Id, out _);
        }
    }

    private enum BatchProcessingDisposition
    {
        Concluded = 1,
        DeferredForMaintenance = 2,
    }

    /// <summary>Owned only by the exact retained batch task, never a DI scope or database row.</summary>
    private sealed class BatchProcessingState
    {
        internal bool Claimed { get; set; }

        internal bool ReadyToPublish { get; set; }

        internal bool HasRecords { get; set; }

        internal bool CancelledMidway { get; set; }

        internal bool BudgetRejected { get; set; }

        /// <summary>An unexpected failure stopped the batch; it publishes what was checkpointed as failed.</summary>
        internal bool UnexpectedFailure { get; set; }

        /// <summary>
        /// Settling a page's accounting (run row, reservation) failed. The batch then stays
        /// <c>in_progress</c> for durable recovery, which reconciles the reservation; publishing it
        /// as failed would strand that reservation until it expires.
        /// </summary>
        internal bool AccountingSettlementFailed { get; set; }
    }

    internal async Task ProcessBatchAsync(BatchRecord batch, CancellationToken stoppingToken)
    {
        BatchProcessingState state = new();

        while (true)
        {
            stoppingToken.ThrowIfCancellationRequested();

            long observedGeneration = _admissionGate.CurrentGeneration;

            BatchProcessingDisposition disposition = await ProcessBatchAttemptAsync(
                batch, state, stoppingToken).ConfigureAwait(false);

            if (disposition == BatchProcessingDisposition.Concluded)
            {
                return;
            }
            // Closing(G) and Closed(G) both reopen as G. A predecessor observation also
            // handles a reopen that races scope disposal without missing its notification.
            await _admissionGate.WaitForNextOpenGenerationAsync(
                Math.Max(0, observedGeneration - 1), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task<BatchProcessingDisposition> ProcessBatchAttemptAsync(
        BatchRecord batch, BatchProcessingState state, CancellationToken stoppingToken)
    {
        if (!_admissionGate.TryAcquireWorkLease(GrimoireWorkKind.BatchProcessing, out IGrimoireWorkLease? admitted))
        {
            return BatchProcessingDisposition.DeferredForMaintenance;
        }

        await using IGrimoireWorkLease lease = admitted!;

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        IBatchRepository batches = scope.ServiceProvider.GetRequiredService<IBatchRepository>();

        if (state.Claimed)
        {
            BatchRecord? current = await batches.GetByIdAsync(batch.Id, stoppingToken).ConfigureAwait(false);

            if (current is null
                || current.InputFileId != batch.InputFileId
                || !string.Equals(current.Endpoint, batch.Endpoint, StringComparison.Ordinal)
                || current.Status is not (BatchStatuses.InProgress or BatchStatuses.Cancelled))
            {
                return BatchProcessingDisposition.Concluded;
            }
        }
        IUploadedFileRepository files = scope.ServiceProvider.GetRequiredService<IUploadedFileRepository>();

        IEncryptedBlobStore blobStore = scope.ServiceProvider.GetRequiredService<IEncryptedBlobStore>();

        if (!state.Claimed)
        {
            if (!await batches.TryCompareAndSetStatusAsync(
                    batch.Id, BatchStatuses.Validating, BatchStatuses.InProgress, null,
                    batch.OutputFileId, batch.ErrorFileId, stoppingToken).ConfigureAwait(false))
            {
                return BatchProcessingDisposition.Concluded;
            }
            state.Claimed = true;
        }
        ArcanumSettings settings = optionsMonitor.CurrentValue;

        if (!state.ReadyToPublish)
        {
            string inputPath = UploadedFileStorage.ResolvePath(batch.InputFileId);

            UploadedFileRecord? inputFile = await files.GetByIdAsync(batch.InputFileId, stoppingToken)
                .ConfigureAwait(false);

            if (inputFile is null || !File.Exists(inputPath))
            {
                await batches.TryCompareAndSetStatusAsync(
                    batch.Id, BatchStatuses.InProgress, BatchStatuses.Failed, DateTimeOffset.UtcNow,
                    batch.OutputFileId, batch.ErrorFileId, CancellationToken.None).ConfigureAwait(false);

                return BatchProcessingDisposition.Concluded;
            }

            await using Stream input = await blobStore.OpenCompatibleReadAsync(
                inputPath, EncryptedBlobPurpose.UploadedFile, inputFile.EncryptionVersion, stoppingToken)
                .ConfigureAwait(false);

            using BatchJsonlRecordReader.Cursor reader = new(input);

            // Rebuilt on every pass over the input, so a resumed batch sees the same first-wins
            // duplicates it saw before.
            BatchCustomIdTracker customIds = new();

            // The physical line number of the last line of the last page this pass finished, so a
            // failure that leaves no prepared line behind can still say where to resume.
            long lastFinishedLine = 0;

            while (true)
            {
                // The lookahead reads the input outside any page, so it is guarded like one: a stream
                // that fails here would otherwise leave the batch in_progress with no note at all.
                bool hasNextRecord;

                try
                {
                    hasNextRecord = await reader.HasNextRecordAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (StopsAsUnexpectedFailure(exception, state, stoppingToken))
                {
                    state.HasRecords = true;

                    if (!await TryStopAfterUnexpectedFailureAsync(
                            batch.Id, null, lastFinishedLine, batches, exception).ConfigureAwait(false))
                    {
                        throw;
                    }

                    state.UnexpectedFailure = true;

                    break;
                }

                if (!hasNextRecord)
                {
                    break;
                }

                state.HasRecords = true;

                if (!lease.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? pageGroup))
                {
                    return BatchProcessingDisposition.DeferredForMaintenance;
                }

                await using (pageGroup!)
                {
                    IReadOnlyList<PreparedBatchRequestLine>? page = null;

                    try
                    {
                        page = await ReadRequestPageAsync(reader, batch.Endpoint, customIds, stoppingToken)
                            .ConfigureAwait(false);

                        await ProcessRequestPageAsync(batch.Id, page, state, scope.ServiceProvider,
                            batches, settings, stoppingToken).ConfigureAwait(false);

                        lastFinishedLine = page[^1].Line;
                    }
                    catch (Exception exception) when (StopsAsUnexpectedFailure(exception, state, stoppingToken))
                    {
                        // An unexpected failure would otherwise leave the batch in_progress until a
                        // restart or a manual reset. Stop it here instead: seal what is in doubt,
                        // note where it stopped, and publish what was checkpointed as failed. If
                        // even that cannot be recorded, the original failure propagates and
                        // startup reconciliation recovers the batch as before.
                        if (!await TryStopAfterUnexpectedFailureAsync(
                                batch.Id, page, lastFinishedLine, batches, exception).ConfigureAwait(false))
                        {
                            throw;
                        }

                        state.UnexpectedFailure = true;
                    }
                }

                if (state.CancelledMidway || state.BudgetRejected || state.UnexpectedFailure)
                {
                    break;
                }
            }
            state.ReadyToPublish = true;
        }
        state.CancelledMidway |= await IsBatchCancelledAsync(batch.Id, batches).ConfigureAwait(false);

        if (!state.HasRecords)
        {
            // No records means no artifact, directory, writer, move, or permission effect.
            await FinalizeBatchStatusAsync(batch.Id,
                state.CancelledMidway ? BatchStatuses.Cancelled : BatchStatuses.Completed,
                new BatchArtifactPublication(null, null), batches).ConfigureAwait(false);

            await DeleteCompletedCheckpointsAsync(batch.Id, batches).ConfigureAwait(false);

            return BatchProcessingDisposition.Concluded;
        }

        if (!lease.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? artifactGroup))
        {
            return BatchProcessingDisposition.DeferredForMaintenance;
        }

        await using (artifactGroup!)
        {
            SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(ArcanumPaths.FilesDirectory);

            string outputTempPath = Path.Combine(ArcanumPaths.FilesDirectory, $".batch-{batch.Id:N}-out.stage");

            string errorTempPath = Path.Combine(ArcanumPaths.FilesDirectory, $".batch-{batch.Id:N}-err.stage");

            List<OwnedBatchArtifact> newlyPublished = [];

            bool linked = false;

            try
            {
                BatchArtifactPublication publication = await PublishCheckpointArtifactsAsync(
                    batch.Id, batches, files, blobStore, outputTempPath, errorTempPath, newlyPublished, stoppingToken)
                    .ConfigureAwait(false);

                string finalStatus = state.CancelledMidway ? BatchStatuses.Cancelled
                    : state.BudgetRejected || state.UnexpectedFailure ? BatchStatuses.Failed : BatchStatuses.Completed;

                await FinalizeBatchStatusAsync(batch.Id, finalStatus, publication, batches).ConfigureAwait(false);

                linked = true;

                await DeleteCompletedCheckpointsAsync(batch.Id, batches).ConfigureAwait(false);
            }
            finally
            {
                if (!linked)
                {
                    foreach (OwnedBatchArtifact artifact in newlyPublished)
                    {
                        try
                        {
                            UploadedFileDeleteStatus deleted = await files.TryDeleteUnreferencedAsync(
                                artifact.FileId, CancellationToken.None).ConfigureAwait(false);

                            if (deleted == UploadedFileDeleteStatus.NotFound)
                            {
                                _ = IdentityOwnedFileSystemCleanup.TryDelete(artifact.OwnedFile);
                            }
                        }
                        catch (Exception exception)
                        {
                            logger.LogWarning(exception, "Batch {BatchId} could not compensate unpublished artifact {FileId}.", batch.Id, artifact.FileId);
                        }
                    }
                }
                TryDeleteFile(outputTempPath);

                TryDeleteFile(errorTempPath);
            }
        }

        return BatchProcessingDisposition.Concluded;
    }

    private async Task ProcessRequestPageAsync(
        Guid batchId, IReadOnlyList<PreparedBatchRequestLine> page, BatchProcessingState state,
        IServiceProvider scopedServices, IBatchRepository batches, ArcanumSettings settings,
        CancellationToken stoppingToken)
    {
        IReadOnlyList<PreparedBatchRequestLine> pending = await PreparePendingPageAsync(
            batchId, page, batches, stoppingToken).ConfigureAwait(false);

        state.CancelledMidway |= await IsBatchCancelledAsync(batchId, batches).ConfigureAwait(false);

        if (state.CancelledMidway)
        {
            await CompleteDispatchedLinesAsync(batchId, batches, CancellationToken.None).ConfigureAwait(false);

            return;
        }

        if (pending.Count == 0)
        {
            return;
        }
        ITurnRunWriter? writer = scopedServices.GetService<ITurnRunWriter>();

        IBudgetReservationService? reservations = scopedServices.GetService<IBudgetReservationService>();

        Result<TurnAccountingHandle> beginning = await TurnAccountingHandle.BeginBatchAsync(
            writer, reservations, settings.ResolvePricing(),
            pending.Select(static line => new BatchReservationLine(
                line.Request!.Body.Model, line.Request.Body.MaxCompletionTokens ?? line.Request.Body.MaxTokens,
                line.Request.Body.ReasoningBudget)).ToArray(),
            requestId: $"batch-{batchId:N}", stoppingToken).ConfigureAwait(false);

        if (beginning.IsFailure)
        {
            logger.LogWarning(
                "Batch {BatchId} stopped at line {Line} because explicit operator budget policy rejected the next checkpoint page ({Code}). Prior page output remains saved.",
                batchId, pending[0].Line, beginning.Error.Code);

            await PersistNonProviderErrorAsync(batchId, pending[0],
                $"Explicit operator budget policy stopped processing ({beginning.Error.Code}). Prior page output was checkpointed; raise the budget policy and submit the remaining lines to continue.",
                batches, CancellationToken.None).ConfigureAwait(false);

            state.BudgetRejected = true;

            return;
        }
        TurnAccountingHandle accounting = beginning.Value;

        InferenceRunStatus runStatus = InferenceRunStatus.Completed;

        Exception? processingFailure = null;

        try
        {
            using (TurnAccountingAmbient.Push(accounting, writer))
            {
                int concurrency = ArcanumSettingClamps.BatchesMaxConcurrentRequestsPerBatch(
                    settings.ResolveBatches().MaxConcurrentRequestsPerBatch);

                state.CancelledMidway = await RunRequestLinesAsync(pending, concurrency, batchId,
                    settings, accounting, writer, stoppingToken).ConfigureAwait(false);

                if (state.CancelledMidway)
                {
                    runStatus = InferenceRunStatus.Abandoned;

                    await CompleteDispatchedLinesAsync(batchId, batches, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception)
        {
            runStatus = InferenceRunStatus.Failed;

            processingFailure = exception;
        }

        Exception? accountingFailure = null;

        try
        {
            await accounting.CompleteAsync(writer, reservations, runStatus, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            accountingFailure = exception;

            state.AccountingSettlementFailed = true;
        }

        if (processingFailure is not null && accountingFailure is not null)
        {
            throw new AggregateException(
                "Batch page processing and its accounting completion both failed.",
                processingFailure,
                accountingFailure);
        }

        if (processingFailure is not null)
        {
            ExceptionDispatchInfo.Capture(processingFailure).Throw();
        }

        if (accountingFailure is not null)
        {
            ExceptionDispatchInfo.Capture(accountingFailure).Throw();
        }
    }

    private async Task DeleteCompletedCheckpointsAsync(Guid batchId, IBatchRepository batches)
    {
        try
        {
            await batches.DeleteLineCheckpointsAsync(batchId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Batch {BatchId} completed, but durable line-checkpoint cleanup will be deferred.", batchId);
        }
    }

    private static async Task<IReadOnlyList<PreparedBatchRequestLine>> PreparePendingPageAsync(
        Guid batchId,

        IReadOnlyList<PreparedBatchRequestLine> requestPage,

        IBatchRepository batches,

        CancellationToken cancellationToken)

    {
        IReadOnlyList<BatchLineCheckpoint> existing = await batches.ListLineCheckpointsAsync(
            batchId,

            requestPage[0].Line,

            requestPage[^1].Line,

            cancellationToken).ConfigureAwait(false);

        Dictionary<long, BatchLineCheckpoint> checkpoints = existing.ToDictionary(
            static checkpoint => checkpoint.LineNumber);

        List<PreparedBatchRequestLine> pendingProviderLines = [];

        foreach (PreparedBatchRequestLine prepared in requestPage)

        {
            cancellationToken.ThrowIfCancellationRequested();

            if (checkpoints.TryGetValue(prepared.Line, out BatchLineCheckpoint? checkpoint))

            {
                if (checkpoint.State == BatchLineCheckpointState.Dispatched)

                {
                    await CompleteInterruptedLineAsync(
                        checkpoint,

                        batches,

                        cancellationToken).ConfigureAwait(false);
                }
                continue;
            }

            if (prepared.ParseError is not null || prepared.Request?.Body is null)

            {
                await PersistNonProviderErrorAsync(
                    batchId,

                    prepared,

                    prepared.ParseError ?? "Line did not contain a 'body' object.",

                    batches,

                    cancellationToken).ConfigureAwait(false);

                continue;
            }
            pendingProviderLines.Add(prepared);
        }

        return pendingProviderLines;
    }

    private static async Task PersistNonProviderErrorAsync(
        Guid batchId,

        PreparedBatchRequestLine prepared,

        string message,

        IBatchRepository batches,

        CancellationToken cancellationToken)

    {
        string customId = ResolveCustomId(prepared);

        string jsonLine = JsonSerializer.Serialize(
            new BatchJsonlParseError(prepared.Line, message),

            ArcanumJsonContext.Default.BatchJsonlParseError);

        // One durable transition, not begin-then-complete. This line never reaches a provider, so a
        // crash between two writes must not leave it Dispatched — restart recovery would seal that
        // as batch_interrupted_after_dispatch in the OUTPUT file and tell the operator the provider
        // may already have charged for a line that can never succeed.
        _ = await batches.TryRecordTerminalLineAsync(
            batchId,

            prepared.Line,

            customId,

            BatchLineOutputKind.Error,

            BatchRequestOutcome.Failed,

            jsonLine,

            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether an exception raised while reading or processing the input should stop the batch as
    /// failed. Host shutdown, a failed accounting settlement and the Grimoire closing for maintenance
    /// are not processing failures: each leaves the batch <c>in_progress</c>, and the exception
    /// propagates to <see cref="ProcessBatchWithCleanupAsync"/>, so the durable recovery that owns the
    /// batch (and its reservation) settles it at the next startup.
    /// </summary>
    private static bool StopsAsUnexpectedFailure(
        Exception exception,
        BatchProcessingState state,
        CancellationToken stoppingToken) =>
        !stoppingToken.IsCancellationRequested
        && !state.AccountingSettlementFailed
        && !IsMaintenanceUnavailable(exception);

    /// <summary>
    /// True when the failure (or anything it wraps) is the Grimoire closing for maintenance. That is a
    /// deliberate, temporary refusal rather than a processing failure, so it is never published as a
    /// failed batch; it leaves the batch to durable recovery exactly as host shutdown does.
    /// </summary>
    private static bool IsMaintenanceUnavailable(Exception exception) =>
        exception switch
        {
            GrimoireMaintenanceUnavailableException => true,
            AggregateException aggregate => aggregate.InnerExceptions.Any(IsMaintenanceUnavailable),
            _ => exception.InnerException is { } inner && IsMaintenanceUnavailable(inner),
        };

    /// <summary>
    /// Records an unexpected failure durably: every line left in doubt is sealed as interrupted (never
    /// replayed, since the provider may have charged it) with the exception type as the reason, and
    /// the first line that never started carries a note in the error file saying where to resume.
    /// When the failure left no prepared page behind, because reading the input is what failed, the
    /// note goes on the line after <paramref name="lastFinishedLine"/>. Runs on
    /// <see cref="CancellationToken.None"/> because the failure has already happened and this is its
    /// bookkeeping. Returns <see langword="false"/> when the bookkeeping itself failed, so the caller
    /// can let the original failure propagate.
    /// </summary>
    private async Task<bool> TryStopAfterUnexpectedFailureAsync(
        Guid batchId,
        IReadOnlyList<PreparedBatchRequestLine>? page,
        long lastFinishedLine,
        IBatchRepository batches,
        Exception failure)
    {
        string exceptionType = failure.GetType().FullName ?? failure.GetType().Name;

        // The persisted reason names the exception type only, because it travels in artifacts a client
        // downloads. The operator's own log keeps the exception itself, as the unexpected-failure log
        // this path replaced did, so the message and stack of a terminal failure are not lost.
        logger.LogError(
            failure,
            "Batch {BatchId} stopped after an unexpected failure (exception type {ExceptionType}); it will be published as failed with its checkpointed lines.",
            batchId,
            exceptionType);

        try
        {
            await CompleteDispatchedLinesAsync(
                batchId,
                batches,
                CancellationToken.None,
                $"Arcanum stopped processing this batch after an unexpected host error (exception type {exceptionType}) once this request was durably marked for dispatch. Arcanum did not replay it because the provider may have completed and charged the request; submit this line again explicitly if another attempt is desired.")
                .ConfigureAwait(false);

            if (page is { Count: > 0 })
            {
                IReadOnlyList<BatchLineCheckpoint> checkpoints = await batches.ListLineCheckpointsAsync(
                    batchId,
                    page[0].Line,
                    page[^1].Line,
                    CancellationToken.None).ConfigureAwait(false);

                HashSet<long> started = [.. checkpoints.Select(static checkpoint => checkpoint.LineNumber)];

                PreparedBatchRequestLine? firstUnstarted = page.FirstOrDefault(line => !started.Contains(line.Line));

                if (firstUnstarted is not null)
                {
                    await PersistNonProviderErrorAsync(
                        batchId,
                        firstUnstarted,
                        $"Processing stopped before this line after an unexpected host error (exception type {exceptionType}). Earlier lines were checkpointed; submit this line and the lines after it to continue.",
                        batches,
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
            else if (page is null)
            {
                long resumeLine = lastFinishedLine + 1;

                await PersistNonProviderErrorAsync(
                    batchId,
                    new PreparedBatchRequestLine(resumeLine, null, null),
                    $"Processing stopped while reading the input at or after line {resumeLine} after an unexpected host error (exception type {exceptionType}). Lines before it were checkpointed; submit the lines from here on to continue.",
                    batches,
                    CancellationToken.None).ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception bookkeeping)
        {
            logger.LogError(
                bookkeeping,
                "Batch {BatchId} could not record its unexpected failure; it remains durable for startup reconciliation.",
                batchId);

            return false;
        }
    }

    internal static async Task CompleteInterruptedLineAsync(
        BatchLineCheckpoint checkpoint,

        IBatchRepository batches,

        CancellationToken cancellationToken,

        string? detail = null)

    {
        BatchJsonlResponseLine responseLine = new(
            Id: $"batch_req_interrupted_{checkpoint.BatchId:N}_{checkpoint.LineNumber}",

            CustomId: checkpoint.CustomId,

            Response: null,

            Error: new BatchJsonlError(
                "batch_interrupted_after_dispatch",

                detail
                    ?? "The host stopped after this request was durably marked for dispatch. Arcanum did not replay it because the provider may have completed and charged the request; submit this line again explicitly if another attempt is desired."));

        await batches.CompleteLineAsync(
            checkpoint.BatchId,

            checkpoint.LineNumber,

            BatchLineOutputKind.Output,

            BatchRequestOutcome.Failed,

            JsonSerializer.Serialize(responseLine, ArcanumJsonContext.Default.BatchJsonlResponseLine),

            cancellationToken).ConfigureAwait(false);
    }

    private static async Task CompleteDispatchedLinesAsync(
        Guid batchId,

        IBatchRepository batches,

        CancellationToken cancellationToken,

        string? detail = null)

    {
        long afterLine = 0;

        while (true)

        {
            IReadOnlyList<BatchLineCheckpoint> page = await batches.ListLineCheckpointsAsync(
                batchId,

                BatchLineCheckpointState.Dispatched,

                afterLine,

                RequestPageSize,

                cancellationToken).ConfigureAwait(false);

            if (page.Count == 0)

            {
                return;
            }

            foreach (BatchLineCheckpoint checkpoint in page)

            {
                await CompleteInterruptedLineAsync(
                    checkpoint,

                    batches,

                    cancellationToken,

                    detail).ConfigureAwait(false);
            }
            afterLine = page[^1].LineNumber;
        }
    }

    private static string ResolveCustomId(PreparedBatchRequestLine prepared) =>
        ResolveCustomId(prepared.Line, prepared.Request);

    /// <summary>
    /// The id a line's result is reported under: its own <c>custom_id</c>, or <c>line-N</c> when it
    /// names none. Duplicate detection tracks this value, not only what the client wrote.
    /// </summary>
    private static string ResolveCustomId(long line, BatchJsonlRequestLine? request) =>
        string.IsNullOrWhiteSpace(request?.CustomId)
            ? $"line-{line}"
            : request.CustomId;

    private static async Task<bool> IsBatchCancelledAsync(
        Guid batchId,

        IBatchRepository batches)

    {
        BatchRecord? current = await batches.GetByIdAsync(batchId, CancellationToken.None).ConfigureAwait(false);

        return current?.Status == BatchStatuses.Cancelled;
    }

    private static async Task FinalizeBatchStatusAsync(
        Guid batchId,

        string finalStatus,

        BatchArtifactPublication publication,

        IBatchRepository batches)

    {
        DateTimeOffset completedAt = DateTimeOffset.UtcNow;

        bool updated = await batches.TryCompareAndSetStatusAsync(
            batchId,

            BatchStatuses.InProgress,

            finalStatus,

            completedAt,

            publication.OutputFileId,

            publication.ErrorFileId,

            CancellationToken.None).ConfigureAwait(false);

        if (updated)

        {
            return;
        }
        BatchRecord? current = await batches.GetByIdAsync(batchId, CancellationToken.None).ConfigureAwait(false);

        if (current?.Status == BatchStatuses.Cancelled

            && await batches.TryCompareAndSetStatusAsync(
                    batchId,

                    BatchStatuses.Cancelled,

                    BatchStatuses.Cancelled,

                    current.CompletedAt ?? completedAt,

                    publication.OutputFileId,

                    publication.ErrorFileId,

                    CancellationToken.None).ConfigureAwait(false))

        {
            return;
        }

        throw new InvalidOperationException(
            $"Batch '{batchId:D}' changed state while its durable output checkpoints were being published.");
    }

    /// <summary>
    /// Streams and parses non-empty JSONL lines into bounded internal pages. Every page is budgeted,
    /// processed, and flushed before the next page is read; the page size is not a total-work cap.
    /// </summary>
    private static async Task<IReadOnlyList<PreparedBatchRequestLine>> ReadRequestPageAsync(
        BatchJsonlRecordReader.Cursor reader,
        string batchEndpoint,
        BatchCustomIdTracker customIds,
        CancellationToken cancellationToken)
    {
        List<PreparedBatchRequestLine> page = new(RequestPageSize);

        do
        {
            BatchJsonlRecordReadResult record = await reader.ReadRecordAsync(cancellationToken).ConfigureAwait(false);

            page.Add(new PreparedBatchRequestLine(record.PhysicalLine, record.Request,
                record.Error ?? (record.Request?.Body is null
                    ? "Line did not contain a 'body' object."
                    : ValidateEnvelope(record.Request, record.PhysicalLine, batchEndpoint, customIds))));
        }

        while (page.Count < RequestPageSize
            && await reader.HasNextRecordAsync(cancellationToken).ConfigureAwait(false));

        return page;
    }

    /// <summary>
    /// Checks the wrapper around the request body. A batch is bound to the endpoint it was created
    /// for, so a line naming another <c>url</c> or a <c>method</c> other than POST is an error of its
    /// own rather than a chat completion that happens to parse; and <c>custom_id</c> is how a client
    /// matches results to requests, so a repeat of an earlier line's id is refused. Returns the
    /// per-line error text, or <see langword="null"/> when the line is acceptable.
    /// </summary>
    private static string? ValidateEnvelope(
        BatchJsonlRequestLine request,
        long line,
        string batchEndpoint,
        BatchCustomIdTracker customIds)
    {
        // Registered first, so every id is remembered even when the line is refused for another reason.
        // The id a line is reported under counts, including the line-N a line without one is given, so
        // an explicit "line-3" cannot share a result key with the third line's synthesized id.
        bool repeatedCustomId = customIds.IsRepeat(ResolveCustomId(line, request));

        if (!BatchJsonlRules.IsRequiredMethod(request.Method))
        {
            return "Line method must be POST.";
        }

        if (!string.Equals(request.Url, batchEndpoint, StringComparison.Ordinal))
        {
            return $"Line url must be {batchEndpoint}, the endpoint this batch was created for.";
        }

        return repeatedCustomId
            ? "Line custom_id repeats the custom_id of an earlier line; custom_id values must be unique within a batch."
            : null;
    }

    /// <summary>
    /// Remembers the <c>custom_id</c> values one pass over a batch input has read, as 64-bit digests so
    /// the memory cost per line is eight bytes rather than the id. Tracking stops at a fixed ceiling:
    /// the input has no total line limit, so this must not grow without bound, and ids first seen
    /// beyond the ceiling are simply not checked for repeats.
    /// </summary>
    private sealed class BatchCustomIdTracker
    {
        private const int MaxTrackedIds = 1 << 20;

        private readonly HashSet<ulong> _seen = [];

        internal bool IsRepeat(string customId)
        {
            ulong digest = BinaryPrimitives.ReadUInt64LittleEndian(
                SHA256.HashData(Encoding.UTF8.GetBytes(customId)));

            if (_seen.Contains(digest))
            {
                return true;
            }

            if (_seen.Count < MaxTrackedIds)
            {
                _ = _seen.Add(digest);
            }

            return false;
        }
    }

    /// <summary>
    /// Runs every request line with bounded concurrency (<paramref name="maxConcurrentRequests"/>)
    /// while a lightweight side task polls the Grimoire every <see cref="CancelWatchInterval"/> for
    /// an externally-set <see cref="BatchStatuses.Cancelled"/> status (set by
    /// <c>POST /v1/batches/{id}/cancel</c>) and, if seen, cancels the in-flight work so the batch
    /// stops promptly instead of running every remaining line to completion first. Returns
    /// <see langword="true"/> when a mid-batch cancellation was observed.
    /// </summary>
    /// <remarks>
    /// Every unit of concurrent work here — the cancellation watcher and each parallel line — owns a
    /// private DI scope, and therefore a private <c>ArcanumDbContext</c> and
    /// <c>SqliteConnection</c>. <c>IBatchRepository</c> issues raw <c>DbCommand</c>s, so EF's
    /// concurrency detector never fires and <c>SqliteBusyRetry</c> (BUSY/LOCKED only) does not
    /// serialize; sharing the caller's one scoped repository across these tasks corrupts the
    /// connection's internal command list even at the default concurrency of 1, where the watcher
    /// alone races the single line worker.
    /// </remarks>
    private async Task<bool> RunRequestLinesAsync(
        IReadOnlyList<PreparedBatchRequestLine> requestLines,
        int maxConcurrentRequests,
        Guid batchId,
        ArcanumSettings settings,
        TurnAccountingHandle batchAccounting,
        ITurnRunWriter? turnRunWriter,
        CancellationToken stoppingToken)
    {
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        Task watcherTask = WatchForCancellationAsync(batchId, linkedCts);

        bool cancelledMidway = false;

        Exception? processingFailure = null;

        try
        {
            await Parallel.ForEachAsync(
                requestLines,
                new ParallelOptions { MaxDegreeOfParallelism = maxConcurrentRequests, CancellationToken = linkedCts.Token },
                async (item, ct) =>
                {
                    await using AsyncServiceScope lineScope = scopeFactory.CreateAsyncScope();

                    IBatchRepository lineBatches = lineScope.ServiceProvider.GetRequiredService<IBatchRepository>();

                    IArcanumIntelligenceProvider lineIntelligence =
                        lineScope.ServiceProvider.GetRequiredService<IArcanumIntelligenceProvider>();

                    TurnAccountingHandle lineAccounting = batchAccounting.CreateNestedOperationHandle();
                    using (TurnAccountingAmbient.Push(lineAccounting, turnRunWriter))
                    {
                        await ProcessRequestLineAsync(
                            batchId,

                            item,

                            lineBatches,

                            lineIntelligence,

                            settings,

                            ct).ConfigureAwait(false);
                    }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            // Cancelled via the watcher (external POST .../cancel), not host shutdown. Only the
            // watcher can have cancelled the linked source here; any other OperationCanceledException
            // (a provider-side timeout, say) is a processing failure and takes the arm below.
            cancelledMidway = true;
        }
        catch (Exception ex)
        {
            processingFailure = ex;
        }

        await StopAndJoinCancellationWatcherAsync(
            linkedCts,
            watcherTask,
            processingFailure).ConfigureAwait(false);

        return cancelledMidway;
    }

    internal static async Task StopAndJoinCancellationWatcherAsync(
        CancellationTokenSource linkedCts,
        Task watcherTask,
        Exception? processingFailure)
    {
        Exception? cancellationFailure = null;

        try
        {
            await linkedCts.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            cancellationFailure = ex;
        }

        Exception? watcherFailure = null;

        try
        {
            await watcherTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            watcherFailure = ex;
        }

        ThrowBatchFailures(processingFailure, cancellationFailure, watcherFailure);
    }

    private static void ThrowBatchFailures(params Exception?[] failures)
    {
        Exception[] present = failures
            .Where(static failure => failure is not null)
            .Select(static failure => failure!)
            .ToArray();

        if (present.Length == 0)
        {
            return;
        }

        if (present.Length == 1)
        {
            ExceptionDispatchInfo.Capture(present[0]).Throw();
            return;
        }

        throw new AggregateException("Batch request processing and watcher cleanup failed.", present);
    }

    private async Task WatchForCancellationAsync(Guid batchId, CancellationTokenSource linkedCts)
    {
        try
        {
            await using AsyncServiceScope watchScope = scopeFactory.CreateAsyncScope();

            IBatchRepository batches = watchScope.ServiceProvider.GetRequiredService<IBatchRepository>();

            using PeriodicTimer watchTimer = new(CancelWatchInterval);

            while (await watchTimer.WaitForNextTickAsync(linkedCts.Token).ConfigureAwait(false))
            {
                BatchRecord? current = await batches.GetByIdAsync(batchId, CancellationToken.None).ConfigureAwait(false);

                if (current?.Status == BatchStatuses.Cancelled)
                {
                    await linkedCts.CancelAsync().ConfigureAwait(false);

                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task ProcessRequestLineAsync(
        Guid batchId,

        PreparedBatchRequestLine prepared,

        IBatchRepository batches,

        IArcanumIntelligenceProvider intelligence,

        ArcanumSettings settings,

        CancellationToken cancellationToken)
    {
        BatchJsonlRequestLine requestLine = prepared.Request!;

        string customId = ResolveCustomId(prepared);

        bool began = await batches.TryBeginLineAsync(
            batchId,

            prepared.Line,

            customId,

            cancellationToken).ConfigureAwait(false);

        if (!began)

        {
            return;
        }
        Result<OpenAiChatResponse> result = await OpenAiV1Endpoints
            .ExecuteChatRequestForBatchAsync(requestLine.Body, intelligence, settings, cancellationToken)
            .ConfigureAwait(false);

        BatchJsonlResponseLine responseLine = result.IsSuccess
            ? new BatchJsonlResponseLine(
                Id: "batch_req_" + Guid.NewGuid().ToString("N"),
                CustomId: customId,
                Response: new BatchJsonlResponseBody(200, Guid.NewGuid().ToString("N"), result.Value),
                Error: null)
            : new BatchJsonlResponseLine(
                Id: "batch_req_" + Guid.NewGuid().ToString("N"),
                CustomId: customId,
                Response: null,
                Error: new BatchJsonlError(result.Error.Code, result.Error.Message));

        await batches.CompleteLineAsync(
            batchId,

            prepared.Line,

            BatchLineOutputKind.Output,

            result.IsSuccess

                ? BatchRequestOutcome.Completed

                : BatchRequestOutcome.Failed,

            JsonSerializer.Serialize(responseLine, ArcanumJsonContext.Default.BatchJsonlResponseLine),

            CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<BatchArtifactPublication> PublishCheckpointArtifactsAsync(
        Guid batchId,
        IBatchRepository batches,
        IUploadedFileRepository files,
        IEncryptedBlobStore blobStore,
        string outputTempPath,
        string errorTempPath,
        List<OwnedBatchArtifact> newlyPublished,
        CancellationToken cancellationToken)
    {
        TryDeleteFile(outputTempPath);

        TryDeleteFile(errorTempPath);

        await using BatchJsonlWriters writers = await BatchJsonlWriters.CreateAsync(
            blobStore,
            outputTempPath,
            errorTempPath,
            batchId,
            cancellationToken).ConfigureAwait(false);

        long afterLine = 0;

        while (true)
        {
            IReadOnlyList<BatchLineCheckpoint> page = await batches.ListLineCheckpointsAsync(
                batchId,
                BatchLineCheckpointState.Completed,
                afterLine,
                RequestPageSize,
                cancellationToken).ConfigureAwait(false);

            if (page.Count == 0)
            {
                break;
            }

            foreach (BatchLineCheckpoint checkpoint in page)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (checkpoint.JsonLine is null || checkpoint.OutputKind is null)
                {
                    throw new InvalidDataException(
                        $"Completed batch checkpoint '{batchId:D}/{checkpoint.LineNumber}' has no terminal JSONL payload.");
                }

                if (checkpoint.OutputKind == BatchLineOutputKind.Output)
                {
                    await writers.WriteOutputLineAsync(checkpoint.JsonLine, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await writers.WriteErrorLineAsync(checkpoint.JsonLine, cancellationToken).ConfigureAwait(false);
                }
            }
            afterLine = page[^1].LineNumber;
        }

        int outputLineCount = writers.OutputLineCount;

        int errorLineCount = writers.ErrorLineCount;

        (EncryptedBlobDescriptor? outputDescriptor, EncryptedBlobDescriptor? errorDescriptor) =

            await writers.CompleteAsync(CancellationToken.None).ConfigureAwait(false);

        Guid? outputFileId = outputLineCount > 0

            ? await FinalizeResultFileAsync(
                    outputTempPath,
                    "batch_output.jsonl",
                    "batch_output",
                    files,
                    blobStore,
                    outputDescriptor!,
                    newlyPublished,
                    CancellationToken.None).ConfigureAwait(false)

            : null;

        Guid? errorFileId = errorLineCount > 0

            ? await FinalizeResultFileAsync(
                    errorTempPath,
                    "batch_errors.jsonl",
                    "error",
                    files,
                    blobStore,
                    errorDescriptor!,
                    newlyPublished,
                    CancellationToken.None).ConfigureAwait(false)

            : null;

        return new BatchArtifactPublication(outputFileId, errorFileId);
    }

    private sealed record BatchArtifactPublication(
        Guid? OutputFileId,
        Guid? ErrorFileId);

    private sealed record OwnedBatchArtifact(Guid FileId, IdentityOwnedFileSystemArtifact OwnedFile);

    /// <summary>
    /// Moves a completed encrypted JSONL stage into the uploaded-files directory and registers it.
    /// </summary>
    private static async Task<Guid> FinalizeResultFileAsync(
        string tempPath,
        string filename,
        string purpose,
        IUploadedFileRepository files,
        IEncryptedBlobStore blobStore,
        EncryptedBlobDescriptor descriptor,
        List<OwnedBatchArtifact> newlyPublished,
        CancellationToken cancellationToken)
    {
        Guid id = Guid.NewGuid();

        string path = UploadedFileStorage.ResolvePath(id);

        bool publicationOwnsCleanup = false;

        IdentityOwnedFileSystemArtifact ownedFile = default;

        try
        {
            File.Move(tempPath, path, overwrite: true);

            if (!IdentityOwnedFileSystemCleanup.TryCapturePath(
                    path, FileSystemObjectKind.RegularFile, out ownedFile))
            {
                throw new UploadedFilePublicationException(id);
            }
            newlyPublished.Add(new OwnedBatchArtifact(id, ownedFile));

            SecureFilePermissions.ApplyOwnerOnlyFile(path);
            await using Stream plaintext = await blobStore.OpenReadAsync(
                    path,
                    EncryptedBlobPurpose.BatchArtifact,
                    cancellationToken)
                .ConfigureAwait(false);
            string plaintextSha256 = Convert.ToHexString(
                await System.Security.Cryptography.SHA256
                    .HashDataAsync(plaintext, cancellationToken)
                    .ConfigureAwait(false));
            UploadedFileRecord record = new(
                id,
                filename,
                descriptor.PlaintextLength,
                purpose,
                "application/jsonl",
                DateTimeOffset.UtcNow,
                descriptor.Version,
                descriptor.KeyId,
                plaintextSha256);
            publicationOwnsCleanup = true;

            await files
                .CreateForOwnedFileAsync(record, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            if (!publicationOwnsCleanup)
            {
                _ = IdentityOwnedFileSystemCleanup.TryDelete(ownedFile);
            }

            throw;
        }
        finally
        {
            TryDeleteFile(tempPath);
        }

        return id;
    }

    /// <summary>
    /// Thread-safe incremental JSONL writers for batch output/error files. Lines are flushed as they
    /// complete so peak memory stays per-line rather than the full result set.
    /// </summary>
    private sealed class BatchJsonlWriters : IAsyncDisposable
    {
        private readonly StreamWriter _output;

        private readonly StreamWriter _error;

        private readonly EncryptedBlobWriter _encryptedOutput;

        private readonly EncryptedBlobWriter _encryptedError;

        private readonly SemaphoreSlim _outputLock = new(1, 1);

        private readonly SemaphoreSlim _errorLock = new(1, 1);

        private int _outputLineCount;

        private int _errorLineCount;

        private int _textWritersDisposed;

        private int _disposeStarted;

        private BatchJsonlWriters(
            StreamWriter output,
            StreamWriter error,
            EncryptedBlobWriter encryptedOutput,
            EncryptedBlobWriter encryptedError)
        {
            _output = output;

            _error = error;

            _encryptedOutput = encryptedOutput;

            _encryptedError = encryptedError;
        }

        public int OutputLineCount => Volatile.Read(ref _outputLineCount);

        public int ErrorLineCount => Volatile.Read(ref _errorLineCount);

        public static async Task<BatchJsonlWriters> CreateAsync(
            IEncryptedBlobStore blobStore,
            string outputTempPath,
            string errorTempPath,
            Guid batchId,
            CancellationToken cancellationToken)
        {
            EncryptedBlobWriter? encryptedOutput = null;

            EncryptedBlobWriter? encryptedError = null;

            StreamWriter? output = null;

            StreamWriter? error = null;

            try
            {
                encryptedOutput = await blobStore.CreateWriterAsync(
                    outputTempPath,
                    EncryptedBlobPurpose.BatchArtifact,
                    batchId.ToByteArray(),
                    cancellationToken)
                    .ConfigureAwait(false);

                encryptedError = await blobStore.CreateWriterAsync(
                    errorTempPath,
                    EncryptedBlobPurpose.BatchArtifact,
                    batchId.ToByteArray(),
                    cancellationToken)
                    .ConfigureAwait(false);

                output = CreateTextWriter(encryptedOutput);

                error = CreateTextWriter(encryptedError);

                return new BatchJsonlWriters(output, error, encryptedOutput, encryptedError);
            }
            catch (Exception creationFailure)
            {
                List<Exception>? cleanupFailures = null;

                if (error is not null)
                {
                    try
                    {
                        await error.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception cleanupFailure)
                    {
                        AddFailure(ref cleanupFailures, cleanupFailure);
                    }
                }

                if (output is not null)
                {
                    try
                    {
                        await output.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception cleanupFailure)
                    {
                        AddFailure(ref cleanupFailures, cleanupFailure);
                    }
                }

                if (encryptedError is not null)
                {
                    try
                    {
                        await encryptedError.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception cleanupFailure)
                    {
                        AddFailure(ref cleanupFailures, cleanupFailure);
                    }
                }

                if (encryptedOutput is not null)
                {
                    try
                    {
                        await encryptedOutput.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception cleanupFailure)
                    {
                        AddFailure(ref cleanupFailures, cleanupFailure);
                    }
                }

                if (cleanupFailures is null)
                {
                    throw;
                }

                cleanupFailures.Insert(0, creationFailure);

                throw new AggregateException(
                    "Batch JSONL writer construction and cleanup both failed.",
                    cleanupFailures);
            }
        }

        private static StreamWriter CreateTextWriter(EncryptedBlobWriter writer) =>
            new(
                writer,
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024,
                leaveOpen: true);

        public async Task<(EncryptedBlobDescriptor? Output, EncryptedBlobDescriptor? Error)>
            CompleteAsync(CancellationToken cancellationToken)
        {
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);

            await _error.FlushAsync(cancellationToken).ConfigureAwait(false);

            await DisposeTextWritersAsync().ConfigureAwait(false);

            EncryptedBlobDescriptor? output = OutputLineCount > 0
                ? await _encryptedOutput.CompleteAsync(cancellationToken).ConfigureAwait(false)
                : null;

            EncryptedBlobDescriptor? error = ErrorLineCount > 0
                ? await _encryptedError.CompleteAsync(cancellationToken).ConfigureAwait(false)
                : null;

            return (output, error);
        }

        public async Task WriteOutputLineAsync(string line, CancellationToken cancellationToken)
        {
            await _outputLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await _output.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);

                _ = Interlocked.Increment(ref _outputLineCount);
            }
            finally
            {
                _ = _outputLock.Release();
            }
        }

        public async Task WriteErrorLineAsync(string line, CancellationToken cancellationToken)
        {
            await _errorLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await _error.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);

                _ = Interlocked.Increment(ref _errorLineCount);
            }
            finally
            {
                _ = _errorLock.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            {
                return;
            }

            List<Exception>? failures = null;

            try
            {
                await DisposeTextWritersAsync().ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                AddFailure(ref failures, failure);
            }

            try
            {
                await _encryptedOutput.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                AddFailure(ref failures, failure);
            }

            try
            {
                await _encryptedError.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                AddFailure(ref failures, failure);
            }

            try
            {
                _outputLock.Dispose();
            }
            catch (Exception failure)
            {
                AddFailure(ref failures, failure);
            }

            try
            {
                _errorLock.Dispose();
            }
            catch (Exception failure)
            {
                AddFailure(ref failures, failure);
            }

            ThrowIfCleanupFailed("Batch JSONL writer disposal failed.", failures);
        }

        private async Task DisposeTextWritersAsync()
        {
            if (Interlocked.Exchange(ref _textWritersDisposed, 1) != 0)
            {
                return;
            }

            List<Exception>? failures = null;

            try
            {
                await _output.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                AddFailure(ref failures, failure);
            }

            try
            {
                await _error.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                AddFailure(ref failures, failure);
            }

            ThrowIfCleanupFailed("Batch JSONL text-writer disposal failed.", failures);
        }

        private static void AddFailure(ref List<Exception>? failures, Exception failure) =>
            (failures ??= []).Add(failure);

        private static void ThrowIfCleanupFailed(string message, List<Exception>? failures)
        {
            if (failures is null)
            {
                return;
            }

            if (failures is [Exception failure])
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }

            throw new AggregateException(message, failures);
        }
    }

    internal static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
