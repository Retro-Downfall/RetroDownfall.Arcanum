using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// <c>EXPLAIN QUERY PLAN</c> evidence for every canonical read.
/// </summary>
/// <remarks>
/// These suites explain the exact strings <see cref="CovenantStore"/> executes, taken from the same
/// builder. A plan test that assembled equivalent SQL of its own would keep passing after the real
/// query quietly lost an index, which is the failure this file exists to catch.
/// </remarks>
public sealed class CovenantQueryPlanTests
{

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public async Task The_turn_snapshot_searches_both_partial_active_head_indexes()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        string plan = await ExplainAsync(
            fixture,
            CovenantStoreSql.TurnSnapshot(),
            [("$campaign", CovenantOperationGateFixture.CampaignOne.ToString("D"))]);

        // Both arms seek an index on the Campaign discriminator, so each reads only its own scope's
        // heads rather than every head on the installation.
        Assert.Equal(2, CountOccurrences(plan, "SEARCH h USING INDEX"));

        Assert.Equal(2, CountOccurrences(plan, "SEARCH v USING INDEX"));

        // No table scan and no temporary sort on the hot path: either would turn a bounded turn load
        // into an unbounded one.
        Assert.DoesNotContain("SCAN h", plan, StringComparison.Ordinal);

        Assert.DoesNotContain("SCAN v", plan, StringComparison.Ordinal);

        Assert.DoesNotContain("TEMP B-TREE", plan, StringComparison.Ordinal);

    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_lane_head_probe_searches_its_scope_index(bool campaignScoped)
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        (string Name, object Value)[] parameters = campaignScoped
            ?
            [
                ("$key", "a.key"),

                ("$lane", 1),

                ("$campaign", CovenantOperationGateFixture.CampaignOne.ToString("D")),
            ]
            :
            [
                ("$key", "a.key"),

                ("$lane", 1),
            ];

        string plan = await ExplainAsync(fixture, CovenantStoreSql.LaneHeadProbe(campaignScoped), parameters);

        Assert.Contains(
            campaignScoped ? "idx_covenant_heads_campaign_active" : "idx_covenant_heads_global_active",
            plan,
            StringComparison.Ordinal);

        Assert.Contains("covenant_key_epochs", plan, StringComparison.Ordinal);

