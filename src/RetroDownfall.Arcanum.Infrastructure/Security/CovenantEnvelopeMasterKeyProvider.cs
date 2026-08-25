using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
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
/// <para>Counters are per purpose, start at one, and are handed out under the same lock that copies
/// their key. At the rollover bound the family stops issuing rather than wrapping; wrapping would
/// silently reuse a nonce under a live key.</para>
/// </remarks>
internal sealed class CovenantEnvelopeMasterKeyProvider
    : ICovenantEnvelopeMasterKeyProvider, ICovenantDiagnosticKeySource, IDisposable
{

    /// <summary>The exact HKDF salt separating the installation root from every other derivation.</summary>
    private static readonly byte[] RootSalt = Encoding.UTF8.GetBytes("Arcanum.Covenant.EnvelopeRoot.v1");

    /// <summary>The exact HKDF salt separating one purpose key from another under the same root.</summary>
    private static readonly byte[] PurposeSalt = Encoding.UTF8.GetBytes("Arcanum.Covenant.PurposeKey.v1");

    private const string DiagnosticLabel = "Arcanum.Covenant.Diagnostics.v1";

    private readonly CovenantRuntimeGenerationProvider _runtime;

    private readonly bool _ownsRuntime;

    private readonly ICovenantEnvelopeDerivationCheckpoint _derivationCheckpoint;

    private readonly ICovenantEnvelopeKeyAccessCheckpoint _keyAccessCheckpoint;

    private readonly byte[] _bootSalt = RandomNumberGenerator.GetBytes(32);

    private byte[]? _root;

    private bool _disposed;

    public CovenantEnvelopeMasterKeyProvider()
        : this(
            new CovenantRuntimeGenerationProvider(),
            CovenantEnvelopeDerivationCheckpoint.None,
            CovenantEnvelopeKeyAccessCheckpoint.None,
            ownsRuntime: true)
    {
    }

    internal CovenantEnvelopeMasterKeyProvider(CovenantRuntimeGenerationProvider runtime)
        : this(
            runtime,
            CovenantEnvelopeDerivationCheckpoint.None,
            CovenantEnvelopeKeyAccessCheckpoint.None,
            ownsRuntime: false)
    {
    }

    internal CovenantEnvelopeMasterKeyProvider(
        ICovenantEnvelopeDerivationCheckpoint derivationCheckpoint)
        : this(
            new CovenantRuntimeGenerationProvider(),
            derivationCheckpoint,
            CovenantEnvelopeKeyAccessCheckpoint.None,
            ownsRuntime: true)
    {
    }

    internal CovenantEnvelopeMasterKeyProvider(
        ICovenantEnvelopeDerivationCheckpoint derivationCheckpoint,
        ICovenantEnvelopeKeyAccessCheckpoint keyAccessCheckpoint)
        : this(
            new CovenantRuntimeGenerationProvider(),
            derivationCheckpoint,
            keyAccessCheckpoint,
            ownsRuntime: true)
    {
    }

    internal CovenantEnvelopeMasterKeyProvider(
        CovenantRuntimeGenerationProvider runtime,
        ICovenantEnvelopeDerivationCheckpoint derivationCheckpoint,
        ICovenantEnvelopeKeyAccessCheckpoint keyAccessCheckpoint,
        bool ownsRuntime = false)
    {

        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

        _derivationCheckpoint =
            derivationCheckpoint ?? throw new ArgumentNullException(nameof(derivationCheckpoint));

        _keyAccessCheckpoint =
            keyAccessCheckpoint ?? throw new ArgumentNullException(nameof(keyAccessCheckpoint));

        _ownsRuntime = ownsRuntime;

    }

    /// <inheritdoc/>
    public CovenantEnvelopeKeyGeneration? Current => _runtime.Current.Keys;

    internal CovenantRuntimeGenerationProvider Runtime => _runtime;

    /// <summary>
    /// Takes startup master material once and prepares the key families available from persisted state.
    /// </summary>
    /// <remarks>
    /// <paramref name="masterKeyMaterial"/> is zeroized before this returns, whether it succeeded or
    /// not. The caller keeps no copy: the whole reason this method takes a mutable buffer rather than
    /// a string is that a string cannot be cleared. The returned generation stays caller-owned and
    /// unpublished until the composite runtime holder initializes keys and authority together.
    ///
    /// <para>Deliberately does not latch <see cref="CovenantProcessResidence"/>. Startup derives here
    /// unconditionally, before anything has consulted <c>Arcanum:Features:Covenant</c>, because the
    /// recovery-keyed families are what let a factory erasure fence protected state on an installation
    /// that never enabled the feature. Latching for that derivation closed the offline host-tools
    /// transition on every installation the moment it booted — including inside the offline command's
    /// own process, which bootstraps the Grimoire before it can run the transition it exists to
    /// perform. Residence begins where Covenant content does, at
    /// <see cref="CovenantConnectionSource.GetOpenConnectionAsync"/> (§10.12).</para>
    /// </remarks>
    internal Result<CovenantPreparedEnvelopeKeyGeneration> PrepareInitial(
        Span<byte> masterKeyMaterial,
        CovenantEnvelopeBootstrapKeyInput input)
    {

        ArgumentNullException.ThrowIfNull(input);

        try
        {

            if (masterKeyMaterial.IsEmpty)
            {
                return Result<CovenantPreparedEnvelopeKeyGeneration>.Failure(
                    new Error(
                        ErrorCodes.Covenant.OperatorAuthorityUnavailable,
                        "No master key material is available to derive Covenant envelope keys."));
            }

            using Lock.Scope scope = _runtime.EnterScope();

            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_root is not null)
            {
                return Result<CovenantPreparedEnvelopeKeyGeneration>.Failure(
                    new Error(
                        ErrorCodes.Covenant.LifecycleConflict,
                        "Covenant envelope master material has already been taken for this process."));
            }

            byte[] root = new byte[32];

            byte[] rootBinding = Encoding.UTF8.GetBytes(input.InstallationIdentity);

            try
            {

                try
                {

                    HKDF.DeriveKey(
                        HashAlgorithmName.SHA256,
                        masterKeyMaterial,
                        root,
                        salt: RootSalt,
                        info: rootBinding);

                }
                finally
                {

                    ZeroAndObserve(rootBinding, CovenantEnvelopeSensitiveBufferKind.RootBinding);

                }

                CovenantEnvelopeKeyGeneration generation = Derive(root, input);

                _root = root;

                return Result<CovenantPreparedEnvelopeKeyGeneration>.Success(
                    new CovenantPreparedEnvelopeKeyGeneration(_runtime, generation));

            }
            catch (Exception)
            {

                CryptographicOperations.ZeroMemory(root);

                return Result<CovenantPreparedEnvelopeKeyGeneration>.Failure(DerivationFailure().Error);

            }

        }
        finally
        {

            CryptographicOperations.ZeroMemory(masterKeyMaterial);

        }

    }

    /// <summary>
    /// Derives a fresh unpublished generation whose caller owns until publication or abandonment.
    /// </summary>
    public Result<CovenantPreparedEnvelopeKeyGeneration> PrepareRekey(
        CovenantCommittedAuthorityTransition transition)
    {

        ArgumentNullException.ThrowIfNull(transition);

        using (_runtime.EnterScope())
        {

            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_root is not { } root)
            {
                return Result<CovenantPreparedEnvelopeKeyGeneration>.Failure(
                    new Error(
                        ErrorCodes.Covenant.OperatorAuthorityUnavailable,
                        "Covenant envelope master material has not been established."));
            }

            try
            {

                CovenantEnvelopeKeyGeneration generation = Derive(
                    root,
                    new CovenantEnvelopeBootstrapKeyInput(
                        transition.InstallationIdentity,
                        transition.MasterKeyVersion,
                        transition.CanonicalEnvelopeEpoch,
                        transition.RecoveryEnvelopeEpoch,
                        transition.Capability.DatasetGeneration));

                return Result<CovenantPreparedEnvelopeKeyGeneration>.Success(
                    new CovenantPreparedEnvelopeKeyGeneration(_runtime, generation));

            }
            catch (Exception)
            {
                return Result<CovenantPreparedEnvelopeKeyGeneration>.Failure(DerivationFailure().Error);
            }

        }

    }

    /// <inheritdoc/>
    public bool TryCopyDiagnosticKey(Span<byte> destination, out uint keyVersion)
    {

        keyVersion = 0;

        using (_runtime.EnterScope())
        {

            if (_runtime.Current.Keys is not { } generation)
            {
                return false;
            }

            return generation.TryCopyDiagnosticKey(
                destination,
                _keyAccessCheckpoint,
                out keyVersion);

        }

    }

    public CovenantEnvelopeKeyCopyStatus TryCopyPurposeKeyAndReserve(
        CovenantEnvelopePurpose purpose,
        Span<byte> destination,
        out CovenantEnvelopeKeyReservation reservation)
    {

        using (_runtime.EnterScope())
        {

            CovenantRuntimeGenerationState current = _runtime.Current;

            if (current.Keys is not { } generation)
            {

                reservation = default;

                return CovenantEnvelopeKeyCopyStatus.NoGeneration;

            }

            return generation.TryCopyPurposeKeyAndReserve(
                purpose,
                destination,
                current.RuntimeAuthorityGeneration,
                out reservation);

        }

    }

    public CovenantEnvelopeKeyCopyStatus TryCopyPurposeKey(
        CovenantEnvelopePurpose purpose,
        Span<byte> destination,
        out CovenantEnvelopeKeyCapture capture)
    {

        using (_runtime.EnterScope())
        {

            CovenantRuntimeGenerationState current = _runtime.Current;

            if (current.Keys is not { } generation)
            {

                capture = default;

                return CovenantEnvelopeKeyCopyStatus.NoGeneration;

            }

            return generation.TryCopyPurposeKey(
                purpose,
                destination,
                current.RuntimeAuthorityGeneration,
                out capture);

        }

    }

    public CovenantEnvelopeMaterializationLease AcquireMaterializationLease(
        long runtimeAuthorityGeneration,
        CovenantEnvelopeKeyGenerationIdentity identity)
    {

        ArgumentNullException.ThrowIfNull(identity);

        Lock.Scope scope = _runtime.EnterScope();

        CovenantRuntimeGenerationState current = _runtime.Current;

        bool isCurrent = current.RuntimeAuthorityGeneration == runtimeAuthorityGeneration
            && current.Keys is { } generation
            && ReferenceEquals(generation.Identity, identity);

        return new CovenantEnvelopeMaterializationLease(scope, isCurrent);

    }

    /// <inheritdoc/>
    public void Dispose()
    {

        using (_runtime.EnterScope())
        {

            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_root is { } root)
            {

                CryptographicOperations.ZeroMemory(root);

                _root = null;

            }

            CryptographicOperations.ZeroMemory(_bootSalt);

        }

        if (_ownsRuntime)
        {

            _runtime.Dispose();

        }

    }

    private CovenantEnvelopeKeyGeneration Derive(
        byte[] root,
        CovenantEnvelopeBootstrapKeyInput input)
    {

        byte[][] purposeKeys = new byte[CovenantEnvelopeKeyGeneration.PurposeCount][];

        byte[] generationSalt = RandomNumberGenerator.GetBytes(32);

        byte[]? diagnosticKey = null;

        int purposeKeysDerived = 0;

        try
        {

            // Deliberately boot-salt-free and epoch-free: a diagnostic tag has to correlate across
            // restarts and across dataset resets, and only a master-key rotation should change it.
            byte[] diagnosticBinding = Encoding.UTF8.GetBytes(input.InstallationIdentity);

            try
            {

                byte[] diagnosticInfo = BuildInfo(
                    DiagnosticLabel,
                    bootSalt: [],
                    generationSalt: [],
                    epoch: input.MasterKeyVersion,
                    binding: diagnosticBinding);

                try
                {

                    diagnosticKey = HKDF.DeriveKey(
                        HashAlgorithmName.SHA256,
                        root,
                        outputLength: 32,
                        salt: PurposeSalt,
                        info: diagnosticInfo);

                    _derivationCheckpoint.Reached(
                        CovenantEnvelopeDerivationStep.DiagnosticKeyDerived,
                        purposeKeysDerived);

                }
                finally
                {

                    ZeroAndObserve(
                        diagnosticInfo,
                        CovenantEnvelopeSensitiveBufferKind.DiagnosticInfo);

                }

            }
            finally
            {

                ZeroAndObserve(
                    diagnosticBinding,
                    CovenantEnvelopeSensitiveBufferKind.DiagnosticBinding);

            }

            foreach (CovenantEnvelopePurpose purpose in Enum.GetValues<CovenantEnvelopePurpose>())
            {

                bool datasetKeyed = CovenantEnvelopeLimits.IsDatasetKeyed(purpose);

                // A dataset-keyed purpose with no dataset generation has nothing to bind to. Deriving a
                // key from a placeholder would produce tokens that survive the very reset that removed
                // the dataset, so the family stays unkeyed and every issuance fails closed instead.
                if (datasetKeyed && input.DatasetGeneration is null)
                {

                    purposeKeys[(int)purpose - 1] = [];

                    continue;

                }

                long epoch = datasetKeyed
                    ? input.CanonicalEnvelopeEpoch
                    : input.RecoveryEnvelopeEpoch;

                byte[] binding = datasetKeyed
                    ? input.DatasetGeneration!.Value.ToByteArray(bigEndian: true)
                    : Encoding.UTF8.GetBytes(input.InstallationIdentity);

                try
                {

                    byte[] info = BuildInfo(
                        CovenantEnvelopeLimits.Label(purpose),
                        _bootSalt,
                        generationSalt,
                        epoch,
                        binding);

                    try
                    {

                        purposeKeys[(int)purpose - 1] = HKDF.DeriveKey(
                            HashAlgorithmName.SHA256,
                            root,
                            outputLength: 32,
                            salt: PurposeSalt,
                            info: info);

                        purposeKeysDerived++;

                        _derivationCheckpoint.Reached(
                            CovenantEnvelopeDerivationStep.PurposeKeyDerived,
                            purposeKeysDerived);

                    }
                    finally
                    {

                        ZeroAndObserve(info, CovenantEnvelopeSensitiveBufferKind.PurposeInfo);

                    }

                }
                finally
                {

                    ZeroAndObserve(binding, CovenantEnvelopeSensitiveBufferKind.PurposeBinding);

                }

            }

            return new CovenantEnvelopeKeyGeneration(
                new CovenantEnvelopeKeySnapshot(
                    input.MasterKeyVersion,
                    input.CanonicalEnvelopeEpoch,
                    input.RecoveryEnvelopeEpoch,
                    input.InstallationIdentity,
                    input.DatasetGeneration),
                purposeKeys,
                diagnosticKey,
                _derivationCheckpoint);

        }
        catch
        {

            foreach (byte[]? purposeKey in purposeKeys)
            {

                if (purposeKey is { Length: > 0 })
                {
                    ZeroAndObserve(purposeKey, CovenantEnvelopeSensitiveBufferKind.PurposeKey);
                }

            }

            if (diagnosticKey is not null)
            {
                ZeroAndObserve(diagnosticKey, CovenantEnvelopeSensitiveBufferKind.DiagnosticKey);
            }

            throw;

        }
        finally
        {

            ZeroAndObserve(generationSalt, CovenantEnvelopeSensitiveBufferKind.GenerationSalt);

        }

    }

    private static byte[] BuildInfo(
        string label,
        ReadOnlySpan<byte> bootSalt,
        ReadOnlySpan<byte> generationSalt,
        long epoch,
        ReadOnlySpan<byte> binding)
    {

        int labelBytes = Encoding.UTF8.GetByteCount(label);

        byte[] info = new byte[
            labelBytes + 1 + bootSalt.Length + generationSalt.Length + 8 + binding.Length];

        _ = Encoding.UTF8.GetBytes(label, info);

        info[labelBytes] = 0;

        int offset = labelBytes + 1;

        bootSalt.CopyTo(info.AsSpan(offset));

        offset += bootSalt.Length;

        generationSalt.CopyTo(info.AsSpan(offset));

        offset += generationSalt.Length;

        BinaryPrimitives.WriteInt64BigEndian(info.AsSpan(offset), epoch);

        offset += 8;

        binding.CopyTo(info.AsSpan(offset));

        return info;

    }

    private void ZeroAndObserve(byte[] buffer, CovenantEnvelopeSensitiveBufferKind kind)
    {

        CryptographicOperations.ZeroMemory(buffer);

        _derivationCheckpoint.Zeroized(kind, IsZero(buffer));

    }

    private static bool IsZero(ReadOnlySpan<byte> buffer)
    {

        foreach (byte value in buffer)
        {

            if (value != 0)
            {
                return false;
            }

        }

        return true;

    }

    private static Result DerivationFailure() =>
        Result.Failure(
            new Error(
                ErrorCodes.Covenant.MaintenanceFailed,
                "Covenant envelope key derivation failed."));

}

