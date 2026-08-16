using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// Holds the in-process key material behind all six envelope purposes and the diagnostic tagger.
/// </summary>
/// <remarks>
/// The master secret is taken exactly once, at startup, and is zeroized before the first envelope is
/// ever issued. What survives is an HKDF root, which is what lets a committed same-process epoch
/// transition derive fresh purpose keys without going back to the secret store — reopening that store
/// mid-operation would put an OS credential prompt inside a reset (§10.12).
///
/// <para>The boot salt is fresh per process and is mixed into every purpose key but not into the
/// diagnostic key. That asymmetry is the point of both. Envelope keys must never repeat across a
/// database snapshot rollback, because a restored counter under a repeated key is a repeated
/// (key, nonce) pair and AES-GCM offers nothing after that. Diagnostic tags must repeat across a
/// restart, because a correlation label that changed every boot would correlate nothing.</para>
///
/// <para>Counters are per purpose, start at one, and are handed out by interlocked increment. At the
/// rollover bound the family stops issuing rather than wrapping; wrapping would silently reuse a
/// nonce under a live key.</para>
/// </remarks>
internal sealed class CovenantEnvelopeMasterKeyProvider
    : ICovenantEnvelopeMasterKeyProvider, ICovenantDiagnosticKeySource, IDisposable
{

    /// <summary>The exact HKDF salt separating the installation root from every other derivation.</summary>
    private static readonly byte[] RootSalt = Encoding.UTF8.GetBytes("Arcanum.Covenant.EnvelopeRoot.v1");

    /// <summary>The exact HKDF salt separating one purpose key from another under the same root.</summary>
    private static readonly byte[] PurposeSalt = Encoding.UTF8.GetBytes("Arcanum.Covenant.PurposeKey.v1");

    private const string DiagnosticLabel = "Arcanum.Covenant.Diagnostics.v1";

    private readonly Lock _transitionLock = new();

    private readonly byte[] _bootSalt = RandomNumberGenerator.GetBytes(32);

    private byte[]? _root;

    private CovenantEnvelopeKeyGeneration? _current;

    private bool _disposed;

    /// <inheritdoc/>
    public CovenantEnvelopeKeyGeneration? Current => Volatile.Read(ref _current);

    /// <summary>
    /// Takes the startup master material once, derives the root, and publishes the first generation.
    /// </summary>
    /// <remarks>
    /// <paramref name="masterKeyMaterial"/> is zeroized before this returns, whether it succeeded or
    /// not. The caller keeps no copy: the whole reason this method takes a mutable buffer rather than
    /// a string is that a string cannot be cleared.
    /// </remarks>
    public Result Initialize(
        Span<byte> masterKeyMaterial,
        CovenantCommittedAuthorityTransition transition)
    {

        ArgumentNullException.ThrowIfNull(transition);

        // Deriving Covenant envelope keys puts Covenant-authorizing material in this process, so it
        // latches residence for the same reason a canonical connection does (§10.12).
        CovenantProcessResidence.MarkOpened();

        try
        {

            if (masterKeyMaterial.IsEmpty)
            {
                return Result.Failure(
                    new Error(
                        ErrorCodes.Covenant.OperatorAuthorityUnavailable,
                        "No master key material is available to derive Covenant envelope keys."));
            }

            lock (_transitionLock)
            {

                ObjectDisposedException.ThrowIf(_disposed, this);

                if (_root is not null)
                {
                    return Result.Failure(
                        new Error(
                            ErrorCodes.Covenant.LifecycleConflict,
                            "Covenant envelope master material has already been taken for this process."));
                }

                byte[] root = new byte[32];

                HKDF.DeriveKey(
                    HashAlgorithmName.SHA256,
                    masterKeyMaterial,
                    root,
                    salt: RootSalt,
                    info: Encoding.UTF8.GetBytes(transition.InstallationIdentity));

                _root = root;

                _ = Interlocked.Exchange(ref _current, Derive(root, transition));

            }

            return Result.Success();

        }
        finally
        {

            CryptographicOperations.ZeroMemory(masterKeyMaterial);

        }

    }

    /// <summary>
    /// Derives and publishes a fresh generation for one already-committed transition.
    /// </summary>
    /// <remarks>
    /// Derivation happens before the swap, so a derivation failure leaves the previous generation in
    /// force and the caller's gate closed rather than leaving the process with no keys at all.
    /// </remarks>
    public Result Rekey(CovenantCommittedAuthorityTransition transition)
    {

        ArgumentNullException.ThrowIfNull(transition);

        lock (_transitionLock)
        {

            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_root is not { } root)
            {
                return Result.Failure(
                    new Error(
                        ErrorCodes.Covenant.OperatorAuthorityUnavailable,
                        "Covenant envelope master material has not been established."));
            }

            CovenantEnvelopeKeyGeneration next = Derive(root, transition);

            CovenantEnvelopeKeyGeneration? previous = Interlocked.Exchange(ref _current, next);

            previous?.Dispose();

            return Result.Success();

        }

    }

    /// <inheritdoc/>
    public bool TryCopyDiagnosticKey(Span<byte> destination, out uint keyVersion)
    {

        keyVersion = 0;

        if (Volatile.Read(ref _current) is not { } generation)
        {
            return false;
        }

        ReadOnlySpan<byte> key = generation.DiagnosticKey;

        if (key.Length != 32 || destination.Length < key.Length)
        {
            return false;
        }

        key.CopyTo(destination);

        keyVersion = generation.Snapshot.MasterKeyVersion;

        return true;

    }

    /// <inheritdoc/>
    public void Dispose()
    {

        lock (_transitionLock)
        {

            if (_disposed)
            {
                return;
            }

            _disposed = true;

            Interlocked.Exchange(ref _current, null)?.Dispose();

            if (_root is { } root)
            {

                CryptographicOperations.ZeroMemory(root);

                _root = null;

            }

            CryptographicOperations.ZeroMemory(_bootSalt);

        }

    }

    private CovenantEnvelopeKeyGeneration Derive(
        byte[] root,
        CovenantCommittedAuthorityTransition transition)
    {

        byte[][] purposeKeys = new byte[CovenantEnvelopeKeyGeneration.PurposeCount][];

        foreach (CovenantEnvelopePurpose purpose in Enum.GetValues<CovenantEnvelopePurpose>())
        {

            bool datasetKeyed = CovenantEnvelopeLimits.IsDatasetKeyed(purpose);

            // A dataset-keyed purpose with no dataset generation has nothing to bind to. Deriving a
            // key from a placeholder would produce tokens that survive the very reset that removed
            // the dataset, so the family stays unkeyed and every issuance fails closed instead.
            if (datasetKeyed && transition.DatasetGeneration is null)
            {

                purposeKeys[(int)purpose - 1] = [];

                continue;

            }

            long epoch = datasetKeyed
                ? transition.CanonicalEnvelopeEpoch
                : transition.RecoveryEnvelopeEpoch;

            byte[] binding = datasetKeyed
                ? transition.DatasetGeneration!.Value.ToByteArray(bigEndian: true)
                : Encoding.UTF8.GetBytes(transition.InstallationIdentity);

            purposeKeys[(int)purpose - 1] = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                root,
                outputLength: 32,
                salt: PurposeSalt,
                info: BuildInfo(CovenantEnvelopeLimits.Label(purpose), _bootSalt, epoch, binding));

        }

        // Deliberately boot-salt-free and epoch-free: a diagnostic tag has to correlate across
        // restarts and across dataset resets, and only a master-key rotation should change it.
        byte[] diagnosticKey = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            root,
            outputLength: 32,
            salt: PurposeSalt,
            info: BuildInfo(
                DiagnosticLabel,
                bootSalt: [],
                epoch: transition.MasterKeyVersion,
                binding: Encoding.UTF8.GetBytes(transition.InstallationIdentity)));

        return new CovenantEnvelopeKeyGeneration(
            new CovenantEnvelopeKeySnapshot(
                transition.MasterKeyVersion,
                transition.CanonicalEnvelopeEpoch,
                transition.RecoveryEnvelopeEpoch,
                transition.InstallationIdentity,
                transition.DatasetGeneration),
            purposeKeys,
            diagnosticKey);

    }

    private static byte[] BuildInfo(string label, ReadOnlySpan<byte> bootSalt, long epoch, ReadOnlySpan<byte> binding)
    {

        int labelBytes = Encoding.UTF8.GetByteCount(label);

        byte[] info = new byte[labelBytes + 1 + bootSalt.Length + 8 + binding.Length];

        _ = Encoding.UTF8.GetBytes(label, info);

        info[labelBytes] = 0;

        int offset = labelBytes + 1;

        bootSalt.CopyTo(info.AsSpan(offset));

        offset += bootSalt.Length;

        BinaryPrimitives.WriteInt64BigEndian(info.AsSpan(offset), epoch);

        offset += 8;

        binding.CopyTo(info.AsSpan(offset));

        return info;

    }

}

