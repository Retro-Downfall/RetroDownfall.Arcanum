using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

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

    /// <summary>
    /// Hashes everything that decides what the request does: route, canonical query, content type, and body.
    /// The query string is material — <c>?workspace=</c> and <c>?version=</c> retarget spell and prompt
    /// execution — so two requests that differ only there must collide into
    /// <c>Security.IdempotencyConflict</c> rather than replay each other's response.
    /// </summary>
    public static string ComputeFingerprintHash(
        byte[] bodyBytes,
        string normalizedRoute,
        string normalizedQuery,
        string? contentType)
    {
        byte[] prefixBytes = Encoding.UTF8.GetBytes(
            normalizedRoute + "\n" + normalizedQuery + "\n" + (contentType ?? string.Empty) + "\n");

        byte[] combined = new byte[prefixBytes.Length + bodyBytes.Length];

        Buffer.BlockCopy(prefixBytes, 0, combined, 0, prefixBytes.Length);
        Buffer.BlockCopy(bodyBytes, 0, combined, prefixBytes.Length, bodyBytes.Length);

        return Sha256Hex(combined);
    }

    public static string ResolvePrincipal(HttpContext httpContext)
    {
        // Single-user local installation identity — API key is not echoed; use a stable local marker.
        // Deliberately NOT derived from the client-supplied Host header: a caller could otherwise
        // partition its own claims (localhost:5001 vs 127.0.0.1:5001) and defeat replay protection.
        // When multi-principal auth exists, replace with the authenticated subject.
        _ = httpContext;

        return "local";
    }

    public static string NormalizeRoute(HttpContext httpContext)
    {
        PathString path = httpContext.Request.Path;

        return path.HasValue ? path.Value! : "/";
    }

    /// <summary>
    /// Canonicalizes the query string as ordinal-sorted <c>key=value</c> pairs so that parameter order
    /// alone never changes the fingerprint, while any differing name or value does.
    /// </summary>
    public static string NormalizeQuery(HttpContext httpContext)
    {
        IQueryCollection query = httpContext.Request.Query;

        if (query.Count == 0)
        {

            return string.Empty;

        }

        List<string> pairs = [];

        foreach (KeyValuePair<string, StringValues> entry in query)
        {

            foreach (string? value in entry.Value)
            {

                pairs.Add(entry.Key + "=" + (value ?? string.Empty));

            }

        }

        pairs.Sort(StringComparer.Ordinal);

        return string.Join('&', pairs);
    }

    private static string Sha256Hex(byte[] bytes)
    {
        byte[] hash = SHA256.HashData(bytes);

        return Convert.ToHexString(hash);
    }

}
