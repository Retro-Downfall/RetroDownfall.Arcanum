using System.Buffers.Text;

using System.Diagnostics.CodeAnalysis;

using System.Security.Cryptography;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>
/// One take of one profile's restore-journal key, and nothing else.
/// </summary>
/// <remarks>
/// Nonserializable and single-take by construction. The key authenticates the evidence that authorizes
/// renaming a live installation, so a coordinator that could hold it — as a string, a serializable
/// array, or a lease it could read twice — would be able to write its own journal at any revision. The
/// only consumer is <see cref="BackupRestoreJournalAuthenticator"/>, which spends the take on exactly
/// one bounded AES-GCM operation and zeroes the buffer in a <c>finally</c>; a lease that is never spent
/// zeroes on disposal instead.
/// </remarks>
internal sealed class BackupRestoreJournalKeyLease : IDisposable
{

    private byte[]? _key;

    private BackupRestoreJournalKeyLease(byte[] key) => _key = key;

    /// <summary>
    /// Wraps exactly <see cref="BackupRestoreJournalAuthenticator.KeyBytes"/> bytes, taking ownership.
    /// </summary>
    /// <remarks>
    /// The caller's array becomes the lease's; it must not be retained, because the lease is the thing
    /// that promises to zero it.
    /// </remarks>
    internal static BackupRestoreJournalKeyLease Mint(byte[] key)
    {

        ArgumentNullException.ThrowIfNull(key);

        return key.Length == BackupRestoreJournalAuthenticator.KeyBytes
            ? new BackupRestoreJournalKeyLease(key)
            : throw new ArgumentException(
                "A restore journal key lease holds exactly "
                + BackupRestoreJournalAuthenticator.KeyBytes
                + " bytes.",
                nameof(key));

    }

    /// <summary>Whether the single take has already been spent or disposed.</summary>
    internal bool IsSpent => Volatile.Read(ref _key) is null;

    /// <summary>
    /// Takes the key exactly once. The caller owns zeroing what it receives.
    /// </summary>
    /// <remarks>
    /// Its only permitted call sites are inside <c>BackupRestoreJournalAuthenticator</c>, pinned by
    /// <c>BackupRestoreJournalKeyLeaseCallSiteTests</c>. A second caller would be a second place that
    /// decides how long the key lives.
    /// </remarks>
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

/// <summary>
/// The sole accessor for one profile's namespaced restore-journal key account.
/// </summary>
/// <remarks>
/// The key is installation-stable and generated only under a proven installation lock, before any new
/// restore work. Recovery has a separate open-existing path that never creates one: minting a
/// replacement would strand every envelope written under the previous key while presenting as a
/// healthy first run, and the thing those envelopes govern is a half-swapped installation.
///
/// <para>Stored as canonical unpadded base64url of exactly thirty-two bytes. Every other spelling is
/// refused rather than repaired, so one key has exactly one stored form (§10.19.6).</para>
/// </remarks>
internal sealed class BackupRestoreJournalKeyProvider(IOsCredentialStore credentials)
{

    internal const int KeyBytes = BackupRestoreJournalAuthenticator.KeyBytes;

    /// <summary>Thirty-two bytes of unpadded base64url is exactly forty-three characters.</summary>
    internal const int EncodedKeyCharacters = 43;

    private readonly IOsCredentialStore _credentials =
        credentials ?? throw new ArgumentNullException(nameof(credentials));

    /// <summary>
    /// Opens the existing key, or generates and verifies one under the caller's installation lock.
    /// </summary>
    internal Result<BackupRestoreJournalKeyLease> CreateOrOpen(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        BackupRestoreProfileNamespace profileNamespace)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentException.ThrowIfNullOrWhiteSpace(guardedDirectory);

        ArgumentNullException.ThrowIfNull(profileNamespace);

        // Identity, not liveness, and deliberately no acquisition: the caller owns the one lock this
        // process holds and nesting a second exclusive open inside it would deadlock.
        heldInstallationLock.AssertHeldFor(guardedDirectory);

        string account = Account(profileNamespace);

        Result<byte[]?> existing = ReadExact(account);

        if (existing.IsFailure)
        {

            return Result<BackupRestoreJournalKeyLease>.Failure(existing.Error);

        }

        if (existing.Value is { } stored)
        {

            return BackupRestoreJournalKeyLease.Mint(stored);

        }

        byte[] created = RandomNumberGenerator.GetBytes(KeyBytes);

        OsCredentialStoreResult written = Set(account, Base64Url.EncodeToString(created));

        if (written.Status is not OsCredentialStoreStatus.Ok)
        {

            CryptographicOperations.ZeroMemory(created);

            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "This profile's restore journal key could not be written to the credential store.");

        }

        // An echo of what was handed in is not proof of persistence, so the key is read back and
        // compared before any envelope is sealed under it.
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
                "This profile's restore journal key did not read back as written.");

        }

        CryptographicOperations.ZeroMemory(created);

        return BackupRestoreJournalKeyLease.Mint(confirmed);

    }

    /// <summary>
    /// Opens an existing key. Never creates, never substitutes, never repairs.
    /// </summary>
    internal Result<BackupRestoreJournalKeyLease> OpenExisting(
        BackupRestoreProfileNamespace profileNamespace)
    {

        ArgumentNullException.ThrowIfNull(profileNamespace);

        Result<byte[]?> existing = ReadExact(Account(profileNamespace));

        if (existing.IsFailure)
        {

            return Result<BackupRestoreJournalKeyLease>.Failure(existing.Error);

        }

        return existing.Value is { } stored
            ? BackupRestoreJournalKeyLease.Mint(stored)
            : new Error(
                ErrorCodes.Covenant.NotFound,
                "This profile has no restore journal key.");

    }

    /// <summary>
    /// Whether this profile's key account holds usable material, without taking a lease.
    /// </summary>
    /// <remarks>
    /// An unreadable store is a failure rather than an absence. Reporting "no key" for a backend that
    /// merely could not be reached is what would let recovery conclude that nothing is in flight.
    /// </remarks>
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
        ArcanumCredentialIdentity.BackupRestoreJournalKeyAccount(profileNamespace.AccountSuffix);

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

    /// <summary>
    /// The stored key, absent, or a typed failure — never a repaired value.
    /// </summary>
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
                "This profile's restore journal key could not be read.");

        }

        if (result.Status is OsCredentialStoreStatus.NotFound)
        {

            return Result<byte[]?>.Success(null);

        }

        if (result.Status is not OsCredentialStoreStatus.Ok)
        {

            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "This profile's restore journal key could not be read.");

        }

        // An Ok with an empty value is malformed, not absent: an absent slot reports NotFound.
        if (result.Value is not { Length: EncodedKeyCharacters } encoded
            || !TryDecodeCanonical(encoded, out byte[] decoded))
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "This profile's restore journal key is not canonical unpadded base64url of "
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

        // Re-encoding is what rejects a noncanonical final character. Two spellings of one key would
        // be two keys as far as a byte comparison is concerned, and one of them would be unwritable.
        if (!string.Equals(Base64Url.EncodeToString(buffer), encoded, StringComparison.Ordinal))
        {

            CryptographicOperations.ZeroMemory(buffer);

            return false;

        }

        decoded = buffer;

        return true;

    }

}
