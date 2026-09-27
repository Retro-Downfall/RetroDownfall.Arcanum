using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

internal sealed partial class ArtifactSensitivityLedger
{
    /// <summary>
    /// Replaces exactly one Lexicon label while retaining its owner and producing evidence. The
    /// subject owner must roll back the entire transaction on any refusal or exception.
    /// </summary>
    internal static async Task<Result<ArtifactSensitivityLabel>> ReplaceLexiconWithinAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ArtifactSensitivityLabel expected,
        CovenantDigest contentDigest,
        DateTimeOffset recordedAt,
        CancellationToken cancellationToken)
    {
        if (expected.ArtifactKind != SensitiveArtifactKind.Lexicon
            || SQLitePCL.raw.sqlite3_get_autocommit(connection.Handle) != 0
            || (transaction is not null && !ReferenceEquals(transaction.Connection, connection)))
        {
            return new Error(ErrorCodes.Covenant.ForbiddenAuthority, "Lexicon label replacement requires its owner's active transaction.");
        }

        if (expected.ArtifactRevision >= (ulong)long.MaxValue)
        {
            return new Error(ErrorCodes.Lexicon.ArtifactRevisionExhausted, "The Lexicon artifact revision is exhausted.");
        }

        ArtifactSensitivityLabel successor = new(Guid.NewGuid(), SensitiveArtifactKind.Lexicon, expected.ArtifactId,
            expected.SessionId, expected.CampaignId, expected.TurnId, checked(expected.ArtifactRevision + 1),
            contentDigest, expected.Sensitivity, expected.Provenance, expected.ProducingPlanDigest,
            expected.ProducingAdmissionDigest, expected.ProducingMaintenanceReceiptDigest, recordedAt);

        using CovenantSqliteAuthorizationScope authority = CovenantSqliteConnectionInitializer.Instance.Authorize(
            connection, CovenantSqliteAuthorizationKind.ArtifactReplacement);

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;

            // Every stored field participates, including the digests and nullable owner/evidence
            // fields. The ordinary ledger entry point remains append-only.
            command.CommandText = """
                DELETE FROM artifact_sensitivity
                WHERE LabelId = $label AND ArtifactKindCode = $kind AND ArtifactId = $artifact
                    AND SessionId IS $session AND CampaignId IS $campaign AND TurnId IS $turn
                    AND ArtifactRevision = $revision AND ArtifactContentDigest = $content
                    AND SensitivityCode = $sensitivity AND ProvenanceModeCode = $mode
                    AND ExactGenerationIds IS $exact AND GenerationBloom IS $bloom
                    AND SensitivityDigest = $sensitivityDigest AND ProducingPlanDigest IS $plan
                    AND ProducingAdmissionDigest IS $admission AND ProducingMaintenanceReceiptDigest IS $maintenance
                    AND ArtifactLabelDigest = $digest AND CreatedAtUtc = $at;
                """;

            command.Parameters.AddWithValue("$label", Format(expected.LabelId));

            command.Parameters.AddWithValue("$kind", (long)expected.ArtifactKind);

            command.Parameters.AddWithValue("$artifact", Format(expected.ArtifactId));

            command.Parameters.AddWithValue("$session", FormatOptional(expected.SessionId));

            command.Parameters.AddWithValue("$campaign", FormatOptional(expected.CampaignId));

            command.Parameters.AddWithValue("$turn", FormatOptional(expected.TurnId));

            command.Parameters.AddWithValue("$revision", checked((long)expected.ArtifactRevision));

            command.Parameters.AddWithValue("$content", expected.ArtifactContentDigest.Bytes);

            command.Parameters.AddWithValue("$sensitivity", (long)expected.Sensitivity);

            command.Parameters.AddWithValue("$mode", (long)expected.Provenance.Mode);

            command.Parameters.AddWithValue("$exact", expected.Provenance.Mode == GenerationProvenanceMode.Exact
                ? expected.Provenance.ToCanonicalExactBytes() : DBNull.Value);

            command.Parameters.AddWithValue("$bloom", expected.Provenance.Mode == GenerationProvenanceMode.BloomOverflow
                ? expected.Provenance.ToCanonicalBloomBytes() : DBNull.Value);

            command.Parameters.AddWithValue("$sensitivityDigest", expected.SensitivityDigest.Bytes);

            command.Parameters.AddWithValue("$plan", OptionalDigest(expected.ProducingPlanDigest));

            command.Parameters.AddWithValue("$admission", OptionalDigest(expected.ProducingAdmissionDigest));

            command.Parameters.AddWithValue("$maintenance", OptionalDigest(expected.ProducingMaintenanceReceiptDigest));

            command.Parameters.AddWithValue("$digest", expected.LabelDigest.Bytes);

            command.Parameters.AddWithValue("$at", UtcInstantText.Format(expected.CreatedAt));

            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                return new Error(ErrorCodes.Lexicon.StaleCurationTarget, "The inspected Lexicon sensitivity label has changed.");
            }
        }

        await InsertLabelAsync(connection, transaction, successor, cancellationToken).ConfigureAwait(false);

        if (successor.SessionId is { } sessionId)
        {
            await using SqliteCommand command = connection.CreateCommand();

            command.Transaction = transaction;

            // One stable artifact is still counted exactly once. Its Session evidence advances
            // without lowering the conservative sensitivity maximum or losing generation history.
            // INTEGER affinity does not guarantee INTEGER storage; reject coercible counters.
            command.CommandText = """
                UPDATE session_sensitivity_state SET
                    MaximumSensitivityCode = max(MaximumSensitivityCode, $sensitivity),
                    GenerationProvenanceDigest = $provenance,
                    Revision = Revision + 1, UpdatedAtUtc = $at
                WHERE SessionId = $session
                    AND typeof(TaintedArtifactCount) = 'integer' AND TaintedArtifactCount > 0
                    AND typeof(Revision) = 'integer' AND Revision >= 0 AND Revision < 9223372036854775807;
                """;

            command.Parameters.AddWithValue("$sensitivity", (long)successor.Sensitivity);

            command.Parameters.AddWithValue("$provenance", await MergedProvenanceDigestAsync(
                connection, transaction, sessionId, successor, cancellationToken).ConfigureAwait(false));

            command.Parameters.AddWithValue("$at", UtcInstantText.Format(recordedAt));

            command.Parameters.AddWithValue("$session", Format(sessionId));

            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                return new Error(ErrorCodes.Lexicon.CurationIntegrityFailed, "The Lexicon owner's sensitivity projection is inconsistent or exhausted.");
            }
        }

        return successor;
    }
}
