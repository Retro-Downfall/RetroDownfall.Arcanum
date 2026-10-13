using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

/// <summary>Data already read by the capability-bearing caller inside the same owning transaction.</summary>
internal sealed record LongRestProtectedApplicationContext(LongRestWorkIdentity Work, ImmutableArray<LongRestSourceLeaf> Inputs);

internal sealed partial class SagaLongRestService
{
    internal static CovenantDigest ProtectedEvidenceDigest(CovenantDigest work, string inputHash)
    {
        if (!work.IsValid || inputHash.Length != 64)
        {
            throw new InvalidDataException();
        }

        using MemoryStream stream = new();

        stream.Write("Arcanum.LongRest.ProtectedEvidence.v2\0"u8);

        Span<byte> version = stackalloc byte[4];

        BinaryPrimitives.WriteInt32BigEndian(version, 2);

        stream.Write(version);

        stream.Write(work.Bytes);

        stream.Write(Convert.FromHexString(inputHash));

        return new(SHA256.HashData(stream.ToArray()));
    }

    internal async Task<Result<LongRestReceipt>> ApplyInTransactionAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestRequest request, LongRestProtectedEvidence? protection, LongRestProtectedApplicationContext? context, CancellationToken ct)
    {
        if (protection is null)
        {
            return await ApplyInTransactionAsync(connection, transaction, request, null, ct).ConfigureAwait(false);
        }

        if (context is null || context.Work.Scope.ProtectedPartitionDigest != protection.PartitionDigest
            || await GrimoireCoreSchemaVersion.ReadAsync(connection, ct, transaction).ConfigureAwait(false) < 18)
        {
            return Refuse(ErrorCodes.LongRest.InvalidRequest, "Protected application requires exact current work and retained manifests.");
        }

        await LongRestDiscoveryStore.ValidateCurrentSweepWorkAsync(connection, transaction, context.Work, context.Inputs, ct).ConfigureAwait(false);

        Result<LongRestReceipt> decision = ProtectedLongRestPolicy.Evaluate(request, context.Inputs, protection);

        if (decision.IsFailure)
        {
            return decision;
        }

        foreach (LongRestSourceLeaf leaf in context.Inputs)
        {
            Result<LoadedSnapshot> snapshot = await ReadSnapshotAsync(connection, transaction, leaf.Target, ct, false).ConfigureAwait(false);

            Result<ArtifactSensitivityLabel?> label = await ArtifactSensitivityLedger.ReadLabelWithinAsync(connection, transaction,
                SensitiveArtifactKind.Saga, leaf.CanonicalLabel!.ArtifactId, ct).ConfigureAwait(false);

            if (snapshot.IsFailure || label.IsFailure || label.Value is null
                || JsonSerializer.Serialize(snapshot.Value.Snapshot, LongRestJsonContext.Default.LongRestSnapshot) != JsonSerializer.Serialize(leaf.Snapshot, LongRestJsonContext.Default.LongRestSnapshot)
                || JsonSerializer.Serialize(label.Value, LongRestJsonContext.Default.ArtifactSensitivityLabel) != JsonSerializer.Serialize(leaf.CanonicalLabel, LongRestJsonContext.Default.ArtifactSensitivityLabel)
                || leaf.Content is null || DerivedArtifactContentDigest.ForText(leaf.Content) != leaf.SagaContentDigest
                || !CryptographicOperations.FixedTimeEquals(AnnalContentDigest.ForSagaMemory(leaf.Content), Convert.FromHexString(leaf.Snapshot.ContentHash)))
            {
                return Refuse(ErrorCodes.LongRest.StaleInput, "Protected inputs no longer match their complete retained decision evidence.");
            }

            await using SqliteCommand manifest = connection.CreateCommand();

            manifest.Transaction = transaction;

            manifest.CommandText = """
                SELECT input.SourceEvidenceJson FROM long_rest_work_inputs input JOIN long_rest_work_items unit ON unit.WorkItemId = input.WorkItemId
                WHERE unit.WorkDigest = $work AND input.MemoryId = $memory AND input.VersionId = $version
                  AND (SELECT count(*) FROM artifact_sensitivity alias WHERE alias.ArtifactKindCode = 6
                    AND lower(replace(alias.ArtifactId, '-', '')) = lower(replace(input.MemoryId, '-', ''))) = 1;
                """;

            manifest.Parameters.AddWithValue("$work", LongRestDiscoveryHash.Work(context.Work).Value.Bytes);

            manifest.Parameters.AddWithValue("$memory", leaf.Snapshot.MemoryId);

            manifest.Parameters.AddWithValue("$version", leaf.Snapshot.VersionId);

            if (await manifest.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string stored
                || stored != JsonSerializer.Serialize(leaf with { Content = null, Vector = [] }, LongRestJsonContext.Default.LongRestSourceLeaf))
            {
                return Refuse(ErrorCodes.LongRest.StaleInput, "The exact protected input manifest is missing or ambiguous.");
            }
        }

        Result<LongRestReceipt>? replay = await ReadReceiptAsync(connection, transaction, decision.Value.ReceiptId, ct).ConfigureAwait(false);

        if (replay is not null)
        {
            return replay;
        }

        await WriteReceiptAsync(connection, transaction, request, decision.Value, context.Inputs.Select(leaf => leaf.Snapshot).ToArray(), ct, context).ConfigureAwait(false);

        return decision;
    }

    private static async Task WriteProtectedEvidenceAsync(DbConnection connection, DbTransaction transaction, LongRestReceipt receipt,
        LongRestProtectedApplicationContext context, CancellationToken ct)
    {
        LongRestSourceLeaf first = context.Inputs[0];

        CovenantDigest work = LongRestDiscoveryHash.Work(context.Work).Value;

        byte[]? exact = first.Provenance.Mode == GenerationProvenanceMode.Exact
            ? first.Provenance.ExactGenerationIds.SelectMany(id => id.ToByteArray(bigEndian: true)).ToArray() : null;

        await ExecuteAsync(connection, transaction, """
            INSERT INTO long_rest_protected_receipt_evidence (ReceiptId, PartitionDigest, ProvenanceModeCode, ExactGenerationIds, GenerationBloom, WorkDigest, EvidenceDigest)
            VALUES (@receipt, @partition, @mode, @exact, @bloom, @work, @evidence);
            """, ct, ("@receipt", receipt.ReceiptId), ("@partition", context.Work.Scope.ProtectedPartitionDigest!.Value.Bytes),
            ("@mode", (int)first.Provenance.Mode), ("@exact", exact), ("@bloom", exact is null ? first.Provenance.BloomBits.ToArray() : null),
            ("@work", work.Bytes), ("@evidence", ProtectedEvidenceDigest(work, receipt.InputHash).Bytes)).ConfigureAwait(false);

        for (int index = 0; index < receipt.Targets.Length; index++)
        {
            LongRestSourceLeaf leaf = context.Inputs.Single(item => item.Snapshot.VersionId == receipt.Targets[index].VersionId);

            ArtifactSensitivityLabel label = leaf.CanonicalLabel!;

            await ExecuteAsync(connection, transaction, """
                INSERT INTO long_rest_protected_receipt_inputs (ReceiptId, Ordinal, MemoryId, ClaimId, VersionId, LabelId, LabelDigest,
                    SagaRevision, SagaContentDigest, SensitivityDigest, LabelJson)
                VALUES (@receipt, @ordinal, @memory, @claim, @version, @label, @digest, @revision, @content, @sensitivity, @json);
                """, ct, ("@receipt", receipt.ReceiptId), ("@ordinal", index + 1), ("@memory", leaf.Snapshot.MemoryId),
                ("@claim", leaf.Snapshot.ClaimId), ("@version", leaf.Snapshot.VersionId), ("@label", label.LabelId.ToString("D").ToUpperInvariant()),
                ("@digest", label.LabelDigest.Bytes), ("@revision", checked((long)label.ArtifactRevision)), ("@content", label.ArtifactContentDigest.Bytes),
                ("@sensitivity", label.SensitivityDigest.Bytes), ("@json", JsonSerializer.Serialize(label, LongRestJsonContext.Default.ArtifactSensitivityLabel))).ConfigureAwait(false);
        }
    }

    private static async Task<Result<LongRestReceipt>> ReplayProtectedAsync(DbConnection connection, DbTransaction transaction, string receipt,
        string inputHash, LongRestRequest request, List<LongRestSnapshot> snapshots, CancellationToken ct)
    {
        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        Add(command, "@receipt", receipt);

        command.CommandText = "SELECT PartitionDigest, ProvenanceModeCode, ExactGenerationIds, GenerationBloom, WorkDigest, EvidenceDigest FROM long_rest_protected_receipt_evidence WHERE ReceiptId = @receipt;";

        CovenantDigest partition;

        GenerationProvenance provenance;

        await using (DbDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return InvalidReceipt();
            }

            partition = new((byte[])reader[0]);

            if (reader.GetInt32(1) == (int)GenerationProvenanceMode.Exact)
            {
                byte[] raw = (byte[])reader[2];

                if (raw.Length is < 16 or > 128 || raw.Length % 16 != 0 || !reader.IsDBNull(3))
                {
                    return InvalidReceipt();
                }

                provenance = GenerationProvenance.CreateExact(Enumerable.Range(0, raw.Length / 16).Select(index => new Guid(raw.AsSpan(index * 16, 16), bigEndian: true)));

                if (!raw.SequenceEqual(provenance.ExactGenerationIds.SelectMany(id => id.ToByteArray(bigEndian: true))))
                {
                    return InvalidReceipt();
                }
            }
            else if (reader.GetInt32(1) == (int)GenerationProvenanceMode.BloomOverflow && reader.IsDBNull(2))
            {
                provenance = GenerationProvenance.CreateBloom((byte[])reader[3]);
            }
            else
            {
                return InvalidReceipt();
            }

            if (ProtectedEvidenceDigest(new((byte[])reader[4]), inputHash) != new CovenantDigest((byte[])reader[5]))
            {
                return InvalidReceipt();
            }
        }

        command.CommandText = "SELECT Ordinal, MemoryId, ClaimId, VersionId, LabelId, LabelDigest, SagaRevision, SagaContentDigest, SensitivityDigest, LabelJson FROM long_rest_protected_receipt_inputs WHERE ReceiptId = @receipt ORDER BY Ordinal LIMIT 17;";

        List<LongRestSourceLeaf> leaves = [];

        await using (DbDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (leaves.Count >= snapshots.Count || reader.GetInt32(0) != leaves.Count + 1)
                {
                    return InvalidReceipt();
                }

                LongRestSnapshot snapshot = snapshots[leaves.Count];

                ArtifactSensitivityLabel label = JsonSerializer.Deserialize(reader.GetString(9), LongRestJsonContext.Default.ArtifactSensitivityLabel) ?? throw new InvalidDataException();

                if (snapshot.MemoryId != reader.GetString(1) || snapshot.ClaimId != reader.GetString(2) || snapshot.VersionId != reader.GetString(3)
                    || label.LabelId != Guid.Parse(reader.GetString(4)) || label.LabelDigest != new CovenantDigest((byte[])reader[5])
                    || label.ArtifactRevision != checked((ulong)reader.GetInt64(6)) || label.ArtifactContentDigest != new CovenantDigest((byte[])reader[7])
                    || label.SensitivityDigest != new CovenantDigest((byte[])reader[8]))
                {
                    return InvalidReceipt();
                }

                leaves.Add(new(snapshot, 1, 0, null, label.Sensitivity, label.Provenance, [label.LabelDigest], CanonicalLabel: label,
                    SagaContentDigest: label.ArtifactContentDigest, SagaRevision: label.ArtifactRevision));
            }
        }

        return leaves.Count != snapshots.Count ? InvalidReceipt() : ProtectedLongRestPolicy.Evaluate(request, leaves,
            new(partition, provenance, [.. leaves.SelectMany(leaf => leaf.LabelDigests)]));
    }
}
