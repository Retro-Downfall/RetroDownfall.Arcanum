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
    internal async Task<Result<CohortDto>> GetOperatorCohortAsync(GuildCallerContext caller, Guid cohortId, CancellationToken ct)
    {
        if (caller is null || caller.Kind != GuildCallerKind.Operator || cohortId == Guid.Empty) return Forbidden;

        Result<Guid> root = await NoteRootAsync(caller, cohortId, ct).ConfigureAwait(false);

        if (root.IsFailure) return root.Error;

        return await authority.InQuestTransactionAsync<CohortDto>(root.Value, async (connection, transaction, _, token) =>
        {
            Result<GuildCohortStored> cohort = await ReadWithinAsync(connection, transaction, cohortId, token).ConfigureAwait(false);

            if (cohort.IsFailure) return cohort.Error;

            Result<GuildQuestStored> issuer = await GuildQuestStore.ReadWithinAsync(connection, transaction, cohort.Value.Cohort.IssuerQuestId, token).ConfigureAwait(false);

            if (issuer.IsFailure) return issuer.Error;

            Result visible = await quests.RequireVisibleAsync(connection, transaction, caller, issuer.Value, token).ConfigureAwait(false);

            return visible.IsFailure ? visible.Error : cohort.Value.Cohort with { Receipt = null };
        }, ct).ConfigureAwait(false);
    }

    internal async Task<Result<CohortDto>> CreateOperatorAsync(GuildCallerContext caller, CreateCohortRequest request, CancellationToken ct)
    {
        if (caller is null || caller.Kind != GuildCallerKind.Operator || caller.PrincipalId is not { } principal || request is null
            || request.IdempotencyKey == Guid.Empty || request.IssuerQuestId == Guid.Empty || string.IsNullOrWhiteSpace(request.Name)
            || Encoding.UTF8.GetByteCount(request.Name) > 65_536 || request.Policy is null || request.ResultContract is null
            || !Enum.IsDefined(request.Policy.Kind) || request.ResultContract.MimeType is not ("text/plain" or "application/json")
            || (request.Policy.Kind == GuildConvergenceKind.Quorum ? request.Policy.Quorum is not > 0 : request.Policy.Quorum is not null)
            || policy is not GuildLocalHostPolicy local) return new Error(ErrorCodes.Guild.InvalidRequest, "The Cohort request is invalid.");

        byte[] body = JsonSerializer.SerializeToUtf8Bytes(request, GuildJsonContext.Default.CreateCohortRequest);

        if (body.Length > 65_536) return new Error(ErrorCodes.Guild.PayloadTooLarge, "The Cohort request exceeds its record boundary.");

        byte[] digest = SHA256.HashData(body);

        Result<GuildQuestStored> located = await quests.GetAsync(caller, request.IssuerQuestId, ct).ConfigureAwait(false);

        if (located.IsFailure) return located.Error;

        return await authority.InQuestTransactionAsync<CohortDto>(located.Value.Quest.RootQuestId, async (connection, transaction, signets, token) =>
        {
            Result<GuildQuestStored> issuer = await GuildQuestStore.ReadWithinAsync(connection, transaction, request.IssuerQuestId, token).ConfigureAwait(false);
            if (issuer.IsFailure) return issuer.Error;
            Result visible = await quests.RequireVisibleAsync(connection, transaction, caller, issuer.Value, token).ConfigureAwait(false);
            if (visible.IsFailure) return visible.Error;
            if (issuer.Value.Quest.RootQuestId != located.Value.Quest.RootQuestId) return Forbidden;

            Result<GuildGrant> live = await signets.ValidateCurrentGrantAsync(request.IssuerQuestId, token).ConfigureAwait(false);
            if (live.IsFailure) return live.Error;
            if (!live.Value.Delegation.Contains(GuildDelegationKind.Cohort)) return Forbidden;

            await using (SqliteCommand prior = Command(connection, transaction, """
                SELECT CohortId,ReceiptId,AcceptedAtUtc,AcceptedRevision,RequestDigest
                FROM guild_operator_cohort_mutations WHERE InstallationGeneration=$generation AND PrincipalId=$principal
                    AND ActionCode=1 AND ParentId=$issuer AND IdempotencyKey=$key
                """))
            {
                Add(prior, "$generation", Id(caller.InstallationGeneration));
                Add(prior, "$principal", Id(principal));
                Add(prior, "$issuer", Id(request.IssuerQuestId));
                Add(prior, "$key", Id(request.IdempotencyKey));
                await using SqliteDataReader reader = await prior.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    if (reader.GetValue(4) is not byte[] existing || !CryptographicOperations.FixedTimeEquals(existing, digest))
                        return new Error(ErrorCodes.Guild.IdempotencyConflict, "The Cohort mutation identity was already used for different input.");
                    Guid cohortId = Guid.Parse(reader.GetString(0));
                    Guid receiptId = Guid.Parse(reader.GetString(1));
                    DateTimeOffset accepted = UtcInstantText.Parse(reader.GetString(2));
                    long revision = reader.GetInt64(3);
                    await reader.DisposeAsync().ConfigureAwait(false);
                    Result<GuildCohortStored> current = await ReadWithinAsync(connection, transaction, cohortId, token).ConfigureAwait(false);
                    return current.IsFailure ? current.Error : current.Value.Cohort with { Receipt = new(receiptId, request.IdempotencyKey, true, accepted, revision) };
                }
            }

            if (await GuildQuestStore.HasClosedAncestorAsync(connection, transaction, request.IssuerQuestId, token).ConfigureAwait(false)) return Forbidden;
            Result<GuildGrant> ceiling = await local.ResolveCohortGrantWithinAsync(connection, transaction, live.Value, request.RequestedGrant, token).ConfigureAwait(false);
            if (ceiling.IsFailure) return ceiling.Error;
            Result<string> grant = GuildSignetGrantJson.Encode(ceiling.Value);
            if (grant.IsFailure) return grant.Error;

            Guid id = Guid.NewGuid();
            Result<Guid> account = await budgets.AllocateAccountWithinAsync(connection, transaction, GuildBudgetOwnerKind.Cohort,
                id, issuer.Value.AllocationAccountId, ceiling.Value.Budget, token).ConfigureAwait(false);
            if (account.IsFailure) return account.Error;
            DateTimeOffset now = time.GetUtcNow();
            Result<long> watermark = await GuildPortalEvents.AppendWithinAsync(connection, transaction, issuer.Value.Quest.RootQuestId,
                request.IssuerQuestId, GuildEventKind.CohortChanged, id, 1, null, now, token).ConfigureAwait(false);
            if (watermark.IsFailure) return watermark.Error;

            await using (SqliteCommand insert = Command(connection, transaction, """
                INSERT INTO guild_cohorts(Id,RootQuestId,IssuerQuestId,CampaignId,Name,PolicyCode,Quorum,MimeType,
                    GrantJson,AllocationAccountId,StateCode,Revision,MembershipVersion,EventWatermark)
                VALUES($id,$root,$issuer,$campaign,$name,$policy,$quorum,$mime,$grant,$account,1,1,0,$watermark)
                """))
            {
                Add(insert, "$id", Id(id)); Add(insert, "$root", Id(issuer.Value.Quest.RootQuestId));
                Add(insert, "$issuer", Id(request.IssuerQuestId)); Add(insert, "$campaign", issuer.Value.Quest.CampaignId is { } campaign ? Id(campaign) : null);
                Add(insert, "$name", request.Name); Add(insert, "$policy", (int)request.Policy.Kind);
                Add(insert, "$quorum", request.Policy.Quorum); Add(insert, "$mime", request.ResultContract.MimeType);
                Add(insert, "$grant", grant.Value); Add(insert, "$account", Id(account.Value)); Add(insert, "$watermark", watermark.Value);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            Guid receipt = Guid.NewGuid();
            await using (SqliteCommand mutation = Command(connection, transaction, """
                INSERT INTO guild_operator_cohort_mutations(InstallationGeneration,PrincipalId,ActionCode,ParentId,IdempotencyKey,
                    ReceiptId,AcceptedAtUtc,RequestDigest,CohortId,AcceptedRevision)
                VALUES($generation,$principal,1,$parent,$key,$receipt,$now,$digest,$cohort,1)
                """))
            {
                Add(mutation, "$generation", Id(caller.InstallationGeneration)); Add(mutation, "$principal", Id(principal));
                Add(mutation, "$parent", Id(request.IssuerQuestId)); Add(mutation, "$key", Id(request.IdempotencyKey));
                Add(mutation, "$receipt", Id(receipt)); Add(mutation, "$now", UtcInstantText.Format(now));
                Add(mutation, "$digest", digest); Add(mutation, "$cohort", Id(id));
                await mutation.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            Result<GuildCohortStored> created = await ReadWithinAsync(connection, transaction, id, token).ConfigureAwait(false);
            return created.IsFailure ? created.Error : created.Value.Cohort with { Receipt = new(receipt, request.IdempotencyKey, false, now, 1) };
        }, ct).ConfigureAwait(false);
    }

    internal async Task<Result<CohortDto>> CloseOperatorAsync(GuildCallerContext caller, Guid cohortId, GuildRevisionRequest request, CancellationToken ct)
    {
        if (caller is null || caller.Kind != GuildCallerKind.Operator || caller.PrincipalId is not { } principal || cohortId == Guid.Empty
            || request is null || request.IdempotencyKey == Guid.Empty || request.ExpectedRevision < 1)
            return new Error(ErrorCodes.Guild.InvalidRequest, "The Cohort close request is invalid.");

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes("close\n" + Id(cohortId) + "\n"
            + request.ExpectedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        Result<Guid> root = await NoteRootAsync(caller, cohortId, ct).ConfigureAwait(false);
        if (root.IsFailure) return root.Error;

        return await authority.InQuestTransactionAsync<CohortDto>(root.Value, async (connection, transaction, signets, token) =>
        {
            Result<GuildCohortStored> found = await ReadWithinAsync(connection, transaction, cohortId, token).ConfigureAwait(false);
            if (found.IsFailure) return found.Error;
            CohortDto cohort = found.Value.Cohort;
            if (cohort.RootQuestId != root.Value) return Forbidden;
            Result<GuildQuestStored> issuer = await GuildQuestStore.ReadWithinAsync(connection, transaction, cohort.IssuerQuestId, token).ConfigureAwait(false);
            if (issuer.IsFailure) return issuer.Error;
            Result visible = await quests.RequireVisibleAsync(connection, transaction, caller, issuer.Value, token).ConfigureAwait(false);
            if (visible.IsFailure) return visible.Error;
            Result<GuildGrant> live = await signets.ValidateCurrentGrantAsync(cohort.IssuerQuestId, token).ConfigureAwait(false);
            if (live.IsFailure) return live.Error;
            if (!live.Value.Delegation.Contains(GuildDelegationKind.Cohort)) return Forbidden;

            await using (SqliteCommand prior = Command(connection, transaction, """
                SELECT ReceiptId,AcceptedAtUtc,AcceptedRevision,RequestDigest FROM guild_operator_cohort_mutations
                WHERE InstallationGeneration=$generation AND PrincipalId=$principal AND ActionCode=2 AND ParentId=$cohort AND IdempotencyKey=$key
                """))
            {
                Add(prior, "$generation", Id(caller.InstallationGeneration)); Add(prior, "$principal", Id(principal));
                Add(prior, "$cohort", Id(cohortId)); Add(prior, "$key", Id(request.IdempotencyKey));
                await using SqliteDataReader reader = await prior.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    if (reader.GetValue(3) is not byte[] existing || !CryptographicOperations.FixedTimeEquals(existing, digest))
                        return new Error(ErrorCodes.Guild.IdempotencyConflict, "The Cohort mutation identity was already used for different input.");
                    return cohort with { Receipt = new(Guid.Parse(reader.GetString(0)), request.IdempotencyKey, true,
                        UtcInstantText.Parse(reader.GetString(1)), reader.GetInt64(2)) };
                }
            }

            if (cohort.State != GuildCohortState.Open || cohort.Revision != request.ExpectedRevision) return RevisionConflict(cohort);
            if (await GuildQuestStore.HasClosedAncestorAsync(connection, transaction, cohort.IssuerQuestId, token).ConfigureAwait(false)) return Forbidden;
            await using (SqliteCommand count = Command(connection, transaction, "SELECT ActiveMemberCount FROM guild_cohorts WHERE Id=$cohort"))
            {
                Add(count, "$cohort", Id(cohortId));
                long voters = (long)(await count.ExecuteScalarAsync(token).ConfigureAwait(false) ?? 0L);
                if (voters == 0 || cohort.Policy.Kind == GuildConvergenceKind.Quorum && cohort.Policy.Quorum > voters)
                    return new Error(ErrorCodes.Guild.ConvergenceInvalid, "The Cohort cannot close without enough active voters.");
            }

            if (cohort.Revision == long.MaxValue) return Unavailable;
            DateTimeOffset now = time.GetUtcNow();
            Result offersClosed = await CloseOffersWithinAsync(connection, transaction, cohort.Id, null, "cohort_closed", now, token).ConfigureAwait(false);
            if (offersClosed.IsFailure) return offersClosed.Error;
            Result<long> watermark = await GuildPortalEvents.AppendWithinAsync(connection, transaction, cohort.RootQuestId, cohort.IssuerQuestId,
                GuildEventKind.CohortChanged, cohort.Id, cohort.Revision + 1, null, now, token).ConfigureAwait(false);
            if (watermark.IsFailure) return watermark.Error;
            await using (SqliteCommand update = Command(connection, transaction, """
                UPDATE guild_cohorts SET StateCode=2,ClosedMembershipVersion=MembershipVersion,FrozenMemberCount=ActiveMemberCount,EventWatermark=$watermark,Revision=Revision+1
                WHERE Id=$cohort AND StateCode=1 AND Revision=$revision
                """))
            {
                Add(update, "$cohort", Id(cohortId)); Add(update, "$watermark", watermark.Value); Add(update, "$revision", cohort.Revision);
                if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return Conflict;
            }
            Guid receipt = Guid.NewGuid();
            await using (SqliteCommand mutation = Command(connection, transaction, """
                INSERT INTO guild_operator_cohort_mutations(InstallationGeneration,PrincipalId,ActionCode,ParentId,IdempotencyKey,
                    ReceiptId,AcceptedAtUtc,RequestDigest,CohortId,AcceptedRevision)
                VALUES($generation,$principal,2,$cohort,$key,$receipt,$now,$digest,$cohort,$revision)
                """))
            {
                Add(mutation, "$generation", Id(caller.InstallationGeneration)); Add(mutation, "$principal", Id(principal));
                Add(mutation, "$cohort", Id(cohortId)); Add(mutation, "$key", Id(request.IdempotencyKey));
                Add(mutation, "$receipt", Id(receipt)); Add(mutation, "$now", UtcInstantText.Format(now));
                Add(mutation, "$digest", digest); Add(mutation, "$revision", cohort.Revision + 1);
                await mutation.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            Result<GuildCohortStored> closed = await ReadWithinAsync(connection, transaction, cohortId, token).ConfigureAwait(false);
            return closed.IsFailure ? closed.Error : closed.Value.Cohort with { Receipt = new(receipt, request.IdempotencyKey, false, now, cohort.Revision + 1) };
        }, ct).ConfigureAwait(false);
    }

    internal async Task<Result<CohortMemberDto>> RemoveOperatorAsync(GuildCallerContext caller, Guid cohortId, Guid memberId,
        GuildRevisionRequest request, CancellationToken ct)
    {
        if (caller is null || caller.Kind != GuildCallerKind.Operator || caller.PrincipalId is not { } principal
            || cohortId == Guid.Empty || memberId == Guid.Empty || request is null || request.IdempotencyKey == Guid.Empty || request.ExpectedRevision < 1)
            return new Error(ErrorCodes.Guild.InvalidRequest, "The member removal request is invalid.");

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes("remove\n" + Id(cohortId) + "\n" + Id(memberId) + "\n"
            + request.ExpectedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Result<Guid> root = await NoteRootAsync(caller, cohortId, ct).ConfigureAwait(false);
        if (root.IsFailure) return root.Error;
        return await authority.InQuestTransactionAsync<CohortMemberDto>(root.Value, async (connection, transaction, signets, token) =>
        {
            Result<GuildCohortStored> found = await ReadWithinAsync(connection, transaction, cohortId, token).ConfigureAwait(false);
            if (found.IsFailure) return found.Error;
            CohortDto cohort = found.Value.Cohort;
            if (cohort.RootQuestId != root.Value) return Forbidden;
            Result<GuildQuestStored> issuer = await GuildQuestStore.ReadWithinAsync(connection, transaction, cohort.IssuerQuestId, token).ConfigureAwait(false);
            if (issuer.IsFailure) return issuer.Error;
            Result visible = await quests.RequireVisibleAsync(connection, transaction, caller, issuer.Value, token).ConfigureAwait(false);
            if (visible.IsFailure) return visible.Error;
            Result<GuildGrant> live = await signets.ValidateCurrentGrantAsync(cohort.IssuerQuestId, token).ConfigureAwait(false);
            if (live.IsFailure) return live.Error;
            if (!live.Value.Delegation.Contains(GuildDelegationKind.Cohort)) return Forbidden;
            await using (SqliteCommand prior = Command(connection, transaction, """
                SELECT ReceiptId,AcceptedAtUtc,AcceptedRevision,RequestDigest,ResultObjectId FROM guild_operator_cohort_mutations
                WHERE InstallationGeneration=$generation AND PrincipalId=$principal AND ActionCode=4 AND ParentId=$cohort AND IdempotencyKey=$key
                """))
            {
                Add(prior, "$generation", Id(caller.InstallationGeneration)); Add(prior, "$principal", Id(principal));
                Add(prior, "$cohort", Id(cohortId)); Add(prior, "$key", Id(request.IdempotencyKey));
                await using SqliteDataReader reader = await prior.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    if (reader.GetValue(3) is not byte[] stored || !CryptographicOperations.FixedTimeEquals(stored, digest))
                        return new Error(ErrorCodes.Guild.IdempotencyConflict, "The Cohort mutation identity was already used for different input.");
                    if (reader.IsDBNull(4) || reader.GetString(4) != Id(memberId)) return Unavailable;
                    GuildMutationReceiptDto retainedReceipt = new(Guid.Parse(reader.GetString(0)), request.IdempotencyKey, true,
                        UtcInstantText.Parse(reader.GetString(1)), reader.GetInt64(2));
                    await reader.DisposeAsync().ConfigureAwait(false);
                    Result<CohortMemberDto> retainedMember = await ReadMemberWithinAsync(connection, transaction, cohort, memberId, token).ConfigureAwait(false);
                    if (retainedMember.IsFailure) return new Error(ErrorCodes.Guild.NotFound, "The Cohort member is unavailable.");
                    return retainedMember.Value with { Receipt = retainedReceipt };
                }
            }
            Result<CohortMemberDto> member = await ReadMemberWithinAsync(connection, transaction, cohort, memberId, token).ConfigureAwait(false);
            if (member.IsFailure) return new Error(ErrorCodes.Guild.NotFound, "The Cohort member is unavailable.");
            if (cohort.State is not (GuildCohortState.Open or GuildCohortState.Closed) || cohort.Revision != request.ExpectedRevision) return RevisionConflict(cohort);
            if (member.Value.State != GuildCohortMemberState.Active) return Conflict;
            if (cohort.Revision == long.MaxValue || cohort.MembershipVersion == long.MaxValue) return Unavailable;
            if (await GuildQuestStore.HasClosedAncestorAsync(connection, transaction, cohort.IssuerQuestId, token).ConfigureAwait(false)) return Forbidden;
            DateTimeOffset now = time.GetUtcNow();
            Result closed = await CloseOffersWithinAsync(connection, transaction, cohort.Id, memberId, "issuer_removed", now, token).ConfigureAwait(false);
            if (closed.IsFailure) return closed.Error;
            Result<long> watermark = await GuildPortalEvents.AppendWithinAsync(connection, transaction, cohort.RootQuestId, cohort.IssuerQuestId,
                GuildEventKind.MembershipChanged, cohort.Id, cohort.Revision + 1, null, now, token).ConfigureAwait(false);
            if (watermark.IsFailure) return watermark.Error;
            long membership = cohort.MembershipVersion + 1;
            await using (SqliteCommand update = Command(connection, transaction,
                "UPDATE guild_cohort_members SET StateCode=2,RemovalSequence=$sequence WHERE Id=$member AND CohortId=$cohort AND StateCode=1"))
            {
                Add(update, "$member", Id(memberId)); Add(update, "$cohort", Id(cohortId)); Add(update, "$sequence", membership);
                if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return Conflict;
            }

            bool withdrewFrozen = false;
            if (cohort.State == GuildCohortState.Closed && member.Value.InClosedSnapshot)
            {
                await using SqliteCommand existing = Command(connection, transaction,
                    "SELECT EXISTS(SELECT 1 FROM guild_cohort_contributions WHERE CohortId=$cohort AND MemberId=$member)");
                Add(existing, "$cohort", Id(cohortId)); Add(existing, "$member", Id(memberId));
                withdrewFrozen = !Equals(1L, await existing.ExecuteScalarAsync(token).ConfigureAwait(false));
            }

            await using (SqliteCommand update = Command(connection, transaction, """
                UPDATE guild_cohorts SET MembershipVersion=$membership,ActiveMemberCount=ActiveMemberCount-1,EventWatermark=$event,
                    ResultSequence=ResultSequence+$vote,Revision=Revision+1
                WHERE Id=$cohort AND Revision=$revision AND ActiveMemberCount>0 AND ($vote=0 OR ResultSequence<9223372036854775807)
                RETURNING ResultSequence
                """))
            {
                Add(update, "$membership", membership); Add(update, "$event", watermark.Value); Add(update, "$vote", withdrewFrozen ? 1 : 0);
                Add(update, "$cohort", Id(cohortId)); Add(update, "$revision", cohort.Revision);
                if (await update.ExecuteScalarAsync(token).ConfigureAwait(false) is not long resultSequence) return Conflict;
                if (withdrewFrozen)
                {
                    await using SqliteCommand vote = Command(connection, transaction, """
                        INSERT INTO guild_cohort_contributions(CohortId,MemberId,PrimaryVotingQuestId,ResultSequence,OutcomeCode)
                        VALUES($cohort,$member,$quest,$sequence,5)
                        """);
                    Add(vote, "$cohort", Id(cohortId)); Add(vote, "$member", Id(memberId));
                    Add(vote, "$quest", Id(member.Value.PrimaryVotingQuestId)); Add(vote, "$sequence", resultSequence);
                    await vote.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            await using (SqliteCommand frontier = Command(connection, transaction,
                "INSERT INTO guild_cohort_stop_frontiers(Id,CohortId,MemberId) VALUES($id,$cohort,$member)"))
            {
                Add(frontier, "$id", Id(Guid.NewGuid())); Add(frontier, "$cohort", Id(cohortId)); Add(frontier, "$member", Id(memberId));
                await frontier.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            Guid receipt = Guid.NewGuid();
            await using (SqliteCommand mutation = Command(connection, transaction, """
                INSERT INTO guild_operator_cohort_mutations(InstallationGeneration,PrincipalId,ActionCode,ParentId,IdempotencyKey,
                    ReceiptId,AcceptedAtUtc,RequestDigest,CohortId,ResultObjectId,AcceptedRevision)
                VALUES($generation,$principal,4,$cohort,$key,$receipt,$now,$digest,$cohort,$member,$revision)
                """))
            {
                Add(mutation, "$generation", Id(caller.InstallationGeneration)); Add(mutation, "$principal", Id(principal));
                Add(mutation, "$cohort", Id(cohortId)); Add(mutation, "$key", Id(request.IdempotencyKey)); Add(mutation, "$receipt", Id(receipt));
                Add(mutation, "$now", UtcInstantText.Format(now)); Add(mutation, "$digest", digest);
                Add(mutation, "$member", Id(memberId)); Add(mutation, "$revision", cohort.Revision + 1);
                await mutation.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            Result<CohortMemberDto> withdrawn = await ReadMemberWithinAsync(connection, transaction, cohort with { MembershipVersion = membership }, memberId, token).ConfigureAwait(false);
            return withdrawn.IsFailure ? withdrawn.Error : withdrawn.Value with { Receipt = new(receipt, request.IdempotencyKey, false, now, cohort.Revision + 1) };
        }, ct).ConfigureAwait(false);
    }

    internal async Task<Result<CohortDto>> CancelOperatorAsync(GuildCallerContext caller, Guid cohortId, GuildRevisionRequest request, CancellationToken ct)
    {
        if (caller is null || caller.Kind != GuildCallerKind.Operator || caller.PrincipalId is not { } principal || cohortId == Guid.Empty
            || request is null || request.IdempotencyKey == Guid.Empty || request.ExpectedRevision < 1)
            return new Error(ErrorCodes.Guild.InvalidRequest, "The Cohort cancellation request is invalid.");
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes("cancel\n" + Id(cohortId) + "\n"
            + request.ExpectedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Result<Guid> root = await NoteRootAsync(caller, cohortId, ct).ConfigureAwait(false);
        if (root.IsFailure) return root.Error;
        return await authority.InQuestTransactionAsync<CohortDto>(root.Value, async (connection, transaction, signets, token) =>
        {
            Result<GuildCohortStored> found = await ReadWithinAsync(connection, transaction, cohortId, token).ConfigureAwait(false);
            if (found.IsFailure) return found.Error;
            CohortDto cohort = found.Value.Cohort;
            if (cohort.RootQuestId != root.Value) return Forbidden;
            Result<GuildQuestStored> issuer = await GuildQuestStore.ReadWithinAsync(connection, transaction, cohort.IssuerQuestId, token).ConfigureAwait(false);
            if (issuer.IsFailure) return issuer.Error;
            Result visible = await quests.RequireVisibleAsync(connection, transaction, caller, issuer.Value, token).ConfigureAwait(false);
            if (visible.IsFailure) return visible.Error;
            Result<GuildGrant> live = await signets.ValidateCurrentGrantAsync(cohort.IssuerQuestId, token).ConfigureAwait(false);
            if (live.IsFailure) return live.Error;
            if (!live.Value.Delegation.Contains(GuildDelegationKind.Cohort)) return Forbidden;
            await using (SqliteCommand prior = Command(connection, transaction, """
                SELECT ReceiptId,AcceptedAtUtc,AcceptedRevision,RequestDigest FROM guild_operator_cohort_mutations
                WHERE InstallationGeneration=$generation AND PrincipalId=$principal AND ActionCode=5 AND ParentId=$cohort AND IdempotencyKey=$key
                """))
            {
                Add(prior, "$generation", Id(caller.InstallationGeneration)); Add(prior, "$principal", Id(principal));
                Add(prior, "$cohort", Id(cohortId)); Add(prior, "$key", Id(request.IdempotencyKey));
                await using SqliteDataReader reader = await prior.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    if (reader.GetValue(3) is not byte[] existing || !CryptographicOperations.FixedTimeEquals(existing, digest))
                        return new Error(ErrorCodes.Guild.IdempotencyConflict, "The Cohort mutation identity was already used for different input.");
                    return cohort with { Receipt = new(Guid.Parse(reader.GetString(0)), request.IdempotencyKey, true,
                        UtcInstantText.Parse(reader.GetString(1)), reader.GetInt64(2)) };
                }
            }

            if (cohort.State is not (GuildCohortState.Open or GuildCohortState.Closed) || cohort.Revision != request.ExpectedRevision) return RevisionConflict(cohort);
            if (cohort.Revision == long.MaxValue) return Unavailable;
            DateTimeOffset now = time.GetUtcNow();
            Result offersClosed = await CloseOffersWithinAsync(connection, transaction, cohort.Id, null, "operator_requested", now, token).ConfigureAwait(false);
            if (offersClosed.IsFailure) return offersClosed.Error;
            Result<long> watermark = await GuildPortalEvents.AppendWithinAsync(connection, transaction, cohort.RootQuestId, cohort.IssuerQuestId,
                GuildEventKind.CohortChanged, cohort.Id, cohort.Revision + 1, null, now, token).ConfigureAwait(false);
            if (watermark.IsFailure) return watermark.Error;
            await using (SqliteCommand update = Command(connection, transaction, """
                UPDATE guild_cohorts SET StateCode=3,DecisionCode=3,ReasonCode=NULL,EventWatermark=$watermark,Revision=Revision+1
                WHERE Id=$cohort AND Revision=$revision AND StateCode IN(1,2);
                INSERT INTO guild_cohort_stop_frontiers(Id,CohortId,MemberId) VALUES($frontier,$cohort,NULL);
                """))
            {
                Add(update, "$cohort", Id(cohortId)); Add(update, "$revision", cohort.Revision);
                Add(update, "$watermark", watermark.Value); Add(update, "$frontier", Id(Guid.NewGuid()));
                if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 2) return Conflict;
            }
            Guid receipt = Guid.NewGuid();
            await using (SqliteCommand mutation = Command(connection, transaction, """
                INSERT INTO guild_operator_cohort_mutations(InstallationGeneration,PrincipalId,ActionCode,ParentId,IdempotencyKey,
                    ReceiptId,AcceptedAtUtc,RequestDigest,CohortId,AcceptedRevision)
                VALUES($generation,$principal,5,$cohort,$key,$receipt,$now,$digest,$cohort,$revision)
                """))
            {
                Add(mutation, "$generation", Id(caller.InstallationGeneration)); Add(mutation, "$principal", Id(principal));
                Add(mutation, "$cohort", Id(cohortId)); Add(mutation, "$key", Id(request.IdempotencyKey));
                Add(mutation, "$receipt", Id(receipt)); Add(mutation, "$now", UtcInstantText.Format(now));
                Add(mutation, "$digest", digest); Add(mutation, "$revision", cohort.Revision + 1);
                await mutation.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            Result<GuildCohortStored> canceled = await ReadWithinAsync(connection, transaction, cohortId, token).ConfigureAwait(false);
            return canceled.IsFailure ? canceled.Error : canceled.Value.Cohort with { Receipt = new(receipt, request.IdempotencyKey, false, now, cohort.Revision + 1) };
        }, ct).ConfigureAwait(false);
    }

    internal async Task<Result<CohortConvergenceDto>> GetOperatorConvergenceAsync(GuildCallerContext caller, Guid cohortId, CancellationToken ct)
    {
        if (caller is null || caller.Kind != GuildCallerKind.Operator || cohortId == Guid.Empty)
            return new Error(ErrorCodes.Guild.InvalidRequest, "The Cohort identity is invalid.");
        Result<Guid> root = await NoteRootAsync(caller, cohortId, ct).ConfigureAwait(false);
        if (root.IsFailure) return root.Error;
        return await authority.InQuestTransactionAsync<CohortConvergenceDto>(root.Value, async (connection, transaction, _, token) =>
        {
            Result<GuildCohortStored> found = await ReadWithinAsync(connection, transaction, cohortId, token).ConfigureAwait(false);
            if (found.IsFailure) return found.Error;
            CohortDto cohort = found.Value.Cohort;
            if (cohort.RootQuestId != root.Value) return Forbidden;
            Result<GuildQuestStored> issuer = await GuildQuestStore.ReadWithinAsync(connection, transaction, cohort.IssuerQuestId, token).ConfigureAwait(false);
            if (issuer.IsFailure) return issuer.Error;
            Result visible = await quests.RequireVisibleAsync(connection, transaction, caller, issuer.Value, token).ConfigureAwait(false);
            if (visible.IsFailure) return visible.Error;
            Result<GuildCohortSettlementNode> rootNode = await NewSettlementNodeAsync(connection, transaction, cohort, cohort.AllocationAccountId, null, token).ConfigureAwait(false);
            if (rootNode.IsFailure) return rootNode.Error;
            GuildCohortSettlementNode rootAccount = rootNode.Value;
            if (rootAccount.Kind != GuildBudgetOwnerKind.Cohort || rootAccount.OwnerId != cohort.Id) return Unavailable;
            long generation;
            await using (SqliteCommand state = Command(connection, transaction, """
                SELECT s.Generation,s.Epoch,s.Complete,COALESCE((SELECT Revision FROM guild_cohort_settlement_clock WHERE RootAccountId=$root),0)
                FROM guild_cohort_settlement_state s WHERE s.CohortId=$cohort
                """))
            {
                Add(state, "$cohort", Id(cohortId)); Add(state, "$root", Id(rootAccount.RootAccountId));
                await using SqliteDataReader reader = await state.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (!await reader.ReadAsync(token).ConfigureAwait(false)) return new Error(ErrorCodes.Guild.MaintenanceDeferred, "The accounting snapshot is not yet available.");
                if (reader.GetValue(0) is not long value || value < 1 || reader.GetValue(1) is not long epoch || epoch < 0
                    || reader.GetValue(2) is not long complete || complete is not (0 or 1) || reader.GetValue(3) is not long current || current < 0) return Unavailable;
                if (complete == 0 || epoch != current) return new Error(ErrorCodes.Guild.MaintenanceDeferred, "The accounting snapshot is not current.");
                generation = value;
            }
            Result<GuildCohortSettlementNode> totalResult = await LoadSettlementNodeAsync(connection, transaction, cohortId, generation, rootAccount.AccountId, token).ConfigureAwait(false);
            if (totalResult.IsFailure) return totalResult.Error;
            GuildCohortSettlementNode total = totalResult.Value;
            if (total.Phase != 2) return Unavailable;
            decimal? remainingUsd = total.UsdLimit is null ? null : total.LiabilityUsd is null ? 0m
                : Math.Max(0m, ExactUsdText.CheckedAdd(Usd(total.UsdLimit), -Usd(total.LiabilityUsd)));
            long? remainingTokens = total.TokenLimit is null ? null : total.LiabilityTokens is null ? 0
                : Math.Max(0L, checked(total.TokenLimit.Value - total.LiabilityTokens.Value));
            GuildSpendDto spend = new(cohort.IssuerQuestId, Usd(total.KnownUsd), total.KnownTokens, total.UnknownUsd, total.UnknownTokens,
                total.Priced, total.Unpriced, total.Uncertain, SettlementGrant(total.ActiveUsd, total.ActiveTokens),
                SettlementGrant(total.HoldUsd, total.HoldTokens), SettlementGrant(remainingUsd is { } amount ? ExactUsdText.Format(amount) : null, remainingTokens), cohort.EventWatermark);
            GuildConvergenceDecision? decision;
            string? reason;
            long frozen;
            await using (SqliteCommand state = Command(connection, transaction,
                "SELECT DecisionCode,ReasonCode,COALESCE(FrozenMemberCount,0) FROM guild_cohorts WHERE Id=$cohort"))
            {
                Add(state, "$cohort", Id(cohortId));
                await using SqliteDataReader reader = await state.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (!await reader.ReadAsync(token).ConfigureAwait(false)) return Unavailable;
                decision = reader.IsDBNull(0) ? null : (GuildConvergenceDecision)reader.GetInt32(0);
                reason = reader.IsDBNull(1) ? null : reader.GetString(1);
                frozen = reader.GetInt64(2);
            }

            if (decision is { } d && !Enum.IsDefined(d)) return Unavailable;
            GuildDataRef? selected = null;
            if (decision == GuildConvergenceDecision.Success && cohort.Policy.Kind != GuildConvergenceKind.All)
            {
                Result<GuildDataRef?> reference = await ReadSingleSelectedReferenceWithinAsync(connection, transaction, cohort, token).ConfigureAwait(false);
                if (reference.IsFailure) return reference.Error;
                selected = reference.Value;
            }

            return new CohortConvergenceDto(cohortId, cohort.Revision, cohort.State, decision, reason, selected, frozen,
                total.PendingMembers, total.PendingDescendants, total.CancelRequested, spend, cohort.EventWatermark);
        }, ct).ConfigureAwait(false);
    }

}
