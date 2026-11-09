using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Guild;
using Xunit.Abstractions;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class GuildCancellationSchemaTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Cancellation_inventory_seeks_its_durable_unconverged_frontier_without_scanning_history()
    {
        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        Assert.True((await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, default)).GuildCanonical.IsHealthy);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "EXPLAIN QUERY PLAN " + GuildLocalQuestScheduler.CancellationFrontierQuery;

        command.Parameters.AddWithValue("$after", string.Empty);

        List<string> plan = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            plan.Add(reader.GetString(3));
        }

        output.WriteLine(string.Join(System.Environment.NewLine, plan));

        Assert.Contains(plan, line => line.Contains("SEARCH quest USING", StringComparison.Ordinal)
            && line.Contains("idx_guild_quests_cancellation", StringComparison.Ordinal)
            && line.Contains("CancellationRequested=? AND CancellationConverged=? AND Retired=? AND Id>?", StringComparison.Ordinal));
    }

    [Fact]
    public void V10_transition_preserves_the_published_v9_fingerprint_and_installs_exact_canonical_indexes()
    {
        Assert.Equal(GuildCanonicalSchemaVersionNineFixture.PublishedFingerprint, GuildCanonicalSchemaVersionNineFixture.Fingerprint);

        GrimoireSchemaVersionChain chain = GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.GuildCanonical);

        Assert.True(chain.TryGetStep(9, out GrimoireSchemaVersionStep step));

        Assert.Equal(GuildCanonicalSchemaVersionNineFixture.PublishedFingerprint, step.FromSourceDefinitionFingerprint);

        Assert.Equal(["idx_guild_budget_effects_quest", "idx_guild_workspace_leases_quest", "idx_guild_quests_cancellation"],
            step.Statements.Select(statement => statement.Name));

        foreach (GrimoireSchemaTransitionStatement statement in step.Statements)
        {
            GrimoireSchemaObject canonical = Assert.Single(GrimoireSchemaCatalog.GuildCanonicalObjects,
                item => item.Name == statement.Name);

            Assert.Equal(GrimoireSqlNormalizer.Normalize(canonical.Sql), GrimoireSqlNormalizer.Normalize(statement.Sql));
        }
    }

    [Fact]
    public async Task V9_upgrade_preserves_cancellation_paid_exposure_and_admitted_writer_and_matches_fresh_v10()
    {
        await using GuildQuestLifecycleTests.Fixture fixture = await GuildQuestLifecycleTests.Fixture.CreateAsync(
            chains: GuildCanonicalSchemaVersionNineFixture.ChainSet());

        SqliteConnection connection = fixture.Connection;

        await fixture.ExecuteAsync("""
            UPDATE guild_quests SET CancellationRequested=1,AccountingStateCode=2,Revision=Revision+1;
            UPDATE LongRunningOperations SET State=3;
            INSERT INTO guild_quest_cancellations(QuestId,IdempotencyKey,CanonicalRequestDigest,ExpectedRevision,
                ReasonCode,AcceptedRevision,AcceptedAtUtc)
            VALUES('11111111-1111-1111-1111-111111111111','33333333-3333-3333-3333-333333333333',zeroblob(32),1,0,2,
                '2026-11-06T00:00:00.0000000Z');
            INSERT INTO guild_budget_accounts(AccountId,OwnerKindCode,OwnerId,RootAccountId,UsdLimit,TokenLimit,Revision)
            VALUES('DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD',0,'11111111-1111-1111-1111-111111111111',
                'DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD','1',100,1);
            INSERT INTO guild_quest_budget(QuestId,AccountId)
            VALUES('11111111-1111-1111-1111-111111111111','DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD');
            INSERT INTO guild_dispatches(DispatchId,QuestId,InvocationDigest,IsContinuation,AccountingAssociationId,EvidenceCode,
                Revision,CreatedAtUtc,UpdatedAtUtc)
            VALUES('EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE','11111111-1111-1111-1111-111111111111',zeroblob(32),0,
                'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF',2,1,'2026-11-06T00:00:00.0000000Z','2026-11-06T00:00:00.0000000Z');
            INSERT INTO guild_budget_effects(EffectId,AccountId,QuestId,DispatchId,AccountingAssociationId,KindCode,SourceId,
                UsdBound,TokenBound,UnknownUsd,UnknownTokens,StateCode,Revision)
            VALUES('77777777-7777-7777-7777-777777777777','DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD',
                '11111111-1111-1111-1111-111111111111','EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE',
                'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF',0,'77777777-7777-7777-7777-777777777777','1',100,1,1,1,1);
            """);

        GuildWorkspaceLeaseRecord lease = GuildWorkspaceLeaseStoreTests.Row();

        await using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            var principal = await GuildWorkspaceLeaseStore.GetNativeOperatorPrincipalWithinAsync(connection, transaction,
                lease.Owner.InstallationGeneration, default);

            Assert.True(principal.IsSuccess, principal.Error.Message);

            lease = lease with { Owner = lease.Owner with { PrincipalId = principal.Value } };

            Assert.True((await GuildWorkspaceLeaseStore.InsertWithinAsync(connection, transaction, lease, default)).IsSuccess);

            var acquisition = await GuildWorkspaceLeaseStore.RecordMutationWithinAsync(connection, transaction, lease,
                new(lease.IdempotencyKey, lease.RequestDigest), GuildAuthorityAction.LeaseAcquire, lease.HeartbeatAt, default);

            Assert.True(acquisition.IsSuccess, acquisition.Error.Message);

            var admitted = await GuildWorkspaceLeaseStore.AdmitPermitWithinAsync(connection, transaction, lease, lease.HeartbeatAt, default);

            Assert.True(admitted.IsSuccess, admitted.Error.Message);

            await transaction.CommitAsync();
        }

        object? otherFamilies = await fixture.ScalarAsync("SELECT group_concat(SourceDefinitionFingerprint || InstalledCatalogFingerprint) FROM grimoire_feature_schemas WHERE FamilyCode<>2");

        object? epoch = await fixture.ScalarAsync("SELECT Epoch FROM guild_signet_authority_state");

        var installed = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, default);

        Assert.True(installed.GuildCanonical.IsHealthy, installed.GuildCanonical.ToString());

        Assert.Equal(10, installed.GuildCanonical.SchemaVersion);

        Assert.Equal(otherFamilies, await fixture.ScalarAsync("SELECT group_concat(SourceDefinitionFingerprint || InstalledCatalogFingerprint) FROM grimoire_feature_schemas WHERE FamilyCode<>2"));

        Assert.Equal(epoch, await fixture.ScalarAsync("SELECT Epoch FROM guild_signet_authority_state"));

        Assert.Equal(1L, await fixture.ScalarAsync("SELECT count(*) FROM guild_quest_cancellations WHERE ExpectedRevision=1 AND AcceptedRevision=2"));

        Assert.Equal(1L, await fixture.ScalarAsync("SELECT count(*) FROM guild_quests WHERE CancellationRequested=1 AND AccountingStateCode=2"));

        Assert.Equal(1L, await fixture.ScalarAsync("SELECT count(*) FROM guild_budget_effects WHERE StateCode=1 AND UnknownUsd=1 AND UnknownTokens=1"));

        Assert.Equal(1L, await fixture.ScalarAsync("SELECT count(*) FROM guild_workspace_leases WHERE StateCode=1 AND InFlightMutationCount=1"));

        Assert.Equal(1L, await fixture.ScalarAsync("SELECT count(*) FROM guild_workspace_lease_mutations WHERE ActionCode=3"));

        const string identity = "SELECT SourceDefinitionFingerprint || InstalledCatalogFingerprint FROM grimoire_feature_schemas WHERE FamilyCode=2";

        object? upgraded = await fixture.ScalarAsync(identity);

        await using SqliteConnection fresh = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        Assert.True((await GrimoireSchemaTestInstaller.InstallAsync(fresh, 768, default)).GuildCanonical.IsHealthy);

        await using SqliteCommand catalog = fresh.CreateCommand();

        catalog.CommandText = identity;

        Assert.Equal(upgraded, await catalog.ExecuteScalarAsync());

        Assert.True((await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, default)).GuildCanonical.IsHealthy);
    }

    [Theory]
    [InlineData("guild_budget_effects", "idx_guild_budget_effects_quest", "StateCode IN(0,1)")]
    [InlineData("guild_workspace_leases", "idx_guild_workspace_leases_quest", "StateCode IS NOT 3 OR InFlightMutationCount IS NOT 0")]
    public async Task Cancellation_physical_obligation_probes_seek_by_quest(string table, string index, string obligation)
    {
        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        var installed = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, default);

        Assert.True(installed.GuildCanonical.IsHealthy, installed.GuildCanonical.ToString());

        output.WriteLine("Published Guild fingerprint: " + GrimoireSchemaCatalog.GuildCanonicalSchemaFingerprint);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = $"EXPLAIN QUERY PLAN SELECT 1 FROM {table} WHERE QuestId=$quest AND ({obligation})";

        command.Parameters.AddWithValue("$quest", Guid.NewGuid().ToString("D").ToUpperInvariant());

        List<string> plan = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            plan.Add(reader.GetString(3));
        }

        Assert.Contains(plan, line => line.Contains("SEARCH", StringComparison.Ordinal)
            && line.Contains(index, StringComparison.Ordinal) && line.Contains("QuestId=?", StringComparison.Ordinal));
    }
}
