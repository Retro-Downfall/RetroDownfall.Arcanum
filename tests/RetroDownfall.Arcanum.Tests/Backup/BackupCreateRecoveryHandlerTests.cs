using System.Text.Json;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Backup;

public sealed class BackupCreateRecoveryHandlerTests : IDisposable
{

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-backup-recovery-" + Guid.NewGuid().ToString("N"));

    public BackupCreateRecoveryHandlerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {

        if (Directory.Exists(_root))
        {

            Directory.Delete(_root, recursive: true);

        }

    }

    [Fact]
    public async Task RecoverAsync_DeletesOnlyTheCheckpointOwnedStagingDirectoryAndAbandons()
    {

        string outputPath = Path.Combine(_root, "portable.arcbackup");

        string stagingPath = CreateStagingDirectory(Path.GetDirectoryName(outputPath)!);

        string stagedFile = Path.Combine(stagingPath, "grimoire", "arcanum.db");

        Directory.CreateDirectory(Path.GetDirectoryName(stagedFile)!);

        await File.WriteAllTextAsync(stagedFile, "encrypted snapshot");

        await File.WriteAllTextAsync(outputPath, "published archive");

        BackupOperationCheckpoint checkpoint = CreateCheckpoint(
            outputPath,
            Capture(stagingPath));

        BackupCreateRecoveryHandler handler = new();

        LongRunningOperationRecoveryResult result = await handler.RecoverAsync(
            Operation(checkpoint),
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Abandoned, result.State);

        Assert.False(Directory.Exists(stagingPath));

        Assert.Equal("published archive", await File.ReadAllTextAsync(outputPath));

        Assert.Empty(Directory.EnumerateDirectories(_root, ".arcanum-cleanup-*"));

    }

    [Fact]
    public async Task RecoverAsync_WhenStagingIsAlreadyMissing_AbandonsIdempotently()
    {

        string outputPath = Path.Combine(_root, "portable.arcbackup");

        string stagingPath = CreateStagingDirectory(_root);

        IdentityOwnedFileSystemArtifact captured = Capture(stagingPath);

        Directory.Delete(stagingPath);

        BackupCreateRecoveryHandler handler = new();

        LongRunningOperationRecoveryResult result = await handler.RecoverAsync(
            Operation(CreateCheckpoint(outputPath, captured)),
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Abandoned, result.State);

        Assert.Null(result.ErrorCode);

    }

    [Fact]
    public async Task RecoverAsync_RejectsAStagingPathOutsideTheOutputParent()
    {

        string outputParent = Path.Combine(_root, "output");

        string unrelatedParent = Path.Combine(_root, "unrelated");

        Directory.CreateDirectory(outputParent);

        Directory.CreateDirectory(unrelatedParent);

        string outputPath = Path.Combine(outputParent, "portable.arcbackup");

        string stagingPath = CreateStagingDirectory(unrelatedParent);

        BackupCreateRecoveryHandler handler = new();

        LongRunningOperationRecoveryResult result = await handler.RecoverAsync(
            Operation(CreateCheckpoint(outputPath, Capture(stagingPath))),
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, result.State);

        Assert.Equal(LongRunningOperationErrorCodes.CorruptCheckpoint, result.ErrorCode);

        Assert.True(Directory.Exists(stagingPath));

    }

    [Fact]
    public async Task RecoverAsync_WhenStagingIdentityChanged_PreservesTheReplacement()
    {

        string outputPath = Path.Combine(_root, "portable.arcbackup");

        string stagingPath = CreateStagingDirectory(_root);

        IdentityOwnedFileSystemArtifact captured = Capture(stagingPath);

        string movedPath = stagingPath + ".original";

        Directory.Move(stagingPath, movedPath);

        Directory.CreateDirectory(stagingPath);

        string replacement = Path.Combine(stagingPath, "replacement.txt");

        await File.WriteAllTextAsync(replacement, "do not delete");

        BackupCreateRecoveryHandler handler = new();

        LongRunningOperationRecoveryResult result = await handler.RecoverAsync(
            Operation(CreateCheckpoint(outputPath, captured)),
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, result.State);

        Assert.Equal(BackupCreateRecoveryHandler.StagingCleanupFailed, result.ErrorCode);

        Assert.Equal("do not delete", await File.ReadAllTextAsync(replacement));

        Assert.True(Directory.Exists(movedPath));

    }

