using Microsoft.Win32.SafeHandles;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

internal sealed record InstallationResetActivePublication(
    InstallationResetActiveLocation Location,
    InstallationResetActiveEnvelopeV2 Envelope,
    CovenantDigest EnvelopeDigest,
    InstallationResetActivePayloadV3 Payload,
    InstallationResetActiveAnchorV1 Anchor);

internal enum InstallationResetActiveRecoveryOutcome : byte
{

    NoActiveRecord = 1,

    AuthenticatedV2 = 2,

    LegacyV1 = 3,

}

internal sealed record InstallationResetActiveRecoveryState(
    InstallationResetActiveRecoveryOutcome Outcome,
    InstallationResetActivePublication? Publication,
    InstallationResetActiveRecord? LegacyRecord,
    FileHandleIdentity? LegacyFileIdentity = null);

/// <summary>The reset-active anchor account's read-compare-write-readback owner.</summary>
internal sealed class InstallationResetActiveAnchorStore(IOsCredentialStore credentials)
{

    private readonly IOsCredentialStore _credentials =
        credentials ?? throw new ArgumentNullException(nameof(credentials));

    internal Result<InstallationResetActiveAnchorV1?> Read(
        BackupRestoreProfileNamespace profileNamespace)
    {

        ArgumentNullException.ThrowIfNull(profileNamespace);

        OsCredentialStoreResult result;

        try
        {

            result = _credentials.TryGet(
                ArcanumCredentialIdentity.Service,
                Account(profileNamespace));

        }
        catch (Exception exception) when (IsCredentialFailure(exception))
        {

            return Unavailable<InstallationResetActiveAnchorV1?>();

        }

        if (result.Status is OsCredentialStoreStatus.NotFound)
        {

            return Result<InstallationResetActiveAnchorV1?>.Success(null);

        }

        if (result.Status is not OsCredentialStoreStatus.Ok)
        {

            return Unavailable<InstallationResetActiveAnchorV1?>();

        }

        Result<InstallationResetActiveAnchorV1> decoded =
            InstallationResetActiveRecordAuthenticator.DecodeAnchor(result.Value);

        return decoded.IsSuccess
            ? Result<InstallationResetActiveAnchorV1?>.Success(decoded.Value)
            : Result<InstallationResetActiveAnchorV1?>.Failure(decoded.Error);

    }

    internal Result WriteOpeningAndVerify(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        BackupRestoreProfileNamespace profileNamespace,
        InstallationResetActiveAnchorV1 opening)
    {

        AssertLock(heldInstallationLock, guardedDirectory);

        Result<InstallationResetActiveAnchorV1?> current = Read(profileNamespace);

        if (current.IsFailure)
        {

            return current.Error;

        }

        return current.Value is null
            ? WriteAndVerify(profileNamespace, opening)
            : Conflict();

    }

    internal Result CompareWriteAndVerify(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        BackupRestoreProfileNamespace profileNamespace,
        InstallationResetActiveAnchorV1 expected,
        InstallationResetActiveAnchorV1 next)
    {

        AssertLock(heldInstallationLock, guardedDirectory);

        Result<InstallationResetActiveAnchorV1?> current = Read(profileNamespace);

        if (current.IsFailure)
        {

            return current.Error;

        }

        return current.Value == expected
            ? WriteAndVerify(profileNamespace, next)
            : Conflict();

    }

    internal Result RemoveAndVerifyAbsent(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        BackupRestoreProfileNamespace profileNamespace,
        InstallationResetActiveAnchorV1 expected)
    {

        AssertLock(heldInstallationLock, guardedDirectory);

        Result<InstallationResetActiveAnchorV1?> current = Read(profileNamespace);

        if (current.IsFailure)
        {

            return current.Error;

        }

        if (current.Value is null)
        {

            return Result.Success();

        }

        if (current.Value != expected)
        {

            return Conflict();

        }

        OsCredentialStoreResult removed;

        try
        {

            removed = _credentials.Delete(
                ArcanumCredentialIdentity.Service,
                Account(profileNamespace));

        }
        catch (Exception exception) when (IsCredentialFailure(exception))
        {

            return Unavailable();

        }

        if (removed.Status is not OsCredentialStoreStatus.Ok
            and not OsCredentialStoreStatus.NotFound)
        {

            return Unavailable();

        }

        Result<InstallationResetActiveAnchorV1?> readback = Read(profileNamespace);

        return readback.IsFailure
            ? readback.Error
            : readback.Value is null
                ? Result.Success()
                : Integrity();

    }

