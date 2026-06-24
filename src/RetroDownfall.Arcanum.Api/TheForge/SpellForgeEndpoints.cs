using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Spells;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Api.TheForge;

internal static class SpellForgeEndpoints
{

    public static RouteGroupBuilder MapSpellForgeEndpoints(this RouteGroupBuilder apiGroup)
    {
        apiGroup.MapGet(
            "/spells/search",
            async (
                string? q,
                string? tag,
                string? tool,
                SpellSource? source,
                Guid? campaignId,
                string? workspace,
                ISpellRepository repo,
                ICampaignRepository campaignRepo,
                SpellWorkspaceResolver workspaceResolver,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Result<string?> workspaceResult = workspaceResolver.Resolve(workspace);

                IResult? workspaceFailure = SpellApiResults.MapOptionalWorkspaceFailure<SpellSummary[]>(
                    workspaceResult,
                    traceId,
                    ArcanumJsonContext.Default.ApiResponseSpellSummaryArray,
                    out string? resolvedWorkspace);

                if (workspaceFailure is not null)
                {
                    return workspaceFailure;
                }

                ListPageResult<Campaign> campaignPage = await campaignRepo
                    .ListAsync(typeFilter: null, limit: ArcanumSettingClamps.ListQueryLimit(10_000), cancellationToken: ctx.RequestAborted)
                    .ConfigureAwait(false);

                IReadOnlyList<Campaign> campaigns = campaignPage.Items;

                SpellSearchQuery query = new(q, tag, tool, source, campaignId, resolvedWorkspace, campaigns);

                SpellSummary[] results = await repo.SearchAsync(query, ctx.RequestAborted).ConfigureAwait(false);

                return Results.Ok(
                    ApiResponse<SpellSummary[]>.FromResult(Result<SpellSummary[]>.Success(results), traceId));
            })
        .WithName("SearchSpells");

        apiGroup.MapPost(
            "/spells/{name}/validate",
            async (
                string name,
                string? workspace,
                ISpellRepository repo,
                SpellWorkspaceResolver workspaceResolver,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Result<string?> workspaceResult = workspaceResolver.Resolve(workspace);

                IResult? workspaceFailure = SpellApiResults.MapOptionalWorkspaceFailure<SpellValidationResultDto>(
                    workspaceResult,
                    traceId,
                    ArcanumJsonContext.Default.ApiResponseSpellValidationResultDto,
                    out string? resolvedWorkspace);

                if (workspaceFailure is not null)
                {
                    return workspaceFailure;
                }

                SpellValidationResultDto result = await repo
                    .ValidateAsync(name, resolvedWorkspace, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return Results.Ok(
                    ApiResponse<SpellValidationResultDto>.FromResult(
                        Result<SpellValidationResultDto>.Success(result),
                        traceId));
            })
        .WithName("ValidateSpell");

        apiGroup.MapPost(
            "/spells/{name}/export",
            async (
                string name,
                string? workspace,
                ISpellRepository repo,
                SpellWorkspaceResolver workspaceResolver,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Result<string?> workspaceResult = workspaceResolver.Resolve(workspace);

                IResult? workspaceFailure = SpellApiResults.MapOptionalWorkspaceFailure<SpellExportDto>(
                    workspaceResult,
                    traceId,
                    ArcanumJsonContext.Default.ApiResponseSpellExportDto,
                    out string? resolvedWorkspace);

                if (workspaceFailure is not null)
                {
                    return workspaceFailure;
                }

                SpellExportDto? exported = await repo
                    .ExportAsync(name, resolvedWorkspace, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (exported is null)
                {
                    return Results.Json(
                        ApiResponse<SpellExportDto>.FromResult(
                            Result<SpellExportDto>.Failure(new Error("Spell.NotFound", "No spell exists with that name.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseSpellExportDto,
                        statusCode: StatusCodes.Status404NotFound);
                }

                return Results.Ok(
                    ApiResponse<SpellExportDto>.FromResult(Result<SpellExportDto>.Success(exported), traceId));
            })
        .WithName("ExportSpell");

        apiGroup.MapPost(
            "/spells/import",
            async (
                SpellImportRequest? request,
                ISpellRepository repo,
                SpellWorkspaceResolver workspaceResolver,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (request is null)
                {
                    return Results.BadRequest(
                        ApiResponse<SpellSummary>.FromResult(
                            Result<SpellSummary>.Failure(new Error("Validation.InvalidBody", "Request body is required.")),
                            traceId));
                }

                string? workspace = request.Workspace;

                Result<string> workspaceRequired = workspaceResolver.ResolveRequired(workspace);

                IResult? workspaceFailure = SpellApiResults.MapRequiredWorkspaceFailure<SpellSummary>(
                    workspaceRequired,
                    traceId,
                    ArcanumJsonContext.Default.ApiResponseSpellSummary,
                    out string resolvedWorkspace);

                if (workspaceFailure is not null)
                {
                    return workspaceFailure;
                }

                SpellImportRequest resolved = request with { Workspace = resolvedWorkspace };

                Result<SpellSummary> result = await repo.ImportAsync(resolved, ctx.RequestAborted).ConfigureAwait(false);

                return result.IsSuccess
                    ? Results.Ok(ApiResponse<SpellSummary>.FromResult(result, traceId))
                    : Results.BadRequest(ApiResponse<SpellSummary>.FromResult(result, traceId));
            })
        .WithName("ImportSpell")
        .WithLargeRequestBody();

        return apiGroup;
    }

}
