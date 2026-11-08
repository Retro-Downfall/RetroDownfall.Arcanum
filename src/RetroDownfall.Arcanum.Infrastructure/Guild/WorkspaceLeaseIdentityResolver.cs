using System.Collections.Immutable;
using Microsoft.Win32.SafeHandles;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal interface IWorkspaceLeaseIdentityResolver
{
    ValueTask<Result<WorkspaceLeaseIdentitySet>> ResolveAsync(string workspaceRoot, ImmutableArray<GuildPathScope> scopes, CancellationToken cancellationToken);

    ValueTask<Result> ValidateAsync(WorkspaceLeaseIdentitySet identitySet, CancellationToken cancellationToken);

    ValueTask<Result> ValidateDirectoryCreationAsync(WorkspaceLeaseIdentitySet identitySet, WorkspaceLeaseDirectoryCreation preparation, CancellationToken cancellationToken);

    ValueTask<Result<WorkspaceLeaseIdentitySet>> AdoptCreatedDirectoryAsync(WorkspaceLeaseIdentitySet identitySet, WorkspaceLeaseCreatedDirectory proof, CancellationToken cancellationToken);

    ValueTask<Result> ValidateDirectoryRemovalAsync(WorkspaceLeaseIdentitySet identitySet, WorkspaceLeaseDirectoryRemoval preparation, CancellationToken cancellationToken);

    ValueTask<Result<WorkspaceLeaseIdentitySet>> AdoptRemovedDirectoryAsync(WorkspaceLeaseIdentitySet identitySet, WorkspaceLeaseRemovedDirectory proof, CancellationToken cancellationToken);
}

internal sealed partial class WorkspaceLeaseIdentityResolver : IWorkspaceLeaseIdentityResolver
{
    internal static Error Unavailable => new(ErrorCodes.Guild.WorkspaceIdentityUnavailable, "The workspace physical identity or filesystem name policy cannot be proved.");

    public ValueTask<Result<WorkspaceLeaseIdentitySet>> ResolveAsync(string workspaceRoot, ImmutableArray<GuildPathScope> scopes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (scopes.IsDefaultOrEmpty || !Path.IsPathFullyQualified(workspaceRoot)
                || !FileHandleIdentityInterop.TryOpenDirectoryMetadata(workspaceRoot, out SafeFileHandle root, out FileHandleMetadata rootMetadata))
            {
                return ValueTask.FromResult(Result<WorkspaceLeaseIdentitySet>.Failure(Unavailable));
            }

            using (root)
            {
                if (!FileHandleIdentityInterop.TryGetHandleKernelPath(root, out string? canonicalRoot)
                    || !FileHandleIdentityInterop.TryGetDirectoryCaseSensitivity(root, out bool caseSensitive)
                    || !TryCaptureRootChain(canonicalRoot, rootMetadata.Identity, out var rootAncestors, out var rootSegments)
                    || caseSensitive != rootAncestors[^1].CaseSensitive)
                {
                    return ValueTask.FromResult(Result<WorkspaceLeaseIdentitySet>.Failure(Unavailable));
                }

                var resolved = ImmutableArray.CreateBuilder<WorkspaceLeaseScopeIdentity>();

                foreach (GuildPathScope scope in scopes)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!TryResolveScope(root, canonicalRoot, rootMetadata.Identity, caseSensitive, rootAncestors, rootSegments, scope, resolved))
                    {
                        return ValueTask.FromResult(Result<WorkspaceLeaseIdentitySet>.Failure(Unavailable));
                    }
                }

                var value = new WorkspaceLeaseIdentitySet(Path.GetFullPath(workspaceRoot), rootMetadata.Identity,
                    [.. resolved.OrderBy(scope => string.Join('/', scope.Segments), StringComparer.Ordinal).ThenBy(scope => scope.Kind)]);

