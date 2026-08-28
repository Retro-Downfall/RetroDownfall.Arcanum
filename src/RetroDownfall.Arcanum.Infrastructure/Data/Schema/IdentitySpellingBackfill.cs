using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using Serilog;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// Verifies that every stored identity holds the one canonical spelling, and repairs the references
/// that can be repaired without breaking a pairing that currently works.
/// </summary>
/// <remarks>
/// This is a verifier before it is a repair for every column but one family, and that distinction is
/// the whole design. Outside the attachment family, both writers that ever rendered the minority
/// spelling were unreachable for their entire existence - the import planner refused every archive, and
/// the merge path returns before it opens a transaction - so an installation that predates this version
/// already holds the canonical form and those counts are zero. They are still taken, and still logged
/// when they are zero, because that log line is the evidence the reasoning held in the field, and
/// because source can prove no code path wrote a bad row while it cannot prove nobody edited the
/// database by hand.
///
/// <para>The <c>SessionAttachments</c> family is the exception, and it is a repair rather than a
/// verification: six reachable writers filled its eight columns with the minority spelling, so every
/// installation that has ever held an attachment reports a number for them and has rows rewritten.</para>
///
/// <para><b>What may be repaired, and why it is narrower than "every identity column".</b> A stored
/// identity is either an <i>identity</i> - the primary key a row is known by - or a <i>reference</i> to
/// one. A reference can be rewritten to match the identity it names. An identity cannot: the schema
/// makes a Session identity immutable on purpose, and eight of its fourteen foreign-key children refuse
/// the write by trigger, four of them unconditionally
/// (<c>assistant_entry_finalizations</c>, <c>assistant_entry_erasure_receipts</c>,
/// <c>session_summary_artifacts</c> and <c>session_title_artifacts</c> abort every update, whatever it
/// changes), while <c>session_turn_quota_state</c>, <c>session_turn_claims</c>,
/// <c>assistant_finalization_capacity_reservations</c> and <c>session_campaign_bindings</c> each abort
/// specifically on a changed <c>SessionId</c>. Since <c>Sessions_turn_quota_state</c> writes one quota
/// row for every Session ever created, a Session identity cannot be moved in place on any installation
/// that has a Session at all. Attempting it would abort at <c>COMMIT</c> and leave the tier permanently
/// unable to reach head, which is worse than not repairing.</para>
///
/// <para><b>A reference is repaired only when its canonical target already exists.</b> The point of a
/// reference column is that it joins; uppercasing one whose target is itself spelled the minority way
/// would break a join that currently works in the name of fixing one that does not. The
/// <c>EXISTS</c> clause in the repair is therefore load-bearing rather than defensive - it makes the
/// repair provably a restoration of a broken pairing.</para>
///
/// <para><b>An identity may be moved where nothing refuses the write and everything that names it moves
/// with it.</b> <c>SessionAttachments.Id</c> is the one such identity in this schema and the one genuine
/// data rewrite in this work: no table that depends on it carries a trigger, so the refusals that make a
/// Session identity immutable have no counterpart here. What it does have is seven columns that name it,
/// five of them in tables the schema will not pair for us - see <see cref="RepairedFamilies"/>, which is
/// where that pairing is kept.</para>
///
/// <para>There is no cursor beyond a marker that the precondition count has been taken. The batch's own
/// predicate is the mismatch, and every row it repairs stops matching in the same transaction, so the
/// corpus shrinks by exactly the work that committed. A crash between the work and its commit leaves
/// those rows unrepaired and the next pass selects them again, which is the resumability the interface
/// asks for, reached without a position that could be advanced past uncommitted work.</para>
/// </remarks>
internal sealed class IdentitySpellingBackfill : IGrimoireSchemaBackfill
{

