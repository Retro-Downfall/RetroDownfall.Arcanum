using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// The observation a full installation reset committed for one Campaign before it touched either
/// host-tools marker.
/// </summary>
/// <remarks>
/// Commitments only. There is no path, no marker payload, and no handle here, so a row that leaked
/// would let its holder recognize an observation it already knew and nothing else.
/// </remarks>
internal sealed record CampaignPathFullResetCleanupEvidenceRow(
    Guid IntentId,
    CovenantDigest CampaignInventoryEntryDigest,
    CovenantDigest IndexedPhysicalIdentityDigest,
    CovenantDigest CanonicalDisplayPathDigest,
    CovenantDigest SameHandleOwnershipEvidenceDigest,
    CampaignPathFullResetCleanupObservationCode ObservationCode,
    // Present only for an opened root, and then equal to the authenticated expectation above. The
    // table enforces both halves, because a row carrying a different opened digest would already be
    // usable deletion authority by the time anything compared them.
    CovenantDigest? OpenedSameHandleOwnershipEvidenceDigest,
    CovenantDigest ObservationDigest);

/// <summary>
/// One kind-four child as recovery has to see it: the intent and the evidence behind it, together.
/// </summary>
/// <remarks>
/// Deliberately one type rather than two lookups. A replay that read the parents and then the
/// companions could align them wrongly and never notice; this one cannot produce a child whose two
/// halves came from different rows.
/// </remarks>
internal sealed record CampaignPathFullResetCleanupChildRow(
    CampaignPathMarkerIntentRow Intent,
    CampaignPathFullResetCleanupEvidenceRow Evidence);

/// <summary>
/// The only reader and writer of <c>campaign_path_full_reset_cleanup_evidence</c>.
/// </summary>
/// <remarks>
/// Transaction-bound and never registered in DI, like the intent store beside it. There is no delete
/// and no update method at all: the row is written once from an observation nobody can repeat, and
/// it leaves only by cascade when its parent intent is removed. Exposing either verb would create a
/// production caller for a transition the schema's own triggers exist to refuse.
/// </remarks>
internal sealed class CampaignPathFullResetCleanupEvidenceStore
{

    private const int FullInstallationResetCleanupKind = 4;

    private readonly CovenantSqliteConnectionInitializer _initializer;

    private readonly SqliteConnection _connection;

    private readonly SqliteTransaction _transaction;

    internal CampaignPathFullResetCleanupEvidenceStore(
        CovenantSqliteConnectionInitializer initializer,
        SqliteConnection connection,
        SqliteTransaction transaction)
    {

        ArgumentNullException.ThrowIfNull(initializer);

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(transaction);

        // Same rule as the intent store: authorizing on one connection and writing through another
        // would leave the guard trigger looking at an unauthorized connection while the caller
        // believed it had permission.
        if (!ReferenceEquals(transaction.Connection, connection))
        {

            throw new ArgumentException(
                "A full-reset cleanup evidence store requires the live transaction of its own connection.",
                nameof(transaction));

        }

        _initializer = initializer;

        _connection = connection;

        _transaction = transaction;

    }

