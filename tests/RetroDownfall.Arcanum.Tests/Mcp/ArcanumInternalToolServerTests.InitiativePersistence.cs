using System.Reflection;
using System.Text.Json;

using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Tests.Hosting;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed partial class ArcanumInternalToolServerTests
{
    [Fact]
    public async Task AdjustInitiativeHandlerOwnsThePacerWrite()
    {
        await using UnseenServantPacerHarness harness = new();

        UnseenServantAdmissionHarness.Checkpoint save = new();

        harness.Watermarks.BeforeSave = save.PauseAsync;

        await using TestMcpSession session = await CreateSessionAsync(suppliedPacer: harness.Pacer);

        JsonElement arguments = JsonSerializer.SerializeToElement(
            new AdjustInitiativeArgs { JobName = "watch", IntervalMinutes = 15 },
            McpJsonSerializerContext.Default.AdjustInitiativeArgs);

        Task<McpToolsCallResultWire> call = (Task<McpToolsCallResultWire>)session.Server.GetType()
            .GetMethod("ExecuteAdjustInitiativeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session.Server, [arguments, CancellationToken.None])!;

        try
        {
            await save.WaitAsync();

            Assert.False(call.IsCompleted);
        }
        finally
        {
            save.Release.TrySetResult();

            Assert.False((await call.WaitAsync(TimeSpan.FromSeconds(10))).IsError);
        }
    }
}
