using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// The write-time refusal that keeps every stored identity on one spelling: a guard per governed
/// column, refusing a value that is not uppercase <i>and</i> dashed <i>and</i> 36 characters, whatever
/// produced it.
/// </summary>
/// <remarks>
/// <b>Every write below is a raw command rather than a production writer, and that is a statement about
/// the code rather than a convenience.</b> Every writer of every column in this family now renders a
/// <see cref="Guid"/> through the house helper or hands the provider a raw <c>Guid</c>, which the
/// SQLite value binder uppercases unconditionally - the conversions that made that true landed before
/// these guards did, which is the whole reason the data could be settled at all. So there is no
/// production path left that can produce a non-canonical identity for any of these columns, and a test
/// that claimed to drive one would be dressing up a raw insert. The guard exists for the writer nobody
/// has written yet: an interpolation, a string copied out of a foreign archive, a hand edit, or SQL a
/// later change adds without knowing this family exists. What each case proves is that such a write is
/// refused at the row rather than at the writer.
///
/// <para>Both wrong spellings are exercised for every column, because a case-only check passes one of
/// them in silence. <c>Guid.ToString("N")</c> renders 32 uppercase hex characters, which is already its
/// own <c>upper()</c> image; the register this family retires and an earlier contract test both shipped
/// a case-only predicate and both were inadequate for exactly that reason. A canonical write is asserted
/// to be accepted for every column too, so a guard that simply aborted everything could not pass this
/// suite.</para>
///
/// <para><b>The two shapes this family settled on.</b> One trigger per column rather than one per table:
/// <c>RAISE(ABORT, …)</c> takes a string literal, so a trigger covering several columns cannot name the
/// one that failed, and five of the twelve guarded tables carry identity-shaped columns that are
/// deliberately outside this family - a table-level name would claim a coverage the trigger does not
/// have. And <c>BEFORE INSERT</c> always, plus <c>BEFORE UPDATE OF</c> that column wherever the table
/// does not already refuse every update: <c>assistant_entry_finalizations</c> and
/// <c>artifact_sensitivity</c> both abort every update whatever it changes, so an update-time identity
/// check on either could never be reached. See <c>Sessions_Id_guard_identity_insert</c> and its update
/// sibling, which carry the full reasoning.</para>
/// </remarks>
public sealed class IdentitySpellingGuardTests
{

    /// <summary>A Campaign, spelled the way the object-relational writer spells one.</summary>
    private const string Campaign = "A0000000-0000-4000-8000-0000000000C1";

    /// <summary>The Session every seeded row hangs from.</summary>
    private const string Session = "B0000000-0000-4000-8000-0000000000E1";

    /// <summary>The Entry every seeded row hangs from.</summary>
    private const string Entry = "C0000000-0000-4000-8000-000000000011";

    /// <summary>The attachment every provenance row names.</summary>
    private const string Attachment = "D0000000-0000-4000-8000-0000000000A1";

    /// <summary>A second identity, used wherever a case has to write a row beside the seeded one.</summary>
    private const string Second = "E0000000-0000-4000-8000-0000000000F1";

    /// <summary>
    /// A third identity, canonical, used only to attempt a rewrite the schema is expected to refuse for
    /// a reason that has nothing to do with spelling.
    /// </summary>
    private const string Rewrite = "E0000000-0000-4000-8000-0000000000F2";

    /// <summary>A second Session, so a case can write a row that references one the seed did not use.</summary>
    private const string SecondSession = "B0000000-0000-4000-8000-0000000000E2";

    /// <summary>A second attachment, so a foreign-key child can be written beside the seeded one.</summary>
    private const string SecondAttachment = "D0000000-0000-4000-8000-0000000000A2";

    private const string Timestamp = "2026-01-01T00:00:00.0000000+00:00";

    /// <summary>A Saga memory identity, which the provenance table keys on.</summary>
    private const string Memory = "11111111-0000-4000-8000-000000000001";

    /// <summary>A second Saga memory, so a case can write a provenance row beside the seeded one.</summary>
    private const string SecondMemory = "11111111-0000-4000-8000-000000000002";

    /// <summary>
    /// A Lexicon entry identity, deliberately in the dash-free form its own writer renders. It is one of
    /// the two columns excluded from this family by design, and seeding it any other way would
    /// misrepresent what that table holds.
    /// </summary>
    private const string LexiconEntry = "22222222000040008000000000000001";

    static IdentitySpellingGuardTests() => SqliteNativeRuntime.Instance.Initialize();

    /// <summary>
    /// Every identity column this change governs: the columns the version-5 sweep declares, plus
    /// <c>artifact_sensitivity.SessionId</c>, which is left to its guard rather than to a count taken
    /// once.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="IdentitySpellingBackfill.VerifiedColumns"/> rather than restated, so a
    /// column added to the sweep without a guard fails
    /// <see cref="Every_governed_identity_column_carries_the_guards_its_table_can_hold"/> rather than
    /// being quietly uncovered.
    /// </remarks>
    internal static IReadOnlyList<(string Table, string Column)> GovernedColumns =>
    [
        .. IdentitySpellingBackfill.VerifiedColumns,
        ("artifact_sensitivity", "SessionId"),
    ];

