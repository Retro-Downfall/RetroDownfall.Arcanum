using System.Net;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Provider_refresh_missing_source_refuses_before_operation_admission()
    {
        await using SpendScenario scenario = await SpendScenario.CreateAsync();

        await using SqliteConnection connection = await scenario.Fixture.OpenAsync();

        await using SqliteCommand query = connection.CreateCommand();

        query.CommandText = "SELECT QuestId FROM guild_budget_effects WHERE EffectId=$effect";

        query.Parameters.AddWithValue("$effect", scenario.EffectId.ToString("D").ToUpperInvariant());

        Guid quest = Guid.Parse(Assert.IsType<string>(await query.ExecuteScalarAsync()));

        var selected = await scenario.Fixture.Store.PrepareProviderSpendRefreshAsync(scenario.Fixture.Caller, quest,
            scenario.EffectId, Guid.NewGuid(), default);

        Assert.True(selected.IsSuccess, selected.IsFailure ? selected.Error.Message : null);

        Assert.Equal("router", selected.Value.Provider);

        Assert.Equal(SpendModel, selected.Value.Model);

        query.CommandText = "DELETE FROM guild_spend_sources WHERE EffectId=$effect";

        Assert.Equal(1, await query.ExecuteNonQueryAsync());

        long operations = await scenario.Fixture.CountAsync("LongRunningOperations");

        var result = await scenario.Fixture.Store.PrepareProviderSpendRefreshAsync(scenario.Fixture.Caller, quest,
            scenario.EffectId, Guid.NewGuid(), default);

        Assert.True(result.IsFailure);

        Assert.Equal(operations, await scenario.Fixture.CountAsync("LongRunningOperations"));

        Assert.Equal(0L, await scenario.Fixture.CountAsync("guild_spend_refreshes"));
    }

    [Fact]
    public async Task Provider_refresh_none_refuses_before_credential_operation_or_get()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        f.Scenario.Settings.Providers[0].GuildUsageReconciliation = GuildUsageReconciliationSource.None;

        CountingRefreshResolver resolver = new();

        using SpendMetadataHandler handler = new();

        GuildSpendRefreshService service = new(f.Scenario.Fixture.Store, f.Configuration, resolver,
            new GuildOpenRouterSpendReader(new PublicSpendDns(), () => handler), null!, null!, null!, null!, TimeProvider.System, new GuildSpendRefreshTracker());

        long operations = await f.Scenario.Fixture.CountAsync("LongRunningOperations");

        long refreshes = await f.Scenario.Fixture.CountAsync("guild_spend_refreshes");

        var result = await service.RefreshAsync(f.Scenario.Fixture.Caller, f.QuestId, f.Scenario.EffectId, Guid.NewGuid(), default);

        Assert.True(result.IsFailure);

        Assert.Equal(0, resolver.Reads);

        Assert.Null(handler.Uri);

        Assert.Equal(operations, await f.Scenario.Fixture.CountAsync("LongRunningOperations"));

        Assert.Equal(refreshes, await f.Scenario.Fixture.CountAsync("guild_spend_refreshes"));
    }

    private sealed class CountingRefreshResolver : IProviderApiKeyResolver
    {
        internal int Reads { get; private set; }

        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken ct = default)
        {
            Reads++;

            return Task.FromResult<string?>(null);
        }

        public Task<ResolvedProviderCredential> PeekWithIdentityAsync(ProviderSettings provider, CancellationToken ct = default)
        {
            Reads++;

            return Task.FromResult(new ResolvedProviderCredential(null, null));
        }
    }

    [Fact]
    public async Task Provider_refresh_service_executes_one_real_reader_and_replays_its_receipt()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        await using SqliteConnection connection = await f.Scenario.Fixture.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        using LongRunningOperationStore operations = new(db, f.Scenario.Fixture.Connections);

        string lockPath = Path.Combine(Path.GetTempPath(), "arcanum-refresh-lock-" + Guid.NewGuid().ToString("N"));

        var acquired = GuildWorkspaceLeaseHostLock.TryAcquire(lockPath);

        Assert.True(acquired.IsSuccess, acquired.IsFailure ? acquired.Error.Message : null);

        using GuildWorkspaceLeaseHostLock hostLock = acquired.Value;

        RefreshTestReadiness readiness = new(hostLock);

        int reads = 0;

        GuildOpenRouterSpendReader reader = new(new PublicSpendDns(), () =>
        {
            reads++;

            return new SpendMetadataHandler();
        });

        GuildSpendRefreshService service = new(f.Scenario.Fixture.Store, f.Configuration, f.Resolver, reader,
            readiness, f.Scenario.Fixture.Gate, operations, new LongRunningOperationOwnership(), TimeProvider.System, new GuildSpendRefreshTracker());

        Guid refresh = Guid.NewGuid();

        var first = await service.RefreshAsync(f.Scenario.Fixture.Caller, f.QuestId, f.Scenario.EffectId, refresh, default);

        Assert.True(first.IsSuccess, first.IsFailure ? first.Error.Message : null);

        Assert.NotNull(first.Value.Receipt);

        Assert.Equal(1, reads);

        var replay = await service.RefreshAsync(f.Scenario.Fixture.Caller, f.QuestId, f.Scenario.EffectId, refresh, default);

        Assert.True(replay.IsSuccess, replay.IsFailure ? replay.Error.Message : null);

        Assert.True(replay.Value.Replayed);

        Assert.Equal(first.Value.Receipt, replay.Value.Receipt);

        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task Provider_refresh_recovery_replays_pointer_without_source_or_reclaiming_process_owner()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        var applied = await f.ApplyAsync();

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : null);

        f.Query.CommandText = "DELETE FROM BillableOperations";

        Assert.Equal(1, await f.Query.ExecuteNonQueryAsync());

        f.Query.CommandText = "SELECT OperationId FROM guild_spend_refreshes WHERE RefreshId=$refresh";

        Guid operationId = Guid.ParseExact(Assert.IsType<string>(await f.Query.ExecuteScalarAsync()), "N");

        await using SqliteConnection connection = await f.Scenario.Fixture.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        using LongRunningOperationStore operations = new(db, f.Scenario.Fixture.Connections);

        LongRunningOperation operation = (await operations.GetAsync(operationId))!;

        LongRunningOperationOwnership ownership = new();

        Assert.True(ownership.TryClaim(operationId, out Guid reconcilerClaim));

        CountingRefreshResolver resolver = new();

        using SpendMetadataHandler handler = new();

        GuildSpendRefreshService service = new(f.Scenario.Fixture.Store, f.Configuration, resolver,
            new GuildOpenRouterSpendReader(new PublicSpendDns(), () => handler), null!, null!, operations, ownership, TimeProvider.System,
            new GuildSpendRefreshTracker());

        var recovered = await service.RecoverClaimedAsync(f.Scenario.Fixture.Caller, operation, default);

        Assert.Equal(LongRunningOperationState.Completed, recovered.State);

        Assert.True(ownership.IsClaimedBy(operationId, reconcilerClaim));

        Assert.Equal(0, resolver.Reads);

        Assert.Null(handler.Uri);

        Assert.True(ownership.Release(operationId, reconcilerClaim));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provider_refresh_recovery_executes_under_existing_reconciler_claim_and_lease(bool sourceExpired)
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        f.Query.CommandText = "SELECT OperationId FROM guild_spend_refreshes WHERE RefreshId=$refresh";

        Guid operationId = Guid.ParseExact(Assert.IsType<string>(await f.Query.ExecuteScalarAsync()), "N");

        await using SqliteConnection connection = await f.Scenario.Fixture.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        using LongRunningOperationStore operations = new(db, f.Scenario.Fixture.Connections);

        LongRunningOperation operation = (await operations.GetAsync(operationId))!;

        LongRunningOperationOwnership ownership = new();

        Assert.True(ownership.TryClaim(operationId, out Guid reconcilerClaim));

        string lockPath = Path.Combine(Path.GetTempPath(), "arcanum-refresh-lock-" + Guid.NewGuid().ToString("N"));

        var acquired = GuildWorkspaceLeaseHostLock.TryAcquire(lockPath);

        Assert.True(acquired.IsSuccess);

        using GuildWorkspaceLeaseHostLock hostLock = acquired.Value;

        int reads = 0;

        GuildOpenRouterSpendReader reader = new(new PublicSpendDns(), () =>
        {
            reads++;

            return new SpendMetadataHandler();
        });

        GuildSpendRefreshService service = new(f.Scenario.Fixture.Store, f.Configuration, f.Resolver, reader,
            new RefreshTestReadiness(hostLock), f.Scenario.Fixture.Gate, operations, ownership, TimeProvider.System,
            new GuildSpendRefreshTracker());

        if (sourceExpired)
        {
            f.Query.CommandText = "DELETE FROM BillableOperations";

            Assert.Equal(1, await f.Query.ExecuteNonQueryAsync());
        }

        var recovered = await service.RecoverClaimedAsync(f.Scenario.Fixture.Caller, operation, default);

        Assert.Equal(sourceExpired ? LongRunningOperationState.ReconciliationRequired : LongRunningOperationState.Completed, recovered.State);

        Assert.Equal(sourceExpired ? ErrorCodes.Guild.SourceExpired : null, recovered.ErrorCode);

        Assert.Equal(sourceExpired ? 0 : 1, reads);

        Assert.True(ownership.IsClaimedBy(operationId, reconcilerClaim));

        if (sourceExpired) Assert.Equal(DBNull.Value, await f.ReadPointerAsync());
        else Assert.NotEqual(DBNull.Value, await f.ReadPointerAsync());

        Assert.Equal(LongRunningOperationState.Running, (await operations.GetAsync(operationId))!.State);

        Assert.True(ownership.Release(operationId, reconcilerClaim));
    }

    [Fact]
    public async Task Provider_refresh_parks_and_releases_ownership_when_async_response_disposal_fails()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        await using SqliteConnection connection = await f.Scenario.Fixture.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        using LongRunningOperationStore operations = new(db, f.Scenario.Fixture.Connections);

        string lockPath = Path.Combine(Path.GetTempPath(), "arcanum-refresh-lock-" + Guid.NewGuid().ToString("N"));

        var acquired = GuildWorkspaceLeaseHostLock.TryAcquire(lockPath);

        Assert.True(acquired.IsSuccess);

        using GuildWorkspaceLeaseHostLock hostLock = acquired.Value;

        LongRunningOperationOwnership ownership = new();

        GuildSpendRefreshService service = new(f.Scenario.Fixture.Store, f.Configuration, f.Resolver,
            new GuildOpenRouterSpendReader(new PublicSpendDns(), () => new FailingDisposeRefreshHandler()),
            new RefreshTestReadiness(hostLock), f.Scenario.Fixture.Gate, operations, ownership, TimeProvider.System,
            new GuildSpendRefreshTracker());

        var result = await service.RefreshAsync(f.Scenario.Fixture.Caller, f.QuestId, f.Scenario.EffectId, Guid.NewGuid(), default);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.True(result.Value.RequiresAttention);

        Assert.False(ownership.IsClaimed(result.Value.OperationId));

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, (await operations.GetAsync(result.Value.OperationId))!.State);

        hostLock.Dispose();

        var successor = GuildWorkspaceLeaseHostLock.TryAcquire(lockPath);

        Assert.True(successor.IsSuccess);

        successor.Value.Dispose();
    }

    private sealed class FailingDisposeRefreshHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FailingDisposeRefreshStream()) });
    }

    private sealed class FailingDisposeRefreshStream : MemoryStream
    {
        public override ValueTask DisposeAsync() => ValueTask.FromException(new InvalidOperationException("injected disposal failure"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provider_refresh_parks_admitted_identity_if_lease_acquisition_is_cancelled(bool commitFirst)
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        await using SqliteConnection connection = await f.Scenario.Fixture.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        using LongRunningOperationStore inner = new(db, f.Scenario.Fixture.Connections);

        BlockingRefreshOperationStore operations = new(inner) { CancelAcquire = true, CommitBeforeCancel = commitFirst };

        string lockPath = Path.Combine(Path.GetTempPath(), "arcanum-refresh-lock-" + Guid.NewGuid().ToString("N"));

        var acquired = GuildWorkspaceLeaseHostLock.TryAcquire(lockPath);

        Assert.True(acquired.IsSuccess);

        using GuildWorkspaceLeaseHostLock hostLock = acquired.Value;

        int reads = 0;

        GuildOpenRouterSpendReader reader = new(new PublicSpendDns(), () => { reads++; return new SpendMetadataHandler(); });

        GuildSpendRefreshService service = new(f.Scenario.Fixture.Store, f.Configuration, f.Resolver, reader,
            new RefreshTestReadiness(hostLock), f.Scenario.Fixture.Gate, operations, new LongRunningOperationOwnership(),
            TimeProvider.System, new GuildSpendRefreshTracker());

        var result = await service.RefreshAsync(f.Scenario.Fixture.Caller, f.QuestId, f.Scenario.EffectId, Guid.NewGuid(), default);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.True(result.Value.RequiresAttention);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, (await inner.GetAsync(result.Value.OperationId))!.State);

        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task Provider_refresh_returns_same_parked_operation_when_provider_attempt_fails()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        await using SqliteConnection connection = await f.Scenario.Fixture.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        using LongRunningOperationStore operations = new(db, f.Scenario.Fixture.Connections);

        string lockPath = Path.Combine(Path.GetTempPath(), "arcanum-refresh-lock-" + Guid.NewGuid().ToString("N"));

        var acquired = GuildWorkspaceLeaseHostLock.TryAcquire(lockPath);

        Assert.True(acquired.IsSuccess);

        using GuildWorkspaceLeaseHostLock hostLock = acquired.Value;

        GuildOpenRouterSpendReader reader = new(new PublicSpendDns(), () => new SpendMetadataHandler { Status = HttpStatusCode.ServiceUnavailable });

        GuildSpendRefreshService service = new(f.Scenario.Fixture.Store, f.Configuration, f.Resolver, reader,
            new RefreshTestReadiness(hostLock), f.Scenario.Fixture.Gate, operations, new LongRunningOperationOwnership(), TimeProvider.System, new GuildSpendRefreshTracker());

        Guid refresh = Guid.NewGuid();

        var result = await service.RefreshAsync(f.Scenario.Fixture.Caller, f.QuestId, f.Scenario.EffectId, refresh, default);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.True(result.Value.RequiresAttention);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, (await operations.GetAsync(result.Value.OperationId))!.State);

        var replay = await service.RefreshAsync(f.Scenario.Fixture.Caller, f.QuestId, f.Scenario.EffectId, refresh, default);

        Assert.True(replay.IsSuccess);

        Assert.Equal(result.Value.OperationId, replay.Value.OperationId);

        Assert.True(replay.Value.RequiresAttention);

        Assert.False(replay.Value.InFlight);
    }

    [Fact]
    public async Task Provider_refresh_parks_same_operation_if_physical_readiness_is_lost_after_admission()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        await using SqliteConnection connection = await f.Scenario.Fixture.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        using LongRunningOperationStore operations = new(db, f.Scenario.Fixture.Connections);

        string lockPath = Path.Combine(Path.GetTempPath(), "arcanum-refresh-lock-" + Guid.NewGuid().ToString("N"));

        var acquired = GuildWorkspaceLeaseHostLock.TryAcquire(lockPath);

        Assert.True(acquired.IsSuccess);

        using GuildWorkspaceLeaseHostLock hostLock = acquired.Value;

        RefreshTestReadiness readiness = new(hostLock) { Available = false };

        int reads = 0;

        GuildOpenRouterSpendReader reader = new(new PublicSpendDns(), () =>
        {
            reads++;

            return new SpendMetadataHandler();
        });

        GuildSpendRefreshService service = new(f.Scenario.Fixture.Store, f.Configuration, f.Resolver, reader,
            readiness, f.Scenario.Fixture.Gate, operations, new LongRunningOperationOwnership(), TimeProvider.System, new GuildSpendRefreshTracker());

        Guid refresh = Guid.NewGuid();

        var result = await service.RefreshAsync(f.Scenario.Fixture.Caller, f.QuestId, f.Scenario.EffectId, refresh, default);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.True(result.Value.RequiresAttention);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, (await operations.GetAsync(result.Value.OperationId))!.State);

        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task Provider_refresh_retains_physical_pin_until_ignored_cancellation_and_disposal_join()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        await using SqliteConnection connection = await f.Scenario.Fixture.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        using LongRunningOperationStore operations = new(db, f.Scenario.Fixture.Connections);

        string lockPath = Path.Combine(Path.GetTempPath(), "arcanum-refresh-lock-" + Guid.NewGuid().ToString("N"));

        var acquired = GuildWorkspaceLeaseHostLock.TryAcquire(lockPath);

        Assert.True(acquired.IsSuccess);

        using GuildWorkspaceLeaseHostLock hostLock = acquired.Value;

        BlockingRefreshStream stream = new();

        int reads = 0;

        GuildOpenRouterSpendReader reader = new(new PublicSpendDns(), () =>
        {
            Interlocked.Increment(ref reads);

            return new BlockingRefreshHandler(stream);
        });

        GuildSpendRefreshService service = new(f.Scenario.Fixture.Store, f.Configuration, f.Resolver, reader,
            new RefreshTestReadiness(hostLock), f.Scenario.Fixture.Gate, operations, new LongRunningOperationOwnership(), TimeProvider.System, new GuildSpendRefreshTracker());

        using CancellationTokenSource cancellation = new();

        Guid refresh = Guid.NewGuid();

        Task<Result<GuildSpendRefreshResult>> running = service.RefreshAsync(f.Scenario.Fixture.Caller, f.QuestId,
            f.Scenario.EffectId, refresh, cancellation.Token);

        try
        {
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));

            var concurrent = await service.RefreshAsync(f.Scenario.Fixture.Caller, f.QuestId, f.Scenario.EffectId, refresh, default);

            Assert.True(concurrent.IsSuccess);

            Assert.True(concurrent.Value.InFlight);

            Assert.Equal(1, Volatile.Read(ref reads));

            cancellation.Cancel();

            hostLock.Dispose();

            Assert.True(GuildWorkspaceLeaseHostLock.TryAcquire(lockPath).IsFailure);

            stream.ReleaseRead.TrySetResult();

            await stream.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.False(running.IsCompleted);

            Assert.True(GuildWorkspaceLeaseHostLock.TryAcquire(lockPath).IsFailure);

            stream.ReleaseDispose.TrySetResult();

            var stopped = await running;

            Assert.True(stopped.IsSuccess);

            Assert.True(stopped.Value.RequiresAttention || stopped.Value.InFlight);

            var successor = GuildWorkspaceLeaseHostLock.TryAcquire(lockPath);

            Assert.True(successor.IsSuccess, successor.IsFailure ? successor.Error.Message : null);

            successor.Value.Dispose();
        }
        finally
        {
            stream.ReleaseRead.TrySetResult();

            stream.ReleaseDispose.TrySetResult();

            await running;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provider_refresh_owner_close_or_host_stop_joins_body_and_disposal_before_returning(bool hostStop)
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        await using SqliteConnection connection = await f.Scenario.Fixture.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        using LongRunningOperationStore operations = new(db, f.Scenario.Fixture.Connections);

        string lockPath = Path.Combine(Path.GetTempPath(), "arcanum-refresh-lock-" + Guid.NewGuid().ToString("N"));

        var acquired = GuildWorkspaceLeaseHostLock.TryAcquire(lockPath);

        Assert.True(acquired.IsSuccess);

        using GuildWorkspaceLeaseHostLock hostLock = acquired.Value;

        BlockingRefreshStream stream = new();

        GuildSpendRefreshTracker tracker = new();

        GuildOpenRouterSpendReader reader = new(new PublicSpendDns(), () => new BlockingRefreshHandler(stream));

        GuildSpendRefreshService service = new(f.Scenario.Fixture.Store, f.Configuration, f.Resolver, reader,
            new RefreshTestReadiness(hostLock), f.Scenario.Fixture.Gate, operations, new LongRunningOperationOwnership(), TimeProvider.System, tracker);

        f.Query.CommandText = "SELECT RootQuestId FROM guild_quests WHERE Id=$quest";

        Guid root = Guid.Parse(Assert.IsType<string>(await f.Query.ExecuteScalarAsync()));

        Task<Result<GuildSpendRefreshResult>> running = service.RefreshAsync(f.Scenario.Fixture.Caller, f.QuestId,
            f.Scenario.EffectId, Guid.NewGuid(), default);

        Task? closing = null;

        try
        {
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));

            closing = hostStop ? tracker.CloseAllAsync(CancellationToken.None) : tracker.CloseRootAsync(root, default);

            Assert.False(closing.IsCompleted);

            stream.ReleaseRead.TrySetResult();

            await stream.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.False(closing.IsCompleted);

            stream.ReleaseDispose.TrySetResult();

            await closing;

            await running;

            long count = await f.Scenario.Fixture.CountAsync("guild_spend_refreshes");

            var refused = await service.RefreshAsync(f.Scenario.Fixture.Caller, f.QuestId, f.Scenario.EffectId, Guid.NewGuid(), default);

            Assert.True(refused.IsFailure);

            Assert.Equal(count, await f.Scenario.Fixture.CountAsync("guild_spend_refreshes"));
        }
        finally
        {
            stream.ReleaseRead.TrySetResult();

            stream.ReleaseDispose.TrySetResult();

            await running;

            if (closing is not null)
            {
                try { await closing; }
                catch (OperationCanceledException) when (hostStop) { }
            }
        }
    }

    [Fact]
    public async Task Provider_refresh_holds_physical_pin_until_blocked_heartbeat_is_joined()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        await using SqliteConnection connection = await f.Scenario.Fixture.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        using LongRunningOperationStore inner = new(db, f.Scenario.Fixture.Connections);

        BlockingRefreshOperationStore operations = new(inner);

        string lockPath = Path.Combine(Path.GetTempPath(), "arcanum-refresh-lock-" + Guid.NewGuid().ToString("N"));

        var acquired = GuildWorkspaceLeaseHostLock.TryAcquire(lockPath);

        Assert.True(acquired.IsSuccess);

        using GuildWorkspaceLeaseHostLock hostLock = acquired.Value;

        BlockingRefreshStream stream = new();

        GuildSpendRefreshService service = new(f.Scenario.Fixture.Store, f.Configuration, f.Resolver,
            new GuildOpenRouterSpendReader(new PublicSpendDns(), () => new BlockingRefreshHandler(stream)),
            new RefreshTestReadiness(hostLock), f.Scenario.Fixture.Gate, operations, new LongRunningOperationOwnership(),
            TimeProvider.System, new GuildSpendRefreshTracker());

        using CancellationTokenSource cancellation = new();

        Task<Result<GuildSpendRefreshResult>> running = service.RefreshAsync(f.Scenario.Fixture.Caller, f.QuestId,
            f.Scenario.EffectId, Guid.NewGuid(), cancellation.Token);

        try
        {
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));

            await operations.HeartbeatEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));

            cancellation.Cancel();

            hostLock.Dispose();

            stream.ReleaseRead.TrySetResult();

            await stream.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(15));

            stream.ReleaseDispose.TrySetResult();

            await stream.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.False(running.IsCompleted);

            Assert.True(GuildWorkspaceLeaseHostLock.TryAcquire(lockPath).IsFailure);

            operations.ReleaseHeartbeat.TrySetResult();

            await running;

            var successor = GuildWorkspaceLeaseHostLock.TryAcquire(lockPath);

            Assert.True(successor.IsSuccess, successor.IsFailure ? successor.Error.Message : null);

            successor.Value.Dispose();
        }
        finally
        {
            stream.ReleaseRead.TrySetResult();

            stream.ReleaseDispose.TrySetResult();

            operations.ReleaseHeartbeat.TrySetResult();

            await running;

            if (operations.HeartbeatEntered.Task.IsCompleted) await operations.HeartbeatFinished.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    private sealed class BlockingRefreshOperationStore(ILongRunningOperationStore inner) : ILongRunningOperationStore
    {
        internal bool CancelAcquire { get; set; }

        internal bool CommitBeforeCancel { get; set; }

        internal TaskCompletionSource HeartbeatEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseHeartbeat { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource HeartbeatFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<LongRunningOperation?> GetAsync(Guid id, CancellationToken ct = default) => inner.GetAsync(id, ct);

        public async Task<LongRunningOperationLeaseResult> TryAcquireLeaseAsync(Guid id, string owner, DateTimeOffset now, DateTimeOffset expiry,
            CancellationToken ct = default)
        {
            if (CancelAcquire)
            {
                if (CommitBeforeCancel) await inner.TryAcquireLeaseAsync(id, owner, now, expiry, CancellationToken.None);

                throw new OperationCanceledException();
            }

            return await inner.TryAcquireLeaseAsync(id, owner, now, expiry, ct);
        }

        public Task<bool> TryTransitionAsync(Guid id, long revision, string? owner, LongRunningOperationState state, DateTimeOffset now,
            string? error = null, CancellationToken ct = default) => inner.TryTransitionAsync(id, revision, owner, state, now, error, ct);

        public async Task<bool> RenewLeaseAsync(Guid id, string owner, DateTimeOffset now, DateTimeOffset expiry, CancellationToken ct = default)
        {
            HeartbeatEntered.TrySetResult();

            try
            {
                await ReleaseHeartbeat.Task;

                return await inner.RenewLeaseAsync(id, owner, now, expiry, CancellationToken.None);
            }
            finally
            {
                HeartbeatFinished.TrySetResult();
            }
        }

        public Task<bool> HeartbeatAsync(Guid id, string owner, DateTimeOffset now, DateTimeOffset expiry, CancellationToken ct = default) =>
            inner.HeartbeatAsync(id, owner, now, expiry, ct);

        public Task<LongRunningOperation> CreateAsync(LongRunningOperationCreateRequest request, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<LongRunningOperationRequestIdentityResult> ResolveOrCreateAsync(LongRunningOperationCreateRequest request,
            LongRunningOperationRequestIdentity identity, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<LongRunningOperation?> TryStartSingleFlightAsync(LongRunningOperationCreateRequest request, string owner,
            DateTimeOffset now, DateTimeOffset expiry, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<LongRunningOperationRequestIdentity?> FindRequestIdentityAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<LongRunningOperationRequestIdentityMatch?> FindByRequestedOperationIdAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<LongRunningOperation>> ListAsync(LongRunningOperationQuery query, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<LongRunningOperation>> FindExpiredAsync(DateTimeOffset now, int limit, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> SaveCheckpointAsync(Guid id, string owner, int expectedVersion, int version, byte[]? payload,
            string? reference, string summary, DateTimeOffset now, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> RequestCancellationAsync(Guid id, long revision, DateTimeOffset now, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> ResetForRetryAsync(Guid id, long revision, DateTimeOffset now, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<LongRunningOperationCount>> GetCountsAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class BlockingRefreshHandler(BlockingRefreshStream stream) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
    }

    private sealed class BlockingRefreshStream : Stream
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Disposing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseDispose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();

            await ReleaseRead.Task;

            return 0;
        }

        public override async ValueTask DisposeAsync()
        {
            Disposing.TrySetResult();

            await ReleaseDispose.Task;

            await base.DisposeAsync();

            Disposed.TrySetResult();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RefreshTestReadiness(GuildWorkspaceLeaseHostLock hostLock) : IGuildLocalHostReadiness
    {
        internal bool Available { get; set; } = true;

        public Result RequireExecutionReady() => Available ? Result.Success() : GuildLocalHostReadiness.Unavailable;

        public Task<Result> EnsureReadyAsync(CancellationToken ct) => Task.FromResult(Result.Success());

        public Result<IDisposable> RetainPhysicalWorkOwnership() => Available ? hostLock.Pin() : GuildLocalHostReadiness.Unavailable;
    }

    [Fact]
    public async Task Provider_refresh_atomically_admits_one_operation_for_concurrent_exact_requests()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        Guid refresh = Guid.NewGuid();

        long operations = await f.Scenario.Fixture.CountAsync("LongRunningOperations");

        long refreshes = await f.Scenario.Fixture.CountAsync("guild_spend_refreshes");

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            f.Scenario.Fixture.Store.AdmitProviderSpendRefreshAsync(f.Scenario.Fixture.Caller, f.QuestId,
                f.Scenario.EffectId, refresh, f.Currentness, default)));

        Assert.All(results, result => Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null));

        Assert.Equal(results[0].Value.OperationId, results[1].Value.OperationId);

        Assert.NotEqual(Guid.Empty, results[0].Value.OperationId);

        Assert.Equal(operations + 1, await f.Scenario.Fixture.CountAsync("LongRunningOperations"));

        Assert.Equal(refreshes + 1, await f.Scenario.Fixture.CountAsync("guild_spend_refreshes"));
    }

    [Fact]
    public async Task Provider_refresh_source_for_network_requires_live_durable_lease()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        var valid = await f.Scenario.Fixture.Store.VerifyProviderSpendRefreshSourceAsync(f.Scenario.Fixture.Caller, f.QuestId,
            f.Scenario.EffectId, f.RefreshId, "test-refresh-owner", f.Currentness, f.Guard, default);

        Assert.True(valid.IsSuccess, valid.IsFailure ? valid.Error.Message : null);

        f.Query.CommandText = "UPDATE LongRunningOperations SET State=3 WHERE Id=$operation";

        Assert.Equal(1, await f.Query.ExecuteNonQueryAsync());

        var cancelled = await f.Scenario.Fixture.Store.VerifyProviderSpendRefreshSourceAsync(f.Scenario.Fixture.Caller, f.QuestId,
            f.Scenario.EffectId, f.RefreshId, "test-refresh-owner", f.Currentness, f.Guard, default);

        Assert.True(cancelled.IsFailure);
    }

    [Fact]
    public async Task Provider_refresh_source_for_network_refuses_bill_inconsistent_after_admission()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        f.Query.CommandText = "UPDATE BillableOperations SET ActualCostUsd='0.1'";

        Assert.Equal(1, await f.Query.ExecuteNonQueryAsync());

        var result = await f.Scenario.Fixture.Store.VerifyProviderSpendRefreshSourceAsync(f.Scenario.Fixture.Caller, f.QuestId,
            f.Scenario.EffectId, f.RefreshId, "test-refresh-owner", f.Currentness, f.Guard, default);

        Assert.True(result.IsFailure);
    }

    [Fact]
    public async Task Provider_refresh_refuses_inconsistent_current_bill_before_operation_admission()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        long operations = await f.Scenario.Fixture.CountAsync("LongRunningOperations");

        long refreshes = await f.Scenario.Fixture.CountAsync("guild_spend_refreshes");

        f.Query.CommandText = "UPDATE BillableOperations SET ActualCostUsd='0.1'";

        Assert.Equal(1, await f.Query.ExecuteNonQueryAsync());

        var result = await f.Scenario.Fixture.Store.AdmitProviderSpendRefreshAsync(f.Scenario.Fixture.Caller, f.QuestId,
            f.Scenario.EffectId, Guid.NewGuid(), f.Currentness, default);

        Assert.True(result.IsFailure);

        Assert.Equal(operations, await f.Scenario.Fixture.CountAsync("LongRunningOperations"));

        Assert.Equal(refreshes, await f.Scenario.Fixture.CountAsync("guild_spend_refreshes"));
    }

    [Fact]
    public async Task Provider_refresh_refuses_missing_real_reservation_before_admission()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync(enableDailyBudget: true);

        f.Query.CommandText = "SELECT ReservationId FROM guild_budget_effects WHERE EffectId=$effect";

        Assert.IsType<string>(await f.Query.ExecuteScalarAsync());

        f.Query.CommandText = "DELETE FROM BudgetReservations";

        Assert.Equal(1, await f.Query.ExecuteNonQueryAsync());

        long operations = await f.Scenario.Fixture.CountAsync("LongRunningOperations");

        long refreshes = await f.Scenario.Fixture.CountAsync("guild_spend_refreshes");

        var result = await f.Scenario.Fixture.Store.AdmitProviderSpendRefreshAsync(f.Scenario.Fixture.Caller, f.QuestId,
            f.Scenario.EffectId, Guid.NewGuid(), f.Currentness, default);

        Assert.True(result.IsFailure);

        Assert.Equal(operations, await f.Scenario.Fixture.CountAsync("LongRunningOperations"));

        Assert.Equal(refreshes, await f.Scenario.Fixture.CountAsync("guild_spend_refreshes"));
    }

    [Fact]
    public async Task Provider_refresh_retains_historical_reservation_after_daily_gate_is_disabled()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync(enableDailyBudget: true);

        f.Scenario.Settings.Cost.Budget.Enabled = false;

        var admitted = await f.Scenario.Fixture.Store.AdmitProviderSpendRefreshAsync(f.Scenario.Fixture.Caller, f.QuestId,
            f.Scenario.EffectId, Guid.NewGuid(), f.Currentness, default);

        Assert.True(admitted.IsSuccess, admitted.IsFailure ? admitted.Error.Message : null);

        var applied = await f.ApplyAsync();

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : null);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("alias")]
    [InlineData("run")]
    public async Task Provider_refresh_rechecks_captured_reservation_before_network_and_application(string change)
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync(enableDailyBudget: true);

        f.Query.CommandText = change switch
        {
            "missing" => "DELETE FROM BudgetReservations",
            "alias" => "UPDATE BudgetReservations SET Id=substr(Id,1,8)||'-'||substr(Id,9,4)||'-'||substr(Id,13,4)||'-'||substr(Id,17,4)||'-'||substr(Id,21)",
            "run" => "UPDATE BudgetReservations SET RunId='00000000000000000000000000000001'",
            _ => throw new InvalidOperationException("Unknown reservation fixture change."),
        };

        Assert.Equal(1, await f.Query.ExecuteNonQueryAsync());

        var source = await f.Scenario.Fixture.Store.VerifyProviderSpendRefreshSourceAsync(f.Scenario.Fixture.Caller, f.QuestId,
            f.Scenario.EffectId, f.RefreshId, "test-refresh-owner", f.Currentness, f.Guard, default);

        Assert.True(source.IsFailure);

        Assert.True((await f.ApplyAsync()).IsFailure);

        Assert.Equal(DBNull.Value, await f.ReadPointerAsync());
    }

    [Fact]
    public async Task Provider_refresh_refuses_duplicate_reservation_alias_before_admission()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync(enableDailyBudget: true);

        f.Query.CommandText = """
            INSERT INTO BudgetReservations(Id,RunId,BudgetPeriod,Status,ExpiresAt,CreatedAt,UpdatedAt,ReservedUsd,ReconciledUsd)
            SELECT substr(Id,1,8)||'-'||substr(Id,9,4)||'-'||substr(Id,13,4)||'-'||substr(Id,17,4)||'-'||substr(Id,21),
                RunId,BudgetPeriod,Status,ExpiresAt,CreatedAt,UpdatedAt,ReservedUsd,ReconciledUsd FROM BudgetReservations
            """;

        Assert.Equal(1, await f.Query.ExecuteNonQueryAsync());

        var result = await f.Scenario.Fixture.Store.AdmitProviderSpendRefreshAsync(f.Scenario.Fixture.Caller, f.QuestId,
            f.Scenario.EffectId, Guid.NewGuid(), f.Currentness, default);

        Assert.True(result.IsFailure);
    }

    [Fact]
    public async Task Operator_correction_refuses_duplicate_real_reservation_alias_without_changes()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync(enableDailyBudget: true);

        f.Query.CommandText = "SELECT Revision FROM guild_budget_effects WHERE EffectId=$effect";

        long revision = Assert.IsType<long>(await f.Query.ExecuteScalarAsync());

        long receipts = await f.Scenario.Fixture.CountAsync("guild_spend_receipts");

        long evidence = await f.Scenario.Fixture.CountAsync("guild_budget_evidence");

        long events = await f.Scenario.Fixture.CountAsync("guild_events");

        f.Query.CommandText = """
            INSERT INTO BudgetReservations(Id,RunId,BudgetPeriod,Status,ExpiresAt,CreatedAt,UpdatedAt,ReservedUsd,ReconciledUsd)
            SELECT substr(Id,1,8)||'-'||substr(Id,9,4)||'-'||substr(Id,13,4)||'-'||substr(Id,17,4)||'-'||substr(Id,21),
                RunId,BudgetPeriod,Status,ExpiresAt,CreatedAt,UpdatedAt,ReservedUsd,ReconciledUsd FROM BudgetReservations
            """;

        Assert.Equal(1, await f.Query.ExecuteNonQueryAsync());

        GuildSpendCorrectionRequest request = new(Guid.NewGuid(), revision, GuildSpendOrigin.OperatorAttested,
            new(110, 25, 45, 6, .30m), new string('B', 64));

        var result = await f.Scenario.Fixture.Store.CorrectOperatorSpendAsync(f.Scenario.Fixture.Caller, f.QuestId,
            f.Scenario.EffectId, request, default);

        Assert.True(result.IsFailure);

        Assert.Equal(receipts, await f.Scenario.Fixture.CountAsync("guild_spend_receipts"));

        Assert.Equal(evidence, await f.Scenario.Fixture.CountAsync("guild_budget_evidence"));

        Assert.Equal(events, await f.Scenario.Fixture.CountAsync("guild_events"));

        f.Query.CommandText = "SELECT Revision FROM guild_budget_effects WHERE EffectId=$effect";

        Assert.Equal(revision, await f.Query.ExecuteScalarAsync());

        f.Query.CommandText = "SELECT ActualCostUsd FROM BillableOperations";

        Assert.Equal("0", await f.Query.ExecuteScalarAsync());

        f.Query.CommandText = "SELECT COUNT(*) FROM BudgetReservations WHERE ReconciledUsd<>'0'";

        Assert.Equal(0L, await f.Query.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Provider_refresh_admission_failure_rolls_back_operation_and_allows_retry()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        Guid refresh = Guid.NewGuid();

        long operations = await f.Scenario.Fixture.CountAsync("LongRunningOperations");

        long refreshes = await f.Scenario.Fixture.CountAsync("guild_spend_refreshes");

        f.Query.CommandText = "CREATE TRIGGER fixture_abort_refresh_admission BEFORE INSERT ON guild_spend_refreshes BEGIN SELECT RAISE(ABORT,'injected'); END";

        await f.Query.ExecuteNonQueryAsync();

        var failed = await f.Scenario.Fixture.Store.AdmitProviderSpendRefreshAsync(f.Scenario.Fixture.Caller, f.QuestId,
            f.Scenario.EffectId, refresh, f.Currentness, default);

        Assert.True(failed.IsFailure);

        Assert.Equal(operations, await f.Scenario.Fixture.CountAsync("LongRunningOperations"));

        Assert.Equal(refreshes, await f.Scenario.Fixture.CountAsync("guild_spend_refreshes"));

        f.Query.CommandText = "DROP TRIGGER fixture_abort_refresh_admission";

        await f.Query.ExecuteNonQueryAsync();

        var retry = await f.Scenario.Fixture.Store.AdmitProviderSpendRefreshAsync(f.Scenario.Fixture.Caller, f.QuestId,
            f.Scenario.EffectId, refresh, f.Currentness, default);

        Assert.True(retry.IsSuccess, retry.IsFailure ? retry.Error.Message : null);

        Assert.Equal(operations + 1, await f.Scenario.Fixture.CountAsync("LongRunningOperations"));

        Assert.Equal(refreshes + 1, await f.Scenario.Fixture.CountAsync("guild_spend_refreshes"));
    }

    [Fact]
    public async Task Provider_refresh_preflight_replays_receipt_before_pruned_source_eligibility()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        var applied = await f.ApplyAsync();

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : null);

        f.Query.CommandText = "DELETE FROM BillableOperations";

        Assert.Equal(1, await f.Query.ExecuteNonQueryAsync());

        var replay = await f.Scenario.Fixture.Store.PrepareProviderSpendRefreshAsync(f.Scenario.Fixture.Caller, f.QuestId,
            f.Scenario.EffectId, f.RefreshId, default);

        Assert.True(replay.IsSuccess, replay.IsFailure ? replay.Error.Message : null);

        Assert.Equal(applied.Value.Receipt, replay.Value.Receipt);

        Assert.Null(replay.Value.Provider);

        var changed = await f.Scenario.Fixture.Store.PrepareProviderSpendRefreshAsync(f.Scenario.Fixture.Caller, f.QuestId,
            Guid.NewGuid(), f.RefreshId, default);

        Assert.True(changed.IsFailure);
    }
}
