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
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Tower;

internal static class MemoryReviewEndpoints
{
    internal static RouteGroupBuilder MapMemoryReviewEndpoints(this RouteGroupBuilder apiGroup)
    {
        apiGroup.MapPost("/memory/saga/review/list", HandleSagaListAsync)
            .WithName("ListSagaMemoryReviewQueue");

        apiGroup.MapPost("/memory/saga/review/prepare", HandleSagaPrepareAsync)
            .WithName("PrepareSagaMemoryReview");

        apiGroup.MapPost("/memory/saga/review/apply", HandleSagaApplyAsync)
            .WithName("ApplySagaMemoryReview");

        apiGroup.MapPost("/memory/lexicon/review/list", HandleLexiconListAsync)
            .RequireConditionalCovenantReadAuthority()
            .WithName("ListLexiconMemoryReviewQueue");

        apiGroup.MapPost("/memory/lexicon/review/prepare", HandleLexiconPrepareAsync)
            .RequireConditionalCovenantReadAuthority()
            .WithName("PrepareLexiconMemoryReview");

        apiGroup.MapPost("/memory/lexicon/review/apply", HandleLexiconApplyAsync)
            .RequireConditionalCovenantReadAuthority()
            .WithMetadata(CovenantConditionalExactWriteRequirementMetadata.Instance)
            .WithName("ApplyLexiconMemoryReview");

        apiGroup.MapPost("/memory/covenant/review/list", HandleCovenantListAsync)
            .RequireCovenantReadAuthority()
            .WithName("ListCovenantMemoryReviewQueue");

        apiGroup.MapPost("/memory/covenant/review/prepare", HandleCovenantPrepareAsync)
            .RequireCovenantOperatorAuthority(CovenantAuthorityRequirement.CovenantManage)
            .WithName("PrepareCovenantMemoryReview");

        apiGroup.MapPost("/memory/covenant/review/apply", HandleCovenantApplyAsync)
            .RequireCovenantOperatorAuthority(CovenantAuthorityRequirement.CovenantManage)
            .WithName("ApplyCovenantMemoryReview");

        return apiGroup;
    }

    private static async Task<IResult> HandleSagaListAsync(
        ISagaMemoryReviewService service,
        HttpContext context)
    {
        (SagaReviewListRequest? request, IResult? error) = await ReadAsync(
            context,
            ArcanumJsonContext.Default.SagaReviewListRequest,
            ArcanumJsonContext.Default.ApiResponseSagaReviewPageDto).ConfigureAwait(false);

        if (error is not null)
        {
            return error;
        }

        Result<SagaReviewPageDto> result = request is null
            ? Invalid<SagaReviewPageDto>("A Saga review-list request is required.")
            : await service.ListAsync(request, context.RequestAborted).ConfigureAwait(false);

        return Respond(context, result, ArcanumJsonContext.Default.ApiResponseSagaReviewPageDto);
    }

    private static async Task<IResult> HandleSagaPrepareAsync(
        ISagaMemoryReviewService service,
        HttpContext context)
    {
        (SagaReviewBulkPrepareRequest? request, IResult? error) = await ReadAsync(
            context,
            ArcanumJsonContext.Default.SagaReviewBulkPrepareRequest,
            ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkPlanDto).ConfigureAwait(false);

        if (error is not null)
        {
            return error;
        }

        Result<MemoryReviewBulkPlanDto> result = request is null
            ? Invalid<MemoryReviewBulkPlanDto>("A Saga review preparation request is required.")
            : await service.PrepareAsync(request, context.RequestAborted).ConfigureAwait(false);

        return Respond(context, result, ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkPlanDto);
    }

    private static async Task<IResult> HandleSagaApplyAsync(
        ISagaMemoryReviewService service,
        HttpContext context)
    {
        (SagaReviewBulkApplyRequest? request, IResult? error) = await ReadAsync(
            context,
            ArcanumJsonContext.Default.SagaReviewBulkApplyRequest,
            ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkResultDto).ConfigureAwait(false);

        if (error is not null)
        {
            return error;
        }

        Result<MemoryReviewBulkResultDto> result = request is null
            ? Invalid<MemoryReviewBulkResultDto>("A Saga review apply request is required.")
            : await service.ApplyAsync(request, context.RequestAborted).ConfigureAwait(false);

        return Respond(context, result, ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkResultDto);
    }