    /// <summary>
    /// The governed columns that carry no update-time identity guard, because the schema already refuses
    /// every update such a guard could judge.
    /// </summary>
    /// <remarks>
    /// Pinned here so a table that later loses its refusal is noticed, and kept per column rather than
    /// per table because the three entries do not share one reason.
    /// <c>assistant_entry_finalizations_guard_update</c> aborts every update to that table, because a
    /// finalization is terminal; <c>artifact_sensitivity_guard_update</c> does the same, because a label
    /// is immutable evidence about one exact artifact revision. <c>session_campaign_bindings</c> refuses
    /// no update in general - it refuses one that <i>changes</i> <c>SessionId</c>, so the only update a
    /// <c>BEFORE UPDATE OF "SessionId"</c> guard could ever see is one setting the column to the value it
    /// already holds. In all three cases the guard would be unreachable code in the schema.
    /// </remarks>
    internal static IReadOnlyList<(string Table, string Column)> ColumnsWithNoUpdateGuard =>
    [
        ("assistant_entry_finalizations", "AssistantEntryId"),
        ("assistant_entry_finalizations", "SessionId"),
        ("artifact_sensitivity", "SessionId"),
        ("session_campaign_bindings", "SessionId"),
    ];

    public static TheoryData<string, string> GuardedInsertColumns
    {

        get
        {

            TheoryData<string, string> data = [];

            foreach ((string table, string column) in GovernedColumns)
            {

                data.Add(table, column);

            }

            return data;

        }

    }

    public static TheoryData<string, string> GuardedUpdateColumns
    {

        get
        {

            TheoryData<string, string> data = [];

            foreach ((string table, string column) in GovernedColumns)
            {

                if (ColumnsWithNoUpdateGuard.Contains((table, column)))
                {

                    continue;

                }

                data.Add(table, column);

            }

            return data;

        }

    }

    /// <summary>
    /// The lowercase dashed form - the spelling six shipped writers once rendered, and the one every
    /// normalised comparison in this repository was added to tolerate.
    /// </summary>
    [Theory]
    [MemberData(nameof(GuardedInsertColumns))]
    public async Task A_lowercase_identity_is_refused_at_the_insert(string table, string column)
    {

        await using GuardHarness harness = await GuardHarness.StartAsync(table);

        SqliteException failure = await Assert.ThrowsAsync<SqliteException>(
            () => harness.InsertAsync(table, column, Lowercase(SecondFor(table, column))));

        Assert.Contains(Message(table, column), failure.Message, StringComparison.Ordinal);

    }

    /// <summary>
    /// The dash-free form, which is the one that matters: <c>Guid.ToString("N")</c> renders 32 uppercase
    /// hex characters, so it is already its own <c>upper()</c> image and a case-only guard would accept
    /// it in silence. Two columns in this schema legitimately hold that form, so it is not hypothetical.
    /// </summary>
    [Theory]
    [MemberData(nameof(GuardedInsertColumns))]
    public async Task A_dash_free_identity_is_refused_at_the_insert(string table, string column)
    {

        await using GuardHarness harness = await GuardHarness.StartAsync(table);

        string dashFree = DashFree(SecondFor(table, column));

        Assert.Equal(dashFree, dashFree.ToUpperInvariant());

        SqliteException failure = await Assert.ThrowsAsync<SqliteException>(
            () => harness.InsertAsync(table, column, dashFree));

        Assert.Contains(Message(table, column), failure.Message, StringComparison.Ordinal);

    }

    /// <summary>
    /// A guard that aborted every write would satisfy every case above while refusing the writes
    /// production actually makes, so the canonical spelling is asserted to pass through the same path.
    /// </summary>
    [Theory]
    [MemberData(nameof(GuardedInsertColumns))]
    public async Task A_canonical_identity_is_accepted_at_the_insert(string table, string column)
    {

        await using GuardHarness harness = await GuardHarness.StartAsync(table);

        await harness.InsertAsync(table, column, SecondFor(table, column));

        // The row is counted before its spelling is judged. A non-canonical count of zero is also what
        // an empty table reports, so on its own it would have accepted a guard that silently swallowed
        // the write - which is the same shape of vacuous pass this family keeps producing.
        Assert.Equal(
            1L,
            await harness.RowCountAsync(table, column, SecondFor(table, column)));

        Assert.Equal(0L, await harness.NonCanonicalCountAsync(table, column));

    }

    /// <summary>
    /// The update half, on every table that does not already refuse every update. The row being written
    /// is the seeded one, so what the guard refuses is a rewrite of an identity that was canonical a
    /// statement ago.
    /// </summary>
    [Theory]
    [MemberData(nameof(GuardedUpdateColumns))]
    public async Task A_lowercase_identity_is_refused_at_the_update(string table, string column)
    {

        await using GuardHarness harness = await GuardHarness.StartAsync(table);

        SqliteException failure = await Assert.ThrowsAsync<SqliteException>(
            () => harness.UpdateAsync(table, column, Lowercase(SecondFor(table, column))));

        Assert.Contains(Message(table, column), failure.Message, StringComparison.Ordinal);

    }

