using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Admission-time relational verification; ordinary reads validate their exact rows separately.</summary>
internal static class GuildStorageIntegrity
{
    internal static async Task ValidateAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT
              EXISTS(SELECT 1 FROM guild_agent_cards a WHERE
                (a.ContentId IS NULL AND a.Available<>0)
                OR (a.ContentId IS NOT NULL AND NOT EXISTS(
                  SELECT 1 FROM guild_content c JOIN guild_agent_card_revisions r ON r.ContentId=c.ContentId
                  WHERE c.ContentId=a.ContentId AND c.ContentKindCode=0 AND c.SourceRecordId=a.CardId
                    AND c.Revision=a.CurrentRevision AND r.CardId=a.CardId AND r.Revision=a.CurrentRevision
                    AND c.SessionId IS a.SessionId AND c.CampaignId IS a.CampaignId)))
              OR EXISTS(SELECT 1 FROM guild_agent_card_revisions r WHERE NOT EXISTS(
                SELECT 1 FROM guild_agent_cards a JOIN guild_content c ON c.ContentId=r.ContentId
                WHERE a.CardId=r.CardId AND c.ContentKindCode=0 AND c.SourceRecordId=r.CardId AND c.Revision=r.Revision
                  AND c.SessionId IS a.SessionId AND c.CampaignId IS a.CampaignId))
              OR EXISTS(SELECT 1 FROM guild_agent_card_skills k WHERE NOT EXISTS(
                SELECT 1 FROM guild_agent_card_revisions r WHERE r.CardId=k.CardId AND r.Revision=k.Revision))
              OR EXISTS(SELECT 1 FROM guild_content c WHERE
                (c.SensitivityCode=1 AND NOT EXISTS(SELECT 1 FROM artifact_sensitivity l
                  WHERE l.ArtifactKindCode=17 AND l.ArtifactId=c.ContentId AND l.ArtifactRevision=c.Revision
                    AND l.ArtifactContentDigest=c.Digest AND l.SessionId IS c.SessionId AND l.CampaignId IS c.CampaignId
                    AND l.SensitivityCode=c.SensitivityCode AND l.ProvenanceModeCode=c.ProvenanceModeCode
                    AND l.ExactGenerationIds IS c.ExactGenerationIds AND l.GenerationBloom IS c.GenerationBloom))
                OR (c.SensitivityCode=0 AND EXISTS(SELECT 1 FROM artifact_sensitivity l
                  WHERE l.ArtifactKindCode=17 AND lower(replace(l.ArtifactId,'-',''))=lower(replace(c.ContentId,'-','')))))
              OR EXISTS(SELECT 1 FROM guild_content c JOIN guild_content_erasure_receipts e
                ON e.ContentId=c.ContentId OR (e.ContentKindCode=c.ContentKindCode AND e.SourceRecordId=c.SourceRecordId AND e.Revision=c.Revision))
              OR EXISTS(SELECT 1 FROM artifact_sensitivity l WHERE l.ArtifactKindCode=17 AND NOT EXISTS(
                SELECT 1 FROM guild_content c WHERE c.ContentId=l.ArtifactId AND c.SensitivityCode=1))
            """;

        if (!Equals(0L, await command.ExecuteScalarAsync(ct).ConfigureAwait(false)))
        {
            throw new GuildStorageIntegrityException();
        }

        // Frozen pre-Signet catalogs are still admitted by historical upgrade fixtures. At the
        // current head the manifest inspector independently requires every Signet object.
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='guild_signets'";

        if (!Equals(1L, await command.ExecuteScalarAsync(ct).ConfigureAwait(false)))
        {
            return;
        }

        command.CommandText = "SELECT CASE WHEN typeof(Epoch)='text' AND length(CAST(Epoch AS BLOB))=36 THEN Epoch ELSE '' END FROM guild_signet_authority_state WHERE StateKey=1";

        object? epoch = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

        if (epoch is not string text || !Guid.TryParseExact(text, "D", out Guid parsed) || parsed == Guid.Empty
            || text != parsed.ToString("D").ToUpperInvariant())
        {
            throw new GuildStorageIntegrityException();
        }

        command.CommandText = """
            WITH RECURSIVE lineage(LeafId,SignetId,IssuerSignetId) AS (
                SELECT SignetId,SignetId,IssuerSignetId FROM guild_signets
                UNION
                SELECT l.LeafId,p.SignetId,p.IssuerSignetId FROM lineage l
                JOIN guild_signets p ON p.SignetId=l.IssuerSignetId
            )
            SELECT EXISTS(SELECT 1 FROM guild_signets child WHERE
                (child.IssuerSignetId IS NOT NULL AND NOT EXISTS(
                    SELECT 1 FROM guild_signets parent WHERE parent.SignetId=child.IssuerSignetId
                    AND parent.RootCommissionId=child.RootCommissionId
                    AND parent.CampaignId IS child.CampaignId
                    AND parent.WorkspaceId IS child.WorkspaceId
                    AND parent.InstallationGeneration=child.InstallationGeneration
                    AND parent.AuthorityEpoch=child.AuthorityEpoch))
                OR NOT EXISTS(SELECT 1 FROM lineage l JOIN guild_signets root ON root.SignetId=l.SignetId
                    WHERE l.LeafId=child.SignetId AND l.IssuerSignetId IS NULL
                    AND root.CommissionId=child.RootCommissionId)
                OR (child.RestoredInert=0 AND NOT EXISTS(SELECT 1 FROM guild_signet_authority_state state
                    WHERE state.StateKey=1 AND state.Epoch=child.AuthorityEpoch)))
            """;

        if (!Equals(0L, await command.ExecuteScalarAsync(ct).ConfigureAwait(false)))
        {
            throw new GuildStorageIntegrityException();
        }
    }
}

internal sealed class GuildStorageIntegrityException() : Exception("Guild storage relations are inconsistent.");
