using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
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
                            Result<SanctumConfig>.Failure(new Error("Campaign.NotFound", "Campaign was not found.")),
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
                SanctumConfig request,
                ICampaignRepository repo,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Campaign? campaign = await repo.GetByIdAsync(campaignId, ctx.RequestAborted).ConfigureAwait(false);

                if (campaign is null)
                {
                    return Results.Json(
                        ApiResponse<SanctumConfig>.FromResult(
                            Result<SanctumConfig>.Failure(new Error("Campaign.NotFound", "Campaign was not found.")),
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
        .WithName("UpdateCampaignSanctum");

        apiGroup.MapGet(
            "/campaigns/{campaignId:guid}/sanctum/breaches",
            async (
                Guid campaignId,
                int? limit,
                ICampaignRepository repo,
                ISanctumGuard sanctumGuard,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Campaign? campaign = await repo.GetByIdAsync(campaignId, ctx.RequestAborted).ConfigureAwait(false);

                if (campaign is null)
                {
                    return Results.Json(
                        ApiResponse<SanctumBreach[]>.FromResult(
                            Result<SanctumBreach[]>.Failure(new Error("Campaign.NotFound", "Campaign was not found.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseSanctumBreachArray,
                        statusCode: StatusCodes.Status404NotFound);
                }

                int queryLimit = ArcanumSettingClamps.SanctumBreachQueryLimit(limit ?? 100);

                IReadOnlyList<SanctumBreach> breaches = await sanctumGuard
                    .GetBreachesAsync(campaignId.ToString(), queryLimit, ctx.RequestAborted)
                    .ConfigureAwait(false);

                SanctumBreach[] payload = breaches.ToArray();

                return Results.Ok(
                    ApiResponse<SanctumBreach[]>.FromResult(
                        Result<SanctumBreach[]>.Success(payload),
                        traceId));
            })
        .WithName("GetCampaignSanctumBreaches");

        return apiGroup;
    }

    private static Result<SanctumConfig> ValidateAndClampSanctumConfig(SanctumConfig request)
    {
        if (request.NetworkPolicy == NetworkPolicy.AllowList && request.AllowedDomains.Count == 0)
        {
            return Result<SanctumConfig>.Failure(
                new Error(
                    "Sanctum.InvalidConfig",
                    "AllowedDomains must not be empty when NetworkPolicy is AllowList."));
        }

        foreach (string path in request.AllowedPaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path.Trim()))
            {
                return Result<SanctumConfig>.Failure(
                    new Error(
                        "Sanctum.InvalidConfig",
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

        SanctumConfig clamped = request with { ResourceLimits = clampedLimits };

        return Result<SanctumConfig>.Success(clamped);
    }

}
