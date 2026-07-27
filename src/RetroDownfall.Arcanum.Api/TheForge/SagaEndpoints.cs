using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Api.TheForge;

/// <summary>
/// Saga long-term associative memory: <c>GET /api/saga</c> (paginated listing), <c>POST /api/saga/divine</c>
/// (semantic search), <c>DELETE /api/saga/{id}</c> / <c>DELETE /api/saga</c> (deletion), and
/// <c>GET /api/saga/stats</c> (aggregate summary). Every step degrades gracefully when The Weave is
/// disabled or unavailable — see the graceful-degradation matrix in
/// <c>docs/Arcanum.DESIGN.md</c> §21.4. Saga is Arcanum's
/// auto-extracted long-term associative memory, distinct from the operator-authored Lore key-value
/// store (<c>/api/lore</c>) — see <c>docs/Arcanum.DESIGN.md</c> §17.
/// </summary>
internal static class SagaEndpoints
{

    private const int DefaultListLimit = 100;

    private const int MaxListLimit = 10_000;

    // Matches ArcanumSettingClamps.ArchiveSearchMaxQueryLength's upper bound, the repo's existing cap
    // for similarly-purposed free-text search filters (SessionRepository, GrimoireRepository).
    private const int MaxFilterQueryChars = 4_096;

