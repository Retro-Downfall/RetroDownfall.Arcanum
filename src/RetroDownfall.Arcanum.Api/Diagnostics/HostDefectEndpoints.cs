using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;

namespace RetroDownfall.Arcanum.Api.Diagnostics;

internal static class HostDefectEndpoints
{
    internal static void AddHostDefectDiagnostics(this IServiceCollection services)
    {
        services.AddSingleton<IHostDefectProbe, OllamaContinuationDefectProbe>();
        services.AddSingleton<GitHubHostDefectTracker>();
        services.AddSingleton<HostDefectDiagnostics>();

        services.AddHttpClient(GitHubHostDefectTracker.ClientName,
            client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
    }

    internal static void MapHostDefectEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/diagnostics/host-defects/ollama-continuation", DiagnoseAsync).WithName("PostOllamaContinuationDiagnostic");
        api.MapGet("/diagnostics/host-defects/{id:guid}", ReadAsync).WithName("GetHostDefectDiagnostic");
        api.MapPost("/diagnostics/host-defects/{id:guid}/tracker", TrackAsync).WithName("PostHostDefectTracker");
    }

    private static async Task<IResult> DiagnoseAsync(HostDefectProbeRequest request, HttpContext context,
        [FromServices] ApiKeyAuthenticator authenticator, [FromServices] HostDefectDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        if (!await authenticator.IsOperatorAuthorizedAsync(context).ConfigureAwait(false)) return Denied(context);

        return Reply(await diagnostics.DiagnoseAsync(request, context.RequestServices.GetService<IToolInvocationStore>(),
            cancellationToken).ConfigureAwait(false), context);
    }

    private static async Task<IResult> ReadAsync(Guid id, HttpContext context, [FromServices] ApiKeyAuthenticator authenticator,
        [FromServices] HostDefectDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        if (!await authenticator.IsOperatorAuthorizedAsync(context).ConfigureAwait(false)) return Denied(context);

        return Reply(await diagnostics.ReadAsync(id, cancellationToken).ConfigureAwait(false), context);
    }

    private static async Task<IResult> TrackAsync(Guid id, HostDefectTrackRequest request, HttpContext context,
        [FromServices] ApiKeyAuthenticator authenticator, [FromServices] HostDefectDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        if (!await authenticator.IsOperatorAuthorizedAsync(context).ConfigureAwait(false)) return Denied(context);

        return Reply(await diagnostics.TrackAsync(id, cancellationToken).ConfigureAwait(false), context);
    }

    private static IResult Denied(HttpContext context) =>
        Results.Json(ApiResponse<HostDefectRecord>.FromResult(
            Result<HostDefectRecord>.Failure(new Error(ErrorCodes.Auth.Unauthorized, "Host diagnostics require operator authorization.")),
            context.TraceIdentifier), ArcanumJsonContext.Default.ApiResponseHostDefectRecord, statusCode: StatusCodes.Status401Unauthorized);

    private static IResult Reply(Result<HostDefectRecord> result, HttpContext context)
    {
        int status = result.IsSuccess ? StatusCodes.Status200OK : result.Error.Code switch
        {
            ErrorCodes.Validation.InvalidBody => StatusCodes.Status400BadRequest,
            HostDefectDiagnostics.Missing => StatusCodes.Status404NotFound,
            HostDefectDiagnostics.Unconfirmed => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status503ServiceUnavailable,
        };

        return Results.Json(ApiResponse<HostDefectRecord>.FromResult(result, context.TraceIdentifier),
            ArcanumJsonContext.Default.ApiResponseHostDefectRecord, statusCode: status);
    }
}
