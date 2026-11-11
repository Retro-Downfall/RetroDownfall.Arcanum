using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildLocalHostPolicy
{
    internal async Task<Result<GuildGrant>> ResolveCohortGrantWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildGrant live, GuildRequestedGrant? request, CancellationToken ct)
    {
        request ??= new();

        if (GuildGrantPolicy.Validate(live).IsFailure
            || request.Workspaces.IsSpecified && request.Workspaces.Value is null
            || request.Tools.IsSpecified && request.Tools.Value is null
            || request.Memory.IsSpecified && request.Memory.Value is null
            || request.Budget.IsSpecified && request.Budget.Value is null
            || request.Delegation.IsSpecified && request.Delegation.Value is null) return Forbidden;

        GuildGrant requested = new(request.Workspaces.IsSpecified ? request.Workspaces.Value!.Value : live.Workspaces,
            request.Tools.IsSpecified ? request.Tools.Value!.Value : live.Tools,
            request.Memory.IsSpecified ? request.Memory.Value!.Value : live.Memory,
            request.Budget.IsSpecified ? request.Budget.Value! : live.Budget,
            request.Delegation.IsSpecified ? request.Delegation.Value!.Value : live.Delegation,
            request.ExpiresAtUtc.IsSpecified ? request.ExpiresAtUtc.Value : live.ExpiresAtUtc);

        var scope = await ScopeAsync(connection, transaction, live, requested, ct).ConfigureAwait(false);

        return scope.IsFailure ? scope.Error : GuildGrantPolicy.Attenuate(live, request, scope.Value);
    }
}
