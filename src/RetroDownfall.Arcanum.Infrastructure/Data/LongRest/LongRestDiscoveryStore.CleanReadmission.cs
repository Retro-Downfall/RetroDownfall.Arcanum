using System.Globalization;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

internal interface ILongRestCleanReadmission
{
    Task<Result<LongRestDiscoveryGeneration>> ReadmitCleanAsync(LongRestDiscoveryGeneration expected, LongRestSweepFence current,
        LongRestRecipeIdentity recipe, string workerOwner, LongRestLiveFenceReader facts,
        IGrimoireWorkLease admission, CancellationToken ct);
}

internal sealed partial class LongRestDiscoveryStore : ILongRestCleanReadmission
{
    public async Task<Result<LongRestDiscoveryGeneration>> ReadmitCleanAsync(LongRestDiscoveryGeneration expected, LongRestSweepFence current,
        LongRestRecipeIdentity recipe, string workerOwner, LongRestLiveFenceReader facts,
        IGrimoireWorkLease admission, CancellationToken ct)
    {
        SqliteBusyRetryCursor retry = SqliteBusyRetry.Begin(BusyRetryDeadlineForTesting);

        try
        {
            while (true)
            {
                try
                {
                    SqliteConnection connection = await OpenAsync(ct).ConfigureAwait(false);

                    await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

                    LongRestVerificationCounters.Transaction(deferred: false);

                    LongRestDiscoveryGeneration generation = await ReadSweepGenerationAsync(connection, transaction, expected.OperationId, ct).ConfigureAwait(false)
                        ?? throw new InvalidDataException();

                    if (generation != expected || generation.Scope.ProtectedPartitionDigest is not null || generation.Recipe != recipe
                        || generation.Phase is LongRestSweepPhase.Completed or LongRestSweepPhase.Invalidated or LongRestSweepPhase.Deferred or LongRestSweepPhase.Retired
                        || generation.Fence with { AdmissionGeneration = current.AdmissionGeneration } != current)
                    {
                        throw new InvalidDataException();
                    }

                    // The interval the lease must attest starts at the admission this generation was captured under.
                    // It is read once from the reloaded row: the adopted fence below no longer carries it.
                    long previousAdmission = generation.Fence.AdmissionGeneration;

                    await ValidateCleanOwnerAsync(connection, transaction, generation, current, workerOwner, facts, admission, previousAdmission, ct).ConfigureAwait(false);

                    await using (SqliteCommand ownership = Command(connection, transaction, """
                        SELECT EXISTS(SELECT 1 FROM long_rest_sweeps WHERE OperationId = $operation AND GenerationId = $generation
                            AND ScopeKindCode = $scope AND CampaignId IS $campaign AND PartitionDigest IS NULL)
                          AND NOT EXISTS(SELECT 1 FROM long_rest_work_items WHERE
                            (GenerationId = $generation AND OperationId <> $operation) OR (OperationId = $operation AND GenerationId <> $generation));
                        """, ("$operation", generation.OperationId.ToString("N")), ("$generation", Format(generation.GenerationId)),
                        ("$scope", (int)generation.Scope.Kind), ("$campaign", generation.Scope.CampaignId is { } campaign ? Format(campaign) : null)))
                    {
                        if ((long)(await ownership.ExecuteScalarAsync(ct).ConfigureAwait(false))! != 1) throw new InvalidDataException();
                    }

                    // A same-boot abort cannot certify an unknown send. Never use reboot settlement here.
                    await using (SqliteCommand unsettled = Command(connection, transaction, """
                        SELECT count(*) FROM long_rest_provider_effects effect JOIN long_rest_work_items unit ON unit.WorkItemId = effect.WorkItemId
                        WHERE unit.OperationId = $operation AND unit.GenerationId = $generation AND effect.StateCode IN (1, 2, 3);
                        """, ("$operation", generation.OperationId.ToString("N")), ("$generation", Format(generation.GenerationId))))
                    {
                        if ((long)(await unsettled.ExecuteScalarAsync(ct).ConfigureAwait(false))! != 0) throw new InvalidDataException();
                    }

                    // Readmission is a seam: whatever the sweep rested on across the abort is re-proved in full before it may continue.
                    await VerifyFrozenSourcesAsync(connection, transaction, generation, null, LongRestVerificationDepth.Seam, ct).ConfigureAwait(false);

                    await ProveCleanWorkAsync(connection, transaction, generation, ct).ConfigureAwait(false);

                    await ValidateCleanOwnerAsync(connection, transaction, generation, current, workerOwner, facts, admission, previousAdmission, ct).ConfigureAwait(false);

                    await AdoptCleanWorkAsync(connection, transaction, generation, current, ct).ConfigureAwait(false);

                    await using SqliteCommand update = Command(connection, transaction, """
                        UPDATE long_rest_discovery_generations SET FenceJson = $fence, Revision = Revision + 1
                        WHERE GenerationId = $generation AND OperationId = $operation AND Revision = $revision AND PhaseCode = $phase;
                        """, ("$fence", JsonSerializer.Serialize(LongRestStoredFence.From(current), LongRestJsonContext.Default.LongRestStoredFence)),
                        ("$generation", Format(generation.GenerationId)), ("$operation", generation.OperationId.ToString("N")),
                        ("$revision", generation.Revision), ("$phase", (int)generation.Phase));

                    if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) throw new InvalidDataException();

                    LongRestDiscoveryGeneration adopted = generation with { Fence = current, Revision = checked(generation.Revision + 1) };

                    await VerifyFrozenSourcesAsync(connection, transaction, adopted, null, LongRestVerificationDepth.Seam, ct).ConfigureAwait(false);

                    await ValidateCleanOwnerAsync(connection, transaction, adopted, current, workerOwner, facts, admission, previousAdmission, ct).ConfigureAwait(false);

                    await transaction.CommitAsync(ct).ConfigureAwait(false);

                    return Result<LongRestDiscoveryGeneration>.Success(adopted);
                }
                catch (Exception busy) when (SqliteBusyRetry.IsBusyOrLocked(busy))
                {
                    await retry.WaitAsync(busy, ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (StorageFailure(exception))
        {
            return Refuse<LongRestDiscoveryGeneration>(ErrorCodes.LongRest.StaleInput, "Clean maintenance continuation could not prove its exact drained admission, worker and retained evidence.");
        }
    }

    private async Task ValidateCleanOwnerAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, LongRestSweepFence current, string workerOwner, LongRestLiveFenceReader facts,
        IGrimoireWorkLease admission, long previousAdmission, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        ValidateCleanLease(facts, admission, previousAdmission, current.AdmissionGeneration, ct);

        if (!await facts.ValidateRequestFenceWithinAsync(connection, transaction, current, ct).ConfigureAwait(false)
            || !facts.IsCleanRecipeCurrent(generation.Recipe)
            || (await LongRestLiveFenceReader.ReadWorkerWithinAsync(connection, transaction, generation, workerOwner, timeProvider.GetUtcNow(), ct).ConfigureAwait(false)).IsFailure)
        {
            throw new InvalidDataException();
        }

        ValidateCleanLease(facts, admission, previousAdmission, current.AdmissionGeneration, ct);
    }

    private static void ValidateCleanLease(LongRestLiveFenceReader facts, IGrimoireWorkLease admission,
        long previousAdmission, long currentAdmission, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (facts.AttestCleanAdmission(admission, previousAdmission, currentAdmission)) return;

        if (admission.MaintenanceRevocationPending) throw new LongRestMaintenanceDeferredException(CancellationToken.None);

        throw new InvalidDataException();
    }

    private static async Task ProveCleanWorkAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, CancellationToken ct)
    {
        string? cursor = null;

        while (true)
        {
            List<CleanWorkRow> units = await ReadCleanWorkPageAsync(connection, transaction, generation, cursor, ct).ConfigureAwait(false);

            if (units.Count == 0) return;

            foreach (var unit in units)
            {
                LongRestWorkIdentity work;

                List<LongRestPaidEvidence> retained = [];

                if (unit.Kind == 2)
                {
                    string[] coordinates = unit.Item.Split(':');

                    if (coordinates.Length != 3 || coordinates[0] != "fold"
                        || !int.TryParse(coordinates[1], NumberStyles.None, CultureInfo.InvariantCulture, out int layer)
                        || !long.TryParse(coordinates[2], NumberStyles.None, CultureInfo.InvariantCulture, out long group)
                        || unit.Disposition is not (1 or 2)) throw new InvalidDataException();

                    // The corpus was proved in full by the caller a moment ago, in this same transaction.
                    LongRestHierarchyFold fold = await ResolveCurrentFoldAsync(connection, transaction, generation, layer, group, null, LongRestVerificationDepth.Step, ct).ConfigureAwait(false);

                    work = fold.Work;

                    LongRestAcceptedHierarchySummary? summary = await ReadAcceptedSummaryAsync(connection, transaction, fold, generation.Recipe, ct).ConfigureAwait(false);

                    if (summary is not null) retained.Add(summary.PaidEvidence);

                    if (unit.Disposition == 2)
                    {
                        if (summary is null) throw new InvalidDataException();

                        _ = await ReadHierarchyNodeAsync(connection, transaction, generation, summary.NodeId, null, ct).ConfigureAwait(false);

                        await using SqliteCommand paid = Command(connection, transaction,
                            "SELECT PaidEvidenceJson FROM long_rest_discovery_embeddings WHERE NodeId = $node;", ("$node", Format(summary.NodeId)));

                        if (await paid.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string json) throw new InvalidDataException();

                        retained.Add((JsonSerializer.Deserialize(json, LongRestJsonContext.Default.LongRestStoredPaid) ?? throw new InvalidDataException()).ToDomain());
                    }
                }
                else
                {
                    LongRestSweepWork anchor = await LongRestSweepStore.VerifyCleanReadmissionWorkAsync(connection, transaction, generation, unit.Item, ct).ConfigureAwait(false);

                    work = anchor.Identity;

                    if (anchor.AcceptedAdjudication?.PaidEvidence is { } paid) retained.Add(paid);
                }

                LongRestWorkIdentity? originalTerminal = await LongRestAttemptStore.ValidateRetainedEffectAsync(connection, transaction, unit.Unit, work, retained, ct).ConfigureAwait(false);

                if (work.ItemId != unit.Item || work.SourceDigest != unit.Source || work.RecipeDigest != unit.Recipe
                    || (unit.Digest != LongRestDiscoveryHash.Work(work).Value && !retained.Any(paid =>
                        LongRestDiscoveryHash.Work(paid.Attempt.Work).Value == unit.Digest && CanAssociateSweepWork(paid.Attempt.Work, work))
                        && !(originalTerminal is not null && LongRestDiscoveryHash.Work(originalTerminal).Value == unit.Digest
                            && CanAssociateSweepWork(originalTerminal, work))))
                {
                    throw new InvalidDataException();
                }
            }

            cursor = units[^1].Unit;
        }
    }

    private static async Task AdoptCleanWorkAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, LongRestSweepFence current, CancellationToken ct)
    {
        string? cursor = null;

        while (true)
        {
            List<CleanWorkRow> units = await ReadCleanWorkPageAsync(connection, transaction, generation, cursor, ct).ConfigureAwait(false);

            if (units.Count == 0) return;

            foreach (CleanWorkRow adoption in units)
            {
                // The read-only first pass proved every row. The same immediate transaction excludes
                // concurrent storage mutation; this pass performs exact CAS without retaining an unbounded plan.
                LongRestWorkIdentity work = new(generation.GenerationId, generation.OperationId, adoption.Item,
                    (LongRestWorkKind)adoption.Kind, generation.Scope, current, adoption.Source, adoption.Recipe);

                await using SqliteCommand bind = Command(connection, transaction, """
                    UPDATE long_rest_work_items SET WorkDigest = $next, Revision = Revision + 1
                    WHERE WorkItemId = $unit AND OperationId = $operation AND GenerationId = $generation AND KindCode = $kind AND ItemId = $item
                      AND WorkDigest = $previous AND SourceDigest = $source AND RecipeDigest = $recipe AND Revision = $revision AND DispositionCode = $disposition;
                    """, ("$next", LongRestDiscoveryHash.Work(work).Value.Bytes), ("$unit", adoption.Unit),
                    ("$operation", generation.OperationId.ToString("N")), ("$generation", Format(generation.GenerationId)), ("$kind", adoption.Kind),
                    ("$item", adoption.Item), ("$previous", adoption.Digest.Bytes), ("$source", adoption.Source.Bytes),
                    ("$recipe", adoption.Recipe.Bytes), ("$revision", adoption.Revision), ("$disposition", adoption.Disposition));

                if (await bind.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) throw new InvalidDataException();
            }

            cursor = units[^1].Unit;
        }
    }

    private static async Task<List<CleanWorkRow>> ReadCleanWorkPageAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, string? cursor, CancellationToken ct)
    {
        List<CleanWorkRow> units = [];

        await using SqliteCommand command = Command(connection, transaction, """
            SELECT WorkItemId, WorkDigest, KindCode, ItemId, SourceDigest, RecipeDigest, Revision, DispositionCode FROM long_rest_work_items
            WHERE OperationId = $operation AND GenerationId = $generation AND (KindCode = 2 OR (KindCode = 3 AND DispositionCode = 1))
              AND ($cursor IS NULL OR WorkItemId > $cursor) ORDER BY WorkItemId LIMIT 128;
            """, ("$operation", generation.OperationId.ToString("N")), ("$generation", Format(generation.GenerationId)), ("$cursor", cursor));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            units.Add(new(reader.GetString(0), new((byte[])reader[1]), reader.GetInt32(2), reader.GetString(3), new((byte[])reader[4]),
                new((byte[])reader[5]), reader.GetInt64(6), reader.GetInt32(7)));
        }

        return units;
    }

    private sealed record CleanWorkRow(string Unit, CovenantDigest Digest, int Kind, string Item, CovenantDigest Source,
        CovenantDigest Recipe, long Revision, int Disposition);
}
