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

        Assert.Equal(1, intelligence.ExecutePromptCallCount);

        string outputPath = UploadedFileStorage.ResolvePath(finished.OutputFileId!.Value);

        _createdFilePaths.Add(outputPath);

        Assert.True(File.Exists(outputPath));

        string outputContent = await File.ReadAllTextAsync(outputPath);

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

        string outputPath = UploadedFileStorage.ResolvePath(finished.OutputFileId!.Value);

        string errorPath = UploadedFileStorage.ResolvePath(finished.ErrorFileId!.Value);

        _createdFilePaths.Add(outputPath);

        _createdFilePaths.Add(errorPath);

        string outputContent = await File.ReadAllTextAsync(outputPath);

        Assert.Contains("req-good", outputContent, StringComparison.Ordinal);

        string errorContent = await File.ReadAllTextAsync(errorPath);

        Assert.Contains("\"line\":1", errorContent, StringComparison.Ordinal);

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

        string outputContent = await File.ReadAllTextAsync(outputPath);

        Assert.Contains("req-fail", outputContent, StringComparison.Ordinal);

        Assert.Contains("Hub.Model", outputContent, StringComparison.Ordinal);

    }

    [SkippableFact]
    public async Task TickAsync_ExpiresOldBatch_AndDeletesItsFiles()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        BatchProcessingService service = CreateService(new FakeIntelligenceProvider());
        int expiryHours = ArcanumSettingClamps.BatchesBatchExpiryHours(
            ArcanumRuntimeDefaults.Batches.BatchExpiryHours);

        Guid inputFileId = await SeedInputFileAsync("{}\n");

        string inputPath = UploadedFileStorage.ResolvePath(inputFileId);

        BatchRecord batch = new(
            Guid.NewGuid(),
            inputFileId,
            "/v1/chat/completions",
            BatchStatuses.Validating,
            DateTimeOffset.UtcNow.AddHours(-(expiryHours + 1)),
            null,
            null,
            null);

        await _batches!.CreateAsync(batch, CancellationToken.None);

        await service.TickAsync(CancellationToken.None);

        BatchRecord? afterTick = await _batches.GetByIdAsync(batch.Id, CancellationToken.None);

        Assert.NotNull(afterTick);

        Assert.Equal(BatchStatuses.Expired, afterTick!.Status);

        Assert.False(File.Exists(inputPath));

    }

    [SkippableFact]
    public async Task TickAsync_InFlightExpiredBatch_CancelsViaProcessor_DoesNotDeleteFromSweep()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        FakeIntelligenceProvider intelligence = new()
        {
            NextText = "slow",
            NextFinishReason = "stop",
            ExecuteGate = gate,
        };

        BatchProcessingService service = CreateService(intelligence);

        Guid inputFileId = await SeedInputFileAsync(
            """{"custom_id":"req-1","method":"POST","url":"/v1/chat/completions","body":{"model":"m","messages":[{"role":"user","content":"hi"}]}}""" + "\n");

        string inputPath = UploadedFileStorage.ResolvePath(inputFileId);

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

        await service.TickAsync(CancellationToken.None);

        Assert.True(await WaitForAsync(() => service.IsBatchInFlight(batch.Id), TimeSpan.FromSeconds(5)));

        Assert.True(File.Exists(inputPath), "Sweep must not delete in-flight input before cancel completes.");

        Assert.True(service.TryRequestExpiryCancel(batch.Id));

        // Processor should observe cancel, mark Expired, and delete files — release the gate so
        // any race where cancel arrives after ExecutePrompt starts still unwinds.
        gate.TrySetResult();

        Assert.True(await WaitForAsync(() => !service.IsBatchInFlight(batch.Id), TimeSpan.FromSeconds(10)));

        BatchRecord? finished = await _batches.GetByIdAsync(batch.Id, CancellationToken.None);

        Assert.NotNull(finished);

        Assert.Equal(BatchStatuses.Expired, finished!.Status);

        Assert.False(File.Exists(inputPath));

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

    private BatchProcessingService CreateService(
        IArcanumIntelligenceProvider intelligence,
        ITurnRunWriter? turnRunWriter = null,
        IBudgetReservationService? budgetReservations = null,
        PricingSettings? pricing = null)
    {
        ArcanumSettings settings = new()
        {
            Execution = new ExecutionSettings
            {
                MaxConcurrentBatches = 3,
                MaxConcurrentRequestsPerBatch = 2,
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

        return new BatchProcessingService(
            root.GetRequiredService<IServiceScopeFactory>(),
            new TestOptionsMonitor<ArcanumSettings>(settings),
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

        public Task<Guid> RecordBillableOperationAsync(
            BillableOperationRecord operation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Guid.NewGuid());
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
            CancellationToken cancellationToken = default,
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
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default,
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

        await File.WriteAllTextAsync(path, jsonlContent);

        _createdFilePaths.Add(path);

        await _files!.CreateAsync(
            new UploadedFileRecord(id, "batch-input.jsonl", jsonlContent.Length, "batch", "application/jsonl", DateTimeOffset.UtcNow),
            CancellationToken.None);

        return id;

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
