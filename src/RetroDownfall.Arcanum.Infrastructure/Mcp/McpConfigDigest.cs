using System.Security.Cryptography;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// The one definition of the digest that binds a trust approval to a workspace <c>mcp.json</c>: SHA-256 of
/// the exact bytes, as upper-case hex. The trust store records it and compares it, and the trust preview
/// reports it, so what an operator is shown and what the host binds trust to are the same bytes.
/// </summary>
public static class McpConfigDigest
{
    /// <summary>The SHA-256 of <paramref name="bytes"/> as upper-case hex.</summary>
    public static string Compute(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));
}
