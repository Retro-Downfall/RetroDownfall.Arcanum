using System.Reflection;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// The dedicated operating-system taint slot, over a real in-memory credential backend.
/// </summary>
/// <remarks>
/// The service suite fakes this store to drive orderings; this one exists so the encoding, the
/// readback, and — most importantly — the three ways a credential backend can fail are asserted
/// against the actual <see cref="IOsCredentialStore"/> contract rather than against an idealized one.
/// </remarks>
public sealed class HostProcessToolsMarkerStoreTests
{

    private const string Installation = "6F1C0B2E-9A44-4E1D-8B7A-2C5D3F6A8E90";

    private static readonly Guid Transition = Guid.Parse("3E5A7C90-1B2D-4F6A-8C0E-9D1F3A5B7C90");

    [Fact]
    public void An_empty_available_slot_reads_as_absent()
    {

        HostProcessToolsMarkerStore store = new(new InMemoryOsCredentialStore());

        Assert.Equal(HostProcessToolsMarkerReadStatus.Absent, store.Read().Status);

    }

    [Fact]
    public void A_written_marker_reads_back_with_every_field_it_was_given()
    {

        InMemoryOsCredentialStore credentials = new();

        HostProcessToolsMarkerStore store = new(credentials);

        Assert.Equal(
            HostProcessToolsMarkerWriteStatus.Written,
            store.Write(Installation, Transition, taintMasterKeyVersion: 7, Fingerprint(3)));

        HostProcessToolsMarkerReadResult read = store.Read();

        Assert.Equal(HostProcessToolsMarkerReadStatus.Present, read.Status);

        Assert.Equal(Installation, read.Marker!.InstallationIdentity);

        Assert.Equal(Transition, read.Marker.TransitionId);

        Assert.Equal(7u, read.Marker.TaintMasterKeyVersion);

        Assert.Equal(Fingerprint(3), read.Marker.TaintFingerprint);

    }

    [Fact]
    public void A_legacy_marker_preserves_its_value_and_exact_stored_bytes_digest()
    {

        InMemoryOsCredentialStore credentials = new();

        byte[] payload = LegacyMarkerPayload(uint.MaxValue);

        _ = credentials.Set(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.HostProcessToolsTaintAccount,
            Convert.ToBase64String(payload));

        HostProcessToolsMarkerReadResult read = new HostProcessToolsMarkerStore(credentials).Read();

        Assert.Equal(HostProcessToolsMarkerReadStatus.Present, read.Status);

        Assert.Equal((ulong)uint.MaxValue, read.Marker!.TaintMasterKeyVersion);

        byte[] domain = Encoding.UTF8.GetBytes("Arcanum.HostProcessTools.Marker.v1\0");

        Assert.Equal(
            new CovenantDigest(SHA256.HashData([.. domain, .. payload])),
            read.Marker.MarkerBytesDigest);

    }

    [Fact]
    public void A_slot_holding_something_that_is_not_a_marker_is_malformed_rather_than_absent()
    {

        InMemoryOsCredentialStore credentials = new();

        _ = credentials.Set(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.HostProcessToolsTaintAccount,
            "not-base64-at-all!!");

        Assert.Equal(
            HostProcessToolsMarkerReadStatus.Malformed,
            new HostProcessToolsMarkerStore(credentials).Read().Status);

    }

    [Fact]
    public void A_platform_with_no_credential_backend_reports_absence_rather_than_blocking()
    {

        HostProcessToolsMarkerStore store = new(new UnavailableOsCredentialStore());

        // A backend that does not exist cannot hold a marker, and a write into it would have been
        // refused, so absence is the honest answer. A tainted installation is still caught by its
        // database row, which joins against an absent marker as a mismatch.
        Assert.Equal(HostProcessToolsMarkerReadStatus.Absent, store.Read().Status);

        Assert.Equal(
            HostProcessToolsMarkerWriteStatus.Refused,
            store.Write(Installation, Transition, 7, Fingerprint(3)));

    }

    /// <summary>
    /// The ordinary store reads and writes. It cannot delete, and neither can its interface.
    /// </summary>
    /// <remarks>
    /// Deleting this slot is reset authority. It used to live here as a read-then-delete pair with a
    /// gap in the middle, which is a race against any concurrent writer as well as authority every
    /// ordinary consumer of the marker store inherited for free. Both are gone: the delete belongs
    /// to the retained-record reset adapter, and this assertion is what keeps it from drifting back
    /// as a convenience — it fails on the member existing, not on a wrong result.
    /// </remarks>
    [Fact]
    public void The_ordinary_marker_store_and_its_interface_expose_no_delete_surface()
    {

        Type[] surfaces = [typeof(HostProcessToolsMarkerStore), typeof(IHostProcessToolsMarkerStore)];

        foreach (Type surface in surfaces)
        {

            Assert.DoesNotContain(
                surface.GetMethods(
                    BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.Instance
                    | BindingFlags.Static
                    | BindingFlags.DeclaredOnly),
                method => method.Name.Contains("Delete", StringComparison.OrdinalIgnoreCase));

        }

    }

    private static CovenantDigest Fingerprint(byte seed)
    {

        byte[] bytes = new byte[32];

        for (int index = 0; index < bytes.Length; index++)
        {

            bytes[index] = (byte)(seed + index);

        }

        return new CovenantDigest(bytes);

    }

    private static byte[] LegacyMarkerPayload(uint taintMasterKeyVersion)
    {

        byte[] identity = Encoding.UTF8.GetBytes(Installation);

        byte[] payload = new byte[182];

        payload[0] = 1;

        payload[1] = checked((byte)identity.Length);

        identity.CopyTo(payload.AsSpan(2));

        _ = Transition.TryWriteBytes(payload.AsSpan(130), bigEndian: true, out _);

        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(146), taintMasterKeyVersion);

        Fingerprint(3).Bytes.CopyTo(payload.AsSpan(150));

        return payload;

    }

    /// <summary>A backend that is simply not present, as on a headless host with no secret service.</summary>
    private sealed class UnavailableOsCredentialStore : IOsCredentialStore
    {

        public bool IsAvailable => false;

        public OsCredentialStoreResult TryGet(string service, string account) =>
            OsCredentialStoreResult.Unavailable("no backend");

        public OsCredentialStoreResult Set(string service, string account, string secret) =>
            OsCredentialStoreResult.Unavailable("no backend");

        public OsCredentialStoreResult Delete(string service, string account) =>
            OsCredentialStoreResult.Unavailable("no backend");

    }

}
