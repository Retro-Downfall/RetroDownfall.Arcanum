using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;
using Xunit.Abstractions;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed class CampaignSummaryClosureCostTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("SELECT DISTINCT RollupArtifactId FROM campaign_rollup_sources WHERE SessionId = $ownerId", "idx_campaign_rollup_sources_session")]
    [InlineData("SELECT ClaimId FROM campaign_maintenance_checkpoints WHERE SessionId = $ownerId OR SourceSessionId = $ownerId", "MULTI-INDEX OR")]
    [InlineData("SELECT ClaimId FROM campaign_maintenance_checkpoints WHERE SessionId = $ownerId OR SourceSessionId = $ownerId OR (OutputArtifactKindCode = 15 AND OutputArtifactId IN (SELECT ArtifactId FROM campaign_contribution_artifacts WHERE SessionId = $ownerId)) OR (OutputArtifactKindCode = 14 AND OutputArtifactId IN (SELECT RollupArtifactId FROM campaign_rollup_sources WHERE SessionId = $ownerId))", "MULTI-INDEX OR")]
    public async Task Session_dependency_lookups_seek_indexes_including_every_checkpoint_arm(string sql, string expected)
    {
        await using CampaignRollupStoreTests.StoreFixture fixture = await CampaignRollupStoreTests.StoreFixture.CreateAsync(legacy: true);

        await using SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = "EXPLAIN QUERY PLAN " + sql;

        command.Parameters.AddWithValue("$ownerId", fixture.SessionId.ToString("D").ToUpperInvariant());

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        List<string> plans = [];

        while (await reader.ReadAsync())
        {
            plans.Add(reader.GetString(3));
        }

        Assert.Contains(plans, plan => plan.Contains(expected, StringComparison.Ordinal));

        Assert.DoesNotContain(plans, plan => plan.StartsWith("SCAN campaign_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Digest_only_closure_has_bounded_per_row_allocations_at_scale()
    {
        await using CampaignRollupStoreTests.StoreFixture fixture = await CampaignRollupStoreTests.StoreFixture.CreateAsync(legacy: true);

        await fixture.PublishCampaignAsync("published");

        const int sourceCount = 20000;

        await fixture.ExecuteAsync($"""
            WITH RECURSIVE ids(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM ids WHERE n < {sourceCount})
            INSERT INTO Sessions(Id,CampaignId,CreatedAt,UpdatedAt)
            SELECT printf('%08X-0000-4000-8000-000000000000',n),'{fixture.CampaignId.ToString().ToUpperInvariant()}','2026-10-01T00:00:00.0000000Z','2026-10-01T00:00:00.0000000Z' FROM ids;
            INSERT INTO session_campaign_bindings(SessionId,BindingKindCode,CampaignId,BoundAtUtc)
            SELECT Id,2,CampaignId,'2026-10-01T00:00:00.0000000Z' FROM Sessions WHERE Id != '{fixture.SessionId.ToString().ToUpperInvariant()}';
            INSERT INTO campaign_contribution_artifacts(ArtifactId,CampaignId,SessionId,Revision,Content,ContentDigest,SensitivityCode,SensitivityDigest,SourceManifestDigest,SourceGeneration,SummarizedThroughSequence,CreatedAtUtc)
            SELECT Id,CampaignId,Id,1,'source',zeroblob(32),0,zeroblob(32),zeroblob(32),0,0,'2026-10-01T00:00:00.0000000Z' FROM Sessions WHERE Id != '{fixture.SessionId.ToString().ToUpperInvariant()}';
            INSERT INTO campaign_rollup_sources(RollupArtifactId,SessionId,CampaignId,RollupRevision,ContributionArtifactId,ContributionRevision,ContentDigest,SensitivityDigest,SummarizedThroughSequence)
            SELECT r.ArtifactId,c.SessionId,c.CampaignId,r.Revision,c.ArtifactId,c.Revision,c.ContentDigest,c.SensitivityDigest,0
            FROM campaign_rollup_artifacts r CROSS JOIN campaign_contribution_artifacts c WHERE c.SessionId != '{fixture.SessionId.ToString().ToUpperInvariant()}';
            """);

        // Warm the reader and hash machinery before measuring allocations on this synchronous SQLite lane.
        _ = await CampaignSummaryLifecycle.ReadSessionClosureAsync(fixture.Connection, null, fixture.SessionId, CancellationToken.None);

        long before = GC.GetAllocatedBytesForCurrentThread();

        CampaignSummaryClosureSnapshot snapshot = await CampaignSummaryLifecycle.ReadSessionClosureAsync(fixture.Connection, null, fixture.SessionId, CancellationToken.None);

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // One contribution, one rollup, all 20,001 sources, and the contribution owner state.
        Assert.Equal(sourceCount + 4L, snapshot.Rows);

        output.WriteLine($"Digest-only closure: {snapshot.Rows:N0} rows, {allocated:N0} allocated bytes.");

        Assert.True(allocated < sourceCount * 1000L, $"Digest-only closure allocated {allocated:N0} bytes for {sourceCount:N0} sources; budget is 1,000 bytes/source including SQLite values.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Streamed_digest_preserves_legacy_framing_and_snapshot_equality(bool includeOwnerRows)
    {
        await using CampaignRollupStoreTests.StoreFixture fixture = await CampaignRollupStoreTests.StoreFixture.CreateAsync(legacy: true);

        await fixture.PublishCampaignAsync("published");

        // Keys are TEXT in SQLite. Exercise multi-byte UTF8 and a supplementary character at a
        // buffer boundary; the legacy prefix counts UTF16 code units, not UTF8 bytes or scalars.
        string identity = new string('a', 1023) + "🌙é:tail";

        await using (SqliteCommand seed = fixture.Connection.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO campaign_contribution_artifacts(ArtifactId,CampaignId,SessionId,Revision,Content,ContentDigest,SensitivityCode,SensitivityDigest,SourceManifestDigest,SourceGeneration,SummarizedThroughSequence,CreatedAtUtc)
                VALUES($identity,$campaign,$session,2,'source',zeroblob(32),0,zeroblob(32),zeroblob(32),0,0,'2026-10-01T00:00:00.0000000Z');
                INSERT INTO campaign_fork_frontiers VALUES($session,'parent',0,1,'2026-10-01T00:00:00.0000000Z');
                """;

            seed.Parameters.AddWithValue("$identity", identity);

            seed.Parameters.AddWithValue("$campaign", fixture.CampaignId.ToString("D").ToUpperInvariant());

            seed.Parameters.AddWithValue("$session", fixture.SessionId.ToString("D").ToUpperInvariant());

            await seed.ExecuteNonQueryAsync();
        }

        await using SqliteTransaction transaction = fixture.Connection.BeginTransaction();

        var legacy = await ReadLegacyClosureAsync(fixture.Connection, transaction, fixture.SessionId, CancellationToken.None, includeOwnerRows);

        CampaignSummaryClosureSnapshot actual = await CampaignSummaryLifecycle.ReadSessionClosureAsync(fixture.Connection, transaction, fixture.SessionId, CancellationToken.None, includeOwnerRows);

        Assert.Equal(legacy.Rows, actual.Rows);

        Assert.Equal(legacy.Authority, actual.Authority);

        Assert.Equal(actual, await CampaignSummaryLifecycle.ReadSessionClosureAsync(fixture.Connection, transaction, fixture.SessionId, CancellationToken.None, includeOwnerRows));

        await using SqliteCommand change = fixture.Connection.CreateCommand();

        change.Transaction = transaction;

        change.CommandText = "UPDATE campaign_contribution_state SET SourceGeneration = SourceGeneration + 1";

        await change.ExecuteNonQueryAsync();

        CampaignSummaryClosureSnapshot changed = await CampaignSummaryLifecycle.ReadSessionClosureAsync(fixture.Connection, transaction, fixture.SessionId, CancellationToken.None, includeOwnerRows);

        Assert.Equal(includeOwnerRows, changed != actual);
    }

    [Fact]
    public async Task Missing_schema_and_installed_empty_schema_keep_distinct_authority()
    {
        await using CampaignRollupStoreTests.StoreFixture fixture = await CampaignRollupStoreTests.StoreFixture.CreateAsync(legacy: true);

        CampaignSummaryClosureSnapshot empty = await CampaignSummaryLifecycle.ReadSessionClosureAsync(fixture.Connection, null, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(0, empty.Rows);

        Assert.Equal(Convert.ToHexString(SHA256.HashData([])), empty.Authority);

        await fixture.ExecuteAsync("DROP TABLE campaign_rollup_artifacts");

        CampaignSummaryClosureSnapshot absent = await CampaignSummaryLifecycle.ReadSessionClosureAsync(fixture.Connection, null, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(0, absent.Rows);

        Assert.Equal("", absent.Authority);
    }

    [Fact]
    public async Task Optional_row_keys_union_shared_dependencies_without_changing_snapshot_authority()
    {
        await using CampaignRollupStoreTests.StoreFixture fixture = await CampaignRollupStoreTests.StoreFixture.CreateAsync(legacy: true);

        await fixture.PublishCampaignAsync("published");

        Guid peer = Guid.Parse("A137254D-0343-4212-9898-8B3E36B80213");

        await fixture.AddSessionAsync(peer, "peer decision");

        var input = (await fixture.Store.PrepareContributionAsync(peer, null, null, CancellationToken.None)).Value!;

        Assert.True((await fixture.Store.PublishContributionAsync(input, "peer contribution", null, null, CancellationToken.None)).IsSuccess);

        var rollup = (await fixture.Store.PrepareRollupAsync(fixture.CampaignId, null, CancellationToken.None)).Value!;

        Assert.True((await fixture.Store.PublishRollupAsync(rollup, "shared publication", null, null, CancellationToken.None)).IsSuccess);

        await fixture.ExecuteAsync(Assert.Single(GrimoireSchemaCatalog.CoreObjects, item => item.Name == "assistant_finalization_capacity_reservations").Sql);

        await fixture.ExecuteAsync($"""
            INSERT INTO assistant_finalization_capacity_reservations VALUES('reservation','{fixture.SessionId.ToString().ToUpperInvariant()}','future',1,'claim',1,'2026-10-01T00:00:00.0000000Z','2026-10-01T00:00:00.0000000Z');
            INSERT INTO session_turn_claims(ClaimId,OriginInstallationId,OriginRestoreEpoch,ClientTurnId,SessionId,SurfaceCode,RequestDigest,DependencyDigest,StateCode,PreRequestHistoryRevision,InputSensitivityRevision,ExpectedCurrentSensitivityRevision,FinalizationReservationId,ExecutorId,CheckpointRevision,CompletedStepMask,CreatedAtUtc)
            VALUES('claim','installation',0,'client','{fixture.SessionId.ToString().ToUpperInvariant()}',1,zeroblob(32),zeroblob(32),1,0,0,0,'reservation','executor',0,0,'2026-10-01T00:00:00.0000000Z');
            INSERT INTO campaign_fork_frontiers VALUES('{fixture.SessionId.ToString().ToUpperInvariant()}','parent',0,1,'2026-10-01T00:00:00.0000000Z');
            INSERT INTO campaign_maintenance_checkpoints(ClaimId,StepCode,CampaignId,SessionId,ExecutorId,ExpectedOutputRevision,CheckpointStateCode,InputSourceGeneration,InputManifestDigest,CheckpointRevision,UpdatedAtUtc)
            VALUES('claim',5,'{fixture.CampaignId.ToString().ToUpperInvariant()}','{fixture.SessionId.ToString().ToUpperInvariant()}','executor',0,1,0,zeroblob(32),0,'2026-10-01T00:00:00.0000000Z');
            INSERT INTO artifact_sensitivity(LabelId,ArtifactKindCode,ArtifactId,SensitivityCode,ProvenanceModeCode,ExactGenerationIds,ArtifactRevision,ArtifactContentDigest,SensitivityDigest,ArtifactLabelDigest,CreatedAtUtc)
            SELECT ArtifactId,14,ArtifactId,1,1,zeroblob(16),Revision,ContentDigest,SensitivityDigest,zeroblob(32),'2026-10-01T00:00:00.0000000Z' FROM campaign_rollup_artifacts;
            """);

        HashSet<string> keys = new(StringComparer.Ordinal) { "existing:row" };

        var legacy = await ReadLegacyClosureAsync(fixture.Connection, null, fixture.SessionId, CancellationToken.None);

        CampaignSummaryClosureSnapshot withoutKeys = await CampaignSummaryLifecycle.ReadSessionClosureAsync(fixture.Connection, null, fixture.SessionId, CancellationToken.None);

        CampaignSummaryClosureSnapshot withKeys = await CampaignSummaryLifecycle.ReadSessionClosureAsync(fixture.Connection, null, fixture.SessionId, CancellationToken.None, rowKeys: keys);

        Assert.Equal(withoutKeys, withKeys);

        Assert.Equal(legacy.RowKeys.Split('\n', StringSplitOptions.RemoveEmptyEntries).Append("existing:row").Order(StringComparer.Ordinal), keys.Order(StringComparer.Ordinal));

        _ = await CampaignSummaryLifecycle.ReadSessionClosureAsync(fixture.Connection, null, fixture.SessionId, CancellationToken.None, rowKeys: keys);

        Assert.Equal(legacy.Rows + 1, keys.Count);

        var peerLegacy = await ReadLegacyClosureAsync(fixture.Connection, null, peer, CancellationToken.None);

        _ = await CampaignSummaryLifecycle.ReadSessionClosureAsync(fixture.Connection, null, peer, CancellationToken.None, rowKeys: keys);

        string[] expected = [.. (legacy.RowKeys + peerLegacy.RowKeys).Split('\n', StringSplitOptions.RemoveEmptyEntries).Append("existing:row").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        Assert.Equal(expected, keys.Order(StringComparer.Ordinal));

        Assert.True(keys.Count < legacy.Rows + peerLegacy.Rows + 1);

        Assert.Equal(7, keys.Where(key => key != "existing:row").Select(key => key[..key.IndexOf(':')]).Distinct().Count());
    }

    [Fact]
    public async Task Cancelled_closure_propagates_without_publishing_a_snapshot()
    {
        await using CampaignRollupStoreTests.StoreFixture fixture = await CampaignRollupStoreTests.StoreFixture.CreateAsync(legacy: true);

        await fixture.PublishCampaignAsync("published");

        using CancellationTokenSource cancellation = new();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CampaignSummaryLifecycle.ReadSessionClosureAsync(fixture.Connection, null, fixture.SessionId, cancellation.Token));
    }

    // Frozen pre-streaming algorithm: this oracle intentionally materializes the small fixture.
    private static async Task<(long Rows, string Authority, string RowKeys)> ReadLegacyClosureAsync(
        DbConnection connection,
        DbTransaction? transaction,
        Guid sessionId,
        CancellationToken cancellationToken,
        bool includeOwnerRows = true)
    {
        await using DbCommand installed = connection.CreateCommand();

        installed.Transaction = transaction;

        installed.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'campaign_rollup_artifacts');";

        if (Convert.ToInt64(await installed.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture) == 0)
        {
            return (0, "", "");
        }

        const string contributions = "SELECT ArtifactId FROM campaign_contribution_artifacts WHERE SessionId = $ownerId";

        const string rollups = "SELECT RollupArtifactId FROM campaign_rollup_sources WHERE SessionId = $ownerId";

        (string Table, string Predicate, string Identity, string Key)[] targets =
        [
            ("campaign_contribution_artifacts", "SessionId = $ownerId", "ArtifactId || ':' || Revision || ':' || hex(SourceManifestDigest)", "ArtifactId"),
            ("campaign_rollup_artifacts", $"ArtifactId IN ({rollups})", "ArtifactId || ':' || Revision || ':' || hex(SourceManifestDigest)", "ArtifactId"),
            ("campaign_rollup_sources", $"RollupArtifactId IN ({rollups})", "RollupArtifactId || ':' || SessionId || ':' || ContributionArtifactId || ':' || ContributionRevision || ':' || hex(ContentDigest) || ':' || hex(SensitivityDigest)", "RollupArtifactId || ':' || SessionId"),
            ("campaign_maintenance_checkpoints", $"SessionId = $ownerId OR SourceSessionId = $ownerId OR (OutputArtifactKindCode = 15 AND OutputArtifactId IN ({contributions})) OR (OutputArtifactKindCode = 14 AND OutputArtifactId IN ({rollups}))", "ClaimId || ':' || StepCode || ':' || hex(InputManifestDigest) || ':' || CheckpointRevision", "ClaimId || ':' || StepCode || ':' || hex(InputManifestDigest)"),
            ("campaign_contribution_state", "SessionId = $ownerId", "SessionId || ':' || Revision || ':' || SourceGeneration || ':' || SummarizedThroughSequence", "SessionId"),
            ("campaign_fork_frontiers", "SessionId = $ownerId", "SessionId || ':' || SourceSessionId || ':' || InheritedThroughSequence || ':' || ProofKindCode", "SessionId"),
            ("artifact_sensitivity", "(ArtifactKindCode = 15 AND lower(replace(ArtifactId, '-', '')) IN (SELECT lower(replace(ArtifactId, '-', '')) FROM campaign_contribution_artifacts WHERE SessionId = $ownerId)) OR (ArtifactKindCode = 14 AND lower(replace(ArtifactId, '-', '')) IN (SELECT lower(replace(RollupArtifactId, '-', '')) FROM campaign_rollup_sources WHERE SessionId = $ownerId))", "LabelId || ':' || ArtifactRevision || ':' || hex(ArtifactContentDigest)", "LabelId"),
        ];

        long rows = 0;

        System.Text.StringBuilder authority = new();

        System.Text.StringBuilder rowKeys = new();

        foreach ((string table, string predicate, string identity, string key) in targets)
        {
            if (!includeOwnerRows && table is "campaign_contribution_state" or "campaign_fork_frontiers")
            {
                continue;
            }

            await using DbCommand command = connection.CreateCommand();

            command.Transaction = transaction;

            command.CommandText = $"SELECT {identity}, {key} FROM {table} WHERE {predicate} ORDER BY 1;";

            command.Parameters.Add(new SqliteParameter("$ownerId", sessionId.ToString("D").ToUpperInvariant()));

            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string value = reader.GetString(0);

                authority.Append(table).Append(':').Append(value.Length).Append(':').Append(value).Append('\n');

                rowKeys.Append(table).Append(':').Append(reader.GetString(1)).Append('\n');

                rows = checked(rows + 1);
            }
        }

        return (rows, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(authority.ToString()))), rowKeys.ToString());
    }
}
