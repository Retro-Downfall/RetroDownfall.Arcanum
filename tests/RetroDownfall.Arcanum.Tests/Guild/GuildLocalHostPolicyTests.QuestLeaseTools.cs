using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    private static WorkspaceReadTransport PrependQuestLeaseCalls(Guid expectedQuest, params (string Name, string Arguments)[] calls)
    {
        (string Name, string Arguments)[] script =
        [
            ("acquire_workspace_lease", """{"scopes":[{"relativePath":"allowed/created.txt","kind":"file"}]}"""),
            ("renew_workspace_lease", ""), ("release_workspace_lease", ""), .. calls,
        ];

        WorkspaceReadTransport transport = new(script);

        GuildWorkspaceLeaseToolReceipt? previous = null;

        transport.BeforeResponse = round =>
        {
            if (round is < 2 or > 4) return Task.CompletedTask;

            using JsonDocument page = ReadWorkspaceToolResult(WorkspaceToolResults(transport.Bodies[^1])[^1]);

            GuildWorkspaceLeaseToolReceipt receipt = Assert.IsType<GuildWorkspaceLeaseToolReceipt>(
                page.RootElement.Deserialize(GuildWorkspaceLeaseToolJsonContext.Default.GuildWorkspaceLeaseToolReceipt));

            Assert.Equal(expectedQuest, receipt.QuestId);

            Assert.Equal(GuildWorkspaceLeaseOwnerKind.Quest, receipt.OwnerKind);

            Assert.Equal(round == 4 ? GuildWorkspaceLeaseState.Released : GuildWorkspaceLeaseState.Held, receipt.State);

            if (previous is not null)
            {
                Assert.Equal(previous.Id, receipt.Id);

                Assert.Equal(previous.Epoch, receipt.Epoch);

                Assert.True(receipt.Revision > previous.Revision);
            }

            if (round < 4)
            {
                string arguments = FormattableString.Invariant(
                    $$$"""{"leaseId":"{{{receipt.Id:D}}}","epoch":{{{receipt.Epoch}}},"expectedRevision":{{{receipt.Revision}}}}""");

                script[round - 1] = (script[round - 1].Name, arguments);
            }

            previous = receipt;

            return Task.CompletedTask;
        };

        return transport;
    }

    [Fact]
    public async Task Quest_lease_tools_manage_the_same_Quest_owned_epoch_and_refuse_a_sibling_path()
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture fixture = await Fixture.CreateAsync(localExecution: true);

        await using GuildLocalHostReadiness readiness = new(fixture.Settings, fixture.Connections, fixture.Policy,
            fixture.Authority, TimeProvider.System, Path.Combine(fixture.WorkspaceRoot, "host.lock"), fixture.Gate);

        fixture.Readiness.Inner = readiness;

        Assert.True((await readiness.EnsureReadyAsync(default)).IsSuccess);

        Guid workspace = await fixture.AddWorkspaceAsync();

        Directory.CreateDirectory(Path.Combine(fixture.WorkspaceRoot, "shelves"));

        Directory.CreateDirectory(Path.Combine(fixture.WorkspaceRoot, "shelves-archive"));

        string[] names = ["acquire_workspace_lease", "renew_workspace_lease", "release_workspace_lease"];

        GuildCallerContext owner = await fixture.OperatorAsync();

        GuildIssueRootRequest request = fixture.Request with
        {
            WorkspaceId = workspace,
            Grant = fixture.Request.Grant with
            {
                Workspaces = [new(workspace, "shelves", GuildWorkspaceAccess.ReadWrite)],
                Tools = [.. names.Select(name => new GuildToolGrant(GuildToolSourceKind.InternalMcp, "arcanum", name))],
            },
        };

        var issue = await fixture.IssueAsync(owner, request);

        Assert.True(issue.IsSuccess, issue.Error.Message);

        GuildLocalInferenceExecution execution = await ToolExecutionAsync(fixture, owner, issue.Value);

        ConclaveArchmage coordinator = new(null!, fixture.Settings, fixture.Store, new ChildQueue());

        async Task<object?> Invoke(string name, AIFunctionArguments arguments, long responseAllocation = 900,
            GuildLocalInferenceExecution? caller = null)
        {
            caller ??= execution;

            var input = await fixture.Store.ReadLocalInputAsync(caller.Driver, default);

            Assert.True(input.IsSuccess, input.Error.Message);

            using GuildLocalToolRegistry registry = new(caller, input.Value, coordinator, 4096 + responseAllocation);

            var context = fixture.Store.CreateToolContext(caller, registry, _ => true);

            GuildToolGrant tool = new(GuildToolSourceKind.InternalMcp, "arcanum", name);

            AIFunction? bound = await context.AdvertiseAsync(new InertChildTool(name), tool, default);

            Assert.NotNull(bound);

            using IDisposable frame = registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), tool);

            return await context.InvokeBoundAsync(bound, arguments, default);
        }

        static AIFunctionArguments Acquire(string path) => new()
        {
            ["scopes"] = JsonSerializer.SerializeToElement([new(path, GuildPathScopeKind.File)],
                GuildJsonContext.Default.ImmutableArrayGuildPathScope),
        };

        static AIFunctionArguments Change(GuildWorkspaceLeaseToolReceipt lease) => new()
        {
            ["leaseId"] = lease.Id.ToString("D"), ["epoch"] = lease.Epoch, ["expectedRevision"] = lease.Revision,
        };

        static GuildWorkspaceLeaseToolReceipt Lease(object? returned)
        {
            var result = Assert.IsType<GuildLocalChildToolResult>(returned);

            Assert.NotNull(result.Projection);

            return Assert.IsType<GuildWorkspaceLeaseToolReceipt>(result.Projection.Value.Deserialize(GuildWorkspaceLeaseToolJsonContext.Default.GuildWorkspaceLeaseToolReceipt));
        }

        string refusal = Assert.IsType<string>(await Invoke(names[0], Acquire("shelves-archive/catalog.cs")));

        Assert.StartsWith("Guild.", refusal, StringComparison.Ordinal);

        AIFunctionArguments acquire = Acquire("shelves/catalog.cs");

        acquire["idempotencyKey"] = Guid.NewGuid().ToString("D");

        string allocation = Assert.IsType<string>(await Invoke(names[0], acquire, responseAllocation: 1));

        Assert.StartsWith("Guild.PayloadTooLarge", allocation, StringComparison.Ordinal);

        Assert.Equal(0L, await fixture.ScalarAsync("SELECT count(*) FROM guild_workspace_leases"));

        GuildWorkspaceLeaseToolReceipt held = Lease(await Invoke(names[0], acquire));

        Assert.Equal(GuildWorkspaceLeaseOwnerKind.Quest, held.OwnerKind);

        Assert.Equal(issue.Value.Quest.Id, held.QuestId);

        Assert.Equal(workspace, held.WorkspaceId);

        Assert.Equal(GuildWorkspaceLeaseState.Held, held.State);

        GuildWorkspaceLeaseToolReceipt replay = Lease(await Invoke(names[0], acquire));

        Assert.Equal(held.Id, replay.Id);

        Assert.Equal(held.Receipt.Id, replay.Receipt.Id);

        Assert.True(replay.Receipt.Replayed);

        var other = await fixture.IssueAsync(owner, request with { IdempotencyKey = Guid.NewGuid() });

        Assert.True(other.IsSuccess, other.Error.Message);

        GuildLocalInferenceExecution otherRun = await ToolExecutionAsync(fixture, owner, other.Value);

        foreach (string change in names[1..])
        {
            string foreign = Assert.IsType<string>(await Invoke(change, Change(held), caller: otherRun));

            Assert.StartsWith("Guild.Forbidden", foreign, StringComparison.Ordinal);
        }

        GuildWorkspaceLeaseToolReceipt renewed = Lease(await Invoke(names[1], Change(held)));

        Assert.Equal(held.Id, renewed.Id);

        Assert.Equal(held.Epoch, renewed.Epoch);

        Assert.True(renewed.Revision > held.Revision);

        GuildWorkspaceLeaseToolReceipt released = Lease(await Invoke(names[2], Change(renewed)));

        Assert.Equal(held.Id, released.Id);

        Assert.Equal(GuildWorkspaceLeaseState.Released, released.State);
    }
}