    /// <summary>The dash-free half of the update guard, for the reason the insert case gives.</summary>
    [Theory]
    [MemberData(nameof(GuardedUpdateColumns))]
    public async Task A_dash_free_identity_is_refused_at_the_update(string table, string column)
    {

        await using GuardHarness harness = await GuardHarness.StartAsync(table);

        SqliteException failure = await Assert.ThrowsAsync<SqliteException>(
            () => harness.UpdateAsync(table, column, DashFree(SecondFor(table, column))));

        Assert.Contains(Message(table, column), failure.Message, StringComparison.Ordinal);

    }

    public static TheoryData<string, string> ColumnsWhoseUpdateIsAlreadyRefused
    {

        get
        {

            TheoryData<string, string> data = [];

            foreach ((string table, string column) in ColumnsWithNoUpdateGuard)
            {

                data.Add(table, column);

            }

            return data;

        }

    }

    /// <summary>
    /// Every column this family deliberately leaves without an update guard is a column the schema
    /// already refuses to let an update reach.
    /// </summary>
    /// <remarks>
    /// <b>This is the pin the omission rests on, and without it the omission is only an assertion.</b>
    /// <see cref="Every_governed_identity_column_carries_the_guards_its_table_can_hold"/> checks that the
    /// trigger is absent, which is the inverse claim: it would stay green if a table quietly lost the
    /// refusal that made the absence safe, leaving the column with no update-time protection at all from
    /// either direction. This case drives the write instead and asserts the schema still turns it back.
    ///
    /// <para>The value written is canonical, so an identity guard would not have fired on it even if one
    /// existed - what turns the write back is the table's own rule. The message is asserted <i>not</i> to
    /// be this family's, which is the whole point: these three columns are protected by something else,
    /// and an identity guard on them would be unreachable code in the schema.</para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(ColumnsWhoseUpdateIsAlreadyRefused))]
    public async Task A_column_with_no_update_guard_is_one_the_schema_already_refuses_to_update(
        string table,
        string column)
    {

        await using GuardHarness harness = await GuardHarness.StartAsync(table);

        await harness.InsertAsync(table, column, SecondFor(table, column));

        SqliteException failure = await Assert.ThrowsAsync<SqliteException>(
            () => harness.UpdateAsync(table, column, Canonical(Rewrite)));

        Assert.DoesNotContain(Message(table, column), failure.Message, StringComparison.Ordinal);

    }

    /// <summary>
    /// A guarded column that is NULL is not judged, because there is no identity there to be spelled
    /// wrongly - and a Campaign deletion clearing <c>Sessions."CampaignId"</c> is a real write that
    /// depends on it.
    /// </summary>
    [Fact]
    public async Task A_null_reference_passes_both_halves_of_its_guard()
    {

        await using GuardHarness harness = await GuardHarness.StartAsync("Sessions");

        await harness.ExecuteAsync(
            """UPDATE "Sessions" SET "CampaignId" = NULL;""");

        Assert.Equal(0L, await harness.NonCanonicalCountAsync("Sessions", "CampaignId"));

    }

    /// <summary>
    /// The closed inventory: every column this change governs has an insert guard in the shipped
    /// catalog, and an update guard unless its table refuses every update.
    /// </summary>
    /// <remarks>
    /// Without this a column added to <see cref="IdentitySpellingBackfill.VerifiedColumns"/> would be
    /// counted by the sweep and guarded by nothing, and every case above would stay green because none
    /// of them knows the column exists. It also pins the naming, which is what makes a guard findable
    /// from the column it protects.
    /// </remarks>
    [Fact]
    public void Every_governed_identity_column_carries_the_guards_its_table_can_hold()
    {

        HashSet<string> objects =
        [
            .. GrimoireSchemaCatalog.CoreObjects.Select(static definition => definition.Name),
        ];

        HashSet<string> statements =
        [
            .. GrimoireSchemaCatalog.TransitionStatements
                .Where(static statement => statement.TransactionTier == GrimoireSchemaTransactionTier.Core)
                .Select(static statement => statement.Name),
        ];

        foreach ((string table, string column) in GovernedColumns)
        {

            string insert = $"{table}_{column}_guard_identity_insert";

            Assert.Contains(insert, objects);

            Assert.Contains(insert, statements);

            string update = $"{table}_{column}_guard_identity_update";

            if (ColumnsWithNoUpdateGuard.Contains((table, column)))
            {

                Assert.DoesNotContain(update, objects);

                continue;

            }

            Assert.Contains(update, objects);

            Assert.Contains(update, statements);

        }

    }