        Assert.DoesNotContain("SCAN h", plan, StringComparison.Ordinal);

    }

    [Fact]
    public async Task The_stable_list_page_uses_the_entry_and_version_keys()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        string plan = await ExplainAsync(
            fixture,
            CovenantStoreSql.ListPage(
                CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignOne),
                laneFiltered: true,
                CovenantLifecycle.Set,
                continued: true),
            [
                ("$campaign", CovenantOperationGateFixture.CampaignOne.ToString("D")),

                ("$lane", 1),

                ("$limit", 51),

                ("$afterScope", 2),

                ("$afterCampaign", CovenantOperationGateFixture.CampaignOne.ToString("D")),

                ("$afterKey", "a.key"),

                ("$afterEntry", Guid.Empty.ToString("D")),

                ("$afterLane", 1),
            ]);

        // The page seeks its scope's heads and then joins entry and version by primary key. The
        // outer ORDER BY does cost a temporary sort, which is affordable only because a scope holds
        // at most CovenantLimits.MaxStableEntriesPerScope heads.
        Assert.Contains("SEARCH h USING INDEX", plan, StringComparison.Ordinal);

        Assert.Contains("SEARCH e USING INDEX", plan, StringComparison.Ordinal);

        Assert.Contains("SEARCH v USING INDEX", plan, StringComparison.Ordinal);

        Assert.Contains(
            "SEARCH owner_deletion_events USING COVERING INDEX idx_owner_deletion_events_kind_sequence",
            plan,
            StringComparison.Ordinal);

        Assert.DoesNotContain("SCAN e", plan, StringComparison.Ordinal);

        Assert.DoesNotContain("SCAN h", plan, StringComparison.Ordinal);

    }

    [Fact]
    public async Task The_descending_version_page_uses_the_entry_lane_revision_index()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        string plan = await ExplainAsync(
            fixture,
            CovenantStoreSql.VersionPage(continued: true),
            [
                ("$entry", Guid.Empty.ToString("D")),

                ("$lane", 1),

                ("$limit", 51),

                ("$afterRevision", 5L),
            ]);

        Assert.Contains("ux_covenant_versions_entry_lane_revision", plan, StringComparison.Ordinal);

        Assert.DoesNotContain("SCAN covenant_versions", plan, StringComparison.Ordinal);

        // The index already yields descending revisions, so no ordering pass is needed inside the page.
        Assert.DoesNotContain("USE TEMP B-TREE FOR ORDER BY", plan, StringComparison.Ordinal);

    }

    [Fact]
    public async Task The_provenance_read_is_one_indexed_join_with_no_n_plus_one()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        string plan = await ExplainAsync(
            fixture,
            CovenantStoreSql.SourcePage(),
            [("$version", Guid.Empty.ToString("D"))]);

        Assert.Contains("SEARCH v USING INDEX", plan, StringComparison.Ordinal);

        Assert.Contains("SEARCH p USING", plan, StringComparison.Ordinal);

        Assert.DoesNotContain("SCAN p", plan, StringComparison.Ordinal);

    }

    [Fact]
    public async Task The_global_effect_scan_streams_the_campaign_registry_by_key()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        string plan = await ExplainAsync(
            fixture,
            CovenantStoreSql.DependentHeadScan(allCampaigns: true),
            [("$key", "shared.key")]);

        // Streaming every Campaign is the point of a Global effect scan; what must stay indexed is
        // the per-Campaign head lookup that would otherwise make it quadratic.
        Assert.Contains("SEARCH h USING INDEX", plan, StringComparison.Ordinal);

        Assert.DoesNotContain("SCAN h", plan, StringComparison.Ordinal);

        string scoped = await ExplainAsync(
            fixture,
            CovenantStoreSql.DependentHeadScan(allCampaigns: false),
            [
                ("$key", "shared.key"),

                ("$campaign", CovenantOperationGateFixture.CampaignOne.ToString("D")),
            ]);

        Assert.DoesNotContain("SCAN c", scoped, StringComparison.Ordinal);

    }

    [Fact]
    public async Task The_effect_facts_read_touches_only_singleton_rows_and_the_key_epoch()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        string plan = await ExplainAsync(fixture, CovenantStoreSql.EffectFacts(), [("$key", "shared.key")]);

        Assert.Contains("covenant_key_epochs", plan, StringComparison.Ordinal);

        Assert.Contains("campaign_registry_state", plan, StringComparison.Ordinal);

        Assert.DoesNotContain("SCAN covenant_key_epochs", plan, StringComparison.Ordinal);

        Assert.DoesNotContain("SCAN campaign_registry_state", plan, StringComparison.Ordinal);

    }

    private static int CountOccurrences(string plan, string needle)
    {

        int count = 0;

        for (int index = plan.IndexOf(needle, StringComparison.Ordinal);
            index >= 0;
            index = plan.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
        {

            count++;

        }

        return count;

    }

    private static async Task<string> ExplainAsync(
        CovenantCanonicalFixture fixture,
        string sql,
        IReadOnlyList<(string Name, object Value)> parameters)
    {

        await using SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = "EXPLAIN QUERY PLAN " + sql;

        foreach ((string name, object value) in parameters)
        {

            _ = command.Parameters.AddWithValue(name, value);

        }

        System.Text.StringBuilder plan = new();

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(Token);

        while (await reader.ReadAsync(Token))
        {

            _ = plan.AppendLine(reader.GetString(reader.FieldCount - 1));

        }

        return plan.ToString();

    }

}
