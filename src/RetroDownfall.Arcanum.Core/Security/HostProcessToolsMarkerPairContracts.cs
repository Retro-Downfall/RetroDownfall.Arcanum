using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Core.Security;

/// <summary>
/// The four answers a database authority row and an operating-system taint marker can produce
/// together.
/// </summary>
/// <remarks>
/// Two markers rather than one because either alone is forgeable by an ordinary operation: a
/// database restore carries an archived row into a clean installation, and an OS secret survives a
/// database reset. Only the pair proves the transition actually happened here, so exactly one
/// disposition — <see cref="TaintedMatched"/> — grants anything, and everything the join cannot
/// prove blocks (§10.12).
/// </remarks>
public enum HostProcessToolsMarkerPairDisposition : byte
{

    Clean = 1,

    PendingBlocked = 2,

    TaintedMatched = 3,

    MismatchBlocked = 4,

}

/// <summary>
/// The validated <c>covenant_authority_state</c> facts the join is allowed to see.
/// </summary>
/// <remarks>
/// A value rather than a row reader: the join has to be pure and testable, and the reader has to be
/// unable to hand it a shape the schema would have refused. The constructor therefore enforces the
/// same clean-or-complete rule the table's CHECK constraint does, so a malformed row fails here
/// rather than being classified.
/// </remarks>
public sealed class HostProcessToolsDatabaseMarkerEvidence
{

    public HostProcessToolsDatabaseMarkerEvidence(
        string installationIdentity,
        CovenantHostToolsState state,
        Guid? transitionId,
        uint? taintMasterKeyVersion,
        CovenantDigest? taintFingerprint)
    {

        InstallationIdentity = HostProcessToolsMarkerPayload.RequireInstallationIdentity(
            installationIdentity,
            nameof(installationIdentity));

        State = state is CovenantHostToolsState.Clean
            or CovenantHostToolsState.PendingHostToolsTaint
            or CovenantHostToolsState.HostToolsTainted
            ? state
            : throw new ArgumentOutOfRangeException(nameof(state));

        if (state is CovenantHostToolsState.Clean)
        {

            if (transitionId is not null || taintMasterKeyVersion is not null || taintFingerprint is not null)
            {

                throw new ArgumentException(
                    "A clean authority row carries no transition, taint version, or taint fingerprint.",
                    nameof(state));

            }

            TransitionId = null;

            TaintMasterKeyVersion = null;

            TaintFingerprint = null;

            TaintIdentityDigest = null;

        }
        else
        {

            TransitionId = transitionId is { } transition && transition != Guid.Empty
                ? transition
                : throw new ArgumentException(
                    "A pending or tainted authority row requires its transition identity.",
                    nameof(transitionId));

            TaintMasterKeyVersion = taintMasterKeyVersion is { } version && version > 0
                ? version
                : throw new ArgumentOutOfRangeException(nameof(taintMasterKeyVersion));

            TaintFingerprint = taintFingerprint is { IsValid: true } fingerprint
                ? fingerprint
                : throw new ArgumentException(
                    "A pending or tainted authority row requires its 32-byte taint fingerprint.",
                    nameof(taintFingerprint));

            TaintIdentityDigest = HostProcessToolsMarkerPayload.DigestOf(
                HostProcessToolsMarkerPayload.Encode(
                    InstallationIdentity,
                    TransitionId.Value,
                    TaintMasterKeyVersion.Value,
                    TaintFingerprint.Value));

        }

        DatabaseMarkerDigest = ComputeDatabaseDigest();

    }

    public string InstallationIdentity { get; }

    public CovenantHostToolsState State { get; }

    public Guid? TransitionId { get; }

    public uint? TaintMasterKeyVersion { get; }

    public CovenantDigest? TaintFingerprint { get; }

    /// <summary>
    /// The taint tuple this row and its marker must agree on, or <see langword="null"/> when clean.
    /// </summary>
    public CovenantDigest? TaintIdentityDigest { get; }

    /// <summary>Content-free evidence of the whole row, including its state code.</summary>
    public CovenantDigest DatabaseMarkerDigest { get; }

    private CovenantDigest ComputeDatabaseDigest()
    {

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        hash.AppendData(HostProcessToolsMarkerPayload.DatabaseDomainLabel);

        hash.AppendData([(byte)State]);

        hash.AppendData(TaintIdentityDigest is { } identity
            ? identity.Bytes
            : HostProcessToolsMarkerPayload.Encode(InstallationIdentity, Guid.Empty, 0, null));

        return new CovenantDigest(hash.GetHashAndReset());

    }

}

