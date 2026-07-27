using System.Security.Cryptography;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

internal sealed record WorkspaceFileCommitOperation(
    string RelativePath,
    WorkspaceFileFingerprint ExpectedFingerprint,
    ReadOnlyMemory<byte>? OutputBytes,
    UnixFileMode? NewFileUnixMode = null)
{

    internal bool IsDelete => !OutputBytes.HasValue;

}

internal enum WorkspaceCommitStatus
{
    Committed,
    Failed,
    RollbackIncomplete,
}

internal enum WorkspaceCommitFailure
{
    None,
    Validation,
    ConcurrentModification,
    StagingFailed,
    CommitFailed,
}

internal sealed record WorkspaceCommitRecovery(
    IReadOnlyList<string> AffectedPaths,
    IReadOnlyList<string> ArtifactPaths);

internal sealed record WorkspaceCommitResult(
    WorkspaceCommitStatus Status,
    WorkspaceCommitFailure Failure,
    ReversibleWorkspaceCommit? Transaction,
    WorkspaceCommitRecovery? Recovery);

internal sealed record WorkspaceArtifactCleanupResult(
    bool Complete,
    IReadOnlyList<string> RetainedArtifactPaths);

internal sealed record WorkspaceRollbackResult(
    bool Complete,
    WorkspaceCommitRecovery? Recovery);

internal sealed record WorkspaceCommitPreparationContext(
    IReadOnlyList<string> StagedArtifactPaths);

internal readonly record struct WorkspaceCommitStepContext(
    int Index,
    string RelativePath);

internal readonly record struct WorkspaceRollbackStepContext(
    int Index,
    string RelativePath);

internal readonly record struct WorkspaceDirectoryRemovalContext(
    string RelativePath,
    string ArtifactRelativePath);

internal readonly record struct WorkspaceFileRemovalContext(
    string RelativePath,
    string ArtifactRelativePath);

internal sealed class MultiFileCommitCoordinatorOptions
{

    internal Func<WorkspaceCommitPreparationContext, CancellationToken, ValueTask>?
        BeforeFirstCommitAsync { get; init; }

    internal Func<WorkspaceCommitStepContext, CancellationToken, ValueTask>?
        BeforeCommitStepAsync { get; init; }

    internal Func<WorkspaceCommitStepContext, CancellationToken, ValueTask>?
        AfterCommitStepAsync { get; init; }

    internal Action<string>? AfterStagingArtifactCreated { get; init; }

    internal Action<string>? BeforeRequiredDirectoryMove { get; init; }

    internal Action<string>? AfterRequiredDirectoryMove { get; init; }

    internal Action<WorkspaceCommitStepContext>? BeforeDestinationMutation { get; init; }

    internal Action<WorkspaceCommitStepContext>? AfterDestinationMutation { get; init; }

    internal Action<WorkspaceCommitStepContext>? AfterOutputRename { get; init; }

    internal Action<WorkspaceRollbackStepContext>? BeforeRollbackMutation { get; init; }

    internal Action<WorkspaceRollbackStepContext>? AfterRollbackStep { get; init; }

    internal Action<string>? BeforeFileRemovalRename { get; init; }

    internal Action<WorkspaceFileRemovalContext>? AfterFileRemovalRename { get; init; }

    internal Action<string>? BeforeDirectoryRemovalRename { get; init; }

    internal Action<WorkspaceDirectoryRemovalContext>? AfterDirectoryRemovalRename { get; init; }

    internal long MaxStagingBytesPerFile { get; init; } = long.MaxValue;

    internal long MaxTotalStagingBytes { get; init; } = long.MaxValue;

    internal TimeSpan CleanupTimeout { get; init; } = TimeSpan.FromSeconds(30);

}

/// <summary>
/// Same-directory staged, reversible multi-file commit. Destination changes are sequential
/// and therefore observable; this coordinator provides rollback semantics, not isolation or
/// crash-atomicity. Every output and backup is prepared before the first destination mutation.
/// </summary>
internal sealed class MultiFileCommitCoordinator
{

    private readonly string _workspaceRoot;

    private readonly MultiFileCommitCoordinatorOptions _options;

    internal MultiFileCommitCoordinator(
        string workspaceRoot,
        MultiFileCommitCoordinatorOptions? options = null)
    {

        _workspaceRoot = Path.GetFullPath(workspaceRoot);

        _options = options ?? new MultiFileCommitCoordinatorOptions();

        if (_options.CleanupTimeout <= TimeSpan.Zero)
        {

            throw new ArgumentOutOfRangeException(nameof(options), "CleanupTimeout must be positive.");

        }

        if (_options.MaxStagingBytesPerFile < 1
            || _options.MaxTotalStagingBytes < 1)
        {

            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Staging byte limits must be positive.");

        }

    }

