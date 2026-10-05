using RetroDownfall.Arcanum.Core.Events;

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

        Assert.Equal(ErrorCodes.Mcp.RestartCanceled, result.Error.Code);

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

        Assert.Equal(ErrorCodes.Mcp.RestartCanceled, result.Error.Code);

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
    public async Task Stop_cancelled_after_the_server_stopped_reports_success_and_still_publishes_the_stopped_event()
    {
        ScriptedMcpClientFactory clients = new();

        RecordingEventBus events = new();

        await using McpConnectionManager manager = McpConnectionManagerHarness.Create(eventBus: events);

        manager.ClientFactoryForTests = clients.Create;

        await RegisterAsync(manager);

        Result started = await manager.StartAsync(ServerName, workingDirectory: null);

        Assert.True(started.IsSuccess, started.IsFailure ? started.Error.Message : null);

        ManagedMcpServerEntry entry = Assert.IsType<ManagedMcpServerEntry>(
            manager.GetManagedEntryForTests(ServerName, workingDirectory: null));

        ScriptedMcpClient first = Assert.Single(clients.Created);

        using CancellationTokenSource cancellation = new();

        // The caller gives up at the instant the server finishes stopping. The stop is already committed
        // (the client is gone and the entry is Stopped), so the answer is the stop's own result and the
        // Stopped event still reaches subscribers; a bare cancellation would say "nothing happened" about
        // a server that is no longer running.
        first.OnDispose = cancellation.Cancel;

        Result result = await manager.StopAsync(ServerName, workingDirectory: null, cancellation.Token);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.Equal(McpServerState.Stopped, entry.State);

        Assert.Null(entry.Client);

        Assert.Equal(1, first.DisposeCount);

        McpServerEvent stopped = Assert.Single(
            events.Published.OfType<McpServerEvent>(),
            static published => published.State == McpServerState.Stopped);

        Assert.Equal(ServerName, stopped.ServerName);
    }

    [Fact]
    public async Task Stop_cancelled_before_it_begins_changes_nothing_and_still_throws()
    {
        ScriptedMcpClientFactory clients = new();

        await using McpConnectionManager manager = McpConnectionManagerHarness.Create();

        manager.ClientFactoryForTests = clients.Create;

        await RegisterAsync(manager);

        _ = await manager.StartAsync(ServerName, workingDirectory: null);

        ManagedMcpServerEntry entry = Assert.IsType<ManagedMcpServerEntry>(
            manager.GetManagedEntryForTests(ServerName, workingDirectory: null));

        using CancellationTokenSource cancellation = new();

        await cancellation.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.StopAsync(ServerName, workingDirectory: null, cancellation.Token));

        Assert.Equal(McpServerState.Running, entry.State);

        Assert.Equal(0, Assert.Single(clients.Created).DisposeCount);
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

    private sealed class RecordingEventBus : IEventBus
    {
        private readonly object _gate = new();

        private readonly List<object> _published = [];

        public IReadOnlyList<object> Published
        {
            get
            {
                lock (_gate)
                {
                    return [.. _published];
                }
            }
        }

        public void Publish<T>(T @event)
            where T : notnull
        {
            lock (_gate)
            {
                _published.Add(@event);
            }
        }

        public IAsyncEnumerable<T> Subscribe<T>(CancellationToken cancellationToken)
            where T : notnull =>
            AsyncEnumerable.Empty<T>();
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
