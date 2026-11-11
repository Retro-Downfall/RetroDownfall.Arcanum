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
    internal async Task<Result<CohortDto>> CreateAsync(GuildCallerContext caller, CreateCohortRequest request,
        CancellationToken ct, GuildChildToolAdmission? toolAdmission = null)
    {
        _ = guild;

        if (caller?.Kind != GuildCallerKind.Run || caller.Execution is not { } execution
            || request is null || request.IdempotencyKey == Guid.Empty || request.IssuerQuestId != execution.QuestId
            || request.Name is null || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 65_536
            || Encoding.UTF8.GetByteCount(request.Name) > 65_536 || request.Policy is null || request.ResultContract is null
            || !Enum.IsDefined(request.Policy.Kind) || request.ResultContract.MimeType is not ("text/plain" or "application/json")
            || (request.Policy.Kind == GuildConvergenceKind.Quorum ? request.Policy.Quorum is not > 0 : request.Policy.Quorum is not null)
            || policy is not GuildLocalHostPolicy local)
            return new Error(ErrorCodes.Guild.InvalidRequest, "The Cohort request is invalid.");

        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(request, GuildJsonContext.Default.CreateCohortRequest);

        if (canonical.Length > 65_536) return new Error(ErrorCodes.Guild.PayloadTooLarge, "The Cohort request exceeds its record boundary.");

        byte[] digest = SHA256.HashData(canonical);

        GuildQuestDto? acceptedParent = null;

        Result<CohortDto> result = await InCohortToolTransactionAsync(execution.RootQuestId, toolAdmission, GuildJsonContext.Default.CohortDto,
            async (connection, transaction, signets, token) =>
            {
                Result allowed = await policy.AuthorizeLocateAsync(caller, connection, transaction, execution.QuestId, null, null, token).ConfigureAwait(false);

                if (allowed.IsFailure) return allowed.Error;

                Result<GuildGrant> live = await signets.VerifyAsync(execution, token).ConfigureAwait(false);

                if (live.IsFailure) return live.Error;

                if (!live.Value.Delegation.Contains(GuildDelegationKind.Cohort)) return Forbidden;

                var found = await GuildQuestStore.ReadWithinAsync(connection, transaction, execution.QuestId, token).ConfigureAwait(false);

                if (found.IsFailure) return found.Error;

                Result visible = await quests.RequireVisibleAsync(connection, transaction, caller, found.Value, token).ConfigureAwait(false);

                if (visible.IsFailure) return visible.Error;

                if (found.Value.Quest.RootQuestId != execution.RootQuestId) return Forbidden;

                if (toolAdmission is not null)
                {
                    if (!ReferenceEquals(toolAdmission.Registry.Parent.Driver.Caller, caller)
                        || !GuildCohortToolArguments.MatchesCreate(toolAdmission, request)) return Forbidden;

                    var tool = await quests.AuthorizeCohortToolWithinAsync(connection, transaction, signets, toolAdmission, token).ConfigureAwait(false);

                    if (tool.IsFailure) return tool.Error;

                    if (tool.Value.Quest.Id != execution.QuestId) return Forbidden;

                    acceptedParent = tool.Value.Quest;
                }

                await using (SqliteCommand prior = Command(connection, transaction, """
                    SELECT CohortId,ReceiptId,AcceptedRevision,AcceptedAtUtc,RequestDigest FROM guild_cohort_mutations
                    WHERE IssuerQuestId=$issuer AND ActionCode=1 AND ParentId=$issuer AND IdempotencyKey=$key
                    """))
                {
                    Add(prior, "$issuer", Id(execution.QuestId));
                    Add(prior, "$key", Id(request.IdempotencyKey));

                    await using SqliteDataReader reader = await prior.ExecuteReaderAsync(token).ConfigureAwait(false);

                    if (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        if (reader.GetValue(4) is not byte[] existing || !CryptographicOperations.FixedTimeEquals(existing, digest))
                            return new Error(ErrorCodes.Guild.IdempotencyConflict, "The Cohort mutation identity was already used for different input.");

                        Guid cohortId = Guid.Parse(reader.GetString(0));
                        Guid receiptId = Guid.Parse(reader.GetString(1));
                        long revision = reader.GetInt64(2);
                        DateTimeOffset accepted = DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture);

                        await reader.DisposeAsync().ConfigureAwait(false);

                        Result<GuildCohortStored> existingCohort = await ReadWithinAsync(connection, transaction, cohortId, token).ConfigureAwait(false);

                        return existingCohort.IsFailure ? existingCohort.Error : existingCohort.Value.Cohort with
                        {
                            Receipt = new(receiptId, request.IdempotencyKey, true, accepted, revision),
                        };
                    }
                }

                if (await GuildQuestStore.HasClosedAncestorAsync(connection, transaction, execution.QuestId, token).ConfigureAwait(false)) return Forbidden;

                Result<GuildGrant> ceiling = await local.ResolveCohortGrantWithinAsync(connection, transaction, live.Value, request.RequestedGrant, token).ConfigureAwait(false);

                if (ceiling.IsFailure) return ceiling.Error;

                Result<string> grant = GuildSignetGrantJson.Encode(ceiling.Value);

                if (grant.IsFailure) return grant.Error;

                Guid id = Guid.NewGuid();

                Result<Guid> account = await budgets.AllocateAccountWithinAsync(connection, transaction, GuildBudgetOwnerKind.Cohort,
                    id, found.Value.AllocationAccountId, ceiling.Value.Budget, token).ConfigureAwait(false);

                if (account.IsFailure) return account.Error;

                DateTimeOffset now = time.GetUtcNow();

                Result<long> eventSequence = await GuildPortalEvents.AppendWithinAsync(connection, transaction,
                    execution.RootQuestId, execution.QuestId, GuildEventKind.CohortChanged, id, 1, null, now, token).ConfigureAwait(false);

                if (eventSequence.IsFailure) return eventSequence.Error;

                await using (SqliteCommand insert = Command(connection, transaction, """
                    INSERT INTO guild_cohorts(Id,RootQuestId,IssuerQuestId,CampaignId,Name,PolicyCode,Quorum,MimeType,
                        GrantJson,AllocationAccountId,StateCode,Revision,MembershipVersion,EventWatermark)
                    VALUES($id,$root,$issuer,$campaign,$name,$policy,$quorum,$mime,$grant,$account,1,1,0,$watermark)
                    """))
                {
                    Add(insert, "$id", Id(id));
                    Add(insert, "$root", Id(execution.RootQuestId));
                    Add(insert, "$issuer", Id(execution.QuestId));
                    Add(insert, "$campaign", found.Value.Quest.CampaignId is { } campaign ? Id(campaign) : null);
                    Add(insert, "$name", request.Name);
                    Add(insert, "$policy", (int)request.Policy.Kind);
                    Add(insert, "$quorum", request.Policy.Quorum);
                    Add(insert, "$mime", request.ResultContract.MimeType);
                    Add(insert, "$grant", grant.Value);
                    Add(insert, "$account", Id(account.Value));
                    Add(insert, "$watermark", eventSequence.Value);

                    _ = await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                Guid receipt = Guid.NewGuid();

                await using (SqliteCommand mutation = Command(connection, transaction, """
                    INSERT INTO guild_cohort_mutations(IssuerQuestId,ActionCode,ParentId,IdempotencyKey,ReceiptId,AcceptedAtUtc,
                        RequestDigest,CohortId,AcceptedRevision)
                    VALUES($issuer,1,$issuer,$key,$receipt,$now,$digest,$cohort,1)
                    """))
                {
                    Add(mutation, "$issuer", Id(execution.QuestId));
                    Add(mutation, "$key", Id(request.IdempotencyKey));
                    Add(mutation, "$receipt", Id(receipt));
                    Add(mutation, "$now", UtcInstantText.Format(now));
                    Add(mutation, "$digest", digest);
                    Add(mutation, "$cohort", Id(id));

                    _ = await mutation.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                Result<GuildCohortStored> created = await ReadWithinAsync(connection, transaction, id, token).ConfigureAwait(false);

                return created.IsFailure ? created.Error : created.Value.Cohort with
                {
                    Receipt = new(receipt, request.IdempotencyKey, false, now, 1),
                };
            }, ct).ConfigureAwait(false);

        if (result.IsSuccess && toolAdmission is not null && acceptedParent is not null)
            toolAdmission.RecordCommittedProjection(acceptedParent, JsonSerializer.SerializeToElement(result.Value, GuildJsonContext.Default.CohortDto));

        return result;
    }

    internal Task<Result<CohortDto>> GetAsync(GuildCallerContext caller, Guid cohortId, CancellationToken ct)
    {
        if (caller?.Execution is not { } execution || cohortId == Guid.Empty)
            return Task.FromResult(Result<CohortDto>.Failure(Forbidden));

        return authority.InQuestTransactionAsync<CohortDto>(execution.RootQuestId, async (connection, transaction, signets, token) =>
        {
            Result<GuildGrant> live = await signets.VerifyAsync(execution, token).ConfigureAwait(false);

            if (live.IsFailure) return live.Error;

            Result<GuildCohortStored> found = await ReadWithinAsync(connection, transaction, cohortId, token).ConfigureAwait(false);

            if (found.IsFailure) return found.Error;

            CohortDto cohort = found.Value.Cohort;

            if (cohort.RootQuestId != execution.RootQuestId) return Forbidden;

            Result<GuildQuestStored> own = await GuildQuestStore.ReadWithinAsync(connection, transaction, execution.QuestId, token).ConfigureAwait(false);

            if (own.IsFailure) return own.Error;

            Result visible = await quests.RequireVisibleAsync(connection, transaction, caller, own.Value, token).ConfigureAwait(false);

            if (visible.IsFailure) return visible.Error;

            if (cohort.IssuerQuestId != execution.QuestId)
            {
                await using SqliteCommand membership = Command(connection, transaction, """
                    SELECT EXISTS(SELECT 1 FROM guild_cohort_member_quests a
                        JOIN guild_cohort_members m ON m.Id=a.MemberId
                        WHERE m.CohortId=$cohort AND m.StateCode=1 AND a.QuestId=$quest AND a.Active=1)
                    """);

                Add(membership, "$cohort", Id(cohortId));
                Add(membership, "$quest", Id(execution.QuestId));

                if (cohort.State is GuildCohortState.Completed or GuildCohortState.Failed or GuildCohortState.Canceled
                    || !Equals(1L, await membership.ExecuteScalarAsync(token).ConfigureAwait(false))) return Forbidden;
            }

            return cohort;
        }, ct);
    }

    internal async Task<Result<CohortDto>> CloseAsync(GuildCallerContext caller, Guid cohortId, GuildRevisionRequest request,
        CancellationToken ct, GuildChildToolAdmission? toolAdmission = null)
    {
        if (caller?.Execution is not { } execution || cohortId == Guid.Empty || request is null
            || request.IdempotencyKey == Guid.Empty || request.ExpectedRevision < 1) return Forbidden;

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes("close\n" + Id(cohortId) + "\n"
            + request.ExpectedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        GuildQuestDto? acceptedParent = null;

        Result<CohortDto> result = await InCohortToolTransactionAsync(execution.RootQuestId, toolAdmission, GuildJsonContext.Default.CohortDto,
            async (connection, transaction, signets, token) =>
            {
                Result<GuildGrant> live = await signets.VerifyAsync(execution, token).ConfigureAwait(false);

                if (live.IsFailure) return live.Error;

                Result<GuildQuestStored> issuer = await GuildQuestStore.ReadWithinAsync(connection, transaction, execution.QuestId, token).ConfigureAwait(false);

                if (issuer.IsFailure) return issuer.Error;

                Result visible = await quests.RequireVisibleAsync(connection, transaction, caller, issuer.Value, token).ConfigureAwait(false);

                if (visible.IsFailure) return visible.Error;

                Result<GuildCohortStored> found = await ReadWithinAsync(connection, transaction, cohortId, token).ConfigureAwait(false);

                if (found.IsFailure) return found.Error;

                CohortDto cohort = found.Value.Cohort;

                if (cohort.RootQuestId != execution.RootQuestId || cohort.IssuerQuestId != execution.QuestId
                    || !live.Value.Delegation.Contains(GuildDelegationKind.Cohort)) return Forbidden;

                if (toolAdmission is not null)
                {
                    if (!ReferenceEquals(toolAdmission.Registry.Parent.Driver.Caller, caller)
                        || !GuildCohortToolArguments.MatchesRevision(toolAdmission, cohortId, null, request)) return Forbidden;

                    Result<GuildQuestStored> tool = await quests.AuthorizeCohortToolWithinAsync(connection, transaction, signets, toolAdmission, token).ConfigureAwait(false);

                    if (tool.IsFailure) return tool.Error;

                    if (tool.Value.Quest.Id != execution.QuestId) return Forbidden;

                    acceptedParent = tool.Value.Quest;
                }

                await using (SqliteCommand prior = Command(connection, transaction, """
                    SELECT ReceiptId,AcceptedRevision,AcceptedAtUtc,RequestDigest FROM guild_cohort_mutations
                    WHERE IssuerQuestId=$issuer AND ActionCode=2 AND ParentId=$cohort AND IdempotencyKey=$key
                    """))
                {
                    Add(prior, "$issuer", Id(execution.QuestId));
                    Add(prior, "$cohort", Id(cohortId));
                    Add(prior, "$key", Id(request.IdempotencyKey));

                    await using SqliteDataReader reader = await prior.ExecuteReaderAsync(token).ConfigureAwait(false);

                    if (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        if (reader.GetValue(3) is not byte[] existing || !CryptographicOperations.FixedTimeEquals(existing, digest))
                            return new Error(ErrorCodes.Guild.IdempotencyConflict, "The Cohort mutation identity was already used for different input.");

                        return cohort with { Receipt = new(Guid.Parse(reader.GetString(0)), request.IdempotencyKey, true,
                            DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture), reader.GetInt64(1)) };
                    }
                }

                if (cohort.State != GuildCohortState.Open || cohort.Revision != request.ExpectedRevision) return RevisionConflict(cohort);

                if (await GuildQuestStore.HasClosedAncestorAsync(connection, transaction, execution.QuestId, token).ConfigureAwait(false)) return Forbidden;

                await using (SqliteCommand count = Command(connection, transaction,
                    "SELECT ActiveMemberCount FROM guild_cohorts WHERE Id=$cohort"))
                {
                    Add(count, "$cohort", Id(cohortId));

                    long voters = (long)(await count.ExecuteScalarAsync(token).ConfigureAwait(false) ?? 0L);

                    if (voters == 0 || cohort.Policy.Kind == GuildConvergenceKind.Quorum && cohort.Policy.Quorum > voters)
                        return new Error(ErrorCodes.Guild.ConvergenceInvalid, "The Cohort cannot close without enough active voters.");
                }

                if (cohort.Revision == long.MaxValue) return Unavailable;

                DateTimeOffset now = time.GetUtcNow();

                Result<long> eventSequence = await GuildPortalEvents.AppendWithinAsync(connection, transaction,
                    cohort.RootQuestId, cohort.IssuerQuestId, GuildEventKind.CohortChanged, cohort.Id,
                    cohort.Revision + 1, null, now, token).ConfigureAwait(false);

                if (eventSequence.IsFailure) return eventSequence.Error;

                await using (SqliteCommand update = Command(connection, transaction, """
                    UPDATE guild_cohorts SET StateCode=2,ClosedMembershipVersion=MembershipVersion,FrozenMemberCount=ActiveMemberCount,EventWatermark=$watermark,Revision=Revision+1
                    WHERE Id=$cohort AND StateCode=1 AND Revision=$revision
                    """))
                {
                    Add(update, "$cohort", Id(cohortId));
                    Add(update, "$watermark", eventSequence.Value);
                    Add(update, "$revision", cohort.Revision);

                    if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return Conflict;
                }

                Guid receipt = Guid.NewGuid();

                await using (SqliteCommand mutation = Command(connection, transaction, """
                    INSERT INTO guild_cohort_mutations(IssuerQuestId,ActionCode,ParentId,IdempotencyKey,ReceiptId,AcceptedAtUtc,
                        RequestDigest,CohortId,AcceptedRevision)
                    VALUES($issuer,2,$cohort,$key,$receipt,$now,$digest,$cohort,$revision)
                    """))
                {
                    Add(mutation, "$issuer", Id(execution.QuestId));
                    Add(mutation, "$cohort", Id(cohortId));
                    Add(mutation, "$key", Id(request.IdempotencyKey));
                    Add(mutation, "$receipt", Id(receipt));
                    Add(mutation, "$now", UtcInstantText.Format(now));
                    Add(mutation, "$digest", digest);
                    Add(mutation, "$revision", cohort.Revision + 1);

                    _ = await mutation.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                var closed = await ReadWithinAsync(connection, transaction, cohortId, token).ConfigureAwait(false);

                return closed.IsFailure ? closed.Error : closed.Value.Cohort with
                {
                    Receipt = new(receipt, request.IdempotencyKey, false, now, cohort.Revision + 1),
                };
            }, ct).ConfigureAwait(false);

        if (result.IsSuccess && toolAdmission is not null && acceptedParent is not null)
            toolAdmission.RecordCommittedProjection(acceptedParent, JsonSerializer.SerializeToElement(result.Value, GuildJsonContext.Default.CohortDto));

        return result;
    }

    internal async Task<Result<CohortMemberDto>> RemoveAsync(GuildCallerContext caller, Guid cohortId, Guid memberId,
        GuildRevisionRequest request, CancellationToken ct, GuildChildToolAdmission? toolAdmission = null)
    {
        if (caller?.Execution is not { } execution || cohortId == Guid.Empty || memberId == Guid.Empty
            || request is null || request.IdempotencyKey == Guid.Empty || request.ExpectedRevision < 1) return Forbidden;

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes("remove\n" + Id(cohortId) + "\n" + Id(memberId) + "\n"
            + request.ExpectedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        GuildQuestDto? acceptedParent = null;

        Result<CohortMemberDto> result = await InCohortToolTransactionAsync(execution.RootQuestId, toolAdmission, GuildJsonContext.Default.CohortMemberDto,
            async (connection, transaction, signets, token) =>
            {
                Result<GuildGrant> live = await signets.VerifyAsync(execution, token).ConfigureAwait(false);

                if (live.IsFailure) return live.Error;

                Result<GuildQuestStored> issuer = await GuildQuestStore.ReadWithinAsync(connection, transaction, execution.QuestId, token).ConfigureAwait(false);

                if (issuer.IsFailure) return issuer.Error;

                Result visible = await quests.RequireVisibleAsync(connection, transaction, caller, issuer.Value, token).ConfigureAwait(false);

                if (visible.IsFailure) return visible.Error;

                Result<GuildCohortStored> found = await ReadWithinAsync(connection, transaction, cohortId, token).ConfigureAwait(false);

                if (found.IsFailure) return found.Error;

                CohortDto cohort = found.Value.Cohort;

                if (cohort.RootQuestId != execution.RootQuestId || cohort.IssuerQuestId != execution.QuestId
                    || !live.Value.Delegation.Contains(GuildDelegationKind.Cohort)) return Forbidden;

                if (toolAdmission is not null)
                {
                    if (!ReferenceEquals(toolAdmission.Registry.Parent.Driver.Caller, caller)
                        || !GuildCohortToolArguments.MatchesRevision(toolAdmission, cohortId, memberId, request)) return Forbidden;

                    Result<GuildQuestStored> tool = await quests.AuthorizeCohortToolWithinAsync(connection, transaction, signets, toolAdmission, token).ConfigureAwait(false);

                    if (tool.IsFailure) return tool.Error;

                    if (tool.Value.Quest.Id != execution.QuestId) return Forbidden;

                    acceptedParent = tool.Value.Quest;
                }

                Result<CohortMemberDto> member = await ReadMemberWithinAsync(connection, transaction, cohort, memberId, token).ConfigureAwait(false);

                if (member.IsFailure) return member.Error;

                await using (SqliteCommand prior = Command(connection, transaction, """
                    SELECT ReceiptId,AcceptedRevision,AcceptedAtUtc,RequestDigest FROM guild_cohort_mutations
                    WHERE IssuerQuestId=$issuer AND ActionCode=3 AND ParentId=$cohort AND IdempotencyKey=$key
                    """))
                {
                    Add(prior, "$issuer", Id(execution.QuestId));
                    Add(prior, "$cohort", Id(cohortId));
                    Add(prior, "$key", Id(request.IdempotencyKey));

                    await using SqliteDataReader reader = await prior.ExecuteReaderAsync(token).ConfigureAwait(false);

                    if (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        if (reader.GetValue(3) is not byte[] existing || !CryptographicOperations.FixedTimeEquals(existing, digest))
                            return new Error(ErrorCodes.Guild.IdempotencyConflict, "The Cohort mutation identity was already used for different input.");

                        return member.Value with { Receipt = new(Guid.Parse(reader.GetString(0)), request.IdempotencyKey, true,
                            DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture), reader.GetInt64(1)) };
                    }
                }

                if (cohort.State is not (GuildCohortState.Open or GuildCohortState.Closed)
                    || cohort.Revision != request.ExpectedRevision) return RevisionConflict(cohort);

                if (member.Value.State != GuildCohortMemberState.Active) return Conflict;

                if (cohort.Revision == long.MaxValue || cohort.MembershipVersion == long.MaxValue) return Unavailable;

                if (await GuildQuestStore.HasClosedAncestorAsync(connection, transaction, execution.QuestId, token).ConfigureAwait(false)) return Forbidden;

                DateTimeOffset now = time.GetUtcNow();

                Result<long> projected = await GuildPortalEvents.AppendWithinAsync(connection, transaction, cohort.RootQuestId,
                    cohort.IssuerQuestId, GuildEventKind.MembershipChanged, cohort.Id, cohort.Revision + 1, null, now, token).ConfigureAwait(false);

                if (projected.IsFailure) return projected.Error;

                long membership = cohort.MembershipVersion + 1;

                await using (SqliteCommand update = Command(connection, transaction, """
                    UPDATE guild_cohort_members SET StateCode=2,RemovalSequence=$sequence
                    WHERE Id=$member AND CohortId=$cohort AND StateCode=1
                    """))
                {
                    Add(update, "$member", Id(memberId));
                    Add(update, "$cohort", Id(cohortId));
                    Add(update, "$sequence", membership);

                    if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return Conflict;
                }

                bool withdrewFrozen = false;

                if (cohort.State == GuildCohortState.Closed && member.Value.InClosedSnapshot)
                {
                    await using SqliteCommand existing = Command(connection, transaction,
                        "SELECT EXISTS(SELECT 1 FROM guild_cohort_contributions WHERE CohortId=$cohort AND MemberId=$member)");
                    Add(existing, "$cohort", Id(cohortId));
                    Add(existing, "$member", Id(memberId));
                    withdrewFrozen = !Equals(1L, await existing.ExecuteScalarAsync(token).ConfigureAwait(false));
                }

                await using (SqliteCommand update = Command(connection, transaction, """
                    UPDATE guild_cohorts SET MembershipVersion=$membership,ActiveMemberCount=ActiveMemberCount-1,EventWatermark=$event,
                        ResultSequence=ResultSequence+$vote,Revision=Revision+1
                    WHERE Id=$cohort AND Revision=$revision AND ActiveMemberCount>0 AND ($vote=0 OR ResultSequence<9223372036854775807)
                    RETURNING ResultSequence
                    """))
                {
                    Add(update, "$membership", membership);
                    Add(update, "$event", projected.Value);
                    Add(update, "$vote", withdrewFrozen ? 1 : 0);
                    Add(update, "$cohort", Id(cohortId));
                    Add(update, "$revision", cohort.Revision);

                    object? sequence = await update.ExecuteScalarAsync(token).ConfigureAwait(false);

                    if (sequence is not long resultSequence) return Conflict;

                    if (withdrewFrozen)
                    {
                        await using SqliteCommand vote = Command(connection, transaction, """
                            INSERT INTO guild_cohort_contributions(CohortId,MemberId,PrimaryVotingQuestId,ResultSequence,OutcomeCode)
                            VALUES($cohort,$member,$quest,$sequence,5)
                            """);
                        Add(vote, "$cohort", Id(cohortId));
                        Add(vote, "$member", Id(memberId));
                        Add(vote, "$quest", Id(member.Value.PrimaryVotingQuestId));
                        Add(vote, "$sequence", resultSequence);

                        _ = await vote.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                }

                await using (SqliteCommand frontier = Command(connection, transaction, """
                    INSERT INTO guild_cohort_stop_frontiers(Id,CohortId,MemberId) VALUES($id,$cohort,$member)
                    """))
                {
                    Add(frontier, "$id", Id(Guid.NewGuid()));
                    Add(frontier, "$cohort", Id(cohortId));
                    Add(frontier, "$member", Id(memberId));

                    _ = await frontier.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                Guid receipt = Guid.NewGuid();

                await using (SqliteCommand mutation = Command(connection, transaction, """
                    INSERT INTO guild_cohort_mutations(IssuerQuestId,ActionCode,ParentId,IdempotencyKey,ReceiptId,AcceptedAtUtc,
                        RequestDigest,CohortId,AcceptedRevision)
                    VALUES($issuer,3,$cohort,$key,$receipt,$now,$digest,$cohort,$revision)
                    """))
                {
                    Add(mutation, "$issuer", Id(execution.QuestId));
                    Add(mutation, "$cohort", Id(cohortId));
                    Add(mutation, "$key", Id(request.IdempotencyKey));
                    Add(mutation, "$receipt", Id(receipt));
                    Add(mutation, "$now", UtcInstantText.Format(now));
                    Add(mutation, "$digest", digest);
                    Add(mutation, "$revision", cohort.Revision + 1);

                    _ = await mutation.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                Result<CohortMemberDto> withdrawn = await ReadMemberWithinAsync(connection, transaction, cohort with
                {
                    MembershipVersion = membership,
                }, memberId, token).ConfigureAwait(false);

                return withdrawn.IsFailure ? withdrawn.Error : withdrawn.Value with
                {
                    Receipt = new(receipt, request.IdempotencyKey, false, now, cohort.Revision + 1),
                };
            }, ct).ConfigureAwait(false);

        if (result.IsSuccess && toolAdmission is not null && acceptedParent is not null)
            toolAdmission.RecordCommittedProjection(acceptedParent, JsonSerializer.SerializeToElement(result.Value, GuildJsonContext.Default.CohortMemberDto));

        return result;
    }

    private static async Task<Result<CohortMemberDto>> ReadMemberWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        CohortDto cohort, Guid memberId, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT Id,PrimaryVotingQuestId,AgentId,AdmissionSequence,StateCode,RemovalSequence,
                (SELECT COUNT(*) FROM guild_cohort_member_quests a WHERE a.MemberId=m.Id)
            FROM guild_cohort_members m WHERE m.Id=$member AND m.CohortId=$cohort
            """);

        Add(command, "$member", Id(memberId));
        Add(command, "$cohort", Id(cohort.Id));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Forbidden;

        long admitted = reader.GetInt64(3);
        long? removed = reader.IsDBNull(5) ? null : reader.GetInt64(5);
        bool frozen = cohort.ClosedMembershipVersion is { } cutoff && admitted <= cutoff && (removed is null || removed > cutoff);

        return new CohortMemberDto(Guid.Parse(reader.GetString(0)), cohort.Id, Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)), admitted, (GuildCohortMemberState)reader.GetInt32(4), frozen, removed, reader.GetInt64(6));
    }
}