/// <summary>
/// The validated facts read back out of the dedicated operating-system taint slot.
/// </summary>
/// <remarks>
/// Every field is required. An OS marker that exists at all is asserting that this installation once
/// handed model-driven code the operator's identity, and a partial assertion of that is not a
/// weaker claim — it is an unreadable one, which blocks.
/// </remarks>
public sealed class HostProcessToolsOsMarkerEvidence
{

    public HostProcessToolsOsMarkerEvidence(
        string installationIdentity,
        Guid transitionId,
        uint taintMasterKeyVersion,
        CovenantDigest taintFingerprint,
        CovenantDigest markerBytesDigest,
        CovenantDigest durableIdentityDigest)
    {

        InstallationIdentity = HostProcessToolsMarkerPayload.RequireInstallationIdentity(
            installationIdentity,
            nameof(installationIdentity));

        TransitionId = transitionId != Guid.Empty
            ? transitionId
            : throw new ArgumentException("The marker requires its transition identity.", nameof(transitionId));

        TaintMasterKeyVersion = taintMasterKeyVersion > 0
            ? taintMasterKeyVersion
            : throw new ArgumentOutOfRangeException(nameof(taintMasterKeyVersion));

        TaintFingerprint = taintFingerprint.IsValid
            ? taintFingerprint
            : throw new ArgumentException("The marker requires its 32-byte taint fingerprint.", nameof(taintFingerprint));

        MarkerBytesDigest = markerBytesDigest.IsValid
            ? markerBytesDigest
            : throw new ArgumentException("The marker requires the digest of its exact stored bytes.", nameof(markerBytesDigest));

        DurableIdentityDigest = durableIdentityDigest.IsValid
            ? durableIdentityDigest
            : throw new ArgumentException("The marker requires the identity of the slot it was opened from.", nameof(durableIdentityDigest));

        TaintIdentityDigest = HostProcessToolsMarkerPayload.DigestOf(
            HostProcessToolsMarkerPayload.Encode(
                InstallationIdentity,
                TransitionId,
                TaintMasterKeyVersion,
                TaintFingerprint));

    }

    public string InstallationIdentity { get; }

    public Guid TransitionId { get; }

    public uint TaintMasterKeyVersion { get; }

    public CovenantDigest TaintFingerprint { get; }

    public CovenantDigest MarkerBytesDigest { get; }

    public CovenantDigest DurableIdentityDigest { get; }

    public CovenantDigest TaintIdentityDigest { get; }

}

/// <summary>The one pair a <see cref="HostProcessToolsMarkerPairDisposition.TaintedMatched"/> carries.</summary>
public sealed record HostProcessToolsMatchedPair(
    HostProcessToolsDatabaseMarkerEvidence Database,
    HostProcessToolsOsMarkerEvidence OsMarker);

/// <summary>The classification, and the matched pair only when there is one.</summary>
public sealed record HostProcessToolsMarkerPairJoinResult(
    HostProcessToolsMarkerPairDisposition Disposition,
    HostProcessToolsMatchedPair? MatchedPair);

/// <summary>
/// Classifies one database row against one optional operating-system marker, with no I/O.
/// </summary>
/// <remarks>
/// An interface so startup, the offline transition, and full-installation reset consume the same
/// classification rather than three that drift. Pure so the exhaustive matrix can be asserted
/// without a database or a keychain.
/// </remarks>
public interface IHostProcessToolsMarkerPairJoiner
{

    HostProcessToolsMarkerPairJoinResult Join(
        HostProcessToolsDatabaseMarkerEvidence database,
        HostProcessToolsOsMarkerEvidence? osMarker);

}

/// <inheritdoc cref="IHostProcessToolsMarkerPairJoiner"/>
public sealed class HostProcessToolsMarkerPairJoiner : IHostProcessToolsMarkerPairJoiner
{

