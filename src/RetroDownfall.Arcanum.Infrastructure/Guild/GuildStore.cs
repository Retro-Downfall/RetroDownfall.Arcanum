using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Admitted, bounded persistence. No public caller or executable authority is minted here.</summary>
internal sealed partial class GuildStore(IGrimoireOrdinaryConnectionFactory connections, IGrimoireConnectionAdmissionGate gate, TimeProvider time)
{
    internal const int MaximumContentBytes = 65_536;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private const string LiveContent = "NOT EXISTS(SELECT 1 FROM guild_content_erasure_receipts e WHERE e.ContentId=c.ContentId OR (e.ContentKindCode=c.ContentKindCode AND e.SourceRecordId=c.SourceRecordId AND e.Revision=c.Revision))";

    private const string LiveOwners = """
        (a.SessionId IS NULL OR (EXISTS(SELECT 1 FROM Sessions s WHERE s.Id=a.SessionId AND s.CampaignId IS a.CampaignId)
          AND NOT EXISTS(SELECT 1 FROM owner_deletion_events d WHERE d.OwnerKindCode=2 AND d.OwnerId=a.SessionId)))
        AND (a.CampaignId IS NULL OR (EXISTS(SELECT 1 FROM Campaigns c WHERE c.Id=a.CampaignId)
          AND NOT EXISTS(SELECT 1 FROM owner_deletion_events d WHERE d.OwnerKindCode=1 AND d.OwnerId=a.CampaignId)))
        AND (a.WorkspaceId IS NULL OR (EXISTS(SELECT 1 FROM Campaigns w WHERE w.Id=a.WorkspaceId)
          AND NOT EXISTS(SELECT 1 FROM owner_deletion_events d WHERE d.OwnerKindCode=1 AND d.OwnerId=a.WorkspaceId)))
        """;

    private const string ReadColumns = "a.SourceIdentity,a.SessionId,a.CampaignId,a.Available,a.ReasonCode,c.ContentId,c.ContentKindCode,c.Revision,c.Digest,c.MimeType,length(c.Body),CASE WHEN c.SensitivityCode<>0 OR EXISTS(SELECT 1 FROM artifact_sensitivity l WHERE l.ArtifactKindCode=17 AND lower(replace(l.ArtifactId,'-',''))=lower(replace(c.ContentId,'-',''))) THEN 1 ELSE 0 END,c.Body,a.CardId,a.AgentId,a.SourceKindCode,a.WorkspaceId,a.PeerId,a.IssuedSequence";

    public Task<Result<GuildStoredCard>> UpsertCardAsync(GuildCardWrite write, CancellationToken ct) =>
        ExecuteAsync(true, (connection, transaction, token) => UpsertWithinAsync(connection, transaction, write, token), ct);

    public Task<Result<GuildStoredCard>> ResolveAsync(Guid agentId, Guid? cardId, long? revision, CancellationToken ct) =>
        ReadOneAsync("a.AgentId=$id AND ($card IS NULL OR a.CardId=$card)", Format(agentId), cardId, revision, null, ct);

    public Task<Result<GuildStoredCard>> ResolveAsync(GuildCardReadScope scope, Guid agentId, Guid? cardId, long? revision, CancellationToken ct) =>
        ReadOneAsync("a.AgentId=$id AND ($card IS NULL OR a.CardId=$card)", Format(agentId), cardId, revision, scope, ct);

    public Task<Result<GuildStoredCard>> GetBySourceAsync(string sourceIdentity, CancellationToken ct) =>
        ReadOneAsync("a.SourceIdentity=$id", sourceIdentity, null, null, null, ct);

    public Task<Result<GuildStoredCard>> GetByExplicitIdAsync(Guid alias, CancellationToken ct) =>
        ReadOneAsync("a.ExplicitAgentId=$id", Format(alias), null, null, null, ct);

    public async Task<Result> RequireScopeAsync(GuildCardReadScope scope, CancellationToken ct)
    {
        Result<bool> result = await ExecuteAsync<bool>(false, async (connection, transaction, token) =>
        {
            Result owner = await RequireOwnerWithinAsync(connection, transaction, scope.SessionId, scope.CampaignId, token).ConfigureAwait(false);

            if (owner.IsFailure)
            {
                return owner.Error;
            }

            Result workspace = await RequireWorkspaceWithinAsync(connection, transaction, scope.WorkspaceId, scope.WorkspaceRoot, token).ConfigureAwait(false);

            return workspace.IsFailure ? Result<bool>.Failure(workspace.Error) : Result<bool>.Success(true);
        }, ct).ConfigureAwait(false);

        return result.IsFailure ? Result.Failure(result.Error) : Result.Success();
    }

    public async Task<Result> SetUnavailableAsync(string sourceIdentity, string reasonCode, CancellationToken ct)
    {
        Result<bool> result = await ExecuteAsync<bool>(true, async (connection, transaction, token) =>
        {
            await using SqliteCommand command = Command(connection, transaction,
                "UPDATE guild_agent_cards SET Available=0,ReasonCode=$reason,UpdatedAtUtc=$now WHERE SourceIdentity=$source");

            Add(command, "$reason", reasonCode);

            Add(command, "$now", UtcInstantText.Format(time.GetUtcNow()));

            Add(command, "$source", sourceIdentity);

            _ = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);

            return Result<bool>.Success(true);
        }, ct).ConfigureAwait(false);

