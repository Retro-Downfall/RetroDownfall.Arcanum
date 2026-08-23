using System.Security.Cryptography;

namespace RetroDownfall.Arcanum.Secrets.Security;

/// <summary>What one open of the fixed host-tools marker slot found.</summary>
/// <remarks>
/// <see cref="PresentInvalid"/> is deliberately not folded into either neighbour. Data that is
/// definitely there and definitely unusable is neither an absent slot nor a backend that failed to
/// answer, and the layer above maps it to a mismatch — a marker that cannot be read is exactly the
/// state a reset must refuse rather than delete past.
/// </remarks>
internal enum HostProcessToolsMarkerCredentialOpenStatus : byte
{

    Opened = 1,

    Absent = 2,

    Unavailable = 3,

    PresentInvalid = 4,

}

/// <summary>The closed outcome of a compare-and-delete against one retained native record.</summary>
internal enum HostProcessToolsMarkerCredentialDeleteStatus : byte
{

    Deleted = 1,

    Mismatch = 2,

    Unavailable = 3,

}

/// <summary>The closed outcome of one durable absence proof over the fixed slot.</summary>
internal enum HostProcessToolsMarkerCredentialAbsenceStatus : byte
{

    Absent = 1,

    Present = 2,

    Unavailable = 3,

}

/// <summary>
/// One live platform record, retained from the read that opened it.
/// </summary>
/// <remarks>
/// Retained rather than re-looked-up, and that is the whole reason this type exists. A delete that
/// found its target again by service and account name would delete whatever now answers to that
/// name, which on a byte-identical live replacement is a different record that was never compared.
/// The implementation holds the platform's own identity for the item — a keychain item reference, a
/// Secret Service item, a credential record including its last-written stamp — and the delete
/// rereads *that* before removing it.
/// </remarks>
internal interface IHostProcessToolsMarkerNativeRecordCapability : IDisposable
{

    /// <summary>
    /// Rereads the retained record, compares it with the caller's expected bytes, deletes it, runs
    /// the platform durability barrier, and reads the slot back — as one operation.
    /// </summary>
    /// <remarks>
    /// One method rather than four, because the sequence is the guarantee. A caller that could run
    /// the delete without the readback, or insert its own step between them, would be able to report
    /// an absence nothing established.
    /// </remarks>
    HostProcessToolsMarkerCredentialDeleteStatus CompareDeleteExact(
        ReadOnlySpan<byte> expectedEncodedSecretUtf8);

}

/// <summary>
/// An opened fixed-slot marker: the exact encoded bytes, and the live record they came from.
/// </summary>
/// <remarks>
/// Owns both halves so they cannot drift apart. The bytes are a private copy this type zeroes, and
/// the record is disposed exactly once — a caller holding one of them without the other could
/// compare against bytes whose record is gone, or delete a record whose bytes nobody checked.
///
/// <para>No string and no buffer is exposed. A caller asks for the length, allocates its own bounded
/// destination, copies into it, and zeroes that copy when it is done; the secret therefore never
/// becomes an interned, relocatable, garbage-collected <see cref="string"/> the way every other
/// credential in this project does.</para>
/// </remarks>
internal sealed class HostProcessToolsMarkerCredentialCapability : IDisposable
{

    /// <summary>
    /// The pinned resource bound on one fixed-slot value.
    /// </summary>
    /// <remarks>
    /// The marker this slot holds is a fixed-length payload in Base64, far below this. The bound is
    /// here so that a backend returning an unexpectedly large value is refused at a declared limit
    /// rather than copied into memory this type then has to zero.
    /// </remarks>
    internal const int MaxEncodedSecretUtf8Bytes = 4096;

    private readonly byte[] _encodedSecretUtf8;

    private IHostProcessToolsMarkerNativeRecordCapability? _nativeCapability;

    private bool _consumed;

    private bool _disposed;

    private HostProcessToolsMarkerCredentialCapability(
        byte[] ownedEncodedSecretUtf8,
        IHostProcessToolsMarkerNativeRecordCapability ownedNativeCapability)
    {

        _encodedSecretUtf8 = ownedEncodedSecretUtf8;

        _nativeCapability = ownedNativeCapability;

    }

    /// <summary>How many bytes a destination has to hold to receive the exact encoded secret.</summary>
    internal int EncodedSecretUtf8Length => _disposed ? 0 : _encodedSecretUtf8.Length;

