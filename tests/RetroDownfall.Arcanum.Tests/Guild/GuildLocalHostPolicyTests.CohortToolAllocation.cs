using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData("create_cohort", 5000)]
    [InlineData("admit_cohort_member", 4097)]
    [InlineData("close_cohort", 5000)]
    [InlineData("remove_cohort_member", 4097)]
    public async Task Cohort_tool_output_refusal_rolls_back_mutation_and_same_call_can_retry_once(string name, long allocation)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        f.Settings.CurrentValue.Features.Conclave = true;

        f.Settings.CurrentValue.Providers = [new() { Name = "provider", Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://provider.example.test/v1", Models = [new() { Name = "model" }] }];

        var owner = await f.OperatorAsync();

        var parent = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with
            { Tools = [.. CohortTools()], Delegation = [GuildDelegationKind.Local, GuildDelegationKind.Cohort] } });

        Assert.True(parent.IsSuccess, parent.Error.Message);

        var execution = await ToolExecutionAsync(f, owner, parent.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        GuildCohortStore cohorts = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority, f.Store, new(), f.Policy, TimeProvider.System);

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, new ChildQueue(), cohorts: cohorts);

        string displayName = new('N', 8192);

        AIFunctionArguments arguments = new() { ["name"] = displayName, ["policy"] = "all", ["resultType"] = "text/plain" };

        if (name != "create_cohort")
        {
            var created = await cohorts.CreateAsync(execution.Driver.Caller!, new(Guid.NewGuid(), parent.Value.Quest.Id,
                displayName, new(GuildConvergenceKind.All, null), new("text/plain"), null), default);

            Assert.True(created.IsSuccess, created.Error.Message);

            Guid? memberId = null;

            if (name is "close_cohort" or "remove_cohort_member")
            {
                Guid issue = Guid.NewGuid();

                var issuer = await f.Store.GetAsync(owner, parent.Value.Quest.Id, default);

                var member = await f.Store.IssueCohortMemberAsync(execution.Driver.Caller!,
                    new(f.Generation, parent.Value.Quest.Id, null, issue, issue), created.Value.Id, created.Value.Revision,
                    new(issue, issuer.Value.Quest.Revision, null, "Member", "Vote", new()), default);

                Assert.True(member.IsSuccess, member.Error.Message);

                await using var connection = await f.OpenAsync();

                await using var query = connection.CreateCommand();

                query.CommandText = "SELECT Id FROM guild_cohort_members WHERE PrimaryVotingQuestId=$quest";

                query.Parameters.AddWithValue("$quest", member.Value.Quest.Id.ToString("D").ToUpperInvariant());

                memberId = Guid.Parse(Assert.IsType<string>(await query.ExecuteScalarAsync()));
            }

            var current = await cohorts.GetAsync(execution.Driver.Caller!, created.Value.Id, default);

            Assert.True(current.IsSuccess, current.Error.Message);

            arguments = new() { ["cohortId"] = created.Value.Id.ToString(), ["expectedRevision"] = current.Value.Revision };

            if (name == "admit_cohort_member") arguments["goal"] = "A bounded returned membership receipt";

            if (memberId is not null && name == "remove_cohort_member") arguments["memberId"] = memberId.Value.ToString();
        }

        Guid step = Guid.NewGuid();

        Guid logical = Guid.NewGuid();

        GuildToolGrant tool = CohortTools().Single(value => value.Name == name);

        async Task<object?> Invoke(long bytes)
        {
            using GuildLocalToolRegistry registry = new(execution, input.Value, coordinator, outputAllocationBytes: bytes);

            var context = f.Store.CreateToolContext(execution, registry, _ => true);

            var function = await context.AdvertiseAsync(new InertChildTool(name), tool, default);

            Assert.NotNull(function);

            using IDisposable frame = registry.BeginInvocation(step, logical, tool);

            return await context.InvokeBoundAsync(function, arguments, default);
        }

        async Task<long[]> Snapshot() =>
        [
            await f.ScalarAsync("SELECT count(*) FROM guild_cohorts"),
            await f.ScalarAsync("SELECT count(*) FROM guild_quests"),
            await f.ScalarAsync("SELECT count(*) FROM guild_signets"),
            await f.ScalarAsync("SELECT count(*) FROM guild_budget_accounts"),
            await f.ScalarAsync("SELECT count(*) FROM guild_cohort_members"),
            await f.ScalarAsync("SELECT count(*) FROM guild_cohort_mutations"),
            await f.ScalarAsync("SELECT count(*) FROM guild_cohort_stop_frontiers"),
            await f.ScalarAsync("SELECT count(*) FROM guild_quest_cancellations"),
            await f.ScalarAsync("SELECT count(*) FROM guild_events"),
            await f.ScalarAsync("SELECT coalesce(sum(Revision),0) FROM guild_cohorts"),
            await f.ScalarAsync("SELECT coalesce(sum(StateCode),0) FROM guild_cohort_members"),
        ];

        long[] before = await Snapshot();

        string refused = Assert.IsType<string>(await Invoke(allocation));

        Assert.Contains(ErrorCodes.Guild.PayloadTooLarge, refused);

        Assert.Equal(before, await Snapshot());

        var accepted = Assert.IsType<GuildLocalChildToolResult>(await Invoke(1_048_576));

        Guid acceptedId = accepted.Projection!.Value.GetProperty("id").GetGuid();

        Assert.NotEqual(Guid.Empty, acceptedId);

        long[] committed = await Snapshot();

        var replay = Assert.IsType<GuildLocalChildToolResult>(await Invoke(1_048_576));

        Assert.Equal(acceptedId, replay.Projection!.Value.GetProperty("id").GetGuid());

        Assert.True(replay.Projection.Value.GetProperty("receipt").GetProperty("replayed").GetBoolean());

        Assert.Equal(committed, await Snapshot());
    }
}