    /// <summary>
    /// Every identity column this step verifies, in the order the count is logged.
    /// </summary>
    /// <remarks>
    /// Internal so a test can assert the sweep's own idea of the family rather than restate it, which is
    /// how a verifier that quietly stopped covering a column would otherwise pass.
    ///
    /// <para><c>Campaigns.Id</c> is counted although nothing repairs it, and that is the point: it is the
    /// identity <c>Sessions.CampaignId</c> is repaired <i>against</i>, so a non-canonical Campaign makes
    /// the repair below decline every row. Without a count the operator would see a silent no-op with
    /// nothing saying why; with one, the decline has a number behind it.</para>
    ///
    /// <para>This is what the step verifies, not every identity column in the Grimoire. The two
    /// <c>ToString("N")</c> columns are a deliberate second canonical form and are excluded, and
    /// <c>artifact_sensitivity.SessionId</c> is left to the guard that refuses a bad write rather than to
    /// a count taken once.</para>
    ///
    /// <para><c>session_campaign_bindings.SessionId</c> is counted although its foreign key to
    /// <c>"Sessions"("Id")</c> already forces it to agree with a column this step verifies. The two say
    /// different things: a foreign key says the child matches the parent, and this count says the value
    /// is canonical. A hand-edited installation could satisfy the first and fail the second, and the
    /// operator should hear about it from a number rather than from the guard refusing the next write.
    /// </para>
    ///
    /// <para><c>session_campaign_bindings.CampaignId</c> and <c>saga_memories.CampaignId</c> are the two
    /// columns this step both counts and repairs without a target - see
    /// <see cref="RepairedColumns"/>. Unlike the attachment family, whose counts are non-zero on any
    /// installation that ever held an attachment, these two are non-zero on any installation that ever
    /// created a Session through the turn-begin path, which is every installation that has been used.
    /// </para>
    /// </remarks>
    internal static readonly IReadOnlyList<(string Table, string Column)> VerifiedColumns =
    [
        ("Sessions", "Id"),
        ("Sessions", "CampaignId"),
        ("Campaigns", "Id"),
        ("Entries", "Id"),
        ("Entries", "SessionId"),
        ("entry_embeddings", "EntryId"),
        ("assistant_entry_finalizations", "AssistantEntryId"),
        ("assistant_entry_finalizations", "SessionId"),
        ("session_sensitivity_state", "SessionId"),
        ("session_campaign_bindings", "SessionId"),
        ("session_campaign_bindings", "CampaignId"),
        ("saga_memories", "CampaignId"),
        ("SessionAttachments", "Id"),
        ("SessionAttachments", "SessionId"),
        ("SessionAttachments", "EntryId"),
        ("session_attachment_chunks", "AttachmentId"),
        ("session_attachment_index_state", "AttachmentId"),
        ("attachment_memory_consultations", "AttachmentId"),
        ("saga_memory_attachment_provenance", "AttachmentId"),
        ("lexicon_fact_attachment_provenance", "AttachmentId"),
    ];

    /// <summary>
    /// The one identity in this schema that both must move and can, together with every column that
    /// names it.
    /// </summary>
    /// <remarks>
    /// <c>SessionAttachments.Id</c> is an identity rather than a reference, so nothing above applies to
    /// it: there is no canonical target for it to be repaired against, and the reason it can move where
    /// a Session identity cannot is that the tables depending on it carry no trigger at all. Every row
    /// of it held the minority spelling, written by an attachment store that rendered a bare
    /// <c>ToString()</c>, and the five columns below were filled from the same value by five more
    /// writers that agreed with it.
    ///
    /// <para><b>Nothing but this declaration pairs the last three with their parent.</b>
    /// <c>session_attachment_chunks</c> and <c>session_attachment_index_state</c> carry a real foreign
    /// key, so leaving either behind aborts the migration at <c>COMMIT</c> and says so.
    /// <c>attachment_memory_consultations</c>, <c>saga_memory_attachment_provenance</c> and
    /// <c>lexicon_fact_attachment_provenance</c> carry none: each joins to
    /// <c>SessionAttachments."Id"</c> by exact equality to decide whether an attachment-derived
    /// consultation, Saga memory or Lexicon fact can still report its source. Missing one of those
    /// converts a join that works into one that silently returns nothing, permanently, on every
    /// installation - which is the worst outcome available in this step and the reason the family is
    /// declared in one place rather than assembled at three call sites.</para>
    ///
    /// <para>Two attachment columns are deliberately absent, and their absence is a decision rather than
    /// an omission. <c>session_attachment_chunks.SessionId</c> and <c>RetrievalScope</c> hold a Session
    /// identity in the minority form and stay there: the tapestry reads
    /// <c>SELECT DISTINCT "SessionId" FROM session_attachment_chunks</c> as its live scope-id set and
    /// those values become <c>tapestry_nodes.ScopeId</c>, so moving them would orphan every
    /// attachment-scoped generation and rebuild the tree at provider cost. Nothing compares either
    /// across a component boundary, which is what this work exists to end.</para>
    ///
    /// <para>Every table here is an ordinary rowid table, which the parent page's <c>rowid IN (…)</c>
    /// selection depends on. Four of these columns take part in a unique constraint -
    /// <c>SessionAttachments."Id"</c> is that table's primary key, <c>session_attachment_index_state</c>
    /// keys on <c>AttachmentId</c> alone, and the chunk and consultation tables each carry it inside a
    /// composite key - so a database holding two spellings of one attachment would collide on
    /// <c>upper()</c> and abort the batch. No writer can produce that state, and the abort is the right
    /// outcome if a hand edit ever does: two rows claiming one attachment is not something a migration
    /// should silently pick a winner for.</para>
    /// </remarks>
    internal static readonly IReadOnlyList<IdentityFamily> RepairedFamilies =
    [
        new(
            "SessionAttachments",
            "Id",
            [
                new("session_attachment_chunks", "AttachmentId"),
                new("session_attachment_index_state", "AttachmentId"),
                new("attachment_memory_consultations", "AttachmentId"),
                new("saga_memory_attachment_provenance", "AttachmentId"),
                new("lexicon_fact_attachment_provenance", "AttachmentId"),
            ]),
    ];

