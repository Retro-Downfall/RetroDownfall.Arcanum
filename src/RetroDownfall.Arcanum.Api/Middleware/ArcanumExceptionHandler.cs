using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Middleware;

[ExcludeFromCodeCoverage] // Reason: ASP.NET exception-handler glue; exercised via integration tests and fault injection.
public sealed class ArcanumExceptionHandler(ILogger<ArcanumExceptionHandler> logger) : IExceptionHandler
{

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {

        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {

            return false;

        }

        string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        if (exception is JsonException)
        {

            if (httpContext.Response.HasStarted)
            {

                return false;

            }

            if (httpContext.Request.Path.StartsWithSegments("/v1", StringComparison.OrdinalIgnoreCase))
            {

                IResult openAiJsonError = OpenAiV1Endpoints.CreateInvalidJsonErrorResult();

                await openAiJsonError.ExecuteAsync(httpContext).ConfigureAwait(false);

                return true;

            }

            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;

            httpContext.Response.ContentType = "application/json";

            ApiResponse<bool> invalidBody = ApiResponse<bool>.FromResult(
                Result<bool>.Failure(new Error("Validation.InvalidBody", ApiRequestJson.MalformedJsonMessage)),
                traceId);

            await httpContext.Response
                .WriteAsJsonAsync(invalidBody, ArcanumJsonContext.Default.ApiResponseBoolean, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return true;

        }

        logger.LogError(
            exception,
            "Unhandled exception on {Method} {Path} (TraceId={TraceId})",
            httpContext.Request.Method,
            httpContext.Request.Path,
            traceId);

        if (httpContext.Response.HasStarted)
        {

            return false;

        }

        if (httpContext.Request.Path.StartsWithSegments("/v1", StringComparison.OrdinalIgnoreCase))
        {

            if (httpContext.Response.HasStarted)
            {

                return false;

            }

            IResult openAiError = OpenAiV1Endpoints.CreateUnhandledInferenceErrorResult();

            await openAiError.ExecuteAsync(httpContext).ConfigureAwait(false);

            return true;

        }

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

        httpContext.Response.ContentType = "application/json";

        ApiResponse<string> body = new(
            null,
            false,
            new Error("Hub.Unhandled", "An internal error occurred."),
            traceId);

        await httpContext.Response
            .WriteAsJsonAsync(body, ArcanumJsonContext.Default.ApiResponseString, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return true;

    }

}
