using System.Collections.Immutable;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

internal sealed partial class LongRestDiscoveryStore
{
    /// <param name="proved">
    /// The receipts already proved in full by the pass this leaf is part of. A decision that consolidated sixteen leaves left one receipt, and
    /// the proof of a receipt is a function of the receipt and the generation alone, so a pass that meets it again for the next leaf of the
    /// same decision need not prove it again. Only a receipt that proved is remembered, and a receipt that does not prove ends the pass.
    /// </param>
    private static async Task VerifyOwnProjectionAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, LongRestSourceLeaf live, LongRestSnapshot frozen, CancellationToken ct, HashSet<string>? proved = null)
    {
        if ((frozen.IsConsolidated && !live.Snapshot.IsConsolidated) || (frozen.IsTransformationOutput && !live.Snapshot.IsTransformationOutput))
        {
            throw new InvalidDataException();
        }

        string? cursor = null;

        bool explainedConsolidation = frozen.IsConsolidated;

        bool explainedOutput = frozen.IsTransformationOutput;

        while (true)
        {
            List<(string Receipt, bool Source, bool Survivor)> page = [];

            await using (SqliteCommand command = Command(connection, transaction, """
                SELECT receipt.ReceiptId,
                    EXISTS(SELECT 1 FROM long_rest_suppressions suppression WHERE suppression.ReceiptId = receipt.ReceiptId AND suppression.SourceVersionId = $version),
                    receipt.SurvivorVersionId = $version
                FROM long_rest_receipts receipt WHERE receipt.OutcomeCode = 1 AND ($cursor IS NULL OR receipt.ReceiptId > $cursor)
                  AND (receipt.SurvivorVersionId = $version OR EXISTS(SELECT 1 FROM long_rest_suppressions suppression
                    WHERE suppression.ReceiptId = receipt.ReceiptId AND suppression.SourceVersionId = $version))
                ORDER BY receipt.ReceiptId LIMIT 128;
                """, ("$version", live.Snapshot.VersionId), ("$cursor", cursor)))
            {
                await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    page.Add((reader.GetString(0), reader.GetInt32(1) == 1, reader.GetInt32(2) == 1));
                }
            }

            if (page.Count == 0)
            {
                break;
            }

            foreach (var cause in page)
            {
                // Every present causer of a newly set flag must be attributable. One own receipt
                // cannot conceal an external receipt that also sets the same projection bit.
                if (((cause.Source && !frozen.IsConsolidated) || (cause.Survivor && !frozen.IsTransformationOutput))
                    && (proved is null || !proved.Contains(cause.Receipt)))
                {
                    await VerifyOwnReceiptAsync(connection, transaction, generation, cause.Receipt, ct).ConfigureAwait(false);

                    _ = proved?.Add(cause.Receipt);
                }

                explainedConsolidation |= cause.Source;

                explainedOutput |= cause.Survivor;
            }

            cursor = page[^1].Receipt;
        }

        if (live.Snapshot.IsConsolidated != explainedConsolidation || live.Snapshot.IsTransformationOutput != explainedOutput)
        {
            throw new InvalidDataException();
        }
    }

    private static Task VerifyOwnReceiptAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, string receiptId, CancellationToken ct) =>
        VerifyCompletedReceiptAsync(connection, transaction, generation, receiptId, ct, appliedOnly: true);

    private static async Task VerifyCompletedReceiptAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, string receiptId, CancellationToken ct, bool appliedOnly = false, string? expectedUnit = null)
    {
        var verified = await SagaLongRestService.ReadSweepReceiptAsync(connection, transaction, receiptId, ct).ConfigureAwait(false);

        if (verified is not { IsSuccess: true } || verified.Value.Outcome is not (LongRestOutcome.Applied or LongRestOutcome.NoChange)
            || (appliedOnly && verified.Value.Outcome != LongRestOutcome.Applied))
        {
            throw new InvalidDataException();
        }

        string unit;

        CovenantDigest source;

        CovenantDigest work;

        await using (SqliteCommand command = Command(connection, transaction, """
            SELECT unit.WorkItemId, unit.SourceDigest, unit.WorkDigest FROM long_rest_work_items unit
            JOIN long_rest_sweeps sweep ON sweep.OperationId = unit.OperationId AND sweep.GenerationId = unit.GenerationId
            WHERE unit.OperationId = $operation AND unit.GenerationId = $generation AND unit.KindCode = 3
              AND unit.DispositionCode = 2 AND unit.ReceiptId = $receipt AND unit.RecipeDigest = $recipe
              AND ($unit IS NULL OR unit.WorkItemId = $unit) LIMIT 1;
            """, ("$operation", generation.OperationId.ToString("N")), ("$generation", Format(generation.GenerationId)),
            ("$receipt", receiptId), ("$recipe", LongRestDiscoveryHash.Recipe(generation.Recipe).Value.Bytes), ("$unit", expectedUnit)))
        {
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new InvalidDataException();
            }

            unit = reader.GetString(0);

            source = new((byte[])reader[1]);

            work = new((byte[])reader[2]);
        }

        ImmutableArray<LongRestSourceLeaf>.Builder inputs = ImmutableArray.CreateBuilder<LongRestSourceLeaf>();

        await using (SqliteCommand command = Command(connection, transaction, """
            SELECT input.Ordinal, input.MemoryId, input.VersionId, json_remove(input.SourceEvidenceJson, '$.content', '$.vector'),
                canonical.SnapshotJson, frozen.SnapshotJson, frozen.SourceDigest
            FROM long_rest_work_inputs input
            JOIN long_rest_receipt_inputs canonical ON canonical.ReceiptId = $receipt AND canonical.MemoryId = input.MemoryId AND canonical.VersionId = input.VersionId
            JOIN long_rest_discovery_leaves frozen ON frozen.GenerationId = $generation AND frozen.MemoryId = input.MemoryId AND frozen.VersionId = input.VersionId
            WHERE input.WorkItemId = $unit ORDER BY input.Ordinal LIMIT 17;
            """, ("$receipt", receiptId), ("$generation", Format(generation.GenerationId)), ("$unit", unit)))
        {
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                LongRestSourceLeaf input = JsonSerializer.Deserialize(reader.GetString(3), LongRestJsonContext.Default.LongRestSourceLeaf) ?? throw new InvalidDataException();

                LongRestSnapshot canonical = JsonSerializer.Deserialize(reader.GetString(4), LongRestJsonContext.Default.LongRestSnapshot) ?? throw new InvalidDataException();

                LongRestSnapshot frozen = JsonSerializer.Deserialize(reader.GetString(5), LongRestJsonContext.Default.LongRestSnapshot) ?? throw new InvalidDataException();

                LongRestSourceLeaf comparable = input with { Snapshot = input.Snapshot with { IsConsolidated = frozen.IsConsolidated, IsTransformationOutput = frozen.IsTransformationOutput } };

                if (inputs.Count >= LongRestPolicy.MaxTargets || reader.GetInt32(0) != inputs.Count + 1
                    || input.Snapshot.MemoryId != reader.GetString(1) || input.Snapshot.VersionId != reader.GetString(2)
                    || JsonSerializer.Serialize(input.Snapshot, LongRestJsonContext.Default.LongRestSnapshot) != JsonSerializer.Serialize(canonical, LongRestJsonContext.Default.LongRestSnapshot)
                    || (frozen.IsConsolidated && !input.Snapshot.IsConsolidated) || (frozen.IsTransformationOutput && !input.Snapshot.IsTransformationOutput)
                    || LongRestDiscoveryHash.SourcePage(generation.Scope, [comparable]).Value != new CovenantDigest((byte[])reader[6]))
                {
                    throw new InvalidDataException();
                }

                inputs.Add(input);
            }
        }

        if (inputs.Count != verified.Value.Targets.Length || LongRestDiscoveryHash.SourcePage(generation.Scope, inputs.ToImmutable()).Value != source)
        {
            throw new InvalidDataException();
        }

        if (generation.Scope.ProtectedPartitionDigest is { } partition)
        {
            await using SqliteCommand command = Command(connection, transaction,
                "SELECT count(*) FROM long_rest_protected_receipt_evidence WHERE ReceiptId = $receipt AND WorkDigest = $work AND PartitionDigest = $partition;",
                ("$receipt", receiptId), ("$work", work.Bytes), ("$partition", partition.Bytes));

            if ((long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))! != 1)
            {
                throw new InvalidDataException();
            }

            LongRestProtectedEvidence protection = new(partition, inputs[0].Provenance, [.. inputs.SelectMany(leaf => leaf.LabelDigests)]);

            var reproduced = ProtectedLongRestPolicy.Evaluate(new(verified.Value.Kind, inputs.Select(input => input.Target).ToArray(),
                verified.Value.Kind == LongRestTransformationKind.Supersession ? verified.Value.SurvivorVersionId : null), inputs, protection);

            if (reproduced.IsFailure || reproduced.Value.ReceiptId != receiptId)
            {
                throw new InvalidDataException();
            }
        }
    }
}