/// <summary>Content-free checkpoints exposed only for deterministic derivation fault tests.</summary>
internal interface ICovenantEnvelopeDerivationCheckpoint
{

    void Reached(CovenantEnvelopeDerivationStep step, int purposeKeysDerived);

    void Zeroized(CovenantEnvelopeSensitiveBufferKind kind, bool isZero);

}

internal enum CovenantEnvelopeDerivationStep
{

    DiagnosticKeyDerived = 1,

    PurposeKeyDerived = 2,

}

internal enum CovenantEnvelopeSensitiveBufferKind
{

    RootBinding = 1,

    GenerationSalt = 2,

    PurposeBinding = 3,

    PurposeInfo = 4,

    DiagnosticBinding = 5,

    DiagnosticInfo = 6,

    PurposeKey = 7,

    DiagnosticKey = 8,

}

internal static class CovenantEnvelopeDerivationCheckpoint
{

    internal static ICovenantEnvelopeDerivationCheckpoint None { get; } = new NoOpCheckpoint();

    private sealed class NoOpCheckpoint : ICovenantEnvelopeDerivationCheckpoint
    {

        public void Reached(CovenantEnvelopeDerivationStep step, int purposeKeysDerived)
        {
        }

        public void Zeroized(CovenantEnvelopeSensitiveBufferKind kind, bool isZero)
        {
        }

    }

}

