using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Same-transaction identity/content participant, never an execution or authority owner.</summary>
internal static partial class GuildLocalExecutionStore
{
    private static readonly Error Invalid = new(ErrorCodes.Guild.AuthorityMismatch, "The local execution input does not match its accepted Quest.");

    private static readonly Error Conflict = new(ErrorCodes.Guild.IdempotencyConflict, "The local execution identity already has different accepted input.");

    private static readonly Error Missing = new(ErrorCodes.Guild.ReconciliationRequired, "The local execution binding is missing or inconsistent.");

    private static readonly Error Erased = new(ErrorCodes.Guild.ContentErased, "The accepted local execution input was erased.");

    internal static async Task<Result<GuildLocalExecutionBinding>> CreateWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildQuestStored quest, GuildLocalExecutionInput input,
        ArtifactSensitivityLabel? sourceLabel, DateTimeOffset now, CancellationToken ct)
    {
        if (quest is null || !ValidInput(input) || now == default || input.Goal != quest.Quest.GoalRef)
            return Invalid;

        byte[] body = JsonSerializer.SerializeToUtf8Bytes(input, GuildStorageJsonContext.Default.GuildLocalExecutionInput);

        if (body.Length > GuildStore.MaximumContentBytes)
            return new Error(ErrorCodes.Guild.PayloadTooLarge, "The local execution input exceeds the content representation limit.");

        Result canonical = await RequireCanonicalAsync(connection, transaction, quest, ct).ConfigureAwait(false);

        if (canonical.IsFailure) return canonical.Error;

        byte[] digest = SHA256.HashData(body);

        await using (SqliteCommand existing = Command(connection, transaction, "SELECT EXISTS(SELECT 1 FROM guild_local_executions WHERE QuestId=$quest)", quest.Quest.Id))
        {
            if (Equals(1L, await existing.ExecuteScalarAsync(ct).ConfigureAwait(false)))
            {
                Result<GuildLocalExecutionBinding> prior = await ReadWithinAsync(connection, transaction, quest.Quest.Id, ct).ConfigureAwait(false);

                return prior.IsFailure ? prior.Error : prior.Value.Input.Digest == Convert.ToHexString(digest) ? prior.Value : Conflict;
            }
        }

        Result owner = await GuildStore.RequireOwnerWithinAsync(connection, transaction, quest.Quest.SessionId, quest.Quest.CampaignId, ct).ConfigureAwait(false);

        if (owner.IsFailure) return owner.Error;

        Result live = await RequireLiveAsync(connection, transaction, quest.Quest.Id, ct).ConfigureAwait(false);

        if (live.IsFailure) return live.Error;

        await using (SqliteCommand started = Command(connection, transaction, "SELECT EXISTS(SELECT 1 FROM guild_dispatches WHERE QuestId=$quest)", quest.Quest.Id))
        {
            if (!Equals(0L, await started.ExecuteScalarAsync(ct).ConfigureAwait(false))) return Missing;
        }

        Guid? parentApprentice = null;

        if (quest.Quest.ParentQuestId is { } parentId)
        {
            Result<GuildLocalExecutionBinding> parent = await ReadWithinAsync(connection, transaction, parentId, ct).ConfigureAwait(false);

            if (parent.IsFailure || parent.Value.RootQuestId != quest.Quest.RootQuestId) return Missing;

            parentApprentice = parent.Value.ApprenticeId;
        }

        GenerationProvenance provenance = sourceLabel?.Provenance ?? GenerationProvenance.CreateExact([]);

        ContentSensitivity sensitivity = sourceLabel?.Sensitivity ?? ContentSensitivity.None;

        foreach (GuildDataRef reference in input.Files.Select(file => file.Content).Prepend(input.Goal))
        {
            Result<ArtifactSensitivityLabel?> inherited = await ValidateReferenceAsync(connection, transaction, reference, ct).ConfigureAwait(false);

            if (inherited.IsFailure) return inherited.Error;

            if (inherited.Value is { } label)
            {
                provenance = provenance.Merge(label.Provenance);

                sensitivity |= label.Sensitivity;
            }
        }

        Guid contentId = Guid.NewGuid();

        DerivedArtifactWrite artifact = new(SensitiveArtifactKind.GuildContent, contentId,
            quest.Quest.SessionId, quest.Quest.CampaignId, sourceLabel?.TurnId, 1, new CovenantDigest(digest), sensitivity,
            provenance, sourceLabel?.ProducingPlanDigest, sourceLabel?.ProducingAdmissionDigest, sourceLabel?.ProducingMaintenanceReceiptDigest);

        Result<GuildDataRef> content = await GuildStore.WriteContentWithinAsync(connection, transaction,
            new(contentId, quest.Quest.Id, GuildContentKind.ExecutionInput, 1, quest.Quest.SessionId,
                quest.Quest.CampaignId, "application/json", body, artifact), now, ct).ConfigureAwait(false);

        if (content.IsFailure) return content.Error;

        Guid apprenticeId = Guid.NewGuid();

        ApprenticeCheckpoint checkpoint = new() { ParentApprenticeId = parentApprentice, LaunchRequested = true, Timestamp = now };

        // This insert deliberately shares the Quest's admitted writer transaction. The skeleton carries
        // no goal, selected files, provider output or reasoning; the engine loads the protected references.
        await using (SqliteCommand skeleton = Command(connection, transaction, """
            INSERT INTO Apprentices(Id,CampaignId,Name,Goal,Plan,CurrentStep,Status,SessionId,WorkspacePath,
                CheckpointData,CreatedAt,UpdatedAt)
            VALUES($apprentice,$campaign,'Guild Quest','Guild-owned execution.','[]',0,'Idle',$session,'',$checkpoint,$now,$now)
            """, quest.Quest.Id))
        {
            Add(skeleton, "$apprentice", Id(apprenticeId));

            Add(skeleton, "$campaign", Id(quest.Quest.CampaignId));

            Add(skeleton, "$session", Id(quest.Quest.SessionId));

            Add(skeleton, "$checkpoint", JsonSerializer.Serialize(checkpoint, ArcanumCoreJsonContext.Default.ApprenticeCheckpoint));

            Add(skeleton, "$now", UtcInstantText.Format(now));

            _ = await skeleton.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using SqliteCommand insert = Command(connection, transaction, """
            INSERT INTO guild_local_executions(QuestId,ApprenticeId,RunId,SessionId,ParentQuestId,RootQuestId,
                ParentApprenticeId,InputContentId,InputRevision,InputDigest,InputByteLength)
            VALUES($quest,$apprentice,$run,$session,$parent,$root,$parentApprentice,$content,1,$digest,$length)
            """, quest.Quest.Id);

        Add(insert, "$apprentice", Id(apprenticeId));

        Add(insert, "$run", Id(quest.Quest.RunId));

        Add(insert, "$session", Id(quest.Quest.SessionId));

        Add(insert, "$parent", Id(quest.Quest.ParentQuestId));

        Add(insert, "$root", Id(quest.Quest.RootQuestId));

        Add(insert, "$parentApprentice", Id(parentApprentice));

        Add(insert, "$content", Id(contentId));

        Add(insert, "$digest", digest);

        Add(insert, "$length", body.Length);

        _ = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return new GuildLocalExecutionBinding(quest.Quest.Id, apprenticeId, quest.Quest.RunId,
            quest.Quest.SessionId!.Value, quest.Quest.ParentQuestId, quest.Quest.RootQuestId, parentApprentice, content.Value);
    }

    private static bool ValidInput(GuildLocalExecutionInput? input) => input is { Version: 1, Goal: not null }
        && Enum.IsDefined(input.Mode) && input.Goal.Kind == GuildContentKind.QuestGoal
        && !string.IsNullOrWhiteSpace(input.Provider) && !string.IsNullOrWhiteSpace(input.Model)
        && !input.Files.IsDefault && input.Files.All(file => file is not null && !string.IsNullOrWhiteSpace(file.Name) && file.Content is not null)
        && input.MaxOutputTokens is null or > 0;

    private static async Task<Result> RequireCanonicalAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildQuestStored supplied, CancellationToken ct)
    {
        GuildQuestDto quest = supplied.Quest;

        await using SqliteCommand command = Command(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM guild_quests q JOIN guild_signets s ON s.SignetId=q.SignetId
                WHERE q.Id=$quest AND q.RunId=$run AND q.SessionId=$session AND q.RootQuestId=$root
                  AND q.ParentQuestId IS $parent AND q.CampaignId IS $campaign AND q.WorkspaceId IS $workspace
                  AND q.SignetId=$signet AND q.OperationId=$operation AND q.AgentId=$agent
                  AND q.InstallationGeneration=$generation AND q.GoalContentId=$goal AND q.GoalRevision=$revision
                  AND q.GoalDigest=$digest AND q.GoalMimeType=$mime AND q.GoalByteLength=$length
                  AND s.QuestId=q.Id AND s.RunId=q.RunId AND s.SessionId IS q.SessionId
                  AND s.CampaignId IS q.CampaignId AND s.WorkspaceId IS q.WorkspaceId
                  AND s.RootQuestId=q.RootQuestId AND s.AgentId=q.AgentId
                  AND s.InstallationGeneration=q.InstallationGeneration
                  AND EXISTS(SELECT 1 FROM guild_quest_start_intents intent WHERE intent.QuestId=q.Id)
                  AND ((q.ParentQuestId IS NULL AND q.RootQuestId=q.Id AND s.IssuerSignetId IS NULL)
                    OR EXISTS(SELECT 1 FROM guild_quests parent WHERE parent.Id=q.ParentQuestId
                        AND parent.RootQuestId=q.RootQuestId AND parent.SignetId=s.IssuerSignetId)))
            """, quest.Id);

        Add(command, "$run", Id(quest.RunId));

        Add(command, "$session", Id(quest.SessionId));

        Add(command, "$root", Id(quest.RootQuestId));

        Add(command, "$parent", Id(quest.ParentQuestId));

        Add(command, "$campaign", Id(quest.CampaignId));

        Add(command, "$workspace", Id(quest.WorkspaceId));

        Add(command, "$signet", Id(quest.SignetId));

        Add(command, "$operation", quest.OperationId.ToString("N"));

        Add(command, "$agent", Id(quest.AgentId));

        Add(command, "$generation", Id(supplied.Issuance.InstallationGeneration));

        Add(command, "$goal", Id(quest.GoalRef.ContentId));

        Add(command, "$revision", quest.GoalRef.Revision);

        Add(command, "$digest", Convert.FromHexString(quest.GoalRef.Digest));

        Add(command, "$mime", quest.GoalRef.MimeType);

        Add(command, "$length", quest.GoalRef.ByteLength);

        return Equals(1L, await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) ? Result.Success() : Invalid;
    }

    private static async Task<Result> RequireLiveAsync(SqliteConnection connection, SqliteTransaction transaction, Guid questId, CancellationToken ct,
        bool allowTerminalQuestObservation = false)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            WITH RECURSIVE lineage(Id,ParentQuestId,SignetId,Retired,CancellationRequested,SessionId,CampaignId,WorkspaceId,OperationId) AS (
                SELECT Id,ParentQuestId,SignetId,Retired,CancellationRequested,SessionId,CampaignId,WorkspaceId,OperationId FROM guild_quests WHERE Id=$quest
                UNION SELECT parent.Id,parent.ParentQuestId,parent.SignetId,parent.Retired,parent.CancellationRequested,
                    parent.SessionId,parent.CampaignId,parent.WorkspaceId,parent.OperationId FROM guild_quests parent JOIN lineage child ON child.ParentQuestId=parent.Id)
            SELECT EXISTS(SELECT 1 FROM lineage) AND NOT EXISTS(SELECT 1 FROM lineage q
                LEFT JOIN guild_signets s ON s.SignetId=q.SignetId
                LEFT JOIN LongRunningOperations operation ON operation.Id=q.OperationId
                WHERE q.Retired<>0 OR s.SignetId IS NULL OR s.Revoked<>0
                  OR s.RestoredInert<>0 OR s.Retired<>0 OR operation.Id IS NULL
                  OR ((q.CancellationRequested<>0 OR s.CancellationRequested<>0 OR operation.State IN(3,4,5,6))
                      AND NOT($observeTerminal=1 AND q.Id=$quest AND operation.State IN(4,5,6)))
                  OR NOT EXISTS(SELECT 1 FROM guild_signet_authority_state epoch WHERE epoch.Epoch=s.AuthorityEpoch)
                  OR NOT EXISTS(SELECT 1 FROM Sessions session WHERE session.Id=q.SessionId AND session.CampaignId IS q.CampaignId)
                  OR (q.CampaignId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM Campaigns campaign WHERE campaign.Id=q.CampaignId))
                  OR (q.WorkspaceId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM Campaigns workspace WHERE workspace.Id=q.WorkspaceId))
                  OR EXISTS(SELECT 1 FROM owner_deletion_events retired WHERE
                      (retired.OwnerKindCode=2 AND retired.OwnerId=q.SessionId)
                      OR (retired.OwnerKindCode=1 AND (retired.OwnerId=q.CampaignId OR retired.OwnerId=q.WorkspaceId)))
                  OR EXISTS(SELECT 1 FROM guild_owner_cleanup retired WHERE
                      (retired.OwnerKindCode=2 AND retired.OwnerId=q.SessionId)
                      OR (retired.OwnerKindCode=1 AND (retired.OwnerId=q.CampaignId OR retired.OwnerId=q.WorkspaceId))))
            """, questId);

        Add(command, "$observeTerminal", allowTerminalQuestObservation ? 1 : 0);

        return Equals(1L, await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) ? Result.Success() : Invalid;
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql, Guid questId)
    {
        SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        Add(command, "$quest", Id(questId));

        return command;
    }

    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string? Id(Guid? id) => id?.ToString("D").ToUpperInvariant();
}
