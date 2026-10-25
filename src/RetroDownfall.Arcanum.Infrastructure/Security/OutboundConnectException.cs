namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// What the outbound egress connect callback raises for every way it ends without a connected socket: the host does not resolve or
/// resolves to nothing, an address is refused by the egress policy, the guard's own connect bound elapsed while resolving, or every
/// candidate address refused the connection. Nothing was connected, so no request byte could have left the process.
/// </summary>
/// <remarks>
/// It is an <see cref="HttpRequestException"/> so every existing handler of a failed send still catches it. It is also a type of its
/// own so a caller can tell a failure that happened before any connection existed from one on an established connection by type
/// alone, without reading any property of the exception: the guard consumes the resolver's <see cref="System.Net.Sockets.SocketException"/>
/// and the address-policy refusal has no socket error at all, so neither is visible beneath it otherwise.
/// </remarks>
internal sealed class OutboundConnectException : HttpRequestException
{
    /// <summary>Typed policy or DNS refusal; transport failures have no policy code.</summary>
    internal string? ErrorCode { get; init; }

    public OutboundConnectException(string message)
        : base(message)
    {
    }

    public OutboundConnectException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
