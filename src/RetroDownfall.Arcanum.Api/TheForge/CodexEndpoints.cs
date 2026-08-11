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
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Api.TheForge;

internal static class CodexEndpoints
{

    /// <summary>
    /// A campaign root is frequently an untrusted repository the operator cloned, and a repository can
    /// ship <c>CODEX.md</c> as a symbolic link. Both the read and the write follow that link, so the
    /// codex path is contained against its own root before either touches the file.
    /// </summary>
    private static readonly Error CodexPathNotContained = new(
        "Codex.PathNotContained",
        "The CODEX.md path resolves outside its campaign or Grimoire directory.");

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
                        new Error(ErrorCodes.Campaign.NotFound, "No campaign exists with that identifier."));

                    return Results.Json(
                        ApiResponse<CodexContentDto>.FromResult(notFound, traceId),
                        ArcanumJsonContext.Default.ApiResponseCodexContentDto,
                        statusCode: StatusCodes.Status404NotFound);
                }

                Result<CodexContentDto> codex = await ReadCodexDtoAsync(
                    campaign.Path,
                    Path.Combine(campaign.Path, "CODEX.md"),
                    settings,
                    ctx.RequestAborted)
                    .ConfigureAwait(false);

                return ToCodexResponse(codex, traceId);
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
                            Result<CodexContentDto>.Failure(new Error(ErrorCodes.Validation.InvalidBody, "Request body is required.")),
                            traceId));
                }

                Campaign? campaign = await repo.GetByIdAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                if (campaign is null)
                {
                    Result<CodexContentDto> notFound = Result<CodexContentDto>.Failure(
                        new Error(ErrorCodes.Campaign.NotFound, "No campaign exists with that identifier."));

                    return Results.Json(
                        ApiResponse<CodexContentDto>.FromResult(notFound, traceId),
                        ArcanumJsonContext.Default.ApiResponseCodexContentDto,
                        statusCode: StatusCodes.Status404NotFound);
                }

                string codexPath = Path.Combine(campaign.Path, "CODEX.md");

                IResult? writeResult = await WriteCodexAsync(
                    campaign.Path,
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

                Result<CodexContentDto> codex = await ReadCodexDtoAsync(
                    campaign.Path,
                    codexPath,
                    settings,
                    ctx.RequestAborted)
                    .ConfigureAwait(false);

                return ToCodexResponse(codex, traceId);
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
                            Result<CodexContentDto>.Failure(new Error(ErrorCodes.Campaign.NotFound, "No campaign exists with that identifier.")),
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

                Result<CodexContentDto> codex = await ReadCodexDtoAsync(
                    ArcanumPaths.GrimoireDirectory,
                    globalPath,
                    settings,
                    ctx.RequestAborted)
                    .ConfigureAwait(false);

                return ToCodexResponse(codex, traceId);
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
                            Result<CodexContentDto>.Failure(new Error(ErrorCodes.Validation.InvalidBody, "Request body is required.")),
                            traceId));
                }

                string globalPath = Path.Combine(ArcanumPaths.GrimoireDirectory, "CODEX.md");

                IResult? writeResult = await WriteCodexAsync(
                    ArcanumPaths.GrimoireDirectory,
                    globalPath,
                    body.Content,
                    settings,
                    traceId,
                    ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (writeResult is not null)
                {
                    return writeResult;
                }

                Result<CodexContentDto> codex = await ReadCodexDtoAsync(
                    ArcanumPaths.GrimoireDirectory,
                    globalPath,
                    settings,
                    ctx.RequestAborted)
                    .ConfigureAwait(false);

                return ToCodexResponse(codex, traceId);
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

    private static IResult ToCodexResponse(Result<CodexContentDto> codex, string traceId)
    {
        if (codex.IsFailure)
        {
            return Results.Json(
                ApiResponse<CodexContentDto>.FromResult(codex, traceId),
                ArcanumJsonContext.Default.ApiResponseCodexContentDto,
                statusCode: StatusCodes.Status400BadRequest);
        }

        return Results.Ok(ApiResponse<CodexContentDto>.FromResult(codex, traceId));
    }

    /// <summary>
    /// Fails closed unless <paramref name="codexFullPath"/> still resolves under
    /// <paramref name="containmentRoot"/> once every existing component — including a <c>CODEX.md</c>
    /// symbolic link at the leaf — has been resolved. <see cref="Path.GetFullPath(string)"/> is purely
    /// lexical and never observes the link.
    /// </summary>
    private static bool IsCodexPathContained(string containmentRoot, string codexFullPath)
    {
        if (string.IsNullOrWhiteSpace(containmentRoot))
        {
            return false;
        }

        string normalizedRoot;

        try
        {
            normalizedRoot = Path.GetFullPath(containmentRoot.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }

        return WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(normalizedRoot, codexFullPath, out _);
    }

    internal static async Task<Result<CodexContentDto>> ReadCodexDtoAsync(
        string containmentRoot,
        string path,
        IOptionsSnapshot<ArcanumSettings> settings,
        CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(path);

        if (!IsCodexPathContained(containmentRoot, fullPath))
        {
            return Result<CodexContentDto>.Failure(CodexPathNotContained);
        }

        long maxBytes = ArcanumSettingClamps.EffectiveCodexMaxSizeBytes(settings.Value);

        string? content = await CodexReader.ReadCodexFileAsync(fullPath, maxBytes, cancellationToken).ConfigureAwait(false);

        bool exists = File.Exists(fullPath);

        return Result<CodexContentDto>.Success(new CodexContentDto(fullPath, content ?? string.Empty, exists));
    }

    internal static async Task<IResult?> WriteCodexAsync(
        string containmentRoot,
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
                        new Error(ErrorCodes.Codex.ContentTooLarge, $"CODEX content exceeds the configured maximum of {maxBytes} bytes (UTF-8).")),
                    traceId));
        }

        string fullPath = Path.GetFullPath(path);

        string? parent = Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        if (!IsCodexPathContained(containmentRoot, fullPath))
        {
            return Results.Json(
                ApiResponse<CodexContentDto>.FromResult(
                    Result<CodexContentDto>.Failure(CodexPathNotContained),
                    traceId),
                ArcanumJsonContext.Default.ApiResponseCodexContentDto,
                statusCode: StatusCodes.Status400BadRequest);
        }

        await File.WriteAllTextAsync(fullPath, content, cancellationToken).ConfigureAwait(false);

        return null;
    }

}
