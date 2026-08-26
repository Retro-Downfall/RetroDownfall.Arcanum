using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// What one bounded backfill batch achieved.
/// </summary>
/// <remarks>
/// <paramref name="NextCursor"/> is opaque to everything except the backfill that produced it. It is
/// written into the transition journal inside the same transaction as the batch's data writes, so
/// there is no ordering between the work and the record of the work to get wrong.
/// </remarks>
internal sealed record GrimoireSchemaBackfillBatch(
    string? NextCursor,
    int RowsProcessed,
    bool IsComplete);

/// <summary>
/// One resumable data sweep a version step depends on before that version may be recorded as
/// installed.
/// </summary>
/// <remarks>
/// The obligations below cannot be expressed in the signature and are therefore the implementer's:
///
/// <list type="bullet">
/// <item>Write only through the supplied transaction. Never commit, roll back, open a second
/// connection, or retry - the coordinator owns all four.</item>
/// <item>Process at most <see cref="MaxRowsPerBatch"/> rows. An unbounded batch is what turns a
/// resumable sweep back into a migration.</item>
/// <item>Be safe to re-run from the last committed cursor and produce the same durable effect. A
/// crash between a batch's work and its commit is indistinguishable from that batch never having
/// run, so the next pass runs it again.</item>
/// <item>Report <see cref="GrimoireSchemaBackfillBatch.IsComplete"/> only when the corpus is
/// drained. A complete batch may still report rows and a cursor; the cursor is discarded with the
/// journal row.</item>
/// </list>
/// </remarks>
internal interface IGrimoireSchemaBackfill
{

    /// <summary>
    /// Stable, 1 to 64 characters, recorded in the journal so a resumed run can prove the pending
    /// sweep is the one this binary declares.
    /// </summary>
    string Name { get; }

    int MaxRowsPerBatch { get; }

    Task<GrimoireSchemaBackfillBatch> AdvanceBatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? cursor,
        CancellationToken cancellationToken);

}
