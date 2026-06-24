using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Configuration;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Api.Configuration;

internal static class ConfigurationEndpoints
{

    public static RouteGroupBuilder MapConfigurationEndpoints(this RouteGroupBuilder apiGroup)
    {

        apiGroup.MapGet("/config", (IOptionsSnapshot<ArcanumSettings> settings, HttpContext httpContext) =>
        {
            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            ArcanumSettings redacted = ConfigurationRedactor.Redact(settings.Value);

            Result<ArcanumSettings> settingsResult = redacted;

            ApiResponse<ArcanumSettings> response = ApiResponse<ArcanumSettings>.FromResult(settingsResult, traceId);

            return Results.Ok(response);
        })
        .WithName("GetConfiguration");

        apiGroup.MapPut("/config", async (
            ConfigurationWriter writer,
            ConfigurationValidator validator,
            IOptionsSnapshot<ArcanumSettings> currentSettings,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            ArcanumSettings? request;

            IResult? jsonError;

            (request, jsonError) = await ApiRequestJson.ReadAsync(
                httpContext,
                ArcanumJsonContext.Default.ArcanumSettings,
                static ctx => ApiRequestJson.InvalidBodyResult(
                    ctx,
                    "Request body must be a valid ArcanumSettings JSON object."),
                cancellationToken).ConfigureAwait(false);

            if (jsonError is not null)
            {
                return jsonError;
            }

            if (request is null)
            {
                Result<bool> invalid = Result<bool>.Failure(
                    new Error("Validation.InvalidBody", "Request body must be a valid ArcanumSettings JSON object."));

                return Results.BadRequest(ApiResponse<bool>.FromResult(invalid, traceId));
            }

            ArcanumSettings merged = ConfigurationRedactor.MergeApiKeys(request, currentSettings.Value);

            Result outbound = await OutboundUrlGuard.ValidateArcanumSettingsAsync(merged, cancellationToken).ConfigureAwait(false);

            if (outbound.IsFailure)
            {
                Result<bool> invalid = Result<bool>.Failure(outbound.Error);

                return Results.BadRequest(ApiResponse<bool>.FromResult(invalid, traceId));
            }

            Result validation = validator.Validate(merged);

            if (validation.IsFailure)
            {
                Result<bool> invalid = Result<bool>.Failure(validation.Error);

                return Results.BadRequest(ApiResponse<bool>.FromResult(invalid, traceId));
            }

            Result writeResult = await writer.WriteAsync(merged, httpContext.RequestAborted).ConfigureAwait(false);

            if (writeResult.IsFailure)
            {
                return Results.Json(
                    ApiResponse<bool>.FromResult(Result<bool>.Failure(writeResult.Error), traceId),
                    ArcanumJsonContext.Default.ApiResponseBoolean,
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            return Results.Ok(ApiResponse<bool>.FromResult(Result<bool>.Success(true), traceId));
        })
        .WithName("UpdateConfiguration")
        .WithLargeRequestBody();

        apiGroup.MapPost("/config/validate", async (
            ConfigurationValidator validator,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            ArcanumSettings? request;

            IResult? jsonError;

            (request, jsonError) = await ApiRequestJson.ReadAsync(
                httpContext,
                ArcanumJsonContext.Default.ArcanumSettings,
                static ctx => ApiRequestJson.InvalidBodyResult(
                    ctx,
                    "Request body must be a valid ArcanumSettings JSON object."),
                cancellationToken).ConfigureAwait(false);

            if (jsonError is not null)
            {
                return jsonError;
            }

            if (request is null)
            {
                Result<bool> invalid = Result<bool>.Failure(
                    new Error("Validation.InvalidBody", "Request body must be a valid ArcanumSettings JSON object."));

                return Results.BadRequest(ApiResponse<bool>.FromResult(invalid, traceId));
            }

            Result outbound = await OutboundUrlGuard.ValidateArcanumSettingsAsync(request, cancellationToken).ConfigureAwait(false);

            if (outbound.IsFailure)
            {
                Result<bool> invalid = Result<bool>.Failure(outbound.Error);

                return Results.BadRequest(ApiResponse<bool>.FromResult(invalid, traceId));
            }

            Result validation = validator.Validate(request);

            Result<bool> result = validation.IsSuccess
                ? Result<bool>.Success(true)
                : Result<bool>.Failure(validation.Error);

            return Results.Ok(ApiResponse<bool>.FromResult(result, traceId));
        })
        .WithName("ValidateConfiguration")
        .WithLargeRequestBody();

        return apiGroup;
    }

}
