using System.Buffers.Binary;
using System.Security.Cryptography;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// The pure classification of one database row against one operating-system marker.
/// </summary>
/// <remarks>
/// Every startup, every resumed transition, and every attested reset asks the same question through
/// this joiner, so the four dispositions are asserted exhaustively here rather than re-derived at
/// each call site. The one answer that grants anything is <c>TaintedMatched</c>; everything the join
/// cannot prove is a block.
/// </remarks>
public sealed class HostProcessToolsMarkerPairJoinerTests
{

    private const string Installation = "6F1C0B2E-9A44-4E1D-8B7A-2C5D3F6A8E90";

    private const string OtherInstallation = "A11B0B2E-9A44-4E1D-8B7A-2C5D3F6A8E90";

    private static readonly Guid Transition = Guid.Parse("3E5A7C90-1B2D-4F6A-8C0E-9D1F3A5B7C90");

    private static readonly Guid OtherTransition = Guid.Parse("11112222-3333-4444-5555-666677778888");

    private readonly HostProcessToolsMarkerPairJoiner _joiner = new();

    [Fact]
    public void A_clean_row_with_no_marker_is_the_only_clean_answer()
    {

        HostProcessToolsMarkerPairJoinResult result = _joiner.Join(CleanDatabase(), osMarker: null);

        Assert.Equal(HostProcessToolsMarkerPairDisposition.Clean, result.Disposition);

        Assert.Null(result.MatchedPair);

    }

    [Fact]
    public void A_clean_row_beside_any_marker_blocks()
    {

        HostProcessToolsMarkerPairJoinResult result = _joiner.Join(CleanDatabase(), OsMarker());

        Assert.Equal(HostProcessToolsMarkerPairDisposition.MismatchBlocked, result.Disposition);

        Assert.Null(result.MatchedPair);

    }

    [Fact]
    public void A_pending_row_blocks_with_or_without_a_matching_marker()
    {

        Assert.Equal(
            HostProcessToolsMarkerPairDisposition.PendingBlocked,
            _joiner.Join(TaintedDatabase(CovenantHostToolsState.PendingHostToolsTaint), osMarker: null).Disposition);

        Assert.Equal(
            HostProcessToolsMarkerPairDisposition.PendingBlocked,
            _joiner.Join(TaintedDatabase(CovenantHostToolsState.PendingHostToolsTaint), OsMarker()).Disposition);

    }

    [Fact]
    public void A_pending_row_beside_a_different_marker_is_a_mismatch_rather_than_a_resumable_pending()
    {

        HostProcessToolsMarkerPairJoinResult result = _joiner.Join(
            TaintedDatabase(CovenantHostToolsState.PendingHostToolsTaint),
            OsMarker(transitionId: OtherTransition));

        Assert.Equal(HostProcessToolsMarkerPairDisposition.MismatchBlocked, result.Disposition);

    }

    [Fact]
    public void A_tainted_row_matches_only_its_own_marker()
    {

        HostProcessToolsMarkerPairJoinResult result = _joiner.Join(
            TaintedDatabase(CovenantHostToolsState.HostToolsTainted),
            OsMarker());

        Assert.Equal(HostProcessToolsMarkerPairDisposition.TaintedMatched, result.Disposition);

        Assert.NotNull(result.MatchedPair);

        Assert.Equal(Installation, result.MatchedPair!.Database.InstallationIdentity);

        Assert.Equal(Transition, result.MatchedPair.OsMarker.TransitionId);

    }

    [Fact]
    public void Taint_versions_that_differ_only_above_bit_thirty_two_do_not_match()
    {

        const ulong highVersion = 0x1_0000_0001;

        HostProcessToolsDatabaseMarkerEvidence database = new(
            Installation,
            CovenantHostToolsState.HostToolsTainted,
            Transition,
            highVersion,
            Fingerprint(7));

        HostProcessToolsMarkerPairJoinResult result = _joiner.Join(
            database,
            OsMarker(taintMasterKeyVersion: 1));

        Assert.Equal(HostProcessToolsMarkerPairDisposition.MismatchBlocked, result.Disposition);

    }

    [Fact]
    public void A_tainted_row_without_a_marker_blocks_rather_than_trusting_the_database_alone()
    {

        HostProcessToolsMarkerPairJoinResult result = _joiner.Join(
            TaintedDatabase(CovenantHostToolsState.HostToolsTainted),
            osMarker: null);

        Assert.Equal(HostProcessToolsMarkerPairDisposition.MismatchBlocked, result.Disposition);

        Assert.Null(result.MatchedPair);

    }

    [Theory]
    [InlineData("installation")]
    [InlineData("transition")]
    [InlineData("version")]
    [InlineData("fingerprint")]
    public void Every_mismatched_field_blocks_a_tainted_pair(string field)
    {

        HostProcessToolsOsMarkerEvidence marker = field switch
        {
            "installation" => OsMarker(installationIdentity: OtherInstallation),

            "transition" => OsMarker(transitionId: OtherTransition),

            "version" => OsMarker(taintMasterKeyVersion: 9),

            _ => OsMarker(fingerprintSeed: 200),
        };

        HostProcessToolsMarkerPairJoinResult result = _joiner.Join(
            TaintedDatabase(CovenantHostToolsState.HostToolsTainted),
            marker);

        Assert.Equal(HostProcessToolsMarkerPairDisposition.MismatchBlocked, result.Disposition);

    }

