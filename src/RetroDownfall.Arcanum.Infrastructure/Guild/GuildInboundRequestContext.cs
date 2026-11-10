namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Logical request scope populated exclusively by the authenticated transport boundary.</summary>
internal sealed class GuildInboundRequestContext
{
    private readonly AsyncLocal<GuildInboundPeer?> _peer = new();

    internal GuildInboundPeer? Peer => _peer.Value;

    internal IDisposable Enter(GuildInboundPeer peer)
    {
        GuildInboundPeer? prior = _peer.Value;

        _peer.Value = peer;

        return new Scope(() => _peer.Value = prior);
    }

    private sealed class Scope(Action exit) : IDisposable
    {
        public void Dispose() => exit();
    }
}
