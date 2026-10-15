using System.Text.Json;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

internal interface ILongRestCompletedCheckpointReconciliation
{
    Task<Result<LongRestSweep>> ReconcileCompletedCheckpointAsync(LongRestDiscoveryGeneration expected, LongRestSweepFence current,
        LongRestRecipeIdentity recipe, string workerOwner, LongRestLiveFenceReader facts,
        IGrimoireWorkLease admission, CancellationToken ct);
}

internal sealed partial class LongRestDiscoveryStore : ILongRestCompletedCheckpointReconciliation
{
    public async Task<Result<LongRestSweep>> ReconcileCompletedCheckpointAsync(LongRestDiscoveryGeneration expected, LongRestSweepFence current,
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

                    LongRestSweep sweep = await LongRestSweepStore.ReadAsync(connection, transaction, expected.OperationId, ct).ConfigureAwait(false)
                        ?? throw new InvalidDataException();

                    if (generation != expected || !generation.Published || generation.Phase != LongRestSweepPhase.Completed
                        || generation.Scope.ProtectedPartitionDigest is not null || generation.Recipe != recipe
                        || generation.Fence with { AdmissionGeneration = current.AdmissionGeneration } != current
                        || sweep.OperationId != generation.OperationId || sweep.GenerationId != generation.GenerationId
                        || sweep.Scope != generation.Scope || sweep.Phase != LongRestSweepPhase.Completed)
                    {
                        throw new InvalidDataException();
                    }

                    // The interval the lease must attest starts at the admission this generation was captured under,
                    // read once from the reloaded row. A completed checkpoint is never adopted, so it cannot move.
                    long previousAdmission = generation.Fence.AdmissionGeneration;

                    await using (SqliteCommand ownership = Command(connection, transaction, """
                        SELECT EXISTS(SELECT 1 FROM long_rest_sweeps WHERE OperationId = $operation AND GenerationId = $generation
                            AND ScopeKindCode = $scope AND CampaignId IS $campaign AND PartitionDigest IS NULL AND PhaseCode = 6
                            AND ConfigurationJson = $configuration)
                          AND NOT EXISTS(SELECT 1 FROM long_rest_work_items WHERE
                            (GenerationId = $generation AND OperationId <> $operation) OR (OperationId = $operation AND GenerationId <> $generation))
                          AND NOT EXISTS(SELECT 1 FROM long_rest_work_items WHERE GenerationId = $generation AND DispositionCode <> 2)
                          AND NOT EXISTS(SELECT 1 FROM long_rest_provider_effects effect JOIN long_rest_work_items unit ON unit.WorkItemId = effect.WorkItemId
                            WHERE unit.GenerationId = $generation AND effect.StateCode IN (1, 2, 3));
                        """, ("$operation", generation.OperationId.ToString("N")), ("$generation", Format(generation.GenerationId)),
                        ("$scope", (int)generation.Scope.Kind), ("$campaign", generation.Scope.CampaignId is { } campaign ? Format(campaign) : null),
                        ("$configuration", JsonSerializer.Serialize(generation.Fence.Configuration, LongRestJsonContext.Default.LongRestConfigurationStamp))))
                    {
                        if ((long)(await ownership.ExecuteScalarAsync(ct).ConfigureAwait(false))! != 1) throw new InvalidDataException();
                    }

                    await using (SqliteCommand coordinates = Command(connection, transaction, """
                        SELECT (($cursor IS NULL AND $digest IS NULL AND NOT EXISTS(SELECT 1 FROM long_rest_work_items
                            WHERE OperationId = $operation AND GenerationId = $generation AND KindCode = 3))
                          OR EXISTS(SELECT 1 FROM long_rest_work_items unit JOIN long_rest_work_inputs input ON input.WorkItemId = unit.WorkItemId AND input.Ordinal = 1
                            WHERE unit.OperationId = $operation AND unit.GenerationId = $generation AND unit.KindCode = 3 AND unit.DispositionCode = 2
                              AND unit.WorkDigest = $digest AND input.MemoryId = $cursor));
                        """, ("$operation", generation.OperationId.ToString("N")), ("$generation", Format(generation.GenerationId)),
                        ("$cursor", sweep.CommittedItemCursor), ("$digest", sweep.CommittedWorkDigest?.Bytes)))
                    {
                        if ((long)(await coordinates.ExecuteScalarAsync(ct).ConfigureAwait(false))! != 1) throw new InvalidDataException();
                    }

                    await ValidateCompletedCheckpointProofAsync(connection, transaction, generation, current, workerOwner, facts, admission, previousAdmission, ct).ConfigureAwait(false);

                    long revision;

                    byte[] previousCheckpoint;

                    await using (SqliteCommand ledger = Command(connection, transaction, """
                        SELECT Revision, CheckpointPayload FROM LongRunningOperations
                        WHERE Id = $operation AND Kind = 'long-rest-sweep' AND State = 1 AND LeaseOwner = $owner AND CheckpointVersion = 1;
                        """, ("$operation", generation.OperationId.ToString("N")), ("$owner", workerOwner)))
                    {
                        await using SqliteDataReader reader = await ledger.ExecuteReaderAsync(ct).ConfigureAwait(false);

                        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.GetInt64(0) < 0 || reader.IsDBNull(1)) throw new InvalidDataException();

                        revision = reader.GetInt64(0);

                        previousCheckpoint = (byte[])reader[1];
                    }

