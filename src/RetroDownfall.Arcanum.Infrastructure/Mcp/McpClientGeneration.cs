using ModelContextProtocol.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Owns one MCP client generation. Retirement rejects new calls before dispatch, lets already
/// active calls drain, and only then disposes the underlying client/channel.
/// </summary>
internal sealed class McpClientGeneration(
    IMcpClient inner) : IMcpClient
{
    private readonly object _gate = new();

    private readonly TaskCompletionSource _drained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _activeCalls;

    private bool _retired;

    private Task? _disposeTask;

    public async Task InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        AcquireCall();

        try
        {
            await inner.InitializeAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ReleaseCall();
        }
    }

    public async Task<IReadOnlyList<McpBridgeTool>> GetToolsAsync(
        CancellationToken cancellationToken = default)
    {
        AcquireCall();

        try
        {
            IReadOnlyList<McpBridgeTool> tools =
                await inner.GetToolsAsync(cancellationToken)
                    .ConfigureAwait(false);

            return tools.Select(
                tool => tool.WithClient(this)).ToArray();
        }
        finally
        {
            ReleaseCall();
        }
    }

    public async Task<CallToolResult> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        TimeSpan? requestTimeout = null,
        CancellationToken cancellationToken = default)
    {
        AcquireCall();

        try
        {
            return await inner.CallToolAsync(
                    toolName,
                    arguments,
                    requestTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ReleaseCall();
        }
    }

    public ValueTask DisposeAsync()
    {
        Task disposal;

        lock (_gate)
        {
            _retired = true;

            if (_activeCalls == 0)
            {
                _drained.TrySetResult();
            }

            disposal = _disposeTask ??= DisposeCoreAsync();
        }

        return new ValueTask(disposal);
    }

    private async Task DisposeCoreAsync()
    {
        await _drained.Task.ConfigureAwait(false);
        await inner.DisposeAsync().ConfigureAwait(false);
    }

    private void AcquireCall()
    {
        lock (_gate)
        {
            if (_retired)
            {
                throw new McpTransportUnavailableException(
                    "The MCP client generation was retired before this tool call could be dispatched.",
                    McpRequestDispatchState.NotDispatched);
            }

            _activeCalls++;
        }
    }

    private void ReleaseCall()
    {
        lock (_gate)
        {
            _activeCalls--;

            if (_retired && _activeCalls == 0)
            {
                _drained.TrySetResult();
            }
        }
    }

}
