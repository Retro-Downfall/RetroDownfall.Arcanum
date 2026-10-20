using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data.MemoryEvaluation;

internal sealed partial class MemorySourceProofReader
{
    private static async Task<ArtifactSensitivityLabel?> ReadLabelAsync(SqliteConnection connection, SqliteTransaction transaction,
        MemoryEvaluationSourceCoordinate source, CovenantCanonicalHashWriter? metadata, CancellationToken ct)
    {
        SensitiveArtifactKind kind = source.Store == MemoryEvaluationSource.Saga ? SensitiveArtifactKind.Saga : SensitiveArtifactKind.Lexicon;

        string? artifactId = null;

        CovenantDigest? persistedLabel = null;

        CovenantDigest? persistedSensitivity = null;

        await using (SqliteCommand aliases = Command(connection, transaction, """
            SELECT ArtifactId, ArtifactLabelDigest, SensitivityDigest FROM artifact_sensitivity
            WHERE ArtifactKindCode = $kind AND lower(replace(ArtifactId, '-', '')) = lower(replace($id, '-', '')) LIMIT 2
            """, ("$kind", (int)kind), ("$id", source.SubjectId)))
        {
            await using SqliteDataReader reader = await aliases.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                artifactId = reader.GetString(0);

                persistedLabel = new((byte[])reader[1]);

                persistedSensitivity = new((byte[])reader[2]);

                Require(!await reader.ReadAsync(ct).ConfigureAwait(false));
            }
        }

        metadata?.WriteByte(artifactId is null ? (byte)0 : (byte)1);

        if (artifactId is null)
        {
            Require(source.Sensitivity == ContentSensitivity.None);

            return null;
        }

        Require(Guid.TryParse(source.SubjectId, out Guid id) && artifactId == id.ToString("D").ToUpperInvariant());

        var loaded = await ArtifactSensitivityLedger.ReadLabelWithinAsync(connection, transaction, kind, id, ct).ConfigureAwait(false);

        Require(loaded.IsSuccess && loaded.Value is not null);

        ArtifactSensitivityLabel label = loaded.Value!;

        Require(label.LabelDigest == persistedLabel && label.SensitivityDigest == persistedSensitivity
            && label.CampaignId == source.CampaignId && label.ArtifactRevision > 0
            && (label.ProducingPlanDigest is null) == (label.ProducingAdmissionDigest is null)
            && (source.Store != MemoryEvaluationSource.Saga || label.SessionId == (source.CanonicalOwnerSessionId is null ? null : Guid.Parse(source.CanonicalOwnerSessionId))));

        metadata?.WriteFixed32(label.LabelDigest.Bytes);

        metadata?.WriteGuid(label.LabelId);

        return label;
    }
}