/// <summary>
/// The single seam the codec and tagger read their key material through.
/// </summary>
internal interface ICovenantEnvelopeMasterKeyProvider
{

    /// <summary>The published generation, or <see langword="null"/> before initialization.</summary>
    CovenantEnvelopeKeyGeneration? Current { get; }

}

/// <summary>
/// One immutable set of purpose keys, their counters, and the nonsecret identity they bind.
/// </summary>
/// <remarks>
/// Replaced whole on a transition rather than mutated. A generation whose keys could be swapped
/// underneath a live counter would be exactly the (key, nonce) reuse the boot salt exists to prevent.
/// </remarks>
internal sealed class CovenantEnvelopeKeyGeneration : IDisposable
{

    internal const int PurposeCount = 6;

    private readonly byte[][] _purposeKeys;

    private readonly byte[] _diagnosticKey;

    // Signed storage because Interlocked has no unsigned overload worth the cast noise. The rollover
    // bound is far below long.MaxValue, so the counter is refused long before the sign bit matters.
    private readonly long[] _counters = new long[PurposeCount];

    private int _disposed;

    internal CovenantEnvelopeKeyGeneration(
        CovenantEnvelopeKeySnapshot snapshot,
        byte[][] purposeKeys,
        byte[] diagnosticKey)
    {

        Snapshot = snapshot;

        _purposeKeys = purposeKeys;

        _diagnosticKey = diagnosticKey;

    }

