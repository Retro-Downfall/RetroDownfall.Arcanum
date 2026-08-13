using System.Diagnostics;

using System.Net;

using System.Text.Json.Serialization.Metadata;

using Microsoft.AspNetCore.Builder;

using Microsoft.AspNetCore.Http;

using Microsoft.AspNetCore.Routing;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Data;

internal static class DataRetentionEndpoints
{

    private const string FactoryResetConfirmation = "factory-reset";

    public static RouteGroupBuilder MapDataRetentionEndpoints(
        this RouteGroupBuilder apiGroup)
    {

        apiGroup.MapGet(
            "/data/status",
            async (
                IDataRetentionService service,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            {

                DataRetentionStatus status = await service
                    .GetStatusAsync(cancellationToken)
                    .ConfigureAwait(false);

                return Success(
                    httpContext,
                    status,
                    ArcanumJsonContext.Default.ApiResponseDataRetentionStatus);

            })
            .WithName("GetDataRetentionStatus");

        apiGroup.MapGet(
            "/data/retention",
            (
                IDataRetentionPolicyStore policyStore,
                HttpContext httpContext) =>
                Success(
                    httpContext,
                    policyStore.Current,
                    ArcanumJsonContext.Default.ApiResponseRetentionSettings))
            .WithName("GetDataRetentionSettings");

        apiGroup.MapPut(
            "/data/retention",
            async (
                RetentionRuleUpdateRequest? request,
                IDataRetentionPolicyStore policyStore,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            {

                if (request is null)
                {

                    return Failure(
                        httpContext,
                        new Error(
                            ErrorCodes.Data.InvalidRequest,
                            "A retention rule update is required."),
                        StatusCodes.Status400BadRequest,
                        ArcanumJsonContext.Default.ApiResponseRetentionSettings);

                }

                Result<RetentionSettings> update = await policyStore
                    .UpdateRuleAsync(request, cancellationToken)
                    .ConfigureAwait(false);

                if (update.IsFailure)
                {

                    return Failure(
                        httpContext,
                        new Error(
                            update.Error.Code,
                            update.Error.Code == ErrorCodes.Data.InvalidRequest
                                ? update.Error.Message
                                : "The retention policy could not be saved."),
                        update.Error.Code == ErrorCodes.Data.InvalidRequest
                            ? StatusCodes.Status400BadRequest
                            : StatusCodes.Status500InternalServerError,
                        ArcanumJsonContext.Default.ApiResponseRetentionSettings);

                }

                return Success(
                    httpContext,
                    update.Value,
                    ArcanumJsonContext.Default.ApiResponseRetentionSettings);

            })
            .WithName("UpdateDataRetentionRule");

        apiGroup.MapPost(
            "/data/prune/plan",
            async (
                DataRetentionRequest? request,
                IDataRetentionService service,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            {

                if (request is null
                    || request.Operation != DataRetentionOperation.Prune)
                {

                    return Failure(
                        httpContext,
                        new Error(
                            ErrorCodes.Data.InvalidRequest,
                            "The prune planning route accepts only the Prune operation."),
                        StatusCodes.Status400BadRequest,
                        ArcanumJsonContext.Default.ApiResponseDataRetentionPlan);

                }

                DataRetentionPlan plan = await service
                    .PlanAsync(request, cancellationToken)
                    .ConfigureAwait(false);

                return Success(
                    httpContext,
                    plan,
                    ArcanumJsonContext.Default.ApiResponseDataRetentionPlan);

            })
            .WithName("PlanDataRetentionPrune");

        apiGroup.MapPost(
            "/data/prune",
            async (
                DataRetentionApplyRequest? request,
                IDataRetentionService service,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            {

                if (request?.Request is null
                    || request.Request.Operation != DataRetentionOperation.Prune)
                {

                    return Failure(
                        httpContext,
                        new Error(
                            ErrorCodes.Data.InvalidRequest,
                            "The prune execution route accepts only the Prune operation."),
                        StatusCodes.Status400BadRequest,
                        ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult);

                }

                return await Apply(
                        service,
                        request,
                        httpContext,
                        cancellationToken)
                    .ConfigureAwait(false);

            })
            .WithName("ApplyDataRetentionPrune");

        apiGroup.MapDelete(
            "/data/sessions/{id:guid}",
            async (
                Guid id,
                IDataRetentionService service,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
                await Apply(
                        service,
                        new DataRetentionApplyRequest(
                            new DataRetentionRequest(
                                DataRetentionOperation.DeleteSession,
                                id)),
                        httpContext,
                        cancellationToken)
                    .ConfigureAwait(false))
            .WithName("DeleteDataRetentionSession");

        apiGroup.MapDelete(
            "/data/attachments/{id:guid}",
            async (
                Guid id,
                IDataRetentionService service,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
                await Apply(
                        service,
                        new DataRetentionApplyRequest(
                            new DataRetentionRequest(
                                DataRetentionOperation.DeleteAttachment,
                                id)),
                        httpContext,
                        cancellationToken)
                    .ConfigureAwait(false))
            .WithName("DeleteDataRetentionAttachment");

        apiGroup.MapPost(
            "/data/memory/reset",
            async (
                MemoryResetRequest? request,
                IDataRetentionService service,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
                request is null
                    ? Failure(
                        httpContext,
                        new Error(
                            ErrorCodes.Data.InvalidRequest,
                            "An explicit memory reset scope is required."),
                        StatusCodes.Status400BadRequest,
                        ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult)
                    : await Apply(
                        service,
                        new DataRetentionApplyRequest(
                            new DataRetentionRequest(
                                DataRetentionOperation.ResetMemory,
                                TargetId: null,
                                request.Scope)),
                        httpContext,
                        cancellationToken)
                    .ConfigureAwait(false))
            .WithName("ResetDataRetentionMemory");

        apiGroup.MapPost(
            "/data/factory-reset/plan",
            async (
                InstallationResetDataPlanRequest? request,
                IDataRetentionService service,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            {

                if (!IsLoopbackPeer(httpContext))
                {

                    return Failure(
                        httpContext,
                        new Error(
                            ErrorCodes.Data.Blocked,
                            "Factory reset planning is available only to loopback peers."),
                        StatusCodes.Status403Forbidden,
                        ArcanumJsonContext.Default.ApiResponseDataRetentionPlan);

                }

                DataRetentionRequest? dataRequest = request switch
                {

                    { Scope: InstallationResetDataScope.Global, Workspace: null } =>
                        new DataRetentionRequest(
                            DataRetentionOperation.FactoryReset),

                    {
                        Scope: InstallationResetDataScope.Workspace,
                        Workspace: { } workspace,
                    } =>
                        new DataRetentionRequest(
                            DataRetentionOperation.ResetWorkspace,
                            Workspace: workspace),

                    _ => null,

                };

                if (dataRequest is null)
                {

                    return Failure(
                        httpContext,
                        new Error(
                            ErrorCodes.Data.InvalidRequest,
                            "Global planning forbids a workspace binding, and workspace planning requires one."),
                        StatusCodes.Status400BadRequest,
                        ArcanumJsonContext.Default.ApiResponseDataRetentionPlan);

                }

                DataRetentionPlan plan = await service
                    .PlanAsync(dataRequest, cancellationToken)
                    .ConfigureAwait(false);

                return Success(
                    httpContext,
                    plan,
                    ArcanumJsonContext.Default.ApiResponseDataRetentionPlan);

            })
            .WithName("PlanFactoryResetDataRetention");

        apiGroup.MapPost(
            "/data/factory-reset",
            async (
                FactoryResetRequest? request,
                IDataRetentionService service,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            {

                if (!string.Equals(
                        request?.Confirmation,
                        FactoryResetConfirmation,
                        StringComparison.Ordinal))
                {

                    return Failure(
                        httpContext,
                        new Error(
                            ErrorCodes.Data.ConfirmationRequired,
                            "Factory reset requires the exact confirmation 'factory-reset'."),
                        StatusCodes.Status400BadRequest,
                        ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult);

                }

                return await Apply(
                        service,
                        new DataRetentionApplyRequest(
                            new DataRetentionRequest(
                                DataRetentionOperation.FactoryReset)),
                        httpContext,
                        cancellationToken)
                    .ConfigureAwait(false);

            })
            .WithName("FactoryResetDataRetention");

        return apiGroup;

    }

    private static bool IsLoopbackPeer(HttpContext httpContext)
    {

        IPAddress? remoteAddress = httpContext.Connection.RemoteIpAddress;

        return remoteAddress is not null
            && IPAddress.IsLoopback(remoteAddress);

    }

    private static async Task<IResult> Apply(
        IDataRetentionService service,
        DataRetentionApplyRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {

        Result<DataRetentionApplyResult> result = await service
            .ApplyAsync(request, cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? Success(
                httpContext,
                result.Value,
                ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult)
            : Failure(
                httpContext,
                result.Error,
                ResolveStatusCode(result.Error.Code),
                ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult);

    }

    private static int ResolveStatusCode(string errorCode) =>
        errorCode switch
        {

            ErrorCodes.Data.InvalidRequest
                or ErrorCodes.Data.ConfirmationRequired =>
                StatusCodes.Status400BadRequest,

            ErrorCodes.Data.NotFound =>
                StatusCodes.Status404NotFound,

            ErrorCodes.Data.PlanChanged
                or ErrorCodes.Data.Blocked
                or ErrorCodes.Data.Conflict =>
                StatusCodes.Status409Conflict,

            ErrorCodes.Data.ReconciliationFailed =>
                StatusCodes.Status500InternalServerError,

            _ => StatusCodes.Status500InternalServerError,

        };

    private static IResult Success<T>(
        HttpContext httpContext,
        T data,
        JsonTypeInfo<ApiResponse<T>> typeInfo)
    {

        string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        ApiResponse<T> response = ApiResponse<T>.FromResult(
            Result<T>.Success(data),
            traceId);

        return Results.Json(response, typeInfo);

    }

    private static IResult Failure<T>(
        HttpContext httpContext,
        Error error,
        int statusCode,
        JsonTypeInfo<ApiResponse<T>> typeInfo)
    {

        string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        ApiResponse<T> response = ApiResponse<T>.FromResult(
            Result<T>.Failure(error),
            traceId);

        return Results.Json(
            response,
            typeInfo,
            statusCode: statusCode);

    }

}
