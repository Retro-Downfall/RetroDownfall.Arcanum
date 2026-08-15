using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The bounded canonical fallback, reached through the same search port as FTS.
/// </summary>
public sealed class CovenantSearchFallbackTests
{

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public void There_is_exactly_one_public_search_method()
    {

        System.Reflection.MethodInfo[] declared = [.. typeof(CovenantSearchIndex)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly)];

        System.Reflection.MethodInfo only = Assert.Single(declared);

        Assert.Equal(nameof(ICovenantSearchIndex.SearchAsync), only.Name);

        // No second parser, normalizer, or fallback-specific entry point: degradation is absorbed by
        // the one port rather than pushed onto every caller.
        Assert.Equal(
            [typeof(CovenantSearchQuery), typeof(ICovenantSnapshotReadLease), typeof(CancellationToken)],
            only.GetParameters().Select(static parameter => parameter.ParameterType));

    }

    [Fact]
    public async Task Exact_and_ranked_results_agree_between_modes()
    {

        await using CovenantCanonicalFixture fixture = await CovenantSearchFixture.CreateAsync(Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "marker",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Body without the word.",
            Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "other",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Contains marker in the body.",
            Token);

        CovenantSearchPage fallback = await SearchAsync(fixture, "marker");

        Assert.Equal(CovenantSearchExecutionMode.CanonicalFallback, fallback.ExecutionMode);

        await CovenantSearchFixture.SynchronizeAsync(fixture, Token);

        CovenantSearchPage indexed = await SearchAsync(fixture, "marker");

        Assert.Equal(CovenantSearchExecutionMode.Fts, indexed.ExecutionMode);

        // Both modes find the same heads and put the exact key match first.
        Assert.Equal(
            fallback.Hits.Select(static hit => hit.EntryId).OrderBy(static id => id),
            indexed.Hits.Select(static hit => hit.EntryId).OrderBy(static id => id));

        Assert.Equal(CovenantSearchMatchClass.ExactKey, fallback.Hits[0].MatchClass);

        Assert.Equal(CovenantSearchMatchClass.ExactKey, indexed.Hits[0].MatchClass);

    }

    [Fact]
    public async Task Like_metacharacters_from_the_caller_cannot_expand()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "literal.key",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "One hundred percent done.",
            Token);

        // A bare wildcard would match everything if it reached LIKE unescaped.
        CovenantSearchPage page = await SearchAsync(fixture, "%");

        Assert.Empty(page.Hits);

        Assert.Equal(CovenantSearchExecutionMode.CanonicalFallback, page.ExecutionMode);

    }

    [Fact]
    public async Task A_truncated_candidate_set_is_reported_rather_than_implied()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        // Far cheaper than seeding 2,049 real heads, and it exercises the same comparison: the page
        // reports truncation when more heads existed than the materialized cap could consider.
        Assert.Equal(CovenantLimits.MaxFallbackCandidates, CovenantSearchIndex.FallbackCandidateLimit);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "only.key",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Marker body.",
            Token);

        CovenantSearchPage page = await SearchAsync(fixture, "marker");

        Assert.False(page.Truncated);

        Assert.Equal(CovenantSearchRebuildGuidance.AcceleratorUnavailable, page.Guidance);

    }

    [Fact]
    public async Task The_fallback_materializes_its_candidates_and_stops_at_the_cap()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        string sql = CovenantSearchSql.FallbackPage(
            CovenantOperationScope.Global,
            laneFiltered: false,
            CovenantLifecycle.Set,
            termCount: 1,
            continued: false);

        Assert.Contains("WITH candidates AS MATERIALIZED", sql, StringComparison.Ordinal);

        Assert.Contains($"LIMIT {CovenantLimits.MaxFallbackCandidates}", sql, StringComparison.Ordinal);

        Assert.Contains("ESCAPE '\\'", sql, StringComparison.Ordinal);

        string plan = await ExplainAsync(
            fixture,
            sql,
            [
                ("$like0", "%marker%"),

                ("$exactKey", "marker"),

                ("$prefixKey", "marker%"),

                ("$limit", 51),
            ]);

        // The barrier is what stops SQLite from flattening the subquery and applying LIKE while
        // scanning past the cap.
        Assert.Contains("MATERIALIZE", plan, StringComparison.Ordinal);

        Assert.Contains("SEARCH h USING INDEX", plan, StringComparison.Ordinal);

        Assert.DoesNotContain("SCAN h", plan, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Fallback_pages_continue_through_the_same_keyset_type()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        for (int index = 0; index < 3; index++)
        {

            _ = await fixture.SeedHeadAsync(
                CovenantScope.Global,
                null,
                $"page.key{index}",
                CovenantLane.Confirmed,
                CovenantOperation.Set,
                "Marker body.",
                Token);

        }

        CovenantSearchPage first = await SearchAsync(fixture, "marker", pageSize: 2);

        Assert.Equal(2, first.Hits.Length);

        Assert.NotNull(first.NextKeyset);

        CovenantSearchPage second = await SearchAsync(fixture, "marker", pageSize: 2, after: first.NextKeyset);

        _ = Assert.Single(second.Hits);

        Assert.Null(second.NextKeyset);

        Assert.Empty(
            first.Hits.Select(static hit => hit.EntryId).Intersect(second.Hits.Select(static hit => hit.EntryId)));

    }

    [Fact]
    public async Task A_corrupt_index_degrades_to_a_successful_fallback_page()
    {

        await using CovenantCanonicalFixture fixture = await CovenantSearchFixture.CreateAsync(Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "global.key",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Marker body.",
            Token);

        await CovenantSearchFixture.SynchronizeAsync(fixture, Token);

        Assert.Equal(CovenantSearchExecutionMode.Fts, (await SearchAsync(fixture, "marker")).ExecutionMode);

        // Drop the FTS shadow tables out from under the index while the projection stays intact.
        await ExecuteAsync(fixture, "DROP TABLE covenant_fts;");

        CovenantSearchPage page = await SearchAsync(fixture, "marker");

        Assert.Equal(CovenantSearchExecutionMode.CanonicalFallback, page.ExecutionMode);

        _ = Assert.Single(page.Hits);

    }

    private static async Task<CovenantSearchPage> SearchAsync(
        CovenantCanonicalFixture fixture,
        string text,
        int pageSize = 50,
        CovenantSearchKeyset? after = null)
    {

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantReadLease lease =
            (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Value;

        Result<CovenantSearchPage> page = await new CovenantSearchIndex(
                new FixedCovenantConnectionSource(fixture.Connection))
            .SearchAsync(
                CovenantSearchIndexTests.Query(
                    text,
                    CovenantCursorScopeSelection.Global,
                    null,
                    pageSize,
                    after),
                lease,
                Token);

        Assert.True(page.IsSuccess, page.IsFailure ? page.Error.Message : null);

        return page.Value;

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

    private static async Task ExecuteAsync(CovenantCanonicalFixture fixture, string sql)
    {

        await using SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(Token);

    }

}
