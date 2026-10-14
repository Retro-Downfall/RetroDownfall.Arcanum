using System.Data;
using System.Text.RegularExpressions;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

/// <summary>
/// The pinned <c>EXPLAIN QUERY PLAN</c> of every Long Rest statement that joins on a normalized identity.
/// </summary>
/// <remarks>
/// A label, a source and a discovery node are all keyed by a Guid whose stored spelling once varied, so each comparison wraps both
/// sides in <c>lower(replace(col, '-', ''))</c> and a mis-spelled protected label still counts as protected. SQLite answers a
/// function-wrapped column only from an index on that exact expression; without one every leaf read, source capture page, partition
/// probe and label write was a scan of the whole label table. The plan is pinned for the statements production executes - the
/// constants and the trigger bodies come from the production schema, not from a restatement - because a predicate that drifted out
/// of the index's shape would fall back to <c>SCAN</c> and the only thing that would notice is a slow install.
/// </remarks>
[Collection("Grimoire")]

[Trait("Category", "Integration")]

public sealed class LongRestIdentityIndexPlanTests(GrimoireFixture fixture) : IAsyncLifetime
{
    private const string LabelIndex = "idx_artifact_sensitivity_artifact_norm";

    private static readonly Regex Parameter = new(@"[$@][A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public Task InitializeAsync()
    {
        _dbPath = fixture.CopyDatabase();

        _db = fixture.CreateContext(_dbPath);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_db is not null)
        {
            SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

            await _db.DisposeAsync();

            SqliteConnection.ClearPool(connection);
        }

        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [SkippableFact]

    public async Task The_leaf_anti_join_finds_each_label_through_the_normalized_index()
    {
        RequireSqlCipher();

        AssertLabelSearches(await ExplainAsync(LongRestDiscoveryStore.FrozenSourceMetadataSql), expected: 2);
    }

    [SkippableFact]

    public async Task The_snapshot_label_subqueries_find_each_label_through_the_normalized_index()
    {
        RequireSqlCipher();

        AssertLabelSearches(await ExplainAsync(SagaLongRestService.SnapshotSql), expected: 2, alias: "a");
    }

    [SkippableFact]

    public async Task The_capture_page_finds_each_partition_label_through_the_normalized_index()
    {
        RequireSqlCipher();

        AssertLabelSearches(await ExplainAsync(LongRestDiscoveryStore.CapturePageSql), expected: 2);
    }

    [SkippableFact]

    public async Task The_protected_partition_probe_finds_each_label_through_the_normalized_index()
    {
        RequireSqlCipher();

        string plan = await ExplainAsync(LongRestDiscoveryStore.ProtectedPartitionProbeSql);

        AssertLabelSearches(plan, expected: 1);

        Assert.DoesNotContain("SCAN long_rest_sources", plan, StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData("long_rest_label_insert", "NEW.ArtifactId")]
    [InlineData("long_rest_label_delete", "OLD.ArtifactId")]

    public async Task A_label_write_finds_its_source_through_the_normalized_index(string trigger, string row)
    {
        RequireSqlCipher();

        string statement = (await TriggerBodyAsync(trigger)).Replace(row, "$id", StringComparison.Ordinal);

        Assert.Equal(
            "SEARCH long_rest_sources USING INDEX idx_long_rest_sources_memory_norm (<expr>=?)",
            await ExplainAsync(statement));
    }

    [SkippableTheory]
    [InlineData("long_rest_discovery_nodes", "idx_long_rest_nodes_node_norm", "NodeId")]
    [InlineData("long_rest_discovery_embeddings", "idx_long_rest_embeddings_node_norm", "NodeId")]
    [InlineData("long_rest_accepted_summaries", "idx_long_rest_accepted_summaries_node_norm", "NodeId")]

    public async Task The_summary_label_guard_finds_a_surviving_projection_through_a_normalized_index(string table, string index, string column)
    {
        RequireSqlCipher();

        string predicate = $"lower(replace({column}, '-', '')) = lower(replace(OLD.ArtifactId, '-', ''))";

        Assert.Contains(predicate, Squash(await TriggerSqlAsync("artifact_sensitivity_guard_delete")), StringComparison.Ordinal);

        Assert.Matches(
            $@"^SEARCH {table} USING (COVERING )?INDEX {index} \(<expr>=\?\)$",
            await ExplainAsync($"SELECT 1 FROM {table} WHERE {predicate.Replace("OLD.ArtifactId", "$id", StringComparison.Ordinal)}"));
    }

    [SkippableFact]

    public async Task The_summary_label_guard_finds_a_surviving_edge_through_both_normalized_indexes()
    {
        RequireSqlCipher();

        string parent = "lower(replace(ParentNodeId, '-', '')) = lower(replace(OLD.ArtifactId, '-', ''))";

        string child = "lower(replace(ChildNodeId, '-', '')) = lower(replace(OLD.ArtifactId, '-', ''))";

        string guard = Squash(await TriggerSqlAsync("artifact_sensitivity_guard_delete"));

        Assert.Contains(parent, guard, StringComparison.Ordinal);

        Assert.Contains(child, guard, StringComparison.Ordinal);

        string plan = await ExplainAsync(
            $"SELECT 1 FROM long_rest_discovery_edges WHERE {parent.Replace("OLD.ArtifactId", "$id", StringComparison.Ordinal)} OR {child.Replace("OLD.ArtifactId", "$id", StringComparison.Ordinal)}");

        Assert.DoesNotContain("SCAN", plan, StringComparison.Ordinal);

        Assert.Contains("idx_long_rest_edges_parent_norm", plan, StringComparison.Ordinal);

        Assert.Contains("idx_long_rest_edges_child_norm", plan, StringComparison.Ordinal);
    }

    /// <summary>
    /// The completed-replay lookup runs on every eligible Session turn, so it must start from the sweeps of its own scope, partition and
    /// phase rather than from every long-rest operation the installation ever ran.
    /// </summary>
    [SkippableFact]

    public async Task The_completed_lookup_starts_from_the_sweeps_of_its_scope_and_partition()
    {
        RequireSqlCipher();

        string exact = await ExplainAsync(LongRestSweepStore.CompletedExactPartitionSql);

        Assert.Contains(
            "SEARCH sweep USING INDEX idx_long_rest_sweeps_scope_phase (ScopeKindCode=? AND CampaignId=? AND PhaseCode=? AND PartitionDigest=?)",
            exact,
            StringComparison.Ordinal);

        string whole = await ExplainAsync(LongRestSweepStore.CompletedWholeScopeSql);

        Assert.Contains(
            "SEARCH sweep USING INDEX idx_long_rest_sweeps_scope_phase (ScopeKindCode=? AND CampaignId=? AND PhaseCode=?)",
            whole,
            StringComparison.Ordinal);

        Assert.DoesNotContain("SCAN", exact, StringComparison.Ordinal);

        Assert.DoesNotContain("SCAN", whole, StringComparison.Ordinal);
    }

    private static void AssertLabelSearches(string plan, int expected, string alias = "label")
    {
        MatchCollection searches = Regex.Matches(
            plan,
            $@"SEARCH {alias} USING (COVERING )?INDEX {LabelIndex} \(ArtifactKindCode=\? AND <expr>=\?\)");

        Assert.Equal(expected, searches.Count);

        Assert.DoesNotContain($"SEARCH {alias} USING INDEX ux_artifact_sensitivity_artifact (ArtifactKindCode=?)", plan, StringComparison.Ordinal);

        Assert.DoesNotContain($"SEARCH {alias} USING COVERING INDEX ux_artifact_sensitivity_artifact (ArtifactKindCode=?)", plan, StringComparison.Ordinal);
    }

    private static string Squash(string sql) => Regex.Replace(sql, @"\s+", " ").Trim();

    private async Task<string> TriggerSqlAsync(string name)
    {
        SqliteConnection connection = await OpenAsync();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'trigger' AND name = $name;";

        _ = command.Parameters.AddWithValue("$name", name);

        return (string)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>The one statement between BEGIN and END of a trigger, exactly as the installed schema carries it.</summary>
    private async Task<string> TriggerBodyAsync(string name)
    {
        string sql = await TriggerSqlAsync(name);

        int begin = sql.IndexOf("BEGIN", StringComparison.Ordinal) + "BEGIN".Length;

        int end = sql.LastIndexOf("END", StringComparison.Ordinal);

        return sql[begin..end].Trim().TrimEnd(';');
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        if (connection.State is not ConnectionState.Open)
        {
            await connection.OpenAsync(CancellationToken.None);
        }

        return connection;
    }

    private async Task<string> ExplainAsync(string sql)
    {
        SqliteConnection connection = await OpenAsync();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "EXPLAIN QUERY PLAN " + sql;

        foreach (string name in Parameter.Matches(sql).Select(static match => match.Value).Distinct(StringComparer.Ordinal))
        {
            _ = command.Parameters.AddWithValue(name, DBNull.Value);
        }

        List<string> rows = [];

        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None))
        {
            while (await reader.ReadAsync(CancellationToken.None))
            {
                rows.Add(reader.GetString(reader.GetOrdinal("detail")));
            }
        }

        return string.Join("\n", rows);
    }

    private static void RequireSqlCipher() =>
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);
}