    [Fact]
    public void A_clean_row_cannot_carry_taint_evidence()
    {

        _ = Assert.Throws<ArgumentException>(() => new HostProcessToolsDatabaseMarkerEvidence(
            Installation,
            CovenantHostToolsState.Clean,
            Transition,
            taintMasterKeyVersion: 1,
            Fingerprint(7)));

    }

    [Fact]
    public void A_tainted_row_cannot_omit_its_transition_version_or_fingerprint()
    {

        _ = Assert.Throws<ArgumentException>(() => new HostProcessToolsDatabaseMarkerEvidence(
            Installation,
            CovenantHostToolsState.HostToolsTainted,
            transitionId: null,
            taintMasterKeyVersion: 1,
            Fingerprint(7)));

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new HostProcessToolsDatabaseMarkerEvidence(
            Installation,
            CovenantHostToolsState.HostToolsTainted,
            Transition,
            taintMasterKeyVersion: 0,
            Fingerprint(7)));

        _ = Assert.Throws<ArgumentException>(() => new HostProcessToolsDatabaseMarkerEvidence(
            Installation,
            CovenantHostToolsState.HostToolsTainted,
            Transition,
            taintMasterKeyVersion: 1,
            taintFingerprint: null));

    }

    [Fact]
    public void The_marker_payload_round_trips_through_its_pinned_encoding()
    {

        byte[] payload = HostProcessToolsMarkerPayload.Encode(
            Installation,
            Transition,
            taintMasterKeyVersion: 3,
            Fingerprint(11));

        Assert.Equal(HostProcessToolsMarkerPayload.Length, payload.Length);

        Assert.True(HostProcessToolsMarkerPayload.TryDecode(payload, out HostProcessToolsMarkerFields fields));

        Assert.Equal(Installation, fields.InstallationIdentity);

        Assert.Equal(Transition, fields.TransitionId);

        Assert.Equal(3u, fields.TaintMasterKeyVersion);

        Assert.Equal(Fingerprint(11), fields.TaintFingerprint);

    }

    [Fact]
    public void A_new_marker_payload_preserves_the_full_unsigned_taint_version()
    {

        const ulong taintVersion = ulong.MaxValue;

        byte[] payload = HostProcessToolsMarkerPayload.Encode(
            Installation,
            Transition,
            taintVersion,
            Fingerprint(11));

        Assert.Equal(2, payload[0]);

        Assert.Equal(186, payload.Length);

        Assert.Equal(
            taintVersion,
            BinaryPrimitives.ReadUInt64BigEndian(payload.AsSpan(146, 8)));

        Assert.True(HostProcessToolsMarkerPayload.TryDecode(payload, out HostProcessToolsMarkerFields fields));

        Assert.Equal(taintVersion, fields.TaintMasterKeyVersion);

    }

    [Fact]
    public void A_truncated_or_reversioned_payload_is_refused_rather_than_guessed_at()
    {

        byte[] payload = HostProcessToolsMarkerPayload.Encode(
            Installation,
            Transition,
            taintMasterKeyVersion: 3,
            Fingerprint(11));

        Assert.False(HostProcessToolsMarkerPayload.TryDecode(payload.AsSpan(0, payload.Length - 1), out _));

        byte[] reversioned = [.. payload];

        reversioned[0] = 99;

        Assert.False(HostProcessToolsMarkerPayload.TryDecode(reversioned, out _));

    }

    /// <summary>
    /// The identity is the one field whose decode is lossy, so it is the one field a refusal
    /// contract has to fuzz.
    /// </summary>
    /// <remarks>
    /// <c>Encoding.UTF8.GetString</c> substitutes U+FFFD for each invalid byte and that replacement
    /// re-encodes to three bytes, so a structurally valid payload carrying non-UTF-8 identity bytes
    /// decodes to a string the marker cannot re-encode. A <c>TryDecode</c> that accepts it hands
    /// <see cref="HostProcessToolsOsMarkerEvidence"/> an identity its constructor rejects, turning a
    /// designed block into an <see cref="ArgumentException"/> thrown out of the startup gate's very
    /// first statement. Refusing here keeps the failure a classification rather than a crash.
    /// </remarks>
    [Theory]
    [InlineData(128, (byte)0xFF)]
    [InlineData(43, (byte)0xFF)]
    [InlineData(1, (byte)0x80)]
    public void An_identity_field_that_is_not_valid_utf8_is_refused_rather_than_replacement_decoded(
        int identityLength,
        byte fill)
    {

        byte[] payload = CorruptedIdentityPayload(identityLength, fill);

        Assert.False(HostProcessToolsMarkerPayload.TryDecode(payload, out _));

    }

    /// <summary>
    /// A whitespace-only identity round-trips through UTF-8 perfectly and still fails the evidence
    /// constructor's first statement, so byte fidelity alone is not the whole predicate.
    /// </summary>
    [Theory]
    [InlineData((byte)0x20)]
    [InlineData((byte)0x09)]
    public void A_blank_identity_field_is_refused_rather_than_carried_into_the_evidence(byte fill)
    {

        byte[] payload = CorruptedIdentityPayload(identityLength: 4, fill);

        Assert.False(HostProcessToolsMarkerPayload.TryDecode(payload, out _));

    }

    /// <summary>
    /// Everything <c>TryDecode</c> accepts must construct, or the refusal contract is decorative.
    /// </summary>
    [Fact]
    public void Every_accepted_payload_constructs_its_evidence_without_throwing()
    {

        byte[] payload = HostProcessToolsMarkerPayload.Encode(
            Installation,
            Transition,
            taintMasterKeyVersion: 3,
            Fingerprint(11));

        foreach (byte fill in new byte[] { 0x00, 0x09, 0x20, 0x41, 0x80, 0xC0, 0xED, 0xF5, 0xFE, 0xFF })
        {

            for (int identityLength = 1; identityLength <= HostProcessToolsMarkerPayload.InstallationIdentityFieldBytes; identityLength++)
            {

                byte[] candidate = CorruptedIdentityPayload(identityLength, fill);

                if (!HostProcessToolsMarkerPayload.TryDecode(candidate, out HostProcessToolsMarkerFields fields))
                {

                    continue;

                }

                _ = new HostProcessToolsOsMarkerEvidence(
                    fields.InstallationIdentity,
                    fields.TransitionId,
                    fields.TaintMasterKeyVersion,
                    fields.TaintFingerprint,
                    HostProcessToolsMarkerPayload.DigestOf(candidate),
                    Fingerprint(5));

            }

        }

        Assert.True(HostProcessToolsMarkerPayload.TryDecode(payload, out _));

    }

    /// <summary>
    /// A structurally valid payload whose identity field is <paramref name="identityLength"/> bytes
    /// of <paramref name="fill"/>, with correct version, zero padding, and non-empty transition,
    /// key version, and fingerprint — everything <c>TryDecode</c> checks except the identity itself.
    /// </summary>
    private static byte[] CorruptedIdentityPayload(int identityLength, byte fill)
    {

        byte[] payload = HostProcessToolsMarkerPayload.Encode(
            Installation,
            Transition,
            taintMasterKeyVersion: 3,
            Fingerprint(11));

        payload[1] = (byte)identityLength;

        payload.AsSpan(2, HostProcessToolsMarkerPayload.InstallationIdentityFieldBytes).Clear();

        payload.AsSpan(2, identityLength).Fill(fill);

        return payload;

    }

    [Fact]
    public void Two_payloads_that_differ_in_any_field_digest_differently()
    {

        CovenantDigest first = HostProcessToolsMarkerPayload.DigestOf(
            HostProcessToolsMarkerPayload.Encode(Installation, Transition, 3, Fingerprint(11)));

        CovenantDigest second = HostProcessToolsMarkerPayload.DigestOf(
            HostProcessToolsMarkerPayload.Encode(Installation, Transition, 4, Fingerprint(11)));

        Assert.NotEqual(first, second);

    }

    private static HostProcessToolsDatabaseMarkerEvidence CleanDatabase() =>
        new(Installation, CovenantHostToolsState.Clean, null, null, null);

    private static HostProcessToolsDatabaseMarkerEvidence TaintedDatabase(CovenantHostToolsState state) =>
        new(Installation, state, Transition, taintMasterKeyVersion: 4, Fingerprint(7));

    private static HostProcessToolsOsMarkerEvidence OsMarker(
        string installationIdentity = Installation,
        Guid? transitionId = null,
        ulong taintMasterKeyVersion = 4,
        byte fingerprintSeed = 7) =>
        new(
            installationIdentity,
            transitionId ?? Transition,
            taintMasterKeyVersion,
            Fingerprint(fingerprintSeed),
            MarkerBytes(installationIdentity, transitionId ?? Transition, taintMasterKeyVersion, fingerprintSeed),
            DurableIdentity());

    private static CovenantDigest MarkerBytes(
        string installationIdentity,
        Guid transitionId,
        ulong taintMasterKeyVersion,
        byte fingerprintSeed) =>
        HostProcessToolsMarkerPayload.DigestOf(HostProcessToolsMarkerPayload.Encode(
            installationIdentity,
            transitionId,
            taintMasterKeyVersion,
            Fingerprint(fingerprintSeed)));

    private static CovenantDigest DurableIdentity() =>
        new(SHA256.HashData("arcanum/host-process-tools-taint"u8));

    private static CovenantDigest Fingerprint(byte seed)
    {

        byte[] bytes = new byte[32];

        for (int index = 0; index < bytes.Length; index++)
        {

            bytes[index] = (byte)(seed + index);

        }

        return new CovenantDigest(bytes);

    }

}
