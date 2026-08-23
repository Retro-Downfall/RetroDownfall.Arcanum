using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;
using RetroDownfall.Arcanum.Secrets.Security;
using Serilog;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// The dedicated operating-system taint slot, read and written as exact pinned bytes.
/// </summary>
/// <remarks>
/// Base64 of a fixed-length payload rather than a structured document: the readback compares bytes,
/// and any format with optional whitespace, ordering, or escaping would make two encodings of the
/// same facts compare unequal — or worse, two different facts compare equal.
///
/// <para>An unavailable backend is never an absent marker. A keychain that cannot be reached says
/// nothing about whether this installation was tainted, and reporting that silence as "clean" is the
/// one failure mode this slot exists to rule out, so it surfaces as its own status and every caller
/// blocks on it (§10.12).</para>
/// </remarks>
internal sealed class HostProcessToolsMarkerStore(IOsCredentialStore credentials) : IHostProcessToolsMarkerStore
{

    private static readonly byte[] SlotIdentityLabel =
        Encoding.UTF8.GetBytes("Arcanum.HostProcessTools.MarkerSlot.v1\0");

    private readonly IOsCredentialStore _credentials =
        credentials ?? throw new ArgumentNullException(nameof(credentials));

    public HostProcessToolsMarkerReadResult Read()
    {

        if (!_credentials.IsAvailable)
        {

            // A backend that does not exist on this platform cannot be holding a marker, and the
            // transition could never have written one into it — the write would have been refused.
            // Reporting absence here is what keeps a headless installation with no credential
            // service from being blocked by a slot it never had. A genuinely tainted installation
            // is unaffected: its database row still says so, and a tainted row beside an absent
            // marker is a mismatch, which blocks (§10.12).
            return new HostProcessToolsMarkerReadResult(HostProcessToolsMarkerReadStatus.Absent, null);

        }

        OsCredentialStoreResult stored = _credentials.TryGet(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.HostProcessToolsTaintAccount);

        switch (stored.Status)
        {

            case OsCredentialStoreStatus.NotFound:

                return new HostProcessToolsMarkerReadResult(HostProcessToolsMarkerReadStatus.Absent, null);

            case OsCredentialStoreStatus.Ok when stored.Value is { Length: > 0 } encoded:

                return Decode(encoded);

            case OsCredentialStoreStatus.Ok:

                // An empty slot is a slot somebody created without a payload, which is malformed
                // rather than absent: an absent slot would have reported NotFound.
                return new HostProcessToolsMarkerReadResult(HostProcessToolsMarkerReadStatus.Malformed, null);

            default:

                Log.Warning(
                    "The host-process-tools taint slot could not be read ({Status}); every Covenant path stays closed.",
                    stored.Status);

                return new HostProcessToolsMarkerReadResult(HostProcessToolsMarkerReadStatus.Unavailable, null);

        }

    }

