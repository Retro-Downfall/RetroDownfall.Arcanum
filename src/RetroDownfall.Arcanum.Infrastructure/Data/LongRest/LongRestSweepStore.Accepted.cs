using System.Text;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

internal sealed partial class LongRestSweepStore
{
    public Task<Result<LongRestAdjudication>> CommitAcceptedAdjudicationAsync(LongRestSweep expected, LongRestAdjudication accepted,
        LongRestMaintenanceCapability? capability, CancellationToken ct) => CommitAcceptedAdjudicationCoreAsync(expected, accepted, null, null, capability, ct, false);

    public Task<Result<LongRestAdjudication>> CommitAcceptedAdjudicationAsync(LongRestSweep expected, LongRestAdjudication accepted,
        LongRestDispatchCompletion completion, string workerOwner, LongRestMaintenanceCapability? capability, CancellationToken ct, bool committedOnly = false) =>
        CommitAcceptedAdjudicationCoreAsync(expected, accepted, completion, workerOwner, capability, ct, committedOnly);

    private Task<Result<LongRestAdjudication>> CommitAcceptedAdjudicationCoreAsync(LongRestSweep expected, LongRestAdjudication accepted,
        LongRestDispatchCompletion? completion, string? workerOwner, LongRestMaintenanceCapability? capability, CancellationToken ct, bool committedOnly) => WithinAsync(async (connection, transaction) =>
    {
        LongRestDiscoveryGeneration generation = await RequireGenerationAsync(connection, transaction, expected, capability, ct).ConfigureAwait(false);

        LongRestSweepWork current = await ResolveAsync(connection, transaction, generation, expected.Revision, accepted.Work.ItemId, capability, ct).ConfigureAwait(false);

        ValidateAccepted(accepted, current.Identity, current.Inputs, generation.Recipe);

        if (current.AcceptedAdjudication is { } existing)
        {
            if (AcceptedJson(existing) != AcceptedJson(accepted))
            {
                throw new InvalidDataException();
            }

            if (completion is not null)
            {
                await LongRestAttemptStore.AcceptWithinAsync(connection, transaction, accepted.PaidEvidence ?? throw new InvalidDataException(),
                    completion, timeProvider.GetUtcNow(), ct, committedOnly: true).ConfigureAwait(false);
            }

            return existing;
        }

        if (committedOnly) throw new InvalidDataException();

        if (LongRestDiscoveryHash.Work(accepted.Work).Value != LongRestDiscoveryHash.Work(current.Identity).Value)
        {
            throw new InvalidDataException();
        }

        if (completion is not null && (await LongRestLiveFenceReader.ReadWorkerWithinAsync(connection, transaction,
            generation, workerOwner ?? string.Empty, timeProvider.GetUtcNow(), ct).ConfigureAwait(false)).IsFailure)
        {
            throw new InvalidDataException();
        }

        if (completion is not null)
        {
            await LongRestAttemptStore.AcceptWithinAsync(connection, transaction, accepted.PaidEvidence ?? throw new InvalidDataException(), completion, timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
        }

        await LongRestDiscoveryStore.VerifyAdjudicationPaidAsync(connection, transaction, accepted, generation.Recipe, ct).ConfigureAwait(false);

        string json = AcceptedJson(accepted);

        if (new UTF8Encoding(false, true).GetByteCount(json) > 1048576)
        {
            throw new InvalidDataException();
        }

        await using SqliteCommand insert = Command(connection, transaction, """
            INSERT INTO long_rest_adjudications (WorkItemId, WorkDigest, DecisionKindCode, SurvivorVersionId, PayloadDigest, EvidenceJson, CreatedAtUtc)
            SELECT WorkItemId, WorkDigest, $kind, $survivor, $payload, $json, $at FROM long_rest_work_items WHERE WorkDigest = $work AND DispositionCode = 1;
            """, ("$kind", (int)accepted.Decision.Kind), ("$survivor", accepted.Decision.SurvivorVersionId), ("$payload", accepted.PayloadDigest.Bytes),
            ("$json", json), ("$at", UtcInstantText.Format(timeProvider.GetUtcNow())), ("$work", LongRestDiscoveryHash.Work(current.Identity).Value.Bytes));

        if (await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
        {
            throw new InvalidDataException();
        }

        if (completion is not null && (await LongRestLiveFenceReader.ReadWorkerWithinAsync(connection, transaction,
            generation, workerOwner ?? string.Empty, timeProvider.GetUtcNow(), ct).ConfigureAwait(false)).IsFailure)
        {
            throw new InvalidDataException();
        }

        return accepted;
    }, ct);

    public Task<Result<LongRestSweep>> CommitDecisionAsync(LongRestSweep expected, LongRestAdjudication decision,
        LongRestMaintenanceCapability? capability, CancellationToken ct) => WithinAsync(async (connection, transaction) =>
    {
        LongRestSweep persisted = await ReadAsync(connection, transaction, expected.OperationId, ct).ConfigureAwait(false) ?? throw new InvalidDataException();

        LongRestDiscoveryGeneration generation = await LongRestDiscoveryStore.ReadSweepGenerationAsync(connection, transaction, expected.OperationId, ct).ConfigureAwait(false) ?? throw new InvalidDataException();

        if (generation.GenerationId != expected.GenerationId || generation.Scope != expected.Scope || decision.Work.OperationId != expected.OperationId
            || decision.Work.GenerationId != expected.GenerationId || decision.Work.Scope != expected.Scope)
        {
            throw new InvalidDataException();
        }

        string unit;

        int disposition;

        string? receipt;

        await using (SqliteCommand command = Command(connection, transaction, "SELECT WorkItemId, DispositionCode, ReceiptId FROM long_rest_work_items WHERE GenerationId = $generation AND OperationId = $operation AND ItemId = $item AND KindCode = 3;",
            ("$generation", Format(expected.GenerationId)), ("$operation", expected.OperationId.ToString("N")), ("$item", decision.Work.ItemId)))
        {
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new InvalidDataException();
            }

            unit = reader.GetString(0);

            disposition = reader.GetInt32(1);

            receipt = reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        LongRestWorkIdentity currentIdentity = decision.Work with { Fence = generation.Fence };

        await ValidateClaimAsync(connection, transaction, generation, capability, ct).ConfigureAwait(false);

        if (capability is not null && (await capability.ValidateWorkAsync(currentIdentity, generation.Fence.Configuration, ct).ConfigureAwait(false)).IsFailure)
        {
            throw new InvalidDataException();
        }

        await LongRestDiscoveryStore.ValidateSweepEvidenceMetadataAsync(connection, transaction, generation, ct).ConfigureAwait(false);

        LongRestAdjudication original = await ReadAcceptedAsync(connection, transaction, unit, currentIdentity, generation.Recipe, ct).ConfigureAwait(false) ?? throw new InvalidDataException();

        if (AcceptedJson(original) != AcceptedJson(decision))
        {
            throw new InvalidDataException();
        }

        if (disposition == 2)
        {
            if (receipt is not null && (await SagaLongRestService.ReadSweepReceiptAsync(connection, transaction, receipt, ct).ConfigureAwait(false)) is not { IsSuccess: true })
            {
                throw new InvalidDataException();
            }

            return persisted;
        }

        _ = await RequireGenerationAsync(connection, transaction, expected, capability, ct).ConfigureAwait(false);

        LongRestSweepWork current = await ResolveAsync(connection, transaction, generation, expected.Revision, decision.Work.ItemId, capability, ct).ConfigureAwait(false);

        if (current.AcceptedAdjudication is null || AcceptedJson(current.AcceptedAdjudication) != AcceptedJson(decision))
        {
            throw new InvalidDataException();
        }

        if (decision.Decision.Kind != LongRestAdjudicationKind.NoChange)
        {
            LongRestRequest request = new(decision.Decision.Kind == LongRestAdjudicationKind.EquivalentObservations
                ? LongRestTransformationKind.EquivalentObservations : LongRestTransformationKind.Supersession,
                current.Inputs.Select(leaf => leaf.Target).ToArray(), decision.Decision.SurvivorVersionId);

            Result<LongRestReceipt> applied = await new SagaLongRestService(db, timeProvider).ApplyInTransactionAsync(connection, transaction, request,
                decision.ProtectedEvidence, decision.ProtectedEvidence is null ? null : new(current.Identity, current.Inputs), ct).ConfigureAwait(false);

            if (applied.IsFailure)
            {
                throw new InvalidDataException();
            }

            receipt = applied.Value.ReceiptId;
        }

        await ValidateClaimAsync(connection, transaction, generation, capability, ct).ConfigureAwait(false);

        if (capability is not null && (await capability.ValidateWorkAsync(current.Identity, generation.Fence.Configuration, ct).ConfigureAwait(false)).IsFailure)
        {
            throw new InvalidDataException();
        }

        return await FinishItemAsync(connection, transaction, expected, current, receipt, ct).ConfigureAwait(false);
    }, ct);

    private async Task<LongRestSweep> FinishItemAsync(SqliteConnection connection, SqliteTransaction transaction, LongRestSweep expected,
        LongRestSweepWork current, string? receipt, CancellationToken ct)
    {
        await using SqliteCommand unitCommand = Command(connection, transaction, "SELECT WorkItemId FROM long_rest_work_items WHERE WorkDigest = $work AND DispositionCode = 1;", ("$work", LongRestDiscoveryHash.Work(current.Identity).Value.Bytes));

        string unit = await unitCommand.ExecuteScalarAsync(ct).ConfigureAwait(false) as string ?? throw new InvalidDataException();

        await using SqliteCommand finish = Command(connection, transaction, "UPDATE long_rest_work_items SET DispositionCode = 2, ReceiptId = $receipt, Revision = Revision + 1 WHERE WorkItemId = $unit AND WorkDigest = $work AND DispositionCode = 1;",
            ("$receipt", receipt), ("$unit", unit), ("$work", LongRestDiscoveryHash.Work(current.Identity).Value.Bytes));

        if (await finish.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
        {
            throw new InvalidDataException();
        }

        string prefix = current.Identity.ItemId[..current.Identity.ItemId.LastIndexOf(':')];

        await using SqliteCommand remaining = Command(connection, transaction, "SELECT EXISTS(SELECT 1 FROM long_rest_work_items WHERE GenerationId = $generation AND OperationId = $operation AND KindCode = 3 AND DispositionCode = 1 AND substr(ItemId, 1, length($prefix)) = $prefix);",
            ("$generation", Format(expected.GenerationId)), ("$operation", expected.OperationId.ToString("N")), ("$prefix", prefix));

        string? cursor = (long)(await remaining.ExecuteScalarAsync(ct).ConfigureAwait(false))! == 1
            ? expected.CommittedItemCursor : current.Inputs[0].Snapshot.MemoryId;

        await using SqliteCommand advance = Command(connection, transaction, """
            UPDATE long_rest_sweeps SET CommittedItemCursor = $cursor, CommittedWorkDigest = $work,
                DiscoveryCursor = CASE WHEN $cursor IS NULL THEN DiscoveryCursor
                    WHEN DiscoveryCursor IS NULL OR DiscoveryCursor < $cursor THEN $cursor ELSE DiscoveryCursor END,
                ReadmitCount = 0, Revision = Revision + 1, UpdatedAtUtc = $at
            WHERE OperationId = $operation AND GenerationId = $generation AND Revision = $revision;
            """, ("$cursor", cursor), ("$work", LongRestDiscoveryHash.Work(current.Identity).Value.Bytes),
            ("$at", UtcInstantText.Format(timeProvider.GetUtcNow())), ("$operation", expected.OperationId.ToString("N")),
            ("$generation", Format(expected.GenerationId)), ("$revision", expected.Revision));

        if (await advance.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
        {
            throw new InvalidDataException();
        }

        return (await ReadAsync(connection, transaction, expected.OperationId, ct).ConfigureAwait(false))!;
    }

    internal static async Task<LongRestAdjudication?> ReadAcceptedAsync(SqliteConnection connection, SqliteTransaction transaction, string unit,
        LongRestWorkIdentity current, LongRestRecipeIdentity recipe, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT accepted.WorkDigest, accepted.DecisionKindCode, accepted.SurvivorVersionId, accepted.PayloadDigest, accepted.EvidenceJson
            FROM long_rest_adjudications accepted JOIN long_rest_work_items unit ON unit.WorkItemId = accepted.WorkItemId
            WHERE accepted.WorkItemId = $unit AND unit.OperationId = $operation AND unit.GenerationId = $generation
              AND unit.KindCode = 3 AND unit.ItemId = $item AND unit.SourceDigest = $source AND unit.RecipeDigest = $recipe;
            """, ("$unit", unit), ("$operation", current.OperationId.ToString("N")), ("$generation", Format(current.GenerationId)),
            ("$item", current.ItemId), ("$source", current.SourceDigest.Bytes), ("$recipe", current.RecipeDigest.Bytes));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        LongRestAdjudication accepted = (JsonSerializer.Deserialize(reader.GetString(4), LongRestJsonContext.Default.LongRestStoredAdjudication) ?? throw new InvalidDataException()).ToDomain();

        if (LongRestDiscoveryHash.Work(accepted.Work).Value != new CovenantDigest((byte[])reader[0]) || (int)accepted.Decision.Kind != reader.GetInt32(1)
            || accepted.Decision.SurvivorVersionId != (reader.IsDBNull(2) ? null : reader.GetString(2)) || accepted.PayloadDigest != new CovenantDigest((byte[])reader[3]))
        {
            throw new InvalidDataException();
        }

        ValidateAccepted(accepted, accepted.Work, accepted.Inputs, recipe);

        await LongRestDiscoveryStore.ValidateStoredWorkAssociationAsync(connection, transaction, accepted.Work, current, ct).ConfigureAwait(false);

        return accepted;
    }

    private static void ValidateAccepted(LongRestAdjudication accepted, LongRestWorkIdentity current,
        System.Collections.Immutable.ImmutableArray<LongRestSourceLeaf> inputs, LongRestRecipeIdentity recipe)
    {
        if (!LongRestDiscoveryStore.CanAssociateSweepWork(accepted.Work, current) || accepted.Inputs.IsDefaultOrEmpty
            || accepted.Inputs.Length != inputs.Length || accepted.Work.Kind != LongRestWorkKind.Anchor
            || !accepted.Inputs.Select(AcceptedLeafJson).SequenceEqual(inputs.Select(AcceptedLeafJson))
            || LongRestDiscoveryHash.SourcePage(current.Scope, accepted.Inputs).Value != current.SourceDigest
            || !accepted.PayloadDigest.IsValid || accepted.Decision.Kind is < LongRestAdjudicationKind.EquivalentObservations or > LongRestAdjudicationKind.NoChange
            || (accepted.Decision.Kind != LongRestAdjudicationKind.Supersession && accepted.Decision.SurvivorVersionId is not null)
            || (accepted.Decision.Kind == LongRestAdjudicationKind.Supersession && !inputs.Any(leaf => leaf.Snapshot.VersionId == accepted.Decision.SurvivorVersionId)))
        {
            throw new InvalidDataException();
        }

        if (current.Scope.ProtectedPartitionDigest is null)
        {
            if (accepted.ProtectedEvidence is not null)
            {
                throw new InvalidDataException();
            }
        }
        else if (accepted.ProtectedEvidence is null || ProtectedLongRestPolicy.Identify(new(LongRestTransformationKind.EquivalentObservations,
            inputs.Select(leaf => leaf.Target).ToArray()), inputs, accepted.ProtectedEvidence).IsFailure)
        {
            throw new InvalidDataException();
        }

        LongRestDiscoveryStore.ValidateAdjudicationPaid(accepted, recipe);
    }

    private static string AcceptedLeafJson(LongRestSourceLeaf value) => JsonSerializer.Serialize(value, LongRestJsonContext.Default.LongRestSourceLeaf);

    private static string AcceptedJson(LongRestAdjudication value) => JsonSerializer.Serialize(LongRestStoredAdjudication.From(value), LongRestJsonContext.Default.LongRestStoredAdjudication);
}
