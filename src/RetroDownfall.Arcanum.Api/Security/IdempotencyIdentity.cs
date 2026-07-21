using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace RetroDownfall.Arcanum.Api.Security;

/// <summary>
/// Separates idempotency claim identity from request fingerprint.
/// </summary>
internal static class IdempotencyIdentity
{

    public const string ApiVersion = "v1";

    public static string ComputeClaimKeyHash(
        string principalOrInstallationId,
        string httpMethod,
        string normalizedRoute,
        string idempotencyKey)
    {
        string material =
            principalOrInstallationId + "\n"
            + ApiVersion + "\n"
            + httpMethod.ToUpperInvariant() + "\n"
            + normalizedRoute + "\n"
            + idempotencyKey;

        return Sha256Hex(Encoding.UTF8.GetBytes(material));
    }

    public static string ComputeFingerprintHash(byte[] bodyBytes, string normalizedRoute, string? contentType)
    {
        byte[] routeBytes = Encoding.UTF8.GetBytes(normalizedRoute);
        byte[] headerBytes = Encoding.UTF8.GetBytes(contentType ?? string.Empty);
        byte[] combined = new byte[routeBytes.Length + 1 + headerBytes.Length + 1 + bodyBytes.Length];

        Buffer.BlockCopy(routeBytes, 0, combined, 0, routeBytes.Length);
        combined[routeBytes.Length] = (byte)'\n';
        Buffer.BlockCopy(headerBytes, 0, combined, routeBytes.Length + 1, headerBytes.Length);
        combined[routeBytes.Length + 1 + headerBytes.Length] = (byte)'\n';
        Buffer.BlockCopy(bodyBytes, 0, combined, routeBytes.Length + 1 + headerBytes.Length + 1, bodyBytes.Length);

        return Sha256Hex(combined);
    }

    public static string ResolvePrincipal(HttpContext httpContext)
    {
        // Single-user local installation identity — API key is not echoed; use a stable local marker.
        // When multi-principal auth exists, replace with authenticated subject.
        string? host = httpContext.Request.Host.Value;

        return string.IsNullOrWhiteSpace(host) ? "local" : "local:" + host;
    }

    public static string NormalizeRoute(HttpContext httpContext)
    {
        PathString path = httpContext.Request.Path;

        return path.HasValue ? path.Value! : "/";
    }

    private static string Sha256Hex(byte[] bytes)
    {
        byte[] hash = SHA256.HashData(bytes);

        return Convert.ToHexString(hash);
    }

}
