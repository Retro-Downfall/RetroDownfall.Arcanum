using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Tests.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Memory;

public sealed class CovenantReviewQueryPlanTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData("marker", false)]
    [InlineData("marker", true)]
    [InlineData("upper", false)]
    [InlineData("upper", true)]
    [InlineData("page", false)]
    [InlineData("page", true)]
    [InlineData("event", false)]
    [InlineData("event", true)]
    [InlineData("through", false)]
    [InlineData("through", true)]
    [InlineData("advance", false)]
    [InlineData("advance", true)]
    [InlineData("curation", false)]
    [InlineData("curation", true)]
    public async Task Exact_executed_review_statements_seek_the_existing_scope_indexes(string statement, bool campaign)
    {
        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(CancellationToken.None);

        string sql = statement switch
        {
            "marker" => CovenantReviewSql.Marker(campaign),
            "upper" => CovenantReviewSql.UpperFrontier(campaign),
            "page" => CovenantReviewSql.Page(campaign),
            "event" => CovenantReviewSql.Event(campaign),
            "through" => CovenantReviewSql.ReviewedThrough(campaign),
            "advance" => CovenantReviewSql.AdvanceMarker(campaign),
            "curation" => CovenantReviewSql.CurationRevision(campaign),
            _ => throw new ArgumentOutOfRangeException(nameof(statement)),
        };

        Guid generation = await fixture.ReadDatasetGenerationAsync(CancellationToken.None);

        (string Name, object Value)[] parameters =
        [
            ("$generation", generation.ToByteArray()),
            ("$scope", campaign ? 2 : 1),
            ("$campaign", Guid.CreateVersion7().ToString("D")),
            ("$lane", 1),
            ("$lower", 1L),
            ("$upper", long.MaxValue),
            ("$keyset", DBNull.Value),
            ("$limit", 50),
            ("$sequence", 1L),
            ("$current", 0L),
            ("$through", 1L),
            ("$expectedThrough", 0L),
            ("$revision", 1L),
            ("$key", "native.key"),
            ("$epoch", 0L),
        ];

        await using SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = "EXPLAIN QUERY PLAN " + sql;

        foreach ((string name, object value) in parameters)
        {
            if (sql.Contains(name, StringComparison.Ordinal))
            {
                command.Parameters.AddWithValue(name, value);
            }
        }

        List<string> lines = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);

        while (await reader.ReadAsync(CancellationToken.None))
        {
            lines.Add(reader.GetString(3));
        }

        string plan = string.Join(" | ", lines);

        output.WriteLine($"{statement}/{(campaign ? "Campaign" : "Global")}: {plan}");

        string expected = statement switch
        {
            "marker" or "advance" => campaign
                ? "ux_covenant_review_markers_campaign_scope" : "ux_covenant_review_markers_global_scope",
            "curation" => campaign
                ? "ux_covenant_curation_heads_campaign_subject" : "ux_covenant_curation_heads_global_subject",
            "event" => "SEARCH event USING INTEGER PRIMARY KEY",
            _ => campaign ? "idx_covenant_review_events_campaign_queue" : "idx_covenant_review_events_global_queue",
        };

        Assert.Contains(expected, plan, StringComparison.Ordinal);

        Assert.DoesNotContain("SCAN event", plan, StringComparison.Ordinal);

        Assert.DoesNotContain("SCAN covenant_review", plan, StringComparison.Ordinal);

        Assert.DoesNotContain("SCAN covenant_curation", plan, StringComparison.Ordinal);

        Assert.DoesNotContain("TEMP B-TREE", plan, StringComparison.Ordinal);
    }
}
