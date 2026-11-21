using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Collections.Immutable;
using Microsoft.Win32.SafeHandles;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Tower;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildWorkspaceRegistration(Guid WorkspaceId, string RootPath, uint PolicyVersion,
    long Revision, CovenantDigest RootIdentity);

/// <summary>Physical scope evidence only; callers must separately hold live Signet admission.</summary>
internal sealed partial class GuildWorkspaceAuthority(GuildWorkspaceRegistration registration, PhysicalCampaignRootOpener opener,
    Func<CancellationToken, Task<Result<GuildWorkspaceRegistration>>> resolveCurrent)
{
    private GuildWorkspaceRegistration Registration => registration;

    private PhysicalCampaignRootOpener Opener => opener;

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

    /// <summary>Retains root, granted-directory and file handles until the caller completes disclosure.</summary>
    internal async Task<Result<RetainedRead>> OpenRetainedReadAsync(GuildGrant grant, Guid workspaceId, string relativePath,
        CancellationToken cancellationToken)
    {
        if (workspaceId != registration.WorkspaceId || !IsRelativePath(relativePath)) return Forbidden;

        string resource = Path.Combine(registration.RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

        foreach (GuildWorkspaceGrant scope in grant.Workspaces)
        {
            if (scope.WorkspaceId != workspaceId || !TryResolveDirectory(scope.RelativeRoot, out string path)
                || !WorkspacePathPolicy.IsPathUnderWorkspace(path, resource) || !TryOpenScope(scope.RelativeRoot, out ScopeHandles? openedScope)) continue;

            ScopeHandles handles = openedScope!;

            FileStream? stream = null;

            bool transferred = false;

            try
            {
                Result<FileStream> opened = await OpenReadAsync(grant, workspaceId, relativePath, cancellationToken).ConfigureAwait(false);

                if (opened.IsFailure) return opened.Error;

                stream = opened.Value;

                RetainedRead evidence = new(this, grant, relativePath, resource, handles, stream);

                Result valid = await evidence.ValidateAsync(cancellationToken).ConfigureAwait(false);

                if (valid.IsFailure) return valid.Error;

                transferred = true;

                return evidence;
            }
            finally
            {
                if (!transferred)
                {
                    stream?.Dispose();
                    handles.Dispose();
                }
            }
        }

        return Forbidden;
    }

    internal sealed class RetainedRead : IDisposable
    {
        private readonly GuildWorkspaceAuthority _owner;

        private readonly GuildGrant _grant;

        private readonly string _relativePath;

        private readonly string _resource;

        private readonly ScopeHandles _handles;

        private readonly FileStream _stream;

        private long _contentOffset;

        private long _contentLength;

        private byte[]? _contentDigest;

        internal RetainedRead(GuildWorkspaceAuthority owner, GuildGrant grant, string relativePath, string resource,
            ScopeHandles handles, FileStream stream)
        {
            _owner = owner;
            _grant = grant;
            _relativePath = relativePath;
            _resource = resource;
            _handles = handles;
            _stream = stream;
        }

        internal FileStream Stream => _stream;

        internal bool IsIndexEligible(IReadOnlySet<string> extensions)
        {
            try
            {
                return WorkspaceIndexEligibility.IsEligible(_relativePath,
                    prefix => File.GetAttributes(Path.Combine(_owner.Registration.RootPath, prefix)), extensions);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        internal void RecordContentEvidence(long offset, long length, byte[] digest)
        {
            _contentOffset = offset;
            _contentLength = length;
            _contentDigest = digest;
        }

        internal async Task<Result> ValidateAsync(CancellationToken cancellationToken)
        {
            Result current = await _owner.ValidateAsync(_grant, cancellationToken).ConfigureAwait(false);

            if (current.IsFailure) return current;

            if (!PhysicalMatches()) return Forbidden;

            cancellationToken.ThrowIfCancellationRequested();

            if (_contentDigest is not null)
            {
                try
                {
                    _stream.Position = 0;

                    using StreamReader reader = new(_stream, leaveOpen: true);

                    char[] buffer = new char[4096];

                    long skip = _contentOffset;

                    while (skip > 0)
                    {
                        int read = await reader.ReadBlockAsync(buffer.AsMemory(0, (int)Math.Min(skip, buffer.Length)), cancellationToken).ConfigureAwait(false);

                        if (read == 0) return Forbidden;

                        skip -= read;
                    }

                    using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                    long remaining = _contentLength;

                    while (remaining > 0)
                    {
                        int read = await reader.ReadBlockAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), cancellationToken).ConfigureAwait(false);

                        if (read == 0) return Forbidden;

                        digest.AppendData(MemoryMarshal.AsBytes(buffer.AsSpan(0, read)));

                        remaining -= read;
                    }

                    if (!CryptographicOperations.FixedTimeEquals(digest.GetHashAndReset(), _contentDigest)) return Forbidden;
                }
                catch (IOException)
                {
                    return Forbidden;
                }
            }

            current = await _owner.ValidateAsync(_grant, cancellationToken).ConfigureAwait(false);

            return current.IsFailure ? current : PhysicalMatches() ? Result.Success() : Forbidden;
        }

        private bool PhysicalMatches()
        {
            string? directory = Path.GetDirectoryName(_relativePath.Replace('/', Path.DirectorySeparatorChar));

            return _owner.TryResolveDirectory(directory?.Replace(Path.DirectorySeparatorChar, '/') ?? ".", out _)
                && FileHandleIdentityInterop.TryGetPathMetadataNoFollow(_resource, out FileHandleMetadata metadata)
                && metadata.Kind == FileSystemObjectKind.RegularFile
                && SecureFileReader.TryValidateRegularFileHandle(_stream.SafeFileHandle, metadata.Identity, out _)
                && _owner.Opener.IdentifyOpenedDirectory(_handles.Root) == _owner.Registration.RootIdentity
                && IsHandleContained(_handles.Root, _handles.Granted)
                && IsHandleContained(_handles.Granted, _stream.SafeFileHandle);
        }

        public void Dispose()
        {
            _stream.Dispose();
            _handles.Dispose();
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

    internal sealed class ScopeHandles(SafeFileHandle root, SafeFileHandle granted) : IDisposable
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
