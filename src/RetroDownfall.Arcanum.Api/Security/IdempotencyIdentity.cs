using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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

    /// <summary>
    /// Canonicalizes the route so that spellings ASP.NET Core routing treats as the same endpoint —
    /// segment casing, a trailing slash — collapse to one claim key and one fingerprint instead of
    /// executing the billed side effect twice. The matched endpoint's registered route pattern
    /// supplies the canonical literal text (it is server-owned, so it never varies with what the
    /// caller typed), and the route <em>values</em> are appended verbatim and ordinal-sorted:
    /// <c>/api/spells/Foo/execute</c> and <c>/api/spells/foo/execute</c> may be different spells, so
    /// they must stay different identities even though they share a pattern.
    /// </summary>
    public static string NormalizeRoute(HttpContext httpContext)
    {
        string? pattern = (httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;

        PathString path = httpContext.Request.Path;

        string canonical = TrimTrailingSlash(pattern ?? (path.HasValue ? path.Value! : "/"));

        RouteValueDictionary routeValues = httpContext.Request.RouteValues;

        if (pattern is null || routeValues.Count == 0)
        {

            return canonical;

        }

        List<string> pairs = [];

        foreach (KeyValuePair<string, object?> entry in routeValues)
        {

            pairs.Add(entry.Key + "=" + (entry.Value?.ToString() ?? string.Empty));

        }

        pairs.Sort(StringComparer.Ordinal);

        return canonical + "|" + string.Join('&', pairs);
    }

    private static string TrimTrailingSlash(string route)
    {
        // Routing ignores exactly one trailing slash, so exactly one is stripped here.
        return route.Length > 1 && route[^1] == '/' ? route[..^1] : route;
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
