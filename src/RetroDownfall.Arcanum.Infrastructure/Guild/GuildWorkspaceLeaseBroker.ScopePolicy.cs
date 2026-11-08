using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildWorkspaceLeaseBroker
{
    private async Task<Result> CoversAllowedAsync(GuildWorkspaceLeaseBinding binding, WorkspaceLeaseIdentitySet requested,
        ImmutableArray<GuildPathScope> scopes, CancellationToken ct)
    {
        if (binding.AllowedScopes.IsDefaultOrEmpty || scopes.IsDefaultOrEmpty) return Forbidden;

        List<WorkspaceLeaseScopeIdentity> allowed = [];

        foreach (GuildPathScope grant in binding.AllowedScopes)
        {
            Result<WorkspaceLeaseIdentitySet> resolved = await identities.ResolveAsync(binding.WorkspaceRoot, [grant], ct).ConfigureAwait(false);

            if (resolved.IsFailure || resolved.Value.RootIdentity != requested.RootIdentity) return Forbidden;

            WorkspaceLeaseScopeIdentity? exact = resolved.Value.Scopes.FirstOrDefault(scope => scope.RelativePath == (grant.RelativePath is "" ? "." : grant.RelativePath) && scope.Kind == grant.Kind);

            if (exact is null) return Forbidden;

            allowed.Add(exact);
        }

        // Missing-parent reservations are effects too; a deeper grant cannot authorize
        // creating an ancestor outside its exact resolved scope.
        foreach (WorkspaceLeaseScopeIdentity scope in requested.Scopes)
        {
            if (!allowed.Any(grant => grant.Covers(scope))) return Forbidden;
        }

        return Result.Success();
    }
}
