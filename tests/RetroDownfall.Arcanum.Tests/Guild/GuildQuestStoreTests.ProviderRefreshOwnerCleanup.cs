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