/// <summary>Content-free checkpoints exposed only for deterministic key-copy race tests.</summary>
internal interface ICovenantEnvelopeKeyAccessCheckpoint
{

    void Reached(CovenantEnvelopeKeyAccessStep step);

}

internal enum CovenantEnvelopeKeyAccessStep
{

    DiagnosticKeyCopied = 1,

}

internal static class CovenantEnvelopeKeyAccessCheckpoint
{

    internal static ICovenantEnvelopeKeyAccessCheckpoint None { get; } = new NoOpCheckpoint();

    private sealed class NoOpCheckpoint : ICovenantEnvelopeKeyAccessCheckpoint
    {

        public void Reached(CovenantEnvelopeKeyAccessStep step)
        {
        }

    }

}

/// <summary>
/// Startup-only persisted facts used to derive a complete or recovery-only envelope generation.
/// </summary>
internal sealed record CovenantEnvelopeBootstrapKeyInput
{

    internal CovenantEnvelopeBootstrapKeyInput(
        string installationIdentity,
        uint masterKeyVersion,
        long canonicalEnvelopeEpoch,
        long recoveryEnvelopeEpoch,
        Guid? datasetGeneration)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(installationIdentity);

        if (masterKeyVersion == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(masterKeyVersion));
        }

        if (canonicalEnvelopeEpoch <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(canonicalEnvelopeEpoch));
        }

        if (recoveryEnvelopeEpoch <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(recoveryEnvelopeEpoch));
        }

        if (datasetGeneration == Guid.Empty)
        {
            throw new ArgumentException(
                "An empty dataset generation is indistinguishable from an absent one.",
                nameof(datasetGeneration));
        }

        InstallationIdentity = installationIdentity;

        MasterKeyVersion = masterKeyVersion;

        CanonicalEnvelopeEpoch = canonicalEnvelopeEpoch;

        RecoveryEnvelopeEpoch = recoveryEnvelopeEpoch;

        DatasetGeneration = datasetGeneration;

    }

    internal string InstallationIdentity { get; }

    internal uint MasterKeyVersion { get; }

    internal long CanonicalEnvelopeEpoch { get; }

    internal long RecoveryEnvelopeEpoch { get; }

    internal Guid? DatasetGeneration { get; }

}

