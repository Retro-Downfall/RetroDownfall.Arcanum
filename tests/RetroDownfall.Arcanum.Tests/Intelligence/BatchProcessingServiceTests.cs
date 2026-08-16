using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// <c>BatchProcessingService</c> — the <c>/v1/batches</c> background JSONL processor (DESIGN.md
/// §11.21). Directly constructs the service (it is <c>internal</c>, visible via
/// <c>InternalsVisibleTo</c>) against a real <see cref="GrimoireFixture"/> database and a
/// <see cref="FakeIntelligenceProvider"/>, matching the pattern used by
/// <c>EntryWeavingServiceTests</c>.
/// </summary>
[Collection("ProcessEnvironment")]
[Trait("Category", "Integration")]
public sealed class BatchProcessingServiceTests : IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private readonly List<string> _createdFilePaths = [];

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    private IBatchRepository? _batches;

    private IUploadedFileRepository? _files;

    private string _testHome = string.Empty;

    private string? _originalDotnetEnvironment;

    private string? _originalAspNetCoreEnvironment;

    private string? _originalTestHome;

    private readonly IEncryptedBlobStore _blobStore = TestEncryptedBlobStore.Create();

    public BatchProcessingServiceTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public Task InitializeAsync()
    {

        _testHome = Path.Combine(
            Path.GetTempPath(),
            "arcanum-batch-processing-tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_testHome);

        _originalDotnetEnvironment = global::System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

        _originalAspNetCoreEnvironment = global::System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        _originalTestHome = global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME");

        global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Testing");

        global::System.Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

        global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", _testHome);

        Assert.StartsWith(
            Path.GetFullPath(_testHome),
            Path.GetFullPath(ArcanumPaths.FilesDirectory),
            StringComparison.Ordinal);

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        _batches = new BatchRepository(_db);

        _files = new UploadedFileRepository(_db);

        return Task.CompletedTask;

    }

    public async Task DisposeAsync()
    {

        if (_db is not null)
        {

            await _db.DisposeAsync();
            SqliteConnection.ClearAllPools();

        }

        if (File.Exists(_dbPath))
        {

            File.Delete(_dbPath);

        }

        foreach (string path in _createdFilePaths)
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

        }

        global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", _originalDotnetEnvironment);

        global::System.Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", _originalAspNetCoreEnvironment);

        global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", _originalTestHome);

        if (Directory.Exists(_testHome))
        {

            Directory.Delete(_testHome, recursive: true);

        }

    }

    [SkippableFact]
    public async Task ProcessBatchAsync_ValidRequestLine_ProducesOutputFileAndMarksCompleted()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FakeIntelligenceProvider intelligence = new() { NextText = "batch response text", NextFinishReason = "stop" };

        BatchProcessingService service = CreateService(intelligence);

        Guid inputFileId = await SeedInputFileAsync(
            """{"custom_id":"req-1","method":"POST","url":"/v1/chat/completions","body":{"model":"m","messages":[{"role":"user","content":"hi"}]}}""" + "\n");

        BatchRecord batch = new(Guid.NewGuid(), inputFileId, "/v1/chat/completions", BatchStatuses.Validating, DateTimeOffset.UtcNow, null, null, null);

        await _batches!.CreateAsync(batch, CancellationToken.None);

        await service.ProcessBatchAsync(batch, CancellationToken.None);

        BatchRecord? finished = await _batches.GetByIdAsync(batch.Id, CancellationToken.None);

        Assert.NotNull(finished);

        Assert.Equal(BatchStatuses.Completed, finished!.Status);

        Assert.NotNull(finished.OutputFileId);

        Assert.Null(finished.ErrorFileId);

        Assert.Equal(1, finished.TotalRequestCount);

        Assert.Equal(1, finished.CompletedRequestCount);

        Assert.Equal(0, finished.FailedRequestCount);

        Assert.Equal(1, intelligence.ExecutePromptCallCount);

        string outputPath = UploadedFileStorage.ResolvePath(finished.OutputFileId!.Value);

        _createdFilePaths.Add(outputPath);

        Assert.True(File.Exists(outputPath));

        Assert.True((await File.ReadAllBytesAsync(outputPath)).AsSpan().StartsWith("ARCABLOB"u8));
        string outputContent = await ReadArtifactTextAsync(outputPath);

        Assert.Contains("req-1", outputContent, StringComparison.Ordinal);

        Assert.Contains("batch response text", outputContent, StringComparison.Ordinal);

    }

    [SkippableFact]
    public async Task ProcessBatchAsync_InvalidJsonLine_RecordsToErrorFile_AndStillProcessesValidLines()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FakeIntelligenceProvider intelligence = new() { NextText = "ok", NextFinishReason = "stop" };

        BatchProcessingService service = CreateService(intelligence);

        string jsonl =
            "{ this is not valid json\n" +
            """{"custom_id":"req-good","method":"POST","url":"/v1/chat/completions","body":{"model":"m","messages":[{"role":"user","content":"hi"}]}}""" + "\n";

        Guid inputFileId = await SeedInputFileAsync(jsonl);

        BatchRecord batch = new(Guid.NewGuid(), inputFileId, "/v1/chat/completions", BatchStatuses.Validating, DateTimeOffset.UtcNow, null, null, null);

        await _batches!.CreateAsync(batch, CancellationToken.None);

        await service.ProcessBatchAsync(batch, CancellationToken.None);

        BatchRecord? finished = await _batches.GetByIdAsync(batch.Id, CancellationToken.None);

        Assert.NotNull(finished);

        Assert.Equal(BatchStatuses.Completed, finished!.Status);

        Assert.NotNull(finished.OutputFileId);

        Assert.NotNull(finished.ErrorFileId);

        Assert.Equal(2, finished.TotalRequestCount);

        Assert.Equal(1, finished.CompletedRequestCount);

        Assert.Equal(1, finished.FailedRequestCount);

        string outputPath = UploadedFileStorage.ResolvePath(finished.OutputFileId!.Value);

        string errorPath = UploadedFileStorage.ResolvePath(finished.ErrorFileId!.Value);

        _createdFilePaths.Add(outputPath);

        _createdFilePaths.Add(errorPath);

        string outputContent = await ReadArtifactTextAsync(outputPath);

        Assert.Contains("req-good", outputContent, StringComparison.Ordinal);

        string errorContent = await ReadArtifactTextAsync(errorPath);

        Assert.Contains("\"line\":1", errorContent, StringComparison.Ordinal);

    }

    /// <summary>
    /// A JSON-parse failure never reaches a provider, so it must never be observable in the
    /// <c>Dispatched</c> state: a crash in that window makes restart recovery seal it in the OUTPUT
    /// file as <c>batch_interrupted_after_dispatch</c> and tell the operator the provider "may have
    /// completed and charged the request" for a line that can never succeed.
    /// </summary>
    [SkippableFact]
    public async Task ProcessBatchAsync_InvalidJsonLine_IsNeverCheckpointedAsDispatched()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FakeIntelligenceProvider intelligence = new() { NextText = "ok", NextFinishReason = "stop" };

        List<long> dispatchedLines = [];

        ServiceCollection services = new();

        services.AddSingleton(_db!);

        services.AddScoped<IBatchRepository>(sp => new DispatchRecordingBatchRepository(
            new BatchRepository(sp.GetRequiredService<ArcanumDbContext>()),
            dispatchedLines));

        services.AddScoped<IUploadedFileRepository, UploadedFileRepository>();

        services.AddSingleton(_blobStore);

        services.AddSingleton<IArcanumIntelligenceProvider>(intelligence);

        services.AddSingleton<IBatchRecoveryService>(_ => new NoOpBatchRecoveryService());

        await using ServiceProvider root = services.BuildServiceProvider();

        BatchProcessingService service = CreateService(root);

        string jsonl =
            "{ this is not valid json\n" +
            """{"custom_id":"req-good","method":"POST","url":"/v1/chat/completions","body":{"model":"m","messages":[{"role":"user","content":"hi"}]}}""" + "\n";

        Guid inputFileId = await SeedInputFileAsync(jsonl);

        BatchRecord batch = new(Guid.NewGuid(), inputFileId, "/v1/chat/completions", BatchStatuses.Validating, DateTimeOffset.UtcNow, null, null, null);

        await _batches!.CreateAsync(batch, CancellationToken.None);

        await service.ProcessBatchAsync(batch, CancellationToken.None);

        BatchRecord? finished = await _batches.GetByIdAsync(batch.Id, CancellationToken.None);

        Assert.NotNull(finished);

        Assert.Equal(BatchStatuses.Completed, finished!.Status);

        Assert.Equal(2, finished.TotalRequestCount);

        Assert.Equal(1, finished.CompletedRequestCount);

        Assert.Equal(1, finished.FailedRequestCount);

        string errorPath = UploadedFileStorage.ResolvePath(finished.ErrorFileId!.Value);

        _createdFilePaths.Add(errorPath);

        _createdFilePaths.Add(UploadedFileStorage.ResolvePath(finished.OutputFileId!.Value));

        Assert.Contains("\"line\":1", await ReadArtifactTextAsync(errorPath), StringComparison.Ordinal);

        Assert.Equal([2L], dispatchedLines);

    }

    /// <summary>
    /// Batches are dispatched with <c>Task.Run</c>. If their handles are not retained,
    /// <c>StopAsync</c> returns the moment the poll loop breaks and the host tears the process down
    /// underneath a worker that is mid-<c>CompleteLineAsync</c> — losing a provider response that
    /// was already received and paid for.
    /// </summary>
    [SkippableFact]
    public async Task StopAsync_DrainsInFlightBatchWorkBeforeReturning()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        FakeIntelligenceProvider intelligence = new()
        {
            NextText = "ok",
            NextFinishReason = "stop",
            ExecuteGate = gate,
            ExecuteEntered = entered,
        };

        BatchProcessingService service = CreateService(intelligence);

        Guid inputFileId = await SeedInputFileAsync(
            """{"custom_id":"draining","method":"POST","url":"/v1/chat/completions","body":{"model":"m","messages":[{"role":"user","content":"hi"}]}}"""
            + "\n");

        BatchRecord batch = new(Guid.NewGuid(), inputFileId, "/v1/chat/completions", BatchStatuses.Validating, DateTimeOffset.UtcNow, null, null, null);

        await _batches!.CreateAsync(batch, CancellationToken.None);

        await service.TickAsync(CancellationToken.None);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Task stopping = service.StopAsync(CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(250));

        Assert.False(stopping.IsCompleted);

        gate.SetResult();

        await stopping.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(service.IsBatchInFlight(batch.Id));

        BatchRecord? finished = await _batches.GetByIdAsync(batch.Id, CancellationToken.None);

        Assert.Equal(BatchStatuses.Completed, finished!.Status);

        _createdFilePaths.Add(UploadedFileStorage.ResolvePath(finished.OutputFileId!.Value));

    }

    [SkippableFact]
    public async Task ProcessBatchAsync_MoreThanOneInternalPage_ProcessesEveryLine()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        const int requestCount = 129;

        FakeIntelligenceProvider intelligence = new()
        {
            NextText = "ok",

            NextFinishReason = "stop",
        };

        BatchProcessingService service = CreateService(intelligence);

        string jsonl = string.Concat(
            Enumerable.Range(1, requestCount).Select(
                static index =>
                    $"{{\"custom_id\":\"req-{index}\",\"method\":\"POST\",\"url\":\"/v1/chat/completions\",\"body\":{{\"model\":\"m\",\"messages\":[{{\"role\":\"user\",\"content\":\"hi\"}}]}}}}\n"));

        Guid inputFileId = await SeedInputFileAsync(jsonl);

        BatchRecord batch = new(
            Guid.NewGuid(),
            inputFileId,
            "/v1/chat/completions",
            BatchStatuses.Validating,
            DateTimeOffset.UtcNow,
            null,
            null,
            null);

        await _batches!.CreateAsync(batch, CancellationToken.None);

        await service.ProcessBatchAsync(batch, CancellationToken.None);

        BatchRecord? finished = await _batches.GetByIdAsync(batch.Id, CancellationToken.None);

        Assert.NotNull(finished);

        Assert.Equal(BatchStatuses.Completed, finished!.Status);

        Assert.Equal(requestCount, intelligence.ExecutePromptCallCount);

        Assert.Equal(requestCount, finished.TotalRequestCount);

        Assert.Equal(requestCount, finished.CompletedRequestCount);

        Assert.Equal(0, finished.FailedRequestCount);

        Assert.NotNull(finished.OutputFileId);

        string outputPath = UploadedFileStorage.ResolvePath(finished.OutputFileId!.Value);

        _createdFilePaths.Add(outputPath);

        string[] outputLines = (await ReadArtifactTextAsync(outputPath))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(requestCount, outputLines.Length);

        Assert.Contains("req-129", outputLines[^1], StringComparison.Ordinal);

    }

    [SkippableFact]

    public async Task ProcessBatchAsync_HostRestart_ResumesAfterDurableLineWithoutReplayingProvider()

    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using CancellationTokenSource stopping = new();

        CancelAfterResponseIntelligenceProvider firstProvider = new(stopping);

        ServiceProvider firstRoot = BuildServiceProvider(firstProvider);

        BatchProcessingService firstService = CreateService(

            firstRoot,

            maxConcurrentRequestsPerBatch: 1);

        Guid inputFileId = await SeedInputFileAsync(

            """{"custom_id":"first","method":"POST","url":"/v1/chat/completions","body":{"model":"m","messages":[{"role":"user","content":"one"}]}}"""

            + "\n"

            + """{"custom_id":"second","method":"POST","url":"/v1/chat/completions","body":{"model":"m","messages":[{"role":"user","content":"two"}]}}"""

            + "\n");

        BatchRecord batch = new(

            Guid.NewGuid(),

            inputFileId,

            "/v1/chat/completions",

            BatchStatuses.Validating,

            DateTimeOffset.UtcNow,

            null,

            null,

            null);

        await _batches!.CreateAsync(batch, CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(

            () => firstService.ProcessBatchAsync(batch, stopping.Token));

        Assert.Equal(1, firstProvider.ExecutePromptCallCount);

        BatchRecoveryService recovery = new(

            firstRoot.GetRequiredService<IServiceScopeFactory>(),

            firstService,

            _blobStore,

            NullLogger<BatchRecoveryService>.Instance);

        await recovery.ReconcileStrandedAsync(CancellationToken.None);

        BatchRecord resumable = Assert.IsType<BatchRecord>(

            await _batches.GetByIdAsync(batch.Id, CancellationToken.None));

        Assert.Equal(BatchStatuses.Validating, resumable.Status);

        FakeIntelligenceProvider resumedProvider = new()

        {

            NextText = "second-pass",

            NextFinishReason = "stop",

        };

        BatchProcessingService resumedService = CreateService(

            resumedProvider,

            maxConcurrentRequestsPerBatch: 1);

        await resumedService.ProcessBatchAsync(resumable, CancellationToken.None);

        Assert.Equal(1, resumedProvider.ExecutePromptCallCount);

        BatchRecord finished = Assert.IsType<BatchRecord>(

            await _batches.GetByIdAsync(batch.Id, CancellationToken.None));

        Assert.Equal(BatchStatuses.Completed, finished.Status);

        Assert.NotNull(finished.OutputFileId);

        string outputPath = UploadedFileStorage.ResolvePath(finished.OutputFileId.Value);

        _createdFilePaths.Add(outputPath);

        string output = await ReadArtifactTextAsync(outputPath);

        Assert.Contains("first-pass", output, StringComparison.Ordinal);

        Assert.Contains("second-pass", output, StringComparison.Ordinal);

    }

    [SkippableFact]

    public async Task ProcessBatchAsync_HostRestart_DoesNotReplayIndeterminateDispatchedLine()

    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid inputFileId = await SeedInputFileAsync(

            """{"custom_id":"uncertain","method":"POST","url":"/v1/chat/completions","body":{"model":"m","messages":[{"role":"user","content":"one"}]}}"""

            + "\n");

        BatchRecord batch = new(

            Guid.NewGuid(),

            inputFileId,

            "/v1/chat/completions",

            BatchStatuses.InProgress,

            DateTimeOffset.UtcNow,

            null,

            null,

            null);

        await _batches!.CreateAsync(batch, CancellationToken.None);

        Assert.True(await _batches.TryBeginLineAsync(

            batch.Id,

            lineNumber: 1,

            customId: "uncertain",

            CancellationToken.None));

        FakeIntelligenceProvider provider = new()

        {

            NextText = "must-not-run",

            NextFinishReason = "stop",

        };

        ServiceProvider root = BuildServiceProvider(provider);

        BatchProcessingService service = CreateService(root);

        BatchRecoveryService recovery = new(

            root.GetRequiredService<IServiceScopeFactory>(),

            service,

            _blobStore,

            NullLogger<BatchRecoveryService>.Instance);

        await recovery.ReconcileStrandedAsync(CancellationToken.None);

        BatchRecord resumable = Assert.IsType<BatchRecord>(

            await _batches.GetByIdAsync(batch.Id, CancellationToken.None));

        await service.ProcessBatchAsync(resumable, CancellationToken.None);

        Assert.Equal(0, provider.ExecutePromptCallCount);

        BatchRecord finished = Assert.IsType<BatchRecord>(

            await _batches.GetByIdAsync(batch.Id, CancellationToken.None));

        Assert.NotNull(finished.OutputFileId);

        string outputPath = UploadedFileStorage.ResolvePath(finished.OutputFileId.Value);

        _createdFilePaths.Add(outputPath);

        string output = await ReadArtifactTextAsync(outputPath);

        Assert.Contains("uncertain", output, StringComparison.Ordinal);

        Assert.Contains("batch_interrupted_after_dispatch", output, StringComparison.Ordinal);

    }

    [SkippableFact]

    public async Task ProcessBatchAsync_CancelledBeforeClaim_LeavesBatchCancelledAndDispatchesNothing()

    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FakeIntelligenceProvider provider = new()

        {

            NextText = "must-never-run",

            NextFinishReason = "stop",

        };

        BatchProcessingService service = CreateService(

            provider,

            maxConcurrentRequestsPerBatch: 1);

        Guid inputFileId = await SeedInputFileAsync(

            """{"custom_id":"cancelled-before-claim","method":"POST","url":"/v1/chat/completions","body":{"model":"m","messages":[{"role":"user","content":"one"}]}}"""

            + "\n");

        BatchRecord batch = new(

            Guid.NewGuid(),

            inputFileId,

            "/v1/chat/completions",

            BatchStatuses.Validating,

            DateTimeOffset.UtcNow,

            null,

            null,

            null);

        await _batches!.CreateAsync(batch, CancellationToken.None);

        DateTimeOffset cancelledAt = DateTimeOffset.UtcNow;

        Assert.True(await _batches.TryCompareAndSetStatusAsync(

            batch.Id,

            BatchStatuses.Validating,

            BatchStatuses.Cancelled,

            cancelledAt,

            outputFileId: null,

            errorFileId: null,

            CancellationToken.None));

        await service.ProcessBatchAsync(batch, CancellationToken.None);

        BatchRecord finished = Assert.IsType<BatchRecord>(

            await _batches.GetByIdAsync(batch.Id, CancellationToken.None));

        Assert.Equal(BatchStatuses.Cancelled, finished.Status);

        Assert.NotNull(finished.CompletedAt);

        Assert.Equal(0, provider.ExecutePromptCallCount);

    }

    [SkippableFact]

    public async Task ProcessBatchAsync_CancelRace_PreservesCancelledStatusAndCompletedLineOutput()

    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        TaskCompletionSource providerGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        FakeIntelligenceProvider provider = new()

        {

            NextText = "completed-before-cancel-observed",

            NextFinishReason = "stop",

            ExecuteGate = providerGate,

        };

        BatchProcessingService service = CreateService(

            provider,

            maxConcurrentRequestsPerBatch: 1);

        Guid inputFileId = await SeedInputFileAsync(

            """{"custom_id":"cancel-race","method":"POST","url":"/v1/chat/completions","body":{"model":"m","messages":[{"role":"user","content":"one"}]}}"""

            + "\n");

        BatchRecord batch = new(

            Guid.NewGuid(),

            inputFileId,

            "/v1/chat/completions",

            BatchStatuses.Validating,

            DateTimeOffset.UtcNow,

            null,

            null,

            null);

        await _batches!.CreateAsync(batch, CancellationToken.None);

        Task processing = service.ProcessBatchAsync(batch, CancellationToken.None);

        Assert.True(await WaitForAsync(

            () => provider.ExecutePromptCallCount == 1,

            TimeSpan.FromSeconds(5)));

        await _batches.UpdateStatusAsync(

            batch.Id,

            BatchStatuses.Cancelled,

            DateTimeOffset.UtcNow,

            outputFileId: null,

            errorFileId: null,

            CancellationToken.None);

        providerGate.TrySetResult();

        await processing;

        BatchRecord finished = Assert.IsType<BatchRecord>(

            await _batches.GetByIdAsync(batch.Id, CancellationToken.None));

        Assert.Equal(BatchStatuses.Cancelled, finished.Status);

        Assert.NotNull(finished.OutputFileId);

        string outputPath = UploadedFileStorage.ResolvePath(finished.OutputFileId.Value);

        _createdFilePaths.Add(outputPath);

        string output = await ReadArtifactTextAsync(outputPath);

        Assert.Contains("completed-before-cancel-observed", output, StringComparison.Ordinal);

    }

    [SkippableFact]

    public async Task ProcessBatchAsync_CancelDuringProviderCall_SealsClaimedLineBeforeCheckpointCleanup()

    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        TaskCompletionSource providerGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        FakeIntelligenceProvider provider = new()

        {

            ExecuteGate = providerGate,

        };

        BatchProcessingService service = CreateService(

            provider,

            maxConcurrentRequestsPerBatch: 1);

        Guid inputFileId = await SeedInputFileAsync(

            """{"custom_id":"cancelled-in-flight","method":"POST","url":"/v1/chat/completions","body":{"model":"m","messages":[{"role":"user","content":"one"}]}}"""

            + "\n");

        BatchRecord batch = new(

            Guid.NewGuid(),

            inputFileId,

            "/v1/chat/completions",

            BatchStatuses.Validating,

            DateTimeOffset.UtcNow,

            null,

            null,

            null);

        await _batches!.CreateAsync(batch, CancellationToken.None);

        Task processing = service.ProcessBatchAsync(batch, CancellationToken.None);

        Assert.True(await WaitForAsync(

            () => provider.ExecutePromptCallCount == 1,

            TimeSpan.FromSeconds(5)));

        await _batches.UpdateStatusAsync(

            batch.Id,

            BatchStatuses.Cancelled,

            DateTimeOffset.UtcNow,

            outputFileId: null,

            errorFileId: null,

            CancellationToken.None);

        await processing.WaitAsync(TimeSpan.FromSeconds(10));

        BatchRecord finished = Assert.IsType<BatchRecord>(

            await _batches.GetByIdAsync(batch.Id, CancellationToken.None));

        Assert.Equal(BatchStatuses.Cancelled, finished.Status);

        Assert.Equal(1, finished.TotalRequestCount);

        Assert.Equal(0, finished.CompletedRequestCount);

        Assert.Equal(1, finished.FailedRequestCount);

        Assert.NotNull(finished.OutputFileId);

        string outputPath = UploadedFileStorage.ResolvePath(finished.OutputFileId.Value);

        _createdFilePaths.Add(outputPath);

        string output = await ReadArtifactTextAsync(outputPath);

        Assert.Contains("cancelled-in-flight", output, StringComparison.Ordinal);

        Assert.Contains("batch_interrupted_after_dispatch", output, StringComparison.Ordinal);

        Assert.Empty(await _batches.ListLineCheckpointsAsync(

            batch.Id,

            1,

            1,

            CancellationToken.None));

    }

    [SkippableFact]

    public async Task TickAsync_UnexpectedProviderException_LeavesClaimedLineRecoverable()

    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ThrowingIntelligenceProvider firstProvider = new();

        ServiceProvider firstRoot = BuildServiceProvider(firstProvider);

        BatchProcessingService firstService = CreateService(

            firstRoot,

            maxConcurrentRequestsPerBatch: 1);

        Guid inputFileId = await SeedInputFileAsync(

            """{"custom_id":"unexpected-failure","method":"POST","url":"/v1/chat/completions","body":{"model":"m","messages":[{"role":"user","content":"one"}]}}"""

            + "\n");

        BatchRecord batch = new(

            Guid.NewGuid(),

            inputFileId,

            "/v1/chat/completions",

            BatchStatuses.Validating,

            DateTimeOffset.UtcNow,

            null,

            null,

            null);

        await _batches!.CreateAsync(batch, CancellationToken.None);

        await firstService.TickAsync(CancellationToken.None);

        Assert.True(await WaitForAsync(

            () => firstService.IsBatchInFlight(batch.Id),

            TimeSpan.FromSeconds(5)));

        Assert.True(await WaitForAsync(

            () => !firstService.IsBatchInFlight(batch.Id),

            TimeSpan.FromSeconds(10)));

        BatchRecord stranded = Assert.IsType<BatchRecord>(

            await _batches.GetByIdAsync(batch.Id, CancellationToken.None));

        Assert.Equal(BatchStatuses.InProgress, stranded.Status);

        BatchLineCheckpoint checkpoint = Assert.Single(await _batches.ListLineCheckpointsAsync(

            batch.Id,

            1,

            1,

            CancellationToken.None));

        Assert.Equal(BatchLineCheckpointState.Dispatched, checkpoint.State);

        BatchRecoveryService recovery = new(

            firstRoot.GetRequiredService<IServiceScopeFactory>(),

            firstService,

            _blobStore,

            NullLogger<BatchRecoveryService>.Instance);

        await recovery.ReconcileStrandedAsync(CancellationToken.None);

        BatchRecord resumable = Assert.IsType<BatchRecord>(

            await _batches.GetByIdAsync(batch.Id, CancellationToken.None));

        Assert.Equal(BatchStatuses.Validating, resumable.Status);

        FakeIntelligenceProvider resumedProvider = new();

        BatchProcessingService resumedService = CreateService(

            resumedProvider,

            maxConcurrentRequestsPerBatch: 1);

        await resumedService.ProcessBatchAsync(resumable, CancellationToken.None);

        Assert.Equal(0, resumedProvider.ExecutePromptCallCount);

        BatchRecord finished = Assert.IsType<BatchRecord>(

            await _batches.GetByIdAsync(batch.Id, CancellationToken.None));

        Assert.Equal(BatchStatuses.Completed, finished.Status);

        Assert.Equal(1, finished.TotalRequestCount);

        Assert.Equal(0, finished.CompletedRequestCount);

        Assert.Equal(1, finished.FailedRequestCount);

        Assert.NotNull(finished.OutputFileId);

        string outputPath = UploadedFileStorage.ResolvePath(finished.OutputFileId.Value);

        _createdFilePaths.Add(outputPath);

        string output = await ReadArtifactTextAsync(outputPath);

        Assert.Contains("unexpected-failure", output, StringComparison.Ordinal);

        Assert.Contains("batch_interrupted_after_dispatch", output, StringComparison.Ordinal);

    }

    [SkippableFact]
    public async Task ProcessBatchAsync_ReservesValidLinesUsingResolvedPricingAndTypedBudgets()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        RecordingTurnRunWriter writer = new();
        RecordingBudgetReservationService reservations = new();
        PricingSettings pricing = new()
        {
            DefaultPricing = new ModelPricingEntry { InputPer1M = 1m, OutputPer1M = 2m },
            ModelPricing =
            {
                ["reasoner"] = new ModelPricingEntry
                {
                    InputPer1M = 3m,
                    OutputPer1M = 4m,
                    ReasoningPer1M = 8m,
                },
            },
        };
        BatchProcessingService service = CreateService(
            new FakeIntelligenceProvider(),
            turnRunWriter: writer,
            budgetReservations: reservations,
            pricing: pricing);
        string jsonl =
            "{ not-json\n"
            + """{"custom_id":"reasoning","method":"POST","url":"/v1/chat/completions","body":{"model":"reasoner","max_completion_tokens":1000,"reasoning_budget":600,"messages":[{"role":"user","content":"hi"}]}}"""
            + "\n"
            + """{"custom_id":"legacy","method":"POST","url":"/v1/chat/completions","body":{"model":"other","max_tokens":200,"messages":[{"role":"user","content":"hi"}]}}"""
            + "\n";
        Guid inputFileId = await SeedInputFileAsync(jsonl);
        BatchRecord batch = new(
            Guid.NewGuid(),
            inputFileId,
            "/v1/chat/completions",
            BatchStatuses.Validating,
            DateTimeOffset.UtcNow,
            null,
            null,
            null);
        await _batches!.CreateAsync(batch, CancellationToken.None);

        await service.ProcessBatchAsync(batch, CancellationToken.None);

        decimal expected =
            BudgetReservationService.EstimateWorstCaseBatchLineUsd(
                pricing.ModelPricing["reasoner"],
                maxOutputTokens: 1_000,
                reasoningBudgetTokens: 600)
            + BudgetReservationService.EstimateWorstCaseBatchLineUsd(
                pricing.DefaultPricing,
                maxOutputTokens: 200,
                reasoningBudgetTokens: null);
        Assert.Equal(expected, reservations.LastRequest?.ReservedUsd);
    }

    [SkippableFact]
    public async Task ProcessBatchAsync_ConcurrentLines_SerializesSharedSqliteAccountingWriter()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        AccountingFakeIntelligenceProvider intelligence = new(expectedConcurrentCalls: 2);
        ConcurrentCallDetectingTurnRunWriter writer = new(new TurnRunWriter(_db!));
        BatchProcessingService service = CreateService(intelligence, turnRunWriter: writer);
        string jsonl =
            """{"custom_id":"first","method":"POST","url":"/v1/chat/completions","body":{"model":"m","messages":[{"role":"user","content":"one"}]}}"""
            + "\n"
            + """{"custom_id":"second","method":"POST","url":"/v1/chat/completions","body":{"model":"m","messages":[{"role":"user","content":"two"}]}}"""
            + "\n";
        Guid inputFileId = await SeedInputFileAsync(jsonl);
        Guid batchId = Guid.NewGuid();
        BatchRecord batch = new(
            batchId,
            inputFileId,
            "/v1/chat/completions",
            BatchStatuses.Validating,
            DateTimeOffset.UtcNow,
            null,
            null,
            null);
        await _batches!.CreateAsync(batch, CancellationToken.None);

        await service.ProcessBatchAsync(batch, CancellationToken.None);

        Assert.False(writer.ConcurrentRecordDetected);
        Assert.Equal(2, await CountBillableOperationsAsync($"batch-{batchId:N}"));
    }

    [SkippableFact]
    public async Task ProcessBatchAsync_IntelligenceFailure_RecordsErrorInOutputFile_NotErrorFile()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FakeIntelligenceProvider intelligence = new()
        {
            NextFailure = new RetroDownfall.Arcanum.Core.Primitives.Error("Hub.Model", "model not found"),
        };

        BatchProcessingService service = CreateService(intelligence);

        Guid inputFileId = await SeedInputFileAsync(
            """{"custom_id":"req-fail","method":"POST","url":"/v1/chat/completions","body":{"model":"m","messages":[{"role":"user","content":"hi"}]}}""" + "\n");

        BatchRecord batch = new(Guid.NewGuid(), inputFileId, "/v1/chat/completions", BatchStatuses.Validating, DateTimeOffset.UtcNow, null, null, null);

        await _batches!.CreateAsync(batch, CancellationToken.None);

        await service.ProcessBatchAsync(batch, CancellationToken.None);

        BatchRecord? finished = await _batches.GetByIdAsync(batch.Id, CancellationToken.None);

        Assert.NotNull(finished);

        Assert.Equal(BatchStatuses.Completed, finished!.Status);

        Assert.NotNull(finished.OutputFileId);

        // Execution failures are recorded per-line in the output file's `error` field — they are NOT
        // JSON parse failures, so they never go to the error file.
        Assert.Null(finished.ErrorFileId);

        string outputPath = UploadedFileStorage.ResolvePath(finished.OutputFileId!.Value);

        _createdFilePaths.Add(outputPath);

        string outputContent = await ReadArtifactTextAsync(outputPath);

        Assert.Contains("req-fail", outputContent, StringComparison.Ordinal);

        Assert.Contains("Hub.Model", outputContent, StringComparison.Ordinal);

    }

    [SkippableFact]
    public async Task TickAsync_OldProgressingBatch_IsNotCancelledByWallClockAge()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        FakeIntelligenceProvider intelligence = new()
        {
            NextText = "completed after waiting",

            NextFinishReason = "stop",

            ExecuteGate = gate,
        };

        BatchProcessingService service = CreateService(intelligence);

        Guid inputFileId = await SeedInputFileAsync(
            """{"custom_id":"old","method":"POST","url":"/v1/chat/completions","body":{"model":"m","messages":[{"role":"user","content":"hi"}]}}""" + "\n");

        string inputPath = UploadedFileStorage.ResolvePath(inputFileId);

        BatchRecord batch = new(
            Guid.NewGuid(),
            inputFileId,
            "/v1/chat/completions",
            BatchStatuses.Validating,
            DateTimeOffset.UtcNow.AddDays(-30),
            null,
            null,
            null);

        await _batches!.CreateAsync(batch, CancellationToken.None);

        await service.TickAsync(CancellationToken.None);

        Assert.True(await WaitForAsync(() => service.IsBatchInFlight(batch.Id), TimeSpan.FromSeconds(5)));

        await service.TickAsync(CancellationToken.None);

        Assert.True(service.IsBatchInFlight(batch.Id));

        Assert.True(File.Exists(inputPath));

        gate.TrySetResult();

        Assert.True(await WaitForAsync(() => !service.IsBatchInFlight(batch.Id), TimeSpan.FromSeconds(10)));

        BatchRecord? finished = await _batches.GetByIdAsync(batch.Id, CancellationToken.None);

        Assert.NotNull(finished);

        Assert.Equal(BatchStatuses.Completed, finished!.Status);

        Assert.True(File.Exists(inputPath));

    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {

            if (condition())
            {

                return true;

            }

            await Task.Delay(25).ConfigureAwait(false);

        }

        return condition();

    }

    /// <summary>
    /// ProcessBatchAsync created ONE scope and shared its <c>IBatchRepository</c> — and therefore the
    /// one <c>SqliteConnection</c> owned by that scope's DbContext — between the fire-and-forget
    /// cancellation watcher and every parallel line worker. <c>IBatchRepository</c> issues raw
    /// DbCommands, so EF's concurrency detector never fires, and SqliteBusyRetry only retries
    /// BUSY/LOCKED; it does not serialize. Each unit of concurrent work must own its scope.
    /// </summary>
    [SkippableFact]
    public async Task ProcessBatchAsync_gives_the_watcher_and_every_request_line_its_own_scope()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FakeIntelligenceProvider intelligence = new() { NextText = "ok", NextFinishReason = "stop" };

        ServiceProvider root = BuildServiceProvider(intelligence);

        CountingScopeFactory scopes = new(root.GetRequiredService<IServiceScopeFactory>());

        ArcanumSettings settings = new()
        {
            Execution = new ExecutionSettings
            {
                MaxConcurrentBatches = 3,
                MaxConcurrentRequestsPerBatch = 4,
            },
            Providers =
            [
                new ProviderSettings
                {
                    Name = "test",
                    Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "http://localhost",
                    Models = [new ModelEntry("m")],
                },
            ],
        };

        BatchProcessingService service = new(
            scopes,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            root,
            NullLogger<BatchProcessingService>.Instance);

        const int lineCount = 4;

        const string lineTemplate =
            """{"custom_id":"req-#","method":"POST","url":"/v1/chat/completions","body":{"model":"m","messages":[{"role":"user","content":"hi"}]}}""";

        string jsonl = string.Concat(
            Enumerable.Range(1, lineCount).Select(i =>
                lineTemplate.Replace("#", i.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) + "\n"));

        Guid inputFileId = await SeedInputFileAsync(jsonl);

        BatchRecord batch = new(Guid.NewGuid(), inputFileId, "/v1/chat/completions", BatchStatuses.Validating, DateTimeOffset.UtcNow, null, null, null);

        await _batches!.CreateAsync(batch, CancellationToken.None);

        int scopesBefore = scopes.Created;

        await service.ProcessBatchAsync(batch, CancellationToken.None);

        BatchRecord? finished = await _batches.GetByIdAsync(batch.Id, CancellationToken.None);

        Assert.Equal(BatchStatuses.Completed, finished!.Status);

        if (finished.OutputFileId is Guid outputFileId)
        {

            _createdFilePaths.Add(UploadedFileStorage.ResolvePath(outputFileId));

        }

        // One outer scope, one for the cancellation watcher, and one per request line.
        Assert.True(
            scopes.Created - scopesBefore >= lineCount + 2,
            $"expected at least {lineCount + 2} scopes, saw {scopes.Created - scopesBefore}");

    }

    private sealed class CountingScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
    {

        private int _created;

        public int Created => Volatile.Read(ref _created);

        public IServiceScope CreateScope()
        {

            _ = Interlocked.Increment(ref _created);

            return inner.CreateScope();

        }

    }

    private BatchProcessingService CreateService(
        IArcanumIntelligenceProvider intelligence,
        ITurnRunWriter? turnRunWriter = null,
        IBudgetReservationService? budgetReservations = null,
        PricingSettings? pricing = null,
        int maxConcurrentRequestsPerBatch = 2)
    {
        ArcanumSettings settings = new()
        {
            Execution = new ExecutionSettings
            {
                MaxConcurrentBatches = 3,
                MaxConcurrentRequestsPerBatch = maxConcurrentRequestsPerBatch,
            },
            Providers =
            [
                new ProviderSettings
                {
                    Name = "test",
                    Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "http://localhost",
                    Models =
                    [
                        new ModelEntry("m"),
                        new ModelEntry("missing"),
                        new ModelEntry("reasoner"),
                        new ModelEntry("other"),
                    ],
                },
            ],
            Cost = new CostSettings { Pricing = pricing ?? new PricingSettings() },
        };

        ServiceProvider root = BuildServiceProvider(intelligence, turnRunWriter, budgetReservations);

        return CreateService(root, settings);

    }

    private static BatchProcessingService CreateService(

        ServiceProvider root,

        ArcanumSettings? settings = null,

        int maxConcurrentRequestsPerBatch = 1)

    {

        ArcanumSettings resolvedSettings = settings ?? new ArcanumSettings

        {

            Execution = new ExecutionSettings

            {

                MaxConcurrentBatches = 3,

                MaxConcurrentRequestsPerBatch = maxConcurrentRequestsPerBatch,

            },

            Providers =

            [

                new ProviderSettings

                {

                    Name = "test",

                    Type = AiProviderKind.OpenAICompatible,

                    Endpoint = "http://localhost",

                    Models = [new ModelEntry("m")],

                },

            ],

        };

        return new BatchProcessingService(
            root.GetRequiredService<IServiceScopeFactory>(),
            new TestOptionsMonitor<ArcanumSettings>(resolvedSettings),
            root,
            NullLogger<BatchProcessingService>.Instance);

    }

    private ServiceProvider BuildServiceProvider(
        IArcanumIntelligenceProvider intelligence,
        ITurnRunWriter? turnRunWriter = null,
        IBudgetReservationService? budgetReservations = null)
    {

        ServiceCollection services = new();

        services.AddSingleton(_db!);

        services.AddScoped<IBatchRepository, BatchRepository>();

        services.AddScoped<IUploadedFileRepository, UploadedFileRepository>();

        services.AddSingleton(_blobStore);

        services.AddSingleton(intelligence);

        services.AddSingleton<IBatchRecoveryService>(_ => new NoOpBatchRecoveryService());

        if (turnRunWriter is not null)
        {
            services.AddSingleton(turnRunWriter);
        }

        if (budgetReservations is not null)
        {
            services.AddSingleton(budgetReservations);
        }

        return services.BuildServiceProvider();

    }

    private IServiceScopeFactory BuildScopeFactory(IArcanumIntelligenceProvider intelligence) =>
        BuildServiceProvider(intelligence).GetRequiredService<IServiceScopeFactory>();

    private sealed class RecordingTurnRunWriter : ITurnRunWriter
    {
        public Task<Guid> StartRunAsync(
            InferenceRunStart start,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Guid.NewGuid());

        public Task CompleteRunAsync(
            Guid runId,
            InferenceRunStatus status,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> TryAbandonRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<Guid> RecordBillableOperationAsync(
            BillableOperationRecord operation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Guid.NewGuid());
    }

    private sealed class CancelAfterResponseIntelligenceProvider(

        CancellationTokenSource stopping) : IArcanumIntelligenceProvider

    {

        private int _executePromptCallCount;

        public int ExecutePromptCallCount => Volatile.Read(ref _executePromptCallCount);

        public Task<Result<PromptTurnResult>> ExecutePromptAsync(

            PingRequest request,

            ArcanumInvocationContext invocationContext,

            CancellationToken cancellationToken,

            InferenceAuditContext? auditContext = null)

        {

            _ = Interlocked.Increment(ref _executePromptCallCount);

            stopping.Cancel();

            return Task.FromResult(Result<PromptTurnResult>.Success(

                new PromptTurnResult("first-pass", null, null, "stop")));

        }

        public async IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(

            PingRequest request,

            ArcanumInvocationContext invocationContext,

            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,

            InferenceAuditContext? auditContext = null)

        {

            await Task.CompletedTask.ConfigureAwait(false);

            yield break;

        }

    }

    private sealed class ThrowingIntelligenceProvider : IArcanumIntelligenceProvider

    {

        public Task<Result<PromptTurnResult>> ExecutePromptAsync(

            PingRequest request,

            ArcanumInvocationContext invocationContext,

            CancellationToken cancellationToken,

            InferenceAuditContext? auditContext = null) =>

            throw new InvalidOperationException("Injected unexpected provider failure.");

        public async IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(

            PingRequest request,

            ArcanumInvocationContext invocationContext,

            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,

            InferenceAuditContext? auditContext = null)

        {

            await Task.CompletedTask.ConfigureAwait(false);

            yield break;

        }

    }

    private sealed class ConcurrentCallDetectingTurnRunWriter(ITurnRunWriter inner) : ITurnRunWriter
    {
        private int _activeRecords;

        public bool ConcurrentRecordDetected { get; private set; }

        public Task<Guid> StartRunAsync(
            InferenceRunStart start,
            CancellationToken cancellationToken = default) =>
            inner.StartRunAsync(start, cancellationToken);

        public Task CompleteRunAsync(
            Guid runId,
            InferenceRunStatus status,
            CancellationToken cancellationToken = default) =>
            inner.CompleteRunAsync(runId, status, cancellationToken);

        public Task<bool> TryAbandonRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
            inner.TryAbandonRunAsync(runId, cancellationToken);

        public async Task<Guid> RecordBillableOperationAsync(
            BillableOperationRecord operation,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _activeRecords) != 1)
            {
                ConcurrentRecordDetected = true;
                _ = Interlocked.Decrement(ref _activeRecords);
                throw new InvalidOperationException("Concurrent access to the shared SQLite accounting writer.");
            }

            try
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                return await inner.RecordBillableOperationAsync(operation, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _ = Interlocked.Decrement(ref _activeRecords);
            }
        }
    }

    private sealed class AccountingFakeIntelligenceProvider(int expectedConcurrentCalls)
        : IArcanumIntelligenceProvider
    {
        private readonly TaskCompletionSource _providersReady =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public async Task<Result<PromptTurnResult>> ExecutePromptAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null)
        {
            if (Interlocked.Increment(ref _calls) >= expectedConcurrentCalls)
            {
                _providersReady.TrySetResult();
            }

            await _providersReady.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
                .ConfigureAwait(false);

            TurnAccountingHandle accounting = Assert.IsType<TurnAccountingHandle>(TurnAccountingAmbient.Current);
            ITurnRunWriter writer = Assert.IsAssignableFrom<ITurnRunWriter>(TurnAccountingAmbient.Writer);
            await accounting.RecordUsageAsync(
                    writer,
                    BillableOperationType.Chat,
                    "test-provider",
                    request.Model ?? "m",
                    purpose: "batch-line",
                    inputTokens: 10,
                    outputTokens: 5,
                    cachedTokens: 0,
                    reasoningTokens: 0,
                    new ModelPricingEntry { InputPer1M = 1m, OutputPer1M = 2m },
                    cancellationToken)
                .ConfigureAwait(false);

            return Result<PromptTurnResult>.Success(
                new PromptTurnResult("ok", null, null, "stop"));
        }

        public async IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }
    }

    private sealed class RecordingBudgetReservationService : IBudgetReservationService
    {
        public BudgetReservationRequest? LastRequest { get; private set; }

        public Task<Result<BudgetReservation>> ReserveAsync(
            BudgetReservationRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(Result<BudgetReservation>.Success(new BudgetReservation(
                Guid.NewGuid(),
                request.RunId,
                request.BudgetPeriod,
                request.ReservedUsd,
                0m,
                BudgetReservationStatus.Reserved,
                request.ExpiresAt,
                DateTimeOffset.UtcNow)));
        }

        public Task ReconcileAsync(
            Guid reservationId,
            decimal actualCostUsd,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<Result> AdjustAsync(
            Guid reservationId,
            decimal reservedUsd,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task ReleaseAsync(
            Guid reservationId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<decimal> GetTodayCommittedSpendAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0m);

        public Task<decimal> GetTodayOutstandingReservationsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0m);

        public Task<int> SweepExpiredAsync(
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    /// <summary>
    /// Forwards everything and records which lines were ever opened in the provider-ambiguous
    /// <c>Dispatched</c> state.
    /// </summary>
    private sealed class DispatchRecordingBatchRepository(
        IBatchRepository inner,
        List<long> dispatchedLines) : IBatchRepository
    {

        public Task CreateAsync(BatchRecord record, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(record, cancellationToken);

        public Task<BatchRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.GetByIdAsync(id, cancellationToken);

        public Task<IReadOnlyList<BatchRecord>> ListAsync(string? status, CancellationToken cancellationToken = default) =>
            inner.ListAsync(status, cancellationToken);

        public Task<BatchListPage> ListPageAsync(
            string? status,
            BatchListPosition? after,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            inner.ListPageAsync(status, after, pageSize, cancellationToken);

        public Task<IReadOnlyList<BatchRecord>> ListPendingPageAsync(
            int pageSize,
            CancellationToken cancellationToken = default) =>
            inner.ListPendingPageAsync(pageSize, cancellationToken);

        public Task<IReadOnlyList<BatchRecord>> ListByStatusAsync(string status, CancellationToken cancellationToken = default) =>
            inner.ListByStatusAsync(status, cancellationToken);

        public Task UpdateStatusAsync(
            Guid id,
            string status,
            DateTimeOffset? completedAt,
            Guid? outputFileId,
            Guid? errorFileId,
            CancellationToken cancellationToken = default) =>
            inner.UpdateStatusAsync(id, status, completedAt, outputFileId, errorFileId, cancellationToken);

        public Task<bool> TryCompareAndSetStatusAsync(
            Guid id,
            string expectedStatus,
            string newStatus,
            DateTimeOffset? completedAt,
            Guid? outputFileId,
            Guid? errorFileId,
            CancellationToken cancellationToken = default) =>
            inner.TryCompareAndSetStatusAsync(
                id,
                expectedStatus,
                newStatus,
                completedAt,
                outputFileId,
                errorFileId,
                cancellationToken);

        public Task<IReadOnlyList<BatchLineCheckpoint>> ListLineCheckpointsAsync(
            Guid batchId,
            long firstLine,
            long lastLine,
            CancellationToken cancellationToken = default) =>
            inner.ListLineCheckpointsAsync(batchId, firstLine, lastLine, cancellationToken);

        public Task<IReadOnlyList<BatchLineCheckpoint>> ListLineCheckpointsAsync(
            Guid batchId,
            BatchLineCheckpointState state,
            long afterLine,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            inner.ListLineCheckpointsAsync(batchId, state, afterLine, pageSize, cancellationToken);

        public async Task<bool> TryBeginLineAsync(
            Guid batchId,
            long lineNumber,
            string customId,
            CancellationToken cancellationToken = default)
        {

            bool began = await inner.TryBeginLineAsync(batchId, lineNumber, customId, cancellationToken);

            if (began)
            {

                lock (dispatchedLines)
                {
                    dispatchedLines.Add(lineNumber);
                }

            }

            return began;

        }

        public Task<bool> TryRecordTerminalLineAsync(
            Guid batchId,
            long lineNumber,
            string customId,
            BatchLineOutputKind outputKind,
            BatchRequestOutcome outcome,
            string jsonLine,
            CancellationToken cancellationToken = default) =>
            inner.TryRecordTerminalLineAsync(
                batchId,
                lineNumber,
                customId,
                outputKind,
                outcome,
                jsonLine,
                cancellationToken);

        public Task CompleteLineAsync(
            Guid batchId,
            long lineNumber,
            BatchLineOutputKind outputKind,
            BatchRequestOutcome outcome,
            string jsonLine,
            CancellationToken cancellationToken = default) =>
            inner.CompleteLineAsync(batchId, lineNumber, outputKind, outcome, jsonLine, cancellationToken);

        public Task DeleteLineCheckpointsAsync(Guid batchId, CancellationToken cancellationToken = default) =>
            inner.DeleteLineCheckpointsAsync(batchId, cancellationToken);

    }

    private sealed class NoOpBatchRecoveryService : IBatchRecoveryService
    {

        public Task ReconcileStrandedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<BatchRecoveryResult> ResetStuckBatchAsync(Guid batchId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BatchRecoveryResult(BatchRecoveryStatus.NotFound));

    }

    private async Task<Guid> SeedInputFileAsync(string jsonlContent)
    {

        Guid id = Guid.NewGuid();

        Directory.CreateDirectory(ArcanumPaths.FilesDirectory);

        string path = UploadedFileStorage.ResolvePath(id);

        byte[] plaintext = System.Text.Encoding.UTF8.GetBytes(jsonlContent);
        EncryptedBlobDescriptor descriptor = await _blobStore.WriteAsync(
            path,
            new MemoryStream(plaintext),
            EncryptedBlobPurpose.UploadedFile,
            id.ToByteArray(),
            plaintext.Length);

        _createdFilePaths.Add(path);

        await _files!.CreateAsync(
            new UploadedFileRecord(
                id,
                "batch-input.jsonl",
                plaintext.Length,
                "batch",
                "application/jsonl",
                DateTimeOffset.UtcNow,
                descriptor.Version,
                descriptor.KeyId),
            CancellationToken.None);

        return id;

    }

    private async Task<string> ReadArtifactTextAsync(string path)
    {
        await using Stream plaintext = await _blobStore.OpenReadAsync(
            path,
            EncryptedBlobPurpose.BatchArtifact);
        using StreamReader reader = new(plaintext);
        return await reader.ReadToEndAsync();
    }

    private async Task<int> CountBillableOperationsAsync(string requestId)
    {
        System.Data.Common.DbConnection connection = _db!.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using System.Data.Common.DbCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM "BillableOperations" AS operations
            INNER JOIN "InferenceRuns" AS runs ON runs."Id" = operations."RunId"
            WHERE runs."RequestId" = @requestId
            """;
        System.Data.Common.DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "@requestId";
        parameter.Value = requestId;
        _ = command.Parameters.Add(parameter);

        object? value = await command.ExecuteScalarAsync();
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

}
