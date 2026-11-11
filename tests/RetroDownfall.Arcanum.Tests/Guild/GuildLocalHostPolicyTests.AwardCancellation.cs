using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Award_primary_revocation_or_expiry_drives_durable_sibling_cancellation_after_restart(bool expiry)
    {
        await using Fixture f = await Fixture.CreateAsync();

        FakeTimeProvider time = new();

        var (owner, cohorts, cohort, primary, work) = await NewAwardCancellationFixtureAsync(f,
            expiry ? time.GetUtcNow().AddHours(1) : null);

        if (expiry) time.Advance(TimeSpan.FromHours(2));
        else
        {
            Guid key = Guid.NewGuid();

            var revoked = await f.Authority.RevokeAsync(owner, new(key, new string('A', 64)), primary.Quest.SignetId,
                new(key, primary.Quest.SignetRevision, GuildCancellationReason.OperatorRequested), default);

            Assert.True(revoked.IsSuccess, revoked.Error.Message);
        }

        SignetAuthority resumedAuthority = new(new(f.Connections), f.Gate, f.Policy, time);

        cohorts = new(new(f.Connections, f.Gate, time), resumedAuthority, f.Store, new(), f.Policy, time);

        var first = await cohorts.ReconcileAsync(owner, cohort.Id, default);

        Assert.Equal(ErrorCodes.Guild.MaintenanceDeferred, first.Error.Code);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_stop_frontiers WHERE MemberId IS NOT NULL"));

        // A new store/authority instance must continue the persisted association coordinate.
        resumedAuthority = new(new(f.Connections), f.Gate, f.Policy, time);
        cohorts = new(new(f.Connections, f.Gate, time), resumedAuthority, f.Store, new(), f.Policy, time);

        for (int step = 0; step < 4; step++)
        {
            var progress = await cohorts.ReconcileAsync(owner, cohort.Id, default);

            Assert.True(progress.IsSuccess || progress.Error.Code == ErrorCodes.Guild.MaintenanceDeferred, progress.Error.Message);
        }

        Assert.Equal(1L, await f.ScalarAsync($"SELECT CancellationRequested FROM guild_quests WHERE Id='{work.Quest.Id:D}' COLLATE NOCASE"));

        Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_quest_cancellations WHERE QuestId='{work.Quest.Id:D}' COLLATE NOCASE"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_stop_frontiers WHERE Completed=1 AND MemberId IS NOT NULL"));
    }

    [Fact]
    public async Task Successful_primary_completion_preserves_existing_award_authority_and_does_not_request_sibling_stop()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, cohorts, cohort, primary, work) = await NewAwardCancellationFixtureAsync(f);

        await CommitConvergenceResultAsync(f, owner, primary, "accepted result");

        for (int step = 0; step < 4; step++)
        {
            var progress = await cohorts.ReconcileAsync(owner, cohort.Id, default);

            Assert.True(progress.IsSuccess || progress.Error.Code == ErrorCodes.Guild.MaintenanceDeferred, progress.Error.Message);
        }

        Assert.Equal(0L, await f.ScalarAsync($"SELECT CancellationRequested FROM guild_quests WHERE Id='{work.Quest.Id:D}' COLLATE NOCASE"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_stop_frontiers WHERE MemberId IS NOT NULL"));
    }

    private static async Task<(GuildCallerContext Owner, GuildCohortStore Cohorts, CohortDto Cohort,
        GuildQuestStored Primary, GuildQuestStored Work)> NewAwardCancellationFixtureAsync(Fixture f, DateTimeOffset? expiry = null)
    {
        var owner = await f.OperatorAsync();

        var root = await f.IssueAsync(owner, f.Request with
        {
            Grant = f.Request.Grant with { Tools = [CohortAuthorityTool], Delegation = [GuildDelegationKind.Local, GuildDelegationKind.Cohort] },
        });

        Assert.True(root.IsSuccess, root.Error.Message);

        var issuer = await BindCohortRunAsync(f, owner, root.Value);

        GuildCohortStore cohorts = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority,
            f.Store, new GuildBudgetLedger(), f.Policy, TimeProvider.System);

        var cohort = await cohorts.CreateAsync(issuer, new(Guid.NewGuid(), root.Value.Quest.Id, "Authority",
            new(GuildConvergenceKind.All, null), new("text/plain"), null), default);

        Assert.True(cohort.IsSuccess, cohort.Error.Message);

        GuildStore cards = new(f.Connections, f.Gate, TimeProvider.System);

        GuildAgentCard card = new(Guid.Empty, Guid.Empty, 0, "Reviewer", "Reviewer", "1", [], ["text/plain"], ["text/plain"],
            new(false, false, false), new(GuildAgentSourceKind.DeclaredGlobal, null, null, true, null));

        var saved = await cards.UpsertCardAsync(new("award-authority-card", null, null, null,
            new(card, "Review work", [CohortAuthorityTool.Name]), new string('A', 64)), default);

        Assert.True(saved.IsSuccess, saved.Error.Message);

        Guid memberKey = Guid.NewGuid();

        var primaryResult = await f.Store.IssueCohortMemberAsync(issuer,
            new(f.Generation, root.Value.Quest.Id, null, memberKey, memberKey), cohort.Value.Id, cohort.Value.Revision,
            new(memberKey, root.Value.Quest.Revision, null, "Primary", "Contribute", expiry is { } expiration ? new(ExpiresAtUtc: new(true, expiration)) : new(), CardId: saved.Value.Payload.Card.CardId), default);

        Assert.True(primaryResult.IsSuccess, primaryResult.Error.Message);

        var primary = primaryResult.Value;

        var current = await f.Store.GetAsync(owner, root.Value.Quest.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        Guid key = Guid.NewGuid();

        var work = await f.Store.IssueAsync(issuer, new(f.Generation, root.Value.Quest.Id, null, key, key),
            root.Value.Quest.Id, new(key, current.Value.Quest.Revision, primary.Quest.AgentId,
                "Award", "Perform awarded work", new()), default);

        Assert.True(work.IsSuccess, work.Error.Message);

        await f.ExecuteAsync($"""
            INSERT INTO guild_cohort_member_quests(MemberId,QuestId,AssociationKindCode,AssociationSequence,Active)
            SELECT Id,'{work.Value.Quest.Id.ToString("D").ToUpperInvariant()}',2,2,1 FROM guild_cohort_members WHERE PrimaryVotingQuestId='{primary.Quest.Id:D}' COLLATE NOCASE;
            """);

        return (owner, cohorts, cohort.Value, primary, work.Value);
    }
}
