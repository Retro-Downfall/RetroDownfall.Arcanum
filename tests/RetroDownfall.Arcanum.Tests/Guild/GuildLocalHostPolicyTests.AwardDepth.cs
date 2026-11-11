using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Nested_award_primary_constraints_recheck_the_earliest_primary()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildCallerContext owner = await f.OperatorAsync();

        var root = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with
            { Tools = [CohortAuthorityTool], Delegation = [GuildDelegationKind.Local, GuildDelegationKind.Cohort] } });

        Assert.True(root.IsSuccess, root.Error.Message);

        GuildQuestStored current = root.Value;

        GuildCallerContext caller = await BindCohortRunAsync(f, owner, current);

        GuildCohortStore cohorts = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority, f.Store, new(), f.Policy, TimeProvider.System);

        GuildStore cards = new(f.Connections, f.Gate, TimeProvider.System);

        GuildQuestStored? earliestPrimary = null;

        for (int depth = 0; depth < 16; depth++)
        {
            var cohort = await cohorts.CreateAsync(caller, new(Guid.NewGuid(), current.Quest.Id, "Nested authority",
                new(GuildConvergenceKind.All, null), new("text/plain"), null), default);

            Assert.True(cohort.IsSuccess, cohort.Error.Message);

            GuildAgentCard card = new(Guid.Empty, Guid.Empty, 0, "Reviewer " + depth, "Reviewer", "1", [],
                ["text/plain"], ["text/plain"], new(false, false, false), new(GuildAgentSourceKind.DeclaredGlobal, null, null, true, null));

            var saved = await cards.UpsertCardAsync(new("nested-authority-" + depth, null, null, null,
                new(card, "Review", [CohortAuthorityTool.Name]), new string('A', 64)), default);

            Assert.True(saved.IsSuccess, saved.Error.Message);

            var currentIssuer = await f.Store.GetAsync(owner, current.Quest.Id, default);

            Assert.True(currentIssuer.IsSuccess, currentIssuer.Error.Message);

            Guid memberKey = Guid.NewGuid();

            var primary = await f.Store.IssueCohortMemberAsync(caller,
                new(f.Generation, current.Quest.Id, null, memberKey, memberKey), cohort.Value.Id, cohort.Value.Revision,
                new(memberKey, currentIssuer.Value.Quest.Revision, null, "Primary", "Review", new(), CardId: saved.Value.Payload.Card.CardId), default);

            Assert.True(primary.IsSuccess, primary.Error.Message);

            earliestPrimary ??= primary.Value;

            currentIssuer = await f.Store.GetAsync(owner, current.Quest.Id, default);

            Assert.True(currentIssuer.IsSuccess, currentIssuer.Error.Message);

            Guid workKey = Guid.NewGuid();

            var work = await f.Store.IssueAsync(caller, new(f.Generation, current.Quest.Id, null, workKey, workKey), current.Quest.Id,
                new(workKey, currentIssuer.Value.Quest.Revision, primary.Value.Quest.AgentId, "Award", "Work", new()), default);

            Assert.True(work.IsSuccess, work.Error.Message);

            string workId = work.Value.Quest.Id.ToString("D").ToUpperInvariant();
            string primaryId = primary.Value.Quest.Id.ToString("D").ToUpperInvariant();

            await f.ExecuteAsync($"INSERT INTO guild_cohort_member_quests(MemberId,QuestId,AssociationKindCode,AssociationSequence,Active) SELECT Id,'{workId}',2,2,1 FROM guild_cohort_members WHERE PrimaryVotingQuestId='{primaryId}'");

            await SeedSyntheticAwardAuthorityAsync(f, cohort.Value.Id, current.Quest.Id, primary.Value, work.Value);

            await CompleteSyntheticAwardAuthorityAsync(f, work.Value.Quest.Id);

            current = work.Value;

            caller = await BindCohortRunAsync(f, owner, current);
        }

        var before = await f.Authority.AdmitToolAsync(caller.Execution!, CohortAuthorityTool, true,
            (_, _) => Task.FromResult(Result<bool>.Success(true)), default);

        Assert.True(before.IsSuccess, before.Error.Message);

        Assert.NotNull(earliestPrimary);

        await f.ExecuteAsync($"UPDATE guild_signets SET Revoked=1,Revision=Revision+1,RevocationGeneration=RevocationGeneration+1 WHERE QuestId='{earliestPrimary.Quest.Id:D}' COLLATE NOCASE");

        var after = await f.Authority.AdmitToolAsync(caller.Execution!, CohortAuthorityTool, true,
            (_, _) => Task.FromResult(Result<bool>.Success(true)), default);

        Assert.Equal(ErrorCodes.Guild.AuthorityRevoked, after.Error.Code);
    }
}
