using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// The Covenant accelerator tier: the projection table, the external-content FTS5 index over it, and
/// the three triggers that are the only thing keeping the two in step.
/// </summary>
/// <remarks>
/// External-content FTS5 stores no copy of the indexed text, so it cannot look up what to subtract
/// when a row changes: the triggers have to hand it every old value. Anything they omit stays in the
/// index as a token matching a row that no longer says it - a retired Covenant head that keeps
/// answering searches for the text it was retired for. That failure is silent until an integrity
/// check runs, which is why these tests probe real insert/update/delete behavior and finish with
/// rank-1 integrity rather than only reading DDL.
///
/// <para>Every test installs the accelerator tier alone. The projection is derived and rebuildable,
/// so it must stand up with no canonical table present; installing canonical first would hide an
/// accelerator object that had grown a dependency on one.</para>
/// </remarks>
public sealed class CovenantAcceleratorSchemaTests
{

    /// <summary>
    /// The scratch database installs the provider too, but a suite that opens SQLCipher connections
    /// declares it itself so a filtered run of this class alone does not depend on the order the
    /// fixture's own static constructor happens to run in.
    /// </summary>
    static CovenantAcceleratorSchemaTests() => SqliteNativeRuntime.Instance.Initialize();

    /// <summary>
    /// Fixed identifiers in the uppercase hyphenated form the raw-SQL tables store, so two runs seed
    /// identical rows and a difference in an assertion is a real difference.
    /// </summary>
    private const string EntryId = "3F2A1B4C-5D6E-4F70-8A91-B2C3D4E5F607";

    private const string VersionId = "9C8B7A65-4D3E-4F21-9081-A1B2C3D4E5F6";

    /// <summary>
    /// The six columns <c>covenant_fts</c> declares, in declaration order. A <c>'delete'</c> command
    /// has to name every one of them plus <c>rowid</c>.
    /// </summary>
    private static readonly string[] FtsColumns =
    [
        "NormalizedKey",
        "AuthoredContent",
        "CompiledContent",
        "EntryId",
        "LaneCode",
        "VersionId",
    ];

    [Fact]
    public void Accelerator_catalog_contains_documents_FTS_and_three_triggers()
    {

        IReadOnlyList<GrimoireSchemaObject> objects = GrimoireSchemaCatalog.CovenantAcceleratorObjects;

        Assert.Equal(5, objects.Count);

        Assert.All(
            objects,
            static definition => Assert.Equal(GrimoireSchemaFamily.Covenant, definition.Family));

        Assert.All(
            objects,
            static definition => Assert.Equal(
                GrimoireSchemaTransactionTier.CovenantAccelerator,
                definition.TransactionTier));

        List<string> names = [.. objects.Select(static definition => definition.Name)];

        // Ordinal name order inside the trigger category puts _ad ahead of _ai and _au; the tier is
        // installed in this exact sequence, so the expectation is the sequence and not a set.
        Assert.Equal(
            [
                "covenant_search_documents",
                "covenant_fts",
                "covenant_search_documents_ad",
                "covenant_search_documents_ai",
                "covenant_search_documents_au",
            ],
            names);

        List<GrimoireSchemaCategory> categories =
            [.. objects.Select(static definition => definition.Category)];

        // The content table has to exist before the index that binds to it, and both before the
        // triggers that write to both, or the install fails on the first object that references a
        // missing one.
        Assert.Equal(
            [
                GrimoireSchemaCategory.Tables,
                GrimoireSchemaCategory.FullTextSearch,
                GrimoireSchemaCategory.Triggers,
                GrimoireSchemaCategory.Triggers,
                GrimoireSchemaCategory.Triggers,
            ],
            categories);

    }

    /// <summary>
    /// The index keeps no copy of the text and no identity of its own: it reads columns back through
    /// <c>content</c> and pairs each FTS row with a projection row through <c>content_rowid</c>. That
    /// binding is only stable because <c>SearchRowId</c> is the SQLite rowid itself rather than an
    /// ordinary column beside one, which is what makes it survive a VACUUM.
    /// </summary>
    [Fact]
    public async Task Covenant_FTS_uses_external_content_and_stable_rowid()
    {

        await using CovenantSchemaScratchDatabase database = await CreateAcceleratorAsync();

        string declaration = await ReadDefinitionAsync(database, "covenant_fts");

        Assert.Contains("content='covenant_search_documents'", declaration, StringComparison.Ordinal);

        Assert.Contains("content_rowid='SearchRowId'", declaration, StringComparison.Ordinal);

        string? type = await database.ScalarStringAsync(
            """
            SELECT "type"
            FROM pragma_table_info('covenant_search_documents')
            WHERE "name" = 'SearchRowId';
            """,
            CancellationToken.None);

        // Only the exact spelling INTEGER PRIMARY KEY aliases the rowid. INT PRIMARY KEY, or the
        // same column in a WITHOUT ROWID table, would create a separate key and let the FTS row and
        // its projection row drift apart.
        Assert.Equal("INTEGER", type);

        long primaryKeyPosition = await database.ScalarLongAsync(
            """
            SELECT pk
            FROM pragma_table_info('covenant_search_documents')
            WHERE "name" = 'SearchRowId';
            """,
            CancellationToken.None);

        Assert.Equal(1L, primaryKeyPosition);

    }

