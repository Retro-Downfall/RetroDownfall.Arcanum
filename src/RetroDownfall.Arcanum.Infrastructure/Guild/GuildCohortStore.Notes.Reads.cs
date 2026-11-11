using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildCohortStore
{
    internal async Task<Result<SharedNotePageDto>> ReadNotesAsync(GuildCallerContext caller, Guid cohortId,
        GuildPageQuery query, CancellationToken ct, GuildChildToolAdmission? toolAdmission = null)
    {
        if (caller is null || cohortId == Guid.Empty || query is null || query.PageSize is < 1 or > 100)
            return new Error(ErrorCodes.Guild.InvalidRequest, "The shared notes page is invalid.");

        var root = await NoteRootAsync(caller, cohortId, ct).ConfigureAwait(false);

        if (root.IsFailure) return root.Error;

        GuildQuestDto? acceptedQuest = null;

        var result = await InCohortToolTransactionAsync(root.Value, toolAdmission, GuildJsonContext.Default.SharedNotePageDto,
            async (connection, transaction, signets, token) =>
            {
                bool boundCursor = false;

                if (query.Cursor is not null)
                {
                    if (query.Cursor.Length != 32 || !Guid.TryParseExact(query.Cursor, "N", out Guid supplied) || supplied == Guid.Empty) return NoteCursorInvalid;

                    Result authenticated = await policy.AuthorizeLocateAsync(caller, connection, transaction,
                        caller.Execution?.QuestId, null, null, token).ConfigureAwait(false);

                    if (authenticated.IsFailure) return authenticated.Error;

                    await using var binding = Command(connection, transaction, """
                        SELECT EXISTS(SELECT 1 FROM guild_portal_cursors WHERE Id=$id AND CallerKey=$caller
                            AND RootQuestId=$root AND ScopeKey=$scope AND VisibilityKey=$cohort)
                        """);

                    Add(binding, "$id", Id(supplied));
                    Add(binding, "$caller", NoteReaderKey(caller));
                    Add(binding, "$root", Id(root.Value));
                    Add(binding, "$scope", "notes:" + Id(cohortId) + ":" + query.PageSize.ToString(CultureInfo.InvariantCulture));
                    Add(binding, "$cohort", Id(cohortId));

                    if (!Equals(1L, await binding.ExecuteScalarAsync(token).ConfigureAwait(false))) return NoteCursorInvalid;

                    boundCursor = true;
                }

                var found = await ReadWithinAsync(connection, transaction, cohortId, token).ConfigureAwait(false);

                if (found.IsFailure) return boundCursor && found.Error.Code == ErrorCodes.Guild.OwnerUnavailable ? NoteCursorGone : found.Error;

                if (found.Value.Cohort.RootQuestId != root.Value) return Forbidden;

                var actor = await AuthorizeNoteWithinAsync(connection, transaction, signets, caller, found.Value.Cohort, false, token).ConfigureAwait(false);

                if (actor.IsFailure) return boundCursor && actor.Error.Code == ErrorCodes.Guild.OwnerUnavailable ? NoteCursorGone : actor.Error;

                if (toolAdmission is not null)
                {
                    if (!ReferenceEquals(toolAdmission.Registry.Parent.Driver.Caller, caller)
                        || !GuildMarketToolArguments.MatchesRead(toolAdmission, cohortId, query)) return Forbidden;

                    var admitted = await quests.AuthorizeCohortToolWithinAsync(connection, transaction, signets, toolAdmission, token).ConfigureAwait(false);

                    if (admitted.IsFailure) return admitted.Error;

                    acceptedQuest = admitted.Value.Quest;
                }

                var epoch = await GuildSignetStore.EpochAsync(connection, transaction, token).ConfigureAwait(false);

                if (epoch.IsFailure) return epoch.Error;

                string scope = "notes:" + Id(cohortId) + ":" + query.PageSize.ToString(CultureInfo.InvariantCulture);
                string callerKey = NoteReaderKey(caller);
                long after = 0;
                long snapshot;

                if (query.Cursor is not null)
                {
                    if (query.Cursor.Length != 32 || !Guid.TryParseExact(query.Cursor, "N", out Guid cursorId) || cursorId == Guid.Empty)
                        return NoteCursorInvalid;

                    await using var cursor = Command(connection, transaction, """
                        SELECT AfterSequence,SnapshotSequence FROM guild_portal_cursors WHERE Id=$id
                            AND InstallationGeneration=$generation AND AuthorityEpoch=$epoch AND RootQuestId=$root
                            AND CallerKey=$caller AND ScopeKey=$scope AND VisibilityKey=$cohort
                        """);

                    Add(cursor, "$id", Id(cursorId));
                    Add(cursor, "$generation", Id(policy.InstallationGeneration));
                    Add(cursor, "$epoch", Id(epoch.Value));
                    Add(cursor, "$root", Id(root.Value));
                    Add(cursor, "$caller", callerKey);
                    Add(cursor, "$scope", scope);
                    Add(cursor, "$cohort", Id(cohortId));

                    await using var reader = await cursor.ExecuteReaderAsync(token).ConfigureAwait(false);

                    if (!await reader.ReadAsync(token).ConfigureAwait(false)) return NoteCursorGone;

                    if (reader.GetValue(0) is not long coordinate || reader.GetValue(1) is not long high || coordinate < 0 || high < coordinate) return Unavailable;

                    after = coordinate;
                    snapshot = high;
                }
                else
                {
                    await using var high = Command(connection, transaction, "SELECT NoteSequence FROM guild_cohorts WHERE Id=$cohort");

                    Add(high, "$cohort", Id(cohortId));

                    if (await high.ExecuteScalarAsync(token).ConfigureAwait(false) is not long value || value < 0) return Unavailable;

                    snapshot = value;
                }

                List<(Guid Id, long Sequence)> rows = [];

                await using (var page = Command(connection, transaction, """
                    SELECT CASE WHEN typeof(Id)='text' AND length(CAST(Id AS BLOB))=36 THEN Id END,Sequence FROM guild_cohort_ledger WHERE CohortId=$cohort AND Sequence>$after AND Sequence<=$snapshot
                    ORDER BY Sequence,Id LIMIT $limit
                    """))
                {
                    Add(page, "$cohort", Id(cohortId));
                    Add(page, "$after", after);
                    Add(page, "$snapshot", snapshot);
                    Add(page, "$limit", query.PageSize);

                    await using var reader = await page.ExecuteReaderAsync(token).ConfigureAwait(false);

                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        if (reader.IsDBNull(0) || !Guid.TryParseExact(reader.GetString(0), "D", out Guid id) || reader.GetValue(1) is not long sequence || sequence <= after || sequence > snapshot) return Unavailable;

                        rows.Add((id, sequence));
                    }
                }

                ImmutableArray<SharedNoteDto>.Builder notes = ImmutableArray.CreateBuilder<SharedNoteDto>();
                long last = after;
                bool more = false;

                foreach (var row in rows.Take(query.PageSize))
                {
                    var note = await ReadNoteWithinAsync(connection, transaction, cohortId, row.Id, false, token).ConfigureAwait(false);

                    if (note.IsFailure)
                    {
                        if (note.Error.Code == ErrorCodes.Guild.OwnerUnavailable)
                        {
                            last = row.Sequence;
                            continue;
                        }

                        return note.Error;
                    }

                    var tentative = new SharedNotePageDto(notes.ToImmutable().Add(note.Value), new string('0', 32), snapshot);

                    if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(tentative, GuildJsonContext.Default.SharedNotePageDto)) > 1_048_576
                        || toolAdmission is not null && toolAdmission.ValidateProjectionAllocation(JsonSerializer.SerializeToElement(tentative, GuildJsonContext.Default.SharedNotePageDto)).IsFailure)
                    {
                        if (notes.Count == 0) return new Error(ErrorCodes.Guild.PayloadTooLarge, "One shared note exceeds the response allocation.");

                        more = true;
                        break;
                    }

                    notes.Add(note.Value);
                    last = row.Sequence;
                }

                if (!more)
                {
                    await using var remaining = Command(connection, transaction,
                        "SELECT EXISTS(SELECT 1 FROM guild_cohort_ledger WHERE CohortId=$cohort AND Sequence>$after AND Sequence<=$snapshot)");

                    Add(remaining, "$cohort", Id(cohortId));
                    Add(remaining, "$after", last);
                    Add(remaining, "$snapshot", snapshot);

                    more = Equals(1L, await remaining.ExecuteScalarAsync(token).ConfigureAwait(false));
                }

                string? next = null;

                if (more)
                {
                    Guid id = Guid.NewGuid();

                    await using var cursor = Command(connection, transaction, """
                        INSERT INTO guild_portal_cursors(Id,InstallationGeneration,AuthorityEpoch,CallerKey,RootQuestId,ScopeKey,
                            AfterSequence,SnapshotSequence,CreatedAtUtc,VisibilityKey)
                        VALUES($id,$generation,$epoch,$caller,$root,$scope,$after,$snapshot,$now,$cohort)
                        """);

                    Add(cursor, "$id", Id(id));
                    Add(cursor, "$generation", Id(policy.InstallationGeneration));
                    Add(cursor, "$epoch", Id(epoch.Value));
                    Add(cursor, "$caller", callerKey);
                    Add(cursor, "$root", Id(root.Value));
                    Add(cursor, "$scope", scope);
                    Add(cursor, "$after", last);
                    Add(cursor, "$snapshot", snapshot);
                    Add(cursor, "$now", UtcInstantText.Format(time.GetUtcNow()));
                    Add(cursor, "$cohort", Id(cohortId));

                    await cursor.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                    next = id.ToString("N");
                }

                return new SharedNotePageDto(notes.ToImmutable(), next, snapshot);
            }, ct).ConfigureAwait(false);

        if (result.IsSuccess && toolAdmission is not null && acceptedQuest is not null)
            toolAdmission.RecordCommittedProjection(acceptedQuest, JsonSerializer.SerializeToElement(result.Value, GuildJsonContext.Default.SharedNotePageDto));

        return result;
    }

    private static string NoteReaderKey(GuildCallerContext caller)
    {
        string identity = ((int)caller.Kind).ToString(CultureInfo.InvariantCulture) + ":" + caller.InstallationGeneration.ToString("D") + ":" + caller.PrincipalId?.ToString("D")
            + ":" + caller.Execution?.QuestId.ToString("D") + ":" + caller.Execution?.RunId.ToString("D")
            + ":" + caller.Execution?.SignetId.ToString("D") + ":" + caller.Execution?.CredentialGeneration.ToString(CultureInfo.InvariantCulture)
            + ":" + caller.Execution?.RevocationGeneration.ToString(CultureInfo.InvariantCulture);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private static Error NoteCursorGone => new(ErrorCodes.Guild.CursorGone, "The note cursor owner or installation authority is no longer available.");

    private static Error NoteCursorInvalid => new(ErrorCodes.Guild.CursorInvalid, "The note cursor does not match this caller and Cohort.");

    internal static async Task<Result<SharedNoteDto>> ReadNoteWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid cohortId, Guid noteId, bool replay, CancellationToken ct)
    {
        SharedNoteDto note;
        Guid content;
        Guid? session;
        Guid? campaign;
        byte[] digest;
        long length;

        await using (var command = Command(connection, transaction, """
            SELECT Id,Sequence,AuthorQuestId,AuthorMemberId,OperatorPrincipalId,AuthorityCode,KindCode,SubjectId,CorrectsNoteId,
                ContentId,SessionId,CampaignId,Digest,ByteLength,IdempotencyKey,ReceiptId,AcceptedRevision,CreatedAtUtc
            FROM guild_cohort_ledger WHERE Id=$id AND CohortId=$cohort
                AND typeof(Id)='text' AND length(CAST(Id AS BLOB))=36 AND typeof(Sequence)='integer' AND Sequence>=1
                AND (AuthorQuestId IS NULL OR typeof(AuthorQuestId)='text' AND length(CAST(AuthorQuestId AS BLOB))=36)
                AND (AuthorMemberId IS NULL OR typeof(AuthorMemberId)='text' AND length(CAST(AuthorMemberId AS BLOB))=36)
                AND (OperatorPrincipalId IS NULL OR typeof(OperatorPrincipalId)='text' AND length(CAST(OperatorPrincipalId AS BLOB))=36)
                AND typeof(AuthorityCode)='integer' AND AuthorityCode IN(1,2,3) AND typeof(KindCode)='integer' AND KindCode IN(1,2,3)
                AND (SubjectId IS NULL OR typeof(SubjectId)='text' AND length(CAST(SubjectId AS BLOB))=36)
                AND (CorrectsNoteId IS NULL OR typeof(CorrectsNoteId)='text' AND length(CAST(CorrectsNoteId AS BLOB))=36)
                AND typeof(ContentId)='text' AND length(CAST(ContentId AS BLOB))=36
                AND (SessionId IS NULL OR typeof(SessionId)='text' AND length(CAST(SessionId AS BLOB))=36)
                AND (CampaignId IS NULL OR typeof(CampaignId)='text' AND length(CAST(CampaignId AS BLOB))=36)
                AND typeof(Digest)='blob' AND length(Digest)=32 AND typeof(ByteLength)='integer' AND ByteLength BETWEEN 1 AND 65536
                AND typeof(IdempotencyKey)='text' AND length(CAST(IdempotencyKey AS BLOB))=36
                AND typeof(ReceiptId)='text' AND length(CAST(ReceiptId AS BLOB))=36
                AND typeof(AcceptedRevision)='integer' AND AcceptedRevision>=1
                AND typeof(CreatedAtUtc)='text' AND length(CAST(CreatedAtUtc AS BLOB)) BETWEEN 1 AND 64
            """))
        {
            Add(command, "$id", Id(noteId));
            Add(command, "$cohort", Id(cohortId));

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return new Error(ErrorCodes.Guild.NotFound, "The shared note is unavailable.");

            try
            {
                Guid? Optional(int index) => reader.IsDBNull(index) ? null : Guid.ParseExact(reader.GetString(index), "D");

                content = Guid.ParseExact(reader.GetString(9), "D");
                session = Optional(10);
                campaign = Optional(11);
                digest = (byte[])reader.GetValue(12);
                length = reader.GetInt64(13);
                DateTimeOffset created = UtcInstantText.Parse(reader.GetString(17));
                GuildAuthorityKind authorityKind = (GuildAuthorityKind)reader.GetInt32(5);
                GuildNoteKind kind = (GuildNoteKind)reader.GetInt32(6);

                if (reader.GetInt64(1) < 1 || length is < 1 or > GuildStore.MaximumContentBytes || digest.Length != 32
                    || authorityKind is not (GuildAuthorityKind.Operator or GuildAuthorityKind.Parent or GuildAuthorityKind.Peer) || !Enum.IsDefined(kind)) return Unavailable;

                note = new(Guid.ParseExact(reader.GetString(0), "D"), cohortId, reader.GetInt64(1), Optional(2), Optional(3), authorityKind,
                    kind, Optional(7), null, Optional(8), created,
                    new(Guid.ParseExact(reader.GetString(15), "D"), Guid.ParseExact(reader.GetString(14), "D"), replay, created, reader.GetInt64(16)),
                    Optional(4), null);
            }
            catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException) { return Unavailable; }
        }

        var owner = await GuildStore.RequireOwnerWithinAsync(connection, transaction, session, campaign, ct).ConfigureAwait(false);

        if (owner.IsFailure) return owner.Error;

        var reference = await ReadCurrentContentWithinAsync(connection, transaction, noteId, session, campaign,
            content, GuildContentKind.CohortLedgerEntry, 1, "text/plain", length, digest, null, null, ct, true).ConfigureAwait(false);

        if (reference.IsFailure) return reference.Error;

        if (reference.Value is null) return note;

        await using var body = Command(connection, transaction, """
            SELECT CASE WHEN typeof(Body)='blob' AND length(Body)=$length THEN Body END FROM guild_content
            WHERE ContentId=$content AND SourceRecordId=$note AND ContentKindCode=$kind AND Revision=1
                AND Digest=$digest AND SensitivityCode=0 AND ProvenanceModeCode=0
                AND NOT EXISTS(SELECT 1 FROM artifact_sensitivity WHERE ArtifactKindCode=17
                    AND lower(replace(ArtifactId,'-',''))=lower(replace($content,'-','')))
            """);

        Add(body, "$length", length);
        Add(body, "$content", Id(content));
        Add(body, "$note", Id(noteId));
        Add(body, "$kind", (int)GuildContentKind.CohortLedgerEntry);
        Add(body, "$digest", digest);

        if (await body.ExecuteScalarAsync(ct).ConfigureAwait(false) is not byte[] bytes || !SHA256.HashData(bytes).AsSpan().SequenceEqual(digest)) return Unavailable;

        try
        {
            string text = new UTF8Encoding(false, true).GetString(bytes);

            return note with { BodyRef = reference.Value, Body = GuildPromptBuilder.Build(note.Authority, GuildEnvelopeKind.Progress, note.Id, text) };
        }
        catch (DecoderFallbackException) { return Unavailable; }
    }
}
