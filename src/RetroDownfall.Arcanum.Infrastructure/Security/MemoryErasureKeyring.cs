using System.Buffers;

using System.Buffers.Text;

using System.Security.Cryptography;

using RetroDownfall.Arcanum.Core.Memory;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// The half of the erasure keyring that may create the key.
/// </summary>
/// <remarks>
/// Registered only in the host container, for erase prepare and <c>reset-key</c>. The CLI and restore
/// containers resolve <see cref="IMemoryErasureKeyProvider"/> alone, so nothing they run can mint a
/// key.
/// </remarks>
internal interface IMemoryErasureKeyCreator
{
    /// <summary>
    /// Opens the key, creating it only on a proven absence and only when no evidence rows exist.
    /// </summary>
    /// <remarks>
    /// With rows, an absent key is returned as <see cref="MemoryErasureKeyState.Absent"/> and nothing
    /// is written: evidence without its key is a lost key, never a reason for a fresh one.
    /// </remarks>
    MemoryErasureKeyOpenResult OpenOrCreate(bool evidenceRowsExist);

    /// <summary>
    /// Asks the store again whatever is latched: keeps a present key, creates one on a proven absence,
    /// and refuses on anything else without writing or deleting.
    /// </summary>
    MemoryErasureKeyOpenResult CreateForReset();
}

