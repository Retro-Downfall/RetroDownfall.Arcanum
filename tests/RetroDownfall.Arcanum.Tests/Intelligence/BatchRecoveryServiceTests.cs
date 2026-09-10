using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;
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

    private readonly List<ServiceProvider> _serviceProviders = [];

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
        foreach (ServiceProvider provider in _serviceProviders)
        {
            await provider.DisposeAsync();
        }

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
    public async Task ReconcileStrandedAsync_resumes_durable_recovery_claim_after_restart()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid inputFileId = await SeedInputFileAsync("{}");
        Guid batchId = Guid.NewGuid();

        await _batches!.CreateAsync(
            new BatchRecord(
                batchId,
                inputFileId,
                "/v1/chat/completions",
                BatchStatuses.InProgress,
                DateTimeOffset.UtcNow,
                null,
                null,
                null),
            CancellationToken.None);

        BatchAccountingRecoveryStore accountingRecovery = new(_db!, TimeProvider.System);

        Assert.Equal(
            BatchAccountingRecoveryClaimStatus.Claimed,
            await accountingRecovery.ClaimRecoveryAsync(batchId, CancellationToken.None));

        BatchRecoveryService recovery = CreateRecoveryService();

        await recovery.ReconcileStrandedAsync(CancellationToken.None);

        BatchRecord loaded = Assert.IsType<BatchRecord>(
            await _batches.GetByIdAsync(batchId, CancellationToken.None));

        Assert.Equal(BatchStatuses.Validating, loaded.Status);
        Assert.Equal(
            BatchAccountingRecoveryClaimStatus.NotRecoverable,
            await accountingRecovery.ClaimRecoveryAsync(batchId, CancellationToken.None));
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

        await _files!.CreateAsync(
            new UploadedFileRecord(
                inputFileId,
                "missing-after-create.jsonl",
                2,
                "batch",
                "application/jsonl",
                DateTimeOffset.UtcNow),
            CancellationToken.None);

        Guid batchId = Guid.NewGuid();

        await _batches!.CreateAsync(
            new BatchRecord(batchId, inputFileId, "/v1/chat/completions", BatchStatuses.InProgress, DateTimeOffset.UtcNow, null, null, null),
            CancellationToken.None);

        await _files.DeleteAsync(inputFileId, CancellationToken.None);

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
    public async Task ResetStuckBatchAsync_allows_only_one_local_cleanup_owner()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid inputFileId = await SeedInputFileAsync("{}");
        Guid batchId = Guid.NewGuid();

        await _batches!.CreateAsync(
            new BatchRecord(
                batchId,
                inputFileId,
                "/v1/chat/completions",
                BatchStatuses.InProgress,
                DateTimeOffset.UtcNow,
                null,
                null,
                null),
            CancellationToken.None);

        BlockingBatchAccountingRecovery accountingRecovery = new();
        ConcurrentBag<ScopedDatabaseIdentity> databases = [];
        BatchRecoveryService recovery = CreateRecoveryService(
            context => accountingRecovery.Wrap(
                new BatchAccountingRecoveryStore(context, TimeProvider.System)),
            databases);

        Task<BatchRecoveryResult> first = recovery.ResetStuckBatchAsync(
            batchId,
            CancellationToken.None);

        await accountingRecovery.ClaimEntered.WaitAsync(TimeSpan.FromSeconds(5));

        BatchRecoveryResult second = await recovery.ResetStuckBatchAsync(
            batchId,
            CancellationToken.None);

        Assert.Equal(BatchRecoveryStatus.InFlight, second.Status);
        Assert.Equal(1, accountingRecovery.ClaimCalls);

        accountingRecovery.ReleaseClaim();

        BatchRecoveryResult completed = await first;

        Assert.Equal(BatchRecoveryStatus.Succeeded, completed.Status);
        Assert.Equal(BatchStatuses.Validating, completed.Record!.Status);
        Assert.Equal(1, accountingRecovery.ClaimCalls);

        Assert.Equal(2, databases.Count);
        Assert.Equal(
            databases.Count,
            databases.Select(database => database.Context)
                .Distinct(ReferenceEqualityComparer.Instance)
                .Count());
        Assert.Equal(
            databases.Count,
            databases.Select(database => database.Connection)
                .Distinct(ReferenceEqualityComparer.Instance)
                .Count());
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
    public async Task ResetStuckBatchAsync_failed_claim_does_not_mutate_checkpoints_or_artifacts()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid inputFileId = await SeedInputFileAsync("{}");
        Guid outputFileId = await SeedInputFileAsync("existing output");
        string outputPath = UploadedFileStorage.ResolvePath(outputFileId);
        Guid batchId = Guid.NewGuid();

        await _batches!.CreateAsync(
            new BatchRecord(
                batchId,
                inputFileId,
                "/v1/chat/completions",
                BatchStatuses.InProgress,
                DateTimeOffset.UtcNow,
                null,
                outputFileId,
                null),
            CancellationToken.None);

        Assert.True(await _batches.TryBeginLineAsync(
            batchId,
            lineNumber: 1,
            customId: "existing-line",
            CancellationToken.None));

        BatchRecoveryService recovery = CreateRecoveryService(
            _ => new RejectingBatchAccountingRecoveryStore());

        BatchRecoveryResult result = await recovery.ResetStuckBatchAsync(
            batchId,
            CancellationToken.None);

        Assert.Equal(BatchRecoveryStatus.ConcurrentModification, result.Status);

        BatchLineCheckpoint checkpoint = Assert.Single(await _batches.ListLineCheckpointsAsync(
            batchId,
            firstLine: 1,
            lastLine: 1,
            CancellationToken.None));

        Assert.Equal(BatchLineCheckpointState.Dispatched, checkpoint.State);
        Assert.NotNull(await _files!.GetByIdAsync(outputFileId, CancellationToken.None));
        Assert.True(File.Exists(outputPath));

        BatchRecord unchanged = Assert.IsType<BatchRecord>(
            await _batches.GetByIdAsync(batchId, CancellationToken.None));

        Assert.Equal(outputFileId, unchanged.OutputFileId);
    }

    [SkippableFact]
    public async Task TryCompareAndSetStatusAsync_no_op_when_expected_mismatches()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid batchId = Guid.NewGuid();

        Guid inputFileId = Guid.NewGuid();

        await _files!.CreateAsync(
            new UploadedFileRecord(
                inputFileId,
                "cas-input.jsonl",
                2,
                "batch",
                "application/jsonl",
                DateTimeOffset.UtcNow),
            CancellationToken.None);

        await _batches!.CreateAsync(
            new BatchRecord(batchId, inputFileId, "/v1/chat/completions", BatchStatuses.Validating, DateTimeOffset.UtcNow, null, null, null),
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

    private BatchRecoveryService CreateRecoveryService(
        Func<ArcanumDbContext, IBatchAccountingRecoveryStore>? accountingRecoveryFactory = null,
        ConcurrentBag<ScopedDatabaseIdentity>? scopedDatabases = null)
    {
        ServiceCollection services = new();

        services.AddScoped(_ =>
        {
            ArcanumDbContext context = _fixture.CreateContext(_dbPath);

            scopedDatabases?.Add(new ScopedDatabaseIdentity(
                context,
                context.Database.GetDbConnection()));

            return context;
        });

        services.AddScoped<IBatchRepository, BatchRepository>();

        services.AddScoped<IUploadedFileRepository, UploadedFileRepository>();

        services.AddSingleton(_blobStore);

        services.AddScoped<IBatchAccountingRecoveryStore>(sp =>
        {
            ArcanumDbContext context = sp.GetRequiredService<ArcanumDbContext>();

            return accountingRecoveryFactory?.Invoke(context)
                ?? new BatchAccountingRecoveryStore(context, TimeProvider.System);
        });

        ServiceProvider root = services.BuildServiceProvider();

        _serviceProviders.Add(root);

        BatchProcessingService processing = new(
            root.GetRequiredService<IServiceScopeFactory>(),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
            root,
            new GrimoireConnectionAdmissionGate(TimeProvider.System),
            NullLogger<BatchProcessingService>.Instance);

        return new BatchRecoveryService(
            root.GetRequiredService<IServiceScopeFactory>(),
            processing,
            _blobStore,
            NullLogger<BatchRecoveryService>.Instance);
    }

    private sealed class RejectingBatchAccountingRecoveryStore : IBatchAccountingRecoveryStore
    {
        public Task<BatchAccountingRecoveryClaimStatus> ClaimRecoveryAsync(
            Guid batchId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(BatchAccountingRecoveryClaimStatus.NotRecoverable);

        public Task<bool> TryCompleteRecoveryAsync(
            Guid batchId,
            BatchAccountingRecoveryTarget target,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed record ScopedDatabaseIdentity(
        ArcanumDbContext Context,
        System.Data.Common.DbConnection Connection);

    private sealed class BlockingBatchAccountingRecovery
    {
        private readonly TaskCompletionSource _claimEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _releaseClaim = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private int _claimCalls;

        public Task ClaimEntered => _claimEntered.Task;

        public int ClaimCalls => Volatile.Read(ref _claimCalls);

        public IBatchAccountingRecoveryStore Wrap(IBatchAccountingRecoveryStore inner) =>
            new ScopedStore(this, inner);

        public void ReleaseClaim() => _releaseClaim.TrySetResult();

        private async Task<BatchAccountingRecoveryClaimStatus> ClaimRecoveryAsync(
            IBatchAccountingRecoveryStore inner,
            Guid batchId,
            CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _claimCalls);
            _claimEntered.TrySetResult();
            await _releaseClaim.Task.WaitAsync(cancellationToken);

            return await inner.ClaimRecoveryAsync(batchId, cancellationToken);
        }

        private sealed class ScopedStore(
            BlockingBatchAccountingRecovery owner,
            IBatchAccountingRecoveryStore inner) : IBatchAccountingRecoveryStore
        {
            public Task<BatchAccountingRecoveryClaimStatus> ClaimRecoveryAsync(
                Guid batchId,
                CancellationToken cancellationToken = default) =>
                owner.ClaimRecoveryAsync(inner, batchId, cancellationToken);

            public Task<bool> TryCompleteRecoveryAsync(
                Guid batchId,
                BatchAccountingRecoveryTarget target,
                CancellationToken cancellationToken = default) =>
                inner.TryCompleteRecoveryAsync(batchId, target, cancellationToken);
        }
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
