using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// An <see cref="IMcpClient"/> the manager-level lifecycle tests hand out through
/// <c>McpConnectionManager.ClientFactoryForTests</c>. It records how often it was disposed and lets a
/// test run code at the instant of disposal, which is where a restart is holding the entry gate.
/// </summary>
internal sealed class ScriptedMcpClient(long transportGeneration) : IMcpClient
{
    private int _disposeCount;

    public long TransportGeneration { get; } = transportGeneration;

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public Action? OnDispose { get; set; }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<McpBridgeTool>> GetToolsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<McpBridgeTool>>([]);

    public Task<ModelContextProtocol.Protocol.CallToolResult> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        TimeSpan? requestTimeout = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask DisposeAsync()
    {
        _ = Interlocked.Increment(ref _disposeCount);

        OnDispose?.Invoke();

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Hands every start its own <see cref="ScriptedMcpClient"/> and remembers them in start order.
/// </summary>
internal sealed class ScriptedMcpClientFactory
{
    private readonly object _gate = new();

    private readonly List<ScriptedMcpClient> _created = [];

    public IReadOnlyList<ScriptedMcpClient> Created
    {
        get
        {
            lock (_gate)
            {
                return [.. _created];
            }
        }
    }

    public IMcpClient Create(ManagedMcpServerEntry entry, long transportGeneration)
    {
        ScriptedMcpClient client = new(transportGeneration);

        lock (_gate)
        {
            _created.Add(client);
        }

        return client;
    }
}
