using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api;

internal static class ApiRequestJson
{

    public const string DefaultInvalidBodyMessage = "Request body is required.";

    public const string MalformedJsonMessage = "Request body could not be parsed as valid JSON.";

    public const string UnsupportedMediaTypeMessage =
        "Request body must be sent with 'Content-Type: application/json'.";

    public const string IncompleteBodyMessage = "Request body could not be read to completion.";

    public const string BodyTooLargeMessage = "Request body exceeded the maximum size this server accepts.";

    public const string BodyReadTimeoutMessage = "Request body arrived too slowly and the server stopped waiting for it.";

    public const string RequestHeadersTooLargeMessage =
        "Request headers or trailers exceeded the total size this server accepts.";

    public const string UnreadableBodyMessage = "Request body could not be read.";

    public static async ValueTask<(T? Body, IResult? Error)> ReadAsync<T>(
        HttpContext httpContext,
        JsonTypeInfo<T> typeInfo,
        Func<HttpContext, IResult> invalidJsonResult,
        CancellationToken cancellationToken)
    {

        // ReadFromJsonAsync throws InvalidOperationException — not JsonException — for a missing or
        // non-JSON Content-Type. Left uncaught it escapes to ArcanumExceptionHandler and a routine client
        // mistake becomes a 500 Hub.Unhandled with an Error-level stack trace.
        if (!httpContext.Request.HasJsonContentType())
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
        catch (InvalidOperationException)
        {

            return (default, UnsupportedMediaTypeResult(httpContext));

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

        string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        (string code, string message) = ResolveBodyFault(failure.StatusCode);

        return Results.Json(
            ApiResponse<bool>.FromResult(
                Result<bool>.Failure(new Error(code, message)),
                traceId),
            ArcanumJsonContext.Default.ApiResponseBoolean,
            statusCode: failure.StatusCode);

    }

    /// <summary>
    /// Picks the code and wording for one request-body fault from the status Kestrel chose.
    /// </summary>
    /// <remarks>
    /// The four statuses named below are the ones Kestrel raises for a fault detected while reading a
    /// body, and each resolves back through <c>ArcanumErrorMapper</c> to the very status it was chosen
    /// for -- <c>ApiRequestJsonBodyFaultTests</c> asserts that round trip. The default arm exists for a
    /// status not on that list: the response still carries Kestrel's status verbatim, and
    /// <see cref="ErrorCodes.Validation.InvalidBody"/> is the honest generic answer for "the body could
    /// not be read", but it is the one case where the mapper's status for the code (400) and the status
    /// on the response may differ. Naming a new status here rather than widening the default is what
    /// keeps that set empty.
    /// </remarks>
    private static (string Code, string Message) ResolveBodyFault(int statusCode) =>
        statusCode switch
        {

            StatusCodes.Status400BadRequest => (ErrorCodes.Validation.InvalidBody, IncompleteBodyMessage),

            StatusCodes.Status408RequestTimeout => (ErrorCodes.Validation.BodyReadTimeout, BodyReadTimeoutMessage),

            StatusCodes.Status413PayloadTooLarge => (ErrorCodes.Validation.BodyTooLarge, BodyTooLargeMessage),

            StatusCodes.Status431RequestHeaderFieldsTooLarge =>
                (ErrorCodes.Validation.RequestHeadersTooLarge, RequestHeadersTooLargeMessage),

            _ => (ErrorCodes.Validation.InvalidBody, UnreadableBodyMessage),

        };

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
