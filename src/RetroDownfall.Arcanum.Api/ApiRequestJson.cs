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

    public static async ValueTask<(T? Body, IResult? Error)> ReadAsync<T>(
        HttpContext httpContext,
        JsonTypeInfo<T> typeInfo,
        Func<HttpContext, IResult> invalidJsonResult,
        CancellationToken cancellationToken)
    {

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
