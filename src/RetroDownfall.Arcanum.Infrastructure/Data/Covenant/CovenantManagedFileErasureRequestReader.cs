using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// The durable producer identity from which the shared managed-file kernel can build one request.
/// </summary>
internal sealed record CovenantManagedFileErasureIdentity(
    Guid SourceWriteOperationId,
    ulong ObservedSourceRevision,
    Guid ArtifactId,
    Guid SensitivityLabelId,
    Guid? ExistingWorkItemId)
{

    /// <summary>
    /// Creates the bounded request immediately before its kernel call, reusing committed work when
    /// present and minting an identity only when no nonterminal row exists.
    /// </summary>
    internal CovenantManagedFileErasureRequest ToRequest(Guid erasureOperationId) =>
        new(
            ExistingWorkItemId ?? Guid.NewGuid(),
            erasureOperationId,
            SourceWriteOperationId,
            ArtifactId,
            SensitivityLabelId,
            ObservedSourceRevision);

}

/// <summary>
/// Reads the one adopted ownership-bearing producer and any exact active erasure work that belongs
/// to it, through a connection and snapshot the caller already owns.
/// </summary>
/// <remarks>
/// Both direct sensitivity purge and whole-family erasure use this reader. It opens no connection,
/// starts no transaction, and accepts no path or ownership evidence from its caller. A missing,
/// duplicate, or internally mismatched producer/work row is not guessed at; the caller receives the
/// one content-free manual-erasure refusal before any filesystem effect.
/// </remarks>
internal sealed class CovenantManagedFileErasureRequestReader
{

    private static readonly Error ManualErasureRequired = new(
        ErrorCodes.Covenant.ManualArtifactErasureRequired,
        "A managed artifact does not have one exact adopted ownership record and requires manual erasure.");

    internal async Task<Result<CovenantManagedFileErasureIdentity?>> TryReadWithinAsync(
        SqliteConnection callerOwnedConnection,
        SqliteTransaction? transaction,
        ArtifactSensitivityLabel label,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(callerOwnedConnection);

        ArgumentNullException.ThrowIfNull(label);

        if (label.ArtifactKind != SensitiveArtifactKind.ManagedWorkspaceFile)
        {

            return Result<CovenantManagedFileErasureIdentity?>.Failure(ManualErasureRequired);

        }

        try
        {

            Result<IReadOnlyList<ProducerIdentity>> producers = await ReadProducersAsync(
                callerOwnedConnection,
                transaction,
                label,
                cancellationToken).ConfigureAwait(false);

            if (producers.IsFailure)
            {

                return Result<CovenantManagedFileErasureIdentity?>.Failure(ManualErasureRequired);

            }

            if (producers.Value.Count == 0)
            {

                return Result<CovenantManagedFileErasureIdentity?>.Success(null);

            }

            if (producers.Value.Count != 1)
            {

                return Result<CovenantManagedFileErasureIdentity?>.Failure(ManualErasureRequired);

            }

            ProducerIdentity producer = producers.Value[0];

            IReadOnlyList<WorkIdentity> active = await ReadActiveWorkAsync(
                callerOwnedConnection,
                transaction,
                producer.WriteOperationId,
                cancellationToken).ConfigureAwait(false);

            if (active.Count > 1)
            {

                return Result<CovenantManagedFileErasureIdentity?>.Failure(ManualErasureRequired);

            }

            Guid? existingWorkItemId = null;

            if (active.Count == 1)
            {

                WorkIdentity work = active[0];

                if (work.ArtifactId != label.ArtifactId
                    || work.SensitivityLabelId != label.LabelId
                    || work.ExpectedSourceRevision != producer.Revision)
                {

                    return Result<CovenantManagedFileErasureIdentity?>.Failure(ManualErasureRequired);

                }

                existingWorkItemId = work.WorkItemId;

            }

            return Result<CovenantManagedFileErasureIdentity?>.Success(
                new CovenantManagedFileErasureIdentity(
                    producer.WriteOperationId,
                    producer.Revision,
                    label.ArtifactId,
                    label.LabelId,
                    existingWorkItemId));

        }
        catch (Exception exception) when (
            exception is SqliteException or FormatException or OverflowException or ArgumentException)
        {

            return Result<CovenantManagedFileErasureIdentity?>.Failure(ManualErasureRequired);

        }

    }

    private static async Task<Result<IReadOnlyList<ProducerIdentity>>> ReadProducersAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ArtifactSensitivityLabel label,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT WriteOperationId, Revision, DurableLocationEvidence, FinalOwnershipEvidence
            FROM managed_file_write_intents
            WHERE ArtifactId = $artifactId
              AND SensitivityLabelId = $labelId
              AND PhaseCode = 7
              AND PendingArtifactSensitivityLabel IS NULL
              AND FinalOwnershipEvidence IS NOT NULL
            ORDER BY WriteOperationId
            LIMIT 2;
            """;

        _ = command.Parameters.AddWithValue("$artifactId", Format(label.ArtifactId));

        _ = command.Parameters.AddWithValue("$labelId", Format(label.LabelId));

        List<ProducerIdentity> rows = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            if (reader.GetValue(2) is not byte[] locationPayload
                || reader.GetValue(3) is not byte[] ownershipPayload
                || ManagedFileEvidenceCodec.DecodeWriteLocation(locationPayload).IsFailure
                || ManagedFileEvidenceCodec.DecodeOwnership(ownershipPayload).IsFailure)
            {

                return Result<IReadOnlyList<ProducerIdentity>>.Failure(ManualErasureRequired);

            }

            rows.Add(
                new ProducerIdentity(
                    Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                    checked((ulong)reader.GetInt64(1))));

        }

        return Result<IReadOnlyList<ProducerIdentity>>.Success(rows);

    }

    private static async Task<IReadOnlyList<WorkIdentity>> ReadActiveWorkAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid sourceWriteOperationId,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT WorkItemId, ArtifactId, SourceSensitivityLabelId, ExpectedSourceRevision
            FROM local_erasure_work_items
            WHERE SourceWriteOperationId = $source
              AND StateCode IN (1, 2)
            ORDER BY WorkItemId
            LIMIT 2;
            """;

        _ = command.Parameters.AddWithValue("$source", Format(sourceWriteOperationId));

        List<WorkIdentity> rows = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            rows.Add(
                new WorkIdentity(
                    Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                    Guid.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                    Guid.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                    checked((ulong)reader.GetInt64(3))));

        }

        return rows;

    }

    private static string Format(Guid value) => value.ToString("D").ToUpperInvariant();

    private sealed record ProducerIdentity(Guid WriteOperationId, ulong Revision);

    private sealed record WorkIdentity(
        Guid WorkItemId,
        Guid ArtifactId,
        Guid SensitivityLabelId,
        ulong ExpectedSourceRevision);

}