/// <summary>
/// Owns one unpublished key generation until it is abandoned or transferred for publication.
/// </summary>
internal sealed class CovenantPreparedEnvelopeKeyGeneration : IDisposable
{

    private readonly object _owner;

    private CovenantEnvelopeKeyGeneration? _generation;

    internal CovenantPreparedEnvelopeKeyGeneration(
        object owner,
        CovenantEnvelopeKeyGeneration generation)
    {

        _owner = owner ?? throw new ArgumentNullException(nameof(owner));

        _generation = generation ?? throw new ArgumentNullException(nameof(generation));

    }

    internal bool IsOwnedBy(object owner) => ReferenceEquals(_owner, owner);

    internal bool Matches(CovenantCommittedAuthorityTransition transition)
    {

        ArgumentNullException.ThrowIfNull(transition);

        CovenantEnvelopeKeySnapshot? snapshot = Volatile.Read(ref _generation)?.Snapshot;

        return snapshot is not null
            && snapshot.MasterKeyVersion == transition.MasterKeyVersion
            && snapshot.CanonicalEnvelopeEpoch == transition.CanonicalEnvelopeEpoch
            && snapshot.RecoveryEnvelopeEpoch == transition.RecoveryEnvelopeEpoch
            && string.Equals(
                snapshot.InstallationIdentity,
                transition.InstallationIdentity,
                StringComparison.Ordinal)
            && snapshot.DatasetGeneration == transition.Capability.DatasetGeneration;

    }

