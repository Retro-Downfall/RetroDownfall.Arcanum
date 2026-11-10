using System.Text.Json;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Tests.Guild;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed partial class ArcanumInternalToolServerTests
{
    [Theory]
    [InlineData("write_file", "{\"relativePath\":\"guild-denied.txt\",\"content\":\"must not be written\"}")]
    [InlineData("read_file", "{\"relativePath\":\"notes/alpha.txt\",\"startLine\":1,\"endLine\":2}")]
    [InlineData("execute_command", "{\"command\":\"touch guild-denied.txt\"}")]
    [InlineData("search_memory", "{}")]
    public async Task Guild_private_context_crosses_server_task_and_refuses_before_effect(string name, string json)
    {
        await using TestMcpSession session = await CreateSessionAsync();

        using JsonDocument arguments = JsonDocument.Parse(json);

        using IDisposable persistedScope = PersistedToolInvocationAmbient.Begin(new(Guid.NewGuid(), Guid.NewGuid()));

        using IDisposable guildScope = GuildToolInvocationAmbient.Begin(GuildToolBoundaryTests.CreateContext());

        McpToolsCallResultWire result = await session.CallToolAsync(name, arguments.RootElement);

        Assert.True(result.IsError);

        Assert.Contains(name == "search_memory" ? ErrorCodes.Guild.ToolNotGranted : ErrorCodes.Guild.ToolBoundaryUnavailable,
            result.Content![0].Text);

        Assert.DoesNotContain("line one", result.Content[0].Text);

        Assert.False(File.Exists(Path.Combine(_workspace.Root, "guild-denied.txt")));
    }
    [Fact]
    public async Task Guild_private_queued_call_rechecks_host_policy_on_server_task_before_disclosure()
    {
        await using TestMcpSession session = await CreateSessionAsync();

        using ManualResetEventSlim release = new();

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        bool eligible = true;

        session.Server.LineHandlerFaultForTesting = _ =>
        {
            entered.TrySetResult();

            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));

            return null;
        };

        using JsonDocument arguments = JsonDocument.Parse("{\"relativePath\":\"notes/alpha.txt\",\"startLine\":1,\"endLine\":2}");

        using IDisposable scope = GuildToolInvocationAmbient.Begin(GuildToolBoundaryTests.CreateContext(_ => Volatile.Read(ref eligible)));

        Task<McpToolsCallResultWire> pending = session.CallToolAsync("read_file", arguments.RootElement);

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Volatile.Write(ref eligible, false);
        }
        finally
        {
            release.Set();
        }

        McpToolsCallResultWire result = await pending;

        Assert.True(result.IsError);

        Assert.Contains(ErrorCodes.Guild.ToolNotGranted, result.Content![0].Text);

        Assert.DoesNotContain("line one", result.Content[0].Text);
    }
}
