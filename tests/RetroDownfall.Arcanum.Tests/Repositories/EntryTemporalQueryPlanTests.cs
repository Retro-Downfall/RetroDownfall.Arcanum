using System.Data.Common;

using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Storage.Entities;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Repositories;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Repositories;

/// <summary>
/// The pinned <c>EXPLAIN QUERY PLAN</c> of every transcript read.
/// </summary>
/// <remarks>
/// <b>This is the test that keeps the reason for the identity settlement paid.</b> Nine defects in one
/// family were fixed by normalising the comparison — <c>lower(replace("SessionId", '-', '')) = @id</c>
/// — and each fix was correct and each cost an index. The tenth landed that shape here, on the
/// conversation read path, which runs once per turn for every user against the largest table in the
/// database. A normalised column cannot use a BINARY-collated index, so every <c>SessionId</c>-led
/// index these reads reach went unused at once. Nothing else in the suite would notice if a later
/// change wrapped that column in a function again: the reads would still return the right rows and
/// still pass every behavioural test in this repository, while scanning. That is not a guess — the
/// normalised shape shipped, and the whole suite was green over it.
///
/// <para><b>The plan is pinned whole and compared whole. Nothing about it is classified, and that is
/// the entire design.</b> Three earlier versions of this gate each judged the plan by reasoning about
/// a name in it, and two of the three were defeated by a name that turned out to live in a different
/// namespace than the version assumed. The first searched the plan text for the table's name and was
/// blind to the two reads that alias it. The second read a derived object's name off a
/// <c>MATERIALIZE</c> row and exempted access rows carrying that name — but SQLite prints the CTE's
/// name on one row and the alias in scope on the other, so renaming the boundary CTE to <c>e</c>
/// exempted the aliased full walk of the real table, and the CTE beside it satisfied every remaining
/// assertion. Both defeats have the same shape, and a fourth naming scheme would have a fourth blind
/// spot. So this version decides nothing about any row: it asserts that the plan is the pinned plan,
/// which cannot misclassify a row because it does not classify one.</para>
///
/// <para><b>The one thing not pinned is the identifier, and it is the one thing that has been
/// ambiguous every time.</b> Each <c>SCAN</c>, <c>SEARCH</c>, <c>MATERIALIZE</c> and
/// <c>CO-ROUTINE</c> row has its object token replaced by <c>&lt;object&gt;</c> before comparison, so
/// renaming a CTE or an alias — a change that alters no plan and no cost — leaves this green. It
/// cannot hide a regression, because a regression is a change of verb, of index, or of which rows the
/// plan has, and all three are pinned verbatim. And the elision fails safe: a row whose shape the
/// pattern does not recognise is pinned including its identifier, so an unfamiliar plan reds rather
/// than slipping through.</para>
///
/// <para><b>What this costs, accepted rather than overlooked.</b> Any change to these statements that
/// moves a plan reds this file and puts a person in front of the difference — a regression, an
/// improvement, and a SQLite upgrade alike. For a performance contract that is the behaviour worth
/// having rather than a cost, and this repository pins its SQLite build, so plan churn is a controlled
/// event and a plan that moved under an engine bump is exactly what somebody should read.</para>
///
/// <para>The plan is taken from the statement <see cref="EntryTemporalQueries"/> actually issues, as
/// the context's own query pipeline renders it, rather than reassembled here. A plan test that
/// explained SQL of its own would keep passing after the real query quietly lost its index, which is
/// the whole failure this file exists to catch.</para>
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
    /// Every read in <see cref="EntryTemporalQueries"/> plans exactly as pinned.
    /// </summary>
    /// <remarks>
    /// One case per production entry point, named, with the arguments its caller supplies — so a read
    /// that regains its rows by losing its index is reported by name rather than folded into a sweep.
    /// <c>LoadRecentDescending</c> is the one the conversation loop makes on every turn and is the
    /// reason the rest of this family exists.
    /// </remarks>
    [SkippableTheory]
    [InlineData("LoadRecentDescending")]
    [InlineData("LoadAfterSequence")]
    [InlineData("LoadBeforeSequence")]
    [InlineData("LoadBeforeDeletedKeyset")]
    [InlineData("SequenceOf")]
    [InlineData("LoadDescendingPaged")]
    [InlineData("CountAfter")]
    [InlineData("LoadAfterWatermarkThroughTimestampGroup")]
    [InlineData("CountAfterWatermarkThroughTimestampGroup")]
    public async Task Every_transcript_read_plans_exactly_as_pinned(string read)
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string plan = await ExplainAsync(db => Query(db, read));

        Assert.Equal(Pinned(read), plan);

    }

    /// <summary>
    /// The plan each read is expected to produce, with identifiers elided.
    /// </summary>
    /// <remarks>
    /// Seven of the nine are one row: a seek into a <c>SessionId</c>-led index, with the ordering
    /// served by the same index where there is one. <c>SequenceOf</c> seeks the <c>"Entries"</c>
    /// identity index instead, because it filters on <c>"Id"</c> as well and a unique seek returns at
    /// most one row — which is the seek only an exact comparison on that column can produce.
    ///
    /// <para>The two watermark reads are the long ones, and two things in them are worth reading off
    /// the pinned text rather than assumed. Their boundary CTE is materialised and their <c>Selected</c>
    /// CTE is not: there is exactly one <c>MATERIALIZE</c> row in each plan. And both carry a temporary
    /// B-tree — the boundary orders by <c>("CreatedAt", "Id")</c> where the index supplies only the
    /// first term, and the load then orders its window by <c>"Sequence"</c> after the planner has
    /// chosen the <c>CreatedAt</c> index to serve the range filter. Those sorts are part of what is
    /// pinned, so they cannot grow or move unremarked either.</para>
    /// </remarks>
    private static string Pinned(string read) =>
        read switch
        {
            "LoadRecentDescending" =>
                "SEARCH <object> USING INDEX IX_Entries_SessionId_Sequence (SessionId=?)\n",

            "LoadAfterSequence" =>
                "SEARCH <object> USING INDEX IX_Entries_SessionId_Sequence (SessionId=? AND Sequence>?)\n",

            "LoadBeforeSequence" =>
                "SEARCH <object> USING INDEX IX_Entries_SessionId_Sequence (SessionId=? AND Sequence<?)\n",

            "LoadBeforeDeletedKeyset" =>
                "SEARCH <object> USING INDEX IX_Entries_SessionId_Sequence (SessionId=?)\n",

            "SequenceOf" =>
                "SEARCH <object> USING INDEX sqlite_autoindex_Entries_1 (Id=?)\n",

            "LoadDescendingPaged" =>
                "SEARCH <object> USING INDEX IX_Entries_SessionId_Sequence (SessionId=?)\n",

            "CountAfter" =>
                "SEARCH <object> USING COVERING INDEX IX_Entries_SessionId_CreatedAt (SessionId=? AND CreatedAt>?)\n",

            "LoadAfterWatermarkThroughTimestampGroup" =>
                """
                SEARCH <object> USING INDEX IX_Entries_SessionId_CreatedAt (SessionId=? AND CreatedAt>?)
                SCALAR SUBQUERY 2
                MATERIALIZE <object>
                SEARCH <object> USING INDEX IX_Entries_SessionId_CreatedAt (SessionId=? AND CreatedAt>?)
                USE TEMP B-TREE FOR LAST TERM OF ORDER BY
                SCAN <object>
                SCALAR SUBQUERY 3
                SCAN <object>
                SCALAR SUBQUERY 5
                SEARCH <object> USING COVERING INDEX IX_Entries_SessionId_CreatedAt (SessionId=? AND CreatedAt>?)
                SCALAR SUBQUERY 2
                SCAN <object>
                SCALAR SUBQUERY 3
                SCAN <object>
                USE TEMP B-TREE FOR ORDER BY

                """,

            "CountAfterWatermarkThroughTimestampGroup" =>
                """
                SEARCH <object> USING COVERING INDEX IX_Entries_SessionId_CreatedAt (SessionId=? AND CreatedAt>?)
                SCALAR SUBQUERY 2
                MATERIALIZE <object>
                SEARCH <object> USING INDEX IX_Entries_SessionId_CreatedAt (SessionId=? AND CreatedAt>?)
                USE TEMP B-TREE FOR LAST TERM OF ORDER BY
                SCAN <object>
                SCALAR SUBQUERY 3
                SCAN <object>

                """,

            _ => throw new ArgumentOutOfRangeException(nameof(read), read, "No plan is pinned for that read."),
        };

    private static IQueryable Query(ArcanumDbContext db, string read)
    {

        Guid sessionId = Guid.NewGuid();

        DateTimeOffset watermark = DateTimeOffset.UtcNow.AddHours(-1);

        return read switch
        {
            "LoadRecentDescending" => EntryTemporalQueries.LoadRecentDescending(db, sessionId, 50),

            "LoadAfterSequence" => EntryTemporalQueries.LoadAfterSequence(db, sessionId, 0, 50),

            "LoadBeforeSequence" => EntryTemporalQueries.LoadBeforeSequence(db, sessionId, 100, 50),

            "LoadBeforeDeletedKeyset" =>
                EntryTemporalQueries.LoadBeforeDeletedKeyset(db, sessionId, watermark, Guid.NewGuid(), 50),

            "SequenceOf" => EntryTemporalQueries.SequenceOf(db, sessionId, Guid.NewGuid()),

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
    /// A plan row that names the object it reaches, introduces, or materialises.
    /// </summary>
    /// <remarks>
    /// The object token is the only part of a plan this file does not pin, so this pattern is the whole
    /// of what it forgives. It deliberately matches nothing else: a row it does not recognise keeps its
    /// text unchanged and is compared verbatim, which makes an unfamiliar plan shape red rather than
    /// pass.
    /// </remarks>
    private static readonly Regex NamedObject =
        new("^(?<head>SCAN|SEARCH|MATERIALIZE|CO-ROUTINE)\\s+(?<object>\\S+)");

    /// <summary>
    /// The plan SQLite produces for the statement the query would have executed, identifiers elided.
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

            _ = plan.Append(Elide(reader.GetString(reader.FieldCount - 1).Trim())).Append('\n');

        }

        return plan.ToString();

    }

    private static string Elide(string row)
    {

        Match named = NamedObject.Match(row);

        if (!named.Success)
        {

            return row;

        }

        Group instance = named.Groups["object"];

        return named.Groups["head"].Value + " <object>" + row[(instance.Index + instance.Length)..];

    }

}
