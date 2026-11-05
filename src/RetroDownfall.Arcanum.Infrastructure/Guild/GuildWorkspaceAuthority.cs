using System.Collections.Immutable;
using Microsoft.Win32.SafeHandles;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Tower;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildWorkspaceRegistration(Guid WorkspaceId, string RootPath, uint PolicyVersion,
    long Revision, CovenantDigest RootIdentity);

/// <summary>Physical scope evidence only; callers must separately hold live Signet admission.</summary>
internal sealed class GuildWorkspaceAuthority(GuildWorkspaceRegistration registration, PhysicalCampaignRootOpener opener,
    Func<CancellationToken, Task<Result<GuildWorkspaceRegistration>>> resolveCurrent)
{
    private static Error Unavailable => new(ErrorCodes.Guild.WorkspaceIdentityUnavailable,
        "The granted physical workspace identity is unavailable.");

    private static Error Forbidden => new(ErrorCodes.Guild.Forbidden,
        "The resource is outside the granted physical workspace scope.");

    internal async Task<Result> ValidateAsync(GuildGrant grant, CancellationToken cancellationToken)
    {
        Result valid = GuildGrantPolicy.Validate(grant);

        if (valid.IsFailure)
        {
            return valid;
        }

        Result current = await ValidateRegistrationAsync(cancellationToken).ConfigureAwait(false);

        if (current.IsFailure)
        {
            return current;
        }

        foreach (GuildWorkspaceGrant scope in grant.Workspaces)
        {
            if (scope.WorkspaceId != registration.WorkspaceId || !TryResolveDirectory(scope.RelativeRoot, out _))
            {
                return Forbidden;
            }
        }

        return Result.Success();
    }

    internal async Task<Result<GuildGrantScopeEvidence>> ResolveContainmentAsync(GuildGrant parent, GuildGrant child,
        CancellationToken cancellationToken)
    {
        Result validParent = await ValidateAsync(parent, cancellationToken).ConfigureAwait(false);

        if (validParent.IsFailure)
        {
            return validParent.Error;
        }

        Result validChild = await ValidateAsync(child, cancellationToken).ConfigureAwait(false);

        if (validChild.IsFailure)
        {
            return validChild.Error;
        }

        ImmutableArray<(GuildWorkspaceGrant Parent, GuildWorkspaceGrant Child)>.Builder containment = ImmutableArray.CreateBuilder<(GuildWorkspaceGrant, GuildWorkspaceGrant)>();

        foreach (GuildWorkspaceGrant issuer in parent.Workspaces)
        {
            foreach (GuildWorkspaceGrant requested in child.Workspaces)
            {
                if (issuer.WorkspaceId == requested.WorkspaceId && TryResolveDirectory(issuer.RelativeRoot, out string issuerPath)
                    && TryResolveDirectory(requested.RelativeRoot, out string requestedPath)
                    && WorkspacePathPolicy.IsPathUnderWorkspace(issuerPath, requestedPath))
                {
                    containment.Add((issuer, requested));
                }
            }
        }

        Result current = await ValidateRegistrationAsync(cancellationToken).ConfigureAwait(false);

        return current.IsFailure ? Result<GuildGrantScopeEvidence>.Failure(current.Error)
            : Result<GuildGrantScopeEvidence>.Success(new(containment.ToImmutable(), []));
    }

    /// <summary>
    /// Opens the exact granted resource with no-follow and post-open handle checks. The caller must
    /// retain its root Signet admission through disclosure; this physical helper never authenticates a grant.
    /// </summary>
    internal async Task<Result<FileStream>> OpenReadAsync(GuildGrant effectiveGrant, Guid workspaceId, string relativePath,
        CancellationToken cancellationToken)
    {
        if (workspaceId != registration.WorkspaceId || !IsRelativePath(relativePath))
        {
            return Forbidden;
        }

        Result valid = await ValidateAsync(effectiveGrant, cancellationToken).ConfigureAwait(false);

        if (valid.IsFailure)
        {
            return valid.Error;
        }

        string resource = Path.Combine(registration.RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

        string? grantedRoot = null;

        string? grantedRelativeRoot = null;

        foreach (GuildWorkspaceGrant scope in effectiveGrant.Workspaces)
        {
            if (scope.WorkspaceId == workspaceId && TryResolveDirectory(scope.RelativeRoot, out string path)
                && WorkspacePathPolicy.IsPathUnderWorkspace(path, resource))
            {
                grantedRoot = path;

                grantedRelativeRoot = scope.RelativeRoot;

                break;
            }
        }

        string? directory = Path.GetDirectoryName(relativePath.Replace('/', Path.DirectorySeparatorChar));

        if (grantedRoot is null || !TryOpenScope(grantedRelativeRoot!, out ScopeHandles? scopeHandles))
        {
            return Forbidden;
        }

        using ScopeHandles physicalScope = scopeHandles!;

        if (!TryResolveDirectory(directory?.Replace(Path.DirectorySeparatorChar, '/') ?? ".", out _)
            || !FileHandleIdentityInterop.TryGetPathMetadataNoFollow(resource, out FileHandleMetadata metadata)
            || metadata.Kind != FileSystemObjectKind.RegularFile
            || !SandboxedFileIo.TryOpenForRead(grantedRoot, resource, out FileStream? opened, out _))
        {
            return Forbidden;
        }

        bool transferred = false;

        try
        {
            // The root registration and directory identities are checked again after open. A renamed
            // root or swapped parent cannot turn a previously validated spelling into read authority.
            Result current = await ValidateRegistrationAsync(cancellationToken).ConfigureAwait(false);

            if (current.IsFailure)
            {
                return current.Error;
            }

            if (!TryResolveDirectory(directory?.Replace(Path.DirectorySeparatorChar, '/') ?? ".", out _)
                || !SecureFileReader.TryValidateRegularFileHandle(opened.SafeFileHandle, metadata.Identity, out _)
                || !IsHandleContained(physicalScope.Root, physicalScope.Granted)
                || !IsHandleContained(physicalScope.Granted, opened.SafeFileHandle))
            {
                return Forbidden;
            }

            cancellationToken.ThrowIfCancellationRequested();

            transferred = true;

            return opened;
        }
        finally
        {
            if (!transferred)
            {
                opened.Dispose();
            }
        }
    }

    private async Task<Result> ValidateRegistrationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Result<GuildWorkspaceRegistration> resolved = await resolveCurrent(cancellationToken).ConfigureAwait(false);

        if (resolved.IsFailure)
        {
            return resolved.Error;
        }

        GuildWorkspaceRegistration current = resolved.Value;

        if (registration.WorkspaceId == Guid.Empty || registration.PolicyVersion != Core.Tower.CampaignPathIdentityPolicy.Version
            || registration.Revision <= 0 || !registration.RootIdentity.IsValid
            || current.WorkspaceId != registration.WorkspaceId || current.PolicyVersion != registration.PolicyVersion
            || current.Revision != registration.Revision || current.RootIdentity != registration.RootIdentity
            || !string.Equals(current.RootPath, registration.RootPath, StringComparison.Ordinal)
            || opener.IdentifyExact(registration.RootPath) != registration.RootIdentity)
        {
            return Unavailable;
        }

        return Result.Success();
    }

    private bool TryResolveDirectory(string relativeRoot, out string resolved)
    {
        resolved = registration.RootPath;

        if (relativeRoot is not ("" or ".") && !IsRelativePath(relativeRoot))
        {
            return false;
        }

        if (!FileHandleIdentityInterop.TryGetPathMetadataNoFollow(resolved, out FileHandleMetadata root)
            || root.Kind != FileSystemObjectKind.Directory || opener.IdentifyExact(resolved) != registration.RootIdentity)
        {
            return false;
        }

        if (relativeRoot is "" or ".")
        {
            return true;
        }

        foreach (string segment in relativeRoot.Split('/'))
        {
            resolved = Path.Combine(resolved, segment);

            if (!FileHandleIdentityInterop.TryGetPathMetadataNoFollow(resolved, out FileHandleMetadata current)
                || current.Kind != FileSystemObjectKind.Directory || current.Identity.VolumeId != root.Identity.VolumeId)
            {
                return false;
            }
        }

        return WorkspacePathPolicy.IsPathUnderWorkspace(registration.RootPath, resolved);
    }

    private bool TryOpenScope(string relativeRoot, out ScopeHandles? handles)
    {
        handles = null;

        if (!FileHandleIdentityInterop.TryOpenDirectoryMetadata(registration.RootPath, out SafeFileHandle root,
                out FileHandleMetadata rootMetadata))
        {
            root.Dispose();

            return false;
        }

        SafeFileHandle current = root;

        bool transferred = false;

        try
        {
            if (opener.IdentifyOpenedDirectory(root) != registration.RootIdentity)
            {
                return false;
            }

            if (relativeRoot is not ("" or "."))
            {
                foreach (string segment in relativeRoot.Split('/'))
                {
                    if (!FileHandleIdentityInterop.TryOpenDirectoryMetadataRelative(current, segment, requestReadControl: false,
                        out SafeFileHandle child, out FileHandleMetadata metadata))
                    {
                        child.Dispose();

                        return false;
                    }

                    if (!ReferenceEquals(current, root))
                    {
                        current.Dispose();
                    }

                    current = child;

                    if (metadata.Identity.VolumeId != rootMetadata.Identity.VolumeId)
                    {
                        return false;
                    }
                }
            }

            handles = new(root, current);

            transferred = true;

            return true;
        }
        finally
        {
            if (!transferred)
            {
                if (!ReferenceEquals(current, root))
                {
                    current.Dispose();
                }

                root.Dispose();
            }
        }
    }

    private static bool IsHandleContained(SafeFileHandle root, SafeFileHandle child) =>
        FileHandleIdentityInterop.TryGetHandleKernelPath(root, out string? rootPath)
        && FileHandleIdentityInterop.TryGetHandleKernelPath(child, out string? childPath)
        && WorkspacePathPolicy.IsPathUnderWorkspace(rootPath, childPath);

    private sealed class ScopeHandles(SafeFileHandle root, SafeFileHandle granted) : IDisposable
    {
        internal SafeFileHandle Root { get; } = root;

        internal SafeFileHandle Granted { get; } = granted;

        public void Dispose()
        {
            if (!ReferenceEquals(Root, Granted))
            {
                Granted.Dispose();
            }

            Root.Dispose();
        }
    }

    private static bool IsRelativePath(string path) => !string.IsNullOrEmpty(path)
        && !path.Contains('\\') && !path.Contains(':') && !path.Contains('*') && !path.Contains('?') && !path.Any(char.IsControl)
        && path.Split('/').All(segment => segment.Length > 0 && segment is not "." and not "..");
}
