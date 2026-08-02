using System.Diagnostics;
using System.Linq;
using System.Text.Json;
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
        apiGroup.MapGet("/config", (
            ConfigurationWriter writer,
            IOptionsSnapshot<ArcanumSettings> settings,
            HttpContext httpContext) =>
        {
            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            ArcanumSettings redacted = ConfigurationRedactor.Redact(
                ResolveCurrentSettings(writer, settings));

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

            (ArcanumSettings? request, IResult? jsonError) = await ReadAndValidateSettingsJsonAsync(
                httpContext,
                validator,
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

            Result<ArcanumSettings> writeResult = await writer.UpdateAsync(
                    ResolveCurrentSettings(writer, currentSettings),
                    async (latest, token) =>
                    {

                        ArcanumSettings merged = ConfigurationRedactor.MergeRedactedSecrets(
                            request,
                            latest);

                        // A residual endpoint mask after merge means a new provider could not be
                        // matched to current configuration; reject it instead of persisting the mask.
                        Result residualMask = ConfigurationRedactor.ValidateNoResidualMask(merged);

                        if (residualMask.IsFailure)
                        {

                            return Result<ArcanumSettings>.Failure(residualMask.Error);

                        }

                        Result outbound = await OutboundUrlGuard
                            .ValidateArcanumSettingsAsync(merged, token)
                            .ConfigureAwait(false);

                        if (outbound.IsFailure)
                        {

                            return Result<ArcanumSettings>.Failure(outbound.Error);

                        }

                        Result validation = validator.Validate(merged);

                        return validation.IsSuccess
                            ? Result<ArcanumSettings>.Success(merged)
                            : Result<ArcanumSettings>.Failure(validation.Error);

                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (writeResult.IsFailure)
            {

                if (IsConfigurationRequestFailure(writeResult.Error))
                {

                    Result<bool> invalid = Result<bool>.Failure(writeResult.Error);

                    return Results.BadRequest(
                        ApiResponse<bool>.FromResult(invalid, traceId));

                }

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
            ConfigurationWriter writer,
            ConfigurationValidator validator,
            IOptionsSnapshot<ArcanumSettings> currentSettings,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            (ArcanumSettings? request, IResult? jsonError) = await ReadAndValidateSettingsJsonAsync(
                httpContext,
                validator,
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

            ArcanumSettings merged = ConfigurationRedactor.MergeRedactedSecrets(
                request,
                ResolveCurrentSettings(writer, currentSettings));

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

            Result<bool> result = validation.IsSuccess
                ? Result<bool>.Success(true)
                : Result<bool>.Failure(validation.Error);

            return Results.Ok(ApiResponse<bool>.FromResult(result, traceId));
        })
        .WithName("ValidateConfiguration")
        .WithLargeRequestBody();

        apiGroup.MapGet("/models", (
            ConfigurationWriter writer,
            IOptionsSnapshot<ArcanumSettings> settings,
            HttpContext httpContext) =>
        {
            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            List<ModelInfoDto> models = ModelInfoBuilder.BuildModelInfoList(
                ResolveCurrentSettings(writer, settings));

            ApiResponse<ModelInfoDto[]> response = ApiResponse<ModelInfoDto[]>.FromResult(
                Result<ModelInfoDto[]>.Success(models.ToArray()),
                traceId);

            return Results.Ok(response);
        })
        .WithName("GetModels");

        apiGroup.MapGet("/providers", (
            ConfigurationWriter writer,
            IOptionsSnapshot<ArcanumSettings> settings,
            HttpContext httpContext) =>
        {
            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            ArcanumSettings current = ResolveCurrentSettings(writer, settings);

            ProviderInfoDto[] providers = (current.Providers ?? [])
                .Select(static p => new ProviderInfoDto(
                    p.Name,
                    p.Type.ToString(),
                    RedactRequired(p.Endpoint),
                    EnvironmentCredentialResolver
                        .GetProviderApiKeyEnvironmentVariableName(p),
                    p.Models.Select(static m => m.Name).ToArray(),
                    p.ContextWindowLimit))
                .ToArray();

            ApiResponse<ProviderInfoDto[]> response = ApiResponse<ProviderInfoDto[]>.FromResult(
                Result<ProviderInfoDto[]>.Success(providers),
                traceId);

            return Results.Ok(response);
        })
        .WithName("GetProviders");

        return apiGroup;
    }

    internal static ArcanumSettings ResolveCurrentSettings(
        ConfigurationWriter writer,
        IOptionsSnapshot<ArcanumSettings> startupSettings) =>
        writer.Latest ?? startupSettings.Value;

    private static bool IsConfigurationRequestFailure(Error error) =>
        error.Code == "Config.UnresolvedMask"
        || error.Code == "Configuration.ValidationFailed"
        || error.Code == ErrorCodes.Security.BlockedOutboundUrl;

    private static async Task<(ArcanumSettings? Request, IResult? Error)> ReadAndValidateSettingsJsonAsync(
        HttpContext httpContext,
        ConfigurationValidator validator,
        CancellationToken cancellationToken)
    {

        string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        JsonDocument document;

        try
        {

            document = await JsonDocument.ParseAsync(httpContext.Request.Body, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

        }
        catch (JsonException)
        {

            return (null, ApiRequestJson.InvalidBodyResult(
                httpContext,
                "Request body must be a valid ArcanumSettings JSON object."));

        }

        using (document)
        {

            Result rawTree = validator.RejectObsoleteJsonKeys(document.RootElement);

            if (rawTree.IsFailure)
            {

                return (null, Results.BadRequest(ApiResponse<bool>.FromResult(
                    Result<bool>.Failure(rawTree.Error),
                    traceId)));

            }

            ArcanumSettings? request;

            try
            {

                request = document.RootElement.Deserialize(ArcanumJsonContext.Default.ArcanumSettings);

            }
            catch (JsonException)
            {

                return (null, ApiRequestJson.InvalidBodyResult(
                    httpContext,
                    "Request body must be a valid ArcanumSettings JSON object."));

            }

            return (request, null);

        }

    }

    private static string RedactRequired(string value) =>
        string.IsNullOrEmpty(value) ? value : "***";

}
