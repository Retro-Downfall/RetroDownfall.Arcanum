using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// Startup / reset recovery for stranded <see cref="BatchStatuses.InProgress"/> batches
/// (<see cref="IBatchRecoveryService"/>).
/// </summary>
[Collection("ProcessEnvironment")]
[Trait("Category", "Integration")]
public sealed class BatchRecoveryServiceTests : IAsyncLifetime
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

    public BatchRecoveryServiceTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public Task InitializeAsync()
    {

        _testHome = Path.Combine(
            Path.GetTempPath(),
            "arcanum-batch-recovery-tests",
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
    public async Task ReconcileStrandedAsync_with_input_resets_to_validating()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid inputFileId = await SeedInputFileAsync("{}");

        Guid batchId = Guid.NewGuid();

        await _batches!.CreateAsync(
            new BatchRecord(batchId, inputFileId, "/v1/chat/completions", BatchStatuses.InProgress, DateTimeOffset.UtcNow, null, null, null),
            CancellationToken.None);

        BatchRecoveryService recovery = CreateRecoveryService();

        await recovery.ReconcileStrandedAsync(CancellationToken.None);

        BatchRecord? loaded = await _batches.GetByIdAsync(batchId, CancellationToken.None);

        Assert.NotNull(loaded);

        Assert.Equal(BatchStatuses.Validating, loaded!.Status);

        Assert.Null(loaded.CompletedAt);

    }

    [SkippableFact]
    public async Task ReconcileStrandedAsync_without_disk_file_marks_failed()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid inputFileId = Guid.NewGuid();

        await _files!.CreateAsync(
            new UploadedFileRecord(inputFileId, "batch_input.jsonl", 2, "batch", "application/jsonl", DateTimeOffset.UtcNow),
            CancellationToken.None);

        Guid batchId = Guid.NewGuid();

        await _batches!.CreateAsync(
            new BatchRecord(batchId, inputFileId, "/v1/chat/completions", BatchStatuses.InProgress, DateTimeOffset.UtcNow, null, null, null),
            CancellationToken.None);

        BatchRecoveryService recovery = CreateRecoveryService();

        await recovery.ReconcileStrandedAsync(CancellationToken.None);

        BatchRecord? loaded = await _batches.GetByIdAsync(batchId, CancellationToken.None);

        Assert.NotNull(loaded);

        Assert.Equal(BatchStatuses.Failed, loaded!.Status);

        Assert.NotNull(loaded.CompletedAt);

    }

    [SkippableFact]
    public async Task ReconcileStrandedAsync_without_metadata_marks_failed()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid inputFileId = Guid.NewGuid();

        Guid batchId = Guid.NewGuid();

        await _batches!.CreateAsync(
            new BatchRecord(batchId, inputFileId, "/v1/chat/completions", BatchStatuses.InProgress, DateTimeOffset.UtcNow, null, null, null),
            CancellationToken.None);

        BatchRecoveryService recovery = CreateRecoveryService();

        await recovery.ReconcileStrandedAsync(CancellationToken.None);

        BatchRecord? loaded = await _batches.GetByIdAsync(batchId, CancellationToken.None);

        Assert.NotNull(loaded);

        Assert.Equal(BatchStatuses.Failed, loaded!.Status);

    }

    [SkippableFact]
    public async Task ResetStuckBatchAsync_with_input_succeeds()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid inputFileId = await SeedInputFileAsync("{}");

        Guid batchId = Guid.NewGuid();

        await _batches!.CreateAsync(
            new BatchRecord(batchId, inputFileId, "/v1/chat/completions", BatchStatuses.InProgress, DateTimeOffset.UtcNow, null, null, null),
            CancellationToken.None);

        BatchRecoveryService recovery = CreateRecoveryService();

        BatchRecoveryResult result = await recovery.ResetStuckBatchAsync(batchId, CancellationToken.None);

        Assert.Equal(BatchRecoveryStatus.Succeeded, result.Status);

        Assert.Equal(BatchStatuses.Validating, result.Record!.Status);

    }

    [SkippableFact]
    public async Task ResetStuckBatchAsync_validating_returns_not_stuck()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid inputFileId = await SeedInputFileAsync("{}");

        Guid batchId = Guid.NewGuid();

        await _batches!.CreateAsync(
            new BatchRecord(batchId, inputFileId, "/v1/chat/completions", BatchStatuses.Validating, DateTimeOffset.UtcNow, null, null, null),
            CancellationToken.None);

        BatchRecoveryService recovery = CreateRecoveryService();

        BatchRecoveryResult result = await recovery.ResetStuckBatchAsync(batchId, CancellationToken.None);

        Assert.Equal(BatchRecoveryStatus.NotStuck, result.Status);

    }

    [SkippableFact]
    public async Task TryCompareAndSetStatusAsync_no_op_when_expected_mismatches()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid batchId = Guid.NewGuid();

        await _batches!.CreateAsync(
            new BatchRecord(batchId, Guid.NewGuid(), "/v1/chat/completions", BatchStatuses.Validating, DateTimeOffset.UtcNow, null, null, null),
            CancellationToken.None);

        bool cas = await _batches.TryCompareAndSetStatusAsync(
            batchId,
            BatchStatuses.InProgress,
            BatchStatuses.Failed,
            DateTimeOffset.UtcNow,
            null,
            null,
            CancellationToken.None);

        Assert.False(cas);

        BatchRecord? loaded = await _batches.GetByIdAsync(batchId, CancellationToken.None);

        Assert.Equal(BatchStatuses.Validating, loaded!.Status);

    }

    private BatchRecoveryService CreateRecoveryService()
    {

        ServiceCollection services = new();

        services.AddSingleton(_db!);

        services.AddScoped<IBatchRepository, BatchRepository>();

        services.AddScoped<IUploadedFileRepository, UploadedFileRepository>();

        services.AddSingleton(_blobStore);

        ServiceProvider root = services.BuildServiceProvider();

        BatchProcessingService processing = new(
            root.GetRequiredService<IServiceScopeFactory>(),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
            root,
            NullLogger<BatchProcessingService>.Instance);

        return new BatchRecoveryService(
            root.GetRequiredService<IServiceScopeFactory>(),
            processing,
            _blobStore,
            NullLogger<BatchRecoveryService>.Instance);

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
                "batch_input.jsonl",
                plaintext.Length,
                "batch",
                "application/jsonl",
                DateTimeOffset.UtcNow,
                descriptor.Version,
                descriptor.KeyId),
            CancellationToken.None);

        return id;

    }

}
