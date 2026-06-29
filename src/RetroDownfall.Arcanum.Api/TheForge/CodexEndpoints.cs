using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Api.TheForge;

internal static class CodexEndpoints
{

    public static RouteGroupBuilder MapCodexEndpoints(this RouteGroupBuilder apiGroup)
    {
        apiGroup.MapGet(
            "/campaigns/{id:guid}/codex",
            async (Guid id, ICampaignRepository repo, IOptionsSnapshot<ArcanumSettings> settings, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Campaign? campaign = await repo.GetByIdAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                if (campaign is null)
                {
                    Result<CodexContentDto> notFound = Result<CodexContentDto>.Failure(
                        new Error("Campaign.NotFound", "No campaign exists with that identifier."));

                    return Results.Json(
                        ApiResponse<CodexContentDto>.FromResult(notFound, traceId),
                        ArcanumJsonContext.Default.ApiResponseCodexContentDto,
                        statusCode: StatusCodes.Status404NotFound);
                }

                CodexContentDto dto = await ReadCodexDtoAsync(Path.Combine(campaign.Path, "CODEX.md"), settings, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return Results.Ok(ApiResponse<CodexContentDto>.FromResult(Result<CodexContentDto>.Success(dto), traceId));
            })
        .WithName("GetCampaignCodex");

        apiGroup.MapPut(
            "/campaigns/{id:guid}/codex",
            async (
                Guid id,
                CodexPutRequest? body,
                ICampaignRepository repo,
                IOptionsSnapshot<ArcanumSettings> settings,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (body is null)
                {
                    return Results.BadRequest(
                        ApiResponse<CodexContentDto>.FromResult(
                            Result<CodexContentDto>.Failure(new Error("Validation.InvalidBody", "Request body is required.")),
                            traceId));
                }

                Campaign? campaign = await repo.GetByIdAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                if (campaign is null)
                {
                    Result<CodexContentDto> notFound = Result<CodexContentDto>.Failure(
                        new Error("Campaign.NotFound", "No campaign exists with that identifier."));

                    return Results.Json(
                        ApiResponse<CodexContentDto>.FromResult(notFound, traceId),
                        ArcanumJsonContext.Default.ApiResponseCodexContentDto,
                        statusCode: StatusCodes.Status404NotFound);
                }

                string codexPath = Path.Combine(campaign.Path, "CODEX.md");

                IResult? writeResult = await WriteCodexAsync(
                    codexPath,
                    body.Content,
                    settings,
                    traceId,
                    ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (writeResult is not null)
                {
                    return writeResult;
                }

                CodexContentDto dto = await ReadCodexDtoAsync(codexPath, settings, ctx.RequestAborted).ConfigureAwait(false);

                return Results.Ok(ApiResponse<CodexContentDto>.FromResult(Result<CodexContentDto>.Success(dto), traceId));
            })
        .WithName("PutCampaignCodex")
        .WithLargeRequestBody();

        apiGroup.MapDelete(
            "/campaigns/{id:guid}/codex",
            async (Guid id, ICampaignRepository repo, HttpContext ctx) =>
            {
                Campaign? campaign = await repo.GetByIdAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                if (campaign is null)
                {
                    string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                    return Results.Json(
                        ApiResponse<CodexContentDto>.FromResult(
                            Result<CodexContentDto>.Failure(new Error("Campaign.NotFound", "No campaign exists with that identifier.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseCodexContentDto,
                        statusCode: StatusCodes.Status404NotFound);
                }

                string codexPath = Path.Combine(campaign.Path, "CODEX.md");

                if (File.Exists(codexPath))
                {
                    File.Delete(codexPath);
                }

                return Results.NoContent();
            })
        .WithName("DeleteCampaignCodex");

        apiGroup.MapGet(
            "/codex",
            async (HttpContext ctx, IOptionsSnapshot<ArcanumSettings> settings) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                string globalPath = Path.Combine(ArcanumPaths.GrimoireDirectory, "CODEX.md");

                CodexContentDto dto = await ReadCodexDtoAsync(globalPath, settings, ctx.RequestAborted).ConfigureAwait(false);

                return Results.Ok(ApiResponse<CodexContentDto>.FromResult(Result<CodexContentDto>.Success(dto), traceId));
            })
        .WithName("GetGlobalCodex");

        apiGroup.MapPut(
            "/codex",
            async (CodexPutRequest? body, IOptionsSnapshot<ArcanumSettings> settings, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (body is null)
                {
                    return Results.BadRequest(
                        ApiResponse<CodexContentDto>.FromResult(
                            Result<CodexContentDto>.Failure(new Error("Validation.InvalidBody", "Request body is required.")),
                            traceId));
                }

                string globalPath = Path.Combine(ArcanumPaths.GrimoireDirectory, "CODEX.md");

                IResult? writeResult = await WriteCodexAsync(globalPath, body.Content, settings, traceId, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (writeResult is not null)
                {
                    return writeResult;
                }

                CodexContentDto dto = await ReadCodexDtoAsync(globalPath, settings, ctx.RequestAborted).ConfigureAwait(false);

                return Results.Ok(ApiResponse<CodexContentDto>.FromResult(Result<CodexContentDto>.Success(dto), traceId));
            })
        .WithName("PutGlobalCodex")
        .WithLargeRequestBody();

        apiGroup.MapDelete(
            "/codex",
            () =>
            {
                string globalPath = Path.Combine(ArcanumPaths.GrimoireDirectory, "CODEX.md");

                if (File.Exists(globalPath))
                {
                    File.Delete(globalPath);
                }

                return Results.NoContent();
            })
        .WithName("DeleteGlobalCodex");

        return apiGroup;
    }

    private static async Task<CodexContentDto> ReadCodexDtoAsync(
        string path,
        IOptionsSnapshot<ArcanumSettings> settings,
        CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(path);

        long maxBytes = ArcanumSettingClamps.EffectiveCodexMaxSizeBytes(settings.Value);

        string? content = await CodexReader.ReadCodexFileAsync(fullPath, maxBytes, cancellationToken).ConfigureAwait(false);

        bool exists = File.Exists(fullPath);

        return new CodexContentDto(fullPath, content ?? string.Empty, exists);
    }

    private static async Task<IResult?> WriteCodexAsync(
        string path,
        string content,
        IOptionsSnapshot<ArcanumSettings> settings,
        string traceId,
        CancellationToken cancellationToken)
    {
        // W3.5: use the EFFECTIVE codex cap (min of the codex cap and Workspaces:MaxFileReadSizeBytes)
        // so the write bound matches the read path — otherwise PUT could accept content the codex
        // GET / inference read path then refuses.
        long maxBytes = ArcanumSettingClamps.EffectiveCodexMaxSizeBytes(settings.Value);

        int contentByteCount = Encoding.UTF8.GetByteCount(content);

        if (contentByteCount > maxBytes)
        {
            return Results.BadRequest(
                ApiResponse<CodexContentDto>.FromResult(
                    Result<CodexContentDto>.Failure(
                        new Error("Codex.ContentTooLarge", $"CODEX content exceeds the configured maximum of {maxBytes} bytes (UTF-8).")),
                    traceId));
        }

        string fullPath = Path.GetFullPath(path);

        string? parent = Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        await File.WriteAllTextAsync(fullPath, content, cancellationToken).ConfigureAwait(false);

        return null;
    }

}
