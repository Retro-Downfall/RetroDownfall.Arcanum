using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Api.TheForge;

internal static class SanctumEndpoints
{

    public static RouteGroupBuilder MapSanctumEndpoints(this RouteGroupBuilder apiGroup)
    {
        apiGroup.MapGet(
            "/campaigns/{campaignId:guid}/sanctum",
            async (Guid campaignId, ICampaignRepository repo, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Campaign? campaign = await repo.GetByIdAsync(campaignId, ctx.RequestAborted).ConfigureAwait(false);

                if (campaign is null)
                {
                    return Results.Json(
                        ApiResponse<SanctumConfig>.FromResult(
                            Result<SanctumConfig>.Failure(new Error(ErrorCodes.Campaign.NotFound, "Campaign was not found.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseSanctumConfig,
                        statusCode: StatusCodes.Status404NotFound);
                }

                SanctumConfig config = CampaignRepository.GetSanctumConfig(campaign);

                return Results.Ok(
                    ApiResponse<SanctumConfig>.FromResult(
                        Result<SanctumConfig>.Success(config),
                        traceId));
            })
        .WithName("GetCampaignSanctum");

        apiGroup.MapPut(
            "/campaigns/{campaignId:guid}/sanctum",
            async (
                Guid campaignId,
                SanctumConfig? request,
                ICampaignRepository repo,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (request is null)
                {

                    return Results.BadRequest(
                        ApiResponse<SanctumConfig>.FromResult(
                            Result<SanctumConfig>.Failure(
                                new Error(ErrorCodes.Validation.InvalidBody, ApiRequestJson.DefaultInvalidBodyMessage)),
                            traceId));

                }

                Campaign? campaign = await repo.GetByIdAsync(campaignId, ctx.RequestAborted).ConfigureAwait(false);

                if (campaign is null)
                {
                    return Results.Json(
                        ApiResponse<SanctumConfig>.FromResult(
                            Result<SanctumConfig>.Failure(new Error(ErrorCodes.Campaign.NotFound, "Campaign was not found.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseSanctumConfig,
                        statusCode: StatusCodes.Status404NotFound);
                }

                Result<SanctumConfig> validation = ValidateAndClampSanctumConfig(request);

                if (validation.IsFailure)
                {
                    return Results.BadRequest(
                        ApiResponse<SanctumConfig>.FromResult(validation, traceId));
                }

                SanctumConfig clamped = validation.Value!;

                CampaignRepository.SetSanctumConfig(campaign, clamped);

                campaign.UpdatedAt = DateTimeOffset.UtcNow;

                await repo.UpdateAsync(campaign, ctx.RequestAborted).ConfigureAwait(false);

                return Results.Ok(
                    ApiResponse<SanctumConfig>.FromResult(
                        Result<SanctumConfig>.Success(clamped),
                        traceId));
            })
        .WithName("UpdateCampaignSanctum")
        .WithLargeRequestBody();

        apiGroup.MapGet(
            "/campaigns/{campaignId:guid}/sanctum/breaches",
            async (
                Guid campaignId,
                int? limit,
                DateTimeOffset? before,
                string? tool,
                ICampaignRepository repo,
                ISanctumBreachRepository breachRepository,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Campaign? campaign = await repo.GetByIdAsync(campaignId, ctx.RequestAborted).ConfigureAwait(false);

                if (campaign is null)
                {
                    return Results.Json(
                        ApiResponse<SanctumBreachQueryResult>.FromResult(
                            Result<SanctumBreachQueryResult>.Failure(new Error(ErrorCodes.Campaign.NotFound, "Campaign was not found.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseSanctumBreachQueryResult,
                        statusCode: StatusCodes.Status404NotFound);
                }

                int queryLimit = ArcanumSettingClamps.SanctumBreachQueryLimit(limit ?? 100);

                // Fetch one extra row to detect whether more history exists beyond this page.
                IReadOnlyList<SanctumBreachRecord> records = await breachRepository
                    .QueryAsync(campaignId.ToString(), queryLimit + 1, before, tool, ctx.RequestAborted)
                    .ConfigureAwait(false);

                bool hasMore = records.Count > queryLimit;

                SanctumBreachDto[] items = records
                    .Take(queryLimit)
                    .Select(ToBreachDto)
                    .ToArray();

                SanctumBreachQueryResult payload = new(items, hasMore);

                return Results.Ok(
                    ApiResponse<SanctumBreachQueryResult>.FromResult(
                        Result<SanctumBreachQueryResult>.Success(payload),
                        traceId));
            })
        .WithName("GetCampaignSanctumBreaches");

        return apiGroup;
    }

    private static SanctumBreachDto ToBreachDto(SanctumBreachRecord record) =>
        new(
            record.Id,
            record.OccurredAt,
            record.ToolName,
            record.BreachType,
            record.Description,
            SanctumPathRedactor.Redact(record.Details?.RequestedPath),
            SanctumPathRedactor.Redact(record.Details?.ResolvedPath),
            SanctumPathRedactor.Redact(record.Details?.WorkspaceRoot),
            record.Details?.RequestedUrl,
            record.Details?.ToolArguments,
            record.Details?.LimitValue,
            record.Details?.ActualValue);

    private static Result<SanctumConfig> ValidateAndClampSanctumConfig(SanctumConfig request)
    {
        if (request.NetworkPolicy == NetworkPolicy.AllowList && request.AllowedDomains.Count == 0)
        {
            return Result<SanctumConfig>.Failure(
                new Error(
                    ErrorCodes.Sanctum.InvalidConfig,
                    "AllowedDomains must not be empty when NetworkPolicy is AllowList."));
        }

        foreach (string path in request.AllowedPaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path.Trim()))
            {
                return Result<SanctumConfig>.Failure(
                    new Error(
                        ErrorCodes.Sanctum.InvalidConfig,
                        "Each AllowedPaths entry must be an absolute path."));
            }
        }

        ResourceLimits limits = request.ResourceLimits;

        ResourceLimits clampedLimits = limits with
        {
            MaxProcessMemoryMb = ArcanumSettingClamps.SanctumMaxProcessMemoryMb(limits.MaxProcessMemoryMb),
            MaxProcessCount = ArcanumSettingClamps.SanctumMaxProcessCount(limits.MaxProcessCount),
            MaxFileWriteMb = ArcanumSettingClamps.SanctumMaxFileWriteMb(limits.MaxFileWriteMb),
            ProcessTimeoutSeconds = ArcanumSettingClamps.SanctumProcessTimeoutSeconds(limits.ProcessTimeoutSeconds),
        };

        SanctumConfig clamped = request with
        {
            ResourceLimits = clampedLimits,
            MaxBreachCount = ArcanumSettingClamps.SanctumMaxBreachCount(request.MaxBreachCount),
        };

        return Result<SanctumConfig>.Success(clamped);
    }

}
