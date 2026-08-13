using System.Text.Json;

using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Infrastructure.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

internal sealed record InstallationResetActiveRecord(
    int Version,
    Guid OperationId,
    string PlanId,
    InstallationResetScope Scope,
    DataRetentionWorkspaceBinding? Workspace,
    InstallationResetAcceptedBinding AcceptedBinding,
    InstallationResetPhase Phase,
    bool PointOfNoReturn,
    long RowsDeleted,
    long FilesDeleted,
    long EstimatedBytesDeleted,
    InstallationResetCredentialResult[] CredentialResults,
    string? LastErrorCode);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false)]
[JsonSerializable(typeof(InstallationResetActiveRecord))]
internal sealed partial class InstallationResetActiveJsonContext : JsonSerializerContext;

internal interface IInstallationResetActiveStore
{

    Task<Result<InstallationResetActiveRecord?>> ReadAsync(
        CancellationToken cancellationToken);

    Task<Result> WriteAsync(
        InstallationResetActiveRecord record,
        CancellationToken cancellationToken);

    Task<Result> RetireAsync(
        Guid operationId,
        CancellationToken cancellationToken);

}

internal sealed class InstallationResetActiveStore : IInstallationResetActiveStore
{

    public const int CurrentVersion = 1;

    public const int MaxBytes = 64 * 1024;

    public InstallationResetActiveStore(string guardedRoot)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(guardedRoot);

        string lockPath = ArcanumMaintenanceLock.LockPathFor(guardedRoot);

        string parent = Path.GetDirectoryName(lockPath)!;

        string name = Path.GetFileNameWithoutExtension(lockPath);

        ActivePath = Path.Combine(parent, name + ".factory-reset.active.json");

    }

    public string ActivePath { get; }

    public async Task<Result<InstallationResetActiveRecord?>> ReadAsync(
        CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(ActivePath) && !Directory.Exists(ActivePath))
        {

            return Result<InstallationResetActiveRecord?>.Success(null);

        }

        using SecureFileReadResult read = await SecureFileReader
            .ReadBytesAsync(ActivePath, MaxBytes, cancellationToken)
            .ConfigureAwait(false);

        if (read.Status is not SecureFileReadStatus.Success)
        {

            return Failure<InstallationResetActiveRecord?>(
                "The installation reset active record could not be read safely.");

        }

        try
        {

            InstallationResetActiveRecord? record = JsonSerializer.Deserialize(
                read.Bytes.Span,
                InstallationResetActiveJsonContext.Default.InstallationResetActiveRecord);

            if (record is null || !IsValid(record))
            {

                return Failure<InstallationResetActiveRecord?>(
                    "The installation reset active record is invalid.");

            }

            return Result<InstallationResetActiveRecord?>.Success(record);

        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException)
        {

            return Failure<InstallationResetActiveRecord?>(
                "The installation reset active record is malformed.");

        }

    }

    public async Task<Result> WriteAsync(
        InstallationResetActiveRecord record,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(record);

        cancellationToken.ThrowIfCancellationRequested();

        if (!IsValid(record))
        {

            return Failure("The installation reset active record is invalid.");

        }

        Result<InstallationResetActiveRecord?> current = await ReadAsync(
            cancellationToken).ConfigureAwait(false);

        if (current.IsFailure)
        {

            return Result.Failure(current.Error);

        }

        if (current.Value is { } existing
            && (existing.OperationId != record.OperationId
                || !string.Equals(existing.PlanId, record.PlanId, StringComparison.Ordinal)
                || !string.Equals(
                    existing.AcceptedBinding.BindingId,
                    record.AcceptedBinding.BindingId,
                    StringComparison.Ordinal)))
        {

            return Failure(
                "A different installation reset owns the active record.",
                ErrorCodes.Data.ResetInProgress);

        }

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            record,
            InstallationResetActiveJsonContext.Default.InstallationResetActiveRecord);

        if (payload.Length > MaxBytes)
        {

            return Failure("The installation reset active record exceeds 64 KiB.");

        }

        string parent = Path.GetDirectoryName(ActivePath)!;

        try
        {

            if (!SecureFilePermissions.TryEnsureOwnerOnlyDirectoryExistsStrict(parent))
            {

                return Failure("The installation reset control directory is unavailable.");

            }

            string temporaryPath = ActivePath + ".tmp." + Guid.NewGuid().ToString("N");

            AtomicReplaceStatus status = await AtomicFile.ReplaceAsync(
                    ActivePath,
                    temporaryPath,
                    async (stream, token) =>
                    {

                        await stream.WriteAsync(payload, token).ConfigureAwait(false);

                    },
                    cancellationToken,
                    afterReplace: () =>
                        SecureFilePermissions.TryApplyOwnerOnlyFileStrict(ActivePath))
                .ConfigureAwait(false);

            return status is AtomicReplaceStatus.Succeeded
                ? Result.Success()
                : Failure(
                    status is AtomicReplaceStatus.ReplacedButUnverified
                        ? "The installation reset active record requires recovery."
                        : "The installation reset active record could not be published.",
                    status is AtomicReplaceStatus.ReplacedButUnverified
                        ? ErrorCodes.Data.RecoveryRequired
                        : ErrorCodes.Data.ControlPathUnavailable);

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {

            return Failure("The installation reset control path is unavailable.");

        }

    }

    public async Task<Result> RetireAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {

        Result<InstallationResetActiveRecord?> read = await ReadAsync(cancellationToken)
            .ConfigureAwait(false);

        if (read.IsFailure)
        {

            return Result.Failure(read.Error);

        }

        InstallationResetActiveRecord? record = read.Value;

        if (record is null)
        {

            return Result.Success();

        }

        if (record.OperationId != operationId)
        {

            return Failure(
                "A different installation reset owns the active record.",
                ErrorCodes.Data.ResetInProgress);

        }

        try
        {

            if (!IdentityOwnedFileSystemCleanup.TryCapturePath(
                    ActivePath,
                    FileSystemObjectKind.RegularFile,
                    out IdentityOwnedFileSystemArtifact artifact)
                || !IdentityOwnedFileSystemCleanup.TryDelete(artifact))
            {

                return Failure("The installation reset active record could not be retired.");

            }

            return Result.Success();

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {

            return Failure("The installation reset active record could not be retired.");

        }

    }

    private static Result Failure(
        string message,
        string code = ErrorCodes.Data.ControlPathUnavailable) =>
        Result.Failure(new Error(code, message));

    private static Result<T> Failure<T>(string message) =>
        Result<T>.Failure(new Error(ErrorCodes.Data.ControlPathUnavailable, message));

    private static bool IsValid(InstallationResetActiveRecord record) =>
        record.Version == CurrentVersion
        && record.OperationId != Guid.Empty
        && !string.IsNullOrWhiteSpace(record.PlanId)
        && !string.IsNullOrWhiteSpace(record.AcceptedBinding.BindingId);

}
