using Microsoft.AspNetCore.Builder;

using Microsoft.AspNetCore.Http;

using Microsoft.AspNetCore.Routing;

using RetroDownfall.Arcanum.Api.Primitives;

using RetroDownfall.Arcanum.Api.Security;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Tower;

/// <summary>
/// The four routes an operator writes the Covenant through.
/// </summary>
/// <remarks>
/// Two prepares and two commits, because a standing agreement is not something to change by
/// accident: prepare measures what a mutation would do and hands back a token binding that exact
/// measurement, and commit refuses anything the measurement no longer describes (§10.22).
///
/// <para>Every route declares <c>CovenantManage</c> operator authority, so the pre-binding middleware
/// refuses an unauthorized request before a single body byte is bound. The prepare routes are
/// read-only but carry the same requirement: what they return is a measurement of protected state,
/// and an authority that could read it could also decide what to change.</para>
/// </remarks>
internal static class CovenantMutationEndpoints
{

    public static RouteGroupBuilder MapCovenantMutationEndpoints(this RouteGroupBuilder apiGroup)
    {

        apiGroup.MapPost(
            "/memory/covenant/set/prepare",
            static async (
                CovenantSetPrepareRequest? request,
                ICovenantMutationService? service,
                ICovenantOperationGate? gate,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
                await PrepareAsync(
                        request,
                        service,
                        gate,
                        httpContext,
                        static (mutation, body, lease, token) =>
                            mutation.PrepareSetAsync(body, lease, token),
                        cancellationToken)
                    .ConfigureAwait(false))
            .WithName("PrepareCovenantSet")
            .RequireCovenantOperatorAuthority(CovenantAuthorityRequirement.CovenantManage);

        apiGroup.MapPost(
            "/memory/covenant/retire/prepare",
            static async (
                CovenantRetirePrepareRequest? request,
                ICovenantMutationService? service,
                ICovenantOperationGate? gate,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
                await PrepareAsync(
                        request,
                        service,
                        gate,
                        httpContext,
                        static (mutation, body, lease, token) =>
                            mutation.PrepareRetireAsync(body, lease, token),
                        cancellationToken)
                    .ConfigureAwait(false))
            .WithName("PrepareCovenantRetire")
            .RequireCovenantOperatorAuthority(CovenantAuthorityRequirement.CovenantManage);

        apiGroup.MapPut(
            "/memory/covenant",
            static async (
                CovenantSetRequest? request,
                ICovenantMutationService? service,
                ICovenantOperationGate? gate,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
                await CommitAsync(
                        request,
                        service,
                        gate,
                        httpContext,
                        request?.Scope,
                        request?.CampaignId,
                        static (mutation, body, lease, token) => mutation.SetAsync(body, lease, token),
                        cancellationToken)
                    .ConfigureAwait(false))
            .WithName("SetCovenantEntry")
            .RequireCovenantOperatorAuthority(CovenantAuthorityRequirement.CovenantManage);

        apiGroup.MapPost(
            "/memory/covenant/retire",
            static async (
                CovenantRetireRequest? request,
                ICovenantMutationService? service,
                ICovenantOperationGate? gate,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
                await CommitAsync(
                        request,
                        service,
                        gate,
                        httpContext,
                        request?.Scope,
                        request?.CampaignId,
                        static (mutation, body, lease, token) => mutation.RetireAsync(body, lease, token),
                        cancellationToken)
                    .ConfigureAwait(false))
            .WithName("RetireCovenantEntry")
            .RequireCovenantOperatorAuthority(CovenantAuthorityRequirement.CovenantManage);

        return apiGroup;

    }

    /// <summary>
    /// Measures one prospective mutation under a read capability wide enough to see its effect.
    /// </summary>
    /// <remarks>
    /// The installation read capability, always. A Global mutation's effect reaches every Campaign, so
    /// measuring it means reading across all of them; taking a scoped lease for a Campaign mutation and
    /// an installation one for a Global mutation would make the capability an operator needs depend on
    /// a field in the body they sent, which is exactly the kind of conditional authority that ends up
    /// wrong in one branch.
    /// </remarks>
    private static async Task<IResult> PrepareAsync<TRequest>(
        TRequest? request,
        ICovenantMutationService? service,
        ICovenantOperationGate? gate,
        HttpContext httpContext,
        Func<ICovenantMutationService, TRequest, ICovenantSnapshotReadLease, CancellationToken,
            ValueTask<Result<CovenantMutationPreflightDto>>> prepare,
        CancellationToken cancellationToken)
        where TRequest : class
    {

        if (request is null)
        {

            return Refuse<CovenantMutationPreflightDto>(
                httpContext,
                new Error(ErrorCodes.Validation.InvalidBody, "A Covenant mutation request is required."),
                ArcanumJsonContext.Default.ApiResponseCovenantMutationPreflightDto);

        }

        if (service is null || gate is null)
        {

            return Refuse<CovenantMutationPreflightDto>(
                httpContext,
                UnavailableError,
                ArcanumJsonContext.Default.ApiResponseCovenantMutationPreflightDto);

        }

        Result<CovenantInstallationReadLease> lease = await gate
            .AcquireInstallationReadAsync(cancellationToken)
            .ConfigureAwait(false);

        if (lease.IsFailure)
        {

            return Refuse<CovenantMutationPreflightDto>(
                httpContext,
                lease.Error,
                ArcanumJsonContext.Default.ApiResponseCovenantMutationPreflightDto);

        }

        CovenantInstallationReadLease? owned = lease.Value;

        try
        {

            Result<CovenantMutationPreflightDto> prepared = await prepare(
                    service,
                    request,
                    owned,
                    cancellationToken)
                .ConfigureAwait(false);

            // Ownership moves to the result, which revalidates before the first byte and disposes in
            // its own finally. Clearing the local is what keeps the guard below from double-releasing.
            IResult response = new CovenantProtectedJsonResult<CovenantMutationPreflightDto>(
                owned,
                prepared,
                ArcanumJsonContext.Default.ApiResponseCovenantMutationPreflightDto);

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

    private static async Task<IResult> CommitAsync<TRequest>(
        TRequest? request,
        ICovenantMutationService? service,
        ICovenantOperationGate? gate,
        HttpContext httpContext,
        CovenantScope? scope,
        Guid? campaignId,
        Func<ICovenantMutationService, TRequest, CovenantWriteLease, CancellationToken,
            ValueTask<Result<CovenantMutationResultDto>>> commit,
        CancellationToken cancellationToken)
        where TRequest : class
    {

        if (request is null || scope is not { } scopeKind)
        {

            return Refuse<CovenantMutationResultDto>(
                httpContext,
                new Error(ErrorCodes.Validation.InvalidBody, "A Covenant mutation request is required."),
                ArcanumJsonContext.Default.ApiResponseCovenantMutationResultDto);

        }

        if (service is null || gate is null)
        {

            return Refuse<CovenantMutationResultDto>(
                httpContext,
                UnavailableError,
                ArcanumJsonContext.Default.ApiResponseCovenantMutationResultDto);

        }

        CovenantOperationScope operationScope = scopeKind is CovenantScope.Global || campaignId is not { } id
            ? CovenantOperationScope.Global
            : CovenantOperationScope.ForCampaign(id);

        Result<CovenantWriteLease> lease = await gate
            .AcquireWriteAsync(operationScope, cancellationToken)
            .ConfigureAwait(false);

        if (lease.IsFailure)
        {

            return Refuse<CovenantMutationResultDto>(
                httpContext,
                lease.Error,
                ArcanumJsonContext.Default.ApiResponseCovenantMutationResultDto);

        }

        CovenantWriteLease? owned = lease.Value;

        try
        {

            Result<CovenantMutationResultDto> committed = await commit(
                    service,
                    request,
                    owned,
                    cancellationToken)
                .ConfigureAwait(false);

            IResult response = new CovenantProtectedJsonResult<CovenantMutationResultDto>(
                owned,
                committed,
                ArcanumJsonContext.Default.ApiResponseCovenantMutationResultDto);

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

    /// <summary>
    /// The refusal for an installation that composed no Covenant arm.
    /// </summary>
    /// <remarks>
    /// A typed answer rather than a missing service, so "this build has no Covenant" and "your request
    /// was wrong" never look the same to a client.
    /// </remarks>
    private static Error UnavailableError { get; } = new(
        ErrorCodes.Covenant.Unavailable,
        "Covenant memory is not available on this installation.");

    private static IResult Refuse<T>(
        HttpContext httpContext,
        Error error,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<ApiResponse<T>> typeInfo) =>
        Results.Json(
            ApiResponse<T>.FromResult(Result<T>.Failure(error), httpContext.TraceIdentifier),
            typeInfo,
            statusCode: ArcanumErrorMapper.ResolveStatusCode(error.Code));

}
