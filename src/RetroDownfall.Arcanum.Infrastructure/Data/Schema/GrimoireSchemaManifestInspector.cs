using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// Compares one tier's installed catalog with its expected manifest and produces the
/// installed-catalog fingerprint recorded in <c>grimoire_feature_schemas</c>.
/// </summary>
/// <remarks>
/// This is the drift gate. <see cref="GrimoireSchemaIdentity"/> answers a different question - it
/// hashes the whole physical file for backup verification - and deliberately keeps doing so. This
/// type answers "is the tier I am about to use the one this binary declares", per tier, so a damaged
/// capability fails closed instead of being guessed at.
///
/// <para>Every identifier reaching a <c>PRAGMA</c> here originates in a trusted manifest or the
/// ownership registry, never in a row read from the database and never from an API value. That
/// matters because <c>PRAGMA index_list(x)</c> takes no parameters and has to be built by
/// interpolation; quoting is applied through one dedicated method as defense in depth.</para>
/// </remarks>
internal sealed class GrimoireSchemaManifestInspector(
    GrimoireSchemaTierOwnershipRegistry ownershipRegistry)
{

    private const char FieldSeparator = '\u001F';

    private const char RowSeparator = '\u001E';

    private readonly GrimoireSchemaTierOwnershipRegistry _ownershipRegistry =
        ownershipRegistry ?? throw new ArgumentNullException(nameof(ownershipRegistry));

    public async Task<GrimoireSchemaInspectionResult> InspectAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        GrimoireSchemaManifest manifest,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(manifest);

        Dictionary<string, InstalledObject> installed;

        try
        {

            installed = await ReadCatalogAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);

        }
        catch (SqliteException)
        {

            return GrimoireSchemaInspectionResult.Invalid(
                GrimoireSchemaInspectionFailure.CatalogReadFailed,
                manifest.TransactionTier.ToString());

        }

        StringBuilder frame = new();

        foreach (GrimoireSchemaManifestEntry entry in manifest.Entries)
        {

            if (!installed.TryGetValue(entry.Name, out InstalledObject found))
            {

                return GrimoireSchemaInspectionResult.Invalid(
                    GrimoireSchemaInspectionFailure.MissingObject,
                    entry.Name);

            }

            if (!string.Equals(found.NormalizedSql, entry.NormalizedSql, StringComparison.Ordinal))
            {

                return GrimoireSchemaInspectionResult.Invalid(
                    entry.IsSynthetic
                        ? GrimoireSchemaInspectionFailure.ShadowObjectDrift
                        : GrimoireSchemaInspectionFailure.DefinitionDrift,
                    entry.Name);

            }

            _ = frame
                .Append(entry.Type.ToString())
                .Append(FieldSeparator)
                .Append(entry.Name)
                .Append(FieldSeparator)
                .Append(found.TableName)
                .Append(FieldSeparator)
                .Append(found.NormalizedSql)
                .Append(RowSeparator);

            if (entry.Type is GrimoireSchemaObjectType.Trigger or GrimoireSchemaObjectType.View)
            {

                continue;

            }

            GrimoireSchemaInspectionResult? indexFailure = await AppendIndexShapesAsync(
                connection,
                transaction,
                entry,
                installed,
                frame,
                cancellationToken).ConfigureAwait(false);

            if (indexFailure is not null)
            {

                return indexFailure;

            }

        }

        GrimoireSchemaInspectionResult? unexpected = DetectUnexpected(manifest, installed);

        if (unexpected is not null)
        {

            return unexpected;

        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(frame.ToString()));

        return GrimoireSchemaInspectionResult.Valid(
            GrimoireSchemaIdentity.Prefix + Convert.ToHexStringLower(hash));

    }

    /// <summary>
    /// Rejects only what nobody declared. An object owned by another installed tier is ignored,
    /// because all three tiers share one database and each is inspected on its own.
    /// </summary>
    private GrimoireSchemaInspectionResult? DetectUnexpected(
        GrimoireSchemaManifest manifest,
        Dictionary<string, InstalledObject> installed)
    {

        HashSet<string> expected =
            new(manifest.Entries.Select(static entry => entry.Name), StringComparer.Ordinal);

        foreach (KeyValuePair<string, InstalledObject> pair in installed)
        {

            if (expected.Contains(pair.Key))
            {

                continue;

            }

            if (pair.Value.Type == "index")
            {

                // An implicit index has no SQL and is validated through its owning table's shape.
                if (pair.Value.NormalizedSql.Length == 0)
                {

                    continue;

                }

                if (!_ownershipRegistry.TryGetIndexOwner(pair.Key, out _))
                {

                    return GrimoireSchemaInspectionResult.Invalid(
                        GrimoireSchemaInspectionFailure.UnexpectedObject,
                        pair.Key);

                }

                continue;

            }

            if (_ownershipRegistry.TryGetObjectOwner(pair.Key, out _))
            {

                continue;

            }

            // A Covenant-prefixed object nobody declared is the dangerous case: it looks like ours
            // and would be trusted by name alone.
            if (pair.Key.StartsWith("covenant_", StringComparison.Ordinal))
            {

                return GrimoireSchemaInspectionResult.Invalid(
                    GrimoireSchemaInspectionFailure.UnexpectedObject,
                    pair.Key);

            }

        }

        return null;

    }

    private static async Task<GrimoireSchemaInspectionResult?> AppendIndexShapesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        GrimoireSchemaManifestEntry entry,
        Dictionary<string, InstalledObject> installed,
        StringBuilder frame,
        CancellationToken cancellationToken)
    {

        List<InstalledIndex> indexes = await ReadIndexListAsync(
            connection,
            transaction,
            entry.Name,
            cancellationToken).ConfigureAwait(false);

        foreach (GrimoireExpectedIndex expected in entry.Indexes)
        {

            InstalledIndex? match = indexes
                .FirstOrDefault(candidate => string.Equals(candidate.Name, expected.Name, StringComparison.Ordinal));

            if (match is null)
            {

                return GrimoireSchemaInspectionResult.Invalid(
                    GrimoireSchemaInspectionFailure.MissingObject,
                    expected.Name);

            }

            if (match.IsUnique != expected.IsUnique
                || !string.Equals(match.Origin, expected.Origin, StringComparison.Ordinal)
                || match.IsPartial != expected.IsPartial)
            {

                return GrimoireSchemaInspectionResult.Invalid(
                    GrimoireSchemaInspectionFailure.IndexShapeDrift,
                    expected.Name);

            }

            // Shape stops here. PRAGMA index_list reduces a partial index to a 0/1 flag and
            // index_xinfo has no predicate column, so an index that keeps its name, uniqueness, and
            // key columns while carrying a different WHERE predicate - or a different expression
            // position - is invisible to everything above. Only the stored DDL carries it, and an
            // explicit index always has one; an implicit index's is null and is validated by shape
            // alone through its owning table.
            if (installed.TryGetValue(expected.Name, out InstalledObject declaration)
                && declaration.NormalizedSql.Length != 0
                && !string.Equals(declaration.NormalizedSql, expected.NormalizedSql, StringComparison.Ordinal))
            {

                return GrimoireSchemaInspectionResult.Invalid(
                    GrimoireSchemaInspectionFailure.IndexShapeDrift,
                    expected.Name);

            }

            List<InstalledIndexColumn> columns = await ReadIndexColumnsAsync(
                connection,
                transaction,
                match.Name,
                cancellationToken).ConfigureAwait(false);

            List<InstalledIndexColumn> keyColumns =
                [.. columns.Where(static column => column.IsKey)];

            if (keyColumns.Count != expected.Columns.Count)
            {

                return GrimoireSchemaInspectionResult.Invalid(
                    GrimoireSchemaInspectionFailure.IndexShapeDrift,
                    expected.Name);

            }

            for (int position = 0; position < keyColumns.Count; position++)
            {

                InstalledIndexColumn actual = keyColumns[position];

                GrimoireExpectedIndexColumn declared = expected.Columns[position];

                if (!string.Equals(actual.Name, declared.Name, StringComparison.Ordinal)
                    || actual.Descending != declared.Descending
                    || !string.Equals(actual.Collation, declared.Collation, StringComparison.OrdinalIgnoreCase))
                {

                    return GrimoireSchemaInspectionResult.Invalid(
                        GrimoireSchemaInspectionFailure.IndexShapeDrift,
                        expected.Name);

                }

            }

        }

        // Every index on an owned table is framed, implicit ones included. A primary key or unique
        // constraint has no name worth pinning, but a change to how SQLite materializes it is still
        // a change to the installed catalog and must move the fingerprint.
        foreach (InstalledIndex index in indexes.OrderBy(static item => item.Origin, StringComparer.Ordinal)
            .ThenBy(static item => item.Name, StringComparer.Ordinal))
        {

            List<InstalledIndexColumn> columns = await ReadIndexColumnsAsync(
                connection,
                transaction,
                index.Name,
                cancellationToken).ConfigureAwait(false);

            _ = frame
                .Append("index")
                .Append(FieldSeparator)
                .Append(index.Origin)
                .Append(FieldSeparator)
                .Append(index.IsUnique ? '1' : '0')
                .Append(FieldSeparator)
                .Append(index.IsPartial ? '1' : '0');

            // Autoindex names encode constraint declaration order and are not stable enough to
            // fingerprint; their shape is. An explicitly named index carries its stored DDL into the
            // frame as well, so the fingerprint moves for a predicate or expression change the same
            // way it moves for a changed table definition.
            if (!index.Name.StartsWith("sqlite_autoindex_", StringComparison.Ordinal))
            {

                _ = frame.Append(FieldSeparator).Append(index.Name);

                if (installed.TryGetValue(index.Name, out InstalledObject declaration))
                {

                    _ = frame.Append(FieldSeparator).Append(declaration.NormalizedSql);

                }

            }

            foreach (InstalledIndexColumn column in columns)
            {

                _ = frame
                    .Append(FieldSeparator)
                    .Append(column.Sequence.ToString(CultureInfo.InvariantCulture))
                    .Append(':')
                    .Append(column.Name ?? string.Empty)
                    .Append(':')
                    .Append(column.Descending ? "DESC" : "ASC")
                    .Append(':')
                    .Append(column.Collation)
                    .Append(':')
                    .Append(column.IsKey ? '1' : '0');

            }

            _ = frame.Append(RowSeparator);

        }

        return null;

    }

    private static async Task<Dictionary<string, InstalledObject>> ReadCatalogAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT "type", "name", "tbl_name", COALESCE("sql", '')
            FROM sqlite_master
            WHERE "name" NOT LIKE 'sqlite\_%' ESCAPE '\'
            ORDER BY "type", "name", "tbl_name";
            """;

        Dictionary<string, InstalledObject> installed = new(StringComparer.Ordinal);

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            string sql = reader.GetString(3);

            installed[reader.GetString(1)] = new InstalledObject(
                reader.GetString(0),
                reader.GetString(2),
                sql.Length == 0 ? string.Empty : GrimoireSqlNormalizer.Normalize(sql));

        }

        return installed;

    }

    private static async Task<List<InstalledIndex>> ReadIndexListAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string tableName,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = $"PRAGMA index_list({QuoteIdentifier(tableName)});";

        List<InstalledIndex> indexes = [];

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            indexes.Add(
                new InstalledIndex(
                    reader.GetString(1),
                    reader.GetInt64(2) != 0,
                    reader.GetString(3),
                    reader.GetInt64(4) != 0));

        }

        return indexes;

    }

    private static async Task<List<InstalledIndexColumn>> ReadIndexColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string indexName,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = $"PRAGMA index_xinfo({QuoteIdentifier(indexName)});";

        List<InstalledIndexColumn> columns = [];

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            columns.Add(
                new InstalledIndexColumn(
                    (int)reader.GetInt64(0),
                    (int)reader.GetInt64(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt64(3) != 0,
                    reader.GetString(4),
                    reader.GetInt64(5) != 0));

        }

        return columns;

    }

    /// <summary>
    /// The single place an identifier becomes SQL text. Inputs are trusted manifest names; doubling
    /// any embedded quote keeps that trust from being the only thing standing between a name and the
    /// parser.
    /// </summary>
    private static string QuoteIdentifier(string name) =>
        "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private readonly record struct InstalledObject(string Type, string TableName, string NormalizedSql);

    private sealed record InstalledIndex(string Name, bool IsUnique, string Origin, bool IsPartial);

    private sealed record InstalledIndexColumn(
        int Sequence,
        int ColumnId,
        string? Name,
        bool Descending,
        string Collation,
        bool IsKey);

}