    public HostProcessToolsMarkerWriteStatus Write(
        string installationIdentity,
        Guid transitionId,
        ulong taintMasterKeyVersion,
        CovenantDigest taintFingerprint)
    {

        byte[] payload = HostProcessToolsMarkerPayload.Encode(
            installationIdentity,
            transitionId,
            taintMasterKeyVersion,
            taintFingerprint);

        try
        {

            OsCredentialStoreResult written = _credentials.Set(
                ArcanumCredentialIdentity.Service,
                ArcanumCredentialIdentity.HostProcessToolsTaintAccount,
                Convert.ToBase64String(payload));

            return written.Status switch
            {
                OsCredentialStoreStatus.Ok => HostProcessToolsMarkerWriteStatus.Written,

                // The backend was never reachable, so it cannot hold a partial write.
                OsCredentialStoreStatus.Unavailable => HostProcessToolsMarkerWriteStatus.Refused,

                // A failure the backend reported after accepting the request may or may not have
                // stored the value. Only a readback can tell, and until one does this is uncertain.
                _ => HostProcessToolsMarkerWriteStatus.Uncertain,
            };

        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {

            Log.Error(exception, "The host-process-tools taint slot write did not prove its outcome.");

            return HostProcessToolsMarkerWriteStatus.Uncertain;

        }

    }

    /// <summary>
    /// The content-free identity of the slot the marker was opened from.
    /// </summary>
    /// <remarks>
    /// A credential account has no inode, so the durable identity is the service and account it was
    /// opened under. It proves a readback came from the slot the write targeted rather than from a
    /// differently named account that happens to hold the same bytes.
    /// </remarks>
    internal static CovenantDigest SlotIdentityDigest()
    {

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        hash.AppendData(SlotIdentityLabel);

        hash.AppendData(Encoding.UTF8.GetBytes(ArcanumCredentialIdentity.Service));

        hash.AppendData([0]);

        hash.AppendData(Encoding.UTF8.GetBytes(ArcanumCredentialIdentity.HostProcessToolsTaintAccount));

        return new CovenantDigest(hash.GetHashAndReset());

    }

    private static HostProcessToolsMarkerReadResult Decode(string encoded)
    {

        byte[] payload;

        try
        {

            payload = Convert.FromBase64String(encoded);

        }
        catch (FormatException)
        {

            return new HostProcessToolsMarkerReadResult(HostProcessToolsMarkerReadStatus.Malformed, null);

        }

        if (!HostProcessToolsMarkerPayload.TryDecode(payload, out HostProcessToolsMarkerFields fields))
        {

            return new HostProcessToolsMarkerReadResult(HostProcessToolsMarkerReadStatus.Malformed, null);

        }

        return new HostProcessToolsMarkerReadResult(
            HostProcessToolsMarkerReadStatus.Present,
            new HostProcessToolsOsMarkerEvidence(
                fields.InstallationIdentity,
                fields.TransitionId,
                fields.TaintMasterKeyVersion,
                fields.TaintFingerprint,
                HostProcessToolsMarkerPayload.DigestOf(payload),
                SlotIdentityDigest()));

    }

}
/// <summary>
/// The reset-only view of the same slot: open a retained record, compare-delete it, prove it gone.
/// </summary>
/// <remarks>
/// Beside the ordinary store rather than inside it, and reached through a different port, because
/// the two want opposite things. The ordinary store reads and writes a value; this one takes
/// ownership of a live platform record and destroys it, which is authority no ordinary caller of
/// the marker store should acquire by depending on the type it already depends on.
///
/// <para>Every mutation here runs under the shared process gate, and every one of them holds it
/// across the <i>complete</i> Secrets-owned operation — delete, platform durability barrier, and
/// readback are one call by construction, so no layer above can repeat, split, or skip that
/// sequence. Infrastructure maps the closed statuses one-for-one and adds no barrier of its own; a
/// second barrier here would be a second thing to get wrong about a guarantee that already has an
/// owner.</para>
/// </remarks>
internal sealed class HostProcessToolsMarkerResetAdapter : IHostToolsMarkerPairResetOsPort
{

    private readonly IHostProcessToolsMarkerCredentialCapabilitySource _slots;

    private readonly HostProcessToolsMarkerMutationGate _gate;

    /// <summary>Proves a capability came from this adapter instance and not from another one.</summary>
    private readonly object _mintTicket = new();

    internal HostProcessToolsMarkerResetAdapter(
        IHostProcessToolsMarkerCredentialCapabilitySource slots,
        HostProcessToolsMarkerMutationGate gate)
    {

        _slots = slots ?? throw new ArgumentNullException(nameof(slots));

        _gate = gate ?? throw new ArgumentNullException(nameof(gate));

    }

    public HostToolsMarkerPairResetOsOpenResult OpenExact() => OpenCore(expectedEvidence: null);

    public HostToolsMarkerPairResetOsOpenResult ReopenExact(
        HostProcessToolsOsMarkerEvidence expectedEvidence) =>
        expectedEvidence is null
            ? HostToolsMarkerPairResetOsOpenResult.Unavailable()
            : OpenCore(expectedEvidence);

    public async Task<HostToolsMarkerPairResetOsDeleteStatus> CompareDeleteExactAsync(
        IHostToolsMarkerPairResetOsCapability capability,
        HostProcessToolsOsMarkerEvidence expectedEvidence,
        CancellationToken cancellationToken)
    {

        // A capability this adapter did not mint is refused as uncertainty rather than acted on. It
        // may be a perfectly good capability over the same slot from another adapter instance, and
        // that is precisely the case where deleting would destroy a record somebody else retains.
        if (capability is not ResetOsCapability owned
            || !owned.BelongsTo(_mintTicket)
            || expectedEvidence is null)
        {

            return HostToolsMarkerPairResetOsDeleteStatus.Unavailable;

        }

        await using IAsyncDisposable lease =
            await _gate.AcquireExclusiveAsync(cancellationToken).ConfigureAwait(false);

        return owned.CompareDeleteExact(expectedEvidence);

    }

