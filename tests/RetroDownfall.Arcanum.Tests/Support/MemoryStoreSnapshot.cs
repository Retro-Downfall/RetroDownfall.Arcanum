using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>The memory stores a snapshot partitions its rows into, plus the ledger no single store owns.</summary>
[Flags]
internal enum MemoryStoreFamily
{
    Saga = 1,
    Lexicon = 2,
    Covenant = 4,
    Shared = 8,
}

/// <summary>How the rows of one discovered memory table are assigned to a family.</summary>
internal abstract record MemoryStorePartitionRule
{
    private MemoryStorePartitionRule()
    {
    }

    /// <summary>Every row of the table belongs to one family.</summary>
    internal sealed record Whole(MemoryStoreFamily Family) : MemoryStorePartitionRule;

    /// <summary>Each row belongs to the family its integer store or kind code names.</summary>
    internal sealed record ByCode(string Column, Func<long, MemoryStoreFamily> Family) : MemoryStorePartitionRule;

    /// <summary>Each row belongs to the family of the parent row its key column names.</summary>
    internal sealed record Through(string Column, string Parent, string ParentColumn) : MemoryStorePartitionRule;
}

/// <summary>
/// Every row of every memory table, rendered exactly and partitioned by the store it belongs to, read
/// inside one SQLite snapshot.
/// </summary>
/// <remarks>
/// <para>Discovery reads the live catalog rather than a list, so a memory table the schema gains is
/// compared the moment it exists. A table whose name says it is shared between stores (an Annals or an
/// erasure-evidence table) must have a rule here, or the capture refuses it by name: guessing its store
/// would let a write into the wrong store hide inside the family it was guessed into.</para>
///
/// <para>A full-text index is compared through its shadow tables, which hold its tokens; the virtual
/// table itself would only read back the content it indexes.</para>
///
/// <para>Rows are compared as rendered text, column by column in declaration order: integers and reals
/// in the invariant culture, text verbatim, blobs as hexadecimal, and SQL NULL as <c>∅</c>. A rowid
/// table is read in rowid order and a <c>WITHOUT ROWID</c> table in the order of all its columns, so two
/// captures of an unchanged table are sequence-equal.</para>
/// </remarks>
internal sealed record MemoryStoreSnapshot(
    IReadOnlyDictionary<MemoryStoreFamily, IReadOnlyList<string>> Rows,
    IReadOnlySet<string> Tables)
{
    private const string NullCell = "∅";

    private static readonly TimeSpan QuiesceTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan QuiescePoll = TimeSpan.FromMilliseconds(50);

    private static readonly MemoryStoreFamily[] Families =
    [
        MemoryStoreFamily.Saga,
        MemoryStoreFamily.Lexicon,
        MemoryStoreFamily.Covenant,
        MemoryStoreFamily.Shared,
    ];

    private static readonly string[] StorePrefixes = ["saga_", "lexicon_", "covenant_", "annal_", "memory_erasure_"];

    private static readonly string[] DisclosureTables =
    [
        "external_disclosure_receipts",
        "disclosure_subject_state",
        "external_disclosure_state",
        "disclosure_subject_aggregates",
    ];

    private static readonly Regex WithoutRowid = new(@"\)\s*WITHOUT\s+ROWID\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Whether the discovery rule selects a table, by name alone.</summary>
    internal static bool IsMemoryTable(string table) =>
        StorePrefixes.Any(prefix => table.StartsWith(prefix, StringComparison.Ordinal))
        || table == "artifact_sensitivity"
        || DisclosureTables.Contains(table, StringComparer.Ordinal);

    /// <summary>The partition rule for one discovered memory table.</summary>
    /// <exception cref="InvalidOperationException">
    /// The table is an Annals or erasure-evidence table with no rule, or not a memory table at all.
    /// </exception>
    internal static MemoryStorePartitionRule RuleFor(string table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return table switch
        {
            "annal_claims" => new MemoryStorePartitionRule.ByCode("SubjectStoreCode", AnnalFamily),
            "annal_heads" or "annal_review_events" or "annal_review_markers" =>
                new MemoryStorePartitionRule.ByCode("SubjectStoreCode", AnnalFamily),
            "annal_versions" => new MemoryStorePartitionRule.Through("ClaimId", "annal_claims", "ClaimId"),
            "annal_dependencies" => new MemoryStorePartitionRule.Through("DependentVersionId", "annal_versions", "VersionId"),
            "annal_review_decision_receipts" =>
                new MemoryStorePartitionRule.Through("ReviewEventSequence", "annal_review_events", "Sequence"),
            "artifact_sensitivity" => new MemoryStorePartitionRule.ByCode("ArtifactKindCode", SensitivityFamily),
            "memory_erasure_fingerprints" or "memory_erasure_receipts" =>
                new MemoryStorePartitionRule.ByCode("StoreCode", ErasureFamily),
            "memory_erasure_receipt_subjects" =>
                new MemoryStorePartitionRule.Through("MutationId", "memory_erasure_receipts", "MutationId"),
            _ when table.StartsWith("annal_", StringComparison.Ordinal)
                || table.StartsWith("memory_erasure_", StringComparison.Ordinal) =>
                throw new InvalidOperationException(
                    $"The memory table '{table}' is shared between stores and has no partition rule, so a cross-store comparison cannot place its rows."),
            _ when table.StartsWith("saga_", StringComparison.Ordinal) => new MemoryStorePartitionRule.Whole(MemoryStoreFamily.Saga),
            _ when table.StartsWith("lexicon_", StringComparison.Ordinal) => new MemoryStorePartitionRule.Whole(MemoryStoreFamily.Lexicon),
            _ when table.StartsWith("covenant_", StringComparison.Ordinal)
                || DisclosureTables.Contains(table, StringComparer.Ordinal) =>
                new MemoryStorePartitionRule.Whole(MemoryStoreFamily.Covenant),
            _ => throw new InvalidOperationException($"'{table}' is not a memory table."),
        };
    }

    /// <summary>
    /// Reads every memory table inside one deferred read transaction on <paramref name="connection"/>,
    /// which is rolled back afterwards, and partitions every row.
    /// </summary>
    internal static async Task<MemoryStoreSnapshot> CaptureAsync(SqliteConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);

        List<(string Name, bool WithoutRowid)> discovered = [];

        await using (SqliteCommand catalog = connection.CreateCommand())
        {
            catalog.Transaction = transaction;

            catalog.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'table' ORDER BY name;";

            await using SqliteDataReader reader = await catalog.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                string name = reader.GetString(0);

                string sql = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);

                if (!IsMemoryTable(name) || sql.StartsWith("CREATE VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                discovered.Add((name, WithoutRowid.IsMatch(sql)));
            }
        }

        discovered.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));

        Dictionary<string, MemoryStorePartitionRule> rules = discovered.ToDictionary(
            static table => table.Name,
            static table => RuleFor(table.Name),
            StringComparer.Ordinal);

        Dictionary<string, CapturedTable> tables = new(StringComparer.Ordinal);

        foreach ((string name, bool withoutRowid) in discovered)
        {
            tables[name] = await ReadTableAsync(connection, transaction, name, withoutRowid, ct);
        }

        await transaction.RollbackAsync(ct);

        Dictionary<string, MemoryStoreFamily[]> placed = new(StringComparer.Ordinal);

        Dictionary<MemoryStoreFamily, List<string>> rows = Families.ToDictionary(static family => family, static _ => new List<string>());

        foreach ((string name, _) in discovered)
        {
            MemoryStoreFamily[] families = Place(name, rules, tables, placed);

            CapturedTable table = tables[name];

            for (int index = 0; index < table.Rendered.Count; index++)
            {
                rows[families[index]].Add(table.Rendered[index]);
            }
        }

        return new MemoryStoreSnapshot(
            rows.ToDictionary(static pair => pair.Key, static pair => (IReadOnlyList<string>)pair.Value.AsReadOnly()),
            new HashSet<string>(discovered.Select(static table => table.Name), StringComparer.Ordinal));
    }

    /// <summary>
    /// Waits until the Covenant search projection has settled: the accelerator is available, the
    /// persisted applied tuple has caught up with the canonical one, and no canonical change is
    /// waiting in the outbox.
    /// </summary>
    /// <remarks>
    /// <para>Settled is read from <c>covenant_state</c> with the comparison
    /// <c>CovenantPersistedAvailabilityPublisher</c> makes, rather than from the published
    /// <see cref="ICovenantAvailability"/> flag. That flag is published from the same comparison, but
    /// only at bootstrap: a host that started before its first projection keeps reporting
    /// <see cref="CovenantFtsSynchronizationState.Dirty"/> for its whole lifetime, however far the
    /// projection has caught up since. The flag is still read, to refuse an unavailable accelerator,
    /// which can never settle.</para>
    ///
    /// <para>Nothing on a request path drains the outbox; the host's maintenance pass does, on a timer
    /// far longer than a test. Each poll that finds the projection behind therefore runs one batch
    /// through the coordinator that pass runs, so a Covenant write's projection lands before the
    /// snapshot that follows it rather than inside whichever later snapshot the timer happens to meet.
    /// A projection already settled is never touched. One that has not settled within the timeout
    /// fails the test with the last state observed.</para>
    /// </remarks>
    internal static async Task QuiesceAsync(IServiceProvider services, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);

        ICovenantAvailability availability = services.GetRequiredService<ICovenantAvailability>();

        // Every read, batch and wait is bounded by the same deadline, so a step that stops answering
        // fails the test with what was last seen rather than holding it open.
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);

        deadline.CancelAfter(QuiesceTimeout);

        string last = "nothing read yet";

        try
        {
            while (true)
            {
                CovenantFtsSynchronizationState published = availability.Current.FtsSynchronization;

                (bool caughtUp, long waiting) = await ReadProjectionAsync(services, deadline.Token);

                last = string.Create(
                    CultureInfo.InvariantCulture,
                    $"published {published}, applied tuple caught up {caughtUp}, {waiting} outbox row(s) waiting");

                if (published != CovenantFtsSynchronizationState.Unavailable && caughtUp && waiting == 0)
                {
                    return;
                }

                if (published != CovenantFtsSynchronizationState.Unavailable)
                {
                    await SynchronizeOutboxAsync(services, deadline.Token);
                }

                await Task.Delay(QuiescePoll, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Assert.Fail($"The Covenant search projection did not settle within {QuiesceTimeout.TotalSeconds} s: {last}.");
        }
    }

    /// <summary>
    /// Fails unless every family outside <paramref name="target"/> is row-for-row identical and the
    /// target itself changed.
    /// </summary>
    /// <remarks>
    /// The second half is the guard against a vacuous pass: a verb that wrote nothing at all would
    /// otherwise prove its isolation trivially.
    /// </remarks>
    internal static void AssertOnlyChanged(MemoryStoreSnapshot before, MemoryStoreSnapshot after, MemoryStoreFamily target)
    {
        ArgumentNullException.ThrowIfNull(before);

        ArgumentNullException.ThrowIfNull(after);

        Assert.True(
            before.Tables.SetEquals(after.Tables),
            "The memory tables differ between the two snapshots, so their rows cannot be compared family by family.");

        List<string> leaks = [];

        foreach (MemoryStoreFamily family in Families.Where(family => !target.HasFlag(family)))
        {
            if (!before.Rows[family].SequenceEqual(after.Rows[family], StringComparer.Ordinal))
            {
                leaks.Add(Describe(family, before.Rows[family], after.Rows[family]));
            }
        }

        if (leaks.Count > 0)
        {
            Assert.Fail($"A verb that may change only {target} changed another family.{System.Environment.NewLine}{string.Join(System.Environment.NewLine, leaks)}");
        }

        bool targetChanged = Families
            .Where(family => target.HasFlag(family))
            .Any(family => !before.Rows[family].SequenceEqual(after.Rows[family], StringComparer.Ordinal));

        Assert.True(targetChanged, $"The {target} family did not change, so the verb proved nothing about isolation.");
    }

    /// <summary>How many rows of one table a family holds.</summary>
    internal int CountRows(MemoryStoreFamily family, string table)
    {
        ArgumentNullException.ThrowIfNull(table);

        string prefix = table + ":";

        return Rows[family].Count(row => row.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static MemoryStoreFamily AnnalFamily(long code) =>
        (AnnalSubjectStore)code switch
        {
            AnnalSubjectStore.Saga => MemoryStoreFamily.Saga,
            AnnalSubjectStore.Lexicon => MemoryStoreFamily.Lexicon,
            _ => throw new InvalidOperationException($"Annals subject store code {code} names no memory store."),
        };

    private static MemoryStoreFamily ErasureFamily(long code) =>
        (MemoryReviewStore)code switch
        {
            MemoryReviewStore.Covenant => MemoryStoreFamily.Covenant,
            MemoryReviewStore.Saga => MemoryStoreFamily.Saga,
            MemoryReviewStore.Lexicon => MemoryStoreFamily.Lexicon,
            _ => throw new InvalidOperationException($"Erasure store code {code} names no memory store."),
        };

    private static MemoryStoreFamily SensitivityFamily(long code) =>
        (SensitiveArtifactKind)code switch
        {
            SensitiveArtifactKind.Saga => MemoryStoreFamily.Saga,
            SensitiveArtifactKind.Lexicon => MemoryStoreFamily.Lexicon,
            _ => MemoryStoreFamily.Shared,
        };

    /// <summary>The family of every row of one table, resolving a parent table's rows first.</summary>
    private static MemoryStoreFamily[] Place(
        string name,
        IReadOnlyDictionary<string, MemoryStorePartitionRule> rules,
        IReadOnlyDictionary<string, CapturedTable> tables,
        Dictionary<string, MemoryStoreFamily[]> placed)
    {
        if (placed.TryGetValue(name, out MemoryStoreFamily[]? known))
        {
            return known;
        }

        CapturedTable table = tables[name];

        MemoryStoreFamily[] families = rules[name] switch
        {
            MemoryStorePartitionRule.Whole whole => [.. table.Values.Select(_ => whole.Family)],
            MemoryStorePartitionRule.ByCode byCode => [.. table.Values.Select(values => byCode.Family(Code(table, values, byCode.Column)))],
            MemoryStorePartitionRule.Through through => PlaceThrough(name, through, rules, tables, placed),
            _ => throw new InvalidOperationException($"The memory table '{name}' has an unrecognized partition rule."),
        };

        placed[name] = families;

        return families;
    }

    private static MemoryStoreFamily[] PlaceThrough(
        string name,
        MemoryStorePartitionRule.Through through,
        IReadOnlyDictionary<string, MemoryStorePartitionRule> rules,
        IReadOnlyDictionary<string, CapturedTable> tables,
        Dictionary<string, MemoryStoreFamily[]> placed)
    {
        if (!tables.TryGetValue(through.Parent, out CapturedTable? parent))
        {
            throw new InvalidOperationException($"The memory table '{name}' is partitioned through '{through.Parent}', which the catalog does not hold.");
        }

        MemoryStoreFamily[] parentFamilies = Place(through.Parent, rules, tables, placed);

        int parentKey = parent.Ordinal(through.ParentColumn);

        Dictionary<string, MemoryStoreFamily> byKey = new(StringComparer.Ordinal);

        for (int index = 0; index < parent.Values.Count; index++)
        {
            byKey[Render(parent.Values[index][parentKey])] = parentFamilies[index];
        }

        CapturedTable table = tables[name];

        int key = table.Ordinal(through.Column);

        return
        [
            .. table.Values.Select(values => byKey.TryGetValue(Render(values[key]), out MemoryStoreFamily family)
                ? family
                : throw new InvalidOperationException(
                    $"A row of '{name}' names {through.Column} {Render(values[key])}, which no row of '{through.Parent}' holds.")),
        ];
    }

    private static long Code(CapturedTable table, object?[] values, string column) =>
        values[table.Ordinal(column)] is long code
            ? code
            : throw new InvalidOperationException($"'{table.Name}.{column}' is not an integer store code.");

    private static async Task<CapturedTable> ReadTableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string name,
        bool withoutRowid,
        CancellationToken ct)
    {
        string quoted = "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

        int columnCount;

        await using (SqliteCommand shape = connection.CreateCommand())
        {
            shape.Transaction = transaction;

            shape.CommandText = "SELECT count(*) FROM pragma_table_info($table);";

            _ = shape.Parameters.AddWithValue("$table", name);

            columnCount = Convert.ToInt32(await shape.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }

        string order = withoutRowid
            ? string.Join(", ", Enumerable.Range(1, columnCount).Select(static ordinal => ordinal.ToString(CultureInfo.InvariantCulture)))
            : "rowid";

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = $"SELECT * FROM {quoted} ORDER BY {order};";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);

        string[] columns = [.. Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)];

        List<object?[]> values = [];

        List<string> rendered = [];

        while (await reader.ReadAsync(ct))
        {
            object?[] row = new object?[reader.FieldCount];

            for (int ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            {
                row[ordinal] = reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
            }

            values.Add(row);

            rendered.Add(name + ":" + string.Join("|", row.Select(Render)));
        }

        return new CapturedTable(name, columns, values, rendered);
    }

    private static string Render(object? value) =>
        value switch
        {
            null => NullCell,
            byte[] blob => Convert.ToHexString(blob),
            long integer => integer.ToString(CultureInfo.InvariantCulture),
            double real => real.ToString("R", CultureInfo.InvariantCulture),
            string text => text,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? NullCell,
        };

    /// <summary>One family's rows that one snapshot holds and the other does not, in both directions.</summary>
    private static string Describe(MemoryStoreFamily family, IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        List<string> removed = [.. before];

        List<string> added = [];

        foreach (string row in after)
        {
            if (!removed.Remove(row))
            {
                added.Add(row);
            }
        }

        return $"The {family} family changed: removed [{Excerpt(removed)}], added [{Excerpt(added)}]"
            + (removed.Count == 0 && added.Count == 0 ? " (the same rows in another order)." : ".");
    }

    private static string Excerpt(IReadOnlyList<string> rows) =>
        string.Join("; ", rows.Take(12).Select(static row => row.Length <= 240 ? row : row[..240] + "…"))
        + (rows.Count > 12 ? $"; and {rows.Count - 12} more" : string.Empty);

    /// <summary>
    /// Whether the persisted applied tuple has caught up with the canonical one, and how many outbox
    /// rows wait, in one statement and therefore one snapshot.
    /// </summary>
    private static async Task<(bool CaughtUp, long Waiting)> ReadProjectionAsync(IServiceProvider services, CancellationToken ct)
    {
        Result<IGrimoireOrdinaryConnectionLease> opened = await services
            .GetRequiredService<IGrimoireOrdinaryConnectionFactory>()
            .OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, ct);

        Assert.True(opened.IsSuccess, opened.IsFailure ? opened.Error.Message : null);

        await using IGrimoireOrdinaryConnectionLease lease = opened.Value;

        await using SqliteCommand command = lease.Connection.CreateCommand();

        command.CommandText =
            """
            SELECT st.AppliedDatasetGeneration IS st.DatasetGeneration
                   AND st.AppliedSearchSequence IS st.CanonicalSearchSequence
                   AND st.AppliedCampaignDeletionSequence IS COALESCE(
                       (SELECT MAX(Sequence) FROM owner_deletion_events WHERE OwnerKindCode = 1), 0),
                   (SELECT count(*) FROM covenant_search_outbox)
            FROM covenant_state st
            WHERE st.StateKey = 1;
            """;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);

        Assert.True(await reader.ReadAsync(ct), "The Covenant canonical tier holds no state row, so its projection cannot settle.");

        return (reader.GetInt64(0) == 1, reader.GetInt64(1));
    }

    /// <summary>Runs one outbox batch the way the host's maintenance pass runs it.</summary>
    private static async Task SynchronizeOutboxAsync(IServiceProvider services, CancellationToken ct)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        if (db.Database.GetDbConnection().State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        Result<CovenantOutboxSyncOutcome> synchronized = await scope.ServiceProvider
            .GetRequiredService<CovenantSearchOutboxCoordinator>()
            .SynchronizeAsync(CovenantSearchOutboxWorker.DefaultBatchRows, ct);

        Assert.True(synchronized.IsSuccess, synchronized.IsFailure ? synchronized.Error.Message : null);
    }

    /// <summary>One table as read: its columns, each row's raw values, and each row rendered.</summary>
    private sealed record CapturedTable(
        string Name,
        IReadOnlyList<string> Columns,
        IReadOnlyList<object?[]> Values,
        IReadOnlyList<string> Rendered)
    {
        internal int Ordinal(string column)
        {
            for (int index = 0; index < Columns.Count; index++)
            {
                if (string.Equals(Columns[index], column, StringComparison.Ordinal))
                {
                    return index;
                }
            }

            throw new InvalidOperationException($"The memory table '{Name}' has no column '{column}'.");
        }
    }
}