    internal bool Matches(
        CovenantAuthoritySnapshot authority,
        CovenantAvailabilitySnapshot availability)
    {

        ArgumentNullException.ThrowIfNull(authority);

        ArgumentNullException.ThrowIfNull(availability);

        CovenantEnvelopeKeySnapshot? snapshot = Volatile.Read(ref _generation)?.Snapshot;

        return snapshot is not null
            && snapshot.MasterKeyVersion == authority.MasterKeyVersion
            && snapshot.RecoveryEnvelopeEpoch == authority.RecoveryEnvelopeEpoch
            && string.Equals(
                snapshot.InstallationIdentity,
                authority.InstallationIdentity,
                StringComparison.Ordinal)
            && (snapshot.DatasetGeneration is null
                || snapshot.DatasetGeneration == availability.DatasetGeneration);

    }

    internal CovenantEnvelopeKeyGeneration Take() =>
        Interlocked.Exchange(ref _generation, null)
        ?? throw new InvalidOperationException("This prepared Covenant envelope generation is no longer owned.");

    public void Dispose() => Interlocked.Exchange(ref _generation, null)?.Dispose();

}

/// <summary>
/// The single seam the codec and tagger read their key material through.
/// </summary>
internal interface ICovenantEnvelopeMasterKeyProvider
{

