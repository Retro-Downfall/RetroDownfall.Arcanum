using System.Runtime.InteropServices;
using System.Text;

namespace RetroDownfall.Arcanum.Secrets.Security;

/// <summary>
/// Selects the platform-native fixed-slot capability, or reports that none answers.
/// </summary>
/// <remarks>
/// The slot is fixed here and nowhere else: every backend below is handed
/// <see cref="ArcanumCredentialIdentity.Service"/> and
/// <see cref="ArcanumCredentialIdentity.HostProcessToolsTaintAccount"/> by this type, never by a
/// caller. A reset that could choose its own service and account would be a general credential
/// deleter wearing a marker's name.
/// </remarks>
internal sealed class HostProcessToolsMarkerCredentialCapabilitySource
    : IHostProcessToolsMarkerCredentialCapabilitySource
{

    private readonly IHostProcessToolsMarkerCredentialCapabilitySource _inner;

    internal HostProcessToolsMarkerCredentialCapabilitySource() =>
        _inner = CreatePlatformSource();

    /// <summary>Test seam: drive an arbitrary fixed-slot backend.</summary>
    internal HostProcessToolsMarkerCredentialCapabilitySource(
        IHostProcessToolsMarkerCredentialCapabilitySource inner) =>
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public HostProcessToolsMarkerCredentialOpenResult OpenFixedSlot()
    {

        try
        {

            return _inner.OpenFixedSlot();

        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {

            return HostProcessToolsMarkerCredentialOpenResult.Unavailable();

        }

    }

    public HostProcessToolsMarkerCredentialAbsenceResult ProveFixedSlotDurablyAbsent()
    {

        try
        {

            return _inner.ProveFixedSlotDurablyAbsent();

        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {

            return HostProcessToolsMarkerCredentialAbsenceResult.Unavailable();

        }

    }

    private static IHostProcessToolsMarkerCredentialCapabilitySource CreatePlatformSource()
    {

        if (OperatingSystem.IsMacOS())
        {

            return new MacOsHostProcessToolsMarkerSlot();

        }

        if (OperatingSystem.IsWindows())
        {

            return new WindowsHostProcessToolsMarkerSlot();

        }

        if (OperatingSystem.IsLinux())
        {

            return new LinuxHostProcessToolsMarkerSlot();

        }

        // A platform with no credential backend cannot be holding a marker, but it also cannot prove
        // one absent, and this source is only ever consulted by a reset that has to know which.
        return new UnavailableHostProcessToolsMarkerSlot();

    }

}

/// <summary>The arm for a platform with no credential backend at all.</summary>
internal sealed class UnavailableHostProcessToolsMarkerSlot
    : IHostProcessToolsMarkerCredentialCapabilitySource
{

    public HostProcessToolsMarkerCredentialOpenResult OpenFixedSlot() =>
        HostProcessToolsMarkerCredentialOpenResult.Unavailable();

    public HostProcessToolsMarkerCredentialAbsenceResult ProveFixedSlotDurablyAbsent() =>
        HostProcessToolsMarkerCredentialAbsenceResult.Unavailable();

}

/// <summary>
/// An in-process fixed slot with a real record identity, for tests and keychain-less hosts.
/// </summary>
/// <remarks>
/// The record identity is a monotonic counter rather than the value, so a byte-identical replacement
/// written after a capability was opened is a *different* record and is refused — which is the one
/// race the retained-record design exists to lose safely, and the one an in-memory store keyed only
/// by value could not express.
///
/// <para><see cref="ReplaceForTest"/> is a supported seam rather than reflection into private state:
/// a race this backend cannot be asked to produce is a race no suite can prove is handled.</para>
/// </remarks>
internal sealed class InMemoryHostProcessToolsMarkerSlot
    : IHostProcessToolsMarkerCredentialCapabilitySource
{

    private readonly object _gate = new();

    private long _nextRecordId = 1;

    private long _recordId;

    private byte[]? _value;

    private bool _unavailable;

    private bool _presentInvalid;

    /// <summary>How many times the durable absence proof read the slot.</summary>
    internal int AbsenceReads { get; private set; }

    /// <summary>How many times the platform durability barrier ran.</summary>
    internal int Barriers { get; private set; }

    internal void Set(ReadOnlySpan<byte> encodedSecretUtf8)
    {

        lock (_gate)
        {

            _value = encodedSecretUtf8.ToArray();

            _recordId = _nextRecordId++;

            _presentInvalid = false;

        }

    }

    /// <summary>Writes a new record over the slot, the way a live replacement would.</summary>
    internal void ReplaceForTest(ReadOnlySpan<byte> encodedSecretUtf8) => Set(encodedSecretUtf8);

    internal void Clear()
    {

        lock (_gate)
        {

            _value = null;

            _recordId = 0;

            _presentInvalid = false;

        }

    }

    /// <summary>Makes the slot definitely present and definitely unusable.</summary>
    internal void SetPresentInvalid()
    {

        lock (_gate)
        {

            _value = null;

            _recordId = _nextRecordId++;

            _presentInvalid = true;

        }

    }

    internal void SetUnavailable(bool unavailable)
    {

        lock (_gate)
        {

            _unavailable = unavailable;

        }

    }

    public HostProcessToolsMarkerCredentialOpenResult OpenFixedSlot()
    {

        lock (_gate)
        {

            if (_unavailable)
            {

                return HostProcessToolsMarkerCredentialOpenResult.Unavailable();

            }

            if (_presentInvalid)
            {

                return HostProcessToolsMarkerCredentialOpenResult.PresentInvalid();

            }

            if (_value is not { Length: > 0 } value)
            {

                return HostProcessToolsMarkerCredentialOpenResult.Absent();

            }

            if (value.Length > HostProcessToolsMarkerCredentialCapability.MaxEncodedSecretUtf8Bytes)
            {

                return HostProcessToolsMarkerCredentialOpenResult.PresentInvalid();

            }

            return HostProcessToolsMarkerCredentialOpenResult.Opened(
                HostProcessToolsMarkerCredentialCapability.CreateOwned(
                    value,
                    new InMemoryRecord(this, _recordId)));

        }

    }

    public HostProcessToolsMarkerCredentialAbsenceResult ProveFixedSlotDurablyAbsent()
    {

        lock (_gate)
        {

            if (_unavailable)
            {

                return HostProcessToolsMarkerCredentialAbsenceResult.Unavailable();

            }

            bool firstPresent = _value is not null || _presentInvalid;

            AbsenceReads++;

            // The barrier this backend can honestly offer: a full fence, so a write published on
            // another thread before the proof began is visible to the second read.
            Thread.MemoryBarrier();

            Barriers++;

            bool secondPresent = _value is not null || _presentInvalid;

            AbsenceReads++;

            return firstPresent || secondPresent
                ? HostProcessToolsMarkerCredentialAbsenceResult.Present()
                : HostProcessToolsMarkerCredentialAbsenceResult.Absent();

        }

    }

    private HostProcessToolsMarkerCredentialDeleteStatus CompareDelete(
        long recordId,
        ReadOnlySpan<byte> expected)
    {

        lock (_gate)
        {

            if (_unavailable)
            {

                return HostProcessToolsMarkerCredentialDeleteStatus.Unavailable;

            }

            // Reread the retained record, not the slot by name. A replacement written since the open
            // carries a later identity and is refused, even when its bytes are identical.
            if (_recordId != recordId
                || _value is not { } current
                || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    expected,
                    current))
            {

                return HostProcessToolsMarkerCredentialDeleteStatus.Mismatch;

            }

            _value = null;

            _recordId = 0;

            Thread.MemoryBarrier();

            Barriers++;

            return _value is null
                ? HostProcessToolsMarkerCredentialDeleteStatus.Deleted
                : HostProcessToolsMarkerCredentialDeleteStatus.Unavailable;

        }

    }

    private sealed class InMemoryRecord(InMemoryHostProcessToolsMarkerSlot owner, long recordId)
        : IHostProcessToolsMarkerNativeRecordCapability
    {

        private bool _disposed;

        public HostProcessToolsMarkerCredentialDeleteStatus CompareDeleteExact(
            ReadOnlySpan<byte> expectedEncodedSecretUtf8) =>
            _disposed
                ? HostProcessToolsMarkerCredentialDeleteStatus.Unavailable
                : owner.CompareDelete(recordId, expectedEncodedSecretUtf8);

        public void Dispose() => _disposed = true;

    }

}

/// <summary>The shared fixed-slot names every backend is handed.</summary>
internal static class HostProcessToolsMarkerSlotIdentity
{

    internal static string Service => ArcanumCredentialIdentity.Service;

    internal static string Account => ArcanumCredentialIdentity.HostProcessToolsTaintAccount;

    /// <summary>
    /// Requires a definitely-present value to be a nonempty, round-trippable, in-bounds UTF-8 string.
    /// </summary>
    /// <remarks>
    /// Round-trippability is the point rather than a formality. Every platform backend hands back a
    /// decoded <see cref="string"/>, and a value whose bytes are not valid UTF-8 comes back holding
    /// replacement characters — re-encoding it would produce bytes that were never in the slot and
    /// that no comparison could match. That is definitely-present, definitely-unusable data, which
    /// is neither absent nor a backend failure.
    /// </remarks>
    internal static bool TryEncode(string? value, out byte[] encodedSecretUtf8)
    {

        encodedSecretUtf8 = [];

        if (value is not { Length: > 0 })
        {

            return false;

        }

        byte[] bytes;

        try
        {

            bytes = new UTF8Encoding(false, true).GetBytes(value);

        }
        catch (EncoderFallbackException)
        {

            return false;

        }

        if (bytes.Length == 0
            || bytes.Length > HostProcessToolsMarkerCredentialCapability.MaxEncodedSecretUtf8Bytes
            || !string.Equals(Encoding.UTF8.GetString(bytes), value, StringComparison.Ordinal))
        {

            return false;

        }

        encodedSecretUtf8 = bytes;

        return true;

    }

    /// <summary>Copies a native buffer without letting its length overrun the pinned bound.</summary>
    internal static bool TryCopyNative(nint pointer, int length, out byte[] bytes)
    {

        bytes = [];

        if (pointer == nint.Zero
            || length <= 0
            || length > HostProcessToolsMarkerCredentialCapability.MaxEncodedSecretUtf8Bytes)
        {

            return false;

        }

        byte[] copied = new byte[length];

        Marshal.Copy(pointer, copied, 0, length);

        bytes = copied;

        return true;

    }

}