                    LongRestSweepCheckpoint checkpoint = new(1, sweep.OperationId, sweep.Scope.Kind, sweep.Scope.CampaignId,
                        sweep.GenerationId, sweep.Phase, sweep.Revision, sweep.CommittedItemCursor, sweep.CommittedWorkDigest, null);

                    byte[] payload = LongRestSweepCheckpointCodec.Encode(checkpoint).Value;

                    await ValidateCompletedCheckpointProofAsync(connection, transaction, generation, current, workerOwner, facts, admission, previousAdmission, ct).ConfigureAwait(false);

                    DateTimeOffset now = timeProvider.GetUtcNow();

                    await using SqliteCommand save = Command(connection, transaction, """
                        UPDATE LongRunningOperations SET CheckpointVersion = 1, CheckpointPayload = $payload, CheckpointReference = NULL,
                            PublicSummary = 'Long Rest sweep progress committed.', HeartbeatAt = $now, Revision = Revision + 1
                        WHERE Id = $operation AND Kind = 'long-rest-sweep' AND State = 1 AND LeaseOwner = $owner
                          AND LeaseExpiresAt > $now AND CheckpointVersion = 1 AND CheckpointPayload = $previous AND Revision = $revision;
                        """, ("$payload", payload), ("$now", UtcInstantText.Format(now)), ("$operation", generation.OperationId.ToString("N")),
                        ("$owner", workerOwner), ("$previous", previousCheckpoint), ("$revision", revision));

                    if (await save.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) throw new InvalidDataException();

                    await ValidateCompletedCheckpointProofAsync(connection, transaction, generation, current, workerOwner, facts, admission, previousAdmission, ct).ConfigureAwait(false);

                    await transaction.CommitAsync(ct).ConfigureAwait(false);

