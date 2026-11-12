using System.Reflection;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Operator_member_pages_preserve_snapshot_and_reject_cross_cohort_cursor()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, _, _, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 2);

        MethodInfo? method = typeof(GuildCohortStore).GetMethod("GetOperatorMemberPageAsync", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        async Task<Result<CohortMemberPageDto>> Read(Guid id, GuildPageQuery query) =>
            await (Task<Result<CohortMemberPageDto>>)method.Invoke(cohorts, [owner, id, query, CancellationToken.None])!;

        var first = await Read(cohort.Id, new(null, 1));

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.Equal(members[0].Quest.Id, Assert.Single(first.Value.Items).PrimaryVotingQuestId);

        Assert.NotNull(first.Value.NextCursor);

        var second = await Read(cohort.Id, new(first.Value.NextCursor, 1));

        Assert.True(second.IsSuccess, second.Error.Message);

        Assert.Equal(members[1].Quest.Id, Assert.Single(second.Value.Items).PrimaryVotingQuestId);

        Assert.Equal(first.Value.SnapshotSequence, second.Value.SnapshotSequence);

        var wrongOwner = await Read(Guid.NewGuid(), new(first.Value.NextCursor, 1));

        Assert.True(wrongOwner.IsFailure);

        Assert.Contains(wrongOwner.Error.Code, new[] { ErrorCodes.Guild.CursorInvalid, ErrorCodes.Guild.CursorGone });
    }

    [Fact]
    public async Task Operator_can_read_owned_cohort_and_cannot_locate_an_unknown_one()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, _, _, cohorts, cohort, _) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 0);

        MethodInfo? method = typeof(GuildCohortStore).GetMethod("GetOperatorCohortAsync", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        async Task<Result<CohortDto>> Read(Guid id) =>
            await (Task<Result<CohortDto>>)method.Invoke(cohorts, [owner, id, CancellationToken.None])!;

        var visible = await Read(cohort.Id);

        Assert.True(visible.IsSuccess, visible.Error.Message);

        Assert.Equal(cohort.Id, visible.Value.Id);

        Assert.Null(visible.Value.Receipt);

        var missing = await Read(Guid.NewGuid());

        Assert.True(missing.IsFailure);
    }


    [Fact]
    public async Task Operator_member_cursor_expires_when_sensitivity_visibility_changes()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, _, _, cohorts, cohort, _) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 2);

        var first = await cohorts.GetOperatorMemberPageAsync(owner, cohort.Id, new(null, 1), default);

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.NotNull(first.Value.NextCursor);

        await f.ExecuteAsync("INSERT INTO artifact_sensitivity_clock(Id,Revision) VALUES(1,1) ON CONFLICT(Id) DO UPDATE SET Revision=Revision+1");

        var second = await cohorts.GetOperatorMemberPageAsync(owner, cohort.Id, new(first.Value.NextCursor, 1), default);

        Assert.True(second.IsFailure);

        Assert.Equal(ErrorCodes.Guild.CursorGone, second.Error.Code);
    }


    [Fact]
    public async Task Operator_close_freezes_membership_and_replays_the_original_receipt()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, _, _, cohorts, cohort, _) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 1);

        MethodInfo? method = typeof(GuildCohortStore).GetMethod("CloseOperatorAsync", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var current = await cohorts.GetOperatorCohortAsync(owner, cohort.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        GuildRevisionRequest request = new(Guid.NewGuid(), current.Value.Revision);

        async Task<Result<CohortDto>> Close() =>
            await (Task<Result<CohortDto>>)method.Invoke(cohorts, [owner, cohort.Id, request, CancellationToken.None])!;

        var first = await Close();

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.Equal(GuildCohortState.Closed, first.Value.State);

        Assert.Equal(1, first.Value.ClosedMembershipVersion);

        var replay = await Close();

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.True(replay.Value.Receipt!.Replayed);

        Assert.Equal(first.Value.Receipt!.Id, replay.Value.Receipt.Id);
    }


    [Fact]
    public async Task Operator_member_quest_page_contains_the_explicit_primary_association()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, _, _, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 1);

        var memberPage = await cohorts.GetOperatorMemberPageAsync(owner, cohort.Id, new(), default);

        Assert.True(memberPage.IsSuccess, memberPage.Error.Message);

        Guid member = Assert.Single(memberPage.Value.Items).Id;

        MethodInfo? method = typeof(GuildCohortStore).GetMethod("GetOperatorMemberQuestsAsync", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var result = await (Task<Result<CohortMemberQuestPageDto>>)method.Invoke(cohorts, [owner, cohort.Id, member, new GuildPageQuery(), CancellationToken.None])!;

        Assert.True(result.IsSuccess, result.Error.Message);

        CohortMemberQuestDto association = Assert.Single(result.Value.Items);

        Assert.Equal(members[0].Quest.Id, association.QuestId);

        Assert.Equal(GuildMemberAssociationKind.Primary, association.AssociationKind);
    }


    [Fact]
    public async Task Operator_remove_records_withdrawal_and_replays_original_receipt()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, _, _, cohorts, cohort, _) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 1);

        var current = await cohorts.GetOperatorCohortAsync(owner, cohort.Id, default);

        var page = await cohorts.GetOperatorMemberPageAsync(owner, cohort.Id, new(), default);

        Assert.True(current.IsSuccess, current.Error.Message);

        Assert.True(page.IsSuccess, page.Error.Message);

        Guid member = Assert.Single(page.Value.Items).Id;

        GuildRevisionRequest request = new(Guid.NewGuid(), current.Value.Revision);

        MethodInfo? method = typeof(GuildCohortStore).GetMethod("RemoveOperatorAsync", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        async Task<Result<CohortMemberDto>> Remove() =>
            await (Task<Result<CohortMemberDto>>)method.Invoke(cohorts, [owner, cohort.Id, member, request, CancellationToken.None])!;

        var first = await Remove();

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.Equal(GuildCohortMemberState.Withdrawn, first.Value.State);

        var replay = await Remove();

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.True(replay.Value.Receipt!.Replayed);

        Assert.Equal(first.Value.Receipt!.Id, replay.Value.Receipt.Id);

        var changed = await cohorts.RemoveOperatorAsync(owner, cohort.Id, Guid.NewGuid(), request, default);

        Assert.Equal(ErrorCodes.Guild.IdempotencyConflict, changed.Error.Code);
    }


    [Fact]
    public async Task Operator_cancellation_records_a_durable_frontier_and_replays()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, _, _, cohorts, cohort, _) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 1);

        var current = await cohorts.GetOperatorCohortAsync(owner, cohort.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        GuildRevisionRequest request = new(Guid.NewGuid(), current.Value.Revision);

        MethodInfo? method = typeof(GuildCohortStore).GetMethod("CancelOperatorAsync", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        async Task<Result<CohortDto>> Cancel() =>
            await (Task<Result<CohortDto>>)method.Invoke(cohorts, [owner, cohort.Id, request, CancellationToken.None])!;

        var first = await Cancel();

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.Equal(GuildCohortState.Draining, first.Value.State);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_stop_frontiers WHERE MemberId IS NULL AND Completed=0"));

        var replay = await Cancel();

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.True(replay.Value.Receipt!.Replayed);

        Assert.Equal(first.Value.Receipt!.Id, replay.Value.Receipt.Id);
    }


    [Fact]
    public async Task Operator_contributions_report_durable_result_and_selection_without_inline_body()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, issuer, _, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.First, null, "text/plain", 1);

        await CommitConvergenceResultAsync(f, owner, members[0], "selected evidence");

        var current = await cohorts.GetAsync(issuer, cohort.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        var closed = await cohorts.CloseAsync(issuer, cohort.Id, new(Guid.NewGuid(), current.Value.Revision), default);

        Assert.True(closed.IsSuccess, closed.Error.Message);

        _ = await WaitForConvergenceAsync(cohorts, owner, cohort.Id, value => value.Decision is not null);

        MethodInfo? method = typeof(GuildCohortStore).GetMethod("ListOperatorContributionsAsync", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var result = await (Task<Result<CohortContributionPageDto>>)method.Invoke(cohorts,
            [owner, cohort.Id, true, new GuildPageQuery(), CancellationToken.None])!;

        Assert.True(result.IsSuccess, result.Error.Message);

        CohortContributionDto contribution = Assert.Single(result.Value.Items);

        Assert.Equal(members[0].Quest.Id, contribution.PrimaryVotingQuestId);

        Assert.Equal(GuildContributionOutcome.Accepted, contribution.Outcome);

        Assert.True(contribution.Selected);

        Assert.NotNull(contribution.ResultRef);
    }


    [Fact]
    public async Task Operator_convergence_reports_the_committed_decision_and_accounting_snapshot()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, issuer, _, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.First, null, "text/plain", 1);

        await CommitConvergenceResultAsync(f, owner, members[0], "result");

        var current = await cohorts.GetAsync(issuer, cohort.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        Assert.True((await cohorts.CloseAsync(issuer, cohort.Id, new(Guid.NewGuid(), current.Value.Revision), default)).IsSuccess);

        CohortConvergenceDto decided = await WaitForConvergenceAsync(cohorts, owner, cohort.Id, value => value.Decision is not null);

        MethodInfo? method = typeof(GuildCohortStore).GetMethod("GetOperatorConvergenceAsync", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var result = await (Task<Result<CohortConvergenceDto>>)method.Invoke(cohorts, [owner, cohort.Id, CancellationToken.None])!;

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(decided.Decision, result.Value.Decision);

        Assert.Equal(decided.FrozenMemberCount, result.Value.FrozenMemberCount);

        Assert.NotNull(result.Value.SelectedResultRef);
    }

    [Fact]
    public async Task Operator_contribution_cursor_preserves_the_selection_snapshot()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, issuer, _, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 2);

        await CommitConvergenceResultAsync(f, owner, members[0], "first");

        await CommitConvergenceResultAsync(f, owner, members[1], "second");

        var first = await cohorts.ListOperatorContributionsAsync(owner, cohort.Id, null, new GuildPageQuery(PageSize: 1), default);

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.False(Assert.Single(first.Value.Items).Selected);

        Assert.NotNull(first.Value.NextCursor);

        var current = await cohorts.GetAsync(issuer, cohort.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        Assert.True((await cohorts.CloseAsync(issuer, cohort.Id, new(Guid.NewGuid(), current.Value.Revision), default)).IsSuccess);

        _ = await WaitForConvergenceAsync(cohorts, owner, cohort.Id, value => value.Decision is not null);

        var second = await cohorts.ListOperatorContributionsAsync(owner, cohort.Id, null, new GuildPageQuery(first.Value.NextCursor, 1), default);

        Assert.True(second.IsSuccess, second.Error.Message);

        Assert.False(Assert.Single(second.Value.Items).Selected);
    }

    [Fact]
    public async Task Operator_cohort_cursors_expire_after_owner_erasure_and_reject_wrong_scope()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, _, root, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 2);

        await CommitConvergenceResultAsync(f, owner, members[0], "first");

        await CommitConvergenceResultAsync(f, owner, members[1], "second");

        var memberPage = await cohorts.GetOperatorMemberPageAsync(owner, cohort.Id, new GuildPageQuery(PageSize: 1), default);

        var contributionPage = await cohorts.ListOperatorContributionsAsync(owner, cohort.Id, null, new GuildPageQuery(PageSize: 1), default);

        Assert.True(memberPage.IsSuccess, memberPage.Error.Message);

        Assert.True(contributionPage.IsSuccess, contributionPage.Error.Message);

        Assert.NotNull(memberPage.Value.NextCursor);

        Assert.NotNull(contributionPage.Value.NextCursor);

        Guid session = root.Quest.SessionId!.Value;

        await f.ExecuteAsync($"INSERT INTO owner_deletion_events(OwnerKindCode,OwnerId,DeletedAtUtc) VALUES(2,'{session.ToString("D").ToUpperInvariant()}','2026-11-05T00:00:00.0000000Z')");

        var goneMembers = await cohorts.GetOperatorMemberPageAsync(owner, cohort.Id, new GuildPageQuery(memberPage.Value.NextCursor, 1), default);

        var goneContributions = await cohorts.ListOperatorContributionsAsync(owner, cohort.Id, null, new GuildPageQuery(contributionPage.Value.NextCursor, 1), default);

        var wrongScope = await cohorts.GetOperatorMemberPageAsync(owner, Guid.NewGuid(), new GuildPageQuery(memberPage.Value.NextCursor, 1), default);

        Assert.Equal(ErrorCodes.Guild.CursorGone, goneMembers.Error.Code);

        Assert.Equal(ErrorCodes.Guild.CursorGone, goneContributions.Error.Code);

        Assert.Equal(ErrorCodes.Guild.CursorInvalid, wrongScope.Error.Code);
    }

    [Fact]
    public async Task Operator_member_quest_cursor_expires_after_owner_erasure_and_rejects_wrong_scope()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var setup = await OperatorAwardFixtureAsync(f);

        var award = await setup.Store.AwardAsync(setup.Owner, setup.Mutation, setup.Offer.Id, setup.Request, default);

        Assert.True(award.IsSuccess, award.Error.Message);

        Guid cohortId = setup.Offer.CohortId;

        var members = await setup.Store.GetOperatorMemberPageAsync(setup.Owner, cohortId, new GuildPageQuery(), default);

        Assert.True(members.IsSuccess, members.Error.Message);

        Guid memberId = Assert.Single(members.Value.Items).Id;

        var first = await setup.Store.GetOperatorMemberQuestsAsync(setup.Owner, cohortId, memberId, new GuildPageQuery(PageSize: 1), default);

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.NotNull(first.Value.NextCursor);

        Guid session = setup.Issuer.Quest.SessionId!.Value;

        await f.ExecuteAsync($"INSERT INTO owner_deletion_events(OwnerKindCode,OwnerId,DeletedAtUtc) VALUES(2,'{session.ToString("D").ToUpperInvariant()}','2026-11-05T00:00:00.0000000Z')");

        var gone = await setup.Store.GetOperatorMemberQuestsAsync(setup.Owner, cohortId, memberId, new GuildPageQuery(first.Value.NextCursor, 1), default);

        var wrong = await setup.Store.GetOperatorMemberQuestsAsync(setup.Owner, cohortId, Guid.NewGuid(), new GuildPageQuery(first.Value.NextCursor, 1), default);

        Assert.Equal(ErrorCodes.Guild.CursorGone, gone.Error.Code);

        Assert.Equal(ErrorCodes.Guild.CursorInvalid, wrong.Error.Code);
    }

}
