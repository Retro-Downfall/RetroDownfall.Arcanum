using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Memory;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>Whether an entry erasure runs against the live installation or a staged restore candidate.</summary>
internal enum CovenantEntryErasureMode
{
    /// <summary>
    /// The live installation: the erased heads' absent deltas are appended and the canonical search
    /// sequence advances once, and a Campaign entry's Campaign must still be registered.
    /// </summary>
    Live = 1,

    /// <summary>
    /// A staged restore candidate whose search projection is rebuilt after it is published, so it
    /// appends no delta, advances no sequence, and has no live Campaign to check.
    /// </summary>
    Staged = 2,
}

/// <summary>One Covenant entry an erasure removes, read inside the erasing transaction.</summary>
/// <param name="IsMasked">
/// Whether a Campaign entry's Campaign has masked the key's Confirmed lane at the key's binding epoch.
/// </param>
/// <param name="ReclaimsKey">
/// Whether no other entry or head in any scope names the key, so erasing this entry reclaims it.
/// </param>
/// <param name="CreatedAtUtc">
/// The entry's creation instant as stored, which opens the window a receipt written before canonical
/// version 6 is matched in.
/// </param>
/// <param name="HeadLanes">The lanes the entry holds a head in, which such a receipt is matched by.</param>
/// <param name="SearchRowIds">
/// The search projection rows of the entry's heads, which key its full-text index rows, captured
/// before anything is deleted so the absence proof can still name them afterwards.
/// </param>
internal sealed record CovenantEntryErasureSubject(
    Guid EntryId,
    CovenantScope Scope,
    Guid? CampaignId,
    string NormalizedKey,
    bool IsMasked,
    bool ReclaimsKey,
    string CreatedAtUtc,
    IReadOnlyList<CovenantLane> HeadLanes,
    IReadOnlyList<long> SearchRowIds);

/// <summary>What one entry-erasure plan run found or removed.</summary>
/// <param name="Targets">
/// Every target in the plan's fixed order, each table once, with <c>covenant_key_epochs</c> last and
/// only when the key is reclaimed. In <see cref="CovenantArtifactPlanMode.Delete"/> mode a directly
/// deleted table reports the rows its own statement removed, and the two tables the versions' delete
/// removes by cascade report the rows measured immediately before it, so the two modes report the same
/// counts for the same entry.
/// </param>
/// <param name="VersionIds">
/// The entry's version ids, captured before anything was deleted, which the absence proof needs once the
/// versions themselves are gone.
/// </param>
/// <param name="FullTextSecureDeleteVerified">
/// Whether <c>covenant_fts</c> reads <c>secure-delete = 1</c>, or is absent. It decides only the
/// recorded verdict, never whether the entry's search documents are deleted.
/// </param>
/// <param name="KeyReclaimed">
/// Whether the key was reclaimed, or in <see cref="CovenantArtifactPlanMode.Count"/> mode would be.
/// </param>
internal sealed record CovenantEntryErasureTally(
    IReadOnlyList<MemoryErasureTableCount> Targets,
    IReadOnlyList<Guid> VersionIds,
    int ConfirmedVersions,
    int ProposedVersions,
    bool FullTextSecureDeleteVerified,
    bool RetainsCampaignMask,
    bool KeyReclaimed);

/// <summary>One live lane head of an erased entry, as an erase compares it with what show reported.</summary>
internal sealed record CovenantEntryErasureHead(Guid VersionId, long LaneRevision, CovenantOperation Operation);

/// <summary>The dataset generation and the key's two epochs an erase binds.</summary>
/// <param name="KeyEpoch">The key's dependency epoch, or 0 when it has no epoch row.</param>
internal sealed record CovenantEntryErasureState(Guid DatasetGeneration, long KeyEpoch, long KeyReclamationEpoch);

/// <summary>The scope facts a Covenant erase plan reports beside its counts.</summary>
/// <param name="IsPinned">Whether any lane of the erased subject is pinned at the key's binding epoch.</param>
/// <param name="GlobalConfirmedResurfaces">
/// Whether a live Global Confirmed entry for the key starts applying in the Campaign once this live
/// Campaign Confirmed entry is gone, which a retained mask prevents.
/// </param>
/// <param name="AffectedCampaigns">1 for a Campaign entry; every registered Campaign for a Global one.</param>
internal sealed record CovenantEntryErasureScopeFacts(bool IsPinned, bool GlobalConfirmedResurfaces, long AffectedCampaigns);