    /// <summary>
    /// Copies the exact encoded bytes into the caller's buffer, or writes nothing at all.
    /// </summary>
    /// <remarks>
    /// A short destination, a disposed capability, and a consumed one all fail the same way, with
    /// <paramref name="bytesWritten"/> at zero. A partial copy would hand the caller a prefix it
    /// could not tell from a complete value.
    /// </remarks>
    internal bool TryCopyEncodedSecretUtf8(Span<byte> destination, out int bytesWritten)
    {

        bytesWritten = 0;

        if (_disposed || _consumed || destination.Length < _encodedSecretUtf8.Length)
        {

            return false;

        }

        _encodedSecretUtf8.CopyTo(destination);

        bytesWritten = _encodedSecretUtf8.Length;

        return true;

    }

    /// <summary>
    /// Compares the caller's expected bytes with what was opened, then delegates the complete native
    /// delete, durability, and readback to the retained record.
    /// </summary>
    /// <remarks>
    /// Single-use. The capability is consumed for all three closed outcomes rather than only for a
    /// successful delete, because a mismatch and an unavailable backend have both already spent the
    /// one comparison this record was opened to make; retrying would compare against a record whose
    /// state has since been acted on. A repeated or post-disposal call is
    /// <see cref="HostProcessToolsMarkerCredentialDeleteStatus.Unavailable"/> and never absence.
    ///
    /// <para>The comparison is constant-time, and it happens here as well as inside the native
    /// record: this one refuses a caller whose expectation never matched what was opened, and that
    /// one refuses a record that changed between the open and the delete. They are different
    /// questions and neither substitutes for the other.</para>
    /// </remarks>
    internal HostProcessToolsMarkerCredentialDeleteStatus CompareDeleteExact(
        ReadOnlySpan<byte> expectedEncodedSecretUtf8)
    {

        if (_disposed || _consumed || _nativeCapability is not { } native)
        {

            return HostProcessToolsMarkerCredentialDeleteStatus.Unavailable;

        }

        _consumed = true;

        if (!CryptographicOperations.FixedTimeEquals(expectedEncodedSecretUtf8, _encodedSecretUtf8))
        {

            return HostProcessToolsMarkerCredentialDeleteStatus.Mismatch;

        }

        try
        {

            return native.CompareDeleteExact(expectedEncodedSecretUtf8) switch
            {

                HostProcessToolsMarkerCredentialDeleteStatus.Deleted =>
                    HostProcessToolsMarkerCredentialDeleteStatus.Deleted,

                HostProcessToolsMarkerCredentialDeleteStatus.Mismatch =>
                    HostProcessToolsMarkerCredentialDeleteStatus.Mismatch,

                // Includes an out-of-range value: an unrecognized status is uncertainty, and
                // uncertainty about a delete is never a report that the slot is now empty.
                _ => HostProcessToolsMarkerCredentialDeleteStatus.Unavailable,

            };

        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {

            return HostProcessToolsMarkerCredentialDeleteStatus.Unavailable;

        }

    }

    /// <summary>
    /// The only construction surface: takes a copy of the bytes and ownership of the record.
    /// </summary>
    /// <remarks>
    /// Nothing here interprets the value. Base64 and payload semantics belong to the one marker
    /// codec, which lives in another project entirely, and a Secrets type that knew the format would
    /// be a second decoder to keep in step with it.
    /// </remarks>
    internal static HostProcessToolsMarkerCredentialCapability CreateOwned(
        ReadOnlySpan<byte> encodedSecretUtf8,
        IHostProcessToolsMarkerNativeRecordCapability ownedNativeCapability)
    {

        ArgumentNullException.ThrowIfNull(ownedNativeCapability);

        if (encodedSecretUtf8.IsEmpty || encodedSecretUtf8.Length > MaxEncodedSecretUtf8Bytes)
        {

            throw new ArgumentOutOfRangeException(
                nameof(encodedSecretUtf8),
                "The host-tools marker slot value is empty or beyond its pinned bound.");

        }

        return new HostProcessToolsMarkerCredentialCapability(
            encodedSecretUtf8.ToArray(),
            ownedNativeCapability);

    }

    /// <summary>Zeroes the owned copy and releases the record, exactly once.</summary>
    public void Dispose()
    {

        if (_disposed)
        {

            return;

        }

        _disposed = true;

        CryptographicOperations.ZeroMemory(_encodedSecretUtf8);

        IHostProcessToolsMarkerNativeRecordCapability? native = _nativeCapability;

        _nativeCapability = null;

        native?.Dispose();

    }

}

/// <summary>The result of one attempt to open the fixed host-tools marker slot.</summary>
/// <remarks>
/// Factory-only, so the nullable shape cannot be assembled wrongly: exactly one status carries a
/// capability, and the caller that receives it also receives its disposal obligation.
/// </remarks>
internal sealed class HostProcessToolsMarkerCredentialOpenResult
{

