using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed class CampaignSharedManifestClosureTests
{
    [Theory]
    [InlineData("SessionId", "idx_campaign_rollup_manifest_sources_session")]
    [InlineData("ContributionArtifactId", "idx_campaign_rollup_manifest_sources_contribution")]
    public async Task Reverse_membership_seeks_shared_sources_instead_of_expanding_every_root(string column, string index)
    {
        await using CampaignRollupStoreTests.StoreFixture fixture = await CampaignRollupStoreTests.StoreFixture.CreateAsync();

        await using SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = $"EXPLAIN QUERY PLAN SELECT DISTINCT RollupArtifactId FROM campaign_rollup_source_membership WHERE {column} = $owner;";

        command.Parameters.AddWithValue("$owner", fixture.SessionId.ToString("D").ToUpperInvariant());

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        List<string> plans = [];

        while (await reader.ReadAsync())
        {
            plans.Add(reader.GetString(3));
        }

        Assert.Contains(plans, plan => plan.Contains(index, StringComparison.Ordinal));

        Assert.Contains(plans, plan => plan.Contains("idx_campaign_rollup_artifacts_manifest", StringComparison.Ordinal));

        Assert.DoesNotContain(plans, plan => plan.StartsWith("SCAN source", StringComparison.Ordinal)
            || plan.StartsWith("SCAN segment", StringComparison.Ordinal) || plan.StartsWith("SCAN artifact", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Shared_closure_counts_physical_rows_once_and_binds_each_source_cutoff()
    {
        await using CampaignRollupStoreTests.StoreFixture fixture = await CampaignRollupStoreTests.StoreFixture.CreateAsync();

        await fixture.PublishCampaignAsync("first summary");

        await CampaignRollupStoreTests.PublishNextSessionAsync(fixture);

        CampaignRollupArtifact current = await CampaignRollupStoreTests.PublishNextSessionAsync(fixture);

        HashSet<string> rows = new(StringComparer.Ordinal);

        CampaignSummaryClosureSnapshot snapshot = await CampaignSummaryLifecycle.ReadSessionClosureAsync(
            fixture.Connection, null, fixture.SessionId, CancellationToken.None, rowKeys: rows);

        // One selected contribution, its owner state, three roots, three segments and three sources.
        Assert.Equal(11, snapshot.Rows);

        Assert.Equal(11, rows.Count);

        Assert.Equal(3, rows.Count(row => row.StartsWith("campaign_rollup_manifest_sources:", StringComparison.Ordinal)));

        Assert.Equal(3, rows.Count(row => row.StartsWith("campaign_rollup_manifests:", StringComparison.Ordinal)));

        Assert.Equal(snapshot, await CampaignSummaryLifecycle.ReadSessionClosureAsync(
            fixture.Connection, null, fixture.SessionId, CancellationToken.None, rowKeys: rows));

        Assert.Equal(11, rows.Count);

        Assert.Equal(10, (await CampaignSummaryLifecycle.ReadSessionClosureAsync(
            fixture.Connection, null, fixture.SessionId, CancellationToken.None, includeOwnerRows: false)).Rows);

        await fixture.ExecuteAsync("DROP TRIGGER campaign_rollup_manifest_sources_guard_update; UPDATE campaign_rollup_manifest_sources SET SummarizedThroughSequence = SummarizedThroughSequence + 1;");

        CampaignSummaryClosureSnapshot changed = await CampaignSummaryLifecycle.ReadSessionClosureAsync(
            fixture.Connection, null, fixture.SessionId, CancellationToken.None);

        Assert.Equal(snapshot.Rows, changed.Rows);

        Assert.NotEqual(snapshot.Authority, changed.Authority);

        Assert.True((await fixture.Store.ValidateAsync(current, null, CancellationToken.None)).IsFailure);
    }

    [Fact]
    public async Task Failed_summary_insert_rolls_back_the_new_segment_and_can_retry_the_same_paid_output()
    {
        await using CampaignRollupStoreTests.StoreFixture fixture = await CampaignRollupStoreTests.StoreFixture.CreateAsync();

        await fixture.PublishCampaignAsync("first summary");

        Guid next = Guid.NewGuid();

        await fixture.AddSessionAsync(next, "next decision");

        CampaignContributionInput contribution = (await fixture.Store.PrepareContributionAsync(next, null, null, CancellationToken.None)).Value!;

        Assert.True((await fixture.Store.PublishContributionAsync(contribution, "next decision", null, null, CancellationToken.None)).IsSuccess);

        CampaignRollupInput fold = (await fixture.Store.PrepareRollupAsync(fixture.CampaignId, null, CancellationToken.None)).Value!;

        await fixture.ExecuteAsync("CREATE TRIGGER refuse_summary BEFORE INSERT ON campaign_rollup_artifacts BEGIN SELECT RAISE(ABORT, 'fixture failure'); END;");

        Assert.True((await fixture.Store.PublishRollupAsync(fold, "already paid summary", null, null, CancellationToken.None)).IsFailure);

        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_rollup_artifacts;"));

        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_rollup_manifests;"));

        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_rollup_manifest_sources;"));

        await fixture.ExecuteAsync("DROP TRIGGER refuse_summary;");

        var retried = await fixture.Store.PublishRollupAsync(fold, "already paid summary", null, null, CancellationToken.None);

        Assert.True(retried.IsSuccess, retried.Error.Message);

        Assert.Equal("already paid summary", retried.Value.Content);

        Assert.Equal(2, retried.Value.Revision);

        Assert.Equal(2, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_rollup_manifest_sources;"));
    }
}
