using System.Globalization;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// The one writer of <c>artifact_sensitivity</c> and <c>session_sensitivity_state</c>.
/// </summary>
/// <remarks>
/// Every derived-output producer routes its label through here, inside the transaction that writes
/// the artifact itself. That is the property the whole information-flow ledger rests on: a tainted
/// artifact and its label share a fate, so no crash, rollback, or partially applied write can leave
/// protected content sitting in storage with nothing recording that it is protected (§10.12).
///
/// <para>The label row is append-only by trigger, so this type never updates one. A downgrade is
/// therefore not a matter of policy here — it is unrepresentable — but the clean-write path still
/// checks for an existing tainted label and refuses, because reaching the trigger would abort the
/// caller's whole transaction with a SQL error instead of a typed refusal.</para>
///
/// <para>The Session projection is maintained in the same transaction and is deliberately monotonic.
/// It is read once per request by the response-cache filter, so it must never be the thing that has
/// to be recomputed from labels on a hot path, and it must never go clean while any tainted artifact
/// is still counted.</para>
/// </remarks>
internal sealed class ArtifactSensitivityLedger(ICovenantConnectionSource connections)
    : IArtifactSensitivityLedger
{

    public async Task<Result<LabeledArtifactWriteReceipt>> LabelAsync(
        DerivedArtifactWrite write,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(write);

        SqliteConnection connection = await connections.GetOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        Result<LabeledArtifactWriteReceipt> receipt = await WriteWithinAsync(
            connection,
            transaction,
            write,
            cancellationToken).ConfigureAwait(false);

        if (receipt.IsFailure)
        {

            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return receipt;

        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return receipt;

    }

    public async Task<Result<ArtifactSensitivityLabel?>> TryReadLabelAsync(
        SensitiveArtifactKind artifactKind,
        Guid artifactId,
        CancellationToken cancellationToken)
    {

        SqliteConnection connection = await connections.GetOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        return await ReadLabelWithinAsync(
            connection,
            transaction: null,
            artifactKind,
            artifactId,
            cancellationToken).ConfigureAwait(false);

    }

    public async Task<Result<SessionSensitivityProjection>> ReadSessionProjectionAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        SqliteConnection connection = await connections.GetOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        return await ReadProjectionWithinAsync(connection, transaction: null, sessionId, cancellationToken)
            .ConfigureAwait(false);

    }

    /// <summary>
    /// Writes the label and advances the projection inside a transaction the caller already owns.
    /// </summary>
    /// <remarks>
    /// The entry point for every producer that has its own artifact write to be atomic with. It
    /// never commits or rolls back: the caller's transaction decides, which is exactly what makes
    /// "the artifact and its label share a fate" true rather than intended.
    /// </remarks>
    internal static async Task<Result<LabeledArtifactWriteReceipt>> WriteWithinAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DerivedArtifactWrite write,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(transaction);

        ArgumentNullException.ThrowIfNull(write);

        Result<ArtifactSensitivityLabel?> existing = await ReadLabelWithinAsync(
            connection,
            transaction,
            write.ArtifactKind,
            write.ArtifactId,
            cancellationToken).ConfigureAwait(false);

        if (existing.IsFailure)
        {

            return existing.Error;

        }

        if (existing.Value is { } current)
        {

            // The artifact already carries evidence. Repeating the exact same label is an idempotent
            // replay; anything else — including a clean relabel — would be a downgrade.
            return current.LabelDigest == BuildLabel(write, current.LabelId, current.CreatedAt).LabelDigest
                ? Result<LabeledArtifactWriteReceipt>.Success(new LabeledArtifactWriteReceipt(
                    write.ArtifactKind,
                    write.ArtifactId,
                    current.Sensitivity,
                    current.LabelId,
                    current.LabelDigest))
                : new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "This artifact already carries different sensitivity evidence; a label is never rewritten.");

        }

        if (!write.RequiresLabel)
        {

            return Result<LabeledArtifactWriteReceipt>.Success(new LabeledArtifactWriteReceipt(
                write.ArtifactKind,
                write.ArtifactId,
                ContentSensitivity.None,
                LabelId: null,
                LabelDigest: null));

        }

        ArtifactSensitivityLabel label = BuildLabel(write, Guid.NewGuid(), DateTimeOffset.UtcNow);

        try
        {

            await InsertLabelAsync(connection, transaction, label, cancellationToken).ConfigureAwait(false);

            if (label.SessionId is { } sessionId)
            {

                await AdvanceProjectionAsync(
                    connection,
                    transaction,
                    sessionId,
                    label,
                    cancellationToken).ConfigureAwait(false);

            }

        }
        catch (SqliteException exception)
        {

            // A constraint or guard trigger refused the row. The caller's transaction is still open
            // and must not commit an artifact whose label did not land.
            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                $"The sensitivity label for this artifact could not be persisted ({exception.SqliteErrorCode}).");

        }

        return Result<LabeledArtifactWriteReceipt>.Success(new LabeledArtifactWriteReceipt(
            write.ArtifactKind,
            write.ArtifactId,
            label.Sensitivity,
            label.LabelId,
            label.LabelDigest));

    }

    internal static async Task<Result<ArtifactSensitivityLabel?>> ReadLabelWithinAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        SensitiveArtifactKind artifactKind,
        Guid artifactId,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT LabelId,
                   SessionId,
                   CampaignId,
                   TurnId,
                   ArtifactRevision,
                   ArtifactContentDigest,
                   SensitivityCode,
                   ProvenanceModeCode,
                   ExactGenerationIds,
                   GenerationBloom,
                   ProducingPlanDigest,
                   ProducingAdmissionDigest,
                   ProducingMaintenanceReceiptDigest,
                   CreatedAtUtc
            FROM artifact_sensitivity
            WHERE ArtifactKindCode = $kind AND ArtifactId = $artifactId;
            """;

        _ = command.Parameters.AddWithValue("$kind", (long)artifactKind);

        _ = command.Parameters.AddWithValue("$artifactId", Format(artifactId));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            return Result<ArtifactSensitivityLabel?>.Success(null);

        }

        try
        {

            GenerationProvenance provenance = (GenerationProvenanceMode)reader.GetInt64(7)
                is GenerationProvenanceMode.Exact
                ? GenerationProvenance.CreateExact(ReadGenerations((byte[])reader.GetValue(8)))
                : GenerationProvenance.CreateBloom((byte[])reader.GetValue(9));

            return new ArtifactSensitivityLabel(
                Guid.Parse(reader.GetString(0)),
                artifactKind,
                artifactId,
                reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
                reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
                checked((ulong)reader.GetInt64(4)),
                new CovenantDigest((byte[])reader.GetValue(5)),
                (ContentSensitivity)reader.GetInt64(6),
                provenance,
                reader.IsDBNull(10) ? null : new CovenantDigest((byte[])reader.GetValue(10)),
                reader.IsDBNull(11) ? null : new CovenantDigest((byte[])reader.GetValue(11)),
                reader.IsDBNull(12) ? null : new CovenantDigest((byte[])reader.GetValue(12)),
                DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture));

        }
        catch (Exception exception) when (
            exception is FormatException or OverflowException or ArgumentException)
        {

            // A label that cannot be reconstructed is not a missing label. Reporting absence here
            // would let a protected read proceed as though its artifact were clean.
            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "This artifact's sensitivity label is malformed and cannot authorize any read.");

        }

    }

    internal static async Task<Result<SessionSensitivityProjection>> ReadProjectionWithinAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT TaintedArtifactCount, MaximumSensitivityCode, GenerationProvenanceDigest, Revision
            FROM session_sensitivity_state
            WHERE SessionId = $sessionId;
            """;

        _ = command.Parameters.AddWithValue("$sessionId", Format(sessionId));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            // No row is the honest answer for a Session that has never produced a tainted artifact.
            return new SessionSensitivityProjection(
                sessionId,
                TaintedArtifactCount: 0,
                ContentSensitivity.None,
                CovenantDigests.Sensitivity(GenerationProvenance.CreateExact([]).ToDigestInput(ContentSensitivity.None)),
                Revision: 0);

        }

        return new SessionSensitivityProjection(
            sessionId,
            reader.GetInt64(0),
            (ContentSensitivity)reader.GetInt64(1),
            new CovenantDigest((byte[])reader.GetValue(2)),
            reader.GetInt64(3));

    }

    private static ArtifactSensitivityLabel BuildLabel(
        DerivedArtifactWrite write,
        Guid labelId,
        DateTimeOffset createdAt) =>
        write.ToLabel(labelId, createdAt);

    private static async Task InsertLabelAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ArtifactSensitivityLabel label,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO artifact_sensitivity (
                LabelId,
                ArtifactKindCode,
                ArtifactId,
                SensitivityCode,
                ProvenanceModeCode,
                ExactGenerationIds,
                GenerationBloom,
                SessionId,
                CampaignId,
                TurnId,
                ArtifactRevision,
                ArtifactContentDigest,
                SensitivityDigest,
                ProducingPlanDigest,
                ProducingAdmissionDigest,
                ProducingMaintenanceReceiptDigest,
                ArtifactLabelDigest,
                CreatedAtUtc)
            VALUES (
                $labelId,
                $kind,
                $artifactId,
                $sensitivityCode,
                $provenanceMode,
                $exactGenerations,
                $generationBloom,
                $sessionId,
                $campaignId,
                $turnId,
                $artifactRevision,
                $contentDigest,
                $sensitivityDigest,
                $planDigest,
                $admissionDigest,
                $maintenanceDigest,
                $labelDigest,
                $createdAtUtc);
            """;

        _ = command.Parameters.AddWithValue("$labelId", Format(label.LabelId));

        _ = command.Parameters.AddWithValue("$kind", (long)label.ArtifactKind);

        _ = command.Parameters.AddWithValue("$artifactId", Format(label.ArtifactId));

        _ = command.Parameters.AddWithValue("$sensitivityCode", (long)label.Sensitivity);

        _ = command.Parameters.AddWithValue("$provenanceMode", (long)label.Provenance.Mode);

        _ = command.Parameters.AddWithValue(
            "$exactGenerations",
            label.Provenance.Mode is GenerationProvenanceMode.Exact
                ? label.Provenance.ToCanonicalExactBytes()
                : DBNull.Value);

        _ = command.Parameters.AddWithValue(
            "$generationBloom",
            label.Provenance.Mode is GenerationProvenanceMode.BloomOverflow
                ? label.Provenance.ToCanonicalBloomBytes()
                : DBNull.Value);

        _ = command.Parameters.AddWithValue("$sessionId", FormatOptional(label.SessionId));

        _ = command.Parameters.AddWithValue("$campaignId", FormatOptional(label.CampaignId));

        _ = command.Parameters.AddWithValue("$turnId", FormatOptional(label.TurnId));

        _ = command.Parameters.AddWithValue("$artifactRevision", checked((long)label.ArtifactRevision));

        _ = command.Parameters.AddWithValue("$contentDigest", label.ArtifactContentDigest.Bytes);

        _ = command.Parameters.AddWithValue("$sensitivityDigest", label.SensitivityDigest.Bytes);

        _ = command.Parameters.AddWithValue("$planDigest", OptionalDigest(label.ProducingPlanDigest));

        _ = command.Parameters.AddWithValue("$admissionDigest", OptionalDigest(label.ProducingAdmissionDigest));

        _ = command.Parameters.AddWithValue(
            "$maintenanceDigest",
            OptionalDigest(label.ProducingMaintenanceReceiptDigest));

        _ = command.Parameters.AddWithValue("$labelDigest", label.LabelDigest.Bytes);

        _ = command.Parameters.AddWithValue(
            "$createdAtUtc",
            label.CreatedAt.ToString("o", CultureInfo.InvariantCulture));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Folds one new tainted label into the Session projection.
    /// </summary>
    /// <remarks>
    /// The provenance digest is recomputed from the merge of what the projection already carried and
    /// what this label adds, so a Session that has seen more than eight generations converges on the
    /// same Bloom the labels themselves would. The count only ever grows here; retention and erasure
    /// own the decrease, in the transaction that removes the artifacts.
    /// </remarks>
    private static async Task AdvanceProjectionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        ArtifactSensitivityLabel label,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO session_sensitivity_state (
                SessionId,
                TaintedArtifactCount,
                MaximumSensitivityCode,
                GenerationProvenanceDigest,
                Revision,
                UpdatedAtUtc)
            VALUES ($sessionId, 1, $sensitivityCode, $provenanceDigest, 1, $updatedAtUtc)
            ON CONFLICT(SessionId) DO UPDATE SET
                TaintedArtifactCount = TaintedArtifactCount + 1,
                MaximumSensitivityCode = max(MaximumSensitivityCode, excluded.MaximumSensitivityCode),
                GenerationProvenanceDigest = excluded.GenerationProvenanceDigest,
                Revision = Revision + 1,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;

        _ = command.Parameters.AddWithValue("$sessionId", Format(sessionId));

        _ = command.Parameters.AddWithValue("$sensitivityCode", (long)label.Sensitivity);

        _ = command.Parameters.AddWithValue(
            "$provenanceDigest",
            await MergedProvenanceDigestAsync(
                connection,
                transaction,
                sessionId,
                label,
                cancellationToken).ConfigureAwait(false));

        _ = command.Parameters.AddWithValue(
            "$updatedAtUtc",
            label.CreatedAt.ToString("o", CultureInfo.InvariantCulture));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Digests the merge of every generation this Session's live labels carry.
    /// </summary>
    /// <remarks>
    /// Recomputed from the distinct provenance values rather than chained onto the previous digest.
    /// A chain would be order-sensitive, so two Sessions holding exactly the same generations would
    /// fingerprint differently depending on the order their artifacts happened to be written, and
    /// the value would stop being comparable — which is the only thing a projection digest is for.
    ///
    /// <para>Distinct is what keeps this bounded. A dataset generation changes on reset and restore,
    /// not per turn, so a Session with thousands of tainted artifacts still has one or two distinct
    /// provenance values, and the indexed lookup collapses them before the merge runs.</para>
    /// </remarks>
    private static async Task<byte[]> MergedProvenanceDigestAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        ArtifactSensitivityLabel label,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT DISTINCT ProvenanceModeCode, ExactGenerationIds, GenerationBloom
            FROM artifact_sensitivity
            WHERE SessionId = $sessionId;
            """;

        _ = command.Parameters.AddWithValue("$sessionId", Format(sessionId));

        GenerationProvenance merged = label.Provenance;

        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                merged = merged.Merge(
                    (GenerationProvenanceMode)reader.GetInt64(0) is GenerationProvenanceMode.Exact
                        ? GenerationProvenance.CreateExact(ReadGenerations((byte[])reader.GetValue(1)))
                        : GenerationProvenance.CreateBloom((byte[])reader.GetValue(2)));

            }

        }

        return CovenantDigests.Sensitivity(merged.ToDigestInput(label.Sensitivity)).Bytes;

    }

    private static IEnumerable<Guid> ReadGenerations(byte[] packed)
    {

        for (int offset = 0; offset + 16 <= packed.Length; offset += 16)
        {

            yield return new Guid(packed.AsSpan(offset, 16), bigEndian: true);

        }

    }

    private static object OptionalDigest(CovenantDigest? digest) =>
        digest is { IsValid: true } value ? value.Bytes : DBNull.Value;

    private static object FormatOptional(Guid? value) =>
        value is { } present ? Format(present) : DBNull.Value;

    private static string Format(Guid value) => value.ToString().ToUpperInvariant();

}
