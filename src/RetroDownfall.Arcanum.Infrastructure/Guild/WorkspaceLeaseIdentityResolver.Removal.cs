using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class WorkspaceLeaseIdentityResolver
{
    public async ValueTask<Result> ValidateDirectoryRemovalAsync(WorkspaceLeaseIdentitySet identitySet, WorkspaceLeaseDirectoryRemoval preparation,
        CancellationToken cancellationToken)
    {
        if (preparation.ValidateBeforeEffect().IsFailure) return Result.Failure(Unavailable);

        WorkspaceLeaseScopeIdentity? target = RemovalTarget(identitySet, preparation);

        if (target is null || !identitySet.Scopes.Any(scope => scope.Covers(target))) return Result.Failure(Unavailable);

        Result valid = await ValidateAsync(identitySet, cancellationToken).ConfigureAwait(false);

        return valid.IsSuccess ? preparation.ValidateBeforeEffect() : valid;
    }

    public async ValueTask<Result<WorkspaceLeaseIdentitySet>> AdoptRemovedDirectoryAsync(WorkspaceLeaseIdentitySet identitySet,
        WorkspaceLeaseRemovedDirectory proof, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (proof.Validate().IsFailure) return Unavailable;

        WorkspaceLeaseScopeIdentity? target = RemovalTarget(identitySet, proof.Preparation);

        if (target is null || !identitySet.Scopes.Any(scope => scope.Covers(target))) return Unavailable;

        Result<WorkspaceLeaseIdentitySet> resolved = await ResolveAsync(identitySet.WorkspaceRoot,
            [.. identitySet.Scopes.Select(scope => new GuildPathScope(scope.RelativePath, scope.Kind)).Distinct()], cancellationToken).ConfigureAwait(false);

        if (resolved.IsFailure || resolved.Value.RootIdentity != identitySet.RootIdentity || !identitySet.Covers(resolved.Value)) return Unavailable;

        foreach (WorkspaceLeaseScopeIdentity before in identitySet.Scopes)
        {
            WorkspaceLeaseScopeIdentity? after = resolved.Value.Scopes.FirstOrDefault(scope => scope.RelativePath == before.RelativePath && scope.Kind == before.Kind);

            if (after is null) return Unavailable;

            int removed = -1;

            for (int index = 0; index < before.Ancestors.Length; index++)
            {
                if (before.Ancestors[index].Identity == proof.Identity && before.Ancestors[index].Path == proof.Path)
                {
                    removed = index;

                    break;
                }
            }

            if (removed >= 0)
            {
                if (after.Ancestors.Length != removed || after.ObjectIdentity is not null
                    || !before.Ancestors.AsSpan()[..removed].SequenceEqual(after.Ancestors.AsSpan())) return Unavailable;

                int prefix = before.Ancestors[removed].Depth;

                if (!before.Segments.AsSpan()[..prefix].SequenceEqual(after.Segments.AsSpan()[..prefix])) return Unavailable;
            }
            else
            {
                if (!before.Ancestors.SequenceEqual(after.Ancestors) || !before.Segments.SequenceEqual(after.Segments)) return Unavailable;

                if (before.IsDirectory && before.ObjectIdentity != after.ObjectIdentity
                    && !(NamesDirectory(before, proof.Path, proof.Identity) && after.ObjectIdentity is null)) return Unavailable;
            }
        }

        return proof.Validate().IsSuccess ? resolved : Unavailable;
    }

    private static WorkspaceLeaseScopeIdentity? RemovalTarget(WorkspaceLeaseIdentitySet set, WorkspaceLeaseDirectoryRemoval preparation)
    {
        WorkspaceLeaseDirectoryIdentity? root = preparation.Ancestors.FirstOrDefault(node => node.Identity == set.RootIdentity);

        if (root is null) return null;

        string relative = Path.GetRelativePath(root.Path, preparation.Path).Replace('\\', '/');

        string leaf = Path.GetFileName(preparation.Path);

        return new(relative, GuildPathScopeKind.Subtree,
            preparation.Segments.Add(preparation.CaseSensitive ? leaf : leaf.ToUpperInvariant()),
            preparation.Ancestors, preparation.Identity, true);
    }
}
