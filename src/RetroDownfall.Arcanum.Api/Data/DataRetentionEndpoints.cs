using System.Diagnostics;

using System.Net;

using System.Text.Json.Serialization.Metadata;

using Microsoft.AspNetCore.Builder;

using Microsoft.AspNetCore.Http;

using Microsoft.AspNetCore.Routing;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Api.Security;

using RetroDownfall.Arcanum.Api.Primitives;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

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
                                TargetId: request.CampaignId,
                                request.Scope),
                            request.ExpectedPlanId),
                        httpContext,
                        cancellationToken)
                    .ConfigureAwait(false))
            .WithName("ResetDataRetentionMemory")
            .RequireCovenantOperatorAuthority(CovenantAuthorityRequirement.LifecycleManage);

        apiGroup.MapPost(
            "/data/memory/reset/plan",
            async (
                MemoryResetRequest? request,
                IDataRetentionService service,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            {

                if (request is null)
                {

                    return Failure(
                        httpContext,
                        new Error(
                            ErrorCodes.Data.InvalidRequest,
                            "An explicit memory reset scope is required."),
                        StatusCodes.Status400BadRequest,
                        ArcanumJsonContext.Default.ApiResponseDataRetentionPlan);

                }

                Result<DataRetentionPlanAdmission> admission = await service
                    .PlanAdmissionAsync(
                        new DataRetentionRequest(
                            DataRetentionOperation.ResetMemory,
                            TargetId: request.CampaignId,
                            request.Scope),
                        cancellationToken)
                    .ConfigureAwait(false);

                return admission.IsSuccess
                    ? PlanSuccess(
                        httpContext,
                        admission.Value,
                        requiresReadLease: request.Scope is MemoryResetScope.Covenant)
                    : Failure(
                        httpContext,
                        admission.Error,
                        ResolveStatusCode(admission.Error.Code),
                        ArcanumJsonContext.Default.ApiResponseDataRetentionPlan);

            })
            .WithName("PlanDataRetentionMemoryReset")
            .RequireCovenantOperatorAuthority(CovenantAuthorityRequirement.LifecycleManage);

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

                Result<DataRetentionPlanAdmission> admission = await service
                    .PlanAdmissionAsync(
                        dataRequest,
                        cancellationToken,
                        DataRetentionPlanAdmissionCapability.Installation)
                    .ConfigureAwait(false);

                return admission.IsSuccess
                    ? PlanSuccess(
                        httpContext,
                        admission.Value,
                        requiresReadLease: true)
                    : Failure(
                        httpContext,
                        admission.Error,
                        ResolveStatusCode(admission.Error.Code),
                        ArcanumJsonContext.Default.ApiResponseDataRetentionPlan);

            })
            .WithName("PlanFactoryResetDataRetention")
            .RequireCovenantOperatorAuthority(CovenantAuthorityRequirement.LifecycleManage);

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

                FactoryResetRequest confirmedRequest = request!;

                if ((confirmedRequest.ExpectedPlanId is null)
                    != (confirmedRequest.RequestedOperationId is null))
                {

                    return Failure(
                        httpContext,
                        new Error(
                            ErrorCodes.Data.InvalidRequest,
                            "A factory reset's expected plan and requested operation identity must be supplied together."),
                        StatusCodes.Status400BadRequest,
                        ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult);

                }

                if (confirmedRequest.InstallationResetHandoff is { } handoff)
                {

                    Result validated = ValidateInstallationResetHandoff(
                        confirmedRequest,
                        handoff,
                        httpContext.RequestServices
                            .GetService<InstallationResetApiAdmission>()?
                            .ActiveRecovery);

                    if (validated.IsFailure)
                    {

                        return Failure(
                            httpContext,
                            validated.Error,
                            ResolveStatusCode(validated.Error.Code),
                            ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult);

                    }

                    return await ApplyInstallationResetHandoffAsync(
                            service,
                            confirmedRequest,
                            handoff,
                            httpContext,
                            cancellationToken)
                        .ConfigureAwait(false);

                }

                return await Apply(
                        service,
                        new DataRetentionApplyRequest(
                            new DataRetentionRequest(
                                DataRetentionOperation.FactoryReset),
                            confirmedRequest.ExpectedPlanId,
                            confirmedRequest.RequestedOperationId),
                        httpContext,
                        cancellationToken)
                    .ConfigureAwait(false);

            })
            .WithName("FactoryResetDataRetention")
            .WithMetadata(InstallationResetRecoveryApiRouteMetadata.FactoryReset)
            .RequireCovenantOperatorAuthority(CovenantAuthorityRequirement.LifecycleManage);

        return apiGroup;

    }

    private static async Task<IResult> ApplyInstallationResetHandoffAsync(
        IDataRetentionService service,
        FactoryResetRequest request,
        InstallationResetHostHandoff handoff,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {

        IInstallationResetMaintenanceLockAccessor accessor = httpContext.RequestServices
            .GetRequiredService<IInstallationResetMaintenanceLockAccessor>();

        Result<ArcanumMaintenanceLock> borrowed = accessor.BorrowHeldLock(
            ArcanumPaths.GrimoireDirectory);

        if (borrowed.IsFailure)
        {

            return Failure(
                httpContext,
                borrowed.Error,
                ResolveStatusCode(borrowed.Error.Code),
                ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult);

        }

        IInstallationResetHostHandoffCoordinator coordinator = httpContext.RequestServices
            .GetRequiredService<IInstallationResetHostHandoffCoordinator>();

        Result begun = await coordinator
            .BeginOrRecoverAsync(handoff, borrowed.Value, cancellationToken)
            .ConfigureAwait(false);

        if (begun.IsFailure)
        {

            return Failure(
                httpContext,
                begun.Error,
                ResolveStatusCode(begun.Error.Code),
                ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult);

        }

        DataRetentionApplyRequest applyRequest = new(
            new DataRetentionRequest(DataRetentionOperation.FactoryReset),
            request.ExpectedPlanId,
            request.RequestedOperationId);

        Result<DataRetentionApplyResult> applied = await service
            .ApplyAsync(applyRequest, cancellationToken)
            .ConfigureAwait(false);

        if (applied.IsFailure)
        {

            if (string.Equals(
                    applied.Error.Code,
                    ErrorCodes.Data.PlanChanged,
                    StringComparison.Ordinal))
            {

                Result retired = await coordinator
                    .RetirePreEffectAsync(
                        handoff,
                        borrowed.Value,
                        CancellationToken.None)
                    .ConfigureAwait(false);

                if (retired.IsFailure)
                {

                    return Failure(
                        httpContext,
                        retired.Error,
                        ResolveStatusCode(retired.Error.Code),
                        ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult);

                }

            }

            return Failure(
                httpContext,
                applied.Error,
                ResolveStatusCode(applied.Error.Code),
                ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult);

        }

        Result recorded = await coordinator
            .RecordOnlineCompletionAsync(
                handoff,
                applied.Value,
                borrowed.Value,
                CancellationToken.None)
            .ConfigureAwait(false);

        return recorded.IsSuccess
            ? Success(
                httpContext,
                applied.Value,
                ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult)
            : Failure(
                httpContext,
                recorded.Error,
                ResolveStatusCode(recorded.Error.Code),
                ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult);

    }

    private static Result ValidateInstallationResetHandoff(
        FactoryResetRequest request,
        InstallationResetHostHandoff handoff,
        InstallationResetRecoveryApiIdentity? activeRecovery)
    {

        InstallationResetAcceptedBinding? binding = handoff.AcceptedBinding;

        bool scopeValid = handoff.Scope switch
        {
            InstallationResetScope.Global => handoff.Workspace is null,
            InstallationResetScope.All => handoff.Workspace is { } workspace
                && workspace.CampaignId != Guid.Empty
                && !string.IsNullOrWhiteSpace(workspace.WorkspaceRoot),
            _ => false,
        };

        bool bindingValid = binding is not null
            && !string.IsNullOrWhiteSpace(binding.BindingId)
            && binding.SelectedRoots is not null
            && binding.ExcludedRoots is not null
            && binding.PreservedBackups is not null
            && binding.CredentialAccounts is not null
            && binding.DataPlanIds is { Length: 1 }
            && !string.IsNullOrWhiteSpace(binding.DataPlanIds[0])
            && binding.SelectedRoots.All(static value => !string.IsNullOrWhiteSpace(value))
            && binding.ExcludedRoots.All(static value => !string.IsNullOrWhiteSpace(value))
            && binding.CredentialAccounts.All(static value => !string.IsNullOrWhiteSpace(value))
            && binding.PreservedBackups.All(static value =>
                value is not null
                && !string.IsNullOrWhiteSpace(value.CanonicalPath)
                && value.Identity is not null
                && !string.IsNullOrWhiteSpace(value.Identity.Value)
                && value.Identity.Length >= 0
                && value.Identity.HardLinkCount > 0);

        bool requestValid = request.RequestedOperationId is { } requestedOperationId
            && requestedOperationId != Guid.Empty
            && requestedOperationId == handoff.RequestedOperationId
            && !string.IsNullOrWhiteSpace(request.ExpectedPlanId)
            && !string.IsNullOrWhiteSpace(handoff.InstallationPlanId)
            && bindingValid
            && string.Equals(
                request.ExpectedPlanId,
                binding!.DataPlanIds[0],
                StringComparison.Ordinal)
            && scopeValid;

        bool recoveryValid = activeRecovery is null
            || activeRecovery.Scope == handoff.Scope
                && activeRecovery.OperationId == handoff.RequestedOperationId
                && string.Equals(
                    activeRecovery.InstallationPlanId,
                    handoff.InstallationPlanId,
                    StringComparison.Ordinal);

        return requestValid && recoveryValid
            ? Result.Success()
            : Result.Failure(new Error(
                ErrorCodes.Data.InvalidRequest,
                "The installation reset host handoff does not match the confirmed reset binding."));

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

    private static IResult PlanSuccess(
        HttpContext httpContext,
        DataRetentionPlanAdmission admission,
        bool requiresReadLease)
    {

        if (admission.ReadLease is { } lease)
        {

            return new CovenantProtectedJsonResult<DataRetentionPlan>(
                lease,
                Result<DataRetentionPlan>.Success(admission.Plan),
                ArcanumJsonContext.Default.ApiResponseDataRetentionPlan);

        }

        if (requiresReadLease)
        {

            Error error = new(
                ErrorCodes.Covenant.MaintenanceFailed,
                "The required Covenant planning capability is unavailable.");

            return Failure(
                httpContext,
                error,
                ResolveStatusCode(error.Code),
                ArcanumJsonContext.Default.ApiResponseDataRetentionPlan);

        }

        return Success(
            httpContext,
            admission.Plan,
            ArcanumJsonContext.Default.ApiResponseDataRetentionPlan);

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

            _ => ArcanumErrorMapper.ResolveStatusCode(errorCode),

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
