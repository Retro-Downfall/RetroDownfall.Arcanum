using System.Reflection;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class LongRunningOperationStoreTests : IAsyncLifetime
{
    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public LongRunningOperationStoreTests(GrimoireFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync()
    {
        _dbPath = _fixture.CopyDatabase();
        _db = _fixture.CreateContext(_dbPath);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_db is not null)
        {
            SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();
            await _db.DisposeAsync();
            SqliteConnection.ClearPool(connection);
        }

        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    /// <summary>
    /// Issue #118 — the request-identity row reads back into the right properties.
    /// </summary>
    /// <remarks>
    /// The three digests are deliberately distinct, because the projection reads its two BLOB
    /// columns positionally: transposing them would leave the round trip looking healthy while
    /// `CovenantResetCheckpointInitiator` compared a derived erasure digest against the caller's
    /// apply-request digest and refused every legitimate named reset.
    /// </remarks>
    [SkippableFact]
    public async Task FindRequestIdentityAsync_ReadsBackEachDigestIntoItsOwnProperty()
    {
        RequireSqlCipher();
        LongRunningOperationStore store = Store(_db!);
        DateTimeOffset now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

        Guid requested = Guid.Parse("31313131-3131-4131-8131-313131313131");
        CovenantDigest apply = new([.. Enumerable.Repeat((byte)0x11, 32)]);
        CovenantDigest effect = new([.. Enumerable.Repeat((byte)0x22, 32)]);

        LongRunningOperationRequestIdentityResult created = await store.ResolveOrCreateAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionMutation,
                LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                "Named Covenant reset.",
                now),
            new LongRunningOperationRequestIdentity(requested, apply, effect));

        Assert.Equal(LongRunningOperationRequestIdentityOutcome.Created, created.Outcome);

        LongRunningOperationRequestIdentity? read =
            await store.FindRequestIdentityAsync(created.Operation!.Id);

        Assert.NotNull(read);
        Assert.Equal(requested, read!.RequestedOperationId);
        Assert.Equal(apply, read.ApplyRequestDigest);
        Assert.Equal(effect, read.EffectDigest);
        Assert.NotEqual(read.ApplyRequestDigest, read.EffectDigest);
    }

    /// <summary>
    /// A server-generated operation has no identity row, and absence is an ordinary answer. Treating
    /// it as corruption would turn every unnamed reset into a recovery escalation.
    /// </summary>
    [SkippableFact]
    public async Task FindRequestIdentityAsync_ReturnsNullForAnOperationCreatedWithoutOne()
    {
        RequireSqlCipher();
        LongRunningOperationStore store = Store(_db!);
        LongRunningOperation operation = await CreateAsync(store, new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));

        Assert.Null(await store.FindRequestIdentityAsync(operation.Id));
        Assert.Null(await store.FindRequestIdentityAsync(Guid.NewGuid()));
    }

    /// <summary>
    /// A replay after erasure must find its original server operation from the caller's durable
    /// name before any new live inventory exists to plan.
    /// </summary>
    [SkippableFact]
    public async Task FindByRequestedOperationIdAsync_ReturnsTheServerOperationAndCompleteIdentity()
    {
        RequireSqlCipher();
        LongRunningOperationStore store = Store(_db!);
        DateTimeOffset now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
        Guid requested = Guid.Parse("41414141-4141-4141-8141-414141414141");
        CovenantDigest apply = new([.. Enumerable.Repeat((byte)0x41, 32)]);
        CovenantDigest effect = new([.. Enumerable.Repeat((byte)0x42, 32)]);

        LongRunningOperationRequestIdentityResult created = await store.ResolveOrCreateAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionFactoryReset,
                LongRunningOperationRecoveryPolicy.RestartIdempotently,
                "Named healthy-catalog factory erasure.",
                now),
            new LongRunningOperationRequestIdentity(requested, apply, effect));

        LongRunningOperationRequestIdentityMatch? match =
            await store.FindByRequestedOperationIdAsync(requested);

        Assert.NotNull(match);
        Assert.Equal(created.Operation, match!.Operation);
        Assert.Equal(requested, match.Identity.RequestedOperationId);
        Assert.Equal(apply, match.Identity.ApplyRequestDigest);
        Assert.Equal(effect, match.Identity.EffectDigest);
        Assert.Null(match.Operation.RootOperationId);
        Assert.Null(match.Operation.ParentOperationId);
    }

    [SkippableFact]
    public async Task FindByRequestedOperationIdAsync_ReturnsNullForAnUnknownName()
    {
        RequireSqlCipher();
        LongRunningOperationStore store = Store(_db!);

        Assert.Null(
            await store.FindByRequestedOperationIdAsync(
                Guid.Parse("42424242-4242-4242-8242-424242424242")));
    }

    [SkippableFact]
    public async Task FindByRequestedOperationIdAsync_RefusesTheEmptyName()
    {
        RequireSqlCipher();
        LongRunningOperationStore store = Store(_db!);

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.FindByRequestedOperationIdAsync(Guid.Empty));
    }

    [SkippableFact]
    public async Task TryAcquireLeaseAsync_OnlyOneWorkerWinsUntilLeaseExpires()
    {
        RequireSqlCipher();
        LongRunningOperationStore store = Store(_db!);
        DateTimeOffset now = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        LongRunningOperation operation = await CreateAsync(store, now);

        LongRunningOperationLeaseResult first = await store.TryAcquireLeaseAsync(
            operation.Id,
            "worker-a",
            now,
            now.AddMinutes(1));
        LongRunningOperationLeaseResult competing = await store.TryAcquireLeaseAsync(
            operation.Id,
            "worker-b",
            now.AddSeconds(30),
            now.AddMinutes(2));
        LongRunningOperationLeaseResult recovered = await store.TryAcquireLeaseAsync(
            operation.Id,
            "worker-b",
            now.AddMinutes(1),
            now.AddMinutes(2));

        Assert.True(first.Acquired);
        Assert.False(competing.Acquired);
        Assert.True(recovered.Acquired);
        Assert.Equal("worker-b", recovered.Operation.LeaseOwner);
        Assert.Equal(2, recovered.Operation.AttemptCount);
        Assert.Equal(LongRunningOperationState.Running, recovered.Operation.State);
    }

    [SkippableFact]

    public async Task TryStartSingleFlightAsync_OnlyOneActiveOperationPerKind()
    {

        RequireSqlCipher();

        await using ArcanumDbContext competingDb = _fixture.CreateContext(_dbPath);

        LongRunningOperationStore firstStore = Store(_db!);

        LongRunningOperationStore secondStore = Store(competingDb);

        DateTimeOffset now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

        LongRunningOperationCreateRequest prune = new(
            LongRunningOperationKinds.DataRetentionPrune,
            LongRunningOperationRecoveryPolicy.RestartIdempotently,
            "Apply one bounded retention sweep.",
            now);

        Task<LongRunningOperation?> first = firstStore.TryStartSingleFlightAsync(
            prune,
            "retention-a",
            now,
            now.AddMinutes(5));

        Task<LongRunningOperation?> second = secondStore.TryStartSingleFlightAsync(
            prune,
            "retention-b",
            now,
            now.AddMinutes(5));

        LongRunningOperation?[] starts = await Task.WhenAll(first, second);

        LongRunningOperation winner = Assert.Single(
            starts.OfType<LongRunningOperation>());

        Assert.Equal(LongRunningOperationState.Running, winner.State);

        Assert.Equal(1, winner.AttemptCount);

        Assert.Contains(
            winner.LeaseOwner,
            new[] { "retention-a", "retention-b" });

        IReadOnlyList<LongRunningOperation> pruneOperations = await firstStore.ListAsync(
            new LongRunningOperationQuery(
                Kind: LongRunningOperationKinds.DataRetentionPrune));

        Assert.Single(pruneOperations);

        LongRunningOperation unrelated = await firstStore.CreateAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.WorkspaceIndex,
                LongRunningOperationRecoveryPolicy.RestartIdempotently,
                "Index an unrelated workspace.",
                now));

        Assert.Equal(LongRunningOperationState.Pending, unrelated.State);

    }

    [SkippableFact]

    public async Task TryStartSingleFlightAsync_RetentionKindsShareOneActiveSlot()
    {

        RequireSqlCipher();

        LongRunningOperationStore store = Store(_db!);

        DateTimeOffset now = new(2026, 8, 2, 13, 0, 0, TimeSpan.Zero);

        LongRunningOperation? prune = await store.TryStartSingleFlightAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionPrune,
                LongRunningOperationRecoveryPolicy.RestartIdempotently,
                "Apply one bounded retention sweep.",
                now),
            "retention-prune",
            now,
            now.AddMinutes(5));

        Assert.NotNull(prune);

        LongRunningOperation? mutation = await store.TryStartSingleFlightAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionMutation,
                LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                "Delete one retained source.",
                now),
            "retention-mutation",
            now,
            now.AddMinutes(5));

        Assert.Null(mutation);

        IReadOnlyList<LongRunningOperation> operations = await store.ListAsync(
            new LongRunningOperationQuery(Limit: 10));

        LongRunningOperation only = Assert.Single(operations);

        Assert.Equal(LongRunningOperationKinds.DataRetentionPrune, only.Kind);

        Assert.Equal(LongRunningOperationState.Running, only.State);

    }

    [SkippableFact]

    public async Task RenewLeaseAsync_UsesIndependentEncryptedConnection()
    {

        RequireSqlCipher();

        LongRunningOperationStore store = Store(_db!);

        DateTimeOffset startedAt = new(2026, 8, 2, 14, 0, 0, TimeSpan.Zero);

        LongRunningOperation operation = Assert.IsType<LongRunningOperation>(
            await store.TryStartSingleFlightAsync(
                new LongRunningOperationCreateRequest(
                    LongRunningOperationKinds.DataRetentionPrune,
                    LongRunningOperationRecoveryPolicy.RestartIdempotently,
                    "Apply one bounded retention sweep.",
                    startedAt),
                "retention-owner",
                startedAt,
                startedAt.AddMinutes(5)));

        DateTimeOffset heartbeatAt = startedAt.AddMinutes(1);

        DateTimeOffset leaseExpiresAt = heartbeatAt.AddMinutes(5);

        bool renewed = await store.RenewLeaseAsync(
            operation.Id,
            "retention-owner",
            heartbeatAt,
            leaseExpiresAt);

        LongRunningOperation persisted = Assert.IsType<LongRunningOperation>(
            await store.GetAsync(operation.Id));

        Assert.True(renewed);

        Assert.Equal(heartbeatAt, persisted.HeartbeatAt);

        Assert.Equal(leaseExpiresAt, persisted.LeaseExpiresAt);

        Assert.Equal(2, persisted.Revision);

    }

    [SkippableFact]
    public async Task RenewLeaseAsync_UsesAnUnpooledIsolatedHeartbeatOrdinaryLease()
    {

        RequireSqlCipher();

        RecordingFreshOrdinaryConnectionFactory connections = new(
            _db!.Database.GetConnectionString()!);

        ConstructorInfo? constructor = typeof(LongRunningOperationStore)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(candidate =>
                candidate.GetParameters() is
                [
                    { ParameterType: var dbType },
                    { ParameterType: var factoryType, HasDefaultValue: false },
                    _,
                ]
                && dbType == typeof(ArcanumDbContext)
                && factoryType == typeof(IGrimoireOrdinaryConnectionFactory));

        Assert.NotNull(constructor);

        using LongRunningOperationStore store = (LongRunningOperationStore)constructor.Invoke(
            [_db, connections, null]);

        DateTimeOffset startedAt = new(2026, 9, 1, 14, 0, 0, TimeSpan.Zero);

        LongRunningOperation operation = Assert.IsType<LongRunningOperation>(
            await store.TryStartSingleFlightAsync(
                new LongRunningOperationCreateRequest(
                    LongRunningOperationKinds.DataRetentionPrune,
                    LongRunningOperationRecoveryPolicy.RestartIdempotently,
                    "Apply one bounded retention sweep.",
                    startedAt),
                "task-6-heartbeat",
                startedAt,
                startedAt.AddMinutes(5)));

        bool renewed = await store.RenewLeaseAsync(
            operation.Id,
            "task-6-heartbeat",
            startedAt.AddMinutes(1),
            startedAt.AddMinutes(6));

        Assert.True(renewed);

        Assert.Equal(
            [GrimoireOrdinaryFreshConnectionKind.IsolatedHeartbeat],
            connections.Kinds);

        SqliteConnection heartbeat = Assert.Single(connections.Opened);

        SqliteConnectionStringBuilder connectionString = new(heartbeat.ConnectionString);

        Assert.False(connectionString.Pooling);

        Assert.Equal(SqliteOpenMode.ReadWriteCreate, connectionString.Mode);

        Assert.Equal(System.Data.ConnectionState.Closed, heartbeat.State);

        Assert.Equal(0, connections.LiveLeaseCount);

    }

    /// <summary>
    /// The heartbeat's own connection is closed when the heartbeat returns, not parked in a pool.
    /// </summary>
    /// <remarks>
    /// Composed over the pooled context the host composes in production, because that is the shape
    /// the defect needs: disposing a pooled connection does not close the database — the native
    /// handle goes back into the pool with the file still open — so a heartbeat that has already
    /// returned leaves a write-ahead log and a wal-index behind it for as long as it takes some
    /// unrelated caller to clear the pools. Nothing enrols that handle with the Covenant connection
    /// drain and nothing can: the drain runs inside the erasure whose lease this heartbeat renews, so
    /// a drain that closed it would cancel the operation it was draining for.
    ///
    /// <para>Asserted through the erasure's own inventory and its own live-handle classes rather than
    /// through a file-existence check, because the claim is about what the erasure refuses on rather
    /// than about what happens to be on disk, and the two must not be able to drift apart.</para>
    /// </remarks>
    [SkippableFact]

    public async Task RenewLeaseAsync_LeavesNoLiveHandleArtifactBesideTheGrimoire()
    {

        RequireSqlCipher();

        string pooledPath = _fixture.CopyDatabase();

        await using ArcanumDbContext pooledDb = _fixture.CreateContext(pooledPath, pooled: true);

        LongRunningOperationStore store = Store(pooledDb);

        DateTimeOffset startedAt = new(2026, 8, 2, 14, 0, 0, TimeSpan.Zero);

        LongRunningOperation operation = Assert.IsType<LongRunningOperation>(
            await store.TryStartSingleFlightAsync(
                new LongRunningOperationCreateRequest(
                    LongRunningOperationKinds.DataRetentionPrune,
                    LongRunningOperationRecoveryPolicy.RestartIdempotently,
                    "Apply one bounded retention sweep.",
                    startedAt),
                "retention-owner",
                startedAt,
                startedAt.AddMinutes(5)));

        // The ledger writes above ran on the scoped EF connection, which the drain does reach — by
        // enrolment while it is open, and by clearing the pools once it is not. Closing and clearing
        // here is that drain, so what survives it afterwards is the heartbeat's handle and nothing
        // else.
        await pooledDb.Database.CloseConnectionAsync();

        SqliteConnection.ClearAllPools();

        Assert.Empty(LiveHandleSurvivors(pooledPath));

        bool renewed = await store.RenewLeaseAsync(
            operation.Id,
            "retention-owner",
            startedAt.AddMinutes(1),
            startedAt.AddMinutes(6));

        Assert.True(renewed);

        Assert.Empty(LiveHandleSurvivors(pooledPath));

    }

    /// <summary>
    /// The ordinary heartbeat runs on a connection this store owns, never the workload's scoped one.
    /// </summary>
    /// <remarks>
    /// <para>Every heartbeat in the repo fires from a background timer while the owner's handler is
    /// still working — SubagentRunner, A2ASendingLeaseRenewer and BlobEncryptionLifecycleService all
    /// do it. Running that UPDATE on the workload's own scoped <c>ArcanumDbContext</c> connection put
    /// two writers on one <see cref="SqliteConnection"/>, whose live-command list is not
    /// synchronized, and folded the renewal into whatever transaction the workload had open — so a
    /// rolled-back unit of work silently took the lease renewal with it and the reconciler could
    /// steal an operation whose owner was still running. <c>RenewLeaseAsync</c> already opened its
    /// own connection for exactly this reason; the ordinary heartbeat has the same requirement and
    /// every caller.</para>
    /// <para><c>busy_timeout</c> is the observation: it is connection-local, so branding the scoped
    /// connection with a sentinel no initialized connection would ever carry lets a trigger report
    /// which connection actually ran the statement. Holding a real write transaction open instead
    /// would only prove SQLite's single-writer rule — the heartbeat would correctly wait for the
    /// lock, which on one thread is a deadlock rather than a result.</para>
    /// </remarks>
    [SkippableFact]
    public async Task HeartbeatAsync_RunsOnAConnectionTheStoreOwns()
    {

        RequireSqlCipher();

        LongRunningOperationStore store = Store(_db!);

        DateTimeOffset startedAt = new(2026, 8, 2, 14, 0, 0, TimeSpan.Zero);

        LongRunningOperation operation = Assert.IsType<LongRunningOperation>(
            await store.TryStartSingleFlightAsync(
                new LongRunningOperationCreateRequest(
                    LongRunningOperationKinds.DataRetentionPrune,
                    LongRunningOperationRecoveryPolicy.RestartIdempotently,
                    "Apply one bounded retention sweep.",
                    startedAt),
                "retention-owner",
                startedAt,
                startedAt.AddMinutes(5)));

        // Pin the scoped connection open so the brand survives; EF otherwise closes between commands
        // and the pragma resets with it.
        await _db!.Database.OpenConnectionAsync();

        await ExecuteAsync("PRAGMA busy_timeout = 424242;");

        await ExecuteAsync(
            """
            CREATE TRIGGER assert_heartbeat_is_not_on_the_scoped_connection
            BEFORE UPDATE ON "LongRunningOperations"
            BEGIN
                SELECT RAISE(ABORT, 'the heartbeat ran on the workload''s scoped connection')
                WHERE (SELECT "timeout" FROM pragma_busy_timeout) = 424242;
            END;
            """);

        DateTimeOffset heartbeatAt = startedAt.AddMinutes(1);

        DateTimeOffset leaseExpiresAt = heartbeatAt.AddMinutes(5);

        bool beat = await store.HeartbeatAsync(
            operation.Id,
            "retention-owner",
            heartbeatAt,
            leaseExpiresAt);

        Assert.True(beat);

        LongRunningOperation persisted = Assert.IsType<LongRunningOperation>(
            await store.GetAsync(operation.Id));

        Assert.Equal(heartbeatAt, persisted.HeartbeatAt);

        Assert.Equal(leaseExpiresAt, persisted.LeaseExpiresAt);

    }

    /// <summary>
    /// A destructive Covenant transition drains the operation ledger's scoped handle before it
    /// replaces the Grimoire, and the next checkpoint reopens that same connection object against
    /// the installed candidate.
    /// </summary>
    [SkippableFact]
    public async Task CovenantDrain_ClosesTheScopedLedgerHandle_AndTheNextReadReopensIt()
    {

        RequireSqlCipher();

        CovenantConnectionDrain drain = new();

        using LongRunningOperationStore store = Store(_db!, drain);

        LongRunningOperation operation = await CreateAsync(
            store,
            new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        Assert.Equal(System.Data.ConnectionState.Open, connection.State);

        Result drained = await drain.DrainAsync(CancellationToken.None);

        Assert.True(drained.IsSuccess, drained.Error.Message);

        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);

        LongRunningOperation reopened = Assert.IsType<LongRunningOperation>(
            await store.GetAsync(operation.Id, CancellationToken.None));

        Assert.Equal(operation.Id, reopened.Id);

        Assert.Equal(System.Data.ConnectionState.Open, connection.State);

    }

    [SkippableFact]

    public async Task RenewLeaseAsync_AppliesConnectionPolicyToItsIndependentConnection()
    {

        RequireSqlCipher();

        LongRunningOperationStore store = Store(_db!);

        DateTimeOffset startedAt = new(2026, 8, 2, 14, 0, 0, TimeSpan.Zero);

        LongRunningOperation operation = Assert.IsType<LongRunningOperation>(
            await store.TryStartSingleFlightAsync(
                new LongRunningOperationCreateRequest(
                    LongRunningOperationKinds.DataRetentionPrune,
                    LongRunningOperationRecoveryPolicy.RestartIdempotently,
                    "Apply one bounded retention sweep.",
                    startedAt),
                "retention-owner",
                startedAt,
                startedAt.AddMinutes(5)));

        // The heartbeat writes on a connection this store opens itself, so the only way to observe
        // that connection's policy is from inside the statement it runs.
        await ExecuteAsync(
            """
            CREATE TRIGGER assert_heartbeat_connection_policy
            BEFORE UPDATE ON "LongRunningOperations"
            BEGIN
                SELECT RAISE(ABORT, 'the heartbeat connection never had Covenant policy applied')
                WHERE (SELECT "secure_delete" FROM pragma_secure_delete) = 0
                   OR (SELECT "timeout" FROM pragma_busy_timeout) = 0;
            END;
            """);

        bool renewed = await store.RenewLeaseAsync(
            operation.Id,
            "retention-owner",
            startedAt.AddMinutes(1),
            startedAt.AddMinutes(6));

        Assert.True(renewed);

    }

    [SkippableFact]
    public async Task SaveCheckpointAsync_IsMonotonicAndRejectsDuplicateVersion()
    {
        RequireSqlCipher();
        LongRunningOperationStore store = Store(_db!);
        DateTimeOffset now = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        LongRunningOperation operation = await CreateAsync(store, now);
        _ = await store.TryAcquireLeaseAsync(operation.Id, "worker", now, now.AddMinutes(1));

        bool first = await store.SaveCheckpointAsync(
            operation.Id,
            "worker",
            expectedCheckpointVersion: 0,
            checkpointVersion: 1,
            checkpointPayload: [1, 2, 3],
            checkpointReference: null,
            publicSummary: "Indexed 10 of 50 safe paths.",
            now.AddSeconds(10));
        bool duplicate = await store.SaveCheckpointAsync(
            operation.Id,
            "worker",
            expectedCheckpointVersion: 0,
            checkpointVersion: 1,
            checkpointPayload: [9, 9, 9],
            checkpointReference: null,
            publicSummary: "must not overwrite",
            now.AddSeconds(11));

        bool progressUpdate = await store.SaveCheckpointAsync(
            operation.Id,
            "worker",
            expectedCheckpointVersion: 1,
            checkpointVersion: 1,
            checkpointPayload: [4, 5, 6],
            checkpointReference: null,
            publicSummary: "Indexed 20 of 50 safe paths.",
            now.AddSeconds(12));

        LongRunningOperation persisted = Assert.IsType<LongRunningOperation>(
            await store.GetAsync(operation.Id));
        Assert.True(first);
        Assert.False(duplicate);

        Assert.True(progressUpdate);

        Assert.Equal(1, persisted.CheckpointVersion);

        Assert.Equal([4, 5, 6], persisted.CheckpointPayload);

        Assert.Equal("Indexed 20 of 50 safe paths.", persisted.PublicSummary);
    }

    [SkippableFact]
    public async Task RequestCancellationAsync_UsesCompareAndSwapAndIsIdempotent()
    {
        RequireSqlCipher();
        LongRunningOperationStore store = Store(_db!);
        DateTimeOffset now = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        LongRunningOperation operation = await CreateAsync(store, now);
        LongRunningOperationLeaseResult leased = await store.TryAcquireLeaseAsync(
            operation.Id,
            "worker",
            now,
            now.AddMinutes(1));

        bool first = await store.RequestCancellationAsync(
            operation.Id,
            leased.Operation.Revision,
            now.AddSeconds(5));
        bool staleDuplicate = await store.RequestCancellationAsync(
            operation.Id,
            leased.Operation.Revision,
            now.AddSeconds(6));

        LongRunningOperation persisted = Assert.IsType<LongRunningOperation>(
            await store.GetAsync(operation.Id));
        Assert.True(first);
        Assert.False(staleDuplicate);
        Assert.Equal(LongRunningOperationState.Cancelling, persisted.State);
    }

    /// <summary>
    /// 'arcanum operation retry' parks the row in Pending. If the only discovery query ignored
    /// Pending, nothing would ever re-drive it, and for a data-retention kind Pending also blocks
    /// single-flight — so the documented repair action would deny the whole subsystem forever.
    /// </summary>
    [SkippableFact]
    public async Task FindExpiredAsync_ReDrivesARetriedOperationButNotOneAwaitingItsFirstLease()
    {
        RequireSqlCipher();
        LongRunningOperationStore store = Store(_db!);
        DateTimeOffset now = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        LongRunningOperation retried = await CreateAsync(store, now);
        LongRunningOperation justCreated = await CreateAsync(store, now);

        LongRunningOperationLeaseResult lease = await store.TryAcquireLeaseAsync(
            retried.Id,
            "dead-host",
            now,
            now.AddMinutes(1));
        Assert.True(
            await store.TryTransitionAsync(
                retried.Id,
                lease.Operation.Revision,
                "dead-host",
                LongRunningOperationState.ReconciliationRequired,
                now.AddSeconds(1),
                LongRunningOperationErrorCodes.CorruptCheckpoint));

        LongRunningOperation attention = Assert.IsType<LongRunningOperation>(
            await store.GetAsync(retried.Id));
        Assert.True(
            await store.ResetForRetryAsync(retried.Id, attention.Revision, now.AddSeconds(2)));

        IReadOnlyList<LongRunningOperation> recoverable = await store.FindExpiredAsync(
            now.AddSeconds(3),
            10);

        Assert.Contains(recoverable, operation => operation.Id == retried.Id);

        // Its creator leases it in the very next statement; reconciling it would race that caller.
        Assert.DoesNotContain(recoverable, operation => operation.Id == justCreated.Id);

        LongRunningOperationLeaseResult recovery = await store.TryAcquireLeaseAsync(
            retried.Id,
            "recovery-worker",
            now.AddSeconds(3),
            now.AddMinutes(2));
        Assert.True(recovery.Acquired);
    }

    /// <summary>
    /// Only the kinds that poll the flag settle their own Cancelling row, and only while their lease
    /// is alive. Once it lapses the row must be recoverable, or a cancellation nobody observed wedges
    /// its kind permanently with no CLI exit.
    /// </summary>
    [SkippableFact]
    public async Task AnUnobservedCancellationIsRecoverableOnceItsLeaseLapses()
    {
        RequireSqlCipher();
        LongRunningOperationStore store = Store(_db!);
        DateTimeOffset now = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        LongRunningOperation operation = await CreateAsync(store, now);
        LongRunningOperationLeaseResult leased = await store.TryAcquireLeaseAsync(
            operation.Id,
            "dead-host",
            now,
            now.AddMinutes(1));
        Assert.True(
            await store.RequestCancellationAsync(
                operation.Id,
                leased.Operation.Revision,
                now.AddSeconds(5)));

        // The owner still holds a live lease, so it is the one that must settle the row.
        Assert.DoesNotContain(
            await store.FindExpiredAsync(now.AddSeconds(10), 10),
            candidate => candidate.Id == operation.Id);

        IReadOnlyList<LongRunningOperation> recoverable = await store.FindExpiredAsync(
            now.AddMinutes(2),
            10);

        Assert.Contains(recoverable, candidate => candidate.Id == operation.Id);

        LongRunningOperationLeaseResult recovery = await store.TryAcquireLeaseAsync(
            operation.Id,
            "recovery-worker",
            now.AddMinutes(2),
            now.AddMinutes(4));

        Assert.True(recovery.Acquired);
    }

    /// <summary>
    /// The operator must also be able to back out of a cancellation nobody observed by hand, without
    /// being able to yank one that is still in progress.
    /// </summary>
    [SkippableFact]
    public async Task ResetForRetryAsync_AcceptsALapsedCancellationAndRefusesALiveOne()
    {
        RequireSqlCipher();
        LongRunningOperationStore store = Store(_db!);
        DateTimeOffset now = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        LongRunningOperation operation = await CreateAsync(store, now);
        LongRunningOperationLeaseResult leased = await store.TryAcquireLeaseAsync(
            operation.Id,
            "worker",
            now,
            now.AddMinutes(1));
        Assert.True(
            await store.RequestCancellationAsync(
                operation.Id,
                leased.Operation.Revision,
                now.AddSeconds(5)));

        LongRunningOperation cancelling = Assert.IsType<LongRunningOperation>(
            await store.GetAsync(operation.Id));

        bool whileOwned = await store.ResetForRetryAsync(
            operation.Id,
            cancelling.Revision,
            now.AddSeconds(10));
        bool onceLapsed = await store.ResetForRetryAsync(
            operation.Id,
            cancelling.Revision,
            now.AddMinutes(2));

        LongRunningOperation persisted = Assert.IsType<LongRunningOperation>(
            await store.GetAsync(operation.Id));
        Assert.False(whileOwned);
        Assert.True(onceLapsed);
        Assert.Equal(LongRunningOperationState.Pending, persisted.State);
        Assert.Null(persisted.LeaseOwner);
    }

    [SkippableFact]
    public async Task CreateAsync_PreservesRootParentAndRecoveryLinks()
    {
        RequireSqlCipher();
        LongRunningOperationStore store = Store(_db!);
        DateTimeOffset now = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        Guid sessionId = Guid.NewGuid();
        Guid runId = Guid.NewGuid();
        Guid reservationId = Guid.NewGuid();
        Guid claimId = Guid.NewGuid();
        LongRunningOperation parent = await CreateAsync(store, now);

        LongRunningOperation child = await store.CreateAsync(new LongRunningOperationCreateRequest(
            Kind: LongRunningOperationKinds.Apprentice,
            RecoveryPolicy: LongRunningOperationRecoveryPolicy.ResumeFromCheckpoint,
            PublicSummary: "Child apprentice recovery.",
            CreatedAt: now.AddSeconds(1),
            RootOperationId: parent.Id,
            ParentOperationId: parent.Id,
            SessionId: sessionId,
            RunId: runId,
            InferenceRunId: runId,
            BudgetReservationId: reservationId,
            IdempotencyClaimId: claimId));

        LongRunningOperation persisted = Assert.IsType<LongRunningOperation>(
            await store.GetAsync(child.Id));
        Assert.Equal(parent.Id, persisted.RootOperationId);
        Assert.Equal(parent.Id, persisted.ParentOperationId);
        Assert.Equal(sessionId, persisted.SessionId);
        Assert.Equal(runId, persisted.RunId);
        Assert.Equal(runId, persisted.InferenceRunId);
        Assert.Equal(reservationId, persisted.BudgetReservationId);
        Assert.Equal(claimId, persisted.IdempotencyClaimId);
    }

    [SkippableFact]
    public async Task ReconcileAsync_ExpiredOperation_IsClaimedOnceAndCompleted()
    {
        RequireSqlCipher();
        LongRunningOperationStore store = Store(_db!);
        DateTimeOffset now = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        LongRunningOperation operation = await CreateAsync(store, now.AddMinutes(-5));
        _ = await store.TryAcquireLeaseAsync(
            operation.Id,
            "dead-host",
            now.AddMinutes(-5),
            now.AddMinutes(-4));
        CompletingRecoveryHandler handler = new(LongRunningOperationKinds.WorkspaceIndex);
        LongRunningOperationReconciler reconciler = new(
            store,
            [handler],
            TimeProvider.System,
            NullLogger<LongRunningOperationReconciler>.Instance);

        LongRunningOperationReconciliationSummary summary = await reconciler.ReconcileAsync(
            now,
            ownerId: "startup-worker",
            maxOperations: 10,
            maxConcurrency: 2);

        LongRunningOperation persisted = Assert.IsType<LongRunningOperation>(
            await store.GetAsync(operation.Id));
        Assert.Equal(1, summary.Completed);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(LongRunningOperationState.Completed, persisted.State);
    }

    [SkippableFact]
    public async Task ReconcileAsync_PageSize_DoesNotLimitTotalRecoveryWork()
    {

        RequireSqlCipher();

        LongRunningOperationStore store = Store(_db!);

        DateTimeOffset now = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

        const int operationCount = 7;

        for (int index = 0; index < operationCount; index++)
        {

            LongRunningOperation operation = await CreateAsync(
                store,
                now.AddMinutes(-10).AddSeconds(index));

            _ = await store.TryAcquireLeaseAsync(
                operation.Id,
                $"dead-host-{index}",
                now.AddMinutes(-5),
                now.AddMinutes(-4));

        }

        CompletingRecoveryHandler handler = new(LongRunningOperationKinds.WorkspaceIndex);

        LongRunningOperationReconciler reconciler = new(
            store,
            [handler],
            TimeProvider.System,
            NullLogger<LongRunningOperationReconciler>.Instance);

        LongRunningOperationReconciliationSummary summary = await reconciler.ReconcileAsync(
            now,
            ownerId: "manual-worker",
            maxOperations: 2,
            maxConcurrency: 2);

        Assert.Equal(operationCount, summary.Examined);

        Assert.Equal(operationCount, summary.Completed);

        Assert.Equal(operationCount, handler.CallCount);

    }

    [SkippableFact]
    public async Task ReconcileAsync_UnsupportedOrCorruptCheckpoint_RequiresOperatorRepair()
    {
        RequireSqlCipher();
        LongRunningOperationStore store = Store(_db!);
        DateTimeOffset now = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        LongRunningOperation unsupported = await CreateAsync(store, now.AddMinutes(-5));
        _ = await store.TryAcquireLeaseAsync(
            unsupported.Id,
            "dead-host",
            now.AddMinutes(-5),
            now.AddMinutes(-4));
        _ = await store.SaveCheckpointAsync(
            unsupported.Id,
            "dead-host",
            expectedCheckpointVersion: 0,
            checkpointVersion: 7,
            checkpointPayload: [1],
            checkpointReference: null,
            publicSummary: "Safe checkpoint summary.",
            now.AddMinutes(-4));
        ThrowingRecoveryHandler handler = new(LongRunningOperationKinds.WorkspaceIndex, supportedCheckpointVersion: 1);
        LongRunningOperationReconciler reconciler = new(
            store,
            [handler],
            TimeProvider.System,
            NullLogger<LongRunningOperationReconciler>.Instance);

        LongRunningOperationReconciliationSummary unsupportedSummary = await reconciler.ReconcileAsync(
            now,
            ownerId: "startup-worker",
            maxOperations: 10,
            maxConcurrency: 1);
        LongRunningOperation afterUnsupported = Assert.IsType<LongRunningOperation>(
            await store.GetAsync(unsupported.Id));

        Assert.Equal(1, unsupportedSummary.RequiresAttention);
        Assert.Equal(LongRunningOperationState.ReconciliationRequired, afterUnsupported.State);
        Assert.Equal(LongRunningOperationErrorCodes.UnsupportedCheckpointVersion, afterUnsupported.TerminalErrorCode);
        Assert.Equal(0, handler.CallCount);

        LongRunningOperation corrupt = await CreateAsync(store, now.AddMinutes(-3));
        _ = await store.TryAcquireLeaseAsync(
            corrupt.Id,
            "dead-host",
            now.AddMinutes(-3),
            now.AddMinutes(-2));
        ThrowingRecoveryHandler corruptHandler = new(LongRunningOperationKinds.WorkspaceIndex, supportedCheckpointVersion: 0);
        LongRunningOperationReconciler corruptReconciler = new(
            store,
            [corruptHandler],
            TimeProvider.System,
            NullLogger<LongRunningOperationReconciler>.Instance);

        LongRunningOperationReconciliationSummary corruptSummary = await corruptReconciler.ReconcileAsync(
            now,
            ownerId: "startup-worker-2",
            maxOperations: 10,
            maxConcurrency: 1);
        LongRunningOperation afterCorrupt = Assert.IsType<LongRunningOperation>(
            await store.GetAsync(corrupt.Id));

        Assert.Equal(1, corruptSummary.RequiresAttention);
        Assert.Equal(LongRunningOperationState.ReconciliationRequired, afterCorrupt.State);
        Assert.Equal(LongRunningOperationErrorCodes.CorruptCheckpoint, afterCorrupt.TerminalErrorCode);
    }

    [SkippableFact]

    public async Task RetentionRecoveryAttention_IsDiscoverableAndClaimable_WhileUnrelatedAttentionIsNot()
    {

        RequireSqlCipher();

        LongRunningOperationStore store = Store(_db!);

        DateTimeOffset now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

        LongRunningOperation retention = await store.CreateAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionMutation,
                LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                "Recover quarantined retention bytes.",
                now));

        LongRunningOperation unrelated = await CreateAsync(store, now);

        foreach (LongRunningOperation operation in new[] { retention, unrelated })
        {

            LongRunningOperationLeaseResult lease = await store.TryAcquireLeaseAsync(
                operation.Id,
                "first-worker",
                now,
                now.AddMinutes(1));

            Assert.True(lease.Acquired);

            Assert.True(
                await store.TryTransitionAsync(
                    operation.Id,
                    lease.Operation.Revision,
                    "first-worker",
                    LongRunningOperationState.ReconciliationRequired,
                    now.AddSeconds(1),
                    ErrorCodes.Data.ReconciliationFailed));

        }

        IReadOnlyList<LongRunningOperation> recoverable = await store.FindExpiredAsync(
            now.AddSeconds(2),
            10);

        Assert.Contains(recoverable, operation => operation.Id == retention.Id);

        Assert.DoesNotContain(recoverable, operation => operation.Id == unrelated.Id);

        LongRunningOperationLeaseResult retentionLease = await store.TryAcquireLeaseAsync(
            retention.Id,
            "recovery-worker",
            now.AddSeconds(2),
            now.AddMinutes(2));

        LongRunningOperationLeaseResult unrelatedLease = await store.TryAcquireLeaseAsync(
            unrelated.Id,
            "recovery-worker",
            now.AddSeconds(2),
            now.AddMinutes(2));

        Assert.True(retentionLease.Acquired);

        Assert.False(unrelatedLease.Acquired);

    }

    [SkippableFact]
    public async Task CovenantMaintenanceAttention_IsClaimableOnlyForTheTwoCovenantRetentionKinds()
    {

        RequireSqlCipher();

        LongRunningOperationStore store = Store(_db!);

        DateTimeOffset now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

        LongRunningOperation mutation = await store.CreateAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionMutation,
                LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                "Resume a Covenant reset.",
                now));

        LongRunningOperation factory = await store.CreateAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionFactoryReset,
                LongRunningOperationRecoveryPolicy.RestartIdempotently,
                "Resume a Covenant factory erasure.",
                now));

        LongRunningOperation prune = await store.CreateAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionPrune,
                LongRunningOperationRecoveryPolicy.RestartIdempotently,
                "Do not adopt a prune as Covenant maintenance.",
                now));

        LongRunningOperation unrelated = await CreateAsync(store, now);

        foreach (LongRunningOperation operation in new[] { mutation, factory, prune, unrelated })
        {

            LongRunningOperationLeaseResult lease = await store.TryAcquireLeaseAsync(
                operation.Id,
                "failed-disposition-owner",
                now,
                now.AddMinutes(1));

            Assert.True(lease.Acquired);

            Assert.True(
                await store.TryTransitionAsync(
                    operation.Id,
                    lease.Operation.Revision,
                    "failed-disposition-owner",
                    LongRunningOperationState.ReconciliationRequired,
                    now.AddSeconds(1),
                    ErrorCodes.Covenant.MaintenanceFailed));

        }

        IReadOnlyList<LongRunningOperation> recoverable = await store.FindExpiredAsync(
            now.AddSeconds(2),
            10);

        Assert.Contains(recoverable, operation => operation.Id == mutation.Id);

        Assert.Contains(recoverable, operation => operation.Id == factory.Id);

        Assert.DoesNotContain(recoverable, operation => operation.Id == prune.Id);

        Assert.DoesNotContain(recoverable, operation => operation.Id == unrelated.Id);

        Assert.True((await store.TryAcquireLeaseAsync(
            mutation.Id,
            "recovery-owner",
            now.AddSeconds(2),
            now.AddMinutes(2))).Acquired);

        Assert.True((await store.TryAcquireLeaseAsync(
            factory.Id,
            "recovery-owner",
            now.AddSeconds(2),
            now.AddMinutes(2))).Acquired);

        Assert.False((await store.TryAcquireLeaseAsync(
            prune.Id,
            "recovery-owner",
            now.AddSeconds(2),
            now.AddMinutes(2))).Acquired);

        Assert.False((await store.TryAcquireLeaseAsync(
            unrelated.Id,
            "recovery-owner",
            now.AddSeconds(2),
            now.AddMinutes(2))).Acquired);

    }

    [SkippableFact]
    public async Task BudgetReservationRecovery_ReleasesStrandedReservationIdempotently()
    {
        RequireSqlCipher();
        LongRunningOperationStore store = Store(_db!);
        Guid reservationId = Guid.NewGuid();
        LongRunningOperation operation = await store.CreateAsync(new LongRunningOperationCreateRequest(
            LongRunningOperationKinds.BudgetReservation,
            LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
            "Release stranded reservation.",
            DateTimeOffset.UtcNow,
            BudgetReservationId: reservationId));
        RecordingBudgetReservationService reservations = new();
        BudgetReservationRecoveryHandler handler = new(reservations);

        LongRunningOperationRecoveryResult first = await handler.RecoverAsync(operation, default);
        LongRunningOperationRecoveryResult duplicate = await handler.RecoverAsync(operation, default);

        Assert.Equal(LongRunningOperationState.Completed, first.State);
        Assert.Equal(LongRunningOperationState.Completed, duplicate.State);
        Assert.Equal([reservationId, reservationId], reservations.Released);
    }

    private static Task<LongRunningOperation> CreateAsync(
        LongRunningOperationStore store,
        DateTimeOffset createdAt) =>
        store.CreateAsync(new LongRunningOperationCreateRequest(
            Kind: LongRunningOperationKinds.WorkspaceIndex,
            RecoveryPolicy: LongRunningOperationRecoveryPolicy.RestartIdempotently,
            PublicSummary: "Indexing workspace.",
            CreatedAt: createdAt));

    private static LongRunningOperationStore Store(
        ArcanumDbContext db,
        ICovenantConnectionDrain? drain = null) =>
        new(db, TestOrdinaryConnectionFactory.For(db), drain);

    private static void RequireSqlCipher() =>
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

    /// <summary>
    /// The residual classes a Covenant erasure reads as a handle still holding this database.
    /// </summary>
    /// <remarks>
    /// Only the live-handle classes, because the copies share one directory: the staging and replaced
    /// classes are matched by an engine or primitive prefix rather than against this database's own
    /// name, so another suite's litter in the same directory would answer a question this test is not
    /// asking.
    /// </remarks>
    private static IReadOnlyList<CovenantResidualArtifactClass> LiveHandleSurvivors(string databasePath) =>
    [
        .. CovenantResidualArtifacts.Survivors(databasePath)
            .Where(CovenantResidualArtifacts.LiveHandleClasses.Contains),
    ];

    private async Task ExecuteAsync(string sql)
    {
        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync();
    }

    private sealed class CompletingRecoveryHandler(string kind) : ILongRunningOperationRecoveryHandler
    {
        public string Kind => kind;

        public int SupportedCheckpointVersion => int.MaxValue;

        public int CallCount { get; private set; }

        public Task<LongRunningOperationRecoveryResult> RecoverAsync(
            LongRunningOperation operation,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(LongRunningOperationRecoveryResult.Completed());
        }
    }

    private sealed class ThrowingRecoveryHandler(
        string kind,
        int supportedCheckpointVersion) : ILongRunningOperationRecoveryHandler
    {
        public string Kind => kind;

        public int SupportedCheckpointVersion => supportedCheckpointVersion;

        public int CallCount { get; private set; }

        public Task<LongRunningOperationRecoveryResult> RecoverAsync(
            LongRunningOperation operation,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidDataException("checkpoint could not be decoded");
        }
    }

    private sealed class RecordingBudgetReservationService : IBudgetReservationService
    {
        public List<Guid> Released { get; } = [];

        public Task<Result<BudgetReservation>> ReserveAsync(
            BudgetReservationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result> AdjustAsync(
            Guid reservationId,
            decimal reservedUsd,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ReconcileAsync(
            Guid reservationId,
            decimal actualCostUsd,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ReleaseAsync(
            Guid reservationId,
            CancellationToken cancellationToken = default)
        {
            Released.Add(reservationId);
            return Task.CompletedTask;
        }

        public Task<decimal> GetTodayCommittedSpendAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0m);

        public Task<decimal> GetTodayOutstandingReservationsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0m);

        public Task<int> SweepExpiredAsync(
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }
}