    /// <summary>The published generation, or <see langword="null"/> before initialization.</summary>
    CovenantEnvelopeKeyGeneration? Current { get; }

    CovenantEnvelopeKeyCopyStatus TryCopyPurposeKeyAndReserve(
        CovenantEnvelopePurpose purpose,
        Span<byte> destination,
        out CovenantEnvelopeKeyReservation reservation);

    CovenantEnvelopeKeyCopyStatus TryCopyPurposeKey(
        CovenantEnvelopePurpose purpose,
        Span<byte> destination,
        out CovenantEnvelopeKeyCapture capture);

    CovenantEnvelopeMaterializationLease AcquireMaterializationLease(
        long runtimeAuthorityGeneration,
        CovenantEnvelopeKeyGenerationIdentity identity);

}

internal enum CovenantEnvelopeKeyCopyStatus
{

    Success = 1,

    NoGeneration = 2,

    PurposeUnavailable = 3,

    CounterExhausted = 4,

}

internal sealed class CovenantEnvelopeKeyGenerationIdentity
{
}

/// <summary>
/// Holds the provider transition lock from identity proof through synchronous result materialization.
/// </summary>
internal ref struct CovenantEnvelopeMaterializationLease
{

    private Lock.Scope _scope;

    internal CovenantEnvelopeMaterializationLease(Lock.Scope scope, bool isCurrent)
    {

        _scope = scope;

        IsCurrent = isCurrent;

    }

    internal bool IsCurrent { get; }

    public void Dispose() => _scope.Dispose();

}