    /// <summary>
    /// The tokenizer is part of the stored index, not a query-time choice: changing any of it later
    /// silently mismatches every token already written. It is pinned here so such a change has to be
    /// deliberate.
    /// </summary>
    [Fact]
    public async Task Covenant_FTS_uses_exact_tokenizer_prefixes_and_unindexed_IDs()
    {

        await using CovenantSchemaScratchDatabase database = await CreateAcceleratorAsync();

        string declaration = CollapseWhitespace(await ReadDefinitionAsync(database, "covenant_fts"));

        Assert.Contains("unicode61", declaration, StringComparison.Ordinal);

        // remove_diacritics 2 is the Unicode-correct setting; 1 mishandles multi-codepoint
        // diacritics and would fold two distinct keys onto the same token.
        Assert.Contains("remove_diacritics 2", declaration, StringComparison.Ordinal);

        // Covenant keys are dotted, underscored, and hyphenated paths. Without these tokenchars the
        // tokenizer splits a key into fragments and an exact-key search stops being exact.
        Assert.Contains("tokenchars ''._-''", declaration, StringComparison.Ordinal);

        Assert.Contains("prefix='2 3 4 8'", declaration, StringComparison.Ordinal);

        // Identifiers travel with a hit so the caller knows which head matched, but they must not be
        // searchable themselves: a GUID is one token under these tokenchars, and an indexed one
        // would let an ordinary word query collide with an identifier.
        foreach (string column in new[] { "EntryId", "LaneCode", "VersionId" })
        {

            Assert.Equal(
                $"{column} UNINDEXED",
                ReadFtsColumnDeclaration(declaration, column));

        }

        foreach (string column in new[] { "NormalizedKey", "AuthoredContent", "CompiledContent" })
        {

            Assert.Equal(column, ReadFtsColumnDeclaration(declaration, column));

        }

    }

    /// <summary>
    /// The behavior the whole tier exists to get right: after an insert, an update, and a delete, the
    /// index contains exactly what the projection says and nothing else.
    /// </summary>
    /// <remarks>
    /// The closing rank-1 integrity check is the part that cannot be faked. FTS5 compares the index
    /// against the content table row by row, so a token left behind by an incomplete <c>'delete'</c>
    /// command fails it - which is precisely the corruption an external-content index invites and the
    /// reason these triggers exist at all.
    /// </remarks>
    [Fact]
    public async Task Insert_update_delete_triggers_leave_no_ghost_tokens()
    {

        await using CovenantSchemaScratchDatabase database = await CreateAcceleratorAsync();

        // Nonsense tokens, so a match can only come from this row and never from the tokenizer
        // stemming or folding something else in the index.
        const string OriginalToken = "zqhwvrunelith";

        const string ReplacementToken = "xbtkoprazmund";

        await database.ExecuteAsync(
            InsertDocumentSql(
                searchRowId: 1,
                entryId: EntryId,
                lifecycleCode: 1,
                normalizedKey: "covenant.key.one",
                authoredContent: $"authored {OriginalToken}",
                compiledContent: $"compiled {OriginalToken}"),
            CancellationToken.None);

        Assert.Equal(1L, await CountMatchesAsync(database, OriginalToken));

        await database.ExecuteAsync(
            $"""
            UPDATE covenant_search_documents
            SET AuthoredContent = 'authored {ReplacementToken}',
                CompiledContent = 'compiled {ReplacementToken}'
            WHERE SearchRowId = 1;
            """,
            CancellationToken.None);

        // The update trigger subtracts the OLD row before inserting the new one. Omitting that half
        // leaves the superseded text matching a row that no longer contains it.
        Assert.Equal(0L, await CountMatchesAsync(database, OriginalToken));

        Assert.Equal(1L, await CountMatchesAsync(database, ReplacementToken));

        await database.ExecuteAsync(
            "DELETE FROM covenant_search_documents WHERE SearchRowId = 1;",
            CancellationToken.None);

        Assert.Equal(0L, await CountMatchesAsync(database, OriginalToken));

        Assert.Equal(0L, await CountMatchesAsync(database, ReplacementToken));

        await database.ExecuteAsync(
            "INSERT INTO covenant_fts(covenant_fts, rank) VALUES('integrity-check', 1);",
            CancellationToken.None);

    }