    [Fact]
    public async Task RecoverAsync_WhenExistingStagingHasNoRecordedIdentity_PreservesIt()
    {

        string outputPath = Path.Combine(_root, "portable.arcbackup");

        string stagingPath = CreateStagingDirectory(_root);

        await File.WriteAllTextAsync(
            Path.Combine(stagingPath, "unproven.txt"),
            "do not delete");

        BackupOperationCheckpoint checkpoint = CreateCheckpoint(
            outputPath,
            Capture(stagingPath)) with
        {

            StagingVolumeId = null,

            StagingFileId = null,

        };

        BackupCreateRecoveryHandler handler = new();

        LongRunningOperationRecoveryResult result = await handler.RecoverAsync(
            Operation(checkpoint),
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, result.State);

        Assert.Equal(BackupCreateRecoveryHandler.StagingCleanupFailed, result.ErrorCode);

        Assert.True(File.Exists(Path.Combine(stagingPath, "unproven.txt")));

    }

    [Fact]
    public async Task RecoverAsync_WithMalformedCheckpoint_RequiresOperatorAttention()
    {

        BackupCreateRecoveryHandler handler = new();

        LongRunningOperation operation = Operation(checkpoint: null) with
        {

            CheckpointPayload = [0xFF],

        };

        LongRunningOperationRecoveryResult result = await handler.RecoverAsync(
            operation,
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, result.State);

        Assert.Equal(LongRunningOperationErrorCodes.CorruptCheckpoint, result.ErrorCode);

    }

    private static string CreateStagingDirectory(string parent)
    {

        string path = Path.Combine(
            parent,
            ".arcanum-backup-stage-" + Guid.NewGuid().ToString("N"));

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(path);

        return path;

    }

    private static IdentityOwnedFileSystemArtifact Capture(string path)
    {

        Assert.True(
            IdentityOwnedFileSystemCleanup.TryCapturePath(
                path,
                FileSystemObjectKind.Directory,
                out IdentityOwnedFileSystemArtifact artifact));

        return artifact;

    }

    private static BackupOperationCheckpoint CreateCheckpoint(
        string outputPath,
        IdentityOwnedFileSystemArtifact staging) =>
        new(
            Version: 2,
            BackupScope.Full,
            SessionId: null,
            Include: [],
            Exclude: [],
            Path.GetFullPath(outputPath),
            staging.Path,
            Overwrite: false,
            Phase: "inventory-complete",
            StagingVolumeId: staging.Metadata.Identity.VolumeId,
            StagingFileId: staging.Metadata.Identity.FileId);

    private static LongRunningOperation Operation(BackupOperationCheckpoint? checkpoint)
    {

        byte[]? payload = checkpoint is null
            ? null
            : JsonSerializer.SerializeToUtf8Bytes(
                checkpoint,
                BackupJsonContext.Default.BackupOperationCheckpoint);

        return new LongRunningOperation(
            Id: Guid.NewGuid(),
            Kind: LongRunningOperationKinds.BackupCreate,
            State: LongRunningOperationState.Running,
            RecoveryPolicy: LongRunningOperationRecoveryPolicy.AbandonSafely,
            RootOperationId: null,
            ParentOperationId: null,
            SessionId: null,
            RunId: null,
            InferenceRunId: null,
            BudgetReservationId: null,
            IdempotencyClaimId: null,
            CreatedAt: DateTimeOffset.UtcNow.AddMinutes(-10),
            StartedAt: DateTimeOffset.UtcNow.AddMinutes(-9),
            HeartbeatAt: DateTimeOffset.UtcNow.AddMinutes(-8),
            CompletedAt: null,
            LeaseOwner: "dead-owner",
            LeaseExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            AttemptCount: 1,
            CheckpointVersion: 2,
            CheckpointPayload: payload,
            CheckpointReference: checkpoint?.OutputPath,
            PublicSummary: "Backup staging requires recovery.",
            TerminalErrorCode: null,
            Revision: 3);

    }

}
