using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

internal sealed class AttachmentSourceResolver(
    IHostWorkspaceContext workspaceContext,
    IWorkspaceRegistry? workspaceRegistry = null)
    : IAttachmentSourceResolver
{
    private const int IoBufferSize = 64 * 1024;

    internal Func<CancellationToken, Task>? AfterFirstRefreshReadForTesting { get; set; }

    internal Func<CancellationToken, Task>? BeforeSourceOpenForTesting { get; set; }

    public async Task<AttachmentSourceResolution> ResolveForPersistenceAsync(
        AttachmentSourceClaim claim,
        ReadOnlyMemory<byte> snapshotBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);

        WorkspaceRootResolution workspace = await ResolveClaimedWorkspaceAsync(
            claim,
            cancellationToken).ConfigureAwait(false);

        if (!workspace.IsSuccess)
        {
            return Snapshot(workspace.FailureStatus, workspace.FailureReason);
        }

        string root = workspace.Root!;

        if (!TryResolveAbsolutePath(claim.AbsolutePath, out string candidate))
        {
            return Snapshot(AttachmentSourceStatus.Unsafe, "The source path is invalid.");
        }

        if (!WorkspacePathPolicy.IsPathUnderWorkspace(root, candidate))
        {
            return Snapshot(AttachmentSourceStatus.Unsafe, "The source is outside the registered workspace.");
        }

        if (!File.Exists(candidate))
        {
            string? parent = Path.GetDirectoryName(candidate);

            if (parent is null
                || !WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(root, parent, out _))
            {
                return Snapshot(AttachmentSourceStatus.Unsafe, "The missing source has an unsafe parent path.");
            }

            return Snapshot(AttachmentSourceStatus.Missing, "The source file is missing.");
        }

        if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                root,
                candidate,
                out string? canonical)
            || canonical is null)
        {
            return Snapshot(AttachmentSourceStatus.Unsafe, "The source is outside the registered workspace.");
        }

        try
        {
            if (BeforeSourceOpenForTesting is not null)
            {
                await BeforeSourceOpenForTesting(cancellationToken).ConfigureAwait(false);
            }

            await using FileStream stream = OpenSource(candidate);

            if (!TryValidateOpenedSource(

                    root,

                    candidate,

                    canonical,

                    stream,

                    out FileHandleIdentity openedIdentity,

                    out string? openedCanonical)

                || openedCanonical is null)
            {
                return Snapshot(
                    AttachmentSourceStatus.Unsafe,
                    "Source identity or containment changed during validation.");
            }

            long observedLength = stream.Length;

            BoundedHashResult first = await HashBoundedAsync(
                stream,
                observedLength,
                cancellationToken).ConfigureAwait(false);

            stream.Position = 0;

            BoundedHashResult stable = await HashBoundedAsync(
                stream,
                observedLength,
                cancellationToken).ConfigureAwait(false);

            if (!TryValidateOpenedSource(

                    root,

                    candidate,

                    openedCanonical,

                    stream,

                    out FileHandleIdentity finalIdentity,

                    out string? finalCanonical)

                || finalCanonical is null

                || !FileHandleIdentity.IdentitiesMatch(openedIdentity, finalIdentity)
                || first.Length != observedLength
                || stable.Length != observedLength
                || !CryptographicOperations.FixedTimeEquals(first.Hash, stable.Hash))
            {
                return Snapshot(
                    AttachmentSourceStatus.Unsafe,
                    "The source changed while its provenance was verified.");
            }

            string hash = Convert.ToHexString(stable.Hash);

            string snapshotHash = Convert.ToHexString(SHA256.HashData(snapshotBytes.Span));

            AttachmentSourceStatus status = hash.Equals(
                snapshotHash,
                StringComparison.OrdinalIgnoreCase)
                    ? AttachmentSourceStatus.Refreshable
                    : AttachmentSourceStatus.PriorVersion;

            FileInfo info = new(candidate);

            string relative = RelativePath(root, candidate);

            AttachmentSourceMetadata metadata = CreateWorkspaceMetadata(
                workspace.Identity!,
                relative,
                finalCanonical,
                hash,
                finalIdentity,
                info.LastWriteTimeUtc,
                stable.Length,
                status);

            ReadOnlyMemory<byte> verified = status == AttachmentSourceStatus.Refreshable
                ? snapshotBytes
                : ReadOnlyMemory<byte>.Empty;

            return new AttachmentSourceResolution(metadata, verified);
        }
        catch (FileNotFoundException)
        {
            return Snapshot(AttachmentSourceStatus.Missing, "The source file is missing.");
        }
        catch (DirectoryNotFoundException)
        {
            return Snapshot(AttachmentSourceStatus.Missing, "The source file is missing.");
        }
        catch (UnauthorizedAccessException)
        {
            return Snapshot(AttachmentSourceStatus.Inaccessible, "The source file is inaccessible.");
        }
        catch (IOException)
        {
            return Snapshot(
                AttachmentSourceStatus.Inaccessible,
                "The source file could not be safely opened or read.");
        }
    }

    public async Task<AttachmentSourceResolution> ResolveForReferenceAsync(
        AttachmentSourceClaim claim,
        long maxBytes,
        AttachmentSourcePathAuthorizer authorizeCanonicalPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);

        ArgumentNullException.ThrowIfNull(authorizeCanonicalPath);

        if (maxBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        WorkspaceRootResolution workspace = await ResolveClaimedWorkspaceAsync(
            claim,
            cancellationToken).ConfigureAwait(false);

        if (!workspace.IsSuccess)
        {
            return Snapshot(workspace.FailureStatus, workspace.FailureReason);
        }

        string root = workspace.Root!;

        if (!TryResolveAbsolutePath(claim.AbsolutePath, out string candidate))
        {
            return Snapshot(AttachmentSourceStatus.Unsafe, "The source path is invalid.");
        }

        if (!WorkspacePathPolicy.IsPathUnderWorkspace(root, candidate))
        {
            return Snapshot(AttachmentSourceStatus.Unsafe, "The source is outside the registered workspace.");
        }

        if (!File.Exists(candidate))
        {
            string? parent = Path.GetDirectoryName(candidate);

            if (parent is null
                || !WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(root, parent, out _))
            {
                return Snapshot(AttachmentSourceStatus.Unsafe, "The missing source has an unsafe parent path.");
            }

            return Snapshot(AttachmentSourceStatus.Missing, "The source file is missing.");
        }

        if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                root,
                candidate,
                out string? canonical)
            || canonical is null)
        {
            return Snapshot(AttachmentSourceStatus.Unsafe, "The source is outside the registered workspace.");
        }

        try
        {
            if (BeforeSourceOpenForTesting is not null)
            {
                await BeforeSourceOpenForTesting(cancellationToken).ConfigureAwait(false);
            }

            await using FileStream stream = OpenSource(candidate);

            if (!TryValidateOpenedSource(

                    root,

                    candidate,

                    canonical,

                    stream,

                    out FileHandleIdentity openedIdentity,

                    out string? openedCanonical)

                || openedCanonical is null)
            {
                return Snapshot(
                    AttachmentSourceStatus.Unsafe,
                    "Source identity or containment changed during validation.");
            }

            if (!await authorizeCanonicalPath(openedCanonical, cancellationToken).ConfigureAwait(false))
            {
                return Snapshot(
                    AttachmentSourceStatus.Inaccessible,
                    "Sanctum denied access to the attachment source.");
            }

            if (stream.Length > maxBytes)
            {
                return Snapshot(
                    AttachmentSourceStatus.Inaccessible,
                    $"The source exceeds the reference size limit of {maxBytes} bytes.");
            }

            byte[] first = await ReadBoundedAsync(
                stream,
                maxBytes,
                cancellationToken).ConfigureAwait(false);

            if (AfterFirstRefreshReadForTesting is not null)
            {
                await AfterFirstRefreshReadForTesting(cancellationToken).ConfigureAwait(false);
            }

            if (!TryValidateOpenedSource(

                    root,

                    candidate,

                    openedCanonical,

                    stream,

                    out FileHandleIdentity afterFirstIdentity,

                    out _)

                || !FileHandleIdentity.IdentitiesMatch(openedIdentity, afterFirstIdentity))
            {
                return Snapshot(
                    AttachmentSourceStatus.Unsafe,
                    "Source identity or containment changed while it was read.");
            }

            stream.Position = 0;

            byte[] stable = await ReadBoundedAsync(
                stream,
                maxBytes,
                cancellationToken).ConfigureAwait(false);

            if (!TryValidateOpenedSource(

                    root,

                    candidate,

                    openedCanonical,

                    stream,

                    out FileHandleIdentity finalIdentity,

                    out string? finalCanonical)

                || finalCanonical is null

                || !FileHandleIdentity.IdentitiesMatch(openedIdentity, finalIdentity))
            {
                return Snapshot(
                    AttachmentSourceStatus.Unsafe,
                    "Source identity or containment changed while it was read.");
            }

            byte[] firstHash = SHA256.HashData(first);

            byte[] stableHash = SHA256.HashData(stable);

            if (first.LongLength != stable.LongLength
                || !CryptographicOperations.FixedTimeEquals(firstHash, stableHash))
            {
                return Snapshot(
                    AttachmentSourceStatus.Unsafe,
                    "The source changed while it was read.");
            }

            string relative = RelativePath(root, candidate);

            string hash = Convert.ToHexString(stableHash);

            FileInfo info = new(candidate);

            AttachmentSourceMetadata metadata = CreateWorkspaceMetadata(
                workspace.Identity!,
                relative,
                finalCanonical,
                hash,
                finalIdentity,
                info.LastWriteTimeUtc,
                stable.LongLength,
                AttachmentSourceStatus.Refreshable);

            return new AttachmentSourceResolution(
                metadata,
                stable,
                AttachmentMimeDetector.Detect(stable, relative));
        }
        catch (FileNotFoundException)
        {
            return Snapshot(AttachmentSourceStatus.Missing, "The source file is missing.");
        }
        catch (DirectoryNotFoundException)
        {
            return Snapshot(AttachmentSourceStatus.Missing, "The source file is missing.");
        }
        catch (UnauthorizedAccessException)
        {
            return Snapshot(AttachmentSourceStatus.Inaccessible, "The source file is inaccessible.");
        }
        catch (IOException)
        {
            return Snapshot(
                AttachmentSourceStatus.Inaccessible,
                "The source file could not be safely opened or read within the size limit.");
        }
    }

    public async Task<AttachmentSourceMetadata> RevalidateAsync(
        AttachmentSourceMetadata source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Kind != AttachmentSourceKind.WorkspaceFile)
        {
            return AttachmentSourceMetadata.SnapshotOnly;
        }

        if (string.IsNullOrWhiteSpace(source.WorkspaceIdentity)
            || string.IsNullOrWhiteSpace(source.WorkspaceRelativePath))
        {
            return SourceFailure(
                source,
                AttachmentSourceStatus.CorruptMetadata,
                "The attachment source metadata is incomplete.");
        }

        WorkspaceRootResolution workspace = await ResolvePersistedWorkspaceAsync(
            source.WorkspaceIdentity,
            cancellationToken).ConfigureAwait(false);

        if (!workspace.IsSuccess)
        {
            return SourceFailure(source, workspace.FailureStatus, workspace.FailureReason);
        }

        if (string.IsNullOrWhiteSpace(source.LastObservedContentSha256)
            || source.LastObservedByteLength is not { } observedLength
            || observedLength < 0)
        {
            return SourceFailure(
                source,
                AttachmentSourceStatus.CorruptMetadata,
                "The attachment source metadata is incomplete.");
        }

        string root = workspace.Root!;

        if (!TryResolveStoredCandidate(root, source.WorkspaceRelativePath, out string candidate))
        {
            return SourceFailure(
                source,
                AttachmentSourceStatus.Unsafe,
                "The stored source path escapes the registered workspace.");
        }

        if (!File.Exists(candidate))
        {
            return SourceFailure(source, AttachmentSourceStatus.Missing, "The source file is missing.");
        }

        if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                root,
                candidate,
                out string? canonical)
            || canonical is null)
        {
            return SourceFailure(
                source,
                AttachmentSourceStatus.Unsafe,
                "The source is outside the registered workspace after link resolution.");
        }

        if (!string.IsNullOrWhiteSpace(source.LastKnownCanonicalPath)
            && !PathEquals(source.LastKnownCanonicalPath, canonical))
        {
            return SourceFailure(
                source,
                AttachmentSourceStatus.Unsafe,
                "The source link target changed since provenance was verified.");
        }

        try
        {
            if (BeforeSourceOpenForTesting is not null)

            {

                await BeforeSourceOpenForTesting(cancellationToken).ConfigureAwait(false);

            }

            await using FileStream stream = OpenSource(candidate);

            if (!TryValidateOpenedSource(

                    root,

                    candidate,

                    canonical,

                    stream,

                    out FileHandleIdentity openedIdentity,

                    out string? openedCanonical)

                || openedCanonical is null)
            {
                return SourceFailure(
                    source,
                    AttachmentSourceStatus.Unsafe,
                    "Source identity or containment changed during validation.");
            }

            if (stream.Length > observedLength)
            {
                return SourceFailure(
                    source,
                    AttachmentSourceStatus.PriorVersion,
                    "The persisted snapshot represents a prior source version.");
            }

            BoundedHashResult first = await HashBoundedAsync(
                stream,
                observedLength,
                cancellationToken).ConfigureAwait(false);

            stream.Position = 0;

            BoundedHashResult stable = await HashBoundedAsync(
                stream,
                observedLength,
                cancellationToken).ConfigureAwait(false);

            if (!TryValidateOpenedSource(

                    root,

                    candidate,

                    openedCanonical,

                    stream,

                    out FileHandleIdentity finalIdentity,

                    out string? finalCanonical)

                || finalCanonical is null

                || !FileHandleIdentity.IdentitiesMatch(openedIdentity, finalIdentity)
                || first.Length != stable.Length
                || !CryptographicOperations.FixedTimeEquals(first.Hash, stable.Hash))
            {
                return SourceFailure(
                    source,
                    AttachmentSourceStatus.Unsafe,
                    "The source changed while it was revalidated.");
            }

            string hash = Convert.ToHexString(stable.Hash);

            bool sameObservedContent = hash.Equals(
                source.LastObservedContentSha256,
                StringComparison.OrdinalIgnoreCase);

            AttachmentSourceStatus status = sameObservedContent
                && source.Status != AttachmentSourceStatus.PriorVersion
                    ? AttachmentSourceStatus.Refreshable
                    : AttachmentSourceStatus.PriorVersion;

            FileInfo info = new(candidate);

            return source with
            {
                LastKnownCanonicalPath = finalCanonical,
                LastObservedContentSha256 = hash,
                LastObservedFileIdentity = FileIdentity(finalIdentity),
                LastObservedWriteTime = info.LastWriteTimeUtc,
                LastObservedByteLength = stable.Length,
                Status = status,
                DiagnosticReason = status == AttachmentSourceStatus.PriorVersion
                    ? "The persisted snapshot represents a prior source version."
                    : null,
            };
        }
        catch (FileNotFoundException)
        {
            return SourceFailure(source, AttachmentSourceStatus.Missing, "The source file is missing.");
        }
        catch (DirectoryNotFoundException)
        {
            return SourceFailure(source, AttachmentSourceStatus.Missing, "The source file is missing.");
        }
        catch (UnauthorizedAccessException)
        {
            return SourceFailure(source, AttachmentSourceStatus.Inaccessible, "The source file is inaccessible.");
        }
        catch (IOException)
        {
            return SourceFailure(
                source,
                AttachmentSourceStatus.Inaccessible,
                "The source file could not be safely revalidated.");
        }
    }

    public async Task<AttachmentSourceResolution> ResolveCurrentAsync(
        AttachmentSourceMetadata source,
        string expectedSnapshotSha256,
        long maxBytes,
        AttachmentSourcePathAuthorizer authorizeCanonicalPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSnapshotSha256);

        ArgumentNullException.ThrowIfNull(authorizeCanonicalPath);

        if (maxBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        if (source.Kind != AttachmentSourceKind.WorkspaceFile
            || string.IsNullOrWhiteSpace(source.WorkspaceIdentity)
            || string.IsNullOrWhiteSpace(source.WorkspaceRelativePath))
        {
            return Fail(
                source,
                AttachmentSourceStatus.CorruptMetadata,
                "The attachment does not carry verified refreshable workspace source metadata.");
        }

        WorkspaceRootResolution workspace = await ResolvePersistedWorkspaceAsync(
            source.WorkspaceIdentity,
            cancellationToken).ConfigureAwait(false);

        if (!workspace.IsSuccess)
        {
            return Fail(source, workspace.FailureStatus, workspace.FailureReason);
        }

        string root = workspace.Root!;

        if (!TryResolveStoredCandidate(root, source.WorkspaceRelativePath, out string candidate))
        {
            return Fail(
                source,
                AttachmentSourceStatus.Unsafe,
                "The stored source path escapes the registered workspace.");
        }

        if (!File.Exists(candidate))
        {
            return Fail(source, AttachmentSourceStatus.Missing, "The source file is missing.");
        }

        if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                root,
                candidate,
                out string? canonical)
            || canonical is null)
        {
            return Fail(
                source,
                AttachmentSourceStatus.Unsafe,
                "The source is outside the registered workspace after link resolution.");
        }

        if (!string.IsNullOrWhiteSpace(source.LastKnownCanonicalPath)
            && !PathEquals(source.LastKnownCanonicalPath, canonical))
        {
            return Fail(
                source,
                AttachmentSourceStatus.Unsafe,
                "The source link target changed since provenance was verified.");
        }

        try
        {
            if (BeforeSourceOpenForTesting is not null)

            {

                await BeforeSourceOpenForTesting(cancellationToken).ConfigureAwait(false);

            }

            await using FileStream stream = OpenSource(candidate);

            if (!TryValidateOpenedSource(

                    root,

                    candidate,

                    canonical,

                    stream,

                    out FileHandleIdentity openedIdentity,

                    out string? openedCanonical)

                || openedCanonical is null)
            {
                return Fail(
                    source,
                    AttachmentSourceStatus.Unsafe,
                    "Source identity or containment changed during validation.");
            }

            if (!await authorizeCanonicalPath(openedCanonical, cancellationToken).ConfigureAwait(false))
            {
                return Fail(
                    source,
                    AttachmentSourceStatus.Inaccessible,
                    "Sanctum denied access to the attachment source.");
            }

            if (stream.Length > maxBytes)
            {
                return Fail(
                    source,
                    AttachmentSourceStatus.Inaccessible,
                    $"The source exceeds the refresh size limit of {maxBytes} bytes.");
            }

            byte[] first = await ReadBoundedAsync(
                stream,
                maxBytes,
                cancellationToken).ConfigureAwait(false);

            if (AfterFirstRefreshReadForTesting is not null)
            {
                await AfterFirstRefreshReadForTesting(cancellationToken).ConfigureAwait(false);
            }

            if (!TryValidateOpenedSource(

                    root,

                    candidate,

                    openedCanonical,

                    stream,

                    out FileHandleIdentity afterFirstIdentity,

                    out _)

                || !FileHandleIdentity.IdentitiesMatch(openedIdentity, afterFirstIdentity))
            {
                return Fail(
                    source,
                    AttachmentSourceStatus.Unsafe,
                    "Source identity or containment changed while it was read.");
            }

            stream.Position = 0;

            byte[] stable = await ReadBoundedAsync(
                stream,
                maxBytes,
                cancellationToken).ConfigureAwait(false);

            if (!TryValidateOpenedSource(

                    root,

                    candidate,

                    openedCanonical,

                    stream,

                    out FileHandleIdentity finalIdentity,

                    out string? finalCanonical)

                || finalCanonical is null

                || !FileHandleIdentity.IdentitiesMatch(openedIdentity, finalIdentity))
            {
                return Fail(
                    source,
                    AttachmentSourceStatus.Unsafe,
                    "Source identity or containment changed while it was read.");
            }

            byte[] firstHash = SHA256.HashData(first);

            byte[] stableHash = SHA256.HashData(stable);

            if (first.LongLength != stable.LongLength
                || !CryptographicOperations.FixedTimeEquals(firstHash, stableHash))
            {
                return Fail(
                    source,
                    AttachmentSourceStatus.Unsafe,
                    "The source changed while it was read.");
            }

            string hash = Convert.ToHexString(stableHash);

            FileInfo info = new(candidate);

            AttachmentSourceStatus status = hash.Equals(
                expectedSnapshotSha256,
                StringComparison.OrdinalIgnoreCase)
                    ? AttachmentSourceStatus.Refreshable
                    : AttachmentSourceStatus.PriorVersion;

            AttachmentSourceMetadata current = source with
            {
                LastKnownCanonicalPath = finalCanonical,
                LastObservedContentSha256 = hash,
                LastObservedFileIdentity = FileIdentity(finalIdentity),
                LastObservedWriteTime = info.LastWriteTimeUtc,
                LastObservedByteLength = stable.LongLength,
                Status = status,
                DiagnosticReason = status == AttachmentSourceStatus.PriorVersion
                    ? "The persisted snapshot represents a prior source version."
                    : null,
            };

            return new AttachmentSourceResolution(
                current,
                stable,
                AttachmentMimeDetector.Detect(stable, source.WorkspaceRelativePath));
        }
        catch (FileNotFoundException)
        {
            return Fail(source, AttachmentSourceStatus.Missing, "The source file is missing.");
        }
        catch (DirectoryNotFoundException)
        {
            return Fail(source, AttachmentSourceStatus.Missing, "The source file is missing.");
        }
        catch (UnauthorizedAccessException)
        {
            return Fail(source, AttachmentSourceStatus.Inaccessible, "The source file is inaccessible.");
        }
        catch (IOException)
        {
            return Fail(
                source,
                AttachmentSourceStatus.Inaccessible,
                "The source file could not be safely opened or read within the size limit.");
        }
    }

    private async Task<WorkspaceRootResolution> ResolveClaimedWorkspaceAsync(
        AttachmentSourceClaim claim,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(claim.WorkspaceRoot))
        {
            string? activeRoot = NormalizeExistingWorkspaceRoot(workspaceContext.WorkspacePath);

            if (activeRoot is null)
            {
                return WorkspaceRootResolution.Failure(
                    AttachmentSourceStatus.WorkspaceUnavailable,
                    "No active registered workspace is available.");
            }

            WorkspaceInfo? registered = await FindRegisteredWorkspaceByPathAsync(
                activeRoot,
                cancellationToken).ConfigureAwait(false);

            return WorkspaceRootResolution.Success(
                activeRoot,
                registered?.Id ?? WorkspaceIdentity(activeRoot));
        }

        string? claimedRoot = NormalizeExistingWorkspaceRoot(claim.WorkspaceRoot);

        if (claimedRoot is null)
        {
            return WorkspaceRootResolution.Failure(
                AttachmentSourceStatus.WorkspaceUnavailable,
                "The claimed registered workspace is unavailable.");
        }

        if (workspaceRegistry is null)
        {
            return WorkspaceRootResolution.Success(claimedRoot, WorkspaceIdentity(claimedRoot));
        }

        WorkspaceInfo? matched = await FindRegisteredWorkspaceByPathAsync(
            claimedRoot,
            cancellationToken).ConfigureAwait(false);

        if (matched is null)
        {
            return WorkspaceRootResolution.Failure(
                AttachmentSourceStatus.Unsafe,
                "The claimed workspace root is not registered.");
        }

        return WorkspaceRootResolution.Success(claimedRoot, matched.Id);
    }

    private async Task<WorkspaceRootResolution> ResolvePersistedWorkspaceAsync(
        string workspaceIdentity,
        CancellationToken cancellationToken)
    {
        if (workspaceRegistry is not null)
        {
            WorkspaceInfo? identified = await workspaceRegistry
                .GetAsync(workspaceIdentity, cancellationToken)
                .ConfigureAwait(false);

            if (identified is not null)
            {
                string? identifiedRoot = NormalizeExistingWorkspaceRoot(identified.Path);

                return identifiedRoot is null
                    ? WorkspaceRootResolution.Failure(
                        AttachmentSourceStatus.WorkspaceUnavailable,
                        "The registered workspace for this attachment is unavailable.")
                    : WorkspaceRootResolution.Success(identifiedRoot, identified.Id);
            }
        }

        bool foundAvailableWorkspace = false;

        string? activeRoot = NormalizeExistingWorkspaceRoot(workspaceContext.WorkspacePath);

        if (activeRoot is not null)
        {
            foundAvailableWorkspace = true;

            if (string.Equals(
                    workspaceIdentity,
                    WorkspaceIdentity(activeRoot),
                    StringComparison.Ordinal))
            {
                return WorkspaceRootResolution.Success(activeRoot, workspaceIdentity);
            }
        }

        if (workspaceRegistry is not null)
        {
            WorkspaceInfo[] registered = await workspaceRegistry
                .GetAllAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (WorkspaceInfo workspace in registered)
            {
                string? root = NormalizeExistingWorkspaceRoot(workspace.Path);

                if (root is null)
                {
                    continue;
                }

                foundAvailableWorkspace = true;

                if (string.Equals(
                        workspaceIdentity,
                        WorkspaceIdentity(root),
                        StringComparison.Ordinal))
                {
                    return WorkspaceRootResolution.Success(root, workspaceIdentity);
                }
            }
        }

        return foundAvailableWorkspace
            ? WorkspaceRootResolution.Failure(
                AttachmentSourceStatus.WorkspaceChanged,
                "The registered workspace identity changed.")
            : WorkspaceRootResolution.Failure(
                AttachmentSourceStatus.WorkspaceUnavailable,
                "No registered workspace is available.");
    }

    private async Task<WorkspaceInfo?> FindRegisteredWorkspaceByPathAsync(
        string root,
        CancellationToken cancellationToken)
    {
        if (workspaceRegistry is null)
        {
            return null;
        }

        WorkspaceInfo[] registered = await workspaceRegistry
            .GetAllAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (WorkspaceInfo workspace in registered)
        {
            string? registeredRoot = NormalizeWorkspaceRoot(workspace.Path);

            if (registeredRoot is not null && PathEquals(root, registeredRoot))
            {
                return workspace;
            }
        }

        return null;
    }

    private static FileStream OpenSource(string candidate) =>
        new(
            candidate,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: IoBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static bool TryValidateOpenedSource(
        string root,
        string candidate,
        string expectedCanonicalPath,
        FileStream stream,
        out FileHandleIdentity identity,
        out string? currentCanonicalPath)
    {
        identity = default;

        currentCanonicalPath = null;

        return FileHandleIdentityInterop.TryGetPathIdentity(candidate, out FileHandleIdentity expected)
            && FileHandleIdentityInterop.TryGetHandleIdentity(stream.SafeFileHandle, out identity)
            && FileHandleIdentity.IdentitiesMatch(expected, identity)
            && WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(

                root,

                candidate,

                out currentCanonicalPath)

            && currentCanonicalPath is not null

            && PathEquals(expectedCanonicalPath, currentCanonicalPath);
    }

    private static bool TryResolveAbsolutePath(
        string path,
        out string candidate)
    {
        try
        {
            candidate = Path.GetFullPath(path);

            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            candidate = string.Empty;

            return false;
        }
    }

    private static bool TryResolveStoredCandidate(
        string root,
        string relativePath,
        out string candidate)
    {
        try
        {
            candidate = Path.GetFullPath(Path.Combine(root, relativePath));

            return WorkspacePathPolicy.IsPathUnderWorkspace(root, candidate);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            candidate = string.Empty;

            return false;
        }
    }

    private static string? NormalizeWorkspaceRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Trim()));
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return null;
        }
    }

    private static string? NormalizeExistingWorkspaceRoot(string? root)
    {
        string? normalized = NormalizeWorkspaceRoot(root);

        return normalized is not null && Directory.Exists(normalized)
            ? normalized
            : null;
    }

    private static AttachmentSourceMetadata CreateWorkspaceMetadata(
        string workspaceIdentity,
        string relativePath,
        string canonicalPath,
        string contentHash,
        FileHandleIdentity fileIdentity,
        DateTimeOffset lastWriteTime,
        long byteLength,
        AttachmentSourceStatus status) =>
        new(
            AttachmentSourceKind.WorkspaceFile,
            workspaceIdentity,
            relativePath,
            canonicalPath,
            contentHash,
            FileIdentity(fileIdentity),
            lastWriteTime,
            byteLength,
            status,
            status == AttachmentSourceStatus.PriorVersion
                ? "The persisted snapshot represents a prior source version."
                : null);

    private static string WorkspaceIdentity(string root) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)));

    private static string RelativePath(string root, string candidate) =>
        Path.GetRelativePath(root, candidate).Replace('\\', '/');

    private static string FileIdentity(FileHandleIdentity identity) =>
        $"{identity.VolumeId:X16}:{identity.FileId:X16}";

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        using MemoryStream output = new();

        byte[] buffer = new byte[IoBufferSize];

        long total = 0;

        while (true)
        {
            int requestBytes = NextReadSize(maxBytes, total, buffer.Length);

            int read = await stream
                .ReadAsync(buffer.AsMemory(0, requestBytes), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            if (read > maxBytes - total)
            {
                throw new IOException("The source grew beyond the configured size limit while being read.");
            }

            total += read;

            await output
                .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }

        return output.ToArray();
    }

    private static async Task<BoundedHashResult> HashBoundedAsync(
        Stream stream,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        byte[] buffer = new byte[IoBufferSize];

        long total = 0;

        while (true)
        {
            int requestBytes = NextReadSize(maxBytes, total, buffer.Length);

            int read = await stream
                .ReadAsync(buffer.AsMemory(0, requestBytes), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            if (read > maxBytes - total)
            {
                throw new IOException("The source grew beyond the verified size bound while being hashed.");
            }

            total += read;

            hash.AppendData(buffer, 0, read);
        }

        return new BoundedHashResult(hash.GetHashAndReset(), total);
    }

    private static int NextReadSize(long maxBytes, long total, int bufferLength)
    {
        long remaining = maxBytes - total;

        return remaining >= bufferLength
            ? bufferLength
            : checked((int)remaining + 1);
    }

    private static bool PathEquals(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return false;
        }
    }

    private static AttachmentSourceMetadata SourceFailure(
        AttachmentSourceMetadata source,
        AttachmentSourceStatus status,
        string reason) =>
        source with
        {
            Status = status,
            DiagnosticReason = reason,
        };

    private static AttachmentSourceResolution Fail(
        AttachmentSourceMetadata source,
        AttachmentSourceStatus status,
        string reason) =>
        new(
            SourceFailure(source, status, reason),
            ReadOnlyMemory<byte>.Empty);

    private static AttachmentSourceResolution Snapshot(
        AttachmentSourceStatus status,
        string reason) =>
        new(
            AttachmentSourceMetadata.SnapshotOnly with
            {
                Status = status,
                DiagnosticReason = reason,
            },
            ReadOnlyMemory<byte>.Empty);

    private sealed record WorkspaceRootResolution(
        string? Root,
        string? Identity,
        AttachmentSourceStatus FailureStatus,
        string FailureReason)
    {
        internal bool IsSuccess => Root is not null && Identity is not null;

        internal static WorkspaceRootResolution Success(string root, string identity) =>
            new(root, identity, AttachmentSourceStatus.NotApplicable, string.Empty);

        internal static WorkspaceRootResolution Failure(
            AttachmentSourceStatus status,
            string reason) =>
            new(null, null, status, reason);
    }

    private readonly record struct BoundedHashResult(
        byte[] Hash,
        long Length);
}