    /// <summary>
    /// The references the repair arm may move, each with the identity it has to agree with.
    /// </summary>
    /// <remarks>
    /// Every one is unenforced by design, and the first two are the ones the design calls out as the
    /// expensive silent failures. A Session naming a Campaign in the minority spelling keeps pointing at
    /// a deleted Campaign and is omitted from the Campaign-filtered listing; an embedding naming its
    /// Entry in the minority spelling makes the weaving service's left join report that Entry as
    /// unembedded, and the corpus is silently re-embedded at provider cost. The two
    /// <c>SessionAttachments</c> entries are the family's own outward references - the Session and the
    /// Entry an attachment belongs to - and are ordinary references rather than part of
    /// <see cref="RepairedFamilies"/>, because nothing ties them atomically to the identity move.
    ///
    /// <para>Internal and pinned by a test for the same reason <see cref="VerifiedColumns"/> is. A
    /// reference added here without a case of its own would be covered by nothing: every existing case
    /// asserts that a <i>particular</i> pairing was restored, so a new one that silently never ran would
    /// leave all of them green.</para>
    ///
    /// <para>Every table here is an ordinary rowid table, which the batch's <c>rowid IN (…)</c> selection
    /// depends on. <c>entry_embeddings.EntryId</c> is also that table's <c>TEXT PRIMARY KEY</c>, so a
    /// hand-edited database holding two spellings of one Entry would collide on <c>upper()</c> and abort
    /// the batch. No writer can produce that state - the weaving service copies whatever spelling
    /// <c>Entries."Id"</c> holds - and the abort is the right outcome if it ever exists, because two rows
    /// claiming one Entry's embedding is not something a migration should silently pick a winner for.
    /// </para>
    /// </remarks>
    internal static readonly IReadOnlyList<IdentityReference> RepairedReferences =
    [
        new("Sessions", "CampaignId", "Campaigns", "Id"),
        new("entry_embeddings", "EntryId", "Entries", "Id"),
        new("SessionAttachments", "SessionId", "Sessions", "Id"),
        new("SessionAttachments", "EntryId", "Entries", "Id"),
    ];

