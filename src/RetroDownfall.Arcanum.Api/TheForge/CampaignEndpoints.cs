using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Api.TheForge;

internal static class CampaignEndpoints
{

    public static RouteGroupBuilder MapCampaignEndpoints(this RouteGroupBuilder apiGroup)
    {
        apiGroup.MapGet(
            "/campaigns",
            async (WorkspaceType? type, int? limit, int? offset, ICampaignRepository repo, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                ListPageResult<Campaign> page = await repo
                    .ListAsync(type, limit, offset ?? 0, ctx.RequestAborted)
                    .ConfigureAwait(false);

                CampaignDto[] dtos = page.Items.Select(CampaignPathPolicy.ToDto).ToArray();

                ListPageResult<CampaignDto> response = new(dtos, page.HasMore, page.NextOffset);

                return Results.Ok(
                    ApiResponse<ListPageResult<CampaignDto>>.FromResult(
                        Result<ListPageResult<CampaignDto>>.Success(response),
                        traceId));
            })
        .WithName("ListCampaigns");

        apiGroup.MapGet(
            "/campaigns/by-path",
            async (string path, ICampaignRepository repo, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (string.IsNullOrWhiteSpace(path))
                {
                    return Results.BadRequest(
                        ApiResponse<CampaignDto>.FromResult(
                            Result<CampaignDto>.Failure(new Error(ErrorCodes.Campaign.InvalidPath, "path query parameter is required.")),
                            traceId));
                }

                Campaign? campaign = await repo.GetByPathAsync(path, ctx.RequestAborted).ConfigureAwait(false);

                if (campaign is null)
                {
                    return Results.Json(
                        ApiResponse<CampaignDto>.FromResult(
                            Result<CampaignDto>.Failure(new Error(ErrorCodes.Campaign.NotFound, "No campaign is registered at that path.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseCampaignDto,
                        statusCode: StatusCodes.Status404NotFound);
                }

                return Results.Ok(
                    ApiResponse<CampaignDto>.FromResult(
                        Result<CampaignDto>.Success(CampaignPathPolicy.ToDto(campaign)),
                        traceId));
            })
        .WithName("GetCampaignByPath");

        apiGroup.MapGet(
            "/campaigns/{id:guid}",
            async (Guid id, ICampaignRepository repo, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Campaign? campaign = await repo.GetByIdAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                if (campaign is null)
                {
                    return Results.Json(
                        ApiResponse<CampaignDto>.FromResult(
                            Result<CampaignDto>.Failure(new Error(ErrorCodes.Campaign.NotFound, "No campaign exists with that identifier.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseCampaignDto,
                        statusCode: StatusCodes.Status404NotFound);
                }

                return Results.Ok(
                    ApiResponse<CampaignDto>.FromResult(
                        Result<CampaignDto>.Success(CampaignPathPolicy.ToDto(campaign)),
                        traceId));
            })
        .WithName("GetCampaign");

        apiGroup.MapPost(
            "/campaigns",
            async (
                RegisterCampaignRequest? request,
                ICampaignRepository repo,
                IOptionsSnapshot<ArcanumSettings> settings,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (request is null)
                {
                    return Results.BadRequest(
                        ApiResponse<CampaignDto>.FromResult(
                            Result<CampaignDto>.Failure(new Error(ErrorCodes.Validation.InvalidBody, "Request body is required.")),
                            traceId));
                }

                if (string.IsNullOrWhiteSpace(request.Name))
                {
                    return Results.BadRequest(
                        ApiResponse<CampaignDto>.FromResult(
                            Result<CampaignDto>.Failure(new Error(ErrorCodes.Campaign.InvalidName, "Campaign name is required.")),
                            traceId));
                }

                Result<string> pathResult = Core.Configuration.CampaignPathPolicy.ValidateAndNormalizePath(request.Path, settings.Value);

                if (pathResult.IsFailure)
                {
                    return MapCampaignError(pathResult.Error, traceId);
                }

                string normalizedPath = pathResult.Value!;

                if (await repo.GetByNameAsync(request.Name.Trim(), ctx.RequestAborted).ConfigureAwait(false) is not null)
                {
                    return Results.BadRequest(
                        ApiResponse<CampaignDto>.FromResult(
                            Result<CampaignDto>.Failure(new Error(ErrorCodes.Campaign.DuplicateName, "A campaign with this name already exists.")),
                            traceId));
                }

                if (await repo.GetByPathAsync(normalizedPath, ctx.RequestAborted).ConfigureAwait(false) is not null)
                {
                    return Results.BadRequest(
                        ApiResponse<CampaignDto>.FromResult(
                            Result<CampaignDto>.Failure(new Error(ErrorCodes.Campaign.DuplicatePath, "A campaign with this path already exists.")),
                            traceId));
                }

                DateTimeOffset now = DateTimeOffset.UtcNow;

                try
                {
                    Directory.CreateDirectory(Path.Combine(normalizedPath, ".arcanum"));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return Results.BadRequest(
                        ApiResponse<CampaignDto>.FromResult(
                            Result<CampaignDto>.Failure(
                                new Error(
                                    ErrorCodes.Campaign.DirectoryCreateFailed,
                                    "Could not create the campaign .arcanum directory at the requested path.")),
                            traceId));
                }

                Campaign campaign = new()
                {
                    Id = Guid.NewGuid(),
                    Name = request.Name.Trim(),
                    Path = normalizedPath,
                    Type = request.Type,
                    Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
                    Settings = CampaignRepository.SerializeSettings(CampaignPathPolicy.DefaultSettings()),
                    SanctumConfigJson = CampaignRepository.SerializeSanctumConfig(CampaignRepository.DefaultSanctumConfig()),
                    CreatedAt = now,
                    UpdatedAt = now,
                };

                Result<Campaign> addResult = await repo
                    .AddAsync(campaign, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (addResult.IsFailure)
                {
                    return MapCampaignError(addResult.Error, traceId);
                }

                CampaignDto dto = CampaignPathPolicy.ToDto(campaign);

                return Results.Created(
                    $"/api/campaigns/{campaign.Id}",
                    ApiResponse<CampaignDto>.FromResult(Result<CampaignDto>.Success(dto), traceId));
            })
        .WithName("RegisterCampaign");

        apiGroup.MapPut(
            "/campaigns/{id:guid}",
            async (Guid id, UpdateCampaignRequest? request, ICampaignRepository repo, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (request is null)
                {
                    return Results.BadRequest(
                        ApiResponse<CampaignDto>.FromResult(
                            Result<CampaignDto>.Failure(new Error(ErrorCodes.Validation.InvalidBody, "Request body is required.")),
                            traceId));
                }

                Campaign? existing = await repo.GetByIdAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                if (existing is null)
                {
                    return Results.Json(
                        ApiResponse<CampaignDto>.FromResult(
                            Result<CampaignDto>.Failure(new Error(ErrorCodes.Campaign.NotFound, "No campaign exists with that identifier.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseCampaignDto,
                        statusCode: StatusCodes.Status404NotFound);
                }

                if (request.Name is not null)
                {
                    if (string.IsNullOrWhiteSpace(request.Name))
                    {
                        return Results.BadRequest(
                            ApiResponse<CampaignDto>.FromResult(
                                Result<CampaignDto>.Failure(new Error(ErrorCodes.Campaign.InvalidName, "Campaign name cannot be empty.")),
                                traceId));
                    }

                    string trimmed = request.Name.Trim();

                    Campaign? nameConflict = await repo.GetByNameAsync(trimmed, ctx.RequestAborted).ConfigureAwait(false);

                    if (nameConflict is not null && nameConflict.Id != id)
                    {
                        return Results.BadRequest(
                            ApiResponse<CampaignDto>.FromResult(
                                Result<CampaignDto>.Failure(new Error(ErrorCodes.Campaign.DuplicateName, "A campaign with this name already exists.")),
                                traceId));
                    }

                    existing.Name = trimmed;
                }

                if (request.Type is { } type)
                {
                    existing.Type = type;
                }

                if (request.Description is not null)
                {
                    existing.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
                }

                if (request.Settings is not null)
                {
                    existing.Settings = CampaignRepository.SerializeSettings(request.Settings);
                }

                existing.UpdatedAt = DateTimeOffset.UtcNow;

                await repo.UpdateAsync(existing, ctx.RequestAborted).ConfigureAwait(false);

                return Results.Ok(
                    ApiResponse<CampaignDto>.FromResult(
                        Result<CampaignDto>.Success(CampaignPathPolicy.ToDto(existing)),
                        traceId));
            })
        .WithName("UpdateCampaign");

        apiGroup.MapDelete(
            "/campaigns/{id:guid}",
            async (Guid id, ICampaignRepository repo, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                bool deleted = await repo.DeleteAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                if (!deleted)
                {
                    return Results.Json(
                        ApiResponse<bool>.FromResult(
                            Result<bool>.Failure(new Error(ErrorCodes.Campaign.NotFound, "No campaign exists with that identifier.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseBoolean,
                        statusCode: StatusCodes.Status404NotFound);
                }

                return Results.NoContent();
            })
        .WithName("DeleteCampaign");

        apiGroup.MapGet(
            "/campaigns/{id:guid}/spells",
            async (
                Guid id,
                string? q,
                string? tag,
                string? tool,
                ICampaignRepository repo,
                ISpellRepository spellRepo,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Campaign? campaign = await repo.GetByIdAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                if (campaign is null)
                {
                    return Results.Json(
                        ApiResponse<SpellSummary[]>.FromResult(
                            Result<SpellSummary[]>.Failure(new Error(ErrorCodes.Campaign.NotFound, "No campaign exists with that identifier.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseSpellSummaryArray,
                        statusCode: StatusCodes.Status404NotFound);
                }

                SpellSearchQuery query = new(q, tag, tool, Source: null, CampaignId: id, Workspace: campaign.Path, Campaigns: []);

                SpellSummary[] results = await spellRepo.SearchAsync(query, ctx.RequestAborted).ConfigureAwait(false);

                return Results.Ok(
                    ApiResponse<SpellSummary[]>.FromResult(Result<SpellSummary[]>.Success(results), traceId));
            })
        .WithName("GetCampaignSpells");

        apiGroup.MapGet(
            "/campaigns/{id:guid}/prompts",
            async (
                Guid id,
                string? q,
                string? tag,
                ICampaignRepository repo,
                IPromptRepository promptRepo,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Campaign? campaign = await repo.GetByIdAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                if (campaign is null)
                {
                    return Results.Json(
                        ApiResponse<ListPageResult<PromptSummaryDto>>.FromResult(
                            Result<ListPageResult<PromptSummaryDto>>.Failure(new Error(ErrorCodes.Campaign.NotFound, "No campaign exists with that identifier.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseListPageResultPromptSummaryDto,
                        statusCode: StatusCodes.Status404NotFound);
                }

                ListPageResult<Prompt> page = await promptRepo
                    .ListAsync(id, ArcanumSettingClamps.ListQueryLimit(10_000), cancellationToken: ctx.RequestAborted)
                    .ConfigureAwait(false);

                IEnumerable<PromptSummaryDto> filtered = page.Items.Select(PromptMapping.ToSummaryDto);

                if (!string.IsNullOrWhiteSpace(q))
                {
                    string term = q.Trim();

                    filtered = filtered.Where(p =>
                        p.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                        || (p.Description?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                        || p.Tags.Any(t => t.Contains(term, StringComparison.OrdinalIgnoreCase)));
                }

                if (!string.IsNullOrWhiteSpace(tag))
                {
                    filtered = filtered.Where(p => p.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)));
                }

                PromptSummaryDto[] result = filtered.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();

                ListPageResult<PromptSummaryDto> response = new(result, false);

                return Results.Ok(
                    ApiResponse<ListPageResult<PromptSummaryDto>>.FromResult(
                        Result<ListPageResult<PromptSummaryDto>>.Success(response),
                        traceId));
            })
        .WithName("GetCampaignPrompts");

        apiGroup.MapGet(
            "/campaigns/{id:guid}/sessions",
            async (
                Guid id,
                string? status,
                string? search,
                int? limit,
                DateTimeOffset? beforeUpdatedAt,
                ICampaignRepository repo,
                ISessionRepository sessionRepo,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Campaign? campaign = await repo.GetByIdAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                if (campaign is null)
                {
                    return Results.Json(
                        ApiResponse<SessionQueryResult>.FromResult(
                            Result<SessionQueryResult>.Failure(new Error(ErrorCodes.Campaign.NotFound, "No campaign exists with that identifier.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseSessionQueryResult,
                        statusCode: StatusCodes.Status404NotFound);
                }

                SessionQueryRequest request = new(
                    CampaignId: id,
                    Status: status,
                    Search: search,
                    Limit: limit,
                    BeforeUpdatedAt: beforeUpdatedAt);

                SessionQueryResult result = await sessionRepo.QueryAsync(request, ctx.RequestAborted).ConfigureAwait(false);

                return Results.Ok(
                    ApiResponse<SessionQueryResult>.FromResult(Result<SessionQueryResult>.Success(result), traceId));
            })
        .WithName("GetCampaignSessions");

        apiGroup.MapPost(
            "/campaigns/{id:guid}/export",
            async (Guid id, ICampaignRepository repo, IPromptRepository promptRepo, ISpellRepository spellRepo, HttpContext ctx) =>
                await ExportCampaignAsync(id, repo, promptRepo, spellRepo, ctx).ConfigureAwait(false))
        .WithName("ExportCampaign")
        .WithLargeRequestBody()
        .RequireConditionalCovenantReadAuthority();

        apiGroup.MapPost(
            "/campaigns/{id:guid}/import",
            async (
                Guid id,
                CampaignImportRequest? request,
                ICampaignRepository repo,
                IPromptRepository promptRepo,
                ISpellRepository spellRepo,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Campaign? campaign = await repo.GetByIdAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                if (campaign is null)
                {
                    return Results.Json(
                        ApiResponse<CampaignDto>.FromResult(
                            Result<CampaignDto>.Failure(new Error(ErrorCodes.Campaign.NotFound, "No campaign exists with that identifier.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseCampaignDto,
                        statusCode: StatusCodes.Status404NotFound);
                }

                string strategy = string.IsNullOrWhiteSpace(request?.Strategy) ? "merge" : request.Strategy.Trim();

                CampaignExportDto? payload = request?.Payload;

                if (payload is null)
                {
                    string diskPath = Path.Combine(campaign.Path, ".arcanum", "campaign.json");

                    if (!File.Exists(diskPath))
                    {
                        return Results.BadRequest(
                            ApiResponse<CampaignImportResultDto>.FromResult(
                                Result<CampaignImportResultDto>.Failure(
                                    new Error(ErrorCodes.Campaign.ImportFailed, "No import payload and no campaign.json on disk.")),
                                traceId));
                    }

                    string json = await File.ReadAllTextAsync(diskPath, ctx.RequestAborted).ConfigureAwait(false);

                    payload = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.CampaignExportDto);

                    if (payload is null)
                    {
                        return Results.BadRequest(
                            ApiResponse<CampaignImportResultDto>.FromResult(
                                Result<CampaignImportResultDto>.Failure(
                                    new Error(ErrorCodes.Campaign.ImportFailed, "Could not parse campaign.json.")),
                                traceId));
                    }
                }

                int spellsImported = 0;

                int promptsImported = 0;

                var warnings = new List<string>();

                if (string.Equals(strategy, "replace", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (Prompt p in (await promptRepo
                        .ListAsync(id, ArcanumSettingClamps.ListQueryLimit(10_000), cancellationToken: ctx.RequestAborted)
                        .ConfigureAwait(false)).Items)
                    {
                        await promptRepo.DeleteAsync(p.Id, ctx.RequestAborted).ConfigureAwait(false);
                    }
                }

                if (payload.Campaign.Settings is not null)
                {
                    campaign.Settings = CampaignRepository.SerializeSettings(payload.Campaign.Settings);

                    campaign.UpdatedAt = DateTimeOffset.UtcNow;

                    await repo.UpdateAsync(campaign, ctx.RequestAborted).ConfigureAwait(false);
                }

                foreach (CampaignExportSpellDto spell in payload.Spells)
                {
                    SkillMetadata? metadata = null;

                    if (!string.IsNullOrWhiteSpace(spell.ResolvedSpellJson))
                    {
                        metadata = JsonSerializer.Deserialize(spell.ResolvedSpellJson, TheForgeJsonContext.Default.SkillMetadata);
                    }

                    SpellImportRequest importReq = new(
                        new SpellExportDto(metadata, spell.FullContent, spell.Scripts
                            .Select(s => new SpellExportScriptDto(s.FileName, s.Base64Content))
                            .ToList()),
                        campaign.Path,
                        id);

                    Result<SpellSummary> importResult = await spellRepo.ImportAsync(importReq, ctx.RequestAborted).ConfigureAwait(false);

                    if (importResult.IsSuccess)
                    {
                        spellsImported++;
                    }
                    else
                    {
                        warnings.Add($"Spell '{spell.Name}': {importResult.Error.Message}");
                    }
                }

                foreach (PromptExportDto promptExport in payload.Prompts)
                {
                    Result<PromptSummaryDto> importResult = await PromptImportHelper
                        .ImportAsync(promptRepo, new PromptImportRequest(promptExport, id), ctx.RequestAborted)
                        .ConfigureAwait(false);

                    if (importResult.IsSuccess)
                    {
                        promptsImported++;
                    }
                    else
                    {
                        warnings.Add($"Prompt '{promptExport.Name}/{promptExport.Version}': {importResult.Error.Message}");
                    }
                }

                return Results.Ok(
                    ApiResponse<CampaignImportResultDto>.FromResult(
                        Result<CampaignImportResultDto>.Success(new CampaignImportResultDto(1, spellsImported, promptsImported, warnings)),
                        traceId));
            })
        .WithName("ImportCampaign")
        .WithLargeRequestBody();

        return apiGroup;
    }

    /// <summary>
    /// Exports one Campaign's spells, prompts, and settings, and reports what it left behind.
    /// </summary>
    /// <remarks>
    /// The bundle has never carried Covenant memory and does not start now: this route composes the
    /// same three sources it always did. What changes is that the omission is now stated. A Campaign
    /// whose Covenant memory was simply left out looked, on the wire, exactly like a Campaign that
    /// never had any — and an operator moving a Campaign to another machine had no way to tell those
    /// two apart (§10.19.11).
    ///
    /// <para>The lease is taken before the inventory and before the Campaign lookup, and is retained
    /// through serialization. Counting under one snapshot and serializing under none would report an
    /// exclusion figure for a generation the bundle beside it does not belong to.</para>
    /// </remarks>
    private static async Task<IResult> ExportCampaignAsync(
        Guid id,
        ICampaignRepository repo,
        IPromptRepository promptRepo,
        ISpellRepository spellRepo,
        HttpContext ctx)
    {

        string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

        ICovenantExportPolicy? policy = ctx.RequestServices.GetService<ICovenantExportPolicy>();

        if (policy is null)
        {

            return await BuildCampaignExportAsync(
                id,
                exclusions: null,
                repo,
                promptRepo,
                spellRepo,
                ctx,
                traceId).ConfigureAwait(false);

        }

        Result<CovenantExportAdmission> admission = await policy
            .AcquireConditionalReadAsync(CovenantOperationScope.ForCampaign(id), ctx.RequestAborted)
            .ConfigureAwait(false);

        if (admission.IsFailure)
        {

            return Results.Json(
                ApiResponse<CampaignExportDto>.FromResult(
                    Result<CampaignExportDto>.Failure(admission.Error),
                    traceId),
                ArcanumJsonContext.Default.ApiResponseCampaignExportDto,
                statusCode: ArcanumErrorMapper.ResolveStatusCodeDefaultBadRequest(admission.Error.Code));

        }

        ICovenantSnapshotReadLease? owned = admission.Value.ReadLease;

        if (owned is null)
        {

            return await BuildCampaignExportAsync(
                id,
                exclusions: null,
                repo,
                promptRepo,
                spellRepo,
                ctx,
                traceId).ConfigureAwait(false);

        }

        try
        {

            Result<CovenantCampaignExportExclusions> exclusions = await policy
                .InventoryCampaignExclusionsAsync(id, owned, ctx.RequestAborted)
                .ConfigureAwait(false);

            Result<CampaignExportDto> result = exclusions.IsFailure
                ? Result<CampaignExportDto>.Failure(exclusions.Error)
                : await ComposeCampaignExportAsync(
                    id,
                    new CampaignExportExclusionsDto(
                        exclusions.Value.CovenantEntryCount,
                        exclusions.Value.TaintedArtifactCount),
                    repo,
                    promptRepo,
                    spellRepo,
                    ctx).ConfigureAwait(false);

            IResult response = new CovenantProtectedJsonResult<CampaignExportDto>(
                owned,
                result,
                ArcanumJsonContext.Default.ApiResponseCampaignExportDto);

            owned = null;

            return response;

        }
        finally
        {

            if (owned is not null)
            {

                await owned.DisposeAsync().ConfigureAwait(false);

            }

        }

    }

    private static async Task<IResult> BuildCampaignExportAsync(
        Guid id,
        CampaignExportExclusionsDto? exclusions,
        ICampaignRepository repo,
        IPromptRepository promptRepo,
        ISpellRepository spellRepo,
        HttpContext ctx,
        string traceId)
    {

        Result<CampaignExportDto> result = await ComposeCampaignExportAsync(
            id,
            exclusions,
            repo,
            promptRepo,
            spellRepo,
            ctx).ConfigureAwait(false);

        return result.IsSuccess
            ? Results.Ok(ApiResponse<CampaignExportDto>.FromResult(result, traceId))
            : Results.Json(
                ApiResponse<CampaignExportDto>.FromResult(result, traceId),
                ArcanumJsonContext.Default.ApiResponseCampaignExportDto,
                statusCode: ArcanumErrorMapper.ResolveStatusCodeDefaultBadRequest(result.Error.Code));

    }

    /// <summary>
    /// Composes the bundle itself: the Campaign's own settings, its non-builtin spells, and its
    /// prompts. Deliberately the only place that reads any of them, so the protected and unprotected
    /// arms cannot come to carry different content.
    /// </summary>
    private static async Task<Result<CampaignExportDto>> ComposeCampaignExportAsync(
        Guid id,
        CampaignExportExclusionsDto? exclusions,
        ICampaignRepository repo,
        IPromptRepository promptRepo,
        ISpellRepository spellRepo,
        HttpContext ctx)
    {

        Campaign? campaign = await repo.GetByIdAsync(id, ctx.RequestAborted).ConfigureAwait(false);

        if (campaign is null)
        {

            return Result<CampaignExportDto>.Failure(
                new Error(ErrorCodes.Campaign.NotFound, "No campaign exists with that identifier."));

        }

        SpellSummary[] summaries = await spellRepo.ListAsync(campaign.Path, ctx.RequestAborted).ConfigureAwait(false);

        var exportSpells = new List<CampaignExportSpellDto>();

        foreach (SpellSummary summary in summaries)
        {
            if (summary.Source == SpellSource.Builtin)
            {
                continue;
            }

            SpellExportDto? exported = await spellRepo
                .ExportAsync(summary.Name, campaign.Path, ctx.RequestAborted)
                .ConfigureAwait(false);

            if (exported is null)
            {
                continue;
            }

            string? spellJson = exported.Metadata is null
                ? null
                : JsonSerializer.Serialize(exported.Metadata, TheForgeJsonContext.Default.SkillMetadata);

            exportSpells.Add(new CampaignExportSpellDto(
                summary.Name,
                spellJson,
                exported.FullContent,
                exported.Scripts.Select(s => new CampaignExportScriptDto(s.FileName, s.Base64Content)).ToList()));
        }

        ListPageResult<Prompt> promptPage = await promptRepo
            .ListAsync(id, ArcanumSettingClamps.ListQueryLimit(10_000), cancellationToken: ctx.RequestAborted)
            .ConfigureAwait(false);

        PromptExportDto[] promptExports = promptPage.Items.Select(PromptMapping.ToExportDto).ToArray();

        return Result<CampaignExportDto>.Success(new CampaignExportDto(
            CampaignPathPolicy.ToDto(campaign),
            exportSpells,
            promptExports,
            exclusions));

    }

    private static IResult MapCampaignError(Error error, string traceId)
    {
        ApiResponse<CampaignDto> response = ApiResponse<CampaignDto>.FromResult(Result<CampaignDto>.Failure(error), traceId);

        if (string.Equals(error.Code, ErrorCodes.Campaign.PathNotAllowed, StringComparison.Ordinal))
        {

            return Results.Json(
                response,
                ArcanumJsonContext.Default.ApiResponseCampaignDto,
                statusCode: ArcanumErrorMapper.ResolveStatusCode(error.Code));

        }

        return Results.Json(
            response,
            ArcanumJsonContext.Default.ApiResponseCampaignDto,
            statusCode: ArcanumErrorMapper.ResolveStatusCodeDefaultBadRequest(error.Code));
    }

}