    /// <summary>
    /// Every guard object in the tree is byte-identical to the version-5 statement that installs it, so
    /// an upgraded installation and a fresh one hold the same trigger rather than two that merely look
    /// alike.
    /// </summary>
    [Fact]
    public void Every_guard_is_the_same_text_in_the_head_tree_and_in_the_version_five_step()
    {

        Dictionary<string, string> statements = GrimoireSchemaCatalog.TransitionStatements
            .Where(static statement =>
                statement.TransactionTier == GrimoireSchemaTransactionTier.Core
                && statement.ToVersion == 5)
            .ToDictionary(
                static statement => statement.Name,
                static statement => statement.Sql,
                StringComparer.Ordinal);

        Assert.NotEmpty(statements);

        foreach (GrimoireSchemaObject definition in GrimoireSchemaCatalog.CoreObjects
            .Where(static definition => definition.Name.Contains("_guard_identity_", StringComparison.Ordinal)))
        {

            Assert.True(
                statements.TryGetValue(definition.Name, out string? statement),
                $"{definition.Name} is a head object with no version-5 statement to install it.");

            Assert.Equal(definition.Sql, statement);

        }

        Assert.Equal(
            statements.Count,
            GrimoireSchemaCatalog.CoreObjects.Count(static definition =>
                definition.Name.Contains("_guard_identity_", StringComparison.Ordinal)));

    }

    /// <summary>The message a developer sees, which has to name the column and the form it expects.</summary>
    private static string Message(string table, string column) =>
        $"{table}.{column} must be stored as an uppercase dashed 36-character identity.";

    /// <summary>The canonical spelling: uppercase, dashed, 36 characters, as the provider renders it.</summary>
    private static string Canonical(string identity) => identity.ToUpperInvariant();

    /// <summary>The minority spelling, as a bare <c>ToString()</c> renders it.</summary>
    private static string Lowercase(string identity) => identity.ToLowerInvariant();

    /// <summary>
    /// The dash-free spelling, as <c>Guid.ToString("N")</c> renders it - already uppercase, which is the
    /// whole point of testing it separately.
    /// </summary>
    private static string DashFree(string identity) =>
        identity.Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    /// <summary>
    /// The identity a case writes into the column under test.
    /// </summary>
    /// <remarks>
    /// A reference column under a real foreign key names a parent the seed created, because the case that
    /// asserts a <i>canonical</i> write is accepted has to reach the insert rather than being turned back
    /// by the foreign key - which would make it pass for a reason that has nothing to do with the guard.
    /// The refusal cases spell the same value wrongly and never reach the foreign key at all, since a
    /// BEFORE INSERT trigger runs before the constraint is checked.
    /// </remarks>
    private static string SecondFor(string table, string column) =>
        (table, column) switch
        {

            ("Entries", "SessionId") => SecondSession,

            ("assistant_entry_finalizations", "SessionId") => SecondSession,

            ("session_sensitivity_state", "SessionId") => SecondSession,

            ("session_attachment_chunks", "AttachmentId") => SecondAttachment,

            ("session_attachment_index_state", "AttachmentId") => SecondAttachment,

            ("session_campaign_bindings", "SessionId") => SecondSession,

            _ => Second,

        };

    /// <summary>
    /// One open scratch installation of the shipped head tree, seeded with exactly one canonical row in
    /// every guarded table so an insert case can write a second row beside it and an update case can
    /// rewrite the one that is there.
    /// </summary>
    private sealed class GuardHarness : IAsyncDisposable
    {

        private readonly EvolutionScratchDatabase _file;

        private readonly SqliteConnection _connection;

        private GuardHarness(EvolutionScratchDatabase file, SqliteConnection connection)
        {

            _file = file;

            _connection = connection;

        }

        /// <summary>
        /// Installs the shipped tree and seeds the parents the table under test needs.
        /// </summary>
        /// <remarks>
        /// <c>assistant_entry_finalizations</c> is the one table whose row cannot be written at all
        /// against the live schema: <c>assistant_entry_finalizations_validate_insert</c> demands a
        /// consumed capacity reservation for the same Session and assistant identity, and minting one
        /// needs an authorized turn-capacity mutation scope that direct SQL cannot open by design. That
        /// unrelated precondition guard is dropped so the identity guard is the thing being exercised;
        /// nothing else about the tree is changed, and the guard under test is never touched.
        /// </remarks>
        internal static async Task<GuardHarness> StartAsync(string table)
        {

            EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

            SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

            _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 1536, CancellationToken.None);

            GuardHarness harness = new(file, connection);

            await harness.SeedAsync(table);

            return harness;

        }

        /// <summary>
        /// Runs one write inside the narrow, false-by-default scope its own guard demands.
        /// </summary>
        /// <remarks>
        /// <c>session_campaign_bindings_guard_insert</c> refuses any insert made without the Session
        /// binding write scope, which is the same authority production borrows. Opening it here rather
        /// than dropping the trigger keeps the identity guard the only thing under test while leaving the
        /// rest of the table's rules in force.
        /// </remarks>
        internal async Task AuthorizedAsync(CovenantSqliteAuthorizationKind kind, Func<Task> write)
        {

            using CovenantSqliteAuthorizationScope scope =
                CovenantSqliteConnectionInitializer.Instance.Authorize(_connection, kind);

            await write().ConfigureAwait(false);

        }