internal readonly record struct CovenantEnvelopeKeyReservation(
    long RuntimeAuthorityGeneration,
    CovenantEnvelopeKeyGenerationIdentity Identity,
    CovenantEnvelopeKeySnapshot Snapshot,
    long Epoch,
    ulong Counter);

internal readonly record struct CovenantEnvelopeKeyCapture(
    long RuntimeAuthorityGeneration,
    CovenantEnvelopeKeyGenerationIdentity Identity,
    CovenantEnvelopeKeySnapshot Snapshot,
    long Epoch);

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

    private readonly ICovenantEnvelopeDerivationCheckpoint _derivationCheckpoint;

    private readonly Lock _keyLock = new();

    private readonly CovenantEnvelopeKeyGenerationIdentity _identity = new();

    // Signed storage keeps the rollover comparison simple. The bound is far below long.MaxValue, so
    // the counter is refused long before the sign bit matters.
    private readonly long[] _counters = new long[PurposeCount];

    private int _disposed;

    internal CovenantEnvelopeKeyGeneration(
        CovenantEnvelopeKeySnapshot snapshot,
        byte[][] purposeKeys,
        byte[] diagnosticKey,
        ICovenantEnvelopeDerivationCheckpoint derivationCheckpoint)
    {

        Snapshot = snapshot;

        _purposeKeys = purposeKeys;

        _diagnosticKey = diagnosticKey;

        _derivationCheckpoint = derivationCheckpoint;

    }

    internal CovenantEnvelopeKeySnapshot Snapshot { get; }

    internal CovenantEnvelopeKeyGenerationIdentity Identity => _identity;

    internal bool TryCopyDiagnosticKey(
        Span<byte> destination,
        ICovenantEnvelopeKeyAccessCheckpoint checkpoint,
        out uint keyVersion)
    {

        lock (_keyLock)
        {

            keyVersion = 0;

            if (_disposed != 0 || destination.Length < _diagnosticKey.Length)
            {
                return false;
            }

            _diagnosticKey.CopyTo(destination);

            checkpoint.Reached(CovenantEnvelopeKeyAccessStep.DiagnosticKeyCopied);

            keyVersion = Snapshot.MasterKeyVersion;

            return true;

        }

    }

    internal CovenantEnvelopeKeyCopyStatus TryCopyPurposeKeyAndReserve(
        CovenantEnvelopePurpose purpose,
        Span<byte> destination,
        long runtimeAuthorityGeneration,
        out CovenantEnvelopeKeyReservation reservation)
    {

        lock (_keyLock)
        {

            reservation = default;

            if (_disposed != 0)
            {
                return CovenantEnvelopeKeyCopyStatus.NoGeneration;
            }

            byte[] key = _purposeKeys[(int)purpose - 1];

            if (key.Length != 32 || destination.Length < key.Length)
            {
                return CovenantEnvelopeKeyCopyStatus.PurposeUnavailable;
            }

            long reserved = ++_counters[(int)purpose - 1];

            if (reserved <= 0 || (ulong)reserved > CovenantEnvelopeLimits.CounterRolloverBound)
            {
                return CovenantEnvelopeKeyCopyStatus.CounterExhausted;
            }

            key.CopyTo(destination);

            reservation = new CovenantEnvelopeKeyReservation(
                runtimeAuthorityGeneration,
                _identity,
                Snapshot,
                EpochFor(purpose),
                (ulong)reserved);

            return CovenantEnvelopeKeyCopyStatus.Success;

        }

    }

    internal CovenantEnvelopeKeyCopyStatus TryCopyPurposeKey(
        CovenantEnvelopePurpose purpose,
        Span<byte> destination,
        long runtimeAuthorityGeneration,
        out CovenantEnvelopeKeyCapture capture)
    {

        lock (_keyLock)
        {

            capture = default;

            if (_disposed != 0)
            {
                return CovenantEnvelopeKeyCopyStatus.NoGeneration;
            }

            byte[] key = _purposeKeys[(int)purpose - 1];

            if (key.Length != 32 || destination.Length < key.Length)
            {
                return CovenantEnvelopeKeyCopyStatus.PurposeUnavailable;
            }

            key.CopyTo(destination);

            capture = new CovenantEnvelopeKeyCapture(
                runtimeAuthorityGeneration,
                _identity,
                Snapshot,
                EpochFor(purpose));

            return CovenantEnvelopeKeyCopyStatus.Success;

        }

    }

    /// <summary>
    /// The epoch this purpose family's key was derived under.
    /// </summary>
    internal long EpochFor(CovenantEnvelopePurpose purpose) =>
        CovenantEnvelopeLimits.IsDatasetKeyed(purpose)
            ? Snapshot.CanonicalEnvelopeEpoch
            : Snapshot.RecoveryEnvelopeEpoch;

    public void Dispose()
    {

        lock (_keyLock)
        {

            if (_disposed != 0)
            {
                return;
            }

            _disposed = 1;

            foreach (byte[] key in _purposeKeys)
            {

                if (key.Length > 0)
                {

                    CryptographicOperations.ZeroMemory(key);

                    _derivationCheckpoint.Zeroized(
                        CovenantEnvelopeSensitiveBufferKind.PurposeKey,
                        IsZero(key));

                }

            }

            CryptographicOperations.ZeroMemory(_diagnosticKey);

            _derivationCheckpoint.Zeroized(
                CovenantEnvelopeSensitiveBufferKind.DiagnosticKey,
                IsZero(_diagnosticKey));

        }

    }

    private static bool IsZero(ReadOnlySpan<byte> buffer)
    {

        foreach (byte value in buffer)
        {

            if (value != 0)
            {
                return false;
            }

        }

        return true;

    }

}
