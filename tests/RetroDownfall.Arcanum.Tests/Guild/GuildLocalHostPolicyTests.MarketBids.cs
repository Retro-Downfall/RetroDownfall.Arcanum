using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Bid_submission_replays_exact_identity_refuses_second_bid_and_withdraws_once()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var (owner, _, root, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 1);

        Guid offer = await SeedMarketReadOfferAsync(f, cohort.Id, root, members[0], 1);

        GuildCallerContext bidder = await BindCohortRunAsync(f, owner, members[0]);

        SubmitBidRequest request = new(Guid.NewGuid(), 1, "I can review this", 0.25m);

        GuildMutationIdentity mutation = new(request.IdempotencyKey,
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, GuildJsonContext.Default.SubmitBidRequest))));

        var first = await cohorts.SubmitBidAsync(bidder.Execution!, mutation, offer, request, default);

        Assert.True(first.IsSuccess, first.Error.Message);

        var replay = await cohorts.SubmitBidAsync(bidder.Execution!, mutation, offer, request, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(first.Value.Id, replay.Value.Id);

        Assert.True(replay.Value.Receipt!.Replayed);

        SubmitBidRequest changed = request with { Note = "Different advice" };

        GuildMutationIdentity changedMutation = new(changed.IdempotencyKey,
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(changed, GuildJsonContext.Default.SubmitBidRequest))));

        Assert.Equal(ErrorCodes.Guild.IdempotencyConflict, (await cohorts.SubmitBidAsync(bidder.Execution!, changedMutation, offer, changed, default)).Error.Code);

        SubmitBidRequest second = request with { IdempotencyKey = Guid.NewGuid() };

        GuildMutationIdentity secondMutation = new(second.IdempotencyKey,
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(second, GuildJsonContext.Default.SubmitBidRequest))));

        Assert.Equal(ErrorCodes.Guild.BidConflict, (await cohorts.SubmitBidAsync(bidder.Execution!, secondMutation, offer, second, default)).Error.Code);

        GuildRevisionRequest withdrawal = new(Guid.NewGuid(), first.Value.Revision);

        GuildMutationIdentity withdrawalMutation = new(withdrawal.IdempotencyKey,
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(withdrawal, GuildJsonContext.Default.GuildRevisionRequest))));

        var withdrawn = await cohorts.WithdrawBidAsync(bidder.Execution!, withdrawalMutation, first.Value.Id, withdrawal, default);

        Assert.True(withdrawn.IsSuccess, withdrawn.Error.Message);

        Assert.Equal(GuildWorkBidState.Withdrawn, withdrawn.Value.State);

        var repeated = await cohorts.WithdrawBidAsync(bidder.Execution!, withdrawalMutation, first.Value.Id, withdrawal, default);

        Assert.True(repeated.IsSuccess, repeated.Error.Message);

        Assert.True(repeated.Value.Receipt!.Replayed);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_work_bids"));
    }

    [Theory]
    [InlineData("changed-card")]
    [InlineData("revoked")]
    public async Task Bid_refuses_stale_recipient_or_revoked_run_without_effects(string boundary)
    {
        await using Fixture f = await Fixture.CreateAsync();

        var (owner, _, root, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 1);

        Guid offer = await SeedMarketReadOfferAsync(f, cohort.Id, root, members[0], 1);

        GuildCallerContext bidder = await BindCohortRunAsync(f, owner, members[0]);

        if (boundary == "changed-card")
        {
            await f.ExecuteAsync($"UPDATE guild_agent_cards SET Available=0 WHERE CardId='{members[0].Quest.CardId.ToString("D").ToUpperInvariant()}'");
        }
        else
        {
            var current = await f.Store.GetAsync(owner, members[0].Quest.Id, default);

            Assert.True(current.IsSuccess, current.Error.Message);

            Guid key = Guid.NewGuid();

            var revoked = await f.Authority.RevokeAsync(owner, new(key, new string('A', 64)), current.Value.Quest.SignetId,
                new(key, current.Value.Quest.SignetRevision, GuildCancellationReason.OperatorRequested), default);

            Assert.True(revoked.IsSuccess, revoked.Error.Message);
        }

        SubmitBidRequest request = new(Guid.NewGuid(), 1, "I can review this", null);

        GuildMutationIdentity mutation = new(request.IdempotencyKey,
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, GuildJsonContext.Default.SubmitBidRequest))));

        var result = await cohorts.SubmitBidAsync(bidder.Execution!, mutation, offer, request, default);

        Assert.Equal(boundary == "changed-card" ? ErrorCodes.Guild.DestinationChanged : ErrorCodes.Guild.AuthorityRevoked, result.Error.Code);

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_work_bids"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_work_mutations"));
    }

    [Fact]
    public async Task Bid_tools_commit_member_authored_submission_and_withdrawal()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        f.Settings.CurrentValue.Features.Conclave = true;

        f.Settings.CurrentValue.Providers = [new() { Name = "provider", Type = RetroDownfall.Arcanum.Core.Configuration.AiProviderKind.OpenAICompatible,
            Endpoint = "https://api.example.test/v1", Models = [new() { Name = "model" }] }];

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant[] tools = [new(GuildToolSourceKind.InternalMcp, "arcanum", "submit_bid"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "withdraw_bid")];

        var root = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with
            { Tools = [.. tools], Delegation = [GuildDelegationKind.Local, GuildDelegationKind.Cohort] } });

        Assert.True(root.IsSuccess, root.Error.Message);

        GuildCallerContext issuer = await BindCohortRunAsync(f, owner, root.Value);

        GuildCohortStore cohorts = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority, f.Store, new(), f.Policy, TimeProvider.System);

        var cohort = await cohorts.CreateAsync(issuer, new(Guid.NewGuid(), root.Value.Quest.Id, "Panel", new(GuildConvergenceKind.All, null), new("text/plain"), null), default);

        Assert.True(cohort.IsSuccess, cohort.Error.Message);

        var rootCurrent = await f.Store.GetAsync(owner, root.Value.Quest.Id, default);

        Guid memberKey = Guid.NewGuid();

        var member = await f.Store.IssueCohortMemberAsync(issuer, new(f.Generation, root.Value.Quest.Id, null, memberKey, memberKey),
            cohort.Value.Id, cohort.Value.Revision, new(memberKey, rootCurrent.Value.Quest.Revision, null, "Reviewer", "Review", new()), default);

        Assert.True(member.IsSuccess, member.Error.Message);

        Guid offer = await SeedMarketReadOfferAsync(f, cohort.Value.Id, root.Value, member.Value, 1);

        var execution = await ToolExecutionAsync(f, owner, member.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, new ChildQueue(), cohorts: cohorts);

        using (GuildLocalToolRegistry smallRegistry = new(execution, input.Value, coordinator, 4096))
        {
            var smallContext = f.Store.CreateToolContext(execution, smallRegistry, _ => true);

            var smallFunction = await smallContext.AdvertiseAsync(new InertChildTool("submit_bid"), tools[0], default);

            Assert.NotNull(smallFunction);

            using IDisposable frame = smallRegistry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), tools[0]);

            object? tooLarge = await smallContext.InvokeBoundAsync(smallFunction,
                new() { ["offerId"] = offer.ToString(), ["expectedRevision"] = 1L, ["note"] = "I can review" }, default);

            Assert.Contains(ErrorCodes.Guild.PayloadTooLarge, Assert.IsType<string>(tooLarge));

            Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_work_bids"));

            Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_work_mutations"));
        }

        using GuildLocalToolRegistry registry = new(execution, input.Value, coordinator);

        var context = f.Store.CreateToolContext(execution, registry, _ => true);

        async Task<GuildLocalChildToolResult> Invoke(string name, AIFunctionArguments arguments)
        {
            GuildToolGrant tool = tools.Single(t => t.Name == name);

            var function = await context.AdvertiseAsync(new InertChildTool(name), tool, default);

            Assert.NotNull(function);

            using IDisposable frame = registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), tool);

            object? result = await context.InvokeBoundAsync(function, arguments, default);

            Assert.False(result is string, result?.ToString());

            return Assert.IsType<GuildLocalChildToolResult>(result);
        }

        var submitted = await Invoke("submit_bid", new() { ["offerId"] = offer.ToString(), ["expectedRevision"] = 1L, ["note"] = "I can review" });

        Assert.Equal(member.Value.Quest.AgentId, submitted.Projection!.Value.GetProperty("agentId").GetGuid());

        Guid bid = submitted.Projection.Value.GetProperty("id").GetGuid();

        var withdrawn = await Invoke("withdraw_bid", new() { ["bidId"] = bid.ToString(), ["expectedRevision"] = 1L });

        Assert.Equal("withdrawn", withdrawn.Projection!.Value.GetProperty("state").GetString());

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_work_bids"));
    }
}
