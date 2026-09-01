using System.Text;

using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Daemons;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Daemons;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Logging;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("Grimoire")]

[Trait("Category", "Integration")]

public sealed class DataRetentionDaemonHistoryTests : IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private string _root = string.Empty;

    private ArcanumDbContext? _db;

    public DataRetentionDaemonHistoryTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _root = Path.Combine(
            Path.GetTempPath(),
            "arcanum-retention-daemon-tests-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_root);

        _db = _fixture.CreateContext(_dbPath);

        return Task.CompletedTask;

    }

    public async Task DisposeAsync()
    {

        if (_db is not null)
        {

            SqliteConnection connection =
                (SqliteConnection)_db.Database.GetDbConnection();

            await _db.DisposeAsync();

            SqliteConnection.ClearPool(connection);

        }

        if (File.Exists(_dbPath))
        {

            File.Delete(_dbPath);

        }

        if (Directory.Exists(_root))
        {

            Directory.Delete(_root, recursive: true);

        }

    }

    [SkippableFact]

    public async Task Prune_ReportsAndDeletesAllOldTerminalDaemonExecutions()
    {

        RequireSqlCipher();

        FakeTimeProvider time = new();

        time.SetUtcNow(DateTimeOffset.Parse("2000-01-01T00:00:00Z"));

        InMemoryDaemonExecutionRepository repository = new(
            new InMemoryLogRingBuffer(),
            time);

        string first = await repository.StartAsync(
            "daemon-a",
            "Daemon A",
            CancellationToken.None);

        _ = await repository.CompleteAsync(first, CancellationToken.None);

        string second = await repository.StartAsync(
            "daemon-b",
            "Daemon B",
            CancellationToken.None);

        _ = await repository.CompleteAsync(second, CancellationToken.None);

        time.Advance(TimeSpan.FromDays(30));

        string running = await repository.StartAsync(
            "daemon-running",
            "Daemon Running",
            CancellationToken.None);

        ArcanumSettings settings = CreateSettings();

        DataRetentionService service = CreateService(settings, repository, time);

        DataRetentionStatus status = await service.GetStatusAsync(
            CancellationToken.None);

        DataRetentionStatusItem daemonStatus = Assert.Single(
            status.Items,
            static item => item.DataClass == RetentionDataClass.DaemonExecutions);

        Assert.Equal(3, daemonStatus.Rows);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        string[] candidates = plan.CandidateIds
            .Where(static id => id.StartsWith("daemon:", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(2, candidates.Length);

        Assert.DoesNotContain(candidates, candidate => candidate.Contains(running, StringComparison.Ordinal));

        Assert.Contains(
            plan.Items,
            static item => item.DataClass == RetentionDataClass.DaemonExecutions
                && item.Rows == 2);

        Result<DataRetentionApplyResult> applied = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.Equal(2, applied.Value.RowsDeleted);

        DaemonExecutionSummary[] remaining = await repository.GetHistoryAsync(
            null,
            CancellationToken.None);

        Assert.Single(remaining);

        Assert.Contains(remaining, item => item.Id == running);

    }

    [SkippableFact]

    public async Task RecoverPrune_AcceptsOpaqueDaemonExecutionCandidateId()
    {

        RequireSqlCipher();

        FakeTimeProvider time = new();

        time.SetUtcNow(DateTimeOffset.Parse("2000-01-01T00:00:00Z"));

        InMemoryDaemonExecutionRepository repository = new(
            new InMemoryLogRingBuffer(),
            time);

        const string executionId = "execution/opaque_id-01";

        bool started = await repository.TryStartAsync(
            "daemon-opaque",
            "Daemon Opaque",
            executionId,
            CancellationToken.None);

        Assert.True(started);

        _ = await repository.CompleteAsync(executionId, CancellationToken.None);

        time.Advance(TimeSpan.FromDays(30));

        DataRetentionService service = CreateService(
            CreateSettings(),
            repository,
            time);

        DataRetentionPlan plan = await service.PlanAsync(
            new DataRetentionRequest(DataRetentionOperation.Prune),
            CancellationToken.None);

        string candidate = Assert.Single(
            plan.CandidateIds,
            static id => id.StartsWith("daemon:", StringComparison.Ordinal));

        Assert.Equal("daemon:" + executionId, candidate);

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        DateTimeOffset now = time.GetUtcNow();

        const string ownerId = "interrupted-daemon-retention-test";

        LongRunningOperation operation = await operations.CreateAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionPrune,
                LongRunningOperationRecoveryPolicy.RestartIdempotently,
                "Interrupted daemon retention test.",
                now));

        LongRunningOperationLeaseResult lease = await operations.TryAcquireLeaseAsync(
            operation.Id,
            ownerId,
            now,
            now.AddMinutes(5));

        Assert.True(lease.Acquired);

        byte[] checkpoint = Encoding.UTF8.GetBytes(
            "ARCADATA2\n"
            + plan.PlanId
            + "\n0\nG:"
            + Convert.ToBase64String(
                Encoding.UTF8.GetBytes(plan.GeneratedAt.ToString("o")))
            + "\nC:"
            + Convert.ToBase64String(Encoding.UTF8.GetBytes(candidate))
            + ":"
            + Convert.ToBase64String(
                Encoding.UTF8.GetBytes(plan.GeneratedAt.AddDays(-30).ToString("o")))
            + "\n");

        bool saved = await operations.SaveCheckpointAsync(
            operation.Id,
            ownerId,
            expectedCheckpointVersion: 0,
            checkpointVersion: 2,
            checkpoint,
            checkpointReference: null,
            "Interrupted before the daemon candidate.",
            now);

        Assert.True(saved);

        LongRunningOperation interrupted = Assert.IsType<LongRunningOperation>(
            await operations.GetAsync(operation.Id));

        LongRunningOperationRecoveryResult recovered = await service.RecoverPruneAsync(
            interrupted,
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Completed, recovered.State);

        Assert.Empty(
            await repository.GetHistoryAsync(
                null,
                CancellationToken.None));

    }

    private DataRetentionService CreateService(
        ArcanumSettings settings,
        IDaemonExecutionRepository repository,
        TimeProvider time,
        IDaemonExecutionMutationGate? daemonMutationGate = null)
    {

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        return new DataRetentionService(
            _db!,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            operations,
            time,
            NullLogger<DataRetentionService>.Instance,
            Path.Combine(_root, "attachments"),
            Path.Combine(_root, "files"),
            Path.Combine(_root, "logs"),
            policyStore: null,
            attachmentStore: null,
            repository,
            daemonMutationGate ?? repository as IDaemonExecutionMutationGate);

    }

    private static ArcanumSettings CreateSettings() =>
        new()
        {

            Retention = new RetentionSettings
            {

                AutomaticSweepsEnabled = false,

                DaemonHistory = new RetentionRuleSettings
                {

                    Enabled = true,

                    Days = 1,

                },

            },

        };

    private static void RequireSqlCipher() =>
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

    internal sealed class ActivatingDaemonRepository(
        TimeProvider time,
        int activateOnHistoryCall) : IDaemonExecutionRepository
    {

        public const string ExecutionId = "factory-boundary-running";

        private readonly InMemoryDaemonExecutionRepository _inner = new(
            new InMemoryLogRingBuffer(),
            time);

        private int _historyCalls;

        public async Task<DaemonExecutionSummary[]> GetHistoryAsync(
            string? daemonId,
            CancellationToken ct)
        {

            int call = Interlocked.Increment(ref _historyCalls);

            if (call == activateOnHistoryCall)
            {

                bool started = await _inner.TryStartAsync(
                    "factory-boundary",
                    "Factory Boundary",
                    ExecutionId,
                    ct);

                Assert.True(started);

            }

            return await _inner.GetHistoryAsync(daemonId, ct);

        }

        public Task<DaemonExecutionDetail?> GetAsync(
            string executionId,
            CancellationToken ct) =>
            _inner.GetAsync(executionId, ct);

        public Task<string> StartAsync(
            string daemonId,
            string daemonName,
            CancellationToken ct) =>
            _inner.StartAsync(daemonId, daemonName, ct);

        public Task<bool> TryStartAsync(
            string daemonId,
            string daemonName,
            string executionId,
            CancellationToken ct) =>
            _inner.TryStartAsync(daemonId, daemonName, executionId, ct);

        public Task<DaemonExecutionSummary> CompleteAsync(
            string executionId,
            CancellationToken ct) =>
            _inner.CompleteAsync(executionId, ct);

        public Task<DaemonExecutionSummary> FailAsync(
            string executionId,
            string errorMessage,
            CancellationToken ct) =>
            _inner.FailAsync(executionId, errorMessage, ct);

        public Task<DaemonExecutionSummary> CancelAsync(
            string executionId,
            CancellationToken ct) =>
            _inner.CancelAsync(executionId, ct);

        public Task ReportDrainedAsync(
            string executionId,
            CancellationToken ct) =>
            _inner.ReportDrainedAsync(executionId, ct);

        public Task<bool> TryDeleteTerminalAsync(
            string executionId,
            CancellationToken ct) =>
            _inner.TryDeleteTerminalAsync(executionId, ct);

        public Task<bool> TryDeleteTerminalBeforeAsync(
            string executionId,
            DateTimeOffset completedAtCutoff,
            CancellationToken ct) =>
            _inner.TryDeleteTerminalBeforeAsync(
                executionId,
                completedAtCutoff,
                ct);

        public bool HasRunningExecution(string daemonId) =>
            _inner.HasRunningExecution(daemonId);

        public CancellationTokenSource? GetCancellationTokenSource(
            string executionId) =>
            _inner.GetCancellationTokenSource(executionId);

    }

    internal sealed class BlockingDaemonMutationGate(
        IDaemonExecutionMutationGate inner) : IDaemonExecutionMutationGate
    {

        private readonly TaskCompletionSource _acquired = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Acquired => _acquired.Task;

        public async ValueTask<IAsyncDisposable> AcquireExclusiveAsync(
            CancellationToken cancellationToken = default)
        {

            IAsyncDisposable lease = await inner.AcquireExclusiveAsync(
                cancellationToken);

            _acquired.TrySetResult();

            await _release.Task.WaitAsync(cancellationToken);

            return lease;

        }

        public void Release() => _release.TrySetResult();

    }

}