    public static RouteGroupBuilder MapSagaEndpoints(this RouteGroupBuilder apiGroup)
    {

        apiGroup.MapGet(
            "/saga",
            async (
                string? q,
                Guid? sessionId,
                int? limit,
                int? offset,
                ISagaMemoryStore store,
                HttpContext ctx) =>
            {

                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (q is { Length: > MaxFilterQueryChars })
                {

                    return Results.Json(
                        ApiResponse<SagaMemoryDto[]>.FromResult(
                            Result<SagaMemoryDto[]>.Failure(
                                new Error(
                                    ErrorCodes.Validation.InvalidBody,
                                    $"q must not exceed {MaxFilterQueryChars} characters.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseSagaMemoryDtoArray,
                        statusCode: ArcanumErrorMapper.ResolveStatusCode(ErrorCodes.Validation.InvalidBody));

                }

                int clampedLimit = Math.Clamp(limit ?? DefaultListLimit, 1, MaxListLimit);

                int clampedOffset = Math.Max(0, offset ?? 0);

                SagaMemoryDto[] memories = await store
                    .ListAsync(q, sessionId, clampedLimit, clampedOffset, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return Results.Ok(
                    ApiResponse<SagaMemoryDto[]>.FromResult(Result<SagaMemoryDto[]>.Success(memories), traceId));

            })
        .WithName("ListSagaMemories");

        apiGroup.MapPost(
            "/saga/divine",
            async (
                SagaSearchRequest? request,
                IWeaveService weaveService,
                IDivinationService divinationService,
                ISagaMemoryStore store,
                IOptionsMonitor<ArcanumSettings> options,
                HttpContext ctx) =>
            {

                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                EmbeddingSettings embeddings = options.CurrentValue.ResolveEmbeddings();

                if (!embeddings.Enabled || !embeddings.SagaEnabled)
                {

                    return DivineFailureResult(
                        traceId,
                        new Error(
                            ErrorCodes.Embeddings.FeatureDisabled,
                            "Saga is disabled (Arcanum:Features:Embeddings and Arcanum:Features:Saga must both be true)."));

                }

                if (request is null || string.IsNullOrWhiteSpace(request.Query))
                {

                    return DivineFailureResult(
                        traceId,
                        new Error(ErrorCodes.Validation.InvalidBody, "Query is required."));

                }

                if (!weaveService.IsAvailable)
                {

                    return DivineFailureResult(
                        traceId,
                        new Error(ErrorCodes.Embeddings.ProviderUnavailable, "The embedding provider is unavailable."));

                }

                Result<Embedding<float>> embedResult = await weaveService
                    .EmbedAsync(request.Query.Trim(), ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (embedResult.IsFailure)
                {

                    return DivineFailureResult(traceId, embedResult.Error);

                }

                int limit = ArcanumSettingClamps.EmbeddingsMaxResults(request.Limit ?? embeddings.MaxResults);

                float similarityThreshold = ArcanumSettingClamps.EmbeddingsSimilarityThreshold(embeddings.SimilarityThreshold);

                Result<DivinationResult[]> searchResult = await divinationService
                    .SearchAsync(
                        "saga_memory_embeddings_vec",
                        "MemoryId",
                        "Embedding",
                        embedResult.Value,
                        limit,
                        similarityThreshold,
                        ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (searchResult.IsFailure)
                {

                    // Preserves the original error code (e.g. Embeddings.ProviderUnavailable -> 503)
                    // rather than remapping to Saga.SearchFailed (-> 500), matching
                    // SessionDivinationEndpoints' identical divination-failure handling. Remapping
                    // here previously turned a "provider is down, try again" signal into an opaque
                    // 500, hiding the real cause from clients.
                    return DivineFailureResult(traceId, searchResult.Error);

                }

                if (searchResult.Value.Length == 0)
                {

                    return Results.Ok(
                        ApiResponse<SagaSearchResult>.FromResult(
                            Result<SagaSearchResult>.Success(new SagaSearchResult([], [])),
                            traceId));

                }

                IReadOnlyDictionary<string, SagaMemoryDto> byId = await store
                    .GetByIdsAsync(
                        [.. searchResult.Value.Select(static hit => hit.Id)],
                        ctx.RequestAborted)
                    .ConfigureAwait(false);

                List<SagaMemoryDto> memories = [];

                List<float> similarities = [];

                foreach (DivinationResult hit in searchResult.Value)
                {

                    if (byId.TryGetValue(hit.Id, out SagaMemoryDto? memory))
                    {

                        memories.Add(memory);

                        similarities.Add(hit.Similarity);

                    }

                }

                SagaSearchResult payload = new([.. memories], [.. similarities]);

                return Results.Ok(
                    ApiResponse<SagaSearchResult>.FromResult(Result<SagaSearchResult>.Success(payload), traceId));

            })
        .WithName("SagaDivination");

        apiGroup.MapDelete(
            "/saga/{id}",
            async (string id, ISagaMemoryStore store, HttpContext ctx) =>
            {

                bool deleted = await store.DeleteAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                if (!deleted)
                {

                    string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                    return Results.Json(
                        ApiResponse<string>.FromResult(
                            Result<string>.Failure(new Error(ErrorCodes.Saga.NotFound, "Saga memory was not found.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseString,
                        statusCode: ArcanumErrorMapper.ResolveStatusCode(ErrorCodes.Saga.NotFound));

                }

                return Results.NoContent();

            })
        .WithName("DeleteSagaMemory");

        apiGroup.MapDelete(
            "/saga",
            async (bool? confirm, ISagaMemoryStore store, HttpContext ctx) =>
            {

                if (confirm != true)
                {

                    string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                    return Results.Json(
                        ApiResponse<string>.FromResult(
                            Result<string>.Failure(
                                new Error(ErrorCodes.Saga.NotEmpty, "Deleting all Saga memories requires ?confirm=true.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseString,
                        statusCode: ArcanumErrorMapper.ResolveStatusCode(ErrorCodes.Saga.NotEmpty));

                }

                await store.DeleteAllAsync(ctx.RequestAborted).ConfigureAwait(false);

                return Results.NoContent();

            })
        .WithName("DeleteAllSagaMemories");

        apiGroup.MapGet(
            "/saga/stats",
            async (ISagaMemoryStore store, HttpContext ctx) =>
            {

                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                SagaStats stats = await store.GetStatsAsync(ctx.RequestAborted).ConfigureAwait(false);

                return Results.Ok(ApiResponse<SagaStats>.FromResult(Result<SagaStats>.Success(stats), traceId));

            })
        .WithName("GetSagaStats");

        return apiGroup;

    }

    private static IResult DivineFailureResult(string traceId, Error error) =>
        Results.Json(
            ApiResponse<SagaSearchResult>.FromResult(Result<SagaSearchResult>.Failure(error), traceId),
            ArcanumJsonContext.Default.ApiResponseSagaSearchResult,
            statusCode: ArcanumErrorMapper.ResolveStatusCode(error.Code));

}