    private HostProcessToolsMarkerCredentialOpenResult(
        HostProcessToolsMarkerCredentialOpenStatus status,
        HostProcessToolsMarkerCredentialCapability? capability)
    {

        Status = status;

        Capability = capability;

    }

    internal HostProcessToolsMarkerCredentialOpenStatus Status { get; }

    /// <summary>Nonnull only for <see cref="HostProcessToolsMarkerCredentialOpenStatus.Opened"/>.</summary>
    internal HostProcessToolsMarkerCredentialCapability? Capability { get; }

    internal static HostProcessToolsMarkerCredentialOpenResult Opened(
        HostProcessToolsMarkerCredentialCapability capability) =>
        new(
            HostProcessToolsMarkerCredentialOpenStatus.Opened,
            capability ?? throw new ArgumentNullException(nameof(capability)));

    internal static HostProcessToolsMarkerCredentialOpenResult Absent() =>
        new(HostProcessToolsMarkerCredentialOpenStatus.Absent, null);

    internal static HostProcessToolsMarkerCredentialOpenResult Unavailable() =>
        new(HostProcessToolsMarkerCredentialOpenStatus.Unavailable, null);

    internal static HostProcessToolsMarkerCredentialOpenResult PresentInvalid() =>
        new(HostProcessToolsMarkerCredentialOpenStatus.PresentInvalid, null);

}

/// <summary>The result of one durable absence proof over the fixed host-tools marker slot.</summary>
/// <remarks>
/// One closed status and nothing else. An absence proof that carried a secret or a capability would
/// be describing something it just established is not there.
/// </remarks>
internal sealed class HostProcessToolsMarkerCredentialAbsenceResult
{

    private HostProcessToolsMarkerCredentialAbsenceResult(
        HostProcessToolsMarkerCredentialAbsenceStatus status) =>
        Status = status;

    internal HostProcessToolsMarkerCredentialAbsenceStatus Status { get; }

    internal static HostProcessToolsMarkerCredentialAbsenceResult Absent() =>
        new(HostProcessToolsMarkerCredentialAbsenceStatus.Absent);

    internal static HostProcessToolsMarkerCredentialAbsenceResult Present() =>
        new(HostProcessToolsMarkerCredentialAbsenceStatus.Present);

    internal static HostProcessToolsMarkerCredentialAbsenceResult Unavailable() =>
        new(HostProcessToolsMarkerCredentialAbsenceStatus.Unavailable);

}

/// <summary>
/// The fixed host-tools marker slot, opened and proven absent — and nothing else.
/// </summary>
/// <remarks>
/// Deliberately not an addition to <see cref="IOsCredentialStore"/>. That interface takes a service
/// and an account from its caller, which is right for a general credential store and wrong for this:
/// a reset that could name its own slot could delete a credential nobody authorized it to touch.
/// Neither method here accepts a name at all.
/// </remarks>
internal interface IHostProcessToolsMarkerCredentialCapabilitySource
{

    /// <summary>Opens the fixed slot, retaining the record behind whatever it found.</summary>
    HostProcessToolsMarkerCredentialOpenResult OpenFixedSlot();

    /// <summary>
    /// Reads the fixed slot, runs the platform durability barrier, and reads it a second time.
    /// </summary>
    /// <remarks>
    /// Owns all three steps, so no caller can repeat, split, or skip them. Only two exact not-found
    /// observations are <see cref="HostProcessToolsMarkerCredentialAbsenceStatus.Absent"/>; an item
    /// observed by either read is <see cref="HostProcessToolsMarkerCredentialAbsenceStatus.Present"/>
    /// even when its data is invalid, and any ambiguity at all is
    /// <see cref="HostProcessToolsMarkerCredentialAbsenceStatus.Unavailable"/>.
    /// </remarks>
    HostProcessToolsMarkerCredentialAbsenceResult ProveFixedSlotDurablyAbsent();

}
