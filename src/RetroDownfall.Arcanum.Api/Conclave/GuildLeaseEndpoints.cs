using System.Diagnostics;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetroDownfall.Arcanum.Api.Primitives;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Api.Conclave;

internal static class GuildLeaseEndpoints
{
    internal static RouteGroupBuilder MapGuildLeaseEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/guild/workspace-leases", async (Guid? workspaceId, Guid? questId, string? state,
            string? cursor, int? pageSize, GuildNativeHost host, GuildNativeLeaseService service, HttpContext context) =>
        {
            GuildWorkspaceLeaseState? selected = state switch
            {
                null => null,
                "held" => GuildWorkspaceLeaseState.Held,
                "draining" => GuildWorkspaceLeaseState.Draining,
                "released" => GuildWorkspaceLeaseState.Released,
                "reconciliation-required" => GuildWorkspaceLeaseState.ReconciliationRequired,
                _ => (GuildWorkspaceLeaseState)0,
            };

            if (selected == (GuildWorkspaceLeaseState)0 || pageSize is < 1 or > 100)
                return Reply(Result<WorkspaceLeasePageDto>.Failure(Invalid), context,
                    ArcanumJsonContext.Default.ApiResponseWorkspaceLeasePageDto);

            Result<GuildCallerContext> caller = await host.CreateCallerAsync(Guid.NewGuid(), context.RequestAborted).ConfigureAwait(false);

            Result<WorkspaceLeasePageDto> result = caller.IsFailure ? caller.Error
                : await service.ListAsync(caller.Value, workspaceId, questId, selected,
                    new(cursor, pageSize ?? 100), context.RequestAborted).ConfigureAwait(false);

            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseWorkspaceLeasePageDto);
        }).WithName("GuildListWorkspaceLeases");

        api.MapPost("/guild/workspace-leases", async (AcquireWorkspaceLeaseRequest? request,
            GuildNativeHost host, GuildNativeLeaseService service, HttpContext context) =>
        {
            if (request is null || request.IdempotencyKey == Guid.Empty || request.WorkspaceId == Guid.Empty || request.Scopes.IsDefaultOrEmpty)
                return Reply(Result<WorkspaceLeaseDto>.Failure(Invalid), context, ArcanumJsonContext.Default.ApiResponseWorkspaceLeaseDto);

            Result<GuildCallerContext> caller = await host.CreateCallerAsync(request.IdempotencyKey, context.RequestAborted).ConfigureAwait(false);

            Result<WorkspaceLeaseDto> result = caller.IsFailure ? caller.Error
                : await service.AcquireAsync(caller.Value, request, context.RequestAborted).ConfigureAwait(false);

            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseWorkspaceLeaseDto,
                result.IsSuccess && result.Value.Receipt?.Replayed != true ? StatusCodes.Status201Created : StatusCodes.Status200OK);
        }).WithName("GuildAcquireWorkspaceLease");

        api.MapPost("/guild/workspace-leases/{leaseId:guid}/renew", async (Guid leaseId, RenewWorkspaceLeaseRequest? request,
            GuildNativeHost host, GuildNativeLeaseService service, HttpContext context) =>
        {
            if (request is null || request.IdempotencyKey == Guid.Empty || request.ExpectedRevision < 1 || request.Epoch < 1 || leaseId == Guid.Empty)
                return Reply(Result<WorkspaceLeaseDto>.Failure(Invalid), context, ArcanumJsonContext.Default.ApiResponseWorkspaceLeaseDto);

            Result<GuildCallerContext> caller = await host.CreateCallerAsync(request.IdempotencyKey, context.RequestAborted).ConfigureAwait(false);

            Result<WorkspaceLeaseDto> result = caller.IsFailure ? caller.Error
                : await service.RenewAsync(caller.Value, leaseId, request, context.RequestAborted).ConfigureAwait(false);

            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseWorkspaceLeaseDto);
        }).WithName("GuildRenewWorkspaceLease");

        api.MapPost("/guild/workspace-leases/{leaseId:guid}/release", async (Guid leaseId, ReleaseWorkspaceLeaseRequest? request,
            GuildNativeHost host, GuildNativeLeaseService service, HttpContext context) =>
        {
            if (request is null || request.IdempotencyKey == Guid.Empty || request.ExpectedRevision < 1 || request.Epoch < 1 || leaseId == Guid.Empty)
                return Reply(Result<WorkspaceLeaseDto>.Failure(Invalid), context, ArcanumJsonContext.Default.ApiResponseWorkspaceLeaseDto);

            Result<GuildCallerContext> caller = await host.CreateCallerAsync(request.IdempotencyKey, context.RequestAborted).ConfigureAwait(false);

            Result<WorkspaceLeaseDto> result = caller.IsFailure ? caller.Error
                : await service.ReleaseAsync(caller.Value, leaseId, request, context.RequestAborted).ConfigureAwait(false);

            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseWorkspaceLeaseDto,
                result.IsSuccess && result.Value.Receipt?.Replayed != true && result.Value.State == GuildWorkspaceLeaseState.Draining
                    ? StatusCodes.Status202Accepted : StatusCodes.Status200OK);
        }).WithName("GuildReleaseWorkspaceLease");

        return api;
    }

    private static IResult Reply<T>(Result<T> result, HttpContext context, JsonTypeInfo<ApiResponse<T>> metadata,
        int status = StatusCodes.Status200OK) => Results.Json(ApiResponse<T>.FromResult(result,
            Activity.Current?.Id ?? context.TraceIdentifier), metadata,
            statusCode: result.IsSuccess ? status : ArcanumErrorMapper.ResolveStatusCodeDefaultBadRequest(result.Error.Code));

    private static Error Invalid => new(ErrorCodes.Guild.InvalidRequest, "An explicit valid lease request is required.");
}
