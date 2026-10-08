using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api;

internal static class ApiRequestJson
{
    public const string DefaultInvalidBodyMessage = "Request body is required.";

    public const string MalformedJsonMessage = "Request body could not be parsed as valid JSON.";

    public const string UnsupportedMediaTypeMessage =
        "Request body must be sent with 'Content-Type: application/json' and, if it names a charset, one this server can decode (UTF-8 always is).";

    public const string IncompleteBodyMessage = "Request body could not be read to completion.";

    public const string BodyTooLargeMessage = "Request body exceeded the maximum size this server accepts.";

    public const string BodyReadTimeoutMessage = "Request body arrived too slowly and the server stopped waiting for it.";

    public const string RequestHeadersTooLargeMessage =
        "Request headers or trailers exceeded the total size this server accepts.";

    public const string UnreadableBodyMessage = "Request body could not be read.";

    public const string UnacceptedMediaTypeMessage = "The request's Content-Type is not accepted by this route.";

    public const string ParameterBindingFailedMessage = "Request parameters could not be bound to this route.";

    /// <summary>
    /// Whether the request carries a body.
    /// </summary>
    /// <remarks>
    /// A declared <c>Content-Length</c> answers it. Without one, the server's own body detection does:
    /// over HTTP/2 and HTTP/3 a body streams with neither a <c>Content-Length</c> nor a
    /// <c>Transfer-Encoding</c>, and only the framework knows whether the request's headers ended its
    /// stream. A host that offers no detection (a hand-built context) falls back to
    /// <c>Transfer-Encoding</c>, which is how HTTP/1.1 sends a body of unknown length.
    /// </remarks>
    public static bool CarriesABody(HttpRequest request)
    {
        if (request.ContentLength is long length)
        {
            return length > 0;
        }

        return request.HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody
            ?? !string.IsNullOrEmpty(request.Headers.TransferEncoding);
    }

    /// <summary>
    /// Whether the request is typed as JSON but names a charset the read cannot decode.
    /// </summary>
    /// <remarks>
    /// The one request shape whose <see cref="InvalidOperationException"/> is the caller's: a route that
    /// binds its body as a handler parameter reads it in framework-generated code with no hook for this,
    /// so <c>ArcanumExceptionHandler</c> uses this to tell that exception from a fault of the server's own.
    /// </remarks>
    public static bool IsJsonWithAnUnreadableCharset(HttpRequest request) =>
        request.HasJsonContentType() && !HasReadableJsonContentType(request);

