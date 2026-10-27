using System.Data.Common;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SQLitePCL;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

public sealed partial class TapestryStoreTests
{
    [Theory]
    [InlineData(true, 1)]
    [InlineData(true, 24)]
    [InlineData(false, 24)]
    public async Task Closure_work_depends_on_selected_reach_not_unrelated_graph(bool leaves, int selectedCount)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));

        CancellationToken token = deadline.Token;

        string generation = await BeginAsync();

        await AppendClosureRowsAsync(generation, 0, 256, token);

        string[] selected = [.. Enumerable.Range(0, selectedCount).Select(i => $"leaf-{i:D5}")];

        string suffix = leaves ? "leaf" : "ancestor";

        long small = await ProbeClosureAsync("small-" + suffix, generation, selected, leaves, token);

        await AppendClosureRowsAsync(generation, 256, 1792, token);

        long large = await ProbeClosureAsync("large-" + suffix, generation, selected, leaves, token);

        Assert.True(large < 10_000, $"Fixed selected closure used {large} VM instructions with unrelated rows.");

        Assert.True(large < 2 * small, $"Fixed selected closure grew from {small} to {large} VM instructions.");
    }

    [Fact]
    public async Task Checkpoint_recount_work_depends_on_reachable_descendants()
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));

        CancellationToken token = deadline.Token;

        string generation = await BeginAsync();

        await AppendClosureRowsAsync(generation, 0, 2048, token);

        Dictionary<string, long> work = await ProbeCheckpointAsync(new(generation,
            [Summary(generation, "summary-a", "synthetic summary", "members-a", 24)],
            [.. Enumerable.Range(0, 24).Select(i => new TapestryGraphEdge("summary-a", $"leaf-{i:D5}", null, null, "Synthetic"))]), token);

        Assert.Equal(24L, await ReadClosureCountAsync(generation, "summary-a", token));

        Assert.True(work["recount"] < 10_000, $"Fixed 24-leaf recount used {work["recount"]} VM instructions.");
    }

    private async Task<long> ReadClosureCountAsync(string generation, string node, CancellationToken token)
    {
        await using DbCommand command = _db!.Database.GetDbConnection().CreateCommand();

        command.CommandText = "SELECT DescendantLeafCount FROM tapestry_nodes WHERE GenerationId=@generation AND NodeId=@node";

        command.Parameters.Add(new SqliteParameter("@generation", generation));

        command.Parameters.Add(new SqliteParameter("@node", node));

        return (long)(await command.ExecuteScalarAsync(token))!;
    }

    private Task AppendClosureRowsAsync(string generation, int start, int count, CancellationToken token)
    {
        List<TapestryNodeWrite> nodes = [.. Enumerable.Range(start, count)
            .Select(i => Leaf(generation, $"leaf-{i:D5}", $"source-{i:D5}", "synthetic"))];

        if (start == 0) nodes.Add(Summary(generation, "unrelated-summary", "synthetic unrelated", "unrelated", count - 24));

        int edgeStart = Math.Max(24, start);

        return _store!.AppendGraphCheckpointAsync(new(generation, nodes,
            [.. Enumerable.Range(edgeStart, start + count - edgeStart)
                .Select(i => new TapestryGraphEdge("unrelated-summary", $"leaf-{i:D5}", null, null, "Synthetic"))]), token);
    }

    private async Task<Dictionary<string, long>> ProbeCheckpointAsync(TapestryGraphCheckpoint checkpoint, CancellationToken token)
    {
        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        long instructions = 0;

        string phase = "other";

        Dictionary<string, long> work = [];

        HashSet<string> queries = [];

        raw.sqlite3_trace(connection.Handle, (object _, string sql) =>
        {
            phase = sql.StartsWith("WITH RECURSIVE reachable", StringComparison.Ordinal) ? "recount"
                : sql.StartsWith("WITH RECURSIVE reach", StringComparison.Ordinal) ? "ancestors"
                : sql.StartsWith("INSERT", StringComparison.Ordinal) ? "insert" : "other";

            if (phase is "recount" or "ancestors") queries.Add(sql);
        }, null);

        raw.sqlite3_progress_handler(connection.Handle, 1, (object _) =>
        {
            instructions++;

            work[phase] = work.GetValueOrDefault(phase) + 1;

            return instructions >= 10_000_000 || token.IsCancellationRequested ? 1 : 0;
        }, null);

        Stopwatch elapsed = Stopwatch.StartNew();

        try
        {
            await _store!.AppendGraphCheckpointAsync(checkpoint, token);
        }
        finally
        {
            raw.sqlite3_progress_handler(connection.Handle, 0, null, null);

            raw.sqlite3_trace(connection.Handle, (strdelegate_trace)null!, null);
        }

        _output.WriteLine($"append_vm={instructions} elapsed_ms={elapsed.Elapsed.TotalMilliseconds:F3}");

        foreach ((string name, long count) in work.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            _output.WriteLine($"append_phase={name} vm={count}");
        }

        foreach (string sql in queries)
        {
            await ExplainClosureAsync(connection, "append", sql, token);
        }

        return work;
    }

    private async Task<long> ProbeClosureAsync(string label, string generation, string[] selected, bool leaves, CancellationToken token)
    {
        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        long instructions = 0;

        List<string> statements = [];

        raw.sqlite3_progress_handler(connection.Handle, 1, (object _) =>
        {
            instructions++;

            return instructions >= 10_000_000 || token.IsCancellationRequested ? 1 : 0;
        }, null);

        raw.sqlite3_trace(connection.Handle, (object _, string sql) => statements.Add(sql), null);

        Stopwatch elapsed = Stopwatch.StartNew();

        IReadOnlyDictionary<string, IReadOnlyList<string>> result;

        try
        {
            result = leaves
                ? await _store!.GetDescendantLeafIdsAsync(generation, selected, token)
                : await _store!.GetAncestorIdsAsync(generation, selected, token);
        }
        finally
        {
            raw.sqlite3_progress_handler(connection.Handle, 0, null, null);

            raw.sqlite3_trace(connection.Handle, (strdelegate_trace)null!, null);
        }

        long originalInstructions = instructions;

        _output.WriteLine($"probe={label} selected={selected.Length} returned={result.Values.Sum(ids => ids.Count)} vm={instructions} elapsed_ms={elapsed.Elapsed.TotalMilliseconds:F3}");

        Assert.Equal(selected.Order(StringComparer.Ordinal), result.Keys.Order(StringComparer.Ordinal));

        if (label.Contains("ancestor", StringComparison.Ordinal))
        {
            Assert.All(result.Values, Assert.Empty);
        }
        else if (selected[0] == "summary-a")
        {
            Assert.Equal(Enumerable.Range(0, 24).Select(i => $"source-{i:D5}"), result["summary-a"]);
        }
        else
        {
            Assert.All(selected, id => Assert.Equal([id.Replace("leaf-", "source-", StringComparison.Ordinal)], result[id]));
        }

        foreach (string sql in statements.Where(sql => sql.StartsWith("WITH RECURSIVE reach", StringComparison.Ordinal)))
        {
            await ExplainClosureAsync(connection, "original", sql, token);
        }

        return originalInstructions;
    }

    private async Task ExplainClosureAsync(SqliteConnection connection, string variant, string sql, CancellationToken token)
    {
        await using DbCommand explain = connection.CreateCommand();

        explain.CommandText = "EXPLAIN QUERY PLAN " + sql;

        await using DbDataReader reader = await explain.ExecuteReaderAsync(token);

        while (await reader.ReadAsync(token))
        {
            _output.WriteLine($"plan={variant} {reader.GetString(3)}");
        }
    }
}