    public async Task<HostToolsMarkerPairResetOsAbsenceStatus> ProveExactAbsenceAsync(
        CancellationToken cancellationToken)
    {

        await using IAsyncDisposable lease =
            await _gate.AcquireExclusiveAsync(cancellationToken).ConfigureAwait(false);

        return _slots.ProveFixedSlotDurablyAbsent().Status switch
        {

            HostProcessToolsMarkerCredentialAbsenceStatus.Absent =>
                HostToolsMarkerPairResetOsAbsenceStatus.Absent,

            // Something is in the slot after the reset believed it emptied it. That is a mismatch
            // rather than a failure to look, and it is never absence.
            HostProcessToolsMarkerCredentialAbsenceStatus.Present =>
                HostToolsMarkerPairResetOsAbsenceStatus.Mismatch,

            _ => HostToolsMarkerPairResetOsAbsenceStatus.Unavailable,

        };

    }

    /// <summary>
    /// Opens the fixed slot and turns valid content into evidence, or closes without one.
    /// </summary>
    /// <remarks>
    /// The decode is strict on purpose. <c>Convert.FromBase64String</c> tolerates embedded
    /// whitespace, so two different texts would decode to the same payload and one of them was never
    /// what the transition wrote; the UTF-8 Base64 decoder used here rejects that, and the
    /// re-encode-and-compare rejects noncanonical padding on top of it. Every malformed shape
    /// disposes the retained record and reports a mismatch — never an absence, because the slot
    /// demonstrably had something in it.
    /// </remarks>
    private HostToolsMarkerPairResetOsOpenResult OpenCore(
        HostProcessToolsOsMarkerEvidence? expectedEvidence)
    {

        HostProcessToolsMarkerCredentialOpenResult opened = _slots.OpenFixedSlot();

        switch (opened.Status)
        {

            case HostProcessToolsMarkerCredentialOpenStatus.Absent:

                return HostToolsMarkerPairResetOsOpenResult.Absent();

            // Definitely present and definitely unusable. No capability was minted and no database
            // is consulted: there is nothing here that a comparison could be made against.
            case HostProcessToolsMarkerCredentialOpenStatus.PresentInvalid:

                return HostToolsMarkerPairResetOsOpenResult.Mismatch();

            case HostProcessToolsMarkerCredentialOpenStatus.Opened
                when opened.Capability is { } capability:

                return AdoptOrDispose(capability, expectedEvidence);

            default:

                return HostToolsMarkerPairResetOsOpenResult.Unavailable();

        }

    }