    /// <summary>
    /// FTS5 subtracts the tokens it is handed rather than looking them up, and by the time an AFTER
    /// DELETE trigger runs the content row is already gone. A column missing from a <c>'delete'</c>
    /// command therefore has nothing to read it back from, and its tokens stay in the index forever.
    /// </summary>
    [Fact]
    public async Task Delete_command_carries_every_old_indexed_value()
    {

        await using CovenantSchemaScratchDatabase database = await CreateAcceleratorAsync();

        foreach (string trigger in new[] { "covenant_search_documents_ad", "covenant_search_documents_au" })
        {

            List<string> columns = ReadDeleteCommandColumns(await ReadDefinitionAsync(database, trigger));

            // The first column is the table name itself, which is how FTS5 distinguishes a command
            // row from an ordinary insert.
            Assert.Equal("covenant_fts", columns[0]);

            // rowid is what binds the subtraction to one projection row; without it FTS5 has no way
            // to know which document the tokens belong to.
            Assert.Contains("rowid", columns, StringComparer.Ordinal);

            foreach (string column in FtsColumns)
            {

                Assert.Contains(column, columns, StringComparer.Ordinal);

            }

        }

    }

    /// <summary>
    /// Secure delete is why the accelerator can hold plaintext-derived tokens at all: without it a
    /// deleted token stays legible in a freed page until something reuses it, so retired Covenant
    /// content would outlive its retirement inside the index.
    /// </summary>
    [Fact]
    public async Task Accelerator_initializer_enables_secure_delete_and_rank_one_integrity()
    {

        await using CovenantSchemaScratchDatabase database = await CreateAcceleratorAsync();

        // The shadow config table is the only place FTS5 reports an applied rank setting back; there
        // is no pragma that answers this.
        Assert.Equal(
            1L,
            await database.ScalarLongAsync(
                "SELECT v FROM covenant_fts_config WHERE k = 'secure-delete';",
                CancellationToken.None));

        await database.ExecuteAsync(
            "INSERT INTO covenant_fts(covenant_fts, rank) VALUES('integrity-check', 1);",
            CancellationToken.None);

    }

    /// <summary>
    /// The projection is derived and rebuildable, so it must never hold a reference that could refuse
    /// a canonical mutation or block a core owner deletion. Divergence is repaired by a rebuild, not
    /// prevented by a constraint.
    /// </summary>
    [Fact]
    public async Task Accelerator_projection_has_no_cross_tier_foreign_key()
    {

        await using CovenantSchemaScratchDatabase database = await CreateAcceleratorAsync();

        Assert.Equal(
            0L,
            await database.ScalarLongAsync(
                "SELECT count(*) FROM pragma_foreign_key_list('covenant_search_documents');",
                CancellationToken.None));

        // The tier installed with no canonical table in the database at all, which is the same
        // independence stated the other way round.
        Assert.False(await database.ObjectExistsAsync("covenant_heads", "table", CancellationToken.None));

    }

    /// <summary>
    /// A retired head indexes its key and lifecycle only. Keeping content on a tombstone would let
    /// search return text the operator asked to remove, and would leave those tokens sitting behind a
    /// row that reads as deleted; a live head with no content is the mirror defect, an entry that
    /// exists in the projection and can never be found.
    /// </summary>
    [Fact]
    public async Task Tombstone_and_live_content_shapes_are_enforced()
    {

        await using CovenantSchemaScratchDatabase database = await CreateAcceleratorAsync();

        await database.ExecuteAsync(
            InsertDocumentSql(
                searchRowId: 1,
                entryId: EntryId,
                lifecycleCode: 2,
                normalizedKey: "covenant.key.retired",
                authoredContent: null,
                compiledContent: null),
            CancellationToken.None);

        await database.ExecuteAsync(
            InsertDocumentSql(
                searchRowId: 2,
                entryId: VersionId,
                lifecycleCode: 1,
                normalizedKey: "covenant.key.live",
                authoredContent: "authored text",
                compiledContent: "compiled text"),
            CancellationToken.None);

        SqliteException retiredWithContent = await Assert.ThrowsAsync<SqliteException>(() =>
            database.ExecuteAsync(
                InsertDocumentSql(
                    searchRowId: 3,
                    entryId: "0A1B2C3D-4E5F-4061-8273-849506A7B8C9",
                    lifecycleCode: 2,
                    normalizedKey: "covenant.key.retired.with.content",
                    authoredContent: "authored text",
                    compiledContent: "compiled text"),
                CancellationToken.None));

        Assert.Contains("CHECK constraint failed", retiredWithContent.Message, StringComparison.Ordinal);

        SqliteException liveWithoutContent = await Assert.ThrowsAsync<SqliteException>(() =>
            database.ExecuteAsync(
                InsertDocumentSql(
                    searchRowId: 4,
                    entryId: "1B2C3D4E-5F60-4172-8394-A5B6C7D8E9F0",
                    lifecycleCode: 1,
                    normalizedKey: "covenant.key.live.without.content",
                    authoredContent: null,
                    compiledContent: null),
                CancellationToken.None));

        Assert.Contains("CHECK constraint failed", liveWithoutContent.Message, StringComparison.Ordinal);

        Assert.Equal(
            2L,
            await database.ScalarLongAsync(
                "SELECT count(*) FROM covenant_search_documents;",
                CancellationToken.None));

    }

