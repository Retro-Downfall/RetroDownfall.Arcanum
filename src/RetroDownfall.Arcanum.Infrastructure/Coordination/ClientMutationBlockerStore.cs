using System.Text.Json;

using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Infrastructure.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Coordination;

internal enum ClientMutationBlockerKind : byte
{

    InstallationReset,

    ReplacementRestore,

}

internal sealed record ClientMutationBlockerRecord(
    int Version,
    Guid BlockerId,
    ClientMutationBlockerKind Kind,
    InstallationResetScope? Scope,
    string? PlanId,
    Guid? OperationId);

internal sealed record ClientMutationBlockerPublication(
    ClientMutationBlockerRecord Record,
    FileHandleIdentity Identity);

internal sealed class ClientMutationBlockerStoreOptions
{

    internal Action? BeforePublishForTests { get; init; }

    internal Action? BeforeAtomicPublishForTests { get; init; }

    internal Action? AfterPublishMoveBeforeVerifyForTests { get; init; }

}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ClientMutationBlockerRecord))]
internal sealed partial class ClientMutationBlockerJsonContext : JsonSerializerContext;

internal sealed class ClientMutationBlockerStore
{

    internal const int CurrentVersion = 1;

    internal const int MaxBytes = 16 * 1024;

    private readonly string _guardedRoot;

    private readonly ClientMutationBlockerStoreOptions _options;

    internal ClientMutationBlockerStore(
        string guardedRoot,
        ClientMutationBlockerStoreOptions? options = null)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(guardedRoot);

