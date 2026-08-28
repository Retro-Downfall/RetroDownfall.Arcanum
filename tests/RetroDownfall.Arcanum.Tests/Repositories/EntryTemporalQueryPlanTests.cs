using System.Data.Common;

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
/// <para>The assertion names the index and refuses a scan of <c>"Entries"</c>, rather than refusing the
/// word <c>SCAN</c> outright: SQLite writes <c>SCAN … USING COVERING INDEX …</c> for a full walk of an
/// index, which is a different thing from the table scan being ruled out here.</para>
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
    /// ordering the index also serves — so a plan naming this index proves both halves at once: the
    /// rows are found by seek rather than by scan, and they come back in <c>Sequence</c> order without
    /// a sort.
    /// </remarks>
    [SkippableFact]
    public async Task The_recent_transcript_window_seeks_its_session_index()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string plan = await ExplainAsync(
            db => EntryTemporalQueries.LoadRecentDescending(db, Guid.NewGuid(), 50));

        Assert.Contains("IX_Entries_SessionId_Sequence", plan, StringComparison.Ordinal);

        Assert.DoesNotContain("SCAN \"Entries\"", plan, StringComparison.Ordinal);

        Assert.DoesNotContain("SCAN Entries", plan, StringComparison.Ordinal);

        // A sort would mean the index was reached for the filter but not for the order, which is half
        // the regression and would otherwise pass the assertion above.
        Assert.DoesNotContain("TEMP B-TREE", plan, StringComparison.Ordinal);

    }

    /// <summary>
    /// Every other read in the file seeks a <c>SessionId</c>-led index too.
    /// </summary>
    /// <remarks>
    /// Named individually rather than swept, because a theory over a list this file also owns would
    /// pass by shrinking. Each case is one production entry point with the arguments its caller
    /// supplies, so a read that regains its rows by losing its index is reported by name.
    ///
    /// <para><c>SequenceOf</c> is absent because it filters on both identity columns and SQLite
    /// resolves it through the stronger of the two; it has its own case below, for the seek that only
    /// an exact comparison on <c>"Entries"."Id"</c> can produce.</para>
    /// </remarks>
    [SkippableTheory]
    [InlineData("LoadAfterSequence")]
    [InlineData("LoadBeforeSequence")]
    [InlineData("LoadBeforeDeletedKeyset")]
    [InlineData("LoadDescendingPaged")]
    [InlineData("CountAfter")]
    [InlineData("LoadAfterWatermarkThroughTimestampGroup")]
    [InlineData("CountAfterWatermarkThroughTimestampGroup")]
    public async Task Every_transcript_read_seeks_a_session_index(string read)
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string plan = await ExplainAsync(db => Query(db, read));

        Assert.Contains("IX_Entries_SessionId", plan, StringComparison.Ordinal);

        Assert.DoesNotContain("SCAN \"Entries\"", plan, StringComparison.Ordinal);

        Assert.DoesNotContain("SCAN Entries", plan, StringComparison.Ordinal);

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

        Assert.Contains("SEARCH Entries USING INDEX", plan, StringComparison.Ordinal);

        Assert.DoesNotContain("SCAN \"Entries\"", plan, StringComparison.Ordinal);

        Assert.DoesNotContain("SCAN Entries", plan, StringComparison.Ordinal);

    }

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
