using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
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
                            Result<CampaignDto>.Failure(new Error("Campaign.InvalidPath", "path query parameter is required.")),
                            traceId));
                }

                Campaign? campaign = await repo.GetByPathAsync(path, ctx.RequestAborted).ConfigureAwait(false);

                if (campaign is null)
                {
                    return Results.Json(
                        ApiResponse<CampaignDto>.FromResult(
                            Result<CampaignDto>.Failure(new Error("Campaign.NotFound", "No campaign is registered at that path.")),
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
                            Result<CampaignDto>.Failure(new Error("Campaign.NotFound", "No campaign exists with that identifier.")),
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
                            Result<CampaignDto>.Failure(new Error("Validation.InvalidBody", "Request body is required.")),
                            traceId));
                }

                if (string.IsNullOrWhiteSpace(request.Name))
                {
                    return Results.BadRequest(
                        ApiResponse<CampaignDto>.FromResult(
                            Result<CampaignDto>.Failure(new Error("Campaign.InvalidName", "Campaign name is required.")),
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
                            Result<CampaignDto>.Failure(new Error("Campaign.DuplicateName", "A campaign with this name already exists.")),
                            traceId));
                }

                if (await repo.GetByPathAsync(normalizedPath, ctx.RequestAborted).ConfigureAwait(false) is not null)
                {
                    return Results.BadRequest(
                        ApiResponse<CampaignDto>.FromResult(
                            Result<CampaignDto>.Failure(new Error("Campaign.DuplicatePath", "A campaign with this path already exists.")),
                            traceId));
                }

                DateTimeOffset now = DateTimeOffset.UtcNow;

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

                try
                {
                    await repo.AddAsync(campaign, ctx.RequestAborted).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex) when (ex.Message == "Campaign.MaxReached")
                {
                    return Results.BadRequest(
                        ApiResponse<CampaignDto>.FromResult(
                            Result<CampaignDto>.Failure(new Error("Campaign.MaxReached", "The maximum number of campaigns has been reached.")),
                            traceId));
                }

                Directory.CreateDirectory(Path.Combine(normalizedPath, ".arcanum"));

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
                            Result<CampaignDto>.Failure(new Error("Validation.InvalidBody", "Request body is required.")),
                            traceId));
                }

                Campaign? existing = await repo.GetByIdAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                if (existing is null)
                {
                    return Results.Json(
                        ApiResponse<CampaignDto>.FromResult(
                            Result<CampaignDto>.Failure(new Error("Campaign.NotFound", "No campaign exists with that identifier.")),
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
                                Result<CampaignDto>.Failure(new Error("Campaign.InvalidName", "Campaign name cannot be empty.")),
                                traceId));
                    }

                    string trimmed = request.Name.Trim();

                    Campaign? nameConflict = await repo.GetByNameAsync(trimmed, ctx.RequestAborted).ConfigureAwait(false);

                    if (nameConflict is not null && nameConflict.Id != id)
                    {
                        return Results.BadRequest(
                            ApiResponse<CampaignDto>.FromResult(
                                Result<CampaignDto>.Failure(new Error("Campaign.DuplicateName", "A campaign with this name already exists.")),
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
                            Result<bool>.Failure(new Error("Campaign.NotFound", "No campaign exists with that identifier.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseBoolean,
                        statusCode: StatusCodes.Status404NotFound);
                }

                return Results.NoContent();
            })
        .WithName("DeleteCampaign");

        apiGroup.MapPost(
            "/campaigns/{id:guid}/export",
            async (Guid id, ICampaignRepository repo, IPromptRepository promptRepo, ISpellRepository spellRepo, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Campaign? campaign = await repo.GetByIdAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                if (campaign is null)
                {
                    return Results.Json(
                        ApiResponse<CampaignDto>.FromResult(
                            Result<CampaignDto>.Failure(new Error("Campaign.NotFound", "No campaign exists with that identifier.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseCampaignDto,
                        statusCode: StatusCodes.Status404NotFound);
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

                    string? skillJson = exported.Metadata is null
                        ? null
                        : JsonSerializer.Serialize(exported.Metadata, TheForgeJsonContext.Default.SkillMetadata);

                    exportSpells.Add(new CampaignExportSpellDto(
                        summary.Name,
                        skillJson,
                        exported.FullContent,
                        exported.Scripts.Select(s => new CampaignExportScriptDto(s.FileName, s.Base64Content)).ToList()));
                }

                ListPageResult<Prompt> promptPage = await promptRepo
                    .ListAsync(id, ArcanumSettingClamps.ListQueryLimit(10_000), cancellationToken: ctx.RequestAborted)
                    .ConfigureAwait(false);

                PromptExportDto[] promptExports = promptPage.Items.Select(PromptMapping.ToExportDto).ToArray();

                CampaignExportDto export = new(
                    CampaignPathPolicy.ToDto(campaign),
                    exportSpells,
                    promptExports);

                return Results.Ok(
                    ApiResponse<CampaignExportDto>.FromResult(Result<CampaignExportDto>.Success(export), traceId));
            })
        .WithName("ExportCampaign");

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
                            Result<CampaignDto>.Failure(new Error("Campaign.NotFound", "No campaign exists with that identifier.")),
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
                                    new Error("Campaign.ImportFailed", "No import payload and no campaign.json on disk.")),
                                traceId));
                    }

                    string json = await File.ReadAllTextAsync(diskPath, ctx.RequestAborted).ConfigureAwait(false);

                    payload = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.CampaignExportDto);

                    if (payload is null)
                    {
                        return Results.BadRequest(
                            ApiResponse<CampaignImportResultDto>.FromResult(
                                Result<CampaignImportResultDto>.Failure(
                                    new Error("Campaign.ImportFailed", "Could not parse campaign.json.")),
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

                    if (!string.IsNullOrWhiteSpace(spell.SkillJson))
                    {
                        metadata = JsonSerializer.Deserialize(spell.SkillJson, TheForgeJsonContext.Default.SkillMetadata);
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
        .WithName("ImportCampaign");

        return apiGroup;
    }

    private static IResult MapCampaignError(Error error, string traceId)
    {
        ApiResponse<CampaignDto> response = ApiResponse<CampaignDto>.FromResult(Result<CampaignDto>.Failure(error), traceId);

        if (string.Equals(error.Code, "Campaign.PathNotAllowed", StringComparison.Ordinal))
        {
            return Results.Json(response, ArcanumJsonContext.Default.ApiResponseCampaignDto, statusCode: StatusCodes.Status403Forbidden);
        }

        return Results.BadRequest(response);
    }

}
