using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildPortalStore
{
    private static string ScopedReplayKey(Guid? subtree, Guid? cohort) =>
        "watch:" + (Id(subtree) ?? "-") + ":" + (Id(cohort) ?? "-");

    // Parsing only locates the gate. The retained cursor is still authenticated against its
    // generation, principal, scope and visibility inside the admitted transaction.
    internal static bool TryGetReplayRoot(string cursor, out Guid root)
    {
        root = Guid.Empty;

        if (!TryParseCursor(cursor, out PortalCursorToken parsed)) return false;

        root = parsed.Root;

        return true;
    }

    private async Task<Result<PortalReadScope>> RequireWatchScopeWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, SignetAuthority.QuestTransaction signets, GuildCallerContext caller,
        Guid root, Guid? subtree, Guid? cohort, PortalReadBinding binding, bool continuing, CancellationToken ct)
    {
        Result<PortalReadScope> scope = await RequireReadScopeWithinAsync(connection, transaction, signets,
            caller, root, binding, continuing, ct).ConfigureAwait(false);

        if (scope.IsFailure) return scope.Error;

        if (cohort is { } selectedCohort)
        {
            if (caller.Kind == GuildCallerKind.Operator)
            {
                Result<GuildCohortStored> stored = await GuildCohortStore.ReadWithinAsync(connection, transaction, selectedCohort, ct).ConfigureAwait(false);

                if (stored.IsFailure || stored.Value.Cohort.RootQuestId != root) return continuing ? CursorGone : Forbidden;

                Result located = await policy.AuthorizeLocateAsync(caller, connection, transaction,
                    stored.Value.Cohort.IssuerQuestId, null, null, ct).ConfigureAwait(false);

                if (located.IsFailure) return continuing ? CursorGone : located.Error;

                Result<GuildQuestStored> issuer = await GuildQuestStore.ReadWithinAsync(connection, transaction,
                    stored.Value.Cohort.IssuerQuestId, ct).ConfigureAwait(false);

                if (issuer.IsFailure) return continuing ? CursorGone : issuer.Error;

                Result visible = await quests.RequireVisibleAsync(connection, transaction, caller, issuer.Value, ct).ConfigureAwait(false);

                if (visible.IsFailure) return continuing ? CursorGone : visible.Error;
            }
            else
            {
                Result allowed = await RequireCohortEventScopeWithinAsync(connection, transaction, caller, root, selectedCohort, ct).ConfigureAwait(false);

                if (allowed.IsFailure) return allowed.Error;
            }
        }

        if (subtree is { } selectedSubtree)
        {
            Result<GuildQuestStored> selected = await GuildQuestStore.ReadWithinAsync(connection, transaction, selectedSubtree, ct).ConfigureAwait(false);

            if (selected.IsFailure || selected.Value.Quest.RootQuestId != root) return continuing ? CursorGone : Forbidden;

            Result<bool> visible = await EventVisibleWithinAsync(connection, transaction, signets, caller,
                scope.Value, root, selectedSubtree, null, null, ct).ConfigureAwait(false);

            if (visible.IsFailure) return visible.Error;

            if (!visible.Value) return continuing ? CursorGone : Forbidden;
        }

        return scope;
    }

    internal Task<Result<GuildPortalReplayCursor>> CaptureScopedReplayCursorAsync(GuildCallerContext caller,
        Guid root, Guid? subtree, Guid? cohort, CancellationToken ct)
    {
        if (root == Guid.Empty || subtree == Guid.Empty || cohort == Guid.Empty || (subtree is not null && cohort is not null))
            return Task.FromResult(Result<GuildPortalReplayCursor>.Failure(Invalid));

        return authority.InQuestTransactionAsync<GuildPortalReplayCursor>(root, async (connection, transaction, signets, token) =>
        {
            Result<PortalReadBinding> binding = await ReadBindingWithinAsync(connection, transaction, caller, token, operatorMetadataOnly: true).ConfigureAwait(false);

            if (binding.IsFailure) return binding.Error;

            Result<PortalReadScope> scope = await RequireWatchScopeWithinAsync(connection, transaction, signets, caller,
                root, subtree, cohort, binding.Value, false, token).ConfigureAwait(false);

            if (scope.IsFailure) return scope.Error;

            Result<(long Last, long Retained)> head = await ReadEventHeadWithinAsync(connection, transaction, root, token).ConfigureAwait(false);

            if (head.IsFailure) return head.Error;

            string cursor = await WriteCursorWithinAsync(connection, transaction, binding.Value, root,
                ScopedReplayKey(subtree, cohort), head.Value.Last, head.Value.Last, token).ConfigureAwait(false);

            return new GuildPortalReplayCursor(head.Value.Last, cursor);
        }, ct);
    }

    internal Task<Result<GuildPortalReplayPage>> ReadScopedReplayAsync(GuildCallerContext caller, Guid root,
        Guid? subtree, Guid? cohort, string cursor, CancellationToken ct, int pageSize = 100)
    {
        if (root == Guid.Empty || subtree == Guid.Empty || cohort == Guid.Empty || (subtree is not null && cohort is not null)
            || string.IsNullOrEmpty(cursor) || pageSize is < 1 or > 100) return Task.FromResult(Result<GuildPortalReplayPage>.Failure(CursorInvalid));

        return authority.InQuestTransactionAsync<GuildPortalReplayPage>(root, async (connection, transaction, signets, token) =>
        {
            Result<PortalReadBinding> binding = await ReadBindingWithinAsync(connection, transaction, caller, token, operatorMetadataOnly: true).ConfigureAwait(false);

            if (binding.IsFailure) return binding.Error;

            string scopeKey = ScopedReplayKey(subtree, cohort);

            Result<PortalCursor> saved = await ReadCursorWithinAsync(connection, transaction, caller, binding.Value,
                root, scopeKey, cursor, token).ConfigureAwait(false);

            if (saved.IsFailure) return saved.Error;

            Result<PortalReadScope> scope = await RequireWatchScopeWithinAsync(connection, transaction, signets, caller,
                root, subtree, cohort, binding.Value, true, token).ConfigureAwait(false);

            if (scope.IsFailure) return scope.Error;

            Result<(long Last, long Retained)> head = await ReadEventHeadWithinAsync(connection, transaction, root, token).ConfigureAwait(false);

            if (head.IsFailure) return head.Error;

            Result<ImmutableArray<PortalEvent>> scanned = await ScanEventsWithinAsync(connection, transaction, root,
                saved.Value.After, head.Value.Last, pageSize, token).ConfigureAwait(false);

            if (scanned.IsFailure) return scanned.Error;

            ImmutableArray<GuildChangeEventDto>.Builder items = ImmutableArray.CreateBuilder<GuildChangeEventDto>();

            long after = saved.Value.After;

            foreach (PortalEvent row in scanned.Value)
            {
                Guid? distributionCohort = null;

                if (IsDistributionEvent(row.Kind))
                {
                    distributionCohort = await DistributionEventCohortWithinAsync(connection, transaction, row, root,
                        scope.Value.OwnQuest?.Quest.Id, token).ConfigureAwait(false);

                    if (distributionCohort is null)
                    {
                        after = row.Sequence;

                        continue;
                    }
                }
                else if (scope.Value.OwnQuest is { } eventActor && !await IsCohortEventActorAuthorizedWithinAsync(connection,
                    transaction, row, root, eventActor.Quest.Id, token).ConfigureAwait(false))
                {
                    after = row.Sequence;

                    continue;
                }

                bool cohortLifecycle = distributionCohort is not null;

                if (cohort is { } eventCohort)
                {
                    bool tagged = row.Kind is GuildEventKind.CohortChanged or GuildEventKind.MembershipChanged or GuildEventKind.ContributionAccepted;

                    if (tagged)
                    {
                        if (row.ObjectId != eventCohort)
                        {
                            after = row.Sequence;

                            continue;
                        }

                        cohortLifecycle = true;
                    }
                    else if (distributionCohort is { } distribution)
                    {
                        if (distribution != eventCohort)
                        {
                            after = row.Sequence;

                            continue;
                        }
                    }
                    else if (row.EnvelopeId is { } envelopeId)
                    {
                        Result<GuildEnvelope> envelope = await ReadEnvelopeWithinAsync(connection, transaction, envelopeId, token).ConfigureAwait(false);

                        if (envelope.IsFailure) return envelope.Error;

                        if (envelope.Value.Address.Kind != GuildCircleAddressKind.Cohort || envelope.Value.Address.CohortId != eventCohort)
                        {
                            after = row.Sequence;

                            continue;
                        }
                    }
                    else if (!(caller.Kind == GuildCallerKind.Operator
                        ? await IsOperatorCohortQuestWithinAsync(connection, transaction, eventCohort, root, row.QuestId, token).ConfigureAwait(false)
                        : await IsCurrentCohortQuestWithinAsync(connection, transaction, eventCohort, root, row.QuestId, token).ConfigureAwait(false)))
                    {
                        after = row.Sequence;

                        continue;
                    }
                }

                Result<bool> visible = await EventVisibleWithinAsync(connection, transaction, signets, caller, scope.Value,
                    root, row.QuestId, row.EnvelopeId, subtree, token, cohortLifecycle).ConfigureAwait(false);

                if (visible.IsFailure) return visible.Error;

                if (visible.Value) items.Add(new(row.Sequence, row.Kind, row.ObjectId, row.Revision, [], row.QuestId));

                after = row.Sequence;
            }

            bool caughtUp = after == head.Value.Last;

            string resume = after == saved.Value.After ? cursor : await WriteCursorWithinAsync(connection, transaction, binding.Value, root, scopeKey,
                after, head.Value.Last, token).ConfigureAwait(false);

            GuildEventPage page = new(items.ToImmutable(), caughtUp ? null : resume, head.Value.Last);

            return JsonSerializer.SerializeToUtf8Bytes(page, GuildJsonContext.Default.GuildEventPage).Length <= PortalPageBytes
                ? new GuildPortalReplayPage(page, resume, caughtUp) : TooLarge;
        }, ct);
    }

    private static async Task<bool> IsOperatorCohortQuestWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid cohort, Guid root, Guid quest, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT 1 FROM guild_cohorts c JOIN guild_cohort_members m ON m.CohortId=c.Id AND m.StateCode=1
            JOIN guild_cohort_member_quests a ON a.MemberId=m.Id AND a.Active=1
            JOIN guild_quests q ON q.Id=a.QuestId AND q.AgentId=m.AgentId AND q.RootQuestId=c.RootQuestId
            WHERE c.Id=$cohort AND c.RootQuestId=$root AND a.QuestId=$quest
            """);

        Add(command, "$cohort", Id(cohort));

        Add(command, "$root", Id(root));

        Add(command, "$quest", Id(quest));

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

}
