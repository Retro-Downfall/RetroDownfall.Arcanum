using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    /// <summary>Releases only the current question text; the execution checkpoint and plan stay private.</summary>
    internal async Task<Result<GuildQuestInputDto>> ReadLocalQuestionAsync(GuildCallerContext caller, Guid questId, CancellationToken ct)
    {
        if (caller is null || questId == Guid.Empty) return OwnerUnavailable;

        Result<GuildQuestStored> located = await GetAsync(caller, questId, ct).ConfigureAwait(false);

        if (located.IsFailure) return located.Error;

        return await authority.InQuestTransactionAsync<GuildQuestInputDto>(located.Value.Quest.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                Result<GuildQuestStored> found = await ReadWithinAsync(connection, transaction, questId, token).ConfigureAwait(false);

                if (found.IsFailure) return found.Error;

                Result visible = await RequireVisibleAsync(connection, transaction, caller, found.Value, token).ConfigureAwait(false);

                if (visible.IsFailure) return visible.Error;

                if (caller.Execution is { } execution)
                {
                    Result<GuildGrant> live = await signets.VerifyAsync(execution, token).ConfigureAwait(false);

                    if (live.IsFailure) return live.Error;
                }

                GuildQuestDto quest = found.Value.Quest;

                // The metadata read admits only a live wait or the exact proven AwaitingInput
                // park. Its reconciliation flag stays set until an answer is accepted.
                if (quest.State != QuestState.InputRequired || quest.AwaitingInput is not { } input)
                    return new Error(ErrorCodes.Guild.NotFound, "The Quest has no current input question.");

                Result<GuildLocalExecutionBinding> binding = await GuildLocalExecutionStore.ReadWithinAsync(connection, transaction, questId, token).ConfigureAwait(false);

                if (binding.IsFailure) return binding.Error;

                if (binding.Value.Input.ContentErased)
                    return new Error(ErrorCodes.Guild.ContentErased, "The accepted local input was erased.");

                Result frontier = await RequireWaitFrontierWithinAsync(connection, transaction, questId, token).ConfigureAwait(false);

                if (frontier.IsFailure) return frontier.Error;

                GuildDataRef reference;

                await using (SqliteCommand read = Command(connection, transaction, $"""
                    SELECT w.ContentId,w.Digest,w.ByteLength,
                        EXISTS(SELECT 1 FROM guild_content_erasure_receipts erased WHERE erased.ContentId=w.ContentId)
                    FROM guild_local_wait_checkpoints w
                    WHERE w.QuestId=$quest AND w.QuestionId=$question AND w.ContinuationId=$continuation AND {WaitCheckpointProof}
                    """))
                {
                    Add(read, "$quest", Id(questId));

                    Add(read, "$question", Id(input.QuestionId));

                    Add(read, "$continuation", Id(input.ContinuationId));

                    await using SqliteDataReader reader = await read.ExecuteReaderAsync(token).ConfigureAwait(false);

                    if (!await reader.ReadAsync(token).ConfigureAwait(false)) return Unavailable;

                    reference = new(Guid.Parse(reader.GetString(0)), GuildContentKind.ExecutionCheckpoint, 1,
                        Convert.ToHexString((byte[])reader.GetValue(1)), "application/json", reader.GetInt64(2), reader.GetBoolean(3));
                }

                if (reference.ContentErased)
                    return new Error(ErrorCodes.Guild.ContentErased, "The current question body was erased.");

                Result<string> content = await ReadProviderContentWithinAsync(connection, transaction, reference, token).ConfigureAwait(false);

                if (content.IsFailure) return content.Error;

                try
                {
                    GuildLocalWaitSnapshot? snapshot = JsonSerializer.Deserialize(content.Value, GuildStorageJsonContext.Default.GuildLocalWaitSnapshot);

                    if (!ValidWaitSnapshot(snapshot, binding.Value)
                        || JsonSerializer.Serialize(snapshot, GuildStorageJsonContext.Default.GuildLocalWaitSnapshot) != content.Value)
                        return Unavailable;

                    return new GuildQuestInputDto(questId, quest.Revision, input, snapshot!.Checkpoint.EscalationReason!);
                }
                catch (JsonException)
                {
                    return Unavailable;
                }
            }, ct).ConfigureAwait(false);
    }
}
