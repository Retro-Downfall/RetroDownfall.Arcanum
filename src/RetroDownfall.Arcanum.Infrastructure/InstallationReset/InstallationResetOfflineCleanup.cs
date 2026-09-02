using System.Buffers.Binary;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

internal sealed record InstallationResetFileSystemInventory(
    InstallationResetTargetDescriptor[] Targets,
    InstallationResetPreservedBackup[] PreservedBackups,
    InstallationResetExclusion[] Exclusions,
    long Files,
    long EstimatedBytes);

internal sealed class InstallationResetOfflineCleanup : IInstallationResetOfflineCleanup
{

    private static ReadOnlySpan<byte> BackupMagic => "ARCABACK"u8;

    private readonly Action? _afterInitialCapture;

    private readonly Action<string>? _afterFileDeleted;

    private readonly Action<string>? _afterDirectoryDeleted;

    public InstallationResetOfflineCleanup()
    {

    }

    internal InstallationResetOfflineCleanup(Action afterInitialCapture)
        : this(afterInitialCapture, afterFileDeleted: null)
    {

    }

    internal InstallationResetOfflineCleanup(
        Action? afterInitialCapture,
        Action<string>? afterFileDeleted,
        Action<string>? afterDirectoryDeleted = null)
    {

        _afterInitialCapture = afterInitialCapture;

        _afterFileDeleted = afterFileDeleted;

        _afterDirectoryDeleted = afterDirectoryDeleted;

    }

    public Task<Result<InstallationResetFileSystemInventory>> PlanAsync(
        string[] selectedRoots,
        string[] excludedRoots,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(selectedRoots);

        ArgumentNullException.ThrowIfNull(excludedRoots);

        List<InstallationResetPreservedBackup> backups = [];

        List<CleanupFile> files = [];

        List<IdentityOwnedFileSystemArtifact> directories = [];

        try
        {

            HashSet<string> exclusions = new(
                excludedRoots.Select(Path.GetFullPath),
                PathComparer);

            foreach (string selectedRoot in selectedRoots
                         .Select(Path.GetFullPath)
                         .Distinct(PathComparer)
                         .Order(PathComparer))
            {

                cancellationToken.ThrowIfCancellationRequested();

                if (HasSymlinkedAncestor(selectedRoot))
                {

                    return Task.FromResult(
                        Result<InstallationResetFileSystemInventory>.Failure(new Error(
                            ErrorCodes.Data.InventoryUnavailable,
                            "A selected reset root contains a symlinked ancestor.")));

                }

                if (exclusions.Contains(selectedRoot))
                {

                    continue;

                }

                Result captured = CaptureSelectedRoot(
                    selectedRoot,
                    files,
                    directories,
                    backups,
                    acceptedBackups: null,
                    encounteredBackups: null,
                    exclusions,
                    cancellationToken);

                if (captured.IsFailure)
                {

                    return Task.FromResult(
                        Result<InstallationResetFileSystemInventory>.Failure(captured.Error));

                }

            }

            InstallationResetTargetDescriptor[] targets =
            [
                .. files
                    .OrderBy(static file => file.Artifact.Path, PathComparer)
                    .Select(static file => new InstallationResetTargetDescriptor(
                        Category: "installation-file",
                        Role: InstallationResetTargetRole.FileSystem,
                        ResourceId: file.Artifact.Path,
                        CanonicalPath: file.Artifact.Path,
                        DatabasePredicate: null,
                        Identity: ToIdentity(file.Artifact.Metadata, file.Length),
                        Rows: null,
                        Files: 1,
                        EstimatedBytes: file.Length)),
            ];

            InstallationResetExclusion[] reportedExclusions =
            [
                .. exclusions
                    .Order(PathComparer)
                    .Select(static path => new InstallationResetExclusion(
                        "nested-campaign",
                        path,
                        "A more-specific registered Campaign owns this root.")),
            ];

            long bytes = files.Aggregate(
                0L,
                static (total, file) => checked(total + file.Length));

            return Task.FromResult(Result<InstallationResetFileSystemInventory>.Success(
                new InstallationResetFileSystemInventory(
                    targets,
                    [.. backups.OrderBy(static backup => backup.CanonicalPath, PathComparer)],
                    reportedExclusions,
                    files.Count,
                    bytes)));

        }
        catch (OperationCanceledException)
        {

            throw;

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException
                or OverflowException)
        {

            return Task.FromResult(Result<InstallationResetFileSystemInventory>.Failure(
                new Error(
                    ErrorCodes.Data.InventoryUnavailable,
                    "The selected reset filesystem inventory is unavailable.")));

        }

    }

