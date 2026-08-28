using System.Data.Common;

using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Storage.Entities;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Repositories;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Repositories;

/// <summary>
/// <c>EXPLAIN QUERY PLAN</c> evidence that the transcript reads seek a <c>SessionId</c> index.
/// </summary>
/// <remarks>
/// <b>This is the test that keeps the reason for the identity settlement paid.</b> Nine defects in one
/// family were fixed by normalising the comparison — <c>lower(replace("SessionId", '-', '')) = @id</c>
/// — and each fix was correct and each cost an index. The tenth landed that shape here, on the
/// conversation read path, which runs once per turn for every user against the largest table in the
/// database. A normalised column cannot use a BINARY-collated index, so every <c>SessionId</c>-led
/// index these reads reach went unused at once. Settling every stored identity on one form is what let
/// the comparison become exact again, and nothing else in the suite would notice if a later change
/// wrapped that column in a function once more: the reads would still return the right rows, and would
/// still pass every behavioural test in this repository, while scanning. That is not a guess — the
/// normalised shape shipped, and the whole suite was green over it.
///
/// <para>The plan is taken from the statement <see cref="EntryTemporalQueries"/> actually issues,
/// as the context's own query pipeline renders it, rather than reassembled here. A plan test that
/// explained SQL of its own would keep passing after the real query quietly lost its index, which is
/// the whole failure this file exists to catch.</para>
///
/// <para><b>The plan is judged row by row, and nothing is keyed on the table's name.</b> An earlier
/// draft searched the whole plan text for <c>SCAN Entries</c>, and that was blind for two of these
/// nine reads. Both wrap the table twice — unaliased inside a boundary CTE, and as <c>e</c> in the
/// outer query — so normalising the outer predicate makes it walk the table under a name that needle
/// does not contain, while the CTE beside it goes on seeking and satisfies the index assertion by
/// itself. Both plans were measured in that state: neither contains <c>SCAN Entries</c> and both
/// contain <c>IX_Entries_SessionId</c>, so that draft passed over both regressions.</para>
///
/// <para>So instead: every row that reaches a stored object must be a <c>SEARCH</c> naming the
/// expected index, whatever alias it wears, and every such row is judged rather than one of them. The
/// only rows exempt are those reaching an object the plan itself declares derived, through a
/// <c>MATERIALIZE</c> or <c>CO-ROUTINE</c> row — read out of the plan under judgement rather than
/// listed here, so a renamed CTE cannot quietly widen the exemption.</para>
///
/// <para><c>SCAN … USING COVERING INDEX …</c> fails that rule and is meant to. It walks an index
/// instead of the table, which is cheaper than a table scan and still a walk of every row: it is
/// exactly what <c>CountAfterWatermarkThroughTimestampGroup</c> degrades to when its predicate is
/// normalised, so treating it as an allowance would have been the blind spot rather than a
/// concession.</para>
/// </remarks>
[Collection("Grimoire")]
public sealed class EntryTemporalQueryPlanTests : IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public EntryTemporalQueryPlanTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    private static CancellationToken Token => CancellationToken.None;

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

            await _db.DisposeAsync();

        }

        if (File.Exists(_dbPath))
        {

            File.Delete(_dbPath);

        }

    }

    /// <summary>
    /// The per-turn transcript window seeks <c>IX_Entries_SessionId_Sequence</c>.
    /// </summary>
    /// <remarks>
    /// The most-recent window is the read the conversation loop makes on every turn, and the one whose
    /// ordering the same index serves — so both halves are worth proving separately here: the rows are
    /// found by seek rather than by walk, and they come back in <c>Sequence</c> order without a sort.
    /// </remarks>
    [SkippableFact]
    public async Task The_recent_transcript_window_seeks_its_session_index()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string plan = await ExplainAsync(
            db => EntryTemporalQueries.LoadRecentDescending(db, Guid.NewGuid(), 50));

        AssertEveryStoredRowIsReachedBySeek(plan, "IX_Entries_SessionId_Sequence");

        // A sort would mean the index was reached for the filter but not for the order, which is half
        // the regression and would otherwise pass the assertion above.
        Assert.DoesNotContain("TEMP B-TREE", plan, StringComparison.Ordinal);

    }

    /// <summary>
    /// The single-statement reads seek a <c>SessionId</c>-led index and never sort.
    /// </summary>
    /// <remarks>
    /// Named individually rather than swept, because a theory over a list this file also owns would
    /// pass by shrinking. Each case is one production entry point with the arguments its caller
    /// supplies, so a read that regains its rows by losing its index is reported by name.
    ///
    /// <para>These five have no subquery and one table reference, so a sort in the plan can only mean
    /// the index served the filter and not the order — half the regression, and worth refusing here.
    /// The two watermark reads cannot carry that assertion and have their own case below.
    /// <c>SequenceOf</c> is absent because it filters on both identity columns and SQLite resolves it
    /// through the stronger of the two.</para>
    /// </remarks>
    [SkippableTheory]
    [InlineData("LoadAfterSequence")]
    [InlineData("LoadBeforeSequence")]
    [InlineData("LoadBeforeDeletedKeyset")]
    [InlineData("LoadDescendingPaged")]
    [InlineData("CountAfter")]
    public async Task Every_transcript_read_seeks_a_session_index(string read)
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string plan = await ExplainAsync(db => Query(db, read));

        AssertEveryStoredRowIsReachedBySeek(plan, "IX_Entries_SessionId");

        Assert.DoesNotContain("TEMP B-TREE", plan, StringComparison.Ordinal);

    }

    /// <summary>
    /// The two watermark reads seek their index through the alias they give the table.
    /// </summary>
    /// <remarks>
    /// <b>These are the two the first version of this gate could not see, and they are the reason it
    /// judges rows rather than text.</b> Both wrap the table twice — once unaliased inside the boundary
    /// CTE and once as <c>e</c> in the outer query — so a normalised predicate leaves the CTE seeking
    /// its index while the outer query walks the whole table under a name no <c>SCAN Entries</c> needle
    /// contains. Measured, the load degrades to <c>SCAN e</c> and the count to
    /// <c>SCAN e USING COVERING INDEX …</c>, and every assertion the earlier version made was satisfied
    /// by the CTE beside them.
    ///
    /// <para>No sort assertion, and the absence is the decision. Both statements legitimately sort: the
    /// boundary CTE orders by <c>("CreatedAt", "Id")</c> where the index supplies only the first term,
    /// and the load's outer query orders the selected window by <c>"Sequence"</c>, which no index over
    /// a materialised CTE can serve. Asserting no temporary B-tree here would fail on correct code, and
    /// a sort that has to be permitted proves nothing about the seek — which is what the row-by-row
    /// rule is for.</para>
    /// </remarks>
    [SkippableTheory]
    [InlineData("LoadAfterWatermarkThroughTimestampGroup")]
    [InlineData("CountAfterWatermarkThroughTimestampGroup")]
    public async Task Every_watermark_read_seeks_its_session_index_under_its_alias(string read)
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string plan = await ExplainAsync(db => Query(db, read));

        AssertEveryStoredRowIsReachedBySeek(plan, "IX_Entries_SessionId_CreatedAt");

    }

    /// <summary>
    /// The cursor resolution seeks the <c>"Entries"</c> identity index.
    /// </summary>
    /// <remarks>
    /// <c>SequenceOf</c> is the one read here that compares <c>"Entries"."Id"</c>, and SQLite resolves
    /// it through that column's unique index rather than through a <c>SessionId</c> index because a
    /// unique seek returns at most one row. Normalised, it could use neither: the plan was a scan of
    /// the table for a single row, and the caller then fell back to a keyset page it did not need.
    /// </remarks>
    [SkippableFact]
    public async Task The_cursor_resolution_seeks_the_entry_identity_index()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string plan = await ExplainAsync(db => EntryTemporalQueries.SequenceOf(db, Guid.NewGuid(), Guid.NewGuid()));

        AssertEveryStoredRowIsReachedBySeek(plan, "sqlite_autoindex_Entries_1");

    }

    /// <summary>
    /// Fails unless every row of the plan that reaches a stored object seeks <paramref name="index"/>.
    /// </summary>
    /// <remarks>
    /// The alias-proof half of this gate. A plan row names the object it reaches and the verb it
    /// reaches it with, and the verb is the whole judgement: <c>SEARCH</c> is a seek into an index,
    /// <c>SCAN</c> is a walk of every row — including <c>SCAN … USING COVERING INDEX …</c>, which walks
    /// an index rather than the table and is still a walk. The object's name is read but never
    /// compared against the table's, so a statement that aliases <c>"Entries"</c> as <c>e</c> is judged
    /// exactly as one that does not.
    ///
    /// <para>A row reaching a derived object is exempt, and the exemption is computed from the plan
    /// under judgement rather than listed here: a name SQLite introduces with <c>MATERIALIZE</c> or
    /// <c>CO-ROUTINE</c> is a temporary result the outer query is entitled to walk. Reading it out of
    /// the plan is what stops a renamed or newly added CTE from silently widening the exemption.</para>
    ///
    /// <para>Every qualifying row is judged rather than one, which is the other half. A statement here
    /// can reach the table twice, and the earlier version of this gate was satisfied by whichever of
    /// the two still seeked. The final count assertion covers the opposite failure: a statement that
    /// stopped reaching the table at all would otherwise pass with nothing to judge.</para>
    /// </remarks>
    private static void AssertEveryStoredRowIsReachedBySeek(string plan, string index)
    {

        string[] rows = plan.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        HashSet<string> derived = new(StringComparer.Ordinal);

        foreach (string row in rows)
        {

            Match introduced = DerivedObject.Match(row);

            if (introduced.Success)
            {

                _ = derived.Add(introduced.Groups["object"].Value);

            }

        }

        int seeks = 0;

        foreach (string row in rows)
        {

            Match access = ObjectAccess.Match(row);

            if (!access.Success || derived.Contains(access.Groups["object"].Value))
            {

                continue;

            }

            Assert.True(
                string.Equals(access.Groups["verb"].Value, "SEARCH", StringComparison.Ordinal),
                $"A plan row walks a stored object instead of seeking it: {row}{Plan(plan)}");

            Assert.True(
                row.Contains(index, StringComparison.Ordinal),
                $"A plan row seeks something other than {index}: {row}{Plan(plan)}");

            seeks++;

        }

        Assert.True(
            seeks > 0,
            $"No plan row reaches a stored object, so there was nothing to judge.{Plan(plan)}");

    }

    private static string Plan(string plan) =>
        System.Environment.NewLine + System.Environment.NewLine + "Whole plan:" + System.Environment.NewLine + plan;

    /// <summary>A plan row that reaches an object, whatever the object is called.</summary>
    private static readonly Regex ObjectAccess =
        new("^(?<verb>SCAN|SEARCH)\\s+(?<object>[A-Za-z_][A-Za-z0-9_]*)\\b");

    /// <summary>A plan row that introduces a derived object the rest of the plan may walk.</summary>
    private static readonly Regex DerivedObject =
        new("^(?:MATERIALIZE|CO-ROUTINE)\\s+(?<object>[A-Za-z_][A-Za-z0-9_]*)\\b");

    private static IQueryable Query(ArcanumDbContext db, string read)
    {

        Guid sessionId = Guid.NewGuid();

        DateTimeOffset watermark = DateTimeOffset.UtcNow.AddHours(-1);

        return read switch
        {
            "LoadAfterSequence" => EntryTemporalQueries.LoadAfterSequence(db, sessionId, 0, 50),

            "LoadBeforeSequence" => EntryTemporalQueries.LoadBeforeSequence(db, sessionId, 100, 50),

            "LoadBeforeDeletedKeyset" =>
                EntryTemporalQueries.LoadBeforeDeletedKeyset(db, sessionId, watermark, Guid.NewGuid(), 50),

            "LoadDescendingPaged" => EntryTemporalQueries.LoadDescendingPaged(db, sessionId, 50, 0),

            "CountAfter" => EntryTemporalQueries.CountAfter(db, sessionId, watermark),

            "LoadAfterWatermarkThroughTimestampGroup" =>
                EntryTemporalQueries.LoadAfterWatermarkThroughTimestampGroup(db, sessionId, watermark, 10, 200),

            "CountAfterWatermarkThroughTimestampGroup" =>
                EntryTemporalQueries.CountAfterWatermarkThroughTimestampGroup(db, sessionId, watermark, 10),

            _ => throw new ArgumentOutOfRangeException(nameof(read), read, "Unknown transcript read."),
        };

    }

    /// <summary>
    /// The plan SQLite produces for the statement the query would have executed.
    /// </summary>
    /// <remarks>
    /// The statement and its parameter values are taken from <c>ToQueryString</c>, which renders the
    /// command EF composed for this provider. Its leading <c>.param set</c> lines are the sqlite3
    /// shell's syntax for binding, not SQL, so they are turned back into real parameters here and the
    /// remainder is explained verbatim — the alternative, substituting the literals into the text,
    /// would explain a statement with no parameters in it and SQLite plans those differently.
    /// </remarks>
    private async Task<string> ExplainAsync(Func<ArcanumDbContext, IQueryable> query)
    {

        ArcanumDbContext db = _db ?? throw new InvalidOperationException("The context is not initialized.");

        string rendered = query(db).ToQueryString();

        DbConnection connection = db.Database.GetDbConnection();

        await using DbCommand command = connection.CreateCommand();

        List<string> statement = [];

        foreach (string line in rendered.Split('\n'))
        {

            string trimmed = line.TrimEnd('\r');

            if (!trimmed.StartsWith(".param set ", StringComparison.Ordinal))
            {

                statement.Add(trimmed);

                continue;

            }

            string[] parts = trimmed[".param set ".Length..].Split(' ', 2);

            DbParameter parameter = command.CreateParameter();

            parameter.ParameterName = parts[0];

            parameter.Value = parts[1].Trim('\'');

            command.Parameters.Add(parameter);

        }

        command.CommandText = "EXPLAIN QUERY PLAN " + string.Join('\n', statement);

        System.Text.StringBuilder plan = new();

        await using DbDataReader reader = await command.ExecuteReaderAsync(Token);

        while (await reader.ReadAsync(Token))
        {

            _ = plan.AppendLine(reader.GetString(reader.FieldCount - 1));

        }

        return plan.ToString();

    }

}
