using System.Diagnostics;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Primitives;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Tower;

public sealed record LexiconShowRequest(string Name, LexiconCurationScope Scope);

public sealed record LexiconCorrectRequest(LexiconCurationTarget Target, LexiconReplacementContent Content);

public sealed record LexiconRetireRequest(LexiconCurationTarget Target);

public sealed record LexiconReinstateRequest(LexiconCurationTarget Target);

public sealed record LexiconPinRequest(LexiconCurationTarget Target);

public sealed record LexiconUnpinRequest(LexiconCurationTarget Target);

/// <summary>Exact operator curation; the existing GET retains effective Campaign-to-Global lookup.</summary>
internal static class LexiconCurationEndpoints
{
    public static RouteGroupBuilder MapLexiconCurationEndpoints(this RouteGroupBuilder apiGroup)
    {
        apiGroup.MapPost("/memory/lexicon/show", HandleShowAsync)
            .RequireConditionalCovenantReadAuthority()
            .WithName("ShowLexiconEntry");

        apiGroup.MapPost("/memory/lexicon/correct", (ILexiconCurationService lexicon, HttpContext context) =>
            HandleMutationAsync(context, ArcanumJsonContext.Default.LexiconCorrectRequest,
                request => request.Target,
                (request, lease) => lexicon.CorrectAsync(request.Target, request.Content, lease, context.RequestAborted),
                request => request.Content))
            .RequireConditionalCovenantReadAuthority()
            .WithMetadata(CovenantConditionalExactWriteRequirementMetadata.Instance)
            .WithName("CorrectLexiconEntry");

        apiGroup.MapPost("/memory/lexicon/retire", (ILexiconCurationService lexicon, HttpContext context) =>
            HandleMutationAsync(context, ArcanumJsonContext.Default.LexiconRetireRequest,
                request => request.Target,
                (request, lease) => lexicon.RetireAsync(request.Target, lease, context.RequestAborted)))
            .RequireConditionalCovenantReadAuthority()
            .WithMetadata(CovenantConditionalExactWriteRequirementMetadata.Instance)
            .WithName("RetireLexiconEntry");

        apiGroup.MapPost("/memory/lexicon/reinstate", (ILexiconCurationService lexicon, HttpContext context) =>
            HandleMutationAsync(context, ArcanumJsonContext.Default.LexiconReinstateRequest,
                request => request.Target,
                (request, lease) => lexicon.ReinstateAsync(request.Target, lease, context.RequestAborted)))
            .RequireConditionalCovenantReadAuthority()
            .WithMetadata(CovenantConditionalExactWriteRequirementMetadata.Instance)
            .WithName("ReinstateLexiconEntry");

        apiGroup.MapPost("/memory/lexicon/pin", (ILexiconCurationService lexicon, HttpContext context) =>
            HandleMutationAsync(context, ArcanumJsonContext.Default.LexiconPinRequest,
                request => request.Target,
                (request, lease) => lexicon.PinAsync(request.Target, lease, context.RequestAborted)))
            .RequireConditionalCovenantReadAuthority()
            .WithMetadata(CovenantConditionalExactWriteRequirementMetadata.Instance)
            .WithName("PinLexiconEntry");

        apiGroup.MapPost("/memory/lexicon/unpin", (ILexiconCurationService lexicon, HttpContext context) =>
            HandleMutationAsync(context, ArcanumJsonContext.Default.LexiconUnpinRequest,
                request => request.Target,
                (request, lease) => lexicon.UnpinAsync(request.Target, lease, context.RequestAborted)))
            .RequireConditionalCovenantReadAuthority()
            .WithMetadata(CovenantConditionalExactWriteRequirementMetadata.Instance)
            .WithName("UnpinLexiconEntry");

        return apiGroup;
    }

    private static async Task<IResult> HandleShowAsync(ILexiconCurationService lexicon, HttpContext context)
    {
        JsonTypeInfo<ApiResponse<LexiconEntryDetail>> typeInfo = ArcanumJsonContext.Default.ApiResponseLexiconEntryDetail;

        (LexiconShowRequest? request, IResult? error) = await ApiRequestJson.ReadAsync(context,
            ArcanumJsonContext.Default.LexiconShowRequest,
            ctx => ApiRequestJson.InvalidBodyResult(ctx, ApiRequestJson.MalformedJsonMessage, typeInfo),
            context.RequestAborted).ConfigureAwait(false);

        if (error is not null)
        {
            return error;
        }

        if (request?.Scope is null || request.Scope.Validate().IsFailure)
        {
            return Respond(context, Result<LexiconEntryDetail>.Failure(new Error(ErrorCodes.Lexicon.InvalidScope,
                "A valid exact Lexicon scope is required.")), typeInfo);
        }

        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > LexiconLimits.MaxNameLength)
        {
            return Respond(context, Result<LexiconEntryDetail>.Failure(new Error(ErrorCodes.Lexicon.InvalidName,
                "A valid Lexicon entity name is required.")), typeInfo);
        }

        ICovenantSnapshotReadLease? owned = null;