    /// <summary>
    /// The two columns repaired on their own shape alone, with no identity anywhere for them to be
    /// qualified against.
    /// </summary>
    /// <remarks>
    /// <b>The absence of a canonical target here is the decision, not an omission.</b> Every other repair
    /// in this file is qualified by an <c>EXISTS</c> against the identity the column names, because those
    /// columns join to a stored column and uppercasing one whose target is spelled the minority way would
    /// break a pairing that works. Neither of these two joins to a stored column at all. Every reader of
    /// both binds a <c>Guid</c> it rendered itself, so the value they must agree with is the canonical
    /// rendering of the identity rather than another row's spelling of it, and there is no pairing an
    /// unqualified repair could break.
    ///
    /// <para>Qualifying them anyway would be actively wrong. <c>session_campaign_bindings.CampaignId</c>
    /// carries no foreign key <i>by design</i> - it is the historical authority identity, so a Campaign
    /// deletion clears its own row without rewriting the durable fact that this Session was bound to that
    /// Campaign - so a repair qualified against <c>Campaigns."Id"</c> would decline exactly the rows whose
    /// Campaign is gone, and leave the table mixed forever on the one class of row nothing else will ever
    /// touch.</para>
    ///
    /// <para><c>saga_memories.CampaignId</c> is the same value copied verbatim by
    /// <see cref="SagaMemoryScopeClassifier"/>, from the live write path and from the version-two
    /// classification sweep alike, so it inherits whichever spelling the binding held. The two are
    /// repaired together and in this order because mixed is the one state that must not survive: repairing
    /// the binding alone would leave every memory already written pointing at a Campaign spelled the other
    /// way, which is the halved recall this step exists to end.</para>
    ///
    /// <para>Neither column takes part in any unique constraint, so the <c>upper()</c> collision hazard
    /// the other two lists have to reason about does not arise here: two rows whose Campaign identities
    /// differ only in case are simply two rows.</para>
    ///
    /// <para><c>session_campaign_bindings</c> is the one table in this file whose own guard has an opinion
    /// about the repair. <c>session_campaign_bindings_guard_update</c> demands the Session binding write
    /// scope on every update and admits exactly one rewrite of this column, the spelling-only
    /// canonicalization below; version 5 replaces that guard with the one carrying the exemption, in the
    /// same step's DDL, before this sweep runs.</para>
    /// </remarks>
    internal static readonly IReadOnlyList<RepairedColumn> RepairedColumns =
    [
        new("session_campaign_bindings", "CampaignId", RequiresSessionBindingWriteScope: true),
        new("saga_memories", "CampaignId", RequiresSessionBindingWriteScope: false),
    ];

    /// <summary>Written once the precondition count has been taken, so a resumed pass does not retake it.</summary>
    private const string VerifiedCursor = "verified";

    /// <summary>Recorded in the transition journal, so a resumed run can prove it is this sweep.</summary>
    public string Name => "identity-spelling-canonical-form";

    /// <summary>
    /// Small enough that one batch's transaction never holds the database while an operator is waiting
    /// for a turn, and large enough that an ordinary installation drains in a pass or two.
    /// </summary>
    /// <remarks>
    /// This bounds the <i>identities</i> a batch moves, not the rows it writes, and the difference is
    /// deliberate. An attachment identity and every column naming it have to move inside one transaction
    /// or the deferred foreign-key check aborts the batch at <c>COMMIT</c>, so the unit of work is the
    /// attachment: its chunks, its index state and its three provenance rows move with it however many
    /// there are, and the batch reports the attachments rather than the rows. Counting the rows instead
    /// would trip the runner's own bound on a batch that had done exactly what it must.
    /// </remarks>
    public int MaxRowsPerBatch => 200;

    public async Task<GrimoireSchemaBackfillBatch> AdvanceBatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? cursor,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(transaction);

        // Deferred rather than disabled: PRAGMA foreign_keys=OFF is a no-op inside a transaction, and
        // the Covenant connection policy both sets and verifies enforcement on every connection. It is
        // what the attachment family rests on - the two foreign-key children of SessionAttachments(Id)
        // dangle between the parent's UPDATE and their own, and only the end state has to be consistent.
        await ExecuteAsync(connection, transaction, "PRAGMA defer_foreign_keys=ON;", cancellationToken)
            .ConfigureAwait(false);

        if (cursor is null)
        {

            await VerifyAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        }

        int counted = 0;

        bool moved = false;

