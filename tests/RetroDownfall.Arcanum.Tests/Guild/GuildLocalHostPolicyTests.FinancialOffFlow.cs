using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
    [InlineData("cap")]
    [InlineData("qualified-cap")]
    [InlineData("qualified-breach")]
    [InlineData("fault")]
    public async Task Financial_off_real_child_inference_is_consumed_by_parent_provider_round(string usage)
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync(localExecution: true, productionOptions: usage is "qualified-cap" or "qualified-breach");

        f.Settings.CurrentValue.Features.Conclave = true;

        f.Settings.CurrentValue.Features.Apprentices = true;

        f.Settings.CurrentValue.Cost.Budget.Enabled = false;

        if (usage is "qualified-cap" or "qualified-breach")
        {
            f.Settings.CurrentValue.DefaultModel = "gpt-5.4-mini-2026-03-17";

            f.Settings.CurrentValue.Providers = [new() { Name = "accepted-provider", Type = AiProviderKind.OpenAICompatible,
                Endpoint = "https://api.openai.com/v1", ContextWindowLimit = 400000, Models = ["gpt-5.4-mini-2026-03-17"] }];
        }

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

        if (usage is "qualified-cap" or "qualified-breach")
        {
            settings.DefaultModel = "gpt-5.4-mini-2026-03-17";

            settings.Providers = [new() { Name = "accepted-provider", Type = AiProviderKind.OpenAICompatible,
                Endpoint = "https://api.openai.com/v1", ContextWindowLimit = 400000,
                GuildUsageReconciliation = GuildUsageReconciliationSource.None, Models = ["gpt-5.4-mini-2026-03-17"] }];
        }

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

            if (usage == "fault")
                await f.ExecuteAsync("CREATE TRIGGER fixture_financial_off_abort_receipt BEFORE INSERT ON guild_spend_receipts BEGIN SELECT RAISE(ABORT,'injected'); END");

            var invocation = runtime.Runtime.ExecuteTurnAsync(execution, input.Value,
                new("Delegate the task.", DisableAllTools: false), deadline.Token);

            if (usage == "fault")
            {
                var interrupted = await invocation;

                Assert.True(interrupted.IsFailure);

                Assert.Single(transport.Bodies);

                Assert.Equal(0, transport.MetadataGets);

                Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_effects WHERE StateCode=1"));

                Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_evidence"));

                Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM BillableOperations"));

                Assert.False(queue.Pending.Reader.TryRead(out _));

                var retry = await runtime.Runtime.ExecuteTurnAsync(execution, input.Value,
                    new("Delegate the task.", DisableAllTools: false), deadline.Token);

                Assert.True(retry.IsFailure);

                Assert.Single(transport.Bodies);

                return;
            }

            Task<GuildQuestDto> scheduled = queue.Pending.Reader.ReadAsync(deadline.Token).AsTask();

            Assert.Same(scheduled, await Task.WhenAny(scheduled, invocation));

            GuildQuestDto child = await scheduled;

            Assert.Equal(GuildBudgetMode.Unlimited, child.ResolvedGrant.Budget.Usd.Mode);

            Assert.Equal(usage is "cap" or "qualified-cap" or "qualified-breach" ? GuildBudgetMode.Finite : GuildBudgetMode.Unlimited, child.ResolvedGrant.Budget.Tokens.Mode);

            if (usage is "cap" or "qualified-cap" or "qualified-breach")
                Assert.Equal(usage == "cap" ? 100_000 : 600_000, child.ResolvedGrant.Budget.Tokens.Value);

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

            if (usage == "qualified-breach")
            {
                Assert.True(result.IsFailure);

                Assert.Equal(ErrorCodes.Guild.GrantExceeded, result.Error.Code);

                Assert.Equal(2, transport.Bodies.Count);

                Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_effects WHERE StateCode=2 AND TokenBound IS NOT NULL AND KnownTokens>TokenBound"));

                Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM BudgetReservations"));

                return;
            }

            Assert.True(result.IsSuccess, result.Error.Message);

            if (usage == "cap")
            {
                Assert.Equal(QuestState.Failed, completed.Value.State);

                var refused = await f.Store.ReadLocalResultAsync(owner, child.Id, true, deadline.Token);

                Assert.True(refused.IsSuccess, refused.Error.Message);

                Assert.NotNull(refused.Value.Receipt);

                Assert.Equal(ErrorCodes.Guild.BudgetCapabilityUnavailable, refused.Value.Receipt!.ReasonCode);

                Assert.Equal(2, transport.Bodies.Count);

                Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_effects WHERE StateCode=2"));

                Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM BudgetReservations"));

                return;
            }

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

            Assert.Equal(3L, await f.ScalarAsync($"SELECT count(*) FROM guild_budget_effects WHERE StateCode=2 AND UnknownUsd={(usage is "qualified-cap" or "qualified-breach" ? 0 : 1)} AND UnknownTokens={(usage is "zero" or "qualified-cap" ? 0 : 1)} AND KnownTokens={(usage == "partial" ? 7 : 0)}"));

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

    [Fact]
    public async Task Financial_off_provider_emits_cohort_tools_and_real_member_converges()
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

        var root = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with
            { Tools = [.. CohortTools()], Delegation = [GuildDelegationKind.Local, GuildDelegationKind.Cohort] } });

        Assert.True(root.IsSuccess, root.Error.Message);

        GuildLocalInferenceExecution execution = await ToolExecutionAsync(f, owner, root.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        Assert.True(input.IsSuccess, input.Error.Message);

        GuildCohortStore cohorts = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority,
            f.Store, new GuildBudgetLedger(), f.Policy, TimeProvider.System);

        ChildQueue queue = new();

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, queue, cohorts: cohorts);

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        ArcanumSettings settings = new()
        {
            Features = new() { Guild = true }, DefaultModel = "accepted-model",
            Providers = [new() { Name = "accepted-provider", Type = AiProviderKind.OpenAICompatible,
                Endpoint = "https://example.test/v1", ContextWindowLimit = 131072,
                GuildUsageReconciliation = GuildUsageReconciliationSource.None, Models = ["accepted-model"] }],
        };

        settings.Cost.Budget.Enabled = false;

        using FinancialOffCohortTransport transport = new();

        ChatClientFactory clients = new(transport, new TestOptionsMonitor<ArcanumSettings>(settings));

        WizardIntelligenceProviderTests wizard = new();

        try
        {
            var runtime = await wizard.CreateGuildRuntimeAsync(new BudgetReservationService(db, new TestOptionsMonitor<ArcanumSettings>(settings)), settings, clients, coordinator);

            using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(2));

            var parent = await runtime.Runtime.ExecuteTurnAsync(execution, input.Value,
                new("Create a cohort, admit one member, and close it.", DisableAllTools: false), deadline.Token);

            Assert.True(parent.IsSuccess, parent.Error.Message);

            Assert.Equal("cohort coordination accepted", parent.Value.Text);

            Assert.NotEqual(Guid.Empty, transport.CohortId);

            GuildQuestDto member = await queue.Pending.Reader.ReadAsync(deadline.Token);

            var recovery = await f.Policy.CreateRecoveryAsync(owner, member.Id, member.OperationId, member.SignetRevision, deadline.Token);

            Assert.True(recovery.IsSuccess, recovery.Error.Message);

            var handle = await f.Authority.RebindAsync(recovery.Value, deadline.Token);

            Assert.True(handle.IsSuccess, handle.Error.Message);

            var pending = await f.Store.ReadLocalPendingAsync(owner, member.Id, deadline.Token);

            Assert.True(pending.IsSuccess, pending.Error.Message);

            using ServiceProvider services = new ServiceCollection().BuildServiceProvider();

            using ApprenticeService engine = new(services.GetRequiredService<IServiceScopeFactory>(), f.Settings, new ChronicleHub(),
                NullLogger<ApprenticeService>.Instance, f.Gate);

            GuildLocalPort port = new(f.Store, f.Policy, f.Authority, new LongRunningOperationStore(db, f.Connections),
                engine, runtime.Runtime, queue, new(), TimeProvider.System);

            var completed = await port.DispatchAsync(handle.Value, pending.Value.Request!, deadline.Token);

            Assert.True(completed.IsSuccess, completed.Error.Message);

            Assert.Equal(QuestState.Completed, completed.Value.State);

            var result = await f.Store.ReadLocalResultAsync(owner, member.Id, true, deadline.Token);

            Assert.True(result.IsSuccess, result.Error.Message);

            Assert.Equal("cohort member answer", result.Value.Body);

            Assert.NotNull(result.Value.Receipt);

            var memberState = await f.Store.GetAsync(owner, member.Id, deadline.Token);

            Assert.True(memberState.IsSuccess, memberState.Error.Message);

            Assert.Equal(GuildAccountingState.Settled, memberState.Value.Quest.AccountingState);

            var draining = await WaitForConvergenceAsync(cohorts, owner, transport.CohortId, value => value.State == GuildCohortState.Draining);

            Assert.Equal(GuildConvergenceDecision.Success, draining.Decision);

            var stopped = await f.Store.RefreshCancellationConvergenceAsync(owner, member.Id, deadline.Token);

            Assert.True(stopped.IsSuccess, stopped.Error.Message);

            Assert.True(stopped.Value);

            var converged = await WaitForConvergenceAsync(cohorts, owner, transport.CohortId, value => value.State == GuildCohortState.Completed);

            Assert.Equal(GuildConvergenceDecision.Success, converged.Decision);

            Assert.True(converged.Spend.UnknownUsd);

            Assert.True(converged.Spend.UnknownTokens);

            Assert.Contains("UNTRUSTED DATA", transport.Bodies[1], StringComparison.Ordinal);

            Assert.Equal(7, transport.Bodies.Count);

            Assert.Equal(0, transport.MetadataGets);

            Assert.Equal(7L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_effects WHERE StateCode=2 AND UnknownUsd=1 AND UnknownTokens=1"));

            Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM BudgetReservations"));
        }
        finally
        {
            await wizard.DisposeAsync();
        }
    }

    private sealed class FinancialOffCohortTransport : HttpMessageHandler, IHttpClientFactory
    {
        internal List<string> Bodies { get; } = [];

        internal Guid CohortId;

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

            string body = await request.Content!.ReadAsStringAsync(ct);

            Bodies.Add(body);

            int ordinal = Bodies.Count;

            if (ordinal == 2)
            {
                using JsonDocument payload = JsonDocument.Parse(body);

                string toolResult = payload.RootElement.GetProperty("messages").EnumerateArray()
                    .Single(message => message.GetProperty("role").GetString() == "tool").GetProperty("content").GetString()!;

                var id = System.Text.RegularExpressions.Regex.Match(toolResult, "\\\"id\\\"\\s*:\\s*\\\"([0-9a-fA-F-]{36})\\\"");

                Assert.True(id.Success, "The actual create_cohort result must expose its id to the next provider round.");

                CohortId = Guid.Parse(id.Groups[1].Value);
            }

            string? tool = ordinal switch { 1 => "create_cohort", 2 => "admit_cohort_member", 3 => "close_cohort", _ => null };

            string arguments = ordinal switch
            {
                1 => "{\"name\":\"Panel\",\"policy\":\"all\",\"resultType\":\"text/plain\"}",
                2 => $"{{\"cohortId\":\"{CohortId:D}\",\"expectedRevision\":1,\"goal\":\"Contribute\"}}",
                3 => $"{{\"cohortId\":\"{CohortId:D}\",\"expectedRevision\":2}}",
                _ => "",
            };

            string message = tool is not null
                ? $$$"""{"role":"assistant","content":null,"tool_calls":[{"id":"call-{{{ordinal}}}","type":"function","function":{"name":"{{{tool}}}","arguments":"{{{JsonEncodedText.Encode(arguments)}}}"}}]}"""
                : ordinal == 5
                ? """{"role":"assistant","content":"[{\"index\":1,\"description\":\"Contribute an answer\"}]"}"""
                : ordinal is 6 or 7
                ? """{"role":"assistant","content":"cohort member answer"}"""
                : ordinal == 4
                ? """{"role":"assistant","content":"cohort coordination accepted"}"""
                : throw new InvalidOperationException("Unexpected provider call");

            string json = $$$"""{"id":"cohort-{{{ordinal}}}","object":"chat.completion","created":1700000000,"model":"accepted-model","choices":[{"index":0,"message":{{{message}}},"finish_reason":"{{{(tool is null ? "stop" : "tool_calls")}}}"}]}""";

            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Financial_off_first_cohort_waits_for_started_loser_to_stop_after_received_response()
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        f.Settings.CurrentValue.Features.Conclave = true;

        f.Settings.CurrentValue.Features.Apprentices = true;

        f.Settings.CurrentValue.Execution.MaxConcurrentApprentices = 2;

        f.Settings.CurrentValue.Cost.Budget.Enabled = false;

        await using GuildLocalHostReadiness readiness = new(f.Settings, f.Connections, f.Policy, f.Authority,
            TimeProvider.System, Path.Combine(f.WorkspaceRoot, "host.lock"), f.Gate);

        f.Readiness.Inner = readiness;

        Assert.True((await readiness.EnsureReadyAsync(default)).IsSuccess);

        var (owner, issuer, _, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.First, null, "text/plain", 2);

        var loserOpened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        var winnerOpened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(loserOpened.IsSuccess, loserOpened.Error.Message);

        Assert.True(winnerOpened.IsSuccess, winnerOpened.Error.Message);

        await using var loserConnection = loserOpened.Value;

        await using var winnerConnection = winnerOpened.Value;

        await using ArcanumDbContext loserDb = GrimoireFixture.CreateContext(loserConnection.Connection);

        await using ArcanumDbContext winnerDb = GrimoireFixture.CreateContext(winnerConnection.Connection);

        ArcanumSettings settings = new()
        {
            Features = new() { Guild = true }, DefaultModel = "accepted-model",
            Providers = [new() { Name = "accepted-provider", Type = AiProviderKind.OpenAICompatible,
                Endpoint = "https://example.test/v1", ContextWindowLimit = 131072,
                GuildUsageReconciliation = GuildUsageReconciliationSource.None, Models = ["accepted-model"] }],
        };

        settings.Cost.Budget.Enabled = false;

        using FinancialOffMemberTransport loserTransport = new();

        using FinancialOffMemberTransport winnerTransport = new();

        ChildQueue queue = new();

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, queue);

        WizardIntelligenceProviderTests loserWizard = new();

        WizardIntelligenceProviderTests winnerWizard = new();

        try
        {
            var loserRuntime = await loserWizard.CreateGuildRuntimeAsync(new BudgetReservationService(loserDb, new TestOptionsMonitor<ArcanumSettings>(settings)),
                settings, new ChatClientFactory(loserTransport, new TestOptionsMonitor<ArcanumSettings>(settings)), coordinator);

            var winnerRuntime = await winnerWizard.CreateGuildRuntimeAsync(new BudgetReservationService(winnerDb, new TestOptionsMonitor<ArcanumSettings>(settings)),
                settings, new ChatClientFactory(winnerTransport, new TestOptionsMonitor<ArcanumSettings>(settings)), coordinator);

            FinancialOffStoppedRuntime pausedLoser = new(loserRuntime.Runtime);

            using ServiceProvider services = new ServiceCollection().BuildServiceProvider();

            using ApprenticeService engine = new(services.GetRequiredService<IServiceScopeFactory>(), f.Settings, new ChronicleHub(),
                NullLogger<ApprenticeService>.Instance, f.Gate);

            ManualTimerTimeProvider loserClock = new();

            GuildLocalPort loserPort = new(f.Store, f.Policy, f.Authority, new LongRunningOperationStore(loserDb, f.Connections),
                engine, pausedLoser, queue, new(), loserClock);

            GuildLocalPort winnerPort = new(f.Store, f.Policy, f.Authority, new LongRunningOperationStore(winnerDb, f.Connections),
                engine, winnerRuntime.Runtime, queue, new(), TimeProvider.System);

            using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(2));

            async Task<(GuildExecutionHandle Handle, GuildDispatchRequest Request)> PrepareAsync(GuildQuestStored member)
            {
                var recovery = await f.Policy.CreateRecoveryAsync(owner, member.Quest.Id, member.Quest.OperationId, member.Quest.SignetRevision, deadline.Token);

                Assert.True(recovery.IsSuccess, recovery.Error.Message);

                var handle = await f.Authority.RebindAsync(recovery.Value, deadline.Token);

                Assert.True(handle.IsSuccess, handle.Error.Message);

                var pending = await f.Store.ReadLocalPendingAsync(owner, member.Quest.Id, deadline.Token);

                Assert.True(pending.IsSuccess, pending.Error.Message);

                return (handle.Value, pending.Value.Request!);
            }

            var loser = await PrepareAsync(members[1]);

            var winner = await PrepareAsync(members[0]);

            Task<RetroDownfall.Arcanum.Core.Primitives.Result<GuildDispatchObservation>> loserTask = loserPort.DispatchAsync(loser.Handle, loser.Request, deadline.Token);

            try
            {
                await pausedLoser.Entered.Task.WaitAsync(deadline.Token);

                await loserClock.FirstTimerCreated.WaitAsync(deadline.Token);

                Assert.Equal(1, loserTransport.Posts);

                var winnerResult = await winnerPort.DispatchAsync(winner.Handle, winner.Request, deadline.Token);

                Assert.True(winnerResult.IsSuccess, winnerResult.Error.Message);

                Assert.Equal(QuestState.Completed, winnerResult.Value.State);

                var current = await cohorts.GetAsync(issuer, cohort.Id, deadline.Token);

                Assert.True(current.IsSuccess, current.Error.Message);

                Assert.True((await cohorts.CloseAsync(issuer, cohort.Id, new(Guid.NewGuid(), current.Value.Revision), deadline.Token)).IsSuccess);

                var draining = await WaitForConvergenceAsync(cohorts, owner, cohort.Id, value => value.Decision == GuildConvergenceDecision.Success);

                Assert.NotEqual(GuildCohortState.Completed, draining.State);

                Assert.False(loserTask.IsCompleted);

                loserClock.Advance(TimeSpan.FromSeconds(11));

                await pausedLoser.CancelObserved.Task.WaitAsync(deadline.Token);

                Assert.False(loserTask.IsCompleted);

                pausedLoser.Release.TrySetResult();

                var stoppedLoser = await loserTask.WaitAsync(deadline.Token);

                Assert.True(stoppedLoser.IsSuccess, stoppedLoser.Error.Message);

                Assert.Equal(QuestState.Canceled, stoppedLoser.Value.State);

                var loserResult = await f.Store.ReadLocalResultAsync(owner, members[1].Quest.Id, true, deadline.Token);

                Assert.True(loserResult.IsSuccess, loserResult.Error.Message);

                Assert.NotNull(loserResult.Value.Receipt);

                await using var proof = loserConnection.Connection.CreateCommand();

                proof.CommandText = "SELECT count(*) FROM guild_result_receipts WHERE QuestId=$quest AND NoDispatchProven=0 AND WorkStoppedProven=1";

                proof.Parameters.AddWithValue("$quest", members[1].Quest.Id.ToString("D").ToUpperInvariant());

                Assert.Equal(1L, await proof.ExecuteScalarAsync(deadline.Token));

                foreach (GuildQuestStored member in members)
                {
                    var stopped = await f.Store.RefreshCancellationConvergenceAsync(owner, member.Quest.Id, deadline.Token);

                    Assert.True(stopped.IsSuccess, stopped.Error.Message);

                    Assert.True(stopped.Value);
                }

                var converged = await WaitForConvergenceAsync(cohorts, owner, cohort.Id, value => value.State == GuildCohortState.Completed);

                Assert.Equal(GuildConvergenceDecision.Success, converged.Decision);

                Assert.Equal(1, loserTransport.Posts);

                Assert.Equal(3, winnerTransport.Posts);

                Assert.Equal(0, loserTransport.MetadataGets + winnerTransport.MetadataGets);

                Assert.Equal(4L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_effects WHERE StateCode=2 AND UnknownUsd=1 AND UnknownTokens=1"));

                Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM BudgetReservations"));
            }
            finally
            {
                pausedLoser.Release.TrySetResult();
            }
        }
        finally
        {
            await winnerWizard.DisposeAsync();

            await loserWizard.DisposeAsync();
        }
    }

    private sealed class FinancialOffStoppedRuntime(IGuildLocalInferenceRuntime inner) : IGuildLocalInferenceRuntime
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource CancelObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsAvailable => inner.IsAvailable;

        public Task<Result<PromptTurnResult>> ExecuteAsync(GuildLocalInferenceExecution execution, GuildLocalExecutionInput input, CancellationToken ct) =>
            inner.ExecuteAsync(execution, input, ct);

        public async Task<Result<PromptTurnResult>> ExecuteTurnAsync(GuildLocalInferenceExecution execution, GuildLocalExecutionInput input,
            GuildLocalInferenceTurn turn, CancellationToken ct)
        {
            var result = await inner.ExecuteTurnAsync(execution, input, turn, ct);

            using CancellationTokenRegistration registration = ct.Register(() => CancelObserved.TrySetResult());

            Entered.TrySetResult();

            await Release.Task;

            return result;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Financial_daily_gate_changes_preserve_physical_obligations(bool enableBeforeDispatch)
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync(productionOptions: true);

        f.Settings.CurrentValue.Features.Conclave = true;

        f.Settings.CurrentValue.Features.Apprentices = true;

        f.Settings.CurrentValue.DefaultModel = "gpt-5.4-mini-2026-03-17";

        f.Settings.CurrentValue.Providers = [new() { Name = "accepted-provider", Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://api.openai.com/v1", ContextWindowLimit = 400000, Models = ["gpt-5.4-mini-2026-03-17"] }];

        await using GuildLocalHostReadiness readiness = new(f.Settings, f.Connections, f.Policy, f.Authority,
            TimeProvider.System, Path.Combine(f.WorkspaceRoot, "host.lock"), f.Gate);

        f.Readiness.Inner = readiness;

        Assert.True((await readiness.EnsureReadyAsync(default)).IsSuccess);

        GuildCallerContext owner = await f.OperatorAsync();

        var root = await f.IssueAsync(owner);

        Assert.True(root.IsSuccess, root.Error.Message);

        GuildLocalInferenceExecution execution = await ToolExecutionAsync(f, owner, root.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        Assert.True(input.IsSuccess, input.Error.Message);

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        ArcanumSettings settings = new()
        {
            Features = new() { Guild = true }, DefaultModel = "gpt-5.4-mini-2026-03-17",
            Cost = new() { Budget = new() { Enabled = true, DailyLimitUsd = 10m } },
            Providers = [new() { Name = "accepted-provider", Type = AiProviderKind.OpenAICompatible,
                Endpoint = "https://api.openai.com/v1", ContextWindowLimit = 400000,
                GuildUsageReconciliation = GuildUsageReconciliationSource.None, Models = ["gpt-5.4-mini-2026-03-17"] }],
        };

        settings.Cost.Budget.Enabled = !enableBeforeDispatch;

        FinancialOffGateFlipOptions? flip = enableBeforeDispatch ? new(settings) : null;

        BudgetReservationService daily = new(db, flip is null ? new TestOptionsMonitor<ArcanumSettings>(settings) : flip);

        GuildQuestStore paidStore = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority, f.Policy,
            new GuildBudgetLedger(daily), TimeProvider.System, new GuildLocalExecutionAdmission(f.Settings), f.Readiness);

        execution = new(paidStore, execution.Driver, execution.Dispatch);

        using FinancialOffDailyTransport transport = new(async () =>
        {
            Assert.False(enableBeforeDispatch, "The dispatch-time gate must stop before the physical send.");

            Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM BudgetReservations"));

            settings.Cost.Budget.Enabled = false;
        });

        ChatClientFactory clients = new(transport, new TestOptionsMonitor<ArcanumSettings>(settings));

        WizardIntelligenceProviderTests wizard = new();

        try
        {
            var runtime = await wizard.CreateGuildRuntimeAsync(daily, settings, clients);

            using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(2));

            var first = await runtime.Runtime.ExecuteTurnAsync(execution, input.Value, new("Answer the question.", DisableAllTools: true), deadline.Token);

            if (enableBeforeDispatch)
            {
                Assert.True(first.IsFailure);

                Assert.Equal(ErrorCodes.Guild.ReconciliationRequired, first.Error.Code);

                Assert.Equal(2, flip!.Reads);

                Assert.True(settings.Cost.Budget.Enabled);

                Assert.Equal(0, transport.Posts);

                Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM BudgetReservations"));

                Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_effects"));

                return;
            }

            Assert.True(first.IsSuccess, first.Error.Message);

            Assert.Equal("received without usage", first.Value.Text);

            Assert.False(settings.Cost.Budget.Enabled);

            Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM BudgetReservations"));

            Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_effects WHERE StateCode=2 AND UnknownUsd=1 AND UnknownTokens=1 AND ReservationId IS NOT NULL"));

            await using (var transaction = connection.Connection.BeginTransaction())
            {
                var snapshot = await new GuildBudgetLedger(daily).ReadWithinAsync(connection.Connection, transaction, root.Value.AllocationAccountId, deadline.Token);

                Assert.True(snapshot.IsSuccess, snapshot.Error.Message);

                Assert.True(snapshot.Value.UsdBlocked);

                Assert.True(snapshot.Value.UnknownUsd);
            }

            var reopened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, deadline.Token);

            Assert.True(reopened.IsSuccess, reopened.Error.Message);

            await using var restartedConnection = reopened.Value;

            await using ArcanumDbContext restartedDb = GrimoireFixture.CreateContext(restartedConnection.Connection);

            BudgetReservationService restartedDaily = new(restartedDb, new TestOptionsMonitor<ArcanumSettings>(settings));

            GuildLocalHostPolicy restartedPolicy = f.NewPolicy();

            using SignetAuthority restartedAuthority = new(new(f.Connections), f.Gate, restartedPolicy, TimeProvider.System);

            GuildQuestStore restartedStore = new(new(f.Connections, f.Gate, TimeProvider.System), restartedAuthority, restartedPolicy,
                new GuildBudgetLedger(restartedDaily), TimeProvider.System, new GuildLocalExecutionAdmission(f.Settings), f.Readiness);

            var restartedOwner = await restartedPolicy.CreateNativeOperatorAsync(Guid.NewGuid(), deadline.Token);

            Assert.True(restartedOwner.IsSuccess, restartedOwner.Error.Message);

            var spend = await ((IGuildTreeSpendLedger)restartedStore).ReadAsync(restartedOwner.Value, root.Value.Quest.Id, deadline.Token);

            Assert.True(spend.IsSuccess, spend.Error.Message);

            Assert.True(spend.Value.UnknownUsd);

            await using (var transaction = restartedConnection.Connection.BeginTransaction())
            {
                var restarted = await new GuildBudgetLedger(restartedDaily).ReadWithinAsync(restartedConnection.Connection, transaction, root.Value.AllocationAccountId, deadline.Token);

                Assert.True(restarted.IsSuccess, restarted.Error.Message);

                Assert.True(restarted.Value.UsdBlocked);

                Assert.True(restarted.Value.UnknownUsd);
            }

            Assert.Equal(1, transport.Posts);

            Assert.Equal(0, transport.MetadataGets);
        }
        finally
        {
            await wizard.DisposeAsync();
        }
    }

    private sealed class FinancialOffGateFlipOptions(ArcanumSettings settings) : IOptionsMonitor<ArcanumSettings>
    {
        internal int Reads;

        public ArcanumSettings CurrentValue
        {
            get
            {
                // The daily service observes disabled during reservation preparation, then enabled at exposure.
                if (Interlocked.Increment(ref Reads) == 2) settings.Cost.Budget.Enabled = true;

                return settings;
            }
        }

        public ArcanumSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ArcanumSettings, string?> listener) => null;
    }

    private sealed class FinancialOffDailyTransport(Func<Task> afterHandoff) : HttpMessageHandler, IHttpClientFactory
    {
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

            Interlocked.Increment(ref Posts);

            await afterHandoff();

            const string json = """{"id":"daily-response","object":"chat.completion","created":1700000000,"model":"gpt-5.4-mini-2026-03-17","choices":[{"index":0,"message":{"role":"assistant","content":"received without usage"},"finish_reason":"stop"}]}""";

            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
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

            string message = ordinal == toolOrdinal && Usage is "qualified-cap" or "qualified-breach"
                ? """{"role":"assistant","content":null,"tool_calls":[{"id":"call-child","type":"function","function":{"name":"delegate_task","arguments":"{\"prompt\":\"child task\",\"max_tokens\":600000}"}}]}"""
                : ordinal == toolOrdinal && Usage == "cap"
                ? """{"role":"assistant","content":null,"tool_calls":[{"id":"call-child","type":"function","function":{"name":"delegate_task","arguments":"{\"prompt\":\"child task\",\"max_tokens\":100000}"}}]}"""
                : ordinal == toolOrdinal
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
                "qualified-breach" when ordinal == 2 => ",\"usage\":{\"prompt_tokens\":500000,\"completion_tokens\":1,\"total_tokens\":500001}",
                "zero" or "cap" or "qualified-cap" or "qualified-breach" => ",\"usage\":{\"prompt_tokens\":0,\"completion_tokens\":0,\"total_tokens\":0}",
                _ => "",
            };

            string json = $$$"""{"id":"response-{{{ordinal}}}","object":"chat.completion","created":1700000000,"model":"accepted-model","choices":[{"index":0,"message":{{{message}}},"finish_reason":"{{{(ordinal == toolOrdinal ? "tool_calls" : "stop")}}}"}]{{{usage}}}}""";

            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