    public Task<Result<InstallationResetOfflineCleanupResult>> ExecuteAsync(
        InstallationResetPlan plan,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(plan);

        long filesDeleted = 0;

        long bytesDeleted = 0;

        // Counts every successful destructive delete, file or directory, so the cancellation catch
        // below can tell "nothing was touched" apart from "a directory was removed" — filesDeleted
        // alone only tracks the file loop and reads a directory-only pass as untouched (W5-7).
        long mutationCount = 0;

        List<InstallationResetPreservedBackup> backups = [];

        List<CleanupFile> files = [];

        List<IdentityOwnedFileSystemArtifact> directories = [];

        try
        {

            Result<Dictionary<string, InstallationResetPreservedBackup>> acceptedResult =
                CreateAcceptedBackupCatalog(plan.AcceptedBinding.PreservedBackups);

            if (acceptedResult.IsFailure)
            {

                return Task.FromResult(Failure(
                    acceptedResult.Error.Code,
                    acceptedResult.Error.Message));

            }

            Dictionary<string, InstallationResetPreservedBackup> acceptedBackups =
                acceptedResult.Value;

            HashSet<string> encounteredBackups = new(PathComparer);

            HashSet<string> excludedRoots = new(
                plan.AcceptedBinding.ExcludedRoots.Select(Path.GetFullPath),
                PathComparer);

            foreach (string selectedRoot in plan.AcceptedBinding.SelectedRoots)
            {

                cancellationToken.ThrowIfCancellationRequested();

                string root = Path.GetFullPath(selectedRoot);

                if (HasSymlinkedAncestor(root))
                {

                    return Task.FromResult(Failure(
                        ErrorCodes.Data.PlanChanged,
                        "A selected reset root contains a symlinked ancestor."));

                }

                if (excludedRoots.Contains(root))
                {

                    continue;

                }

                Result capture = CaptureSelectedRoot(
                    root,
                    files,
                    directories,
                    backups,
                    acceptedBackups,
                    encounteredBackups,
                    excludedRoots,
                    cancellationToken);

                if (capture.IsFailure)
                {

                    return Task.FromResult(Failure(
                        capture.Error.Code,
                        capture.Error.Message));

                }

            }

            string? missingBackup = acceptedBackups.Keys
                .FirstOrDefault(path => !encounteredBackups.Contains(path));

            if (missingBackup is not null)
            {

                return Task.FromResult(Failure(
                    ErrorCodes.Data.PlanChanged,
                    "An accepted backup is missing or no longer a valid archive."));

            }

            _afterInitialCapture?.Invoke();

            Result destructiveBoundaryVerification = VerifyAcceptedBackups(
                acceptedBackups);

            if (destructiveBoundaryVerification.IsFailure)
            {

                return Task.FromResult(Failure(
                    destructiveBoundaryVerification.Error.Code,
                    destructiveBoundaryVerification.Error.Message));

            }

            foreach (CleanupFile file in files
                         .OrderBy(static candidate => candidate.Artifact.Path, StringComparer.Ordinal))
            {

                cancellationToken.ThrowIfCancellationRequested();

                if (!IdentityOwnedFileSystemCleanup.TryDelete(file.Artifact))
                {

                    return Task.FromResult(FailureOrIncomplete(
                        filesDeleted,
                        bytesDeleted,
                        backups,
                        ErrorCodes.Data.RecoveryRequired,
                        "A selected reset file could not be deleted safely."));

                }

                filesDeleted++;

                mutationCount++;

                bytesDeleted += file.Length;

                _afterFileDeleted?.Invoke(file.Artifact.Path);

            }

            foreach (IdentityOwnedFileSystemArtifact directory in directories
                         .OrderByDescending(static artifact => artifact.Path.Length)
                         .ThenBy(static artifact => artifact.Path, StringComparer.Ordinal))
            {

                cancellationToken.ThrowIfCancellationRequested();

                if (!FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                        directory.Path,
                        out FileHandleMetadata current))
                {

                    if (!Directory.Exists(directory.Path)
                        && !File.Exists(directory.Path))
                    {

                        continue;

                    }

                    return Task.FromResult(FailureOrIncomplete(
                        filesDeleted,
                        bytesDeleted,
                        backups,
                        ErrorCodes.Data.RecoveryRequired,
                        "A selected reset directory could not be inspected safely."));

                }

                if (!MatchesDirectoryIdentity(current, directory.Metadata))
                {

                    return Task.FromResult(FailureOrIncomplete(
                        filesDeleted,
                        bytesDeleted,
                        backups,
                        ErrorCodes.Data.RecoveryRequired,
                        "A selected reset directory changed identity before deletion."));

                }

                if (Directory.EnumerateFileSystemEntries(
                        directory.Path,
                        "*",
                        SelectedEntryEnumeration)
                    .Any())
                {

                    continue;

                }

                if (!IdentityOwnedFileSystemCleanup.TryDelete(directory))
                {

                    return Task.FromResult(FailureOrIncomplete(
                        filesDeleted,
                        bytesDeleted,
                        backups,
                        ErrorCodes.Data.RecoveryRequired,
                        "An empty selected reset directory could not be deleted safely."));

                }

                mutationCount++;

                _afterDirectoryDeleted?.Invoke(directory.Path);

            }

            Result backupVerification = VerifyAcceptedBackups(acceptedBackups);

            if (backupVerification.IsFailure)
            {

                return Task.FromResult(FailureOrIncomplete(
                    filesDeleted,
                    bytesDeleted,
                    backups,
                    backupVerification.Error.Code,
                    backupVerification.Error.Message));

            }

            Result<CleanupFile[]> remainingFiles = CaptureRemainingFiles(
                plan.AcceptedBinding.SelectedRoots,
                excludedRoots,
                acceptedBackups,
                cancellationToken);

            if (remainingFiles.IsFailure)
            {

                return Task.FromResult(FailureOrIncomplete(
                    filesDeleted,
                    bytesDeleted,
                    backups,
                    remainingFiles.Error.Code,
                    remainingFiles.Error.Message));

            }

            if (remainingFiles.Value.Length > 0)
            {

                InstallationResetIssueSummary[] issues =
                [
                    .. remainingFiles.Value.Select(static file =>
                        new InstallationResetIssueSummary(
                            ErrorCodes.Data.ReconciliationFailed,
                            "A selected reset file remains after offline cleanup.",
                            file.Artifact.Path)),
                ];

                return Task.FromResult(Incomplete(
                    filesDeleted,
                    bytesDeleted,
                    backups,
                    issues));

            }

            InstallationResetVerification verification = new(
                true,
                []);

            return Task.FromResult(Result<InstallationResetOfflineCleanupResult>.Success(
                new InstallationResetOfflineCleanupResult(
                    filesDeleted,
                    bytesDeleted,
                    CredentialResults: [],
                    [.. backups.OrderBy(static backup => backup.CanonicalPath, StringComparer.Ordinal)],
                    verification)));

        }
        catch (OperationCanceledException) when (mutationCount > 0)
        {

            return Task.FromResult(Incomplete(
                filesDeleted,
                bytesDeleted,
                backups,
                [
                    new InstallationResetIssueSummary(
                        ErrorCodes.Data.RecoveryRequired,
                        "Installation reset cleanup was cancelled after filesystem mutation."),
                ]));

        }
        catch (OperationCanceledException)
        {

            throw;

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException)
        {

            return Task.FromResult(FailureOrIncomplete(
                filesDeleted,
                bytesDeleted,
                backups,
                ErrorCodes.Data.RecoveryRequired,
                "The selected reset filesystem inventory changed or became unavailable."));

        }

    }

    private static Result<CleanupFile[]> CaptureRemainingFiles(
        string[] selectedRoots,
        HashSet<string> excludedRoots,
        IReadOnlyDictionary<string, InstallationResetPreservedBackup> acceptedBackups,
        CancellationToken cancellationToken)
    {

        List<CleanupFile> remainingFiles = [];

        List<IdentityOwnedFileSystemArtifact> remainingDirectories = [];

        List<InstallationResetPreservedBackup> remainingBackups = [];

        HashSet<string> encounteredBackups = new(PathComparer);

        foreach (string selectedRoot in selectedRoots)
        {

            cancellationToken.ThrowIfCancellationRequested();

            string root = Path.GetFullPath(selectedRoot);

            if (excludedRoots.Contains(root))
            {

                continue;

            }

            Result capture = CaptureSelectedRoot(
                root,
                remainingFiles,
                remainingDirectories,
                remainingBackups,
                acceptedBackups,
                encounteredBackups,
                excludedRoots,
                cancellationToken);

            if (capture.IsFailure)
            {

                return Result<CleanupFile[]>.Failure(capture.Error);

            }

        }

        string? missingBackup = acceptedBackups.Keys
            .FirstOrDefault(path => !encounteredBackups.Contains(path));

        return missingBackup is null
            ? Result<CleanupFile[]>.Success(
                [.. remainingFiles.OrderBy(static file => file.Artifact.Path, PathComparer)])
            : Result<CleanupFile[]>.Failure(new Error(
                ErrorCodes.Data.RecoveryRequired,
                "An accepted backup is missing after offline cleanup."));

    }

    private static Result CaptureSelectedRoot(
        string root,
        List<CleanupFile> files,
        List<IdentityOwnedFileSystemArtifact> directories,
        List<InstallationResetPreservedBackup> backups,
        IReadOnlyDictionary<string, InstallationResetPreservedBackup>? acceptedBackups,
        ISet<string>? encounteredBackups,
        ISet<string> excludedRoots,
        CancellationToken cancellationToken)
    {

        if (!TryCaptureSelectedRoot(root, out IdentityOwnedFileSystemArtifact rootArtifact))
        {

            return Result.Success();

        }

        Stack<IdentityOwnedFileSystemArtifact> pending = new();

        pending.Push(rootArtifact);

        directories.Add(rootArtifact);

        while (pending.TryPop(out IdentityOwnedFileSystemArtifact directory))
        {

            cancellationToken.ThrowIfCancellationRequested();

            if (!IdentityOwnedFileSystemCleanup.TryCapturePath(
                    directory.Path,
                    FileSystemObjectKind.Directory,
                    out IdentityOwnedFileSystemArtifact currentDirectory)
                || currentDirectory.Metadata != directory.Metadata)
            {

                return Result.Failure(new Error(
                    ErrorCodes.Data.RecoveryRequired,
                    "A selected reset directory changed identity during inventory."));

            }

            string[] entries =
            [
                .. Directory.EnumerateFileSystemEntries(
                        directory.Path,
                        "*",
                        SelectedEntryEnumeration)
                    .OrderBy(static path => path, StringComparer.Ordinal),
            ];

            if (!IdentityOwnedFileSystemCleanup.TryCapturePath(
                    directory.Path,
                    FileSystemObjectKind.Directory,
                    out currentDirectory)
                || currentDirectory.Metadata != directory.Metadata)
            {

                return Result.Failure(new Error(
                    ErrorCodes.Data.RecoveryRequired,
                    "A selected reset directory changed identity during inventory."));

            }

            foreach (string entry in entries)
            {

                cancellationToken.ThrowIfCancellationRequested();

                if (excludedRoots.Contains(Path.GetFullPath(entry)))
                {

                    continue;

                }

                if (!FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                        entry,
                        out FileHandleMetadata metadata))
                {

                    return Result.Failure(new Error(
                        ErrorCodes.Data.RecoveryRequired,
                        "A selected reset entry could not be inspected safely."));

                }

                if (metadata.Kind == FileSystemObjectKind.Other)
                {

                    return Result.Failure(new Error(
                        ErrorCodes.Data.RecoveryRequired,
                        "A selected reset entry is a symlink or unsupported filesystem object."));

                }

                if (!IdentityOwnedFileSystemCleanup.TryCapturePath(
                        entry,
                        metadata.Kind,
                        out IdentityOwnedFileSystemArtifact artifact)
                    || artifact.Metadata != metadata)
                {

                    return Result.Failure(new Error(
                        ErrorCodes.Data.RecoveryRequired,
                        "A selected reset entry changed identity during inventory."));

                }

                if (metadata.Kind == FileSystemObjectKind.Directory)
                {

                    directories.Add(artifact);

                    pending.Push(artifact);

                    continue;

                }

                if (metadata.HardLinkCount != 1)
                {

                    return Result.Failure(new Error(
                        ErrorCodes.Data.RecoveryRequired,
                        "A selected reset file has multiple hard links."));

                }

                long length = new FileInfo(entry).Length;

                if (!FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                        entry,
                        out FileHandleMetadata verified)
                    || verified != metadata)
                {

                    return Result.Failure(new Error(
                        ErrorCodes.Data.RecoveryRequired,
                        "A selected reset file changed identity during inventory."));

                }

                if (IsValidBackup(entry))
                {

                    if (!FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                            entry,
                            out verified)
                        || verified != metadata)
                    {

                        return Result.Failure(new Error(
                            ErrorCodes.Data.RecoveryRequired,
                            "A selected backup changed identity during validation."));

                    }

                    InstallationResetPreservedBackup backup = new(
                        artifact.Path,
                        new InstallationResetFileIdentity(
                            $"{metadata.Identity.VolumeId:X16}:{metadata.Identity.FileId:X16}",
                            length,
                            metadata.HardLinkCount));

                    if (acceptedBackups is not null
                        && (!acceptedBackups.TryGetValue(
                                backup.CanonicalPath,
                                out InstallationResetPreservedBackup? accepted)
                            || accepted != backup))
                    {

                        return Result.Failure(new Error(
                            ErrorCodes.Data.PlanChanged,
                            "A valid backup was not present in the accepted reset binding or changed identity."));

                    }

                    encounteredBackups?.Add(backup.CanonicalPath);

                    backups.Add(backup);

                    continue;

                }

                files.Add(new CleanupFile(artifact, length));

            }

        }

        return Result.Success();

    }

    private static Result<Dictionary<string, InstallationResetPreservedBackup>>
        CreateAcceptedBackupCatalog(
            InstallationResetPreservedBackup[] acceptedBackups)
    {

        Dictionary<string, InstallationResetPreservedBackup> catalog =
            new(PathComparer);

        foreach (InstallationResetPreservedBackup accepted in acceptedBackups)
        {

            if (string.IsNullOrWhiteSpace(accepted.CanonicalPath))
            {

                return Result<Dictionary<string, InstallationResetPreservedBackup>>.Failure(
                    new Error(
                        ErrorCodes.Data.PlanChanged,
                        "The accepted backup binding contains an invalid path."));

            }

            string canonicalPath = Path.GetFullPath(accepted.CanonicalPath);

            InstallationResetPreservedBackup canonical = accepted with
            {

                CanonicalPath = canonicalPath,

            };

            if (!catalog.TryAdd(canonicalPath, canonical))
            {

                return Result<Dictionary<string, InstallationResetPreservedBackup>>.Failure(
                    new Error(
                        ErrorCodes.Data.PlanChanged,
                        "The accepted backup binding contains a duplicate path."));

            }

        }

        return Result<Dictionary<string, InstallationResetPreservedBackup>>.Success(catalog);

    }

    private static Result VerifyAcceptedBackups(
        IReadOnlyDictionary<string, InstallationResetPreservedBackup> acceptedBackups)
    {

        foreach (InstallationResetPreservedBackup accepted in acceptedBackups.Values)
        {

            if (!TryReadValidBackup(accepted.CanonicalPath, out InstallationResetPreservedBackup current)
                || current != accepted)
            {

                return Result.Failure(new Error(
                    ErrorCodes.Data.RecoveryRequired,
                    "An accepted backup changed or became unavailable during reset cleanup."));

            }

        }

        return Result.Success();

    }

    private static bool TryReadValidBackup(
        string path,
        out InstallationResetPreservedBackup backup)
    {

        backup = default!;

        if (!FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                path,
                out FileHandleMetadata metadata)
            || metadata.Kind != FileSystemObjectKind.RegularFile
            || metadata.HardLinkCount != 1
            || !IsValidBackup(path))
        {

            return false;

        }

        long length = new FileInfo(path).Length;

        if (!FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                path,
                out FileHandleMetadata verified)
            || verified != metadata)
        {

            return false;

        }

        backup = new InstallationResetPreservedBackup(
            Path.GetFullPath(path),
            new InstallationResetFileIdentity(
                $"{metadata.Identity.VolumeId:X16}:{metadata.Identity.FileId:X16}",
                length,
                metadata.HardLinkCount));

        return true;

    }

    private static bool TryCaptureSelectedRoot(
        string root,
        out IdentityOwnedFileSystemArtifact artifact)
    {

        artifact = default;

        if (FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                root,
                out FileHandleMetadata metadata))
        {

            if (metadata.Kind != FileSystemObjectKind.Directory
                || !IdentityOwnedFileSystemCleanup.TryCapturePath(
                    root,
                    FileSystemObjectKind.Directory,
                    out artifact)
                || artifact.Metadata != metadata)
            {

                throw new IOException(
                    "A selected reset root is not an identity-owned ordinary directory.");

            }

            return true;

        }

        try
        {

            _ = File.GetAttributes(root);

        }
        catch (FileNotFoundException)
        {

            return false;

        }
        catch (DirectoryNotFoundException)
        {

            return false;

        }

        throw new IOException(
            "A selected reset root could not be inspected safely.");

    }

    private static bool HasSymlinkedAncestor(string path)
    {

        string fullPath = NormalizeMacOsSystemAlias(Path.GetFullPath(path));

        string? root = Path.GetPathRoot(fullPath);

        if (string.IsNullOrEmpty(root))
        {

            return true;

        }

        string current = root;

        foreach (string component in fullPath[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {

            current = Path.Combine(current, component);

            try
            {

                FileSystemInfo entry = new DirectoryInfo(current);

                if (!entry.Exists)
                {

                    return false;

                }

                if (entry.LinkTarget is not null)
                {

                    return true;

                }

            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or NotSupportedException)
            {

                return true;

            }

        }

        return false;

    }

    private static string NormalizeMacOsSystemAlias(string path)
    {

        if (!OperatingSystem.IsMacOS())
        {

            return path;

        }

        foreach ((string Alias, string Target) mapping in new[]
                 {
                     ("/etc", "/private/etc"),
                     ("/tmp", "/private/tmp"),
                     ("/var", "/private/var"),
                 })
        {

            if (string.Equals(path, mapping.Alias, StringComparison.Ordinal))
            {

                return mapping.Target;

            }

            string prefix = mapping.Alias + Path.DirectorySeparatorChar;

            if (path.StartsWith(prefix, StringComparison.Ordinal))
            {

                return mapping.Target + path[mapping.Alias.Length..];

            }

        }

        return path;

    }

    private static bool IsValidBackup(string path)
    {

        if (!string.Equals(
                Path.GetExtension(path),
                BackupArchiveFormat.Extension,
                StringComparison.OrdinalIgnoreCase))
        {

            return false;

        }

        try
        {

            Span<byte> header = stackalloc byte[68];

            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            stream.ReadExactly(header);

            int version = BinaryPrimitives.ReadInt32BigEndian(header[8..]);

            int headerLength = BinaryPrimitives.ReadInt32BigEndian(header[12..]);

            return header[..8].SequenceEqual(BackupMagic)
                && version == BackupArchiveFormat.CurrentVersion
                && headerLength == 68
                && header[16] == 1
                && header[17] == 1;

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or EndOfStreamException)
        {

            return false;

        }

    }

    private static bool MatchesDirectoryIdentity(
        FileHandleMetadata current,
        FileHandleMetadata expected) =>
        current.Kind == FileSystemObjectKind.Directory
        && expected.Kind == FileSystemObjectKind.Directory
        && FileHandleIdentity.IdentitiesMatch(
            current.Identity,
            expected.Identity);

    private static InstallationResetFileIdentity ToIdentity(
        FileHandleMetadata metadata,
        long length) =>
        new(
            $"{metadata.Identity.VolumeId:X16}:{metadata.Identity.FileId:X16}",
            length,
            metadata.HardLinkCount);

    private static Result<InstallationResetOfflineCleanupResult> Failure(
        string code,
        string message) =>
        Result<InstallationResetOfflineCleanupResult>.Failure(new Error(
            code,
            message));

    private static Result<InstallationResetOfflineCleanupResult> FailureOrIncomplete(
        long filesDeleted,
        long bytesDeleted,
        List<InstallationResetPreservedBackup> backups,
        string code,
        string message) =>
        filesDeleted == 0
            ? Failure(code, message)
            : Incomplete(
                filesDeleted,
                bytesDeleted,
                backups,
                [new InstallationResetIssueSummary(code, message)]);

    private static Result<InstallationResetOfflineCleanupResult> Incomplete(
        long filesDeleted,
        long bytesDeleted,
        List<InstallationResetPreservedBackup> backups,
        InstallationResetIssueSummary[] issues) =>
        Result<InstallationResetOfflineCleanupResult>.Success(
            new InstallationResetOfflineCleanupResult(
                filesDeleted,
                bytesDeleted,
                CredentialResults: [],
                [.. backups.OrderBy(static backup => backup.CanonicalPath, PathComparer)],
                new InstallationResetVerification(false, issues)));

    private static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static EnumerationOptions SelectedEntryEnumeration { get; } =
        new()
        {

            RecurseSubdirectories = false,

            AttributesToSkip = 0,

            IgnoreInaccessible = false,

            ReturnSpecialDirectories = false,

        };

    private sealed record CleanupFile(
        IdentityOwnedFileSystemArtifact Artifact,
        long Length);

}
