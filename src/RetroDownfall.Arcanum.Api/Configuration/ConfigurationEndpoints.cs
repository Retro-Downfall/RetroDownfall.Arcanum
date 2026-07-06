using System.Diagnostics;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence;
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
                    new Error(ErrorCodes.Validation.InvalidBody, "Request body must be a valid ArcanumSettings JSON object."));

                return Results.BadRequest(ApiResponse<bool>.FromResult(invalid, traceId));
            }

            ArcanumSettings merged = ConfigurationRedactor.MergeApiKeys(request, currentSettings.Value);

            // W3.5: a residual "***" after merge means a new provider / model-map key whose masked
            // value could not be restored — reject it instead of persisting the literal mask.
            Result residualMask = ConfigurationRedactor.ValidateNoResidualMask(merged);

            if (residualMask.IsFailure)
            {
                Result<bool> invalid = Result<bool>.Failure(residualMask.Error);

                return Results.BadRequest(ApiResponse<bool>.FromResult(invalid, traceId));
            }

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
                    new Error(ErrorCodes.Validation.InvalidBody, "Request body must be a valid ArcanumSettings JSON object."));

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

        apiGroup.MapGet("/models", (IOptionsSnapshot<ArcanumSettings> settings, HttpContext httpContext) =>
        {
            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            List<ModelInfoDto> models = ModelInfoBuilder.BuildModelInfoList(settings.Value);

            ApiResponse<ModelInfoDto[]> response = ApiResponse<ModelInfoDto[]>.FromResult(
                Result<ModelInfoDto[]>.Success(models.ToArray()),
                traceId);

            return Results.Ok(response);
        })
        .WithName("GetModels");

        apiGroup.MapGet("/providers", (IOptionsSnapshot<ArcanumSettings> settings, HttpContext httpContext) =>
        {
            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            ProviderInfoDto[] providers = (settings.Value.Providers ?? [])
                .Select(static p => new ProviderInfoDto(
                    p.Name,
                    p.Type.ToString(),
                    RedactRequired(p.Endpoint),
                    RedactOptional(p.ApiKey),
                    p.Models.Select(static m => m.Name).ToArray(),
                    p.ContextWindowLimit,
                    p.LlamaCpp?.ModelMap is { Count: > 0 }))
                .ToArray();

            ApiResponse<ProviderInfoDto[]> response = ApiResponse<ProviderInfoDto[]>.FromResult(
                Result<ProviderInfoDto[]>.Success(providers),
                traceId);

            return Results.Ok(response);
        })
        .WithName("GetProviders");

        return apiGroup;
    }

    private static string RedactRequired(string value) =>
        string.IsNullOrEmpty(value) ? value : "***";

    private static string? RedactOptional(string? value) =>
        string.IsNullOrEmpty(value) ? value : "***";
}
