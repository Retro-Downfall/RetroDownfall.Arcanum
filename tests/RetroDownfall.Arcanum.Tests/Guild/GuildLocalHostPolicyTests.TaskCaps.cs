using System.Text.Json;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Roster_task_page_obeys_the_call_allocation_and_preserves_unseen_cards(bool oversized)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant tool = new(GuildToolSourceKind.InternalMcp, "arcanum", "list_agents");

        var parent = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with { Tools = [tool], Delegation = [] } });

        var execution = await ToolExecutionAsync(f, owner, parent.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        GuildStore cards = new(f.Connections, f.Gate, TimeProvider.System);

        for (int index = 0; index < (oversized ? 1 : 2); index++)
        {
            GuildAgentCard card = new(Guid.Empty, Guid.Empty, 0, "Card " + index, new string('x', oversized ? 62_000 : 30_000), "1",
                [], ["text/plain"], ["text/plain"], new(false, false, false), new(GuildAgentSourceKind.DeclaredGlobal, null, null, true, null));

            var added = await cards.UpsertCardAsync(new("cap:" + index, null, null, null, new(card, "", []), new string('A', 64)), default);

            Assert.True(added.IsSuccess, added.Error.Message);
        }

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, new ChildQueue());

        using GuildLocalToolRegistry registry = new(execution, input.Value, coordinator);

        var context = f.Store.CreateToolContext(execution, registry, _ => true);

        using IDisposable ambient = GuildToolInvocationAmbient.Begin(context);

        await using GuildMcpRoute route = await GuildMcpRoute.CreateAsync(false, outputCapBytes: 65_536);

        async Task<(bool Error, string Text)> Page(string? cursor)
        {
            using IDisposable frame = registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), tool);

            string arguments = cursor is null ? "{\"source\":\"declaredGlobal\",\"pageSize\":100}"
                : "{\"source\":\"declaredGlobal\",\"pageSize\":100,\"cursor\":\"" + cursor + "\"}";

            return await route.CallAsync("list_agents", arguments);
        }

        var first = await Page(null);

        using GuildLocalToolRegistry nativeRegistry = new(execution, input.Value, coordinator, outputAllocationBytes: 65_536);

        var nativeContext = f.Store.CreateToolContext(execution, nativeRegistry, _ => true);

        var nativeFunction = await nativeContext.AdvertiseAsync(new InertChildTool(tool.Name), tool, default);

        Assert.NotNull(nativeFunction);

        object? native;

        using (nativeRegistry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), tool))
            native = await nativeContext.InvokeBoundAsync(nativeFunction,
                new() { ["source"] = "declaredGlobal", ["pageSize"] = 100 }, default);

        if (oversized)
        {
            Assert.True(first.Error);

            Assert.Contains(ErrorCodes.Guild.PayloadTooLarge, first.Text);

            Assert.Contains(ErrorCodes.Guild.PayloadTooLarge, Assert.IsType<string>(native));

            return;
        }

        Assert.False(first.Error, first.Text);

        using JsonDocument firstPage = JsonDocument.Parse(first.Text);

        Assert.Single(firstPage.RootElement.GetProperty("items").EnumerateArray());

        var nativePage = Assert.IsType<GuildLocalChildToolResult>(native).Projection!.Value;

        Assert.Single(nativePage.GetProperty("items").EnumerateArray());

        Assert.Equal(firstPage.RootElement.GetProperty("items")[0].GetProperty("cardId").GetGuid(),
            nativePage.GetProperty("items")[0].GetProperty("cardId").GetGuid());

        string? next = firstPage.RootElement.GetProperty("nextCursor").GetString();

        Assert.NotNull(next);

        var second = await Page(next);

        Assert.False(second.Error, second.Text);

        using JsonDocument secondPage = JsonDocument.Parse(second.Text);

        Assert.Single(secondPage.RootElement.GetProperty("items").EnumerateArray());

        Assert.NotEqual(firstPage.RootElement.GetProperty("items")[0].GetProperty("cardId").GetGuid(),
            secondPage.RootElement.GetProperty("items")[0].GetProperty("cardId").GetGuid());

        Assert.Equal(JsonValueKind.Null, secondPage.RootElement.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task Quest_task_projection_over_call_allocation_returns_typed_too_large_instead_of_truncated_JSON()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant tool = new(GuildToolSourceKind.InternalMcp, "arcanum", "get_agent_task");

        var parent = await f.IssueAsync(owner, f.Request with { Name = new string('é', 9_000),
            Grant = f.Request.Grant with { Tools = [tool, new(GuildToolSourceKind.Adapter, "cap-adapter", new string('é', 2_000))], Delegation = [] } });

        Assert.True(parent.IsSuccess, parent.Error.Message);

        var execution = await ToolExecutionAsync(f, owner, parent.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        using GuildLocalToolRegistry registry = new(execution, input.Value, new ConclaveArchmage(null!, f.Settings, f.Store, new ChildQueue()));

        using IDisposable ambient = GuildToolInvocationAmbient.Begin(f.Store.CreateToolContext(execution, registry, _ => true));

        await using GuildMcpRoute route = await GuildMcpRoute.CreateAsync(false, outputCapBytes: 65_536);

        using IDisposable frame = registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), tool);

        var result = await route.CallAsync("get_agent_task", "{\"questId\":\"" + parent.Value.Quest.Id + "\"}");

        Assert.True(result.Error);

        Assert.Contains(ErrorCodes.Guild.PayloadTooLarge, result.Text);

        using GuildLocalToolRegistry nativeRegistry = new(execution, input.Value,
            new ConclaveArchmage(null!, f.Settings, f.Store, new ChildQueue()), outputAllocationBytes: 65_536);

        var nativeContext = f.Store.CreateToolContext(execution, nativeRegistry, _ => true);

        var nativeFunction = await nativeContext.AdvertiseAsync(new InertChildTool(tool.Name), tool, default);

        Assert.NotNull(nativeFunction);

        using IDisposable nativeFrame = nativeRegistry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), tool);

        object? native = await nativeContext.InvokeBoundAsync(nativeFunction,
            new() { ["questId"] = parent.Value.Quest.Id.ToString() }, default);

        Assert.Contains(ErrorCodes.Guild.PayloadTooLarge, Assert.IsType<string>(native));
    }
}
