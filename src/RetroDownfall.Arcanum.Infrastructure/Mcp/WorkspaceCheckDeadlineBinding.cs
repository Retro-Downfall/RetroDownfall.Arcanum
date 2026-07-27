namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Bridges the host-owned monotonic inference deadline across the in-process MCP task boundary by
/// connection + JSON-RPC request ID. Entries are one-shot and opportunistically expired.
/// </summary>
internal static class WorkspaceCheckDeadlineBinding
{
    private static readonly long TtlMilliseconds =
        (long)TimeSpan.FromMinutes(10).TotalMilliseconds;

    private static readonly long SweepIntervalMilliseconds =
        (long)TimeSpan.FromMinutes(1).TotalMilliseconds;

    private static readonly ExpiringRequestBindingStore<long> ByRequest =
        new(
            TtlMilliseconds,
            SweepIntervalMilliseconds,
            static () => Environment.TickCount64);

    internal static void BindRequest(
        string connectionKey,
        string requestId,
        long deadlineTimestamp)
    {

        if (string.IsNullOrWhiteSpace(connectionKey)
            || string.IsNullOrWhiteSpace(requestId))
        {

            return;
        }

        ByRequest.Bind(
            connectionKey,
            requestId,
            deadlineTimestamp);
    }

    internal static bool TryResolveRequest(
        string connectionKey,
        string requestId,
        out long deadlineTimestamp)
    {

        return ByRequest.TryResolve(
            connectionKey,
            requestId,
            out deadlineTimestamp);
    }

    internal static void UnbindRequest(
        string connectionKey,
        string requestId)
    {

        if (string.IsNullOrWhiteSpace(connectionKey)
            || string.IsNullOrWhiteSpace(requestId))
        {

            return;
        }

        ByRequest.Unbind(connectionKey, requestId);
    }

}
