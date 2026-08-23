using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Primitives;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Security;

/// <summary>
/// The response policy every Covenant-bearing result shares.
/// </summary>
/// <remarks>
/// Written once and applied by both result types, because "these responses are never cached" is the
/// kind of rule a second surface forgets. A protected page that picked up an <c>ETag</c> from a
/// generic filter would become conditionally revalidatable, and a 304 is a cache hit on content the
/// installation may since have erased (§10.16).
///
/// <para>Issue #89 widened the emitted tuple from a bare <c>no-store</c> to the full
/// <c>no-store, private</c> / <c>Pragma: no-cache</c> / <c>Expires: 0</c> set now owned by
/// <see cref="CovenantProtectedResponseHeaders"/>, so that every protected path — buffered, streamed,
/// and refused — emits the same headers rather than two nearly identical ones (§10.18).</para>
/// </remarks>
internal static class CovenantProtectedResponse
{

    /// <summary>
    /// Marks a response uncacheable and strips every validator that could make it conditional.
    /// </summary>
    internal static void Seal(HttpResponse response) => CovenantProtectedResponseHeaders.Apply(response);

}

/// <summary>
/// A JSON response that owns its Covenant lease for the whole of serialization.
/// </summary>
/// <remarks>
/// The lease is acquired by the caller, revalidated here immediately before the first byte, and
/// released in a <c>finally</c> once the body is written or refused. That ordering is the whole
/// point: a handler that released its lease on return would still be serializing protected content
/// while a reset was draining, and the destructive operation would report completion over a socket
/// that was still writing the erased bytes.
///
/// <para>Single-use. A result that could execute twice would write a second body under a lease it had
/// already released, so the second attempt throws rather than proceeding without coverage.</para>
/// </remarks>
public sealed class CovenantProtectedJsonResult<T> : IResult
{

    private readonly ICovenantOperationLease _lease;

    private readonly Result<T> _payload;

    private readonly JsonTypeInfo<ApiResponse<T>> _typeInfo;

    private int _executed;

    public CovenantProtectedJsonResult(
        ICovenantOperationLease lease,
        Result<T> payload,
        JsonTypeInfo<ApiResponse<T>> typeInfo)
    {

        ArgumentNullException.ThrowIfNull(lease);

        ArgumentNullException.ThrowIfNull(payload);

        ArgumentNullException.ThrowIfNull(typeInfo);

        _lease = lease;

        _payload = payload;

        _typeInfo = typeInfo;

    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {

        ArgumentNullException.ThrowIfNull(httpContext);

        if (Interlocked.Exchange(ref _executed, 1) != 0)
        {

            throw new InvalidOperationException(
                "A lease-bound Covenant result may be executed once; its lease is released when it completes.");

        }

        try
        {

            CovenantProtectedResponse.Seal(httpContext.Response);

            // Revalidated before anything is written. A lease checked after the first byte would
            // prove the dataset was current at some point during a response the client already has.
            Result revalidated = await _lease
                .RevalidateAsync(httpContext.RequestAborted)
                .ConfigureAwait(false);

            Result<T> effective = revalidated.IsFailure
                ? Result<T>.Failure(revalidated.Error)
                : _payload;

            httpContext.Response.StatusCode = effective.IsSuccess
                ? StatusCodes.Status200OK
                : ArcanumErrorMapper.ResolveStatusCode(effective.Error.Code);

            httpContext.Response.ContentType = "application/json; charset=utf-8";

            await JsonSerializer
                .SerializeAsync(
                    httpContext.Response.Body,
                    ApiResponse<T>.FromResult(effective, httpContext.TraceIdentifier),
                    _typeInfo,
                    httpContext.RequestAborted)
                .ConfigureAwait(false);

        }
        finally
        {

            await _lease.DisposeAsync().ConfigureAwait(false);

        }

    }

}

/// <summary>
/// A streamed response that owns its Covenant lease until the stream completes.
/// </summary>
/// <remarks>
/// The same contract as the JSON result, and it matters more here: a stream can be long enough for a
/// reset to start and finish inside it. The source stream is disposed on every path, including the
/// refusal path, because a stream opened over protected content is itself a handle the erasure
/// sequence has to be able to drain.
/// </remarks>
public sealed class CovenantProtectedStreamResult : IResult
{

    private readonly ICovenantOperationLease _lease;

    private readonly Stream _content;

    private readonly string _contentType;

    private int _executed;

    public CovenantProtectedStreamResult(ICovenantOperationLease lease, Stream content, string contentType)
    {

        ArgumentNullException.ThrowIfNull(lease);

        ArgumentNullException.ThrowIfNull(content);

        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        _lease = lease;

        _content = content;

        _contentType = contentType;

    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {

        ArgumentNullException.ThrowIfNull(httpContext);

        if (Interlocked.Exchange(ref _executed, 1) != 0)
        {

            throw new InvalidOperationException(
                "A lease-bound Covenant result may be executed once; its lease is released when it completes.");

        }

        try
        {

            CovenantProtectedResponse.Seal(httpContext.Response);

            Result revalidated = await _lease
                .RevalidateAsync(httpContext.RequestAborted)
                .ConfigureAwait(false);

            if (revalidated.IsFailure)
            {

                httpContext.Response.StatusCode = ArcanumErrorMapper.ResolveStatusCode(revalidated.Error.Code);

                httpContext.Response.ContentType = "application/json; charset=utf-8";

                await JsonSerializer
                    .SerializeAsync(
                        httpContext.Response.Body,
                        ApiResponse<bool>.FromResult(
                            Result<bool>.Failure(revalidated.Error),
                            httpContext.TraceIdentifier),
                        ArcanumJsonContext.Default.ApiResponseBoolean,
                        httpContext.RequestAborted)
                    .ConfigureAwait(false);

                return;

            }

            httpContext.Response.StatusCode = StatusCodes.Status200OK;

            httpContext.Response.ContentType = _contentType;

            await _content.CopyToAsync(httpContext.Response.Body, httpContext.RequestAborted).ConfigureAwait(false);

        }
        finally
        {

            await _content.DisposeAsync().ConfigureAwait(false);

            await _lease.DisposeAsync().ConfigureAwait(false);

        }

    }

}
