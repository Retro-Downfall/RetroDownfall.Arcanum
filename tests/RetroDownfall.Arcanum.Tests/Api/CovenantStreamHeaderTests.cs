using Microsoft.AspNetCore.Http;

using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Streaming;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// Issue #89 — a streaming writer may set the ordinary cache default and may never weaken a protected
/// one.
/// </summary>
/// <remarks>
/// The asymmetry is the point, and it is one-way for a physical reason: headers cannot be corrected
/// after the first byte has left. A writer that assigned <c>Cache-Control: no-cache</c>
/// unconditionally would silently downgrade a protected stream from <c>no-store, private</c>, and the
/// downgrade would be undiscoverable from either end.
///
/// <para>The reverse must not happen either. An explicit-<c>none</c>, untainted response that is
/// genuinely eligible for the generic response cache must keep the shipped streaming default;
/// stamping every stream private would quietly delete a shipped optimization in the name of a
/// protection it does not need.</para>
/// </remarks>
public sealed class CovenantStreamHeaderTests
{

    [Fact]
    public void An_ordinary_stream_keeps_the_shipped_streaming_default()
    {

        DefaultHttpContext context = new();

        SseStreamWriter.PrepareResponse(context);

        Assert.Equal("no-cache", context.Response.Headers.CacheControl.ToString());

        Assert.True(string.IsNullOrEmpty(context.Response.Headers.Pragma.ToString()));

        Assert.Equal("text/event-stream; charset=utf-8", context.Response.ContentType);

    }

    [Fact]
    public void A_protected_stream_keeps_the_exact_private_tuple()
    {

        DefaultHttpContext context = new();

        CovenantRequestFeatures.MarkProtectedResponse(context);

        SseStreamWriter.PrepareResponse(context);

        Assert.Equal("no-store, private", context.Response.Headers.CacheControl.ToString());

        Assert.Equal("no-cache", context.Response.Headers.Pragma.ToString());

        Assert.Equal("0", context.Response.Headers.Expires.ToString());

    }

    [Fact]
    public void Marking_a_response_protected_after_the_writer_ran_still_reaches_the_wire()
    {

        DefaultHttpContext context = new();

        SseStreamWriter.PrepareResponse(context);

        Assert.Equal("no-cache", context.Response.Headers.CacheControl.ToString());

        // The mark registers an OnStarting callback, so a decision reached after the writer set its
        // default still lands before the first byte. A stream whose protected arm is discovered
        // mid-preparation must not ship with the weaker header.
        CovenantRequestFeatures.MarkProtectedResponse(context);

        Assert.True(CovenantRequestFeatures.IsProtectedResponse(context));

        CovenantProtectedResponseHeaders.ApplyStreamingDefaultWithoutWeakening(context);

        Assert.Equal("no-store, private", context.Response.Headers.CacheControl.ToString());

    }

    [Fact]
    public void An_unmarked_response_is_not_protected()
    {

        Assert.False(CovenantRequestFeatures.IsProtectedResponse(new DefaultHttpContext()));

    }

}