        internal async Task ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
        {

            await using SqliteCommand command = _connection.CreateCommand();

            command.CommandText = sql;

            foreach ((string name, object? value) in parameters)
            {

                _ = command.Parameters.AddWithValue(name, value ?? DBNull.Value);

            }

            _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

        }

        /// <summary>How many rows hold exactly this value in this column.</summary>
        internal async Task<long> RowCountAsync(string table, string column, string value)
        {

            await using SqliteCommand command = _connection.CreateCommand();

            command.CommandText = $"""SELECT COUNT(*) FROM "{table}" WHERE "{column}" = $value;""";

            _ = command.Parameters.AddWithValue("$value", value);

            return Convert.ToInt64(
                await command.ExecuteScalarAsync(CancellationToken.None),
                System.Globalization.CultureInfo.InvariantCulture);

        }

        /// <summary>Asks the sweep's own question of one column, so no case carries a second copy of it.</summary>
        internal Task<long> NonCanonicalCountAsync(string table, string column) =>
            IdentitySpellingBackfill.CountNonCanonicalAsync(
                _connection,
                transaction: null,
                table,
                column,
                CancellationToken.None);

        /// <summary>Writes a row into one guarded table with the column under test set to a given spelling.</summary>
        internal Task InsertAsync(string table, string column, string value) =>
            (table, column) switch
            {

                ("Sessions", "Id") => ExecuteAsync(
                    """
                    INSERT INTO "Sessions" ("Id", "CampaignId", "Status", "CreatedAt", "UpdatedAt")
                    VALUES ($value, $campaign, 'active', $now, $now);
                    """,
                    ("$value", value),
                    ("$campaign", Canonical(Campaign)),
                    ("$now", Timestamp)),

                ("Sessions", "CampaignId") => ExecuteAsync(
                    """
                    INSERT INTO "Sessions" ("Id", "CampaignId", "Status", "CreatedAt", "UpdatedAt")
                    VALUES ($id, $value, 'active', $now, $now);
                    """,
                    ("$id", Canonical(Second)),
                    ("$value", value),
                    ("$now", Timestamp)),

                ("Campaigns", "Id") => ExecuteAsync(
                    """
                    INSERT INTO "Campaigns" ("Id", "Name", "NameLower", "Path", "Type", "Settings", "CreatedAt", "UpdatedAt")
                    VALUES ($value, 'Beta', 'beta', '/campaigns/beta', 0, '{}', $now, $now);
                    """,
                    ("$value", value),
                    ("$now", Timestamp)),

                ("Entries", "Id") => ExecuteAsync(
                    """
                    INSERT INTO "Entries" ("Id", "SessionId", "Role", "Content", "ModelUsed", "CreatedAt", "Sequence")
                    VALUES ($value, $session, 0, 'content', 'model', $now, 2);
                    """,
                    ("$value", value),
                    ("$session", Canonical(Session)),
                    ("$now", Timestamp)),

                ("Entries", "SessionId") => ExecuteAsync(
                    """
                    INSERT INTO "Entries" ("Id", "SessionId", "Role", "Content", "ModelUsed", "CreatedAt", "Sequence")
                    VALUES ($id, $value, 0, 'content', 'model', $now, 3);
                    """,
                    ("$id", Canonical(Second)),
                    ("$value", value),
                    ("$now", Timestamp)),

                ("entry_embeddings", "EntryId") => ExecuteAsync(
                    "INSERT INTO entry_embeddings (EntryId, Embedding, Dim) VALUES ($value, zeroblob(8), 2);",
                    ("$value", value)),

                ("assistant_entry_finalizations", "AssistantEntryId") => ExecuteAsync(
                    """
                    INSERT INTO assistant_entry_finalizations (
                        AssistantEntryId, SessionId, OutcomeCode, ContentSensitivityCode,
                        ContentSensitivityDigest, RequestDigest, FinalizedAtUtc)
                    VALUES ($value, $session, 1, 0, zeroblob(32), zeroblob(32), $now);
                    """,
                    ("$value", value),
                    ("$session", Canonical(Session)),
                    ("$now", Timestamp)),

                ("assistant_entry_finalizations", "SessionId") => ExecuteAsync(
                    """
                    INSERT INTO assistant_entry_finalizations (
                        AssistantEntryId, SessionId, OutcomeCode, ContentSensitivityCode,
                        ContentSensitivityDigest, RequestDigest, FinalizedAtUtc)
                    VALUES ($id, $value, 1, 0, zeroblob(32), zeroblob(32), $now);
                    """,
                    ("$id", Canonical(Second)),
                    ("$value", value),
                    ("$now", Timestamp)),

                ("session_sensitivity_state", "SessionId") => ExecuteAsync(
                    """
                    INSERT INTO session_sensitivity_state (
                        SessionId, TaintedArtifactCount, MaximumSensitivityCode,
                        GenerationProvenanceDigest, Revision, UpdatedAtUtc)
                    VALUES ($value, 0, 0, zeroblob(32), 0, $now);
                    """,
                    ("$value", value),
                    ("$now", Timestamp)),

                ("SessionAttachments", "Id") => ExecuteAsync(
                    AttachmentInsert,
                    ("$id", value),
                    ("$session", Canonical(Session)),
                    ("$entry", Canonical(Entry)),
                    ("$key", "second"),
                    ("$now", Timestamp)),

                ("SessionAttachments", "SessionId") => ExecuteAsync(
                    AttachmentInsert,
                    ("$id", Canonical(Second)),
                    ("$session", value),
                    ("$entry", Canonical(Entry)),
                    ("$key", "second"),
                    ("$now", Timestamp)),

                ("SessionAttachments", "EntryId") => ExecuteAsync(
                    AttachmentInsert,
                    ("$id", Canonical(Second)),
                    ("$session", Canonical(Session)),
                    ("$entry", value),
                    ("$key", "second"),
                    ("$now", Timestamp)),

                ("session_attachment_chunks", "AttachmentId") => ExecuteAsync(
                    """
                    INSERT INTO session_attachment_chunks (
                        ChunkId, GenerationId, SessionId, AttachmentId, LogicalKey, Version,
                        OriginalFileName, MimeType, ContentSha256, ChunkIndex, CharacterStart,
                        CharacterEnd, StartLine, EndLine, Content, EmbeddingDimension, ExtractedAt,
                        IndexedAt)
                    VALUES ($chunk, 'g2', $session, $value, 'key', 1, 'f.txt', 'text/plain', 'sha',
                            1, 0, 1, 1, 1, 'body', 1536, $now, $now);
                    """,
                    ("$chunk", Canonical(Second)),
                    ("$session", Session.ToLowerInvariant()),
                    ("$value", value),
                    ("$now", Timestamp)),

                ("session_attachment_index_state", "AttachmentId") => ExecuteAsync(
                    """
                    INSERT INTO session_attachment_index_state (AttachmentId, Status, ContentSha256, UpdatedAt)
                    VALUES ($value, 'Indexed', 'sha', $now);
                    """,
                    ("$value", value),
                    ("$now", Timestamp)),

                ("attachment_memory_consultations", "AttachmentId") => ExecuteAsync(
                    """
                    INSERT INTO attachment_memory_consultations (
                        SourceEntryId, SessionId, AttachmentId, LogicalKey, Version, ContentHash,
                        MaterializedAt, SourceType)
                    VALUES ($entry, $session, $value, 'key', 1, 'hash', $now, 'Attachment');
                    """,
                    ("$entry", Canonical(Entry)),
                    ("$session", Canonical(Session)),
                    ("$value", value),
                    ("$now", Timestamp)),

                ("saga_memory_attachment_provenance", "AttachmentId") => ExecuteAsync(
                    """
                    INSERT INTO saga_memory_attachment_provenance (
                        MemoryId, SessionId, AttachmentId, LogicalKey, Version, ContentHash,
                        MaterializedAt, SourceType)
                    VALUES ($memory, $session, $value, 'key', 1, 'hash', $now, 'Attachment');
                    """,
                    ("$memory", Canonical(SecondMemory)),
                    ("$session", Canonical(Session)),
                    ("$value", value),
                    ("$now", Timestamp)),

                ("lexicon_fact_attachment_provenance", "AttachmentId") => ExecuteAsync(
                    """
                    INSERT INTO lexicon_fact_attachment_provenance (
                        EntryId, FactHash, Fact, SessionId, AttachmentId, LogicalKey, Version,
                        ContentHash, MaterializedAt, SourceType)
                    VALUES ($lexicon, 'hash-2', 'fact', $session, $value, 'key', 1, 'hash', $now,
                            'Attachment');
                    """,
                    ("$lexicon", LexiconEntry),
                    ("$session", Canonical(Session)),
                    ("$value", value),
                    ("$now", Timestamp)),

                ("session_campaign_bindings", "SessionId") => AuthorizedAsync(
                    CovenantSqliteAuthorizationKind.SessionBindingWrite,
                    () => ExecuteAsync(
                        """
                        INSERT INTO session_campaign_bindings (SessionId, BindingKindCode, CampaignId, BoundAtUtc)
                        VALUES ($value, 1, NULL, $now);
                        """,
                        ("$value", value),
                        ("$now", Timestamp))),

                ("artifact_sensitivity", "SessionId") => ExecuteAsync(
                    ArtifactSensitivityInsert,
                    ("$label", Canonical(Second)),
                    ("$artifact", Canonical(Second)),
                    ("$value", value),
                    ("$now", Timestamp)),

                _ => throw new InvalidOperationException($"No insert template for {table}.{column}."),

            };