    private HostToolsMarkerPairResetOsOpenResult AdoptOrDispose(
        HostProcessToolsMarkerCredentialCapability capability,
        HostProcessToolsOsMarkerEvidence? expectedEvidence)
    {

        byte[] encoded = new byte[capability.EncodedSecretUtf8Length];

        byte[] payload = new byte[HostProcessToolsMarkerPayload.Length];

        try
        {

            if (!capability.TryCopyEncodedSecretUtf8(encoded, out int copied)
                || copied != encoded.Length
                || !TryDecodeExact(encoded, payload, out HostProcessToolsMarkerFields fields))
            {

                capability.Dispose();

                return HostToolsMarkerPairResetOsOpenResult.Mismatch();

            }

            HostProcessToolsOsMarkerEvidence evidence = new(
                fields.InstallationIdentity,
                fields.TransitionId,
                fields.TaintMasterKeyVersion,
                fields.TaintFingerprint,
                HostProcessToolsMarkerPayload.DigestOf(payload),
                HostProcessToolsMarkerStore.SlotIdentityDigest());

            // Only the recovery arm compares. A first open has nothing authenticated to compare
            // against yet — the caller is about to journal what this returns — and comparing there
            // would mean inventing an expectation.
            if (expectedEvidence is not null && !EvidenceEquals(evidence, expectedEvidence))
            {

                capability.Dispose();

                return HostToolsMarkerPairResetOsOpenResult.Mismatch();

            }

            return HostToolsMarkerPairResetOsOpenResult.Opened(
                evidence,
                new ResetOsCapability(_mintTicket, capability, encoded));

        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {

            capability.Dispose();

            CryptographicOperations.ZeroMemory(encoded);

            return HostToolsMarkerPairResetOsOpenResult.Unavailable();

        }
        finally
        {

            CryptographicOperations.ZeroMemory(payload);

        }

    }

    private static bool TryDecodeExact(
        ReadOnlySpan<byte> encoded,
        Span<byte> payload,
        out HostProcessToolsMarkerFields fields)
    {

        fields = default;

        if (Base64.DecodeFromUtf8(encoded, payload, out int consumed, out int written)
                is not OperationStatus.Done
            || consumed != encoded.Length
            || written != payload.Length)
        {

            return false;

        }

        Span<byte> canonical = stackalloc byte[Base64.GetMaxEncodedToUtf8Length(payload.Length)];

        return Base64.EncodeToUtf8(payload, canonical, out _, out int encodedLength)
                is OperationStatus.Done
            && encodedLength == encoded.Length
            && canonical[..encodedLength].SequenceEqual(encoded)
            && HostProcessToolsMarkerPayload.TryDecode(payload, out fields);

    }

    private static bool EvidenceEquals(
        HostProcessToolsOsMarkerEvidence left,
        HostProcessToolsOsMarkerEvidence right) =>
        string.Equals(left.InstallationIdentity, right.InstallationIdentity, StringComparison.Ordinal)
        && left.TransitionId == right.TransitionId
        && left.TaintMasterKeyVersion == right.TaintMasterKeyVersion
        && DigestEquals(left.TaintFingerprint, right.TaintFingerprint)
        && DigestEquals(left.MarkerBytesDigest, right.MarkerBytesDigest)
        && DigestEquals(left.DurableIdentityDigest, right.DurableIdentityDigest);

    private static bool DigestEquals(CovenantDigest left, CovenantDigest right) =>
        left.IsValid
        && right.IsValid
        && CryptographicOperations.FixedTimeEquals(left.Bytes, right.Bytes);

    /// <summary>
    /// The reset port's opaque capability: the Secrets record, and this adapter's copy of its bytes.
    /// </summary>
    /// <remarks>
    /// Disposal zeroes the Infrastructure-owned copy and disposes the Secrets capability exactly
    /// once, and a second disposal is a no-op — the copy is the only plaintext marker this layer
    /// ever holds, and leaving it in a collected array would leave it in memory the process later
    /// hands to something else.
    /// </remarks>
    private sealed class ResetOsCapability : IHostToolsMarkerPairResetOsCapability
    {

        private readonly object _mintTicket;

        private readonly byte[] _encoded;

        private HostProcessToolsMarkerCredentialCapability? _capability;

        private bool _disposed;

        internal ResetOsCapability(
            object mintTicket,
            HostProcessToolsMarkerCredentialCapability capability,
            byte[] encoded)
        {

            _mintTicket = mintTicket;

            _capability = capability;

            _encoded = encoded;

        }

        internal bool BelongsTo(object mintTicket) => ReferenceEquals(_mintTicket, mintTicket);

        internal HostToolsMarkerPairResetOsDeleteStatus CompareDeleteExact(
            HostProcessToolsOsMarkerEvidence expectedEvidence)
        {

            if (_disposed || _capability is not { } capability)
            {

                return HostToolsMarkerPairResetOsDeleteStatus.Unavailable;

            }

            // The expected evidence is rechecked at the delete boundary rather than trusted from the
            // open. Between the two there is a journal write and a durability barrier, and a caller
            // that changed its mind in between must not be able to delete on the strength of an
            // agreement it no longer holds.
            Span<byte> payload = stackalloc byte[HostProcessToolsMarkerPayload.Length];

            try
            {

                if (Base64.DecodeFromUtf8(_encoded, payload, out _, out int written)
                        is not OperationStatus.Done
                    || written != payload.Length
                    || !DigestEquals(
                        HostProcessToolsMarkerPayload.DigestOf(payload),
                        expectedEvidence.MarkerBytesDigest))
                {

                    return HostToolsMarkerPairResetOsDeleteStatus.Mismatch;

                }

            }
            finally
            {

                CryptographicOperations.ZeroMemory(payload);

            }

            return capability.CompareDeleteExact(_encoded) switch
            {

                HostProcessToolsMarkerCredentialDeleteStatus.Deleted =>
                    HostToolsMarkerPairResetOsDeleteStatus.Deleted,

                HostProcessToolsMarkerCredentialDeleteStatus.Mismatch =>
                    HostToolsMarkerPairResetOsDeleteStatus.Mismatch,

                _ => HostToolsMarkerPairResetOsDeleteStatus.Unavailable,

            };

        }

        public void Dispose()
        {

            if (_disposed)
            {

                return;

            }

            _disposed = true;

            CryptographicOperations.ZeroMemory(_encoded);

            HostProcessToolsMarkerCredentialCapability? capability = _capability;

            _capability = null;

            capability?.Dispose();

        }

    }

}
