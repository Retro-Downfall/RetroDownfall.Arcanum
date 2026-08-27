using System.Globalization;

using Microsoft.Data.Sqlite;

using Serilog;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// Verifies that every stored identity holds the one canonical spelling, and repairs the references
/// that can be repaired without breaking a pairing that currently works.
/// </summary>
/// <remarks>
/// This is a verifier before it is a repair, and the distinction is the whole design. Both writers that
/// ever rendered the minority spelling were unreachable for their entire existence - the import planner
/// refused every archive, and the merge path returns before it opens a transaction - so an installation
/// that predates this version already holds the canonical form and the count below is zero on every
/// column. The count is still taken, and still logged when it is zero, because that log line is the
/// evidence the reasoning held in the field, and because source can prove no code path wrote a bad row
/// while it cannot prove nobody edited the database by hand.
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
/// <para><b>This sweep is not finished, and version 5 must not reach an installation until it is.</b>
/// The <c>SessionAttachments</c> column family and its children are the one genuine data rewrite in this
/// work, and they land in a later change against this same version. A journal that records the
/// <c>(Core, 5)</c> sweep complete is never re-run, so an installation upgraded before that change lands
/// keeps the minority spelling in those columns forever, with nothing left to notice it.</para>
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
    ];

    /// <summary>
    /// The references the repair arm may move, each with the identity it has to agree with.
    /// </summary>
    /// <remarks>
    /// Both are unenforced by design and are the two the design calls out as the expensive silent
    /// failures. A Session naming a Campaign in the minority spelling keeps pointing at a deleted
    /// Campaign and is omitted from the Campaign-filtered listing; an embedding naming its Entry in the
    /// minority spelling makes the weaving service's left join report that Entry as unembedded, and the
    /// corpus is silently re-embedded at provider cost.
    ///
    /// <para>Internal and pinned by a test for the same reason <see cref="VerifiedColumns"/> is. A third
    /// reference added here without a case of its own would be covered by nothing: every existing case
    /// asserts that a <i>particular</i> pairing was restored, so a new one that silently never ran would
    /// leave all of them green.</para>
    ///
    /// <para>Both tables are ordinary rowid tables, which the batch's <c>rowid IN (…)</c> selection
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
    ];

    /// <summary>Written once the precondition count has been taken, so a resumed pass does not retake it.</summary>
    private const string VerifiedCursor = "verified";

    /// <summary>Recorded in the transition journal, so a resumed run can prove it is this sweep.</summary>
    public string Name => "identity-spelling-canonical-form";

    /// <summary>
    /// Small enough that one batch's transaction never holds the database while an operator is waiting
    /// for a turn, and large enough that an ordinary installation drains in a pass or two. On every
    /// installation that predates this version the first batch repairs nothing and the sweep is done.
    /// </summary>
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
        // the Covenant connection policy both sets and verifies enforcement on every connection. This
        // is a no-op for the two references below, neither of which carries a foreign key, and it is
        // set anyway because it is the property the whole repair rests on - parent and child may move
        // in any order and only the end state has to be consistent.
        await ExecuteAsync(connection, transaction, "PRAGMA defer_foreign_keys=ON;", cancellationToken)
            .ConfigureAwait(false);

        if (cursor is null)
        {

            await VerifyAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        }

        int repaired = 0;

        foreach (IdentityReference reference in RepairedReferences)
        {

            int budget = MaxRowsPerBatch - repaired;

            if (budget <= 0)
            {

                break;

            }

            repaired += await RepairAsync(connection, transaction, reference, budget, cancellationToken)
                .ConfigureAwait(false);

        }

        return new GrimoireSchemaBackfillBatch(VerifiedCursor, repaired, IsComplete: repaired == 0);

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
    /// no-op proves nothing, and a line saying the sweep looked at eight columns and found nothing is
    /// what turns "verifier, not backfill" from an argument into evidence on that installation.
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

        // Named rather than merely counted, because the only thing that can produce this state is an
        // edit made outside Arcanum, and an operator who sees it needs to know that the two identity
        // columns below are the only ones this step is able to move.
        Log.Warning(
            "Identity spelling: {Count} stored identities are not canonical. Only a reference whose canonical "
                + "target still exists is repaired; an identity a row is known by cannot be moved in place, "
                + "because the tables that depend on it refuse the write.",
            total);

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

}