    internal async Task<WorkspaceCommitResult> CommitAsync(
        IReadOnlyList<WorkspaceFileCommitOperation> operations,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(operations);

        if (operations.Count == 0)
        {

            return new WorkspaceCommitResult(
                WorkspaceCommitStatus.Failed,
                WorkspaceCommitFailure.Validation,
                Transaction: null,
                Recovery: null);

        }

        List<StagedOperation> staged = [];

        List<CreatedDirectory> createdDirectories = [];

        try
        {

            ValidateAndResolveOperations(operations, staged);

            ValidateStagingCapacity(staged);

            await ValidateExpectedFingerprintsAsync(staged, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            CreateRequiredDirectories(
                staged,
                createdDirectories,
                cancellationToken);

            CaptureDestinationParentIdentities(
                staged,
                cancellationToken);

            await StageAllAsync(staged, cancellationToken).ConfigureAwait(false);

            if (_options.BeforeFirstCommitAsync is not null)
            {

                WorkspaceCommitPreparationContext context = new(
                    staged
                        .SelectMany(operation => operation.ExistingArtifactRelativePaths())
                        .OrderBy(path => path, WorkspaceRelativePath.DeterministicComparer)
                        .ToArray());

                await _options.BeforeFirstCommitAsync(context, cancellationToken).ConfigureAwait(false);

            }

            for (int index = 0; index < staged.Count; index++)
            {

                cancellationToken.ThrowIfCancellationRequested();

                StagedOperation operation = staged[index];

                WorkspaceCommitStepContext context = new(index, operation.RelativePath);

                if (_options.BeforeCommitStepAsync is not null)
                {

                    await _options.BeforeCommitStepAsync(context, cancellationToken).ConfigureAwait(false);

                }

                if (!await WorkspaceFileFingerprintService.MatchesCurrentAsync(
                        _workspaceRoot,
                        operation.RelativePath,
                        operation.ExpectedFingerprint,
                        cancellationToken).ConfigureAwait(false))
                {

                    throw new WorkspaceMutationRejectedException(
                        WorkspaceMutationRejection.ConcurrentModification,
                        "A destination changed after staging and before its commit step.");

                }

                await ValidateStagedArtifactsAsync(
                    operation,
                    cancellationToken).ConfigureAwait(false);

                await CommitOneAsync(operation, context).ConfigureAwait(false);

                _options.AfterDestinationMutation?.Invoke(context);

                cancellationToken.ThrowIfCancellationRequested();

                if (_options.AfterCommitStepAsync is not null)
                {

                    await _options.AfterCommitStepAsync(context, cancellationToken).ConfigureAwait(false);

                }

            }

            ReversibleWorkspaceCommit transaction = new(
                _workspaceRoot,
                staged,
                createdDirectories,
                _options);

            return new WorkspaceCommitResult(
                WorkspaceCommitStatus.Committed,
                WorkspaceCommitFailure.None,
                transaction,
                BuildRecovery(staged));

        }
        catch (OperationCanceledException cancellation)
        {

            WorkspaceRollbackResult rollback = await RollbackWithIndependentDeadlineAsync(
                staged,
                createdDirectories).ConfigureAwait(false);

            if (rollback.Recovery is not null)
            {

                cancellation.Data[nameof(WorkspaceCommitRecovery)] = rollback.Recovery;

            }

            throw;

        }
        catch (Exception exception) when (IsExpectedCommitFailure(exception))
        {

            WorkspaceCommitFailure failure = ClassifyFailure(exception, staged);

            WorkspaceRollbackResult rollback = await RollbackWithIndependentDeadlineAsync(
                staged,
                createdDirectories).ConfigureAwait(false);

            return new WorkspaceCommitResult(
                rollback.Complete
                    ? WorkspaceCommitStatus.Failed
                    : WorkspaceCommitStatus.RollbackIncomplete,
                failure,
                Transaction: null,
                rollback.Recovery);

        }

    }

    private void CaptureDestinationParentIdentities(
        IReadOnlyList<StagedOperation> staged,
        CancellationToken cancellationToken)
    {

        foreach (StagedOperation operation in staged)
        {

            cancellationToken.ThrowIfCancellationRequested();

            string parent =
                Path.GetDirectoryName(operation.AbsolutePath) ?? _workspaceRoot;

            if (!WorkspacePathPolicy.RevalidatePathBeforeIo(
                    _workspaceRoot,
                    parent)
                || !WorkspaceFileFingerprintService.TryGetDirectoryIdentity(
                    parent,
                    out FileHandleIdentity identity))
            {

                throw new WorkspaceMutationRejectedException(
                    WorkspaceMutationRejection.MetadataUnavailable,
                    "A destination parent identity could not be captured.");

            }

            operation.DestinationParentAbsolutePath = parent;

            operation.DestinationParentIdentity = identity;

        }

    }

    private void ValidateStagingCapacity(
        IReadOnlyList<StagedOperation> staged)
    {

        long total = 0;

        foreach (StagedOperation operation in staged)
        {

            long operationBytes;

            try
            {

                operationBytes = checked(
                    (operation.OutputBytes?.Length ?? 0)
                    + (operation.ExpectedFingerprint.Exists
                        ? operation.ExpectedFingerprint.Length
                        : 0));

                total = checked(total + operationBytes);

            }
            catch (OverflowException exception)
            {

                throw new WorkspaceMutationRejectedException(
                    WorkspaceMutationRejection.MetadataUnavailable,
                    "Staging byte capacity overflowed.",
                    exception);

            }

            if (operationBytes > _options.MaxStagingBytesPerFile
                || total > _options.MaxTotalStagingBytes)
            {

                throw new WorkspaceMutationRejectedException(
                    WorkspaceMutationRejection.MetadataUnavailable,
                    "The commit exceeds its preflight staging byte capacity.");

            }

        }

    }

    private async Task<WorkspaceFileFingerprint> CapturePostCommitFingerprintAsync(
        StagedOperation operation)
    {

        using CancellationTokenSource deadline = new(_options.CleanupTimeout);

        try
        {

            return await WorkspaceFileFingerprintService.CaptureForMutationAsync(
                _workspaceRoot,
                operation.RelativePath,
                deadline.Token).ConfigureAwait(false);

        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {

            throw new IOException(
                "Post-mutation fingerprint capture exceeded the bounded cleanup deadline.");

        }

    }

    private void ValidateAndResolveOperations(
        IReadOnlyList<WorkspaceFileCommitOperation> operations,
        List<StagedOperation> staged)
    {

        HashSet<string> destinations = new(WorkspaceRelativePath.Comparer);

        foreach (WorkspaceFileCommitOperation operation in operations)
        {

            (string root, string absolutePath, string normalized) =
                WorkspaceFileFingerprintService.Resolve(
                    _workspaceRoot,
                    operation.RelativePath);

            if (!string.Equals(root, _workspaceRoot, WorkspaceRelativePath.Comparison)
                || !destinations.Add(normalized))
            {

                throw new WorkspaceMutationRejectedException(
                    WorkspaceMutationRejection.InvalidRelativePath,
                    "Commit destinations must be unique normalized workspace-relative paths.");

            }

            if (operation.IsDelete && !operation.ExpectedFingerprint.Exists)
            {

                throw new WorkspaceMutationRejectedException(
                    WorkspaceMutationRejection.InvalidRelativePath,
                    "Deleting a destination that was captured as missing is not a valid operation.");

            }

            ReadOnlyMemory<byte>? stagedOutput = null;

            if (operation.OutputBytes.HasValue)
            {

                stagedOutput = new ReadOnlyMemory<byte>(
                    operation.OutputBytes.Value.ToArray());

            }

            staged.Add(
                new StagedOperation(
                    normalized,
                    absolutePath,
                    operation.ExpectedFingerprint,
                    stagedOutput,
                    operation.NewFileUnixMode));

        }

    }

    private async Task ValidateExpectedFingerprintsAsync(
        IReadOnlyList<StagedOperation> staged,
        CancellationToken cancellationToken)
    {

        foreach (StagedOperation operation in staged)
        {

            if (!await WorkspaceFileFingerprintService.MatchesCurrentAsync(
                    _workspaceRoot,
                    operation.RelativePath,
                    operation.ExpectedFingerprint,
                    cancellationToken).ConfigureAwait(false))
            {

                throw new WorkspaceMutationRejectedException(
                    WorkspaceMutationRejection.ConcurrentModification,
                    "A destination changed before staging began.");

            }

        }

    }

    private void CreateRequiredDirectories(
        IReadOnlyList<StagedOperation> staged,
        List<CreatedDirectory> createdDirectories,
        CancellationToken cancellationToken)
    {

        foreach (StagedOperation operation in staged)
        {

            cancellationToken.ThrowIfCancellationRequested();

            string? parent = Path.GetDirectoryName(operation.AbsolutePath);

            if (string.IsNullOrEmpty(parent)
                || Directory.Exists(parent))
            {

                continue;

            }

            Stack<string> missing = new();

            string? current = parent;

            while (!string.IsNullOrEmpty(current)
                && !Directory.Exists(current)
                && !string.Equals(
                    current,
                    _workspaceRoot,
                    WorkspaceRelativePath.Comparison))
            {

                missing.Push(current);

                current = Path.GetDirectoryName(current);

            }

            while (missing.Count > 0)
            {

                cancellationToken.ThrowIfCancellationRequested();

                string directory = missing.Pop();

                if (!WorkspacePathPolicy.RevalidatePathBeforeIo(_workspaceRoot, directory))
                {

                    throw new WorkspaceMutationRejectedException(
                        WorkspaceMutationRejection.PathEscapesWorkspace,
                        "A destination directory failed containment revalidation.");

                }

                if (Directory.Exists(directory))
                {

                    continue;

                }

                CreateRequiredDirectory(
                    directory,
                    createdDirectories,
                    cancellationToken);

            }

        }

    }

    private void CreateRequiredDirectory(
        string directory,
        List<CreatedDirectory> createdDirectories,
        CancellationToken cancellationToken)
    {

        string parent = Path.GetDirectoryName(directory) ?? _workspaceRoot;

        if (!WorkspacePathPolicy.RevalidatePathBeforeIo(_workspaceRoot, parent)
            || !WorkspaceFileFingerprintService.TryGetDirectoryIdentity(
                parent,
                out FileHandleIdentity parentIdentity))
        {

            throw new WorkspaceMutationRejectedException(
                WorkspaceMutationRejection.ConcurrentModification,
                "A required destination parent could not be identity-bound.");

        }

        string stagingPath = Path.Combine(
            parent,
            $".arcanum-dir-{Guid.NewGuid():N}");

        Directory.CreateDirectory(stagingPath);

        CreatedDirectory stagingDirectory = new(
            WorkspaceRelativePath.FromAbsolute(
                _workspaceRoot,
                stagingPath),
            stagingPath);

        createdDirectories.Add(stagingDirectory);

        FileHandleIdentity stagingIdentity = default;

        bool moved = false;

        try
        {

            if (!WorkspacePathPolicy.RevalidatePathBeforeIo(
                    _workspaceRoot,
                    stagingPath)
                || !WorkspaceFileFingerprintService.TryGetDirectoryIdentity(
                    stagingPath,
                    out stagingIdentity))
            {

                throw new WorkspaceMutationRejectedException(
                    WorkspaceMutationRejection.MetadataUnavailable,
                    "A staged destination directory identity could not be captured.");

            }

            stagingDirectory.Identity = stagingIdentity;

            string relative =
                WorkspaceRelativePath.FromAbsolute(_workspaceRoot, directory);

            _options.BeforeRequiredDirectoryMove?.Invoke(relative);

            cancellationToken.ThrowIfCancellationRequested();

            if (Directory.Exists(directory)
                || File.Exists(directory)
                || !WorkspacePathPolicy.RevalidatePathBeforeIo(
                    _workspaceRoot,
                    directory)
                || !WorkspaceFileFingerprintService.TryGetDirectoryIdentity(
                    parent,
                    out FileHandleIdentity currentParentIdentity)
                || !FileHandleIdentity.IdentitiesMatch(
                    parentIdentity,
                    currentParentIdentity))
            {

                throw new WorkspaceMutationRejectedException(
                    WorkspaceMutationRejection.ConcurrentModification,
                    "A required destination directory appeared or its parent changed.");

            }

            Directory.Move(stagingPath, directory);

            moved = true;

            _ = createdDirectories.Remove(stagingDirectory);

            CreatedDirectory createdDirectory = new(relative, directory)
            {
                Identity = stagingIdentity,
            };

            createdDirectories.Add(createdDirectory);

            _options.AfterRequiredDirectoryMove?.Invoke(relative);

            cancellationToken.ThrowIfCancellationRequested();

            if (!WorkspaceFileFingerprintService.TryGetDirectoryIdentity(
                    directory,
                    out FileHandleIdentity movedIdentity)
                || !FileHandleIdentity.IdentitiesMatch(
                    stagingIdentity,
                    movedIdentity))
            {

                throw new WorkspaceMutationRejectedException(
                    WorkspaceMutationRejection.ConcurrentModification,
                    "A transaction-created directory changed during its create-only rename.");

            }

        }
        finally
        {

            if (!moved
                && Directory.Exists(stagingPath)
                && WorkspaceFileFingerprintService.TryGetDirectoryIdentity(
                    stagingPath,
                    out FileHandleIdentity currentStagingIdentity)
                && FileHandleIdentity.IdentitiesMatch(
                    stagingIdentity,
                    currentStagingIdentity))
            {

                try
                {

                    Directory.Delete(stagingPath, recursive: false);

                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {

                }

            }

        }

    }

    private async Task StageAllAsync(
        IReadOnlyList<StagedOperation> staged,
        CancellationToken cancellationToken)
    {

        foreach (StagedOperation operation in staged)
        {

            cancellationToken.ThrowIfCancellationRequested();

            string directory = Path.GetDirectoryName(operation.AbsolutePath)
                ?? _workspaceRoot;

            string fileName = Path.GetFileName(operation.AbsolutePath);

            if (operation.OutputBytes is not null)
            {

                operation.TempAbsolutePath = GenerateArtifactPath(
                    directory,
                    fileName,
                    "tmp",
                    out string tempRelative);

                operation.TempRelativePath = tempRelative;

                await WriteStagedOutputAsync(operation, cancellationToken).ConfigureAwait(false);

                operation.TempFingerprint =
                    await WorkspaceFileFingerprintService.CaptureForMutationAsync(
                        _workspaceRoot,
                        tempRelative,
                        cancellationToken).ConfigureAwait(false);

            }

            if (operation.ExpectedFingerprint.Exists)
            {

                operation.BackupAbsolutePath = GenerateArtifactPath(
                    directory,
                    fileName,
                    "bak",
                    out string backupRelative);

                operation.BackupRelativePath = backupRelative;

                File.Copy(
                    operation.AbsolutePath,
                    operation.BackupAbsolutePath,
                    overwrite: false);

                if (!FileHandleIdentityInterop.TryGetPathMetadata(
                        operation.BackupAbsolutePath,
                        out FileHandleMetadata backupMetadata)
                    || backupMetadata.HardLinkCount != 1
                    || backupMetadata.Kind != FileSystemObjectKind.RegularFile)
                {

                    throw new WorkspaceMutationRejectedException(
                        WorkspaceMutationRejection.MetadataUnavailable,
                        "A staged backup identity could not be captured.");

                }

                operation.BackupIdentity = backupMetadata.Identity;

                ApplyUnixMode(
                    operation.BackupAbsolutePath,
                    operation.ExpectedFingerprint.UnixMode);

                await VerifyBackupAsync(operation, cancellationToken).ConfigureAwait(false);

                operation.BackupFingerprint =
                    await WorkspaceFileFingerprintService.CaptureForMutationAsync(
                        _workspaceRoot,
                        backupRelative,
                        cancellationToken).ConfigureAwait(false);

            }

            if (!await WorkspaceFileFingerprintService.MatchesCurrentAsync(
                    _workspaceRoot,
                    operation.RelativePath,
                    operation.ExpectedFingerprint,
                    cancellationToken).ConfigureAwait(false))
            {

                throw new WorkspaceMutationRejectedException(
                    WorkspaceMutationRejection.ConcurrentModification,
                    "A destination changed while all commit inputs were being staged.");

            }

        }

    }

    private async Task WriteStagedOutputAsync(
        StagedOperation operation,
        CancellationToken cancellationToken)
    {

        await using (FileStream stream = new(
            operation.TempAbsolutePath!,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {

            if (!FileHandleIdentityInterop.TryGetHandleMetadata(
                    stream.SafeFileHandle,
                    out FileHandleMetadata tempMetadata)
                || tempMetadata.HardLinkCount != 1
                || tempMetadata.Kind != FileSystemObjectKind.RegularFile)
            {

                throw new WorkspaceMutationRejectedException(
                    WorkspaceMutationRejection.MetadataUnavailable,
                    "A staged output identity could not be captured.");

            }

            operation.TempIdentity = tempMetadata.Identity;

            _options.AfterStagingArtifactCreated?.Invoke(operation.TempRelativePath!);

            await stream.WriteAsync(
                operation.OutputBytes!.Value,
                cancellationToken).ConfigureAwait(false);

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            stream.Flush(flushToDisk: true);

        }

        UnixFileMode? mode = operation.ExpectedFingerprint.Exists
            ? operation.ExpectedFingerprint.UnixMode
            : operation.NewFileUnixMode;

        ApplyUnixMode(operation.TempAbsolutePath!, mode);

    }

    private static async Task VerifyBackupAsync(
        StagedOperation operation,
        CancellationToken cancellationToken)
    {

        await using FileStream backup = new(
            operation.BackupAbsolutePath!,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        byte[] hash = await SHA256.HashDataAsync(
            backup,
            cancellationToken).ConfigureAwait(false);

        bool contentMatches =
            backup.Length == operation.ExpectedFingerprint.Length
            && string.Equals(
                Convert.ToHexString(hash),
                operation.ExpectedFingerprint.ContentSha256,
                StringComparison.Ordinal);

        bool modeMatches =
            OperatingSystem.IsWindows()
            || operation.ExpectedFingerprint.UnixMode is null
            || File.GetUnixFileMode(operation.BackupAbsolutePath!)
                == operation.ExpectedFingerprint.UnixMode.Value;

        if (!contentMatches || !modeMatches)
        {

            throw new IOException("A staged backup did not match its prepared input.");

        }

    }

    private async Task ValidateStagedArtifactsAsync(
        StagedOperation operation,
        CancellationToken cancellationToken)
    {

        if (operation.TempRelativePath is not null
            && operation.TempFingerprint is WorkspaceFileFingerprint tempFingerprint
            && !await WorkspaceFileFingerprintService.MatchesCurrentAsync(
                _workspaceRoot,
                operation.TempRelativePath,
                tempFingerprint,
                cancellationToken).ConfigureAwait(false))
        {

            throw new WorkspaceMutationRejectedException(
                WorkspaceMutationRejection.ConcurrentModification,
                "A staged output changed before its commit step.");

        }

        if (operation.BackupRelativePath is not null
            && operation.BackupFingerprint is WorkspaceFileFingerprint backupFingerprint
            && !await WorkspaceFileFingerprintService.MatchesCurrentAsync(
                _workspaceRoot,
                operation.BackupRelativePath,
                backupFingerprint,
                cancellationToken).ConfigureAwait(false))
        {

            throw new WorkspaceMutationRejectedException(
                WorkspaceMutationRejection.ConcurrentModification,
                "A staged backup changed before its commit step.");

        }

    }

    private async Task CommitOneAsync(
        StagedOperation operation,
        WorkspaceCommitStepContext context)
    {

        if (!WorkspacePathPolicy.RevalidatePathBeforeIo(
                _workspaceRoot,
                operation.AbsolutePath))
        {

            throw new WorkspaceMutationRejectedException(
                WorkspaceMutationRejection.PathEscapesWorkspace,
                "A destination failed its final containment check.");

        }

        if (!TryRevalidateDestinationWithExclusiveHandle(operation))
        {

            throw new WorkspaceMutationRejectedException(
                WorkspaceMutationRejection.ConcurrentModification,
                "A destination changed during its final mutation revalidation.");

        }

        _options.BeforeDestinationMutation?.Invoke(context);

        if (operation.IsDelete)
        {

            string directory = Path.GetDirectoryName(operation.AbsolutePath) ?? _workspaceRoot;

            operation.DeleteArtifactAbsolutePath = GenerateArtifactPath(
                directory,
                Path.GetFileName(operation.AbsolutePath),
                "deleted",
                out string deleteArtifactRelative);

            operation.DeleteArtifactRelativePath = deleteArtifactRelative;

            if (!TryRevalidateDestinationWithExclusiveHandle(operation))
            {

                throw new WorkspaceMutationRejectedException(
                    WorkspaceMutationRejection.ConcurrentModification,
                    "A destination changed immediately before deletion.");

            }

            _options.BeforeFileRemovalRename?.Invoke(operation.RelativePath);

            File.Move(
                operation.AbsolutePath,
                operation.DeleteArtifactAbsolutePath,
                overwrite: false);

            operation.Committed = true;

            if (!TryRevalidateDestinationParent(operation))
            {

                operation.RetainRecoveryArtifacts = true;

                throw new WorkspaceMutationRejectedException(
                    WorkspaceMutationRejection.ConcurrentModification,
                    "The destination parent changed during delete-by-rename.");

            }

            _options.AfterFileRemovalRename?.Invoke(
                new WorkspaceFileRemovalContext(
                    operation.RelativePath,
                    operation.DeleteArtifactRelativePath));

            using CancellationTokenSource verificationDeadline =
                new(_options.CleanupTimeout);

            operation.DeleteArtifactFingerprint =
                await WorkspaceFileFingerprintService.CaptureForMutationAsync(
                    _workspaceRoot,
                    operation.DeleteArtifactRelativePath,
                    verificationDeadline.Token).ConfigureAwait(false);

            operation.DeleteArtifactIdentity =
                operation.DeleteArtifactFingerprint.Value.Identity;

            if (!ReversibleWorkspaceCommit.MatchesMovedArtifact(
                    operation.DeleteArtifactFingerprint.Value,
                    operation.ExpectedFingerprint))
            {

                operation.RetainRecoveryArtifacts = true;

                _ = ReversibleWorkspaceCommit.TryRestoreNoOverwrite(
                    operation.DeleteArtifactAbsolutePath,
                    operation.AbsolutePath);

                throw new WorkspaceMutationRejectedException(
                    WorkspaceMutationRejection.ConcurrentModification,
                    "The destination identity changed during delete-by-rename.");

            }

            operation.PostCommitFingerprint =
                await CapturePostCommitFingerprintAsync(operation).ConfigureAwait(false);

            if (operation.PostCommitFingerprint.Value.Exists)
            {

                operation.RetainRecoveryArtifacts = true;

                throw new WorkspaceMutationRejectedException(
                    WorkspaceMutationRejection.ConcurrentModification,
                    "The deleted destination reappeared before commit verification completed.");

            }

            return;

        }

        ValidateArtifact(operation.TempAbsolutePath!, operation.AbsolutePath);

        if (!TryRevalidateDestinationWithExclusiveHandle(operation))
        {

            throw new WorkspaceMutationRejectedException(
                WorkspaceMutationRejection.ConcurrentModification,
                "A destination changed immediately before replacement.");

        }

        // Existing-file replacement has no portable compare-and-swap primitive. The exclusive
        // identity/content handle is therefore held for the strongest available verification,
        // released only because Windows cannot rename over an open destination, and the path is
        // revalidated immediately before this atomic rename. New files always use no-overwrite.
        File.Move(
            operation.TempAbsolutePath!,
            operation.AbsolutePath,
            overwrite: operation.ExpectedFingerprint.Exists);

        operation.Committed = true;

        if (!TryRevalidateDestinationParent(operation))
        {

            operation.RetainRecoveryArtifacts = true;

            throw new WorkspaceMutationRejectedException(
                WorkspaceMutationRejection.ConcurrentModification,
                "The destination parent changed during output rename.");

        }

        _options.AfterOutputRename?.Invoke(context);

        WorkspaceFileFingerprint postCommit =
            await CapturePostCommitFingerprintAsync(operation).ConfigureAwait(false);

        if (operation.TempFingerprint is not WorkspaceFileFingerprint stagedOutput
            || !ReversibleWorkspaceCommit.MatchesMovedArtifact(
                postCommit,
                stagedOutput)
            || (!operation.ExpectedFingerprint.Exists
                && !WorkspaceFileFingerprintService.MatchesMissingParent(
                    _workspaceRoot,
                    operation.ExpectedFingerprint)))
        {

            operation.RetainRecoveryArtifacts = true;

            throw new WorkspaceMutationRejectedException(
                WorkspaceMutationRejection.ConcurrentModification,
                "The committed destination does not match the staged output identity and content.");

        }

        operation.PostCommitFingerprint = postCommit;

    }

    private bool TryRevalidateDestinationWithExclusiveHandle(
        StagedOperation operation)
    {

        if (!TryRevalidateDestinationParent(operation))
        {

            return false;

        }

        if (!operation.ExpectedFingerprint.Exists)
        {

            return !File.Exists(operation.AbsolutePath)
                && !Directory.Exists(operation.AbsolutePath)
                && WorkspaceFileFingerprintService.MatchesMissingParent(
                    _workspaceRoot,
                    operation.ExpectedFingerprint);

        }

        try
        {

            using FileStream stream = new(
                operation.AbsolutePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.SequentialScan);

            if (!FileHandleIdentityInterop.TryGetHandleMetadata(
                    stream.SafeFileHandle,
                    out FileHandleMetadata metadata)
                || metadata.HardLinkCount != 1
                || metadata.Kind != FileSystemObjectKind.RegularFile
                || !FileHandleIdentity.IdentitiesMatch(
                    operation.ExpectedFingerprint.Identity,
                    metadata.Identity)
                || stream.Length != operation.ExpectedFingerprint.Length)
            {

                return false;

            }

            string contentHash = Convert.ToHexString(SHA256.HashData(stream));

            if (!string.Equals(
                    contentHash,
                    operation.ExpectedFingerprint.ContentSha256,
                    StringComparison.Ordinal))
            {

                return false;

            }

            if (!OperatingSystem.IsWindows()
                && operation.ExpectedFingerprint.UnixMode is UnixFileMode expectedMode
                && File.GetUnixFileMode(operation.AbsolutePath) != expectedMode)
            {

                return false;

            }

            return FileHandleIdentityInterop.TryGetPathMetadata(
                    operation.AbsolutePath,
                    out FileHandleMetadata pathMetadata)
                && pathMetadata.HardLinkCount == 1
                && FileHandleIdentity.IdentitiesMatch(
                    metadata.Identity,
                    pathMetadata.Identity);

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
        {

            return false;

        }

    }

    private bool TryRevalidateDestinationParent(
        StagedOperation operation) =>
        operation.DestinationParentAbsolutePath is not null
        && operation.DestinationParentIdentity
            is FileHandleIdentity expectedIdentity
        && WorkspacePathPolicy.RevalidatePathBeforeIo(
            _workspaceRoot,
            operation.DestinationParentAbsolutePath)
        && WorkspaceFileFingerprintService.TryGetDirectoryIdentity(
            operation.DestinationParentAbsolutePath,
            out FileHandleIdentity currentIdentity)
        && FileHandleIdentity.IdentitiesMatch(
            expectedIdentity,
            currentIdentity);

    private string GenerateArtifactPath(
        string directory,
        string fileName,
        string extension,
        out string relativePath)
    {

        for (int attempt = 0; attempt < 16; attempt++)
        {

            string candidate = Path.Combine(
                directory,
                $".{fileName}.arcanum-{Guid.NewGuid():N}.{extension}");

            if (File.Exists(candidate) || Directory.Exists(candidate))
            {

                continue;

            }

            if (!WorkspacePathPolicy.RevalidatePathBeforeIo(_workspaceRoot, candidate))
            {

                throw new WorkspaceMutationRejectedException(
                    WorkspaceMutationRejection.PathEscapesWorkspace,
                    "A server-generated staging artifact failed containment validation.");

            }

            relativePath = WorkspaceRelativePath.FromAbsolute(_workspaceRoot, candidate);

            return candidate;

        }

        throw new IOException("A unique server-generated staging artifact could not be allocated.");

    }

    private void ValidateArtifact(string artifactPath, string destinationPath)
    {

        if (!WorkspacePathPolicy.RevalidatePathBeforeIo(_workspaceRoot, artifactPath)
            || !string.Equals(
                Path.GetDirectoryName(artifactPath),
                Path.GetDirectoryName(destinationPath),
                WorkspaceRelativePath.Comparison))
        {

            throw new WorkspaceMutationRejectedException(
                WorkspaceMutationRejection.PathEscapesWorkspace,
                "A staging artifact is not in its destination directory.");

        }

    }

    private async Task<WorkspaceRollbackResult> RollbackWithIndependentDeadlineAsync(
        IReadOnlyList<StagedOperation> staged,
        IReadOnlyList<CreatedDirectory> createdDirectories)
    {

        using CancellationTokenSource cleanupDeadline = new(_options.CleanupTimeout);

        try
        {

            return await ReversibleWorkspaceCommit.RollbackCoreAsync(
                _workspaceRoot,
                staged,
                createdDirectories,
                _options,
                cleanupDeadline.Token).ConfigureAwait(false);

        }
        catch (OperationCanceledException cancellation)
        {

            return new WorkspaceRollbackResult(
                Complete: false,
                ReversibleWorkspaceCommit.CaptureCancellationRecovery(
                    cancellation,
                    staged));

        }

    }

    private static WorkspaceCommitFailure ClassifyFailure(
        Exception exception,
        IReadOnlyList<StagedOperation> staged)
    {

        if (exception is WorkspaceMutationRejectedException rejected)
        {

            return rejected.Rejection == WorkspaceMutationRejection.ConcurrentModification
                ? WorkspaceCommitFailure.ConcurrentModification
                : WorkspaceCommitFailure.Validation;

        }

        return staged.Any(operation => operation.Committed)
            ? WorkspaceCommitFailure.CommitFailed
            : WorkspaceCommitFailure.StagingFailed;

    }

    private static bool IsExpectedCommitFailure(Exception exception) =>
        exception is WorkspaceMutationRejectedException
            or IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException;

    private static void ApplyUnixMode(string path, UnixFileMode? mode)
    {

        if (!OperatingSystem.IsWindows() && mode is not null)
        {

            File.SetUnixFileMode(path, mode.Value);

        }

    }

    private static WorkspaceCommitRecovery? BuildRecovery(
        IReadOnlyList<StagedOperation> staged)
    {

        string[] artifacts = staged
            .SelectMany(operation => operation.ExistingArtifactRelativePaths())
            .Distinct(WorkspaceRelativePath.Comparer)
            .OrderBy(path => path, WorkspaceRelativePath.DeterministicComparer)
            .ToArray();

        if (artifacts.Length == 0)
        {

            return null;

        }

        string[] affected = staged
            .Select(operation => operation.RelativePath)
            .Distinct(WorkspaceRelativePath.Comparer)
            .OrderBy(path => path, WorkspaceRelativePath.DeterministicComparer)
            .ToArray();

        return new WorkspaceCommitRecovery(affected, artifacts);

    }

    internal sealed class StagedOperation(
        string relativePath,
        string absolutePath,
        WorkspaceFileFingerprint expectedFingerprint,
        ReadOnlyMemory<byte>? outputBytes,
        UnixFileMode? newFileUnixMode)
    {

        internal string RelativePath { get; } = relativePath;

        internal string AbsolutePath { get; } = absolutePath;

        internal WorkspaceFileFingerprint ExpectedFingerprint { get; } = expectedFingerprint;

        internal ReadOnlyMemory<byte>? OutputBytes { get; } = outputBytes;

        internal UnixFileMode? NewFileUnixMode { get; } = newFileUnixMode;

        internal bool IsDelete => !OutputBytes.HasValue;

        internal string? DestinationParentAbsolutePath { get; set; }

        internal FileHandleIdentity? DestinationParentIdentity { get; set; }

        internal string? TempAbsolutePath { get; set; }

        internal string? TempRelativePath { get; set; }

        internal FileHandleIdentity? TempIdentity { get; set; }

        internal WorkspaceFileFingerprint? TempFingerprint { get; set; }

        internal string? BackupAbsolutePath { get; set; }

        internal string? BackupRelativePath { get; set; }

        internal FileHandleIdentity? BackupIdentity { get; set; }

        internal WorkspaceFileFingerprint? BackupFingerprint { get; set; }

        internal string? DeleteArtifactAbsolutePath { get; set; }

        internal string? DeleteArtifactRelativePath { get; set; }

        internal FileHandleIdentity? DeleteArtifactIdentity { get; set; }

        internal WorkspaceFileFingerprint? DeleteArtifactFingerprint { get; set; }

        internal string? RollbackArtifactAbsolutePath { get; set; }

        internal string? RollbackArtifactRelativePath { get; set; }

        internal FileHandleIdentity? RollbackArtifactIdentity { get; set; }

        internal WorkspaceFileFingerprint? RollbackArtifactFingerprint { get; set; }

        internal bool Committed { get; set; }

        internal bool RolledBack { get; set; }

        internal WorkspaceFileFingerprint? PostCommitFingerprint { get; set; }

        internal bool RetainRecoveryArtifacts { get; set; }

        internal IEnumerable<string> ExistingArtifactRelativePaths()
        {

            if (TempAbsolutePath is not null
                && File.Exists(TempAbsolutePath)
                && TempRelativePath is not null)
            {

                yield return TempRelativePath;

            }

            if (BackupAbsolutePath is not null
                && File.Exists(BackupAbsolutePath)
                && BackupRelativePath is not null)
            {

                yield return BackupRelativePath;

            }

            if (DeleteArtifactAbsolutePath is not null
                && File.Exists(DeleteArtifactAbsolutePath)
                && DeleteArtifactRelativePath is not null)
            {

                yield return DeleteArtifactRelativePath;

            }

            if (RollbackArtifactAbsolutePath is not null
                && File.Exists(RollbackArtifactAbsolutePath)
                && RollbackArtifactRelativePath is not null)
            {

                yield return RollbackArtifactRelativePath;

            }

        }

    }

    internal sealed class CreatedDirectory(
        string relativePath,
        string absolutePath)
    {

        internal string RelativePath { get; } = relativePath;

        internal string AbsolutePath { get; } = absolutePath;

        internal FileHandleIdentity? Identity { get; set; }

    }

}

/// <summary>
/// A successfully committed filesystem change whose backups remain available until the caller
/// durably classifies its receipt. The caller may roll back or mark it irreversible exactly once.
/// </summary>
internal sealed class ReversibleWorkspaceCommit
{
    private const string RetainedArtifactPathsDataKey =
        "WorkspaceRetainedArtifactPaths";

    private readonly string _workspaceRoot;

    private readonly IReadOnlyList<MultiFileCommitCoordinator.StagedOperation> _staged;

    private readonly IReadOnlyList<MultiFileCommitCoordinator.CreatedDirectory> _createdDirectories;

    private readonly MultiFileCommitCoordinatorOptions _options;

    private readonly SemaphoreSlim _stateGate = new(1, 1);

    private TransactionState _state;

    private WorkspaceArtifactCleanupResult? _irreversibleCleanupResult;

    internal ReversibleWorkspaceCommit(
        string workspaceRoot,
        IReadOnlyList<MultiFileCommitCoordinator.StagedOperation> staged,
        IReadOnlyList<MultiFileCommitCoordinator.CreatedDirectory> createdDirectories,
        MultiFileCommitCoordinatorOptions options)
    {

        _workspaceRoot = workspaceRoot;

        _staged = staged;

        _createdDirectories = createdDirectories;

        _options = options;

    }

    internal async Task<WorkspaceRollbackResult> RollbackAsync(
        CancellationToken cancellationToken)
    {

        bool callerCancelled = cancellationToken.IsCancellationRequested;

        await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);

        try
        {

            if (_state == TransactionState.Irreversible)
            {

                throw new InvalidOperationException("An irreversible workspace commit cannot be rolled back.");

            }

            if (_state == TransactionState.RolledBack)
            {

                return new WorkspaceRollbackResult(Complete: true, Recovery: null);

            }

            using CancellationTokenSource cleanupDeadline = new(_options.CleanupTimeout);

            WorkspaceRollbackResult result;

            try
            {

                result = await RollbackCoreAsync(
                    _workspaceRoot,
                    _staged,
                    _createdDirectories,
                    _options,
                    cleanupDeadline.Token).ConfigureAwait(false);

            }
            catch (OperationCanceledException cancellation)
            {

                result = new WorkspaceRollbackResult(
                    Complete: false,
                    CaptureCancellationRecovery(
                        cancellation,
                        _staged));

            }

            if (result.Complete)
            {

                _state = TransactionState.RolledBack;

            }

            callerCancelled |= cancellationToken.IsCancellationRequested;

            if (callerCancelled)
            {

                OperationCanceledException cancellation =
                    new(cancellationToken);

                if (result.Recovery is not null)
                {

                    cancellation.Data[nameof(WorkspaceCommitRecovery)] = result.Recovery;

                }

                throw cancellation;

            }

            return result;

        }
        finally
        {

            _stateGate.Release();

        }

    }

    internal async Task<WorkspaceArtifactCleanupResult> MarkIrreversibleAsync(
        CancellationToken cancellationToken)
    {

        bool callerCancelled = cancellationToken.IsCancellationRequested;

        await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);

        try
        {

            if (_state == TransactionState.RolledBack)
            {

                throw new InvalidOperationException("A rolled-back workspace commit cannot become irreversible.");

            }

            if (_state == TransactionState.Irreversible)
            {

                return _irreversibleCleanupResult
                    ?? new WorkspaceArtifactCleanupResult(Complete: false, []);

            }

            using CancellationTokenSource cleanupDeadline = new(_options.CleanupTimeout);

            List<string> retained = [];

            try
            {

                foreach (MultiFileCommitCoordinator.StagedOperation operation in _staged)
                {

                    await TryDeleteArtifactAsync(
                        _workspaceRoot,
                        _options,
                        operation.TempAbsolutePath,
                        operation.TempRelativePath,
                        operation.TempFingerprint,
                        operation.TempIdentity,
                        retained,
                        cleanupDeadline.Token).ConfigureAwait(false);

                    await TryDeleteArtifactAsync(
                        _workspaceRoot,
                        _options,
                        operation.BackupAbsolutePath,
                        operation.BackupRelativePath,
                        operation.BackupFingerprint,
                        operation.BackupIdentity,
                        retained,
                        cleanupDeadline.Token).ConfigureAwait(false);

                    await TryDeleteArtifactAsync(
                        _workspaceRoot,
                        _options,
                        operation.DeleteArtifactAbsolutePath,
                        operation.DeleteArtifactRelativePath,
                        operation.DeleteArtifactFingerprint,
                        operation.DeleteArtifactIdentity,
                        retained,
                        cleanupDeadline.Token).ConfigureAwait(false);

                }

            }
            catch (OperationCanceledException)
            {

                retained.AddRange(
                    _staged.SelectMany(operation => operation.ExistingArtifactRelativePaths()));

            }

            retained = retained
                .Distinct(WorkspaceRelativePath.Comparer)
                .OrderBy(path => path, WorkspaceRelativePath.DeterministicComparer)
                .ToList();

            WorkspaceArtifactCleanupResult cleanupResult = new(
                retained.Count == 0,
                retained.AsReadOnly());

            _irreversibleCleanupResult = cleanupResult;

            _state = TransactionState.Irreversible;

            callerCancelled |= cancellationToken.IsCancellationRequested;

            if (callerCancelled)
            {

                OperationCanceledException cancellation =
                    new(cancellationToken);

                if (retained.Count > 0)
                {

                    cancellation.Data[nameof(WorkspaceCommitRecovery)] =
                        new WorkspaceCommitRecovery(
                            _staged
                                .Select(operation => operation.RelativePath)
                                .Distinct(WorkspaceRelativePath.Comparer)
                                .OrderBy(
                                    path => path,
                                    WorkspaceRelativePath.DeterministicComparer)
                                .ToArray(),
                            retained);

                }

                throw cancellation;

            }

            return cleanupResult;

        }
        finally
        {

            _stateGate.Release();

        }

    }

    internal static async Task<WorkspaceRollbackResult> RollbackCoreAsync(
        string workspaceRoot,
        IReadOnlyList<MultiFileCommitCoordinator.StagedOperation> staged,
        IReadOnlyList<MultiFileCommitCoordinator.CreatedDirectory> createdDirectories,
        MultiFileCommitCoordinatorOptions options,
        CancellationToken cleanupToken)
    {

        bool complete = true;

        for (int index = staged.Count - 1; index >= 0; index--)
        {

            MultiFileCommitCoordinator.StagedOperation operation = staged[index];

            if (!operation.Committed || operation.RolledBack)
            {

                continue;

            }

            cleanupToken.ThrowIfCancellationRequested();

            bool restored = await TryRollbackOperationAsync(
                workspaceRoot,
                operation,
                options,
                new WorkspaceRollbackStepContext(index, operation.RelativePath),
                cleanupToken).ConfigureAwait(false);

            complete &= restored;

            if (restored)
            {

                operation.RolledBack = true;

                options.AfterRollbackStep?.Invoke(
                    new WorkspaceRollbackStepContext(index, operation.RelativePath));

            }

        }

        List<string> retainedArtifacts = [];

        try
        {

            foreach (MultiFileCommitCoordinator.StagedOperation operation in staged)
            {

                if (operation.RetainRecoveryArtifacts)
                {

                    retainedArtifacts.AddRange(
                        operation.ExistingArtifactRelativePaths());

                    continue;

                }

                await TryDeleteArtifactAsync(
                    workspaceRoot,
                    options,
                    operation.TempAbsolutePath,
                    operation.TempRelativePath,
                    operation.TempFingerprint,
                    operation.TempIdentity,
                    retainedArtifacts,
                    cleanupToken).ConfigureAwait(false);

                if (!operation.Committed || operation.RolledBack)
                {

                    await TryDeleteArtifactAsync(
                        workspaceRoot,
                        options,
                        operation.BackupAbsolutePath,
                        operation.BackupRelativePath,
                        operation.BackupFingerprint,
                        operation.BackupIdentity,
                        retainedArtifacts,
                        cleanupToken).ConfigureAwait(false);

                    await TryDeleteArtifactAsync(
                        workspaceRoot,
                        options,
                        operation.DeleteArtifactAbsolutePath,
                        operation.DeleteArtifactRelativePath,
                        operation.DeleteArtifactFingerprint,
                        operation.DeleteArtifactIdentity,
                        retainedArtifacts,
                        cleanupToken).ConfigureAwait(false);

                }

            }

            CleanupCreatedDirectories(
                workspaceRoot,
                createdDirectories,
                options,
                retainedArtifacts,
                cleanupToken);

        }
        catch (OperationCanceledException cancellation)
        {

            cancellation.Data[RetainedArtifactPathsDataKey] =
                retainedArtifacts
                    .Distinct(WorkspaceRelativePath.Comparer)
                    .ToArray();

            throw;

        }

        complete &= retainedArtifacts.Count == 0;

        WorkspaceCommitRecovery? recovery = CaptureRecovery(
            staged,
            retainedArtifacts);

        return new WorkspaceRollbackResult(
            complete && recovery is null,
            recovery);

    }

    private static async Task<bool> TryRollbackOperationAsync(
        string workspaceRoot,
        MultiFileCommitCoordinator.StagedOperation operation,
        MultiFileCommitCoordinatorOptions options,
        WorkspaceRollbackStepContext context,
        CancellationToken cancellationToken)
    {

        if (operation.PostCommitFingerprint is not WorkspaceFileFingerprint postCommit)
        {

            return false;

        }

        WorkspaceFileFingerprint current;

        try
        {

            current = await WorkspaceFileFingerprintService.CaptureForMutationAsync(
                workspaceRoot,
                operation.RelativePath,
                cancellationToken).ConfigureAwait(false);

        }
        catch (WorkspaceMutationRejectedException)
        {

            return false;

        }

        if (current != postCommit)
        {

            return false;

        }

        options.BeforeRollbackMutation?.Invoke(context);

        try
        {

            current = await WorkspaceFileFingerprintService.CaptureForMutationAsync(
                workspaceRoot,
                operation.RelativePath,
                cancellationToken).ConfigureAwait(false);

        }
        catch (WorkspaceMutationRejectedException)
        {

            return false;

        }

        if (current != postCommit)
        {

            return false;

        }

        try
        {

            if (operation.IsDelete)
            {

                if (operation.DeleteArtifactAbsolutePath is null
                    || operation.DeleteArtifactRelativePath is null
                    || !File.Exists(operation.DeleteArtifactAbsolutePath))
                {

                    return false;

                }

                WorkspaceFileFingerprint currentDeletedArtifact =
                    await WorkspaceFileFingerprintService.CaptureForMutationAsync(
                        workspaceRoot,
                        operation.DeleteArtifactRelativePath,
                        cancellationToken).ConfigureAwait(false);

                if (!MatchesMovedArtifact(
                        currentDeletedArtifact,
                        operation.ExpectedFingerprint))
                {

                    return false;

                }

                File.Move(
                    operation.DeleteArtifactAbsolutePath,
                    operation.AbsolutePath,
                    overwrite: false);

                WorkspaceFileFingerprint restoredDelete =
                    await WorkspaceFileFingerprintService.CaptureForMutationAsync(
                        workspaceRoot,
                        operation.RelativePath,
                        cancellationToken).ConfigureAwait(false);

                bool restoredDeleteMatches = MatchesPromisedOriginal(
                    restoredDelete,
                    operation.ExpectedFingerprint);

                return restoredDeleteMatches;

            }

            if (!TryAllocateRollbackArtifact(workspaceRoot, operation))
            {

                return false;

            }

            File.Move(
                operation.AbsolutePath,
                operation.RollbackArtifactAbsolutePath!,
                overwrite: false);

            if (!FileHandleIdentityInterop.TryGetPathMetadata(
                    operation.RollbackArtifactAbsolutePath!,
                    out FileHandleMetadata rollbackMetadata)
                || rollbackMetadata.HardLinkCount != 1
                || rollbackMetadata.Kind != FileSystemObjectKind.RegularFile)
            {

                return false;

            }

            operation.RollbackArtifactIdentity = rollbackMetadata.Identity;

            operation.RollbackArtifactFingerprint =
                await WorkspaceFileFingerprintService.CaptureForMutationAsync(
                    workspaceRoot,
                    operation.RollbackArtifactRelativePath!,
                    cancellationToken).ConfigureAwait(false);

            if (operation.RollbackArtifactFingerprint != postCommit)
            {

                return false;

            }

            if (!operation.ExpectedFingerprint.Exists)
            {

                List<string> retained = [];

                await TryDeleteArtifactAsync(
                    workspaceRoot,
                    options,
                    operation.RollbackArtifactAbsolutePath,
                    operation.RollbackArtifactRelativePath,
                    operation.RollbackArtifactFingerprint,
                    operation.RollbackArtifactIdentity,
                    retained,
                    cancellationToken).ConfigureAwait(false);

                WorkspaceFileFingerprint missing =
                    await WorkspaceFileFingerprintService.CaptureForMutationAsync(
                        workspaceRoot,
                        operation.RelativePath,
                        cancellationToken).ConfigureAwait(false);

                return retained.Count == 0 && !missing.Exists;

            }

            if (operation.BackupAbsolutePath is null
                || operation.BackupRelativePath is null
                || operation.BackupFingerprint is not WorkspaceFileFingerprint backupFingerprint
                || !File.Exists(operation.BackupAbsolutePath))
            {

                return false;

            }

            if (!await WorkspaceFileFingerprintService.MatchesCurrentAsync(
                    workspaceRoot,
                    operation.BackupRelativePath,
                    backupFingerprint,
                    cancellationToken).ConfigureAwait(false))
            {

                return false;

            }

            File.Move(
                operation.BackupAbsolutePath,
                operation.AbsolutePath,
                overwrite: false);

            WorkspaceFileFingerprint restored =
                await WorkspaceFileFingerprintService.CaptureForMutationAsync(
                    workspaceRoot,
                    operation.RelativePath,
                    cancellationToken).ConfigureAwait(false);

            if (!MatchesPromisedOriginal(restored, operation.ExpectedFingerprint))
            {

                return false;

            }

            List<string> retainedRollbackArtifacts = [];

            await TryDeleteArtifactAsync(
                workspaceRoot,
                options,
                operation.RollbackArtifactAbsolutePath,
                operation.RollbackArtifactRelativePath,
                operation.RollbackArtifactFingerprint,
                operation.RollbackArtifactIdentity,
                retainedRollbackArtifacts,
                cancellationToken).ConfigureAwait(false);

            return retainedRollbackArtifacts.Count == 0;

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or WorkspaceMutationRejectedException)
        {

            return false;

        }

    }

    private static bool TryAllocateRollbackArtifact(
        string workspaceRoot,
        MultiFileCommitCoordinator.StagedOperation operation)
    {

        string directory = Path.GetDirectoryName(operation.AbsolutePath) ?? workspaceRoot;

        string fileName = Path.GetFileName(operation.AbsolutePath);

        for (int attempt = 0; attempt < 16; attempt++)
        {

            string candidate = Path.Combine(
                directory,
                $".{fileName}.arcanum-{Guid.NewGuid():N}.rollback");

            if (File.Exists(candidate)
                || Directory.Exists(candidate)
                || !WorkspacePathPolicy.RevalidatePathBeforeIo(workspaceRoot, candidate))
            {

                continue;

            }

            operation.RollbackArtifactAbsolutePath = candidate;

            operation.RollbackArtifactRelativePath =
                WorkspaceRelativePath.FromAbsolute(workspaceRoot, candidate);

            return true;

        }

        return false;

    }

    internal static bool TryRestoreNoOverwrite(
        string artifactAbsolutePath,
        string originalAbsolutePath)
    {

        try
        {

            if (!File.Exists(artifactAbsolutePath)
                || File.Exists(originalAbsolutePath)
                || Directory.Exists(originalAbsolutePath))
            {

                return false;

            }

            File.Move(
                artifactAbsolutePath,
                originalAbsolutePath,
                overwrite: false);

            return true;

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException)
        {

            return false;

        }

    }

    private static bool TryRestoreDirectoryNoOverwrite(
        string artifactAbsolutePath,
        string originalAbsolutePath)
    {

        try
        {

            if (!Directory.Exists(artifactAbsolutePath)
                || File.Exists(originalAbsolutePath)
                || Directory.Exists(originalAbsolutePath))
            {

                return false;

            }

            Directory.Move(artifactAbsolutePath, originalAbsolutePath);

            return true;

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException)
        {

            return false;

        }

    }

    private static bool TryAllocateRemovalArtifact(
        string workspaceRoot,
        string originalAbsolutePath,
        string extension,
        out string? artifactAbsolutePath,
        out string? artifactRelativePath)
    {

        artifactAbsolutePath = null;

        artifactRelativePath = null;

        string directory = Path.GetDirectoryName(originalAbsolutePath) ?? workspaceRoot;

        string fileName = Path.GetFileName(originalAbsolutePath).TrimStart('.');

        if (fileName.Length == 0)
        {

            fileName = "artifact";

        }

        for (int attempt = 0; attempt < 16; attempt++)
        {

            string candidate = Path.Combine(
                directory,
                $".{fileName}.arcanum-{Guid.NewGuid():N}.{extension}");

            if (File.Exists(candidate)
                || Directory.Exists(candidate)
                || !WorkspacePathPolicy.RevalidatePathBeforeIo(workspaceRoot, candidate))
            {

                continue;

            }

            artifactAbsolutePath = candidate;

            artifactRelativePath =
                WorkspaceRelativePath.FromAbsolute(workspaceRoot, candidate);

            return true;

        }

        return false;

    }

    private static bool MatchesPromisedOriginal(
        WorkspaceFileFingerprint restored,
        WorkspaceFileFingerprint expected) =>
        restored.Exists
        && restored.HardLinkCount == 1
        && restored.Length == expected.Length
        && string.Equals(
            restored.ContentSha256,
            expected.ContentSha256,
            StringComparison.Ordinal)
        && (OperatingSystem.IsWindows() || restored.UnixMode == expected.UnixMode);

    internal static bool MatchesMovedArtifact(
        WorkspaceFileFingerprint moved,
        WorkspaceFileFingerprint expected) =>
        moved.Exists
        && moved.HardLinkCount == 1
        && FileHandleIdentity.IdentitiesMatch(
            moved.Identity,
            expected.Identity)
        && moved.Length == expected.Length
        && string.Equals(
            moved.ContentSha256,
            expected.ContentSha256,
            StringComparison.Ordinal)
        && (OperatingSystem.IsWindows() || moved.UnixMode == expected.UnixMode);

    private static async Task TryDeleteArtifactAsync(
        string workspaceRoot,
        MultiFileCommitCoordinatorOptions options,
        string? absolutePath,
        string? relativePath,
        WorkspaceFileFingerprint? expectedFingerprint,
        FileHandleIdentity? expectedIdentity,
        List<string> retained,
        CancellationToken cancellationToken)
    {

        if (absolutePath is null
            || relativePath is null
            || !File.Exists(absolutePath))
        {

            return;

        }

        if (expectedFingerprint is null && expectedIdentity is null)
        {

            retained.Add(relativePath);

            return;

        }

        string? removalAbsolutePath = null;

        string? removalRelativePath = null;

        try
        {

            FileHandleIdentity identityToDelete;

            if (expectedFingerprint is WorkspaceFileFingerprint expected)
            {

                WorkspaceFileFingerprint current =
                    await WorkspaceFileFingerprintService.CaptureForMutationAsync(
                        workspaceRoot,
                        relativePath,
                        cancellationToken).ConfigureAwait(false);

                if (current != expected)
                {

                    retained.Add(relativePath);

                    return;

                }

                identityToDelete = expected.Identity;

            }
            else
            {

                identityToDelete = expectedIdentity!.Value;

            }

            if (!FileHandleIdentityInterop.TryGetPathMetadata(
                    absolutePath,
                    out FileHandleMetadata currentMetadata)
                || currentMetadata.HardLinkCount != 1
                || currentMetadata.Kind != FileSystemObjectKind.RegularFile
                || !FileHandleIdentity.IdentitiesMatch(
                    identityToDelete,
                    currentMetadata.Identity))
            {

                retained.Add(relativePath);

                return;

            }

            if (!TryAllocateRemovalArtifact(
                    workspaceRoot,
                    absolutePath,
                    "cleanup",
                    out removalAbsolutePath,
                    out removalRelativePath))
            {

                retained.Add(relativePath);

                return;

            }

            options.BeforeFileRemovalRename?.Invoke(relativePath);

            File.Move(
                absolutePath,
                removalAbsolutePath!,
                overwrite: false);

            retained.Add(removalRelativePath!);

            options.AfterFileRemovalRename?.Invoke(
                new WorkspaceFileRemovalContext(
                    relativePath,
                    removalRelativePath!));

            bool movedIdentityMatches;

            if (expectedFingerprint is WorkspaceFileFingerprint expectedMoved)
            {

                WorkspaceFileFingerprint moved =
                    await WorkspaceFileFingerprintService.CaptureForMutationAsync(
                        workspaceRoot,
                        removalRelativePath!,
                        cancellationToken).ConfigureAwait(false);

                movedIdentityMatches = MatchesMovedArtifact(
                    moved,
                    expectedMoved);

            }
            else
            {

                movedIdentityMatches =
                    FileHandleIdentityInterop.TryGetPathMetadata(
                        removalAbsolutePath!,
                        out FileHandleMetadata movedMetadata)
                    && movedMetadata.HardLinkCount == 1
                    && movedMetadata.Kind
                        == FileSystemObjectKind.RegularFile
                    && FileHandleIdentity.IdentitiesMatch(
                        identityToDelete,
                        movedMetadata.Identity);

            }

            if (!movedIdentityMatches)
            {

                bool restored = TryRestoreNoOverwrite(
                    removalAbsolutePath!,
                    absolutePath);

                _ = retained.Remove(removalRelativePath!);

                retained.Add(restored ? relativePath : removalRelativePath!);

                return;

            }

            if (!FileHandleIdentityInterop.TryGetPathMetadata(
                    removalAbsolutePath!,
                    out FileHandleMetadata beforeDeleteMetadata)
                || beforeDeleteMetadata.HardLinkCount != 1
                || beforeDeleteMetadata.Kind != FileSystemObjectKind.RegularFile
                || !FileHandleIdentity.IdentitiesMatch(
                    identityToDelete,
                    beforeDeleteMetadata.Identity))
            {

                bool restored = TryRestoreNoOverwrite(
                    removalAbsolutePath!,
                    absolutePath);

                _ = retained.Remove(removalRelativePath!);

                retained.Add(restored ? relativePath : removalRelativePath!);

                return;

            }

            File.Delete(removalAbsolutePath!);

            if (File.Exists(removalAbsolutePath))
            {

                retained.Add(removalRelativePath!);

            }
            else
            {

                _ = retained.Remove(removalRelativePath!);

            }

        }
        catch (OperationCanceledException)
        {

            throw;

        }
        catch (Exception exception) when (
            exception is WorkspaceMutationRejectedException
                or IOException
                or UnauthorizedAccessException)
        {

            retained.Add(
                removalAbsolutePath is not null && File.Exists(removalAbsolutePath)
                    ? removalRelativePath!
                    : relativePath);

        }

    }

    private static void CleanupCreatedDirectories(
        string workspaceRoot,
        IReadOnlyList<MultiFileCommitCoordinator.CreatedDirectory> createdDirectories,
        MultiFileCommitCoordinatorOptions options,
        List<string> retained,
        CancellationToken cancellationToken)
    {

        foreach (MultiFileCommitCoordinator.CreatedDirectory directory in createdDirectories
            .OrderByDescending(
                item => item.RelativePath.Count(character => character == '/'))
            .ThenByDescending(
                item => item.RelativePath,
                WorkspaceRelativePath.DeterministicComparer))
        {

            cancellationToken.ThrowIfCancellationRequested();

            if (!Directory.Exists(directory.AbsolutePath))
            {

                continue;

            }

            string? removalAbsolutePath = null;

            string? removalRelativePath = null;

            try
            {

                if (directory.Identity is not FileHandleIdentity expectedIdentity
                    || !WorkspaceFileFingerprintService.TryGetDirectoryIdentity(
                        directory.AbsolutePath,
                        out FileHandleIdentity currentIdentity)
                    || !FileHandleIdentity.IdentitiesMatch(
                        expectedIdentity,
                        currentIdentity))
                {

                    retained.Add(directory.RelativePath);

                    continue;

                }

                if (!TryAllocateRemovalArtifact(
                        workspaceRoot,
                        directory.AbsolutePath,
                        "directory-cleanup",
                        out removalAbsolutePath,
                        out removalRelativePath))
                {

                    retained.Add(directory.RelativePath);

                    continue;

                }

                options.BeforeDirectoryRemovalRename?.Invoke(directory.RelativePath);

                Directory.Move(
                    directory.AbsolutePath,
                    removalAbsolutePath!);

                retained.Add(removalRelativePath!);

                if (!WorkspaceFileFingerprintService.TryGetDirectoryIdentity(
                        removalAbsolutePath!,
                        out FileHandleIdentity movedIdentity)
                    || !FileHandleIdentity.IdentitiesMatch(
                        expectedIdentity,
                        movedIdentity))
                {

                    bool restored = TryRestoreDirectoryNoOverwrite(
                        removalAbsolutePath!,
                        directory.AbsolutePath);

                    _ = retained.Remove(removalRelativePath!);

                    retained.Add(
                        restored
                            ? directory.RelativePath
                            : removalRelativePath!);

                    continue;

                }

                options.AfterDirectoryRemovalRename?.Invoke(
                    new WorkspaceDirectoryRemovalContext(
                        directory.RelativePath,
                        removalRelativePath!));

                if (!WorkspaceFileFingerprintService.TryGetDirectoryIdentity(
                        removalAbsolutePath!,
                        out FileHandleIdentity beforeDeleteIdentity)
                    || !FileHandleIdentity.IdentitiesMatch(
                        expectedIdentity,
                        beforeDeleteIdentity)
                    || Directory.EnumerateFileSystemEntries(removalAbsolutePath!).Any())
                {

                    bool restored = TryRestoreDirectoryNoOverwrite(
                        removalAbsolutePath!,
                        directory.AbsolutePath);

                    _ = retained.Remove(removalRelativePath!);

                    retained.Add(
                        restored
                            ? directory.RelativePath
                            : removalRelativePath!);

                    continue;

                }

                Directory.Delete(removalAbsolutePath!, recursive: false);

                if (!Directory.Exists(removalAbsolutePath))
                {

                    _ = retained.Remove(removalRelativePath!);

                }

            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException)
            {

                // The directory gained content or was replaced between revalidation and deletion.
                // Retaining it is the fail-closed behavior.
                retained.Add(
                    removalAbsolutePath is not null
                        && Directory.Exists(removalAbsolutePath)
                            ? removalRelativePath!
                            : directory.RelativePath);

            }

        }

    }

    internal static WorkspaceCommitRecovery? CaptureRecovery(
        IReadOnlyList<MultiFileCommitCoordinator.StagedOperation> staged,
        IReadOnlyList<string>? retainedArtifacts = null)
    {

        string[] artifacts = (retainedArtifacts ?? [])
            .Concat(
                staged.SelectMany(operation => operation.ExistingArtifactRelativePaths()))
            .Distinct(WorkspaceRelativePath.Comparer)
            .OrderBy(path => path, WorkspaceRelativePath.DeterministicComparer)
            .ToArray();

        string[] affected = staged
            .Where(operation => operation.Committed && !operation.RolledBack)
            .Select(operation => operation.RelativePath)
            .Distinct(WorkspaceRelativePath.Comparer)
            .OrderBy(path => path, WorkspaceRelativePath.DeterministicComparer)
            .ToArray();

        if (artifacts.Length == 0 && affected.Length == 0)
        {

            return null;

        }

        return new WorkspaceCommitRecovery(affected, artifacts);

    }

    internal static WorkspaceCommitRecovery? CaptureCancellationRecovery(
        OperationCanceledException cancellation,
        IReadOnlyList<MultiFileCommitCoordinator.StagedOperation> staged) =>
        CaptureRecovery(
            staged,
            cancellation.Data[RetainedArtifactPathsDataKey]
                as IReadOnlyList<string>);

    private enum TransactionState
    {
        Reversible,
        RolledBack,
        Irreversible,
    }

}
