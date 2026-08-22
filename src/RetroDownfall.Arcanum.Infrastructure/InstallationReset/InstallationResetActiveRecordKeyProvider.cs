using System.Buffers.Text;

using System.Diagnostics.CodeAnalysis;

using System.Security.Cryptography;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

/// <summary>One single-use take of one profile's installation-reset active-record key.</summary>
internal sealed class InstallationResetActiveRecordKeyLease : IDisposable
{

    private byte[]? _key;

    private InstallationResetActiveRecordKeyLease(byte[] key) => _key = key;

    internal static InstallationResetActiveRecordKeyLease Mint(byte[] key)
    {

        ArgumentNullException.ThrowIfNull(key);

        return key.Length == InstallationResetActiveRecordKeyProvider.KeyBytes
            ? new InstallationResetActiveRecordKeyLease(key)
            : throw new ArgumentException(
                "An installation-reset active-record key lease holds exactly "
                + InstallationResetActiveRecordKeyProvider.KeyBytes
                + " bytes.",
                nameof(key));

    }

    internal bool IsSpent => Volatile.Read(ref _key) is null;

    internal bool TryTakeKey([NotNullWhen(true)] out byte[]? key)
    {

        key = Interlocked.Exchange(ref _key, null);

        return key is not null;

    }

    public void Dispose()
    {

        if (Interlocked.Exchange(ref _key, null) is { } key)
        {

            CryptographicOperations.ZeroMemory(key);

        }

    }

}

/// <summary>The sole accessor for one profile's installation-reset active-record key account.</summary>
internal sealed class InstallationResetActiveRecordKeyProvider(IOsCredentialStore credentials)
{

    internal const int KeyBytes = 32;

    internal const int EncodedKeyCharacters = 43;

    private readonly IOsCredentialStore _credentials =
        credentials ?? throw new ArgumentNullException(nameof(credentials));

    internal Result<InstallationResetActiveRecordKeyLease> CreateOrOpen(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        BackupRestoreProfileNamespace profileNamespace)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentException.ThrowIfNullOrWhiteSpace(guardedDirectory);

        ArgumentNullException.ThrowIfNull(profileNamespace);

        heldInstallationLock.AssertHeldFor(guardedDirectory);

        string account = Account(profileNamespace);

        Result<byte[]?> existing = ReadExact(account);

        if (existing.IsFailure)
        {

            return Result<InstallationResetActiveRecordKeyLease>.Failure(existing.Error);

        }

        if (existing.Value is { } stored)
        {

            return InstallationResetActiveRecordKeyLease.Mint(stored);

        }

        byte[] created = RandomNumberGenerator.GetBytes(KeyBytes);

        OsCredentialStoreResult written = Set(account, Base64Url.EncodeToString(created));

