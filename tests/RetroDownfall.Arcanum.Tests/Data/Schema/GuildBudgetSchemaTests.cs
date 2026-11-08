using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class GuildBudgetSchemaTests
{
    private const string Quest = "11111111-1111-1111-1111-111111111111";

    private const string Account = "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA";

    private const string Child = "BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB";

    private const string Effect = "CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC";

    private const string Receipt = "DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD";

    [Fact]
    public async Task V7_fresh_and_published_v6_upgrade_have_identical_catalogs_without_changing_other_families()
    {
        Assert.Equal(GuildCanonicalSchemaVersionSixFixture.PublishedFingerprint, GuildCanonicalSchemaVersionSixFixture.Fingerprint);

        await using SqliteConnection upgraded = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(upgraded, GuildCanonicalSchemaVersionSixFixture.ChainSet(), 768, default);

        await ExecuteAsync(upgraded, $"""
            INSERT INTO guild_content(ContentId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,Body,SensitivityCode,ProvenanceModeCode,CreatedAtUtc)
            VALUES('{Quest}',0,'{Account}',1,zeroblob(32),'application/json',X'7B7D',0,0,'2026-11-06T00:00:00.0000000Z');
            """);

        object? otherFamilies = await ScalarAsync(upgraded, "SELECT group_concat(SourceDefinitionFingerprint || InstalledCatalogFingerprint) FROM grimoire_feature_schemas WHERE FamilyCode<>2");

        object? epoch = await ScalarAsync(upgraded, "SELECT Epoch FROM guild_signet_authority_state");

        GrimoireSchemaInstallResult result = await GrimoireSchemaTestInstaller.InstallAsync(upgraded, 768, default);

        Assert.True(result.GuildCanonical.IsHealthy, result.GuildCanonical.ToString());

        Assert.Equal(7, result.GuildCanonical.SchemaVersion);

        Assert.Equal(otherFamilies, await ScalarAsync(upgraded, "SELECT group_concat(SourceDefinitionFingerprint || InstalledCatalogFingerprint) FROM grimoire_feature_schemas WHERE FamilyCode<>2"));

        Assert.Equal(epoch, await ScalarAsync(upgraded, "SELECT Epoch FROM guild_signet_authority_state"));

        Assert.Equal(Quest, await ScalarAsync(upgraded, "SELECT ContentId FROM guild_content"));

        await using SqliteConnection fresh = await OpenAsync();

        const string identity = "SELECT SourceDefinitionFingerprint || InstalledCatalogFingerprint FROM grimoire_feature_schemas WHERE FamilyCode=2";

        Assert.Equal(await ScalarAsync(fresh, identity), await ScalarAsync(upgraded, identity));

        Assert.True((await GrimoireSchemaTestInstaller.InstallAsync(upgraded, 768, default)).GuildCanonical.IsHealthy);
    }

    [Fact]
    public void V7_transition_leaves_the_frozen_v6_fingerprint_and_installs_exact_canonical_objects()
    {
        GrimoireSchemaVersionChain chain = GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.GuildCanonical);

        Assert.True(chain.TryGetStep(6, out GrimoireSchemaVersionStep step));

        Assert.Equal(GuildCanonicalSchemaVersionSixFixture.PublishedFingerprint, chain.SourceDefinitionFingerprintFor(6));

        string[] names =
        [
            "guild_budget_accounts", "guild_quest_budget", "guild_budget_effects", "guild_budget_evidence",
            "idx_guild_budget_accounts_parent", "idx_guild_budget_accounts_root", "idx_guild_budget_effects_account", "idx_guild_budget_effects_dispatch",
            "ux_guild_budget_effects_reservation",
            "idx_guild_budget_effects_accounting",
            "guild_budget_accounts_guard_update", "guild_quest_budget_guard_update", "guild_budget_effects_guard_update", "guild_budget_evidence_guard_update",
        ];

        Assert.Equal(names, step.Statements.Select(statement => statement.Name));

        foreach (GrimoireSchemaTransitionStatement statement in step.Statements)
        {
            GrimoireSchemaObject canonical = Assert.Single(GrimoireSchemaCatalog.GuildCanonicalObjects, definition => definition.Name == statement.Name);

            Assert.Equal(GrimoireSqlNormalizer.Normalize(canonical.Sql), GrimoireSqlNormalizer.Normalize(statement.Sql));
        }
    }

    [Fact]
    public async Task Allocation_binding_can_precede_Quest_and_rolls_back_with_its_transaction()
    {
        await using SqliteConnection connection = await OpenAsync();

        await ExecuteAsync(connection, "BEGIN IMMEDIATE");

        await SeedAccountAsync(connection);

        await ExecuteAsync(connection, $"INSERT INTO guild_quest_budget(QuestId,AccountId) VALUES('{Quest}','{Account}')");

        await GuildQuestSchemaTests.SeedQuestAsync(connection);

        await ExecuteAsync(connection, "ROLLBACK");

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM guild_budget_accounts"));

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM guild_quest_budget"));

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM guild_quests"));

        await ExecuteAsync(connection, "BEGIN IMMEDIATE");

        await SeedAccountAsync(connection);

        await ExecuteAsync(connection, $"INSERT INTO guild_quest_budget(QuestId,AccountId) VALUES('{Quest}','{Account}')");

        await GuildQuestSchemaTests.SeedQuestAsync(connection);

        await ExecuteAsync(connection, "COMMIT");

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM guild_quest_budget"));
    }

    [Theory]
    [InlineData("AccountId='EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE'")]
    [InlineData("OwnerKindCode=1")]
    [InlineData("OwnerId='EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE'")]
    [InlineData("ParentAccountId='BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB'")]
    [InlineData("RootAccountId='BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB'")]
    [InlineData("UsdLimit='2'")]
    [InlineData("TokenLimit=2")]
    [InlineData("Revision=Revision+2")]
    public async Task Account_identity_parent_limits_and_revision_cannot_be_replaced(string mutation)
    {
        await using SqliteConnection connection = await OpenAsync();

        await SeedAccountAsync(connection);

        string revision = mutation.StartsWith("Revision=", StringComparison.Ordinal) ? string.Empty : ",Revision=Revision+1";

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE guild_budget_accounts SET " + mutation + revision));

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT Revision FROM guild_budget_accounts"));
    }

    [Fact]
    public async Task Closure_and_settlement_receipt_are_irreversible_and_child_parent_is_retained()
    {
        await using SqliteConnection connection = await OpenAsync();

        await SeedAccountAsync(connection);

        await ExecuteAsync(connection, $"INSERT INTO guild_budget_accounts(AccountId,OwnerKindCode,OwnerId,RootAccountId,ParentAccountId,Revision) VALUES('{Child}',1,'{Child}','{Account}','{Account}',1)");

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, $"DELETE FROM guild_budget_accounts WHERE AccountId='{Account}'"));

        await ExecuteAsync(connection, $"UPDATE guild_budget_accounts SET Closed=1,SettlementReceiptId='{Receipt}',Revision=Revision+1 WHERE AccountId='{Child}'");

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, $"UPDATE guild_budget_accounts SET Closed=0,Revision=Revision+1 WHERE AccountId='{Child}'"));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, $"UPDATE guild_budget_accounts SET SettlementReceiptId=NULL,Revision=Revision+1 WHERE AccountId='{Child}'"));
    }

    [Theory]
    [InlineData("AccountId", "'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'")]
    [InlineData("OwnerKindCode", "2")]
    [InlineData("TokenLimit", "-1")]
    [InlineData("TokenLimit", "1.5")]
    [InlineData("Revision", "0")]
    [InlineData("Closed", "2")]
    [InlineData("ParentAccountId", "'AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA'")]
    public async Task Account_rows_reject_malformed_identity_enum_integer_or_root_shape(string column, string value)
    {
        await using SqliteConnection connection = await OpenAsync();

        string[] columns = ["AccountId", "OwnerKindCode", "OwnerId", "RootAccountId", "ParentAccountId", "UsdLimit", "TokenLimit", "Closed", "Revision"];

        string[] values = [$"'{Account}'", "0", $"'{Quest}'", $"'{Account}'", "NULL", "'0.1234567890123456789012345678'", "1", "0", "1"];

        values[Array.IndexOf(columns, column)] = value;

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, $"INSERT INTO guild_budget_accounts({string.Join(',', columns)}) VALUES({string.Join(',', values)})"));
    }

    [Fact]
    public async Task Physical_source_and_evidence_revision_are_unique_and_owner_cleanup_cannot_remove_their_parents()
    {
        await using SqliteConnection connection = await OpenAsync();

        await SeedEffectAsync(connection);

        await SeedEvidenceAsync(connection);

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, $"INSERT INTO guild_budget_effects SELECT 'EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE',AccountId,QuestId,DispatchId,AccountingAssociationId,KindCode,SourceId,ReservationId,UsdBound,TokenBound,KnownUsd,KnownTokens,UnknownUsd,UnknownTokens,StateCode,Revision FROM guild_budget_effects"));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "INSERT INTO guild_budget_evidence SELECT 'EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE',EffectId,Digest,KnownUsd,KnownTokens,UnknownUsd,UnknownTokens,Complete,NoDispatchProven,AppliedRevision FROM guild_budget_evidence"));

        foreach (string sql in new[] { "DELETE FROM guild_quests", "DELETE FROM guild_dispatches", "DELETE FROM guild_budget_accounts", "DELETE FROM guild_budget_effects", "UPDATE guild_budget_evidence SET KnownTokens=1", "UPDATE guild_quest_budget SET AccountId=AccountId" })
        {
            await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, sql));
        }

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM guild_budget_evidence"));
    }

    [Theory]
    [InlineData("SourceId='EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE'")]
    [InlineData("AccountId='EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE'")]
    [InlineData("QuestId='EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE'")]
    [InlineData("DispatchId='EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE'")]
    [InlineData("AccountingAssociationId='EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE'")]
    [InlineData("ReservationId='EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE'")]
    [InlineData("KindCode=1")]
    [InlineData("UsdBound='2'")]
    [InlineData("TokenBound=2")]
    [InlineData("StateCode=0")]
    [InlineData("StateCode=3")]
    public async Task Exposed_effect_bindings_bounds_and_terminal_state_cannot_regress(string mutation)
    {
        await using SqliteConnection connection = await OpenAsync();

        await SeedEffectAsync(connection);

        await ExecuteAsync(connection, "UPDATE guild_budget_effects SET StateCode=1,Revision=Revision+1");

        await ExecuteAsync(connection, "UPDATE guild_budget_effects SET StateCode=2,Revision=Revision+1");

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE guild_budget_effects SET " + mutation + ",Revision=Revision+1"));

        await ExecuteAsync(connection, "UPDATE guild_budget_effects SET KnownUsd='1.234567890123456789012345678',KnownTokens=1,Revision=Revision+1");

        Assert.Equal("1.234567890123456789012345678", await ScalarAsync(connection, "SELECT KnownUsd FROM guild_budget_effects"));
    }

    [Fact]
    public async Task Physical_accounting_obligations_seek_the_normalized_association_index()
    {
        await using SqliteConnection connection = await OpenAsync();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "EXPLAIN QUERY PLAN SELECT EffectId FROM guild_budget_effects WHERE lower(replace(AccountingAssociationId,'-',''))='eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee' AND KindCode=0";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());

        Assert.Contains("idx_guild_budget_effects_accounting", reader.GetString(3), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_real_daily_reservation_cannot_fund_two_physical_effects()
    {
        await using SqliteConnection connection = await OpenAsync();

        await SeedEffectAsync(connection);

        await ExecuteAsync(connection, $"""
            INSERT INTO guild_budget_effects
            SELECT 'EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE',AccountId,QuestId,DispatchId,AccountingAssociationId,KindCode,
                'EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE','{Receipt}',UsdBound,TokenBound,KnownUsd,KnownTokens,UnknownUsd,UnknownTokens,StateCode,Revision
            FROM guild_budget_effects WHERE EffectId='{Effect}';
            """);

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, $"""
            INSERT INTO guild_budget_effects
            SELECT 'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF',AccountId,QuestId,DispatchId,AccountingAssociationId,KindCode,
                'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF','{Receipt}',UsdBound,TokenBound,KnownUsd,KnownTokens,UnknownUsd,UnknownTokens,StateCode,Revision
            FROM guild_budget_effects WHERE EffectId='{Effect}';
            """));
    }

    [Fact]
    public async Task Reservation_link_is_nullable_and_does_not_reference_synthetic_Core_reservations()
    {
        await using SqliteConnection connection = await OpenAsync();

        await SeedEffectAsync(connection);

        Assert.Equal(DBNull.Value, await ScalarAsync(connection, "SELECT ReservationId FROM guild_budget_effects"));

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM pragma_foreign_key_list('guild_budget_effects') WHERE \"from\"='ReservationId'"));

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM pragma_foreign_key_check"));
    }

    private static async Task<SqliteConnection> OpenAsync()
    {
        SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        GrimoireSchemaInstallResult installed = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, default);

        Assert.True(installed.GuildCanonical.IsHealthy, installed.GuildCanonical.ToString());

        return connection;
    }

    private static Task SeedAccountAsync(SqliteConnection connection) => ExecuteAsync(connection,
        $"INSERT INTO guild_budget_accounts(AccountId,OwnerKindCode,OwnerId,RootAccountId,UsdLimit,TokenLimit,Revision) VALUES('{Account}',0,'{Quest}','{Account}','0.1234567890123456789012345678',1,1)");

    private static async Task SeedEffectAsync(SqliteConnection connection)
    {
        await GuildQuestSchemaTests.SeedQuestAsync(connection);

        await SeedAccountAsync(connection);

        await ExecuteAsync(connection, $"""
            INSERT INTO guild_quest_budget(QuestId,AccountId) VALUES('{Quest}','{Account}');
            INSERT INTO guild_dispatches(DispatchId,QuestId,InvocationDigest,IsContinuation,AccountingAssociationId,EvidenceCode,Revision,CreatedAtUtc,UpdatedAtUtc)
            VALUES('{Child}','{Quest}',zeroblob(32),0,'{Receipt}',1,1,'2026-11-06T00:00:00.0000000Z','2026-11-06T00:00:00.0000000Z');
            INSERT INTO guild_budget_effects(EffectId,AccountId,QuestId,DispatchId,AccountingAssociationId,KindCode,SourceId,UsdBound,TokenBound,StateCode,Revision)
            VALUES('{Effect}','{Account}','{Quest}','{Child}','{Receipt}',0,'{Effect}','1',1,0,1);
            """);
    }

    private static Task SeedEvidenceAsync(SqliteConnection connection) => ExecuteAsync(connection, $"""
        INSERT INTO guild_budget_evidence(ReceiptId,EffectId,Digest,KnownUsd,KnownTokens,UnknownUsd,UnknownTokens,Complete,NoDispatchProven,AppliedRevision)
        VALUES('{Receipt}','{Effect}',zeroblob(32),'0',0,0,0,0,0,1);
        """);

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return await command.ExecuteScalarAsync();
    }
}
