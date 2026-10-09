using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class CampaignRollupSchemaEvolutionTests
{
    private const string Campaign = "AAAAAAAA-1111-4111-8111-AAAAAAAAAAAA";

    private const string OtherCampaign = "BBBBBBBB-2222-4222-8222-BBBBBBBBBBBB";

    private const string Session = "CCCCCCCC-3333-4333-8333-CCCCCCCCCCCC";

    private const string Rollup = "DDDDDDDD-4444-4444-8444-DDDDDDDDDDDD";

    private const string Contribution = "EEEEEEEE-5555-4555-8555-EEEEEEEEEEEE";

    [Fact]
    public void The_core_catalog_installs_revisioned_campaign_sources_and_claim_checkpoints()
    {
        string[] required =
        [
            "campaign_rollup_artifacts",
            "campaign_rollup_state",
            "campaign_contribution_artifacts",
            "campaign_contribution_state",
            "campaign_rollup_sources",
            "campaign_fork_frontiers",
            "campaign_maintenance_checkpoints",
        ];

        string[] missing = required
            .Where(name => !GrimoireSchemaCatalog.CoreObjects.Any(definition => definition.Name == name))
            .ToArray();

        Assert.True(
            missing.Length == 0,
            $"Missing Campaign schema objects: {string.Join(", ", missing)}. "
                + $"Published source fingerprint: {GrimoireSchemaCatalog.CoreSchemaFingerprint}.");
    }

    [Fact]
    public void Version_fifteen_is_recognized_by_its_original_published_fingerprint()
    {
        Assert.Equal(CoreSchemaVersionFifteenFixture.PublishedFingerprint, CoreSchemaVersionFifteenFixture.Fingerprint);

        Assert.Equal(
            CoreSchemaVersionFifteenFixture.PublishedFingerprint,
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core)
                .SourceDefinitionFingerprintFor(15));
    }

    [Fact]
    public async Task Fresh_and_resumed_version_sixteen_installations_have_the_same_catalog()
    {
        await using SqliteConnection evolved = await OpenAsync();

        await using SqliteConnection fresh = await OpenAsync();

        GrimoireSchemaInstallResult original = await GrimoireSchemaTestInstaller.InstallAsync(
            evolved, CoreSchemaVersionFifteenFixture.ChainSet(), 1536, CancellationToken.None);

        Assert.Equal(15, original.Core.SchemaVersion);

        await SeedOwnersAsync(evolved);

        await InstallHeadAsync(evolved);

        await InstallHeadAsync(fresh);

        Assert.Equal(await DefinitionsAsync(fresh), await DefinitionsAsync(evolved));

        Assert.Equal(1, await ScalarAsync(evolved, $"SELECT COUNT(*) FROM \"Sessions\" WHERE Id = '{Session}';"));

        Assert.Equal(0, await ScalarAsync(evolved, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
    }

    [Fact]
    public async Task An_artifact_and_its_pointer_cannot_cross_campaign_ownership()
    {
        await using SqliteConnection connection = await OpenHeadAsync();

        await SeedOwnersAsync(connection);

        await InsertRollupAsync(connection);

        SqliteException error = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection,
            $"INSERT INTO campaign_rollup_state (CampaignId, CurrentArtifactId, Revision, UpdatedAtUtc) VALUES ('{OtherCampaign}', '{Rollup}', 1, '2026-10-08T00:00:00.0000000Z');"));

        Assert.Equal(19, error.SqliteErrorCode);

        Assert.Equal(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM campaign_rollup_state;"));
    }

    [Fact]
    public async Task A_contribution_requires_the_sessions_canonical_campaign_binding()
    {
        await using SqliteConnection connection = await OpenHeadAsync();

        await SeedOwnersAsync(connection);

        SqliteException error = await Assert.ThrowsAsync<SqliteException>(
            () => InsertContributionAsync(connection, campaign: OtherCampaign));

        Assert.Equal(19, error.SqliteErrorCode);

        Assert.Equal(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM campaign_contribution_artifacts;"));
    }

    [Fact]
    public async Task Summary_bounds_measure_utf8_bytes_and_reject_oversized_multibyte_content()
    {
        await using SqliteConnection connection = await OpenHeadAsync();

        await SeedOwnersAsync(connection);

        await InsertRollupAsync(connection, content: new string('é', 4096));

        SqliteException error = await Assert.ThrowsAsync<SqliteException>(
            () => InsertContributionAsync(connection, content: new string('é', 4097)));

        Assert.Equal(19, error.SqliteErrorCode);
    }

    [Fact]
    public async Task Published_artifact_text_and_its_owner_are_immutable()
    {
        await using SqliteConnection connection = await OpenHeadAsync();

        await SeedOwnersAsync(connection);

        await InsertRollupAsync(connection);

        SqliteException error = await Assert.ThrowsAsync<SqliteException>(
            () => ExecuteAsync(connection, "UPDATE campaign_rollup_artifacts SET Content = 'substituted';"));

        Assert.Equal(19, error.SqliteErrorCode);

        Assert.Equal(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM campaign_rollup_artifacts WHERE Content = 'decisions';"));
    }

    [Fact]
    public async Task Replacing_a_contributor_creates_refold_debt_but_timestamp_only_updates_do_not()
    {
        await using SqliteConnection connection = await OpenHeadAsync();

        await SeedPublishedCampaignAsync(connection);

        await ExecuteAsync(connection, "UPDATE campaign_contribution_state SET UpdatedAtUtc = '2026-10-09T00:00:00.0000000Z';");

        Assert.Equal(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM campaign_rollup_state WHERE CurrentArtifactId IS NOT NULL AND RefoldRequired = 0;"));

        long generation = await ScalarAsync(connection, "SELECT SourceGeneration FROM campaign_rollup_state;");

        await ExecuteAsync(connection, "UPDATE campaign_contribution_state SET CurrentArtifactId = NULL, RefoldRequired = 1, SourceGeneration = SourceGeneration + 1;");

        Assert.Equal(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM campaign_rollup_state WHERE CurrentArtifactId IS NULL AND RefoldRequired = 1;"));

        Assert.Equal(generation + 1, await ScalarAsync(connection, "SELECT SourceGeneration FROM campaign_rollup_state;"));
    }

    [Fact]
    public async Task Publishing_a_new_unconsumed_contributor_preserves_the_current_incremental_rollup()
    {
        await using SqliteConnection connection = await OpenHeadAsync();

        await SeedPublishedCampaignAsync(connection);

        const string newcomer = "11111111-8888-4888-8888-111111111111";

        const string artifact = "22222222-9999-4999-8999-222222222222";

        await ExecuteAsync(connection, $"INSERT INTO \"Sessions\" (Id, CampaignId, Status, CreatedAt, UpdatedAt) VALUES ('{newcomer}', '{Campaign}', 'active', '2026-10-08T00:00:00.0000000Z', '2026-10-08T00:00:00.0000000Z');");

        using (CovenantSqliteAuthorizationScope scope = CovenantSqliteConnectionInitializer.Instance.Authorize(connection, CovenantSqliteAuthorizationKind.SessionBindingWrite))
        {
            await ExecuteAsync(connection, $"INSERT INTO session_campaign_bindings (SessionId, BindingKindCode, CampaignId, BoundAtUtc) VALUES ('{newcomer}', 2, '{Campaign}', '2026-10-08T00:00:00.0000000Z');");
        }

        await ExecuteAsync(connection, $"""
            INSERT INTO campaign_contribution_state (SessionId, CampaignId, UpdatedAtUtc)
            VALUES ('{newcomer}', '{Campaign}', '2026-10-08T00:00:00.0000000Z');
            INSERT INTO campaign_contribution_artifacts (ArtifactId, CampaignId, SessionId, Revision, Content, ContentDigest, SensitivityCode, SensitivityDigest, SourceManifestDigest, SourceGeneration, SummarizedThroughSequence, CreatedAtUtc)
            VALUES ('{artifact}', '{Campaign}', '{newcomer}', 1, 'novel decision', zeroblob(32), 0, zeroblob(32), zeroblob(32), 0, 1, '2026-10-08T00:00:00.0000000Z');
            """);

        long generation = await ScalarAsync(connection, "SELECT SourceGeneration FROM campaign_rollup_state;");

        await ExecuteAsync(connection, $"UPDATE campaign_contribution_state SET CurrentArtifactId = '{artifact}', Revision = 1, SummarizedThroughSequence = 1 WHERE SessionId = '{newcomer}';");

        Assert.Equal(1, await ScalarAsync(connection, $"SELECT COUNT(*) FROM campaign_rollup_state WHERE CurrentArtifactId = '{Rollup}' AND RefoldRequired = 0;"));

        Assert.Equal(generation + 1, await ScalarAsync(connection, "SELECT SourceGeneration FROM campaign_rollup_state;"));
    }

    [Theory]
    [InlineData("UPDATE \"Entries\" SET Content = 'changed' WHERE Sequence = 1;")]
    [InlineData("DELETE FROM \"Entries\" WHERE Sequence = 1;")]
    public async Task Editing_or_deleting_consumed_history_invalidates_both_summary_layers(string mutation)
    {
        await using SqliteConnection connection = await OpenHeadAsync();

        await SeedPublishedCampaignAsync(connection);

        await ExecuteAsync(connection, mutation);

        Assert.Equal(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM campaign_contribution_state WHERE CurrentArtifactId IS NULL AND RefoldRequired = 1;"));

        Assert.Equal(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM campaign_rollup_state WHERE CurrentArtifactId IS NULL AND RefoldRequired = 1;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_foreign_Entry_label_identity_invalidates_its_consumed_Campaign_source(bool deleting)
    {
        await using SqliteConnection connection = await OpenHeadAsync();

        await SeedPublishedCampaignAsync(connection);

        if (deleting)
        {
            await ExecuteAsync(connection, "DROP TRIGGER artifact_sensitivity_campaign_contribution_insert;");
        }

        await ExecuteAsync(connection, $"""
            INSERT INTO artifact_sensitivity(LabelId,ArtifactKindCode,ArtifactId,SensitivityCode,ProvenanceModeCode,
                ExactGenerationIds,SessionId,CampaignId,ArtifactRevision,ArtifactContentDigest,SensitivityDigest,ArtifactLabelDigest,CreatedAtUtc)
            VALUES('AAAAAAAA-9999-4999-8999-AAAAAAAAAAAA',1,'99999999777747778777999999999999',1,1,
                x'00000000000000000000000000000001','{Session}','{Campaign}',0,zeroblob(32),zeroblob(32),zeroblob(32),'2026-10-08T00:00:00.0000000Z');
            """);

        if (deleting)
        {
            using CovenantSqliteAuthorizationScope scope = CovenantSqliteConnectionInitializer.Instance
                .Authorize(connection, CovenantSqliteAuthorizationKind.ArtifactReplacement);

            await ExecuteAsync(connection, "DELETE FROM artifact_sensitivity WHERE LabelId='AAAAAAAA-9999-4999-8999-AAAAAAAAAAAA';");
        }

        Assert.Equal(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM campaign_contribution_state WHERE CurrentArtifactId IS NULL AND RefoldRequired = 1;"));

        Assert.Equal(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM campaign_rollup_state WHERE CurrentArtifactId IS NULL AND RefoldRequired = 1;"));
    }

    [Fact]
    public async Task Appending_native_tail_advances_snapshot_generation_without_discarding_committed_context()
    {
        await using SqliteConnection connection = await OpenHeadAsync();

        await SeedPublishedCampaignAsync(connection);

        long generation = await ScalarAsync(connection, "SELECT SourceGeneration FROM campaign_contribution_state;");

        await ExecuteAsync(connection, $"INSERT INTO \"Entries\" (Id, SessionId, Role, Content, ModelUsed, CreatedAt, Sequence, IsPinned) VALUES ('FFFFFFFF-6666-4666-8666-FFFFFFFFFFFF', '{Session}', 0, 'new tail', '', '2026-10-08T00:00:02.0000000Z', 2, 0);");

        Assert.Equal(generation + 1, await ScalarAsync(connection, "SELECT SourceGeneration FROM campaign_contribution_state;"));

        Assert.Equal(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM campaign_contribution_state WHERE CurrentArtifactId IS NOT NULL AND RefoldRequired = 0;"));

        Assert.Equal(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM campaign_rollup_state WHERE CurrentArtifactId IS NOT NULL AND RefoldRequired = 0;"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Legacy_forks_require_proven_copied_identity_or_exclude_the_whole_existing_transcript(bool proven)
    {
        await using SqliteConnection connection = await OpenAsync();

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection,
            CoreSchemaVersionFifteenFixture.ChainSet(), 1536, CancellationToken.None);

        await SeedOwnersAsync(connection);

        Guid child = Guid.Parse("11111111-8888-4888-8888-111111111111");

        Guid copied = ForkEntryId(child, Guid.Parse("99999999-7777-4777-8777-999999999999"));

        await ExecuteAsync(connection, $"""
            INSERT INTO "Sessions" (Id, ForkedFromSessionId, Status, CreatedAt, UpdatedAt)
            VALUES ('{child.ToString("D").ToUpperInvariant()}', '{(proven ? Session : OtherCampaign)}', 'active', '2026-10-08T00:00:00.0000000Z', '2026-10-08T00:00:00.0000000Z');
            INSERT INTO "Entries" (Id, SessionId, Role, Content, ModelUsed, CreatedAt, Sequence, IsPinned)
            VALUES ('{copied.ToString("D").ToUpperInvariant()}', '{child.ToString("D").ToUpperInvariant()}', 0, 'copy', '', '2026-10-08T00:00:01.0000000Z', 1, 0);
            """);

        await InstallHeadAsync(connection);

        Assert.Equal(1, await ScalarAsync(connection, "SELECT InheritedThroughSequence FROM campaign_fork_frontiers;"));

        Assert.Equal(proven ? 1 : 2, await ScalarAsync(connection, "SELECT ProofKindCode FROM campaign_fork_frontiers;"));
    }

    [Theory]
    [InlineData("2026-10-08T00:00:00.1234567-04:00", "2026-10-08T04:00:00.1234567Z")]
    [InlineData("2026-10-08T04:00:00.1234567", "2026-10-08T04:00:00.1234567Z")]
    public async Task Legacy_fork_frontiers_normalize_historical_source_instants_at_publication(
        string createdAt,
        string expectedUtc)
    {
        await using SqliteConnection connection = await OpenAsync();

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection,
            CoreSchemaVersionFifteenFixture.ChainSet(), 1536, CancellationToken.None);

        await SeedOwnersAsync(connection);

        Guid child = Guid.Parse("11111111-8888-4888-8888-111111111111");

        await ExecuteAsync(connection, $"""
            INSERT INTO "Sessions" (Id, ForkedFromSessionId, Status, CreatedAt, UpdatedAt)
            VALUES ('{child.ToString("D").ToUpperInvariant()}', '{Session}', 'active', '{createdAt}', '2026-10-08T00:00:00.0000000Z');
            """);

        await InstallHeadAsync(connection);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT CreatedAtUtc FROM campaign_fork_frontiers;";

        Assert.Equal(expectedUtc, Assert.IsType<string>(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task A_legacy_fork_sweep_pages_its_entry_proof_and_publishes_no_partial_frontier()
    {
        await using SqliteConnection connection = await OpenAsync();

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection,
            CoreSchemaVersionFifteenFixture.ChainSet(), 1536, CancellationToken.None);

        await SeedOwnersAsync(connection);

        Guid child = Guid.Parse("11111111-8888-4888-8888-111111111111");

        await ExecuteAsync(connection, $"INSERT INTO \"Sessions\" (Id, ForkedFromSessionId, Status, CreatedAt, UpdatedAt) VALUES ('{child.ToString("D").ToUpperInvariant()}', '{Session}', 'active', '2026-10-08T00:00:00.0000000Z', '2026-10-08T00:00:00.0000000Z');");

        for (int sequence = 2; sequence <= 205; sequence++)
        {
            Guid source = Guid.NewGuid();

            await ExecuteAsync(connection, $"""
                INSERT INTO "Entries" (Id, SessionId, Role, Content, ModelUsed, CreatedAt, Sequence, IsPinned)
                VALUES ('{source.ToString("D").ToUpperInvariant()}', '{Session}', 0, 'source', '', '2026-10-08T00:00:01.0000000Z', {sequence}, 0),
                       ('{ForkEntryId(child, source).ToString("D").ToUpperInvariant()}', '{child.ToString("D").ToUpperInvariant()}', 0, 'copy', '', '2026-10-08T00:00:01.0000000Z', {sequence}, 0);
                """);
        }

        GrimoireSchemaInstallResult installed = await GrimoireSchemaTestInstaller.InstallAsync(connection, 1536, CancellationToken.None);

        Assert.Equal(GrimoireSchemaTierHealth.TransitionIncomplete, installed.Core.Health);

        GrimoireSchemaVersionStep step = Assert.Single(GrimoireSchemaVersionChains.Default
            .ForTier(GrimoireSchemaTransactionTier.Core).Steps, candidate => candidate.ToVersion == 16);

        IGrimoireSchemaBackfill sweep = Assert.IsAssignableFrom<IGrimoireSchemaBackfill>(step.Backfill);

        GrimoireSchemaBackfillBatch first;

        await using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            first = await sweep.AdvanceBatchAsync(connection, transaction, null, CancellationToken.None);

            Assert.InRange(first.RowsProcessed, 1, sweep.MaxRowsPerBatch);

            Assert.False(first.IsComplete);

            Assert.NotNull(first.NextCursor);

            Assert.Equal(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM campaign_fork_frontiers;", transaction));

            await transaction.CommitAsync();
        }

        await using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            GrimoireSchemaBackfillBatch resumed = await sweep.AdvanceBatchAsync(connection, transaction, first.NextCursor, CancellationToken.None);

            Assert.InRange(resumed.RowsProcessed, 1, sweep.MaxRowsPerBatch);

            Assert.Equal(205, await ScalarAsync(connection, "SELECT InheritedThroughSequence FROM campaign_fork_frontiers;", transaction));

            await transaction.CommitAsync();
        }
    }

    private static Guid ForkEntryId(Guid child, Guid source)
    {
        Span<byte> material = stackalloc byte[33];

        _ = child.TryWriteBytes(material[..16]);

        _ = source.TryWriteBytes(material[16..32]);

        material[32] = 1;

        Span<byte> digest = stackalloc byte[32];

        _ = System.Security.Cryptography.SHA256.HashData(material, digest);

        return new Guid(digest[..16]);
    }

    internal static async Task InstallHeadAsync(SqliteConnection connection)
    {
        GrimoireSchemaVersionChainSet chains = CoreSchemaVersionSixteenFixture.ChainSet();

        GrimoireSchemaInstaller installer = GrimoireSchemaTestInstaller.Create(chains);

        GrimoireSchemaInitializationContext context = GrimoireSchemaTestInstaller.CreateContext();

        GrimoireSchemaInstallResult installed = await installer.InstallAsync(connection, 1536, context, CancellationToken.None);

        if (installed.Core.Health is GrimoireSchemaTierHealth.TransitionIncomplete)
        {
            GrimoireSchemaBackfillRunner runner = new(installer, TimeProvider.System);

            for (int pass = 0; pass < 64; pass++)
            {
                GrimoireSchemaTransitionJournalRow? journal = await GrimoireSchemaTransitionJournal.ReadAsync(
                    connection, null, GrimoireSchemaTransactionTier.Core, CancellationToken.None);

                if (journal is null)
                {
                    break;
                }

                _ = await runner.AdvanceAsync(connection,
                    chains.ForTier(GrimoireSchemaTransactionTier.Core),
                    journal, context, 16, CancellationToken.None);
            }

            installed = await installer.InstallAsync(connection, 1536, context, CancellationToken.None);
        }

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, installed.Core.Health);

        Assert.Equal(16, installed.Core.SchemaVersion);
    }

    private static Task<SqliteConnection> OpenAsync() =>
        GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", CancellationToken.None);

    private static async Task<SqliteConnection> OpenHeadAsync()
    {
        SqliteConnection connection = await OpenAsync();

        await InstallHeadAsync(connection);

        Assert.True(await ScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'campaign_rollup_artifacts';") == 1, "Campaign rollup artifacts must be installed.");

        return connection;
    }

    private static async Task SeedOwnersAsync(SqliteConnection connection)
    {
        await ExecuteAsync(connection, $"""
            INSERT INTO "Campaigns" (Id, Name, NameLower, Path, Type, Settings, CreatedAt, UpdatedAt)
            VALUES ('{Campaign}', 'one', 'one', '/one', 0, char(123) || char(125), '2026-10-08T00:00:00.0000000Z', '2026-10-08T00:00:00.0000000Z'),
                   ('{OtherCampaign}', 'two', 'two', '/two', 0, char(123) || char(125), '2026-10-08T00:00:00.0000000Z', '2026-10-08T00:00:00.0000000Z');
            INSERT INTO "Sessions" (Id, CampaignId, Status, CreatedAt, UpdatedAt)
            VALUES ('{Session}', '{Campaign}', 'active', '2026-10-08T00:00:00.0000000Z', '2026-10-08T00:00:00.0000000Z');
            INSERT INTO "Entries" (Id, SessionId, Role, Content, ModelUsed, CreatedAt, Sequence, IsPinned)
            VALUES ('99999999-7777-4777-8777-999999999999', '{Session}', 0, 'original', '', '2026-10-08T00:00:01.0000000Z', 1, 0);
            """);

        using CovenantSqliteAuthorizationScope scope = CovenantSqliteConnectionInitializer.Instance
            .Authorize(connection, CovenantSqliteAuthorizationKind.SessionBindingWrite);

        await ExecuteAsync(connection, $"INSERT INTO session_campaign_bindings (SessionId, BindingKindCode, CampaignId, BoundAtUtc) VALUES ('{Session}', 2, '{Campaign}', '2026-10-08T00:00:00.0000000Z');");
    }

    private static async Task SeedPublishedCampaignAsync(SqliteConnection connection)
    {
        await SeedOwnersAsync(connection);

        await InsertRollupAsync(connection, sourceCount: 1);

        await InsertContributionAsync(connection);

        await ExecuteAsync(connection, $"INSERT INTO campaign_rollup_sources (RollupArtifactId, SessionId, CampaignId, RollupRevision, ContributionArtifactId, ContributionRevision, ContentDigest, SensitivityDigest, SummarizedThroughSequence) VALUES ('{Rollup}', '{Session}', '{Campaign}', 1, '{Contribution}', 1, zeroblob(32), zeroblob(32), 1);");

        await ExecuteAsync(connection, $"""
            INSERT INTO campaign_rollup_state (CampaignId, CurrentArtifactId, Revision, UpdatedAtUtc)
            VALUES ('{Campaign}', '{Rollup}', 1, '2026-10-08T00:00:00.0000000Z');
            INSERT INTO campaign_contribution_state (SessionId, CampaignId, CurrentArtifactId, Revision, SummarizedThroughSequence, UpdatedAtUtc)
            VALUES ('{Session}', '{Campaign}', '{Contribution}', 1, 1, '2026-10-08T00:00:00.0000000Z');
            """);
    }

    private static Task InsertRollupAsync(SqliteConnection connection, string content = "decisions", int sourceCount = 0) =>
        ExecuteAsync(connection, $"INSERT INTO campaign_rollup_artifacts (ArtifactId, CampaignId, Revision, Content, ContentDigest, SensitivityCode, SensitivityDigest, SourceManifestDigest, SourceGeneration, SourceCount, CreatedAtUtc) VALUES ('{Rollup}', '{Campaign}', 1, $content, zeroblob(32), 0, zeroblob(32), zeroblob(32), 0, {sourceCount}, '2026-10-08T00:00:00.0000000Z');", content);

    private static Task InsertContributionAsync(SqliteConnection connection, string campaign = Campaign, string content = "decisions") =>
        ExecuteAsync(connection, $"INSERT INTO campaign_contribution_artifacts (ArtifactId, CampaignId, SessionId, Revision, Content, ContentDigest, SensitivityCode, SensitivityDigest, SourceManifestDigest, SourceGeneration, SummarizedThroughSequence, CreatedAtUtc) VALUES ('{Contribution}', '{campaign}', '{Session}', 1, $content, zeroblob(32), 0, zeroblob(32), zeroblob(32), 0, 1, '2026-10-08T00:00:00.0000000Z');", content);

    private static async Task<string[]> DefinitionsAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT name, sql FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' ORDER BY name;";

        List<string> definitions = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            definitions.Add(reader.GetString(0) + ":" + GrimoireSqlNormalizer.Normalize(reader.GetString(1)));
        }

        return [.. definitions];
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, string? content = null)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        if (content is not null)
        {
            _ = command.Parameters.AddWithValue("$content", content);
        }

        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        command.Transaction = transaction;

        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
