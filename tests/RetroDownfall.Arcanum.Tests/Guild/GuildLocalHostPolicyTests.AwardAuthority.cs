using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Award_work_refuses_effects_when_its_primary_signet_is_revoked()
    {
        await using Fixture f = await Fixture.CreateAsync();

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
            new(memberKey, root.Value.Quest.Revision, null, "Primary", "Contribute", new(), CardId: saved.Value.Payload.Card.CardId), default);

        Assert.True(primaryResult.IsSuccess, primaryResult.Error.Message);

        var primary = primaryResult.Value;

        var current = await f.Store.GetAsync(owner, root.Value.Quest.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        Guid key = Guid.NewGuid();

        var work = await f.Store.IssueAsync(issuer, new(f.Generation, root.Value.Quest.Id, null, key, key),
            root.Value.Quest.Id, new(key, current.Value.Quest.Revision, primary.Quest.AgentId,
                "Award", "Perform awarded work", new()), default);

        Assert.True(work.IsSuccess, work.Error.Message);

        var run = await BindCohortRunAsync(f, owner, work.Value);

        await f.ExecuteAsync($"""
            INSERT INTO guild_cohort_member_quests(MemberId,QuestId,AssociationKindCode,AssociationSequence,Active)
            SELECT Id,'{work.Value.Quest.Id.ToString("D").ToUpperInvariant()}',2,2,1 FROM guild_cohort_members WHERE PrimaryVotingQuestId='{primary.Quest.Id:D}' COLLATE NOCASE;
            """);

        await SeedSyntheticAwardAuthorityAsync(f, cohort.Value.Id, root.Value.Quest.Id, primary, work.Value);

        var deniedIncomplete = await f.Authority.AdmitToolAsync(run.Execution!, CohortAuthorityTool, true,
            (_, _) => Task.FromResult(Result<bool>.Success(true)), default);

        Assert.Equal(ErrorCodes.Guild.AuthorityUnavailable, deniedIncomplete.Error.Code);

        await CompleteSyntheticAwardAuthorityAsync(f, work.Value.Quest.Id);

        var before = await f.Authority.AdmitToolAsync(run.Execution!, CohortAuthorityTool, true,
            (_, _) => Task.FromResult(Result<bool>.Success(true)), default);

        Assert.True(before.IsSuccess, before.Error.Message);

        await f.ExecuteAsync($"UPDATE guild_signets SET Revoked=1,Revision=Revision+1,RevocationGeneration=RevocationGeneration+1 WHERE QuestId='{primary.Quest.Id:D}' COLLATE NOCASE");

        var after = await f.Authority.AdmitToolAsync(run.Execution!, CohortAuthorityTool, true,
            (_, _) => Task.FromResult(Result<bool>.Success(true)), default);

        Assert.Equal(ErrorCodes.Guild.AuthorityRevoked, after.Error.Code);
    }

    [Fact]
    public async Task Award_primary_cycle_is_rejected_without_authorizing_effects()
    {
        await using Fixture f = await Fixture.CreateAsync();

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
            new(memberKey, root.Value.Quest.Revision, null, "Primary", "Contribute", new(), CardId: saved.Value.Payload.Card.CardId), default);

        Assert.True(primaryResult.IsSuccess, primaryResult.Error.Message);

        var primary = primaryResult.Value;

        var current = await f.Store.GetAsync(owner, root.Value.Quest.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        Guid key = Guid.NewGuid();

        var work = await f.Store.IssueAsync(issuer, new(f.Generation, root.Value.Quest.Id, null, key, key),
            root.Value.Quest.Id, new(key, current.Value.Quest.Revision, primary.Quest.AgentId,
                "Award", "Perform awarded work", new()), default);

        Assert.True(work.IsSuccess, work.Error.Message);

        var run = await BindCohortRunAsync(f, owner, work.Value);

        string workId = work.Value.Quest.Id.ToString("D").ToUpperInvariant();
        string primaryId = primary.Quest.Id.ToString("D").ToUpperInvariant();

        // Simulate corrupted durable relationships while preserving the declared schema and its guards.
        // The inserted award points to its own work Signet, making an actual authority dependency cycle.
        await f.ExecuteAsync($"""
            CREATE TEMP TABLE retained_member AS SELECT Id,CohortId,AgentId,AdmissionSequence,StateCode,RemovalSequence FROM guild_cohort_members WHERE PrimaryVotingQuestId='{primaryId}';
            DELETE FROM guild_cohort_member_quests WHERE MemberId IN (SELECT Id FROM retained_member);
            DELETE FROM guild_cohort_members WHERE Id IN (SELECT Id FROM retained_member);
            INSERT INTO guild_cohort_members(Id,CohortId,AgentId,PrimaryVotingQuestId,AdmissionSequence,StateCode,RemovalSequence)
            SELECT Id,CohortId,AgentId,'{workId}',AdmissionSequence,StateCode,RemovalSequence FROM retained_member;
            INSERT INTO guild_cohort_member_quests(MemberId,QuestId,AssociationKindCode,AssociationSequence,Active)
            SELECT Id,'{workId}',2,2,1 FROM retained_member;
            DROP TABLE retained_member;
            """);

        await SeedSyntheticAwardAuthorityAsync(f, cohort.Value.Id, root.Value.Quest.Id, work.Value, work.Value);
        await CompleteSyntheticAwardAuthorityAsync(f, work.Value.Quest.Id);

        var refused = await f.Authority.AdmitToolAsync(run.Execution!, CohortAuthorityTool, true,
            (_, _) => Task.FromResult(Result<bool>.Success(true)), default);

        Assert.Equal(ErrorCodes.Guild.AuthorityUnavailable, refused.Error.Code);
    }

    private static async Task SeedSyntheticAwardAuthorityAsync(Fixture f, Guid cohortId, Guid issuerId,
        GuildQuestStored primary, GuildQuestStored work)
    {
        string offer = Guid.NewGuid().ToString("D").ToUpperInvariant();
        string bid = Guid.NewGuid().ToString("D").ToUpperInvariant();
        string award = Guid.NewGuid().ToString("D").ToUpperInvariant();
        string cohort = cohortId.ToString("D").ToUpperInvariant();
        string issuer = issuerId.ToString("D").ToUpperInvariant();
        string primaryId = primary.Quest.Id.ToString("D").ToUpperInvariant();
        string workId = work.Quest.Id.ToString("D").ToUpperInvariant();

        await f.ExecuteAsync($"""
            INSERT INTO guild_work_offers(Id,CohortId,IssuerQuestId,OfferSequence,SkillId,GoalContentId,RequestedGrantJson,MimeType,StateCode,Revision)
            SELECT '{offer}','{cohort}','{issuer}',1,'review',GoalContentId,'null','text/plain',1,1 FROM guild_quests WHERE Id='{primaryId}';
            INSERT INTO guild_work_bids(Id,OfferId,MemberId,AgentId,AuthorQuestId,CardRevision,BidSequence,Revision,StateCode,NoteContentId)
            SELECT '{bid}','{offer}',m.Id,m.AgentId,q.Id,q.CardRevision,1,1,1,q.GoalContentId FROM guild_cohort_members m JOIN guild_quests q ON q.Id=m.PrimaryVotingQuestId WHERE q.Id='{primaryId}';
            INSERT INTO guild_work_awards(Id,OfferId,BidId,QuestId,MemberId,PrimaryQuestId,PrimarySignetId,PrimaryRevocationGeneration)
            SELECT '{award}','{offer}','{bid}','{workId}',m.Id,q.Id,q.SignetId,s.RevocationGeneration FROM guild_cohort_members m JOIN guild_quests q ON q.Id=m.PrimaryVotingQuestId JOIN guild_signets s ON s.QuestId=q.Id WHERE q.Id='{primaryId}';
            """);
    }

    private static async Task CompleteSyntheticAwardAuthorityAsync(Fixture f, Guid questId)
    {
        string quest = questId.ToString("D").ToUpperInvariant();
        await f.ExecuteAsync($"""
            UPDATE guild_work_offers SET StateCode=2,Revision=Revision+1,
                AwardId=(SELECT Id FROM guild_work_awards WHERE QuestId='{quest}'),QuestId='{quest}'
            WHERE Id=(SELECT OfferId FROM guild_work_awards WHERE QuestId='{quest}');
            """);
    }
}
