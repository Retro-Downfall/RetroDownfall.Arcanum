using System.Diagnostics.CodeAnalysis;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Per-connection register of the attended turns whose tool calls are in flight on one MCP server. A
/// server-initiated <c>elicitation/create</c> arrives on the SDK client's receive loop, which started at
/// connect time on an execution context that never sees <see cref="HumanPromptLiveEmitterAmbient"/>, so
/// the elicitation handler cannot read the ambient itself. <see cref="SdkMcpClientWrapper.CallToolAsync"/>
/// enters the calling turn's emitter here for the duration of each tool call, and
/// <see cref="McpElicitationBridge"/> resolves the request's emitter from here instead.
/// </summary>
internal sealed class McpElicitationSink
{

    internal const string NoAttendedTurnReason =
        "Elicitation requires an attended streaming turn with a live human-response channel.";

    internal const string AmbiguousTurnReason =
        "Elicitation cannot be routed: more than one attended turn has a tool call in flight on this server, so the request's origin is ambiguous.";

    private readonly Lock _sync = new();

    private readonly List<IHumanPromptLiveEmitter> _active = [];

    /// <summary>
    /// Registers <paramref name="emitter"/> as a caller with a tool call in flight until the returned scope
    /// is disposed. A null emitter (a buffered or unattended turn) registers nothing.
    /// </summary>
    public IDisposable Enter(IHumanPromptLiveEmitter? emitter)
    {

        if (emitter is null)
        {
            return NoOpScope.Instance;
        }

        lock (_sync)
        {
            _active.Add(emitter);
        }

        return new Scope(this, emitter);

    }

    /// <summary>
    /// Resolves the one attended turn a server-initiated request can belong to. Parallel calls from the
    /// same turn share one emitter instance and stay unambiguous; calls from different turns do not, and
    /// the request is declined rather than routed to a guess.
    /// </summary>
    public bool TryResolve(
        [NotNullWhen(true)] out IHumanPromptLiveEmitter? emitter,
        [NotNullWhen(false)] out string? declineReason)
    {

        lock (_sync)
        {

            emitter = null;

            foreach (IHumanPromptLiveEmitter candidate in _active)
            {

                if (emitter is null)
                {
                    emitter = candidate;

                    continue;
                }

                if (!ReferenceEquals(emitter, candidate))
                {
                    emitter = null;

                    declineReason = AmbiguousTurnReason;

                    return false;
                }

            }

            if (emitter is null)
            {
                declineReason = NoAttendedTurnReason;

                return false;
            }

            declineReason = null;

            return true;

        }

    }

    private void Exit(IHumanPromptLiveEmitter emitter)
    {

        lock (_sync)
        {

            for (int index = _active.Count - 1; index >= 0; index--)
            {

                if (ReferenceEquals(_active[index], emitter))
                {
                    _active.RemoveAt(index);

                    return;
                }

            }

        }

    }

    private sealed class Scope(McpElicitationSink owner, IHumanPromptLiveEmitter emitter) : IDisposable
    {

        private int _disposed;

        public void Dispose()
        {

            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Exit(emitter);
            }

        }

    }

    private sealed class NoOpScope : IDisposable
    {

        public static NoOpScope Instance { get; } = new();

        public void Dispose()
        {
        }

    }

}
