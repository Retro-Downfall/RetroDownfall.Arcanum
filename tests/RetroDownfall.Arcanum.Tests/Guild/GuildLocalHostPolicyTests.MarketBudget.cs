using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Tool_offer_budget_hint_clamps_at_award_while_native_request_is_strict(bool toolOffer)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        f.Settings.CurrentValue.Features.Conclave = true;

        f.Settings.CurrentValue.Providers = [new() { Name = "provider", Type = RetroDownfall.Arcanum.Core.Configuration.AiProviderKind.OpenAICompatible,
            Endpoint = "https://api.example.test/v1", Models = [new() { Name = "model" }] }];

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant postTool = new(GuildToolSourceKind.InternalMcp, "arcanum", "post_work_offer");

        var root = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with
        {
            Tools = [postTool], Delegation = [GuildDelegationKind.Local, GuildDelegationKind.Cohort],
            Budget = new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Finite, 1000)),
        } });

        Assert.True(root.IsSuccess, root.Error.Message);

        GuildCallerContext issuer = await BindCohortRunAsync(f, owner, root.Value);

        GuildCohortStore cohorts = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority, f.Store, new(), f.Policy, TimeProvider.System);

        var created = await cohorts.CreateAsync(issuer, new(Guid.NewGuid(), root.Value.Quest.Id, "Budget panel",
            new(GuildConvergenceKind.All, null), new("text/plain"), null), default);

        Assert.True(created.IsSuccess, created.Error.Message);

        GuildStore cards = new(f.Connections, f.Gate, TimeProvider.System);
        GuildQuestStored? winner = null;

        foreach (int tokens in new[] { 500, 400 })
        {
            GuildAgentCard card = new(Guid.Empty, Guid.Empty, 0, "Reviewer " + tokens, "Reviewer", "1",
                [new("review", "Review", "Review work", [], ["text/plain"], ["text/plain"])],
                ["text/plain"], ["text/plain"], new(false, false, false), new(GuildAgentSourceKind.DeclaredGlobal, null, null, true, null));

            var saved = await cards.UpsertCardAsync(new("budget-reviewer-" + tokens, null, null, null,
                new(card, "Review", [], RequestedToolsSpecified: true), new string('A', 64)), default);

            Assert.True(saved.IsSuccess, saved.Error.Message);

            var currentRoot = await f.Store.GetAsync(owner, root.Value.Quest.Id, default);
            var currentCohort = await cohorts.GetAsync(issuer, created.Value.Id, default);

            Assert.True(currentRoot.IsSuccess, currentRoot.Error.Message);
            Assert.True(currentCohort.IsSuccess, currentCohort.Error.Message);

            Guid key = Guid.NewGuid();

            GuildRequestedGrant grant = new(Budget: new(true, new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Finite, tokens))));

            var member = await f.Store.IssueCohortMemberAsync(issuer, new(f.Generation, root.Value.Quest.Id, null, key, key),
                created.Value.Id, currentCohort.Value.Revision,
                new(key, currentRoot.Value.Quest.Revision, null, "Reviewer", "Review", grant, CardId: saved.Value.Payload.Card.CardId), default);

            Assert.True(member.IsSuccess, member.Error.Message);

            winner ??= member.Value;
        }

        Assert.NotNull(winner);

        var beforeOffer = await cohorts.GetAsync(issuer, created.Value.Id, default);

        Assert.True(beforeOffer.IsSuccess, beforeOffer.Error.Message);

        Guid offerId;
        long offerRevision;

        if (toolOffer)
        {
            var currentRoot = await f.Store.GetAsync(owner, root.Value.Quest.Id, default);

            Assert.True(currentRoot.IsSuccess, currentRoot.Error.Message);

            var execution = await ToolExecutionAsync(f, owner, currentRoot.Value);

            issuer = execution.Driver.Caller!;

            var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);
            ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, new ChildQueue(), cohorts: cohorts);
            using GuildLocalToolRegistry registry = new(execution, input.Value, coordinator);
            var context = f.Store.CreateToolContext(execution, registry, _ => true);
            var function = await context.AdvertiseAsync(new InertChildTool(postTool.Name), postTool, default);

            Assert.NotNull(function);

            using IDisposable frame = registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), postTool);

            object? raw = await context.InvokeBoundAsync(function, new AIFunctionArguments { ["cohortId"] = created.Value.Id.ToString(),
                ["expectedCohortRevision"] = beforeOffer.Value.Revision, ["skillId"] = "review", ["goal"] = "Review", ["resultType"] = "text/plain" }, default);

            Assert.False(raw is string, raw?.ToString());

            JsonElement projection = Assert.IsType<GuildLocalChildToolResult>(raw).Projection!.Value;

            offerId = projection.GetProperty("id").GetGuid();
            offerRevision = projection.GetProperty("revision").GetInt64();
        }
        else
        {
            PostWorkOfferRequest post = new(Guid.NewGuid(), beforeOffer.Value.Revision, "review", "Review", null, new("text/plain"));

            string digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(post, GuildJsonContext.Default.PostWorkOfferRequest)));

            var offer = await cohorts.PostOfferAsync(issuer, new(post.IdempotencyKey, digest), created.Value.Id, post, default);

            Assert.True(offer.IsSuccess, offer.Error.Message);

            offerId = offer.Value.Id;
            offerRevision = offer.Value.Revision;
        }

        GuildCallerContext bidder = await BindCohortRunAsync(f, owner, winner);

        SubmitBidRequest bid = new(Guid.NewGuid(), offerRevision, "I can review", null);

        string bidDigest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(bid, GuildJsonContext.Default.SubmitBidRequest)));

        var submitted = await cohorts.SubmitBidAsync(bidder.Execution!, new(bid.IdempotencyKey, bidDigest), offerId, bid, default);

        Assert.True(submitted.IsSuccess, submitted.Error.Message);

        var current = await cohorts.GetAsync(issuer, created.Value.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        AwardWorkRequest award = new(Guid.NewGuid(), offerRevision, current.Value.Revision, submitted.Value.Id);

        string awardDigest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(award, GuildJsonContext.Default.AwardWorkRequest)));

        long beforeAccounts = await f.ScalarAsync("SELECT count(*) FROM guild_budget_accounts");
        long beforeQuests = await f.ScalarAsync("SELECT count(*) FROM guild_quests");

        var result = await cohorts.AwardAsync(owner, new(award.IdempotencyKey, awardDigest), offerId, award, default);

        if (!toolOffer)
        {
            Assert.Equal(ErrorCodes.Guild.GrantExceeded, result.Error.Code);

            Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_work_awards"));

            Assert.Equal(beforeAccounts, await f.ScalarAsync("SELECT count(*) FROM guild_budget_accounts"));

            Assert.Equal(beforeQuests, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));

            return;
        }

        Assert.True(result.IsSuccess, result.Error.Message);

        var child = await f.Store.GetAsync(owner, result.Value.QuestId, default);

        Assert.True(child.IsSuccess, child.Error.Message);

        Assert.Equal(100L, child.Value.Quest.ResolvedGrant.Budget.Tokens.Value);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_work_awards"));
    }
}