    /// <inheritdoc/>
    public HostProcessToolsMarkerPairJoinResult Join(
        HostProcessToolsDatabaseMarkerEvidence database,
        HostProcessToolsOsMarkerEvidence? osMarker)
    {

        ArgumentNullException.ThrowIfNull(database);

        if (database.State is CovenantHostToolsState.Clean)
        {

            // A clean row beside a marker is the restore-into-clean-installation case, and it is the
            // one shape that must never be resolved in the clean direction.
            return new HostProcessToolsMarkerPairJoinResult(
                osMarker is null
                    ? HostProcessToolsMarkerPairDisposition.Clean
                    : HostProcessToolsMarkerPairDisposition.MismatchBlocked,
                null);

        }

        if (osMarker is null)
        {

            // Pending without a marker is the ordinary "the OS write never happened" resume point.
            // Tainted without one is evidence loss, and it blocks.
            return new HostProcessToolsMarkerPairJoinResult(
                database.State is CovenantHostToolsState.PendingHostToolsTaint
                    ? HostProcessToolsMarkerPairDisposition.PendingBlocked
                    : HostProcessToolsMarkerPairDisposition.MismatchBlocked,
                null);

        }

        if (!Matches(database, osMarker))
        {

            return new HostProcessToolsMarkerPairJoinResult(
                HostProcessToolsMarkerPairDisposition.MismatchBlocked,
                null);

        }

        return database.State is CovenantHostToolsState.PendingHostToolsTaint
            ? new HostProcessToolsMarkerPairJoinResult(HostProcessToolsMarkerPairDisposition.PendingBlocked, null)
            : new HostProcessToolsMarkerPairJoinResult(
                HostProcessToolsMarkerPairDisposition.TaintedMatched,
                new HostProcessToolsMatchedPair(database, osMarker));

    }

    /// <summary>
    /// Compares every field without short-circuiting.
    /// </summary>
    /// <remarks>
    /// Fixed time is not about secrecy of the fingerprint, which is already a one-way digest. It is
    /// about not leaking <i>which</i> field disagreed to a caller that can retry with a crafted
    /// marker: an early return on installation identity would let an attacker search the fields one
    /// at a time.
    /// </remarks>
    private static bool Matches(
        HostProcessToolsDatabaseMarkerEvidence database,
        HostProcessToolsOsMarkerEvidence osMarker)
    {

        int difference = 0;

        difference |= CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(database.InstallationIdentity),
            Encoding.UTF8.GetBytes(osMarker.InstallationIdentity))
            ? 0
            : 1;

        difference |= CryptographicOperations.FixedTimeEquals(
            database.TransitionId!.Value.ToByteArray(bigEndian: true),
            osMarker.TransitionId.ToByteArray(bigEndian: true))
            ? 0
            : 1;

        Span<byte> databaseVersion = stackalloc byte[4];

        Span<byte> markerVersion = stackalloc byte[4];

        BinaryPrimitives.WriteUInt32BigEndian(databaseVersion, database.TaintMasterKeyVersion!.Value);

        BinaryPrimitives.WriteUInt32BigEndian(markerVersion, osMarker.TaintMasterKeyVersion);

        difference |= CryptographicOperations.FixedTimeEquals(databaseVersion, markerVersion) ? 0 : 1;

        difference |= CryptographicOperations.FixedTimeEquals(
            database.TaintFingerprint!.Value.Bytes,
            osMarker.TaintFingerprint.Bytes)
            ? 0
            : 1;

        difference |= CryptographicOperations.FixedTimeEquals(
            database.TaintIdentityDigest!.Value.Bytes,
            osMarker.TaintIdentityDigest.Bytes)
            ? 0
            : 1;

        return difference == 0;

    }

}

/// <summary>The decoded fields of one operating-system marker payload.</summary>
public readonly record struct HostProcessToolsMarkerFields(
    string InstallationIdentity,
    Guid TransitionId,
    uint TaintMasterKeyVersion,
    CovenantDigest TaintFingerprint);

/// <summary>
/// The pinned wire form of the operating-system taint marker.
/// </summary>
/// <remarks>
/// Fixed length and fixed field order so a readback compares exact bytes rather than reparsing into
/// a shape a later version might interpret differently. The leading version byte means a future
/// format is a refusal here rather than a silent misread of one installation's marker as another's.
///
/// <para>Deliberately outside <see cref="CovenantPolicyV1Manifest"/>. This digest names installation
/// authority, not rendered or published Covenant content, and folding it into the frozen v1 domain
/// set would make an authority-evidence change look like a content-policy change.</para>
/// </remarks>
public static class HostProcessToolsMarkerPayload
{

    /// <summary>The one format version this build writes and accepts.</summary>
    public const byte Version = 1;

    /// <summary>Widest installation identity the authority row permits.</summary>
    public const int InstallationIdentityFieldBytes = 128;

    /// <summary>Version byte, identity length, identity field, transition, key version, fingerprint.</summary>
    public const int Length = 1 + 1 + InstallationIdentityFieldBytes + 16 + 4 + 32;

    internal static readonly byte[] DatabaseDomainLabel =
        Encoding.UTF8.GetBytes("Arcanum.HostProcessTools.DatabaseMarker.v1\0");

    private static readonly byte[] PayloadDomainLabel =
        Encoding.UTF8.GetBytes("Arcanum.HostProcessTools.Marker.v1\0");

