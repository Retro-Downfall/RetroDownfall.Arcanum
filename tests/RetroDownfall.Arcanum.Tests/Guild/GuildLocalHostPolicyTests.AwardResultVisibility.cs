using System.Text.Json;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Tests.Intelligence;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Winning_primary_reads_associated_award_result_but_cannot_cancel_sibling()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var setup = await AwardPeerVisibilityFixtureAsync(f);

        await CommitConvergenceResultAsync(f, setup.Owner, setup.Work, "award result");

        var execution = await ToolExecutionAsync(f, setup.Owner, setup.Primary);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, new ChildQueue());

        using GuildLocalToolRegistry registry = new(execution, input.Value, coordinator);

        var context = f.Store.CreateToolContext(execution, registry, _ => true);

        GuildToolGrant get = new(GuildToolSourceKind.InternalMcp, "arcanum", "get_agent_task");

        var function = await context.AdvertiseAsync(new InertChildTool(get.Name), get, default);

        Assert.NotNull(function);

        using (registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), get))
        {
            object? raw = await context.InvokeBoundAsync(function, new() { ["questId"] = setup.Work.Quest.Id.ToString() }, default);

            Assert.False(raw is string, raw?.ToString());

            var accepted = Assert.IsType<GuildLocalChildToolResult>(raw);

            Assert.Equal(setup.Work.Quest.Id, accepted.Quest.Id);

            Assert.Equal(QuestState.Completed, accepted.Quest.State);

            Assert.NotNull(accepted.Quest.ResultRef);
        }

        using (registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), get))
        {
            var output = await ToolExecutionPipelineTests.CreatePipeline().ProcessSingleToolCallAsync(
                new FunctionCallContent("award-read", get.Name, new Dictionary<string, object?> { ["questId"] = setup.Work.Quest.Id.ToString() }),
                new PingRequest("read"), new ChatOptions { Tools = [function] }, null, null,
                new ToolExecutionPipeline.TurnContext { GuildTools = context }, true, default);

            Assert.False(output.Denied);

            Assert.Contains("Authority: Peer", output.ResultText);
        }

        GuildToolGrant cancel = new(GuildToolSourceKind.InternalMcp, "arcanum", "cancel_agent_task");

        var cancellation = await context.AdvertiseAsync(new InertChildTool(cancel.Name), cancel, default);

        Assert.NotNull(cancellation);

        using (registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), cancel))
        {
            object? denied = await context.InvokeBoundAsync(cancellation, new() { ["questId"] = setup.Work.Quest.Id.ToString(), ["expectedRevision"] = 1L }, default);

            Assert.Contains(ErrorCodes.Guild.NotFound, Assert.IsType<string>(denied));
        }

        await f.ExecuteAsync($"UPDATE guild_signets SET Revoked=1,Revision=Revision+1,RevocationGeneration=RevocationGeneration+1 WHERE QuestId='{setup.Primary.Quest.Id:D}' COLLATE NOCASE");

        using (registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), get))
        {
            object? denied = await context.InvokeBoundAsync(function, new() { ["questId"] = setup.Work.Quest.Id.ToString() }, default);

            Assert.Contains(ErrorCodes.Guild.AuthorityRevoked, Assert.IsType<string>(denied));
        }
    }

    [Fact]
    public async Task Award_result_publishes_typed_receipt_to_exact_winning_primary()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var setup = await AwardPeerVisibilityFixtureAsync(f);

        await CommitConvergenceResultAsync(f, setup.Owner, setup.Work, "award result canary");

        Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_inbox WHERE RecipientQuestId='{setup.Primary.Quest.Id:D}' COLLATE NOCASE"));

        var current = await f.Store.GetAsync(setup.Owner, setup.Primary.Quest.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        GuildCallerContext primary = await BindCohortRunAsync(f, setup.Owner, current.Value);

        GuildPortalStore portal = new(f.Store, f.Authority, f.Policy, TimeProvider.System);

        var envelope = await f.Authority.InQuestTransactionAsync<GuildEnvelope>(primary.Execution!.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                await using var command = connection.CreateCommand();

                command.Transaction = transaction;
                command.CommandText = "SELECT EnvelopeId FROM guild_inbox WHERE RecipientQuestId=$quest";
                command.Parameters.AddWithValue("$quest", setup.Primary.Quest.Id.ToString("D").ToUpperInvariant());

                Guid id = Guid.Parse((string)(await command.ExecuteScalarAsync(token))!);

                return await GuildPortalStore.ReadEnvelopeWithinAsync(connection, transaction, id, token);
            }, default);

        Assert.True(envelope.IsSuccess, envelope.Error.Message);

        var body = await f.Authority.InQuestTransactionAsync<byte[]>(primary.Execution!.RootQuestId,
            (connection, transaction, signets, token) => portal.ReadBodyWithinAsync(connection, transaction, signets, primary, envelope.Value, token), default);

        Assert.True(body.IsSuccess, body.Error.Message);

        var notice = JsonSerializer.Deserialize(body.Value, GuildJsonContext.Default.GuildCohortProgress);

        Assert.NotNull(notice);

        Assert.Equal("work.result", notice.Type);

        Assert.Equal(setup.Work.Quest.Id, notice.ObjectId);

        Assert.DoesNotContain("award result canary", System.Text.Encoding.UTF8.GetString(body.Value));

        var prompt = await f.Authority.InQuestTransactionAsync<string>(primary.Execution!.RootQuestId,
            (connection, transaction, signets, token) => portal.ReadInputPromptWithinAsync(connection, transaction, signets, primary, envelope.Value, token), default);

        Assert.True(prompt.IsSuccess, prompt.Error.Message);

        Assert.Contains("award result canary", prompt.Value);

        Assert.Contains("DATA", prompt.Value);

        await f.ExecuteAsync($"UPDATE guild_signets SET Revoked=1,Revision=Revision+1,RevocationGeneration=RevocationGeneration+1 WHERE QuestId='{setup.Primary.Quest.Id:D}' COLLATE NOCASE");

        var revoked = await f.Authority.InQuestTransactionAsync<string>(primary.Execution!.RootQuestId,
            (connection, transaction, signets, token) => portal.ReadInputPromptWithinAsync(connection, transaction, signets, primary, envelope.Value, token), default);

        Assert.True(revoked.IsFailure);
    }

    [Fact]
    public async Task Award_work_reads_its_exact_primary_but_not_an_unrelated_same_agent_quest()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var setup = await AwardPeerVisibilityFixtureAsync(f);

        var issuer = await f.Store.GetAsync(setup.Owner, setup.Issuer.Quest.Id, default);

        Assert.True(issuer.IsSuccess, issuer.Error.Message);

        GuildCallerContext issuerRun = await BindCohortRunAsync(f, setup.Owner, issuer.Value);

        Guid key = Guid.NewGuid();

        var unrelated = await f.Store.IssueAsync(issuerRun, new(f.Generation, issuer.Value.Quest.Id, null, key, key), issuer.Value.Quest.Id,
            new(key, issuer.Value.Quest.Revision, setup.Primary.Quest.AgentId, "Unrelated", "Work", new()), default);

        Assert.True(unrelated.IsSuccess, unrelated.Error.Message);

        var execution = await ToolExecutionAsync(f, setup.Owner, setup.Work);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, new ChildQueue());

        using GuildLocalToolRegistry registry = new(execution, input.Value, coordinator);

        var context = f.Store.CreateToolContext(execution, registry, _ => true);

        GuildToolGrant get = new(GuildToolSourceKind.InternalMcp, "arcanum", "get_agent_task");

        var function = await context.AdvertiseAsync(new InertChildTool(get.Name), get, default);

        Assert.NotNull(function);

        using (registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), get))
        {
            object? raw = await context.InvokeBoundAsync(function, new() { ["questId"] = setup.Primary.Quest.Id.ToString() }, default);

            Assert.Equal(setup.Primary.Quest.Id, Assert.IsType<GuildLocalChildToolResult>(raw).Quest.Id);
        }

        using (registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), get))
        {
            object? denied = await context.InvokeBoundAsync(function, new() { ["questId"] = unrelated.Value.Quest.Id.ToString() }, default);

            Assert.Contains(ErrorCodes.Guild.NotFound, Assert.IsType<string>(denied));
        }
    }

    private static async Task<(GuildCallerContext Owner, GuildQuestStored Issuer, GuildQuestStored Primary, GuildQuestStored Work)>
        AwardPeerVisibilityFixtureAsync(Fixture f)
    {
        f.Settings.CurrentValue.Features.Conclave = true;
        f.Settings.CurrentValue.Providers = [new() { Name = "provider", Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://api.example.test/v1", Models = [new() { Name = "model" }] }];

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant[] tools = [new(GuildToolSourceKind.InternalMcp, "arcanum", "get_agent_task"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "cancel_agent_task")];

        var root = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with
            { Tools = [.. tools], Delegation = [GuildDelegationKind.Local, GuildDelegationKind.Cohort] } });

        Assert.True(root.IsSuccess, root.Error.Message);

        GuildCallerContext issuer = await BindCohortRunAsync(f, owner, root.Value);

        GuildCohortStore cohorts = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority, f.Store, new(), f.Policy, TimeProvider.System);

        var cohort = await cohorts.CreateAsync(issuer, new(Guid.NewGuid(), root.Value.Quest.Id, "Results",
            new(GuildConvergenceKind.All, null), new("text/plain"), null), default);

        Assert.True(cohort.IsSuccess, cohort.Error.Message);

        GuildStore cards = new(f.Connections, f.Gate, TimeProvider.System);

        GuildAgentCard card = new(Guid.Empty, Guid.Empty, 0, "Reviewer", "Reviewer", "1", [], ["text/plain"], ["text/plain"],
            new(false, false, false), new(GuildAgentSourceKind.DeclaredGlobal, null, null, true, null));

        var saved = await cards.UpsertCardAsync(new("award-result-reviewer", null, null, null,
            new(card, "Review", [.. tools.Select(t => t.Name)]), new string('A', 64)), default);

        Assert.True(saved.IsSuccess, saved.Error.Message);

        var current = await f.Store.GetAsync(owner, root.Value.Quest.Id, default);

        Guid memberKey = Guid.NewGuid();

        var primary = await f.Store.IssueCohortMemberAsync(issuer, new(f.Generation, root.Value.Quest.Id, null, memberKey, memberKey),
            cohort.Value.Id, cohort.Value.Revision, new(memberKey, current.Value.Quest.Revision, null, "Primary", "Review", new(), CardId: saved.Value.Payload.Card.CardId), default);

        Assert.True(primary.IsSuccess, primary.Error.Message);

        current = await f.Store.GetAsync(owner, root.Value.Quest.Id, default);

        Guid workKey = Guid.NewGuid();

        var work = await f.Store.IssueAsync(issuer, new(f.Generation, root.Value.Quest.Id, null, workKey, workKey), root.Value.Quest.Id,
            new(workKey, current.Value.Quest.Revision, primary.Value.Quest.AgentId, "Award", "Work", new()), default);

        Assert.True(work.IsSuccess, work.Error.Message);

        string workId = work.Value.Quest.Id.ToString("D").ToUpperInvariant();
        string primaryId = primary.Value.Quest.Id.ToString("D").ToUpperInvariant();

        await f.ExecuteAsync($"INSERT INTO guild_cohort_member_quests(MemberId,QuestId,AssociationKindCode,AssociationSequence,Active) SELECT Id,'{workId}',2,2,1 FROM guild_cohort_members WHERE PrimaryVotingQuestId='{primaryId}'");

        await SeedSyntheticAwardAuthorityAsync(f, cohort.Value.Id, root.Value.Quest.Id, primary.Value, work.Value);

        await CompleteSyntheticAwardAuthorityAsync(f, work.Value.Quest.Id);

        return (owner, root.Value, primary.Value, work.Value);
    }
}
