using System.Security.Cryptography;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildCohortStore
{
    internal async Task<Result<WorkOfferDto>> WithdrawOfferAsync(GuildCallerContext caller, GuildMutationIdentity mutation, Guid offerId,
        GuildRevisionRequest request, CancellationToken ct, GuildChildToolAdmission? toolAdmission = null)
    {
        if (caller is null || mutation is null || request is null || offerId == Guid.Empty
            || request.IdempotencyKey == Guid.Empty || mutation.IdempotencyKey != request.IdempotencyKey || request.ExpectedRevision < 1)
            return new Error(ErrorCodes.Guild.InvalidRequest, "The offer withdrawal request is invalid.");

        byte[] digest = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, GuildJsonContext.Default.GuildRevisionRequest));
        if (mutation.CanonicalRequestDigest != (toolAdmission?.ArgumentsDigest ?? Convert.ToHexString(digest))) return Forbidden;

        var root = await OfferRootAsync(caller, offerId, ct).ConfigureAwait(false);
        if (root.IsFailure) return root.Error;

        GuildQuestDto? acceptedQuest = null;

        var result = await InCohortToolTransactionAsync(root.Value, toolAdmission, GuildJsonContext.Default.WorkOfferDto,
            async (connection, transaction, signets, token) =>
            {
                var scope = await OfferScopeWithinAsync(connection, transaction, offerId, token).ConfigureAwait(false);
                if (scope.IsFailure || !await MarketRootMatchesWithinAsync(connection, transaction, scope.Value.Cohort, root.Value, token).ConfigureAwait(false)) return MarketNotFound;
                var found = await ReadWithinAsync(connection, transaction, scope.Value.Cohort, token).ConfigureAwait(false);
                if (found.IsFailure) return found.Error;
                CohortDto cohort = found.Value.Cohort;
                if (cohort.RootQuestId != root.Value) return MarketNotFound;

                var actor = await AuthorizeNoteWithinAsync(connection, transaction, signets, caller, cohort, true, token).ConfigureAwait(false);
                if (actor.IsFailure) return actor.Error;
                GuildQuestStored source = actor.Value.Source;

                if (caller.Kind == GuildCallerKind.Operator)
                {
                    var issuer = await GuildQuestStore.ReadWithinAsync(connection, transaction, scope.Value.Issuer, token).ConfigureAwait(false);
                    if (issuer.IsFailure) return issuer.Error;
                    Result visible = await quests.RequireVisibleAsync(connection, transaction, caller, issuer.Value, token).ConfigureAwait(false);
                    if (visible.IsFailure) return visible.Error;
                    var live = await signets.ValidateCurrentGrantAsync(issuer.Value.Quest.Id, token).ConfigureAwait(false);
                    if (live.IsFailure) return live.Error;
                    source = issuer.Value;
                }

                if (source.Quest.Id != scope.Value.Issuer) return MarketNotFound;

                var offer = await ReadOfferWithinAsync(connection, transaction, offerId, token).ConfigureAwait(false);
                if (offer.IsFailure) return offer.Error;

                if (toolAdmission is not null)
                {
                    if (!ReferenceEquals(toolAdmission.Registry.Parent.Driver.Caller, caller)
                        || !GuildMarketToolArguments.MatchesWithdrawOffer(toolAdmission, offerId, request)) return Forbidden;
                    var admitted = await quests.AuthorizeCohortToolWithinAsync(connection, transaction, signets, toolAdmission, token).ConfigureAwait(false);
                    if (admitted.IsFailure) return admitted.Error;
                    acceptedQuest = admitted.Value.Quest;
                }

                int kind = caller.Kind == GuildCallerKind.Operator ? 1 : 2;
                Guid principal = kind == 1 ? caller.PrincipalId!.Value : source.Quest.Id;

                await using (var prior = Command(connection, transaction, """
                    SELECT ReceiptId,AcceptedRevision,AcceptedAtUtc,RequestDigest FROM guild_work_mutations
                    WHERE PrincipalKindCode=$kind AND PrincipalId=$principal AND ActionCode=2 AND ParentId=$offer AND IdempotencyKey=$key
                    """))
                {
                    Add(prior, "$kind", kind);
                    Add(prior, "$principal", Id(principal));
                    Add(prior, "$offer", Id(offerId));
                    Add(prior, "$key", Id(request.IdempotencyKey));
                    await using var reader = await prior.ExecuteReaderAsync(token).ConfigureAwait(false);
                    if (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        if (reader.GetValue(3) is not byte[] old || !CryptographicOperations.FixedTimeEquals(old, digest))
                            return new Error(ErrorCodes.Guild.IdempotencyConflict, "The withdrawal identity was accepted with different input.");
                        return offer.Value with { Receipt = new(Guid.Parse(reader.GetString(0)), request.IdempotencyKey, true,
                            GuildMarketStorage.ParseReceiptInstant(reader.GetString(2)), reader.GetInt64(1)) };
                    }
                }

                if (cohort.State != GuildCohortState.Open || offer.Value.State != GuildWorkOfferState.Open
                    || offer.Value.Revision != request.ExpectedRevision || cohort.Revision == long.MaxValue || offer.Value.Revision == long.MaxValue) return Conflict;
                if (await GuildQuestStore.HasClosedAncestorAsync(connection, transaction, source.Quest.Id, token).ConfigureAwait(false)) return Forbidden;

                DateTimeOffset now = time.GetUtcNow();
                Guid receipt = Guid.NewGuid();
                await using (var update = Command(connection, transaction, "UPDATE guild_work_offers SET StateCode=3,Revision=Revision+1 WHERE Id=$id AND StateCode=1 AND Revision=$revision"))
                {
                    Add(update, "$id", Id(offerId));
                    Add(update, "$revision", request.ExpectedRevision);
                    if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return Conflict;
                }

                var projected = await GuildPortalEvents.AppendWithinAsync(connection, transaction, cohort.RootQuestId, source.Quest.Id,
                    GuildEventKind.OfferChanged, offerId, offer.Value.Revision + 1, null, now, token).ConfigureAwait(false);
                if (projected.IsFailure) return projected.Error;

                await using (var aggregate = Command(connection, transaction, "UPDATE guild_cohorts SET Revision=Revision+1,EventWatermark=$event WHERE Id=$id AND StateCode=1 AND Revision=$revision"))
                {
                    Add(aggregate, "$event", projected.Value);
                    Add(aggregate, "$id", Id(cohort.Id));
                    Add(aggregate, "$revision", cohort.Revision);
                    if (await aggregate.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return Conflict;
                }

                await using (var accepted = Command(connection, transaction, """
                    INSERT INTO guild_work_mutations(PrincipalKindCode,PrincipalId,ActionCode,ParentId,IdempotencyKey,RequestDigest,ResultId,ReceiptId,AcceptedRevision,AcceptedAtUtc)
                    VALUES($kind,$principal,2,$parent,$key,$digest,$result,$receipt,$revision,$now)
                    """))
                {
                    Add(accepted, "$kind", kind); Add(accepted, "$principal", Id(principal)); Add(accepted, "$parent", Id(offerId));
                    Add(accepted, "$key", Id(request.IdempotencyKey)); Add(accepted, "$digest", digest); Add(accepted, "$result", Id(offerId));
                    Add(accepted, "$receipt", Id(receipt)); Add(accepted, "$revision", offer.Value.Revision + 1); Add(accepted, "$now", UtcInstantText.Format(now));
                    await accepted.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                string after = "";
                while (true)
                {
                    Guid member;
                    Guid primary;
                    await using (var next = Command(connection, transaction, "SELECT MemberId,PrimaryQuestId FROM guild_work_offer_recipients WHERE OfferId=$offer AND MemberId>$after ORDER BY MemberId LIMIT 1"))
                    {
                        Add(next, "$offer", Id(offerId)); Add(next, "$after", after);
                        await using var reader = await next.ExecuteReaderAsync(token).ConfigureAwait(false);
                        if (!await reader.ReadAsync(token).ConfigureAwait(false)) break;
                        if (!Guid.TryParse(reader.GetString(0), out member) || !Guid.TryParse(reader.GetString(1), out primary)) return Unavailable;
                    }
                    after = Id(member);
                    var notice = await new GuildPortalStore(quests, authority, policy, time).PublishDistributionWithinAsync(connection, transaction,
                        signets, caller, source, cohort, new("offer.withdrawn", cohort.Id, offerId, offer.Value.OfferSequence),
                        Guid.NewGuid(), member, now, token, primary).ConfigureAwait(false);
                    if (notice.IsFailure) return notice.Error;
                }

                return offer.Value with { State = GuildWorkOfferState.Withdrawn, Revision = offer.Value.Revision + 1,
                    Receipt = new(receipt, request.IdempotencyKey, false, now, offer.Value.Revision + 1) };
            }, ct).ConfigureAwait(false);

        if (result.IsSuccess && toolAdmission is not null && acceptedQuest is not null)
            toolAdmission.RecordCommittedProjection(acceptedQuest, JsonSerializer.SerializeToElement(result.Value, GuildJsonContext.Default.WorkOfferDto));

        return result;
    }
}