    private static async Task<CovenantSchemaScratchDatabase> CreateAcceleratorAsync()
    {

        CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        try
        {

            await database.InstallAcceleratorAsync(CancellationToken.None);

        }
        catch
        {

            await database.DisposeAsync();

            throw;

        }

        return database;

    }

    private static async Task<string> ReadDefinitionAsync(
        CovenantSchemaScratchDatabase database,
        string name)
    {

        string? sql = await database.ScalarStringAsync(
            $"SELECT sql FROM sqlite_master WHERE \"name\" = '{name}';",
            CancellationToken.None);

        Assert.NotNull(sql);

        return sql;

    }

    private static async Task<long> CountMatchesAsync(CovenantSchemaScratchDatabase database, string term) =>
        await database.ScalarLongAsync(
            $"SELECT count(*) FROM covenant_fts WHERE covenant_fts MATCH '{term}';",
            CancellationToken.None);

    /// <summary>
    /// The one comma-separated segment of an fts5 declaration that declares <paramref name="column"/>,
    /// so a test can assert on that column alone rather than on the whole statement text.
    /// </summary>
    private static string ReadFtsColumnDeclaration(string declaration, string column)
    {

        const string marker = "fts5(";

        int start = declaration.IndexOf(marker, StringComparison.Ordinal);

        if (start < 0)
        {

            throw new InvalidOperationException("The covenant_fts declaration names no fts5 module.");

        }

        foreach (string segment in declaration[(start + marker.Length)..].Split(','))
        {

            string trimmed = segment.Trim();

            if (string.Equals(trimmed, column, StringComparison.Ordinal)
                || trimmed.StartsWith(column + " ", StringComparison.Ordinal))
            {

                return trimmed;

            }

        }

        throw new InvalidOperationException($"The covenant_fts declaration declares no column '{column}'.");

    }

    /// <summary>
    /// The column list of the <c>'delete'</c> command inside a trigger body. Reading the list rather
    /// than searching the whole statement matters: the VALUES clause names every <c>old.</c> column
    /// anyway, so a substring search over the trigger would pass even with a column missing from the
    /// list that actually decides what FTS5 subtracts.
    /// </summary>
    private static List<string> ReadDeleteCommandColumns(string triggerSql)
    {

        foreach (string statement in CollapseWhitespace(triggerSql).Split(';'))
        {

            if (!statement.Contains("'delete'", StringComparison.Ordinal))
            {

                continue;

            }

            int open = statement.IndexOf('(');

            int close = statement.IndexOf(')');

            if (open < 0 || close < open)
            {

                break;

            }

            return [.. statement[(open + 1)..close].Split(',').Select(static column => column.Trim())];

        }

        throw new InvalidOperationException("The trigger declares no FTS5 'delete' command.");

    }

    private static string InsertDocumentSql(
        int searchRowId,
        string entryId,
        int lifecycleCode,
        string normalizedKey,
        string? authoredContent,
        string? compiledContent) =>
        $"""
        INSERT INTO covenant_search_documents (
            SearchRowId,
            EntryId,
            LaneCode,
            VersionId,
            ScopeCode,
            CampaignId,
            LifecycleCode,
            NormalizedKey,
            AuthoredContent,
            CompiledContent,
            DatasetGeneration,
            CanonicalSearchSequence)
        VALUES (
            {searchRowId},
            '{entryId}',
            1,
            '{VersionId}',
            1,
            NULL,
            {lifecycleCode},
            '{normalizedKey}',
            {TextLiteral(authoredContent)},
            {TextLiteral(compiledContent)},
            X'000102030405060708090A0B0C0D0E0F',
            1);
        """;

    private static string TextLiteral(string? value) =>
        value is null
            ? "NULL"
            : $"'{value}'";

    private static string CollapseWhitespace(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

}
