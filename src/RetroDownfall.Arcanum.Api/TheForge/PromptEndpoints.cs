using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Spells;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Workspace;

namespace RetroDownfall.Arcanum.Api.TheForge;

internal static class PromptEndpoints
{

    public static RouteGroupBuilder MapPromptEndpoints(this RouteGroupBuilder apiGroup)
    {
        apiGroup.MapGet(
            "/prompts",
            async (
                Guid? campaignId,
                string? q,
                string? tag,
                int? limit,
                int? offset,
                IPromptRepository repo,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                bool hasClientFilters = !string.IsNullOrWhiteSpace(q) || !string.IsNullOrWhiteSpace(tag);

                int? effectiveLimit = hasClientFilters
                    ? ArcanumSettingClamps.ListQueryLimit(10_000)
                    : limit;

                int effectiveOffset = hasClientFilters ? 0 : offset ?? 0;

                ListPageResult<Prompt> page = await repo
                    .ListAsync(campaignId, effectiveLimit, effectiveOffset, ctx.RequestAborted)
                    .ConfigureAwait(false);

                IEnumerable<PromptSummaryDto> query = page.Items.Select(PromptMapping.ToSummaryDto);

                if (!string.IsNullOrWhiteSpace(q))
                {
                    string term = q.Trim();

                    query = query.Where(p =>
                        p.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                        || (p.Description?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                        || p.Tags.Any(t => t.Contains(term, StringComparison.OrdinalIgnoreCase)));
                }

                if (!string.IsNullOrWhiteSpace(tag))
                {
                    query = query.Where(p => p.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)));
                }

                PromptSummaryDto[] result = query
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                ListPageResult<PromptSummaryDto> response = hasClientFilters
                    ? new ListPageResult<PromptSummaryDto>(result, false)
                    : new ListPageResult<PromptSummaryDto>(
                        result,
                        page.HasMore,
                        page.NextOffset);

                return Results.Ok(
                    ApiResponse<ListPageResult<PromptSummaryDto>>.FromResult(
                        Result<ListPageResult<PromptSummaryDto>>.Success(response),
                        traceId));
            })
        .WithName("ListPrompts");

