using System.Security.Cryptography;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Award_remote_route_pins_skill_and_result_or_refuses_unenforceable_finite_budget(bool finite)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);
        f.Settings.CurrentValue.Features.Conclave = true;
        f.Settings.CurrentValue.Features.A2AClient = true;
        f.Settings.CurrentValue.Integrations.A2A.AllowedRemoteAgents = ["http://remote-port.example/"];

        GuildRemoteTransportDescriptor target = new("remote-port", "http://remote-port.example/agent", "1.0", ["JSONRPC"], [], null, null, null);
        GuildAgentCard card = new(Guid.Empty, Guid.Empty, 0, "Remote", "Remote reviewer", "1",
            [new("review", "Review", "Review work", [], ["text/plain"], ["text/plain", "application/json"])],
            ["text/plain"], ["text/plain", "application/json"], new(false, false, false),
            new(GuildAgentSourceKind.Remote, null, target.PeerId, true, null));
        GuildStore roster = new(f.Connections, f.Gate, TimeProvider.System);
        var stored = await roster.UpsertCardAsync(new("remote-award", null, null, null,
            new(card, "", [], target, "http://remote-port.example/"), new string('A', 64)), default);
        Assert.True(stored.IsSuccess, stored.Error.Message);

        var owner = await f.OperatorAsync();
        var root = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with
        {
            Delegation = [GuildDelegationKind.Local, GuildDelegationKind.Remote, GuildDelegationKind.Cohort],
        } });
        Assert.True(root.IsSuccess, root.Error.Message);
        var issuer = await BindCohortRunAsync(f, owner, root.Value);
        GuildCohortStore cohorts = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority,
            f.Store, new GuildBudgetLedger(), f.Policy, TimeProvider.System);
        var cohort = await cohorts.CreateAsync(issuer, new(Guid.NewGuid(), root.Value.Quest.Id, "Remote award",
            new(GuildConvergenceKind.All, null), new("text/plain"), null), default);
        Assert.True(cohort.IsSuccess, cohort.Error.Message);

        Guid memberKey = Guid.NewGuid();
        var member = await f.Store.IssueCohortMemberAsync(issuer, new(f.Generation, root.Value.Quest.Id, null, memberKey, memberKey),
            cohort.Value.Id, cohort.Value.Revision, new(memberKey, root.Value.Quest.Revision, stored.Value.Payload.Card.AgentId,
                "Remote member", "Contribute", new(), new(GuildRemoteDispatchMode.Blocking)), default);
        Assert.True(member.IsSuccess, member.Error.Message);
        var bidder = await BindCohortRunAsync(f, owner, member.Value);
        var current = await cohorts.GetAsync(issuer, cohort.Value.Id, default);
        Assert.True(current.IsSuccess, current.Error.Message);

        GuildRequestedGrant? grant = finite
            ? new(Budget: new(true, new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Finite, 100)))) : null;
        PostWorkOfferRequest post = new(Guid.NewGuid(), current.Value.Revision, "review", "Return JSON", grant, new("application/json"));
        string postDigest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(post, GuildJsonContext.Default.PostWorkOfferRequest)));
        var offer = await cohorts.PostOfferAsync(issuer, new(post.IdempotencyKey, postDigest), cohort.Value.Id, post, default);
        Assert.True(offer.IsSuccess, offer.Error.Message);

        SubmitBidRequest bidRequest = new(Guid.NewGuid(), offer.Value.Revision, "Available", null);
        string bidDigest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(bidRequest, GuildJsonContext.Default.SubmitBidRequest)));
        var bid = await cohorts.SubmitBidAsync(bidder.Execution!, new(bidRequest.IdempotencyKey, bidDigest), offer.Value.Id, bidRequest, default);
        Assert.True(bid.IsSuccess, bid.Error.Message);
        current = await cohorts.GetAsync(issuer, cohort.Value.Id, default);
        Assert.True(current.IsSuccess, current.Error.Message);

        AwardWorkRequest awardRequest = new(Guid.NewGuid(), offer.Value.Revision, current.Value.Revision, bid.Value.Id);
        string digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(awardRequest, GuildJsonContext.Default.AwardWorkRequest)));
        var award = await cohorts.AwardAsync(owner, new(awardRequest.IdempotencyKey, digest), offer.Value.Id, awardRequest, default);

        if (finite)
        {
            Assert.Equal(ErrorCodes.Guild.BudgetCapabilityUnavailable, award.Error.Code);
            Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_work_awards"));
            Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));
            return;
        }

        Assert.True(award.IsSuccess, award.Error.Message);
        await using var connection = await f.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        var input = await GuildRemoteExecutionStore.ReadInputWithinAsync(connection, transaction, award.Value.QuestId, default);
        Assert.True(input.IsSuccess, input.Error.Message);
        Assert.Equal(target.Endpoint, input.Value.Transport.Endpoint);
        Assert.Equal("review", input.Value.SkillId);
        Assert.Equal("application/json", Assert.Single(input.Value.AcceptedOutputModes));
    }
}
