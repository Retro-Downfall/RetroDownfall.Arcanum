using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Annals;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Lexicon;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Data;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildMemoryAttributionLifecycleTests
{
    [Fact]
    public async Task Protected_erasure_plan_counts_and_proves_absence_of_attribution_without_foreign_key_cascades()
    {
        await using MemoryFixture f = await MemoryFixture.CreateAsync();

        await f.ExecuteAsync("PRAGMA foreign_keys=OFF");

        await using SqliteTransaction transaction = f.Connection.BeginTransaction();

        var before = await CovenantArtifactPlanRunner.RunAsync(f.Connection, transaction, SensitiveArtifactKind.Lexicon,
            CovenantIdentitySql.Key(f.Entry.Id), CovenantArtifactPlanMode.Count, default);

        Assert.Equal(1L, Assert.Single(before.Targets, target => target.Table == "guild_memory_attribution").Rows);

        _ = await CovenantArtifactPlanRunner.RunAsync(f.Connection, transaction, SensitiveArtifactKind.Lexicon,
            CovenantIdentitySql.Key(f.Entry.Id), CovenantArtifactPlanMode.Delete, default);

        var after = await CovenantArtifactPlanRunner.RunAsync(f.Connection, transaction, SensitiveArtifactKind.Lexicon,
            CovenantIdentitySql.Key(f.Entry.Id), CovenantArtifactPlanMode.Count, default);

        Assert.All(after.Targets, target => Assert.Equal(0L, target.Rows));

        await transaction.CommitAsync();

        Assert.Equal(0L, await f.Signets.ScalarAsync("SELECT count(*) FROM guild_memory_attribution"));
    }

    [Fact]
    public async Task Ordinary_delete_removes_attribution_even_when_foreign_keys_are_disabled()
    {
        await using MemoryFixture f = await MemoryFixture.CreateAsync();

        await f.ExecuteAsync("PRAGMA foreign_keys=OFF");

        var removed = await f.Lexicon.DeleteByNameAsync("Attributed", LexiconScope.Global);

        Assert.True(removed.IsSuccess, removed.Error.Message);

        Assert.True(removed.Value);

        Assert.Equal(0L, await f.Signets.ScalarAsync("SELECT count(*) FROM guild_memory_attribution"));
    }

    [Theory]
    [InlineData("AgentId")]
    [InlineData("RunId")]
    [InlineData("QuestId")]
    [InlineData("SubjectId")]
    [InlineData("StoreCode")]
    public async Task Guild_integrity_refuses_attribution_that_does_not_match_its_version_and_signet(string axis)
    {
        await using MemoryFixture f = await MemoryFixture.CreateAsync();

        await f.ExecuteAsync("DROP TRIGGER guild_memory_attribution_guard_update");

        await f.ExecuteAsync(axis == "StoreCode"
            ? "UPDATE guild_memory_attribution SET StoreCode=1"
            : $"UPDATE guild_memory_attribution SET {axis}='FFFFFFFF-FFFF-4FFF-8FFF-FFFFFFFFFFFF'");

        await using SqliteTransaction transaction = f.Connection.BeginTransaction();

        await Assert.ThrowsAsync<GuildStorageIntegrityException>(() =>
            GuildStorageIntegrity.ValidateAsync(f.Connection, transaction, default, declaredSchemaVersion: 6));
    }

    [Fact]
    public async Task Absent_optional_Guild_sidecar_does_not_block_ordinary_memory_read_or_delete()
    {
        await using MemoryFixture f = await MemoryFixture.CreateAsync();

        await f.ExecuteAsync("DROP TABLE guild_memory_attribution");

        var found = await f.Lexicon.GetByNameAsync("Attributed", LexiconScope.Global);

        Assert.True(found.IsSuccess, found.Error.Message);

        Assert.NotNull(found.Value);

        Assert.True((await f.Lexicon.DeleteByNameAsync("Attributed", LexiconScope.Global)).IsSuccess);
    }

    [Fact]
    public async Task Restore_retains_historical_attribution_but_never_revives_its_execution()
    {
        await using MemoryFixture f = await MemoryFixture.CreateAsync();

        object? version = await f.Signets.ScalarAsync("SELECT ImmutableVersionId FROM guild_memory_attribution");

        Assert.True((await BackupRestoreGuildSignetSanitation.SanitizeStagedAsync(f.Connection, default)).IsSuccess);

        Assert.Equal(version, await f.Signets.ScalarAsync("SELECT ImmutableVersionId FROM guild_memory_attribution"));

        Assert.Equal(1L, await f.Signets.ScalarAsync("SELECT RestoredInert FROM guild_signets"));

        using IDisposable invocation = GuildMemoryInvocationAmbient.Begin(new(f.Signets.Authority, f.Execution));

        Assert.True((await f.Lexicon.UpsertAsync("Attributed", null, ["must not append"], LexiconScope.Global)).IsFailure);
    }

    [Fact]
    public async Task Guild_evidence_status_counts_attribution_until_its_canonical_memory_is_deleted()
    {
        await using MemoryFixture f = await MemoryFixture.CreateAsync();

        DataRetentionService retention = new(f.Db, new TestOptionsMonitor<ArcanumSettings>(new()),
            new LongRunningOperationStore(f.Db, TestOrdinaryConnectionFactory.For(f.Db)), TimeProvider.System,
            NullLogger<DataRetentionService>.Instance, FixtureLabeledArtifactGuard.For(f.Db),
            f.Root, f.Root, f.Root);

        DataRetentionStatus before = await retention.GetStatusAsync();

        long beforeEvidence = Assert.Single(before.Items, item => item.DataClass == RetentionDataClass.GuildEvidence).Rows;

        Assert.Equal(1L, await f.Signets.ScalarAsync("SELECT count(*) FROM guild_memory_attribution"));

        var deleted = await f.Lexicon.DeleteByNameAsync("Attributed", LexiconScope.Global);

        Assert.True(deleted.IsSuccess, deleted.Error.Message);

        Assert.True(deleted.Value);

        Assert.Equal(0L, await f.Signets.ScalarAsync("SELECT count(*) FROM guild_memory_attribution"));

        DataRetentionStatus after = await retention.GetStatusAsync();

        long afterEvidence = Assert.Single(after.Items, item => item.DataClass == RetentionDataClass.GuildEvidence).Rows;

        Assert.Equal(beforeEvidence - 1, afterEvidence);
    }

    [Fact]
    public async Task Campaign_memory_reset_removes_owned_attribution_and_preserves_global_memory()
    {
        await using MemoryFixture f = await MemoryFixture.CreateAsync(campaignOwned: true);

        Assert.True((await f.Lexicon.UpsertAsync("Global survivor", null, ["ordinary"], LexiconScope.Global)).IsSuccess);

        DataRetentionService retention = new(f.Db, new TestOptionsMonitor<ArcanumSettings>(new()),
            new LongRunningOperationStore(f.Db, TestOrdinaryConnectionFactory.For(f.Db)), TimeProvider.System,
            NullLogger<DataRetentionService>.Instance, FixtureLabeledArtifactGuard.For(f.Db),
            f.Root, f.Root, f.Root);

        DataRetentionRequest request = new(DataRetentionOperation.ResetMemory, f.CampaignId, MemoryResetScope.Lexicon);

        var plan = await retention.PlanAsync(request);

        var applied = await retention.ApplyAsync(new(request, plan.PlanId));

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.Equal(0L, await f.Signets.ScalarAsync("SELECT count(*) FROM guild_memory_attribution"));

        Assert.NotNull((await f.Lexicon.GetByNameAsync("Global survivor", LexiconScope.Global)).Value);
    }

    [Fact]
    public async Task Author_Session_cleanup_revokes_execution_and_preserves_global_historical_attribution()
    {
        await using MemoryFixture f = await MemoryFixture.CreateAsync(sessionOwned: true);

        object? version = await f.Signets.ScalarAsync("SELECT ImmutableVersionId FROM guild_memory_attribution");

        await f.Signets.DeleteSessionAsync(f.SessionId!.Value);

        GuildSignetCleanupParticipant participant = new(f.Signets.Gate);

        GuildCleanupOwner owner = new(2, f.SessionId.Value, null);

        Assert.True((await participant.PrepareSealAsync(owner, f.Connection, default)).IsSuccess);

        await using (SqliteTransaction transaction = f.Connection.BeginTransaction(deferred: false))
        {
            Assert.True((await participant.SealAsync(owner, f.Connection, transaction, default)).IsSuccess);

            Assert.True((await participant.DeleteAsync(owner, f.Connection, transaction, default)).IsSuccess);

            await transaction.CommitAsync();
        }

        Assert.Equal(version, await f.Signets.ScalarAsync("SELECT ImmutableVersionId FROM guild_memory_attribution"));

        Assert.Equal(1L, await f.Signets.ScalarAsync("SELECT Revoked FROM guild_signets"));

        Assert.NotNull((await f.Lexicon.GetByNameAsync("Attributed", LexiconScope.Global)).Value);

        using IDisposable invocation = GuildMemoryInvocationAmbient.Begin(new(f.Signets.Authority, f.Execution));

        Assert.True((await f.Lexicon.UpsertAsync("Attributed", null, ["refused"], LexiconScope.Global)).IsFailure);
    }

    private sealed class MemoryFixture : IAsyncDisposable
    {
        internal required GuildSignetAuthorityTests.Fixture Signets { get; init; }

        internal required SqliteConnection Connection { get; init; }

        internal required ArcanumDbContext Db { get; init; }

        internal required LexiconService Lexicon { get; init; }

        internal required GuildExecutionHandle Execution { get; init; }

        internal required LexiconEntryDto Entry { get; init; }

        internal required string Root { get; init; }

        internal Guid? SessionId { get; init; }

        internal Guid? CampaignId { get; init; }

        internal static async Task<MemoryFixture> CreateAsync(bool campaignOwned = false, bool sessionOwned = false)
        {
            var signets = await GuildSignetAuthorityTests.Fixture.CreateAsync();

            SqliteConnection connection = await signets.OpenAsync();

            ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

            LexiconService lexicon = new(db, NullLogger<LexiconService>.Instance,
                new TestOptionsMonitor<ArcanumSettings>(new() { Features = new() { Annals = false } }), MemoryErasureTestKeys.Isolated());

            Guid? campaign = campaignOwned ? Guid.NewGuid() : null;

            Guid? authorSession = null;

            if (campaignOwned || sessionOwned)
            {
                Guid owner = campaign ?? Guid.NewGuid();

                Guid session = Guid.NewGuid();

                authorSession = session;

                await signets.SeedOwnersAsync(owner, session, Guid.NewGuid());

                signets.Policy.NextOwners = new(session, owner, null);
            }

            GuildGrant grant = signets.Grant with
            {
                Memory = [new(GuildMemoryStore.Lexicon, GuildMemoryOperation.Append,
                    new(campaign is null ? GuildMemoryScopeKind.Global : GuildMemoryScopeKind.Campaign, campaign))],
            };

            signets.Policy.HostGrant = grant;

            GuildExecutionHandle execution = await signets.RootAsync(grant);

            LexiconEntryDto entry;

            using (GuildMemoryInvocationAmbient.Begin(new(signets.Authority, execution)))
            {
                var written = await lexicon.UpsertAsync("Attributed", null, ["guild fact"], LexiconScope.ForResolvedCampaign(campaign));

                Assert.True(written.IsSuccess, written.Error.Message);

                entry = written.Value;
            }

            string root = Path.Combine(Path.GetTempPath(), "arcanum-guild-attribution-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(root);

            return new() { Signets = signets, Connection = connection, Db = db, Lexicon = lexicon,
                Execution = execution, Entry = entry, CampaignId = campaign, SessionId = authorSession, Root = root };
        }

        internal async Task ExecuteAsync(string sql)
        {
            await using SqliteCommand command = Connection.CreateCommand();

            command.CommandText = sql;

            _ = await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();

            await Connection.DisposeAsync();

            await Signets.DisposeAsync();

            Directory.Delete(Root, true);
        }
    }
}
