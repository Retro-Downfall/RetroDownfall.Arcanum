using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_cleanup_waits_for_real_refresh_body_disposal_before_sealing(bool cancelWaiter)
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        await using SqliteConnection connection = await f.Scenario.Fixture.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        using LongRunningOperationStore operations = new(db, f.Scenario.Fixture.Connections);

        string lockPath = Path.Combine(Path.GetTempPath(), "arcanum-owner-refresh-" + Guid.NewGuid().ToString("N"));

        var acquired = GuildWorkspaceLeaseHostLock.TryAcquire(lockPath);

        Assert.True(acquired.IsSuccess);

        using GuildWorkspaceLeaseHostLock hostLock = acquired.Value;

        OwnerRefreshStream stream = new();

        GuildSpendRefreshTracker tracker = new();

        GuildSpendRefreshService service = new(f.Scenario.Fixture.Store, f.Configuration, f.Resolver,
            new GuildOpenRouterSpendReader(new PublicSpendDns(), () => new OwnerRefreshHandler(stream)),
            new RefreshTestReadiness(hostLock), f.Scenario.Fixture.Gate, operations, new LongRunningOperationOwnership(), TimeProvider.System, tracker);

        Task<Result<GuildSpendRefreshResult>> running = service.RefreshAsync(f.Scenario.Fixture.Caller, f.QuestId,
            f.Scenario.EffectId, Guid.NewGuid(), default);

        Task<Result<GuildCleanupOutcome>>? cleanup = null;

        using CancellationTokenSource interruption = new();

        Assert.True(tracker.TryEnter(Guid.NewGuid(), out var unrelated));

        Assert.NotNull(unrelated);

        try
        {
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));

            await using SqliteCommand owner = connection.CreateCommand();

            owner.CommandText = "SELECT SessionId FROM guild_quests WHERE Id=$quest";

            owner.Parameters.AddWithValue("$quest", f.QuestId.ToString("D").ToUpperInvariant());

            string session = Assert.IsType<string>(await owner.ExecuteScalarAsync());

            using (CovenantSqliteConnectionInitializer.Instance.Authorize(connection, CovenantSqliteAuthorizationKind.SessionRetention))
            {
                owner.CommandText = "DELETE FROM Sessions WHERE Id=$session";

                owner.Parameters.AddWithValue("$session", session);

                Assert.Equal(1, await owner.ExecuteNonQueryAsync());
            }

            OwnerCleanupSealWitness witness = new();

            GuildOwnerCleanupCoordinator coordinator = new(f.Scenario.Fixture.Connections, f.Scenario.Fixture.Gate,
                new OwnerCleanupPurger(), FixtureLabeledArtifactGuard.For(db),
                [new GuildSignetCleanupParticipant(f.Scenario.Fixture.Gate), witness], TimeProvider.System,
                new GuildSpendRefreshCleanupParticipant(tracker));

            cleanup = coordinator.RunBatchAsync(8, 8, interruption.Token);

            Task observed = await Task.WhenAny(stream.Cancelled.Task, witness.SealEntered.Task, cleanup)
                .WaitAsync(TimeSpan.FromSeconds(15));

            if (ReferenceEquals(observed, cleanup))
            {
                Result<GuildCleanupOutcome> premature = await cleanup;

                Assert.Fail(premature.IsFailure ? $"Cleanup failed before signaling refresh: {premature.Error.Code}" : "Cleanup completed before signaling refresh.");
            }

            Assert.True(ReferenceEquals(stream.Cancelled.Task, observed), "Cleanup reached its seal before signaling the active refresh.");

            Assert.False(cleanup.IsCompleted);

            Assert.False(witness.SealEntered.Task.IsCompleted);

            Assert.False(unrelated.Stop.IsCancellationRequested);

            // The coordinator is waiting for physical work, but neither the root gate nor a
            // SQLite writer is held while this independent transaction executes.
            using CancellationTokenSource gateTimeout = new(TimeSpan.FromSeconds(10));

            Result<int> free = await GuildSignetRootGate.RunAsync<int>(f.Scenario.Fixture.Gate, f.RootId, async token =>
            {
                await using SqliteConnection probe = await f.Scenario.Fixture.OpenAsync();

                await using SqliteTransaction writer = probe.BeginTransaction(deferred: false);

                await writer.CommitAsync(token);

                return Result<int>.Success(1);
            }, gateTimeout.Token);

            Assert.True(free.IsSuccess, free.IsFailure ? free.Error.Message : null);

            if (cancelWaiter) interruption.Cancel();

            stream.ReleaseRead.TrySetResult();

            await stream.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.False(cleanup.IsCompleted);

            Assert.False(witness.SealEntered.Task.IsCompleted);

            stream.ReleaseDispose.TrySetResult();

            if (cancelWaiter)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cleanup.WaitAsync(TimeSpan.FromSeconds(15)));
            else
            {
                Result<GuildCleanupOutcome> result = await cleanup.WaitAsync(TimeSpan.FromSeconds(15));

                Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

                Assert.True(witness.SealEntered.Task.IsCompleted);
            }

            await running.WaitAsync(TimeSpan.FromSeconds(15));

            var replay = await service.RefreshAsync(f.Scenario.Fixture.Caller, f.QuestId, f.Scenario.EffectId, Guid.NewGuid(), default);

            Assert.True(replay.IsFailure);
        }
        finally
        {
            stream.ReleaseRead.TrySetResult();

            stream.ReleaseDispose.TrySetResult();

            await running;

            unrelated.Dispose();

            if (cleanup is not null)
            {
                try { await cleanup; }
                catch (OperationCanceledException) when (cancelWaiter) { }
            }
        }
    }

    [Fact]
    public async Task Composed_cleanup_and_readiness_share_one_refresh_tracker()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = await ReceiptRootAsync(f);

        await using SqliteConnection connection = await f.OpenAsync();

        Guid session = root.Quest.SessionId!.Value;

        await using SqliteCommand command = connection.CreateCommand();

        command.Parameters.AddWithValue("$session", session.ToString("D").ToUpperInvariant());

        using (CovenantSqliteConnectionInitializer.Instance.Authorize(connection, CovenantSqliteAuthorizationKind.SessionRetention))
        {
            command.CommandText = "DELETE FROM Sessions WHERE Id=$session";

            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        command.CommandText = "SELECT Sequence FROM owner_deletion_events WHERE OwnerKindCode=2 AND OwnerId=$session";

        long sequence = Assert.IsType<long>(await command.ExecuteScalarAsync());

        command.CommandText = "INSERT INTO guild_owner_cleanup(OwnerKindCode,OwnerId,EventSequence,PhaseCode,CreatedAtUtc,UpdatedAtUtc) VALUES(2,$session,$sequence,0,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z')";

        command.Parameters.AddWithValue("$sequence", sequence);

        Assert.Equal(1, await command.ExecuteNonQueryAsync());

        ServiceCollection services = new();

        services.AddArcanumInfrastructure(new ConfigurationBuilder().Build());

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(GuildSpendRefreshTracker));

        await using ServiceProvider provider = services.BuildServiceProvider();

        using IServiceScope scope = provider.CreateScope();

        GuildSpendRefreshTracker tracker = provider.GetRequiredService<GuildSpendRefreshTracker>();

        GuildSpendRefreshCleanupParticipant cleanup = scope.ServiceProvider.GetRequiredService<GuildSpendRefreshCleanupParticipant>();

        Assert.True(tracker.TryEnter(root.Quest.RootQuestId, out var entry));

        Assert.NotNull(entry);

        TaskCompletionSource signaled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using CancellationTokenRegistration signal = entry.Stop.Register(() => signaled.TrySetResult());

        Task<Result<GuildSpendRefreshCleanupParticipant.Scope>> closing = cleanup.PrepareAsync(new(2, session, sequence), connection, default);

        await signaled.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(entry.Stop.IsCancellationRequested);

        Assert.False(closing.IsCompleted);

        entry.Dispose();

        Result<GuildSpendRefreshCleanupParticipant.Scope> prepared = await closing.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : null);

        prepared.Value.Dispose();

        Assert.True(tracker.TryEnter(Guid.NewGuid(), out var hostEntry));

        Assert.NotNull(hostEntry);

        GuildLocalHostReadiness readiness = provider.GetRequiredService<GuildLocalHostReadiness>();

        Task stop = readiness.StopAsync(default);

        Assert.True(hostEntry.Stop.IsCancellationRequested);

        Assert.False(stop.IsCompleted);

        hostEntry.Dispose();

        await stop.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_cleanup_writer_refuses_changed_evidence_or_new_affected_root(bool changeEvidence)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = await ReceiptRootAsync(f);

        await using SqliteConnection connection = await f.OpenAsync();

        Guid session = root.Quest.SessionId!.Value;

        await using SqliteCommand command = connection.CreateCommand();

        command.Parameters.AddWithValue("$session", session.ToString("D").ToUpperInvariant());

        using (CovenantSqliteConnectionInitializer.Instance.Authorize(connection, CovenantSqliteAuthorizationKind.SessionRetention))
        {
            command.CommandText = "DELETE FROM Sessions WHERE Id=$session";

            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        command.CommandText = "SELECT Sequence FROM owner_deletion_events WHERE OwnerKindCode=2 AND OwnerId=$session";

        long sequence = Assert.IsType<long>(await command.ExecuteScalarAsync());

        command.CommandText = "INSERT INTO guild_owner_cleanup(OwnerKindCode,OwnerId,EventSequence,PhaseCode,CreatedAtUtc,UpdatedAtUtc) VALUES(2,$session,$sequence,0,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z')";

        command.Parameters.AddWithValue("$sequence", sequence);

        Assert.Equal(1, await command.ExecuteNonQueryAsync());

        GuildSpendRefreshTracker tracker = new();

        GuildSpendRefreshCleanupParticipant cleanup = new(tracker);

        GuildCleanupOwner owner = new(2, session, sequence);

        Result<GuildSpendRefreshCleanupParticipant.Scope> prepared = await cleanup.PrepareAsync(owner, connection, default);

        Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : null);

        using GuildSpendRefreshCleanupParticipant.Scope scope = prepared.Value;

        GuildSpendRefreshTracker.Entry? entry = null;

        if (!changeEvidence) Assert.True(tracker.TryEnter(root.Quest.RootQuestId, out entry));

        using (entry)
        await using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            if (changeEvidence)
            {
                command.Transaction = transaction;

                command.CommandText = "UPDATE guild_owner_cleanup SET EventSequence=$changed WHERE OwnerKindCode=2 AND OwnerId=$session";

                command.Parameters.AddWithValue("$changed", sequence + 1);

                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }

            Result valid = await cleanup.ValidateWithinAsync(owner, scope, connection, transaction, default);

            Assert.True(valid.IsFailure);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_active_root_membership_uses_root_index(bool campaign)
    {
        await using Fixture f = await Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = campaign ? """
            EXPLAIN QUERY PLAN SELECT EXISTS(SELECT 1 FROM guild_quests INDEXED BY idx_guild_quests_root WHERE RootQuestId=$root
                AND (CampaignId=$owner OR WorkspaceId=$owner) LIMIT 1);
            """ : """
            EXPLAIN QUERY PLAN SELECT EXISTS(SELECT 1 FROM guild_quests INDEXED BY idx_guild_quests_root WHERE RootQuestId=$root
                AND SessionId=$owner LIMIT 1);
            """;

        command.Parameters.AddWithValue("$root", Guid.NewGuid().ToString("D").ToUpperInvariant());

        command.Parameters.AddWithValue("$owner", Guid.NewGuid().ToString("D").ToUpperInvariant());

        List<string> details = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync()) details.Add(reader.GetString(3));

        Assert.Contains(details, detail => detail.Contains("SEARCH guild_quests USING INDEX idx_guild_quests_root", StringComparison.Ordinal));
    }

    private sealed class OwnerRefreshHandler(OwnerRefreshStream stream) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(stream) });
    }

    private sealed class OwnerRefreshStream : Stream
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Disposing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseDispose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using CancellationTokenRegistration registration = cancellationToken.Register(() => Cancelled.TrySetResult());

            Entered.TrySetResult();

            await ReleaseRead.Task;

            return 0;
        }

        public override async ValueTask DisposeAsync()
        {
            Disposing.TrySetResult();

            await ReleaseDispose.Task;

            await base.DisposeAsync();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class OwnerCleanupSealWitness : IGuildOwnerCleanupParticipant
    {
        internal TaskCompletionSource SealEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<Result> SealAsync(GuildCleanupOwner owner, SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
        {
            SealEntered.TrySetResult();

            return ValueTask.FromResult(Result.Success());
        }

        public ValueTask<Result> ReconcileAsync(GuildCleanupOwner owner, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

        public ValueTask<Result> DeleteAsync(GuildCleanupOwner owner, SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
    }

    private sealed class OwnerCleanupPurger : ICovenantSensitiveArtifactPurger
    {
        public ValueTask<Result<CovenantSensitivePurgeOutcome>> PurgeAsync(IReadOnlyList<CovenantSensitivePurgeTarget> targets, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result<CovenantSensitivePurgeOutcome>.Success(new(targets.Select(target =>
                new CovenantSensitivePurgeResult(target.ArtifactId, target.Kind, CovenantSensitivePurgeDisposition.Unlabeled, CovenantErasureBlocker.None)).ToArray(), CovenantArtifactErasureProgress.Empty)));
    }
}
