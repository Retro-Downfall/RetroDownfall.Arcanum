using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Api.Spells;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Api.TheForge;

[ExcludeFromCodeCoverage] // Reason: spell execution HTTP streaming endpoints; covered via spell execution integration tests.
internal static partial class SpellExecutionEndpoints
{

    public static RouteGroupBuilder MapSpellExecutionEndpoints(this RouteGroupBuilder apiGroup)
    {
        apiGroup.MapPost(
            "/spells/{name}/execute",
            async (
                string name,
                string? workspace,
                string? version,
                SpellExecuteRequest? body,
                ISpellRepository repo,
                SpellWorkspaceResolver workspaceResolver,
                ICampaignRepository campaignRepository,
                IArcanumIntelligenceProvider intelligence,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (body is null || string.IsNullOrWhiteSpace(body.Prompt))
                {
                    Result<PromptResponseDto> invalid = Result<PromptResponseDto>.Failure(
                        new Error(ErrorCodes.Validation.InvalidPrompt, "Prompt is required."));

                    return Results.Json(
                        ApiResponse<PromptResponseDto>.FromResult(invalid, traceId),
                        ArcanumJsonContext.Default.ApiResponsePromptResponseDto,
                        statusCode: StatusCodes.Status400BadRequest);
                }

                Result<string?> workspaceResult = await ExecuteWorkspaceResolver
                    .ResolveAsync(workspace, body.Workspace, body.CampaignId, workspaceResolver, campaignRepository, ctx.RequestAborted)
                    .ConfigureAwait(false);

                IResult? workspaceFailure = SpellApiResults.MapOptionalWorkspaceFailure<PromptResponseDto>(
                    workspaceResult,
                    traceId,
                    ArcanumJsonContext.Default.ApiResponsePromptResponseDto,
                    out string? resolvedWorkspace);

                if (workspaceFailure is not null)
                {
                    return workspaceFailure;
                }

                SpellDetail? spell = await repo
                    .GetAsync(name, resolvedWorkspace, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (spell is null)
                {
                    Result<PromptResponseDto> notFound = Result<PromptResponseDto>.Failure(
                        new Error(ErrorCodes.Spell.NotFound, "No spell exists with that name in the resolved workspace."));

                    return Results.Json(
                        ApiResponse<PromptResponseDto>.FromResult(notFound, traceId),
                        ArcanumJsonContext.Default.ApiResponsePromptResponseDto,
                        statusCode: StatusCodes.Status404NotFound);
                }

                Result<PingRequest> pingResult = BuildSpellExecutePingRequest(
                    body,
                    name,
                    spell,
                    resolvedWorkspace ?? string.Empty,
                    version);

                if (pingResult.IsFailure)
                {
                    return Results.Json(
                        ApiResponse<PromptResponseDto>.FromResult(
                            Result<PromptResponseDto>.Failure(pingResult.Error),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponsePromptResponseDto,
                        statusCode: StatusCodes.Status400BadRequest);
                }

                Result<PingRequest> resolvedPing = await PingRequestResolver
                    .ResolveCampaignAsync(pingResult.Value, campaignRepository, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (resolvedPing.IsFailure)
                {
                    return Results.Json(
                        ApiResponse<PromptResponseDto>.FromResult(
                            Result<PromptResponseDto>.Failure(resolvedPing.Error),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponsePromptResponseDto,
                        statusCode: StatusCodes.Status400BadRequest);
                }

                Result<PromptTurnResult> turn = await intelligence
                    .ExecutePromptAsync(resolvedPing.Value, ctx.RequestAborted)
                    .ConfigureAwait(false);

                Result<PromptResponseDto> envelopeResult = turn.IsFailure
                    ? Result<PromptResponseDto>.Failure(turn.Error)
                    : Result<PromptResponseDto>.Success(new PromptResponseDto(
                        turn.Value.Text,
                        turn.Value.Usage,
                        turn.Value.ToolCalls,
                        turn.Value.FinishReason));

                ApiResponse<PromptResponseDto> response = ApiResponse<PromptResponseDto>.FromResult(envelopeResult, traceId);

                return turn.IsSuccess
                    ? Results.Ok(response)
                    : Results.Json(
                        response,
                        ArcanumJsonContext.Default.ApiResponsePromptResponseDto,
                        statusCode: ArcanumErrorMapper.ResolveStatusCode(turn.Error.Code));
            })
        .WithName("Spell_Execute")
        .AddEndpointFilter(IdempotencyEndpointFilters.ForBoundArgument(3, ArcanumJsonContext.Default.SpellExecuteRequest));

        apiGroup.MapPost(
            "/spells/{name}/execute-stream",
            async (
                string name,
                string? workspace,
                string? version,
                SpellExecuteRequest? body,
                ISpellRepository repo,
                SpellWorkspaceResolver workspaceResolver,
                ICampaignRepository campaignRepository,
                IArcanumIntelligenceProvider intelligence,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (body is null || string.IsNullOrWhiteSpace(body.Prompt))
                {
                    Result<string> invalid = Result<string>.Failure(
                        new Error(ErrorCodes.Validation.InvalidPrompt, "Prompt is required."));

                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;

                    await ctx.Response
                        .WriteAsJsonAsync(
                            ApiResponse<string>.FromResult(invalid, traceId),
                            ArcanumJsonContext.Default.ApiResponseString,
                            cancellationToken: ctx.RequestAborted)
                        .ConfigureAwait(false);

                    return;
                }

                Result<string?> workspaceResult = await ExecuteWorkspaceResolver
                    .ResolveAsync(workspace, body.Workspace, body.CampaignId, workspaceResolver, campaignRepository, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (workspaceResult.IsFailure)
                {
                    ctx.Response.StatusCode = ArcanumErrorMapper.ResolveStatusCodeDefaultBadRequest(workspaceResult.Error.Code);

                    await ctx.Response
                        .WriteAsJsonAsync(
                            ApiResponse<string>.FromResult(Result<string>.Failure(workspaceResult.Error), traceId),
                            ArcanumJsonContext.Default.ApiResponseString,
                            cancellationToken: ctx.RequestAborted)
                        .ConfigureAwait(false);

                    return;
                }

                SpellDetail? spell = await repo
                    .GetAsync(name, workspaceResult.Value, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (spell is null)
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;

                    await ctx.Response
                        .WriteAsJsonAsync(
                            ApiResponse<string>.FromResult(
                                Result<string>.Failure(new Error(ErrorCodes.Spell.NotFound, "No spell exists with that name in the resolved workspace.")),
                                traceId),
                            ArcanumJsonContext.Default.ApiResponseString,
                            cancellationToken: ctx.RequestAborted)
                        .ConfigureAwait(false);

                    return;
                }

                Result<PingRequest> pingResult = BuildSpellExecutePingRequest(
                    body,
                    name,
                    spell,
                    workspaceResult.Value ?? string.Empty,
                    version);

                if (pingResult.IsFailure)
                {
                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;

                    await ctx.Response
                        .WriteAsJsonAsync(
                            ApiResponse<string>.FromResult(Result<string>.Failure(pingResult.Error), traceId),
                            ArcanumJsonContext.Default.ApiResponseString,
                            cancellationToken: ctx.RequestAborted)
                        .ConfigureAwait(false);

                    return;
                }

                Result<PingRequest> resolvedPing = await PingRequestResolver
                    .ResolveCampaignAsync(pingResult.Value, campaignRepository, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (resolvedPing.IsFailure)
                {
                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;

                    await ctx.Response
                        .WriteAsJsonAsync(
                            ApiResponse<string>.FromResult(Result<string>.Failure(resolvedPing.Error), traceId),
                            ArcanumJsonContext.Default.ApiResponseString,
                            cancellationToken: ctx.RequestAborted)
                        .ConfigureAwait(false);

                    return;
                }

                await InferenceExecuteWriter
                    .WriteStreamAsync(ctx, intelligence, resolvedPing.Value, ctx.RequestAborted)
                    .ConfigureAwait(false);
            })
        .WithName("Spell_ExecuteStream")
        .AddEndpointFilter(IdempotencyEndpointFilters.ForBoundArgument(3, ArcanumJsonContext.Default.SpellExecuteRequest));

        apiGroup.MapGet(
            "/spells/{name}/versions",
            async (
                string name,
                string? workspace,
                Guid? campaignId,
                ISpellRepository repo,
                SpellWorkspaceResolver workspaceResolver,
                ICampaignRepository campaignRepository,
                IOptionsSnapshot<ArcanumSettings> settings,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Result<string?> workspaceResult = await ExecuteWorkspaceResolver
                    .ResolveAsync(workspace, null, campaignId, workspaceResolver, campaignRepository, ctx.RequestAborted)
                    .ConfigureAwait(false);

                IResult? workspaceFailure = SpellApiResults.MapOptionalWorkspaceFailure<SpellVersionDto[]>(
                    workspaceResult,
                    traceId,
                    ArcanumJsonContext.Default.ApiResponseSpellVersionDtoArray,
                    out string? resolvedWorkspace);

                if (workspaceFailure is not null)
                {
                    return workspaceFailure;
                }

                SpellDetail? spell = await repo
                    .GetAsync(name, resolvedWorkspace, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (spell is null || string.IsNullOrWhiteSpace(spell.FilePath))
                {
                    Result<SpellVersionDto[]> notFound = Result<SpellVersionDto[]>.Failure(
                        new Error(ErrorCodes.Spell.NotFound, "No spell exists with that name in the resolved workspace."));

                    return Results.Json(
                        ApiResponse<SpellVersionDto[]>.FromResult(notFound, traceId),
                        ArcanumJsonContext.Default.ApiResponseSpellVersionDtoArray,
                        statusCode: StatusCodes.Status404NotFound);
                }

                string? spellDir = Path.GetDirectoryName(spell.FilePath);

                if (string.IsNullOrWhiteSpace(spellDir) || !Directory.Exists(spellDir))
                {
                    Result<SpellVersionDto[]> notFound = Result<SpellVersionDto[]>.Failure(
                        new Error(ErrorCodes.Spell.NotFound, "The spell directory does not exist."));

                    return Results.Json(
                        ApiResponse<SpellVersionDto[]>.FromResult(notFound, traceId),
                        ArcanumJsonContext.Default.ApiResponseSpellVersionDtoArray,
                        statusCode: StatusCodes.Status404NotFound);
                }

                List<SpellVersionDto> versions = [];

                long maxFileSizeBytes = ArcanumSettingClamps.EffectiveSpellMaxFileSizeBytes(settings.Value);

                string? activeVersionLabel = spell.ActiveVersion;

                string unversionedPath = Path.Combine(spellDir, "SPELL.md");

                if (File.Exists(unversionedPath))
                {
                    versions.Add(await BuildSpellVersionDtoAsync(
                        activeVersionLabel ?? "(active)",
                        isActive: true,
                        unversionedPath,
                        maxFileSizeBytes,
                        ctx.RequestAborted).ConfigureAwait(false));
                }

                foreach (string versionFile in Directory.EnumerateFiles(spellDir, "SPELL.v*.md"))
                {
                    string fileName = Path.GetFileName(versionFile);

                    if (!SpellVersionPathPolicy.TryParseLabelFromFileName(fileName, out string label))
                    {
                        continue;
                    }

                    versions.Add(await BuildSpellVersionDtoAsync(
                        label,
                        isActive: false,
                        versionFile,
                        maxFileSizeBytes,
                        ctx.RequestAborted).ConfigureAwait(false));
                }

                SpellVersionDto[] sorted = versions
                    .OrderByDescending(v => v.IsActive)
                    .ThenByDescending(v => v.CreatedAt)
                    .ToArray();

                return Results.Ok(
                    ApiResponse<SpellVersionDto[]>.FromResult(Result<SpellVersionDto[]>.Success(sorted), traceId));
            })
        .WithName("Spell_ListVersions");

        apiGroup.MapGet(
            "/spells/{name}/versions/{version}",
            async (
                string name,
                string version,
                string? workspace,
                Guid? campaignId,
                ISpellRepository repo,
                SpellWorkspaceResolver workspaceResolver,
                ICampaignRepository campaignRepository,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Result<string?> workspaceResult = await ExecuteWorkspaceResolver
                    .ResolveAsync(workspace, null, campaignId, workspaceResolver, campaignRepository, ctx.RequestAborted)
                    .ConfigureAwait(false);

                IResult? workspaceFailure = SpellApiResults.MapOptionalWorkspaceFailure<SpellVersionDetailDto>(
                    workspaceResult,
                    traceId,
                    ArcanumJsonContext.Default.ApiResponseSpellVersionDetailDto,
                    out string? resolvedWorkspace);

                if (workspaceFailure is not null)
                {
                    return workspaceFailure;
                }

                Result<SpellVersionDetailDto> result = await repo
                    .GetVersionDetailAsync(name, Uri.UnescapeDataString(version), resolvedWorkspace, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (result.IsFailure)
                {
                    return Results.Json(
                        ApiResponse<SpellVersionDetailDto>.FromResult(result, traceId),
                        ArcanumJsonContext.Default.ApiResponseSpellVersionDetailDto,
                        statusCode: ArcanumErrorMapper.ResolveStatusCodeDefaultBadRequest(result.Error.Code));
                }

                return Results.Ok(ApiResponse<SpellVersionDetailDto>.FromResult(result, traceId));
            })
        .WithName("Spell_GetVersionDetail");

        apiGroup.MapPost(
            "/spells/{name}/versions",
            async (
                string name,
                CreateSpellVersionRequest? request,
                ISpellRepository repo,
                SpellWorkspaceResolver workspaceResolver,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (request is null || string.IsNullOrWhiteSpace(request.Version) || string.IsNullOrWhiteSpace(request.Body))
                {
                    return Results.BadRequest(
                        ApiResponse<SpellVersionDto>.FromResult(
                            Result<SpellVersionDto>.Failure(new Error(ErrorCodes.Validation.InvalidBody, "version and body are required.")),
                            traceId));
                }

                Result<string> workspaceRequired = workspaceResolver.ResolveRequired(request.Workspace);

                IResult? workspaceFailure = SpellApiResults.MapRequiredWorkspaceFailure<SpellVersionDto>(
                    workspaceRequired,
                    traceId,
                    ArcanumJsonContext.Default.ApiResponseSpellVersionDto,
                    out string resolvedWorkspace);

                if (workspaceFailure is not null)
                {
                    return workspaceFailure;
                }

                Result<SpellVersionDto> result = await repo
                    .CreateVersionAsync(name, resolvedWorkspace, request, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (result.IsFailure)
                {
                    return Results.Json(
                        ApiResponse<SpellVersionDto>.FromResult(result, traceId),
                        ArcanumJsonContext.Default.ApiResponseSpellVersionDto,
                        statusCode: ArcanumErrorMapper.ResolveStatusCodeDefaultBadRequest(result.Error.Code));
                }

                return Results.Created(
                    $"/api/spells/{name}/versions/{Uri.EscapeDataString(result.Value.Version)}",
                    ApiResponse<SpellVersionDto>.FromResult(result, traceId));
            })
        .WithName("Spell_CreateVersion");

        apiGroup.MapPut(
            "/spells/{name}/versions/{version}",
            async (
                string name,
                string version,
                UpdateSpellVersionRequest? request,
                ISpellRepository repo,
                SpellWorkspaceResolver workspaceResolver,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (request is null || string.IsNullOrWhiteSpace(request.Body))
                {
                    return Results.BadRequest(
                        ApiResponse<SpellVersionDto>.FromResult(
                            Result<SpellVersionDto>.Failure(new Error(ErrorCodes.Validation.InvalidBody, "body is required.")),
                            traceId));
                }

                Result<string> workspaceRequired = workspaceResolver.ResolveRequired(request.Workspace);

                IResult? workspaceFailure = SpellApiResults.MapRequiredWorkspaceFailure<SpellVersionDto>(
                    workspaceRequired,
                    traceId,
                    ArcanumJsonContext.Default.ApiResponseSpellVersionDto,
                    out string resolvedWorkspace);

                if (workspaceFailure is not null)
                {
                    return workspaceFailure;
                }

                Result<SpellVersionDto> result = await repo
                    .UpdateVersionAsync(name, Uri.UnescapeDataString(version), resolvedWorkspace, request, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (result.IsFailure)
                {
                    return Results.Json(
                        ApiResponse<SpellVersionDto>.FromResult(result, traceId),
                        ArcanumJsonContext.Default.ApiResponseSpellVersionDto,
                        statusCode: ArcanumErrorMapper.ResolveStatusCodeDefaultBadRequest(result.Error.Code));
                }

                return Results.Ok(ApiResponse<SpellVersionDto>.FromResult(result, traceId));
            })
        .WithName("Spell_UpdateVersion");

        apiGroup.MapPost(
            "/spells/{name}/versions/{version}/activate",
            async (
                string name,
                string version,
                ActivateSpellVersionRequest? request,
                ISpellRepository repo,
                SpellWorkspaceResolver workspaceResolver,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Result<string> workspaceRequired = workspaceResolver.ResolveRequired(request?.Workspace);

                IResult? workspaceFailure = SpellApiResults.MapRequiredWorkspaceFailure<SpellVersionDto>(
                    workspaceRequired,
                    traceId,
                    ArcanumJsonContext.Default.ApiResponseSpellVersionDto,
                    out string resolvedWorkspace);

                if (workspaceFailure is not null)
                {
                    return workspaceFailure;
                }

                Result<SpellVersionDto> result = await repo
                    .ActivateVersionAsync(name, Uri.UnescapeDataString(version), resolvedWorkspace, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (result.IsFailure)
                {
                    return Results.Json(
                        ApiResponse<SpellVersionDto>.FromResult(result, traceId),
                        ArcanumJsonContext.Default.ApiResponseSpellVersionDto,
                        statusCode: ArcanumErrorMapper.ResolveStatusCodeDefaultBadRequest(result.Error.Code));
                }

                return Results.Ok(ApiResponse<SpellVersionDto>.FromResult(result, traceId));
            })
        .WithName("Spell_ActivateVersion");

        apiGroup.MapPost(
            "/spells/{name}/clone",
            async (
                string name,
                CloneSpellRequest? request,
                ISpellRepository repo,
                SpellWorkspaceResolver workspaceResolver,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (request is null || string.IsNullOrWhiteSpace(request.NewName))
                {
                    return Results.BadRequest(
                        ApiResponse<SpellSummary>.FromResult(
                            Result<SpellSummary>.Failure(new Error(ErrorCodes.Validation.InvalidBody, "newName is required.")),
                            traceId));
                }

                Result<string> workspaceRequired = workspaceResolver.ResolveRequired(request.Workspace);

                IResult? workspaceFailure = SpellApiResults.MapRequiredWorkspaceFailure<SpellSummary>(
                    workspaceRequired,
                    traceId,
                    ArcanumJsonContext.Default.ApiResponseSpellSummary,
                    out string resolvedWorkspace);

                if (workspaceFailure is not null)
                {
                    return workspaceFailure;
                }

                Result<SpellSummary> result = await repo
                    .CloneAsync(name, resolvedWorkspace, request, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (result.IsFailure)
                {
                    return Results.Json(
                        ApiResponse<SpellSummary>.FromResult(result, traceId),
                        ArcanumJsonContext.Default.ApiResponseSpellSummary,
                        statusCode: ArcanumErrorMapper.ResolveStatusCodeDefaultBadRequest(result.Error.Code));
                }

                return Results.Created(
                    $"/api/spells/{Uri.EscapeDataString(result.Value.Name)}",
                    ApiResponse<SpellSummary>.FromResult(result, traceId));
            })
        .WithName("Spell_Clone");

        apiGroup.MapPost(
            "/spells/{name}/cast",
            async (
                string name,
                SpellCastRequest? request,
                ISpellCastPreviewService castPreview,
                SpellWorkspaceResolver workspaceResolver,
                ICampaignRepository campaignRepository,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                SpellCastRequest effectiveRequest = request ?? new SpellCastRequest();

                Result<string?> workspaceResult = await ExecuteWorkspaceResolver
                    .ResolveAsync(null, effectiveRequest.Workspace, effectiveRequest.CampaignId, workspaceResolver, campaignRepository, ctx.RequestAborted)
                    .ConfigureAwait(false);

                IResult? workspaceFailure = SpellApiResults.MapOptionalWorkspaceFailure<SpellCastResult>(
                    workspaceResult,
                    traceId,
                    ArcanumJsonContext.Default.ApiResponseSpellCastResult,
                    out string? resolvedWorkspace);

                if (workspaceFailure is not null)
                {
                    return workspaceFailure;
                }

                Result<SpellCastResult> result = await castPreview
                    .CastAsync(name, resolvedWorkspace, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (result.IsFailure)
                {
                    return Results.Json(
                        ApiResponse<SpellCastResult>.FromResult(result, traceId),
                        ArcanumJsonContext.Default.ApiResponseSpellCastResult,
                        statusCode: ArcanumErrorMapper.ResolveStatusCodeDefaultBadRequest(result.Error.Code));
                }

                return Results.Ok(ApiResponse<SpellCastResult>.FromResult(result, traceId));
            })
        .WithName("Spell_Cast");

        return apiGroup;
    }

    private static Result<PingRequest> BuildSpellExecutePingRequest(
        SpellExecuteRequest body,
        string spellName,
        SpellDetail spell,
        string workingDirectory,
        string? version)
    {
        string? overrideSpellPath = null;

        string? overrideSpellName = spellName;

        if (!string.IsNullOrWhiteSpace(version))
        {
            string label = version.Trim();

            if (!SpellVersionPathPolicy.IsValidLabel(label))
            {
                return Result<PingRequest>.Failure(
                    new Error(ErrorCodes.Spell.InvalidVersion, $"Invalid version label \"{label}\" \u2014 alphanumeric and dots only."));
            }

            string? spellDir = string.IsNullOrWhiteSpace(spell.FilePath)
                ? null
                : Path.GetDirectoryName(spell.FilePath);

            if (string.IsNullOrWhiteSpace(spellDir))
            {
                return Result<PingRequest>.Failure(
                    new Error(ErrorCodes.Spell.InvalidVersion, "Cannot resolve the spell directory for the requested version."));
            }

            string versionPath = Path.Combine(spellDir, SpellVersionPathPolicy.BuildVersionFileName(label));

            if (!File.Exists(versionPath))
            {
                return Result<PingRequest>.Failure(
                    new Error(ErrorCodes.Spell.InvalidVersion, $"Spell version \"{label}\" does not exist."));
            }

            overrideSpellPath = versionPath;

            overrideSpellName = null;
        }

        PingRequest ping = new(
            Prompt: body.Prompt,
            Model: body.Model,
            WorkingDirectory: workingDirectory,
            SessionId: body.SessionId,
            OverrideSpellName: overrideSpellName,
            SkipSpellRouting: false,
            Temperature: body.Temperature,
            TopP: body.TopP,
            MaxOutputTokens: body.MaxOutputTokens,
            Stop: body.Stop,
            Seed: body.Seed,
            ResponseFormat: body.ResponseFormat,
            PresencePenalty: body.PresencePenalty,
            FrequencyPenalty: body.FrequencyPenalty,
            CampaignId: body.CampaignId,
            ToolPolicy: body.ToolPolicy,
            OverrideSpellPath: overrideSpellPath);

        return Result<PingRequest>.Success(ping);
    }

    private static async Task<SpellVersionDto> BuildSpellVersionDtoAsync(
        string version,
        bool isActive,
        string filePath,
        long maxFileSizeBytes,
        CancellationToken cancellationToken)
    {
        DateTimeOffset createdAt;

        try
        {
            createdAt = File.GetLastWriteTimeUtc(filePath);
        }
        catch (IOException)
        {
            createdAt = DateTimeOffset.UtcNow;
        }
        catch (UnauthorizedAccessException)
        {
            createdAt = DateTimeOffset.UtcNow;
        }

        ParsedSpell? parsed = await SpellScanner.LoadFullAsync(filePath, cancellationToken, maxFileSizeBytes).ConfigureAwait(false);

        string? description = string.IsNullOrWhiteSpace(parsed?.Description) ? null : parsed.Description;

        return new SpellVersionDto(version, isActive, createdAt, description);
    }

}
