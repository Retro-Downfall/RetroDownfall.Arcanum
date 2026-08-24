using System.Collections.Immutable;
using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// The bounded canonical read port over raw, parameterized SQLite commands.
/// </summary>
/// <remarks>
/// Every method validates its caller-owned lease before it opens anything, opens exactly one read
/// transaction, and closes it before returning. Nothing here acquires a lease, starts a write, or
/// retries: escalating coverage would defeat the gate's drain proof, and a retry would silently
/// straddle two read snapshots.
///
/// <para>The turn load verifies every artifact it returns. A Confirmed artifact that fails is a
/// structured failure, because the authoritative context must never be silently omitted; a Proposed
/// one is quarantined, because review-only content is not worth failing a turn over.</para>
/// </remarks>
internal sealed class CovenantStore(ICovenantConnectionSource connections) : ICovenantStore
{

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async ValueTask<Result<CovenantTurnSnapshot>> ReadTurnSnapshotAsync(
        CanonicalCampaignContext campaign,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken)
    {

        Guid? campaignId = campaign.IsCampaignBound ? campaign.CampaignId : null;

        CovenantOperationScope required = campaignId is { } present
            ? CovenantOperationScope.ForCampaign(present)
            : CovenantOperationScope.Global;

        Result validated = await ValidateLeaseAsync(readLease, required, requireInstallation: false, cancellationToken)
            .ConfigureAwait(false);

        if (validated.IsFailure)
        {

            return validated.Error;

        }

        SqliteConnection connection = await connections.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = CovenantStoreSql.TurnSnapshot();

        Bind(command, "$campaign", campaignId is { } bound ? bound.ToString("D") : DBNull.Value);

        Guid datasetGeneration = Guid.Empty;

        ulong canonicalSequence = 0;

        List<CovenantSnapshotCandidate> candidates = [];

        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                datasetGeneration = ReadGuidBlob(reader, 0);

                canonicalSequence = checked((ulong)reader.GetInt64(1));

                if (reader.IsDBNull(2))
                {

                    // The left join produced the state row alone: this installation has no active
                    // heads, which is an ordinary empty snapshot rather than an error.
                    continue;

                }

                Result<CovenantSnapshotCandidate> candidate = MaterializeCandidate(reader, offset: 2);

                if (candidate.IsFailure)
                {

                    return candidate.Error;

                }

                candidates.Add(candidate.Value);

            }

        }

        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

        if (datasetGeneration == Guid.Empty)
        {

            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "The Covenant canonical tier has no state row, so no turn snapshot can be bound to a dataset.");

        }

        if (candidates.Count > CovenantLimits.MaxActiveSnapshotRows)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "The active Covenant head set exceeds its hard bound, so the canonical tier is not linkable.");

        }

        try
        {

            return new CovenantTurnSnapshot(
                new CovenantGenerationId(datasetGeneration),
                campaignId,
                canonicalSequence,
                [.. candidates]);

        }
        catch (ArgumentException exception)
        {

            return new Error(ErrorCodes.Covenant.IntegrityFailure, exception.Message);

        }

    }

    public async ValueTask<Result<CovenantLaneHeadProbe>> ProbeLaneHeadAsync(
        CanonicalCampaignContext campaign,
        CovenantLane lane,
        string normalizedKey,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(normalizedKey);

        Guid? campaignId = campaign.IsCampaignBound ? campaign.CampaignId : null;

        CovenantOperationScope scope = campaignId is { } present
            ? CovenantOperationScope.ForCampaign(present)
            : CovenantOperationScope.Global;

        Result validated = await ValidateLeaseAsync(readLease, scope, requireInstallation: false, cancellationToken)
            .ConfigureAwait(false);

        if (validated.IsFailure)
        {

            return validated.Error;

        }

        SqliteConnection connection = await connections.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = CovenantStoreSql.LaneHeadProbe(campaignScoped: campaignId is not null);

        Bind(command, "$key", normalizedKey);

        Bind(command, "$lane", (int)lane);

        if (campaignId is { } bound)
        {

            Bind(command, "$campaign", bound.ToString("D"));

        }

        CovenantLaneHeadProbe probe = CovenantLaneHeadProbe.NotFound(scope, lane, normalizedKey, 0);

        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {

            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                long keyEpoch = reader.GetInt64(0);

                probe = reader.IsDBNull(1)
                    ? CovenantLaneHeadProbe.NotFound(scope, lane, normalizedKey, keyEpoch)
                    : new CovenantLaneHeadProbe(
                        scope,
                        lane,
                        normalizedKey,
                        reader.GetInt32(4) == (int)CovenantOperation.Set
                            ? CovenantLaneHeadPresence.Present
                            : CovenantLaneHeadPresence.Retired,
                        ReadGuidText(reader, 1),
                        ReadGuidText(reader, 2),
                        reader.GetInt64(3),
                        (CovenantOrigin)reader.GetInt32(5),
                        reader.GetInt64(6),
                        keyEpoch);

            }

        }

        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

        return probe;

    }

    public async ValueTask<Result<CovenantListPage>> ReadListPageAsync(
        CovenantListQuery query,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(query);

        Result<CovenantOperationScope?> scope = ResolveSelection(query.ScopeSelection, query.CampaignId);

        if (scope.IsFailure)
        {

            return scope.Error;

        }

        Result validated = await ValidateLeaseAsync(
                readLease,
                scope.Value,
                requireInstallation: scope.Value is null,
                cancellationToken)
            .ConfigureAwait(false);

        if (validated.IsFailure)
        {

            return validated.Error;

        }

        SqliteConnection connection = await connections.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = CovenantStoreSql.ListPage(
            scope.Value,
            query.Lane is not null,
            query.Lifecycle,
            query.After is not null);

        Bind(command, "$limit", query.EffectivePageSize + 1);

        if (scope.Value is { Kind: CovenantScope.Campaign } campaignScope)
        {

            Bind(command, "$campaign", campaignScope.CampaignId!.Value.ToString("D"));

        }

        if (query.Lane is { } lane)
        {

            Bind(command, "$lane", (int)lane);

        }

        if (query.After is { } after)
        {

            Bind(command, "$afterScope", (int)after.ScopeOrdinal);

            Bind(command, "$afterCampaign", after.CampaignId == Guid.Empty ? string.Empty : after.CampaignId.ToString("D"));

            Bind(command, "$afterKey", after.NormalizedKey);

            Bind(command, "$afterEntry", after.EntryId.ToString("D"));

            Bind(command, "$afterLane", (int)after.LaneOrdinal);

        }

        Guid datasetGeneration = Guid.Empty;

        long canonicalSequence = 0;

        long deletionSequence = 0;

        List<CovenantHeadItem> items = [];

        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                datasetGeneration = ReadGuidBlob(reader, 0);

                canonicalSequence = reader.GetInt64(1);

                deletionSequence = reader.GetInt64(2);

                if (reader.IsDBNull(3))
                {

                    continue;

                }

                items.Add(MaterializeHeadItem(reader, offset: 3));

            }

        }

        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

        bool hasMore = items.Count > query.EffectivePageSize;

        if (hasMore)
        {

            items.RemoveAt(items.Count - 1);

        }

        CovenantListKeyset? next = hasMore && items.Count > 0
            ? ToKeyset(items[^1])
            : null;

        return new CovenantListPage(
            [.. items],
            next,
            datasetGeneration,
            canonicalSequence,
            deletionSequence,
            hasMore);

    }

    public async ValueTask<Result<CovenantDetail>> ReadDetailAsync(
        CovenantDetailQuery query,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(query);

        Result validated = await ValidateLeaseAsync(readLease, query.Scope, requireInstallation: false, cancellationToken)
            .ConfigureAwait(false);

        if (validated.IsFailure)
        {

            return validated.Error;

        }

        SqliteConnection connection = await connections.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = CovenantStoreSql.Detail(query.Scope.Kind == CovenantScope.Campaign);

        Bind(command, "$key", query.NormalizedKey);

        if (query.Scope.Kind == CovenantScope.Campaign)
        {

            Bind(command, "$campaign", query.Scope.CampaignId!.Value.ToString("D"));

        }

        Guid datasetGeneration = Guid.Empty;

        long canonicalSequence = 0;

        long keyEpoch = 0;

        CovenantHeadItem? confirmed = null;

        CovenantHeadItem? proposed = null;

        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                datasetGeneration = ReadGuidBlob(reader, 0);

                canonicalSequence = reader.GetInt64(1);

                keyEpoch = reader.GetInt64(2);

                if (reader.IsDBNull(3))
                {

                    continue;

                }

                CovenantHeadItem item = MaterializeHeadItem(reader, offset: 3);

                if (item.Lane == CovenantLane.Confirmed)
                {

                    confirmed = item;

                }
                else
                {

                    proposed = item;

                }

            }

        }

        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

        return new CovenantDetail(
            query.Scope,
            query.NormalizedKey,
            confirmed?.EntryId ?? proposed?.EntryId,
            confirmed,
            proposed,
            keyEpoch,
            datasetGeneration,
            canonicalSequence);

    }

    public async ValueTask<Result<CovenantVersionPage>> ReadVersionPageAsync(
        CovenantVersionQuery query,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(query);

        Result validated = await ValidateLeaseAsync(readLease, required: null, requireInstallation: false, cancellationToken)
            .ConfigureAwait(false);

        if (validated.IsFailure)
        {

            return validated.Error;

        }

        SqliteConnection connection = await connections.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = CovenantStoreSql.VersionPage(query.After is not null);

        Bind(command, "$entry", query.EntryId.ToString("D"));

        Bind(command, "$lane", (int)query.Lane);

        Bind(command, "$limit", query.EffectivePageSize + 1);

        if (query.After is { } after)
        {

            Bind(command, "$afterRevision", after.LaneRevision);

        }

        Guid datasetGeneration = Guid.Empty;

        long canonicalSequence = 0;

        List<CovenantVersionItem> items = [];

        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                datasetGeneration = ReadGuidBlob(reader, 0);

                canonicalSequence = reader.GetInt64(1);

                if (reader.IsDBNull(2))
                {

                    continue;

                }

                items.Add(
                    new CovenantVersionItem(
                        ReadGuidText(reader, 2)!.Value,
                        ReadGuidText(reader, 3)!.Value,
                        (CovenantLane)reader.GetInt32(4),
                        reader.GetInt64(5),
                        (CovenantOperation)reader.GetInt32(6),
                        (CovenantOrigin)reader.GetInt32(7),
                        ReadOptionalDigest(reader, 8),
                        ReadOptionalDigest(reader, 9),
                        reader.GetInt64(10),
                        reader.GetInt32(11),
                        reader.GetInt32(12),
                        ReadGuidText(reader, 13),
                        ReadGuidText(reader, 14)!.Value,
                        checked((uint)reader.GetInt64(15)),
                        new CovenantDigest(ReadBlob(reader, 16)),
                        ReadTimestamp(reader, 17)));

            }

        }

        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

        bool hasMore = items.Count > query.EffectivePageSize;

        if (hasMore)
        {

            items.RemoveAt(items.Count - 1);

        }

        CovenantVersionKeyset? next = hasMore && items.Count > 0
            ? new CovenantVersionKeyset(items[^1].LaneRevision, items[^1].VersionId)
            : null;

        return new CovenantVersionPage([.. items], next, datasetGeneration, canonicalSequence, hasMore);

    }

    public async ValueTask<Result<CovenantSourcePage>> ReadSourcePageAsync(
        CovenantSourceQuery query,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(query);

        Result validated = await ValidateLeaseAsync(readLease, required: null, requireInstallation: false, cancellationToken)
            .ConfigureAwait(false);

        if (validated.IsFailure)
        {

            return validated.Error;

        }

        SqliteConnection connection = await connections.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = CovenantStoreSql.SourcePage();

        Bind(command, "$version", query.VersionId.ToString("D"));

        uint storedCount = 0;

        CovenantDigest storedDigest = default;

        bool found = false;

        List<CovenantSourceItem> items = [];

        List<MaterializationSourceDigestInput> leaves = [];

        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                found = true;

                storedCount = checked((uint)reader.GetInt64(0));

                storedDigest = new CovenantDigest(ReadBlob(reader, 1));

                if (reader.IsDBNull(2))
                {

                    continue;

                }

                Guid attachmentId = ReadGuidText(reader, 3)!.Value;

                string identity = reader.GetString(4);

                string logicalKey = reader.GetString(5);

                CovenantDigest contentHash = new(ReadBlob(reader, 6));

                CovenantMaterializationSourceRange rangeKind =
                    (CovenantMaterializationSourceRange)reader.GetInt32(7);

                long? start = reader.IsDBNull(8) ? null : reader.GetInt64(8);

                long? end = reader.IsDBNull(9) ? null : reader.GetInt64(9);

                items.Add(
                    new CovenantSourceItem(
                        reader.GetInt32(2),
                        attachmentId,
                        identity,
                        logicalKey,
                        contentHash,
                        rangeKind,
                        start,
                        end,
                        ReadGuidText(reader, 10),
                        reader.IsDBNull(11) ? null : reader.GetString(11)));

                leaves.Add(
                    new MaterializationSourceDigestInput(
                        attachmentId,
                        Guid.TryParse(identity, out Guid parsed) ? parsed : Guid.Empty,
                        logicalKey,
                        contentHash,
                        rangeKind,
                        start is { } presentStart ? checked((uint)presentStart) : null,
                        end is { } presentEnd ? checked((uint)presentEnd) : null,
                        []));

            }

        }

        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

        if (!found)
        {

            return new Error(ErrorCodes.Covenant.NotFound, "No Covenant version with that identity exists.");

        }

        if (items.Count > CovenantLimits.MaxVersionSources)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "This Covenant version carries more attachment sources than its hard bound allows.");

        }

        CovenantDigest recomputed;

        try
        {

            recomputed = CovenantDigests.Materialization(
                new MaterializationDigestInput(Unprovenanced: leaves.Count == 0, [.. leaves]));

        }
        catch (ArgumentException exception)
        {

            return new Error(ErrorCodes.Covenant.IntegrityFailure, exception.Message);

        }

        return new CovenantSourcePage(query.VersionId, [.. items], storedCount, storedDigest, recomputed);

    }

    /// <summary>
    /// Counts current heads by scope, lane, and lifecycle without reading a byte of content.
    /// </summary>
    /// <remarks>
    /// One grouped scan over the head table rather than a page walk: the caller wants totals, and
    /// paging to a total would make the cost of asking "how much do I have" grow with the answer.
    ///
    /// <para>Requires the installation read capability because it crosses every Campaign. A scoped
    /// lease could only ever answer for its own scope, and a census that silently omitted the rest
    /// would understate what an operator holds.</para>
    ///
    /// <para>Two scans, because the two answers are shaped differently. The bucket counts fold every
    /// Campaign together; the Campaign byte totals must not, since the ceiling they are read against
    /// bounds one Campaign's rendered section on one turn. A second grouped scan takes the largest
    /// single Campaign per lane, which is the only Campaign figure that number actually bounds.</para>
    /// </remarks>
    public async ValueTask<Result<CovenantScopeCensus>> ReadScopeCensusAsync(
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken)
    {

        Result validated = await ValidateLeaseAsync(
                readLease,
                required: null,
                requireInstallation: true,
                cancellationToken)
            .ConfigureAwait(false);

        if (validated.IsFailure)
        {

            return validated.Error;

        }

        SqliteConnection connection = await connections.GetOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                .ConfigureAwait(false);

        try
        {

            await using SqliteCommand command = connection.CreateCommand();

            command.Transaction = transaction;

            command.CommandText = """
                SELECT ScopeCode, LaneCode, CurrentOperationCode, COUNT(*), COALESCE(SUM(CompiledByteCost), 0)
                FROM covenant_heads
                GROUP BY ScopeCode, LaneCode, CurrentOperationCode
                ORDER BY ScopeCode, LaneCode, CurrentOperationCode;
                """;

            ImmutableArray<CovenantScopeCensusRow>.Builder rows =
                ImmutableArray.CreateBuilder<CovenantScopeCensusRow>();

            long globalConfirmed = 0;

            await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false))
            {

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {

                    CovenantScope scope = (CovenantScope)reader.GetInt64(0);

                    CovenantLane lane = (CovenantLane)reader.GetInt64(1);

                    // A head whose current operation is a tombstone is a retired entry. The lifecycle
                    // an operator reads is derived from that, never stored twice.
                    CovenantLifecycle lifecycle = (CovenantOperation)reader.GetInt64(2) is CovenantOperation.Retire
                        ? CovenantLifecycle.Retired
                        : CovenantLifecycle.Set;

                    long count = reader.GetInt64(3);

                    long bytes = reader.GetInt64(4);

                    rows.Add(new CovenantScopeCensusRow(scope, lane, lifecycle, count, bytes));

                    // Retired heads need no filtering here. A tombstone version carries neither
                    // compiled content nor a nonzero byte cost by table constraint, versions are
                    // append-only, and a trigger pins every head's byte cost to its own version's —
                    // so a retired head's recorded cost is zero and cannot become anything else. A
                    // guard would be a branch no writer can reach, and the section totals below
                    // would read as though it were load-bearing.
                    if (scope is CovenantScope.Global)
                    {

                        globalConfirmed += bytes;

                    }

                }

            }

            await using SqliteCommand campaignPeak = connection.CreateCommand();

            campaignPeak.Transaction = transaction;

            // Summed inside one Campaign, then maximised across Campaigns. The inner sum is what a
            // single turn would render for that Campaign's lane; the outer maximum is the worst case
            // an operator can act on. Doing it in SQL keeps the per-Campaign totals out of the
            // projection, which has to stay content-free — a Campaign identity is a pointer to
            // protected content even when no byte of it travels.
            campaignPeak.CommandText = """
                SELECT LaneCode, COALESCE(MAX(LaneBytes), 0)
                FROM (
                    SELECT LaneCode, CampaignId, SUM(CompiledByteCost) AS LaneBytes
                    FROM covenant_heads
                    WHERE ScopeCode = 2
                    GROUP BY LaneCode, CampaignId
                )
                GROUP BY LaneCode;
                """;

            long maxCampaignConfirmed = 0;

            long maxCampaignProposed = 0;

            await using (SqliteDataReader reader = await campaignPeak.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false))
            {

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {

                    if ((CovenantLane)reader.GetInt64(0) is CovenantLane.Confirmed)
                    {

                        maxCampaignConfirmed = reader.GetInt64(1);

                    }
                    else
                    {

                        maxCampaignProposed = reader.GetInt64(1);

                    }

                }

            }

            return new CovenantScopeCensus(
                rows.ToImmutable(),
                globalConfirmed,
                maxCampaignConfirmed,
                maxCampaignProposed);

        }
        catch (SqliteException exception)
        {

            // A read-only status request must not unwind through the endpoint. The management
            // service already renders a census it could not take as empty counts beside health that
            // says the tier was not read, and that is only reachable through a returned failure.
            return new Error(
                ErrorCodes.Covenant.Unavailable,
                $"The Covenant canonical tier could not be read for a census: {exception.SqliteErrorCode}.");

        }
        finally
        {

            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

        }

    }

    public async ValueTask<Result<CovenantMutationEffectSnapshot>> ReadMutationEffectSnapshotAsync(
        CovenantMutationEffectQuery query,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(query);

        // A Global effect scan crosses every Campaign, so it takes the one all-scopes capability. A
        // Campaign effect scan stays inside its own scope.
        bool global = query.Scope.Kind == CovenantScope.Global;

        Result validated = await ValidateLeaseAsync(
                readLease,
                global ? null : query.Scope,
                requireInstallation: global,
                cancellationToken)
            .ConfigureAwait(false);

        if (validated.IsFailure)
        {

            return validated.Error;

        }

        SqliteConnection connection = await connections.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                .ConfigureAwait(false);

        EffectFacts facts = await ReadEffectFactsAsync(connection, transaction, query.NormalizedKey, cancellationToken)
            .ConfigureAwait(false);

        LocalHeadFacts local = await ReadLocalHeadFactsAsync(
                connection,
                transaction,
                query,
                cancellationToken)
            .ConfigureAwait(false);

        List<CovenantMutationEffectExample> examples = [];

        List<DependentHeadDigestInput> vector = [];

        long affected = 0;

        await using (SqliteCommand command = connection.CreateCommand())
        {

            command.Transaction = transaction;

            command.CommandText = CovenantStoreSql.DependentHeadScan(allCampaigns: global);

            Bind(command, "$key", query.NormalizedKey);

            if (!global)
            {

                BindCoreCampaignIdentity(command, "$campaign", query.Scope.CampaignId!.Value);

            }

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                Guid campaignId = ReadGuidText(reader, 0)!.Value;

                long confirmedRevision = reader.GetInt64(1);

                long proposedRevision = reader.GetInt64(2);

                affected = checked(affected + 1);

                vector.Add(
                    new DependentHeadDigestInput(
                        campaignId,
                        confirmedRevision > 0,
                        proposedRevision > 0,
                        checked((ulong)confirmedRevision),
                        checked((ulong)proposedRevision)));

                if (examples.Count < CovenantMutationEffectSnapshot.MaxExamples)
                {

                    examples.Add(
                        new CovenantMutationEffectExample(
                            campaignId,
                            global
                                ? DecideForCampaign(query.Operation, confirmedRevision > 0)
                                : DecideLocally(query, local),
                            confirmedRevision > 0,
                            proposedRevision > 0));

                }

            }

        }

        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

        CovenantDigest vectorDigest = CovenantDigests.DependentHeadVector(
            new DependentHeadVectorDigestInput(
                query.Scope.Kind,
                query.Scope.CampaignId,
                query.NormalizedKey,
                query.Lane,
                query.Operation,
                checked((ulong)facts.KeyEpoch),
                checked((ulong)facts.KeyReclamationEpoch),
                checked((ulong)facts.CampaignRegistryEpoch),
                [.. vector]));

        return new CovenantMutationEffectSnapshot(
            query.Scope,
            query.NormalizedKey,
            query.Lane,
            query.Operation,
            DecideLocally(query, local),
            affected,
            [.. examples],
            affected > CovenantMutationEffectSnapshot.MaxExamples,
            vectorDigest,
            facts.KeyEpoch,
            facts.KeyReclamationEpoch,
            facts.CampaignRegistryEpoch,
            facts.DatasetGeneration,
            facts.CanonicalSearchSequence);

    }

    private static CovenantEffectDecision DecideForCampaign(CovenantOperation operation, bool hasConfirmedHead) =>
        hasConfirmedHead
            ? CovenantEffectDecision.NoEffect
            : operation == CovenantOperation.Retire
                ? CovenantEffectDecision.HeadRetired
                : CovenantEffectDecision.GlobalConfirmedResurfaces;

    private static CovenantEffectDecision DecideLocally(CovenantMutationEffectQuery query, LocalHeadFacts local)
    {

        if (query.Operation == CovenantOperation.Retire)
        {

            return local.Present ? CovenantEffectDecision.HeadRetired : CovenantEffectDecision.NoEffect;

        }

        if (query.Lane == CovenantLane.Proposed)
        {

            return local.EffectiveConfirmedPresent
                ? CovenantEffectDecision.ProposedRemainsReviewOnly
                : CovenantEffectDecision.ProposedBecomesEligible;

        }

        return local.Present ? CovenantEffectDecision.HeadUpdated : CovenantEffectDecision.HeadCreated;

    }

    private static async Task<EffectFacts> ReadEffectFactsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string normalizedKey,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = CovenantStoreSql.EffectFacts();

        Bind(command, "$key", normalizedKey);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new EffectFacts(
                ReadGuidBlob(reader, 0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4))
            : new EffectFacts(Guid.Empty, 0, 0, 0, 0);

    }

    private static async Task<LocalHeadFacts> ReadLocalHeadFactsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CovenantMutationEffectQuery query,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = CovenantStoreSql.LocalHeadFactsSql(query.Scope.Kind == CovenantScope.Campaign);

        Bind(command, "$key", query.NormalizedKey);

        if (query.Scope.Kind == CovenantScope.Campaign)
        {

            Bind(command, "$campaign", query.Scope.CampaignId!.Value.ToString("D"));

        }

        bool present = false;

        bool confirmedPresent = false;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            CovenantLane lane = (CovenantLane)reader.GetInt32(0);

            bool live = reader.GetInt32(1) == (int)CovenantOperation.Set;

            if (lane == query.Lane)
            {

                present = live;

            }

            if (lane == CovenantLane.Confirmed && live)
            {

                confirmedPresent = true;

            }

        }

        return new LocalHeadFacts(present, confirmedPresent);

    }

    private static Result<CovenantOperationScope?> ResolveSelection(
        CovenantCursorScopeSelection selection,
        Guid? campaignId) =>
        selection switch
        {
            CovenantCursorScopeSelection.Global when campaignId is null =>
                Result<CovenantOperationScope?>.Success(CovenantOperationScope.Global),

            CovenantCursorScopeSelection.Campaign when campaignId is { } present && present != Guid.Empty =>
                Result<CovenantOperationScope?>.Success(CovenantOperationScope.ForCampaign(present)),

            CovenantCursorScopeSelection.AllScopes when campaignId is null =>
                Result<CovenantOperationScope?>.Success(null),

            _ => Result<CovenantOperationScope?>.Failure(
                new Error(
                    ErrorCodes.Covenant.InvalidScope,
                    "The Covenant scope selection and Campaign identity do not agree.")),
        };

    private async ValueTask<Result> ValidateLeaseAsync(
        ICovenantSnapshotReadLease? readLease,
        CovenantOperationScope? required,
        bool requireInstallation,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(readLease);

        Result revalidated = await readLease.RevalidateAsync(cancellationToken).ConfigureAwait(false);

        if (revalidated.IsFailure)
        {

            return revalidated;

        }

        CovenantOperationLeaseSnapshot snapshot = readLease.Snapshot;

        if (snapshot.Coverage == CovenantLeaseCoverage.Installation)
        {

            return Result.Success();

        }

        if (requireInstallation)
        {

            return new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "An all-scopes Covenant read requires the installation read capability.");

        }

        if (required is null)
        {

            return Result.Success();

        }

        CovenantOperationScope held = snapshot.Scope!.Value;

        bool matches = held.Kind == required.Value.Kind && held.CampaignId == required.Value.CampaignId;

        return matches
            ? Result.Success()
            : new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "This Covenant lease does not cover the scope the read names.");

    }

    private static Result<CovenantSnapshotCandidate> MaterializeCandidate(SqliteDataReader reader, int offset)
    {

        long searchRowId = reader.GetInt64(offset + 0);

        Guid entryId = ReadGuidText(reader, offset + 1)!.Value;

        Guid versionId = ReadGuidText(reader, offset + 2)!.Value;

        string normalizedKey = reader.GetString(offset + 3);

        CovenantScope scope = (CovenantScope)reader.GetInt32(offset + 4);

        Guid? campaignId = ReadGuidText(reader, offset + 5);

        CovenantLane lane = (CovenantLane)reader.GetInt32(offset + 6);

        CovenantOperation operation = (CovenantOperation)reader.GetInt32(offset + 7);

        long laneRevision = reader.GetInt64(offset + 8);

        long headByteCost = reader.GetInt64(offset + 9);

        CovenantOrigin headOrigin = (CovenantOrigin)reader.GetInt32(offset + 10);

        CovenantOrigin versionOrigin = (CovenantOrigin)reader.GetInt32(offset + 12);

        Guid? predecessorId = ReadGuidText(reader, offset + 13);

        int compilerPolicy = reader.GetInt32(offset + 14);

        int rendererPolicy = reader.GetInt32(offset + 15);

        CovenantDigest? authoredHash = ReadOptionalDigest(reader, offset + 16);

        CovenantDigest? renderedHash = ReadOptionalDigest(reader, offset + 17);

        long provenanceCount = reader.GetInt64(offset + 18);

        CovenantDigest provenanceDigest = new(ReadBlob(reader, offset + 19));

        string? compiledContent = reader.IsDBNull(offset + 20) ? null : reader.GetString(offset + 20);

        byte[] compiledUtf8 = compiledContent is null ? [] : StrictUtf8.GetBytes(compiledContent);

        string? damage = DetectDamage(
            normalizedKey,
            headOrigin,
            versionOrigin,
            compilerPolicy,
            rendererPolicy,
            authoredHash,
            renderedHash,
            headByteCost,
            provenanceCount,
            provenanceDigest,
            compiledUtf8);

        if (damage is not null && lane == CovenantLane.Confirmed)
        {

            return new Error(ErrorCodes.Covenant.IntegrityFailure, damage);

        }

        CovenantSnapshotCandidateIntegrity integrity = damage is null
            ? CovenantSnapshotCandidateIntegrity.Verified
            : CovenantSnapshotCandidateIntegrity.Quarantined;

        try
        {

            return new CovenantSnapshotCandidate(
                checked((ulong)searchRowId),
                entryId,
                versionId,
                new CovenantKey(normalizedKey),
                scope,
                campaignId,
                lane,
                operation,
                versionOrigin,
                checked((ulong)laneRevision),
                predecessorId,
                checked((uint)compilerPolicy),
                checked((uint)rendererPolicy),
                authoredHash ?? default,
                renderedHash ?? default,
                checked((uint)provenanceCount),
                provenanceDigest,
                [.. compiledUtf8],
                integrity);

        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {

            return new Error(ErrorCodes.Covenant.IntegrityFailure, exception.Message);

        }

    }

    /// <summary>
    /// Every check the loader can make without leaving the row it already has. Returns the first
    /// failure, or null when the artifact is intact.
    /// </summary>
    private static string? DetectDamage(
        string normalizedKey,
        CovenantOrigin headOrigin,
        CovenantOrigin versionOrigin,
        int compilerPolicy,
        int rendererPolicy,
        CovenantDigest? authoredHash,
        CovenantDigest? renderedHash,
        long headByteCost,
        long provenanceCount,
        CovenantDigest provenanceDigest,
        byte[] compiledUtf8)
    {

        if (headOrigin != versionOrigin)
        {

            return "A Covenant head and its current version disagree about origin.";

        }

        if (compilerPolicy != CovenantCompiler.CompilerPolicyVersion
            || rendererPolicy != CovenantCompiler.RendererPolicyVersion)
        {

            return "A Covenant version was compiled under an unsupported policy version.";

        }

        if (authoredHash is null || renderedHash is null)
        {

            return "A live Covenant version is missing its authored or rendered hash.";

        }

        if (headByteCost != compiledUtf8.Length)
        {

            return "A Covenant head's recorded byte cost disagrees with its compiled fragment.";

        }

        if (provenanceCount is < 0 or > CovenantLimits.MaxVersionSources)
        {

            return "A Covenant version reports more attachment sources than its bound allows.";

        }

        if (!provenanceDigest.IsValid)
        {

            return "A Covenant version carries a malformed provenance digest.";

        }

        return CovenantCompiler.HashFragment(normalizedKey, compiledUtf8) == renderedHash.Value
            ? null
            : "A Covenant compiled fragment does not match its stored rendered hash.";

    }

    private static CovenantHeadItem MaterializeHeadItem(SqliteDataReader reader, int offset) =>
        new(
            ReadGuidText(reader, offset + 1)!.Value,
            ReadGuidText(reader, offset + 2)!.Value,
            (CovenantScope)reader.GetInt32(offset + 4),
            ReadGuidText(reader, offset + 5),
            reader.GetString(offset + 3),
            reader.GetString(offset + 21),
            (CovenantLane)reader.GetInt32(offset + 6),
            reader.GetInt64(offset + 8),
            reader.GetInt32(offset + 7) == (int)CovenantOperation.Set
                ? CovenantLifecycle.Set
                : CovenantLifecycle.Retired,
            (CovenantOrigin)reader.GetInt32(offset + 10),
            ReadOptionalDigest(reader, offset + 16),
            ReadOptionalDigest(reader, offset + 17),
            reader.GetInt64(offset + 9),
            checked((uint)reader.GetInt64(offset + 18)),
            new CovenantDigest(ReadBlob(reader, offset + 19)),
            reader.GetInt64(offset + 0),
            ReadTimestamp(reader, offset + 22),
            ReadTimestamp(reader, offset + 11));

    private static CovenantListKeyset ToKeyset(CovenantHeadItem item) =>
        new(
            (byte)item.Scope,
            item.CampaignId ?? Guid.Empty,
            item.NormalizedKey,
            item.EntryId,
            (byte)item.Lane);

    private static void Bind(SqliteCommand command, string name, object value) =>
        _ = command.Parameters.AddWithValue(name, value);

    /// <summary>
    /// Binds a Campaign identity that will be compared against the EF-owned <c>"Campaigns"."Id"</c>.
    /// </summary>
    /// <remarks>
    /// The value is bound as a <see cref="Guid"/> rather than as a formatted string so the provider
    /// produces exactly the uppercase text EF stored. A lowercase <c>D</c>-format literal matched no
    /// row for any identity carrying a hex letter, which made a Campaign-scoped effect preflight
    /// report that its mutation affected nothing at all. Covenant's own tables keep the lowercase
    /// representation their writers use, so this helper is only for that EF-owned crossing.
    /// </remarks>
    private static void BindCoreCampaignIdentity(SqliteCommand command, string name, Guid value) =>
        _ = command.Parameters.AddWithValue(name, value);

    private static Guid ReadGuidBlob(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? Guid.Empty : new Guid((byte[])reader.GetValue(ordinal));

    private static Guid? ReadGuidText(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);

    private static byte[] ReadBlob(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? [] : (byte[])reader.GetValue(ordinal);

    private static CovenantDigest? ReadOptionalDigest(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : new CovenantDigest((byte[])reader.GetValue(ordinal));

    private static DateTimeOffset ReadTimestamp(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? default
            : DateTimeOffset.Parse(
                reader.GetString(ordinal),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private readonly record struct EffectFacts(
        Guid DatasetGeneration,
        long CanonicalSearchSequence,
        long KeyReclamationEpoch,
        long KeyEpoch,
        long CampaignRegistryEpoch);

    private readonly record struct LocalHeadFacts(bool Present, bool EffectiveConfirmedPresent);

}
