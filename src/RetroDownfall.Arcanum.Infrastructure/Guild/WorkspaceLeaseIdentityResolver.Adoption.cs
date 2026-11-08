using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class WorkspaceLeaseIdentityResolver
{
    public async ValueTask<Result<WorkspaceLeaseIdentitySet>> AdoptCreatedDirectoryAsync(WorkspaceLeaseIdentitySet identitySet,
        WorkspaceLeaseCreatedDirectory proof, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (proof.Validate().IsFailure) return Unavailable;

        WorkspaceLeaseScopeIdentity? target = DirectoryTarget(identitySet, proof.Preparation, proof.Identity);

        bool coversTarget = target is not null && identitySet.Scopes.Any(scope => scope.Covers(target));

        bool coversSource = proof.SourceIdentity is { } sourceIdentity
            && identitySet.Scopes.Any(scope => NamesDirectory(scope, proof.SourcePath, sourceIdentity));

        if (!coversTarget && !coversSource) return Unavailable;

        ImmutableArray<GuildPathScope> scopes = [.. identitySet.Scopes.Select(scope => new GuildPathScope(scope.RelativePath, scope.Kind))
            .Concat(coversTarget ? [new GuildPathScope(target!.RelativePath, GuildPathScopeKind.Subtree)] : [])
            .Distinct()];

        Result<WorkspaceLeaseIdentitySet> resolved = await ResolveAsync(identitySet.WorkspaceRoot, scopes, cancellationToken).ConfigureAwait(false);

        if (resolved.IsFailure || resolved.Value.RootIdentity != identitySet.RootIdentity || !identitySet.Covers(resolved.Value)) return Unavailable;

        if (coversTarget)
        {
            WorkspaceLeaseScopeIdentity? actualTarget = resolved.Value.Scopes.FirstOrDefault(scope => scope.RelativePath == target!.RelativePath && scope.Kind == GuildPathScopeKind.Subtree);

            if (actualTarget is null || actualTarget.ObjectIdentity != proof.Identity
                || !actualTarget.Ancestors.SequenceEqual(proof.Preparation.Ancestors)) return Unavailable;
        }

        foreach (WorkspaceLeaseScopeIdentity before in identitySet.Scopes)
        {
            WorkspaceLeaseScopeIdentity? after = resolved.Value.Scopes.FirstOrDefault(scope => scope.RelativePath == before.RelativePath && scope.Kind == before.Kind);

            if (after is null || !OnlyCreatedDirectoryChanged(before, after, proof, coversSource)) return Unavailable;
        }

        return proof.Validate().IsSuccess ? resolved : Unavailable;
    }

    internal static WorkspaceLeaseScopeIdentity? DirectoryTarget(WorkspaceLeaseIdentitySet set, WorkspaceLeaseDirectoryCreation preparation,
        FileHandleIdentity? identity = null)
    {
        WorkspaceLeaseDirectoryIdentity? root = preparation.Ancestors.FirstOrDefault(node => node.Identity == set.RootIdentity);

        if (root is null) return null;

        string relative = Path.GetRelativePath(root.Path, preparation.Path).Replace('\\', '/');

        string leaf = Path.GetFileName(preparation.Path);

        ImmutableArray<string> segments = preparation.Segments.Add(preparation.CaseSensitive ? leaf : leaf.ToUpperInvariant());

        return new(relative, GuildPathScopeKind.Subtree, segments, preparation.Ancestors, identity, true);
    }

    public async ValueTask<Result> ValidateDirectoryCreationAsync(WorkspaceLeaseIdentitySet set, WorkspaceLeaseDirectoryCreation preparation,
        CancellationToken cancellationToken)
    {
        if (preparation.ValidateBeforeEffect().IsFailure) return Result.Failure(Unavailable);

        WorkspaceLeaseScopeIdentity? target = DirectoryTarget(set, preparation);

        if (target is null || !set.Scopes.Any(scope => scope.Covers(target))) return Result.Failure(Unavailable);

        Result current = await ValidateAsync(set, cancellationToken).ConfigureAwait(false);

        if (current.IsFailure) return current;

        foreach (WorkspaceLeaseDirectoryIdentity expected in set.Scopes.SelectMany(scope => scope.Ancestors))
        {
            WorkspaceLeaseDirectoryIdentity? actual = preparation.Ancestors.FirstOrDefault(node => node.Path == expected.Path);

            if (actual is not null && actual != expected) return Result.Failure(Unavailable);
        }

        if (preparation.SourceIdentity is { } source
            && !set.Scopes.Any(scope => NamesDirectory(scope, preparation.SourcePath, source)))
            return Result.Failure(Unavailable);

        return preparation.ValidateBeforeEffect();
    }

    internal static bool NamesDirectory(WorkspaceLeaseScopeIdentity scope, string? path, FileHandleIdentity identity)
    {
        if (path is null) return false;

        if (scope.Ancestors.Any(node => node.Identity == identity && node.Path == path)) return true;

        if (!scope.IsDirectory || scope.ObjectIdentity != identity) return false;

        WorkspaceLeaseDirectoryIdentity parent = scope.Ancestors[^1];

        if (scope.Segments.Length == parent.Depth) return parent.Path == path;

        if (scope.Segments.Length != parent.Depth + 1 || Path.GetDirectoryName(path) != parent.Path) return false;

        string leaf = Path.GetFileName(path);

        return scope.Segments[^1] == (parent.CaseSensitive ? leaf : leaf.ToUpperInvariant());
    }

    private static bool OnlyCreatedDirectoryChanged(WorkspaceLeaseScopeIdentity before, WorkspaceLeaseScopeIdentity after,
        WorkspaceLeaseCreatedDirectory proof, bool coversSource)
    {
        int common = Math.Min(before.Ancestors.Length, after.Ancestors.Length);

        if (!before.Ancestors.AsSpan()[..common].SequenceEqual(after.Ancestors.AsSpan()[..common])) return false;

        if (after.Ancestors.Length < before.Ancestors.Length)
        {
            if (proof.SourceIdentity is not { } source || before.Ancestors[common].Identity != source
                || before.Ancestors[common].Path != proof.SourcePath) return false;
        }
        else
        {
            bool movedTree = false;

            for (int index = common; index < after.Ancestors.Length; index++)
            {
                if (after.Ancestors[index].Identity == proof.Identity)
                {
                    movedTree = coversSource && proof.SourceIdentity == proof.Identity;

                    continue;
                }

                if (!movedTree) return false;
            }
        }

        if (before.IsDirectory && before.ObjectIdentity != after.ObjectIdentity)
        {
            bool created = before.ObjectIdentity is null && after.ObjectIdentity == proof.Identity;

            bool moved = proof.SourceIdentity is { } source && NamesDirectory(before, proof.SourcePath, source) && after.ObjectIdentity is null;

            if (!created && !moved) return false;
        }

        int pinnedSegments = Math.Min(before.Segments.Length, before.Ancestors[^1].Depth + 1);

        return after.Segments.Length >= pinnedSegments
            && before.Segments.AsSpan()[..pinnedSegments].SequenceEqual(after.Segments.AsSpan()[..pinnedSegments]);
    }
}
