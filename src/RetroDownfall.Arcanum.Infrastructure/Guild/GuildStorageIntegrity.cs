using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Admission-time relational verification; ordinary reads validate their exact rows separately.</summary>
internal static class GuildStorageIntegrity
{
    internal static async Task ValidateAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct, int declaredSchemaVersion = 3)
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
            if (declaredSchemaVersion < 2)
            {
                return;
            }

            throw new GuildStorageIntegrityException();
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
                    AND parent.RootQuestId=child.RootQuestId
                    AND parent.CampaignId IS child.CampaignId
                    AND parent.WorkspaceId IS child.WorkspaceId
                    AND parent.InstallationGeneration=child.InstallationGeneration
                    AND parent.AuthorityEpoch=child.AuthorityEpoch))
                OR NOT EXISTS(SELECT 1 FROM lineage l JOIN guild_signets root ON root.SignetId=l.SignetId
                    WHERE l.LeafId=child.SignetId AND l.IssuerSignetId IS NULL
                    AND root.QuestId=child.RootQuestId)
                OR (child.RestoredInert=0 AND NOT EXISTS(SELECT 1 FROM guild_signet_authority_state state
                    WHERE state.StateKey=1 AND state.Epoch=child.AuthorityEpoch)))
            """;

        // Historical installers validate the frozen v2 Signet columns before the v3 rename.
        // Both spellings are fixed schema identifiers; no caller data enters this substitution.
        if (declaredSchemaVersion < 3)
        {
            command.CommandText = command.CommandText.Replace("QuestId", "CommissionId", StringComparison.Ordinal);
        }

        if (!Equals(0L, await command.ExecuteScalarAsync(ct).ConfigureAwait(false)))
        {
            throw new GuildStorageIntegrityException();
        }

        if (declaredSchemaVersion >= 3)
        {
            await ValidateQuestsAsync(connection, transaction, ct).ConfigureAwait(false);
        }

        if (declaredSchemaVersion >= 6)
        {
            await ValidateMemoryAttributionAsync(connection, transaction, ct).ConfigureAwait(false);
        }
    }

    private static async Task ValidateMemoryAttributionAsync(SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM guild_memory_attribution attribution WHERE
                NOT EXISTS(SELECT 1 FROM annal_versions version
                    JOIN annal_claims claim ON claim.ClaimId=version.ClaimId
                    WHERE version.VersionId=attribution.ImmutableVersionId
                        AND claim.SubjectStoreCode=attribution.StoreCode AND claim.SubjectId=attribution.SubjectId)
                OR NOT EXISTS(SELECT 1 FROM guild_signets signet WHERE signet.SignetId=attribution.SignetId
                    AND signet.AgentId=attribution.AgentId AND signet.RunId=attribution.RunId AND signet.QuestId=attribution.QuestId)
                OR (attribution.StoreCode=1 AND NOT EXISTS(SELECT 1 FROM saga_memories memory WHERE memory.Id=attribution.SubjectId))
                OR (attribution.StoreCode=2 AND NOT EXISTS(SELECT 1 FROM lexicon_entries entry WHERE entry.Id=attribution.SubjectId)))
            """;

        if (!Equals(0L, await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)))
        {
            throw new GuildStorageIntegrityException();
        }
    }

    private static async Task ValidateQuestsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN(
                'guild_quests','guild_dispatches','guild_result_receipts','guild_result_outbox',
                'guild_result_applications','guild_result_inputs','guild_quest_start_intents','guild_continuations')
            """;

        if (!Equals(8L, await command.ExecuteScalarAsync(ct).ConfigureAwait(false)))
        {
            throw new GuildStorageIntegrityException();
        }

        string goal = ContentProof("c.GoalContentId", "c.Id", "c.GoalRevision", "c.GoalDigest", 1, "c.GoalMimeType", "c.GoalByteLength");

        string result = ContentProof("r.ContentId", "r.ResultId", "r.ContentRevision", "r.Digest", 2, "r.MimeType", "r.ByteLength");

        string answer = ContentProof("answer.ContentId", "answer.ContinuationId", "1", null, 3);

        command.CommandText = $"""
            SELECT
              EXISTS(SELECT 1 FROM guild_quests c WHERE
                NOT EXISTS(SELECT 1 FROM LongRunningOperations operation WHERE operation.Id=c.OperationId
                    AND operation.Kind='guild-quest' AND operation.RecoveryPolicy=2 AND operation.CheckpointVersion=1
                    AND operation.SessionId IS c.SessionId AND operation.RunId IS c.RunId)
                OR NOT EXISTS(SELECT 1 FROM guild_signets signet WHERE signet.SignetId=c.SignetId
                    AND signet.QuestId=c.Id AND signet.RootQuestId=c.RootQuestId
                    AND signet.AgentId=c.AgentId AND signet.RunId=c.RunId AND signet.SessionId IS c.SessionId
                    AND signet.CampaignId IS c.CampaignId AND signet.WorkspaceId IS c.WorkspaceId
                    AND signet.InstallationGeneration=c.InstallationGeneration
                    AND ((c.ParentQuestId IS NULL AND signet.IssuerSignetId IS NULL)
                         OR EXISTS(SELECT 1 FROM guild_quests parent WHERE parent.Id=c.ParentQuestId AND parent.SignetId=signet.IssuerSignetId)))
                OR (c.ParentQuestId IS NULL AND (c.RootQuestId<>c.Id OR c.IssuerKindCode<>0))
                OR (c.ParentQuestId IS NOT NULL AND (c.IssuerKindCode<>1 OR c.IssuerIdentity<>c.ParentQuestId
                    OR NOT EXISTS(SELECT 1 FROM guild_quests parent WHERE parent.Id=c.ParentQuestId
                        AND parent.RootQuestId=c.RootQuestId AND parent.InstallationGeneration=c.InstallationGeneration
                        AND parent.CampaignId IS c.CampaignId AND parent.WorkspaceId IS c.WorkspaceId)))
                OR NOT ({goal}))
              OR EXISTS(SELECT 1 FROM LongRunningOperations operation WHERE operation.Kind='guild-quest'
                  AND NOT EXISTS(SELECT 1 FROM guild_quests c WHERE c.OperationId=operation.Id))
              OR EXISTS(SELECT 1 FROM guild_quests c JOIN LongRunningOperations operation ON operation.Id=c.OperationId
                  WHERE operation.State IN(4,5,6) AND NOT EXISTS(SELECT 1 FROM guild_result_receipts terminal WHERE terminal.QuestId=c.Id))
              OR EXISTS(SELECT 1 FROM guild_dispatches dispatch WHERE NOT EXISTS(SELECT 1 FROM guild_quests c WHERE c.Id=dispatch.QuestId))
              OR EXISTS(SELECT 1 FROM guild_result_receipts r LEFT JOIN guild_quests c ON c.Id=r.QuestId WHERE c.Id IS NULL
                  OR r.AcceptedRevision>c.Revision OR (r.OutcomeCode=3 AND r.ContentId IS NULL)
                  OR (r.ContentId IS NOT NULL AND NOT ({result}))
                  OR NOT EXISTS(SELECT 1 FROM guild_result_outbox delivery WHERE delivery.QuestId=r.QuestId
                      AND delivery.ResultId=r.ResultId AND delivery.Digest=r.Digest AND delivery.RecipientQuestId IS c.ParentQuestId)
                  OR NOT EXISTS(SELECT 1 FROM LongRunningOperations operation WHERE operation.Id=c.OperationId
                      AND operation.TerminalErrorCode IS r.ReasonCode
                      AND ((r.OutcomeCode IN(3,5) AND operation.State=4) OR (r.OutcomeCode IN(4,6) AND operation.State IN(5,6)))))
              OR EXISTS(SELECT 1 FROM guild_result_outbox delivery LEFT JOIN guild_result_receipts r ON r.QuestId=delivery.QuestId
                  WHERE r.ResultId IS NOT delivery.ResultId OR r.Digest IS NOT delivery.Digest)
              OR EXISTS(SELECT 1 FROM guild_result_applications application WHERE
                  NOT EXISTS(SELECT 1 FROM guild_result_receipts r WHERE r.QuestId=application.QuestId AND r.ResultId=application.ResultId AND r.Digest=application.Digest)
                  OR NOT EXISTS(SELECT 1 FROM guild_result_inputs input WHERE input.RecipientQuestId=application.RecipientQuestId
                      AND input.QuestId=application.QuestId AND input.ResultId=application.ResultId AND input.Digest=application.Digest))
              OR EXISTS(SELECT 1 FROM guild_result_inputs input WHERE NOT EXISTS(SELECT 1 FROM guild_result_applications application
                  WHERE application.RecipientQuestId=input.RecipientQuestId AND application.QuestId=input.QuestId
                    AND application.ResultId=input.ResultId AND application.Digest=input.Digest))
              OR EXISTS(SELECT 1 FROM guild_continuations answer LEFT JOIN guild_quests c ON c.Id=answer.QuestId
                  WHERE c.Id IS NULL OR answer.AcceptedRevision>c.Revision OR NOT ({answer}))
            """;

        if (!Equals(0L, await command.ExecuteScalarAsync(ct).ConfigureAwait(false)))
        {
            throw new GuildStorageIntegrityException();
        }

        command.CommandText = """
            SELECT c.Id,operation.CheckpointVersion,
                CASE WHEN length(operation.CheckpointPayload) BETWEEN 1 AND 512 THEN operation.CheckpointPayload ELSE NULL END
            FROM guild_quests c JOIN LongRunningOperations operation ON operation.Id=c.OperationId
            """;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var checkpoint = GuildQuestCheckpoint.Decode(reader.GetInt32(1), reader.GetValue(2) as byte[]);

            if (checkpoint.IsFailure || checkpoint.Value.QuestId != Guid.Parse(reader.GetString(0)))
            {
                throw new GuildStorageIntegrityException();
            }
        }
    }

    private static string ContentProof(string id, string source, string revision, string? digest, int kind,
        string? mime = null, string? length = null)
    {
        string metadata = mime is null ? string.Empty : $" AND body.MimeType={mime}";

        string liveLength = length is null ? string.Empty : $" AND length(body.Body)={length}";

        string erasedLength = length is null ? string.Empty : $" AND body.ByteLength={length}";

        string digestMatch = digest is null ? string.Empty : $" AND body.Digest={digest}";

        string identity = $"body.ContentId={id} AND body.ContentKindCode={kind} AND body.SourceRecordId={source}"
            + $" AND body.Revision={revision}{digestMatch} AND body.SessionId IS c.SessionId AND body.CampaignId IS c.CampaignId{metadata}";

        return $"EXISTS(SELECT 1 FROM guild_content body WHERE {identity}{liveLength})"
            + $" OR EXISTS(SELECT 1 FROM guild_content_erasure_receipts body WHERE {identity}{erasedLength})";
    }
}

internal sealed class GuildStorageIntegrityException() : Exception("Guild storage relations are inconsistent.");
