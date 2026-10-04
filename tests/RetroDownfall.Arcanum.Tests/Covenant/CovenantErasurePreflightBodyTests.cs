using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// The body a Covenant entry erasure's preflight token carries.
/// </summary>
public sealed class CovenantErasurePreflightBodyTests
{
    private static readonly Guid ConfirmedVersion = Guid.Parse("44444444-5555-4666-8777-888888888888");

    private static readonly Guid ProposedVersion = Guid.Parse("66666666-7777-4888-8999-aaaaaaaaaaaa");

    /// <summary>
    /// Each of the three head shapes an entry can carry round-trips: both lanes, the Confirmed lane
    /// alone, and the Proposed lane alone, which is an entry nobody has confirmed yet.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Round_trip_preserves_every_field(bool confirmedHead, bool proposedHead)
    {
        CovenantErasurePreflightBody original = Body() with
        {
            ConfirmedVersionId = confirmedHead ? ConfirmedVersion : null,
            ProposedVersionId = proposedHead ? ProposedVersion : null,
        };

        byte[] encoded = original.Encode();

        // The two presence bytes follow the entry id, each ahead of its sixteen-byte slot.
        Assert.Equal(confirmedHead ? (byte)1 : (byte)0, encoded[90]);

        Assert.Equal(proposedHead ? (byte)1 : (byte)0, encoded[107]);

        Result<CovenantErasurePreflightBody> decoded = CovenantErasurePreflightBody.TryDecode(encoded);

        Assert.True(decoded.IsSuccess, decoded.IsFailure ? decoded.Error.Message : string.Empty);

        Assert.Equal(original, decoded.Value);

        Assert.Equal(confirmedHead ? ConfirmedVersion : null, decoded.Value.ConfirmedVersionId);

        Assert.Equal(proposedHead ? ProposedVersion : null, decoded.Value.ProposedVersionId);
    }

    [Fact]
    public void Encoded_body_is_172_bytes_with_format_byte_one()
    {
        byte[] encoded = Body().Encode();

        Assert.Equal(171, CovenantErasurePreflightBody.EncodedBytes);

        Assert.Equal(172, encoded.Length);

        Assert.Equal(0x01, encoded[0]);
    }

    /// <summary>
    /// Both bodies travel in the same operator-preflight envelope purpose, so neither may ever read as
    /// the other: an erasure token must not authorize a set, and a set token must not authorize an erase.
    /// </summary>
    [Fact]
    public void Operator_and_erasure_bodies_never_decode_as_each_other()
    {
        byte[] operatorPayload = OperatorBody().Encode();

        AssertRefused(operatorPayload);

        // Restamped with the erasure format byte, the operator body is still refused on its length
        // alone. Its bytes at the erasure body's presence offsets are all zero, so nothing but the
        // length check stands between it and a clean decode.
        byte[] restamped = [.. operatorPayload];

        restamped[0] = 0x01;

        AssertRefused(restamped);

        byte[] erasurePayload = Body().Encode();

        Assert.True(CovenantOperatorPreflightBody.TryDecode(erasurePayload).IsFailure);

        byte[] erasureRestamped = [.. erasurePayload];

        erasureRestamped[0] = operatorPayload[0];

        Assert.True(CovenantOperatorPreflightBody.TryDecode(erasureRestamped).IsFailure);
    }

    [Fact]
    public void Presence_bytes_above_one_and_wrong_lengths_are_refused()
    {
        foreach (int offset in new[] { 73, 90, 107 })
        {
            byte[] payload = Body().Encode();

            payload[offset] = 2;

            AssertRefused(payload);
        }

        byte[] encoded = Body().Encode();

        AssertRefused(encoded.AsSpan(0, 171).ToArray());

        AssertRefused([.. encoded, 0x00]);

        byte[] wrongFormat = Body().Encode();

        wrongFormat[0] = 0x02;

        AssertRefused(wrongFormat);
    }

    private static void AssertRefused(byte[] payload)
    {
        Result<CovenantErasurePreflightBody> decoded = CovenantErasurePreflightBody.TryDecode(payload);

        Assert.True(decoded.IsFailure);

        Assert.Equal(ErrorCodes.MemoryErasure.InvalidPreflight, decoded.Error.Code);
    }

    private static CovenantErasurePreflightBody Body() =>
        new(
            CovenantTask6Fixture.D(1),
            OperatorAuthorityEpoch: 7,
            CovenantTask6Fixture.DatasetGeneration,
            KeyEpoch: 3,
            KeyReclamationEpoch: 2,
            ReclaimsKey: true,
            Guid.Parse("BBBBBBBB-2222-4222-8222-222222222222"),
            ConfirmedVersion,
            ProposedVersionId: null,
            CovenantTask6Fixture.D(4),
            IssuedAt: 1_700_000_000,
            ExpiresAt: 1_700_000_300);

    private static CovenantOperatorPreflightBody OperatorBody() =>
        new(
            CovenantTask6Fixture.D(1),
            OperatorAuthorityEpoch: 7,
            CovenantTask6Fixture.DatasetGeneration,
            ExpectedTargetRevision: 3,
            NormalizedKeyDependencyEpoch: 2,
            KeyReclamationEpoch: 1,
            CampaignRegistryEpoch: null,
            CompiledArtifactDigest: null,
            CovenantTask6Fixture.D(3),
            CovenantTask6Fixture.D(4),
            IssuedAt: 1_700_000_000,
            ExpiresAt: 1_700_000_300);
}
