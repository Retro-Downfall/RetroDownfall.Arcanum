using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace RetroDownfall.Arcanum.Api.Security;

/// <summary>
/// The one place the protected-response header tuple is written.
/// </summary>
/// <remarks>
/// Exactly <c>Cache-Control: no-store, private</c>, <c>Pragma: no-cache</c>, and <c>Expires: 0</c>,
/// with any <c>ETag</c> and <c>Last-Modified</c> removed. Written once and applied by every protected
/// path, because "these responses are never cached" is the kind of rule a second surface forgets.
///
/// <para>Each element earns its place. <c>no-store</c> is the modern instruction; <c>private</c>
/// survives the intermediaries that treat an unqualified <c>no-store</c> as advisory; <c>Pragma</c>
/// and <c>Expires</c> are for the HTTP/1.0-era caches that ignore <c>Cache-Control</c> entirely. A
/// validator is removed rather than merely not set, because another filter may have added one earlier
/// in the pipeline and a validator on a no-store response is exactly the combination a permissive
/// intermediary resolves in the wrong direction — a 304 is a cache hit on content the installation may
/// since have erased (§10.18).</para>
/// </remarks>
internal static class CovenantProtectedResponseHeaders
{

    private const string ProtectedCacheControl = "no-store, private";

    private const string StreamingDefaultCacheControl = "no-cache";

    /// <summary>
    /// Applies the exact tuple. Safe to call more than once; the result is identical.
    /// </summary>
    internal static void Apply(HttpResponse response)
    {

        ArgumentNullException.ThrowIfNull(response);

        response.Headers.CacheControl = ProtectedCacheControl;

        response.Headers.Pragma = "no-cache";

        response.Headers.Expires = "0";

        response.Headers.Remove(HeaderNames.ETag);

        response.Headers.Remove(HeaderNames.LastModified);

    }

    /// <summary>
    /// Applies the tuple only when this request was decided to be protected.
    /// </summary>
    /// <remarks>
    /// An explicit-<c>none</c>, untainted response that is genuinely eligible for the generic response
    /// cache must not inherit private headers it does not need; marking every inference response
    /// uncacheable would quietly delete a shipped optimization.
    /// </remarks>
    internal static void ApplyIfProtected(HttpContext context)
    {

        ArgumentNullException.ThrowIfNull(context);

        if (CovenantRequestFeatures.IsProtectedResponse(context))
        {

            Apply(context.Response);

        }

    }

    /// <summary>
    /// Sets the ordinary streaming cache default without ever weakening a protected response.
    /// </summary>
    /// <remarks>
    /// The asymmetry matters. A streaming writer that assigned <c>Cache-Control: no-cache</c>
    /// unconditionally would overwrite <c>no-store, private</c> on a protected stream, and there is no
    /// second chance: headers cannot be corrected after the first byte has left.
    /// </remarks>
    internal static void ApplyStreamingDefaultWithoutWeakening(HttpContext context)
    {

        ArgumentNullException.ThrowIfNull(context);

        if (CovenantRequestFeatures.IsProtectedResponse(context))
        {

            Apply(context.Response);

            return;

        }

        context.Response.Headers.CacheControl = StreamingDefaultCacheControl;

    }

}
