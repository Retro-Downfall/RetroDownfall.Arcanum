using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Primitives;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.DataLifecycle;
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

        if (exception is BadHttpRequestException badRequest)
        {
            // Expected control flow, not a fault. The framework's parameter binder and Kestrel's body
            // reader raise this for a request the client got wrong -- a body that is not valid JSON for
            // the parameter, a missing Content-Type the generated reader checks, a body past the ceiling or
            // under the minimum data rate on a route that reads it itself -- and carry the status they chose.
            // AddArcanumApiServices turns RouteHandlerOptions.ThrowOnBadRequest on in every environment so a
            // bound route reaches this arm instead of the binder's own empty status outside Development, and
            // instead of the logged 500 below inside it. A bound route's too-large and too-slow faults never
            // get here (the generated reader records them as a status and returns), and neither does a
            // Content-Type the route does not declare it accepts (routing refuses that with a bare 415 before
            // the route is chosen); the status-code hook in UseArcanumExceptionHandler answers those.
            //
            // The line is Debug and carries only the status: the client's mistake is not the operator's
            // error, and the exception text is the framework's wording, which is not echoed back.
            logger.LogDebug("A request was refused before its handler ran ({StatusCode}).", badRequest.StatusCode);

            if (httpContext.Response.HasStarted)
            {
                return false;
            }

            IResult bodyFault = httpContext.Request.Path.StartsWithSegments("/v1", StringComparison.OrdinalIgnoreCase)
                ? OpenAiV1Endpoints.CreateRequestBodyReadErrorResult(badRequest.StatusCode)
                : ApiRequestJson.UnreadableBodyResult(httpContext, badRequest);

            await bodyFault.ExecuteAsync(httpContext).ConfigureAwait(false);

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

        if (exception is LabeledArtifactRefusalException refusal
            && !httpContext.Request.Path.StartsWithSegments("/v1", StringComparison.OrdinalIgnoreCase))
        {
            // Expected control flow, not a fault. A raw delete that returns no Result of its own asked the
            // labelled-artifact guard inside its own transaction and was refused: the artifact is labelled,
            // or the label table cannot be read. The refusal carries the guard's own Error, so the status
            // follows its code (403 for a labelled artifact, 503 for an unreadable table) wherever the
            // delete was reached, a route that dispatched the purge first or one that never could.
            //
            // The line is Debug and carries only the code: the guard's message names the boundary and never
            // the artifact, and a refusal is the answer the installation chose, not an error to page on.
            logger.LogDebug("A raw delete was refused by the labelled-artifact guard ({Code}).", refusal.Error.Code);

            if (httpContext.Response.HasStarted)
            {
                return false;
            }

            httpContext.Response.StatusCode = ArcanumErrorMapper.ResolveStatusCode(refusal.Error.Code);

            httpContext.Response.ContentType = "application/json";

            ApiResponse<string> refused = ApiResponse<string>.FromResult(Result<string>.Failure(refusal.Error), traceId);

            await httpContext.Response
                .WriteAsJsonAsync(refused, ArcanumJsonContext.Default.ApiResponseString, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return true;
        }

        // A route that binds its body as a handler parameter reads it in framework-generated code, so unlike
        // the routes that read it themselves it has no pre-check to answer a charset the read cannot decode:
        // ReadFromJsonAsync raises InvalidOperationException for it. That exception is the caller's only
        // when the request itself is JSON-typed with an unreadable charset and carries a body to read; an
        // InvalidOperationException on any other request is a fault of the server's own and falls through to
        // the logged 500 below. The reader raises the exact type, so a type derived from it -- the
        // maintenance and labelled-artifact refusals above are two -- is never mistaken for it, whatever
        // Content-Type the request that hit it happened to carry.
        if (exception.GetType() == typeof(InvalidOperationException)
            && ApiRequestJson.CarriesABody(httpContext.Request)
            && ApiRequestJson.IsJsonWithAnUnreadableCharset(httpContext.Request))
        {
            logger.LogDebug("A request was refused because its JSON Content-Type names a charset that cannot be decoded.");

            if (httpContext.Response.HasStarted)
            {
                return false;
            }

            IResult unreadableCharset = httpContext.Request.Path.StartsWithSegments("/v1", StringComparison.OrdinalIgnoreCase)
                ? OpenAiV1Endpoints.CreateRequestBodyReadErrorResult(StatusCodes.Status415UnsupportedMediaType)
                : ApiRequestJson.UnsupportedMediaTypeResult(httpContext);

            await unreadableCharset.ExecuteAsync(httpContext).ConfigureAwait(false);

            return true;
        }

        // No JsonException that reaches this handler is the request body's. Every reader of a request body
        // answers its own: ApiRequestJson.ReadAsync and the routes that read by hand catch it at the read,
        // and the generated binder wraps it in the BadHttpRequestException answered above. One that gets
        // here was raised after the body was read, or on a request that never had one -- corrupt data the
        // server loaded or a payload it built -- and is a fault to log, not a malformed request to explain.

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