    private static Task<IResult> HandleLexiconListAsync(
        ILexiconMemoryReviewService service,
        HttpContext context) =>
        WithLexiconReadAsync(
            context,
            ArcanumJsonContext.Default.LexiconReviewListRequest,
            ArcanumJsonContext.Default.ApiResponseLexiconReviewPageDto,
            static request => request.Validate(),
            static request => request.Scope.CampaignId,
            (request, lease) => service.ListAsync(request, lease, context.RequestAborted),
            "A Lexicon review-list request is required.");

    private static Task<IResult> HandleLexiconPrepareAsync(
        ILexiconMemoryReviewService service,
        HttpContext context) =>
        WithLexiconReadAsync(
            context,
            ArcanumJsonContext.Default.LexiconReviewBulkPrepareRequest,
            ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkPlanDto,
            static request => request.Validate(),
            static request => request.Scope.CampaignId,
            (request, lease) => service.PrepareAsync(request, lease, context.RequestAborted),
            "A Lexicon review preparation request is required.");

    private static async Task<IResult> HandleLexiconApplyAsync(
        ILexiconMemoryReviewService service,
        HttpContext context)
    {
        (LexiconReviewBulkApplyRequest? request, IResult? error) = await ReadAsync(
            context,
            ArcanumJsonContext.Default.LexiconReviewBulkApplyRequest,
            ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkResultDto).ConfigureAwait(false);

        if (error is not null)
        {
            return error;
        }

        if (request is null)
        {
            return Respond(
                context,
                Invalid<MemoryReviewBulkResultDto>("A Lexicon review apply request is required."),
                ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkResultDto);
        }

        Result validation = request.Validate();

        if (validation.IsFailure)
        {
            return Respond(
                context,
                Result<MemoryReviewBulkResultDto>.Failure(validation.Error),
                ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkResultDto);
        }

        CovenantWriteLease? owned = null;

        Result<CovenantWriteLease>? leaseFailure = null;

        try
        {
            if (context.RequestServices.GetService<ICovenantOperationGate>() is { } gate)
            {
                Result<CovenantWriteLease> acquired = await gate
                    .AcquireWriteAsync(OwnerScope(request.Request.Scope.CampaignId), context.RequestAborted)
                    .ConfigureAwait(false);

                if (acquired.IsSuccess)
                {
                    owned = acquired.Value;
                }
                else
                {
                    leaseFailure = acquired;
                }
            }

            Result<MemoryReviewBulkResultDto> result = await service
                .ApplyAsync(request, owned, context.RequestAborted)
                .ConfigureAwait(false);

            if (result.IsFailure && owned is null && leaseFailure is { IsFailure: true } refused
                && result.Error.Code == ErrorCodes.Covenant.ForbiddenAuthority)
            {
                result = Result<MemoryReviewBulkResultDto>.Failure(refused.Error);
            }

            if (owned is null)
            {
                return Respond(context, result, ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkResultDto);
            }

            IResult response = new CovenantProtectedJsonResult<MemoryReviewBulkResultDto>(
                owned,
                result,
                ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkResultDto);

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

    private static async Task<IResult> HandleCovenantListAsync(
        ICovenantMemoryReviewService? service,
        ICovenantOperationGate? gate,
        HttpContext context)
    {
        (CovenantReviewListRequest? request, IResult? error) = await ReadAsync(
            context,
            ArcanumJsonContext.Default.CovenantReviewListRequest,
            ArcanumJsonContext.Default.ApiResponseCovenantReviewPageDto).ConfigureAwait(false);

        if (error is not null)
        {
            return error;
        }

        return await WithCovenantReadAsync(
            request,
            service,
            gate,
            context,
            static request => request.Validate(),
            static request => request.Scope,
            static request => request.CampaignId,
            static (owner, body, lease, token) => owner.ListAsync(body, lease, token),
            ArcanumJsonContext.Default.ApiResponseCovenantReviewPageDto,
            "A Covenant review-list request is required.").ConfigureAwait(false);
    }

    private static async Task<IResult> HandleCovenantPrepareAsync(
        ICovenantMemoryReviewService? service,
        ICovenantOperationGate? gate,
        HttpContext context)
    {
        (CovenantReviewBulkPrepareRequest? request, IResult? error) = await ReadAsync(
            context,
            ArcanumJsonContext.Default.CovenantReviewBulkPrepareRequest,
            ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkPlanDto).ConfigureAwait(false);

        if (error is not null)
        {
            return error;
        }

        return await WithCovenantReadAsync(
            request,
            service,
            gate,
            context,
            static request => request.Validate(),
            static request => request.Scope,
            static request => request.CampaignId,
            static (owner, body, lease, token) => owner.PrepareAsync(body, lease, token),
            ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkPlanDto,
            "A Covenant review preparation request is required.").ConfigureAwait(false);
    }

    private static async Task<IResult> HandleCovenantApplyAsync(
        ICovenantMemoryReviewService? service,
        ICovenantOperationGate? gate,
        HttpContext context)
    {
        (CovenantReviewBulkApplyRequest? request, IResult? error) = await ReadAsync(
            context,
            ArcanumJsonContext.Default.CovenantReviewBulkApplyRequest,
            ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkResultDto).ConfigureAwait(false);

        if (error is not null)
        {
            return error;
        }

        if (request is null)
        {
            return Respond(
                context,
                Invalid<MemoryReviewBulkResultDto>("A Covenant review apply request is required."),
                ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkResultDto);
        }

        Result validation = request.Validate();

        if (validation.IsFailure)
        {
            return Respond(
                context,
                Result<MemoryReviewBulkResultDto>.Failure(validation.Error),
                ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkResultDto);
        }

        if (service is null || gate is null)
        {
            return Respond(
                context,
                Unavailable<MemoryReviewBulkResultDto>(),
                ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkResultDto);
        }

        Result<CovenantWriteLease> acquired = await gate
            .AcquireWriteAsync(OwnerScope(request.Request.Scope, request.Request.CampaignId), context.RequestAborted)
            .ConfigureAwait(false);

        if (acquired.IsFailure)
        {
            return Respond(
                context,
                Result<MemoryReviewBulkResultDto>.Failure(acquired.Error),
                ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkResultDto);
        }

        CovenantWriteLease? owned = acquired.Value;

        try
        {
            Result<MemoryReviewBulkResultDto> result = await service
                .ApplyAsync(request, owned, context.RequestAborted)
                .ConfigureAwait(false);

            IResult response = new CovenantProtectedJsonResult<MemoryReviewBulkResultDto>(
                owned,
                result,
                ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkResultDto);

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

    private static async Task<IResult> WithLexiconReadAsync<TRequest, TResponse>(
        HttpContext context,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<ApiResponse<TResponse>> responseTypeInfo,
        Func<TRequest, Result> validate,
        Func<TRequest, Guid?> campaignId,
        Func<TRequest, ICovenantSnapshotReadLease?, Task<Result<TResponse>>> read,
        string missingMessage)
        where TRequest : class
    {
        (TRequest? request, IResult? error) = await ReadAsync(
            context,
            requestTypeInfo,
            responseTypeInfo).ConfigureAwait(false);

        if (error is not null)
        {
            return error;
        }

        if (request is null)
        {
            return Respond(context, Invalid<TResponse>(missingMessage), responseTypeInfo);
        }

        Result validation = validate(request);

        if (validation.IsFailure)
        {
            return Respond(context, Result<TResponse>.Failure(validation.Error), responseTypeInfo);
        }

        Result<CovenantExportAdmission> acquired = await context.RequestServices
            .GetRequiredService<ICovenantExportPolicy>()
            .AcquireConditionalReadAsync(OwnerScope(campaignId(request)), context.RequestAborted)
            .ConfigureAwait(false);

        if (acquired.IsFailure)
        {
            return Respond(context, Result<TResponse>.Failure(acquired.Error), responseTypeInfo);
        }

        ICovenantSnapshotReadLease? owned = acquired.Value.ReadLease;

        try
        {
            Result<TResponse> result = await read(request, owned).ConfigureAwait(false);

            if (owned is null)
            {
                return Respond(context, result, responseTypeInfo);
            }

            IResult response = new CovenantProtectedJsonResult<TResponse>(owned, result, responseTypeInfo);

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

    private static async Task<IResult> WithCovenantReadAsync<TRequest, TResponse>(
        TRequest? request,
        ICovenantMemoryReviewService? service,
        ICovenantOperationGate? gate,
        HttpContext context,
        Func<TRequest, Result> validate,
        Func<TRequest, CovenantScope> scope,
        Func<TRequest, Guid?> campaignId,
        Func<ICovenantMemoryReviewService, TRequest, ICovenantSnapshotReadLease, CancellationToken,
            ValueTask<Result<TResponse>>> read,
        JsonTypeInfo<ApiResponse<TResponse>> responseTypeInfo,
        string missingMessage)
        where TRequest : class
    {
        if (request is null)
        {
            return Respond(context, Invalid<TResponse>(missingMessage), responseTypeInfo);
        }

        Result validation = validate(request);

        if (validation.IsFailure)
        {
            return Respond(context, Result<TResponse>.Failure(validation.Error), responseTypeInfo);
        }

        if (service is null || gate is null)
        {
            return Respond(context, Unavailable<TResponse>(), responseTypeInfo);
        }

        CovenantOperationScope operationScope = OwnerScope(scope(request), campaignId(request));

        Result<CovenantReadLease> acquired = await gate
            .AcquireReadAsync(operationScope, context.RequestAborted)
            .ConfigureAwait(false);

        if (acquired.IsFailure)
        {
            return Respond(context, Result<TResponse>.Failure(acquired.Error), responseTypeInfo);
        }

        CovenantReadLease? owned = acquired.Value;

        try
        {
            Result<TResponse> result = await read(service, request, owned, context.RequestAborted)
                .ConfigureAwait(false);

            IResult response = new CovenantProtectedJsonResult<TResponse>(owned, result, responseTypeInfo);

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

    private static async Task<(TRequest? Request, IResult? Error)> ReadAsync<TRequest, TResponse>(
        HttpContext context,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<ApiResponse<TResponse>> responseTypeInfo)
        where TRequest : class =>
        await ApiRequestJson.ReadAsync(
            context,
            requestTypeInfo,
            ctx => ApiRequestJson.InvalidBodyResult(
                ctx,
                ApiRequestJson.MalformedJsonMessage,
                responseTypeInfo),
            context.RequestAborted).ConfigureAwait(false);

    private static CovenantOperationScope OwnerScope(Guid? campaignId) =>
        campaignId is { } id ? CovenantOperationScope.ForCampaign(id) : CovenantOperationScope.Global;

    private static CovenantOperationScope OwnerScope(CovenantScope scope, Guid? campaignId) =>
        scope is CovenantScope.Campaign && campaignId is { } id
            ? CovenantOperationScope.ForCampaign(id)
            : CovenantOperationScope.Global;

    private static Result<T> Invalid<T>(string message) =>
        Result<T>.Failure(new Error(ErrorCodes.Validation.InvalidBody, message));

    private static Result<T> Unavailable<T>() =>
        Result<T>.Failure(new Error(
            ErrorCodes.Covenant.Unavailable,
            "Covenant memory is not available on this installation."));

    private static IResult Respond<T>(
        HttpContext context,
        Result<T> result,
        JsonTypeInfo<ApiResponse<T>> typeInfo) =>
        Results.Json(
            ApiResponse<T>.FromResult(result, Activity.Current?.Id ?? context.TraceIdentifier),
            typeInfo,
            statusCode: result.IsSuccess
                ? StatusCodes.Status200OK
                : ArcanumErrorMapper.ResolveStatusCode(result.Error.Code));
}