        _guardedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(guardedRoot));

        _options = options ?? new ClientMutationBlockerStoreOptions();

        string lockPath = ArcanumClientMutationLock.LockPathFor(_guardedRoot);

        string parent = Path.GetDirectoryName(lockPath)!;

        string name = Path.GetFileNameWithoutExtension(lockPath);

        BlockerPath = Path.Combine(parent, name + ".blocked.json");

    }

    internal string BlockerPath { get; }

    internal async Task<Result<ClientMutationBlockerPublication?>> InspectAsync(
        CancellationToken cancellationToken = default)
    {

        cancellationToken.ThrowIfCancellationRequested();

        Result<NoFollowPathTopologyKind> topology =
            NoFollowPathTopology.Classify(BlockerPath);

        if (topology.IsFailure)
        {

            return Failure<ClientMutationBlockerPublication?>(
                "The client-mutation blocker topology could not be classified safely.");

        }

        if (topology.Value is NoFollowPathTopologyKind.Absent)
        {

            return Result<ClientMutationBlockerPublication?>.Success(null);

        }

        if (topology.Value is not NoFollowPathTopologyKind.RegularFile
            || !FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                BlockerPath,
                out FileHandleMetadata named)
            || named.Kind is not FileSystemObjectKind.RegularFile
            || named.HardLinkCount != 1
            || !SecureFilePermissions.HasOwnerOnlyPosture(
                BlockerPath,
                isDirectory: false))
        {

            return Failure<ClientMutationBlockerPublication?>(
                "The client-mutation blocker identity or owner-only permissions are unsafe.");

        }

        try
        {

            using SecureFileReadResult read = await SecureFileReader
                .ReadBytesAsync(
                    BlockerPath,
                    MaxBytes,
                    cancellationToken,
                    named.Identity)
                .ConfigureAwait(false);

            if (read.Status is not SecureFileReadStatus.Success)
            {

                return Failure<ClientMutationBlockerPublication?>(
                    "The client-mutation blocker could not be read safely.");

            }

            ClientMutationBlockerRecord? record = JsonSerializer.Deserialize(
                read.Bytes.Span,
                ClientMutationBlockerJsonContext.Default.ClientMutationBlockerRecord);

            if (!IsValid(record))
            {

                return Failure<ClientMutationBlockerPublication?>(
                    "The client-mutation blocker is invalid.");

            }

            return Result<ClientMutationBlockerPublication?>.Success(
                new ClientMutationBlockerPublication(
                    record!,
                    read.Metadata.Identity));

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or NotSupportedException)
        {

            return Failure<ClientMutationBlockerPublication?>(
                "The client-mutation blocker could not be read safely.");

        }

    }

    internal async Task<Result<ClientMutationBlockerPublication>> PublishAsync(
        ArcanumClientMutationLock heldClientMutationLock,
        ClientMutationBlockerRecord record,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(heldClientMutationLock);

        ArgumentNullException.ThrowIfNull(record);

        heldClientMutationLock.AssertHeldFor(_guardedRoot);

        if (!IsValid(record))
        {

            return Failure<ClientMutationBlockerPublication>(
                "The client-mutation blocker is invalid.");

        }

        Result<ClientMutationBlockerPublication?> current = await InspectAsync(
            cancellationToken).ConfigureAwait(false);

        if (current.IsFailure)
        {

            return Result<ClientMutationBlockerPublication>.Failure(current.Error);

        }

        if (current.Value is { } existing)
        {

            return existing.Record == record
                ? Result<ClientMutationBlockerPublication>.Success(existing)
                : Failure<ClientMutationBlockerPublication>(
                    "A different maintenance operation owns the client-mutation blocker.",
                    ErrorCodes.Data.ResetInProgress);

        }

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            record,
            ClientMutationBlockerJsonContext.Default.ClientMutationBlockerRecord);

        if (payload.Length > MaxBytes)
        {

            return Failure<ClientMutationBlockerPublication>(
                "The client-mutation blocker exceeds its byte limit.");

        }

        string temporaryPath = BlockerPath + ".tmp." + Guid.NewGuid().ToString("N");

        try
        {

            _options.BeforeAtomicPublishForTests?.Invoke();

            AtomicReplaceStatus status = await AtomicFile.ReplaceAsync(
                    BlockerPath,
                    temporaryPath,
                    async (stream, token) =>
                    {

                        await stream.WriteAsync(payload, token).ConfigureAwait(false);

                    },
                    cancellationToken,
                    beforeReplace: () =>
                    {

                        if (!SecureFilePermissions.TryApplyOwnerOnlyFileStrict(
                                temporaryPath,
                                logFailure: false))
                        {

                            return false;

                        }

                        _options.BeforePublishForTests?.Invoke();

                        cancellationToken.ThrowIfCancellationRequested();

                        Result<NoFollowPathTopologyKind> destination =
                            NoFollowPathTopology.Classify(BlockerPath);

                        return destination.IsSuccess
                            && destination.Value is NoFollowPathTopologyKind.Absent;

                    },
                    afterReplace: () =>
                        SecureFilePermissions.TryApplyOwnerOnlyFileStrict(
                            BlockerPath,
                            logFailure: false),
                    afterMoveBeforeVerify:
                        _options.AfterPublishMoveBeforeVerifyForTests)
                .ConfigureAwait(false);

            if (status is not AtomicReplaceStatus.Succeeded)
            {

                return Failure<ClientMutationBlockerPublication>(
                    status is AtomicReplaceStatus.ReplacedButUnverified
                        ? "The client-mutation blocker publication requires recovery."
                        : "The client-mutation blocker could not be published.",
                    status is AtomicReplaceStatus.ReplacedButUnverified
                        ? ErrorCodes.Data.RecoveryRequired
                        : ErrorCodes.Data.ControlPathUnavailable);

            }

            Result flushed = FlushParent();

            if (flushed.IsFailure)
            {

                return Result<ClientMutationBlockerPublication>.Failure(flushed.Error);

            }

            Result<ClientMutationBlockerPublication?> inspected = await InspectAsync(
                cancellationToken).ConfigureAwait(false);

            return inspected.IsSuccess
                && inspected.Value is { } published
                && published.Record == record
                ? Result<ClientMutationBlockerPublication>.Success(published)
                : Failure<ClientMutationBlockerPublication>(
                    "The client-mutation blocker could not be verified after publication.",
                    ErrorCodes.Data.RecoveryRequired);

        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {

            return Failure<ClientMutationBlockerPublication>(
                "The client-mutation blocker could not be published.");

        }

    }

    internal async Task<Result> RemoveAsync(
        ArcanumClientMutationLock heldClientMutationLock,
        ClientMutationBlockerPublication expected,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(heldClientMutationLock);

        ArgumentNullException.ThrowIfNull(expected);

        heldClientMutationLock.AssertHeldFor(_guardedRoot);

        Result<ClientMutationBlockerPublication?> inspected = await InspectAsync(
            cancellationToken).ConfigureAwait(false);

        if (inspected.IsFailure)
        {

            return Result.Failure(inspected.Error);

        }

        if (inspected.Value is null)
        {

            return Result.Success();

        }

        ClientMutationBlockerPublication actual = inspected.Value;

        if (actual.Record != expected.Record
            || !FileHandleIdentity.IdentitiesMatch(
                actual.Identity,
                expected.Identity)
            || !IdentityOwnedFileSystemCleanup.TryCapturePath(
                BlockerPath,
                FileSystemObjectKind.RegularFile,
                out IdentityOwnedFileSystemArtifact artifact)
            || !FileHandleIdentity.IdentitiesMatch(
                artifact.Metadata.Identity,
                expected.Identity)
            || !IdentityOwnedFileSystemCleanup.TryDelete(artifact))
        {

            return Result.Failure(new Error(
                ErrorCodes.Data.RecoveryRequired,
                "The client-mutation blocker could not be removed from the exact publication."));

        }

        Result<NoFollowPathTopologyKind> after =
            NoFollowPathTopology.Classify(BlockerPath);

        if (after.IsFailure
            || after.Value is not NoFollowPathTopologyKind.Absent)
        {

            return Result.Failure(new Error(
                ErrorCodes.Data.RecoveryRequired,
                "The client-mutation blocker removal could not be verified."));

        }

        return FlushParent();

    }

    private Result FlushParent()
    {

        string parent = Path.GetDirectoryName(BlockerPath)!;

        if (!FileHandleIdentityInterop.TryOpenDirectoryMetadata(
                parent,
                out Microsoft.Win32.SafeHandles.SafeFileHandle handle,
                out _))
        {

            return Result.Failure(new Error(
                ErrorCodes.Data.ControlPathUnavailable,
                "The client-mutation control directory could not be opened durably."));

        }

        using (handle)
        {

            return Backup.BackupRestoreJournalNativeMethods.TryFlushDirectory(handle)
                ? Result.Success()
                : Result.Failure(new Error(
                    ErrorCodes.Data.ControlPathUnavailable,
                    "The client-mutation control directory could not be flushed durably."));

        }

    }

    private static bool IsValid(ClientMutationBlockerRecord? record) =>
        record is not null
        && record.Version == CurrentVersion
        && record.BlockerId != Guid.Empty
        && Enum.IsDefined(record.Kind)
        && record.OperationId != Guid.Empty
        && (record.Kind is ClientMutationBlockerKind.InstallationReset
            ? record.Scope is InstallationResetScope.Global or InstallationResetScope.All
                && !string.IsNullOrWhiteSpace(record.PlanId)
            : record.Scope is null && record.PlanId is null);

    private static Result<T> Failure<T>(
        string message,
        string code = ErrorCodes.Data.ControlPathUnavailable) =>
        Result<T>.Failure(new Error(code, message));

}
