using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Infrastructure.Storage;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;
using Spectre.Console;
using Spectre.Console.Testing;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("ProcessEnvironment")]
[Trait("Category", "Integration")]
public sealed class BlobEncryptionLifecycleNativeTests : IAsyncLifetime
{
    private readonly string _testHome = Path.Combine(Path.GetTempPath(), "arcanum-blob-native-" + Guid.NewGuid().ToString("N"));
    private readonly string? _oldTestHome = global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME");
    private readonly string? _oldEnvironment = global::System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
    private readonly string _databasePath;

    private readonly ArcanumDbContext _db;

    public BlobEncryptionLifecycleNativeTests(GrimoireFixture fixture)
    {
        _databasePath = fixture.CopyDatabase();
        _db = fixture.CreateContext(_databasePath);
        Directory.CreateDirectory(_testHome);
        global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Testing");
        global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", _testHome);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        try
        {
            SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();
            await _db.DisposeAsync();
            SqliteConnection.ClearPool(connection);
            File.Delete(_databasePath);
            Directory.Delete(_testHome, recursive: true);
        }
        finally
        {
            global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", _oldTestHome);
            global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", _oldEnvironment);
        }
    }

    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Native_pass_processes_healthy_neighbors_and_settles_the_real_ledger(bool rotate, bool corrupt)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        string path = await SeedAsync(corrupt);
        NativeTestKeys keys = new();
        (BlobEncryptionLifecycleService service, LongRunningOperationStore operations, EncryptedBlobStore blobs) = CreateService(keys);

        BlobEncryptionOperationResult result = rotate
            ? await service.RotateKeyAsync(1, long.MaxValue)
            : await service.MigrateAsync(1, long.MaxValue);

