using RetroDownfall.Arcanum.Core.Mcp;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Mcp;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class McpConnectionManagerTransportEndedTests
{
    private const string ServerName = "scripted";

    [Fact]
    public async Task Stale_transport_ended_handler_does_not_dispose_restarted_client()
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

        // The entry holds the production generation wrapper around the scripted client, as it does for a
        // real transport.
        IMcpClient firstGeneration = Assert.IsType<McpClientGeneration>(entry.Client);

        // The first client's transport ends at the very moment a restart is replacing it: the restart
        // already holds the entry gate and is disposing the first client, and has not yet moved the
        // entry to the next transport generation. The handler therefore still sees its own generation
        // as current, passes its first check, and parks on the gate the restart holds.
        Task? staleHandler = null;

        first.OnDispose = () =>
            staleHandler = manager.HandleTransportEndedAsync(entry, first.TransportGeneration);

        Result restarted = await manager.RestartAsync(ServerName, workingDirectory: null);

        Assert.True(restarted.IsSuccess, restarted.IsFailure ? restarted.Error.Message : null);

        Task handler = Assert.IsAssignableFrom<Task>(staleHandler);

        await handler.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, clients.Created.Count);

        ScriptedMcpClient second = clients.Created[1];

        Assert.True(second.TransportGeneration > first.TransportGeneration);

        // The restart's own generation bump is what makes the first client's callback stale.
        Assert.Equal(second.TransportGeneration, entry.TransportGeneration);

        Assert.Equal(McpServerState.Running, entry.State);

        IMcpClient secondGeneration = Assert.IsType<McpClientGeneration>(entry.Client);

        Assert.NotSame(firstGeneration, secondGeneration);

        Assert.Equal(0, second.DisposeCount);

        // The restart retired the first generation, so a late call on it is refused before dispatch rather
        // than reaching a client that is gone.
        McpTransportUnavailableException retired = await Assert.ThrowsAsync<McpTransportUnavailableException>(
            () => firstGeneration.GetToolsAsync());

        Assert.Equal(McpRequestDispatchState.NotDispatched, retired.DispatchState);

        Assert.Null(entry.ErrorMessage);

        Assert.Null(entry.RestartAfterUtc);
    }

    [Fact]
    public async Task Transport_ended_for_the_current_client_marks_it_failed_and_backs_off()
    {
        ScriptedMcpClientFactory clients = new();

        await using McpConnectionManager manager = McpConnectionManagerHarness.Create();

        manager.ClientFactoryForTests = clients.Create;

        await RegisterAsync(manager);

        _ = await manager.StartAsync(ServerName, workingDirectory: null);

        ManagedMcpServerEntry entry = Assert.IsType<ManagedMcpServerEntry>(
            manager.GetManagedEntryForTests(ServerName, workingDirectory: null));

        ScriptedMcpClient client = Assert.Single(clients.Created);

        await manager
            .HandleTransportEndedAsync(entry, client.TransportGeneration)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(McpServerState.Error, entry.State);

        Assert.Null(entry.Client);

        Assert.Equal(1, client.DisposeCount);

        Assert.NotNull(entry.RestartAfterUtc);
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