        apiGroup.MapGet(
            "/prompts/{id:guid}",
            async (Guid id, IPromptRepository repo, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Prompt? prompt = await repo.GetByIdAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                if (prompt is null)
                {
                    return Results.Json(
                        ApiResponse<PromptDetailDto>.FromResult(
                            Result<PromptDetailDto>.Failure(new Error("Prompt.NotFound", "No prompt exists with that identifier.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponsePromptDetailDto,
                        statusCode: StatusCodes.Status404NotFound);
                }

                return Results.Ok(
                    ApiResponse<PromptDetailDto>.FromResult(
                        Result<PromptDetailDto>.Success(PromptMapping.ToDetailDto(prompt)),
                        traceId));
            })
        .WithName("GetPrompt");

        apiGroup.MapGet(
            "/prompts/by-name/{name}/versions",
            async (string name, Guid? campaignId, IPromptRepository repo, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                IReadOnlyList<Prompt> versions = await repo
                    .ListVersionsAsync(name, campaignId, ctx.RequestAborted)
                    .ConfigureAwait(false);

                PromptVersionDto[] dtos = versions
                    .Select(p => new PromptVersionDto(p.Id, p.Version, p.UpdatedAt))
                    .ToArray();

                return Results.Ok(
                    ApiResponse<PromptVersionDto[]>.FromResult(
                        Result<PromptVersionDto[]>.Success(dtos),
                        traceId));
            })
        .WithName("ListPromptVersions");

        apiGroup.MapPost(
            "/prompts",
            async (CreatePromptRequest? request, IPromptRepository repo, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (request is null || string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Version))
                {
                    return Results.BadRequest(
                        ApiResponse<PromptDetailDto>.FromResult(
                            Result<PromptDetailDto>.Failure(new Error("Prompt.InvalidRequest", "Name and version are required.")),
                            traceId));
                }

                Prompt? existing = await repo
                    .GetByNameAndVersionAsync(request.Name, request.Version, request.CampaignId, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (existing is not null)
                {
                    return Results.BadRequest(
                        ApiResponse<PromptDetailDto>.FromResult(
                            Result<PromptDetailDto>.Failure(new Error("Prompt.DuplicateVersion", "A prompt with this name and version already exists.")),
                            traceId));
                }

                DateTimeOffset now = DateTimeOffset.UtcNow;

                Prompt prompt = new()
                {
                    Id = Guid.NewGuid(),
                    CampaignId = request.CampaignId,
                    Name = request.Name.Trim(),
                    Version = request.Version.Trim(),
                    Description = request.Description,
                    Tags = PromptRepository.SerializeTags(request.Tags ?? []),
                    Template = request.Template,
                    ParameterSchema = PromptMapping.SerializeJsonDocument(request.ParameterSchema),
                    DefaultParameters = PromptMapping.SerializeJsonDocument(request.DefaultParameters),
                    Model = request.Model,
                    Provider = request.Provider,
                    Temperature = request.Temperature,
                    TopP = request.TopP,
                    MaxOutputTokens = request.MaxOutputTokens,
                    CreatedAt = now,
                    UpdatedAt = now,
                };

                await repo.AddAsync(prompt, ctx.RequestAborted).ConfigureAwait(false);

                return Results.Created(
                    $"/api/prompts/{prompt.Id}",
                    ApiResponse<PromptDetailDto>.FromResult(
                        Result<PromptDetailDto>.Success(PromptMapping.ToDetailDto(prompt)),
                        traceId));
            })
        .WithName("CreatePrompt");

        apiGroup.MapPut(
            "/prompts/{id:guid}",
            async (Guid id, UpdatePromptRequest? request, IPromptRepository repo, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (request is null)
                {
                    return Results.BadRequest(
                        ApiResponse<PromptDetailDto>.FromResult(
                            Result<PromptDetailDto>.Failure(new Error("Validation.InvalidBody", "Request body is required.")),
                            traceId));
                }

                Prompt? existing = await repo.GetByIdAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                if (existing is null)
                {
                    return Results.Json(
                        ApiResponse<PromptDetailDto>.FromResult(
                            Result<PromptDetailDto>.Failure(new Error("Prompt.NotFound", "No prompt exists with that identifier.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponsePromptDetailDto,
                        statusCode: StatusCodes.Status404NotFound);
                }

                if (request.Name is not null)
                {
                    existing.Name = request.Name.Trim();
                }

                if (request.Version is not null)
                {
                    existing.Version = request.Version.Trim();
                }

                if (request.Description is not null)
                {
                    existing.Description = request.Description;
                }

                if (request.Tags is not null)
                {
                    existing.Tags = PromptRepository.SerializeTags(request.Tags);
                }

                if (request.Template is not null)
                {
                    existing.Template = request.Template;
                }

                if (request.ParameterSchema is not null)
                {
                    existing.ParameterSchema = PromptMapping.SerializeJsonDocument(request.ParameterSchema);
                }

                if (request.DefaultParameters is not null)
                {
                    existing.DefaultParameters = PromptMapping.SerializeJsonDocument(request.DefaultParameters);
                }

                if (request.Model is not null)
                {
                    existing.Model = request.Model;
                }

                if (request.Provider is not null)
                {
                    existing.Provider = request.Provider;
                }

                if (request.Temperature is not null)
                {
                    existing.Temperature = request.Temperature;
                }

                if (request.TopP is not null)
                {
                    existing.TopP = request.TopP;
                }

                if (request.MaxOutputTokens is not null)
                {
                    existing.MaxOutputTokens = request.MaxOutputTokens;
                }

                existing.UpdatedAt = DateTimeOffset.UtcNow;

                await repo.UpdateAsync(existing, ctx.RequestAborted).ConfigureAwait(false);

                return Results.Ok(
                    ApiResponse<PromptDetailDto>.FromResult(
                        Result<PromptDetailDto>.Success(PromptMapping.ToDetailDto(existing)),
                        traceId));
            })
        .WithName("UpdatePrompt");

        apiGroup.MapDelete(
            "/prompts/{id:guid}",
            async (Guid id, IPromptRepository repo, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                bool deleted = await repo.DeleteAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                if (!deleted)
                {
                    return Results.Json(
                        ApiResponse<bool>.FromResult(
                            Result<bool>.Failure(new Error("Prompt.NotFound", "No prompt exists with that identifier.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseBoolean,
                        statusCode: StatusCodes.Status404NotFound);
                }

                return Results.NoContent();
            })
        .WithName("DeletePrompt");

        apiGroup.MapPost(
            "/prompts/{id:guid}/render",
            async (
                Guid id,
                PromptRenderRequest? request,
                IPromptRepository repo,
                PromptRenderer renderer,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Prompt? prompt = await repo.GetByIdAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                if (prompt is null)
                {
                    return Results.Json(
                        ApiResponse<PromptRenderResultDto>.FromResult(
                            Result<PromptRenderResultDto>.Failure(new Error("Prompt.NotFound", "No prompt exists with that identifier.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponsePromptRenderResultDto,
                        statusCode: StatusCodes.Status404NotFound);
                }

                Result<PromptRenderResultDto> result = renderer.Render(prompt, request?.Parameters);

                return result.IsSuccess
                    ? Results.Ok(ApiResponse<PromptRenderResultDto>.FromResult(result, traceId))
                    : Results.BadRequest(ApiResponse<PromptRenderResultDto>.FromResult(result, traceId));
            })
        .WithName("RenderPrompt");

        apiGroup.MapPost(
            "/prompts/{id:guid}/test",
            async (
                Guid id,
                TestPromptRequest? request,
                IPromptRepository repo,
                ICampaignRepository campaignRepo,
                IOptionsSnapshot<ArcanumSettings> settings,
                PromptRenderer renderer,
                IMcpConnectionManager mcpManager,
                IManaMeter manaMeter,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Prompt? prompt = await repo.GetByIdAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                if (prompt is null)
                {
                    return Results.Json(
                        ApiResponse<PromptTestResultDto>.FromResult(
                            Result<PromptTestResultDto>.Failure(new Error("Prompt.NotFound", "No prompt exists with that identifier.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponsePromptTestResultDto,
                        statusCode: StatusCodes.Status404NotFound);
                }

                Result<Dictionary<string, string>> defaultsResult = renderer.ResolveDefaultParameters(prompt);

                if (defaultsResult.IsFailure)
                {
                    return Results.BadRequest(
                        ApiResponse<PromptTestResultDto>.FromResult(
                            Result<PromptTestResultDto>.Failure(defaultsResult.Error),
                            traceId));
                }

                Result<PromptRenderResultDto> rendered = renderer.Render(prompt, defaultsResult.Value);

                if (rendered.IsFailure)
                {
                    return Results.BadRequest(
                        ApiResponse<PromptTestResultDto>.FromResult(
                            Result<PromptTestResultDto>.Failure(rendered.Error),
                            traceId));
                }

                string workingDirectory = request?.WorkingDirectory ?? string.Empty;

                string? codexContent = null;

                if (!string.IsNullOrWhiteSpace(request?.CodexPath))
                {
                    string? containmentRoot = null;

                    if (prompt.CampaignId is Guid campaignId)
                    {
                        Campaign? campaign = await campaignRepo
                            .GetByIdAsync(campaignId, ctx.RequestAborted)
                            .ConfigureAwait(false);

                        containmentRoot = campaign?.Path;
                    }

                    if (string.IsNullOrWhiteSpace(containmentRoot)
                        && !string.IsNullOrWhiteSpace(workingDirectory))
                    {
                        try
                        {
                            string normalizedWorkingDirectory = Path.GetFullPath(workingDirectory.Trim());

                            if (Directory.Exists(normalizedWorkingDirectory))
                            {
                                containmentRoot = normalizedWorkingDirectory;
                            }
                        }
                        catch (Exception)
                        {
                            // fall through
                        }
                    }

                    long maxBytes = ArcanumSettingClamps.MaxFileReadSizeBytes(
                        settings.Value.Workspaces?.MaxFileReadSizeBytes ?? new WorkspaceSettings().MaxFileReadSizeBytes);

                    Result<string> codexPathResult = CodexPathPolicy.ValidateContainedFile(
                        request.CodexPath,
                        containmentRoot ?? string.Empty,
                        maxBytes);

                    if (codexPathResult.IsFailure)
                    {
                        return Results.BadRequest(
                            ApiResponse<PromptTestResultDto>.FromResult(
                                Result<PromptTestResultDto>.Failure(codexPathResult.Error),
                                traceId));
                    }

                    if (!ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(
                            Path.GetFullPath(containmentRoot ?? string.Empty),
                            codexPathResult.Value!,
                            out _))
                    {
                        return Results.BadRequest(
                            ApiResponse<PromptTestResultDto>.FromResult(
                                Result<PromptTestResultDto>.Failure(
                                    new Error(
                                        "Prompt.CodexPathNotContained",
                                        "The codex path must be under the prompt campaign or working directory.")),
                                traceId));
                    }

                    codexContent = await File
                        .ReadAllTextAsync(codexPathResult.Value, ctx.RequestAborted)
                        .ConfigureAwait(false);
                }

                ParsedSpell activeSpell = new(
                    prompt.Name,
                    prompt.Description ?? string.Empty,
                    string.Empty,
                    rendered.Value!.RenderedText,
                    string.Empty,
                    Array.Empty<string>())
                {
                    SystemPrompt = rendered.Value.RenderedText,
                };

                PingRequest ping = new(
                    Prompt: string.Empty,
                    WorkingDirectory: workingDirectory,
                    ContextSnapshot: request?.ContextSnapshot,
                    ChronosyncDelta: request?.ChronosyncDelta,
                    AttachedFiles: request?.AttachedFiles);

                string assembled = SystemPromptBuilder.Build(ping, codexContent, activeSpell);

                int tokenCount = manaMeter.CountTokens(assembled);

                IReadOnlyList<Microsoft.Extensions.AI.AITool> tools =
                    await mcpManager.GetAvailableToolsAsync(workingDirectory, ctx.RequestAborted).ConfigureAwait(false);

                PromptTestResultDto dto = new(
                    assembled,
                    tokenCount,
                    new ResolvedSpellInfoDto(prompt.Name, prompt.Version),
                    tools.Count);

                return Results.Ok(
                    ApiResponse<PromptTestResultDto>.FromResult(
                        Result<PromptTestResultDto>.Success(dto),
                        traceId));
            })
        .WithName("TestPrompt");

        apiGroup.MapPost(
            "/prompts/{id:guid}/export",
            async (Guid id, IPromptRepository repo, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Prompt? prompt = await repo.GetByIdAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                if (prompt is null)
                {
                    return Results.Json(
                        ApiResponse<PromptExportDto>.FromResult(
                            Result<PromptExportDto>.Failure(new Error("Prompt.NotFound", "No prompt exists with that identifier.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponsePromptExportDto,
                        statusCode: StatusCodes.Status404NotFound);
                }

                PromptExportDto export = PromptMapping.ToExportDto(prompt);

                return Results.Ok(
                    ApiResponse<PromptExportDto>.FromResult(Result<PromptExportDto>.Success(export), traceId));
            })
        .WithName("ExportPrompt");

        apiGroup.MapPost(
            "/prompts/import",
            async (PromptImportRequest? request, IPromptRepository repo, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (request is null)
                {
                    return Results.BadRequest(
                        ApiResponse<PromptSummaryDto>.FromResult(
                            Result<PromptSummaryDto>.Failure(new Error("Validation.InvalidBody", "Request body is required.")),
                            traceId));
                }

                Result<PromptSummaryDto> result = await PromptImportHelper
                    .ImportAsync(repo, request, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return result.IsSuccess
                    ? Results.Ok(ApiResponse<PromptSummaryDto>.FromResult(result, traceId))
                    : Results.BadRequest(ApiResponse<PromptSummaryDto>.FromResult(result, traceId));
            })
        .WithName("ImportPrompt");

        apiGroup.MapPost(
            "/prompts/{id:guid}/execute",
            async (
                Guid id,
                string? workspace,
                PromptExecuteRequest? body,
                IPromptRepository repo,
                PromptRenderer renderer,
                SpellWorkspaceResolver workspaceResolver,
                ICampaignRepository campaignRepository,
                IArcanumIntelligenceProvider intelligence,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (body is null || string.IsNullOrWhiteSpace(body.UserMessage))
                {
                    Result<PromptResponseDto> invalid = Result<PromptResponseDto>.Failure(
                        new Error("Validation.InvalidPrompt", "userMessage is required."));

                    return Results.Json(
                        ApiResponse<PromptResponseDto>.FromResult(invalid, traceId),
                        ArcanumJsonContext.Default.ApiResponsePromptResponseDto,
                        statusCode: StatusCodes.Status400BadRequest);
                }

                Prompt? prompt = await repo.GetByIdAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                if (prompt is null)
                {
                    Result<PromptResponseDto> notFound = Result<PromptResponseDto>.Failure(
                        new Error("Prompt.NotFound", "No prompt exists with that identifier."));

                    return Results.Json(
                        ApiResponse<PromptResponseDto>.FromResult(notFound, traceId),
                        ArcanumJsonContext.Default.ApiResponsePromptResponseDto,
                        statusCode: StatusCodes.Status404NotFound);
                }

                Result<PromptRenderResultDto> renderResult = renderer.Render(prompt, body.Parameters);

                if (renderResult.IsFailure)
                {
                    return Results.BadRequest(
                        ApiResponse<PromptResponseDto>.FromResult(
                            Result<PromptResponseDto>.Failure(renderResult.Error),
                            traceId));
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

                PingRequest ping = new(
                    Prompt: body.UserMessage,
                    Model: body.Model ?? prompt.Model,
                    WorkingDirectory: resolvedWorkspace ?? string.Empty,
                    SessionId: body.SessionId,
                    SkipSpellRouting: true,
                    Temperature: body.Temperature ?? (float?)prompt.Temperature,
                    TopP: body.TopP ?? (float?)prompt.TopP,
                    MaxOutputTokens: body.MaxOutputTokens ?? prompt.MaxOutputTokens,
                    Stop: body.Stop,
                    Seed: body.Seed,
                    ResponseFormat: body.ResponseFormat,
                    PresencePenalty: body.PresencePenalty,
                    FrequencyPenalty: body.FrequencyPenalty,
                    CampaignId: body.CampaignId,
                    ToolPolicy: body.ToolPolicy,
                    AdditionalSystemPrompt: renderResult.Value.RenderedText);

                Result<PingRequest> resolvedPing = await PingRequestResolver
                    .ResolveCampaignAsync(ping, campaignRepository, ctx.RequestAborted)
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
                        statusCode: InferenceErrorMapper.ResolveStatusCode(turn.Error.Code));
            })
        .WithName("Prompt_Execute");

        apiGroup.MapPost(
            "/prompts/{id:guid}/execute-stream",
            async (
                Guid id,
                string? workspace,
                PromptExecuteRequest? body,
                IPromptRepository repo,
                PromptRenderer renderer,
                SpellWorkspaceResolver workspaceResolver,
                ICampaignRepository campaignRepository,
                IArcanumIntelligenceProvider intelligence,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (body is null || string.IsNullOrWhiteSpace(body.UserMessage))
                {
                    Result<string> invalid = Result<string>.Failure(
                        new Error("Validation.InvalidPrompt", "userMessage is required."));

                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;

                    await ctx.Response
                        .WriteAsJsonAsync(ApiResponse<string>.FromResult(invalid, traceId), ArcanumJsonContext.Default.ApiResponseString, cancellationToken: ctx.RequestAborted)
                        .ConfigureAwait(false);

                    return;
                }

                Prompt? prompt = await repo.GetByIdAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                if (prompt is null)
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;

                    await ctx.Response
                        .WriteAsJsonAsync(
                            ApiResponse<string>.FromResult(
                                Result<string>.Failure(new Error("Prompt.NotFound", "No prompt exists with that identifier.")),
                                traceId),
                            ArcanumJsonContext.Default.ApiResponseString,
                            cancellationToken: ctx.RequestAborted)
                        .ConfigureAwait(false);

                    return;
                }

                Result<PromptRenderResultDto> renderResult = renderer.Render(prompt, body.Parameters);

                if (renderResult.IsFailure)
                {
                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;

                    await ctx.Response
                        .WriteAsJsonAsync(
                            ApiResponse<string>.FromResult(Result<string>.Failure(renderResult.Error), traceId),
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
                    ctx.Response.StatusCode = string.Equals(workspaceResult.Error.Code, "Spell.PathNotAllowed", StringComparison.Ordinal)
                        ? StatusCodes.Status403Forbidden
                        : StatusCodes.Status400BadRequest;

                    await ctx.Response
                        .WriteAsJsonAsync(
                            ApiResponse<string>.FromResult(Result<string>.Failure(workspaceResult.Error), traceId),
                            ArcanumJsonContext.Default.ApiResponseString,
                            cancellationToken: ctx.RequestAborted)
                        .ConfigureAwait(false);

                    return;
                }

                PingRequest ping = new(
                    Prompt: body.UserMessage,
                    Model: body.Model ?? prompt.Model,
                    WorkingDirectory: workspaceResult.Value ?? string.Empty,
                    SessionId: body.SessionId,
                    SkipSpellRouting: true,
                    Temperature: body.Temperature ?? (float?)prompt.Temperature,
                    TopP: body.TopP ?? (float?)prompt.TopP,
                    MaxOutputTokens: body.MaxOutputTokens ?? prompt.MaxOutputTokens,
                    Stop: body.Stop,
                    Seed: body.Seed,
                    ResponseFormat: body.ResponseFormat,
                    PresencePenalty: body.PresencePenalty,
                    FrequencyPenalty: body.FrequencyPenalty,
                    CampaignId: body.CampaignId,
                    ToolPolicy: body.ToolPolicy,
                    AdditionalSystemPrompt: renderResult.Value.RenderedText);

                Result<PingRequest> resolvedPing = await PingRequestResolver
                    .ResolveCampaignAsync(ping, campaignRepository, ctx.RequestAborted)
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
        .WithName("Prompt_ExecuteStream");

        return apiGroup;
    }

}
