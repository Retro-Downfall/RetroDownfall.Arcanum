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

            // Kestrel raises this for a body that ends early and for one past the size ceiling, and it
            // is neither a JsonException nor an InvalidOperationException. Uncaught it escapes to
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
    /// has already distinguished the two cases that matter to a client: 413 for a body past the ceiling,
    /// 400 for one that ended early. Only the wording is ours -- the framework's own message is not
    /// echoed back.
    /// </remarks>
    public static IResult UnreadableBodyResult(HttpContext httpContext, BadHttpRequestException failure)
    {

        string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        string message = failure.StatusCode == StatusCodes.Status413PayloadTooLarge
            ? BodyTooLargeMessage
            : IncompleteBodyMessage;

        return Results.Json(
            ApiResponse<bool>.FromResult(
                Result<bool>.Failure(new Error(ErrorCodes.Validation.InvalidBody, message)),
                traceId),
            ArcanumJsonContext.Default.ApiResponseBoolean,
            statusCode: failure.StatusCode);

    }

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
