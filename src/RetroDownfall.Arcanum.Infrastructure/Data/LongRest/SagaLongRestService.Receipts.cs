using System.Data.Common;
using System.Text.Json;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

internal sealed partial class SagaLongRestService
{
    private async Task WriteReceiptAsync(DbConnection connection, DbTransaction transaction,
        LongRestRequest request, LongRestReceipt receipt, LongRestSnapshot[] snapshots, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO long_rest_receipts
                (ReceiptId, PolicyVersion, KindCode, OutcomeCode, ReasonCode, InputHash, OutputHash,
                 SurvivorMemoryId, SurvivorVersionId, RequestedSurvivorVersionId, CreatedAtUtc)
            VALUES (@id, @policy, @kind, @outcome, @reason, @input, @output, @memory, @version, @requested, @created)
            """, cancellationToken,
            ("@id", receipt.ReceiptId), ("@policy", receipt.PolicyVersion), ("@kind", (int)receipt.Kind),
            ("@outcome", (int)receipt.Outcome), ("@reason", (int)receipt.Reason), ("@input", receipt.InputHash),
            ("@output", receipt.OutputHash), ("@memory", receipt.SurvivorMemoryId), ("@version", receipt.SurvivorVersionId),
            ("@requested", request.SurvivorVersionId), ("@created", UtcInstantText.Format(timeProvider.GetUtcNow()))).ConfigureAwait(false);

        Dictionary<string, LongRestSnapshot> byVersion = snapshots.ToDictionary(snapshot => snapshot.VersionId, StringComparer.Ordinal);

        for (int ordinal = 0; ordinal < receipt.Targets.Length; ordinal++)
        {
            LongRestReceiptTarget target = receipt.Targets[ordinal];

            string json = JsonSerializer.Serialize(byVersion[target.VersionId], LongRestJsonContext.Default.LongRestSnapshot);

            await ExecuteAsync(connection, transaction,
                """
                INSERT INTO long_rest_receipt_inputs
                    (ReceiptId, Ordinal, MemoryId, VersionId, ContentHashFormatCode, ContentHash, SnapshotHash, SnapshotJson)
                VALUES (@receipt, @ordinal, @memory, @version, @format, @hash, @snapshot, @json)
                """, cancellationToken,
                ("@receipt", receipt.ReceiptId), ("@ordinal", ordinal + 1), ("@memory", target.MemoryId),
                ("@version", target.VersionId), ("@format", (int)target.ContentHashFormat),
                ("@hash", Convert.FromHexString(target.ContentHash)), ("@snapshot", receipt.InputHash), ("@json", json)).ConfigureAwait(false);
        }

        foreach (string source in receipt.SupersededVersionIds)
        {
            await ExecuteAsync(connection, transaction,
                "INSERT INTO long_rest_suppressions (SourceVersionId, SurvivorVersionId, ReceiptId) VALUES (@source, @survivor, @receipt)",
                cancellationToken, ("@source", source), ("@survivor", receipt.SurvivorVersionId), ("@receipt", receipt.ReceiptId)).ConfigureAwait(false);
        }
    }

    private static async Task<Result<LongRestReceipt>?> ReadHistoricalAppliedReceiptAsync(
        DbConnection connection, DbTransaction transaction, LongRestRequest request,
        LongRestSnapshot[] currentSnapshots, CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        Add(command, "@kind", (int)request.Kind);

        Add(command, "@survivor", request.SurvivorVersionId);

        Add(command, "@count", request.Targets.Length);

        List<string> matches = [];

        for (int index = 0; index < request.Targets.Length; index++)
        {
            LongRestTarget target = request.Targets[index];

            matches.Add($"(i.MemoryId = @memory{index} AND i.VersionId = @version{index} AND i.ContentHash = @hash{index})");

            Add(command, $"@memory{index}", target.MemoryId);

            Add(command, $"@version{index}", target.ExpectedVersionId);

            Add(command, $"@hash{index}", Convert.FromHexString(target.ExpectedContentHash));
        }

        // Only an applied transformation has one historical result for an exact group. No-change
        // groups can acquire new decisions after pin, label or dependency state changes.
        command.CommandText = $"""
            SELECT r.ReceiptId
            FROM long_rest_receipts r
            WHERE r.OutcomeCode = 1 AND r.KindCode = @kind
              AND r.RequestedSurvivorVersionId IS @survivor
              AND (SELECT COUNT(*) FROM long_rest_receipt_inputs i WHERE i.ReceiptId = r.ReceiptId) = @count
              AND (SELECT COUNT(*) FROM long_rest_receipt_inputs i WHERE i.ReceiptId = r.ReceiptId
                   AND ({string.Join(" OR ", matches)})) = @count
            ORDER BY r.ReceiptId LIMIT 2
            """;

        List<string> receipts = [];

        await using (DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                receipts.Add(reader.GetString(0));
            }
        }

        if (receipts.Count == 0)
        {
            return null;
        }

        if (receipts.Count != 1)
        {
            return InvalidReceipt();
        }

        Result<LongRestReceipt>? receipt = await ReadReceiptAsync(
            connection, transaction, receipts[0], cancellationToken).ConfigureAwait(false);

        if (receipt is null || receipt.IsFailure || currentSnapshots.Any(snapshot => !snapshot.IsCurrent))
        {
            return receipt;
        }

        command.Parameters.Clear();

        Add(command, "@id", receipts[0]);

        command.CommandText = "SELECT SnapshotJson FROM long_rest_receipt_inputs WHERE ReceiptId = @id ORDER BY Ordinal LIMIT 17";

        Dictionary<string, LongRestSnapshot> current = currentSnapshots.ToDictionary(snapshot => snapshot.VersionId, StringComparer.Ordinal);

        List<LongRestSnapshot> comparable = [];

        await using (DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                LongRestSnapshot frozen = JsonSerializer.Deserialize(
                    reader.GetString(0), LongRestJsonContext.Default.LongRestSnapshot)!;

                // Ignore only the projection created by this already applied decision. Mutable
                // pin, label, embedding and dependency changes remain bound and need a new decision.
                comparable.Add(current[frozen.VersionId] with
                {
                    IsConsolidated = frozen.IsConsolidated,

                    IsTransformationOutput = frozen.IsTransformationOutput,
                });
            }
        }

        Result<string> identity = LongRestPolicy.Identify(request, comparable);

        return identity.IsSuccess && identity.Value == receipt.Value.ReceiptId ? receipt : null;
    }

    private static async Task<Result<LongRestReceipt>?> ReadReceiptAsync(
        DbConnection connection, DbTransaction transaction, string receiptId, CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            """
            SELECT PolicyVersion, KindCode, OutcomeCode, ReasonCode, InputHash, OutputHash,
                   SurvivorMemoryId, SurvivorVersionId, RequestedSurvivorVersionId
            FROM long_rest_receipts WHERE ReceiptId = @id
            """;

        Add(command, "@id", receiptId);

        int policy;

        LongRestTransformationKind kind;

        LongRestOutcome outcome;

        LongRestReason reason;

        string inputHash;

        string outputHash;

        string? survivorMemory;

        string? survivorVersion;

        string? requestedSurvivor;

        await using (DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            policy = reader.GetInt32(0);

            kind = (LongRestTransformationKind)reader.GetInt32(1);

            outcome = (LongRestOutcome)reader.GetInt32(2);

            reason = (LongRestReason)reader.GetInt32(3);

            inputHash = reader.GetString(4);

            outputHash = reader.GetString(5);

            survivorMemory = reader.IsDBNull(6) ? null : reader.GetString(6);

            survivorVersion = reader.IsDBNull(7) ? null : reader.GetString(7);

            requestedSurvivor = reader.IsDBNull(8) ? null : reader.GetString(8);
        }

        command.CommandText =
            """
            SELECT i.Ordinal, i.MemoryId, i.VersionId, i.ContentHashFormatCode, i.ContentHash, i.SnapshotHash, i.SnapshotJson,
                   v.ClaimId, v.Revision
            FROM long_rest_receipt_inputs i JOIN annal_versions v ON v.VersionId = i.VersionId
            WHERE i.ReceiptId = @id ORDER BY i.Ordinal LIMIT 17
            """;

        List<LongRestSnapshot> snapshots = [];

        List<LongRestTarget> targets = [];

        await using (DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (snapshots.Count == LongRestPolicy.MaxTargets || reader.GetInt32(0) != snapshots.Count + 1)
                {
                    return InvalidReceipt();
                }

                LongRestSnapshot? snapshot;

                try
                {
                    snapshot = JsonSerializer.Deserialize(reader.GetString(6), LongRestJsonContext.Default.LongRestSnapshot);
                }
                catch (JsonException)
                {
                    return InvalidReceipt();
                }

                string hash = Convert.ToHexString((byte[])reader[4]);

                if (snapshot is null || snapshot.MemoryId != reader.GetString(1) || snapshot.VersionId != reader.GetString(2)
                    || (int)snapshot.ContentHashFormat != reader.GetInt32(3) || snapshot.ContentHash != hash
                    || reader.GetString(5) != inputHash || snapshot.ClaimId != reader.GetString(7)
                    || snapshot.Revision != reader.GetInt32(8))
                {
                    return InvalidReceipt();
                }

                snapshots.Add(snapshot);

                targets.Add(new LongRestTarget(snapshot.MemoryId, snapshot.VersionId, snapshot.ContentHash));
            }
        }

        Result<LongRestReceipt> replayed = LongRestPolicy.Evaluate(new LongRestRequest(kind, targets.ToArray(), requestedSurvivor), snapshots);

        if (replayed.IsFailure || policy != LongRestPolicy.PolicyVersion || replayed.Value.ReceiptId != receiptId
            || replayed.Value.InputHash != inputHash || replayed.Value.OutputHash != outputHash
            || replayed.Value.Outcome != outcome || replayed.Value.Reason != reason
            || replayed.Value.SurvivorMemoryId != survivorMemory || replayed.Value.SurvivorVersionId != survivorVersion)
        {
            return InvalidReceipt();
        }

        command.CommandText = "SELECT SourceVersionId, SurvivorVersionId FROM long_rest_suppressions WHERE ReceiptId = @id ORDER BY SourceVersionId LIMIT 17";

        List<string> sources = [];

        await using (DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.GetString(1) != survivorVersion)
                {
                    return InvalidReceipt();
                }

                sources.Add(reader.GetString(0));
            }
        }

        if (!sources.SequenceEqual(replayed.Value.SupersededVersionIds.Order(StringComparer.Ordinal)))
        {
            return InvalidReceipt();
        }

        return replayed;
    }

    private static Result<LongRestReceipt> InvalidReceipt() =>
        Refuse(ErrorCodes.LongRest.IntegrityFailure, "The immutable transformation receipt is inconsistent.");
}
