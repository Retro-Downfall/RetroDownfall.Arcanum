using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Native inspection and lease management retain the installation's physical broker ownership.</summary>
internal sealed class GuildNativeLeaseService(GuildLocalHostPolicy policy, IGuildLocalHostReadiness readiness,
    IGrimoireOrdinaryConnectionFactory connections, GuildRosterRefreshStore cursors)
{
    internal async Task<Result<WorkspaceLeasePageDto>> ListAsync(GuildCallerContext caller, Guid? workspaceId,
        Guid? questId, GuildWorkspaceLeaseState? state, GuildPageQuery query, CancellationToken ct)
    {
        if (!Authenticates(caller)) return Missing;

        if (workspaceId == Guid.Empty || questId == Guid.Empty || query.PageSize is < 1 or > 100
            || state is { } selected && !Enum.IsDefined(selected)) return Invalid;

        string binding = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"native-leases|{workspaceId:D}|{questId:D}|{(state is { } filter ? ((int)filter).ToString(CultureInfo.InvariantCulture) : "")}")));

        GuildRosterCursorState? cursor = null;

        if (query.Cursor is { } continuation)
        {
            Result<GuildRosterCursorState> loaded = await cursors.ReadCursorAsync(caller.InstallationGeneration,
                caller.PrincipalId!.Value, binding, "list", continuation, ct).ConfigureAwait(false);

            if (loaded.IsFailure) return loaded.Error;

            cursor = loaded.Value;
        }

        GuildWorkspaceLeaseStore store = new(connections);

        Result<GuildOperatorLeasePage> read = await store.ReadOperatorPageAsync(caller, workspaceId, questId,
            state, query.PageSize, cursor, ct).ConfigureAwait(false);

        if (read.IsFailure) return read.Error;

        if (read.Value.Page.NextCursor is null) return read.Value.Page;

        Result<string> saved = await cursors.SaveCursorAsync(caller.InstallationGeneration, caller.PrincipalId!.Value,
            binding, "list", new(0, read.Value.LastSequence.ToString(CultureInfo.InvariantCulture),
                read.Value.Page.SnapshotSequence, read.Value.VisibilityEpoch), ct).ConfigureAwait(false);

        return saved.IsFailure ? saved.Error : read.Value.Page with { NextCursor = saved.Value };
    }

    internal Task<Result<WorkspaceLeaseDto>> AcquireAsync(GuildCallerContext caller, AcquireWorkspaceLeaseRequest request, CancellationToken ct)
    {
        if (!Authenticates(caller)) return Task.FromResult(Result<WorkspaceLeaseDto>.Failure(Missing));

        if (request.IdempotencyKey == Guid.Empty || request.WorkspaceId == Guid.Empty || request.Scopes.IsDefaultOrEmpty
            || request.Scopes.Any(scope => scope is null || scope.RelativePath is null || !Enum.IsDefined(scope.Kind)))
            return Task.FromResult(Result<WorkspaceLeaseDto>.Failure(Invalid));

        if (JsonSerializer.SerializeToUtf8Bytes(request.Scopes, GuildJsonContext.Default.ImmutableArrayGuildPathScope).Length
            > GuildStore.MaximumContentBytes)
            return Task.FromResult(Result<WorkspaceLeaseDto>.Failure(new Error(ErrorCodes.Guild.PayloadTooLarge,
                "The lease scope metadata exceeds its encoded representation bound.")));

        Result ready = readiness.RequireExecutionReady();

        if (ready.IsFailure) return Task.FromResult(Result<WorkspaceLeaseDto>.Failure(ready.Error));

        Result<IGuildWorkspaceLeaseService> broker = readiness.GetWorkspaceLeaseService();

        return broker.IsFailure ? Task.FromResult(Result<WorkspaceLeaseDto>.Failure(broker.Error))
            : broker.Value.AcquireAsync(caller, Mutation("acquire", null, request.IdempotencyKey, request,
                GuildJsonContext.Default.AcquireWorkspaceLeaseRequest), request, ct);
    }

    internal Task<Result<WorkspaceLeaseDto>> RenewAsync(GuildCallerContext caller, Guid leaseId,
        RenewWorkspaceLeaseRequest request, CancellationToken ct)
    {
        if (!Authenticates(caller)) return Task.FromResult(Result<WorkspaceLeaseDto>.Failure(Missing));

        Result<IGuildWorkspaceLeaseService> broker = readiness.GetWorkspaceLeaseService();

        return broker.IsFailure ? Task.FromResult(Result<WorkspaceLeaseDto>.Failure(broker.Error))
            : broker.Value.RenewAsync(caller, Mutation("renew", leaseId, request.IdempotencyKey, request,
                GuildJsonContext.Default.RenewWorkspaceLeaseRequest), leaseId, request, ct);
    }

    internal Task<Result<WorkspaceLeaseDto>> ReleaseAsync(GuildCallerContext caller, Guid leaseId,
        ReleaseWorkspaceLeaseRequest request, CancellationToken ct)
    {
        if (!Authenticates(caller)) return Task.FromResult(Result<WorkspaceLeaseDto>.Failure(Missing));

        Result<IGuildWorkspaceLeaseService> broker = readiness.GetWorkspaceLeaseService();

        return broker.IsFailure ? Task.FromResult(Result<WorkspaceLeaseDto>.Failure(broker.Error))
            : broker.Value.ReleaseAsync(caller, Mutation("release", leaseId, request.IdempotencyKey, request,
                GuildJsonContext.Default.ReleaseWorkspaceLeaseRequest), leaseId, request, ct);
    }

    private bool Authenticates(GuildCallerContext caller) => policy.Authenticates(caller)
        && caller.Kind == GuildCallerKind.Operator && caller.PrincipalId == policy.OperatorPrincipalId;

    private static GuildMutationIdentity Mutation<T>(string action, Guid? id, Guid key, T request, JsonTypeInfo<T> metadata) =>
        new(key, Convert.ToHexString(SHA256.HashData([.. Encoding.UTF8.GetBytes($"native-lease:{action}:{id:D}:"),
            .. JsonSerializer.SerializeToUtf8Bytes(request, metadata)])));

    private static Error Missing => new(ErrorCodes.Guild.NotFound, "The Guild resource was not found.");

    private static Error Invalid => new(ErrorCodes.Guild.InvalidRequest, "The lease query is invalid.");
}
