using System.Data;

using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>
/// The pinned <c>EXPLAIN QUERY PLAN</c> of the retention sweep's normalized identity predicates.
/// </summary>
/// <remarks>
/// Every identity comparison in both retention partials wraps the column in
/// <c>lower(replace(col, '-', ''))</c>, because a stored identity had two spellings and an exact
/// equality would have missed half of them. SQLite cannot answer a function-wrapped column from an
/// ordinary column index, so each of those predicates was a full table scan - once per candidate
/// Session in the planning pass, and again per candidate in the apply pass.
///
/// <para>Core schema version 6 declares one expression index per hot column, in the exact shape the
/// predicate has. The plan is pinned whole rather than searched for a word: <c>SEARCH</c> with the
/// index named is the only output that proves the index was chosen, and a predicate that drifted out
/// of the index's shape would fall back to <c>SCAN</c> and red here.</para>
///
/// <para>The predicates are stated here rather than reassembled from the service, which
/// <c>DataRetentionEntriesFtsQueryPlanTests</c> warns against - so the last case below scans the
/// production files and fails if the text this suite explains is no longer the text the sweep
/// executes. That keeps the pin honest without opening a file this change does not own.</para>
/// </remarks>
[Collection("Grimoire")]

[Trait("Category", "Integration")]

public sealed class DataRetentionNormalizedIdentityQueryPlanTests : IAsyncLifetime
{
    private const string EntryEmbeddingsPredicate = "lower(replace(EntryId, '-', '')) = @id";

    private const string EntriesPredicate = "lower(replace(entry.SessionId, '-', '')) = @id";

    private const string AttachmentSessionPredicate = "lower(replace(attachment.SessionId, '-', '')) = @id";

    private const string AttachmentIdentityPredicate = "lower(replace(attachment.Id, '-', '')) = @id";

    private const string SessionKeyPredicate = "lower(replace(SessionId, '-', '')) = @id";

    private const string InferenceRunSessionPredicate = "lower(replace(run.SessionId, '-', '')) = @id";

