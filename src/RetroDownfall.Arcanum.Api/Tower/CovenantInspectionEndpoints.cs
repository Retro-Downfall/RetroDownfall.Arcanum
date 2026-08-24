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
/// The six routes an operator reads their own Covenant through.
/// </summary>
/// <remarks>
/// All six are <c>POST</c> with a typed body, including the ones that only read. Scope selections,
/// Campaign identities, keys, free text, and cursors never enter a request URL, because a URL is the
/// one part of a request that reliably reaches an access log — and every one of those values is
/// either protected content or a direct pointer to it.
///
/// <para>Each takes a scoped read lease for a named scope and the installation read capability for an
/// all-scopes request, which is the store's own rule rather than this layer's: an all-scopes read
/// crosses every Campaign and a scoped lease does not cover that. <c>Explain</c> is the exception —
/// it detaches its own lease, so the handler transfers what the service hands back rather than
/// acquiring anything.</para>
/// </remarks>
internal static class CovenantInspectionEndpoints
{

    public static RouteGroupBuilder MapCovenantInspectionEndpoints(this RouteGroupBuilder apiGroup)
    {

        apiGroup.MapPost(
            "/memory/covenant/list",
            static async Task<IResult> (
                CovenantListRequest? request,
                ICovenantManagementService? service,
                ICovenantOperationGate? gate,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
                await ReadAsync(
                        request,
                        service,
                        gate,
                        httpContext,
                        request?.Scope,
                        request?.CampaignId,
                        static (management, body, lease, token) => management.ListAsync(body, lease, token),
                        ArcanumJsonContext.Default.ApiResponseCovenantPageDto,
                        cancellationToken)
                    .ConfigureAwait(false))
            .WithName("ListCovenantEntries")
            .RequireCovenantReadAuthority();

        apiGroup.MapPost(
            "/memory/covenant/query",
            static async Task<IResult> (
                CovenantQueryRequest? request,
                ICovenantManagementService? service,
                ICovenantOperationGate? gate,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
                await ReadAsync(
                        request,
                        service,
                        gate,
                        httpContext,
                        request?.Scope,
                        request?.CampaignId,
                        static (management, body, lease, token) => management.QueryAsync(body, lease, token),
                        ArcanumJsonContext.Default.ApiResponseCovenantPageDto,
                        cancellationToken)
                    .ConfigureAwait(false))
            .WithName("QueryCovenantEntries")
            .RequireCovenantReadAuthority();

        apiGroup.MapPost(
            "/memory/covenant/detail",
            static async Task<IResult> (
                CovenantDetailRequest? request,
                ICovenantManagementService? service,
                ICovenantOperationGate? gate,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
                await ReadAsync(
                        request,
                        service,
                        gate,
                        httpContext,
                        request?.Scope is CovenantScope.Campaign
                            ? CovenantCursorScopeSelection.Campaign
                            : CovenantCursorScopeSelection.Global,
                        request?.CampaignId,
                        static (management, body, lease, token) => management.DetailAsync(body, lease, token),
                        ArcanumJsonContext.Default.ApiResponseCovenantDetailDto,
                        cancellationToken)
                    .ConfigureAwait(false))
            .WithName("ShowCovenantEntry")
            .RequireCovenantReadAuthority();

        apiGroup.MapPost(
            "/memory/covenant/versions",
            static async Task<IResult> (
                CovenantVersionsRequest? request,
                ICovenantManagementService? service,
                ICovenantOperationGate? gate,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
                await ReadAsync(
                        request,
                        service,
                        gate,
                        httpContext,

                        // A version page is keyed by entry identity, not by scope, and the store lets
                        // any valid lease read one. The installation capability is taken because the
                        // entry's scope is not known until the row is read.
                        CovenantCursorScopeSelection.AllScopes,
                        null,
                        static (management, body, lease, token) => management.VersionsAsync(body, lease, token),
                        ArcanumJsonContext.Default.ApiResponseCovenantVersionPageDto,
                        cancellationToken)
                    .ConfigureAwait(false))
            .WithName("ListCovenantVersions")
            .RequireCovenantReadAuthority();

        apiGroup.MapPost(
            "/memory/covenant/sources",
            static async Task<IResult> (
                CovenantSourcesRequest? request,
                ICovenantManagementService? service,
                ICovenantOperationGate? gate,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
                await ReadAsync(
                        request,
                        service,
                        gate,
                        httpContext,
                        CovenantCursorScopeSelection.AllScopes,
                        null,
                        static (management, body, lease, token) => management.SourcesAsync(body, lease, token),
                        ArcanumJsonContext.Default.ApiResponseCovenantSourcesDto,
                        cancellationToken)
                    .ConfigureAwait(false))
            .WithName("ListCovenantSources")
            .RequireCovenantReadAuthority();

        apiGroup.MapPost(
            "/memory/covenant/explain",
            static async Task<IResult> (
                CovenantExplainRequest? request,
                ICovenantManagementService? service,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
                await ExplainAsync(request, service, httpContext, cancellationToken)
                    .ConfigureAwait(false))
            .WithName("ExplainCovenant")
            .RequireCovenantReadAuthority();

        return apiGroup;

    }

    /// <summary>
    /// Transfers the lease Explain detached for itself.
    /// </summary>
    /// <remarks>
    /// Nothing is acquired here. Explain builds its own snapshot and hands back the lease that covers
    /// it; <c>Take()</c> moves that lease exactly once into the protected result, which revalidates
    /// before the first byte and disposes it after the last.
    /// </remarks>
    private static async Task<IResult> ExplainAsync(
        CovenantExplainRequest? request,
        ICovenantManagementService? service,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {

        if (request is null)
        {

            return Refuse(
                httpContext,
                new Error(ErrorCodes.Validation.InvalidBody, "A Covenant explain request is required."),
                ArcanumJsonContext.Default.ApiResponseCovenantExplainDto);

        }

        if (service is null)
        {

            return Refuse(
                httpContext,
                UnavailableError,
                ArcanumJsonContext.Default.ApiResponseCovenantExplainDto);

        }

        CovenantLeasedServiceResult<CovenantExplainDto> explained = await service
            .ExplainAsync(request, cancellationToken)
            .ConfigureAwait(false);

        (Result<CovenantExplainDto> payload, ICovenantOperationLease lease) = explained.Take();

        return new CovenantProtectedJsonResult<CovenantExplainDto>(
            lease,
            payload,
            ArcanumJsonContext.Default.ApiResponseCovenantExplainDto);

    }

    private static async Task<IResult> ReadAsync<TRequest, TResponse>(
        TRequest? request,
        ICovenantManagementService? service,
        ICovenantOperationGate? gate,
        HttpContext httpContext,
        CovenantCursorScopeSelection? selection,
        Guid? campaignId,
        Func<ICovenantManagementService, TRequest, ICovenantSnapshotReadLease, CancellationToken,
            ValueTask<Result<TResponse>>> read,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<ApiResponse<TResponse>> typeInfo,
        CancellationToken cancellationToken)
        where TRequest : class
    {

        if (request is null || selection is not { } scopeSelection)
        {

            return Refuse(
                httpContext,
                new Error(ErrorCodes.Validation.InvalidBody, "A Covenant inspection request is required."),
                typeInfo);

        }

        if (service is null || gate is null)
        {

            return Refuse(httpContext, UnavailableError, typeInfo);

        }

        // An all-scopes read crosses every Campaign, which a scoped lease does not cover. The store
        // enforces this itself; taking the right lease here is what turns its refusal into a case that
        // never arises rather than an error an operator sees.
        Result<ICovenantSnapshotReadLease> lease = scopeSelection is CovenantCursorScopeSelection.Campaign
            && campaignId is { } id
            ? Widen(await gate.AcquireReadAsync(CovenantOperationScope.ForCampaign(id), cancellationToken)
                .ConfigureAwait(false))
            : Widen(await gate.AcquireInstallationReadAsync(cancellationToken).ConfigureAwait(false));

        if (lease.IsFailure)
        {

            return Refuse(httpContext, lease.Error, typeInfo);

        }

        ICovenantSnapshotReadLease? owned = lease.Value;

        try
        {

            Result<TResponse> answered = await read(service, request, owned, cancellationToken)
                .ConfigureAwait(false);

            IResult response = new CovenantProtectedJsonResult<TResponse>(owned, answered, typeInfo);

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

    private static Result<ICovenantSnapshotReadLease> Widen<TLease>(Result<TLease> lease)
        where TLease : ICovenantSnapshotReadLease =>
        lease.IsFailure
            ? Result<ICovenantSnapshotReadLease>.Failure(lease.Error)
            : Result<ICovenantSnapshotReadLease>.Success(lease.Value);

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