    internal CovenantEnvelopeKeySnapshot Snapshot { get; }

    /// <summary>
    /// The key for one purpose, or an empty span when that family is unkeyed in this generation.
    /// </summary>
    internal ReadOnlySpan<byte> PurposeKey(CovenantEnvelopePurpose purpose) =>
        Volatile.Read(ref _disposed) == 0
            ? _purposeKeys[(int)purpose - 1]
            : [];

    internal ReadOnlySpan<byte> DiagnosticKey =>
        Volatile.Read(ref _disposed) == 0
            ? _diagnosticKey
            : [];

    /// <summary>
    /// The epoch this purpose family's key was derived under.
    /// </summary>
    internal long EpochFor(CovenantEnvelopePurpose purpose) =>
        CovenantEnvelopeLimits.IsDatasetKeyed(purpose)
            ? Snapshot.CanonicalEnvelopeEpoch
            : Snapshot.RecoveryEnvelopeEpoch;

    /// <summary>
    /// Reserves the next issuance ordinal, or reports that this family must re-key first.
    /// </summary>
    internal bool TryReserveCounter(CovenantEnvelopePurpose purpose, out ulong counter)
    {

        long reserved = Interlocked.Increment(ref _counters[(int)purpose - 1]);

        if (reserved > 0 && (ulong)reserved <= CovenantEnvelopeLimits.CounterRolloverBound)
        {

            counter = (ulong)reserved;

            return true;

        }

        counter = 0;

        return false;

    }

    public void Dispose()
    {

        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (byte[] key in _purposeKeys)
        {
            CryptographicOperations.ZeroMemory(key);
        }

        CryptographicOperations.ZeroMemory(_diagnosticKey);

    }

}