        if (written.Status is not OsCredentialStoreStatus.Ok)
        {

            CryptographicOperations.ZeroMemory(created);

            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "This profile's installation-reset active-record key could not be written.");

        }

        Result<byte[]?> readback = ReadExact(account);

        if (readback.IsFailure || readback.Value is not { } confirmed
            || !CryptographicOperations.FixedTimeEquals(confirmed, created))
        {

            CryptographicOperations.ZeroMemory(created);

            if (readback.IsSuccess && readback.Value is { } mismatched)
            {

                CryptographicOperations.ZeroMemory(mismatched);

            }

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "This profile's installation-reset active-record key did not read back as written.");

        }

        CryptographicOperations.ZeroMemory(created);

        return InstallationResetActiveRecordKeyLease.Mint(confirmed);

    }

    /// <summary>Opens existing key material without creating, substituting, or repairing it.</summary>
    internal Result<InstallationResetActiveRecordKeyLease> OpenExisting(
        BackupRestoreProfileNamespace profileNamespace)
    {

        ArgumentNullException.ThrowIfNull(profileNamespace);

        Result<byte[]?> existing = ReadExact(Account(profileNamespace));

        if (existing.IsFailure)
        {

            return Result<InstallationResetActiveRecordKeyLease>.Failure(existing.Error);

        }

        return existing.Value is { } stored
            ? InstallationResetActiveRecordKeyLease.Mint(stored)
            : new Error(
                ErrorCodes.Covenant.NotFound,
                "This profile has no installation-reset active-record key.");

    }

    /// <summary>Whether this profile's key account holds canonical usable material.</summary>
    internal Result<bool> IsPresent(BackupRestoreProfileNamespace profileNamespace)
    {

        ArgumentNullException.ThrowIfNull(profileNamespace);

        Result<byte[]?> existing = ReadExact(Account(profileNamespace));

        if (existing.IsFailure)
        {

            return Result<bool>.Failure(existing.Error);

        }

        if (existing.Value is { } stored)
        {

            CryptographicOperations.ZeroMemory(stored);

            return true;

        }

        return false;

    }

    /// <summary>Deletes this profile's key under the installation lock and proves it absent.</summary>
    internal Result RemoveAndVerifyAbsent(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        BackupRestoreProfileNamespace profileNamespace)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentException.ThrowIfNullOrWhiteSpace(guardedDirectory);

        ArgumentNullException.ThrowIfNull(profileNamespace);

        heldInstallationLock.AssertHeldFor(guardedDirectory);

        string account = Account(profileNamespace);

        OsCredentialStoreResult removed;

        try
        {

            removed = _credentials.Delete(ArcanumCredentialIdentity.Service, account);

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or NotSupportedException)
        {

            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "This profile's installation-reset active-record key could not be removed.");

        }

        if (removed.Status is not OsCredentialStoreStatus.Ok
            and not OsCredentialStoreStatus.NotFound)
        {

            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "This profile's installation-reset active-record key could not be removed.");

        }

        OsCredentialStoreResult verified;

        try
        {

            verified = _credentials.TryGet(ArcanumCredentialIdentity.Service, account);

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or NotSupportedException)
        {

            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "This profile's installation-reset active-record key absence could not be verified.");

        }

        return verified.Status is OsCredentialStoreStatus.NotFound
            ? Result.Success()
            : new Error(
                verified.Status is OsCredentialStoreStatus.Ok
                    ? ErrorCodes.Covenant.IntegrityFailure
                    : ErrorCodes.Covenant.Unavailable,
                "This profile's installation-reset active-record key remains present or its absence "
                + "could not be verified.");

    }

    private static string Account(BackupRestoreProfileNamespace profileNamespace) =>
        ArcanumCredentialIdentity.InstallationResetActiveKeyAccount(profileNamespace.AccountSuffix);

    private OsCredentialStoreResult Set(string account, string value)
    {

        try
        {

            return _credentials.Set(ArcanumCredentialIdentity.Service, account, value);

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or NotSupportedException)
        {

            return OsCredentialStoreResult.Failed(exception.Message);

        }

    }

    private Result<byte[]?> ReadExact(string account)
    {

        OsCredentialStoreResult result;

        try
        {

            result = _credentials.TryGet(ArcanumCredentialIdentity.Service, account);

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or NotSupportedException)
        {

            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "This profile's installation-reset active-record key could not be read.");

        }

        if (result.Status is OsCredentialStoreStatus.NotFound)
        {

            return Result<byte[]?>.Success(null);

        }

        if (result.Status is not OsCredentialStoreStatus.Ok)
        {

            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "This profile's installation-reset active-record key could not be read.");

        }

        if (result.Value is not { Length: EncodedKeyCharacters } encoded
            || !TryDecodeCanonical(encoded, out byte[] decoded))
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "This profile's installation-reset active-record key is not canonical unpadded "
                + "base64url of "
                + KeyBytes
                + " bytes.");

        }

        return decoded;

    }

    private static bool TryDecodeCanonical(string encoded, out byte[] decoded)
    {

        decoded = [];

        foreach (char value in encoded)
        {

            bool allowed = value is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '-'
                or '_';

            if (!allowed)
            {

                return false;

            }

        }

        byte[] buffer = new byte[KeyBytes];

        if (!Base64Url.TryDecodeFromChars(encoded, buffer, out int written) || written != KeyBytes)
        {

            CryptographicOperations.ZeroMemory(buffer);

            return false;

        }

        if (!string.Equals(Base64Url.EncodeToString(buffer), encoded, StringComparison.Ordinal))
        {

            CryptographicOperations.ZeroMemory(buffer);

            return false;

        }

        decoded = buffer;

        return true;

    }

}