    /// <summary>
    /// Commits the evidence for one already-inserted kind-four parent.
    /// </summary>
    /// <remarks>
    /// No insert-or-read twin, unlike the parent. The parent's owner/Campaign/kind uniqueness is the
    /// replay key for the pair; a companion that quietly returned an existing row would let a second
    /// attempt adopt an observation it never made.
    /// </remarks>
    internal async Task<Result> InsertAsync(
        CampaignPathFullResetCleanupEvidenceRow row,
        CancellationToken cancellationToken)
    {

        if (row is null || !IsWellFormed(row))
        {

            return Result.Failure(new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A full-installation reset cleanup child requires complete observation evidence."));

        }

        using CovenantSqliteAuthorizationScope scope = _initializer.Authorize(
            _connection,
            CovenantSqliteAuthorizationKind.CampaignPathMarkerIntentMutation);

        await using SqliteCommand command = _connection.CreateCommand();

        command.Transaction = _transaction;

        command.CommandText = """
            INSERT INTO campaign_path_full_reset_cleanup_evidence (
                IntentId, CampaignInventoryEntryDigest, IndexedPhysicalIdentityDigest,
                CanonicalDisplayPathDigest, SameHandleOwnershipEvidenceDigest, ObservationCode,
                OpenedSameHandleOwnershipEvidenceDigest, ObservationDigest)
            VALUES (
                $intent, $entry, $identity,
                $display, $ownership, $observation,
                $opened, $digest);
            """;

        _ = command.Parameters.AddWithValue("$intent", row.IntentId.ToString("D"));

        _ = command.Parameters.AddWithValue("$entry", row.CampaignInventoryEntryDigest.Bytes);

        _ = command.Parameters.AddWithValue("$identity", row.IndexedPhysicalIdentityDigest.Bytes);

        _ = command.Parameters.AddWithValue("$display", row.CanonicalDisplayPathDigest.Bytes);

        _ = command.Parameters.AddWithValue(
            "$ownership",
            row.SameHandleOwnershipEvidenceDigest.Bytes);

        _ = command.Parameters.AddWithValue("$observation", (int)row.ObservationCode);

        _ = command.Parameters.AddWithValue(
            "$opened",
            row.OpenedSameHandleOwnershipEvidenceDigest is { IsValid: true } opened
                ? opened.Bytes
                : DBNull.Value);

        _ = command.Parameters.AddWithValue("$digest", row.ObservationDigest.Bytes);

        int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return affected == 1
            ? Result.Success()
            : Result.Failure(new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A full-installation reset cleanup child was not committed."));

    }

    /// <summary>
    /// Reads every kind-four child one owner holds, each with the evidence behind it.
    /// </summary>
    /// <remarks>
    /// A left join rather than an inner one, on purpose. An inner join would silently drop a parent
    /// whose companion is missing, and "missing" is exactly the state a replay must refuse rather
    /// than count past — so a companion-less parent surfaces here as a malformed row and fails the
    /// read instead of shrinking the vector.
    /// </remarks>
    internal async Task<Result<IReadOnlyList<CampaignPathFullResetCleanupChildRow>>>
        ReadOwnerChildrenAsync(
            Guid ownerOperationId,
            CancellationToken cancellationToken)
    {

        if (ownerOperationId == Guid.Empty)
        {

            return Malformed();

        }

        await using SqliteCommand command = _connection.CreateCommand();

        command.Transaction = _transaction;

        command.CommandText = """
            SELECT i.IntentId, i.OwnerOperationId, i.CampaignId, i.IntentKindCode,
                   i.ExclusiveOwnerOperationCode, i.OwnerEffectDigest, i.MarkerDigest,
                   i.TargetDisplayPath, i.PriorRevision, i.PhaseCode, i.PhaseRevision,
                   i.PendingDispositionCode,
                   e.CampaignInventoryEntryDigest, e.IndexedPhysicalIdentityDigest,
                   e.CanonicalDisplayPathDigest, e.SameHandleOwnershipEvidenceDigest,
                   e.ObservationCode, e.OpenedSameHandleOwnershipEvidenceDigest,
                   e.ObservationDigest
            FROM campaign_path_marker_intents AS i
            LEFT JOIN campaign_path_full_reset_cleanup_evidence AS e ON e.IntentId = i.IntentId
            WHERE i.OwnerOperationId = $owner AND i.IntentKindCode = $kind
            ORDER BY i.CampaignId;
            """;

        _ = command.Parameters.AddWithValue("$owner", ownerOperationId.ToString("D"));

        _ = command.Parameters.AddWithValue("$kind", FullInstallationResetCleanupKind);

        List<CampaignPathFullResetCleanupChildRow> children = [];

        HashSet<Guid> campaigns = [];

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            if (!TryProject(reader, out CampaignPathFullResetCleanupChildRow? child)
                || !campaigns.Add(child.Intent.CampaignId))
            {

                return Malformed();

            }

            children.Add(child);

        }

        return children;

    }

    /// <summary>
    /// Every column is proven here rather than trusted, because this projection is positional.
    /// </summary>
    /// <remarks>
    /// A left join means the companion half can legitimately be all nulls, so "absent" has to be
    /// distinguished from "present and malformed" before anything downstream reads a digest. Both
    /// end in the same refusal; neither ends in a child.
    /// </remarks>
    private static bool TryProject(
        SqliteDataReader reader,
        out CampaignPathFullResetCleanupChildRow child)
    {

        child = null!;

        if (reader.GetValue(0) is not string intentText
            || !Guid.TryParse(intentText, out Guid intentId)
            || intentId == Guid.Empty
            || reader.GetValue(1) is not string ownerText
            || !Guid.TryParse(ownerText, out Guid ownerOperationId)
            || reader.GetValue(2) is not string campaignText
            || !Guid.TryParse(campaignText, out Guid campaignId)
            || campaignId == Guid.Empty
            || reader.GetValue(3) is not long kind
            || kind != FullInstallationResetCleanupKind
            || !reader.IsDBNull(4)
            || !TryDigest(reader, 5, out CovenantDigest ownerEffectDigest)
            || !TryDigest(reader, 6, out CovenantDigest markerDigest)
            || reader.GetValue(8) is not long priorRevision
            || priorRevision <= 0
            || reader.GetValue(9) is not long phaseCode
            || phaseCode is not (1 or 12 or 14)
            || reader.GetValue(10) is not long phaseRevision
            || phaseRevision <= 0
            || !reader.IsDBNull(11))
        {

            return false;

        }

        string? targetDisplayPath = reader.IsDBNull(7) ? null : reader.GetValue(7) as string;

        if (!reader.IsDBNull(7) && targetDisplayPath is null)
        {

            return false;

        }

        if (!TryDigest(reader, 12, out CovenantDigest inventoryEntryDigest)
            || !TryDigest(reader, 13, out CovenantDigest indexedIdentityDigest)
            || !TryDigest(reader, 14, out CovenantDigest displayPathDigest)
            || !TryDigest(reader, 15, out CovenantDigest ownershipDigest)
            || reader.GetValue(16) is not long observationCode
            || observationCode is not (1 or 2 or 3)
            || !TryDigest(reader, 18, out CovenantDigest observationDigest))
        {

            return false;

        }

        CovenantDigest? openedOwnershipDigest = null;

        if (!reader.IsDBNull(17))
        {

            if (!TryDigest(reader, 17, out CovenantDigest opened))
            {

                return false;

            }

            openedOwnershipDigest = opened;

        }

        CampaignPathFullResetCleanupEvidenceRow evidence = new(
            intentId,
            inventoryEntryDigest,
            indexedIdentityDigest,
            displayPathDigest,
            ownershipDigest,
            (CampaignPathFullResetCleanupObservationCode)observationCode,
            openedOwnershipDigest,
            observationDigest);

        if (!IsWellFormed(evidence))
        {

            return false;

        }

        child = new CampaignPathFullResetCleanupChildRow(
            new CampaignPathMarkerIntentRow(
                intentId,
                ownerOperationId,
                campaignId,
                CampaignPathMarkerIntentKind.FullInstallationResetCleanup,
                ExclusiveOwnerOperation: null,
                ownerEffectDigest,
                markerDigest,
                targetDisplayPath,
                priorRevision,
                (CampaignPathMarkerPhase)phaseCode,
                phaseRevision,
                PendingDisposition: null),
            evidence);

        return true;

    }

    /// <summary>
    /// The opened/blocked shape, restated in code so a row that reached the table before its guards
    /// existed still cannot be adopted.
    /// </summary>
    private static bool IsWellFormed(CampaignPathFullResetCleanupEvidenceRow row) =>
        row.IntentId != Guid.Empty
        && row.CampaignInventoryEntryDigest.IsValid
        && row.IndexedPhysicalIdentityDigest.IsValid
        && row.CanonicalDisplayPathDigest.IsValid
        && row.SameHandleOwnershipEvidenceDigest.IsValid
        && row.ObservationDigest.IsValid
        && row.ObservationCode switch
        {

            CampaignPathFullResetCleanupObservationCode.Opened =>
                row.OpenedSameHandleOwnershipEvidenceDigest is { IsValid: true } opened
                    && opened == row.SameHandleOwnershipEvidenceDigest,

            CampaignPathFullResetCleanupObservationCode.Unavailable
                or CampaignPathFullResetCleanupObservationCode.Mismatch =>
                row.OpenedSameHandleOwnershipEvidenceDigest is null,

            _ => false,

        };

    private static bool TryDigest(SqliteDataReader reader, int ordinal, out CovenantDigest digest)
    {

        digest = default;

        if (reader.IsDBNull(ordinal)
            || reader.GetValue(ordinal) is not byte[] bytes
            || bytes.Length != CovenantLimits.DigestBytes)
        {

            return false;

        }

        digest = new CovenantDigest(bytes);

        return true;

    }

    private static Result<IReadOnlyList<CampaignPathFullResetCleanupChildRow>> Malformed() =>
        Result<IReadOnlyList<CampaignPathFullResetCleanupChildRow>>.Failure(new Error(
            ErrorCodes.Covenant.IntegrityFailure,
            "The full-installation reset Campaign cleanup journal is not readable as written."));

}
