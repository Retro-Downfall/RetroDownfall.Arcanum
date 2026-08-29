using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Annals;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Weave;

using RetroDownfall.Arcanum.Infrastructure.Data.Annals;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// Gives every Saga memory and Lexicon entry written before the Annals existed the claim that records
/// what it asserted, when that was true, and when Arcanum came to hold it.
/// </summary>
/// <remarks>
/// Conservative by construction. Every version this writes carries
/// <see cref="AnnalOrigin.SystemBackfilled"/> and copies its subject's own scope verbatim: a Saga row at
/// <see cref="SagaMemoryScopeKind.Unclassified"/> stays there, and one at
/// <see cref="SagaMemoryScopeKind.LegacyUnresolved"/> stays there too. Neither becomes
/// <see cref="SagaMemoryScopeKind.Global"/>, because an installation-global claim is retrievable inside
/// every Campaign and a memory whose ownership was never resolved has no authority to become one.
///
/// <para>Timestamps come from the subject row rather than from the sweep's clock. That is when Arcanum
/// actually first held the claim, and stamping an upgrade's clock on a six-month-old memory would make
/// transaction time useless for exactly the historical questions it exists to answer.</para>
///
/// <para>There is no cursor. The batch's own predicate is the absence of a claim, and every row it reads
/// is claimed in the same transaction, so the corpus shrinks by exactly the work that committed. A crash
/// between the work and its commit leaves those rows unclaimed and the next pass selects them again,
/// which is the resumability the interface asks for, reached without a position that could be advanced
/// past uncommitted work.</para>
/// </remarks>
internal sealed class MemoryAnnalsBackfill : IGrimoireSchemaBackfill
{

    /// <summary>Recorded in the transition journal, so a resumed run can prove it is this sweep.</summary>
    public string Name => "memory-annals-claims";

    /// <summary>
    /// Small enough that one batch's transaction never holds the database while an operator is waiting
    /// for a turn, and large enough that an ordinary installation drains in a pass or two.
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

        // Both pages are materialized before the first write. The selecting queries filter on the absence
        // of rows in the very table the writes insert into, and writing to a table an open cursor is
        // still filtering against is the case SQLite leaves undefined.
        List<PendingClaim> pending = await ReadBatchAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);

        if (pending.Count == 0)
        {

            return new GrimoireSchemaBackfillBatch(NextCursor: null, RowsProcessed: 0, IsComplete: true);

        }

        int written = 0;

        foreach (PendingClaim claim in pending)
        {

            if (await AnnalsClaimWriter.AppendAssertAsync(
                    connection,
                    transaction,
                    claim.SubjectStore,
                    claim.SubjectId,
                    AnnalOrigin.SystemBackfilled,
                    claim.ScopeKind,
                    claim.CampaignId,
                    ContentSensitivity.None,
                    claim.ContentHash,
                    claim.Timestamp,
                    claim.Timestamp,
                    sourceSessionId: null,
                    cancellationToken).ConfigureAwait(false))
            {

                written++;

            }

        }

        return new GrimoireSchemaBackfillBatch(NextCursor: null, written, IsComplete: false);

    }

    /// <summary>
    /// Reads one bounded page of unclaimed Saga memories and, if the batch has room left, one of
    /// unclaimed Lexicon entries, deciding every row's claim before the first write.
    /// </summary>
    private async Task<List<PendingClaim>> ReadBatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {

        List<PendingClaim> pending = [];

        await using (SqliteCommand saga = connection.CreateCommand())
        {

            saga.Transaction = transaction;

            saga.CommandText = """
                SELECT memory.Id, memory.Content, memory.CreatedAt, memory.ScopeKindCode, memory.CampaignId
                FROM saga_memories AS memory
                WHERE NOT EXISTS (
                    SELECT 1 FROM annal_claims AS claim
                    WHERE claim.SubjectStoreCode = 1 AND claim.SubjectId = memory.Id)
                ORDER BY memory.Id
                LIMIT $limit;
                """;

            _ = saga.Parameters.AddWithValue("$limit", MaxRowsPerBatch);

            await using SqliteDataReader reader =
                await saga.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                pending.Add(
                    new PendingClaim(
                        AnnalSubjectStore.Saga,
                        reader.GetString(0),
                        AnnalContentDigest.ForSagaMemory(reader.GetString(1)),
                        ParseTimestamp(reader.GetString(2)),
                        (SagaMemoryScopeKind)reader.GetInt64(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4)));

            }

        }

        int remaining = MaxRowsPerBatch - pending.Count;

        if (remaining <= 0)
        {

            return pending;

        }

        await using SqliteCommand lexicon = connection.CreateCommand();

        lexicon.Transaction = transaction;

        lexicon.CommandText = """
            SELECT entry.Id, entry.Type, entry.FactsText, entry.UpdatedAt, entry.ScopeCampaignId
            FROM lexicon_entries AS entry
            WHERE NOT EXISTS (
                SELECT 1 FROM annal_claims AS claim
                WHERE claim.SubjectStoreCode = 2 AND claim.SubjectId = entry.Id)
            ORDER BY entry.Id
            LIMIT $limit;
            """;

        _ = lexicon.Parameters.AddWithValue("$limit", remaining);

        await using SqliteDataReader entries =
            await lexicon.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await entries.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            // The Lexicon's empty-string scope is the global tier, not an absent one: the column is
            // NOT NULL DEFAULT '', so every row has always had an unambiguous tier and none of them is
            // unresolved. Reading it as Global is therefore not laundering, it is what the row says.
            string scopeCampaignId = entries.GetString(4);

            bool global = scopeCampaignId.Length == 0;

            pending.Add(
                new PendingClaim(
                    AnnalSubjectStore.Lexicon,
                    entries.GetString(0),
                    AnnalContentDigest.ForLexiconEntry(entries.GetString(1), entries.GetString(2)),
                    ParseTimestamp(entries.GetString(3)),
                    global ? SagaMemoryScopeKind.Global : SagaMemoryScopeKind.Campaign,
                    global ? null : scopeCampaignId));

        }

        return pending;

    }

    /// <summary>
    /// Reads a stored timestamp without letting a machine's locale or time zone change the answer.
    /// </summary>
    /// <remarks>
    /// Round-trip parsing keeps the offset the row recorded. A row written before this format was
    /// universal, or one an operator edited by hand, falls back to the epoch rather than throwing: a
    /// sweep that aborted on one malformed timestamp would leave a tier permanently below head, and an
    /// unparseable timestamp is a worse reason to refuse an upgrade than it is to record a conservative
    /// one.
    /// </remarks>
    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out DateTimeOffset parsed)
            ? parsed
            : DateTimeOffset.UnixEpoch;

    /// <summary>One subject row's claim, decided before any write in this batch begins.</summary>
    private sealed record PendingClaim(
        AnnalSubjectStore SubjectStore,
        string SubjectId,
        byte[] ContentHash,
        DateTimeOffset Timestamp,
        SagaMemoryScopeKind ScopeKind,
        string? CampaignId);

}
