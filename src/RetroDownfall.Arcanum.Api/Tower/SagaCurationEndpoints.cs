using System.Diagnostics;

using Microsoft.AspNetCore.Builder;

using Microsoft.AspNetCore.Http;

using Microsoft.AspNetCore.Routing;

using RetroDownfall.Arcanum.Api.Primitives;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Api.Tower;

/// <summary>
/// The body of <c>POST /api/memory/saga/{id}/correct</c>: the replacement text, and the caller's proof
/// of what it read before deciding the stored text was wrong.
/// </summary>
/// <param name="ExpectedContentHash">
/// Hex of <c>AnnalContentDigest.ForSagaMemory</c> over the content the caller last read. Compared
/// inside the write transaction, which is what makes it proof rather than a courtesy.
/// </param>
public sealed record SagaCorrectRequest(string ExpectedContentHash, string Content);

/// <summary>The body of <c>POST /api/memory/saga/{id}/retire</c>, carrying the same proof a correction does.</summary>
public sealed record SagaRetireRequest(string ExpectedContentHash);

/// <summary>The body of <c>POST /api/memory/saga/{id}/reinstate</c>, carrying the same proof a correction does.</summary>
public sealed record SagaReinstateRequest(string ExpectedContentHash);

/// <summary>
/// The operator's curation surface over Saga: show one memory, correct it, retire it, reinstate it,
/// pin it, unpin it.
/// </summary>
/// <remarks>
/// Every verb here names exactly one store — Saga — in its path and in what it returns, which is why
/// these sit under <c>/api/memory/saga/</c> beside the Covenant's and the Lexicon's curation surfaces
/// rather than under <c>/api/saga</c>. <c>/api/saga</c> is the store's own read-and-delete surface and
/// answers a different question: what is in there, and take this out of it. Curation answers what one
/// memory is, and what the operator has decided about it.
///
/// <para>Authentication and the rest of the request pipeline come from the <c>/api</c> group these are
/// mapped onto in <c>ApiBootstrapper</c>; that same registration is what keeps them off the
/// OpenAI-compatible <c>/v1</c> surface, which maps its own routes and knows nothing about Saga
/// curation.</para>
///
/// <para>The detail route answers <c>ApiResponse&lt;SagaMemoryDetail&gt;</c>; the five write routes answer
/// <c>ApiResponse&lt;SagaCurationResult&gt;</c>, which carries what the call did beside the memory it
/// left behind. That extra field is what lets a caller tell "I retired it" from "it was already
/// retired" — both of which are successes here. Asking for a state a memory is already in is not
/// refused: <see cref="SagaCurationOutcomeKind.AlreadyRetired"/>,
/// <see cref="SagaCurationOutcomeKind.NotRetired"/> and <see cref="SagaCurationOutcomeKind.Unchanged"/>
/// all reach the caller as 200 carrying their own kind.</para>
///
/// <para>What is refused is what the caller could not have seen: no such memory, content that moved
/// since it was read, an embedding substrate that cannot produce a vector, and a body or hash the route
/// could not parse. <see cref="ISagaCurationService"/> decides which outcomes are errors and
/// <see cref="ArcanumErrorMapper"/> decides their status codes; neither decision is restated here.</para>
/// </remarks>
internal static class SagaCurationEndpoints
{

