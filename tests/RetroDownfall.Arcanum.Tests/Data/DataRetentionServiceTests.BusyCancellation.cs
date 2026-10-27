using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retention_busy_begin_observes_cancellation_while_the_independent_writer_remains_held(bool cancel)
    {
        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        await using ArcanumDbContext other = _fixture.CreateContext(_dbPath);

        await other.Database.OpenConnectionAsync();

        SqliteConnection writer = (SqliteConnection)other.Database.GetDbConnection();

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        int providerTimeoutSeconds = connection.DefaultTimeout;

        int nativeBusyTimeoutMilliseconds = await ReadRetentionBusyTimeoutForTestAsync(connection);

        SqliteTransaction? held = null;

        TaskCompletionSource began = new(TaskCreationOptions.RunContinuationsAsynchronously);

        int beginCount = 0;

        int gateCount = 0;

        LongRunningOperationStore operations = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        DataRetentionService service = CreateBoundaryService(new ArcanumSettings(), async (_, _) =>
        {
            gateCount++;

            Assert.Equal(1, SQLitePCL.raw.sqlite3_get_autocommit(connection.Handle));

            LongRunningOperation operation = Assert.Single(await operations.ListAsync(new(Limit: 10)));

            Assert.Equal(2, operation.CheckpointVersion);

            held = writer.BeginTransaction(deferred: false);

            SQLitePCL.raw.sqlite3_trace(connection.Handle, (object? _, string sql) =>
            {
                if (sql.TrimStart().StartsWith("BEGIN IMMEDIATE", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref beginCount);

                    began.TrySetResult();
                }
            }, null);
        });

        DataRetentionRequest request = new(DataRetentionOperation.DeleteSession, sessionId);

        DataRetentionPlan plan = await service.PlanAsync(request);

        using CancellationTokenSource cancellation = new();

        Task<Result<DataRetentionApplyResult>> pending = Task.Run(() => service.ApplyAsync(new(request, plan.PlanId), cancellation.Token));

        bool completedWhileHeld = false;

        Result<DataRetentionApplyResult>? result = null;

        Exception? failure = null;

        Stopwatch elapsed = new();

        try
        {
            await began.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.NotNull(held);

            elapsed.Start();

            if (cancel)
            {
                cancellation.Cancel();

                completedWhileHeld = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromMilliseconds(2500))) == pending;
            }
        }
        finally
        {
            if (held is not null)
            {
                await held.RollbackAsync(CancellationToken.None);

                await held.DisposeAsync();
            }

            failure = await Record.ExceptionAsync(async () => result = await pending);

            elapsed.Stop();

            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);
        }

        Assert.Equal(providerTimeoutSeconds, connection.DefaultTimeout);

        Assert.Equal(nativeBusyTimeoutMilliseconds, await ReadRetentionBusyTimeoutForTestAsync(connection));

        Assert.Equal(1, gateCount);

        Assert.True(beginCount > 0);

        if (cancel)
        {
            Assert.True(completedWhileHeld, $"Actual BEGIN IMMEDIATE ignored cancellation while the native writer stayed held; providerTimeoutSeconds={providerTimeoutSeconds}; nativeBusyTimeoutMilliseconds={nativeBusyTimeoutMilliseconds}; elapsed={elapsed.Elapsed}; failure={failure?.GetType().Name}; result={result?.IsSuccess}.");

            OperationCanceledException cancelled = Assert.IsAssignableFrom<OperationCanceledException>(failure);

            Assert.Equal(cancellation.Token, cancelled.CancellationToken);

            Assert.Equal(1, await CountAllAsync("Sessions"));

            Assert.True(File.Exists(attachment.AbsolutePath));
        }
        else
        {
            Assert.Null(failure);

            Assert.True(result!.IsSuccess);

            Assert.Equal(0, await CountAllAsync("Sessions"));

            Assert.False(File.Exists(attachment.AbsolutePath));
        }
    }

    [SkippableFact]
    public async Task Retention_native_ledger_cancellation_bounds_the_actual_owner_revision_update_attempt()
    {
        RequireSqlCipher();

        LongRunningOperationStore operations = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        DateTimeOffset now = DateTimeOffset.UtcNow;

        LongRunningOperation operation = await operations.CreateAsync(new(
            LongRunningOperationKinds.DataRetentionMutation,
            LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
            "Native bounded ledger attempt.", now));

        LongRunningOperationLeaseResult lease = await operations.TryAcquireLeaseAsync(
            operation.Id, "ledger-budget-owner", now, now.AddMinutes(5));

        Assert.True(lease.Acquired);

        await using ArcanumDbContext other = _fixture.CreateContext(_dbPath);

        await other.Database.OpenConnectionAsync();

        SqliteConnection writer = (SqliteConnection)other.Database.GetDbConnection();

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        int providerTimeoutSeconds = connection.DefaultTimeout;

        int nativeBusyTimeoutMilliseconds = await ReadRetentionBusyTimeoutForTestAsync(connection);

        SqliteTransaction held = writer.BeginTransaction(deferred: false);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        SQLitePCL.raw.sqlite3_trace(connection.Handle, (object? _, string sql) =>
        {
            if (sql.Contains("UPDATE \"LongRunningOperations\"", StringComparison.Ordinal)
                && sql.Contains("SET \"State\"", StringComparison.Ordinal)
                && sql.Contains("\"TerminalErrorCode\"", StringComparison.Ordinal)) entered.TrySetResult();
        }, null);

        using CancellationTokenSource budget = new();

        Task<bool> pending = Task.Run(() => operations.TryTransitionAsync(operation.Id, lease.Operation.Revision,
            lease.Operation.LeaseOwner, LongRunningOperationState.Failed, DateTimeOffset.UtcNow,
            ErrorCodes.Data.Conflict, budget.Token));

        bool completedWhileHeld = false;

        bool? updated = null;

        Exception? failure = null;

        Stopwatch elapsed = new();

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            elapsed.Start();

            budget.Cancel();

            completedWhileHeld = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromMilliseconds(2500))) == pending;
        }
        finally
        {
            await held.RollbackAsync(CancellationToken.None);

            await held.DisposeAsync();

            failure = await Record.ExceptionAsync(async () => updated = await pending);

            elapsed.Stop();

            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);
        }

        Assert.Equal(providerTimeoutSeconds, connection.DefaultTimeout);

        Assert.Equal(nativeBusyTimeoutMilliseconds, await ReadRetentionBusyTimeoutForTestAsync(connection));

        LongRunningOperation after = Assert.IsType<LongRunningOperation>(await operations.GetAsync(operation.Id));

        Assert.True(completedWhileHeld, $"Real guarded ledger UPDATE ignored its per-call budget while the native writer stayed held; providerTimeoutSeconds={providerTimeoutSeconds}; nativeBusyTimeoutMilliseconds={nativeBusyTimeoutMilliseconds}; elapsed={elapsed.Elapsed}; updated={updated}; failure={failure?.GetType().Name}; beforeRevision={lease.Operation.Revision}; afterRevision={after.Revision}; afterState={after.State}.");

        OperationCanceledException cancelled = Assert.IsAssignableFrom<OperationCanceledException>(failure);

        Assert.Equal(budget.Token, cancelled.CancellationToken);

        Assert.Equal(lease.Operation, after);
    }

    [SkippableFact]
    public async Task Retention_busy_acquisition_exhaustion_refuses_before_mutation_and_retains_truthful_recovery()
    {
        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        await using ArcanumDbContext other = _fixture.CreateContext(_dbPath);

        await other.Database.OpenConnectionAsync();

        SqliteConnection writer = (SqliteConnection)other.Database.GetDbConnection();

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        int originalTimeout = connection.DefaultTimeout;

        int originalBusy = await ReadRetentionBusyTimeoutForTestAsync(connection);

        SqliteTransaction? held = null;

        TaskCompletionSource began = new(TaskCreationOptions.RunContinuationsAsynchronously);

        FakeTimeProvider clock = new();

        DataRetentionService service = CreateBoundaryService(new ArcanumSettings(), (_, _) =>
        {
            held = writer.BeginTransaction(deferred: false);

            SQLitePCL.raw.sqlite3_trace(connection.Handle, (object? _, string sql) =>
            {
                if (sql.TrimStart().StartsWith("BEGIN IMMEDIATE", StringComparison.OrdinalIgnoreCase)) began.TrySetResult();
            }, null);

            return Task.CompletedTask;
        }, timeProvider: clock);

        DataRetentionRequest request = new(DataRetentionOperation.DeleteSession, sessionId);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Task<Result<DataRetentionApplyResult>> pending = Task.Run(() => service.ApplyAsync(new(request, plan.PlanId)));

        bool concludedWhileHeld = false;

        Result<DataRetentionApplyResult>? result = null;

        Exception? failure = null;

        try
        {
            await began.Task.WaitAsync(TimeSpan.FromSeconds(10));

            concludedWhileHeld = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromMilliseconds(7500))) == pending;
        }
        finally
        {
            if (held is not null)
            {
                await held.RollbackAsync(CancellationToken.None);

                await held.DisposeAsync();
            }

            failure = await Record.ExceptionAsync(async () => result = await pending);

            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);
        }

        Assert.Null(failure);

        Assert.True(concludedWhileHeld, $"Acquisition outlived its five-second budget plus bounded attempt/cleanup allowance; resultSuccess={result?.IsSuccess}.");

        Assert.True(result!.IsFailure);

        Assert.Equal(ErrorCodes.Data.Conflict, result.Error.Code);

        Assert.Contains("busy", result.Error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("requires operator review", result.Error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(originalTimeout, connection.DefaultTimeout);

        Assert.Equal(originalBusy, await ReadRetentionBusyTimeoutForTestAsync(connection));

        Assert.Equal(1, await CountAllAsync("Sessions"));

        Assert.True(File.Exists(attachment.AbsolutePath));

        LongRunningOperationStore store = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation operation = Assert.Single(await store.ListAsync(new(Limit: 10)));

        Assert.Equal(2, operation.CheckpointVersion);

        Assert.NotEqual(LongRunningOperationState.Completed, operation.State);

        // No fake marker succeeded while the independent writer was held. Existing expiry and a
        // physically fresh handler prove that the retained journal owns the eventual disposition.
        Assert.Equal(LongRunningOperationState.Running, operation.State);

        Assert.NotNull(operation.LeaseOwner);

        clock.Advance(TimeSpan.FromMinutes(10));

        await using ArcanumDbContext fresh = _fixture.CreateContext(_dbPath);

        Assert.NotSame(connection, fresh.Database.GetDbConnection());

        LongRunningOperationStore freshStore = new(fresh, TestOrdinaryConnectionFactory.For(fresh));

        DataRetentionService recovery = new(fresh, new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
            freshStore, clock, NullLogger<DataRetentionService>.Instance, FixtureLabeledArtifactGuard.For(fresh),
            _attachmentsRoot, _filesRoot, _logsRoot);

        LongRunningOperationReconciler reconciler = new(freshStore, [new DataRetentionMutationRecoveryHandler(recovery)],
            clock, NullLogger<LongRunningOperationReconciler>.Instance, new LongRunningOperationOwnership());

        LongRunningOperationReconciliationSummary summary = await reconciler.ReconcileNowAsync("expired-busy-acquisition", 10, 1);

        Assert.Equal(1, summary.Failed);

        Assert.Equal(0, summary.Completed);

        Assert.Equal(0, summary.RequiresAttention);

        Assert.True(File.Exists(attachment.AbsolutePath));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retention_native_transition_cleanup_never_replays_or_masks_a_known_outcome(bool commandSucceeded)
    {
        RequireSqlCipher();

        LongRunningOperationStore store = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        DateTimeOffset now = DateTimeOffset.UtcNow;

        LongRunningOperation operation = await store.CreateAsync(new(LongRunningOperationKinds.DataRetentionMutation,
            LongRunningOperationRecoveryPolicy.ReconcileAndComplete, "Cleanup proof control.", now));

        LongRunningOperationLeaseResult lease = await store.TryAcquireLeaseAsync(operation.Id, "cleanup-proof-owner", now, now.AddMinutes(5));

        Assert.True(lease.Acquired);

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        int originalProvider = connection.DefaultTimeout;

        int originalNative = await ReadRetentionBusyTimeoutForTestAsync(connection);

        await using ArcanumDbContext other = _fixture.CreateContext(_dbPath);

        await other.Database.OpenConnectionAsync();

        SqliteConnection writer = (SqliteConnection)other.Database.GetDbConnection();

        SqliteTransaction? held = commandSucceeded ? null : writer.BeginTransaction(deferred: false);

        int attempts = 0;

        int cleanupCalls = 0;

        SQLitePCL.raw.sqlite3_trace(connection.Handle, (object? _, string sql) =>
        {
            if (sql.Contains("UPDATE \"LongRunningOperations\"", StringComparison.Ordinal)
                && sql.Contains("SET \"State\"", StringComparison.Ordinal)) Interlocked.Increment(ref attempts);
        }, null);

        const string canary = "PRIVATE-CLEANUP-TEXT /private/operator/path";

        store.BeforeTransitionAttemptRestoreForTesting = () =>
        {
            cleanupCalls++;

            throw new IOException(canary);
        };

        BusyAttemptLogSink sink = new();

        Serilog.ILogger previousLogger = Serilog.Log.Logger;

        using Serilog.Core.Logger capture = new Serilog.LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

        Serilog.Log.Logger = capture;

        using CancellationTokenSource token = new();

        Task<bool> pending = Task.Run(() => store.TryTransitionAsync(operation.Id, lease.Operation.Revision,
            lease.Operation.LeaseOwner, LongRunningOperationState.Failed, now, ErrorCodes.Data.Conflict, token.Token));

        bool concludedWhileHeld = false;

        bool? changed = null;

        Exception? failure = null;

        try
        {
            concludedWhileHeld = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromMilliseconds(2500))) == pending;
        }
        finally
        {
            if (held is not null)
            {
                await held.RollbackAsync();

                await held.DisposeAsync();
            }

            failure = await Record.ExceptionAsync(async () => changed = await pending);

            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);

            Serilog.Log.Logger = previousLogger;
        }

        Assert.True(concludedWhileHeld, $"Cleanup failure retried a native outcome: successExpected={commandSucceeded}; attempts={attempts}; changed={changed}; failure={failure?.GetType().Name}.");

        // The pinned provider can step the same SQL several times within one synchronous attempt.
        // Cleanup runs once per managed attempt, so it is the no-replay oracle.
        Assert.True(attempts > 0);

        Assert.Equal(1, cleanupCalls);

        Assert.Equal(originalProvider, connection.DefaultTimeout);

        Assert.Equal(originalNative, await ReadRetentionBusyTimeoutForTestAsync(connection));

        LongRunningOperation after = Assert.IsType<LongRunningOperation>(await store.GetAsync(operation.Id));

        if (commandSucceeded)
        {
            Assert.Null(failure);

            Assert.Equal(true, changed);

            Assert.Equal(lease.Operation.Revision + 1, after.Revision);
        }
        else
        {
            SqliteException primary = Assert.IsType<SqliteException>(failure);

            Assert.Equal(5, primary.SqliteErrorCode);

            Assert.Equal(lease.Operation, after);
        }

        Serilog.Events.LogEvent warning = Assert.Single(sink.Events);

        Assert.Null(warning.Exception);

        Assert.DoesNotContain(canary, warning.RenderMessage(), StringComparison.Ordinal);

        Assert.All(warning.Properties.Values, value => Assert.DoesNotContain(canary, value.ToString(), StringComparison.Ordinal));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retention_upstream_ledger_stages_observe_cancellation_before_source_mutation(bool journal)
    {
        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        await using ArcanumDbContext other = _fixture.CreateContext(_dbPath);

        await other.Database.OpenConnectionAsync();

        SqliteConnection writer = (SqliteConnection)other.Database.GetDbConnection();

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        SqliteTransaction? held = journal ? null : writer.BeginTransaction(deferred: false);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        SQLitePCL.raw.sqlite3_trace(connection.Handle, (object? _, string sql) =>
        {
            bool target = journal
                ? sql.Contains("UPDATE \"LongRunningOperations\"", StringComparison.Ordinal)
                    && sql.Contains("\"CheckpointVersion\"", StringComparison.Ordinal)
                : sql.TrimStart().StartsWith("INSERT INTO \"LongRunningOperations\"", StringComparison.OrdinalIgnoreCase);

            if (target) entered.TrySetResult();
        }, null);

        LongRunningOperationStore inner = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        HeartbeatCountingOperationStore store = new(inner)
        {
            AfterStartAsync = async _ =>
            {
                if (!journal) return;

                LongRunningOperation started = Assert.Single(await inner.ListAsync(new(Limit: 10)));

                Assert.Equal(0, started.CheckpointVersion);

                Assert.Equal(1, SQLitePCL.raw.sqlite3_get_autocommit(connection.Handle));

                held = writer.BeginTransaction(deferred: false);
            },
        };

        FakeTimeProvider clock = new();

        DataRetentionService service = CreateBoundaryService(new ArcanumSettings(), static (_, _) => Task.CompletedTask,
            operationStore: store, timeProvider: clock);

        DataRetentionRequest request = new(DataRetentionOperation.DeleteSession, sessionId);

        DataRetentionPlan plan = await service.PlanAsync(request);

        using CancellationTokenSource cancellation = new();

        Task<Result<DataRetentionApplyResult>> pending = Task.Run(() => service.ApplyAsync(new(request, plan.PlanId), cancellation.Token));

        bool completedWhileHeld = false;

        Exception? failure = null;

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.NotNull(held);

            cancellation.Cancel();

            completedWhileHeld = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromMilliseconds(2500))) == pending;
        }
        finally
        {
            if (held is not null)
            {
                await held.RollbackAsync();

                await held.DisposeAsync();
            }

            failure = await Record.ExceptionAsync(async () => await pending);

            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);
        }

        Assert.True(completedWhileHeld, $"Upstream retention ledger stage ignored cancellation under a held writer: stage={(journal ? "SaveCheckpoint" : "SingleFlightInsert")}; failure={failure?.GetType().Name}.");

        OperationCanceledException cancelled = Assert.IsAssignableFrom<OperationCanceledException>(failure);

        Assert.Equal(cancellation.Token, cancelled.CancellationToken);

        Assert.Equal(1, await CountAllAsync("Sessions"));

        Assert.True(File.Exists(attachment.AbsolutePath));

        IReadOnlyList<LongRunningOperation> retained = await inner.ListAsync(new(Limit: 10));

        if (!journal)
        {
            Assert.Empty(retained);

            return;
        }

        LongRunningOperation interrupted = Assert.Single(retained);

        Assert.Equal(0, interrupted.CheckpointVersion);

        Assert.Equal(LongRunningOperationState.Running, interrupted.State);

        Assert.NotNull(interrupted.LeaseOwner);

        clock.Advance(TimeSpan.FromMinutes(10));

        await using ArcanumDbContext fresh = _fixture.CreateContext(_dbPath);

        LongRunningOperationStore freshStore = new(fresh, TestOrdinaryConnectionFactory.For(fresh));

        DataRetentionService recovery = new(fresh, new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
            freshStore, clock, NullLogger<DataRetentionService>.Instance, FixtureLabeledArtifactGuard.For(fresh),
            _attachmentsRoot, _filesRoot, _logsRoot);

        LongRunningOperationReconciler reconciler = new(freshStore, [new DataRetentionMutationRecoveryHandler(recovery)],
            clock, NullLogger<LongRunningOperationReconciler>.Instance, new LongRunningOperationOwnership());

        LongRunningOperationReconciliationSummary summary = await reconciler.ReconcileNowAsync("expired-before-journal", 10, 1);

        Assert.Equal(1, summary.Abandoned);

        Assert.Equal(0, summary.Completed);

        Assert.Equal(0, summary.RequiresAttention);

        Assert.Equal(1, await CountAllAsync("Sessions"));

        Assert.True(File.Exists(attachment.AbsolutePath));
    }

    [SkippableFact]
    public async Task Retention_native_renewal_cancellation_bounds_the_actual_fresh_lease_update_attempt()
    {
        RequireSqlCipher();

        RecordingFreshOrdinaryConnectionFactory factory = TestOrdinaryConnectionFactory.For(_db!);

        int? providerAtRelease = null;

        int? nativeAtRelease = null;

        ObservedReleaseConnectionFactory observed = new(factory, connection =>
        {
            providerAtRelease = connection.DefaultTimeout;

            nativeAtRelease = ReadRetentionBusyTimeoutForTestAsync(connection).GetAwaiter().GetResult();

            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);
        });

        LongRunningOperationStore store = new(_db!, observed);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        LongRunningOperation operation = await store.CreateAsync(new(LongRunningOperationKinds.DataRetentionPrune,
            LongRunningOperationRecoveryPolicy.RestartIdempotently, "Fresh heartbeat cancellation.", now));

        LongRunningOperationLeaseResult lease = await store.TryAcquireLeaseAsync(operation.Id,
            "fresh-heartbeat-owner", now, now.AddMinutes(5));

        Assert.True(lease.Acquired);

        await using ArcanumDbContext other = _fixture.CreateContext(_dbPath);

        await other.Database.OpenConnectionAsync();

        SqliteConnection writer = (SqliteConnection)other.Database.GetDbConnection();

        SqliteConnection? actual = null;

        SqliteTransaction? held = null;

        factory.BlockNextOpen();

        factory.BlockNextRelease();

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using CancellationTokenSource cancellation = new();

        Task<bool> pending = Task.Run(() => store.RenewLeaseAsync(operation.Id, lease.Operation.LeaseOwner!,
            now, now.AddMinutes(10), cancellation.Token));

        bool releaseObservedWhileHeld = false;

        bool completedWhileHeld = false;

        int? providerBefore = null;

        int? nativeBefore = null;

        bool? changed = null;

        Exception? failure = null;

        try
        {
            await factory.OpenBlocked.WaitAsync(TimeSpan.FromSeconds(10));

            actual = Assert.Single(factory.Opened);

            providerBefore = actual.DefaultTimeout;

            nativeBefore = await ReadRetentionBusyTimeoutForTestAsync(actual);

            held = writer.BeginTransaction(deferred: false);

            SQLitePCL.raw.sqlite3_trace(actual.Handle, (object? _, string sql) =>
            {
                if (sql.Contains("UPDATE \"LongRunningOperations\"", StringComparison.Ordinal)
                    && sql.Contains("SET \"HeartbeatAt\"", StringComparison.Ordinal)) entered.TrySetResult();
            }, null);

            factory.AllowOpen();

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            cancellation.Cancel();

            releaseObservedWhileHeld = await Task.WhenAny(factory.ReleaseEntered, Task.Delay(TimeSpan.FromMilliseconds(2500))) == factory.ReleaseEntered;

            factory.AllowRelease();

            if (releaseObservedWhileHeld) completedWhileHeld = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(1))) == pending;
        }
        finally
        {
            factory.AllowOpen();

            factory.AllowRelease();

            if (held is not null)
            {
                await held.RollbackAsync();

                await held.DisposeAsync();
            }

            failure = await Record.ExceptionAsync(async () => changed = await pending);

            if (actual?.State == System.Data.ConnectionState.Open)
            {
                SQLitePCL.raw.sqlite3_trace(actual.Handle, (SQLitePCL.strdelegate_trace)null!, null);
            }
        }

        LongRunningOperation after = Assert.IsType<LongRunningOperation>(await store.GetAsync(operation.Id));

        Assert.Equal(0, factory.LiveLeaseCount);

        Assert.True(completedWhileHeld, $"Actual isolated heartbeat UPDATE ignored cancellation: providerBefore={providerBefore}; nativeBefore={nativeBefore}; releaseObservedWhileHeld={releaseObservedWhileHeld}; changed={changed}; failure={failure?.GetType().Name}; beforeRevision={lease.Operation.Revision}; afterRevision={after.Revision}.");

        OperationCanceledException cancelled = Assert.IsAssignableFrom<OperationCanceledException>(failure);

        Assert.Equal(cancellation.Token, cancelled.CancellationToken);

        Assert.Equal(providerBefore, providerAtRelease);

        Assert.Equal(nativeBefore, nativeAtRelease);

        Assert.Equal(lease.Operation, after);
    }

    [SkippableTheory]
    [InlineData("start", true)]
    [InlineData("start", false)]
    [InlineData("checkpoint", true)]
    [InlineData("checkpoint", false)]
    [InlineData("renew", true)]
    [InlineData("renew", false)]
    public async Task Retention_native_known_result_survives_late_cancellation_and_cleanup_failure(string stage, bool match)
    {
        RequireSqlCipher();

        RecordingFreshOrdinaryConnectionFactory factory = TestOrdinaryConnectionFactory.For(_db!);

        LongRunningOperationStore store = new(_db!, factory);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        LongRunningOperationCreateRequest request = new(LongRunningOperationKinds.DataRetentionPrune,
            LongRunningOperationRecoveryPolicy.RestartIdempotently, "Known native ledger outcome.", now);

        LongRunningOperation? before = stage == "start" && match
            ? null : await store.TryStartSingleFlightAsync(request, "known-outcome-owner", now, now.AddMinutes(5));

        using CancellationTokenSource cancellation = new();

        int cleanupCalls = 0;

        store.BeforeTransitionAttemptRestoreForTesting = () =>
        {
            cleanupCalls++;

            cancellation.Cancel();

            throw new IOException("PRIVATE-LATE-CLEANUP /operator/private-path");
        };

        if (stage == "start")
        {
            LongRunningOperation? created = await store.TryStartSingleFlightAsync(request, "known-outcome-owner",
                now, now.AddMinutes(5), cancellation.Token);

            Assert.Equal(match, created is not null);
        }
        else if (stage == "checkpoint")
        {
            Assert.Equal(match, await store.SaveCheckpointAsync(before!.Id, match ? "known-outcome-owner" : "foreign-owner",
                0, 2, [1, 2, 3], null, "Known checkpoint.", now, cancellation.Token));
        }
        else
        {
            Assert.Equal(match, await store.RenewLeaseAsync(before!.Id, match ? "known-outcome-owner" : "foreign-owner",
                now, now.AddMinutes(10), cancellation.Token));
        }

        Assert.Equal(1, cleanupCalls);

        LongRunningOperation actual = Assert.Single(await store.ListAsync(new(Limit: 10)));

        Assert.Equal(stage != "start" && match ? 2 : 1, actual.Revision);

        Assert.Equal(stage == "checkpoint" && match ? 2 : 0, actual.CheckpointVersion);

        Assert.Equal(0, factory.LiveLeaseCount);
    }

    [SkippableTheory]
    [InlineData(0, 0)]
    [InlineData(4, 17)]
    public async Task Retention_native_short_attempt_restores_custom_and_infinite_settings(int provider, int native)
    {
        RequireSqlCipher();

        LongRunningOperationStore store = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        DateTimeOffset now = DateTimeOffset.UtcNow;

        LongRunningOperation operation = (await store.TryStartSingleFlightAsync(new(LongRunningOperationKinds.DataRetentionPrune,
            LongRunningOperationRecoveryPolicy.RestartIdempotently, "Custom native settings.", now),
            "settings-owner", now, now.AddMinutes(5)))!;

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        int originalProvider = connection.DefaultTimeout;

        int originalNative = await ReadRetentionBusyTimeoutForTestAsync(connection);

        using CancellationTokenSource cancellation = new();

        try
        {
            connection.DefaultTimeout = provider;

            Assert.Equal(SQLitePCL.raw.SQLITE_OK, SQLitePCL.raw.sqlite3_busy_timeout(connection.Handle, native));

            Assert.True(await store.TryTransitionAsync(operation.Id, operation.Revision, operation.LeaseOwner,
                LongRunningOperationState.Failed, now, ErrorCodes.Data.Conflict, cancellation.Token));

            Assert.Equal(provider, connection.DefaultTimeout);

            Assert.Equal(native, await ReadRetentionBusyTimeoutForTestAsync(connection));
        }
        finally
        {
            connection.DefaultTimeout = originalProvider;

            _ = SQLitePCL.raw.sqlite3_busy_timeout(connection.Handle, originalNative);
        }
    }

    [SkippableFact]
    public async Task Retention_busy_later_prune_candidate_preserves_progress_and_pending_recovery()
    {
        RequireSqlCipher();

        _ = await SeedSessionAsync(pinned: false);

        _ = await SeedSessionAsync(pinned: false);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.ArchivedSessions = EnabledRule(days: 30);

        await using ArcanumDbContext other = _fixture.CreateContext(_dbPath);

        await other.Database.OpenConnectionAsync();

        SqliteConnection writer = (SqliteConnection)other.Database.GetDbConnection();

        SqliteTransaction? held = null;

        int gates = 0;

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        LongRunningOperationStore store = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        DataRetentionService service = CreateBoundaryService(settings, async (_, _) =>
        {
            if (++gates != 2) return;

            Assert.Equal(1, await CountAllAsync("Sessions"));

            LongRunningOperation current = Assert.Single(await store.ListAsync(new(Limit: 10)));

            string checkpoint = System.Text.Encoding.UTF8.GetString(current.CheckpointPayload!);

            Assert.Contains("\n1\n", checkpoint, StringComparison.Ordinal);

            Assert.Contains("\nP:", checkpoint, StringComparison.Ordinal);

            held = writer.BeginTransaction(deferred: false);

            entered.TrySetResult();
        }, operationStore: store);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Assert.Equal(2, plan.CandidateIds.Length);

        Task<Result<DataRetentionApplyResult>> pending = Task.Run(() => service.ApplyAsync(new(request, plan.PlanId)));

        bool concludedWhileHeld = false;

        Result<DataRetentionApplyResult>? result = null;

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));

            concludedWhileHeld = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(8))) == pending;
        }
        finally
        {
            if (held is not null)
            {
                await held.RollbackAsync();

                await held.DisposeAsync();
            }

            result = await pending;
        }

        Assert.True(concludedWhileHeld);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.ReconciliationFailed, result.Error.Code);

        Assert.Equal(1, await CountAllAsync("Sessions"));

        LongRunningOperation retained = Assert.Single(await store.ListAsync(new(Limit: 10)));

        Assert.NotEqual(LongRunningOperationState.Completed, retained.State);

        string retainedCheckpoint = System.Text.Encoding.UTF8.GetString(retained.CheckpointPayload!);

        Assert.Contains("\n1\n", retainedCheckpoint, StringComparison.Ordinal);

        Assert.Contains("\nP:", retainedCheckpoint, StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData("fresh")]
    [InlineData("unreadable")]
    [InlineData("foreign")]
    [InlineData("nonzero")]
    [InlineData("payload")]
    [InlineData("reference")]
    [InlineData("cas-loser")]
    public async Task Retention_busy_initial_large_snapshot_requires_positive_current_no_effect_proof(string proof)
    {
        RequireSqlCipher();

        await SeedLargeWorkspacePruneAsync(1_001, 0);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.WorkspaceIndexes = EnabledRule(days: 30);

        FakeTimeProvider clock = new();

        await using ArcanumDbContext other = _fixture.CreateContext(_dbPath);

        await other.Database.OpenConnectionAsync();

        SqliteConnection writer = (SqliteConnection)other.Database.GetDbConnection();

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        SqliteTransaction? held = null;

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        bool acquisitionEntered = false;

        int transitionCalls = 0;

        LongRunningOperationStore inner = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        HeartbeatCountingOperationStore store = new(inner)
        {
            AfterStartAsync = async _ =>
            {
                LongRunningOperation current = Assert.Single(await inner.ListAsync(new(Limit: 10)));

                Assert.Equal(0, current.CheckpointVersion);

                Assert.Null(current.CheckpointPayload);

                Assert.Null(current.CheckpointReference);

                if (proof == "foreign")
                {
                    await ExecuteAsync("UPDATE LongRunningOperations SET LeaseOwner = 'independent-adopter', Revision = Revision + 1 WHERE Id = @id",
                        ("@id", current.Id.ToString("N")));
                }
                else if (proof == "nonzero")
                {
                    Assert.True(await inner.SaveCheckpointAsync(current.Id, current.LeaseOwner!, 0, 2,
                        [1], null, "Deliberately uncertain progress control.", clock.GetUtcNow()));
                }

                if (proof == "payload")
                {
                    await ExecuteAsync("UPDATE LongRunningOperations SET CheckpointPayload = @payload, Revision = Revision + 1 WHERE Id = @id",
                        ("@payload", new byte[] { 1 }), ("@id", current.Id.ToString("N")));
                }
                else if (proof == "reference")
                {
                    await ExecuteAsync("UPDATE LongRunningOperations SET CheckpointReference = 'uncertain-reference', Revision = Revision + 1 WHERE Id = @id",
                        ("@id", current.Id.ToString("N")));
                }

                held = writer.BeginTransaction(deferred: false);

                SQLitePCL.raw.sqlite3_trace(connection.Handle, (object? _, string sql) =>
                {
                    if (sql.TrimStart().StartsWith("BEGIN IMMEDIATE", StringComparison.OrdinalIgnoreCase))
                    {
                        acquisitionEntered = true;

                        entered.TrySetResult();
                    }
                }, null);
            },
            BeforeTransition = state =>
            {
                if (proof != "cas-loser" || state != LongRunningOperationState.Failed) return;

                transitionCalls++;

                // The original contention ends only after classification read its current owner.
                // A competing native write then wins the exact owner/revision fence.
                held!.Rollback();

                held.Dispose();

                held = null;

                LongRunningOperation current = Assert.Single(inner.ListAsync(new(Limit: 10)).GetAwaiter().GetResult());

                ExecuteAsync("UPDATE LongRunningOperations SET LeaseOwner = 'independent-adopter', Revision = Revision + 1 WHERE Id = @id",
                    ("@id", current.Id.ToString("N"))).GetAwaiter().GetResult();
            },
            BeforeGetAsync = (_, _) => proof == "unreadable" && acquisitionEntered
                ? Task.FromException(new IOException("PRIVATE-EVIDENCE /operator/private")) : Task.CompletedTask,
        };

        DataRetentionService service = CreateService(settings, operationStore: store, timeProvider: clock);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Assert.Equal(1_001, plan.CandidateIds.Length);

        Task<Result<DataRetentionApplyResult>> pending = Task.Run(() => service.ApplyAsync(new(request, plan.PlanId)));

        bool concludedWithinGuard = false;

        Result<DataRetentionApplyResult>? result = null;

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));

            concludedWithinGuard = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(8))) == pending;
        }
        finally
        {
            if (held is not null)
            {
                await held.RollbackAsync();

                await held.DisposeAsync();
            }

            result = await pending;

            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);
        }

        Assert.True(concludedWithinGuard);

        Assert.True(result.IsFailure);

        Assert.Equal(proof == "fresh" ? ErrorCodes.Data.Conflict : ErrorCodes.Data.ReconciliationFailed, result.Error.Code);

        Assert.Equal(1_001, await CountLargeWorkspacePruneAsync());

        Assert.Equal(0, await CountAllAsync("data_retention_prune_snapshots"));

        LongRunningOperation retained = Assert.Single(await inner.ListAsync(new(Limit: 10)));

        Assert.Equal(LongRunningOperationState.Running, retained.State);

        Assert.Equal(proof == "nonzero" ? 2 : 0, retained.CheckpointVersion);

        if (proof is "foreign" or "cas-loser") Assert.Equal("independent-adopter", retained.LeaseOwner);

        else Assert.StartsWith("data-retention:", retained.LeaseOwner, StringComparison.Ordinal);

        if (proof == "payload") Assert.NotNull(retained.CheckpointPayload);

        if (proof == "reference") Assert.NotNull(retained.CheckpointReference);

        if (proof == "cas-loser") Assert.Equal(1, transitionCalls);

        if (proof != "fresh") return;

        Assert.Contains("recover", result.Error.Message, StringComparison.OrdinalIgnoreCase);

        clock.Advance(TimeSpan.FromMinutes(10));

        await using ArcanumDbContext fresh = _fixture.CreateContext(_dbPath);

        LongRunningOperationStore freshStore = new(fresh, TestOrdinaryConnectionFactory.For(fresh));

        DataRetentionService recovery = new(fresh, new TestOptionsMonitor<ArcanumSettings>(settings), freshStore, clock,
            NullLogger<DataRetentionService>.Instance, FixtureLabeledArtifactGuard.For(fresh),
            _attachmentsRoot, _filesRoot, _logsRoot);

        LongRunningOperationReconciler reconciler = new(freshStore, [new DataRetentionRecoveryHandler(recovery)],
            clock, NullLogger<LongRunningOperationReconciler>.Instance, new LongRunningOperationOwnership());

        LongRunningOperationReconciliationSummary recovered = await reconciler.ReconcileNowAsync("retained-initial-prune", 10, 1);

        Assert.Equal(1, recovered.Completed);

        Assert.Equal(0, recovered.RequiresAttention);

        Assert.Equal(0, await CountLargeWorkspacePruneAsync());
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retention_native_renewal_never_replays_after_fresh_lease_cleanup_failure(bool cancellable)
    {
        RequireSqlCipher();

        RecordingFreshOrdinaryConnectionFactory inner = TestOrdinaryConnectionFactory.For(_db!);

        FaultAfterReleaseConnectionFactory factory = new(inner);

        LongRunningOperationStore store = new(_db!, factory);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        LongRunningOperation started = (await store.TryStartSingleFlightAsync(new(LongRunningOperationKinds.DataRetentionPrune,
            LongRunningOperationRecoveryPolicy.RestartIdempotently, "Fresh lease cleanup outcome.", now),
            "lease-cleanup-owner", now, now.AddMinutes(5)))!;

        using CancellationTokenSource cancellation = new();

        Assert.True(await store.RenewLeaseAsync(started.Id, started.LeaseOwner!, now, now.AddMinutes(10),
            cancellable ? cancellation.Token : CancellationToken.None));

        LongRunningOperation actual = Assert.IsType<LongRunningOperation>(await store.GetAsync(started.Id));

        Assert.Equal(started.Revision + 1, actual.Revision);

        Assert.Equal(1, factory.Releases);

        Assert.Equal(0, inner.LiveLeaseCount);
    }

    private sealed class FaultAfterReleaseConnectionFactory(
        IGrimoireOrdinaryConnectionFactory inner) : IGrimoireOrdinaryConnectionFactory
    {
        internal int Releases { get; private set; }

        public Task<Result<IGrimoireOrdinaryConnectionLease>> AcquireScopedAsync(
            SqliteConnection connection,
            RetroDownfall.Arcanum.Infrastructure.Data.Covenant.CovenantSqliteConnectionMode mode,
            CancellationToken cancellationToken) => inner.AcquireScopedAsync(connection, mode, cancellationToken);

        public async Task<Result<IGrimoireOrdinaryConnectionLease>> OpenFreshAsync(
            GrimoireOrdinaryFreshConnectionKind kind,
            CancellationToken cancellationToken)
        {
            Result<IGrimoireOrdinaryConnectionLease> result = await inner.OpenFreshAsync(kind, cancellationToken);

            return result.IsFailure ? result : new FaultAfterReleaseLease(this, result.Value);
        }

        private void AfterRelease()
        {
            if (++Releases == 1) throw new SqliteException("PRIVATE-LEASE-CLEANUP /operator/private", 5);
        }

        private sealed class FaultAfterReleaseLease(
            FaultAfterReleaseConnectionFactory owner,
            IGrimoireOrdinaryConnectionLease innerLease) : IGrimoireOrdinaryConnectionLease
        {
            public SqliteConnection Connection => innerLease.Connection;

            public void Dispose()
            {
                innerLease.Dispose();

                owner.AfterRelease();
            }

            public async ValueTask DisposeAsync()
            {
                await innerLease.DisposeAsync();

                owner.AfterRelease();
            }
        }
    }

    private sealed class ObservedReleaseConnectionFactory(
        IGrimoireOrdinaryConnectionFactory inner,
        Action<SqliteConnection> beforeRelease) : IGrimoireOrdinaryConnectionFactory
    {
        public Task<Result<IGrimoireOrdinaryConnectionLease>> AcquireScopedAsync(
            SqliteConnection connection,
            RetroDownfall.Arcanum.Infrastructure.Data.Covenant.CovenantSqliteConnectionMode mode,
            CancellationToken cancellationToken) => inner.AcquireScopedAsync(connection, mode, cancellationToken);

        public async Task<Result<IGrimoireOrdinaryConnectionLease>> OpenFreshAsync(
            GrimoireOrdinaryFreshConnectionKind kind,
            CancellationToken cancellationToken = default)
        {
            Result<IGrimoireOrdinaryConnectionLease> result = await inner.OpenFreshAsync(kind, cancellationToken);

            return result.IsFailure ? result : new ObservedLease(result.Value, beforeRelease);
        }

        private sealed class ObservedLease(
            IGrimoireOrdinaryConnectionLease innerLease,
            Action<SqliteConnection> observe) : IGrimoireOrdinaryConnectionLease
        {
            public SqliteConnection Connection => innerLease.Connection;

            public void Dispose()
            {
                observe(Connection);

                innerLease.Dispose();
            }

            public async ValueTask DisposeAsync()
            {
                observe(Connection);

                await innerLease.DisposeAsync();
            }
        }
    }

    private sealed class BusyAttemptLogSink : Serilog.Core.ILogEventSink
    {
        internal List<Serilog.Events.LogEvent> Events { get; } = [];

        public void Emit(Serilog.Events.LogEvent logEvent) => Events.Add(logEvent);
    }

    private static async Task<int> ReadRetentionBusyTimeoutForTestAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "PRAGMA busy_timeout";

        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
}