        try
        {
            Result<CovenantExportAdmission> acquired = await context.RequestServices.GetRequiredService<ICovenantExportPolicy>()
                .AcquireConditionalReadAsync(OwnerScope(request.Scope), context.RequestAborted).ConfigureAwait(false);

            if (acquired.IsFailure)
            {
                return Respond(context, Result<LexiconEntryDetail>.Failure(acquired.Error), typeInfo);
            }

            owned = acquired.Value.ReadLease;

            Result<LexiconInspectionResult<LexiconEntryDetail>> inspected = await lexicon
                .ShowExactAsync(request.Scope, request.Name, owned, context.RequestAborted).ConfigureAwait(false);

            Result<LexiconEntryDetail> result = inspected.IsFailure
                ? Result<LexiconEntryDetail>.Failure(inspected.Error)
                : Result<LexiconEntryDetail>.Success(inspected.Value.Value);

            if (inspected.IsSuccess && inspected.Value.ContainsProtectedContent)
            {
                if (owned is null)
                {
                    return Respond(context, Result<LexiconEntryDetail>.Failure(new Error(ErrorCodes.Covenant.ForbiddenAuthority,
                        "Protected Lexicon content requires read authority.")), typeInfo);
                }

                IResult response = new CovenantProtectedJsonResult<LexiconEntryDetail>(owned, result, typeInfo);

                owned = null;

                return response;
            }

            return Respond(context, result, typeInfo);
        }
        finally
        {
            if (owned is not null)
            {
                await owned.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<IResult> HandleMutationAsync<TRequest>(
        HttpContext context,
        JsonTypeInfo<TRequest> requestTypeInfo,
        Func<TRequest, LexiconCurationTarget?> targetOf,
        Func<TRequest, CovenantWriteLease?, Task<Result<LexiconCurationResult>>> mutate,
        Func<TRequest, LexiconReplacementContent?>? replacementOf = null)
        where TRequest : class
    {
        JsonTypeInfo<ApiResponse<LexiconCurationResult>> typeInfo = ArcanumJsonContext.Default.ApiResponseLexiconCurationResult;

        TRequest? request;

        IResult? error;

        try
        {
            (request, error) = await ApiRequestJson.ReadAsync(context, requestTypeInfo,
                ctx => ApiRequestJson.InvalidBodyResult(ctx, ApiRequestJson.MalformedJsonMessage, typeInfo),
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // GenerationProvenance validates its constructor arguments. A malformed wire vector
            // is a body refusal even when source-generated deserialization invokes that constructor.
            return ApiRequestJson.InvalidBodyResult(context, ApiRequestJson.MalformedJsonMessage, typeInfo);
        }

        if (error is not null)
        {
            return error;
        }

        LexiconCurationTarget? target = request is null ? null : targetOf(request);

        if (target is null || target.Validate().IsFailure)
        {
            return Respond(context, Result<LexiconCurationResult>.Failure(new Error(ErrorCodes.Lexicon.InvalidCurationTarget,
                "A complete Lexicon target is required.")), typeInfo);
        }

        if (replacementOf is not null)
        {
            LexiconReplacementContent? content = replacementOf(request!);

            Result<LexiconCanonicalValue> normalized = LexiconValueNormalizer.NormalizeCorrection(target.NormalizedName, content?.Type, content?.Facts);

            if (normalized.IsFailure)
            {
                return Respond(context, Result<LexiconCurationResult>.Failure(normalized.Error), typeInfo);
            }
        }

        CovenantWriteLease? owned = null;

        try
        {
            // Only the supplied label arm can request authority. A label added since an absent target
            // was read is a stale target; it must not silently acquire a broader capability here.
            if (target.SensitivityLabel.IsPresent)
            {
                Result<CovenantWriteLease> acquired = await context.RequestServices.GetRequiredService<ICovenantOperationGate>()
                    .AcquireWriteAsync(OwnerScope(target.Scope), context.RequestAborted).ConfigureAwait(false);

                if (acquired.IsFailure)
                {
                    return Respond(context, Result<LexiconCurationResult>.Failure(acquired.Error), typeInfo);
                }

                owned = acquired.Value;
            }

            Result<LexiconCurationResult> result = await mutate(request!, owned).ConfigureAwait(false);

            if (owned is not null)
            {
                // Both successful content and mapped refusals retain the exact write capability
                // until the protected result has revalidated and written its final byte.
                IResult response = new CovenantProtectedJsonResult<LexiconCurationResult>(owned, result, typeInfo);

                owned = null;

                return response;
            }

            return Respond(context, result, typeInfo);
        }
        finally
        {
            if (owned is not null)
            {
                await owned.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static CovenantOperationScope OwnerScope(LexiconCurationScope scope) => scope.Kind == LexiconScopeKind.Global
        ? CovenantOperationScope.Global : CovenantOperationScope.ForCampaign(scope.CampaignId!.Value);

    private static IResult Respond<T>(HttpContext context, Result<T> result, JsonTypeInfo<ApiResponse<T>> typeInfo) =>
        Results.Json(ApiResponse<T>.FromResult(result, Activity.Current?.Id ?? context.TraceIdentifier), typeInfo,
            statusCode: result.IsSuccess ? StatusCodes.Status200OK : ArcanumErrorMapper.ResolveStatusCode(result.Error.Code));
}
