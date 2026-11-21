using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildPortalStore
{
    private sealed record RemotePortalTarget(GuildRemoteRecoveryTarget Route, string TaskId, string ContextId, byte[] Digest);

    private async Task<Result> PrepareRemoteRecipientsWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildEnvelope envelope, DateTimeOffset now, CancellationToken ct)
    {
        string after = string.Empty;

        bool hasRemoteAudience = false;

        while (true)
        {
            List<(Guid Inbox, Guid Quest)> page = [];

            await using (SqliteCommand read = Command(connection, transaction, """
                SELECT i.Id,i.RecipientQuestId FROM guild_inbox i
                JOIN guild_remote_executions remote ON remote.QuestId=i.RecipientQuestId
                WHERE i.EnvelopeId=$envelope AND i.Id>$after ORDER BY i.Id LIMIT 100
                """))
            {
                Add(read, "$envelope", Id(envelope.Id));

                Add(read, "$after", after);

                await using SqliteDataReader reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);

                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    page.Add((Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1))));
            }

            if (page.Count == 0) break;

            if (!hasRemoteAudience)
            {
                Result clean = await RequireOrdinaryRemoteMessageWithinAsync(connection, transaction, envelope.ContentRef.ContentId, ct)
                    .ConfigureAwait(false);

                if (clean.IsFailure) return clean;

                hasRemoteAudience = true;
            }

            foreach ((Guid inbox, Guid quest) in page)
            {
                var recipient = await GuildQuestStore.ReadWithinAsync(connection, transaction, quest, ct).ConfigureAwait(false);

                if (recipient.IsFailure) return recipient.Error;

                if (recipient.Value.Retired || recipient.Value.Quest.RootQuestId != envelope.RootQuestId) return Forbidden;

                if (ClosedRemoteRecipient(recipient.Value.Quest)) return ClosedRecipient;

                var target = await ReadPortalTargetWithinAsync(connection, transaction, quest, ct).ConfigureAwait(false);

                if (target.IsFailure) return target.Error;

                await using SqliteCommand save = Command(connection, transaction, """
                    INSERT INTO guild_remote_portal_deliveries(InboxId,RecipientQuestId,EnvelopeId,TaskId,ContextId,TargetDigest,TransportStateCode)
                    VALUES($inbox,$quest,$envelope,$task,$context,$target,0)
                    """);

                Add(save, "$inbox", Id(inbox));

                Add(save, "$quest", Id(quest));

                Add(save, "$envelope", Id(envelope.Id));

                Add(save, "$task", target.Value.TaskId);

                Add(save, "$context", target.Value.ContextId);

                Add(save, "$target", target.Value.Digest);

                if (await save.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return Unavailable;
            }

            after = Id(page[^1].Inbox);
        }

        return hasRemoteAudience
            ? await AppendRemoteAudienceEventsWithinAsync(connection, transaction, envelope, now, ct).ConfigureAwait(false)
            : Result.Success();
    }

    private static async Task<Result> AppendRemoteAudienceEventsWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildEnvelope envelope, DateTimeOffset now, CancellationToken ct)
    {
        string after = string.Empty;

        while (true)
        {
            List<(Guid Inbox, Guid Quest)> page = [];

            await using (SqliteCommand read = Command(connection, transaction, """
                SELECT Id,RecipientQuestId FROM guild_inbox
                WHERE EnvelopeId=$envelope AND RecipientQuestId IS NOT NULL AND Id>$after ORDER BY Id LIMIT 100
                """))
            {
                Add(read, "$envelope", Id(envelope.Id));

                Add(read, "$after", after);

                await using SqliteDataReader reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);

                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    page.Add((Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1))));
            }

            if (page.Count == 0) return Result.Success();

            foreach ((Guid inbox, Guid quest) in page)
            {
                var projected = await GuildPortalEvents.AppendWithinAsync(connection, transaction, envelope.RootQuestId, quest,
                    GuildEventKind.MessageCommitted, inbox, 1, envelope.Id, now, ct, GuildPortalEventSource.Inbox).ConfigureAwait(false);

                if (projected.IsFailure) return projected.Error;
            }

            after = Id(page[^1].Inbox);
        }
    }

    private async Task<Result<RemotePortalTarget>> ReadPortalTargetWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid recipient, CancellationToken ct)
    {
        var target = await GuildRemoteExecutionStore.ReadRecoveryWithinAsync(connection, transaction, recipient, ct).ConfigureAwait(false);

        if (target.IsFailure) return target.Error;

        if (!target.Value.Transport.PortalMessagesV1 || policy is not GuildLocalHostPolicy host
            || !host.IsConfiguredPortalDestination(target.Value.DiscoveryEndpoint)) return RemoteMessagingUnavailable;

        var identity = await GuildQuestStore.ReadRemoteTaskIdentityWithinAsync(connection, transaction, recipient, ct).ConfigureAwait(false);

        if (identity.IsFailure || !GuildInboundTaskStore.ValidIdentifier(identity.Value.TaskId)
            || !GuildInboundTaskStore.ValidIdentifier(identity.Value.ContextId)) return RemoteMessagingUnavailable;

        await using SqliteCommand read = Command(connection, transaction, """
            SELECT CASE WHEN typeof(RecoveryDigest)='blob' AND length(RecoveryDigest)=32 THEN RecoveryDigest END
            FROM guild_remote_executions WHERE QuestId=$quest
            """);

        Add(read, "$quest", Id(recipient));

        return await read.ExecuteScalarAsync(ct).ConfigureAwait(false) is byte[] digest
            ? new RemotePortalTarget(target.Value, identity.Value.TaskId, identity.Value.ContextId!, digest) : Unavailable;
    }

    private static async Task<Result> RequireOrdinaryRemoteMessageWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid content, CancellationToken ct)
    {
        var label = await ReadBoundedLabelWithinAsync(connection, transaction, content, ct).ConfigureAwait(false);

        if (label.IsFailure) return label.Error;

        if (label.Value is not null) return RemoteDisclosureUnavailable;

        await using SqliteCommand read = Command(connection, transaction, """
            SELECT 1 FROM guild_content WHERE ContentId=$content AND SensitivityCode=0 AND ProvenanceModeCode=0
              AND ExactGenerationIds IS NULL AND GenerationBloom IS NULL
              AND NOT EXISTS(SELECT 1 FROM guild_content_erasure_receipts erasure WHERE erasure.ContentId=$content)
            """);

        Add(read, "$content", Id(content));

        return await read.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null ? Result.Success() : RemoteDisclosureUnavailable;
    }

    private static Error RemoteMessagingUnavailable => new(ErrorCodes.Guild.RemoteMessagingUnavailable,
        "The remote recipient has no qualified informational message transport for this task.");

    private static Error RemoteDisclosureUnavailable => new(ErrorCodes.Guild.AuthorityUnavailable,
        "The peer transport cannot disclose this protected or unavailable message.");
}
