using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Intelligence;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData("absent")]
    [InlineData("partial")]
    [InlineData("zero")]
    public async Task Financial_off_real_child_inference_is_consumed_by_parent_provider_round(string usage)
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        f.Settings.CurrentValue.Features.Conclave = true;

        f.Settings.CurrentValue.Features.Apprentices = true;

        f.Settings.CurrentValue.Cost.Budget.Enabled = false;

        await using GuildLocalHostReadiness readiness = new(f.Settings, f.Connections, f.Policy, f.Authority,
            TimeProvider.System, Path.Combine(f.WorkspaceRoot, "host.lock"), f.Gate);

        f.Readiness.Inner = readiness;

        Assert.True((await readiness.EnsureReadyAsync(default)).IsSuccess);

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant tool = new(GuildToolSourceKind.BuiltIn, "arcanum", "delegate_task");

        var root = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with { Tools = [tool] } });

        Assert.True(root.IsSuccess, root.Error.Message);

        GuildLocalInferenceExecution execution = await ToolExecutionAsync(f, owner, root.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        Assert.True(input.IsSuccess, input.Error.Message);

        f.ToolChildMode = GuildLocalExecutionMode.Sterile;

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        var childOpened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(childOpened.IsSuccess, childOpened.Error.Message);

        await using var childConnection = childOpened.Value;

        await using ArcanumDbContext childDb = GrimoireFixture.CreateContext(childConnection.Connection);

        ArcanumSettings settings = new()
        {
            Features = new() { Guild = true }, DefaultModel = "accepted-model",
            Providers = [new() { Name = "accepted-provider", Type = AiProviderKind.OpenAICompatible,
                Endpoint = "https://example.test/v1", GuildUsageReconciliation = GuildUsageReconciliationSource.None, Models = ["accepted-model"] }],
        };

        settings.Cost.Budget.Enabled = false;

        BudgetReservationService daily = new(db, new TestOptionsMonitor<ArcanumSettings>(settings));

        using FinancialOffTransport transport = new() { Usage = usage };

        ChatClientFactory clients = new(transport, new TestOptionsMonitor<ArcanumSettings>(settings));

        ChildQueue queue = new();

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, queue);

        WizardIntelligenceProviderTests wizard = new();

        WizardIntelligenceProviderTests childWizard = new();

        try
        {
            var runtime = await wizard.CreateGuildRuntimeAsync(daily, settings, clients, coordinator);

            var childRuntime = await childWizard.CreateGuildRuntimeAsync(
                new BudgetReservationService(childDb, new TestOptionsMonitor<ArcanumSettings>(settings)), settings, clients, coordinator);

            using ServiceProvider services = new ServiceCollection().BuildServiceProvider();

            using ApprenticeService engine = new(services.GetRequiredService<IServiceScopeFactory>(), f.Settings, new ChronicleHub(),
                NullLogger<ApprenticeService>.Instance, f.Gate);

            using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(2));

            var invocation = runtime.Runtime.ExecuteTurnAsync(execution, input.Value,
                new("Delegate the task.", DisableAllTools: false), deadline.Token);

            Task<GuildQuestDto> scheduled = queue.Pending.Reader.ReadAsync(deadline.Token).AsTask();

            Assert.Same(scheduled, await Task.WhenAny(scheduled, invocation));

            GuildQuestDto child = await scheduled;

            Assert.Equal(GuildBudgetMode.Unlimited, child.ResolvedGrant.Budget.Usd.Mode);

            Assert.Equal(GuildBudgetMode.Unlimited, child.ResolvedGrant.Budget.Tokens.Mode);

            Assert.Empty(child.ResolvedGrant.Tools);

            Assert.Empty(child.ResolvedGrant.Workspaces);

            Assert.Empty(child.ResolvedGrant.Memory);

            Assert.Empty(child.ResolvedGrant.Delegation);

            var recovery = await f.Policy.CreateRecoveryAsync(owner, child.Id, child.OperationId, child.SignetRevision, deadline.Token);

            Assert.True(recovery.IsSuccess, recovery.Error.Message);

            var handle = await f.Authority.RebindAsync(recovery.Value, deadline.Token);

            Assert.True(handle.IsSuccess, handle.Error.Message);

            var pending = await f.Store.ReadLocalPendingAsync(owner, child.Id, deadline.Token);

            Assert.True(pending.IsSuccess, pending.Error.Message);

            GuildLocalPort port = new(f.Store, f.Policy, f.Authority, new LongRunningOperationStore(childDb, f.Connections),
                engine, childRuntime.Runtime, queue, new(), TimeProvider.System);

            var completed = await port.DispatchAsync(handle.Value, pending.Value.Request!, deadline.Token);

            Assert.True(completed.IsSuccess, completed.Error.Message);

            var result = await invocation;

            Assert.True(result.IsSuccess, result.Error.Message);

            Assert.Equal("parent used the real child answer", result.Value.Text);

            Assert.Equal(3, transport.Bodies.Count);

            Assert.Equal(0, transport.MetadataGets);

            Assert.Contains("unique real child answer", transport.Bodies[2], StringComparison.Ordinal);

            Assert.Contains("UNTRUSTED DATA", transport.Bodies[2], StringComparison.Ordinal);

            using JsonDocument first = JsonDocument.Parse(transport.Bodies[0]);

            JsonElement schema = first.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("parameters");

            Assert.Equal("prompt", Assert.Single(schema.GetProperty("required").EnumerateArray()).GetString());

            Assert.False(schema.TryGetProperty("anyOf", out _));

            Assert.False(schema.GetProperty("properties").GetProperty("files").GetProperty("items").GetProperty("properties").TryGetProperty("attachment_id", out _));

            var childResult = await f.Store.ReadLocalResultAsync(owner, child.Id, true, deadline.Token);

            Assert.True(childResult.IsSuccess, childResult.Error.Message);

            Assert.Equal("unique real child answer", childResult.Value.Body);

            Assert.NotNull(childResult.Value.Receipt);

            Assert.Equal(3L, await f.ScalarAsync($"SELECT count(*) FROM guild_budget_effects WHERE StateCode=2 AND UnknownUsd=1 AND UnknownTokens={(usage == "zero" ? 0 : 1)} AND KnownTokens={(usage == "partial" ? 7 : 0)}"));

            Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_result_applications"));

            Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM BudgetReservations"));
        }
        finally
        {
            await childWizard.DisposeAsync();

            await wizard.DisposeAsync();
        }
    }

    [Fact]
    public async Task Financial_off_real_parent_and_child_ports_commit_actual_provider_results()
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        f.Settings.CurrentValue.Features.Conclave = true;

        f.Settings.CurrentValue.Features.Apprentices = true;

        f.Settings.CurrentValue.Cost.Budget.Enabled = false;

        await using GuildLocalHostReadiness readiness = new(f.Settings, f.Connections, f.Policy, f.Authority,
            TimeProvider.System, Path.Combine(f.WorkspaceRoot, "host.lock"), f.Gate);

        f.Readiness.Inner = readiness;

        Assert.True((await readiness.EnsureReadyAsync(default)).IsSuccess);

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant tool = new(GuildToolSourceKind.BuiltIn, "arcanum", "delegate_task");

        var root = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with { Tools = [tool] } });

        Assert.True(root.IsSuccess, root.Error.Message);

        var rootRecovery = await f.Policy.CreateRecoveryAsync(owner, root.Value.Quest.Id, root.Value.Quest.OperationId,
            root.Value.Quest.SignetRevision, default);

        Assert.True(rootRecovery.IsSuccess, rootRecovery.Error.Message);

        var rootHandle = await f.Authority.RebindAsync(rootRecovery.Value, default);

        Assert.True(rootHandle.IsSuccess, rootHandle.Error.Message);

        var rootPending = await f.Store.ReadLocalPendingAsync(owner, root.Value.Quest.Id, default);

        Assert.True(rootPending.IsSuccess, rootPending.Error.Message);

        f.ToolChildMode = GuildLocalExecutionMode.Sterile;

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        var childOpened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(childOpened.IsSuccess, childOpened.Error.Message);

        await using var childConnection = childOpened.Value;

        await using ArcanumDbContext childDb = GrimoireFixture.CreateContext(childConnection.Connection);

        ArcanumSettings settings = new()
        {
            Features = new() { Guild = true }, DefaultModel = "accepted-model",
            Providers = [new() { Name = "accepted-provider", Type = AiProviderKind.OpenAICompatible,
                Endpoint = "https://example.test/v1", GuildUsageReconciliation = GuildUsageReconciliationSource.None, Models = ["accepted-model"] }],
        };

        settings.Cost.Budget.Enabled = false;

        BudgetReservationService daily = new(db, new TestOptionsMonitor<ArcanumSettings>(settings));

        using FinancialOffTransport transport = new() { FullParent = true };

        ChatClientFactory clients = new(transport, new TestOptionsMonitor<ArcanumSettings>(settings));

        ChildQueue queue = new();

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, queue);

        WizardIntelligenceProviderTests wizard = new();

        WizardIntelligenceProviderTests childWizard = new();

        try
        {
            var runtime = await wizard.CreateGuildRuntimeAsync(daily, settings, clients, coordinator);

            var childRuntime = await childWizard.CreateGuildRuntimeAsync(
                new BudgetReservationService(childDb, new TestOptionsMonitor<ArcanumSettings>(settings)), settings, clients, coordinator);

            using ServiceProvider services = new ServiceCollection().BuildServiceProvider();

            using ApprenticeService engine = new(services.GetRequiredService<IServiceScopeFactory>(), f.Settings, new ChronicleHub(),
                NullLogger<ApprenticeService>.Instance, f.Gate);

            using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(2));

            GuildLocalPort parentPort = new(f.Store, f.Policy, f.Authority, new LongRunningOperationStore(db, f.Connections),
                engine, runtime.Runtime, queue, new(), TimeProvider.System);

            var invocation = parentPort.DispatchAsync(rootHandle.Value, rootPending.Value.Request!, deadline.Token);

            Task<GuildQuestDto> scheduled = queue.Pending.Reader.ReadAsync(deadline.Token).AsTask();

            Assert.Same(scheduled, await Task.WhenAny(scheduled, invocation));

            GuildQuestDto child = await scheduled;

            Assert.Equal(GuildBudgetMode.Unlimited, child.ResolvedGrant.Budget.Usd.Mode);

            Assert.Equal(GuildBudgetMode.Unlimited, child.ResolvedGrant.Budget.Tokens.Mode);

            Assert.Empty(child.ResolvedGrant.Tools);

            Assert.Empty(child.ResolvedGrant.Workspaces);

            Assert.Empty(child.ResolvedGrant.Memory);

            Assert.Empty(child.ResolvedGrant.Delegation);

            var recovery = await f.Policy.CreateRecoveryAsync(owner, child.Id, child.OperationId, child.SignetRevision, deadline.Token);

            Assert.True(recovery.IsSuccess, recovery.Error.Message);

            var handle = await f.Authority.RebindAsync(recovery.Value, deadline.Token);

            Assert.True(handle.IsSuccess, handle.Error.Message);

            var pending = await f.Store.ReadLocalPendingAsync(owner, child.Id, deadline.Token);

            Assert.True(pending.IsSuccess, pending.Error.Message);

            GuildLocalPort port = new(f.Store, f.Policy, f.Authority, new LongRunningOperationStore(childDb, f.Connections),
                engine, childRuntime.Runtime, queue, new(), TimeProvider.System);

            var completed = await port.DispatchAsync(handle.Value, pending.Value.Request!, deadline.Token);

            Assert.True(completed.IsSuccess, completed.Error.Message);

            var result = await invocation;

            Assert.True(result.IsSuccess, result.Error.Message);

            Assert.Equal(QuestState.Completed, result.Value.State);

            var parentResult = await f.Store.ReadLocalResultAsync(owner, root.Value.Quest.Id, true, deadline.Token);

            Assert.True(parentResult.IsSuccess, parentResult.Error.Message);

            Assert.Equal("parent used the real child answer", parentResult.Value.Body);

            Assert.NotNull(parentResult.Value.Receipt);

            var parentState = await f.Store.GetAsync(owner, root.Value.Quest.Id, deadline.Token);

            var childState = await f.Store.GetAsync(owner, child.Id, deadline.Token);

            Assert.True(parentState.IsSuccess, parentState.Error.Message);

            Assert.True(childState.IsSuccess, childState.Error.Message);

            Assert.Equal(GuildAccountingState.Settled, parentState.Value.Quest.AccountingState);

            Assert.Equal(GuildAccountingState.Settled, childState.Value.Quest.AccountingState);

            var replay = await parentPort.DispatchAsync(rootHandle.Value, rootPending.Value.Request!, deadline.Token);

            Assert.True(replay.IsSuccess, replay.Error.Message);

            Assert.Equal(5, transport.Bodies.Count);

            Assert.Equal(0, transport.MetadataGets);

            Assert.Contains("unique real child answer", transport.Bodies[3], StringComparison.Ordinal);

            Assert.Contains("UNTRUSTED DATA", transport.Bodies[3], StringComparison.Ordinal);

            using JsonDocument first = JsonDocument.Parse(transport.Bodies[1]);

            JsonElement schema = first.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("parameters");

            Assert.Equal("prompt", Assert.Single(schema.GetProperty("required").EnumerateArray()).GetString());

            Assert.False(schema.TryGetProperty("anyOf", out _));

            Assert.False(schema.GetProperty("properties").GetProperty("files").GetProperty("items").GetProperty("properties").TryGetProperty("attachment_id", out _));

            var childResult = await f.Store.ReadLocalResultAsync(owner, child.Id, true, deadline.Token);

            Assert.True(childResult.IsSuccess, childResult.Error.Message);

            Assert.Equal("unique real child answer", childResult.Value.Body);

            Assert.NotNull(childResult.Value.Receipt);

            Assert.Equal(5L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_effects WHERE StateCode=2 AND UnknownUsd=1 AND UnknownTokens=1"));

            Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_portal_outbox WHERE SourceKindCode=2"));

            Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_result_applications"));

            Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM BudgetReservations"));
        }
        finally
        {
            await childWizard.DisposeAsync();

            await wizard.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Financial_off_actual_local_cohort_members_converge(bool first)
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        f.Settings.CurrentValue.Features.Conclave = true;

        f.Settings.CurrentValue.Features.Apprentices = true;

        f.Settings.CurrentValue.Cost.Budget.Enabled = false;

        f.Settings.CurrentValue.Providers = [new() { Name = "accepted-provider", Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://example.test/v1", ContextWindowLimit = 131072, Models = ["accepted-model"] }];

        await using GuildLocalHostReadiness readiness = new(f.Settings, f.Connections, f.Policy, f.Authority,
            TimeProvider.System, Path.Combine(f.WorkspaceRoot, "host.lock"), f.Gate);

        f.Readiness.Inner = readiness;

        Assert.True((await readiness.EnsureReadyAsync(default)).IsSuccess);

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant[] tools = [.. CohortTools(), new(GuildToolSourceKind.InternalMcp, "arcanum", "send_agent_message")];

        var root = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with
            { Tools = [.. tools], Delegation = [GuildDelegationKind.Local, GuildDelegationKind.Cohort] } });

        Assert.True(root.IsSuccess, root.Error.Message);

        GuildLocalInferenceExecution execution = await ToolExecutionAsync(f, owner, root.Value);

        var rootInput = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        Assert.True(rootInput.IsSuccess, rootInput.Error.Message);

        GuildCallerContext issuer = execution.Driver.Caller!;

        GuildCohortStore cohorts = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority,
            f.Store, new GuildBudgetLedger(), f.Policy, TimeProvider.System);

        ChildQueue queue = new();

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, queue, cohorts: cohorts);

        using GuildLocalToolRegistry registry = new(execution, rootInput.Value, coordinator);

        var context = f.Store.CreateToolContext(execution, registry, _ => true);

        async Task<GuildLocalChildToolResult> Invoke(string name, AIFunctionArguments arguments)
        {
            GuildToolGrant tool = tools.Single(value => value.Name == name);

            var function = await context.AdvertiseAsync(new InertChildTool(name), tool, default);

            Assert.NotNull(function);

            using IDisposable frame = registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), tool);

            object? raw = await context.InvokeBoundAsync(function, arguments, default);

            Assert.False(raw is string, raw?.ToString());

            return Assert.IsType<GuildLocalChildToolResult>(raw);
        }

        var created = await Invoke("create_cohort", new() { ["name"] = "Panel", ["policy"] = first ? "first" : "all", ["resultType"] = "text/plain" });

        Guid cohortId = created.Projection!.Value.GetProperty("id").GetGuid();

        List<GuildQuestStored> members = [];

        for (int index = 0; index < 2; index++)
        {
            var currentCohort = await cohorts.GetAsync(issuer, cohortId, default);

            Assert.True(currentCohort.IsSuccess, currentCohort.Error.Message);

            var accepted = await Invoke("admit_cohort_member", new() { ["cohortId"] = cohortId.ToString(),
                ["expectedRevision"] = currentCohort.Value.Revision, ["goal"] = "Contribute" });

            var member = await f.Store.GetAsync(owner, accepted.Quest.Id, default);

            Assert.True(member.IsSuccess, member.Error.Message);

            members.Add(member.Value);
        }

        _ = await Invoke("send_agent_message", new() { ["questId"] = members[0].Quest.Id.ToString(),
            ["kind"] = "message", ["contentType"] = "text/plain", ["body"] = "parent informational message canary" });

        Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_inbox WHERE RecipientQuestId='{members[0].Quest.Id:D}' COLLATE NOCASE AND DeliveryStateCode=1"));

        Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_events e JOIN guild_envelopes m ON m.Id=e.EnvelopeId AND m.Id=e.ObjectId AND m.RootQuestId=e.RootQuestId JOIN guild_inbox i ON i.EnvelopeId=m.Id WHERE e.SourceKindCode=3 AND e.EventKindCode=1 AND e.Revision=1 AND i.RecipientQuestId='{members[0].Quest.Id:D}' COLLATE NOCASE AND i.DeliveryStateCode=1"));

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        ArcanumSettings settings = new()
        {
            Features = new() { Guild = true }, DefaultModel = "accepted-model",
            Providers = [new() { Name = "accepted-provider", Type = AiProviderKind.OpenAICompatible,
                Endpoint = "https://example.test/v1", ContextWindowLimit = 131072, GuildUsageReconciliation = GuildUsageReconciliationSource.None, Models = ["accepted-model"] }],
        };

        settings.Cost.Budget.Enabled = false;

        using FinancialOffMemberTransport transport = new();

        ChatClientFactory clients = new(transport, new TestOptionsMonitor<ArcanumSettings>(settings));

        WizardIntelligenceProviderTests wizard = new();

        try
        {
            var runtime = await wizard.CreateGuildRuntimeAsync(new BudgetReservationService(db, new TestOptionsMonitor<ArcanumSettings>(settings)), settings, clients, coordinator);

            using ServiceProvider services = new ServiceCollection().BuildServiceProvider();

            using ApprenticeService engine = new(services.GetRequiredService<IServiceScopeFactory>(), f.Settings, new ChronicleHub(),
                NullLogger<ApprenticeService>.Instance, f.Gate);

            using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(2));

            FinancialOffRuntimeCapture capture = new(runtime.Runtime);

            LongRunningOperationOwnership ownership = new();

            GuildLocalPort port = new(f.Store, f.Policy, f.Authority, new LongRunningOperationStore(db, f.Connections),
                engine, capture, queue, ownership, TimeProvider.System);

            foreach (GuildQuestStored member in first ? members.Take(1) : members)
            {
                var recovery = await f.Policy.CreateRecoveryAsync(owner, member.Quest.Id, member.Quest.OperationId, member.Quest.SignetRevision, deadline.Token);

                Assert.True(recovery.IsSuccess, recovery.Error.Message);

                var handle = await f.Authority.RebindAsync(recovery.Value, deadline.Token);

                Assert.True(handle.IsSuccess, handle.Error.Message);

                var pending = await f.Store.ReadLocalPendingAsync(owner, member.Quest.Id, deadline.Token);

                Assert.True(pending.IsSuccess, pending.Error.Message);

                var result = await port.DispatchAsync(handle.Value, pending.Value.Request!, deadline.Token);

                Assert.True(result.IsSuccess, result.Error.Message);

                Assert.True(result.Value.State == QuestState.Completed, $"state={result.Value.State}, reason={result.Value.ReasonCode}, evidence={result.Value.Evidence}, posts={transport.Posts}, runtime={capture.Diagnostic}");

                var body = await f.Store.ReadLocalResultAsync(owner, member.Quest.Id, true, deadline.Token);

                Assert.True(body.IsSuccess, body.Error.Message);

                Assert.Equal("actual cohort member answer", body.Value.Body);

                Assert.NotNull(body.Value.Receipt);
            }

            var current = await cohorts.GetAsync(issuer, cohortId, deadline.Token);

            Assert.True(current.IsSuccess, current.Error.Message);

            _ = await Invoke("close_cohort", new() { ["cohortId"] = cohortId.ToString(), ["expectedRevision"] = current.Value.Revision });

            CohortConvergenceDto draining = await WaitForConvergenceAsync(cohorts, owner, cohortId, value => value.State == GuildCohortState.Draining);

            Assert.Equal(2, draining.PendingMemberCount);

            if (first)
            {
                GuildQuestStored loser = members[1];

                var cancellation = await f.Store.ReadLocalCancellationAsync(owner, loser.Quest.Id, deadline.Token);

                Assert.True(cancellation.IsSuccess, cancellation.Error.Message);

                Assert.True(ownership.TryClaim(loser.Quest.OperationId, out Guid stopClaim));

                try
                {
                    var stoppedLoser = await port.RequestCancellationOwnedAsync(owner, cancellation.Value, loser.Quest.Id,
                        loser.Quest.Id, stopClaim, deadline.Token);

                    Assert.True(stoppedLoser.IsSuccess, stoppedLoser.Error.Message);

                    Assert.Equal(QuestState.Canceled, stoppedLoser.Value.State);
                }
                finally
                {
                    ownership.Release(loser.Quest.OperationId, stopClaim);
                }

                var loserResult = await f.Store.ReadLocalResultAsync(owner, loser.Quest.Id, true, deadline.Token);

                Assert.True(loserResult.IsSuccess, loserResult.Error.Message);

                Assert.NotNull(loserResult.Value.Receipt);

                await using var proof = connection.Connection.CreateCommand();

                proof.CommandText = "SELECT count(*) FROM guild_result_receipts WHERE QuestId=$quest AND NoDispatchProven=1 AND WorkStoppedProven=1";

                proof.Parameters.AddWithValue("$quest", loser.Quest.Id.ToString("D").ToUpperInvariant());

                Assert.Equal(1L, await proof.ExecuteScalarAsync(deadline.Token));
            }

            foreach (GuildQuestStored member in members)
            {
                var stopped = await f.Store.RefreshCancellationConvergenceAsync(owner, member.Quest.Id, deadline.Token);

                Assert.True(stopped.IsSuccess, stopped.Error.Message);

                Assert.True(stopped.Value);
            }

            CohortConvergenceDto converged = await WaitForConvergenceAsync(cohorts, owner, cohortId, value => value.State == GuildCohortState.Completed);

            Assert.Equal(GuildConvergenceDecision.Success, converged.Decision);

            Assert.Equal(2, converged.FrozenMemberCount);

            Assert.Equal(0, converged.PendingMemberCount);

            Assert.Equal(0, converged.PendingDescendantCount);

            Assert.Equal(first ? 3 : 6, transport.Posts);

            Assert.Equal(0, transport.MetadataGets);

            Assert.True(transport.Bodies.Any(body => body.Contains("parent informational message canary", StringComparison.Ordinal)),
                $"Portal message not seen: inputs={await f.ScalarAsync("SELECT count(*) FROM guild_portal_inputs")}, receipts={await f.ScalarAsync("SELECT count(*) FROM guild_portal_receipts")}, pending={await f.ScalarAsync("SELECT count(*) FROM guild_inbox WHERE DeliveryStateCode=1")}, withheld={await f.ScalarAsync("SELECT count(*) FROM guild_inbox WHERE DeliveryStateCode=3")}");

            Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_portal_inputs"));

            Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_portal_receipts"));

            Assert.Equal(first ? 3L : 6L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_effects WHERE StateCode=2 AND UnknownUsd=1 AND UnknownTokens=1"));

            Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM BudgetReservations"));
        }
        finally
        {
            await wizard.DisposeAsync();
        }
    }

    [Fact]
    public async Task Parent_portal_message_is_bound_to_exact_child_and_live_lineage()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        GuildCallerContext owner = await f.OperatorAsync();

        var root = await f.IssueAsync(owner);

        Assert.True(root.IsSuccess, root.Error.Message);

        GuildCallerContext parent = await RunCallerAsync(f, owner, root.Value);

        GuildQuestStored child = await IssueChildAsync(f, parent, root.Value);

        GuildQuestStored sibling = await IssueChildAsync(f, parent, root.Value);

        var foreign = await f.IssueAsync(owner, f.Request with { IdempotencyKey = Guid.NewGuid() });

        Assert.True(foreign.IsSuccess, foreign.Error.Message);

        GuildLocalInferenceExecution childExecution = await ToolExecutionAsync(f, owner, child);

        GuildLocalInferenceExecution siblingExecution = await ToolExecutionAsync(f, owner, sibling);

        GuildLocalInferenceExecution foreignExecution = await ToolExecutionAsync(f, owner, foreign.Value);

        GuildPortalStore publisher = new(f.Store, f.Authority, new EventDisclosurePolicy(f.Policy), TimeProvider.System);

        var published = await publisher.PublishAsync(parent, root.Value.Quest.Id,
            new(Guid.NewGuid(), new(GuildCircleAddressKind.Child, child.Quest.Id, null, null), GuildEnvelopeKind.Progress,
                "text/plain", "parent message boundary canary", null), default);

        Assert.True(published.IsSuccess, published.Error.Message);

        GuildPortalStore portal = new(f.Store, f.Authority, f.Policy, TimeProvider.System);

        var claim = await portal.ClaimAsync(childExecution.Driver, default);

        Assert.True(claim.IsSuccess, claim.Error.Message);

        var prompt = await portal.ReadPromptAsync(claim.Value, default);

        Assert.True(prompt.IsSuccess, prompt.Error.Message);

        Assert.Contains("Authority: Parent", prompt.Value, StringComparison.Ordinal);

        Assert.Contains("parent message boundary canary", prompt.Value, StringComparison.Ordinal);

        Assert.Contains("UNTRUSTED DATA", prompt.Value, StringComparison.Ordinal);

        Assert.True((await f.Store.GetAsync(childExecution.Driver.Caller!, root.Value.Quest.Id, default)).IsFailure);

        async Task InsertMismatchedInboxAsync(Guid recipient)
        {
            await using var connection = await f.OpenAsync();

            await using var command = connection.CreateCommand();

            command.CommandText = """
                INSERT INTO guild_inbox(Id,EnvelopeId,RecipientKey,RecipientQuestId,StreamId,SenderSequence,DeliveryStateCode,Revision,CreatedAtUtc)
                SELECT $new,EnvelopeId,$key,$quest,StreamId,SenderSequence,1,1,CreatedAtUtc FROM guild_inbox WHERE Id=$id
                """;

            command.Parameters.AddWithValue("$new", Guid.NewGuid().ToString("D").ToUpperInvariant());

            command.Parameters.AddWithValue("$quest", recipient.ToString("D").ToUpperInvariant());

            command.Parameters.AddWithValue("$key", "quest:" + recipient.ToString("D").ToUpperInvariant());

            command.Parameters.AddWithValue("$id", claim.Value.InboxId.ToString("D").ToUpperInvariant());

            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        await InsertMismatchedInboxAsync(sibling.Quest.Id);

        var siblingClaim = await portal.ClaimAsync(siblingExecution.Driver, default);

        Assert.True(siblingClaim.IsSuccess, siblingClaim.Error.Message);

        Assert.True((await portal.ReadPromptAsync(siblingClaim.Value, default)).IsFailure);

        await InsertMismatchedInboxAsync(foreign.Value.Quest.Id);

        var foreignClaim = await portal.ClaimAsync(foreignExecution.Driver, default);

        Assert.True(foreignClaim.IsSuccess, foreignClaim.Error.Message);

        Assert.True((await portal.ReadPromptAsync(foreignClaim.Value, default)).IsFailure);

        Assert.True((await portal.ReadPromptAsync(claim.Value with { Driver = siblingExecution.Driver }, default)).IsFailure);

        await RevokeDiscoveryRootAsync(f, owner, root.Value.Quest);

        Assert.True((await portal.ReadPromptAsync(claim.Value, default)).IsFailure);
    }

    private sealed class FinancialOffRuntimeCapture(IGuildLocalInferenceRuntime inner) : IGuildLocalInferenceRuntime
    {
        internal string? Diagnostic;

        public bool IsAvailable => inner.IsAvailable;

        public Task<Result<PromptTurnResult>> ExecuteAsync(GuildLocalInferenceExecution execution, GuildLocalExecutionInput input, CancellationToken ct) =>
            RecordAsync(() => inner.ExecuteAsync(execution, input, ct));

        public Task<Result<PromptTurnResult>> ExecuteTurnAsync(GuildLocalInferenceExecution execution, GuildLocalExecutionInput input,
            GuildLocalInferenceTurn turn, CancellationToken ct) => RecordAsync(() => inner.ExecuteTurnAsync(execution, input, turn, ct));

        private async Task<Result<PromptTurnResult>> RecordAsync(Func<Task<Result<PromptTurnResult>>> action)
        {
            try
            {
                var result = await action();

                if (result.IsFailure) Diagnostic = result.Error.Code + ": " + result.Error.Message;

                return result;
            }
            catch (Exception error)
            {
                Diagnostic = error.ToString();

                throw;
            }
        }
    }

    private sealed class FinancialOffMemberTransport : HttpMessageHandler, IHttpClientFactory
    {
        internal List<string> Bodies { get; } = [];

        internal int Posts;

        internal int MetadataGets;

        public HttpClient CreateClient(string name) => new(this, false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get)
            {
                Interlocked.Increment(ref MetadataGets);

                throw new InvalidOperationException("Unexpected metadata GET");
            }

            Assert.Equal(HttpMethod.Post, request.Method);

            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));

            int ordinal = Interlocked.Increment(ref Posts);

            string message = ordinal % 3 == 1
                ? """{"role":"assistant","content":"[{\"index\":1,\"description\":\"Contribute an answer\"}]"}"""
                : """{"role":"assistant","content":"actual cohort member answer"}""";

            string json = $$$"""{"id":"member-{{{ordinal}}}","object":"chat.completion","created":1700000000,"model":"accepted-model","choices":[{"index":0,"message":{{{message}}},"finish_reason":"stop"}]}""";

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FinancialOffTransport : HttpMessageHandler, IHttpClientFactory
    {
        internal List<string> Bodies { get; } = [];

        internal int MetadataGets;

        internal bool FullParent;

        internal string Usage = "absent";

        public HttpClient CreateClient(string name) => new(this, false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get)
            {
                Interlocked.Increment(ref MetadataGets);

                throw new InvalidOperationException("Unexpected metadata GET");
            }

            Assert.Equal(HttpMethod.Post, request.Method);

            string body = await request.Content!.ReadAsStringAsync(ct);

            int ordinal;

            lock (Bodies)
            {
                Bodies.Add(body);

                ordinal = Bodies.Count;
            }

            int toolOrdinal = FullParent ? 2 : 1;

            string message = ordinal == toolOrdinal
                ? """{"role":"assistant","content":null,"tool_calls":[{"id":"call-child","type":"function","function":{"name":"delegate_task","arguments":"{\"prompt\":\"child task\"}"}}]}"""
                : FullParent && ordinal == 1
                ? """{"role":"assistant","content":"[{\"index\":1,\"description\":\"Delegate a child task and summarize its answer\"}]"}"""
                : ordinal == toolOrdinal + 1
                ? """{"role":"assistant","content":"unique real child answer"}"""
                : ordinal <= (FullParent ? 5 : 3)
                ? """{"role":"assistant","content":"parent used the real child answer"}"""
                : throw new InvalidOperationException("Unexpected provider call");

            string usage = Usage switch
            {
                "partial" => ",\"usage\":{\"prompt_tokens\":7}",
                "zero" => ",\"usage\":{\"prompt_tokens\":0,\"completion_tokens\":0,\"total_tokens\":0}",
                _ => "",
            };

            string json = $$$"""{"id":"response-{{{ordinal}}}","object":"chat.completion","created":1700000000,"model":"accepted-model","choices":[{"index":0,"message":{{{message}}},"finish_reason":"{{{(ordinal == toolOrdinal ? "tool_calls" : "stop")}}}"}]{{{usage}}}}""";

            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