                    // The original sweep is historical completion evidence, never an adopted
                    // executable generation. Only the registered reconciler may settle the ledger.
                    return Result<LongRestSweep>.Success(sweep);
                }
                catch (Exception busy) when (SqliteBusyRetry.IsBusyOrLocked(busy))
                {
                    await retry.WaitAsync(busy, ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (StorageFailure(exception) || exception is InvalidOperationException)
        {
            return Refuse<LongRestSweep>(ErrorCodes.LongRest.StaleInput, "Completed maintenance recovery could not prove its exact historical corpus, live worker and drained admission.");
        }
    }

    private async Task ValidateCompletedCheckpointProofAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, LongRestSweepFence current, string workerOwner, LongRestLiveFenceReader facts,
        IGrimoireWorkLease admission, long previousAdmission, CancellationToken ct)
    {
        await ValidateCleanOwnerAsync(connection, transaction, generation, current, workerOwner, facts, admission, previousAdmission, ct).ConfigureAwait(false);

        await ValidateCompletedCorpusMetadataAsync(connection, transaction, generation, generation.Scope, ct).ConfigureAwait(false);

        await ProveCleanWorkAsync(connection, transaction, generation, ct).ConfigureAwait(false);

        await ValidateCompletedAnchorEffectsAsync(connection, transaction, generation, ct).ConfigureAwait(false);

        await ValidateCompletedReceiptLinksAsync(connection, transaction, generation, ct).ConfigureAwait(false);

        await ValidateCleanOwnerAsync(connection, transaction, generation, current, workerOwner, facts, admission, previousAdmission, ct).ConfigureAwait(false);
    }

    private static async Task ValidateCompletedReceiptLinksAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, CancellationToken ct)
    {
        string? cursor = null;

        while (true)
        {
            List<(string Unit, string Receipt)> page = [];

            await using (SqliteCommand command = Command(connection, transaction, """
                SELECT WorkItemId, ReceiptId FROM long_rest_work_items
                WHERE OperationId = $operation AND GenerationId = $generation AND KindCode = 3 AND DispositionCode = 2 AND ReceiptId IS NOT NULL
                  AND ($cursor IS NULL OR WorkItemId > $cursor) ORDER BY WorkItemId LIMIT 128;
                """, ("$operation", generation.OperationId.ToString("N")), ("$generation", Format(generation.GenerationId)), ("$cursor", cursor)))
            {
                await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

                while (await reader.ReadAsync(ct).ConfigureAwait(false)) page.Add((reader.GetString(0), reader.GetString(1)));
            }

            if (page.Count == 0) return;

            foreach (var row in page) await VerifyCompletedReceiptAsync(connection, transaction, generation, row.Receipt, ct, expectedUnit: row.Unit).ConfigureAwait(false);

            cursor = page[^1].Unit;
        }
    }

    private static async Task ValidateCompletedAnchorEffectsAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, CancellationToken ct)
    {
        string? cursor = null;

        while (true)
        {
            List<(string Unit, LongRestWorkIdentity Original)> page = [];

            await using (SqliteCommand command = Command(connection, transaction, """
                SELECT unit.WorkItemId, unit.ItemId, unit.SourceDigest, unit.RecipeDigest, unit.WorkDigest, effect.WorkDigest, effect.OriginalWorkJson, unit.PairingFloorPermille
                FROM long_rest_work_items unit JOIN long_rest_provider_effects effect ON effect.WorkItemId = unit.WorkItemId
                WHERE unit.OperationId = $operation AND unit.GenerationId = $generation AND unit.KindCode = 3 AND unit.DispositionCode = 2
                  AND ($cursor IS NULL OR unit.WorkItemId > $cursor) ORDER BY unit.WorkItemId LIMIT 128;
                """, ("$operation", generation.OperationId.ToString("N")), ("$generation", Format(generation.GenerationId)), ("$cursor", cursor)))
            {
                await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    LongRestWorkIdentity original = LongRestAttemptStore.ReadOriginalWork(reader.GetString(6), new((byte[])reader[5]));

                    LongRestWorkIdentity current = new(generation.GenerationId, generation.OperationId, reader.GetString(1), LongRestWorkKind.Anchor,
                        generation.Scope, generation.Fence, new((byte[])reader[2]), new((byte[])reader[3]), reader.GetInt32(7));

                    CovenantDigest unitDigest = new((byte[])reader[4]);

                    bool associated = CanAssociateSweepWork(original, current)
                        || (original with { Fence = current.Fence } == current
                            && original.Fence.AdmissionGeneration < current.Fence.AdmissionGeneration
                            && original.Fence with { AdmissionGeneration = current.Fence.AdmissionGeneration } == current.Fence);

                    if (!associated || original.RecipeDigest != LongRestDiscoveryHash.Recipe(generation.Recipe).Value
                        || (unitDigest != LongRestDiscoveryHash.Work(original).Value && unitDigest != LongRestDiscoveryHash.Work(current).Value))
                    {
                        // A different finished-unit fence without exact retained evidence is not
                        // reconstructed by guessing an admission from the historical interval.
                        throw new InvalidDataException();
                    }

                    page.Add((reader.GetString(0), original));
                }
            }

            if (page.Count == 0) return;

            foreach (var row in page)
            {
                LongRestAdjudication? accepted = await LongRestSweepStore.ReadAcceptedAsync(connection, transaction, row.Unit,
                    row.Original, generation.Recipe, ct).ConfigureAwait(false);

                IReadOnlyList<LongRestPaidEvidence> retained = accepted?.PaidEvidence is { } paid ? [paid] : [];

                _ = await LongRestAttemptStore.ValidateRetainedEffectAsync(connection, transaction, row.Unit, row.Original, retained, ct).ConfigureAwait(false);
            }

            cursor = page[^1].Unit;
        }
    }
}