/// <summary>
/// The one plan a Covenant entry erasure counts, deletes and proves absent by, inside its caller's
/// transaction, and the reads that decide it.
/// </summary>
/// <remarks>
/// <para><b>One predicate per target, three uses.</b> Count mode measures each target with the
/// predicate Delete mode removes it by, and the absence proof counts it again with the same predicate,
/// so a count, a delete and a proof can never disagree about which rows an entry owns. Version ids are
/// captured before anything is deleted, because the predicates that follow a version find nothing once
/// the versions themselves are gone. Identities are compared normalised through
/// <see cref="CovenantIdentitySql.Keyed"/>, because a staged restore reads somebody else's database
/// whose identity spellings this build does not control.</para>
///
/// <para><b>The order is the deletion order.</b> Search documents go whenever the accelerator table
/// exists, whatever <c>covenant_fts</c> says about secure delete, which only decides the recorded
/// verdict. Then the entry's pending outbox deltas, then its mutation receipts, then provenance, heads,
/// versions (their review events and decision receipts cascade) and the entry. Then the subject's
/// curation: its heads, versions and receipts in both lanes and every epoch, except that a Campaign's
/// Confirmed mask at the key's binding epoch, with its chain, is kept while the key itself stays,
/// because it is the operator's policy about the Global key rather than content of this entry. When no
/// other entry or head in any scope names the key, the erase reclaims it: every curation row for the key
/// in every scope goes with its epoch row, walked in the canonical family's own deletion order, and the
/// key-reclamation epoch advances once by compare-and-swap.</para>
///
/// <para><b>Live and staged.</b> A live erase appends one content-free absent delta per erased head at
/// the next canonical search sequence and advances the sequence once, and refuses a Campaign entry
/// whose Campaign is no longer registered. A staged erase does neither: its candidate's projection is
/// rebuilt before it is published.</para>
///
/// <para>It never begins, authorizes or commits a transaction. The caller's authorization is what the
/// canonical delete guards admit: the live erase grants entry erasure, and a staged restore family
/// maintenance. This is the only Covenant entry-erasure file that names a Covenant table, and it never
/// logs.</para>
/// </remarks>
internal static class CovenantEntryErasurePlan
{
    private const string SearchDocuments = "covenant_search_documents";

    private const string Outbox = "covenant_search_outbox";

    private const string MutationReceipts = "covenant_mutation_receipts";

    private const string Provenance = "covenant_version_attachment_provenance";

    private const string Heads = "covenant_heads";

    private const string Versions = "covenant_versions";

    private const string ReviewEvents = "covenant_review_events";

    private const string DecisionReceipts = "covenant_review_decision_receipts";

    private const string Entries = "covenant_entries";

    private const string CurationHeads = "covenant_curation_heads";

    private const string CurationVersions = "covenant_curation_versions";

    private const string CurationReceipts = "covenant_curation_receipts";

    private const string KeyEpochs = "covenant_key_epochs";

    private const string FullTextIndex = "covenant_fts";

    private const string CurationPrefix = "covenant_curation_";

    private const int OutcomeNoChange = 2;

    /// <summary>The key's binding epoch, the one a pin or a mask records, or 0 while it has no epoch row.</summary>
    private const string BindingEpoch = "COALESCE((SELECT IncarnationEpoch FROM covenant_key_epochs WHERE NormalizedKey = $key), 0)";

    /// <summary>Every target in the order the plan reports it, before the reclaimed key's epoch row.</summary>
    private static readonly string[] OrderedTargets =
    [
        SearchDocuments,
        Outbox,
        MutationReceipts,
        Provenance,
        Heads,
        Versions,
        ReviewEvents,
        DecisionReceipts,
        Entries,
        CurationHeads,
        CurationVersions,
        CurationReceipts,
    ];

    /// <summary>
    /// The key's own family: every canonical content table keyed by the normalized key, in the
    /// canonical deletion order, which puts the key's curation ahead of its epoch row.
    /// </summary>
    /// <remarks>
    /// Taken from the one list every family deleter walks, so a curation table added to that list is a
    /// table a reclaiming erase deletes too, or a plan this file refuses to run.
    /// </remarks>
    private static readonly string[] KeyFamily =
    [
        .. CovenantCanonicalContentTables.InDeletionOrder.Where(static table =>
            table.StartsWith(CurationPrefix, StringComparison.Ordinal)
            || string.Equals(table, KeyEpochs, StringComparison.Ordinal)),
    ];

    /// <summary>
    /// Counts or deletes every target of one entry erasure inside the caller's transaction.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A live delete of a Campaign entry whose Campaign is no longer registered, a reclamation whose
    /// compare-and-swap lost, or a canonical family that grew a curation table this plan does not know.
    /// </exception>
    internal static async Task<CovenantEntryErasureTally> RunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CovenantEntryErasureSubject subject,
        CovenantArtifactPlanMode mode,
        CovenantEntryErasureMode erasureMode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(transaction);

        ArgumentNullException.ThrowIfNull(subject);

        RequireModes(mode, erasureMode);

        RequireKnownFamily();

        bool deleting = mode is CovenantArtifactPlanMode.Delete;

        bool live = erasureMode is CovenantEntryErasureMode.Live;

