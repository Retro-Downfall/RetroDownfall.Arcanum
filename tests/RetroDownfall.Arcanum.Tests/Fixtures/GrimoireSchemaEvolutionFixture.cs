using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>
/// A bounded test backfill over the two synthetic tables the evolution chains own.
/// </summary>
internal sealed class TestBackfill(string name, int maxRowsPerBatch = 2) : IGrimoireSchemaBackfill
{

    public string Name { get; } = name;

    public int MaxRowsPerBatch { get; } = maxRowsPerBatch;

    /// <summary>Set to a batch ordinal to make that batch throw, for the interruption cases.</summary>
    internal int? ThrowOnBatch { get; set; }

    /// <summary>Set above <see cref="MaxRowsPerBatch"/> to make a batch break its own bound.</summary>
    internal int? OverrunToRows { get; set; }

    internal int BatchesRun { get; private set; }

    public async Task<GrimoireSchemaBackfillBatch> AdvanceBatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? cursor,
        CancellationToken cancellationToken)
    {

        BatchesRun++;

        if (ThrowOnBatch == BatchesRun)
        {

            throw new InvalidOperationException("The test backfill was asked to fail this batch.");

        }

        long after = cursor is null ? 0 : long.Parse(cursor, CultureInfo.InvariantCulture);

        List<(long Id, string Value)> rows = [];

        await using (SqliteCommand read = connection.CreateCommand())
        {

            read.Transaction = transaction;

            read.CommandText = "SELECT Id, Value FROM evolution_source WHERE Id > $after ORDER BY Id LIMIT $limit;";

            _ = read.Parameters.AddWithValue("$after", after);

            _ = read.Parameters.AddWithValue("$limit", OverrunToRows ?? MaxRowsPerBatch);

            await using SqliteDataReader reader =
                await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                rows.Add((reader.GetInt64(0), reader.GetString(1)));

            }

        }

        foreach ((long id, string value) in rows)
        {

            await using SqliteCommand write = connection.CreateCommand();

            write.Transaction = transaction;

            // Idempotent on purpose: a batch re-run from the last committed cursor must produce the
            // same durable effect, because a crash between the work and its commit is
            // indistinguishable from the batch never having run.
            write.CommandText = """
                INSERT INTO evolution_target (Id, Value) VALUES ($id, $value)
                ON CONFLICT (Id) DO UPDATE SET Value = excluded.Value;
                """;

            _ = write.Parameters.AddWithValue("$id", id);

            _ = write.Parameters.AddWithValue("$value", value);

            _ = await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        }

        return rows.Count == 0
            ? new GrimoireSchemaBackfillBatch(cursor, 0, IsComplete: true)
            : new GrimoireSchemaBackfillBatch(
                rows[^1].Id.ToString(CultureInfo.InvariantCulture),
                rows.Count,
                IsComplete: rows.Count < (OverrunToRows ?? MaxRowsPerBatch));

    }

}

/// <summary>
/// The synthetic chains every evolution suite drives through the production installer.
/// </summary>
internal static class GrimoireSchemaEvolutionFixture
{

    /// <summary>
    /// A pinned value standing in for what a version-1 tree published; 64 characters, as a real pin
    /// is.
    /// </summary>
    internal const string VersionOneFingerprint =
        "1111111111111111111111111111111111111111111111111111111111111111";

    internal static GrimoireSchemaTransitionStatement Statement(int ordinal, string name, string sql) =>
        new($"Transitions.V2.{ordinal:D3}_{name}", ordinal, name, sql);

    internal static GrimoireSchemaVersionStep Step(
        int fromVersion,
        int toVersion,
        IReadOnlyList<GrimoireSchemaTransitionStatement>? statements = null,
        IGrimoireSchemaBackfill? backfill = null) =>
        new(
            GrimoireSchemaFamily.Core,
            GrimoireSchemaTransactionTier.Core,
            fromVersion,
            toVersion,
            VersionOneFingerprint,
            statements
                ?? [Statement(10, "noop", "CREATE TABLE IF NOT EXISTS evolution_noop (Id INTEGER PRIMARY KEY);")],
            backfill);

    /// <summary>
    /// A chain for constructor-validation tests only.
    /// </summary>
    /// <remarks>
    /// Its head manifest is the shipped Core one, which does not declare the objects its steps
    /// create, so it must never be installed: the finalization inspection would report them as
    /// unexpected. Installable chains live beside this one and include every object their steps
    /// create in their head manifest.
    /// </remarks>
    internal static GrimoireSchemaVersionChain ChainWithSteps(
        int headVersion,
        params GrimoireSchemaVersionStep[] steps) =>
        new(
            GrimoireSchemaManifestBuilder.Build(
                GrimoireSchemaFamily.Core,
                GrimoireSchemaTransactionTier.Core,
                headVersion,
                GrimoireSchemaCatalog.CoreSchemaFingerprint,
                GrimoireSchemaCatalog.CoreObjects),
            GrimoireSchemaCatalog.CoreObjects,
            steps);

    internal static GrimoireSchemaVersionChain TwoVersionChain() =>
        ChainWithSteps(2, Step(1, 2));

    /// <summary>What the version-2 head tree publishes; distinct from the version-1 pin, as a real one is.</summary>
    internal const string VersionTwoFingerprint =
        "3333333333333333333333333333333333333333333333333333333333333333";

    internal const string SourceTableSql =
        "CREATE TABLE IF NOT EXISTS evolution_source (Id INTEGER PRIMARY KEY, Value TEXT NOT NULL);";

