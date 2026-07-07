using Microsoft.EntityFrameworkCore;
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
/// <remarks>
/// Writes/deletes only fresh, GUID-named files under the real <c>ArcanumPaths.FilesDirectory</c>
/// (never a fixed/shared name), and removes every file it creates in <see cref="DisposeAsync"/> —
/// <see cref="UploadedFileStorage.ResolvePath"/> is a hardcoded static path with no DI seam to
/// redirect, and overriding the process-wide <c>HOME</c> environment variable here (as
/// <c>ArcanumWebApplicationFactory</c> does) would race against concurrently-running "ApiHost"
/// collection tests that do the same.
/// </remarks>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class BatchProcessingServiceTests : IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private readonly List<string> _createdFilePaths = [];

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    private IBatchRepository? _batches;

    private IUploadedFileRepository? _files;

    public BatchProcessingServiceTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public Task InitializeAsync()
    {

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
    public async Task ProcessBatchAsync_IntelligenceFailure_RecordsErrorInOutputFile_NotErrorFile()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FakeIntelligenceProvider intelligence = new()
        {
            NextFailure = new RetroDownfall.Arcanum.Core.Primitives.Error("Hub.Model", "model not found"),
        };

        BatchProcessingService service = CreateService(intelligence);

        Guid inputFileId = await SeedInputFileAsync(
            """{"custom_id":"req-fail","method":"POST","url":"/v1/chat/completions","body":{"model":"missing","messages":[{"role":"user","content":"hi"}]}}""" + "\n");

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

        BatchProcessingService service = CreateService(new FakeIntelligenceProvider(), batchExpiryHours: 1);

        Guid inputFileId = await SeedInputFileAsync("{}\n");

        string inputPath = UploadedFileStorage.ResolvePath(inputFileId);

        BatchRecord batch = new(
            Guid.NewGuid(),
            inputFileId,
            "/v1/chat/completions",
            BatchStatuses.Validating,
            DateTimeOffset.UtcNow.AddHours(-2),
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

    private BatchProcessingService CreateService(IArcanumIntelligenceProvider intelligence, int? batchExpiryHours = null)
    {

        BatchesSettings batches = new()
        {
            MaxConcurrentBatches = 3,
            MaxRequestsPerBatch = 100,
            MaxConcurrentRequestsPerBatch = 2,
            BatchExpiryHours = batchExpiryHours ?? 24,
        };

        IServiceScopeFactory scopeFactory = BuildScopeFactory(intelligence);

        return new BatchProcessingService(
            scopeFactory,
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings { Batches = batches }),
            NullLogger<BatchProcessingService>.Instance);

    }

    private IServiceScopeFactory BuildScopeFactory(IArcanumIntelligenceProvider intelligence)
    {

        ServiceCollection services = new();

        services.AddSingleton(_db!);

        services.AddScoped<IBatchRepository, BatchRepository>();

        services.AddScoped<IUploadedFileRepository, UploadedFileRepository>();

        services.AddSingleton(intelligence);

        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

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

}
