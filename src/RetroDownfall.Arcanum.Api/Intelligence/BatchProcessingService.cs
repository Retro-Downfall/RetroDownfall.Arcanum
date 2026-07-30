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

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan CancelWatchInterval = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();

    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _inFlightCancellation = new();

    private readonly ConcurrentDictionary<Guid, byte> _expiryRequested = new();

    private sealed record PreparedBatchRequestLine(
        int Line,
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

    /// <summary>
    /// Test/ops hook: request expiry cancellation for an in-flight batch without deleting files
    /// from the expiry sweep. The processor finalizer owns terminal status and file cleanup.
    /// </summary>
    internal bool TryRequestExpiryCancel(Guid batchId)
    {

        if (!_inFlight.ContainsKey(batchId))
        {

            return false;

        }

        _ = _expiryRequested.TryAdd(batchId, 0);

        if (_inFlightCancellation.TryGetValue(batchId, out CancellationTokenSource? cts))
        {

            try
            {

                cts.Cancel();

            }
            catch (ObjectDisposedException)
            {

            }

        }

        return true;

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

        IReadOnlyList<BatchRecord> active = await batches.ListActiveAsync(stoppingToken).ConfigureAwait(false);

        if (active.Count == 0)
        {

            return;

        }

        BatchesSettings settings = optionsMonitor.CurrentValue.ResolveBatches();

        int expiryHours = ArcanumSettingClamps.BatchesBatchExpiryHours(settings.BatchExpiryHours);

        int maxConcurrentBatches = ArcanumSettingClamps.BatchesMaxConcurrentBatches(settings.MaxConcurrentBatches);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        foreach (BatchRecord batch in active)
        {

            if (now - batch.CreatedAt > TimeSpan.FromHours(expiryHours))
            {

                if (_inFlight.ContainsKey(batch.Id))
                {

                    // In-flight: signal the processor CTS; never delete files from the sweep.
                    // The processor/finalizer owns terminal status and file cleanup.
                    _ = _expiryRequested.TryAdd(batch.Id, 0);

                    if (_inFlightCancellation.TryGetValue(batch.Id, out CancellationTokenSource? cts))
                    {

                        try
                        {

                            await cts.CancelAsync().ConfigureAwait(false);

                        }
                        catch (ObjectDisposedException)
                        {

                        }

                    }

                    continue;

                }

                await ExpireBatchAsync(batch, batches, stoppingToken).ConfigureAwait(false);

            }

        }

        foreach (BatchRecord batch in active)
        {

            if (batch.Status != BatchStatuses.Validating)
            {

                continue;

            }

            if (_inFlight.Count >= maxConcurrentBatches)
            {

                break;

            }

            if (!_inFlight.TryAdd(batch.Id, 0))
            {

                continue;

            }

            CancellationTokenSource batchCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

            if (!_inFlightCancellation.TryAdd(batch.Id, batchCts))
            {

                batchCts.Dispose();

                _ = _inFlight.TryRemove(batch.Id, out _);

                continue;

            }

            _ = Task.Run(() => ProcessBatchWithCleanupAsync(batch, batchCts), CancellationToken.None);

        }

    }

    private async Task ExpireBatchAsync(BatchRecord batch, IBatchRepository batches, CancellationToken cancellationToken)
    {

        try
        {

            TryDeleteFile(UploadedFileStorage.ResolvePath(batch.InputFileId));

            if (batch.OutputFileId is { } outputFileId)
            {

                TryDeleteFile(UploadedFileStorage.ResolvePath(outputFileId));

            }

            if (batch.ErrorFileId is { } errorFileId)
            {

                TryDeleteFile(UploadedFileStorage.ResolvePath(errorFileId));

            }

            await batches.UpdateStatusAsync(
                batch.Id,
                BatchStatuses.Expired,
                DateTimeOffset.UtcNow,
                batch.OutputFileId,
                batch.ErrorFileId,
                cancellationToken).ConfigureAwait(false);

        }
        catch (Exception ex)
        {

            logger.LogWarning(ex, "Failed to expire batch {BatchId}; will retry on the next sweep.", batch.Id);

        }

    }

    private async Task ProcessBatchWithCleanupAsync(BatchRecord batch, CancellationTokenSource batchCts)
    {

        try
        {

            await ProcessBatchAsync(batch, batchCts.Token).ConfigureAwait(false);

        }
        catch (OperationCanceledException) when (batchCts.IsCancellationRequested)
        {

            bool expired = _expiryRequested.TryRemove(batch.Id, out _);

            try
            {

                using IServiceScope scope = scopeFactory.CreateScope();

                IBatchRepository batches = scope.ServiceProvider.GetRequiredService<IBatchRepository>();

                if (expired)
                {

                    // Expiry-driven cancel: mark expired and delete files from the processor, not the sweep.
                    TryDeleteFile(UploadedFileStorage.ResolvePath(batch.InputFileId));

                    if (batch.OutputFileId is { } outputFileId)
                    {

                        TryDeleteFile(UploadedFileStorage.ResolvePath(outputFileId));

                    }

                    if (batch.ErrorFileId is { } errorFileId)
                    {

                        TryDeleteFile(UploadedFileStorage.ResolvePath(errorFileId));

                    }

                    await batches.UpdateStatusAsync(
                        batch.Id,
                        BatchStatuses.Expired,
                        DateTimeOffset.UtcNow,
                        batch.OutputFileId,
                        batch.ErrorFileId,
                        CancellationToken.None).ConfigureAwait(false);

                }
                else
                {

                    await batches.UpdateStatusAsync(
                        batch.Id,
                        BatchStatuses.Cancelled,
                        DateTimeOffset.UtcNow,
                        null,
                        null,
                        CancellationToken.None).ConfigureAwait(false);

                }

            }
            catch (Exception markTerminalEx)
            {

                logger.LogError(
                    markTerminalEx,
                    "Failed to mark batch {BatchId} as {Status} after cancel.",
                    batch.Id,
                    expired ? BatchStatuses.Expired : BatchStatuses.Cancelled);

            }

        }
        catch (Exception ex)
        {

            logger.LogError(ex, "Batch {BatchId} processing failed unexpectedly.", batch.Id);

            try
            {

                using IServiceScope scope = scopeFactory.CreateScope();

                IBatchRepository batches = scope.ServiceProvider.GetRequiredService<IBatchRepository>();

                await batches.UpdateStatusAsync(batch.Id, BatchStatuses.Failed, DateTimeOffset.UtcNow, null, null, CancellationToken.None).ConfigureAwait(false);

            }
            catch (Exception markFailedEx)
            {

                logger.LogError(markFailedEx, "Failed to mark batch {BatchId} as failed after a processing exception.", batch.Id);

            }

        }
        finally
        {

            _ = _inFlight.TryRemove(batch.Id, out _);

            _ = _expiryRequested.TryRemove(batch.Id, out _);

            if (_inFlightCancellation.TryRemove(batch.Id, out CancellationTokenSource? cts))
            {

                cts.Dispose();

            }

        }

    }

    internal async Task ProcessBatchAsync(BatchRecord batch, CancellationToken stoppingToken)
    {

        using IServiceScope scope = scopeFactory.CreateScope();

        IBatchRepository batches = scope.ServiceProvider.GetRequiredService<IBatchRepository>();

        IUploadedFileRepository files = scope.ServiceProvider.GetRequiredService<IUploadedFileRepository>();

        IEncryptedBlobStore blobStore = scope.ServiceProvider.GetRequiredService<IEncryptedBlobStore>();

        IArcanumIntelligenceProvider intelligence = scope.ServiceProvider.GetRequiredService<IArcanumIntelligenceProvider>();

        await batches.UpdateStatusAsync(batch.Id, BatchStatuses.InProgress, null, batch.OutputFileId, batch.ErrorFileId, stoppingToken).ConfigureAwait(false);

        string inputPath = UploadedFileStorage.ResolvePath(batch.InputFileId);

        if (!File.Exists(inputPath))
        {

            await batches.UpdateStatusAsync(batch.Id, BatchStatuses.Failed, DateTimeOffset.UtcNow, null, null, CancellationToken.None).ConfigureAwait(false);

            return;

        }

        UploadedFileRecord? inputFile = await files
            .GetByIdAsync(batch.InputFileId, stoppingToken)
            .ConfigureAwait(false);
        if (inputFile is null)
        {
            await batches.UpdateStatusAsync(
                    batch.Id,
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

        int maxRequests = ArcanumSettingClamps.BatchesMaxRequestsPerBatch(batchesSettings.MaxRequestsPerBatch);

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

            int outputLineCount;

            int errorLineCount;

            bool cancelledMidway;

            EncryptedBlobDescriptor? outputDescriptor = null;

            EncryptedBlobDescriptor? errorDescriptor = null;

            {

                List<PreparedBatchRequestLine> requestLines = await CollectRequestLinesAsync(
                    inputPath,
                    blobStore,
                    inputFile.EncryptionVersion,
                    maxRequests,
                    stoppingToken).ConfigureAwait(false);

                ITurnRunWriter? turnRunWriter = scope.ServiceProvider.GetService<ITurnRunWriter>();

                IBudgetReservationService? budgetReservations =
                    scope.ServiceProvider.GetService<IBudgetReservationService>();

                Result<TurnAccountingHandle> batchAccountingBegin = await TurnAccountingHandle.BeginBatchAsync(
                        turnRunWriter,
                        budgetReservations,
                        settings.ResolvePricing(),
                        requestLines
                            .Where(static line => line.Request?.Body is not null)
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
                        "Batch {BatchId} could not reserve budget ({Code}); marking failed.",
                        batch.Id,
                        batchAccountingBegin.Error.Code);

                    await batches.UpdateStatusAsync(
                            batch.Id,
                            BatchStatuses.Failed,
                            DateTimeOffset.UtcNow,
                            null,
                            null,
                            CancellationToken.None)
                        .ConfigureAwait(false);

                    return;
                }

                TurnAccountingHandle batchAccounting = batchAccountingBegin.Value;
                InferenceRunStatus batchRunStatus = InferenceRunStatus.Completed;

                try
                {
                    using (TurnAccountingAmbient.Push(batchAccounting, turnRunWriter))
                    {
                        try
                        {
                            await using BatchJsonlWriters writers = await BatchJsonlWriters
                                .CreateAsync(
                                    blobStore,
                                    outputTempPath,
                                    errorTempPath,
                                    batch.Id,
                                    stoppingToken)
                                .ConfigureAwait(false);

                            cancelledMidway = await RunRequestLinesAsync(
                                requestLines,
                                maxConcurrentRequests,
                                batch.Id,
                                batches,
                                intelligence,
                                settings,
                                writers,
                                batchAccounting,
                                turnRunWriter,
                                stoppingToken).ConfigureAwait(false);

                            outputLineCount = writers.OutputLineCount;

                            errorLineCount = writers.ErrorLineCount;

                            (outputDescriptor, errorDescriptor) = await writers
                                .CompleteAsync(CancellationToken.None)
                                .ConfigureAwait(false);

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

            }

            Guid? outputFileId = outputLineCount > 0
                ? await FinalizeResultFileAsync(
                        outputTempPath,
                        "batch_output.jsonl",
                        "batch_output",
                        files,
                        blobStore,
                        outputDescriptor!,
                        CancellationToken.None)
                    .ConfigureAwait(false)
                : null;

            Guid? errorFileId = errorLineCount > 0
                ? await FinalizeResultFileAsync(
                        errorTempPath,
                        "batch_errors.jsonl",
                        "error",
                        files,
                        blobStore,
                        errorDescriptor!,
                        CancellationToken.None)
                    .ConfigureAwait(false)
                : null;

            // Temps were moved (or never written); clear so the finally block does not delete finals.
            if (outputFileId is not null)
            {

                outputTempPath = string.Empty;

            }

            if (errorFileId is not null)
            {

                errorTempPath = string.Empty;

            }

            string finalStatus = cancelledMidway ? BatchStatuses.Cancelled : BatchStatuses.Completed;

            await batches.UpdateStatusAsync(batch.Id, finalStatus, DateTimeOffset.UtcNow, outputFileId, errorFileId, CancellationToken.None).ConfigureAwait(false);

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

    /// <summary>
    /// Materializes non-empty JSONL request lines up to <paramref name="maxRequests"/> so the
    /// batch can reserve budget once for the full line count.
    /// </summary>
    private static async Task<List<PreparedBatchRequestLine>> CollectRequestLinesAsync(
        string inputPath,
        IEncryptedBlobStore blobStore,
        int encryptionVersion,
        int maxRequests,
        CancellationToken cancellationToken)
    {
        List<PreparedBatchRequestLine> lines = [];

        await foreach ((int Line, string Text) item in EnumerateRequestLinesAsync(
                           inputPath,
                           blobStore,
                           encryptionVersion,
                           maxRequests,
                           cancellationToken)
            .ConfigureAwait(false))
        {
            try
            {
                BatchJsonlRequestLine? request = JsonSerializer.Deserialize(
                    item.Text,
                    ArcanumJsonContext.Default.BatchJsonlRequestLine);

                lines.Add(request?.Body is null
                    ? new PreparedBatchRequestLine(item.Line, request, "Line did not contain a 'body' object.")
                    : new PreparedBatchRequestLine(item.Line, request, ParseError: null));
            }
            catch (JsonException ex)
            {
                lines.Add(new PreparedBatchRequestLine(item.Line, Request: null, ex.Message));
            }
        }

        return lines;
    }

    /// <summary>
    /// Streams non-empty JSONL request lines from <paramref name="inputPath"/> without loading the
    /// entire file into memory. Stops after <paramref name="maxRequests"/> accepted lines.
    /// </summary>
    private static async IAsyncEnumerable<(int Line, string Text)> EnumerateRequestLinesAsync(
        string inputPath,
        IEncryptedBlobStore blobStore,
        int encryptionVersion,
        int maxRequests,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {

        await using Stream stream = await blobStore
            .OpenCompatibleReadAsync(
                inputPath,
                EncryptedBlobPurpose.UploadedFile,
                encryptionVersion,
                cancellationToken)
            .ConfigureAwait(false);

        using StreamReader reader = new(stream);

        int lineNumber = 0;

        int accepted = 0;

        while (accepted < maxRequests)
        {

            cancellationToken.ThrowIfCancellationRequested();

            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (line is null)
            {

                yield break;

            }

            lineNumber++;

            if (string.IsNullOrWhiteSpace(line))
            {

                continue;

            }

            accepted++;

            yield return (lineNumber, line);

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
    private async Task<bool> RunRequestLinesAsync(
        IReadOnlyList<PreparedBatchRequestLine> requestLines,
        int maxConcurrentRequests,
        Guid batchId,
        IBatchRepository batches,
        IArcanumIntelligenceProvider intelligence,
        ArcanumSettings settings,
        BatchJsonlWriters writers,
        TurnAccountingHandle batchAccounting,
        ITurnRunWriter? turnRunWriter,
        CancellationToken stoppingToken)
    {

        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        Task watcherTask = WatchForCancellationAsync(batchId, batches, linkedCts);

        bool cancelledMidway = false;

        try
        {

            await Parallel.ForEachAsync(
                requestLines,
                new ParallelOptions { MaxDegreeOfParallelism = maxConcurrentRequests, CancellationToken = linkedCts.Token },
                async (item, ct) =>
                {
                    TurnAccountingHandle lineAccounting = batchAccounting.CreateNestedOperationHandle();
                    using (TurnAccountingAmbient.Push(lineAccounting, turnRunWriter))
                    {
                        await ProcessRequestLineAsync(item, intelligence, settings, writers, ct).ConfigureAwait(false);
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

    private static async Task WatchForCancellationAsync(Guid batchId, IBatchRepository batches, CancellationTokenSource linkedCts)
    {

        try
        {

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
        PreparedBatchRequestLine prepared,
        IArcanumIntelligenceProvider intelligence,
        ArcanumSettings settings,
        BatchJsonlWriters writers,
        CancellationToken cancellationToken)
    {

        if (prepared.ParseError is not null || prepared.Request?.Body is null)
        {

            await writers.WriteErrorLineAsync(
                JsonSerializer.Serialize(
                    new BatchJsonlParseError(
                        prepared.Line,
                        prepared.ParseError ?? "Line did not contain a 'body' object."),
                    ArcanumJsonContext.Default.BatchJsonlParseError),
                cancellationToken).ConfigureAwait(false);

            return;

        }

        BatchJsonlRequestLine requestLine = prepared.Request;
        string customId = string.IsNullOrWhiteSpace(requestLine.CustomId) ? $"line-{prepared.Line}" : requestLine.CustomId;

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

        await writers.WriteOutputLineAsync(
            JsonSerializer.Serialize(responseLine, ArcanumJsonContext.Default.BatchJsonlResponseLine),
            cancellationToken).ConfigureAwait(false);

    }

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
            await files.CreateAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteFile(path);
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
