using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Primitives;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Api.Tower;

/// <summary>
/// Session Divination via <c>POST /api/sessions/divine</c>: semantic search over Grimoire entries embedded by
/// <c>EntryWeavingService</c>. Every step degrades gracefully when The Weave is disabled or unavailable
/// — see the graceful-degradation matrix in <c>docs/Arcanum.DESIGN.md</c> §21.4.
/// </summary>
internal static class SessionDivinationEndpoints
{
    // Mirrors Sessions.Status's DB constraint (ArcanumDbContext: HasMaxLength(32)) and the only two
    // values ever written to that column (SessionRepository, SessionEndpoints). Allow-listing here
    // means an unrecognized/garbage status fails fast with a clear 400 instead of silently matching
    // zero rows.
    private static readonly string[] AllowedStatuses = ["active", "archived"];

    public static RouteGroupBuilder MapSessionDivinationEndpoints(this RouteGroupBuilder apiGroup)
    {
        apiGroup.MapPost(
            "/sessions/divine",
            async (
                SemanticSearchRequest? request,
                IWeaveService weaveService,
                ISessionDivinationService divinationService,
                IOptionsMonitor<ArcanumSettings> options,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                EmbeddingSettings embeddings = options.CurrentValue.ResolveEmbeddings();

                if (!embeddings.Enabled || !embeddings.SessionSearchEnabled)
                {
                    return FailureResult(
                        traceId,
                        new Error(
                            ErrorCodes.Embeddings.FeatureDisabled,
                            "Session semantic search is disabled (Arcanum:Features:Embeddings and Arcanum:Features:SessionSearch must both be true)."));
                }

                if (request is null || string.IsNullOrWhiteSpace(request.Query))
                {
                    return FailureResult(
                        traceId,
                        new Error(ErrorCodes.Validation.InvalidBody, "Query is required."));
                }

                if (request.Status is { } status
                    && !AllowedStatuses.Contains(status.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    return FailureResult(
                        traceId,
                        new Error(
                            ErrorCodes.Validation.InvalidBody,
                            $"Status must be one of: {string.Join(", ", AllowedStatuses)}."));
                }

                if (!weaveService.IsAvailable)
                {
                    return FailureResult(
                        traceId,
                        new Error(ErrorCodes.Embeddings.ProviderUnavailable, "The embedding provider is unavailable."));
                }

                Result<Embedding<float>> embedResult = await weaveService
                    .EmbedAsync(request.Query.Trim(), ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (embedResult.IsFailure)
                {
                    return FailureResult(traceId, embedResult.Error);
                }

                int limit = ArcanumSettingClamps.EmbeddingsMaxResults(request.Limit ?? embeddings.MaxResults);

                float similarityThreshold = ArcanumSettingClamps.EmbeddingsSimilarityThreshold(embeddings.SimilarityThreshold);

                Result<SemanticSearchResult> searchResult = await divinationService.SearchAsync(
                    embedResult.Value,
                    request.CampaignId,
                    request.Status,
                    limit,
                    similarityThreshold,
                    ctx.RequestAborted).ConfigureAwait(false);

                if (searchResult.IsFailure)
                {
                    return FailureResult(traceId, searchResult.Error);
                }

                return Results.Ok(
                    ApiResponse<SemanticSearchResult>.FromResult(searchResult, traceId));
            })
        .WithName("SessionDivination");

        return apiGroup;
    }

    private static IResult FailureResult(string traceId, Error error) =>
        Results.Json(
            ApiResponse<SemanticSearchResult>.FromResult(Result<SemanticSearchResult>.Failure(error), traceId),
            ArcanumJsonContext.Default.ApiResponseSemanticSearchResult,
            statusCode: ArcanumErrorMapper.ResolveStatusCode(error.Code));
}
