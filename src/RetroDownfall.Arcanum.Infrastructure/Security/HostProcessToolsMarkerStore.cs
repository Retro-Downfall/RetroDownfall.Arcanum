using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Security;
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
        uint taintMasterKeyVersion,
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

    public bool CompareDelete(HostProcessToolsOsMarkerEvidence expected)
    {

        ArgumentNullException.ThrowIfNull(expected);

        if (Read() is not { Status: HostProcessToolsMarkerReadStatus.Present, Marker: { } current })
        {

            return false;

        }

        if (!CryptographicOperations.FixedTimeEquals(
            current.TaintIdentityDigest.Bytes,
            expected.TaintIdentityDigest.Bytes))
        {

            return false;

        }

        OsCredentialStoreResult deleted = _credentials.Delete(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.HostProcessToolsTaintAccount);

        return deleted.Status is OsCredentialStoreStatus.Ok or OsCredentialStoreStatus.NotFound;

    }

    /// <summary>
    /// The content-free identity of the slot the marker was opened from.
    /// </summary>
    /// <remarks>
    /// A credential account has no inode, so the durable identity is the service and account it was
    /// opened under. It proves a readback came from the slot the write targeted rather than from a
    /// differently named account that happens to hold the same bytes.
    /// </remarks>
    private static CovenantDigest SlotIdentityDigest()
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