        /// <summary>
        /// Rewrites the seeded row's identity column, which is the write a
        /// <c>BEFORE UPDATE OF &lt;column&gt;</c> guard exists to judge.
        /// </summary>
        /// <remarks>
        /// No <c>WHERE</c> clause, deliberately: the statement names the column and nothing else, which
        /// is the shape that makes <c>UPDATE OF</c> fire at all. It rewrites every row of the table, and
        /// for three of them - <c>"Sessions"</c>, <c>"SessionAttachments"</c> and <c>saga_memories</c> -
        /// the seed holds two rather than one, because a foreign-key child needs a second parent to name.
        /// That is harmless here and worth stating rather than implying: what the case asserts is that
        /// the write is refused, and one refused row is enough to refuse the statement.
        /// </remarks>
        internal Task UpdateAsync(string table, string column, string value) =>
            ExecuteAsync(
                $"""UPDATE "{table}" SET "{column}" = $value;""",
                ("$value", value));

        public async ValueTask DisposeAsync()
        {

            await _connection.DisposeAsync();

            _file.Dispose();

        }

        /// <summary>
        /// One canonical row in every guarded table, plus the parents a foreign key demands. Seeded in
        /// dependency order and always in full, so an insert case and an update case see the same
        /// installation whatever table they name.
        /// </summary>
        private async Task SeedAsync(string table)
        {

            if (string.Equals(table, "assistant_entry_finalizations", StringComparison.Ordinal))
            {

                await ExecuteAsync(
                    "DROP TRIGGER IF EXISTS assistant_entry_finalizations_validate_insert;");

            }

            await ExecuteAsync(
                """
                INSERT INTO "Campaigns" ("Id", "Name", "NameLower", "Path", "Type", "Settings", "CreatedAt", "UpdatedAt")
                VALUES ($id, 'Alpha', 'alpha', '/campaigns/alpha', 0, '{}', $now, $now);
                """,
                ("$id", Canonical(Campaign)),
                ("$now", Timestamp));

            await ExecuteAsync(
                """
                INSERT INTO "Sessions" ("Id", "CampaignId", "Status", "CreatedAt", "UpdatedAt")
                VALUES ($id, $campaign, 'active', $now, $now);
                """,
                ("$id", Canonical(Session)),
                ("$campaign", Canonical(Campaign)),
                ("$now", Timestamp));

            // A second Session and a second attachment exist so that the case asserting a canonical write
            // is accepted has a parent to name. Without them a reference column under a foreign key would
            // be turned back by the constraint and the case would pass for a reason unrelated to its guard.
            await ExecuteAsync(
                """
                INSERT INTO "Sessions" ("Id", "CampaignId", "Status", "CreatedAt", "UpdatedAt")
                VALUES ($id, $campaign, 'active', $now, $now);
                """,
                ("$id", Canonical(SecondSession)),
                ("$campaign", Canonical(Campaign)),
                ("$now", Timestamp));

            await ExecuteAsync(
                """
                INSERT INTO "Entries" ("Id", "SessionId", "Role", "Content", "ModelUsed", "CreatedAt", "Sequence")
                VALUES ($id, $session, 0, 'content', 'model', $now, 1);
                """,
                ("$id", Canonical(Entry)),
                ("$session", Canonical(Session)),
                ("$now", Timestamp));

            await ExecuteAsync(
                "INSERT INTO entry_embeddings (EntryId, Embedding, Dim) VALUES ($id, zeroblob(8), 2);",
                ("$id", Canonical(Entry)));

            await ExecuteAsync(
                """
                INSERT INTO session_sensitivity_state (
                    SessionId, TaintedArtifactCount, MaximumSensitivityCode,
                    GenerationProvenanceDigest, Revision, UpdatedAtUtc)
                VALUES ($session, 0, 0, zeroblob(32), 0, $now);
                """,
                ("$session", Canonical(Session)),
                ("$now", Timestamp));

            await ExecuteAsync(
                AttachmentInsert,
                ("$id", Canonical(Attachment)),
                ("$session", Canonical(Session)),
                ("$entry", Canonical(Entry)),
                ("$key", "seed"),
                ("$now", Timestamp));

            await ExecuteAsync(
                AttachmentInsert,
                ("$id", Canonical(SecondAttachment)),
                ("$session", Canonical(SecondSession)),
                ("$entry", Canonical(Entry)),
                ("$key", "spare"),
                ("$now", Timestamp));

            await ExecuteAsync(
                """
                INSERT INTO session_attachment_chunks (
                    ChunkId, GenerationId, SessionId, AttachmentId, LogicalKey, Version,
                    OriginalFileName, MimeType, ContentSha256, ChunkIndex, CharacterStart,
                    CharacterEnd, StartLine, EndLine, Content, EmbeddingDimension, ExtractedAt,
                    IndexedAt)
                VALUES ($chunk, 'g1', $session, $attachment, 'key', 1, 'f.txt', 'text/plain', 'sha',
                        0, 0, 1, 1, 1, 'body', 1536, $now, $now);
                """,
                ("$chunk", Canonical(Entry)),
                ("$session", Session.ToLowerInvariant()),
                ("$attachment", Canonical(Attachment)),
                ("$now", Timestamp));

            await ExecuteAsync(
                """
                INSERT INTO session_attachment_index_state (AttachmentId, Status, ContentSha256, UpdatedAt)
                VALUES ($attachment, 'Indexed', 'sha', $now);
                """,
                ("$attachment", Canonical(Attachment)),
                ("$now", Timestamp));

            await ExecuteAsync(
                """
                INSERT INTO attachment_memory_consultations (
                    SourceEntryId, SessionId, AttachmentId, LogicalKey, Version, ContentHash,
                    MaterializedAt, SourceType)
                VALUES ($entry, $session, $attachment, 'key', 1, 'hash', $now, 'Attachment');
                """,
                ("$entry", Canonical(Entry)),
                ("$session", Canonical(Session)),
                ("$attachment", Canonical(Attachment)),
                ("$now", Timestamp));

            await ExecuteAsync(
                """
                INSERT INTO saga_memories (Id, Content, CreatedAt) VALUES ($id, 'memory', $now);
                """,
                ("$id", Canonical(Memory)),
                ("$now", Timestamp));

            await ExecuteAsync(
                """
                INSERT INTO saga_memories (Id, Content, CreatedAt) VALUES ($id, 'second', $now);
                """,
                ("$id", Canonical(SecondMemory)),
                ("$now", Timestamp));

            await ExecuteAsync(
                """
                INSERT INTO saga_memory_attachment_provenance (
                    MemoryId, SessionId, AttachmentId, LogicalKey, Version, ContentHash,
                    MaterializedAt, SourceType)
                VALUES ($memory, $session, $attachment, 'key', 1, 'hash', $now, 'Attachment');
                """,
                ("$memory", Canonical(Memory)),
                ("$session", Canonical(Session)),
                ("$attachment", Canonical(Attachment)),
                ("$now", Timestamp));

            await ExecuteAsync(
                """
                INSERT INTO lexicon_entries (Id, Name, NameNormalized, Type, FactsJson, FactsText, UpdatedAt)
                VALUES ($id, 'Name', 'name', 'Thing', '[]', '', $now);
                """,
                ("$id", LexiconEntry),
                ("$now", Timestamp));

            await ExecuteAsync(
                """
                INSERT INTO lexicon_fact_attachment_provenance (
                    EntryId, FactHash, Fact, SessionId, AttachmentId, LogicalKey, Version,
                    ContentHash, MaterializedAt, SourceType)
                VALUES ($lexicon, 'hash-1', 'fact', $session, $attachment, 'key', 1, 'hash', $now,
                        'Attachment');
                """,
                ("$lexicon", LexiconEntry),
                ("$session", Canonical(Session)),
                ("$attachment", Canonical(Attachment)),
                ("$now", Timestamp));

            await AuthorizedAsync(
                CovenantSqliteAuthorizationKind.SessionBindingWrite,
                () => ExecuteAsync(
                    """
                    INSERT INTO session_campaign_bindings (SessionId, BindingKindCode, CampaignId, BoundAtUtc)
                    VALUES ($value, 1, NULL, $now);
                    """,
                    ("$value", Canonical(Session)),
                    ("$now", Timestamp)));

            await ExecuteAsync(
                ArtifactSensitivityInsert,
                ("$label", Canonical(Attachment)),
                ("$artifact", Canonical(Entry)),
                ("$value", Canonical(Session)),
                ("$now", Timestamp));

        }

