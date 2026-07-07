using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
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
/// Background processor for <c>/v1/batches</c> (DESIGN.md §11.21) — modeled on
/// <c>EntryWeavingService</c>'s poll-and-process shape, but lives in the Api project (not
/// Infrastructure) because it needs the <c>/v1</c> OpenAI DTOs and
/// <see cref="OpenAiV1Endpoints.ExecuteChatRequestForBatchAsync"/>, which Infrastructure must not
/// depend on (Api → Infrastructure is the only allowed direction).
/// </summary>
[ExcludeFromCodeCoverage] // Reason: IHostedService background poller; behavior covered via BatchProcessingServiceTests using a directly-constructed instance + fakes.
internal sealed class BatchProcessingService(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<ArcanumSettings> optionsMonitor,
    ILogger<BatchProcessingService> logger) : BackgroundService
{

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan CancelWatchInterval = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();

    // Best-effort early-rejection guard, not a correctness guarantee — there is a race window between
    // this check and the status reset. The real double-processing guard is the worker's
    // _inFlight.TryAdd(batch.Id, 0) before processing (BatchProcessingService.cs:131), which prevents
    // a second worker from picking the same batch up. This endpoint check just gives the operator a
    // clear 409 instead of a confusing reset-while-running.
    public bool IsBatchInFlight(Guid batchId) => _inFlight.ContainsKey(batchId);

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

        BatchesSettings settings = optionsMonitor.CurrentValue.Batches ?? new BatchesSettings();

        int expiryHours = ArcanumSettingClamps.BatchesBatchExpiryHours(settings.BatchExpiryHours);

        int maxConcurrentBatches = ArcanumSettingClamps.BatchesMaxConcurrentBatches(settings.MaxConcurrentBatches);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        foreach (BatchRecord batch in active)
        {

            if (now - batch.CreatedAt > TimeSpan.FromHours(expiryHours))
            {

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

            _ = Task.Run(() => ProcessBatchWithCleanupAsync(batch, stoppingToken), CancellationToken.None);

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

    private async Task ProcessBatchWithCleanupAsync(BatchRecord batch, CancellationToken stoppingToken)
    {

        try
        {

            await ProcessBatchAsync(batch, stoppingToken).ConfigureAwait(false);

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

        }

    }

    internal async Task ProcessBatchAsync(BatchRecord batch, CancellationToken stoppingToken)
    {

        using IServiceScope scope = scopeFactory.CreateScope();

        IBatchRepository batches = scope.ServiceProvider.GetRequiredService<IBatchRepository>();

        IUploadedFileRepository files = scope.ServiceProvider.GetRequiredService<IUploadedFileRepository>();

        IArcanumIntelligenceProvider intelligence = scope.ServiceProvider.GetRequiredService<IArcanumIntelligenceProvider>();

        await batches.UpdateStatusAsync(batch.Id, BatchStatuses.InProgress, null, batch.OutputFileId, batch.ErrorFileId, stoppingToken).ConfigureAwait(false);

        string inputPath = UploadedFileStorage.ResolvePath(batch.InputFileId);

        if (!File.Exists(inputPath))
        {

            await batches.UpdateStatusAsync(batch.Id, BatchStatuses.Failed, DateTimeOffset.UtcNow, null, null, CancellationToken.None).ConfigureAwait(false);

            return;

        }

        string[] lines = await File.ReadAllLinesAsync(inputPath, stoppingToken).ConfigureAwait(false);

        ArcanumSettings settings = optionsMonitor.CurrentValue;

        BatchesSettings batchesSettings = settings.Batches ?? new BatchesSettings();

        int maxRequests = ArcanumSettingClamps.BatchesMaxRequestsPerBatch(batchesSettings.MaxRequestsPerBatch);

        int maxConcurrentRequests = ArcanumSettingClamps.BatchesMaxConcurrentRequestsPerBatch(batchesSettings.MaxConcurrentRequestsPerBatch);

        List<(int Line, string Text)> requestLines = [];

        for (int i = 0; i < lines.Length && requestLines.Count < maxRequests; i++)
        {

            if (!string.IsNullOrWhiteSpace(lines[i]))
            {

                requestLines.Add((i + 1, lines[i]));

            }

        }

        ConcurrentBag<string> outputLines = [];

        ConcurrentBag<string> errorLines = [];

        bool cancelledMidway = await RunRequestLinesAsync(
            requestLines,
            maxConcurrentRequests,
            batch.Id,
            batches,
            intelligence,
            settings,
            outputLines,
            errorLines,
            stoppingToken).ConfigureAwait(false);

        Guid? outputFileId = outputLines.IsEmpty
            ? null
            : await WriteResultFileAsync(outputLines, "batch_output.jsonl", "batch_output", files, stoppingToken).ConfigureAwait(false);

        Guid? errorFileId = errorLines.IsEmpty
            ? null
            : await WriteResultFileAsync(errorLines, "batch_errors.jsonl", "error", files, stoppingToken).ConfigureAwait(false);

        string finalStatus = cancelledMidway ? BatchStatuses.Cancelled : BatchStatuses.Completed;

        await batches.UpdateStatusAsync(batch.Id, finalStatus, DateTimeOffset.UtcNow, outputFileId, errorFileId, CancellationToken.None).ConfigureAwait(false);

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
        List<(int Line, string Text)> requestLines,
        int maxConcurrentRequests,
        Guid batchId,
        IBatchRepository batches,
        IArcanumIntelligenceProvider intelligence,
        ArcanumSettings settings,
        ConcurrentBag<string> outputLines,
        ConcurrentBag<string> errorLines,
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

                    await ProcessRequestLineAsync(item.Line, item.Text, intelligence, settings, outputLines, errorLines, ct).ConfigureAwait(false);

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
        int lineNumber,
        string rawLine,
        IArcanumIntelligenceProvider intelligence,
        ArcanumSettings settings,
        ConcurrentBag<string> outputLines,
        ConcurrentBag<string> errorLines,
        CancellationToken cancellationToken)
    {

        BatchJsonlRequestLine? requestLine;

        try
        {

            requestLine = JsonSerializer.Deserialize(rawLine, ArcanumJsonContext.Default.BatchJsonlRequestLine);

        }
        catch (JsonException ex)
        {

            errorLines.Add(JsonSerializer.Serialize(new BatchJsonlParseError(lineNumber, ex.Message), ArcanumJsonContext.Default.BatchJsonlParseError));

            return;

        }

        if (requestLine is null || requestLine.Body is null)
        {

            errorLines.Add(JsonSerializer.Serialize(
                new BatchJsonlParseError(lineNumber, "Line did not contain a 'body' object."),
                ArcanumJsonContext.Default.BatchJsonlParseError));

            return;

        }

        string customId = string.IsNullOrWhiteSpace(requestLine.CustomId) ? $"line-{lineNumber}" : requestLine.CustomId;

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

        outputLines.Add(JsonSerializer.Serialize(responseLine, ArcanumJsonContext.Default.BatchJsonlResponseLine));

    }

    private static async Task<Guid> WriteResultFileAsync(
        IEnumerable<string> lines,
        string filename,
        string purpose,
        IUploadedFileRepository files,
        CancellationToken cancellationToken)
    {

        Guid id = Guid.NewGuid();

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(ArcanumPaths.FilesDirectory);

        string path = UploadedFileStorage.ResolvePath(id);

        string content = string.Join('\n', lines) + "\n";

        await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);

        SecureFilePermissions.ApplyOwnerOnlyFile(path);

        UploadedFileRecord record = new(id, filename, new FileInfo(path).Length, purpose, "application/jsonl", DateTimeOffset.UtcNow);

        await files.CreateAsync(record, cancellationToken).ConfigureAwait(false);

        return id;

    }

    private static void TryDeleteFile(string path)
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
