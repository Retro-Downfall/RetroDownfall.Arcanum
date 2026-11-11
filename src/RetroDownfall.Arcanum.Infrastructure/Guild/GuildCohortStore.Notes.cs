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
    private sealed record NoteActor(GuildQuestStored Source, Guid? Member, GuildAuthorityKind Kind, string Key);

    internal async Task<Result<SharedNoteDto>> AppendNoteAsync(GuildCallerContext caller, GuildMutationIdentity mutation,
        Guid cohortId, AppendSharedNoteRequest request, CancellationToken ct, GuildChildToolAdmission? toolAdmission = null)
    {
        if (caller is null || request is null || mutation is null || cohortId == Guid.Empty
            || request.IdempotencyKey == Guid.Empty || mutation.IdempotencyKey != request.IdempotencyKey
            || request.ExpectedRevision < 1 || !Enum.IsDefined(request.Kind) || string.IsNullOrWhiteSpace(request.Body)
            || request.Body.Length > GuildStore.MaximumContentBytes || request.SubjectId == Guid.Empty
            || (request.Kind == GuildNoteKind.Correction ? request.CorrectsNoteId is null || request.CorrectsNoteId == Guid.Empty : request.CorrectsNoteId is not null))
            return new Error(ErrorCodes.Guild.InvalidRequest, "The shared note request is invalid.");

        byte[] body;

        try { body = new UTF8Encoding(false, true).GetBytes(request.Body); }
        catch (EncoderFallbackException) { return new Error(ErrorCodes.Guild.InvalidRequest, "The shared note body is not valid UTF-8."); }

        if (body.Length > GuildStore.MaximumContentBytes) return new Error(ErrorCodes.Guild.PayloadTooLarge, "The shared note exceeds its content boundary.");

        byte[] digest = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, GuildJsonContext.Default.AppendSharedNoteRequest));

        if (mutation.CanonicalRequestDigest != (toolAdmission?.ArgumentsDigest ?? Convert.ToHexString(digest))) return Forbidden;

        Result<Guid> root = await NoteRootAsync(caller, cohortId, ct).ConfigureAwait(false);

        if (root.IsFailure) return root.Error;

        GuildQuestDto? acceptedQuest = null;

        var result = await InCohortToolTransactionAsync(root.Value, toolAdmission, GuildJsonContext.Default.SharedNoteDto,
            async (connection, transaction, signets, token) =>
            {
                var found = await ReadWithinAsync(connection, transaction, cohortId, token).ConfigureAwait(false);

                if (found.IsFailure) return found.Error;

                CohortDto cohort = found.Value.Cohort;

                if (cohort.RootQuestId != root.Value) return Forbidden;

                var actor = await AuthorizeNoteWithinAsync(connection, transaction, signets, caller, cohort, true, token).ConfigureAwait(false);

                if (actor.IsFailure) return actor.Error;

                if (toolAdmission is not null)
                {
                    if (!ReferenceEquals(toolAdmission.Registry.Parent.Driver.Caller, caller)
                        || !GuildMarketToolArguments.MatchesAppend(toolAdmission, cohortId, request)) return Forbidden;

                    var admitted = await quests.AuthorizeCohortToolWithinAsync(connection, transaction, signets, toolAdmission, token).ConfigureAwait(false);

                    if (admitted.IsFailure) return admitted.Error;

                    acceptedQuest = admitted.Value.Quest;
                }

                Guid? prior = null;

                await using (var replay = Command(connection, transaction, "SELECT Id,RequestDigest FROM guild_cohort_ledger WHERE CohortId=$cohort AND ActorKey=$actor AND IdempotencyKey=$key"))
                {
                    Add(replay, "$cohort", Id(cohortId));
                    Add(replay, "$actor", actor.Value.Key);
                    Add(replay, "$key", Id(request.IdempotencyKey));

                    await using var reader = await replay.ExecuteReaderAsync(token).ConfigureAwait(false);

                    if (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        if (reader.GetValue(1) is not byte[] previous || !CryptographicOperations.FixedTimeEquals(previous, digest))
                            return new Error(ErrorCodes.Guild.IdempotencyConflict, "The note identity was accepted with different input.");

                        prior = Guid.Parse(reader.GetString(0));
                    }
                }

                if (prior is { } existing) return await ReadNoteWithinAsync(connection, transaction, cohortId, existing, true, token).ConfigureAwait(false);

                if (cohort.State is not (GuildCohortState.Open or GuildCohortState.Closed) || cohort.Revision != request.ExpectedRevision)
                    return RevisionConflict(cohort);

                if (cohort.Revision == long.MaxValue) return Unavailable;

                if (request.CorrectsNoteId is { } corrected)
                {
                    var target = await ReadNoteWithinAsync(connection, transaction, cohortId, corrected, false, token).ConfigureAwait(false);

                    if (target.IsFailure || target.Value.BodyRef is null) return new Error(ErrorCodes.Guild.NotFound, "The note to correct is unavailable.");
                }

                long sequence;

                await using (var counter = Command(connection, transaction, "SELECT NoteSequence FROM guild_cohorts WHERE Id=$cohort"))
                {
                    Add(counter, "$cohort", Id(cohortId));

                    if (await counter.ExecuteScalarAsync(token).ConfigureAwait(false) is not long current || current == long.MaxValue) return Unavailable;

                    sequence = current + 1;
                }

                Guid id = Guid.NewGuid();
                Guid content = Guid.NewGuid();
                Guid receipt = Guid.NewGuid();
                DateTimeOffset now = time.GetUtcNow();

                var label = await GuildQuestStore.ResultSensitivityWithinAsync(connection, transaction, actor.Value.Source, content, body, null, token).ConfigureAwait(false);

                if (label.IsFailure) return label.Error;

                var written = await GuildStore.WriteContentWithinAsync(connection, transaction,
                    new(content, id, GuildContentKind.CohortLedgerEntry, 1, actor.Value.Source.Quest.SessionId,
                        actor.Value.Source.Quest.CampaignId, "text/plain", body, label.Value), now, token).ConfigureAwait(false);

                if (written.IsFailure) return written.Error;

                var appendedEvent = await GuildPortalEvents.AppendWithinAsync(connection, transaction, cohort.RootQuestId,
                    actor.Value.Source.Quest.Id, GuildEventKind.NoteAppended, id, 1, null, now, token).ConfigureAwait(false);

                if (appendedEvent.IsFailure) return appendedEvent.Error;

                var ev = await GuildPortalEvents.AppendWithinAsync(connection, transaction, cohort.RootQuestId, cohort.IssuerQuestId,
                    GuildEventKind.CohortChanged, cohortId, cohort.Revision + 1, null, now, token).ConfigureAwait(false);

                if (ev.IsFailure) return ev.Error;

                await using (var update = Command(connection, transaction, "UPDATE guild_cohorts SET Revision=Revision+1,NoteSequence=$sequence,EventWatermark=$event WHERE Id=$cohort AND Revision=$revision AND NoteSequence=$previous AND StateCode IN(1,2)"))
                {
                    Add(update, "$sequence", sequence);
                    Add(update, "$previous", sequence - 1);
                    Add(update, "$event", ev.Value);
                    Add(update, "$cohort", Id(cohortId));
                    Add(update, "$revision", cohort.Revision);

                    if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return Conflict;
                }

                await using (var insert = Command(connection, transaction, """
                    INSERT INTO guild_cohort_ledger(Id,CohortId,Sequence,AuthorQuestId,AuthorMemberId,OperatorPrincipalId,
                        AuthorityCode,KindCode,SubjectId,CorrectsNoteId,ContentId,SessionId,CampaignId,Digest,ByteLength,
                        ActorKey,IdempotencyKey,RequestDigest,ReceiptId,AcceptedRevision,CreatedAtUtc)
                    VALUES($id,$cohort,$sequence,$quest,$member,$operator,$authority,$kind,$subject,$corrects,$content,
                        $session,$campaign,$digest,$bytes,$actor,$key,$request,$receipt,$revision,$now)
                    """))
                {
                    Add(insert, "$id", Id(id));
                    Add(insert, "$cohort", Id(cohortId));
                    Add(insert, "$sequence", sequence);
                    Add(insert, "$quest", caller.Kind == GuildCallerKind.Run ? Id(actor.Value.Source.Quest.Id) : null);
                    Add(insert, "$member", actor.Value.Member is { } member ? Id(member) : null);
                    Add(insert, "$operator", caller.Kind == GuildCallerKind.Operator ? Id(caller.PrincipalId!.Value) : null);
                    Add(insert, "$authority", (int)actor.Value.Kind);
                    Add(insert, "$kind", (int)request.Kind);
                    Add(insert, "$subject", request.SubjectId is { } subject ? Id(subject) : null);
                    Add(insert, "$corrects", request.CorrectsNoteId is { } corrects ? Id(corrects) : null);
                    Add(insert, "$content", Id(content));
                    Add(insert, "$session", actor.Value.Source.Quest.SessionId is { } session ? Id(session) : null);
                    Add(insert, "$campaign", actor.Value.Source.Quest.CampaignId is { } campaign ? Id(campaign) : null);
                    Add(insert, "$digest", Convert.FromHexString(written.Value.Digest));
                    Add(insert, "$bytes", body.Length);
                    Add(insert, "$actor", actor.Value.Key);
                    Add(insert, "$key", Id(request.IdempotencyKey));
                    Add(insert, "$request", digest);
                    Add(insert, "$receipt", Id(receipt));
                    Add(insert, "$revision", cohort.Revision + 1);
                    Add(insert, "$now", UtcInstantText.Format(now));

                    await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                var notification = await new GuildPortalStore(quests, authority, policy, time).PublishDistributionWithinAsync(
                    connection, transaction, signets, caller, actor.Value.Source, cohort,
                    new("note.appended", cohortId, id, sequence), Guid.NewGuid(), null, now, token).ConfigureAwait(false);

                if (notification.IsFailure) return notification.Error;

                return await ReadNoteWithinAsync(connection, transaction, cohortId, id, false, token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

        if (result.IsSuccess && toolAdmission is not null && acceptedQuest is not null)
            toolAdmission.RecordCommittedProjection(acceptedQuest, JsonSerializer.SerializeToElement(result.Value, GuildJsonContext.Default.SharedNoteDto));

        return result;
    }

    private Task<Result<Guid>> NoteRootAsync(GuildCallerContext caller, Guid cohort, CancellationToken ct) =>
        caller.Kind == GuildCallerKind.Run && caller.Execution is { } execution ? Task.FromResult(Result<Guid>.Success(execution.RootQuestId))
            : caller.Kind == GuildCallerKind.Operator ? guild.FindCohortRootAsync(caller, policy, cohort, ct)
            : Task.FromResult(Result<Guid>.Failure(Forbidden));

    private async Task<Result<NoteActor>> AuthorizeNoteWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        SignetAuthority.QuestTransaction signets, GuildCallerContext caller, CohortDto cohort, bool append, CancellationToken ct)
    {
        Guid sourceId = caller.Kind == GuildCallerKind.Run && caller.Execution is { } execution ? execution.QuestId : cohort.IssuerQuestId;

        var source = await GuildQuestStore.ReadWithinAsync(connection, transaction, sourceId, ct).ConfigureAwait(false);

        if (source.IsFailure) return source.Error;

        var visible = await quests.RequireVisibleAsync(connection, transaction, caller, source.Value, ct).ConfigureAwait(false);

        if (visible.IsFailure) return visible.Error;

        if (source.Value.Quest.RootQuestId != cohort.RootQuestId) return Forbidden;

        if (caller.Kind == GuildCallerKind.Operator)
        {
            if (append)
            {
                var current = await signets.ValidateCurrentGrantAsync(cohort.IssuerQuestId, ct).ConfigureAwait(false);

                if (current.IsFailure) return current.Error;
            }

            return new NoteActor(source.Value, null, GuildAuthorityKind.Operator, "operator:" + Id(caller.PrincipalId!.Value) + ":" + Id(caller.InstallationGeneration));
        }

        if (caller.Kind != GuildCallerKind.Run || caller.Execution is null) return Forbidden;

        var live = await signets.VerifyAsync(caller.Execution, ct).ConfigureAwait(false);

        if (live.IsFailure) return live.Error;

        if (sourceId == cohort.IssuerQuestId) return new NoteActor(source.Value, null, GuildAuthorityKind.Parent, "quest:" + Id(sourceId) + ":" + Id(caller.InstallationGeneration));

        await using var memberQuery = Command(connection, transaction, """
            SELECT m.Id FROM guild_cohort_members m JOIN guild_cohort_member_quests a ON a.MemberId=m.Id
            WHERE m.CohortId=$cohort AND m.StateCode=1 AND a.Active=1 AND a.QuestId=$quest AND m.AgentId=$agent
            """);

        Add(memberQuery, "$cohort", Id(cohort.Id));
        Add(memberQuery, "$quest", Id(sourceId));
        Add(memberQuery, "$agent", Id(source.Value.Quest.AgentId));

        if (await memberQuery.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string value || !Guid.TryParseExact(value, "D", out Guid member)) return Forbidden;

        return new NoteActor(source.Value, member, GuildAuthorityKind.Peer, "quest:" + Id(sourceId) + ":" + Id(caller.InstallationGeneration));
    }
}
