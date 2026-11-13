using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Guild;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class GuildSpendRefreshIndexTests
{
    [Fact]
    public async Task Refresh_operations_are_found_by_effect_without_scanning_all_refreshes()
    {
        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        Assert.True((await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, default)).GuildCanonical.IsHealthy);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            EXPLAIN QUERY PLAN SELECT refresh.OperationId,operation.State
            FROM guild_spend_refreshes AS refresh
            CROSS JOIN LongRunningOperations AS operation ON operation.Id=refresh.OperationId
            WHERE refresh.EffectId=$effect
            """;

        command.Parameters.AddWithValue("$effect", "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA");

        List<string> plan = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync()) plan.Add(reader.GetString(3));

        Assert.Contains(plan, line => line.Contains("SEARCH refresh", StringComparison.Ordinal)
            && line.Contains("idx_guild_spend_refreshes_effect", StringComparison.Ordinal));

        Assert.DoesNotContain(plan, line => line.Contains("SCAN refresh", StringComparison.Ordinal));
    }

    private const string V16Fingerprint = "CCD0B587F7913F2F0652E88EFDAC17128F556C6CB6ADA704E24CE0BEE9E10944";

    private static IReadOnlyList<GrimoireSchemaObject> V16Objects() => JsonSerializer.Deserialize(
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "Schema", "Guild16.json")),
        Core37FixtureJsonContext.Default.GrimoireSchemaObjectArray)!;

    private static GrimoireSchemaVersionChainSet V16Chains()
    {
        GrimoireSchemaVersionChainSet current = GrimoireSchemaVersionChains.Default;

        IReadOnlyList<GrimoireSchemaObject> objects = V16Objects();

        return new([
            current.ForTier(GrimoireSchemaTransactionTier.Core),
            current.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            current.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
            new GrimoireSchemaVersionChain(GrimoireSchemaManifestBuilder.Build(GrimoireSchemaFamily.Guild,
                GrimoireSchemaTransactionTier.GuildCanonical, 16, V16Fingerprint, objects), objects,
                [.. current.ForTier(GrimoireSchemaTransactionTier.GuildCanonical).Steps.Where(step => step.ToVersion <= 16)]),
        ]);
    }

    [Fact]
    public async Task Version_sixteen_upgrade_preserves_refresh_evidence_and_matches_fresh_install()
    {
        Assert.Equal(V16Fingerprint, GrimoireSchemaCatalog.ComputeSourceFingerprint(V16Objects()));

        await using GuildQuestLifecycleTests.Fixture fixture = await GuildQuestLifecycleTests.Fixture.CreateAsync(chains: V16Chains());

        await SeedAsync(fixture.Connection);

        string core = (string)(await ScalarAsync(fixture.Connection, "SELECT SourceDefinitionFingerprint || InstalledCatalogFingerprint FROM grimoire_feature_schemas WHERE FamilyCode=0"))!;

        Assert.True((await GrimoireSchemaTestInstaller.InstallAsync(fixture.Connection, 768, default)).GuildCanonical.IsHealthy);

        Assert.Equal(core, await ScalarAsync(fixture.Connection, "SELECT SourceDefinitionFingerprint || InstalledCatalogFingerprint FROM grimoire_feature_schemas WHERE FamilyCode=0"));

        Assert.Equal(2L, await ScalarAsync(fixture.Connection, "SELECT count(*) FROM guild_spend_refreshes WHERE EffectId='77777777-7777-7777-7777-777777777777'"));

        Assert.Equal(1L, await ScalarAsync(fixture.Connection, "SELECT count(*) FROM guild_spend_refreshes WHERE AcceptedReceiptId='66666666-6666-6666-6666-666666666666'"));

        Assert.Equal(0L, await ScalarAsync(fixture.Connection, "SELECT count(*) FROM pragma_foreign_key_check"));

        await using (SqliteCommand lookup = fixture.Connection.CreateCommand())
        {
            lookup.CommandText = """
                SELECT group_concat(State, ',') FROM (SELECT operation.State FROM guild_spend_refreshes refresh
                  JOIN LongRunningOperations operation ON operation.Id=refresh.OperationId
                  WHERE refresh.EffectId=$effect ORDER BY operation.State)
                """;

            lookup.Parameters.AddWithValue("$effect", "77777777-7777-7777-7777-777777777777");

            Assert.Equal("4,7", await lookup.ExecuteScalarAsync());

            lookup.Parameters["$effect"].Value = "99999999-9999-9999-9999-999999999999";

            Assert.Equal(DBNull.Value, await lookup.ExecuteScalarAsync());
        }

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(fixture.Connection,
            "UPDATE guild_spend_refreshes SET EffectId='99999999-9999-9999-9999-999999999999'"));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(fixture.Connection,
            "UPDATE guild_spend_sources SET SourceId='99999999-9999-9999-9999-999999999999'"));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(fixture.Connection,
            "UPDATE guild_spend_receipts SET OriginalResult=X'02'"));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(fixture.Connection, """
            INSERT INTO guild_spend_refreshes(RefreshId,EffectId,OperationId,RequestDigest)
            VALUES('99999999-9999-9999-9999-999999999999','77777777-7777-7777-7777-777777777777',
              'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',zeroblob(32))
            """));

        await using SqliteConnection fresh = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        Assert.True((await GrimoireSchemaTestInstaller.InstallAsync(fresh, 768, default)).GuildCanonical.IsHealthy);

        const string identity = "SELECT SourceDefinitionFingerprint || InstalledCatalogFingerprint FROM grimoire_feature_schemas WHERE FamilyCode=2";

        Assert.Equal(await ScalarAsync(fresh, identity), await ScalarAsync(fixture.Connection, identity));

        await AssertPlansAsync(fixture.Connection);
    }

    [Fact]
    public async Task Interrupted_index_transition_rolls_back_then_reopens_without_losing_refreshes()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using (GuildQuestLifecycleTests.Fixture fixture = await GuildQuestLifecycleTests.Fixture.CreateAsync(chains: V16Chains()))
        {
            await SeedAsync(fixture.Connection);

            await using SqliteConnection original = await file.OpenAsync(default);

            fixture.Connection.BackupDatabase(original);
        }

        GrimoireSchemaVersionChainSet standard = GrimoireSchemaVersionChains.Default;

        GrimoireSchemaVersionChain guild = standard.ForTier(GrimoireSchemaTransactionTier.GuildCanonical);

        GrimoireSchemaVersionStep last = guild.Steps.Single(step => step.ToVersion == 17);

        GrimoireSchemaVersionStep fault = last with { Statements = [.. last.Statements,
            new("fixture.failure", 20, "failure", "SELECT * FROM missing_refresh_index_fixture;")] };

        GrimoireSchemaVersionChainSet interrupted = new([
            standard.ForTier(GrimoireSchemaTransactionTier.Core),
            standard.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            standard.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
            new GrimoireSchemaVersionChain(guild.HeadManifest, guild.HeadObjects, [.. guild.Steps.Select(step => step.ToVersion == 17 ? fault : step)]),
        ]);

        await using (SqliteConnection failed = await file.OpenAsync(default))
        {
            Assert.False((await GrimoireSchemaTestInstaller.InstallAsync(failed, interrupted, 768, default)).GuildCanonical.IsHealthy);

            Assert.Equal(16L, await ScalarAsync(failed, "SELECT SchemaVersion FROM grimoire_feature_schemas WHERE FamilyCode=2"));

            Assert.Equal(0L, await ScalarAsync(failed, "SELECT count(*) FROM sqlite_schema WHERE type='index' AND name='idx_guild_spend_refreshes_effect'"));

            Assert.Equal(2L, await ScalarAsync(failed, "SELECT count(*) FROM guild_spend_refreshes"));
        }

        await using SqliteConnection reopened = await file.OpenAsync(default);

        var recovered = await GrimoireSchemaTestInstaller.InstallAsync(reopened, 768, default);

        Assert.True(recovered.GuildCanonical.IsHealthy, recovered.GuildCanonical.ToString());

        Assert.Equal(2L, await ScalarAsync(reopened, "SELECT count(*) FROM guild_spend_refreshes"));

        Assert.Equal(0L, await ScalarAsync(reopened, "SELECT count(*) FROM pragma_foreign_key_check"));

        await AssertPlansAsync(reopened);
    }

    private static async Task AssertPlansAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            EXPLAIN QUERY PLAN SELECT refresh.OperationId,operation.State
            FROM guild_budget_effects AS effect INDEXED BY idx_guild_budget_effects_accounting
            CROSS JOIN guild_spend_refreshes AS refresh ON refresh.EffectId=effect.EffectId
            CROSS JOIN LongRunningOperations AS operation ON operation.Id=refresh.OperationId
            WHERE lower(replace(effect.AccountingAssociationId,'-',''))=$run
            """;

        command.Parameters.AddWithValue("$run", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        List<string> plan = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync()) plan.Add(reader.GetString(3));

        Assert.Contains(plan, line => line.Contains("SEARCH effect", StringComparison.Ordinal) && line.Contains("idx_guild_budget_effects_accounting", StringComparison.Ordinal));

        Assert.Contains(plan, line => line.Contains("SEARCH refresh", StringComparison.Ordinal) && line.Contains("idx_guild_spend_refreshes_effect", StringComparison.Ordinal));

        Assert.Contains(plan, line => line.Contains("SEARCH operation", StringComparison.Ordinal));

        Assert.DoesNotContain(plan, line => line.Contains("SCAN refresh", StringComparison.Ordinal));
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        await command.ExecuteNonQueryAsync();
    }

    private static Task SeedAsync(SqliteConnection connection) => ExecuteAsync(connection, """
        INSERT INTO guild_budget_accounts(AccountId,OwnerKindCode,OwnerId,RootAccountId,Revision)
        VALUES('DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD',0,'11111111-1111-1111-1111-111111111111','DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD',1);
        INSERT INTO guild_quest_budget(QuestId,AccountId) VALUES('11111111-1111-1111-1111-111111111111','DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD');
        INSERT INTO guild_dispatches(DispatchId,QuestId,InvocationDigest,IsContinuation,AccountingAssociationId,EvidenceCode,Revision,CreatedAtUtc,UpdatedAtUtc)
        VALUES('EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE','11111111-1111-1111-1111-111111111111',zeroblob(32),0,
          'AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA',2,1,'2026-11-06T00:00:00.0000000Z','2026-11-06T00:00:00.0000000Z');
        INSERT INTO guild_budget_effects(EffectId,AccountId,QuestId,DispatchId,AccountingAssociationId,KindCode,SourceId,KnownUsd,KnownTokens,UnknownUsd,UnknownTokens,StateCode,Revision)
        VALUES('77777777-7777-7777-7777-777777777777','DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD','11111111-1111-1111-1111-111111111111',
          'EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE','AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA',0,'88888888-8888-8888-8888-888888888888','0.125',10,0,0,2,1);
        INSERT INTO guild_budget_evidence(ReceiptId,EffectId,Digest,KnownUsd,KnownTokens,UnknownUsd,UnknownTokens,Complete,NoDispatchProven,AppliedRevision)
        VALUES('55555555-5555-5555-5555-555555555555','77777777-7777-7777-7777-777777777777',zeroblob(32),'0.125',10,0,0,1,0,1);
        INSERT INTO guild_spend_sources(EffectId,SourceId,SourceKindCode)
        VALUES('77777777-7777-7777-7777-777777777777','88888888-8888-8888-8888-888888888888',0);
        INSERT INTO guild_spend_receipts(ReceiptId,EffectId,EvidenceReceiptId,EvidenceRevision,OriginCode,RequestIdentity,RequestDigest,OriginalResult,OriginalEventSequence,AcceptedAtUtc)
        VALUES('66666666-6666-6666-6666-666666666666','77777777-7777-7777-7777-777777777777','55555555-5555-5555-5555-555555555555',1,3,'fixture',zeroblob(32),X'01',1,'2026-11-06T00:00:00.0000000Z');
        INSERT INTO LongRunningOperations(Id,Kind,State,RecoveryPolicy,CreatedAt,PublicSummary)
        VALUES('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','guild-spend-refresh',7,2,'2026-11-06T00:00:00.0000000Z','fixture'),
              ('bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb','guild-spend-refresh',4,2,'2026-11-06T00:00:00.0000000Z','fixture');
        INSERT INTO guild_spend_refreshes(RefreshId,EffectId,OperationId,RequestDigest,AcceptedReceiptId)
        VALUES('AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA','77777777-7777-7777-7777-777777777777','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',zeroblob(32),NULL),
              ('BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB','77777777-7777-7777-7777-777777777777','bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',zeroblob(32),'66666666-6666-6666-6666-666666666666');
        """);
}