        /// <summary>An attachment row, with the three guarded columns and the logical key parameterised.</summary>
        private const string AttachmentInsert =
            """
            INSERT INTO "SessionAttachments" (
                "Id", "SessionId", "EntryId", "State", "LogicalKey", "OriginalFileName", "Version",
                "RelativePath", "ContentSha256", "MimeType", "ByteLength", "Kind", "CreatedAt")
            VALUES ($id, $session, $entry, 'Bound', $key, 'f.txt', 1, 'p/f.txt', 'sha', 'text/plain',
                    1, 'Text', $now);
            """;

        /// <summary>
        /// A Covenant-derived artifact label, in the exact-provenance mode its CHECK constraints demand:
        /// one 16-byte generation identity, no Bloom, and three 32-byte digests.
        /// </summary>
        private const string ArtifactSensitivityInsert =
            """
            INSERT INTO artifact_sensitivity (
                LabelId, ArtifactKindCode, ArtifactId, SensitivityCode, ProvenanceModeCode,
                ExactGenerationIds, GenerationBloom, SessionId, CampaignId, TurnId,
                ArtifactRevision, ArtifactContentDigest, SensitivityDigest, ArtifactLabelDigest,
                CreatedAtUtc)
            VALUES ($label, 1, $artifact, 1, 1, zeroblob(16), NULL, $value, NULL, NULL,
                    1, zeroblob(32), zeroblob(32), zeroblob(32), $now);
            """;

    }

}
