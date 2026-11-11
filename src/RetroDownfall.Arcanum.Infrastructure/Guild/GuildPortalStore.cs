using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Dormant, root-gated durable Portal admission and delivery. No public execution path is registered.</summary>
internal sealed partial class GuildPortalStore(GuildQuestStore quests, SignetAuthority authority, IGuildQuestHostPolicy policy, TimeProvider time)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal async Task<Result<GuildMessageReceipt>> PublishAsync(GuildCallerContext caller, Guid questId, GuildPublishRequest request, CancellationToken ct, GuildChildToolAdmission? toolAdmission = null)
    {
        Result<GuildPublishRequest> normalized = Normalize(request, caller?.Kind == GuildCallerKind.HostReceipt);

        if (normalized.IsFailure) return normalized.Error;

        if (caller is null || questId == Guid.Empty || caller.InstallationGeneration != policy.InstallationGeneration)
            return Forbidden;

        // The initial lookup only discovers immutable linkage. All authority and owner checks run again
        // under the actual root gate; no gate or transaction survives into the second admission.
        Result<GuildQuestStored> located = await authority.InQuestTransactionAsync<GuildQuestStored>(caller.Execution?.RootQuestId ?? questId,
            (connection, transaction, _, token) => GuildQuestStore.ReadWithinAsync(connection, transaction, questId, token), ct).ConfigureAwait(false);

        if (located.IsFailure) return located.Error;

        return await authority.InQuestTransactionAsync<GuildMessageReceipt>(located.Value.Quest.RootQuestId,
            (connection, transaction, signets, token) => PublishWithinAsync(connection, transaction, signets,
                caller, questId, normalized.Value, token, toolAdmission), ct).ConfigureAwait(false);
    }

    private static Result<GuildPublishRequest> Normalize(GuildPublishRequest? request, bool preserveBody = false)
    {
        if (request is null || request.IdempotencyKey == Guid.Empty || request.Address is null || !Enum.IsDefined(request.Kind)
            || !Enum.IsDefined(request.Address.Kind) || request.Body is null || request.MimeType is not ("text/plain" or "application/json")
            || request.ExpectedRevision is < 1) return Invalid;

        if (request.Body.Length > GuildStore.MaximumContentBytes) return TooLarge;

        try
        {
            string body = preserveBody ? request.Body : GuildQuestIntent.NormalizeGoal(request.Body);

            if (StrictUtf8.GetByteCount(body) > GuildStore.MaximumContentBytes) return TooLarge;

            if (request.Address.Kind == GuildCircleAddressKind.Cohort)
            {
                if ((request.Address.CohortId is null || request.Address.CohortId == Guid.Empty) || request.Address.QuestId is not null
                    || request.Address.MembershipVersion is not null) return Invalid;
            }
            else if (request.Address.CohortId is not null || request.Address.MembershipVersion is not null) return Invalid;

            if (request.Address.QuestId == Guid.Empty) return Invalid;

            return request with { Body = body };
        }
        catch (ArgumentException)
        {
            return Invalid;
        }
    }

    internal static async Task<Result<GuildEnvelope>> ReadEnvelopeWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid id, CancellationToken ct)
    {
        try
        {
            await using SqliteCommand command = Command(connection, transaction, """
                SELECT CASE WHEN typeof(e.MetadataJson)='text' AND length(CAST(e.MetadataJson AS BLOB))<=16384 THEN e.MetadataJson ELSE NULL END,
                    e.QuestId,e.RootQuestId,e.SessionId,e.CampaignId,e.KindCode,e.ContentId,e.SenderSequence,e.CreatedAtUtc,
                    s.ProducerKindCode,s.ProducerKey,e.ProducerRequestId,
                    EXISTS(SELECT 1 FROM guild_content_erasure_receipts r WHERE r.ContentId=e.ContentId),
                    CASE WHEN typeof(e.QuestId)='text' AND length(CAST(e.QuestId AS BLOB))=36
                        AND typeof(e.RootQuestId)='text' AND length(CAST(e.RootQuestId AS BLOB))=36
                        AND (e.SessionId IS NULL OR (typeof(e.SessionId)='text' AND length(CAST(e.SessionId AS BLOB))=36))
                        AND (e.CampaignId IS NULL OR (typeof(e.CampaignId)='text' AND length(CAST(e.CampaignId AS BLOB))=36))
                        AND typeof(e.KindCode)='integer' AND e.KindCode BETWEEN 0 AND 5
                        AND typeof(e.ContentId)='text' AND length(CAST(e.ContentId AS BLOB))=36
                        AND typeof(e.SenderSequence)='integer' AND e.SenderSequence>=1
                        AND typeof(e.CreatedAtUtc)='text' AND length(CAST(e.CreatedAtUtc AS BLOB)) BETWEEN 1 AND 64
                        AND typeof(s.ProducerKindCode)='integer' AND s.ProducerKindCode BETWEEN 0 AND 2
                        AND typeof(s.ProducerKey)='text' AND length(CAST(s.ProducerKey AS BLOB)) BETWEEN 1 AND 4096
                        AND typeof(e.ProducerRequestId)='text' AND length(CAST(e.ProducerRequestId AS BLOB)) BETWEEN 1 AND 4096
                        THEN 1 ELSE 0 END
                FROM guild_envelopes e JOIN guild_portal_streams s ON s.StreamId=e.StreamId AND s.QuestId=e.QuestId WHERE e.Id=$id
                """);

            Add(command, "$id", Id(id));

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return new Error(ErrorCodes.Guild.NotFound, "The Portal envelope is unavailable.");

            // Retained rows can violate their original CHECKs. Validate every selected scalar in
            // SQLite before a provider getter can allocate its managed representation.
            if (reader.IsDBNull(0) || reader.GetInt64(13) != 1) return Unavailable;

            GuildEnvelope? envelope = JsonSerializer.Deserialize(reader.GetString(0), GuildJsonContext.Default.GuildEnvelope);

            if (envelope is null || envelope.Producer is null || envelope.ContentRef is null || envelope.Address is null || envelope.Id != id || envelope.QuestId != Guid.Parse(reader.GetString(1))
                || envelope.RootQuestId != Guid.Parse(reader.GetString(2))
                || Id(envelope.SessionId) != (reader.IsDBNull(3) ? null : reader.GetString(3))
                || Id(envelope.CampaignId) != (reader.IsDBNull(4) ? null : reader.GetString(4))
                || (int)envelope.Kind != reader.GetInt32(5) || Id(envelope.ContentRef.ContentId) != reader.GetString(6)
                || envelope.SenderSequence != reader.GetInt64(7) || envelope.CreatedAt != UtcInstantText.Parse(reader.GetString(8))
                || (int)envelope.Producer.Kind != reader.GetInt32(9) || ProducerKey(envelope.Producer) != reader.GetString(10)
                || envelope.Producer.Kind == GuildProducerKind.HostReceipt && Id(envelope.Producer.ReceiptId) != reader.GetString(11)
                || envelope.ContentRef.ByteLength is < 0 or > GuildStore.MaximumContentBytes || envelope.ContentRef.Revision < 1
                || envelope.ContentRef.Digest is not { Length: 64 }) return Unavailable;

            _ = envelope.Producer.StreamKey(envelope.QuestId);

            return envelope with { ContentRef = envelope.ContentRef with { ContentErased = reader.GetBoolean(12) } };
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException or InvalidCastException or OverflowException)
        {
            return Unavailable;
        }
    }

    private static string ProducerKey(GuildProducerIdentity producer) => producer.Kind switch
    {
        GuildProducerKind.Run => Id(producer.AgentId!.Value) + ":" + Id(producer.RunId!.Value),
        GuildProducerKind.Operator => producer.OperatorPrincipalId!,
        GuildProducerKind.HostReceipt => Id(producer.DomainOwnerId!.Value) + ":" + ((int)producer.SourceKind!.Value).ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => throw new ArgumentOutOfRangeException(nameof(producer)),
    };

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        return command;
    }

    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string Id(Guid value) => value.ToString("D").ToUpperInvariant();

    private static string? Id(Guid? value) => value is { } id ? Id(id) : null;

    private static Error Forbidden => new(ErrorCodes.Guild.Forbidden, "The caller cannot address this Portal relationship.");

    private static Error Invalid => new(ErrorCodes.Guild.InvalidRequest, "The Portal intent is invalid.");

    private static Error TooLarge => new(ErrorCodes.Guild.PayloadTooLarge, "The Portal payload exceeds its representation bound.");

    private static Error Unavailable => new(ErrorCodes.Guild.AuthorityUnavailable, "The Portal evidence is unavailable or inconsistent.");

    private static Error Conflict => new(ErrorCodes.Guild.IdempotencyConflict, "The Portal publication identity already binds different intent.");

    private static Error Exhausted => new(ErrorCodes.Guild.SequenceExhausted, "The Portal sequence is exhausted.");
}