        Assert.Equal(1, result.ProcessedFiles);
        Assert.Equal(8, result.ProcessedBytes);
        Assert.Equal(corrupt ? 1 : 0, result.FailedFiles);
        Assert.Equal(corrupt ? 1 : 0, result.RemainingFiles);
        Assert.Equal(corrupt ? null : (long?)0, result.RemainingBytes);
        LongRunningOperation operation = (await operations.GetAsync(result.OperationId))!;
        Assert.Equal(corrupt ? LongRunningOperationState.ReconciliationRequired : LongRunningOperationState.Completed, operation.State);
        Assert.Equal(corrupt ? LongRunningOperationErrorCodes.BlobEncryptionIncomplete : null, operation.TerminalErrorCode);
        Assert.Null(operation.LeaseOwner);
        Assert.Equal(rotate && !corrupt ? 1 : 0, keys.Retired.Count);
        await using Stream plaintext = await blobs.OpenReadAsync(path, EncryptedBlobPurpose.UploadedFile);
        using StreamReader reader = new(plaintext);
        Assert.Equal("fixture\n", await reader.ReadToEndAsync());
        BlobEncryptionStatus status = await service.GetStatusAsync();
        Assert.Equal(corrupt ? 2 : 1, status.TotalFiles);
        Assert.Equal(corrupt ? 1 : 0, status.InvalidFiles);
    }

    [SkippableTheory]
    [InlineData(LongRunningOperationKinds.BlobEncryptionMigration)]
    [InlineData(LongRunningOperationKinds.BlobEncryptionKeyRotation)]
    public async Task Native_recovery_returns_attention_after_processing_a_healthy_neighbor(string kind)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        string path = await SeedAsync(corrupt: true);
        NativeTestKeys keys = new();
        (BlobEncryptionLifecycleService service, LongRunningOperationStore operations, EncryptedBlobStore blobs) = CreateService(keys);
        LongRunningOperationCoordinator coordinator = new(operations, TimeProvider.System);
        LongRunningOperationLeaseResult lease = await coordinator.StartAsync(
            new LongRunningOperationCreateRequest(kind, LongRunningOperationRecoveryPolicy.RestartIdempotently,
                "Isolated native recovery fixture.", DateTimeOffset.UtcNow), "native-recovery", TimeSpan.FromMinutes(1));
        Assert.True(lease.Acquired);

        LongRunningOperationRecoveryResult result = await service.RecoverAsync(lease.Operation, CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, result.State);
        Assert.Equal(LongRunningOperationErrorCodes.RecoveryFailed, result.ErrorCode);
        Assert.True(blobs.HasEnvelope(path));
        Assert.Empty(keys.Retired);
        // The handler returns its disposition; the reconciler owns the final ledger transition.
        Assert.Equal("native-recovery", (await operations.GetAsync(lease.Operation.Id))!.LeaseOwner);
    }

    [SkippableTheory]
    [InlineData("status")]
    [InlineData("verify")]
    [InlineData("migrate")]
    [InlineData("rotate")]
    public async Task Human_cli_reports_unknown_bytes_and_nonzero_exit_for_native_invalid_metadata(string verb)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        await SeedAsync(corrupt: true);
        (BlobEncryptionLifecycleService service, _, _) = CreateService(new());
        ServiceCollection registrations = new();
        registrations.AddSingleton<IBlobEncryptionLifecycleService>(service);
        await using ServiceProvider services = registrations.BuildServiceProvider();
        IsolatedInitialization initialization = new(services);
        DataEncryptionCommands commands = new(initialization);
        TestConsole console = new();
        IAnsiConsole original = AnsiConsole.Console;
        AnsiConsole.Console = console;
        try
        {
            int exit = verb switch
            {
                "status" => await commands.Status(CancellationToken.None),
                "verify" => await commands.Verify(1, long.MaxValue, CancellationToken.None),
                "migrate" => await commands.Migrate(1, long.MaxValue, CancellationToken.None),
                _ => await commands.RotateKey(1, long.MaxValue, CancellationToken.None),
            };
            Assert.Equal(1, exit);
            Assert.Contains("unknown", console.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("PRIVATE", console.Output, StringComparison.Ordinal);
            Assert.Equal(1, initialization.Calls);
        }
        finally { AnsiConsole.Console = original; }
    }

    [SkippableTheory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    public async Task Native_expired_lease_refuses_the_next_foreground_phase(bool rotate, int inventoryBoundary)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string path = await SeedAsync(corrupt: false);

        NativeTestKeys keys = new();

        BlobEncryptionMetadataStore nativeMetadata = new(_db, NullLogger<BlobEncryptionMetadataStore>.Instance);

        LongRunningOperationStore operations = new(_db, TestOrdinaryConnectionFactory.For(_db));

        EncryptedBlobStore blobs = new(keys);

        BoundaryMetadata metadata = new(nativeMetadata, inventoryBoundary, ExpireOwnedLeaseAsync);

        BlobEncryptionLifecycleService service = new(metadata, new(metadata, blobs), blobs, keys,
            new LongRunningOperationCoordinator(operations, TimeProvider.System), operations, TimeProvider.System);

        Exception? failure = await Record.ExceptionAsync(() => rotate
            ? service.RotateKeyAsync(1, long.MaxValue)
            : service.MigrateAsync(1, long.MaxValue));

        Assert.Equal(1, metadata.BoundaryHits);

        if (inventoryBoundary == 1) Assert.False(blobs.HasEnvelope(path));

        Assert.Empty(keys.Retired);

        Assert.IsType<InvalidOperationException>(failure);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, (await ReadOperationAsync(operations)).State);
    }

    [SkippableTheory]
    [InlineData(LongRunningOperationKinds.BlobEncryptionMigration, 1)]
    [InlineData(LongRunningOperationKinds.BlobEncryptionMigration, 2)]
    [InlineData(LongRunningOperationKinds.BlobEncryptionKeyRotation, 1)]
    [InlineData(LongRunningOperationKinds.BlobEncryptionKeyRotation, 2)]
    [InlineData(LongRunningOperationKinds.BlobEncryptionKeyRotation, 3)]
    public async Task Native_expired_lease_refuses_the_next_recovery_phase(string kind, int inventoryBoundary)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string path = await SeedAsync(corrupt: false);

        NativeTestKeys keys = new();

        BlobEncryptionMetadataStore nativeMetadata = new(_db, NullLogger<BlobEncryptionMetadataStore>.Instance);

        LongRunningOperationStore operations = new(_db, TestOrdinaryConnectionFactory.For(_db));

        EncryptedBlobStore blobs = new(keys);

        BoundaryMetadata metadata = new(nativeMetadata, inventoryBoundary, ExpireOwnedLeaseAsync);

        LongRunningOperationCoordinator coordinator = new(operations, TimeProvider.System);

        LongRunningOperationLeaseResult lease = await coordinator.StartAsync(
            new LongRunningOperationCreateRequest(kind, LongRunningOperationRecoveryPolicy.RestartIdempotently,
                "Native expiry recovery fixture.", DateTimeOffset.UtcNow), "expiry-recovery", TimeSpan.FromMinutes(2));

        Assert.True(lease.Acquired);

        BlobEncryptionLifecycleService service = new(metadata, new(metadata, blobs), blobs, keys,
            coordinator, operations, TimeProvider.System);

        Exception? failure = await Record.ExceptionAsync(() => service.RecoverAsync(lease.Operation, CancellationToken.None));

        Assert.Equal(1, metadata.BoundaryHits);

        if (inventoryBoundary == 1) Assert.False(blobs.HasEnvelope(path));

        Assert.Empty(keys.Retired);

        Assert.IsType<InvalidOperationException>(failure);

        // The handler refuses; only its reconciler owns the final recovery disposition CAS.
        Assert.Equal(LongRunningOperationState.Running, (await operations.GetAsync(lease.Operation.Id))!.State);
    }

    [SkippableTheory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    public async Task Native_inventory_cancellation_retains_already_observed_invalid_metadata(bool recovery, int inventoryBoundary)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string path = await SeedAsync(corrupt: inventoryBoundary == 1);

        NativeTestKeys keys = new();

        BlobEncryptionMetadataStore nativeMetadata = new(_db, NullLogger<BlobEncryptionMetadataStore>.Instance);

        LongRunningOperationStore operations = new(_db, TestOrdinaryConnectionFactory.For(_db));

        LongRunningOperationCoordinator coordinator = new(operations, TimeProvider.System);

        EncryptedBlobStore blobs = new(keys);

        BoundaryMetadata metadata = new(nativeMetadata, inventoryBoundary, async () =>
        {
            LongRunningOperation current = await ReadOperationAsync(operations);

            Assert.True(await operations.RequestCancellationAsync(current.Id, current.Revision, DateTimeOffset.UtcNow));
        }, inventoryBoundary >= 2 ? InsertInvalidMetadataAsync : null);

        BlobEncryptionLifecycleService service = new(metadata, new(metadata, blobs), blobs, keys,
            coordinator, operations, TimeProvider.System);

        if (recovery)
        {
            LongRunningOperationLeaseResult lease = await coordinator.StartAsync(
                new LongRunningOperationCreateRequest(inventoryBoundary == 3
                        ? LongRunningOperationKinds.BlobEncryptionKeyRotation : LongRunningOperationKinds.BlobEncryptionMigration,
                    LongRunningOperationRecoveryPolicy.RestartIdempotently, "Native inventory cancellation fixture.", DateTimeOffset.UtcNow),
                "inventory-cancellation", TimeSpan.FromMinutes(2));

            Assert.True(lease.Acquired);

            LongRunningOperationRecoveryResult result = await service.RecoverAsync(lease.Operation, CancellationToken.None);

            Assert.Equal(LongRunningOperationState.ReconciliationRequired, result.State);

            Assert.Equal(LongRunningOperationErrorCodes.RecoveryFailed, result.ErrorCode);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inventoryBoundary == 3
                ? service.RotateKeyAsync(1, long.MaxValue) : service.MigrateAsync(1, long.MaxValue));

            Assert.Equal(LongRunningOperationState.ReconciliationRequired, (await ReadOperationAsync(operations)).State);
        }

        Assert.Equal(1, metadata.BoundaryHits);

        if (inventoryBoundary == 1) Assert.False(blobs.HasEnvelope(path));

        Assert.Empty(keys.Retired);
    }

    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Native_long_running_phases_renew_a_still_live_lease(bool recovery, bool rotate)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string path = await SeedAsync(corrupt: false);

        FakeTimeProvider clock = new();

        DateTimeOffset started = clock.GetUtcNow();

        NativeTestKeys keys = new();

        BlobEncryptionMetadataStore nativeMetadata = new(_db, NullLogger<BlobEncryptionMetadataStore>.Instance);

        LongRunningOperationStore operations = new(_db, TestOrdinaryConnectionFactory.For(_db));

        LongRunningOperationCoordinator coordinator = new(operations, clock);

        EncryptedBlobStore blobs = new(keys);

        bool advancedAtomicTransition = false;

        BoundaryMetadata metadata = new(nativeMetadata, 0, () =>
        {
            clock.Advance(TimeSpan.FromMinutes(recovery ? 1 : 14));

            return Task.CompletedTask;
        }, beforeUpdate: () =>
        {
            if (!advancedAtomicTransition)
            {
                advancedAtomicTransition = true;

                clock.Advance(TimeSpan.FromMinutes(14));
            }

            return Task.CompletedTask;
        });

        BlobEncryptionLifecycleService service = new(metadata, new(metadata, blobs), blobs, keys,
            coordinator, operations, clock);

        if (recovery)
        {
            LongRunningOperationLeaseResult lease = await coordinator.StartAsync(
                new LongRunningOperationCreateRequest(rotate ? LongRunningOperationKinds.BlobEncryptionKeyRotation
                    : LongRunningOperationKinds.BlobEncryptionMigration, LongRunningOperationRecoveryPolicy.RestartIdempotently,
                    "Native renewal recovery fixture.", started), "renewal-recovery", TimeSpan.FromMinutes(2));

            Assert.True(lease.Acquired);

            LongRunningOperationRecoveryResult result = await service.RecoverAsync(lease.Operation, CancellationToken.None);

            Assert.Equal(LongRunningOperationState.Completed, result.State);

            Assert.True((await operations.GetAsync(lease.Operation.Id))!.LeaseExpiresAt > clock.GetUtcNow());
        }
        else
        {
            BlobEncryptionOperationResult result = rotate
                ? await service.RotateKeyAsync(1, long.MaxValue) : await service.MigrateAsync(1, long.MaxValue);

            Assert.Equal(0, result.FailedFiles);

            Assert.Equal(LongRunningOperationState.Completed, (await operations.GetAsync(result.OperationId))!.State);
        }

        Assert.True(clock.GetUtcNow() - started >= TimeSpan.FromMinutes(15));

        Assert.Equal(rotate ? 3 : 2, metadata.BoundaryHits);

        Assert.True(blobs.HasEnvelope(path));
    }

    [SkippableFact]
    public async Task Native_throttle_renews_the_real_lease_across_each_bounded_wait()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await _db.Database.OpenConnectionAsync();

        FakeTimeProvider clock = new();

        LongRunningOperationStore operations = new(_db, TestOrdinaryConnectionFactory.For(_db));

        LongRunningOperationCoordinator coordinator = new(operations, clock);

        LongRunningOperationLeaseResult lease = await coordinator.StartAsync(
            new LongRunningOperationCreateRequest(LongRunningOperationKinds.BlobEncryptionMigration,
                LongRunningOperationRecoveryPolicy.RestartIdempotently, "Native throttle renewal fixture.", clock.GetUtcNow()),
            "throttle-renewal", TimeSpan.FromMinutes(15));

        Assert.True(lease.Acquired);

        int renewals = 0;

        BlobEncryptionLifecycleService.OperationIoThrottle throttle = new(1);

        await throttle.WaitAsync(2, CancellationToken.None, async token =>
        {
            clock.Advance(TimeSpan.FromMinutes(14));

            Assert.True(await coordinator.HeartbeatAsync(lease.Operation.Id, "throttle-renewal", TimeSpan.FromMinutes(15), token));

            renewals++;
        });

        Assert.Equal(4, renewals);

        Assert.True((await operations.GetAsync(lease.Operation.Id))!.LeaseExpiresAt > clock.GetUtcNow());
    }

    private async Task InsertInvalidMetadataAsync()
    {
        await using DbCommand command = _db.Database.GetDbConnection().CreateCommand();

        command.CommandText = """
            INSERT INTO UploadedFiles (rowid, Id, Filename, Bytes, Purpose, MimeType, CreatedAt)
            VALUES (-17, 'PRIVATE-invalid-id', 'PRIVATE-name', 'PRIVATE-size', 'batch', 'text/plain', '2026-10-27T00:00:00.0000000Z');
            """;

        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task ExpireOwnedLeaseAsync()
    {
        await using DbCommand command = _db.Database.GetDbConnection().CreateCommand();

        command.CommandText = """
            UPDATE LongRunningOperations SET LeaseExpiresAt = '2000-01-01T00:00:00.0000000Z'
            WHERE State = 1 AND LeaseOwner IS NOT NULL;
            """;

        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task<LongRunningOperation> ReadOperationAsync(LongRunningOperationStore operations)
    {
        await using DbCommand command = _db.Database.GetDbConnection().CreateCommand();

        command.CommandText = "SELECT Id FROM LongRunningOperations;";

        Guid id = Guid.Parse(Assert.IsType<string>(await command.ExecuteScalarAsync()));

        return (await operations.GetAsync(id))!;
    }

    private sealed class BoundaryMetadata(IBlobEncryptionMetadataStore inner, int boundary, Func<Task> atBoundary,
        Func<Task>? beforeBoundary = null, Func<Task>? beforeUpdate = null)
        : IBlobEncryptionMetadataStore
    {
        private int _calls;

        public int BoundaryHits { get; private set; }

        public async Task<BlobEncryptionInventory> ListAsync(CancellationToken cancellationToken = default)
        {
            int call = ++_calls;

            if ((boundary == 0 || call == boundary) && beforeBoundary is not null) await beforeBoundary();

            BlobEncryptionInventory inventory = await inner.ListAsync(cancellationToken);

            if (boundary == 0 || call == boundary)
            {
                BoundaryHits++;

                await atBoundary();
            }

            return inventory;
        }

        public async Task UpdateEncryptionMetadataAsync(BlobEncryptionCandidate candidate, EncryptedBlobDescriptor descriptor,
            string plaintextSha256, CancellationToken cancellationToken = default)
        {
            if (beforeUpdate is not null) await beforeUpdate();

            await inner.UpdateEncryptionMetadataAsync(candidate, descriptor, plaintextSha256, cancellationToken);
        }
    }

    private (BlobEncryptionLifecycleService, LongRunningOperationStore, EncryptedBlobStore) CreateService(NativeTestKeys keys)
    {
        BlobEncryptionMetadataStore metadata = new(_db, NullLogger<BlobEncryptionMetadataStore>.Instance);
        LongRunningOperationStore operations = new(_db, TestOrdinaryConnectionFactory.For(_db));
        EncryptedBlobStore blobs = new(keys);
        return (new(metadata, new BlobEncryptionFileProcessor(metadata, blobs), blobs, keys,
            new LongRunningOperationCoordinator(operations, TimeProvider.System), operations, TimeProvider.System), operations, blobs);
    }

    private async Task<string> SeedAsync(bool corrupt)
    {
        Guid healthyId = Guid.NewGuid();
        string path = UploadedFileStorage.ResolvePath(healthyId);
        Assert.StartsWith(Path.GetFullPath(_testHome) + Path.DirectorySeparatorChar, Path.GetFullPath(path), StringComparison.Ordinal);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "fixture\n");
        await _db.Database.OpenConnectionAsync();
        await using DbCommand command = _db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            INSERT INTO UploadedFiles (rowid, Id, Filename, Bytes, Purpose, MimeType, CreatedAt)
            VALUES (12, @id, 'healthy.txt', 8, 'custom-purpose', 'text/plain', '2026-10-27T00:00:00.0000000Z');
            """;
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = healthyId.ToString("D");
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync();
        if (corrupt)
        {
            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO UploadedFiles (rowid, Id, Filename, Bytes, Purpose, MimeType, CreatedAt)
                VALUES (-17, 'PRIVATE-invalid-id', 'PRIVATE-name', 'PRIVATE-size', 'batch', 'text/plain', '2026-10-27T00:00:00.0000000Z');
                """;
            await command.ExecuteNonQueryAsync();
        }

        return path;
    }

    private sealed class IsolatedInitialization(IServiceProvider services) : IGrimoireCliInitialization
    {
        public int Calls { get; private set; }

        public Task<T> RunExclusiveAsync<T>(Func<IServiceProvider, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
        {
            Calls++;
            return operation(services, cancellationToken);
        }

        public Task<T> RunExclusiveWithBootstrapAsync<T>(Func<IServiceProvider, CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
            RunExclusiveAsync(operation, cancellationToken);
    }

    private sealed class NativeTestKeys : IFileEncryptionKeyRing
    {
        private readonly Dictionary<string, FileEncryptionKeyMaterial> _keys = [];
        private FileEncryptionKeyMaterial _active;

        public NativeTestKeys()
        {
            _active = FileEncryptionKeyMaterial.Create(RandomNumberGenerator.GetBytes(32));
            _keys.Add(_active.KeyId, _active);
        }

        public List<string> Retired { get; } = [];

        public Task<FileEncryptionKeyMaterial> RotateAsync(CancellationToken cancellationToken = default)
        {
            _active = FileEncryptionKeyMaterial.Create(RandomNumberGenerator.GetBytes(32));
            _keys.Add(_active.KeyId, _active);
            return Task.FromResult(_active);
        }

        public Task RetireAsync(string keyId, CancellationToken cancellationToken = default)
        {
            Assert.NotEqual(_active.KeyId, keyId);
            Assert.True(_keys.Remove(keyId));
            Retired.Add(keyId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetActiveKeyIdsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(_keys.Keys.ToArray());

        public ValueTask<FileEncryptionKeyMaterial> GetForWriteAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(_active);

        public ValueTask<FileEncryptionKeyMaterial> GetForReadAsync(string keyId, CancellationToken cancellationToken = default) => ValueTask.FromResult(_keys[keyId]);
    }
}
