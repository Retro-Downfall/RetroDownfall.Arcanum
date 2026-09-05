using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

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
                Result<bool>.Failure(new Error(ErrorCodes.Validation.InvalidBody, ApiRequestJson.MalformedJsonMessage)),
                traceId);

            await httpContext.Response
                .WriteAsJsonAsync(invalidBody, ArcanumJsonContext.Default.ApiResponseBoolean, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return true;

        }

        if (exception is GrimoireMaintenanceUnavailableException)
        {

            // Expected control flow, not a fault. Admission refuses what arrives after a transition
            // begins; this is the request that was already in flight when admission closed under it,
            // and it deserves the same answer rather than "Arcanum broke".
            //
            // The line is Debug, carries no path and no exception object, and is written before the
            // response-started check so a refusal that cannot be written is still observable. Logging
            // it at Error would put the request path into a sink on every scrape of an endpoint that
            // reads the database, for the whole of a planned window.
            logger.LogDebug("A request was refused because Grimoire maintenance owns connection admission.");

            _ = await GrimoireMaintenanceRefusal.TryWriteAsync(httpContext).ConfigureAwait(false);

            // Handled either way, and the return value says so even when nothing could be written.
            // Reporting false hands the exception back to the framework's own exception middleware,
            // which logs it at Error with the request path before rethrowing - which is precisely the
            // pair this arm exists to avoid, and it would happen on exactly the requests that had
            // already begun a response when admission closed under them. A response whose first byte
            // has left is finished by its own writer; there is nothing further to say about it.
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
            new Error(ErrorCodes.Hub.Unhandled, "An internal error occurred."),
            traceId);

        await httpContext.Response
            .WriteAsJsonAsync(body, ArcanumJsonContext.Default.ApiResponseString, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return true;

    }

}
