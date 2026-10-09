using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

internal sealed partial class CampaignRollupStore
{
    private static async Task InsertArtifactAsync(SqliteConnection connection, SqliteTransaction transaction,
        CampaignRollupArtifact artifact, CampaignMaintenancePublication? publication, CancellationToken ct)
    {
        bool contribution = artifact.SessionId.HasValue;

        string sql = contribution
            ?
            """
            INSERT INTO campaign_contribution_artifacts (ArtifactId, CampaignId, SessionId, Revision, Content,
                ContentDigest, SensitivityCode, SensitivityDigest, SourceManifestDigest, SourceGeneration,
                SummarizedThroughSequence, CreatedAtUtc, ProducingMaintenanceReceiptDigest)
            VALUES ($id, $campaign, $session, $revision, $content, $contentDigest, $sensitivity, $sensitivityDigest,
                $manifest, $generation, $through, $now, $receipt);
            """
            :
            """
            INSERT INTO campaign_rollup_artifacts (ArtifactId, CampaignId, Revision, Content,
                ContentDigest, SensitivityCode, SensitivityDigest, SourceManifestDigest, SourceGeneration,
                SourceCount, CreatedAtUtc, ProducingMaintenanceReceiptDigest)
            VALUES ($id, $campaign, $revision, $content, $contentDigest, $sensitivity, $sensitivityDigest,
                $manifest, $generation, $count, $now, $receipt);
            """;

        _ = await ExecuteAsync(connection, transaction, sql, ct,
            ("$id", Format(artifact.ArtifactId)), ("$campaign", Format(artifact.CampaignId)),
            ("$session", artifact.SessionId is { } sessionId ? Format(sessionId) : null),
            ("$revision", artifact.Revision), ("$content", artifact.Content), ("$contentDigest", artifact.ContentDigest.Bytes),
            ("$sensitivity", (int)artifact.Sensitivity), ("$sensitivityDigest", artifact.SensitivityDigest.Bytes),
            ("$manifest", artifact.SourceManifestDigest.Bytes), ("$generation", artifact.SourceGeneration),
            ("$through", artifact.SummarizedThroughSequence), ("$count", artifact.SourceCount),
            ("$now", UtcInstantText.Format(DateTimeOffset.UtcNow)),
            ("$receipt", publication?.DisclosureReceiptDigest.Bytes)).ConfigureAwait(false);
    }

    private static async Task LabelAsync(SqliteConnection connection, SqliteTransaction transaction,
        CampaignRollupArtifact artifact, CampaignMaintenancePublication? publication, CancellationToken ct)
    {
        Result<LabeledArtifactWriteReceipt> result = await ArtifactSensitivityLedger.WriteWithinAsync(connection, transaction,
            new DerivedArtifactWrite(artifact.SessionId.HasValue ? SensitiveArtifactKind.CampaignContribution : SensitiveArtifactKind.CampaignRollup,
                artifact.ArtifactId, artifact.SessionId, artifact.CampaignId, publication?.Attempt.Identity.ClaimLease.Claim.ClientTurnId,
                checked((ulong)artifact.Revision), artifact.ContentDigest, artifact.Sensitivity, artifact.Provenance,
                producingMaintenanceReceiptDigest: artifact.Sensitivity == ContentSensitivity.None ? null : publication?.DisclosureReceiptDigest),
            ct).ConfigureAwait(false);

        if (result.IsFailure)
        {
            throw Refuse(result.Error);
        }
    }

    private static async Task ValidatePublicationAsync(SqliteConnection connection, SqliteTransaction transaction,
        CampaignMaintenancePublication? publication, ICovenantSnapshotReadLease? authority,
        Guid campaignId, Guid? sourceSessionId, long expectedRevision, long sourceGeneration,
        CovenantDigest manifest, ContentSensitivity sensitivity, CancellationToken ct)
    {
        await RevalidateAsync(authority, campaignId, sensitivity, ct).ConfigureAwait(false);

        if (publication is null)
        {
            if (sensitivity != ContentSensitivity.None)
            {
                throw Refuse(new Error(ErrorCodes.Covenant.ForbiddenAuthority,
                    "Protected Campaign summary output requires its acknowledged claim-bound maintenance attempt."));
            }

            return;
        }

        CampaignMaintenanceIdentity actual = publication.Attempt.Identity;

        if (!actual.Campaign.IsCampaignBound || actual.Campaign.CampaignId != campaignId)
        {
            throw Refuse(Stale("The maintenance publication names a different canonical Campaign."));
        }

        CampaignMaintenanceIdentity expected = actual with
        {
            Step = sourceSessionId.HasValue ? CovenantMaintenanceStep.CampaignContribution : CovenantMaintenanceStep.CampaignRollup,
            SourceSessionId = sourceSessionId,
            ExpectedOutputRevision = expectedRevision,
            SourceGeneration = sourceGeneration,
            SourceManifestDigest = manifest,
        };

        Result valid = await CampaignMaintenanceCheckpointStore.ValidatePublicationAsync(
            new CovenantMutationTransaction(connection, transaction), publication, expected, ct).ConfigureAwait(false);

        if (valid.IsFailure)
        {
            throw Refuse(valid.Error);
        }
    }

    private static async Task CommitPublicationAsync(SqliteConnection connection, SqliteTransaction transaction,
        CampaignMaintenancePublication? publication, CampaignRollupArtifact output, CancellationToken ct)
    {
        if (publication is null)
        {
            return;
        }

        Result committed = await CampaignMaintenanceCheckpointStore.CommitPublicationAsync(
            new CovenantMutationTransaction(connection, transaction), publication, output, ct).ConfigureAwait(false);

        if (committed.IsFailure)
        {
            throw Refuse(committed.Error);
        }
    }
}