                return ValueTask.FromResult(Result<WorkspaceLeaseIdentitySet>.Success(value));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ValueTask.FromResult(Result<WorkspaceLeaseIdentitySet>.Failure(Unavailable));
        }
    }

    public async ValueTask<Result> ValidateAsync(WorkspaceLeaseIdentitySet identitySet, CancellationToken cancellationToken)
    {
        Result<WorkspaceLeaseIdentitySet> current = await ResolveAsync(identitySet.WorkspaceRoot,
            [.. identitySet.Scopes.Select(scope => new GuildPathScope(scope.RelativePath, scope.Kind))], cancellationToken);

        if (current.IsFailure || current.Value.RootIdentity != identitySet.RootIdentity)
        {
            return Result.Failure(Unavailable);
        }

        foreach (WorkspaceLeaseScopeIdentity scope in identitySet.Scopes)
        {
            WorkspaceLeaseScopeIdentity? actual = current.Value.Scopes.FirstOrDefault(candidate => candidate.RelativePath == scope.RelativePath && candidate.Kind == scope.Kind);

            if (actual is null || !scope.Ancestors.SequenceEqual(actual.Ancestors)
                || !scope.Segments.SequenceEqual(actual.Segments)
                || (scope.IsDirectory || actual.IsDirectory) && scope.ObjectIdentity != actual.ObjectIdentity)
            {
                return Result.Failure(Unavailable);
            }
        }

        return Result.Success();
    }

    internal static bool TryCaptureRootChain(string canonicalRoot, FileHandleIdentity expectedRoot,
        out ImmutableArray<WorkspaceLeaseDirectoryIdentity> ancestors, out ImmutableArray<string> segments)
    {
        ancestors = [];

        segments = [];

        string? volumeRoot = Path.GetPathRoot(canonicalRoot);

        if (volumeRoot is null
            || !FileHandleIdentityInterop.TryOpenDirectoryMetadata(volumeRoot, out SafeFileHandle volume, out FileHandleMetadata metadata))
        {
            return false;
        }

        var handles = new List<SafeFileHandle> { volume };

        try
        {
            if (!FileHandleIdentityInterop.TryGetDirectoryCaseSensitivity(volume, out bool sensitive))
            {
                return false;
            }

            var chain = ImmutableArray.CreateBuilder<WorkspaceLeaseDirectoryIdentity>();

            var names = ImmutableArray.CreateBuilder<string>();

            chain.Add(new(volumeRoot, metadata.Identity, sensitive, 0));

            SafeFileHandle parent = volume;

            string parentPath = volumeRoot;

            string[] parts = canonicalRoot[volumeRoot.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

            foreach (string name in parts)
            {
                if (!IsSegment(name) || name.Any(character => character > 127))
                {
                    return false;
                }

                names.Add(sensitive ? name : name.ToUpperInvariant());

                if (!FileHandleIdentityInterop.TryOpenDirectoryMetadataRelative(parent, name, false, out SafeFileHandle child, out metadata))
                {
                    child.Dispose();

                    return false;
                }

                handles.Add(child);

                if (!FileHandleIdentityInterop.TryGetHandleKernelPath(child, out string? childPath)
                    || !string.Equals(Path.GetDirectoryName(childPath), parentPath, StringComparison.Ordinal)
                    || sensitive && Path.GetFileName(childPath) != name
                    || !FileHandleIdentityInterop.TryGetDirectoryCaseSensitivity(child, out sensitive))
                {
                    return false;
                }

                chain.Add(new(childPath, metadata.Identity, sensitive, names.Count));

                parent = child;

                parentPath = childPath;
            }

            if (metadata.Identity != expectedRoot)
            {
                return false;
            }

            ancestors = chain.ToImmutable();

            segments = names.ToImmutable();

            return true;
        }
        finally
        {
            foreach (SafeFileHandle handle in handles)
            {
                handle.Dispose();
            }
        }
    }

    private static bool TryResolveScope(SafeFileHandle root, string rootPath, FileHandleIdentity rootIdentity,
        bool caseSensitive, ImmutableArray<WorkspaceLeaseDirectoryIdentity> rootAncestors, ImmutableArray<string> rootSegments,
        GuildPathScope scope, ImmutableArray<WorkspaceLeaseScopeIdentity>.Builder output)
    {
        if (scope is null || scope.Kind is not (GuildPathScopeKind.File or GuildPathScopeKind.Subtree)
            || scope.RelativePath is null || Path.IsPathRooted(scope.RelativePath))
        {
            return false;
        }

        string[] parts = scope.RelativePath is "" or "." ? [] : scope.RelativePath.Replace('\\', '/').Split('/');

        if (parts.Any(part => !IsSegment(part)))
        {
            return false;
        }

        var ancestors = ImmutableArray.CreateBuilder<WorkspaceLeaseDirectoryIdentity>();

        ancestors.AddRange(rootAncestors);

        if (parts.Length == 0)
        {
            if (scope.Kind != GuildPathScopeKind.Subtree)
            {
                return false;
            }

            output.Add(new(".", scope.Kind, rootSegments, ancestors.ToImmutable(), rootIdentity, true));

            return true;
        }

        var segments = ImmutableArray.CreateBuilder<string>();

        segments.AddRange(rootSegments);

        var owned = new List<SafeFileHandle>();

        SafeFileHandle parent = root;

        string parentPath = rootPath;

        try
        {
            for (int index = 0; index < parts.Length; index++)
            {
                string name = parts[index];

                // ASCII folding is proved for the supported filesystems. Unicode normalization/folding
                // is filesystem-specific and is deliberately unavailable until it can be proved too.
                if (name.Any(character => character > 127))
                {
                    return false;
                }

                segments.Add(caseSensitive ? name : name.ToUpperInvariant());

                bool last = index == parts.Length - 1;

                if (FileHandleIdentityInterop.TryOpenDirectoryMetadataRelative(parent, name, false, out SafeFileHandle directory, out FileHandleMetadata directoryMetadata))
                {
                    owned.Add(directory);

                    if (!FileHandleIdentityInterop.TryGetHandleKernelPath(directory, out string? directoryPath)
                        || !WorkspacePathPolicy.IsPathUnderWorkspace(rootPath, directoryPath)
                        || !string.Equals(Path.GetDirectoryName(directoryPath), parentPath, StringComparison.Ordinal)
                        || !string.Equals(Path.GetFileName(directoryPath), name, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)
                        || !FileHandleIdentityInterop.TryGetDirectoryCaseSensitivity(directory, out bool childCaseSensitive))
                    {
                        return false;
                    }

                    if (last)
                    {
                        if (scope.Kind != GuildPathScopeKind.Subtree)
                        {
                            return false;
                        }

                        output.Add(new(scope.RelativePath, scope.Kind, segments.ToImmutable(), ancestors.ToImmutable(), directoryMetadata.Identity, true));

                        return true;
                    }

                    ancestors.Add(new(directoryPath, directoryMetadata.Identity, childCaseSensitive, rootSegments.Length + index + 1));

                    parent = directory;

                    parentPath = directoryPath;

                    caseSensitive = childCaseSensitive;

                    continue;
                }

                directory.Dispose();

                SecureFileOpenStatus status = FileHandleIdentityInterop.TryOpenReadOnlyNoFollowRelative(parent, name, out SafeFileHandle? file);

                using (file)
                {
                    if (status == SecureFileOpenStatus.Success)
                    {
                        if (!last || scope.Kind != GuildPathScopeKind.File || file is null
                            || !FileHandleIdentityInterop.TryGetHandleMetadata(file, out FileHandleMetadata metadata)
                            || metadata.Kind != FileSystemObjectKind.RegularFile || metadata.HardLinkCount != 1
                            || !FileHandleIdentityInterop.TryGetHandleKernelPath(file, out string? filePath)
                            || !WorkspacePathPolicy.IsPathUnderWorkspace(rootPath, filePath)
                            || !string.Equals(Path.GetDirectoryName(filePath), parentPath, StringComparison.Ordinal)
                            || !string.Equals(Path.GetFileName(filePath), name, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }

                        output.Add(new(scope.RelativePath, scope.Kind, segments.ToImmutable(), ancestors.ToImmutable(), metadata.Identity, false));

                        return true;
                    }
                }

                if (status != SecureFileOpenStatus.NotFound)
                {
                    return false;
                }

                if (!last)
                {
                    output.Add(new(string.Join('/', parts.Take(index + 1)), GuildPathScopeKind.Subtree,
                        segments.ToImmutable(), ancestors.ToImmutable(), null, true));
                }

                for (int remaining = index + 1; remaining < parts.Length; remaining++)
                {
                    if (parts[remaining].Any(character => character > 127))
                    {
                        return false;
                    }

                    segments.Add(caseSensitive ? parts[remaining] : parts[remaining].ToUpperInvariant());
                }

                output.Add(new(scope.RelativePath, scope.Kind, segments.ToImmutable(), ancestors.ToImmutable(), null, scope.Kind == GuildPathScopeKind.Subtree));

                return true;
            }

            return false;
        }
        finally
        {
            foreach (SafeFileHandle handle in owned)
            {
                handle.Dispose();
            }
        }
    }

    internal static bool IsSegment(string segment) => !string.IsNullOrEmpty(segment) && segment is not ("." or "..")
        && segment.IndexOfAny(['/', '\\', '\0', ':']) < 0 && !segment.EndsWith('.') && !segment.EndsWith(' ');
}