    /// <summary>
    /// Whether <c>ReadFromJsonAsync</c> can read this request's body: a JSON media type, and a
    /// <c>charset</c> parameter, if there is one, that names an encoding .NET can decode.
    /// </summary>
    /// <remarks>
    /// <see cref="HttpRequestJsonExtensions.HasJsonContentType(HttpRequest)"/> checks only the media type.
    /// The read then resolves the charset itself and raises <see cref="InvalidOperationException"/>, not
    /// <see cref="JsonException"/>, for one it does not know -- <c>windows-1252</c>, <c>shift_jis</c> and
    /// <c>gbk</c> are unknown unless an encoding provider is registered, and so is any made-up name, a
    /// quoted value, or an empty one. That is a request the caller got wrong, so every route that reads
    /// its own body answers it here, before the read, with the same 415 as a non-JSON media type.
    ///
    /// <para>Nothing after this check catches <see cref="InvalidOperationException"/>: with the media type
    /// and charset proven, one that still escapes the read is a fault of the server's own, and mapping it
    /// to 415 would tell the caller a lie and hide the fault. The charset test below follows the
    /// framework's own resolution exactly (a charset is looked up verbatim, and only <c>utf-8</c> is
    /// short-circuited), and <c>ApiRequestJsonCharsetTests</c> pins the two to agree, because a
    /// disagreement is the 500 this exists to prevent.</para>
    /// </remarks>
    public static bool HasReadableJsonContentType(HttpRequest request)
    {
        if (!request.HasJsonContentType())
        {
            return false;
        }

        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out MediaTypeHeaderValue? mediaType))
        {
            return false;
        }

        StringSegment charset = mediaType.Charset;

        if (!charset.HasValue || charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            _ = Encoding.GetEncoding(charset.Value);

            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// The request body as UTF-8, for a route that parses the raw bytes itself rather than through
    /// <c>ReadFromJsonAsync</c>.
    /// </summary>
    /// <remarks>
    /// Call only after <see cref="HasReadableJsonContentType"/> has accepted the request. A body with no
    /// charset, or a UTF-8 one, is the request stream itself; one sent in any other charset .NET can decode
    /// is transcoded to UTF-8 on the way through, which is what <c>ReadFromJsonAsync</c> does for every
    /// other route. A parser that reads the stream as UTF-8 regardless would read such a body as garbage
    /// and blame the caller for malformed JSON. The returned stream leaves the request body open.
    /// </remarks>
    public static Stream OpenUtf8Body(HttpRequest request)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out MediaTypeHeaderValue? mediaType))
        {
            return request.Body;
        }

        StringSegment charset = mediaType.Charset;

        if (!charset.HasValue || charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
        {
            return request.Body;
        }

        return Encoding.CreateTranscodingStream(
            request.Body,
            Encoding.GetEncoding(charset.Value!),
            Encoding.UTF8,
            leaveOpen: true);
    }

    public static async ValueTask<(T? Body, IResult? Error)> ReadAsync<T>(
        HttpContext httpContext,
        JsonTypeInfo<T> typeInfo,
        Func<HttpContext, IResult> invalidJsonResult,
        CancellationToken cancellationToken)
    {
        // ReadFromJsonAsync throws InvalidOperationException — not JsonException — for a missing or
        // non-JSON Content-Type and for a charset it cannot decode, so both are answered here, before the
        // read. Nothing after this check catches InvalidOperationException: with the media type and charset
        // proven, one that still escapes the read is a fault of the server's own, and mapping it to 415 told
        // the caller a lie and hid the fault.
        if (!HasReadableJsonContentType(httpContext.Request))
        {
            return (default, UnsupportedMediaTypeResult(httpContext));
        }

        try
        {
            T? body = await httpContext.Request
                .ReadFromJsonAsync(typeInfo, cancellationToken)
                .ConfigureAwait(false);

            return (body, null);
        }
        catch (JsonException)
        {
            return (default, invalidJsonResult(httpContext));
        }
        catch (BadHttpRequestException failure)
        {
            // Kestrel raises this for every request-level fault it detects while a body is being read
            // -- an early end, a body past the size ceiling, one under the minimum data rate, trailers
            // over the header ceiling -- and it is neither a JsonException nor an InvalidOperationException.
            // Uncaught it escapes to
            // ArcanumExceptionHandler, which special-cases only JsonException, so a client that dropped
            // mid-upload was told the server broke -- a 500 Hub.Unhandled with an Error-level log for a
            // routine client-side fault. Minimal-API parameter binding answers these with the
            // exception's own status, and every caller of this helper had lost that by using it.
            return (default, UnreadableBodyResult(httpContext, failure));
        }
    }

    /// <summary>
    /// A body the server could not finish reading, answered with the status Kestrel itself chose.
    /// </summary>
    /// <remarks>
    /// The status comes from <paramref name="failure"/> rather than being decided here, because Kestrel
    /// has already distinguished the cases that matter to a client, and the code is derived from that
    /// status by <see cref="ResolveBodyFault"/> so the two are chosen together rather than separately.
    /// Each gets its own code because what the caller should do next differs -- resend corrected, resend
    /// unchanged on a better connection, shrink the headers, or do not resend at all -- and because
    /// every other code on this installation's 413 is distinct from its family's invalid-request code.
    /// Only the wording is ours; the framework's own message is not echoed back.
    /// </remarks>
    public static IResult UnreadableBodyResult(HttpContext httpContext, BadHttpRequestException failure)
    {
        (string code, string message) = ResolveBodyFault(failure.StatusCode, failure);

        return BodyFaultResult(httpContext, failure.StatusCode, code, message);
    }

    /// <summary>
    /// A body fault the framework's generated reader recorded as a status on the response and did not
    /// throw, answered with that status.
    /// </summary>
    /// <remarks>
    /// There is no exception to read an inner <see cref="JsonException"/> or a binder message from, so
    /// only the statuses that name their own fault resolve to anything but the generic wording.
    /// </remarks>
    public static IResult UnreadableBodyResult(HttpContext httpContext, int statusCode)
    {
        (string code, string message) = ResolveBodyFault(statusCode, failure: null);

        return BodyFaultResult(httpContext, statusCode, code, message);
    }

    /// <summary>
    /// A 415 for a request whose Content-Type the route does not accept, with no exception to read.
    /// </summary>
    /// <remarks>
    /// A route that binds its body declares the media types it accepts, and routing refuses a request
    /// carrying any other before the route is chosen: its accepts policy selects an endpoint that sets 415
    /// and writes nothing, so no exception reaches the handler whatever <c>ThrowOnBadRequest</c> says. This
    /// result is what the status-code hook puts on that otherwise empty response. The wording does not say
    /// JSON because a multipart route reaches it too.
    /// </remarks>
    public static IResult UnacceptedMediaTypeResult(HttpContext httpContext) =>
        BodyFaultResult(
            httpContext,
            StatusCodes.Status415UnsupportedMediaType,
            ErrorCodes.Validation.UnsupportedMediaType,
            UnacceptedMediaTypeMessage);

    private static IResult BodyFaultResult(HttpContext httpContext, int statusCode, string code, string message)
    {
        string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        return Results.Json(
            ApiResponse<bool>.FromResult(
                Result<bool>.Failure(new Error(code, message)),
                traceId),
            ArcanumJsonContext.Default.ApiResponseBoolean,
            statusCode: statusCode);
    }

    /// <summary>
    /// Picks the code and wording for one request-body fault from the status Kestrel chose.
    /// </summary>
    /// <remarks>
    /// The five statuses named below are the ones the framework raises for a fault detected while
    /// binding or reading a body, and each resolves back through <c>ArcanumErrorMapper</c> to the very
    /// status it was chosen for -- <c>ApiRequestJsonBodyFaultTests</c> asserts that round trip. A 400
    /// whose inner exception is a <see cref="JsonException"/> is the framework's own binder reporting a
    /// body that is not valid JSON for the parameter, and is worded the same as the routes that read
    /// the body themselves, so a route's answer does not depend on which of the two reads it. The default
    /// arm exists for a status not on that list: the response still carries the framework's status
    /// verbatim, and <see cref="ErrorCodes.Validation.InvalidBody"/> is the honest generic answer for
    /// "the body could not be read", but it is the one case where the mapper's status for the code
    /// (400) and the status on the response may differ. Naming a new status here rather than widening
    /// the default is what keeps that set empty.
    /// </remarks>
    private static (string Code, string Message) ResolveBodyFault(int statusCode, BadHttpRequestException? failure) =>
        statusCode switch
        {
            StatusCodes.Status400BadRequest when failure?.InnerException is JsonException =>
                (ErrorCodes.Validation.InvalidBody, MalformedJsonMessage),

            StatusCodes.Status400BadRequest when failure is not null && IsMissingBodyFault(failure) =>
                (ErrorCodes.Validation.InvalidBody, DefaultInvalidBodyMessage),

            StatusCodes.Status400BadRequest when failure is not null && IsParameterBindingFault(failure) =>
                (ErrorCodes.Validation.InvalidBody, ParameterBindingFailedMessage),

            StatusCodes.Status400BadRequest => (ErrorCodes.Validation.InvalidBody, IncompleteBodyMessage),

            StatusCodes.Status408RequestTimeout => (ErrorCodes.Validation.BodyReadTimeout, BodyReadTimeoutMessage),

            StatusCodes.Status413PayloadTooLarge => (ErrorCodes.Validation.BodyTooLarge, BodyTooLargeMessage),

            StatusCodes.Status415UnsupportedMediaType =>
                (ErrorCodes.Validation.UnsupportedMediaType, UnacceptedMediaTypeMessage),

            StatusCodes.Status431RequestHeaderFieldsTooLarge =>
                (ErrorCodes.Validation.RequestHeadersTooLarge, RequestHeadersTooLargeMessage),

            _ => (ErrorCodes.Validation.InvalidBody, UnreadableBodyMessage),
        };

    /// <summary>
    /// Whether the framework's binder raised this 400 because a body the route requires was never sent.
    /// </summary>
    /// <remarks>
    /// The binder says so in two ways: a required body parameter that was not provided "from body", and an
    /// inferred body with no body behind it. Both are a missing body, answered in the words a route that
    /// reads its body itself uses for one, not as a parameter that could not be converted.
    /// </remarks>
    private static bool IsMissingBodyFault(BadHttpRequestException failure) =>
        failure.InnerException is null
        && ((failure.Message.StartsWith("Required parameter", StringComparison.Ordinal)
                && failure.Message.EndsWith("was not provided from body.", StringComparison.Ordinal))
            || failure.Message.StartsWith("Implicit body inferred", StringComparison.Ordinal));

    /// <summary>
    /// Whether the framework's parameter binder, not a body read, raised this 400.
    /// </summary>
    /// <remarks>
    /// The binder reports a query, route or header value it could not convert, and a required one that
    /// was absent, with a 400 and no inner exception, and the wording below is what it has said since
    /// minimal APIs shipped. A missing body is told apart first (<see cref="IsMissingBodyFault"/>). Matching
    /// the wording is only a choice of which honest message to send: a wording change falls back to the
    /// body message, never to a different status or code.
    /// </remarks>
    private static bool IsParameterBindingFault(BadHttpRequestException failure) =>
        failure.InnerException is null
        && (failure.Message.StartsWith("Failed to bind parameter", StringComparison.Ordinal)
            || failure.Message.StartsWith("Required parameter", StringComparison.Ordinal));

    public static IResult UnsupportedMediaTypeResult(HttpContext httpContext)
    {
        string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        return Results.Json(
            ApiResponse<bool>.FromResult(
                Result<bool>.Failure(
                    new Error(ErrorCodes.Validation.UnsupportedMediaType, UnsupportedMediaTypeMessage)),
                traceId),
            ArcanumJsonContext.Default.ApiResponseBoolean,
            statusCode: StatusCodes.Status415UnsupportedMediaType);
    }

    public static IResult InvalidBodyResult<TResponse>(
        HttpContext httpContext,
        string message,
        JsonTypeInfo<ApiResponse<TResponse>> responseTypeInfo)
    {
        string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        return Results.Json(
            ApiResponse<TResponse>.FromResult(
                Result<TResponse>.Failure(new Error(ErrorCodes.Validation.InvalidBody, message)),
                traceId),
            responseTypeInfo,
            statusCode: StatusCodes.Status400BadRequest);
    }

    public static IResult InvalidBodyResult(
        HttpContext httpContext,
        string message)
    {
        return InvalidBodyResult(
            httpContext,
            message,
            ArcanumJsonContext.Default.ApiResponseBoolean);
    }
}
