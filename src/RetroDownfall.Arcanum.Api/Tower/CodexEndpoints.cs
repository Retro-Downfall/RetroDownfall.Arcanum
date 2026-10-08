using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetroDownfall.Arcanum.Api.Primitives;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Storage;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Api.Tower;

internal static class CodexEndpoints
{
    /// <summary>
    /// A campaign root is frequently an untrusted repository the operator cloned, and a repository can
    /// ship <c>CODEX.md</c> as a symbolic link. Both the read and the write follow that link, so the
    /// codex path is contained against its own root before either touches the file.
    /// </summary>
    private static readonly Error CodexPathNotContained = new(
        ErrorCodes.Codex.PathNotContained,
        "The CODEX.md path resolves outside its campaign or Grimoire directory.");

    /// <summary>
    /// A write is never made through a <c>CODEX.md</c> that is itself a link, even one whose target is inside
    /// the root: the write replaces the entry, which would quietly end whatever the link shared.
    /// </summary>
    private static readonly Error CodexIsALink = new(
        ErrorCodes.Workspace.SymbolicLinkEscape,
        "CODEX.md is a symbolic link or a file with more than one hard link, and is not written through. Delete it (DELETE removes the link, not its target) or write the file it points to.");

    public static RouteGroupBuilder MapCodexEndpoints(this RouteGroupBuilder apiGroup)
    {
        apiGroup.MapGet(
            "/campaigns/{id:guid}/codex",
            async (Guid id, ICampaignRepository repo, HttpContext ctx) =>
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
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Campaign? campaign = await repo.GetByIdAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                if (campaign is null)
                {
                    return Results.Json(
                        ApiResponse<CodexContentDto>.FromResult(
                            Result<CodexContentDto>.Failure(new Error(ErrorCodes.Campaign.NotFound, "No campaign exists with that identifier.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseCodexContentDto,
                        statusCode: StatusCodes.Status404NotFound);
                }

                return DeleteCodex(campaign.Path, Path.Combine(campaign.Path, "CODEX.md"), traceId);
            })
        .WithName("DeleteCampaignCodex");

        apiGroup.MapGet(
            "/codex",
            async (HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                string globalPath = Path.Combine(ArcanumPaths.GrimoireDirectory, "CODEX.md");

                Result<CodexContentDto> codex = await ReadCodexDtoAsync(
                    ArcanumPaths.GrimoireDirectory,
                    globalPath,
                    ctx.RequestAborted)
                    .ConfigureAwait(false);

                return ToCodexResponse(codex, traceId);
            })
        .WithName("GetGlobalCodex");

        apiGroup.MapPut(
            "/codex",
            async (CodexPutRequest? body, HttpContext ctx) =>
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
                    ctx.RequestAborted)
                    .ConfigureAwait(false);

                return ToCodexResponse(codex, traceId);
            })
        .WithName("PutGlobalCodex")
        .WithLargeRequestBody();

        apiGroup.MapDelete(
            "/codex",
            (HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                return DeleteCodex(
                    ArcanumPaths.GrimoireDirectory,
                    Path.Combine(ArcanumPaths.GrimoireDirectory, "CODEX.md"),
                    traceId);
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
        CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(path);

        if (!IsCodexPathContained(containmentRoot, fullPath))
        {
            return Result<CodexContentDto>.Failure(CodexPathNotContained);
        }

        long maxBytes = ArcanumSettingClamps.EffectiveCodexMaxSizeBytes();

        string? content = await CodexReader.ReadCodexFileAsync(fullPath, maxBytes, cancellationToken).ConfigureAwait(false);

        bool exists = File.Exists(fullPath);

        return Result<CodexContentDto>.Success(new CodexContentDto(fullPath, content ?? string.Empty, exists));
    }

    /// <summary>
    /// Removes the <c>CODEX.md</c> entry itself. A link in the leaf is unlinked, never followed, which is the
    /// documented behaviour and the one way an operator removes a hostile link through the API; the
    /// directory the entry lives in must still resolve under <paramref name="containmentRoot"/>, because
    /// unlinking through a linked directory removes a file somewhere else.
    /// </summary>
    internal static IResult DeleteCodex(string containmentRoot, string path, string traceId)
    {
        string fullPath = Path.GetFullPath(path);

        string? parent = Path.GetDirectoryName(fullPath);

        if (string.IsNullOrWhiteSpace(parent) || !IsCodexPathContained(containmentRoot, parent))
        {
            return CodexFailure(CodexPathNotContained, traceId);
        }

        try
        {
            // File.Delete is a no-op for an absent entry and removes a link without touching its target; the
            // File.Exists pre-check this replaces answered false for a dangling link and so left it in place.
            File.Delete(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CodexFailure(
                new Error(ErrorCodes.Workspace.DeleteFailed, "The CODEX.md could not be removed."),
                traceId);
        }

        return Results.NoContent();
    }

    internal static async Task<IResult?> WriteCodexAsync(
        string containmentRoot,
        string path,
        string? content,
        string traceId,
        CancellationToken cancellationToken)
    {
        if (content is null)
        {
            // System.Text.Json does not enforce constructor-parameter nullability, so a body of `{}` or
            // `{"content":null}` deserializes to a request whose content is null.
            return CodexFailure(
                new Error(ErrorCodes.Validation.InvalidBody, "Request body must include the CODEX content."),
                traceId);
        }

        // W3.5: use the EFFECTIVE codex cap (min of the clamped codex cap and the code-owned
        // workspace read cap, ArcanumRuntimeDefaults.WorkspaceMaxFileReadSizeBytes) so the write
        // bound matches the read path — otherwise PUT could accept content the codex GET /
        // inference read path then refuses. No setting is consulted.
        long maxBytes = ArcanumSettingClamps.EffectiveCodexMaxSizeBytes();

        byte[] contentBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);

        if (contentBytes.Length > maxBytes)
        {
            return Results.BadRequest(
                ApiResponse<CodexContentDto>.FromResult(
                    Result<CodexContentDto>.Failure(
                        new Error(ErrorCodes.Codex.ContentTooLarge, $"CODEX content exceeds the configured maximum of {maxBytes} bytes (UTF-8).")),
                    traceId));
        }

        string fullPath = Path.GetFullPath(path);

        // Containment first: a directory made before the check, through a linked component, is a directory
        // made outside the root by a request that was then refused.
        if (!IsCodexPathContained(containmentRoot, fullPath))
        {
            return CodexFailure(CodexPathNotContained, traceId);
        }

        string? directory = Path.GetDirectoryName(fullPath);

        if (string.IsNullOrWhiteSpace(directory))
        {
            return CodexFailure(CodexPathNotContained, traceId);
        }

        // A request that is already gone may stop here; once the write begins it runs to the end.
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            Directory.CreateDirectory(directory);

            // Same-directory temporary file, then an atomic replace. Writing the destination in place on the
            // request's token truncated it before the first byte and left a half-written codex when the
            // client went away; the destination is now either the old file or the new one. The replace takes
            // no token for the same reason: after the first byte is written the work is not the caller's to
            // abandon.
            string tempPath = Path.Combine(directory, $".arcanum-{Guid.NewGuid():N}.tmp");

            AtomicReplaceStatus status = await AtomicFile.ReplaceAsync(
                    fullPath,
                    tempPath,
                    async (stream, token) => await stream.WriteAsync(contentBytes, token).ConfigureAwait(false),
                    CancellationToken.None,
                    beforeReplace: () => IsCodexPathContained(containmentRoot, fullPath))
                .ConfigureAwait(false);

            return status switch
            {
                AtomicReplaceStatus.Succeeded => null,

                // The replace refuses a destination that is itself a link -- a symbolic link, wherever it
                // points, or a file with more than one hard link -- because replacing the entry would silently
                // stop sharing the content the link shared. That is not an escape from the root, so it is not
                // reported as one.
                AtomicReplaceStatus.Aborted when IsLinkedCodex(fullPath) => CodexFailure(CodexIsALink, traceId),

                // The gate above refuses a destination that moved outside the root since the check.
                AtomicReplaceStatus.Aborted => CodexFailure(CodexPathNotContained, traceId),

                _ => CodexFailure(
                    new Error(ErrorCodes.Workspace.WriteFailed, "The CODEX.md could not be written."),
                    traceId),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CodexFailure(
                new Error(ErrorCodes.Workspace.WriteFailed, "The CODEX.md could not be written."),
                traceId);
        }
    }

    /// <summary>
    /// Whether the <c>CODEX.md</c> entry is a symbolic link or a file with more than one hard link, the two
    /// shapes the atomic replace refuses to write through.
    /// </summary>
    private static bool IsLinkedCodex(string path)
    {
        try
        {
            if (new FileInfo(path).LinkTarget is not null)
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return FileHandleIdentityInterop.TryGetPathMetadataNoFollow(path, out FileHandleMetadata metadata)
            && metadata.Kind == FileSystemObjectKind.RegularFile
            && metadata.HardLinkCount > 1;
    }

    private static IResult CodexFailure(Error error, string traceId) =>
        Results.Json(
            ApiResponse<CodexContentDto>.FromResult(Result<CodexContentDto>.Failure(error), traceId),
            ArcanumJsonContext.Default.ApiResponseCodexContentDto,
            statusCode: ArcanumErrorMapper.ResolveStatusCode(error.Code));
}