        if (deleting && live && subject.Scope is CovenantScope.Campaign
            && !await CampaignIsLiveAsync(connection, transaction, subject.CampaignId!.Value, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("A live erase of a Campaign entry requires its Campaign to be registered.");
        }

        IReadOnlyList<(Guid VersionId, CovenantLane Lane)> versions = await ReadVersionsAsync(connection, transaction, subject, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<Guid> versionIds = [.. versions.Select(static version => version.VersionId)];

        IReadOnlyList<HeadRow> heads = await ReadHeadRowsAsync(connection, transaction, subject, cancellationToken).ConfigureAwait(false);

        bool hasDocuments = await ObjectExistsAsync(connection, transaction, SearchDocuments, cancellationToken).ConfigureAwait(false);

        bool verified = await FullTextSecureDeleteVerifiedAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        bool retainsMask = subject.IsMasked && !subject.ReclaimsKey;

        // Read before any head is deleted in either mode: deleting a head never touches the epoch, and
        // the compare-and-swap below needs the value this transaction saw.
        long reclamationEpoch = subject.ReclaimsKey
            ? await ReadReclamationEpochAsync(connection, transaction, cancellationToken).ConfigureAwait(false)
            : 0;

        Dictionary<string, long> counts = new(StringComparer.Ordinal);

        // Deleted whenever the table exists. The full-text verdict is read for the record only, so an
        // index whose secure delete cannot be proved still loses the entry's documents.
        counts[SearchDocuments] = hasDocuments
            ? await StepAsync(connection, transaction, mode, SearchDocuments, EntryPredicate("EntryId"), subject, versionIds, cancellationToken).ConfigureAwait(false)
            : 0;

        counts[Outbox] = await StepAsync(connection, transaction, mode, Outbox, SearchRowPredicate(heads), subject, versionIds, cancellationToken)
            .ConfigureAwait(false);

        if (deleting && live && heads.Count > 0)
        {
            await AppendAbsentDeltasAsync(connection, transaction, heads, cancellationToken).ConfigureAwait(false);
        }

        counts[MutationReceipts] = await StepAsync(
            connection, transaction, mode, MutationReceipts, MutationReceiptPredicate(subject, versionIds), subject, versionIds, cancellationToken)
            .ConfigureAwait(false);

        counts[Provenance] = await StepAsync(
            connection, transaction, mode, Provenance, VersionPredicate("VersionId", versionIds), subject, versionIds, cancellationToken)
            .ConfigureAwait(false);

        counts[Heads] = await StepAsync(connection, transaction, mode, Heads, EntryPredicate("EntryId"), subject, versionIds, cancellationToken)
            .ConfigureAwait(false);

        // The versions' delete removes their review events and decision receipts by cascade, which
        // SQLite does not count as the statement's change, so both are measured first, in either mode.
        counts[ReviewEvents] = await StepAsync(
            connection, transaction, CovenantArtifactPlanMode.Count, ReviewEvents, ReviewEventPredicate(versionIds), subject, versionIds, cancellationToken)
            .ConfigureAwait(false);

        counts[DecisionReceipts] = await StepAsync(
            connection, transaction, CovenantArtifactPlanMode.Count, DecisionReceipts, DecisionReceiptPredicate(versionIds), subject, versionIds, cancellationToken)
            .ConfigureAwait(false);

        counts[Versions] = await StepAsync(connection, transaction, mode, Versions, EntryPredicate("EntryId"), subject, versionIds, cancellationToken)
            .ConfigureAwait(false);

        counts[Entries] = await StepAsync(connection, transaction, mode, Entries, EntryPredicate("EntryId"), subject, versionIds, cancellationToken)
            .ConfigureAwait(false);

        foreach (string table in KeyFamily)
        {
            if (string.Equals(table, KeyEpochs, StringComparison.Ordinal))
            {
                if (subject.ReclaimsKey)
                {
                    counts[KeyEpochs] = await StepAsync(
                        connection, transaction, mode, KeyEpochs, "NormalizedKey = $key", subject, versionIds, cancellationToken)
                        .ConfigureAwait(false);
                }

                continue;
            }

            counts[table] = await StepAsync(
                connection, transaction, mode, table, CurationPredicate(table, subject), subject, versionIds, cancellationToken)
                .ConfigureAwait(false);
        }

        if (deleting && subject.ReclaimsKey)
        {
            await AdvanceReclamationEpochAsync(connection, transaction, reclamationEpoch, cancellationToken).ConfigureAwait(false);
        }

        List<MemoryErasureTableCount> targets = [.. OrderedTargets.Select(table => new MemoryErasureTableCount(table, counts[table]))];

        if (subject.ReclaimsKey)
        {
            targets.Add(new MemoryErasureTableCount(KeyEpochs, counts[KeyEpochs]));
        }

        return new CovenantEntryErasureTally(
            targets,
            versionIds,
            versions.Count(static version => version.Lane is CovenantLane.Confirmed),
            versions.Count(static version => version.Lane is CovenantLane.Proposed),
            verified,
            retainsMask,
            subject.ReclaimsKey);
    }

    /// <summary>
    /// Reads one entry as the subject of an erasure, or null when no row has its id.
    /// </summary>
    /// <remarks>
    /// Whether the key is reclaimed is decided here, per entry: a staged restore that purges two entries
    /// of one key reads the second only after the first is gone, and only then does it reclaim.
    /// </remarks>
    /// <exception cref="InvalidDataException">Two rows share the entry's normalised id, or a stored identity is not a GUID.</exception>
    internal static async Task<CovenantEntryErasureSubject?> ReadSubjectAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid entryId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(transaction);

        string entry = CovenantIdentitySql.Key(entryId);

        CovenantScope scope;

        Guid? campaignId;

        string normalizedKey;

        string createdAtUtc;

        await using (SqliteCommand command = Command(
            connection,
            transaction,
            $"""
            SELECT ScopeCode, CampaignId, NormalizedKey, CreatedAtUtc
            FROM covenant_entries
            WHERE {CovenantIdentitySql.Keyed("EntryId", "$entry")};
            """))
        {
            _ = command.Parameters.AddWithValue("$entry", entry);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            scope = (CovenantScope)reader.GetInt32(0);

            campaignId = reader.IsDBNull(1) ? null : ParseGuid(reader.GetString(1));

            normalizedKey = reader.GetString(2);

            createdAtUtc = reader.GetString(3);

            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("Two Covenant entries share one normalised entry id.");
            }
        }

