using System.Text.Json;

using RetroDownfall.Arcanum.Core.Operations;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

internal sealed class BackupCreateRecoveryHandler : ILongRunningOperationRecoveryHandler
{

    internal const string StagingCleanupFailed = "backup.staging_cleanup_failed";

    private const string StagingPrefix = ".arcanum-backup-stage-";

    public string Kind => LongRunningOperationKinds.BackupCreate;

    public int SupportedCheckpointVersion => 2;

    public Task<LongRunningOperationRecoveryResult> RecoverAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(operation);

        cancellationToken.ThrowIfCancellationRequested();

        if (operation.CheckpointVersion == 0
            && operation.CheckpointPayload is null)
        {

            return Task.FromResult(LongRunningOperationRecoveryResult.Abandoned());

        }

        if (operation.CheckpointVersion != SupportedCheckpointVersion
            || operation.CheckpointPayload is null)
        {

            return CorruptCheckpoint();

        }

        BackupOperationCheckpoint? checkpoint;

        try
        {

            checkpoint = JsonSerializer.Deserialize(
                operation.CheckpointPayload,
                BackupJsonContext.Default.BackupOperationCheckpoint);

        }
        catch (Exception exception) when (
            exception is JsonException
                or NotSupportedException)
        {

            return CorruptCheckpoint();

        }

        if (checkpoint is null
            || checkpoint.Version != SupportedCheckpointVersion
            || !TryValidatePaths(
                checkpoint,
                operation.CheckpointReference,
                out string stagingPath))
        {

            return CorruptCheckpoint();

        }

        if (!PathEntryExists(stagingPath))
        {

            return Task.FromResult(LongRunningOperationRecoveryResult.Abandoned());

        }

        if (!checkpoint.StagingVolumeId.HasValue
            || !checkpoint.StagingFileId.HasValue)
        {

            return CleanupFailed();

        }

        cancellationToken.ThrowIfCancellationRequested();

        if (OwnedTemporaryDirectory.TryDelete(
                stagingPath,
                checkpoint.StagingVolumeId.Value,
                checkpoint.StagingFileId.Value)
            || !PathEntryExists(stagingPath))
        {

            return Task.FromResult(LongRunningOperationRecoveryResult.Abandoned());

        }

        return CleanupFailed();

    }

    private static bool TryValidatePaths(
        BackupOperationCheckpoint checkpoint,
        string? checkpointReference,
        out string stagingPath)
    {

        stagingPath = string.Empty;

        if (string.IsNullOrWhiteSpace(checkpoint.OutputPath)
            || string.IsNullOrWhiteSpace(checkpoint.StagingRoot)
            || !Path.IsPathFullyQualified(checkpoint.OutputPath)
            || !Path.IsPathFullyQualified(checkpoint.StagingRoot))
        {

            return false;

        }

        string outputPath;

        try
        {

            outputPath = Path.GetFullPath(checkpoint.OutputPath);

            stagingPath = Path.GetFullPath(checkpoint.StagingRoot);

        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {

            return false;

        }

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!string.Equals(outputPath, checkpoint.OutputPath, comparison)
            || !string.Equals(stagingPath, checkpoint.StagingRoot, comparison)
            || (checkpointReference is not null
                && !string.Equals(outputPath, checkpointReference, comparison)))
        {

            return false;

        }

        string? outputParent = Path.GetDirectoryName(outputPath);

        string? stagingParent = Path.GetDirectoryName(stagingPath);

        if (outputParent is null
            || stagingParent is null
            || !string.Equals(outputParent, stagingParent, comparison))
        {

            return false;

        }

        string stagingName = Path.GetFileName(stagingPath);

        if (!stagingName.StartsWith(StagingPrefix, StringComparison.Ordinal))
        {

            return false;

        }

        string suffix = stagingName[StagingPrefix.Length..];

        return suffix.Length == 32 && IsLowerHex(suffix);

    }

    private static bool IsLowerHex(string value)
    {

        foreach (char character in value)
        {

            if (character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f'))
            {

                return false;

            }

        }

        return true;

    }

    private static bool PathEntryExists(string path)
    {

        string? parent = Path.GetDirectoryName(path);

        if (parent is null || !Directory.Exists(parent))
        {

            return false;

        }

        string name = Path.GetFileName(path);

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        try
        {

            return Directory
                .EnumerateFileSystemEntries(parent, name, SearchOption.TopDirectoryOnly)
                .Any(candidate => string.Equals(
                    Path.GetFullPath(candidate),
                    path,
                    comparison));

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException)
        {

            return true;

        }

    }

    private static Task<LongRunningOperationRecoveryResult> CorruptCheckpoint() =>
        Task.FromResult(
            LongRunningOperationRecoveryResult.RequiresAttention(
                LongRunningOperationErrorCodes.CorruptCheckpoint));

    private static Task<LongRunningOperationRecoveryResult> CleanupFailed() =>
        Task.FromResult(
            LongRunningOperationRecoveryResult.RequiresAttention(
                StagingCleanupFailed));

}
