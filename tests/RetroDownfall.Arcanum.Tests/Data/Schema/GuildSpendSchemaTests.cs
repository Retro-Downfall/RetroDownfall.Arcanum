using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Guild;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class GuildSpendSchemaTests
{
    [Fact]
    public async Task V14_upgrade_preserves_deleted_operator_sequence_high_water_and_converges()
    {
        Assert.Equal(GuildCanonicalSchemaVersionFourteenFixture.PublishedFingerprint, GuildCanonicalSchemaVersionFourteenFixture.Fingerprint);

        await using var upgraded = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        Assert.True((await GrimoireSchemaTestInstaller.InstallAsync(upgraded, GuildCanonicalSchemaVersionFourteenFixture.ChainSet(), 768, default)).GuildCanonical.IsHealthy);

        await using var command = upgraded.CreateCommand();

        command.CommandText = "INSERT INTO sqlite_sequence(name,seq) VALUES('guild_operator_sequences',900) ON CONFLICT DO NOTHING";

        // sqlite_sequence has no unique constraint; no row exists before the first allocation.
        await command.ExecuteNonQueryAsync();

        command.CommandText = """
            INSERT INTO guild_event_streams(RootQuestId,LastSequence,RetainedAfterSequence)
            VALUES('11111111-1111-1111-1111-111111111111',7,2);
            INSERT INTO guild_events(RootQuestId,EventSequence,EventKindCode,ObjectId,Revision,QuestId,CreatedAtUtc)
            VALUES('11111111-1111-1111-1111-111111111111',7,0,'22222222-2222-2222-2222-222222222222',1,
                '11111111-1111-1111-1111-111111111111','2026-01-01T00:00:00.0000000Z');
            INSERT INTO guild_portal_cursors(Id,InstallationGeneration,CallerKey,RootQuestId,ScopeKey,AfterSequence,
                SnapshotSequence,CreatedAtUtc,AuthorityEpoch,VisibilityKey)
            VALUES('33333333-3333-3333-3333-333333333333','44444444-4444-4444-4444-444444444444','caller',
                '11111111-1111-1111-1111-111111111111','scope',2,7,'2026-01-01T00:00:00.0000000Z',
                '55555555-5555-5555-5555-555555555555','visibility');
            """;

        await command.ExecuteNonQueryAsync();

        command.CommandText = "SELECT SourceDefinitionFingerprint || InstalledCatalogFingerprint FROM grimoire_feature_schemas WHERE FamilyCode=0";

        object? originalCore = await command.ExecuteScalarAsync();

        Assert.NotNull(originalCore);

        var migrated = await GrimoireSchemaTestInstaller.InstallAsync(upgraded, 768, default);

        Assert.True(migrated.GuildCanonical.IsHealthy, migrated.GuildCanonical.ToString());

        command.CommandText = "SELECT SourceDefinitionFingerprint || InstalledCatalogFingerprint FROM grimoire_feature_schemas WHERE FamilyCode=0";

        Assert.Equal(originalCore, await command.ExecuteScalarAsync());

        command.CommandText = "SELECT count(*) FROM guild_events WHERE EventSequence=7 AND SpendEffectId IS NULL AND SpendReceiptId IS NULL AND SpendEvidenceRevision IS NULL AND SpendOriginCode IS NULL";

        Assert.Equal(1L, await command.ExecuteScalarAsync());

        command.CommandText = "SELECT AfterSequence || ':' || SnapshotSequence FROM guild_portal_cursors";

        Assert.Equal("2:7", await command.ExecuteScalarAsync());

        command.CommandText = "SELECT seq FROM sqlite_sequence WHERE name='guild_operator_sequences'";

        Assert.Equal(900L, await command.ExecuteScalarAsync());

        command.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='guild_spend_sources'";

        Assert.Equal(1L, await command.ExecuteScalarAsync());

        await using var fresh = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        Assert.True((await GrimoireSchemaTestInstaller.InstallAsync(fresh, 768, default)).GuildCanonical.IsHealthy);

        await using var freshCommand = fresh.CreateCommand();

        command.CommandText = freshCommand.CommandText = "SELECT SourceDefinitionFingerprint || InstalledCatalogFingerprint FROM grimoire_feature_schemas WHERE FamilyCode=2";

        Assert.Equal(await freshCommand.ExecuteScalarAsync(), await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Fresh_spend_authority_has_a_random_key_and_partial_event_tuples_are_rejected()
    {
        await using var connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        Assert.True((await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, default)).GuildCanonical.IsHealthy);

        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT count(*) FROM guild_spend_authority_state WHERE length(CredentialKey)=32";

        Assert.Equal(1L, await command.ExecuteScalarAsync());

        command.CommandText = """
            INSERT INTO guild_events(RootQuestId,EventSequence,EventKindCode,ObjectId,Revision,QuestId,CreatedAtUtc,SpendEffectId)
            VALUES('11111111-1111-1111-1111-111111111111',1,0,'22222222-2222-2222-2222-222222222222',1,
                '11111111-1111-1111-1111-111111111111','2026-01-01T00:00:00.0000000Z','33333333-3333-3333-3333-333333333333')
            """;

        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());

        command.CommandText = """
            INSERT INTO guild_events(RootQuestId,EventSequence,EventKindCode,ObjectId,Revision,QuestId,CreatedAtUtc,
                SpendEffectId,SpendReceiptId,SpendEvidenceRevision,SpendOriginCode)
            VALUES('11111111-1111-1111-1111-111111111111',1,0,'22222222-2222-2222-2222-222222222222',1,
                '11111111-1111-1111-1111-111111111111','2026-01-01T00:00:00.0000000Z',
                'XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX','44444444-4444-4444-4444-444444444444',1,0)
            """;

        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Scoped_membership_pages_seek_within_the_requested_owner(int kind)
    {
        await using var connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        Assert.True((await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, default)).GuildCanonical.IsHealthy);

        await using var command = connection.CreateCommand();

        command.CommandText = "EXPLAIN QUERY PLAN SELECT Sequence,ObjectId FROM guild_operator_sequences WHERE KindCode=$kind AND ScopeId=$scope AND Sequence>$after ORDER BY Sequence LIMIT 20";

        command.Parameters.AddWithValue("$kind", kind);

        command.Parameters.AddWithValue("$scope", "11111111-1111-1111-1111-111111111111");

        command.Parameters.AddWithValue("$after", 0);

        List<string> plan = [];

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync()) plan.Add(reader.GetString(3));

        Assert.Contains(plan, line => line.Contains("SEARCH", StringComparison.Ordinal)
            && line.Contains("idx_guild_operator_sequences_scope", StringComparison.Ordinal)
            && line.Contains("ScopeId=?", StringComparison.Ordinal));

        Assert.DoesNotContain(plan, line => line.Contains("SCAN guild_operator_sequences", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Interrupted_v15_transition_rolls_back_and_reopens_from_v14()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using (SqliteConnection original = await file.OpenAsync(default))
        {
            Assert.True((await GrimoireSchemaTestInstaller.InstallAsync(original, GuildCanonicalSchemaVersionFourteenFixture.ChainSet(), 768, default)).GuildCanonical.IsHealthy);

            await using SqliteCommand seed = original.CreateCommand();

            seed.CommandText = "INSERT INTO sqlite_sequence(name,seq) VALUES('guild_operator_sequences',900)";

            await seed.ExecuteNonQueryAsync();
        }

        GrimoireSchemaVersionChainSet standard = GrimoireSchemaVersionChains.Default;

        GrimoireSchemaVersionChain guild = standard.ForTier(GrimoireSchemaTransactionTier.GuildCanonical);

        GrimoireSchemaVersionStep v15 = guild.Steps.Single(step => step.ToVersion == 15);

        GrimoireSchemaVersionStep fault = v15 with
        {
            Statements = [.. v15.Statements, new("fixture.failure", 35, "failure", "SELECT * FROM no_such_transition_table")],
        };

        GrimoireSchemaVersionChainSet interrupted = new(
        [
            standard.ForTier(GrimoireSchemaTransactionTier.Core),
            standard.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            standard.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
            new GrimoireSchemaVersionChain(guild.HeadManifest, guild.HeadObjects, [.. guild.Steps.Select(step => step.ToVersion == 15 ? fault : step)]),
        ]);

        await using (SqliteConnection failed = await file.OpenAsync(default))
        {
            var result = await GrimoireSchemaTestInstaller.InstallAsync(failed, interrupted, 768, default);

            Assert.False(result.GuildCanonical.IsHealthy);

            await using SqliteCommand verify = failed.CreateCommand();

            verify.CommandText = "SELECT SchemaVersion FROM grimoire_feature_schemas WHERE FamilyCode=2";

            Assert.Equal(14L, await verify.ExecuteScalarAsync());

            verify.CommandText = "SELECT seq FROM sqlite_sequence WHERE name='guild_operator_sequences'";

            Assert.Equal(900L, await verify.ExecuteScalarAsync());

            verify.CommandText = "SELECT count(*) FROM sqlite_schema WHERE name='guild_spend_sources'";

            Assert.Equal(0L, await verify.ExecuteScalarAsync());
        }

        await using SqliteConnection reopened = await file.OpenAsync(default);

        Assert.True((await GrimoireSchemaTestInstaller.InstallAsync(reopened, 768, default)).GuildCanonical.IsHealthy);

        await using SqliteCommand check = reopened.CreateCommand();

        check.CommandText = "SELECT seq FROM sqlite_sequence WHERE name='guild_operator_sequences'";

        Assert.Equal(900L, await check.ExecuteScalarAsync());
    }

    [Fact]
    public async Task V14_effect_and_bill_gain_membership_without_inventing_initial_snapshot()
    {
        await using GuildQuestLifecycleTests.Fixture fixture = await GuildQuestLifecycleTests.Fixture.CreateAsync(
            chains: GuildCanonicalSchemaVersionFourteenFixture.ChainSet());

        await fixture.ExecuteAsync("""
            INSERT INTO guild_budget_accounts(AccountId,OwnerKindCode,OwnerId,RootAccountId,Revision)
            VALUES('DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD',0,'11111111-1111-1111-1111-111111111111',
                'DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD',1);
            INSERT INTO guild_quest_budget(QuestId,AccountId)
            VALUES('11111111-1111-1111-1111-111111111111','DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD');
            INSERT INTO guild_dispatches(DispatchId,QuestId,InvocationDigest,IsContinuation,AccountingAssociationId,EvidenceCode,
                Revision,CreatedAtUtc,UpdatedAtUtc)
            VALUES('EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE','11111111-1111-1111-1111-111111111111',zeroblob(32),0,
                'AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA',2,1,'2026-11-06T00:00:00.0000000Z','2026-11-06T00:00:00.0000000Z');
            INSERT INTO guild_budget_effects(EffectId,AccountId,QuestId,DispatchId,AccountingAssociationId,KindCode,SourceId,
                KnownUsd,KnownTokens,UnknownUsd,UnknownTokens,StateCode,Revision)
            VALUES('77777777-7777-7777-7777-777777777777','DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD',
                '11111111-1111-1111-1111-111111111111','EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE',
                'AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA',0,'77777777-7777-7777-7777-777777777777','0.125',10,0,0,2,2);
            INSERT INTO guild_budget_evidence(ReceiptId,EffectId,Digest,KnownUsd,KnownTokens,UnknownUsd,UnknownTokens,
                Complete,NoDispatchProven,AppliedRevision)
            VALUES('77777777-7777-7777-7777-777777777777','77777777-7777-7777-7777-777777777777',zeroblob(32),
                '0.125',10,0,0,1,0,2);
            INSERT INTO InferenceRuns(Id,RequestId,Surface,Purpose,StartedAt,Status)
            VALUES('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','legacy','guild','quest','2026-11-06T00:00:00.0000000Z',0);
            INSERT INTO BillableOperations(Id,RunId,OperationType,Provider,Model,Purpose,StartedAt,CompletedAt,
                InputTokens,OutputTokens,ReasoningTokens,CachedTokens,PricingSnapshotJson,Status,ProviderRequestId,ActualCostUsd)
            VALUES('77777777777777777777777777777777','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',0,'provider','model','guild',
                '2026-11-06T00:00:00.0000000Z','2026-11-06T00:00:01.0000000Z',7,3,1,2,'{}',0,'legacy-id','0.125');
            """);

        Assert.Equal(0L, await fixture.ScalarAsync("SELECT count(*) FROM pragma_foreign_key_check"));

        var installed = await GrimoireSchemaTestInstaller.InstallAsync(fixture.Connection, 768, default);

        Assert.True(installed.GuildCanonical.IsHealthy, installed.GuildCanonical.ToString());

        Assert.Equal(1L, await fixture.ScalarAsync("SELECT count(*) FROM guild_operator_sequences WHERE KindCode=4 AND ObjectId='77777777-7777-7777-7777-777777777777' AND ScopeId='11111111-1111-1111-1111-111111111111'"));

        Assert.Equal(1L, await fixture.ScalarAsync("SELECT count(*) FROM BillableOperations"));

        Assert.Equal(1L, await fixture.ScalarAsync("SELECT count(*) FROM guild_budget_evidence"));

        Assert.Equal(0L, await fixture.ScalarAsync("SELECT count(*) FROM guild_spend_sources"));
    }
}