    public static RouteGroupBuilder MapSagaCurationEndpoints(this RouteGroupBuilder apiGroup)
    {

        apiGroup.MapGet(
            "/memory/saga/{id}",
            async (string id, ISagaCurationService curation, HttpContext ctx) =>
            {

                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Result<SagaMemoryDetail> detail = await curation
                    .ShowAsync(id, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return Results.Json(
                    ApiResponse<SagaMemoryDetail>.FromResult(detail, traceId),
                    ArcanumJsonContext.Default.ApiResponseSagaMemoryDetail,
                    statusCode: detail.IsSuccess
                        ? StatusCodes.Status200OK
                        : ArcanumErrorMapper.ResolveStatusCode(detail.Error.Code));

            })
        .WithName("ShowSagaMemory");

        apiGroup.MapPost(
            "/memory/saga/{id}/correct",
            async (string id, SagaCorrectRequest? request, ISagaCurationService curation, HttpContext ctx) =>
            {

                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (request is null || string.IsNullOrWhiteSpace(request.ExpectedContentHash) || request.Content is null)
                {

                    return CurationBadBody(traceId, "ExpectedContentHash and Content are required.");

                }

                Result<SagaCurationResult> corrected = await curation
                    .CorrectAsync(id, request.ExpectedContentHash, request.Content, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return Results.Json(
                    ApiResponse<SagaCurationResult>.FromResult(corrected, traceId),
                    ArcanumJsonContext.Default.ApiResponseSagaCurationResult,
                    statusCode: corrected.IsSuccess
                        ? StatusCodes.Status200OK
                        : ArcanumErrorMapper.ResolveStatusCode(corrected.Error.Code));

            })
        .WithName("CorrectSagaMemory");

        apiGroup.MapPost(
            "/memory/saga/{id}/retire",
            async (string id, SagaRetireRequest? request, ISagaCurationService curation, HttpContext ctx) =>
            {

                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (request is null || string.IsNullOrWhiteSpace(request.ExpectedContentHash))
                {

                    return CurationBadBody(traceId, "ExpectedContentHash is required.");

                }

                Result<SagaCurationResult> retired = await curation
                    .RetireAsync(id, request.ExpectedContentHash, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return Results.Json(
                    ApiResponse<SagaCurationResult>.FromResult(retired, traceId),
                    ArcanumJsonContext.Default.ApiResponseSagaCurationResult,
                    statusCode: retired.IsSuccess
                        ? StatusCodes.Status200OK
                        : ArcanumErrorMapper.ResolveStatusCode(retired.Error.Code));

            })
        .WithName("RetireSagaMemory");

        apiGroup.MapPost(
            "/memory/saga/{id}/reinstate",
            async (string id, SagaReinstateRequest? request, ISagaCurationService curation, HttpContext ctx) =>
            {

                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (request is null || string.IsNullOrWhiteSpace(request.ExpectedContentHash))
                {

                    return CurationBadBody(traceId, "ExpectedContentHash is required.");

                }

                Result<SagaCurationResult> reinstated = await curation
                    .ReinstateAsync(id, request.ExpectedContentHash, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return Results.Json(
                    ApiResponse<SagaCurationResult>.FromResult(reinstated, traceId),
                    ArcanumJsonContext.Default.ApiResponseSagaCurationResult,
                    statusCode: reinstated.IsSuccess
                        ? StatusCodes.Status200OK
                        : ArcanumErrorMapper.ResolveStatusCode(reinstated.Error.Code));

            })
        .WithName("ReinstateSagaMemory");

        // Neither pin nor unpin takes a content hash. A pin is not a statement about what a memory
        // says, and requiring proof of the text would make pinning fail after an unrelated correction.
        apiGroup.MapPost(
            "/memory/saga/{id}/pin",
            async (string id, ISagaCurationService curation, HttpContext ctx) =>
            {

                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Result<SagaCurationResult> pinned = await curation
                    .SetPinAsync(id, pinned: true, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return Results.Json(
                    ApiResponse<SagaCurationResult>.FromResult(pinned, traceId),
                    ArcanumJsonContext.Default.ApiResponseSagaCurationResult,
                    statusCode: pinned.IsSuccess
                        ? StatusCodes.Status200OK
                        : ArcanumErrorMapper.ResolveStatusCode(pinned.Error.Code));

            })
        .WithName("PinSagaMemory");

        apiGroup.MapPost(
            "/memory/saga/{id}/unpin",
            async (string id, ISagaCurationService curation, HttpContext ctx) =>
            {

                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Result<SagaCurationResult> unpinned = await curation
                    .SetPinAsync(id, pinned: false, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return Results.Json(
                    ApiResponse<SagaCurationResult>.FromResult(unpinned, traceId),
                    ArcanumJsonContext.Default.ApiResponseSagaCurationResult,
                    statusCode: unpinned.IsSuccess
                        ? StatusCodes.Status200OK
                        : ArcanumErrorMapper.ResolveStatusCode(unpinned.Error.Code));

            })
        .WithName("UnpinSagaMemory");

        return apiGroup;

    }

    /// <summary>
    /// A body that never reached the curation service, refused as a request-shape problem.
    /// </summary>
    /// <remarks>
    /// Shared by the three routes that take a body because the refusal is about the envelope rather
    /// than about the verb: none of them can name a memory, an outcome, or a store from a body they
    /// could not read.
    /// </remarks>
    private static IResult CurationBadBody(string traceId, string detail) =>
        Results.Json(
            ApiResponse<SagaCurationResult>.FromResult(
                Result<SagaCurationResult>.Failure(new Error(ErrorCodes.Validation.InvalidBody, detail)),
                traceId),
            ArcanumJsonContext.Default.ApiResponseSagaCurationResult,
            statusCode: ArcanumErrorMapper.ResolveStatusCode(ErrorCodes.Validation.InvalidBody));

}
