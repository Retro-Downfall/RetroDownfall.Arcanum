using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Primitives;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Api.Tower;

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
            async (
                string id,
                ISagaMemoryStore store,
                ICovenantSensitiveArtifactPurger purger,
                HttpContext ctx) =>
            {

                // A Saga id that is not a Guid cannot carry a label — the label table keys on Guid
                // identities — so it takes the ordinary path unchanged rather than being refused here.
                if (Guid.TryParse(id, out Guid memoryId))
                {

                    Result<CovenantSensitivePurgeOutcome> purged = await CovenantSensitiveDeletion
                        .DispatchAsync(purger, SensitiveArtifactKind.Saga, memoryId, ctx.RequestAborted)
                        .ConfigureAwait(false);

                    if (purged.IsFailure)
                    {

                        return SagaPurgeRefusal(ctx, purged.Error);

                    }

                    CovenantSensitiveDeletion.MarkProtectedWhenPurged(ctx, purged.Value);

                    if (purged.Value.IsBlocked)
                    {

                        return SagaPurgeRefusal(ctx, CovenantSensitiveDeletion.BlockedError(purged.Value));

                    }

                    if (purged.Value.WasPurged(memoryId))
                    {

                        return Results.NoContent();

                    }

                }

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
        .RequireConditionalSensitivityRetentionPurge()
        .WithName("DeleteSagaMemory");

        apiGroup.MapDelete(
            "/saga",
            async (
                bool? confirm,
                ISagaMemoryStore store,
                ICovenantSensitiveArtifactPurger purger,
                HttpContext ctx) =>
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

                // Bounded stable identity pages, dispatched before the set-based delete runs. A bulk
                // `DELETE FROM saga_memories` would remove labelled rows nothing ever examined, which is
                // exactly the legacy path this boundary exists to close (§10.20.2).
                Result<CovenantSensitivePurgeOutcome> purgedAll = await PurgeEveryLabeledSagaAsync(
                    store,
                    purger,
                    ctx.RequestAborted).ConfigureAwait(false);

                if (purgedAll.IsFailure)
                {

                    return SagaPurgeRefusal(ctx, purgedAll.Error);

                }

                CovenantSensitiveDeletion.MarkProtectedWhenPurged(ctx, purgedAll.Value);

                if (purgedAll.Value.IsBlocked)
                {

                    return SagaPurgeRefusal(ctx, CovenantSensitiveDeletion.BlockedError(purgedAll.Value));

                }

                await store.DeleteAllAsync(ctx.RequestAborted).ConfigureAwait(false);

                return Results.NoContent();

            })
        .RequireConditionalSensitivityRetentionPurge()
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

    /// <summary>
    /// Walks every Saga memory in bounded identity pages and dispatches each page through the purge
    /// boundary.
    /// </summary>
    /// <remarks>
    /// The offset walk is deliberately re-read per page rather than snapshotted whole: a bulk delete on
    /// a large Saga would otherwise hold a list in memory whose size the operator never bounded. Each
    /// page is a stable list of identities that were examined before anything was removed, which is the
    /// property that makes "no unexamined labelled row leaves through a set-based call" true rather than
    /// intended (§10.20.2).
    /// </remarks>
    private static async Task<Result<CovenantSensitivePurgeOutcome>> PurgeEveryLabeledSagaAsync(
        ISagaMemoryStore store,
        ICovenantSensitiveArtifactPurger purger,
        CancellationToken cancellationToken)
    {

        const int PageSize = 128;

        List<CovenantSensitivePurgeResult> results = [];

        CovenantArtifactErasureProgress progress = CovenantArtifactErasureProgress.Empty;

        int offset = 0;

        while (true)
        {

            SagaMemoryDto[] page = await store
                .ListAsync(null, null, PageSize, offset, cancellationToken)
                .ConfigureAwait(false);

            if (page.Length == 0)
            {

                break;

            }

            Guid[] identities =
            [
                .. page
                    .Select(static memory => Guid.TryParse(memory.Id, out Guid parsed) ? parsed : Guid.Empty)
                    .Where(static parsed => parsed != Guid.Empty),
            ];

            if (identities.Length > 0)
            {

                Result<CovenantSensitivePurgeOutcome> purged = await CovenantSensitiveDeletion
                    .DispatchAsync(purger, SensitiveArtifactKind.Saga, identities, cancellationToken)
                    .ConfigureAwait(false);

                if (purged.IsFailure)
                {

                    return purged.Error;

                }

                results.AddRange(purged.Value.Results);

                progress = progress.Add(purged.Value.Progress);

                if (purged.Value.IsBlocked)
                {

                    return Result<CovenantSensitivePurgeOutcome>.Success(
                        new CovenantSensitivePurgeOutcome(results, progress));

                }

            }

            // The cursor advances by the page it read rather than by what it purged: a purged row is
            // gone, so advancing by the purged count would skip the rows that slid into its place.
            offset += page.Length;

        }

        return Result<CovenantSensitivePurgeOutcome>.Success(
            new CovenantSensitivePurgeOutcome(results, progress));

    }

    private static IResult SagaPurgeRefusal(HttpContext context, Error error)
    {

        string traceId = Activity.Current?.Id ?? context.TraceIdentifier;

        return Results.Json(
            ApiResponse<string>.FromResult(Result<string>.Failure(error), traceId),
            ArcanumJsonContext.Default.ApiResponseString,
            statusCode: ArcanumErrorMapper.ResolveStatusCode(error.Code));

    }

}