    internal const string TargetTableSql =
        "CREATE TABLE IF NOT EXISTS evolution_target (Id INTEGER PRIMARY KEY, Value TEXT NOT NULL);";

    /// <summary>
    /// The same object as a <b>step</b> statement, deliberately without <c>IF NOT EXISTS</c>.
    /// </summary>
    /// <remarks>
    /// A step's statements commit with the journal write that records the step, so nothing re-runs a
    /// committed step and a step statement is free to be non-idempotent - which every real one is,
    /// because <c>ALTER TABLE ... ADD COLUMN</c> has no idempotent form. Making the fixture's
    /// statement non-idempotent is what lets a test see a resume that wrongly re-executes committed
    /// DDL; an <c>IF NOT EXISTS</c> statement would swallow that bug and pass.
    /// </remarks>
    internal const string TargetTableStepSql =
        "CREATE TABLE evolution_target (Id INTEGER PRIMARY KEY, Value TEXT NOT NULL);";

    /// <summary>
    /// Version 1 of the evolution tier: every shipped Core object plus <c>evolution_source</c>.
    /// </summary>
    /// <remarks>
    /// An installable chain's head manifest must declare every object its steps create, or the
    /// finalization inspection reports them as unexpected. That is why these chains are separate from
    /// <see cref="ChainWithSteps"/>, whose head manifest is the shipped Core one and which therefore
    /// exists only to be constructed, never installed.
    /// </remarks>
    private static IReadOnlyList<GrimoireSchemaObject> VersionOneObjects =>
        [.. GrimoireSchemaCatalog.CoreObjects, SyntheticObject("evolution_source", SourceTableSql)];

    private static IReadOnlyList<GrimoireSchemaObject> VersionTwoObjects =>
        [.. VersionOneObjects, SyntheticObject("evolution_target", TargetTableSql)];

    private static GrimoireSchemaObject SyntheticObject(string name, string sql) =>
        new(
            GrimoireSchemaFamily.Core,
            GrimoireSchemaTransactionTier.Core,
            GrimoireSchemaCategory.Tables,
            name,
            $"Tables.{name}",
            sql);

    /// <summary>An installable version-1 chain: no step, head is the Core tree plus one synthetic table.</summary>
    internal static GrimoireSchemaVersionChainSet OneVersionChainSet() =>
        ChainSet(CoreChain(1, VersionOneFingerprint, VersionOneObjects));

    /// <summary>
    /// An installable version-2 chain whose one step creates <c>evolution_target</c>, optionally
    /// depending on a sweep before version 2 may be recorded.
    /// </summary>
    internal static GrimoireSchemaVersionChainSet TwoVersionChainSet(IGrimoireSchemaBackfill? backfill = null) =>
        TwoVersionChainSet(VersionOneFingerprint, backfill);

    /// <summary>
    /// The same chain with a chosen pin for the version its step leaves, for the case where an
    /// installation's recorded version 1 is not the version 1 this chain knows by that number.
    /// </summary>
    internal static GrimoireSchemaVersionChainSet TwoVersionChainSet(
        string versionOnePin,
        IGrimoireSchemaBackfill? backfill = null) =>
        ChainSet(
            CoreChain(
                2,
                VersionTwoFingerprint,
                VersionTwoObjects,
                new GrimoireSchemaVersionStep(
                    GrimoireSchemaFamily.Core,
                    GrimoireSchemaTransactionTier.Core,
                    1,
                    2,
                    versionOnePin,
                    [Statement(10, "add_evolution_target", TargetTableStepSql)],
                    backfill)));

    private static GrimoireSchemaVersionChain CoreChain(
        int headVersion,
        string headFingerprint,
        IReadOnlyList<GrimoireSchemaObject> headObjects,
        params GrimoireSchemaVersionStep[] steps) =>
        new(
            GrimoireSchemaManifestBuilder.Build(
                GrimoireSchemaFamily.Core,
                GrimoireSchemaTransactionTier.Core,
                headVersion,
                headFingerprint,
                headObjects),
            headObjects,
            steps);

    private static GrimoireSchemaVersionChainSet ChainSet(GrimoireSchemaVersionChain core) =>
        new(
        [
            core,
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
        ]);

}

/// <summary>
/// One file-backed scratch Grimoire an evolution suite can close and reopen, because evolution is
/// about what a <i>second</i> run of the installer does to a database the first one left behind.
/// </summary>
internal sealed class EvolutionScratchDatabase : IDisposable
{

    private readonly string _directory;

    private EvolutionScratchDatabase(string directory, string connectionString)
    {

        _directory = directory;

        ConnectionString = connectionString;

    }

    internal string ConnectionString { get; }

    internal static EvolutionScratchDatabase Create()
    {

        string directory = Directory
            .CreateDirectory(Path.Combine(Path.GetTempPath(), $"grimoire-evolution-{Guid.NewGuid():N}"))
            .FullName;

        return new EvolutionScratchDatabase(
            directory,
            new SqliteConnectionStringBuilder
            {

                DataSource = Path.Combine(directory, "arcanum.db"),

                // Pooling would hand a later connection the same native handle with whatever state
                // the previous test left on it, and these suites are entirely about what one run of
                // the installer leaves for the next.
                Pooling = false,

            }.ToString());

    }

    internal Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken) =>
        GrimoireSchemaTestInstaller.OpenAsync(ConnectionString, cancellationToken);

    public void Dispose()
    {

        try
        {

            Directory.Delete(_directory, recursive: true);

        }
        catch (IOException)
        {

            // A scratch directory under the OS temp root that outlives one test is the operating
            // system's problem, not a test failure.

        }

    }

}
