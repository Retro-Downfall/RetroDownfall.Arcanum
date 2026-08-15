using System.Text;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// Builds a tier's expected-object manifest from its catalog resources.
/// </summary>
/// <remarks>
/// The input is trusted by construction: these are embedded resources compiled into the binary, not
/// anything read from a database or supplied by a caller. That is what makes it safe for the
/// inspector to interpolate the names produced here into <c>PRAGMA</c> statements, which take no
/// parameters.
///
/// <para>The grammar accepted here is deliberately narrow. An unparseable collation, a duplicate
/// index name, or a declaration whose name disagrees with its file all throw at build time rather
/// than producing an expectation that could never match.</para>
/// </remarks>
internal static class GrimoireSchemaManifestBuilder
{

    /// <summary>Both Covenant tiers ship at schema version 1.</summary>
    internal const int CovenantSchemaVersion = 1;

    public static GrimoireSchemaManifest Build(
        GrimoireSchemaFamily family,
        GrimoireSchemaTransactionTier transactionTier,
        int version,
        string sourceDefinitionFingerprint,
        IReadOnlyList<GrimoireSchemaObject> definitions)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDefinitionFingerprint);

        ArgumentNullException.ThrowIfNull(definitions);

        List<GrimoireSchemaManifestEntry> entries = [];

        Dictionary<string, List<GrimoireExpectedIndex>> indexesByTable =
            new(StringComparer.Ordinal);

        HashSet<string> indexNames = new(StringComparer.Ordinal);

        foreach (GrimoireSchemaObject definition in definitions)
        {

            if (definition.TransactionTier != transactionTier)
            {

                throw new InvalidOperationException(
                    $"Schema object '{definition.ResourcePath}.sql' does not belong to the manifest's transaction tier.");

            }

            ParseResource(definition, entries, indexesByTable, indexNames);

        }

        List<GrimoireSchemaManifestEntry> resolved = [];

        foreach (GrimoireSchemaManifestEntry entry in entries)
        {

            resolved.Add(
                indexesByTable.TryGetValue(entry.Name, out List<GrimoireExpectedIndex>? owned)
                    ? entry with { Indexes = owned }
                    : entry);

        }

        if (transactionTier == GrimoireSchemaTransactionTier.CovenantAccelerator)
        {

            resolved.AddRange(CovenantAcceleratorSyntheticManifest.Entries);

        }

        return new GrimoireSchemaManifest(
            family,
            transactionTier,
            version,
            sourceDefinitionFingerprint,
            resolved);

    }

    private static void ParseResource(
        GrimoireSchemaObject definition,
        List<GrimoireSchemaManifestEntry> entries,
        Dictionary<string, List<GrimoireExpectedIndex>> indexesByTable,
        HashSet<string> indexNames)
    {

        foreach (string statement in SplitStatements(definition.Sql))
        {

            string normalized = GrimoireSqlNormalizer.Normalize(statement);

            if (normalized.Length == 0)
            {

                continue;

            }

            if (StartsWithKeywords(normalized, "CREATE", "INDEX")
                || StartsWithKeywords(normalized, "CREATE", "UNIQUE", "INDEX"))
            {

                GrimoireExpectedIndex index = ParseIndex(normalized, definition, out string ownerTable);

                if (!indexNames.Add(index.Name))
                {

                    throw new InvalidOperationException(
                        $"Schema object '{definition.ResourcePath}.sql' declares duplicate index '{index.Name}'.");

                }

                if (!indexesByTable.TryGetValue(ownerTable, out List<GrimoireExpectedIndex>? owned))
                {

                    owned = [];

                    indexesByTable[ownerTable] = owned;

                }

                owned.Add(index);

                continue;

            }

            GrimoireSchemaObjectType type = ClassifyObject(normalized, definition);

            entries.Add(
                new GrimoireSchemaManifestEntry(
                    type,
                    definition.Name,
                    definition.Name,
                    normalized,
                    IsSynthetic: false,
                    Indexes: []));

        }

    }

    private static GrimoireSchemaObjectType ClassifyObject(
        string normalized,
        GrimoireSchemaObject definition)
    {

        if (StartsWithKeywords(normalized, "CREATE", "VIRTUAL", "TABLE"))
        {

            return GrimoireSchemaObjectType.VirtualTable;

        }

        if (StartsWithKeywords(normalized, "CREATE", "TABLE"))
        {

            return GrimoireSchemaObjectType.Table;

        }

        if (StartsWithKeywords(normalized, "CREATE", "TRIGGER"))
        {

            return GrimoireSchemaObjectType.Trigger;

        }

        if (StartsWithKeywords(normalized, "CREATE", "VIEW"))
        {

            return GrimoireSchemaObjectType.View;

        }

        throw new InvalidOperationException(
            $"Schema object '{definition.ResourcePath}.sql' declares an unsupported statement kind.");

    }

    private static GrimoireExpectedIndex ParseIndex(
        string normalized,
        GrimoireSchemaObject definition,
        out string ownerTable)
    {

        bool isUnique = StartsWithKeywords(normalized, "CREATE", "UNIQUE", "INDEX");

        int cursor = isUnique
            ? SkipKeywords(normalized, 0, "CREATE", "UNIQUE", "INDEX")
            : SkipKeywords(normalized, 0, "CREATE", "INDEX");

        string name = ReadIdentifier(normalized, ref cursor, definition);

        cursor = SkipKeywords(normalized, cursor, "ON");

        ownerTable = ReadIdentifier(normalized, ref cursor, definition);

        int open = normalized.IndexOf('(', cursor);

        int close = FindMatchingParenthesis(normalized, open, definition);

        string columnList = normalized[(open + 1)..close];

        bool isPartial = normalized.AsSpan(close)
            .Contains("WHERE", StringComparison.OrdinalIgnoreCase);

        List<GrimoireExpectedIndexColumn> columns = [];

        int sequence = 0;

        foreach (string part in SplitTopLevel(columnList))
        {

            string column = part.Trim();

            bool descending = EndsWithKeyword(column, "DESC");

            if (descending || EndsWithKeyword(column, "ASC"))
            {

                column = column[..column.LastIndexOf(' ')].Trim();

            }

            string collation = "BINARY";

            int collateAt = column.IndexOf(" COLLATE ", StringComparison.OrdinalIgnoreCase);

            if (collateAt >= 0)
            {

                collation = column[(collateAt + " COLLATE ".Length)..].Trim();

                column = column[..collateAt].Trim();

                if (collation.Length == 0 || collation.Contains(' ', StringComparison.Ordinal))
                {

                    throw new InvalidOperationException(
                        $"Schema object '{definition.ResourcePath}.sql' declares an unparseable index collation.");

                }

            }

            // An expression position is ordinary SQLite and the shipped catalog contains one, so it
            // is expected rather than refused: PRAGMA index_xinfo reports it with a null column name
            // and column id -2, which is exactly the shape recorded here. The expression text itself
            // is not part of the shape, because the index's own stored DDL already carries it.
            bool isExpression = column.Contains('(', StringComparison.Ordinal);

            columns.Add(
                new GrimoireExpectedIndexColumn(
                    sequence,
                    ColumnId: isExpression ? -2 : -1,
                    isExpression ? null : Unquote(column),
                    descending,
                    collation.ToUpperInvariant(),
                    IsKey: true));

            sequence++;

        }

        if (columns.Count == 0)
        {

            throw new InvalidOperationException(
                $"Schema object '{definition.ResourcePath}.sql' declares an index with no columns.");

        }

        return new GrimoireExpectedIndex(name, isUnique, "c", isPartial, columns);

    }

    /// <summary>
    /// Splits a resource into top-level statements. A trigger is never split: its
    /// <c>BEGIN ... END</c> body contains semicolons that do not terminate the statement.
    /// </summary>
    /// <remarks>
    /// Comments are copied through but never interpreted. These files are prose-heavy, and English
    /// prose contains both apostrophes and semicolons: reading a comment as SQL would let
    /// <c>head's own</c> open a string literal that swallows the rest of the file, and would let a
    /// semicolon in a sentence cut a table declaration in half. Either produces a manifest entry
    /// whose expected definition could never match what SQLite installed.
    /// </remarks>
    private static IReadOnlyList<string> SplitStatements(string sql)
    {

        string stripped = GrimoireSqlNormalizer.Normalize(sql);

        if (StartsWithKeywords(stripped, "CREATE", "TRIGGER"))
        {

            return [sql];

        }

        List<string> statements = [];

        StringBuilder current = new();

        int index = 0;

        while (index < sql.Length)
        {

            char value = sql[index];

            if (value == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {

                index = CopyLineComment(sql, index, current);

                continue;

            }

            if (value == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {

                index = CopyBlockComment(sql, index, current);

                continue;

            }

            if (value is '\'' or '"' or '`')
            {

                index = CopyQuoted(sql, index, current);

                continue;

            }

            if (value == ';')
            {

                statements.Add(current.ToString());

                _ = current.Clear();

                index++;

                continue;

            }

            _ = current.Append(value);

            index++;

        }

        if (current.ToString().Trim().Length > 0)
        {

            statements.Add(current.ToString());

        }

        return statements;

    }

    private static int CopyLineComment(string sql, int index, StringBuilder current)
    {

        while (index < sql.Length && sql[index] != '\n')
        {

            _ = current.Append(sql[index]);

            index++;

        }

        return index;

    }

    private static int CopyBlockComment(string sql, int index, StringBuilder current)
    {

        _ = current.Append(sql[index]).Append(sql[index + 1]);

        index += 2;

        while (index + 1 < sql.Length && !(sql[index] == '*' && sql[index + 1] == '/'))
        {

            _ = current.Append(sql[index]);

            index++;

        }

        if (index + 1 >= sql.Length)
        {

            _ = current.Append(sql.AsSpan(index));

            return sql.Length;

        }

        _ = current.Append(sql[index]).Append(sql[index + 1]);

        return index + 2;

    }

    /// <summary>
    /// Copies a quoted literal or identifier verbatim, treating a doubled quote as an escape rather
    /// than as the end of the literal.
    /// </summary>
    private static int CopyQuoted(string sql, int index, StringBuilder current)
    {

        char quote = sql[index];

        _ = current.Append(quote);

        index++;

        while (index < sql.Length)
        {

            char value = sql[index];

            _ = current.Append(value);

            index++;

            if (value != quote)
            {

                continue;

            }

            if (index < sql.Length && sql[index] == quote)
            {

                _ = current.Append(quote);

                index++;

                continue;

            }

            break;

        }

        return index;

    }

    private static IReadOnlyList<string> SplitTopLevel(string value)
    {

        List<string> parts = [];

        int depth = 0;

        StringBuilder current = new();

        foreach (char item in value)
        {

            if (item == '(')
            {

                depth++;

            }
            else if (item == ')')
            {

                depth--;

            }

            if (item == ',' && depth == 0)
            {

                parts.Add(current.ToString());

                current.Clear();

                continue;

            }

            _ = current.Append(item);

        }

        if (current.Length > 0)
        {

            parts.Add(current.ToString());

        }

        return parts;

    }

    private static int FindMatchingParenthesis(string value, int open, GrimoireSchemaObject definition)
    {

        if (open < 0)
        {

            throw new InvalidOperationException(
                $"Schema object '{definition.ResourcePath}.sql' declares an index with no column list.");

        }

        int depth = 0;

        for (int index = open; index < value.Length; index++)
        {

            if (value[index] == '(')
            {

                depth++;

            }
            else if (value[index] == ')')
            {

                depth--;

                if (depth == 0)
                {

                    return index;

                }

            }

        }

        throw new InvalidOperationException(
            $"Schema object '{definition.ResourcePath}.sql' has an unbalanced index column list.");

    }

    private static string ReadIdentifier(string value, ref int cursor, GrimoireSchemaObject definition)
    {

        while (cursor < value.Length && value[cursor] == ' ')
        {

            cursor++;

        }

        if (cursor >= value.Length)
        {

            throw new InvalidOperationException(
                $"Schema object '{definition.ResourcePath}.sql' ends before an expected identifier.");

        }

        if (value[cursor] == '"')
        {

            int end = value.IndexOf('"', cursor + 1);

            if (end < 0)
            {

                throw new InvalidOperationException(
                    $"Schema object '{definition.ResourcePath}.sql' has an unterminated quoted identifier.");

            }

            string quoted = value[(cursor + 1)..end];

            cursor = end + 1;

            return quoted;

        }

        int start = cursor;

        while (cursor < value.Length
            && (char.IsLetterOrDigit(value[cursor]) || value[cursor] == '_'))
        {

            cursor++;

        }

        if (cursor == start)
        {

            throw new InvalidOperationException(
                $"Schema object '{definition.ResourcePath}.sql' has an unparseable identifier.");

        }

        return value[start..cursor];

    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1]
            : value;

    private static bool StartsWithKeywords(string value, params string[] keywords)
    {

        int cursor = 0;

        foreach (string keyword in keywords)
        {

            while (cursor < value.Length && value[cursor] == ' ')
            {

                cursor++;

            }

            if (cursor + keyword.Length > value.Length
                || !value.AsSpan(cursor, keyword.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase))
            {

                return false;

            }

            cursor += keyword.Length;

        }

        return true;

    }

    private static int SkipKeywords(string value, int cursor, params string[] keywords)
    {

        foreach (string keyword in keywords)
        {

            while (cursor < value.Length && value[cursor] == ' ')
            {

                cursor++;

            }

            cursor += keyword.Length;

        }

        return cursor;

    }

    private static bool EndsWithKeyword(string value, string keyword) =>
        value.Length > keyword.Length
        && value[^(keyword.Length + 1)] == ' '
        && value.AsSpan(value.Length - keyword.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase);

}
