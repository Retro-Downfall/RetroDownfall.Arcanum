using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Intelligence;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provider_mutates_only_its_Quest_workspace_and_commits_the_patch_receipt(bool peerOwned)
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture fixture = await Fixture.CreateAsync(localExecution: true, inboundPeers: peerOwned);

        Guid workspace = await fixture.AddWorkspaceAsync();

        string allowed = Directory.CreateDirectory(Path.Combine(fixture.WorkspaceRoot, "allowed")).FullName;

        string denied = Directory.CreateDirectory(Path.Combine(fixture.WorkspaceRoot, "denied")).FullName;

        string foreign = Path.Combine(denied, "unowned.txt");

        await File.WriteAllTextAsync(foreign, "outside [needle]\n");

        await File.WriteAllTextAsync(Path.Combine(allowed, "second.txt"), "second before\n");

        string[] names = ["acquire_workspace_lease", "renew_workspace_lease", "release_workspace_lease", "read_file", "write_file", "edit_file", "apply_patch"];

        ArcanumSettings settings = fixture.Settings.CurrentValue;

        settings.DefaultModel = "accepted-model";

        settings.Providers = [new() { Name = "accepted-provider", Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://example.test/v1", ContextWindowLimit = 131072,
            GuildUsageReconciliation = GuildUsageReconciliationSource.None, Models = ["accepted-model"] }];

        settings.Cost.Budget.Enabled = false;

        await using GuildLocalHostReadiness readiness = new(fixture.Settings, fixture.Connections, fixture.Policy, fixture.Authority,
            TimeProvider.System, Path.Combine(fixture.WorkspaceRoot, "host.lock"), fixture.Gate);

        fixture.Readiness.Inner = readiness;

        Assert.True((await readiness.EnsureReadyAsync(default)).IsSuccess);

        ConclaveArchmage coordinator = new(null!, fixture.Settings, fixture.Store, new InboundScheduler());

        GuildCallerContext scheduler = await fixture.OperatorAsync();

        GuildQuestStored quest;

        if (peerOwned)
        {
            ReloadInbound(fixture, item =>
            {
                item.CampaignId = workspace;

                item.WorkspaceId = workspace;

                item.Grant!.Workspaces = [new() { WorkspaceId = workspace, RelativeRoot = "allowed", Access = GuildWorkspaceAccess.ReadWrite }];

                item.Grant.Tools = [.. names.Select(name => new GuildToolGrantSettings
                { SourceKind = GuildToolSourceKind.InternalMcp, SourceRegistrationId = "arcanum", Name = name })];
            });

            GuildInboundPeer peer = Assert.IsType<GuildInboundPeer>(fixture.InboundPeers!.Resolve(Fixture.PeerCredential));

            GuildInboundHost host = new(fixture.Policy, fixture.Store, coordinator);

            var issued = await host.IssueAsync(peer, "workspace-mutation-message", "workspace-mutation-task", null,
                "Edit the permitted workspace", default);

            Assert.True(issued.IsSuccess, issued.Error.Message);

            var loaded = await fixture.Store.GetAsync(scheduler, issued.Value.Quest.Id, default);

            Assert.True(loaded.IsSuccess, loaded.Error.Message);

            quest = loaded.Value;
        }
        else
        {
            var issued = await fixture.IssueAsync(scheduler, fixture.Request with
            {
                WorkspaceId = workspace,
                Grant = fixture.Request.Grant with
                {
                    Workspaces = [new(workspace, "allowed", GuildWorkspaceAccess.ReadWrite)],
                    Tools = [.. names.Select(name => new GuildToolGrant(GuildToolSourceKind.InternalMcp, "arcanum", name))],
                },
            });

            Assert.True(issued.IsSuccess, issued.Error.Message);

            quest = issued.Value;
        }

        GuildLocalInferenceExecution execution = await ToolExecutionAsync(fixture, scheduler, quest);

        var input = await fixture.Store.ReadLocalInputAsync(execution.Driver, default);

        Assert.True(input.IsSuccess, input.Error.Message);

        var opened = await fixture.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        GrimoireRepository repository = new(db, new NoOpSessionAttachmentStore(), NullLogger<GrimoireRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(settings), covenantKernel: null, availabilityRepublisher: null,
            fixture.Connections, FixtureLabeledArtifactGuard.For(db), memoryDelegation: null);

        using WorkspaceReadTransport transport = PrependQuestLeaseCalls(quest.Quest.Id,
            ("write_file", """{"relativePath":"allowed/created.txt","content":"literal [needle]\n"}"""),
            ("read_file", """{"relativePath":"allowed/created.txt","startLine":1,"endLine":1}"""),
            ("read_file", """{"relativePath":"allowed/second.txt","startLine":1,"endLine":1}"""),
            ("edit_file", """{"relativePath":"allowed/created.txt","exactSearchText":"[needle]","replacementText":"☃"}"""),
            ("apply_patch", """{"patch":"--- a/allowed/created.txt\n+++ b/allowed/created.txt\n@@ -1 +1 @@\n-literal ☃\n+patched\n--- a/allowed/second.txt\n+++ b/allowed/second.txt\n@@ -1 +1 @@\n-second before\n+second after\n"}"""),
            ("edit_file", """{"relativePath":"denied/unowned.txt","exactSearchText":"[needle]","replacementText":"foreign change"}"""));

        ChatClientFactory clients = new(transport, fixture.Settings);

        WizardIntelligenceProviderTests wizard = new();

        try
        {
            BudgetReservationService daily = new(db, fixture.Settings);

            var runtime = await wizard.CreateGuildRuntimeAsync(daily, settings, clients, coordinator, repository);

            var result = await runtime.Runtime.ExecuteTurnAsync(execution, input.Value,
                new("Use the permitted workspace mutation tools.", DisableAllTools: false), default);

            Assert.NotEmpty(transport.Bodies);

            using JsonDocument first = JsonDocument.Parse(transport.Bodies[0]);

            Assert.True(first.RootElement.TryGetProperty("tools", out JsonElement tools), "The authorized provider round did not advertise workspace mutations.");

            Assert.Equal(names.Order(StringComparer.Ordinal), tools.EnumerateArray()
                .Select(tool => tool.GetProperty("function").GetProperty("name").GetString()).Order(StringComparer.Ordinal));

            Assert.True(result.IsSuccess, $"Provider requests: {transport.Bodies.Count}. {result.Error.Code}: {result.Error.Message}");

            Assert.Equal("patched\n", await File.ReadAllTextAsync(Path.Combine(allowed, "created.txt")));

            Assert.Equal("second after\n", await File.ReadAllTextAsync(Path.Combine(allowed, "second.txt")));

            Assert.Equal("outside [needle]\n", await File.ReadAllTextAsync(foreign));

            Assert.Contains("Guild.Forbidden", WorkspaceToolResults(transport.Bodies[^1])[^1]);

            string[] afterEdit = WorkspaceToolResults(transport.Bodies[7]);

            Assert.Contains("Read the current source again", afterEdit[4]);

            using JsonDocument prior = ReadWorkspaceToolResult(WorkspaceToolResults(transport.Bodies[6])[4]);

            Assert.Equal("literal [needle]", prior.RootElement.GetProperty("content").GetString());

            using JsonDocument sibling = ReadWorkspaceToolResult(afterEdit[5]);

            Assert.Equal("second before", sibling.RootElement.GetProperty("content").GetString());

            string session = quest.Quest.SessionId!.Value.ToString("D").ToUpperInvariant();

            Assert.Equal(1L, await fixture.ScalarAsync($$"""
                SELECT count(*) FROM Entries call JOIN Entries result
                    ON result.SessionId=call.SessionId AND result.Sequence=call.Sequence+1
                JOIN Entries assistant ON assistant.SessionId=call.SessionId
                    AND lower(assistant.Id)=lower(json_extract(call.ToolArguments,'$.assistantEntryId'))
                WHERE call.SessionId='{{session}}' AND call.ToolName='apply_patch' AND call.ToolCallId IS NOT NULL
                    AND call.ToolArguments LIKE '{"sha256":"%'
                    AND result.Content LIKE '[ToolResult: %'
                    AND assistant.Content='Guild workspace patch processed.'
                """));

            Assert.Equal(0, transport.MetadataRequests);

            Assert.Equal(0L, await fixture.ScalarAsync("SELECT count(*) FROM BudgetReservations"));

            if (!peerOwned)
                await ExercisePatchAllocationAsync(fixture, coordinator, Assert.IsType<WizardIntelligenceProvider>(runtime.Runtime),
                    daily, execution, input.Value, Path.Combine(allowed, "created.txt"));
        }
        finally { await wizard.DisposeAsync(); }
    }

    private static async Task ExercisePatchAllocationAsync(Fixture fixture, ConclaveArchmage coordinator,
        WizardIntelligenceProvider runtime, BudgetReservationService daily, GuildLocalInferenceExecution execution,
        GuildLocalExecutionInput input, string path)
    {
        using GuildProviderCallBinding binding = new(execution, input, new PingRequest("Exercise patch receipt allocation."), daily, new());

        GuildToolGrant patch = new(GuildToolSourceKind.InternalMcp, "arcanum", "apply_patch");

        const string diff = """
            --- a/allowed/created.txt
            +++ b/allowed/created.txt
            @@ -1 +1 @@
            -patched
            +compact-receipt
            """;

        foreach (int allocation in new[] { 8, 128 })
        {
            using GuildLocalToolRegistry registry = new(execution, input, coordinator, 4096 + allocation,
                new WizardIntelligenceProvider.WorkspaceMutationRuntime(runtime, binding));

            var context = fixture.Store.CreateToolContext(execution, registry, GuildTestToolEligibility.Enabled);

            var tool = await context.AdvertiseAsync(new InertChildTool(patch.Name), patch, default);

            Assert.NotNull(tool);

            using IDisposable invocation = registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), patch);

            object? result = await context.InvokeBoundAsync(tool, new() { ["patch"] = diff }, default);

            if (allocation == 8)
            {
                Assert.StartsWith(ErrorCodes.Guild.PayloadTooLarge, Assert.IsType<string>(result), StringComparison.Ordinal);

                Assert.Equal("patched\n", await File.ReadAllTextAsync(path));

                continue;
            }

            Assert.True(result is GuildLocalChildToolResult,
                result is string refusal ? $"Compact patch returned {refusal}." : "Compact patch did not return its typed result.");

            GuildLocalChildToolResult receipt = (GuildLocalChildToolResult)result;

            Assert.Equal("allowed/created.txt", Assert.Single(receipt.ChangedWorkspacePaths));

            Assert.True(System.Text.Encoding.UTF8.GetByteCount(receipt.Projection!.Value.GetRawText()) <= allocation);

            Assert.Equal("compact-receipt\n", await File.ReadAllTextAsync(path));
        }

        await fixture.ExecuteAsync("""
            CREATE TRIGGER fail_guild_patch_receipt BEFORE INSERT ON Entries
            WHEN NEW.ToolName='apply_patch'
            BEGIN SELECT RAISE(ABORT,'fixture receipt write failed'); END;
            """);

        using GuildLocalToolRegistry recoveryRegistry = new(execution, input, coordinator,
            workspaceMutations: new WizardIntelligenceProvider.WorkspaceMutationRuntime(runtime, binding));

        var recoveryContext = fixture.Store.CreateToolContext(execution, recoveryRegistry, GuildTestToolEligibility.Enabled);

        var recoveryTool = await recoveryContext.AdvertiseAsync(new InertChildTool(patch.Name), patch, default);

        Assert.NotNull(recoveryTool);

        using IDisposable recoveryCall = recoveryRegistry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), patch);

        object? rejected = await recoveryContext.InvokeBoundAsync(recoveryTool, new()
        {
            ["patch"] = diff.Replace("-patched", "-compact-receipt", StringComparison.Ordinal)
                .Replace("+compact-receipt", "+must-roll-back", StringComparison.Ordinal),
        }, default);

        GuildLocalChildToolResult failure = Assert.IsType<GuildLocalChildToolResult>(rejected);

        Assert.Equal("receipt_failed", failure.Projection!.Value.GetProperty("code").GetString());

        Assert.Equal("compact-receipt\n", await File.ReadAllTextAsync(path));

        Assert.Equal(2L, await fixture.ScalarAsync("SELECT count(*) FROM Entries WHERE ToolName='apply_patch'"));
    }
}
