using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// Builds the <see cref="GrimoireSchemaInitializationContext"/> the three install transactions run
/// against, from the master key this process actually holds.
/// </summary>
/// <remarks>
/// The fingerprint this produces is content-free evidence, never key material. It is
/// <c>SHA-256(UTF8("Arcanum.Covenant.MasterKeyFingerprint.v1\0") || keyBytes)</c>, a one-way digest
/// under an explicit domain label, so the durable row can prove "the master key changed" without the
/// raw key ever reaching the database, a log, or an operator surface. The literal NUL after the label
/// is the domain separator: without it a label and a key could be split at a different boundary and
/// two different inputs would collide on the same digest.
///
/// <para>The intermediate UTF-8 key array is zeroed in a <c>finally</c>. It is the only plaintext
/// copy this type makes, and leaving it for the garbage collector would put the master key in
/// reusable managed memory for an unbounded time.</para>
///
/// <para>The installation lock is <b>borrowed</b>, never acquired. The caller already holds the
/// process-wide <see cref="ArcanumMaintenanceLock"/>, which is a <c>FileShare.None</c> handle;
/// opening a second one from inside the startup it guards would block on the caller's own handle and
/// deadlock startup permanently. So this type asserts the caller's identity through
/// <see cref="ArcanumMaintenanceLock.AssertHeldFor(string)"/> and never calls
/// <c>TryAcquire</c>, <c>IsHeld</c>, or <c>Dispose</c>.</para>
/// </remarks>
internal sealed class CovenantAuthorityBootstrapper
{

    /// <summary>The generation a fresh installation starts at. Zero is excluded by the schema.</summary>
    private const long FreshAuthorityEpoch = 1;

    /// <summary>The first master key version. Zero would read as a default-initialized column.</summary>
    private const uint FreshMasterKeyVersion = 1;

    /// <summary>The recovery envelope generation a fresh installation starts at.</summary>
    private const long FreshRecoveryEnvelopeEpoch = 1;

    /// <summary>
    /// The domain label and its trailing NUL separator, encoded once. Prepending it means this digest
    /// can never collide with a digest of the same key computed for some other purpose.
    /// </summary>
    private static readonly byte[] FingerprintDomainLabel =
        Encoding.UTF8.GetBytes("Arcanum.Covenant.MasterKeyFingerprint.v1\0");

    /// <summary>
    /// Prepares the installation context while the caller holds the installation lock.
    /// </summary>
    /// <param name="heldInstallationLock">
    /// The live lock the caller already owns. It is asserted against
    /// <paramref name="guardedDirectory"/> and otherwise left completely alone.
    /// </param>
    /// <param name="guardedDirectory">The Grimoire directory the lock guards.</param>
    /// <param name="masterApiKey">The master key this process holds. Never stored, only digested.</param>
    /// <param name="installedAtUtc">The single timestamp every tier records for this installation.</param>
    public GrimoireSchemaInitializationContext PrepareUnderInstallationLock(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        string masterApiKey,
        DateTimeOffset installedAtUtc)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentException.ThrowIfNullOrWhiteSpace(guardedDirectory);

        ArgumentException.ThrowIfNullOrEmpty(masterApiKey);

        // Identity, not liveness, and deliberately no acquisition: see the type remarks.
        heldInstallationLock.AssertHeldFor(guardedDirectory);

        return Prepare(masterApiKey, installedAtUtc);

    }

    /// <summary>
    /// Prepares the same context for a caller that legitimately holds no installation lock, using the
    /// identical fingerprint computation.
    /// </summary>
    /// <remarks>
    /// The CLI coexists with a running host that already owns the lock, and a restore works on a
    /// staged copy nobody else can see. Neither may hard-fail for want of a lock, and neither may
    /// invent a different fingerprint rule, so both come through here rather than computing their own.
    /// </remarks>
    public static GrimoireSchemaInitializationContext PrepareWithoutInstallationLock(
        string masterApiKey,
        DateTimeOffset installedAtUtc)
    {

        ArgumentException.ThrowIfNullOrEmpty(masterApiKey);

        return Prepare(masterApiKey, installedAtUtc);

    }

    /// <summary>
    /// Digests the master key under the versioned domain label.
    /// </summary>
    /// <remarks>
    /// The returned 32 bytes are safe to store and compare; the input never is. Comparison at read
    /// time is <see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>
    /// in the Core initializer, so this value is treated as secret-adjacent everywhere it is used
    /// even though it discloses nothing.
    /// </remarks>
    public static byte[] ComputeMasterKeyFingerprint(string masterApiKey)
    {

        ArgumentException.ThrowIfNullOrEmpty(masterApiKey);

        byte[] keyBytes = Encoding.UTF8.GetBytes(masterApiKey);

        try
        {

            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            hash.AppendData(FingerprintDomainLabel);

            hash.AppendData(keyBytes);

            return hash.GetHashAndReset();

        }
        finally
        {

            CryptographicOperations.ZeroMemory(keyBytes);

        }

    }

    /// <summary>
    /// The one context shape both entry points produce.
    /// </summary>
    /// <remarks>
    /// The counters are always the fresh-installation values. Advancing them when the fingerprint has
    /// changed belongs to <see cref="CoreGrimoireSchemaDataInitializer"/>, which advances from the
    /// durable row inside the install transaction; a bootstrapper that guessed at the next generation
    /// from outside that transaction could only ever disagree with it.
    ///
    /// <para>The identity is minted the same way, and for the same reason. Both entry points used to
    /// accept an already-recorded identity to pass through instead, documented as what keeps an
    /// installation stable across restarts and key rotations — and no caller under <c>src</c> ever
    /// passed one, because none of them has one to pass: a fresh database has no row to read, and the
    /// restore path that does read one builds the whole context from that row without coming here.
    /// Stability is real, but it is the initializer's: it inserts only when no row exists, and its
    /// convergence statement lists the key-derived columns and deliberately not the identity. A
    /// parameter that documented someone else's guarantee and that nothing could supply was a promise
    /// this type was in no position to make.</para>
    /// </remarks>
    private static GrimoireSchemaInitializationContext Prepare(
        string masterApiKey,
        DateTimeOffset installedAtUtc) =>
        new(
            // Uppercase, matching how the rest of the Grimoire stores identities, so a value written
            // here compares equal to the same value written anywhere else.
            Guid.NewGuid().ToString().ToUpperInvariant(),
            FreshAuthorityEpoch,
            FreshMasterKeyVersion,
            ComputeMasterKeyFingerprint(masterApiKey),
            FreshRecoveryEnvelopeEpoch,
            installedAtUtc);

}