        return result.IsFailure ? Result.Failure(result.Error) : Result.Success();
    }

    public async Task<Result> RebindExplicitAsync(Guid alias, string expectedOldSource, string newSource, long expectedRevision, CancellationToken ct)
    {
        Result<bool> result = await ExecuteAsync<bool>(true, async (connection, transaction, token) =>
        {
            await using SqliteCommand command = Command(connection, transaction,
                "UPDATE guild_agent_cards SET SourceIdentity=$new,UpdatedAtUtc=$now WHERE ExplicitAgentId=$alias AND SourceIdentity=$old AND CurrentRevision=$revision");

            Add(command, "$new", newSource);

            Add(command, "$old", expectedOldSource);

            Add(command, "$alias", Format(alias));

            Add(command, "$revision", expectedRevision);

            Add(command, "$now", UtcInstantText.Format(time.GetUtcNow()));

            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1
                ? Result<bool>.Success(true) : Conflict();
        }, ct).ConfigureAwait(false);

        return result.IsFailure ? Result.Failure(result.Error) : Result.Success();
    }

    public Task<Result<GuildStoredCardPage>> ListAsync(GuildCardReadScope scope, GuildAgentSourceKind? source, string? skill,
        string? afterCardId, int pageSize, CancellationToken ct, long? snapshotSequence = null) =>
        ExecuteAsync<GuildStoredCardPage>(false, async (connection, transaction, token) =>
        {
            if (pageSize is < 1 or > 100 || snapshotSequence < 0)
            {
                return Invalid();
            }

            Result owner = await RequireOwnerWithinAsync(connection, transaction, scope.SessionId, scope.CampaignId, token).ConfigureAwait(false);

            if (owner.IsFailure)
            {
                return owner.Error;
            }

            Result workspace = await RequireWorkspaceWithinAsync(connection, transaction, scope.WorkspaceId, scope.WorkspaceRoot, token).ConfigureAwait(false);

            if (workspace.IsFailure)
            {
                return workspace.Error;
            }

            long afterSequence = 0;

            if (afterCardId is not null)
            {
                int separator = afterCardId.IndexOf(':');

                if (separator < 1 || !long.TryParse(afterCardId.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out afterSequence)
                    || afterSequence < 1 || !Guid.TryParseExact(afterCardId.AsSpan(separator + 1), "D", out _))
                {
                    return new Error(ErrorCodes.Guild.CursorInvalid, "The Guild storage continuation is invalid.");
                }
            }

            await using SqliteCommand high = Command(connection, transaction, "SELECT COALESCE(MAX(IssuedSequence),0) FROM guild_agent_cards");

            long snapshot = snapshotSequence ?? (long)(await high.ExecuteScalarAsync(token).ConfigureAwait(false))!;

            await using SqliteCommand command = Command(connection, transaction, $"""
                SELECT {ReadColumns} FROM guild_agent_cards a
                JOIN guild_agent_card_revisions r ON r.CardId=a.CardId AND r.Revision=a.CurrentRevision
                JOIN guild_content c ON c.ContentId=r.ContentId
                WHERE {LiveOwners} AND {LiveContent} AND {ScopePredicate}
                  AND ($source IS NULL OR a.SourceKindCode=$source)
                  AND ($skill IS NULL OR EXISTS(SELECT 1 FROM guild_agent_card_skills k WHERE k.CardId=a.CardId AND k.Revision=a.CurrentRevision AND k.SkillId=$skill))
                  AND a.IssuedSequence>$after AND a.IssuedSequence<=$snapshot AND c.SensitivityCode=0 AND NOT EXISTS(SELECT 1 FROM artifact_sensitivity l WHERE l.ArtifactKindCode=17 AND lower(replace(l.ArtifactId,'-',''))=lower(replace(c.ContentId,'-','')))
                ORDER BY a.IssuedSequence,a.CardId LIMIT $limit
                """);

            BindScope(command, scope);

            Add(command, "$source", source.HasValue ? (int)source.Value : null);

            Add(command, "$skill", skill);

            Add(command, "$after", afterSequence);

            Add(command, "$snapshot", snapshot);

            Add(command, "$limit", pageSize + 1);

            ImmutableArray<GuildStoredCard>.Builder items = ImmutableArray.CreateBuilder<GuildStoredCard>();

            long bytes = 0;

            string? last = null;

            bool more = false;

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                long size = reader.GetInt64(10) + Encoding.UTF8.GetByteCount(reader.GetString(0)) + 4096;

                if (items.Count == pageSize || (items.Count > 0 && bytes + size > 1_048_576))
                {
                    more = true;

                    break;
                }

                Result<GuildStoredCard> item = Materialize(reader);

                if (item.IsFailure)
                {
                    return item.Error;
                }

                items.Add(item.Value);

                bytes += size;

                last = reader.GetInt64(18).ToString(CultureInfo.InvariantCulture) + ":" + reader.GetString(13);
            }

            return Result<GuildStoredCardPage>.Success(new(items.ToImmutable(), more ? last : null, snapshot));
        }, ct);

    public Task<Result<GuildDataRef>> WriteContentAsync(GuildContentWrite write, CancellationToken ct) =>
        ExecuteAsync(true, (connection, transaction, token) => WriteContentWithinAsync(connection, transaction, write, time.GetUtcNow(), token), ct);

    internal static async Task<Result<GuildDataRef>> WriteContentWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildContentWrite write, DateTimeOffset now, CancellationToken ct)
    {
        if (write.ContentId == Guid.Empty || write.SourceRecordId == Guid.Empty || !Enum.IsDefined(write.Kind)
            || write.Revision < 1 || string.IsNullOrWhiteSpace(write.MimeType) || write.Body is null || write.Sensitivity is null)
        {
            return Invalid();
        }

        if (write.Body.Length > MaximumContentBytes || Encoding.UTF8.GetByteCount(write.MimeType) > MaximumContentBytes)
        {
            return TooLarge();
        }

        if (write.MimeType is not ("text/plain" or "application/json"))
        {
            return Invalid();
        }

        try
        {
            _ = StrictUtf8.GetCharCount(write.Body);
        }
        catch (DecoderFallbackException)
        {
            return Invalid();
        }

        Result available = await GuildStorageAvailability.CheckAsync(connection, transaction, ct).ConfigureAwait(false);

        if (available.IsFailure)
        {
            return available.Error;
        }

        Result owner = await RequireOwnerWithinAsync(connection, transaction, write.SessionId, write.CampaignId, ct).ConfigureAwait(false);

        if (owner.IsFailure)
        {
            return owner.Error;
        }

        await using (SqliteCommand erased = Command(connection, transaction, """
            SELECT 1 FROM guild_content_erasure_receipts WHERE ContentId=$id
            UNION ALL
            SELECT 1 FROM guild_content_erasure_receipts WHERE ContentKindCode=$kind AND SourceRecordId=$source AND Revision=$revision
            LIMIT 1
            """))
        {
            Add(erased, "$id", Format(write.ContentId));

            Add(erased, "$kind", (int)write.Kind);

            Add(erased, "$source", Format(write.SourceRecordId));

            Add(erased, "$revision", write.Revision);

            if (await erased.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null)
            {
                return new Error(ErrorCodes.Guild.ContentErased, "The immutable Guild content revision was erased.");
            }
        }

        byte[] digest = SHA256.HashData(write.Body);

        DerivedArtifactWrite label = write.Sensitivity;

        if (label.ArtifactKind != SensitiveArtifactKind.GuildContent || label.ArtifactId != write.ContentId
            || label.SessionId != write.SessionId || label.CampaignId != write.CampaignId
            || label.ArtifactRevision != (ulong)write.Revision || !label.ArtifactContentDigest.Bytes.AsSpan().SequenceEqual(digest))
        {
            return new Error(ErrorCodes.Covenant.IntegrityFailure, "Guild content sensitivity must bind the exact immutable bytes and owner.");
        }

        if (label.RequiresLabel)
        {
            Result covenant = await GuildStorageAvailability.CheckCovenantAsync(connection, transaction, ct).ConfigureAwait(false);

            if (covenant.IsFailure)
            {
                return covenant.Error;
            }
        }

        await using SqliteCommand command = Command(connection, transaction, """
            INSERT INTO guild_content(ContentId,SessionId,CampaignId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,Body,SensitivityCode,ProvenanceModeCode,ExactGenerationIds,GenerationBloom,CreatedAtUtc)
            VALUES($id,$session,$campaign,$kind,$source,$revision,$digest,$mime,$body,$sensitivity,$mode,$exact,$bloom,$now)
            """);

        Add(command, "$id", Format(write.ContentId));

        Add(command, "$session", Format(write.SessionId));

        Add(command, "$campaign", Format(write.CampaignId));

        Add(command, "$kind", (int)write.Kind);

        Add(command, "$source", Format(write.SourceRecordId));

        Add(command, "$revision", write.Revision);

        Add(command, "$digest", digest);

        Add(command, "$mime", write.MimeType);

        Add(command, "$body", write.Body);

        Add(command, "$sensitivity", (int)label.Sensitivity);

        Add(command, "$mode", label.RequiresLabel ? (int)label.Provenance.Mode : 0);

        Add(command, "$exact", label.RequiresLabel && label.Provenance.Mode == GenerationProvenanceMode.Exact ? label.Provenance.ToCanonicalExactBytes() : null);

        Add(command, "$bloom", label.RequiresLabel && label.Provenance.Mode == GenerationProvenanceMode.BloomOverflow ? label.Provenance.ToCanonicalBloomBytes() : null);

        Add(command, "$now", UtcInstantText.Format(now));

        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        Result<LabeledArtifactWriteReceipt> labelled = await ArtifactSensitivityLedger.WriteWithinAsync(connection, transaction, label, ct).ConfigureAwait(false);

        return labelled.IsFailure ? labelled.Error : new GuildDataRef(write.ContentId, write.Kind, write.Revision, Convert.ToHexString(digest), write.MimeType, write.Body.Length, false);
    }

    private async Task<Result<GuildStoredCard>> UpsertWithinAsync(SqliteConnection connection, SqliteTransaction transaction, GuildCardWrite write, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(write.SourceIdentity) || string.IsNullOrWhiteSpace(write.SourceDigest) || write.Payload?.Card is not { } incoming
            || incoming.Skills.IsDefault || incoming.DefaultInputModes.IsDefault || incoming.DefaultOutputModes.IsDefault || write.Payload.RequestedTools.IsDefault
            || !Enum.IsDefined(incoming.Source.Kind) || (incoming.Source.Kind == GuildAgentSourceKind.Derived) != write.SessionId.HasValue)
        {
            return Invalid();
        }

        if (Encoding.UTF8.GetByteCount(write.SourceIdentity) + Encoding.UTF8.GetByteCount(write.SourceDigest) > MaximumContentBytes)
        {
            return TooLarge();
        }

        Result owner = await RequireOwnerWithinAsync(connection, transaction, write.SessionId, write.CampaignId, ct).ConfigureAwait(false);

        if (owner.IsFailure)
        {
            return owner.Error;
        }

        Result workspace = await RequireWorkspaceWithinAsync(connection, transaction, incoming.Source.WorkspaceId, write.WorkspaceRoot, ct).ConfigureAwait(false);

        if (workspace.IsFailure)
        {
            return workspace.Error;
        }

        Result claims = await RequireDeclaredSourceClaimsWithinAsync(connection, transaction, write, ct).ConfigureAwait(false);

        if (claims.IsFailure)
        {
            return claims.Error;
        }

        if (write.Label is { } sourceOwner && (sourceOwner.SessionId != write.SessionId || sourceOwner.CampaignId != write.CampaignId))
        {
            return new Error(ErrorCodes.Covenant.IntegrityFailure, "Guild Card content preserves its source owner.");
        }

        if (write.ExplicitAgentId is { } alias)
        {
            await using SqliteCommand conflict = Command(connection, transaction,
                "SELECT COUNT(*) FROM guild_agent_cards WHERE ExplicitAgentId=$alias AND SourceIdentity<>$source");

            Add(conflict, "$alias", Format(alias));

            Add(conflict, "$source", write.SourceIdentity);

            if (!Equals(0L, await conflict.ExecuteScalarAsync(ct).ConfigureAwait(false)))
            {
                return new Error(ErrorCodes.Guild.SourceConflict, "The declared Guild identity is bound to another source.");
            }
        }

        Guid agentId = Guid.NewGuid();

        Guid cardId = Guid.NewGuid();

        long revision = 1;

        bool exists = false;

        await using (SqliteCommand existing = Command(connection, transaction, """
            SELECT a.AgentId,a.CardId,a.CurrentRevision,r.SourceDigest,a.SessionId,a.CampaignId,a.ExplicitAgentId,a.SourceKindCode,a.WorkspaceId,a.PeerId,
              CASE WHEN length(CAST(a.AgentId AS BLOB))>36 OR length(CAST(a.CardId AS BLOB))>36
                OR COALESCE(length(CAST(a.SessionId AS BLOB)),0)>36 OR COALESCE(length(CAST(a.CampaignId AS BLOB)),0)>36
                OR COALESCE(length(CAST(a.ExplicitAgentId AS BLOB)),0)>36 OR COALESCE(length(CAST(a.WorkspaceId AS BLOB)),0)>36
                OR COALESCE(length(CAST(r.SourceDigest AS BLOB)),0)>65536 OR COALESCE(length(CAST(a.PeerId AS BLOB)),0)>65536
              THEN 1 ELSE 0 END
            FROM guild_agent_cards a LEFT JOIN guild_agent_card_revisions r ON r.CardId=a.CardId AND r.Revision=a.CurrentRevision
            WHERE a.SourceIdentity=$source
            """))
        {
            Add(existing, "$source", write.SourceIdentity);

            await using SqliteDataReader reader = await existing.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (reader.GetInt32(10) != 0)
                {
                    return TooLarge();
                }

                exists = true;

                agentId = Guid.Parse(reader.GetString(0));

                cardId = Guid.Parse(reader.GetString(1));

                long current = reader.GetInt64(2);

                if (ReadGuid(reader,4) != write.SessionId || ReadGuid(reader,5) != write.CampaignId
                    || ReadGuid(reader,6) != write.ExplicitAgentId || reader.GetInt32(7) != (int)incoming.Source.Kind
                    || ReadGuid(reader,8) != incoming.Source.WorkspaceId || ReadString(reader,9) != incoming.Source.PeerId)
                {
                    return Conflict();
                }

                if (write.ExpectedRevision is { } expected && expected != current)
                {
                    return Conflict();
                }

                if (!reader.IsDBNull(3) && reader.GetString(3) == write.SourceDigest)
                {
                    revision = current;
                }
                else
                {
                    if (current == long.MaxValue)
                    {
                        return new Error(ErrorCodes.Guild.SequenceExhausted, "The Guild Card revision is exhausted.");
                    }

                    revision = current + 1;
                }
            }
            else if (write.ExpectedRevision is not null)
            {
                return Conflict();
            }
        }

        if (exists)
        {
            Result<GuildStoredCard?> replay = await TryReplayWithinAsync(connection, transaction, write, agentId, cardId, revision, ct).ConfigureAwait(false);

            if (replay.IsFailure)
            {
                return replay.Error;
            }

            if (replay.Value is { } accepted)
            {
                await using SqliteCommand restored = Command(connection, transaction,
                    "UPDATE guild_agent_cards SET Available=$available,ReasonCode=$reason,UpdatedAtUtc=$now WHERE CardId=$card");

                Add(restored, "$card", Format(cardId));

                Add(restored, "$available", incoming.Source.Available ? 1 : 0);

                Add(restored, "$reason", incoming.Source.ReasonCode);

                Add(restored, "$now", UtcInstantText.Format(time.GetUtcNow()));

                _ = await restored.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                return accepted;
            }

            await using SqliteCommand current = Command(connection, transaction, "SELECT CurrentRevision FROM guild_agent_cards WHERE CardId=$card");

            Add(current, "$card", Format(cardId));

            long currentRevision = (long)(await current.ExecuteScalarAsync(ct).ConfigureAwait(false))!;

            if (revision == currentRevision)
            {
                if (revision == long.MaxValue)
                {
                    return new Error(ErrorCodes.Guild.SequenceExhausted, "The Guild Card revision is exhausted.");
                }

                revision++;
            }
        }

        Guid contentId = Guid.NewGuid();

        GuildAgentCard card = incoming with { AgentId = agentId, CardId = cardId, Revision = revision };

        GuildStoredAgentCardPayload payload = write.Payload with { Card = card };

        if (!IsBoundedPayload(payload))
        {
            return TooLarge();
        }

        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload, GuildStorageJsonContext.Default.GuildStoredAgentCardPayload);

        if (body.Length > MaximumContentBytes)
        {
            return TooLarge();
        }

        ArtifactSensitivityLabel? source = write.Label;

        DerivedArtifactWrite sensitivity = new(SensitiveArtifactKind.GuildContent, contentId, write.SessionId, write.CampaignId, source?.TurnId,
            (ulong)revision, new CovenantDigest(SHA256.HashData(body)), source?.Sensitivity ?? ContentSensitivity.None,
            source?.Provenance ?? GenerationProvenance.CreateExact([]), source?.ProducingPlanDigest, source?.ProducingAdmissionDigest, source?.ProducingMaintenanceReceiptDigest);

        if (source is not null && (source.SessionId != write.SessionId || source.CampaignId != write.CampaignId))
        {
            return new Error(ErrorCodes.Covenant.IntegrityFailure, "Guild Card content preserves its source owner.");
        }

        Result<GuildDataRef> content = await WriteContentWithinAsync(connection, transaction,
            new(contentId, cardId, GuildContentKind.AgentCard, revision, write.SessionId, write.CampaignId, "application/json", body, sensitivity), time.GetUtcNow(), ct).ConfigureAwait(false);

        if (content.IsFailure)
        {
            return content.Error;
        }

        DateTimeOffset cardWrittenAt = time.GetUtcNow();

        await using SqliteCommand command = Command(connection, transaction, exists ? """
            UPDATE guild_agent_cards SET CurrentRevision=$revision,ContentId=$content,Available=$available,ReasonCode=$reason,UpdatedAtUtc=$now WHERE CardId=$card
            """ : """
            INSERT INTO guild_agent_cards(CardId,AgentId,ExplicitAgentId,SourceKindCode,SourceIdentity,WorkspaceId,PeerId,SessionId,CampaignId,CurrentRevision,ContentId,Available,ReasonCode,CreatedAtUtc,UpdatedAtUtc)
            VALUES($card,$agent,$alias,$kind,$source,$workspace,$peer,$session,$campaign,$revision,$content,$available,$reason,$now,$now)
            """);

        Add(command, "$card", Format(cardId));

        Add(command, "$agent", Format(agentId));

        Add(command, "$alias", Format(write.ExplicitAgentId));

        Add(command, "$kind", (int)card.Source.Kind);

        Add(command, "$source", write.SourceIdentity);

        Add(command, "$workspace", Format(card.Source.WorkspaceId));

        Add(command, "$peer", card.Source.PeerId);

        Add(command, "$session", Format(write.SessionId));

        Add(command, "$campaign", Format(write.CampaignId));

        Add(command, "$revision", revision);

        Add(command, "$content", Format(contentId));

        Add(command, "$available", card.Source.Available ? 1 : 0);

        Add(command, "$reason", card.Source.ReasonCode);

        Add(command, "$now", UtcInstantText.Format(cardWrittenAt));

        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using SqliteCommand revisionCommand = Command(connection, transaction,
            "INSERT INTO guild_agent_card_revisions(CardId,Revision,ContentId,SourceDigest,CreatedAtUtc) VALUES($card,$revision,$content,$digest,$now)");

        Add(revisionCommand, "$card", Format(cardId));

        Add(revisionCommand, "$revision", revision);

        Add(revisionCommand, "$content", Format(contentId));

        Add(revisionCommand, "$digest", write.SourceDigest);

        Add(revisionCommand, "$now", UtcInstantText.Format(cardWrittenAt));

        _ = await revisionCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using SqliteCommand skillCommand = Command(connection, transaction,
            "INSERT INTO guild_agent_card_skills(CardId,Revision,SkillId,Ordinal) VALUES($card,$revision,$skill,$ordinal)");

        Add(skillCommand, "$card", Format(cardId));

        Add(skillCommand, "$revision", revision);

        for (int i = 0; i < card.Skills.Length; i++)
        {
            if (skillCommand.Parameters.Contains("$skill"))
            {
                skillCommand.Parameters.RemoveAt("$skill");
            }

            if (skillCommand.Parameters.Contains("$ordinal"))
            {
                skillCommand.Parameters.RemoveAt("$ordinal");
            }

            Add(skillCommand, "$skill", card.Skills[i].Id);

            Add(skillCommand, "$ordinal", i);

            _ = await skillCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return new GuildStoredCard(payload, content.Value, write.SourceIdentity, write.SessionId, write.CampaignId);
    }

    private static async Task<Result<GuildStoredCard?>> TryReplayWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildCardWrite write, Guid agentId, Guid cardId, long revision, CancellationToken ct)
    {
        GuildStoredAgentCardPayload payload = write.Payload with { Card = write.Payload.Card with { AgentId = agentId, CardId = cardId, Revision = revision } };

        if (!IsBoundedPayload(payload))
        {
            return TooLarge();
        }

        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload, GuildStorageJsonContext.Default.GuildStoredAgentCardPayload);

        if (body.Length > MaximumContentBytes)
        {
            return TooLarge();
        }

        await using SqliteCommand command = Command(connection, transaction, $"""
            SELECT c.ContentId,c.Digest,c.Body,c.SensitivityCode,c.ProvenanceModeCode,c.ExactGenerationIds,c.GenerationBloom,
              length(CAST(c.ContentId AS BLOB)),length(c.Digest),length(c.Body),
              COALESCE(length(c.ExactGenerationIds),0),COALESCE(length(c.GenerationBloom),0)
            FROM guild_agent_card_revisions r JOIN guild_content c ON c.ContentId=r.ContentId
            WHERE r.CardId=$card AND r.Revision=$revision AND {LiveContent}
            """);

        Add(command, "$card", Format(cardId));

        Add(command, "$revision", revision);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return Result<GuildStoredCard?>.Success(null);
        }

        if (reader.GetInt64(7) > 36 || reader.GetInt64(8) > 32 || reader.GetInt64(9) > MaximumContentBytes
            || reader.GetInt64(10) > 128 || reader.GetInt64(11) > 32)
        {
            return TooLarge();
        }

        Guid contentId = Guid.Parse(reader.GetString(0));

        byte[] digest = (byte[])reader.GetValue(1);

        byte[] storedBody = (byte[])reader.GetValue(2);

        int sensitivity = reader.GetInt32(3);

        int mode = reader.GetInt32(4);

        byte[]? exact = reader.IsDBNull(5) ? null : (byte[])reader.GetValue(5);

        byte[]? bloom = reader.IsDBNull(6) ? null : (byte[])reader.GetValue(6);

        await reader.DisposeAsync().ConfigureAwait(false);

        if (!SHA256.HashData(storedBody).AsSpan().SequenceEqual(digest))
        {
            return new Error(ErrorCodes.Covenant.IntegrityFailure, "Guild content digest verification failed.");
        }

        await using (SqliteCommand labelBounds = Command(connection, transaction, """
            SELECT CASE WHEN length(CAST(LabelId AS BLOB))>36 OR COALESCE(length(CAST(SessionId AS BLOB)),0)>36
                OR COALESCE(length(CAST(CampaignId AS BLOB)),0)>36 OR COALESCE(length(CAST(TurnId AS BLOB)),0)>36
                OR length(CAST(CreatedAtUtc AS BLOB))>64 OR length(ArtifactContentDigest)>32
                OR COALESCE(length(ExactGenerationIds),0)>128 OR COALESCE(length(GenerationBloom),0)>32
                OR COALESCE(length(ProducingPlanDigest),0)>32 OR COALESCE(length(ProducingAdmissionDigest),0)>32
                OR COALESCE(length(ProducingMaintenanceReceiptDigest),0)>32
              THEN 1 ELSE 0 END
            FROM artifact_sensitivity WHERE ArtifactKindCode=17 AND ArtifactId=$id
            """))
        {
            Add(labelBounds, "$id", Format(contentId));

            if (Equals(1L, await labelBounds.ExecuteScalarAsync(ct).ConfigureAwait(false)))
            {
                return TooLarge();
            }
        }

        Result<ArtifactSensitivityLabel?> labelled = await ArtifactSensitivityLedger.ReadLabelWithinAsync(connection, transaction,
            SensitiveArtifactKind.GuildContent, contentId, ct).ConfigureAwait(false);

        if (labelled.IsFailure)
        {
            return labelled.Error;
        }

        ArtifactSensitivityLabel? stored = labelled.Value;

        if ((sensitivity == 0) != (stored is null)
            || stored is not null && (stored.ArtifactRevision != (ulong)revision || stored.ArtifactContentDigest != new CovenantDigest(digest)
                || stored.SessionId != write.SessionId || stored.CampaignId != write.CampaignId
                || mode != (int)stored.Provenance.Mode
                || (stored.Provenance.Mode == GenerationProvenanceMode.Exact
                    ? exact is null || bloom is not null || !exact.AsSpan().SequenceEqual(stored.Provenance.ToCanonicalExactBytes())
                    : bloom is null || exact is not null || !bloom.AsSpan().SequenceEqual(stored.Provenance.ToCanonicalBloomBytes()))))
        {
            return new Error(ErrorCodes.Covenant.IntegrityFailure, "Guild content sensitivity does not bind its immutable revision.");
        }

        ArtifactSensitivityLabel? source = write.Label;

        bool sameSensitivity = source?.Sensitivity is null or ContentSensitivity.None
            ? stored is null
            : stored is not null && stored.SensitivityDigest == source.SensitivityDigest && stored.TurnId == source.TurnId
                && stored.ProducingPlanDigest == source.ProducingPlanDigest && stored.ProducingAdmissionDigest == source.ProducingAdmissionDigest
                && stored.ProducingMaintenanceReceiptDigest == source.ProducingMaintenanceReceiptDigest;

        GuildStoredAgentCardPayload? storedPayload = JsonSerializer.Deserialize(storedBody, GuildStorageJsonContext.Default.GuildStoredAgentCardPayload);

        if (storedPayload?.Card?.Source is not { } storedSource)
        {
            return Invalid();
        }

        // Availability is a mutable header projection, not a new authored content revision.
        GuildStoredAgentCardPayload canonicalReplay = payload with { Card = payload.Card with
        {
            Source = payload.Card.Source with { Available = storedSource.Available, ReasonCode = storedSource.ReasonCode }
        } };

        byte[] replayBody = JsonSerializer.SerializeToUtf8Bytes(canonicalReplay, GuildStorageJsonContext.Default.GuildStoredAgentCardPayload);

        if (!sameSensitivity || !replayBody.AsSpan().SequenceEqual(storedBody))
        {
            return Result<GuildStoredCard?>.Success(null);
        }

        if (stored is not null)
        {
            Result covenant = await GuildStorageAvailability.CheckCovenantAsync(connection, transaction, ct).ConfigureAwait(false);

            if (covenant.IsFailure)
            {
                return covenant.Error;
            }
        }

        GuildDataRef reference = new(contentId, GuildContentKind.AgentCard, revision, Convert.ToHexString(digest), "application/json", storedBody.Length, false);

        // This returns only bytes supplied by the trusted writer; it does not authorize a protected read.
        return Result<GuildStoredCard?>.Success(new(payload, reference, write.SourceIdentity, write.SessionId, write.CampaignId));
    }

    private Task<Result<GuildStoredCard>> ReadOneAsync(string predicate, string id, Guid? cardId, long? revision, GuildCardReadScope? scope, CancellationToken ct) =>
        ExecuteAsync(false, (connection, transaction, token) => ReadCardWithinAsync(connection, transaction, predicate, id, cardId, revision, scope, token), ct);

    private static async Task<Result<GuildStoredCard>> ReadCardWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        string predicate, string id, Guid? cardId, long? revision, GuildCardReadScope? scope, CancellationToken ct)
    {
        if (scope is not null)
        {
            Result workspace = await RequireWorkspaceWithinAsync(connection, transaction, scope.WorkspaceId, scope.WorkspaceRoot, ct).ConfigureAwait(false);

            if (workspace.IsFailure)
            {
                return workspace.Error;
            }
        }

        await using SqliteCommand command = Command(connection, transaction, $"""
            SELECT {ReadColumns} FROM guild_agent_cards a
            JOIN guild_agent_card_revisions r ON r.CardId=a.CardId AND r.Revision=COALESCE($revision,a.CurrentRevision)
            JOIN guild_content c ON c.ContentId=r.ContentId
            WHERE {predicate} AND {LiveOwners} AND {LiveContent}
            """ + (scope is null ? "" : " AND " + ScopePredicate));

        Add(command, "$id", id);

        Add(command, "$card", Format(cardId));

        Add(command, "$revision", revision);

        if (scope is not null)
        {
            BindScope(command, scope);
        }

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Materialize(reader) : NotFound();
    }

    private static Result<GuildStoredCard> Materialize(SqliteDataReader reader)
    {
        if (reader.GetInt32(11) != 0)
        {
            return new Error(ErrorCodes.Guild.AuthorityUnavailable, "Protected Guild content requires current verified read authority.");
        }

        if (reader.GetInt64(10) > MaximumContentBytes)
        {
            return TooLarge();
        }

        byte[] body = (byte[])reader.GetValue(12);

        byte[] digest = (byte[])reader.GetValue(8);

        if (!SHA256.HashData(body).AsSpan().SequenceEqual(digest))
        {
            return new Error(ErrorCodes.Covenant.IntegrityFailure, "Guild content digest verification failed.");
        }

        GuildStoredAgentCardPayload? payload = JsonSerializer.Deserialize(body, GuildStorageJsonContext.Default.GuildStoredAgentCardPayload);

        if (payload?.Card?.Source is null || payload.Card.Skills.IsDefault || payload.RequestedTools.IsDefault
            || payload.Card.AgentId != Guid.Parse(reader.GetString(14)) || payload.Card.CardId != Guid.Parse(reader.GetString(13))
            || payload.Card.Revision != reader.GetInt64(7) || (int)payload.Card.Source.Kind != reader.GetInt32(15)
            || payload.Card.Source.WorkspaceId != ReadGuid(reader,16) || payload.Card.Source.PeerId != ReadString(reader,17))
        {
            return Invalid();
        }

        payload = payload with { Card = payload.Card with { Source = payload.Card.Source with { Available = reader.GetInt32(3) == 1, ReasonCode = ReadString(reader,4) } } };

        GuildDataRef reference = new(Guid.Parse(reader.GetString(5)), (GuildContentKind)reader.GetInt32(6), reader.GetInt64(7), Convert.ToHexString(digest), reader.GetString(9), body.Length, false);

        return new GuildStoredCard(payload, reference, reader.GetString(0), ReadGuid(reader,1), ReadGuid(reader,2));
    }

    internal static async Task<Result> RequireOwnerWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid? sessionId, Guid? campaignId, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT CASE WHEN
              ($session IS NULL OR (EXISTS(SELECT 1 FROM Sessions WHERE Id=$session AND CampaignId IS $campaign)
                AND NOT EXISTS(SELECT 1 FROM owner_deletion_events WHERE OwnerKindCode=2 AND OwnerId=$session)))
              AND ($campaign IS NULL OR (EXISTS(SELECT 1 FROM Campaigns WHERE Id=$campaign)
                AND NOT EXISTS(SELECT 1 FROM owner_deletion_events WHERE OwnerKindCode=1 AND OwnerId=$campaign)))
            THEN 1 ELSE 0 END
            """);

        Add(command, "$session", Format(sessionId));

        Add(command, "$campaign", Format(campaignId));

        return Equals(1L, await command.ExecuteScalarAsync(ct).ConfigureAwait(false))
            ? Result.Success() : new Error(ErrorCodes.Guild.OwnerUnavailable, "The Guild owner is unavailable.");
    }

    private static bool IsBoundedPayload(GuildStoredAgentCardPayload payload)
    {
        long bytes = 0;

        bool Text(string? value)
        {
            if (value is null)
            {
                return false;
            }

            bytes += Encoding.UTF8.GetByteCount(value) + 2L;

            return bytes <= MaximumContentBytes;
        }

        bool Texts(ImmutableArray<string> values)
        {
            if (values.IsDefault)
            {
                return false;
            }

            foreach (string value in values)
            {
                if (!Text(value))
                {
                    return false;
                }
            }

            return true;
        }

        GuildAgentCard card = payload.Card;

        if (!Text(card.Name) || !Text(card.Description) || !Text(card.Version) || !Text(payload.Guidance)
            || !Texts(payload.RequestedTools) || !Texts(card.DefaultInputModes) || !Texts(card.DefaultOutputModes))
        {
            return false;
        }

        foreach (GuildAgentSkillDto skill in card.Skills)
        {
            if (skill is null || !Text(skill.Id) || !Text(skill.Name) || !Text(skill.Description)
                || !Texts(skill.Tags) || !Texts(skill.InputModes) || !Texts(skill.OutputModes))
            {
                return false;
            }
        }

        return Text(card.Source.PeerId ?? "") && Text(card.Source.ReasonCode ?? "");
    }

    private static async Task<Result> RequireWorkspaceWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid? workspaceId, string? expectedRoot, CancellationToken ct)
    {
        Result owner = await RequireOwnerWithinAsync(connection, transaction, null, workspaceId, ct).ConfigureAwait(false);

        if (owner.IsFailure || workspaceId is null || expectedRoot is null)
        {
            return owner;
        }

        await using SqliteCommand command = Command(connection, transaction, "SELECT Path FROM Campaigns WHERE Id=$id");

        Add(command, "$id", Format(workspaceId));

        object? stored = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

        if (stored is not string path || !WorkspacePathPolicy.TryNormalizeWorkspace(path, out string? actual, out _)
            || !WorkspacePathPolicy.TryNormalizeWorkspace(expectedRoot, out string? expected, out _)
            || !string.Equals(Path.TrimEndingDirectorySeparator(actual), Path.TrimEndingDirectorySeparator(expected),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return new Error(ErrorCodes.Guild.WorkspaceIdentityUnavailable, "The selected Guild workspace registration changed.");
        }

        return Result.Success();
    }

    private async Task<Result<T>> ExecuteAsync<T>(bool write,
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<Result<T>>> action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!gate.TryAcquireWorkLease(GrimoireWorkKind.GuildStorage, out IGrimoireWorkLease? work))
        {
            return Deferred();
        }

        await using IGrimoireWorkLease held = work!;

        IGrimoireExternalEffectGroup? effect = null;

        if (write && !held.TryBeginExternalEffectGroup(out effect))
        {
            return Deferred();
        }

        await using IGrimoireExternalEffectGroup? heldEffect = effect;

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, held.MaintenanceRevocation);

        try
        {
            Result<IGrimoireOrdinaryConnectionLease> acquired = await connections.OpenFreshAsync(
                write ? GrimoireOrdinaryFreshConnectionKind.ReadWrite : GrimoireOrdinaryFreshConnectionKind.ReadOnly, linked.Token).ConfigureAwait(false);

            if (acquired.IsFailure)
            {
                return acquired.Error.Code == ErrorCodes.Grimoire.MaintenanceUnavailable ? Deferred() : Unavailable();
            }

            await using IGrimoireOrdinaryConnectionLease lease = acquired.Value;

            await using SqliteTransaction transaction = lease.Connection.BeginTransaction(deferred: !write);

            Result available = await GuildStorageAvailability.CheckAsync(lease.Connection, transaction, linked.Token).ConfigureAwait(false);

            if (available.IsFailure)
            {
                return available.Error;
            }

            Result<T> result = await action(lease.Connection, transaction, linked.Token).ConfigureAwait(false);

            if (result.IsFailure)
            {
                return result;
            }

            linked.Token.ThrowIfCancellationRequested();

            await transaction.CommitAsync(linked.Token).ConfigureAwait(false);

            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && held.MaintenanceRevocation.IsCancellationRequested)
        {
            return Deferred();
        }
        catch (SqliteException exception)
        {
            return exception.SqliteErrorCode switch
            {
                13 or 5 or 6 => new Error(ErrorCodes.Guild.StoragePressure, "Guild storage could not admit the operation."),
                19 => Conflict(),
                _ => Unavailable(),
            };
        }
        catch (JsonException)
        {
            return Invalid();
        }
    }

    private const string ScopePredicate = "((a.SourceKindCode IN(0,3) AND $installation=1) OR (a.SourceKindCode=1 AND a.WorkspaceId=$workspace) OR (a.SourceKindCode=2 AND a.SessionId=$session AND a.CampaignId IS $campaign))";

    private static void BindScope(SqliteCommand command, GuildCardReadScope scope)
    {
        Add(command, "$installation", scope.IncludeInstallation ? 1 : 0);

        Add(command, "$workspace", Format(scope.WorkspaceId));

        Add(command, "$session", Format(scope.SessionId));

        Add(command, "$campaign", Format(scope.CampaignId));
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        return command;
    }

    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string Format(Guid value) => value.ToString("D").ToUpperInvariant();

    private static string? Format(Guid? value) => value.HasValue ? Format(value.Value) : null;

    private static Guid? ReadGuid(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));

    private static string? ReadString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static Error Invalid() => new(ErrorCodes.Guild.InvalidRequest, "Guild storage data is invalid.");

    private static Error TooLarge() => new(ErrorCodes.Guild.PayloadTooLarge, "Guild content exceeds its materialization bound.");

    private static Error Conflict() => new(ErrorCodes.Guild.RevisionConflict, "The Guild source or revision conflicts with the stored identity.");

    private static Error NotFound() => new(ErrorCodes.Guild.NotFound, "The Guild Card is unavailable.");

    private static Error Deferred() => new(ErrorCodes.Guild.MaintenanceDeferred, "Guild storage is deferred for maintenance.");

    private static Error Unavailable() => new(ErrorCodes.Guild.Unavailable, "Guild storage is unavailable.");
}