    private Result WriteAndVerify(
        BackupRestoreProfileNamespace profileNamespace,
        InstallationResetActiveAnchorV1 anchor)
    {

        Result<string> encoded = InstallationResetActiveRecordAuthenticator.EncodeAnchor(anchor);

        if (encoded.IsFailure)
        {

            return encoded.Error;

        }

        OsCredentialStoreResult written;

        try
        {

            written = _credentials.Set(
                ArcanumCredentialIdentity.Service,
                Account(profileNamespace),
                encoded.Value);

        }
        catch (Exception exception) when (IsCredentialFailure(exception))
        {

            return Unavailable();

        }

        if (written.Status is not OsCredentialStoreStatus.Ok)
        {

            return Unavailable();

        }

        Result<InstallationResetActiveAnchorV1?> readback = Read(profileNamespace);

        return readback.IsFailure
            ? readback.Error
            : readback.Value == anchor
                ? Result.Success()
                : Integrity();

    }

    private static void AssertLock(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentException.ThrowIfNullOrWhiteSpace(guardedDirectory);

        heldInstallationLock.AssertHeldFor(guardedDirectory);

    }

    private static string Account(BackupRestoreProfileNamespace profileNamespace) =>
        ArcanumCredentialIdentity.InstallationResetActiveAnchorAccount(
            profileNamespace.AccountSuffix);

    private static bool IsCredentialFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or NotSupportedException;

    private static Result Conflict() =>
        new Error(
            ErrorCodes.Covenant.RevisionConflict,
            "The installation-reset active anchor is not the revision this transition read.");

    private static Result Integrity() =>
        new Error(
            ErrorCodes.Covenant.IntegrityFailure,
            "The installation-reset active anchor did not read back as written.");

    private static Result Unavailable() =>
        new Error(
            ErrorCodes.Covenant.Unavailable,
            "The installation-reset active anchor credential is unavailable.");

    private static Result<T> Unavailable<T>() =>
        new Error(
            ErrorCodes.Covenant.Unavailable,
            "The installation-reset active anchor credential is unavailable.");

}

internal sealed class InstallationResetActiveFileRead : IDisposable
{

    private byte[]? _bytes;

    internal InstallationResetActiveFileRead(byte[] bytes, FileHandleMetadata metadata)
    {

        _bytes = bytes;

        Metadata = metadata;

    }

    internal ReadOnlyMemory<byte> Bytes => _bytes ?? [];

    internal FileHandleMetadata Metadata { get; }

    public void Dispose()
    {

        if (Interlocked.Exchange(ref _bytes, null) is { } bytes)
        {

            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);

        }

    }

}

