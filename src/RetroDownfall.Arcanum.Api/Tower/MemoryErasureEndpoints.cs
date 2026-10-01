using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Serialization.Metadata;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using RetroDownfall.Arcanum.Api.Primitives;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;

namespace RetroDownfall.Arcanum.Api.Tower;

/// <summary>
/// The selective-erasure routes: per-store prepare and apply, and per-store release, each an
/// authenticated static POST with a typed body under exactly one operator authority.
/// </summary>
/// <remarks>
/// <para>Every route declares its authority as metadata, so the pre-binding middleware issues the
/// operator context before the body is read, refuses a host-tools-tainted installation, and marks the
/// response protected: every status carries the no-store header tuple (API §8.35). Saga and Lexicon
/// erase require <see cref="CovenantAuthorityRequirement.SensitivityRetentionPurge"/>, the authority
/// their label purges need.</para>
///
/// <para>A body is read through <see cref="ReadBodyAsync{T}"/>, which answers malformed JSON with the
/// house invalid-body envelope rather than an empty minimal-API 400. Each handler validates the body's
/// shape before the service sees it, and only the endpoint maps a result onto an envelope and a
/// status.</para>
///
/// <para>Every release route requires <see cref="CovenantAuthorityRequirement.LifecycleManage"/>, whatever
/// its store: release is the unsafe direction, because it lets extraction and agents write an erased
/// identity again. A release takes no mutation id and is naturally idempotent. Its handler refuses a
/// missing body or member as an invalid body, and the release port validates the rest.</para>
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

        api.MapPost("/memory/lexicon/erase/prepare", HandleLexiconErasePrepareAsync)
            .RequireCovenantOperatorAuthority(CovenantAuthorityRequirement.SensitivityRetentionPurge)
            .WithName("PrepareLexiconEntryErasure");

        api.MapPost("/memory/lexicon/erase", HandleLexiconEraseAsync)
            .RequireCovenantOperatorAuthority(CovenantAuthorityRequirement.SensitivityRetentionPurge)
            .WithName("EraseLexiconEntry");

        api.MapPost("/memory/covenant/erase/prepare", HandleCovenantErasePrepareAsync)
            .RequireCovenantOperatorAuthority(CovenantAuthorityRequirement.LifecycleManage)
            .WithName("PrepareCovenantErasure");

        api.MapPost("/memory/covenant/erase", HandleCovenantEraseAsync)
            .RequireCovenantOperatorAuthority(CovenantAuthorityRequirement.LifecycleManage)
            .WithName("EraseCovenantEntry");

        api.MapPost("/memory/saga/release", HandleSagaReleaseAsync)
            .RequireCovenantOperatorAuthority(CovenantAuthorityRequirement.LifecycleManage)
            .WithName("ReleaseSagaErasure");

        api.MapPost("/memory/lexicon/release", HandleLexiconReleaseAsync)
            .RequireCovenantOperatorAuthority(CovenantAuthorityRequirement.LifecycleManage)
            .WithName("ReleaseLexiconErasure");

        api.MapPost("/memory/covenant/release", HandleCovenantReleaseAsync)
            .RequireCovenantOperatorAuthority(CovenantAuthorityRequirement.LifecycleManage)
            .WithName("ReleaseCovenantErasure");

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

    private static async Task<IResult> HandleLexiconErasePrepareAsync(ILexiconErasureService lexicon, HttpContext context)
    {
        JsonTypeInfo<ApiResponse<MemoryErasurePreflightDto>> typeInfo =
            ArcanumJsonContext.Default.ApiResponseMemoryErasurePreflightDto;

        (LexiconErasePrepareRequest? request, IResult? error) = await ReadLexiconBodyAsync(
            context,
            ArcanumJsonContext.Default.LexiconErasePrepareRequest).ConfigureAwait(false);

        if (error is not null)
        {
            return error;
        }

        Result shape = CheckLexiconBody(request?.Target, request?.MutationId, preflightToken: null, applying: false);

        if (shape.IsFailure)
        {
            return Respond(context, Result<MemoryErasurePreflightDto>.Failure(shape.Error), typeInfo);
        }

        Result<MemoryErasurePreflightDto> prepared = await lexicon
            .PrepareAsync(request!, context.RequestAborted)
            .ConfigureAwait(false);

        return Respond(context, prepared, typeInfo);
    }

    private static async Task<IResult> HandleLexiconEraseAsync(ILexiconErasureService lexicon, HttpContext context)
    {
        JsonTypeInfo<ApiResponse<MemoryErasureResultDto>> typeInfo =
            ArcanumJsonContext.Default.ApiResponseMemoryErasureResultDto;

        (LexiconEraseRequest? request, IResult? error) = await ReadLexiconBodyAsync(
            context,
            ArcanumJsonContext.Default.LexiconEraseRequest).ConfigureAwait(false);

        if (error is not null)
        {
            return error;
        }

        Result shape = CheckLexiconBody(request?.Target, request?.MutationId, request?.PreflightToken, applying: true);

        if (shape.IsFailure)
        {
            return Respond(context, Result<MemoryErasureResultDto>.Failure(shape.Error), typeInfo);
        }

        // The middleware issued this route's context before the body was bound, so it is present
        // whenever the handler runs; the filter has already refused a request without it.
        OperatorAuthorityContext authority = CovenantRequestFeatures.Authority(context)!.Context;

        Result<MemoryErasureResultDto> applied = await lexicon
            .ApplyAsync(request!, authority, context.RequestAborted)
            .ConfigureAwait(false);

        return Respond(context, applied, typeInfo);
    }

    /// <summary>
    /// Prepares a Covenant erase under an installation read lease, which the protected response holds
    /// until the body is written, so a reset or another erase never drains past a preflight still being
    /// serialized.
    /// </summary>
    private static async Task<IResult> HandleCovenantErasePrepareAsync(ICovenantEntryErasurePreparer covenant, HttpContext context)
    {
        JsonTypeInfo<ApiResponse<MemoryErasurePreflightDto>> typeInfo =
            ArcanumJsonContext.Default.ApiResponseMemoryErasurePreflightDto;

        (CovenantErasePrepareRequest? request, IResult? error) = await ReadBodyAsync(
            context,
            ArcanumJsonContext.Default.CovenantErasePrepareRequest).ConfigureAwait(false);

        if (error is not null)
        {
            return error;
        }

        Result shape = CheckCovenantBody(
            request?.Scope,
            request?.CampaignId,
            request?.Key,
            request?.EntryId,
            request?.Proposed,
            request?.MutationId,
            preflightToken: null,
            applying: false);

        if (shape.IsFailure)
        {
            return Respond(context, Result<MemoryErasurePreflightDto>.Failure(shape.Error), typeInfo);
        }

        OperatorAuthorityContext authority = CovenantRequestFeatures.Authority(context)!.Context;

        Result<CovenantEntryErasurePrepared> prepared = await covenant
            .PrepareHeldAsync(request!, authority, context.RequestAborted)
            .ConfigureAwait(false);

        if (prepared.IsFailure)
        {
            return Respond(context, Result<MemoryErasurePreflightDto>.Failure(prepared.Error), typeInfo);
        }

        ICovenantSnapshotReadLease? owned = prepared.Value.ReadLease;

        try
        {
            // Ownership moves to the result, which revalidates before the first byte and disposes in
            // its own finally. Clearing the local is what keeps the guard below from double-releasing.
            IResult response = new CovenantProtectedJsonResult<MemoryErasurePreflightDto>(
                owned,
                prepared.Value.Preflight,
                typeInfo);

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

    private static async Task<IResult> HandleCovenantEraseAsync(ICovenantEntryErasureService covenant, HttpContext context)
    {
        JsonTypeInfo<ApiResponse<MemoryErasureResultDto>> typeInfo =
            ArcanumJsonContext.Default.ApiResponseMemoryErasureResultDto;

        (CovenantEraseRequest? request, IResult? error) = await ReadBodyAsync(
            context,
            ArcanumJsonContext.Default.CovenantEraseRequest).ConfigureAwait(false);

        if (error is not null)
        {
            return error;
        }

        Result shape = CheckCovenantBody(
            request?.Scope,
            request?.CampaignId,
            request?.Key,
            request?.EntryId,
            request?.Proposed,
            request?.MutationId,
            request?.PreflightToken,
            applying: true);

        if (shape.IsFailure)
        {
            return Respond(context, Result<MemoryErasureResultDto>.Failure(shape.Error), typeInfo);
        }

        // The middleware issued this route's context before the body was bound, so it is present
        // whenever the handler runs; the filter has already refused a request without it.
        OperatorAuthorityContext authority = CovenantRequestFeatures.Authority(context)!.Context;

        Result<MemoryErasureResultDto> applied = await covenant
            .ApplyAsync(request!, authority, context.RequestAborted)
            .ConfigureAwait(false);

        return Respond(context, applied, typeInfo);
    }

    private static async Task<IResult> HandleSagaReleaseAsync(IMemoryErasureRelease release, HttpContext context)
    {
        CovenantProtectedResponseHeaders.Apply(context.Response);

        (SagaErasureReleaseRequest? request, IResult? error) = await ReadBodyAsync(
            context,
            ArcanumJsonContext.Default.SagaErasureReleaseRequest).ConfigureAwait(false);

        if (error is not null)
        {
            return error;
        }

        if (request?.Content is null)
        {
            return RespondRelease(context, InvalidReleaseBody("A Saga release names a scopeKind, a campaignId when the scope is Campaign, and content."));
        }

        return RespondRelease(
            context,
            await release.ReleaseSagaAsync(request, context.RequestAborted).ConfigureAwait(false));
    }

    private static async Task<IResult> HandleLexiconReleaseAsync(IMemoryErasureRelease release, HttpContext context)
    {
        CovenantProtectedResponseHeaders.Apply(context.Response);

        (LexiconErasureReleaseRequest? request, IResult? error) = await ReadBodyAsync(
            context,
            ArcanumJsonContext.Default.LexiconErasureReleaseRequest).ConfigureAwait(false);

        if (error is not null)
        {
            return error;
        }

        if (request?.Scope is null || request.Name is null)
        {
            return RespondRelease(context, InvalidReleaseBody("A Lexicon release names a scope and a name."));
        }

        return RespondRelease(
            context,
            await release.ReleaseLexiconAsync(request, context.RequestAborted).ConfigureAwait(false));
    }

    private static async Task<IResult> HandleCovenantReleaseAsync(IMemoryErasureRelease release, HttpContext context)
    {
        CovenantProtectedResponseHeaders.Apply(context.Response);

        (CovenantErasureReleaseRequest? request, IResult? error) = await ReadBodyAsync(
            context,
            ArcanumJsonContext.Default.CovenantErasureReleaseRequest).ConfigureAwait(false);

        if (error is not null)
        {
            return error;
        }

        if (request?.Key is null)
        {
            return RespondRelease(context, InvalidReleaseBody("A Covenant release names a scope, a campaignId when the scope is Campaign, and a key."));
        }

        return RespondRelease(
            context,
            await release.ReleaseCovenantAsync(request, context.RequestAborted).ConfigureAwait(false));
    }

    private static Result<MemoryErasureReleaseResultDto> InvalidReleaseBody(string message) =>
        Result<MemoryErasureReleaseResultDto>.Failure(new Error(ErrorCodes.Validation.InvalidBody, message));

    private static IResult RespondRelease(HttpContext context, Result<MemoryErasureReleaseResultDto> result) =>
        Respond(context, result, ArcanumJsonContext.Default.ApiResponseMemoryErasureReleaseResultDto);

    /// <summary>
    /// A recognized scope with its Campaign exactly when it is Campaign scope and no Proposed head for a
    /// Global entry, a key, a nonempty entry id, a nonempty mutation id and, to apply, a preflight token.
    /// </summary>
    private static Result CheckCovenantBody(
        CovenantScope? scope,
        Guid? campaignId,
        string? key,
        Guid? entryId,
        CovenantEraseHeadExpectation? proposed,
        Guid? mutationId,
        string? preflightToken,
        bool applying)
    {
        if (scope is not (CovenantScope.Global or CovenantScope.Campaign)
            || key is null
            || entryId is not { } entry
            || entry == Guid.Empty
            || mutationId is not { } mutation
            || mutation == Guid.Empty
            || (applying && string.IsNullOrWhiteSpace(preflightToken)))
        {
            return Result.Failure(new Error(
                ErrorCodes.Validation.InvalidBody,
                "A Covenant erase names a scope, a key, the entryId and lane heads show reported, a mutationId and, to apply, a preflightToken."));
        }

        bool paired = scope is CovenantScope.Campaign
            ? campaignId is { } campaign && campaign != Guid.Empty
            : campaignId is null;

        return paired && !(scope is CovenantScope.Global && proposed is not null)
            ? Result.Success()
            : Result.Failure(new Error(
                ErrorCodes.Covenant.InvalidScope,
                "A Global Covenant entry has no Proposed lane, and a Campaign scope names exactly one Campaign."));
    }

    /// <summary>
    /// Reads one Lexicon erase body. A target's label arm carries generation provenance whose own
    /// constructor validates it, so a malformed one is a body refusal like any other malformed JSON.
    /// </summary>
    private static async Task<(T? Body, IResult? Error)> ReadLexiconBodyAsync<T>(HttpContext context, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        try
        {
            return await ReadBodyAsync(context, typeInfo).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return (null, ApiRequestJson.InvalidBodyResult(context, ApiRequestJson.MalformedJsonMessage));
        }
    }

    /// <summary>
    /// The complete target show reported, a nonempty mutation id and, to apply, a preflight token.
    /// </summary>
    private static Result CheckLexiconBody(LexiconCurationTarget? target, Guid? mutationId, string? preflightToken, bool applying)
    {
        if (target is null || target.Validate().IsFailure)
        {
            return Result.Failure(new Error(
                ErrorCodes.Lexicon.InvalidCurationTarget,
                "A Lexicon erase names the complete target that show reported."));
        }

        return mutationId is { } id && id != Guid.Empty && (!applying || !string.IsNullOrWhiteSpace(preflightToken))
            ? Result.Success()
            : Result.Failure(new Error(
                ErrorCodes.Validation.InvalidBody,
                "A Lexicon erase names the complete target that show reported, a mutationId and, to apply, a preflightToken."));
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