/// <summary>
/// The process-wide holder of the installation's erasure key, kept in the OS credential store.
/// </summary>
/// <remarks>
/// <para>One latch serves every caller. Automatic callers take the latched answer, so the store is
/// asked for them at most once per process, and a failure is remembered rather than retried per call.
/// Operator calls re-probe anything short of Present and publish what they find into the same latch,
/// so a transient failure at the first automatic probe clears at the next operator status or erase.
/// A Present latch is never re-probed: only <see cref="CreateForReset()"/> reads the account whatever is
/// latched, and <c>reset-key</c> reaches it only after the latch read Absent. So once this process
/// holds a key it keeps it until it restarts, even if the account is overwritten or deleted meanwhile:
/// a second test home that overwrites the shared account cannot swap the key under this process, and
/// whatever that home records carries a key identifier this process can tell apart. A deleted account
/// is reported as lost only after a restart.</para>
///
/// <para>Every read, write, and read-back runs under one lock, so concurrent first erasures create
/// exactly one key. A create writes canonical unpadded base64url, reads it back, and compares in
/// constant time. Any failure along the way is Unavailable, with no second write. A malformed stored
/// value is reported and never overwritten.</para>
///
/// <para>The latch itself is one immutable snapshot, replaced whole under that lock and read without
/// it. <see cref="Latch"/> and <see cref="TryCopyLatched"/> may be read while a Covenant lease is held,
/// and a probe can sit behind a keychain prompt for as long as the operator leaves it there, so those
/// two must never wait for the lock a probe holds.</para>
///
/// <para>Construction performs no credential I/O, and nothing here logs.</para>
/// </remarks>
internal sealed class MemoryErasureKeyring(IOsCredentialStore credentials)
    : IMemoryErasureKeyProvider, IMemoryErasureKeyCreator, IDisposable
{
    /// <summary>The dedicated credential account. Never shared with another Arcanum secret.</summary>
    internal const string Account = ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount;

    /// <summary>Thirty-two bytes of unpadded base64url is exactly forty-three characters.</summary>
    internal const int EncodedKeyCharacters = 43;

    private const int KeyBytes = MemoryErasureDigestGrammar.KeyBytes;

    private readonly Lock _gate = new();

    private readonly IOsCredentialStore _credentials =
        credentials ?? throw new ArgumentNullException(nameof(credentials));

    /// <summary>Written only under <see cref="_gate"/>; read anywhere.</summary>
    private Latched _latched = new(MemoryErasureKeyState.Unresolved, null, null);

    private bool _disposed;

    /// <inheritdoc/>
    public MemoryErasureKeyLatch Latch
    {
        get
        {
            Latched latched = Volatile.Read(ref _latched);

            return new MemoryErasureKeyLatch(latched.State, latched.KeyId is { } keyId ? [.. keyId] : null);
        }
    }

    /// <inheritdoc/>
    public MemoryErasureKey? TryCopyLatched()
    {
        while (true)
        {
            Latched latched = Volatile.Read(ref _latched);

            if (latched.Key is not { } key)
            {
                return null;
            }

            MemoryErasureKey copy = MemoryErasureKey.FromBytes(key);

            // A publish swaps the snapshot before it zeroes the key it replaced. A copy that overlapped
            // that swap is discarded here rather than handed out part-zeroed.
            Interlocked.MemoryBarrier();

            if (ReferenceEquals(Volatile.Read(ref _latched), latched))
            {
                return copy;
            }

            copy.Dispose();
        }
    }

    /// <inheritdoc/>
    public MemoryErasureKeyOpenResult OpenExisting(MemoryErasureKeyProbe probe)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return new MemoryErasureKeyOpenResult(MemoryErasureKeyState.Unavailable, null);
            }

            if (_latched.State is MemoryErasureKeyState.Present)
            {
                return CopyPresent();
            }

            if (probe is not MemoryErasureKeyProbe.Reprobe
                && _latched.State is not MemoryErasureKeyState.Unresolved)
            {
                return new MemoryErasureKeyOpenResult(_latched.State, null);
            }

            (MemoryErasureKeyState state, byte[]? key) = Probe();

            return Publish(state, key);
        }
    }

    /// <inheritdoc/>
    public MemoryErasureKeyOpenResult OpenOrCreate(bool evidenceRowsExist)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return new MemoryErasureKeyOpenResult(MemoryErasureKeyState.Unavailable, null);
            }

            if (_latched.State is MemoryErasureKeyState.Present)
            {
                return CopyPresent();
            }

            (MemoryErasureKeyState state, byte[]? key) = Probe();

            return state is MemoryErasureKeyState.Absent && !evidenceRowsExist
                ? Create()
                : Publish(state, key);
        }
    }

    /// <inheritdoc/>
    public MemoryErasureKeyOpenResult CreateForReset() => CreateForReset(out _);

    /// <summary>
    /// <see cref="CreateForReset()"/>, also saying whether this call wrote the key.
    /// </summary>
    /// <param name="created">
    /// True only when this call found the account absent and wrote and read back a new key. A key it
    /// found, perhaps written by another caller since the reset last read the account, was not created
    /// here.
    /// </param>
    internal MemoryErasureKeyOpenResult CreateForReset(out bool created)
    {
        created = false;

        lock (_gate)
        {
            if (_disposed)
            {
                return new MemoryErasureKeyOpenResult(MemoryErasureKeyState.Unavailable, null);
            }

            (MemoryErasureKeyState state, byte[]? key) = Probe();

            if (state is not MemoryErasureKeyState.Absent)
            {
                return Publish(state, key);
            }

            MemoryErasureKeyOpenResult result = Create();

            created = result.State is MemoryErasureKeyState.Present;

            return result;
        }
    }

    /// <summary>
    /// Asks the credential store only whether the key's item exists, reading no secret.
    /// </summary>
    /// <remarks>
    /// The erasure status uses this when no evidence exists, so an installation that has never erased
    /// anything is reported without the key's bytes ever being asked for, which on macOS is the difference
    /// between a silent answer and a prompt. It runs under the keyring's lock, like every other credential
    /// call, and never changes the latch: whether an item exists says nothing about whether it holds a
    /// key, so only a read may publish.
    /// </remarks>
    /// <returns>
    /// The store's answer; <see cref="OsCredentialStoreStatus.Unavailable"/> when the probe faults or the
    /// keyring is disposed; null when the store has no metadata-only probe.
    /// </returns>
    internal OsCredentialStoreStatus? ProbePresence()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return OsCredentialStoreStatus.Unavailable;
            }

            if (_credentials is not IOsCredentialPresenceProbe presence)
            {
                return null;
            }

            try
            {
                return presence.ProbePresence(ArcanumCredentialIdentity.Service, Account);
            }
            catch (Exception exception) when (IsStoreFault(exception))
            {
                return OsCredentialStoreStatus.Unavailable;
            }
        }
    }

    /// <summary>Zeroes the cached key. Every later call reads as Unavailable and performs no I/O.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;

            _ = Publish(MemoryErasureKeyState.Unavailable, null);
        }
    }

    /// <summary>Called under <see cref="_gate"/>, where no publish can race the copy.</summary>
    private MemoryErasureKeyOpenResult CopyPresent() =>
        new(MemoryErasureKeyState.Present, MemoryErasureKey.FromBytes(_latched.Key!));

    /// <summary>
    /// Replaces the latch, taking ownership of <paramref name="key"/>, and returns a private copy.
    /// </summary>
    private MemoryErasureKeyOpenResult Publish(MemoryErasureKeyState state, byte[]? key)
    {
        MemoryErasureKeyOpenResult result = new(state, key is null ? null : MemoryErasureKey.FromBytes(key));

        Latched next = new(state, key, key is null ? null : MemoryErasureDigestGrammar.KeyId(key));

        // The exchange is a full fence, so a reader that sees any zeroed byte below also sees the new
        // snapshot and discards its copy.
        if (Interlocked.Exchange(ref _latched, next).Key is { } replaced)
        {
            CryptographicOperations.ZeroMemory(replaced);
        }

        return result;
    }

    /// <summary>
    /// Writes a fresh key, reads it back, and publishes Present only when the read-back matches.
    /// </summary>
    private MemoryErasureKeyOpenResult Create()
    {
        byte[] created = RandomNumberGenerator.GetBytes(KeyBytes);

        try
        {
            if (Set(Base64Url.EncodeToString(created)) is not OsCredentialStoreStatus.Ok)
            {
                return Publish(MemoryErasureKeyState.Unavailable, null);
            }

            // An echo of what was handed in is not proof of persistence, so the key is read back and
            // compared before anything is recorded under it.
            (MemoryErasureKeyState state, byte[]? confirmed) = Probe();

            if (state is not MemoryErasureKeyState.Present
                || confirmed is null
                || !CryptographicOperations.FixedTimeEquals(confirmed, created))
            {
                if (confirmed is not null)
                {
                    CryptographicOperations.ZeroMemory(confirmed);
                }

                return Publish(MemoryErasureKeyState.Unavailable, null);
            }

            return Publish(MemoryErasureKeyState.Present, confirmed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(created);
        }
    }

    /// <summary>Classifies one read of the account. Present always carries the decoded key.</summary>
    private (MemoryErasureKeyState State, byte[]? Key) Probe()
    {
        OsCredentialStoreResult result;

        try
        {
            result = _credentials.TryGet(ArcanumCredentialIdentity.Service, Account);
        }
        catch (Exception exception) when (IsStoreFault(exception))
        {
            return (MemoryErasureKeyState.Unavailable, null);
        }

        if (result.Status is OsCredentialStoreStatus.NotFound)
        {
            return (MemoryErasureKeyState.Absent, null);
        }

        if (result.Status is not OsCredentialStoreStatus.Ok)
        {
            return (MemoryErasureKeyState.Unavailable, null);
        }

        // An Ok with an empty value is malformed, not absent: an absent slot reports NotFound.
        return result.Value is { Length: EncodedKeyCharacters } encoded
            && TryDecodeCanonical(encoded, out byte[] decoded)
                ? (MemoryErasureKeyState.Present, decoded)
                : (MemoryErasureKeyState.Malformed, null);
    }

    private OsCredentialStoreStatus Set(string value)
    {
        try
        {
            return _credentials.Set(ArcanumCredentialIdentity.Service, Account, value).Status;
        }
        catch (Exception exception) when (IsStoreFault(exception))
        {
            return OsCredentialStoreStatus.Failed;
        }
    }

    private static bool IsStoreFault(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or NotSupportedException;

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

        // The status overload rather than TryDecodeFromChars, which throws FormatException instead of
        // returning false when the unused low bits of the final character are set.
        if (Base64Url.DecodeFromChars(encoded, buffer, out int consumed, out int written)
                is not OperationStatus.Done
            || consumed != encoded.Length
            || written != KeyBytes)
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

    /// <summary>One published latch. <see cref="Key"/> is set exactly when the state is Present.</summary>
    private sealed record Latched(MemoryErasureKeyState State, byte[]? Key, byte[]? KeyId);
}
