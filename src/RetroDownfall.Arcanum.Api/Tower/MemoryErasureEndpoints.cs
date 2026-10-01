using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Serialization.Metadata;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using RetroDownfall.Arcanum.Api.Primitives;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Tower;

/// <summary>
/// The selective-erasure routes: per-store prepare and apply, each an authenticated static POST with a
/// typed body under exactly one operator authority.
/// </summary>
/// <remarks>
/// <para>Every route declares its authority as metadata, so the pre-binding middleware issues the
/// operator context before the body is read, refuses a host-tools-tainted installation, and marks the
/// response protected: every status carries the no-store header tuple (API §8.35). Saga erase requires
/// <see cref="CovenantAuthorityRequirement.SensitivityRetentionPurge"/>, the authority its label purge
/// needs.</para>
///
/// <para>A body is read through <see cref="ReadBodyAsync{T}"/>, which answers malformed JSON with the
/// house invalid-body envelope rather than an empty minimal-API 400. Each handler validates the body's
/// shape before the service sees it, and only the endpoint maps a result onto an envelope and a
/// status.</para>
/// </remarks>
internal static class MemoryErasureEndpoints
{
    private const int ContentHashHexLength = 64;

    internal static RouteGroupBuilder MapMemoryErasureEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/memory/saga/erase/prepare", HandleSagaErasePrepareAsync)
            .RequireCovenantOperatorAuthority(CovenantAuthorityRequirement.SensitivityRetentionPurge)
            .WithName("PrepareSagaMemoryErasure");

        api.MapPost("/memory/saga/erase", HandleSagaEraseAsync)
            .RequireCovenantOperatorAuthority(CovenantAuthorityRequirement.SensitivityRetentionPurge)
            .WithName("EraseSagaMemory");

        return api;
    }

    private static async Task<IResult> HandleSagaErasePrepareAsync(ISagaMemoryErasureService saga, HttpContext context)
    {
        JsonTypeInfo<ApiResponse<MemoryErasurePreflightDto>> typeInfo =
            ArcanumJsonContext.Default.ApiResponseMemoryErasurePreflightDto;

        (SagaErasePrepareRequest? request, IResult? error) = await ReadBodyAsync(
            context,
            ArcanumJsonContext.Default.SagaErasePrepareRequest).ConfigureAwait(false);

        if (error is not null)
        {
            return error;
        }

        if (request is null
            || !IsSagaTarget(request.MemoryId, request.ExpectedContentHash, request.ExpectedClaimVersionId, request.MutationId))
        {
            return Respond(context, InvalidSagaBody<MemoryErasurePreflightDto>(), typeInfo);
        }

        Result<MemoryErasurePreflightDto> prepared = await saga
            .PrepareAsync(request, context.RequestAborted)
            .ConfigureAwait(false);

        return Respond(context, prepared, typeInfo);
    }

    private static async Task<IResult> HandleSagaEraseAsync(ISagaMemoryErasureService saga, HttpContext context)
    {
        JsonTypeInfo<ApiResponse<MemoryErasureResultDto>> typeInfo =
            ArcanumJsonContext.Default.ApiResponseMemoryErasureResultDto;

        (SagaEraseRequest? request, IResult? error) = await ReadBodyAsync(
            context,
            ArcanumJsonContext.Default.SagaEraseRequest).ConfigureAwait(false);

        if (error is not null)
        {
            return error;
        }

        if (request is null
            || !IsSagaTarget(request.MemoryId, request.ExpectedContentHash, request.ExpectedClaimVersionId, request.MutationId)
            || string.IsNullOrWhiteSpace(request.PreflightToken))
        {
            return Respond(context, InvalidSagaBody<MemoryErasureResultDto>(), typeInfo);
        }

        // The middleware issued this route's context before the body was bound, so it is present
        // whenever the handler runs; the filter has already refused a request without it.
        OperatorAuthorityContext authority = CovenantRequestFeatures.Authority(context)!.Context;

        Result<MemoryErasureResultDto> applied = await saga
            .ApplyAsync(request, authority, context.RequestAborted)
            .ConfigureAwait(false);

        return Respond(context, applied, typeInfo);
    }

    /// <summary>
    /// Reads one typed body, answering malformed JSON, a wrong media type, or an unreadable body with
    /// the house envelope instead of minimal-API binding's empty responses.
    /// </summary>
    private static async Task<(T? Body, IResult? Error)> ReadBodyAsync<T>(HttpContext context, JsonTypeInfo<T> typeInfo)
        where T : class =>
        await ApiRequestJson.ReadAsync(
            context,
            typeInfo,
            static ctx => ApiRequestJson.InvalidBodyResult(ctx, ApiRequestJson.MalformedJsonMessage),
            context.RequestAborted).ConfigureAwait(false);

    /// <summary>
    /// A GUID memory id, a 64-character hexadecimal content hash, an absent or GUID claim version, and a
    /// nonempty mutation id.
    /// </summary>
    private static bool IsSagaTarget(string? memoryId, string? contentHash, string? claimVersionId, Guid mutationId) =>
        Guid.TryParse(memoryId, CultureInfo.InvariantCulture, out _)
        && contentHash is { Length: ContentHashHexLength }
        && contentHash.All(char.IsAsciiHexDigit)
        && (claimVersionId is null || Guid.TryParse(claimVersionId, CultureInfo.InvariantCulture, out _))
        && mutationId != Guid.Empty;

    private static Result<T> InvalidSagaBody<T>() =>
        Result<T>.Failure(new Error(
            ErrorCodes.Validation.InvalidBody,
            "A Saga erase names a GUID memoryId, a 64-character hexadecimal expectedContentHash, an optional GUID expectedClaimVersionId, a mutationId and, to apply, a preflightToken."));

    private static IResult Respond<T>(HttpContext context, Result<T> result, JsonTypeInfo<ApiResponse<T>> typeInfo) =>
        Results.Json(
            ApiResponse<T>.FromResult(result, Activity.Current?.Id ?? context.TraceIdentifier),
            typeInfo,
            statusCode: result.IsSuccess
                ? StatusCodes.Status200OK
                : ArcanumErrorMapper.ResolveStatusCode(result.Error.Code));
}