        // The two Campaign columns first, and the order between them is load-bearing rather than tidy:
        // the binding is the authority a memory's Campaign was taken from, so repairing the binding
        // first and the memories second means a batch cut short by its budget leaves the two agreeing on
        // fewer rows rather than disagreeing on more.
        //
        // Ahead of the attachment families for a reason that is a courtesy rather than a correctness
        // requirement. Nothing depends on these two being settled early - the classifier canonicalizes
        // what it hands on, so a Saga write is correct at any point in the drain - but a Campaign memory
        // reset selects on both columns exactly, and on an installation holding many attachments the
        // families would otherwise spend every batch's budget for a long time and leave that one
        // operator-facing path selecting only the rows the sweep had reached.
        foreach (RepairedColumn column in RepairedColumns)
        {

            int columnBudget = MaxRowsPerBatch - counted;

            if (columnBudget <= 0)
            {

                break;

            }

            int settled = await RepairColumnAsync(
                connection,
                transaction,
                column,
                columnBudget,
                cancellationToken).ConfigureAwait(false);

            counted += settled;

            moved |= settled > 0;

        }

        // Families before the plain references, so the bounded page is never spent on a reference and
        // left unable to finish an attachment. Starving the plain references for a batch or two costs
        // nothing, because none of them carries a foreign key; splitting a family across two
        // transactions aborts the migration.
        foreach (IdentityFamily family in RepairedFamilies)
        {

            int budget = MaxRowsPerBatch - counted;

            int parents = budget <= 0
                ? 0
                : await MoveIdentityPageAsync(connection, transaction, family, budget, cancellationToken)
                    .ConfigureAwait(false);

            counted += parents;

            moved |= parents > 0;

            moved |= await RepairDependentsAsync(connection, transaction, family, cancellationToken)
                .ConfigureAwait(false) > 0;

        }

        foreach (IdentityReference reference in RepairedReferences)
        {

            int budget = MaxRowsPerBatch - counted;

            if (budget <= 0)
            {

                break;

            }

            int repaired = await RepairAsync(connection, transaction, reference, budget, cancellationToken)
                .ConfigureAwait(false);

            counted += repaired;

            moved |= repaired > 0;

        }

