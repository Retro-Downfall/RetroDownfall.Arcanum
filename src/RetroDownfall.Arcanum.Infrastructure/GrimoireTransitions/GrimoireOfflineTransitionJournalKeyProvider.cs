using System.Buffers.Text;

using System.Diagnostics.CodeAnalysis;

using System.Security.Cryptography;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

internal sealed class GrimoireOfflineTransitionJournalKeyLease : IDisposable
{

    private byte[]? _key;

    private GrimoireOfflineTransitionJournalKeyLease(byte[] key) => _key = key;

    internal static GrimoireOfflineTransitionJournalKeyLease Mint(byte[] key)
    {

        ArgumentNullException.ThrowIfNull(key);

        return key.Length == GrimoireOfflineTransitionJournalAuthenticator.KeyBytes
            ? new GrimoireOfflineTransitionJournalKeyLease(key)
            : throw new ArgumentException("A transition journal key lease holds exactly 32 bytes.", nameof(key));

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

internal sealed class GrimoireOfflineTransitionJournalKeyProvider(IOsCredentialStore credentials)
{

    internal const int KeyBytes = GrimoireOfflineTransitionJournalAuthenticator.KeyBytes;

    internal const int EncodedKeyCharacters = 43;

    private readonly IOsCredentialStore _credentials =
        credentials ?? throw new ArgumentNullException(nameof(credentials));

    internal Result<GrimoireOfflineTransitionJournalKeyLease> CreateOrOpen(
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

            return Result<GrimoireOfflineTransitionJournalKeyLease>.Failure(existing.Error);

        }

        if (existing.Value is { } stored)
        {

            return GrimoireOfflineTransitionJournalKeyLease.Mint(stored);

        }

        byte[] created = RandomNumberGenerator.GetBytes(KeyBytes);

        OsCredentialStoreResult written = Set(account, Base64Url.EncodeToString(created));

        if (written.Status is not OsCredentialStoreStatus.Ok)
        {

            CryptographicOperations.ZeroMemory(created);

            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "This profile's transition journal key could not be written to the credential store.");

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
                "This profile's transition journal key did not read back as written.");

        }

        CryptographicOperations.ZeroMemory(created);

        return GrimoireOfflineTransitionJournalKeyLease.Mint(confirmed);

    }

    internal Result<GrimoireOfflineTransitionJournalKeyLease> OpenExisting(
        BackupRestoreProfileNamespace profileNamespace)
    {

        ArgumentNullException.ThrowIfNull(profileNamespace);

        Result<byte[]?> existing = ReadExact(Account(profileNamespace));

        if (existing.IsFailure)
        {

            return Result<GrimoireOfflineTransitionJournalKeyLease>.Failure(existing.Error);

        }

        return existing.Value is { } stored
            ? GrimoireOfflineTransitionJournalKeyLease.Mint(stored)
            : new Error(ErrorCodes.Covenant.NotFound, "This profile has no transition journal key.");

    }

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

    private static string Account(BackupRestoreProfileNamespace profileNamespace) =>
        ArcanumCredentialIdentity.GrimoireTransitionJournalKeyAccount(profileNamespace.AccountSuffix);

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
                "This profile's transition journal key could not be read.");

        }

        if (result.Status is OsCredentialStoreStatus.NotFound)
        {

            return Result<byte[]?>.Success(null);

        }

        if (result.Status is not OsCredentialStoreStatus.Ok
            || result.Value is not { Length: EncodedKeyCharacters } encoded
            || !TryDecodeCanonical(encoded, out byte[] decoded))
        {

            return new Error(
                result.Status is OsCredentialStoreStatus.Ok
                    ? ErrorCodes.Covenant.IntegrityFailure
                    : ErrorCodes.Covenant.Unavailable,
                "This profile's transition journal key could not be read canonically.");

        }

        return decoded;

    }

    private static bool TryDecodeCanonical(string encoded, out byte[] decoded)
    {

        decoded = [];

        if (encoded.Any(static value => value is not (>= 'A' and <= 'Z')
            and not (>= 'a' and <= 'z')
            and not (>= '0' and <= '9')
            and not '-' and not '_'))
        {

            return false;

        }

        byte[] buffer = new byte[KeyBytes];

        if (!Base64Url.TryDecodeFromChars(encoded, buffer, out int written) || written != KeyBytes
            || !string.Equals(Base64Url.EncodeToString(buffer), encoded, StringComparison.Ordinal))
        {

            CryptographicOperations.ZeroMemory(buffer);

            return false;

        }

        decoded = buffer;

        return true;

    }

}
