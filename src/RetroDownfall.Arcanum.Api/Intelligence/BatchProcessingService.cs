using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
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
    ILogger<BatchProcessingService> logger) : BackgroundService
{

    private const int RequestPageSize = 64;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan CancelWatchInterval = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();

    private sealed record PreparedBatchRequestLine(
        long Line,
        BatchJsonlRequestLine? Request,
        string? ParseError);

    // Best-effort early-rejection guard, not a correctness guarantee — there is a race window between
    // this check and the status reset. The real double-processing guard is the worker's
    // _inFlight.TryAdd(batch.Id, 0) before processing (BatchProcessingService.cs:131), which prevents
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

        using IServiceScope scope = scopeFactory.CreateScope();

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

            if (!_inFlight.TryAdd(batch.Id, 0))
            {

                continue;

            }

            _ = Task.Run(
                () => ProcessBatchWithCleanupAsync(batch, stoppingToken),
                CancellationToken.None);

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

    internal async Task ProcessBatchAsync(BatchRecord batch, CancellationToken stoppingToken)
    {

        using IServiceScope scope = scopeFactory.CreateScope();

        IBatchRepository batches = scope.ServiceProvider.GetRequiredService<IBatchRepository>();

        IUploadedFileRepository files = scope.ServiceProvider.GetRequiredService<IUploadedFileRepository>();

        IEncryptedBlobStore blobStore = scope.ServiceProvider.GetRequiredService<IEncryptedBlobStore>();

        IArcanumIntelligenceProvider intelligence = scope.ServiceProvider.GetRequiredService<IArcanumIntelligenceProvider>();

        // Claim the row instead of overwriting it: TickAsync read this record before queueing the
        // worker, so an operator cancel (validating → cancelled) can have committed in the meantime.
        // A losing CAS means the batch is no longer ours to run — leave the durable status alone.
        bool claimed = await batches.TryCompareAndSetStatusAsync(
                batch.Id,
                BatchStatuses.Validating,
                BatchStatuses.InProgress,
                completedAt: null,
                batch.OutputFileId,
                batch.ErrorFileId,
                stoppingToken)
            .ConfigureAwait(false);

        if (!claimed)
        {

            logger.LogInformation(
                "Batch {BatchId} was no longer validating when the worker claimed it; leaving its durable status untouched.",
                batch.Id);

            return;

        }

        string inputPath = UploadedFileStorage.ResolvePath(batch.InputFileId);

        if (!File.Exists(inputPath))
        {

            _ = await batches.TryCompareAndSetStatusAsync(
                    batch.Id,
                    BatchStatuses.InProgress,
                    BatchStatuses.Failed,
                    DateTimeOffset.UtcNow,
                    null,
                    null,
                    CancellationToken.None)
                .ConfigureAwait(false);

            return;

        }

        UploadedFileRecord? inputFile = await files
            .GetByIdAsync(batch.InputFileId, stoppingToken)
            .ConfigureAwait(false);
        if (inputFile is null)
        {
            _ = await batches.TryCompareAndSetStatusAsync(
                    batch.Id,
                    BatchStatuses.InProgress,
                    BatchStatuses.Failed,
                    DateTimeOffset.UtcNow,
                    null,
                    null,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        ArcanumSettings settings = optionsMonitor.CurrentValue;

        BatchesSettings batchesSettings = settings.ResolveBatches();

        int maxConcurrentRequests = ArcanumSettingClamps.BatchesMaxConcurrentRequestsPerBatch(batchesSettings.MaxConcurrentRequestsPerBatch);

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(ArcanumPaths.FilesDirectory);

        string outputTempPath = Path.Combine(
            ArcanumPaths.FilesDirectory,
            $".batch-{batch.Id:N}-out.stage");

        string errorTempPath = Path.Combine(
            ArcanumPaths.FilesDirectory,
            $".batch-{batch.Id:N}-err.stage");

        try
        {
            bool cancelledMidway = false;

            bool budgetRejected = false;

            ITurnRunWriter? turnRunWriter = scope.ServiceProvider.GetService<ITurnRunWriter>();

            IBudgetReservationService? budgetReservations =
                scope.ServiceProvider.GetService<IBudgetReservationService>();

            await foreach (IReadOnlyList<PreparedBatchRequestLine> requestPage in EnumerateRequestPagesAsync(
                               inputPath,
                               blobStore,
                               inputFile.EncryptionVersion,
                               stoppingToken)
                .ConfigureAwait(false))
            {

                IReadOnlyList<PreparedBatchRequestLine> pendingProviderLines = await PreparePendingPageAsync(

                    batch.Id,

                    requestPage,

                    batches,

                    stoppingToken).ConfigureAwait(false);

                if (await IsBatchCancelledAsync(batch.Id, batches).ConfigureAwait(false))

                {

                    cancelledMidway = true;

                    break;

                }

                if (pendingProviderLines.Count == 0)

                {

                    continue;

                }

                Result<TurnAccountingHandle> batchAccountingBegin = await TurnAccountingHandle.BeginBatchAsync(
                        turnRunWriter,
                        budgetReservations,
                        settings.ResolvePricing(),
                        pendingProviderLines
                            .Select(static line => new BatchReservationLine(
                                line.Request!.Body.Model,
                                line.Request.Body.MaxCompletionTokens ?? line.Request.Body.MaxTokens,
                                line.Request.Body.ReasoningBudget))
                            .ToArray(),
                        requestId: $"batch-{batch.Id:N}",
                        stoppingToken)
                    .ConfigureAwait(false);

                if (batchAccountingBegin.IsFailure)
                {

                    logger.LogWarning(
                        "Batch {BatchId} stopped at line {Line} because explicit operator budget policy rejected the next checkpoint page ({Code}). Prior page output remains saved.",
                        batch.Id,
                        pendingProviderLines[0].Line,
                        batchAccountingBegin.Error.Code);

                    await PersistNonProviderErrorAsync(

                        batch.Id,

                        pendingProviderLines[0],

                        $"Explicit operator budget policy stopped processing ({batchAccountingBegin.Error.Code}). Prior page output was checkpointed; raise the budget policy and submit the remaining lines to continue.",

                        batches,

                        CancellationToken.None).ConfigureAwait(false);

                    budgetRejected = true;

                    break;

                }

                TurnAccountingHandle batchAccounting = batchAccountingBegin.Value;

                InferenceRunStatus batchRunStatus = InferenceRunStatus.Completed;

                try
                {

                    using (TurnAccountingAmbient.Push(batchAccounting, turnRunWriter))
                    {

                        try
                        {

                            cancelledMidway = await RunRequestLinesAsync(
                                pendingProviderLines,
                                maxConcurrentRequests,
                                batch.Id,
                                settings,
                                batchAccounting,
                                turnRunWriter,
                                stoppingToken).ConfigureAwait(false);

                            if (cancelledMidway)
                            {

                                batchRunStatus = InferenceRunStatus.Abandoned;

                            }

                        }
                        catch
                        {

                            batchRunStatus = InferenceRunStatus.Failed;

                            throw;

                        }

                    }

                }
                finally
                {

                    try
                    {

                        await batchAccounting.CompleteAsync(
                                turnRunWriter,
                                budgetReservations,
                                batchRunStatus,
                                CancellationToken.None)
                            .ConfigureAwait(false);

                    }
                    catch (Exception ex)
                    {

                        logger.LogWarning(ex, "Failed to complete batch accounting for {BatchId}.", batch.Id);

                    }

                }

                if (cancelledMidway)
                {

                    break;

                }

            }

            cancelledMidway = cancelledMidway

                || await IsBatchCancelledAsync(batch.Id, batches).ConfigureAwait(false);

            if (cancelledMidway)

            {

                await CompleteDispatchedLinesAsync(

                    batch.Id,

                    batches,

                    CancellationToken.None).ConfigureAwait(false);

            }

            BatchArtifactPublication publication = await PublishCheckpointArtifactsAsync(

                batch.Id,

                batches,

                files,

                blobStore,

                outputTempPath,

                errorTempPath,

                stoppingToken).ConfigureAwait(false);

            string finalStatus = cancelledMidway
                ? BatchStatuses.Cancelled
                : budgetRejected
                    ? BatchStatuses.Failed
                    : BatchStatuses.Completed;

            await FinalizeBatchStatusAsync(

                batch.Id,

                finalStatus,

                publication,

                batches).ConfigureAwait(false);

            try

            {

                await batches.DeleteLineCheckpointsAsync(batch.Id, CancellationToken.None).ConfigureAwait(false);

            }

            catch (Exception ex)

            {

                logger.LogWarning(

                    ex,

                    "Batch {BatchId} completed, but durable line-checkpoint cleanup will be deferred.",

                    batch.Id);

            }

        }
        finally
        {

            if (!string.IsNullOrEmpty(outputTempPath))
            {

                TryDeleteFile(outputTempPath);

            }

            if (!string.IsNullOrEmpty(errorTempPath))
            {

                TryDeleteFile(errorTempPath);

            }

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

        bool began = await batches.TryBeginLineAsync(

            batchId,

            prepared.Line,

            customId,

            cancellationToken).ConfigureAwait(false);

        if (!began)

        {

            return;

        }

        string jsonLine = JsonSerializer.Serialize(

            new BatchJsonlParseError(prepared.Line, message),

            ArcanumJsonContext.Default.BatchJsonlParseError);

        await batches.CompleteLineAsync(

            batchId,

            prepared.Line,

            BatchLineOutputKind.Error,

            BatchRequestOutcome.Failed,

            jsonLine,

            CancellationToken.None).ConfigureAwait(false);

    }

    internal static async Task CompleteInterruptedLineAsync(

        BatchLineCheckpoint checkpoint,

        IBatchRepository batches,

        CancellationToken cancellationToken)

    {

        BatchJsonlResponseLine responseLine = new(

            Id: $"batch_req_interrupted_{checkpoint.BatchId:N}_{checkpoint.LineNumber}",

            CustomId: checkpoint.CustomId,

            Response: null,

            Error: new BatchJsonlError(

                "batch_interrupted_after_dispatch",

                "The host stopped after this request was durably marked for dispatch. Arcanum did not replay it because the provider may have completed and charged the request; submit this line again explicitly if another attempt is desired."));

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

        CancellationToken cancellationToken)

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

                    cancellationToken).ConfigureAwait(false);

            }

            afterLine = page[^1].LineNumber;

        }

    }

    private static string ResolveCustomId(PreparedBatchRequestLine prepared) =>

        string.IsNullOrWhiteSpace(prepared.Request?.CustomId)

            ? $"line-{prepared.Line}"

            : prepared.Request.CustomId;

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
    private static async IAsyncEnumerable<IReadOnlyList<PreparedBatchRequestLine>> EnumerateRequestPagesAsync(
        string inputPath,
        IEncryptedBlobStore blobStore,
        int encryptionVersion,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {

        List<PreparedBatchRequestLine> page = new(RequestPageSize);

        await using Stream stream = await blobStore

            .OpenCompatibleReadAsync(

                inputPath,

                EncryptedBlobPurpose.UploadedFile,

                encryptionVersion,

                cancellationToken)

            .ConfigureAwait(false);

        await foreach (BatchJsonlRecordReadResult item in BatchJsonlRecordReader.ReadAsync(

                           stream,

                           temporaryDirectory: null,

                           spillCreated: null,

                           cancellationToken)

            .ConfigureAwait(false))
        {

            PreparedBatchRequestLine prepared = item.Error is not null

                ? new PreparedBatchRequestLine(

                    item.PhysicalLine,

                    Request: null,

                    item.Error)

                : item.Request?.Body is null

                    ? new PreparedBatchRequestLine(

                        item.PhysicalLine,

                        item.Request,

                        "Line did not contain a 'body' object.")

                    : new PreparedBatchRequestLine(

                        item.PhysicalLine,

                        item.Request,

                        ParseError: null);

            page.Add(prepared);

            if (page.Count < RequestPageSize)
            {

                continue;

            }

            yield return page;

            page = new List<PreparedBatchRequestLine>(RequestPageSize);

        }

        if (page.Count > 0)
        {

            yield return page;

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
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {

            // Cancelled via the watcher (external POST .../cancel), not host shutdown.
            cancelledMidway = true;

        }
        finally
        {

            await linkedCts.CancelAsync().ConfigureAwait(false);

            try
            {

                await watcherTask.ConfigureAwait(false);

            }
            catch (OperationCanceledException)
            {

            }

        }

        return cancelledMidway;

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

                    CancellationToken.None).ConfigureAwait(false)

            : null;

        return new BatchArtifactPublication(outputFileId, errorFileId);

    }

    private sealed record BatchArtifactPublication(

        Guid? OutputFileId,

        Guid? ErrorFileId);

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
        CancellationToken cancellationToken)
    {

        Guid id = Guid.NewGuid();

        string path = UploadedFileStorage.ResolvePath(id);

        bool publicationOwnsCleanup = false;

        try
        {
            File.Move(tempPath, path, overwrite: true);
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

                TryDeleteFile(path);

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

        private bool _textWritersDisposed;

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
            EncryptedBlobWriter output = await blobStore.CreateWriterAsync(
                    outputTempPath,
                    EncryptedBlobPurpose.BatchArtifact,
                    batchId.ToByteArray(),
                    cancellationToken)
                .ConfigureAwait(false);
            try
            {
                EncryptedBlobWriter error = await blobStore.CreateWriterAsync(
                        errorTempPath,
                        EncryptedBlobPurpose.BatchArtifact,
                        batchId.ToByteArray(),
                        cancellationToken)
                    .ConfigureAwait(false);
                return new BatchJsonlWriters(
                    new StreamWriter(
                        output,
                        new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                        bufferSize: 1024,
                        leaveOpen: true),
                    new StreamWriter(
                        error,
                        new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                        bufferSize: 1024,
                        leaveOpen: true),
                    output,
                    error);
            }
            catch
            {
                await output.DisposeAsync().ConfigureAwait(false);
                throw;
            }

        }

        public async Task<(EncryptedBlobDescriptor? Output, EncryptedBlobDescriptor? Error)>
            CompleteAsync(CancellationToken cancellationToken)
        {
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
            await _error.FlushAsync(cancellationToken).ConfigureAwait(false);
            await _output.DisposeAsync().ConfigureAwait(false);
            await _error.DisposeAsync().ConfigureAwait(false);
            _textWritersDisposed = true;
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

            if (!_textWritersDisposed)
            {
                await _output.DisposeAsync().ConfigureAwait(false);
                await _error.DisposeAsync().ConfigureAwait(false);
                _textWritersDisposed = true;
            }

            await _encryptedOutput.DisposeAsync().ConfigureAwait(false);
            await _encryptedError.DisposeAsync().ConfigureAwait(false);

            _outputLock.Dispose();

            _errorLock.Dispose();

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
