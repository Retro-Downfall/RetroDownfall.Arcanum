using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal static partial class GuildLocalExecutionStore
{
    // Bodies and engine rows can be erased independently; immutable owner/replay evidence remains.
    internal const string BindingProof = """
        EXISTS(SELECT 1 FROM guild_quests q JOIN guild_signets s ON s.SignetId=q.SignetId
            WHERE q.Id=b.QuestId AND q.RunId=b.RunId AND q.SessionId=b.SessionId
              AND q.RootQuestId=b.RootQuestId AND q.ParentQuestId IS b.ParentQuestId
              AND s.QuestId=q.Id AND s.RunId=q.RunId AND s.SessionId IS q.SessionId
              AND s.CampaignId IS q.CampaignId AND s.WorkspaceId IS q.WorkspaceId
              AND s.RootQuestId=q.RootQuestId AND s.AgentId=q.AgentId
              AND s.InstallationGeneration=q.InstallationGeneration
              AND ((b.ParentQuestId IS NULL AND b.ParentApprenticeId IS NULL AND b.RootQuestId=b.QuestId AND s.IssuerSignetId IS NULL)
                OR EXISTS(SELECT 1 FROM guild_local_executions parent JOIN guild_quests p ON p.Id=parent.QuestId
                    WHERE parent.QuestId=b.ParentQuestId AND parent.ApprenticeId=b.ParentApprenticeId
                      AND parent.RootQuestId=b.RootQuestId AND p.SignetId=s.IssuerSignetId))
              AND (EXISTS(SELECT 1 FROM guild_content content WHERE content.ContentId=b.InputContentId
                  AND content.ContentKindCode=9 AND content.SourceRecordId=b.QuestId AND content.Revision=b.InputRevision
                  AND content.Digest=b.InputDigest AND content.MimeType='application/json'
                  AND length(content.Body)=b.InputByteLength AND content.SessionId IS q.SessionId AND content.CampaignId IS q.CampaignId)
                OR EXISTS(SELECT 1 FROM guild_content_erasure_receipts content WHERE content.ContentId=b.InputContentId
                  AND content.ContentKindCode=9 AND content.SourceRecordId=b.QuestId AND content.Revision=b.InputRevision
                  AND content.Digest=b.InputDigest AND content.MimeType='application/json'
                  AND content.ByteLength=b.InputByteLength AND content.SessionId IS q.SessionId AND content.CampaignId IS q.CampaignId)))
        """;

    /// <summary>Metadata only. The transaction owner authorizes visibility before calling this seam.</summary>
    internal static async Task<Result<GuildLocalExecutionBinding>> ReadWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid questId, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, $"""
            SELECT b.ApprenticeId,b.RunId,b.SessionId,b.ParentQuestId,b.RootQuestId,b.ParentApprenticeId,
                b.InputContentId,b.InputRevision,b.InputDigest,b.InputByteLength,
                EXISTS(SELECT 1 FROM guild_content_erasure_receipts erased WHERE erased.ContentId=b.InputContentId)
            FROM guild_local_executions b WHERE b.QuestId=$quest AND {BindingProof}
            """, questId);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Missing;

        try
        {
            GuildDataRef input = new(Guid.Parse(reader.GetString(6)), GuildContentKind.ExecutionInput,
                reader.GetInt64(7), Convert.ToHexString((byte[])reader.GetValue(8)), "application/json", reader.GetInt64(9), reader.GetBoolean(10));

            return new GuildLocalExecutionBinding(questId, Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)), OptionalGuid(reader, 3), Guid.Parse(reader.GetString(4)), OptionalGuid(reader, 5), input);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            return Missing;
        }
    }

    /// <summary>
    /// The host's root-gated caller must first verify its exact driver and grant. Labelled bytes additionally
    /// require the supplied current disclosure admission; an absent callback fails closed.
    /// </summary>
    internal static async Task<Result<GuildLocalExecutionInput>> ReadInputWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid questId,
        Func<ArtifactSensitivityLabel, CancellationToken, Task<Result>>? authorizeProtectedContent, CancellationToken ct,
        bool allowTerminalQuestObservation = false)
    {
        Result<GuildLocalExecutionBinding> found = await ReadWithinAsync(connection, transaction, questId, ct).ConfigureAwait(false);

        if (found.IsFailure) return found.Error;

        GuildLocalExecutionBinding binding = found.Value;

        if (binding.Input.ContentErased) return Erased;

        Result live = await RequireLiveAsync(connection, transaction, questId, ct, allowTerminalQuestObservation).ConfigureAwait(false);

        if (live.IsFailure) return live.Error;

        await using (SqliteCommand skeleton = Command(connection, transaction, """
            SELECT a.CheckpointData FROM Apprentices a JOIN guild_quests q ON q.Id=$quest
            WHERE a.Id=$apprentice AND a.SessionId=q.SessionId AND a.CampaignId IS q.CampaignId
                AND length(CAST(a.CheckpointData AS BLOB))<=65536
            """, questId))
        {
            Add(skeleton, "$apprentice", Id(binding.ApprenticeId));

            object? checkpoint = await skeleton.ExecuteScalarAsync(ct).ConfigureAwait(false);

            if (checkpoint is not string json) return Missing;

            var parsed = ApprenticeRepository.DeserializeCheckpoint(json);

            if (parsed?.LaunchRequested != true || parsed.ParentApprenticeId != binding.ParentApprenticeId) return Missing;
        }

        Result<ArtifactSensitivityLabel?> checkedReference = await ValidateReferenceAsync(connection, transaction, binding.Input, ct).ConfigureAwait(false);

        if (checkedReference.IsFailure) return checkedReference.Error;

        if (checkedReference.Value is { } label)
        {
            if (authorizeProtectedContent is null) return new Error(ErrorCodes.Guild.AuthorityUnavailable, "Protected input requires current disclosure admission.");

            Result disclosed = await authorizeProtectedContent(label, ct).ConfigureAwait(false);

            if (disclosed.IsFailure) return disclosed.Error;
        }

        await using SqliteCommand content = Command(connection, transaction,
            "SELECT Body FROM guild_content WHERE ContentId=$content AND typeof(Body)='blob' AND length(Body) BETWEEN 1 AND 65536", questId);

        Add(content, "$content", Id(binding.Input.ContentId));

        if (await content.ExecuteScalarAsync(ct).ConfigureAwait(false) is not byte[] body
            || Convert.ToHexString(SHA256.HashData(body)) != binding.Input.Digest) return Missing;

        try
        {
            GuildLocalExecutionInput? input = JsonSerializer.Deserialize(body, GuildStorageJsonContext.Default.GuildLocalExecutionInput);

            // The generated encoding is the accepted representation. Reject duplicate/extra fields and
            // ambiguous alternative spellings as well as unknown versions before handing input to an engine.
            if (!ValidInput(input) || !body.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(input, GuildStorageJsonContext.Default.GuildLocalExecutionInput)))
                return Missing;

            return input!;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            return Missing;
        }
    }

    internal static async Task<Result<ArtifactSensitivityLabel?>> ValidateReferenceAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildDataRef reference, CancellationToken ct)
    {
        if (reference.ContentId == Guid.Empty || reference.Revision < 1 || reference.Digest is not { Length: 64 }
            || !reference.Digest.All(Uri.IsHexDigit) || reference.ContentErased) return Invalid;

        await using SqliteCommand command = Command(connection, transaction, """
            SELECT SensitivityCode FROM guild_content WHERE ContentId=$content AND ContentKindCode=$kind
                AND Revision=$revision AND Digest=$digest AND MimeType=$mime AND length(Body)=$length
                AND NOT EXISTS(SELECT 1 FROM guild_content_erasure_receipts WHERE ContentId=$content)
            """, Guid.Empty);

        Add(command, "$content", Id(reference.ContentId));

        Add(command, "$kind", (int)reference.Kind);

        Add(command, "$revision", reference.Revision);

        Add(command, "$digest", Convert.FromHexString(reference.Digest));

        Add(command, "$mime", reference.MimeType);

        Add(command, "$length", reference.ByteLength);

        object? stored = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

        if (stored is not long sensitivity) return Erased;

        Result<ArtifactSensitivityLabel?> label = await ArtifactSensitivityLedger.ReadLabelWithinAsync(connection, transaction,
            SensitiveArtifactKind.GuildContent, reference.ContentId, ct).ConfigureAwait(false);

        if (label.IsFailure) return label.Error;

        if (sensitivity == 0 && label.Value is null) return Result<ArtifactSensitivityLabel?>.Success(null);

        if (sensitivity != 1 || label.Value is null) return Missing;

        Result verified = await GuildContentLifecycle.ValidateArtifactAsync(connection, transaction, label.Value, ct).ConfigureAwait(false);

        return verified.IsFailure ? verified.Error : label.Value;
    }

    private static Guid? OptionalGuid(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));
}
