using System.Text.Json;
using RetroDownfall.Arcanum.Api.Intelligence;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Tests.Guild;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class ToolExecutionPipelineTests
{
    [Theory]
    [InlineData("lookup")]
    [InlineData("retire_covenant")]
    [InlineData("apply_patch")]
    public async Task Guild_pipeline_refuses_unbound_native_and_special_lanes_before_effect(string name)
    {
        GuildCountingFunction native = new(name);

        ChatOptions options = new() { Tools = [native] };

        ToolExecutionPipeline.ProcessedToolCall result = await CreatePipeline().ProcessSingleToolCallAsync(
            new FunctionCallContent("guild-call", name, new Dictionary<string, object?>()),
            new PingRequest("parent"), options, null, null,
            new ToolExecutionPipeline.TurnContext { GuildTools = GuildToolBoundaryTests.CreateContext() }, true, default);

        Assert.True(result.Denied);

        Assert.Contains(ErrorCodes.Guild.ToolBoundaryUnavailable, result.ResultText);

        Assert.Equal(0, native.Calls);

        Assert.Equal(2, result.WardEvents.Count);
    }

    private sealed class GuildCountingFunction(string name) : AIFunction
    {
        public int Calls { get; private set; }

        public override string Name => name;

        public override JsonElement JsonSchema => JsonDocument.Parse("{}").RootElement;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            Calls++;

            return ValueTask.FromResult<object?>("effect");
        }
    }
}
