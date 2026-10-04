using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// Erasure keys for suites that must never reach the real keychain.
/// </summary>
/// <remarks>
/// A key is created by one keyring and read by another, fresh one, so the reader's latch starts
/// <see cref="MemoryErasureKeyState.Unresolved"/> exactly as a new process's does. Fingerprints are
/// seeded through the evidence store's own insert with a real key, because until the erase routes
/// exist there is no production path that writes one.
/// </remarks>
internal static class MemoryErasureTestKeys
{
    /// <summary>A keyring with its own fresh latch over <paramref name="credentials"/>, or an empty store.</summary>
    internal static MemoryErasureKeyring Isolated(IOsCredentialStore? credentials = null) =>
        new(credentials ?? new InMemoryOsCredentialStore());

    /// <summary>
    /// Creates the key in <paramref name="credentials"/> through the production creator, as the first
    /// erase would on an installation with no evidence, and returns a private copy of it.
    /// </summary>
    internal static MemoryErasureKey CreateKey(IOsCredentialStore credentials)
    {
        using MemoryErasureKeyring creator = new(credentials);

        MemoryErasureKeyOpenResult created = creator.OpenOrCreate(evidenceRowsExist: false);

        Assert.Equal(MemoryErasureKeyState.Present, created.State);

        return created.Key!;
    }

    /// <summary>Records the fingerprint <paramref name="key"/> computes for <paramref name="identity"/>.</summary>
    internal static Task SeedFingerprintAsync(
        SqliteConnection connection,
        MemoryErasureKey key,
        MemoryErasureIdentity identity,
        CancellationToken cancellationToken) =>
        MemoryErasureEvidence.InsertFingerprintAsync(
            connection,
            null,
            key.Fingerprint(identity),
            identity.Store,
            key.KeyId.ToArray(),
            cancellationToken);
}

/// <summary>
/// Counts every call a keyring makes into the credential store, and can make every one of them fail.
/// </summary>
/// <remarks>
/// A count of zero is how a suite proves an installation with no evidence did no keychain I/O, which
/// on macOS is the difference between a silent write and a prompt.
/// </remarks>
internal sealed class CountingOsCredentialStore(InMemoryOsCredentialStore inner)
    : IOsCredentialStore, IOsCredentialPresenceProbe
{
    private int _calls;

    private int _secretReads;

    private int _presenceProbes;

    /// <summary>Every member call so far, whatever it returned.</summary>
    internal int Calls => Volatile.Read(ref _calls);

    /// <summary>The calls that read the secret, which is what can raise a keychain prompt.</summary>
    internal int SecretReads => Volatile.Read(ref _secretReads);

    /// <summary>The metadata-only calls that asked whether the item exists.</summary>
    internal int PresenceProbes => Volatile.Read(ref _presenceProbes);

    /// <summary>When set, every member answers with this status instead of asking the inner store.</summary>
    internal OsCredentialStoreStatus? FailWith { get; set; }

    public bool IsAvailable
    {
        get
        {
            _ = Interlocked.Increment(ref _calls);

            return FailWith is null && inner.IsAvailable;
        }
    }

    public OsCredentialStoreResult TryGet(string service, string account)
    {
        _ = Interlocked.Increment(ref _calls);

        _ = Interlocked.Increment(ref _secretReads);

        return FailWith is { } status ? Failed(status) : inner.TryGet(service, account);
    }

    public OsCredentialStoreResult Set(string service, string account, string secret)
    {
        _ = Interlocked.Increment(ref _calls);

        return FailWith is { } status ? Failed(status) : inner.Set(service, account, secret);
    }

    public OsCredentialStoreResult Delete(string service, string account)
    {
        _ = Interlocked.Increment(ref _calls);

        return FailWith is { } status ? Failed(status) : inner.Delete(service, account);
    }

    public OsCredentialStoreStatus ProbePresence(string service, string account)
    {
        _ = Interlocked.Increment(ref _calls);

        _ = Interlocked.Increment(ref _presenceProbes);

        return FailWith ?? inner.ProbePresence(service, account);
    }

    private static OsCredentialStoreResult Failed(OsCredentialStoreStatus status) =>
        new(status, null, "test credential store failure");
}
