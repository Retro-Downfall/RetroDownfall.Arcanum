using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Gathers operator/agent input for a Streamable HTTP multi-round tool response (MRTR). When a
/// <c>tools/call</c> returns an <see cref="McpInputRequiredResult"/>, the HTTP client invokes this to
/// resolve each <see cref="McpInputRequest"/> into an <see cref="McpInputResponse"/> before re-POSTing.
/// </summary>
internal interface IMcpInputElicitor
{
    /// <summary>
    /// Resolves a response for every request. Implementations must return one response per request
    /// (correlated by <see cref="McpInputRequest.Id"/>) or honor <paramref name="cancellationToken"/>.
    /// </summary>
    Task<IReadOnlyList<McpInputResponse>> ElicitAsync(
        IReadOnlyList<McpInputRequest> requests,
        CancellationToken cancellationToken);
}