        return new GrimoireSchemaBackfillBatch(VerifiedCursor, counted, IsComplete: !moved);

    }

    /// <summary>
    /// Counts the rows of one column that are not stored in the canonical spelling.
    /// </summary>
    /// <remarks>
    /// Canonical means uppercase <i>and</i> dashed <i>and</i> 36 characters, so the predicate checks all
    /// three. Case alone would pass a dash-free rendering silently, since a 32-character hex string is
    /// already its own uppercase image. SQLite's <c>substr</c> returns an empty string rather than
    /// throwing when the value is shorter than the offset, which is what makes it safe here.
    ///
    /// <para>Internal so a suite can ask the sweep's own question of a database instead of writing a
    /// second predicate that could drift from this one.</para>
    ///
    /// <para><b>The count and the repair do not ask the same question, and the gap is worth knowing
    /// about.</b> Everything that moves a row - the reference repair and the identity page alike - fixes
    /// case alone, because case is the only half of canonical an <c>upper()</c> can fix: a dash-free or
    /// truncated value has no correct dashed form to be rewritten into. Such a row is therefore never
    /// selected for repair at all - see <see cref="CanonicalShapeClause"/> - and keeps being reported
    /// outstanding by this count, forever, with the sweep still declaring itself complete because
    /// nothing moved. That is the right behaviour - inventing dashes would be guessing at data - but an
    /// operator reading a non-zero count that never falls needs to know it means "shape, not case"
    /// rather than "the sweep is stuck". Inherited from the version-5 step this extends rather than
    /// introduced with the attachment family.</para>
    /// </remarks>
    internal static async Task<long> CountNonCanonicalAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        string column,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            $"""
            SELECT COUNT(*) FROM "{table}"
            WHERE "{column}" IS NOT NULL
              AND ("{column}" <> upper("{column}")
                OR length("{column}") <> 36
                OR substr("{column}", 9, 1) <> '-'
                OR substr("{column}", 14, 1) <> '-'
                OR substr("{column}", 19, 1) <> '-'
                OR substr("{column}", 24, 1) <> '-');
            """;

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);

    }

    /// <summary>
    /// Takes the step's precondition count and records it, whatever it is.
    /// </summary>
    /// <remarks>
    /// Zero across the board is the outcome this design predicts and the one every installation that
    /// predates this version will produce. Logging it rather than skipping it is the point: a silent
    /// no-op proves nothing, and a line naming every column it looked at and what it found there is what
    /// turns "verifier, not backfill" from an argument into evidence on that installation. The
    /// attachment family is the exception the count exists to make visible: on an installation that has
    /// ever held an attachment those eight columns are the ones that report a number.
    /// </remarks>
    private static async Task VerifyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {

        long total = 0;

        foreach ((string table, string column) in VerifiedColumns)
        {

            long count = await CountNonCanonicalAsync(connection, transaction, table, column, cancellationToken)
                .ConfigureAwait(false);

            total += count;

            Log.Information(
                "Identity spelling: {Table}.{Column} holds {Count} identities that are not the canonical uppercase dashed form.",
                table,
                column,
                count);

        }

        if (total == 0)
        {

            Log.Information(
                "Identity spelling: every verified identity column already holds the canonical form; nothing to repair.");

            return;

        }

        // Named rather than merely counted, because an operator who sees a number here needs to know
        // which half of it this step can act on. Two of the three repairable kinds are expected on an
        // ordinary installation - the attachment family on any that has held an attachment, and the two
        // Campaign columns on any that has created a Session through the turn path - so a number here is
        // not by itself evidence of anything having gone wrong. What is left behind is: outside those,
        // the only thing that can produce a non-canonical identity is an edit made outside Arcanum.
        Log.Warning(
            "Identity spelling: {Count} stored identities are not canonical. The attachment identity and "
                + "every column naming it are moved together; a reference whose canonical target still "
                + "exists is repaired onto it; and a Campaign identity on a Session binding or a Saga "
                + "memory is settled on its own. An identity a row is known by is left where it is, "
                + "because the tables that depend on it refuse the write.",
            total);

    }

    /// <summary>
    /// Uppercases one bounded page of an identity column, with no canonical target to qualify it,
    /// because it <i>is</i> the target its dependents are qualified against.
    /// </summary>
    /// <remarks>
    /// This runs before <see cref="RepairDependentsAsync"/> in the same transaction and that order is
    /// load-bearing rather than tidy. A dependent is only moved where the identity it names already
    /// exists in the canonical form, so a dependent whose parent has not moved yet declines and stays
    /// paired with it - which is exactly what keeps the deferred foreign-key check satisfied at every
    /// batch boundary, whatever the page size is and however many batches the corpus takes.
    /// </remarks>
    private static async Task<int> MoveIdentityPageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IdentityFamily family,
        int limit,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            $"""
            UPDATE "{family.Table}"
            SET "{family.Column}" = upper("{family.Column}")
            WHERE rowid IN (
                SELECT rowid FROM "{family.Table}"
                WHERE "{family.Column}" IS NOT NULL
                  AND "{family.Column}" <> upper("{family.Column}")
                  AND {CanonicalShapeClause(family.Column)}
                LIMIT $limit);
            """;

        _ = command.Parameters.AddWithValue("$limit", limit);

        int moved = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (moved > 0)
        {

            Log.Information(
                "Identity spelling: moved {Count} {Table}.{Column} identities onto the canonical form.",
                moved,
                family.Table,
                family.Column);

        }

        return moved;

    }

    /// <summary>
    /// Moves every column that names a family's identity onto the spelling that identity now holds,
    /// without a bound.
    /// </summary>
    /// <remarks>
    /// <b>The absence of a limit here is the whole safety property, not an oversight.</b> Two of the
    /// dependents carry a foreign key to the identity above, and a deferred foreign key is still checked
    /// at <c>COMMIT</c>; a page of parents whose children were cut off by a row budget would abort the
    /// batch, and every retry of it, permanently. What is bounded is the number of <i>identities</i> a
    /// batch moves, and every column naming those identities moves with them, which is why the batch
    /// reports the identity count rather than the row count - see <see cref="MaxRowsPerBatch"/>.
    ///
    /// <para>The canonical-target <c>EXISTS</c> is the same one the reference repair carries and does
    /// two jobs here. It is what makes the parent-then-dependents order sufficient, since a dependent
    /// of an unmoved parent declines rather than half-moving. And it reaches a row this batch's page
    /// never selected: an attachment minted canonically by the protected artifact transfer store whose
    /// provenance row was written in the minority form by a writer that had not been converted yet was
    /// already reporting its source unavailable, and no parent has to move for that pairing to be
    /// restored.</para>
    /// </remarks>
    private static async Task<int> RepairDependentsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IdentityFamily family,
        CancellationToken cancellationToken)
    {

        int moved = 0;

        foreach (IdentityColumn dependent in family.Dependents)
        {

            await using SqliteCommand command = connection.CreateCommand();

            command.Transaction = transaction;

            command.CommandText =
                $"""
                UPDATE "{dependent.Table}"
                SET "{dependent.Column}" = upper("{dependent.Column}")
                WHERE "{dependent.Column}" IS NOT NULL
                  AND "{dependent.Column}" <> upper("{dependent.Column}")
                  AND {CanonicalShapeClause(dependent.Column)}
                  AND EXISTS (
                      SELECT 1 FROM "{family.Table}"
                      WHERE "{family.Table}"."{family.Column}"
                          = upper("{dependent.Table}"."{dependent.Column}"));
                """;

            int rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            moved += rows;

            if (rows > 0)
            {

                Log.Information(
                    "Identity spelling: moved {Count} {Table}.{Column} references onto the {Target} they name.",
                    rows,
                    dependent.Table,
                    dependent.Column,
                    family.Table);

            }

        }

        return moved;

    }

    /// <summary>
    /// Uppercases one bounded page of a reference column, and only where the identity it names already
    /// exists in the canonical form.
    /// </summary>
    /// <remarks>
    /// The <c>EXISTS</c> clause appears in the selection as well as being the reason for it, so the
    /// predicate that chooses the work and the predicate that performs it are the same one. A row it
    /// declines never enters a later batch either, which is what keeps the sweep draining instead of
    /// selecting the same unrepairable row forever.
    /// </remarks>
    private static async Task<int> RepairAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IdentityReference reference,
        int limit,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            $"""
            UPDATE "{reference.Table}"
            SET "{reference.Column}" = upper("{reference.Column}")
            WHERE rowid IN (
                SELECT rowid FROM "{reference.Table}"
                WHERE "{reference.Column}" IS NOT NULL
                  AND "{reference.Column}" <> upper("{reference.Column}")
                  AND {CanonicalShapeClause(reference.Column)}
                  AND EXISTS (
                      SELECT 1 FROM "{reference.TargetTable}"
                      WHERE "{reference.TargetTable}"."{reference.TargetColumn}"
                          = upper("{reference.Table}"."{reference.Column}"))
                LIMIT $limit);
            """;

        _ = command.Parameters.AddWithValue("$limit", limit);

        int moved = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (moved > 0)
        {

            Log.Information(
                "Identity spelling: repaired {Count} {Table}.{Column} references onto the {Target} they name.",
                moved,
                reference.Table,
                reference.Column,
                reference.TargetTable);

        }

        return moved;

    }

    /// <summary>
    /// Uppercases one bounded page of a column that names no stored identity, opening the authority
    /// scope its table's own guard demands and only when there is work for it.
    /// </summary>
    /// <remarks>
    /// The count exists only to keep a scope from being opened for nothing, so it is taken only for the
    /// column that needs one - the same discipline the Session binding backfill keeps, and for the same
    /// reason: the Session binding write scope is the narrow permission that lets a writer state a
    /// Session's Campaign authority, and nothing should be able to read a granted scope as evidence that
    /// work happened. A column with no scope to open goes straight to the page and lets the
    /// <c>UPDATE</c>'s own row count answer, which is one scan per batch rather than two.
    ///
    /// <para>It counts what it is about to repair rather than what
    /// <see cref="CountNonCanonicalAsync"/> reports, so a hand-edited dash-free row - which
    /// <see cref="CanonicalShapeClause"/> declines and which therefore stays outstanding forever - never
    /// causes the scope to be opened for an update that would move nothing.</para>
    /// </remarks>
    private static async Task<int> RepairColumnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RepairedColumn column,
        int limit,
        CancellationToken cancellationToken)
    {

        if (!column.RequiresSessionBindingWriteScope)
        {

            return await MoveColumnPageAsync(connection, transaction, column, limit, cancellationToken)
                .ConfigureAwait(false);

        }

        if (await CountRepairableAsync(connection, transaction, column, cancellationToken)
            .ConfigureAwait(false) == 0)
        {

            return 0;

        }

        using CovenantSqliteAuthorizationScope scope = CovenantSqliteConnectionInitializer.Instance
            .Authorize(connection, CovenantSqliteAuthorizationKind.SessionBindingWrite);

        return await MoveColumnPageAsync(connection, transaction, column, limit, cancellationToken)
            .ConfigureAwait(false);

    }

    /// <summary>How many rows of one unqualified column this sweep would move if it ran now.</summary>
    private static async Task<long> CountRepairableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RepairedColumn column,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            $"""
            SELECT COUNT(*) FROM "{column.Table}"
            WHERE "{column.Column}" IS NOT NULL
              AND "{column.Column}" <> upper("{column.Column}")
              AND {CanonicalShapeClause(column.Column)};
            """;

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);

    }

    /// <summary>Uppercases one bounded page of an unqualified column.</summary>
    private static async Task<int> MoveColumnPageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RepairedColumn column,
        int limit,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            $"""
            UPDATE "{column.Table}"
            SET "{column.Column}" = upper("{column.Column}")
            WHERE rowid IN (
                SELECT rowid FROM "{column.Table}"
                WHERE "{column.Column}" IS NOT NULL
                  AND "{column.Column}" <> upper("{column.Column}")
                  AND {CanonicalShapeClause(column.Column)}
                LIMIT $limit);
            """;

        _ = command.Parameters.AddWithValue("$limit", limit);

        int moved = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (moved > 0)
        {

            Log.Information(
                "Identity spelling: settled {Count} {Table}.{Column} Campaign identities on the canonical form.",
                moved,
                column.Table,
                column.Column);

        }

        return moved;

    }

    /// <summary>
    /// The half of canonical an <c>upper()</c> cannot fix, written as the condition a row must already
    /// satisfy before a repair is allowed to move it.
    /// </summary>
    /// <remarks>
    /// Everything that moves a row here rewrites it as <c>upper(col)</c>, which corrects case and
    /// nothing else. A dash-free or truncated value has no correct dashed form to be rewritten into, so
    /// uppercasing one produces a second non-canonical spelling rather than the canonical one. Version 5
    /// now also installs a write-time guard on every column this sweep touches, and that guard refuses
    /// exactly the value such a rewrite would produce - so without this clause a single hand-edited row
    /// would abort the batch, and every retry of it, leaving the tier permanently unable to reach head.
    /// Selecting on the shape as well as the case makes <i>the sweep cannot trip its own guards</i> a
    /// property of the SQL rather than an assumption about the data.
    ///
    /// <para>A row this clause declines is never repaired and never will be, and
    /// <see cref="CountNonCanonicalAsync"/> goes on reporting it outstanding while the sweep declares
    /// itself complete because nothing moved. That is the outcome the count's own remarks describe and
    /// the right one: inventing dashes would be guessing at data an operator has to look at.</para>
    /// </remarks>
    private static string CanonicalShapeClause(string column) =>
        $"""
        length("{column}") = 36
          AND substr("{column}", 9, 1) = '-'
          AND substr("{column}", 14, 1) = '-'
          AND substr("{column}", 19, 1) = '-'
          AND substr("{column}", 24, 1) = '-'
        """;

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    /// <summary>One reference column and the identity column it has to agree with.</summary>
    internal sealed record IdentityReference(
        string Table,
        string Column,
        string TargetTable,
        string TargetColumn);

    /// <summary>One column of one table, named where the table it belongs to is already known.</summary>
    internal sealed record IdentityColumn(string Table, string Column);

    /// <summary>
    /// One column repaired on its own shape, with whether its table's guard demands the Session binding
    /// write scope before it will accept the rewrite.
    /// </summary>
    /// <remarks>
    /// The flag is declared per column rather than derived from the table name, because it is a statement
    /// about what the schema refuses rather than about what this file happens to know. A column added here
    /// on a table whose guard demands a scope, and flagged <see langword="false"/>, aborts its own batch
    /// and says which guard turned it back.
    /// </remarks>
    internal sealed record RepairedColumn(
        string Table,
        string Column,
        bool RequiresSessionBindingWriteScope);

    /// <summary>
    /// An identity that has to be moved in place, and every column that names it and must move with it.
    /// </summary>
    internal sealed record IdentityFamily(
        string Table,
        string Column,
        IReadOnlyList<IdentityColumn> Dependents);

}
