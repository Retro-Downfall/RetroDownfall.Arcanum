using System.Collections.Immutable;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

internal sealed partial class LongRestSweepStore
{
    public Task<Result<LongRestSweep>> CommitExactDuplicatesAsync(LongRestSweep expected, LongRestWorkIdentity work,
        LongRestMaintenanceCapability? capability, CancellationToken ct) => WithinAsync(async (connection, transaction) =>
    {
        LongRestDiscoveryGeneration generation = await LongRestDiscoveryStore.ReadSweepGenerationAsync(connection, transaction, expected.OperationId, ct).ConfigureAwait(false) ?? throw new InvalidDataException();

        if (work.Kind != LongRestWorkKind.Anchor || work.OperationId != expected.OperationId || work.GenerationId != expected.GenerationId
            || work.Scope != expected.Scope || work.Fence != generation.Fence || work.RecipeDigest != LongRestDiscoveryHash.Recipe(generation.Recipe).Value
            || generation.GenerationId != work.GenerationId || generation.Scope != work.Scope)
        {
            throw new InvalidDataException();
        }

        await ValidateClaimAsync(connection, transaction, generation, capability, ct).ConfigureAwait(false);

        await LongRestDiscoveryStore.ValidateSweepGenerationAsync(connection, transaction, generation, capability, LongRestVerificationDepth.Step, ct).ConfigureAwait(false);

        string unit;

        int disposition;

        string? receipt;

        await using (SqliteCommand command = Command(connection, transaction, "SELECT WorkItemId, DispositionCode, ReceiptId FROM long_rest_work_items WHERE OperationId = $operation AND GenerationId = $generation AND KindCode = 3 AND ItemId = $item AND WorkDigest = $work AND SourceDigest = $source AND RecipeDigest = $recipe;",
            ("$operation", work.OperationId.ToString("N")), ("$generation", Format(work.GenerationId)), ("$item", work.ItemId), ("$work", LongRestDiscoveryHash.Work(work).Value.Bytes),
            ("$source", work.SourceDigest.Bytes), ("$recipe", work.RecipeDigest.Bytes)))
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

        if (disposition == 2)
        {
            await VerifyExactReplayAsync(connection, transaction, work, unit, receipt, ct).ConfigureAwait(false);

            return await ReadAsync(connection, transaction, expected.OperationId, ct).ConfigureAwait(false) ?? throw new InvalidDataException();
        }

        _ = await RequireGenerationAsync(connection, transaction, expected, capability, LongRestVerificationDepth.Step, ct).ConfigureAwait(false);

        LongRestSweepWork current = await ResolveAsync(connection, transaction, generation, expected.Revision, work.ItemId, capability, ct).ConfigureAwait(false);

        if (current.Identity != work || current.AcceptedAdjudication is not null || current.Inputs.Length < 2
            || current.Inputs.Any(leaf => leaf.Snapshot.ContentHashFormat != current.Inputs[0].Snapshot.ContentHashFormat
                || leaf.Snapshot.ContentHash != current.Inputs[0].Snapshot.ContentHash))
        {
            throw new InvalidDataException();
        }

        LongRestProtectedEvidence? protection = work.Scope.ProtectedPartitionDigest is { } partition
            ? new(partition, current.Inputs[0].Provenance, [.. current.Inputs.SelectMany(leaf => leaf.LabelDigests)]) : null;

        Result<LongRestReceipt> applied = await new SagaLongRestService(db, timeProvider).ApplyInTransactionAsync(connection, transaction,
            new(LongRestTransformationKind.ExactDuplicates, current.Inputs.Select(leaf => leaf.Target).ToArray()), protection,
            protection is null ? null : new(current.Identity, current.Inputs), ct).ConfigureAwait(false);

        if (applied.IsFailure)
        {
            throw new InvalidDataException();
        }

        await ValidateClaimAsync(connection, transaction, generation, capability, ct).ConfigureAwait(false);

        if (capability is not null && (await capability.ValidateWorkAsync(work, generation.Fence.Configuration, ct).ConfigureAwait(false)).IsFailure)
        {
            throw new InvalidDataException();
        }

        return await FinishItemAsync(connection, transaction, expected, current, applied.Value.ReceiptId, ct).ConfigureAwait(false);
    }, ct);

    private static async Task VerifyExactReplayAsync(SqliteConnection connection, SqliteTransaction transaction, LongRestWorkIdentity work,
        string unit, string? receipt, CancellationToken ct)
    {
        if (receipt is null || (await SagaLongRestService.ReadSweepReceiptAsync(connection, transaction, receipt, ct).ConfigureAwait(false)) is not { IsSuccess: true } verified
            || verified.Value.Kind != LongRestTransformationKind.ExactDuplicates)
        {
            throw new InvalidDataException();
        }

        ImmutableArray<LongRestSourceLeaf>.Builder inputs = ImmutableArray.CreateBuilder<LongRestSourceLeaf>();

        await using SqliteCommand command = Command(connection, transaction, "SELECT Ordinal, MemoryId, VersionId, json_remove(SourceEvidenceJson, '$.content', '$.vector') FROM long_rest_work_inputs WHERE WorkItemId = $unit ORDER BY Ordinal LIMIT 17;", ("$unit", unit));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            LongRestSourceLeaf input = JsonSerializer.Deserialize(reader.GetString(3), LongRestJsonContext.Default.LongRestSourceLeaf) ?? throw new InvalidDataException();

            if (inputs.Count >= LongRestPolicy.MaxTargets || reader.GetInt32(0) != inputs.Count + 1
                || input.Snapshot.MemoryId != reader.GetString(1) || input.Snapshot.VersionId != reader.GetString(2))
            {
                throw new InvalidDataException();
            }

            inputs.Add(input);
        }

        if (inputs.Count < 2 || LongRestDiscoveryHash.SourcePage(work.Scope, inputs.ToImmutable()).Value != work.SourceDigest)
        {
            throw new InvalidDataException();
        }

        LongRestRequest request = new(LongRestTransformationKind.ExactDuplicates, inputs.Select(leaf => leaf.Target).ToArray());

        Result<string> identity = work.Scope.ProtectedPartitionDigest is { } partition
            ? ProtectedLongRestPolicy.Identify(request, inputs, new(partition, inputs[0].Provenance, [.. inputs.SelectMany(leaf => leaf.LabelDigests)]))
            : LongRestPolicy.Identify(request, inputs.Select(leaf => leaf.Snapshot).ToArray());

        if (identity.IsFailure || identity.Value != receipt)
        {
            throw new InvalidDataException();
        }
    }
}