/// <summary>Reset-local durable replacement, secure reread, and identity-owned deletion.</summary>
internal sealed class InstallationResetActiveFilePersistence(
    Action<string>? afterStep = null,
    Func<string, bool>? failBeforeStep = null)
{

    internal async Task<Result<InstallationResetActiveFileRead?>> ReadIfPresentAsync(
        InstallationResetActiveLocation location,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(location);

        cancellationToken.ThrowIfCancellationRequested();

        Result<bool> evidence = InspectDirectory(location, requireCanonicalAbsent: false);

        if (evidence.IsFailure)
        {

            return Result<InstallationResetActiveFileRead?>.Failure(evidence.Error);

        }

        if (!evidence.Value)
        {

            return Result<InstallationResetActiveFileRead?>.Success(null);

        }

        using SecureFileReadResult read = await SecureFileReader.ReadBytesAsync(
                location.ActivePath,
                InstallationResetActiveRecordAuthenticator.MaxActiveFileBytes,
                cancellationToken)
            .ConfigureAwait(false);

        if (read.Status is not SecureFileReadStatus.Success)
        {

            return EvidenceFailure<InstallationResetActiveFileRead?>();

        }

        byte[] exact = read.Bytes.ToArray();

        afterStep?.Invoke("file:secure-reread");

        return new InstallationResetActiveFileRead(exact, read.Metadata);

    }

    internal Result RequireNoEvidence(InstallationResetActiveLocation location)
    {

        Result<bool> inspected = InspectDirectory(location, requireCanonicalAbsent: true);

        return inspected.IsFailure
            ? inspected.Error
            : inspected.Value
                ? EvidenceFailure()
                : Result.Success();

    }

    internal async Task<Result> ReplaceDurablyAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        InstallationResetActiveLocation location,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {

        AssertLock(heldInstallationLock, guardedDirectory);

        ArgumentNullException.ThrowIfNull(location);

        cancellationToken.ThrowIfCancellationRequested();

        string parent = Path.GetDirectoryName(location.ActivePath)!;

        string temporaryPath = location.ActivePath + ".tmp." + Guid.NewGuid().ToString("N");

        IdentityOwnedFileSystemArtifact temporary = default;

        bool replaced = false;

        try
        {

            if (!SecureFilePermissions.TryEnsureOwnerOnlyDirectoryExistsStrict(parent))
            {

                return Unavailable();

            }

            using (FileStream stream = SecureFilePermissions.CreateOwnerOnlyTempFile(temporaryPath))
            {

                if (!IdentityOwnedFileSystemCleanup.TryCaptureOpenFile(
                        temporaryPath,
                        stream.SafeFileHandle,
                        out temporary))
                {

                    return Unavailable();

                }

                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();

                if (ShouldFail("file:temporary-flushed"))
                {

                    return Unavailable();

                }

                stream.Flush(flushToDisk: true);

                afterStep?.Invoke("file:temporary-flushed");

            }

            cancellationToken.ThrowIfCancellationRequested();

            if (ShouldFail("file:atomic-replace"))
            {

                return Unavailable();

            }

            File.Move(temporaryPath, location.ActivePath, overwrite: true);

            replaced = true;

            SecureFilePermissions.ApplyOwnerOnlyFile(location.ActivePath);

            afterStep?.Invoke("file:atomic-replace");

            if (ShouldFail("file:parent-flushed"))
            {

                return RecoveryRequired();

            }

            Result flushed = FlushParent(parent);

            if (flushed.IsFailure)
            {

                return flushed;

            }

            afterStep?.Invoke("file:parent-flushed");

            return Result.Success();

        }
        catch (OperationCanceledException) when (replaced)
        {

            return RecoveryRequired();

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {

            return replaced ? RecoveryRequired() : Unavailable();

        }
        finally
        {

            if (!replaced && temporary != default)
            {

                _ = IdentityOwnedFileSystemCleanup.TryDelete(temporary);

            }

        }

    }

    internal Result DeleteDurably(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        InstallationResetActiveLocation location,
        FileHandleMetadata expected)
    {

        AssertLock(heldInstallationLock, guardedDirectory);

        ArgumentNullException.ThrowIfNull(location);

        IdentityOwnedFileSystemArtifact artifact = new(location.ActivePath, expected);

        if (ShouldFail("file:delete")
            || !IdentityOwnedFileSystemCleanup.TryDelete(artifact))
        {

            return RecoveryRequired();

        }

        afterStep?.Invoke("file:delete");

        Result flushed = FlushParent(Path.GetDirectoryName(location.ActivePath)!);

        if (flushed.IsFailure)
        {

            return flushed;

        }

        afterStep?.Invoke("file:delete-parent-flushed");

        Result<bool> absent = InspectDirectory(location, requireCanonicalAbsent: true);

        if (absent.IsFailure || absent.Value)
        {

            return RecoveryRequired();

        }

        afterStep?.Invoke("file:absence-proved");

        return Result.Success();

    }

    internal Result ProveAbsentDurably(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        InstallationResetActiveLocation location)
    {

        AssertLock(heldInstallationLock, guardedDirectory);

        ArgumentNullException.ThrowIfNull(location);

        Result<bool> beforeFlush = InspectDirectory(location, requireCanonicalAbsent: true);

        if (beforeFlush.IsFailure || beforeFlush.Value)
        {

            return RecoveryRequired();

        }

        Result flushed = FlushParent(Path.GetDirectoryName(location.ActivePath)!);

        if (flushed.IsFailure)
        {

            return flushed;

        }

        afterStep?.Invoke("file:absence-parent-flushed");

        Result<bool> afterFlush = InspectDirectory(location, requireCanonicalAbsent: true);

        if (afterFlush.IsFailure || afterFlush.Value)
        {

            return RecoveryRequired();

        }

        afterStep?.Invoke("file:absence-proved");

        return Result.Success();

    }

    private bool ShouldFail(string step) => failBeforeStep?.Invoke(step) is true;

    private static void AssertLock(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentException.ThrowIfNullOrWhiteSpace(guardedDirectory);

        heldInstallationLock.AssertHeldFor(guardedDirectory);

    }

    private static Result<bool> InspectDirectory(
        InstallationResetActiveLocation location,
        bool requireCanonicalAbsent)
    {

        string parent = Path.GetDirectoryName(location.ActivePath)!;

        bool canonical = false;

        try
        {

            foreach (string entry in Directory.EnumerateFileSystemEntries(parent))
            {

                string leaf = Path.GetFileName(entry);

                if (string.Equals(
                        leaf,
                        location.ActiveLeaf,
                        StringComparison.Ordinal))
                {

                    canonical = true;

                    continue;

                }

                if (string.Equals(
                        leaf,
                        location.ActiveLeaf,
                        StringComparison.OrdinalIgnoreCase)
                    || leaf.StartsWith(
                        location.ActiveLeaf + ".tmp",
                        StringComparison.OrdinalIgnoreCase))
                {

                    return EvidenceFailure<bool>();

                }

            }

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {

            return EvidenceFailure<bool>();

        }

        if (!canonical)
        {

            Result<bool> probed = ProbeCanonicalPathNoFollow(location.ActivePath);

            if (probed.IsFailure)
            {

                return probed;

            }

            canonical = probed.Value;

        }

        if (requireCanonicalAbsent && canonical)
        {

            return true;

        }

        return canonical;

    }

    private static Result<bool> ProbeCanonicalPathNoFollow(string path)
    {

        if (FileHandleIdentityInterop.TryGetPathMetadataNoFollow(path, out _))
        {

            return true;

        }

        SecureFileOpenStatus status = FileHandleIdentityInterop.TryOpenReadOnlyNoFollow(
            path,
            out SafeFileHandle? handle);

        handle?.Dispose();

        return status switch
        {
            SecureFileOpenStatus.NotFound => false,
            SecureFileOpenStatus.Success => true,
            _ => EvidenceFailure<bool>(),
        };

    }

    private static Result FlushParent(string parent)
    {

        if (!FileHandleIdentityInterop.TryOpenDirectoryMetadata(
                parent,
                out SafeFileHandle handle,
                out _))
        {

            return RecoveryRequired();

        }

        using (handle)
        {

            return BackupRestoreJournalNativeMethods.TryFlushDirectory(handle)
                ? Result.Success()
                : RecoveryRequired();

        }

    }

    private static Result Unavailable() =>
        new Error(
            ErrorCodes.Data.ControlPathUnavailable,
            "The installation-reset active evidence could not be written durably.");

    private static Result RecoveryRequired() =>
        new Error(
            ErrorCodes.Data.RecoveryRequired,
            "The installation-reset active evidence requires recovery.");

    private static Result EvidenceFailure() =>
        new Error(
            ErrorCodes.Covenant.ManualRecoveryRequired,
            "The installation-reset active evidence could not be proven safe.");

    private static Result<T> EvidenceFailure<T>() =>
        new Error(
            ErrorCodes.Covenant.ManualRecoveryRequired,
            "The installation-reset active evidence could not be proven safe.");

}