        List<CovenantLane> lanes = [];

        List<long> searchRows = [];

        await using (SqliteCommand command = Command(
            connection,
            transaction,
            $"""
            SELECT LaneCode, SearchRowId FROM covenant_heads
            WHERE {CovenantIdentitySql.Keyed("EntryId", "$entry")}
            ORDER BY LaneCode;
            """))
        {
            _ = command.Parameters.AddWithValue("$entry", entry);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                lanes.Add((CovenantLane)reader.GetInt32(0));

                searchRows.Add(reader.GetInt64(1));
            }
        }

        bool masked = false;

        if (scope is CovenantScope.Campaign)
        {
            await using SqliteCommand command = Command(
                connection,
                transaction,
                $"""
                SELECT EXISTS (
                    SELECT 1 FROM covenant_curation_heads
                    WHERE ScopeCode = 2
                      AND {CovenantIdentitySql.Keyed("CampaignId", "$campaign")}
                      AND NormalizedKey = $key
                      AND LaneCode = 1
                      AND IsMasked = 1
                      AND KeyEpoch = {BindingEpoch});
                """);

            _ = command.Parameters.AddWithValue("$campaign", CovenantIdentitySql.Key(campaignId!.Value));

            _ = command.Parameters.AddWithValue("$key", normalizedKey);

            masked = await ExistsAsync(command, cancellationToken).ConfigureAwait(false);
        }

        bool reclaims;

        await using (SqliteCommand command = Command(
            connection,
            transaction,
            $"""
            SELECT NOT EXISTS (
                       SELECT 1 FROM covenant_entries
                       WHERE NormalizedKey = $key AND NOT ({CovenantIdentitySql.Keyed("EntryId", "$entry")}))
               AND NOT EXISTS (
                       SELECT 1 FROM covenant_heads
                       WHERE NormalizedKey = $key AND NOT ({CovenantIdentitySql.Keyed("EntryId", "$entry")}));
            """))
        {
            _ = command.Parameters.AddWithValue("$key", normalizedKey);

            _ = command.Parameters.AddWithValue("$entry", entry);

            reclaims = await ExistsAsync(command, cancellationToken).ConfigureAwait(false);
        }

        return new CovenantEntryErasureSubject(
            entryId,
            scope,
            campaignId,
            normalizedKey,
            masked,
            reclaims,
            createdAtUtc,
            lanes,
            searchRows);
    }

    /// <summary>
    /// The authoritative absence proof: every target counted again with the predicate that deleted it,
    /// reporting only what remains.
    /// </summary>
    /// <remarks>
    /// <para>The outbox is counted by the erased version ids alone, so this erase's own content-free
    /// absent deltas, which name no version, are not counted against it.</para>
    ///
    /// <para>The full-text index holds the entry's tokens, so it is proved too, although no plan
    /// statement deletes from it: the search documents' delete trigger does. FTS5 keeps one
    /// <c>covenant_fts_docsize</c> row for every row it indexes, keyed by the content row id, which is
    /// the head's search row id, so that row's absence is the exact proof that the index no longer
    /// holds the entry. An index that has fallen out of step with its documents fails here, and the
    /// erase rolls back rather than reporting an erase the index contradicts.</para>
    /// </remarks>
    internal static async Task<IReadOnlyList<MemoryErasureTableCount>> ProveAbsentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CovenantEntryErasureSubject subject,
        IReadOnlyList<Guid> versionIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(transaction);

        ArgumentNullException.ThrowIfNull(subject);

        ArgumentNullException.ThrowIfNull(versionIds);

        RequireKnownFamily();

        List<(string Table, string Predicate)> proofs = [];

        if (await ObjectExistsAsync(connection, transaction, SearchDocuments, cancellationToken).ConfigureAwait(false))
        {
            proofs.Add((SearchDocuments, EntryPredicate("EntryId")));
        }

        List<MemoryErasureTableCount> remaining = [];

        if (subject.SearchRowIds.Count > 0
            && await ObjectExistsAsync(connection, transaction, FullTextIndex, cancellationToken).ConfigureAwait(false))
        {
            long indexed = await FullTextRowCountAsync(connection, transaction, subject.SearchRowIds, cancellationToken)
                .ConfigureAwait(false);

            if (indexed != 0)
            {
                remaining.Add(new MemoryErasureTableCount(FullTextIndex, indexed));
            }
        }

        proofs.Add((Outbox, VersionPredicate("DesiredVersionId", versionIds)));

        proofs.Add((MutationReceipts, MutationReceiptPredicate(subject, versionIds)));

        proofs.Add((Provenance, VersionPredicate("VersionId", versionIds)));

        proofs.Add((Heads, EntryPredicate("EntryId")));

        proofs.Add((Versions, $"{EntryPredicate("EntryId")} OR {VersionPredicate("VersionId", versionIds)}"));

        proofs.Add((ReviewEvents, $"{EntryPredicate("EntryId")} OR {ReviewEventPredicate(versionIds)}"));

        proofs.Add((DecisionReceipts, DecisionReceiptPredicate(versionIds)));

        proofs.Add((Entries, EntryPredicate("EntryId")));

        foreach (string table in KeyFamily)
        {
            if (string.Equals(table, KeyEpochs, StringComparison.Ordinal))
            {
                if (subject.ReclaimsKey)
                {
                    proofs.Add((KeyEpochs, "NormalizedKey = $key"));
                }

                continue;
            }

            proofs.Add((table, CurationPredicate(table, subject)));
        }

        foreach ((string table, string predicate) in proofs)
        {
            long rows = await StepAsync(
                connection, transaction, CovenantArtifactPlanMode.Count, table, predicate, subject, versionIds, cancellationToken)
                .ConfigureAwait(false);

            if (rows != 0)
            {
                remaining.Add(new MemoryErasureTableCount(table, rows));
            }
        }

        return remaining;
    }

    /// <summary>The canonical tier's recorded schema version, or null when the tier is not installed.</summary>
    internal static async Task<long?> ReadCanonicalSchemaVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using (SqliteCommand probe = Command(
            connection,
            null,
            "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'grimoire_feature_schemas');"))
        {
            if (!await ExistsAsync(probe, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
        }

        await using SqliteCommand command = Command(
            connection,
            null,
            "SELECT SchemaVersion FROM grimoire_feature_schemas WHERE FamilyCode = 1 AND TransactionTierCode = 1;");

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long version ? version : null;
    }

    /// <summary>The entry's live lane heads, keyed by lane.</summary>
    internal static async Task<IReadOnlyDictionary<CovenantLane, CovenantEntryErasureHead>> ReadHeadsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid entryId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(transaction);

        await using SqliteCommand command = Command(
            connection,
            transaction,
            $"""
            SELECT LaneCode, CurrentVersionId, CurrentLaneRevision, CurrentOperationCode
            FROM covenant_heads
            WHERE {CovenantIdentitySql.Keyed("EntryId", "$entry")};
            """);

        _ = command.Parameters.AddWithValue("$entry", CovenantIdentitySql.Key(entryId));

        Dictionary<CovenantLane, CovenantEntryErasureHead> heads = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            heads[(CovenantLane)reader.GetInt32(0)] = new CovenantEntryErasureHead(
                ParseGuid(reader.GetString(1)),
                reader.GetInt64(2),
                (CovenantOperation)reader.GetInt32(3));
        }

        return heads;
    }

    /// <summary>The dataset generation, the key's dependency epoch and the key-reclamation epoch.</summary>
    /// <exception cref="InvalidDataException">The canonical tier has no state row.</exception>
    internal static async Task<CovenantEntryErasureState> ReadStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string normalizedKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(transaction);

        await using SqliteCommand command = Command(
            connection,
            transaction,
            """
            SELECT DatasetGeneration,
                   COALESCE((SELECT KeyEpoch FROM covenant_key_epochs WHERE NormalizedKey = $key), 0),
                   KeyReclamationEpoch
            FROM covenant_state
            WHERE StateKey = 1;
            """);

        _ = command.Parameters.AddWithValue("$key", normalizedKey);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The Covenant canonical tier has no state row.");
        }

        return new CovenantEntryErasureState(new Guid((byte[])reader.GetValue(0)), reader.GetInt64(1), reader.GetInt64(2));
    }

    /// <summary>Whether the Campaign is still registered in Core, compared normalised.</summary>
    internal static async Task<bool> CampaignIsLiveAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid campaignId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using SqliteCommand command = Command(
            connection,
            transaction,
            $"""SELECT EXISTS(SELECT 1 FROM "Campaigns" WHERE {CovenantIdentitySql.Keyed("\"Id\"", "$campaign")});""");

        _ = command.Parameters.AddWithValue("$campaign", CovenantIdentitySql.Key(campaignId));

        return await ExistsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The scope facts an erase plan reports beside its counts.</summary>
    internal static async Task<CovenantEntryErasureScopeFacts> ReadScopeFactsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CovenantEntryErasureSubject subject,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(transaction);

        ArgumentNullException.ThrowIfNull(subject);

        bool pinned;

        await using (SqliteCommand command = Command(
            connection,
            transaction,
            $"SELECT EXISTS(SELECT 1 FROM covenant_curation_heads WHERE {SubjectScopePredicate(subject)} AND IsPinned = 1 AND KeyEpoch = {BindingEpoch});"))
        {
            Bind(command, subject, []);

            pinned = await ExistsAsync(command, cancellationToken).ConfigureAwait(false);
        }

        if (subject.Scope is CovenantScope.Global)
        {
            await using SqliteCommand count = Command(connection, transaction, """SELECT count(*) FROM "Campaigns";""");

            return new CovenantEntryErasureScopeFacts(
                pinned,
                GlobalConfirmedResurfaces: false,
                Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture));
        }

        bool resurfaces;

        await using (SqliteCommand command = Command(
            connection,
            transaction,
            $"""
            SELECT EXISTS (
                       SELECT 1 FROM covenant_heads
                       WHERE {CovenantIdentitySql.Keyed("EntryId", "$entry")} AND LaneCode = 1 AND CurrentOperationCode = 1)
               AND EXISTS (
                       SELECT 1 FROM covenant_heads
                       WHERE CampaignId IS NULL AND NormalizedKey = $key AND LaneCode = 1 AND CurrentOperationCode = 1);
            """))
        {
            Bind(command, subject, []);

            resurfaces = await ExistsAsync(command, cancellationToken).ConfigureAwait(false);
        }

        return new CovenantEntryErasureScopeFacts(pinned, resurfaces && !(subject.IsMasked && !subject.ReclaimsKey), 1);
    }

    /// <summary>The Covenant facts a preflight reports for one measured plan.</summary>
    /// <remarks>
    /// Built here rather than by the service, so the plan stays the one entry-erasure file that names a
    /// Covenant table. Curation rows are the three curation tables together.
    /// </remarks>
    internal static CovenantErasurePlanFacts Facts(
        CovenantEntryErasureSubject subject,
        CovenantEntryErasureTally measured,
        CovenantEntryErasureScopeFacts scope)
    {
        ArgumentNullException.ThrowIfNull(subject);

        ArgumentNullException.ThrowIfNull(measured);

        ArgumentNullException.ThrowIfNull(scope);

        int Rows(string table) =>
            checked((int)measured.Targets.Single(target => string.Equals(target.Table, table, StringComparison.Ordinal)).Rows);

        return new CovenantErasurePlanFacts(
            measured.ConfirmedVersions,
            measured.ProposedVersions,
            Rows(Provenance),
            Rows(MutationReceipts),
            checked(Rows(CurationHeads) + Rows(CurationVersions) + Rows(CurationReceipts)),
            Rows(Outbox),
            Rows(SearchDocuments),
            subject.ReclaimsKey,
            measured.RetainsCampaignMask,
            scope.GlobalConfirmedResurfaces,
            scope.IsPinned,
            checked((int)scope.AffectedCampaigns));
    }

    /// <summary>One target's count, or its delete reporting the rows the statement itself removed.</summary>
    private static async Task<long> StepAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CovenantArtifactPlanMode mode,
        string table,
        string predicate,
        CovenantEntryErasureSubject subject,
        IReadOnlyList<Guid> versionIds,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = Command(
            connection,
            transaction,
            mode is CovenantArtifactPlanMode.Count
                ? $"SELECT count(*) FROM {table} WHERE {predicate};"
                : $"DELETE FROM {table} WHERE {predicate};");

        Bind(command, subject, versionIds);

        return mode is CovenantArtifactPlanMode.Count
            ? Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture)
            : await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One content-free absent delta per erased head at the next canonical search sequence, and the
    /// sequence advanced once, exactly as owner cleanup removes a head.
    /// </summary>
    private static async Task AppendAbsentDeltasAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<HeadRow> heads,
        CancellationToken cancellationToken)
    {
        long sequence;

        await using (SqliteCommand read = Command(connection, transaction, "SELECT CanonicalSearchSequence FROM covenant_state WHERE StateKey = 1;"))
        {
            sequence = checked(Convert.ToInt64(await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) + 1);
        }

        for (int ordinal = 0; ordinal < heads.Count; ordinal++)
        {
            await using SqliteCommand insert = Command(
                connection,
                transaction,
                """
                INSERT INTO covenant_search_outbox (SearchSequence, Ordinal, SearchRowId, EntryId, LaneCode, DesiredVersionId)
                VALUES ($sequence, $ordinal, $row, $entry, $lane, NULL);
                """);

            _ = insert.Parameters.AddWithValue("$sequence", sequence);

            _ = insert.Parameters.AddWithValue("$ordinal", ordinal);

            _ = insert.Parameters.AddWithValue("$row", heads[ordinal].SearchRowId);

            _ = insert.Parameters.AddWithValue("$entry", heads[ordinal].EntryId);

            _ = insert.Parameters.AddWithValue("$lane", heads[ordinal].LaneCode);

            _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using SqliteCommand advance = Command(
            connection,
            transaction,
            """
            UPDATE covenant_state
            SET CanonicalSearchSequence = $sequence, UpdatedAtUtc = $updated
            WHERE StateKey = 1 AND CanonicalSearchSequence = $sequence - 1;
            """);

        _ = advance.Parameters.AddWithValue("$sequence", sequence);

        _ = advance.Parameters.AddWithValue("$updated", UtcInstantText.Format(DateTimeOffset.UtcNow));

        if (await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The canonical search sequence moved inside the erase's own transaction.");
        }
    }

    /// <summary>Advances the key-reclamation epoch by exactly one from the value this transaction read.</summary>
    private static async Task AdvanceReclamationEpochAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long read,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = Command(
            connection,
            transaction,
            """
            UPDATE covenant_state
            SET KeyReclamationEpoch = KeyReclamationEpoch + 1, UpdatedAtUtc = $updated
            WHERE StateKey = 1 AND KeyReclamationEpoch = $read;
            """);

        _ = command.Parameters.AddWithValue("$updated", UtcInstantText.Format(DateTimeOffset.UtcNow));

        _ = command.Parameters.AddWithValue("$read", read);

        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The key-reclamation epoch moved inside the erase's own transaction.");
        }
    }

    private static async Task<long> ReadReclamationEpochAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = Command(connection, transaction, "SELECT KeyReclamationEpoch FROM covenant_state WHERE StateKey = 1;");

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long epoch
            ? epoch
            : throw new InvalidDataException("The Covenant canonical tier has no state row.");
    }

    private static async Task<IReadOnlyList<(Guid VersionId, CovenantLane Lane)>> ReadVersionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CovenantEntryErasureSubject subject,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = Command(
            connection,
            transaction,
            $"SELECT VersionId, LaneCode FROM covenant_versions WHERE {EntryPredicate("EntryId")} ORDER BY LaneCode, LaneRevision;");

        Bind(command, subject, []);

        List<(Guid, CovenantLane)> versions = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            versions.Add((ParseGuid(reader.GetString(0)), (CovenantLane)reader.GetInt32(1)));
        }

        return versions;
    }

    private static async Task<IReadOnlyList<HeadRow>> ReadHeadRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CovenantEntryErasureSubject subject,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = Command(
            connection,
            transaction,
            $"SELECT EntryId, LaneCode, SearchRowId FROM covenant_heads WHERE {EntryPredicate("EntryId")} ORDER BY SearchRowId;");

        Bind(command, subject, []);

        List<HeadRow> heads = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            heads.Add(new HeadRow(reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2)));
        }

        return heads;
    }

    /// <summary>How many of the given search rows the full-text index still holds.</summary>
    private static async Task<long> FullTextRowCountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<long> searchRowIds,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = Command(
            connection,
            transaction,
            $"SELECT count(*) FROM covenant_fts_docsize WHERE id IN ({string.Join(", ", searchRowIds.Select(static row => row.ToString(CultureInfo.InvariantCulture)))});");

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The full-text verdict: secure delete reads 1 on <c>covenant_fts</c>, or the index is absent and
    /// holds nothing to scrub.
    /// </summary>
    private static async Task<bool> FullTextSecureDeleteVerifiedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (!await ObjectExistsAsync(connection, transaction, "covenant_fts", cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        await using SqliteCommand command = Command(connection, transaction, "SELECT v FROM covenant_fts_config WHERE k = 'secure-delete';");

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is not (null or DBNull) && Convert.ToInt64(value, CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<bool> ObjectExistsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = Command(
            connection,
            transaction,
            "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name);");

        _ = command.Parameters.AddWithValue("$name", table);

        return await ExistsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Rows naming the entry, its id compared normalised.</summary>
    private static string EntryPredicate(string column) => CovenantIdentitySql.Keyed(column, "$entry");

    /// <summary>Rows naming one of the captured version ids, compared normalised, or none when there are none.</summary>
    private static string VersionPredicate(string column, IReadOnlyList<Guid> versionIds) =>
        versionIds.Count == 0
            ? "0"
            : $"lower(replace({column}, '-', '')) IN ({string.Join(", ", Enumerable.Range(0, versionIds.Count).Select(static index => $"$v{index}"))})";

    private static string ReviewEventPredicate(IReadOnlyList<Guid> versionIds) => VersionPredicate("VersionId", versionIds);

    private static string DecisionReceiptPredicate(IReadOnlyList<Guid> versionIds) =>
        $"ReviewEventSequence IN (SELECT Sequence FROM covenant_review_events WHERE {EntryPredicate("EntryId")} OR {ReviewEventPredicate(versionIds)})";

    /// <summary>The pending deltas of the entry's heads, by their projection row ids.</summary>
    private static string SearchRowPredicate(IReadOnlyList<HeadRow> heads) =>
        heads.Count == 0
            ? "0"
            : $"SearchRowId IN ({string.Join(", ", heads.Select(static head => head.SearchRowId.ToString(CultureInfo.InvariantCulture)))})";

    /// <summary>
    /// The entry's mutation receipts: by its id, by a version it produced, and the NoChange receipts
    /// written before canonical version 6 named an entry, in its scope, Campaign and lanes since it was
    /// created.
    /// </summary>
    /// <remarks>
    /// The last arm is deliberately conservative: such a receipt carries no key, so another key's NoChange
    /// receipt in the same scope, lanes and window is deleted with it. A very late replay of that other
    /// mutation then answers stale rather than replaying.
    /// </remarks>
    private static string MutationReceiptPredicate(CovenantEntryErasureSubject subject, IReadOnlyList<Guid> versionIds)
    {
        string lanes = subject.HeadLanes.Count == 0
            ? "0"
            : $"LaneCode IN ({string.Join(", ", subject.HeadLanes.Select(static lane => ((int)lane).ToString(CultureInfo.InvariantCulture)))})";

        return $"""
            {EntryPredicate("EntryId")}
            OR {VersionPredicate("ResultingVersionId", versionIds)}
            OR (EntryId IS NULL AND OutcomeCode = {OutcomeNoChange} AND ScopeCode = $scope AND {CampaignPredicate(subject)}
                AND {lanes} AND CommittedAtUtc >= $created)
            """;
    }

    /// <summary>
    /// One curation table's rows the erase removes: the key in every scope when the key is reclaimed;
    /// otherwise the subject's rows in both lanes and every epoch, less a retained Campaign mask and its
    /// chain.
    /// </summary>
    private static string CurationPredicate(string table, CovenantEntryErasureSubject subject)
    {
        if (subject.ReclaimsKey)
        {
            return "NormalizedKey = $key";
        }

        string scoped = SubjectScopePredicate(subject);

        if (!subject.IsMasked)
        {
            return scoped;
        }

        // Kept: the Campaign's Confirmed mask at the key's binding epoch, the versions of its chain, and
        // the receipts that produced them.
        string retainedVersions = $"LaneCode = 1 AND KeyEpoch = {BindingEpoch}";

        return table switch
        {
            CurationHeads => $"{scoped} AND NOT (LaneCode = 1 AND KeyEpoch = {BindingEpoch} AND IsMasked = 1)",
            CurationVersions => $"{scoped} AND NOT ({retainedVersions})",
            CurationReceipts => $"""
                {scoped} AND (ResultingVersionId IS NULL OR lower(replace(ResultingVersionId, '-', '')) NOT IN (
                    SELECT lower(replace(CurationVersionId, '-', '')) FROM covenant_curation_versions
                    WHERE {scoped} AND {retainedVersions}))
                """,
            _ => throw new InvalidOperationException("The canonical family names a curation table the entry erasure does not know."),
        };
    }

    /// <summary>The erased subject's scope, Campaign and key, as curation and receipts record them.</summary>
    private static string SubjectScopePredicate(CovenantEntryErasureSubject subject) =>
        $"ScopeCode = $scope AND {CampaignPredicate(subject)} AND NormalizedKey = $key";

    private static string CampaignPredicate(CovenantEntryErasureSubject subject) =>
        subject.Scope is CovenantScope.Global ? "CampaignId IS NULL" : CovenantIdentitySql.Keyed("CampaignId", "$campaign");

    /// <summary>Binds every parameter a plan statement can name; a statement ignores those it does not.</summary>
    private static void Bind(SqliteCommand command, CovenantEntryErasureSubject subject, IReadOnlyList<Guid> versionIds)
    {
        _ = command.Parameters.AddWithValue("$entry", CovenantIdentitySql.Key(subject.EntryId));

        _ = command.Parameters.AddWithValue("$scope", (int)subject.Scope);

        _ = command.Parameters.AddWithValue(
            "$campaign",
            subject.CampaignId is { } campaign ? CovenantIdentitySql.Key(campaign) : DBNull.Value);

        _ = command.Parameters.AddWithValue("$key", subject.NormalizedKey);

        _ = command.Parameters.AddWithValue("$created", subject.CreatedAtUtc);

        for (int index = 0; index < versionIds.Count; index++)
        {
            _ = command.Parameters.AddWithValue($"$v{index}", CovenantIdentitySql.Key(versionIds[index]));
        }
    }

    private static void RequireModes(CovenantArtifactPlanMode mode, CovenantEntryErasureMode erasureMode)
    {
        if (mode is not (CovenantArtifactPlanMode.Count or CovenantArtifactPlanMode.Delete))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "A plan run counts or deletes.");
        }

        if (erasureMode is not (CovenantEntryErasureMode.Live or CovenantEntryErasureMode.Staged))
        {
            throw new ArgumentOutOfRangeException(nameof(erasureMode), erasureMode, "An entry erasure is live or staged.");
        }
    }

    /// <summary>
    /// Refuses a canonical family whose key-keyed tables are not exactly this plan's curation targets
    /// followed by the key's epoch row.
    /// </summary>
    private static void RequireKnownFamily()
    {
        string[] curation = [.. KeyFamily.Where(static table => table.StartsWith(CurationPrefix, StringComparison.Ordinal))];

        bool known = KeyFamily.Length == curation.Length + 1
            && string.Equals(KeyFamily[^1], KeyEpochs, StringComparison.Ordinal)
            && curation.Order(StringComparer.Ordinal).SequenceEqual(
                [CurationHeads, CurationReceipts, CurationVersions],
                StringComparer.Ordinal);

        if (!known)
        {
            throw new InvalidOperationException("The canonical family's key-keyed tables are not the ones the entry erasure removes.");
        }
    }

    private static Guid ParseGuid(string value) =>
        Guid.TryParse(value, CultureInfo.InvariantCulture, out Guid parsed)
            ? parsed
            : throw new InvalidDataException("A Covenant identity this erase reads is not a GUID.");

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        return command;
    }

    private static async Task<bool> ExistsAsync(SqliteCommand command, CancellationToken cancellationToken) =>
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) switch
        {
            0L => false,
            1L => true,
            _ => throw new InvalidDataException("A Covenant erasure existence check did not answer 0 or 1."),
        };

    /// <summary>One head of the erased entry, as its absent delta names it.</summary>
    private sealed record HeadRow(string EntryId, int LaneCode, long SearchRowId);
}
