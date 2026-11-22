using System.Text.Json;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Unissued_tool_frame_does_not_reach_the_Guild_dispatcher()
    {
        await using Fixture fixture = await Fixture.CreateAsync(localExecution: true);

        GuildCallerContext owner = await fixture.OperatorAsync();

        GuildToolGrant tool = new(GuildToolSourceKind.InternalMcp, "arcanum", "get_agent_task");

        var issued = await fixture.IssueAsync(owner, fixture.Request with { Grant = fixture.Request.Grant with { Tools = [tool] } });

        Assert.True(issued.IsSuccess, issued.Error.Message);

        GuildLocalInferenceExecution execution = await ToolExecutionAsync(fixture, owner, issued.Value);

        var input = await fixture.Store.ReadLocalInputAsync(execution.Driver, default);

        Assert.True(input.IsSuccess, input.Error.Message);

        CountingFrameDispatcher dispatcher = new();

        using GuildLocalToolRegistry registry = new(execution, input.Value, dispatcher);

        var context = fixture.Store.CreateToolContext(execution, registry, GuildTestToolEligibility.Enabled);

        Assert.NotNull(await context.AdvertiseAsync(new InertChildTool(tool.Name), tool, default));

        // This carries the correct registry and tool fields, but no host invocation issued it.
        GuildLocalToolRegistry.Invocation unissued = new(registry, Guid.NewGuid(), Guid.NewGuid(), tool);

        using JsonDocument empty = JsonDocument.Parse("{}");

        object? output = await context.DispatchMcpAsync(unissued, tool.Name, empty.RootElement, default);

        Assert.Equal(0, dispatcher.Calls);

        Assert.False(unissued.IsActive);

        Assert.StartsWith(ErrorCodes.Guild.ToolBoundaryUnavailable, Assert.IsType<string>(output), StringComparison.Ordinal);
    }

    private sealed class CountingFrameDispatcher : IGuildLocalChildToolDispatcher
    {
        internal int Calls { get; private set; }

        public Task<Result<GuildLocalChildToolResult>> DispatchAsync(GuildLocalChildToolCall call, CancellationToken cancellationToken)
        {
            Calls++;

            return Task.FromResult(Result<GuildLocalChildToolResult>.Failure(GuildLocalToolRegistry.Unavailable));
        }
    }
}