    /// <summary>The three small session-keyed tables a Session delete clears by its normalized identity.</summary>
    private static readonly string[] SessionKeyedTables =
        ["attachment_memory_consultations", "saga_extraction_watermarks", "SessionContextPins"];

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public DataRetentionNormalizedIdentityQueryPlanTests(GrimoireFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

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

    public async Task The_entry_embedding_delete_is_answered_by_an_expression_index()
    {
        RequireSqlCipher();

        Assert.Equal(
            "SEARCH entry_embeddings USING INDEX IX_entry_embeddings_EntryId_Norm (<expr>=?)",
            await ExplainAsync($"SELECT Dim FROM entry_embeddings WHERE {EntryEmbeddingsPredicate}"));
    }

    [SkippableFact]

    public async Task The_entry_candidate_scan_is_answered_by_an_expression_index()
    {
        RequireSqlCipher();

        Assert.Equal(
            "SEARCH entry USING INDEX IX_Entries_SessionId_Norm (<expr>=?)",
            await ExplainAsync($"SELECT entry.Content FROM \"Entries\" entry WHERE {EntriesPredicate}"));
    }

    [SkippableFact]

    public async Task The_attachment_candidate_scans_are_answered_by_expression_indexes()
    {
        RequireSqlCipher();

        Assert.Equal(
            "SEARCH attachment USING INDEX IX_SessionAttachments_SessionId_Norm (<expr>=?)",
            await ExplainAsync(
                $"SELECT attachment.State FROM \"SessionAttachments\" attachment WHERE {AttachmentSessionPredicate}"));

        Assert.Equal(
            "SEARCH attachment USING INDEX IX_SessionAttachments_Id_Norm (<expr>=?)",
            await ExplainAsync(
                $"SELECT attachment.State FROM \"SessionAttachments\" attachment WHERE {AttachmentIdentityPredicate}"));
    }

    /// <summary>
    /// A Session delete's per-Session deletes and reconciliation counts over the three small
    /// session-keyed tables are answered by expression indexes, not by a scan of each table.
    /// </summary>
    /// <remarks>
    /// Core schema version 14 declares these three in the predicate's exact shape. Each is explained as
    /// the <c>DELETE</c> the Session delete runs; the post-commit counts use the same predicate.
    /// </remarks>
    [SkippableTheory]
    [InlineData("attachment_memory_consultations", "IX_attachment_memory_consultations_SessionId_Norm")]
    [InlineData("saga_extraction_watermarks", "IX_saga_extraction_watermarks_SessionId_Norm")]
    [InlineData("SessionContextPins", "IX_SessionContextPins_SessionId_Norm")]
    public async Task The_session_keyed_deletes_are_answered_by_expression_indexes(string table, string index)
    {
        RequireSqlCipher();

        Assert.Equal(
            $"SEARCH {table} USING INDEX {index} (<expr>=?)",
            await ExplainAsync(SessionKeyedDelete(table)));
    }

    /// <summary>
    /// The inference-run lookups by Session are answered by an expression index, not by a scan of the run ledger.
    /// </summary>
    /// <remarks>
    /// <c>InferenceRuns.SessionId</c> is written dash-free by <c>TurnRunWriter</c> while <c>Sessions.Id</c> is
    /// the dashed spelling, so every retention comparison between them wraps both sides in
    /// <c>lower(replace(col, '-', ''))</c>. Core schema version 15 declares the expression index in that exact
    /// shape; the run ledger grows with every turn, so a scan per candidate Session is the expensive one.
    /// </remarks>
    [SkippableFact]

    public async Task The_inference_run_session_lookup_is_answered_by_an_expression_index()
    {
        RequireSqlCipher();

        Assert.Equal(
            "SEARCH run USING INDEX IX_InferenceRuns_SessionId_Norm (<expr>=?)",
            await ExplainAsync($"SELECT run.Id FROM \"InferenceRuns\" run WHERE {InferenceRunSessionPredicate}"));
    }

    /// <summary>
    /// The predicates pinned above are still the text the sweep executes.
    /// </summary>
    /// <remarks>
    /// A plan test that explained SQL of its own would keep passing after the real predicate quietly
    /// changed shape and stopped matching the index - the failure this whole suite exists to catch. The
    /// retention partials are not this change's to edit, so the tie between the two is a source scan
    /// rather than a shared constant.
    ///
    /// <para>The session-keyed deletes are tied by their whole statement, table and all. Their predicate
    /// alone, <c>lower(replace(SessionId, '-', ''))</c>, is also carried by the Entry and attachment
    /// statements, so a needle that short stayed green after one of the three deletes stopped using the
    /// indexed shape.</para>
    /// </remarks>
    [Fact]
    public void The_explained_predicates_are_the_ones_the_sweep_executes()
    {
        foreach (string predicate in
            (string[])[EntryEmbeddingsPredicate, EntriesPredicate, AttachmentSessionPredicate, AttachmentIdentityPredicate, InferenceRunSessionPredicate])
        {
            string needle = predicate.Replace(" = @id", string.Empty, StringComparison.Ordinal);

            Assert.True(
                ProductionSourceInventory.Sources().Any(source => source.Names(needle)),
                $"No production source carries '{needle}', so the plan pinned here explains a predicate "
                + "the retention sweep no longer executes.");
        }

        foreach (string table in SessionKeyedTables)
        {
            string statement = SessionKeyedDelete(table);

            Assert.True(
                ProductionSourceInventory.Sources().Any(source => source.Names(statement)),
                $"No production source carries '{statement}', so the plan pinned here explains a delete "
                + "the Session delete no longer executes.");
        }
    }

    /// <summary>The Session delete's statement for one session-keyed table, exactly as it is executed.</summary>
    private static string SessionKeyedDelete(string table) => $"DELETE FROM {table} WHERE {SessionKeyPredicate}";

    private async Task<string> ExplainAsync(string sql)
    {
        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        if (connection.State is not ConnectionState.Open)
        {
            await connection.OpenAsync(CancellationToken.None);
        }

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "EXPLAIN QUERY PLAN " + sql;

        _ = command.Parameters.AddWithValue("@id", "00000000000000000000000000000000");

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