    /// <summary>
    /// Encodes one marker payload. A null fingerprint is permitted only for the all-zero clean
    /// placeholder the database digest uses, never for a stored marker.
    /// </summary>
    public static byte[] Encode(
        string installationIdentity,
        Guid transitionId,
        uint taintMasterKeyVersion,
        CovenantDigest? taintFingerprint)
    {

        _ = RequireInstallationIdentity(installationIdentity, nameof(installationIdentity));

        byte[] identityBytes = Encoding.UTF8.GetBytes(installationIdentity);

        byte[] payload = new byte[Length];

        payload[0] = Version;

        payload[1] = checked((byte)identityBytes.Length);

        identityBytes.CopyTo(payload.AsSpan(2));

        _ = transitionId.TryWriteBytes(payload.AsSpan(2 + InstallationIdentityFieldBytes), bigEndian: true, out _);

        BinaryPrimitives.WriteUInt32BigEndian(
            payload.AsSpan(2 + InstallationIdentityFieldBytes + 16),
            taintMasterKeyVersion);

        if (taintFingerprint is { IsValid: true } fingerprint)
        {

            fingerprint.Bytes.CopyTo(payload.AsSpan(2 + InstallationIdentityFieldBytes + 16 + 4));

        }

        return payload;

    }

    /// <summary>Decodes an exact-length, exact-version payload, or refuses it.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> payload, out HostProcessToolsMarkerFields fields)
    {

        fields = default;

        if (payload.Length != Length || payload[0] != Version)
        {
            return false;
        }

        int identityLength = payload[1];

        if (identityLength is 0 || identityLength > InstallationIdentityFieldBytes)
        {
            return false;
        }

        ReadOnlySpan<byte> identityField = payload.Slice(2, InstallationIdentityFieldBytes);

        // Padding has to be exactly zero. Otherwise two markers whose identities differ only in the
        // pad would decode to the same identity while digesting differently.
        if (identityField[identityLength..].ContainsAnyExcept((byte)0))
        {
            return false;
        }

        string installationIdentity = Encoding.UTF8.GetString(identityField[..identityLength]);

        // The identity is the one field whose decode is lossy, so it is the one field that has to be
        // re-checked rather than trusted. GetString substitutes U+FFFD for each invalid byte and that
        // replacement re-encodes to three bytes, so as few as 43 invalid bytes already exceed the
        // pinned field width. Accepting such a payload hands HostProcessToolsOsMarkerEvidence an
        // identity its constructor rejects, and that ArgumentException escapes the startup gate as a
        // crash where the design calls for a block. Refuse anything this decoder cannot hand back
        // byte for byte, and anything blank, so everything TryDecode accepts the constructor takes.
        Span<byte> roundTripped = stackalloc byte[InstallationIdentityFieldBytes];

        if (string.IsNullOrWhiteSpace(installationIdentity)
            || !Encoding.UTF8.TryGetBytes(installationIdentity, roundTripped, out int roundTrippedLength)
            || roundTrippedLength != identityLength
            || !roundTripped[..roundTrippedLength].SequenceEqual(identityField[..identityLength]))
        {
            return false;
        }

        Guid transitionId = new(payload.Slice(2 + InstallationIdentityFieldBytes, 16), bigEndian: true);

        if (transitionId == Guid.Empty)
        {
            return false;
        }

        uint version = BinaryPrimitives.ReadUInt32BigEndian(
            payload.Slice(2 + InstallationIdentityFieldBytes + 16, 4));

        if (version == 0)
        {
            return false;
        }

        ReadOnlySpan<byte> fingerprint = payload[(2 + InstallationIdentityFieldBytes + 16 + 4)..];

        if (!fingerprint.ContainsAnyExcept((byte)0))
        {
            return false;
        }

        fields = new HostProcessToolsMarkerFields(
            installationIdentity,
            transitionId,
            version,
            new CovenantDigest(fingerprint.ToArray()));

        return true;

    }

    /// <summary>Content-free evidence of exactly these payload bytes.</summary>
    public static CovenantDigest DigestOf(ReadOnlySpan<byte> payload)
    {

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        hash.AppendData(PayloadDomainLabel);

        hash.AppendData(payload);

        return new CovenantDigest(hash.GetHashAndReset());

    }

    internal static string RequireInstallationIdentity(string value, string parameterName)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        return Encoding.UTF8.GetByteCount(value) <= InstallationIdentityFieldBytes
            ? value
            : throw new ArgumentException(
                "The installation identity exceeds the marker's pinned field width.",
                parameterName);

    }

}
