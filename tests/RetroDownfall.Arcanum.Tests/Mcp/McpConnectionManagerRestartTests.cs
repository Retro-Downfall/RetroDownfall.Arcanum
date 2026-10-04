using RetroDownfall.Arcanum.Core.Mcp;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Mcp;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class McpConnectionManagerRestartTests
{
    private const string ServerName = "scripted";

    [Fact]
    public async Task Cancelled_restart_after_stop_reports_stopped_server_in_result()
    {
        ScriptedMcpClientFactory clients = new();

        await using McpConnectionManager manager = McpConnectionManagerHarness.Create();

        manager.ClientFactoryForTests = clients.Create;

        await RegisterAsync(manager);

        Result started = await manager.StartAsync(ServerName, workingDirectory: null);

        Assert.True(started.IsSuccess, started.IsFailure ? started.Error.Message : null);

        ManagedMcpServerEntry entry = Assert.IsType<ManagedMcpServerEntry>(
            manager.GetManagedEntryForTests(ServerName, workingDirectory: null));

        using CancellationTokenSource cancellation = new();

        // The caller gives up at the very moment the old server finishes stopping, which is after the
        // point where the restart can still be abandoned without having changed anything.
        ScriptedMcpClient first = Assert.Single(clients.Created);

        first.OnDispose = cancellation.Cancel;

        Result result = await manager.RestartAsync(ServerName, workingDirectory: null, cancellation.Token);

        // The server really is stopped by now, so the answer has to say so rather than surface as a
        // bare cancellation the caller cannot tell from "nothing happened".
        Assert.True(result.IsFailure);

        Assert.Equal("Mcp.RestartCanceled", result.Error.Code);

        Assert.Contains("stopped", result.Error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(McpServerState.Stopped, entry.State);

        Assert.Null(entry.Client);

        Assert.Equal(1, first.DisposeCount);

        // No replacement was started for a caller that had already left.
        Assert.Single(clients.Created);

        McpServerInfo? status = await manager.GetStatusAsync(ServerName, workingDirectory: null);

        Assert.Equal(McpServerState.Stopped, status?.State);
    }

    [Fact]
    public async Task Restart_cancelled_while_the_replacement_is_starting_reports_the_stopped_server_in_result()
    {
        ScriptedMcpClientFactory clients = new();

        await using McpConnectionManager manager = McpConnectionManagerHarness.Create();

        manager.ClientFactoryForTests = clients.Create;

        await RegisterAsync(manager);

        Result started = await manager.StartAsync(ServerName, workingDirectory: null);

        Assert.True(started.IsSuccess, started.IsFailure ? started.Error.Message : null);

        ManagedMcpServerEntry entry = Assert.IsType<ManagedMcpServerEntry>(
            manager.GetManagedEntryForTests(ServerName, workingDirectory: null));

        ScriptedMcpClient first = Assert.Single(clients.Created);

        using CancellationTokenSource cancellation = new();

        // The old server stops normally. The caller then leaves in the middle of the replacement's
        // initialize handshake, which is the longest wait of the whole restart.
        clients.OnCreate = client =>
        {
            client.OnInitialize = async token =>
            {
                await cancellation.CancelAsync();

                await Task.Delay(Timeout.Infinite, token);
            };
        };

        Result result = await manager.RestartAsync(ServerName, workingDirectory: null, cancellation.Token);

        // The old server is gone and the replacement never came up, so the answer has to say that
        // rather than surface as a bare cancellation that reads as "nothing happened".
        Assert.True(result.IsFailure);

        Assert.Equal("Mcp.RestartCanceled", result.Error.Code);

        Assert.Contains("not running", result.Error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(McpServerState.Error, entry.State);

        Assert.Null(entry.Client);

        Assert.Equal(1, first.DisposeCount);

        // The half-started replacement was disposed rather than leaked.
        ScriptedMcpClient replacement = clients.Created[1];

        Assert.Equal(1, replacement.DisposeCount);

        Assert.Equal(2, clients.Created.Count);
    }

    [Fact]
    public async Task Restart_without_cancellation_replaces_the_client()
    {
        ScriptedMcpClientFactory clients = new();

        await using McpConnectionManager manager = McpConnectionManagerHarness.Create();

        manager.ClientFactoryForTests = clients.Create;

        await RegisterAsync(manager);

        _ = await manager.StartAsync(ServerName, workingDirectory: null);

        Result result = await manager.RestartAsync(ServerName, workingDirectory: null);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.Equal(2, clients.Created.Count);

        Assert.Equal(1, clients.Created[0].DisposeCount);

        Assert.Equal(0, clients.Created[1].DisposeCount);
    }

    private static Task RegisterAsync(McpConnectionManager manager) =>
        manager.RegisterFromConfigAsync(
            new McpConfig
            {
                McpServers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
                {
                    [ServerName] = new()
                    {
                        Command = "arcanum-test-scripted-server",
                    },
                },
            },
            scopeWorkingDirectory: null,
            CancellationToken.None);
}
