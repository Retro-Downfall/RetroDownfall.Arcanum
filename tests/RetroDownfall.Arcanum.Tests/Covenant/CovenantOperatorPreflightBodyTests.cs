using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// The prepared-mutation body a commit recomputes its authorization from.
/// </summary>
public sealed class CovenantOperatorPreflightBodyTests
{

    [Fact]
    public void A_body_round_trips_every_field_it_carries()
    {

        CovenantOperatorPreflightBody original = Body();

        Result<CovenantOperatorPreflightBody> decoded =
            CovenantOperatorPreflightBody.TryDecode(original.Encode());

        Assert.True(decoded.IsSuccess, decoded.IsFailure ? decoded.Error.Message : string.Empty);

        Assert.Equal(original, decoded.Value);

    }

    [Fact]
    public void An_absent_optional_round_trips_as_absent_rather_than_as_zero()
    {

        CovenantOperatorPreflightBody original = Body() with
        {
            CampaignRegistryEpoch = null,
            CompiledArtifactDigest = null,
        };

        CovenantOperatorPreflightBody decoded =
            CovenantOperatorPreflightBody.TryDecode(original.Encode()).Value;

        // A retirement carries no compiled artifact and a Campaign mutation binds no registry epoch.
        // Reading either back as a zero would silently authorize against a fact nobody measured.
        Assert.Null(decoded.CampaignRegistryEpoch);

        Assert.Null(decoded.CompiledArtifactDigest);

        Assert.NotEqual(Body().Digest(), decoded.Digest());

    }

    [Fact]
    public void Every_field_is_part_of_the_digest_a_commit_compares()
    {

        CovenantDigest baseline = Body().Digest();

        Assert.NotEqual(baseline, (Body() with { OperatorAuthorityEpoch = 99 }).Digest());

        Assert.NotEqual(baseline, (Body() with { ExpectedTargetRevision = 99 }).Digest());

        Assert.NotEqual(baseline, (Body() with { KeyReclamationEpoch = 99 }).Digest());

        Assert.NotEqual(baseline, (Body() with { ExpiresAt = 99 }).Digest());

        Assert.NotEqual(baseline, (Body() with { EffectDigest = CovenantTask6Fixture.D(77) }).Digest());

    }

    [Fact]
    public void A_body_of_the_wrong_length_is_refused_without_saying_why()
    {

        Result<CovenantOperatorPreflightBody> decoded =
            CovenantOperatorPreflightBody.TryDecode(Body().Encode().AsSpan(0, 40));

        Assert.True(decoded.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, decoded.Error.Code);

    }

    [Fact]
    public void An_unknown_format_version_is_refused()
    {

        byte[] payload = Body().Encode();

        // One past whatever this build writes, read from the payload rather than written as a literal.
        // A literal stops being an unknown version the moment the format reaches it, and the case then
        // decodes cleanly while still reading as a refusal test.
        payload[0]++;

        Assert.True(CovenantOperatorPreflightBody.TryDecode(payload).IsFailure);

    }

    [Fact]
    public void A_presence_byte_that_is_neither_absent_nor_present_is_refused()
    {

        byte[] payload = Body().Encode();

        // The registry-epoch presence byte. A body that could carry 2 here would be a body whose
        // meaning depends on how a future build chose to read it.
        payload[1 + 32 + 8 + 16 + 8 + 8 + 8] = 2;

        Assert.True(CovenantOperatorPreflightBody.TryDecode(payload).IsFailure);

    }

    /// <summary>
    /// A correction names the exact version and compiled hash it believes it is replacing, and the
    /// token has to carry both or the commit has nothing to compare against.
    /// </summary>
    [Fact]
    public void A_body_carrying_a_correction_target_round_trips_it()
    {

        CovenantOperatorPreflightBody original = Body() with
        {
            TargetVersionId = Guid.Parse("00000000-0000-4000-8000-0000000000cc"),
            TargetRenderedHash = CovenantTask6Fixture.D(9),
        };

        Result<CovenantOperatorPreflightBody> decoded =
            CovenantOperatorPreflightBody.TryDecode(original.Encode());

        Assert.True(decoded.IsSuccess, decoded.IsFailure ? decoded.Error.Message : string.Empty);

        Assert.Equal(original, decoded.Value);

    }

    /// <summary>
    /// An absent target is absent, not a zeroed one. A body that decoded a missing target as
    /// <see cref="Guid.Empty"/> would let a correction commit against a version nobody named.
    /// </summary>
    [Fact]
    public void An_absent_correction_target_round_trips_as_absent()
    {

        Result<CovenantOperatorPreflightBody> decoded =
            CovenantOperatorPreflightBody.TryDecode(Body().Encode());

        Assert.True(decoded.IsSuccess, decoded.IsFailure ? decoded.Error.Message : string.Empty);

        Assert.Null(decoded.Value.TargetVersionId);

        Assert.Null(decoded.Value.TargetRenderedHash);

    }

    /// <summary>
    /// A body from a build that did not carry a target is refused rather than read as one with none.
    /// Reading it as "no target" would silently drop the binding a correction exists to carry.
    /// </summary>
    [Fact]
    public void A_body_declaring_an_older_format_version_is_refused()
    {

        byte[] encoded = Body().Encode();

        encoded[0] = 1;

        Assert.True(CovenantOperatorPreflightBody.TryDecode(encoded).IsFailure);

    }

    /// <summary>
    /// Every presence byte is a Boolean or the body is not one. A third value would let a caller
    /// choose which half of the target the commit compares.
    /// </summary>
    [Fact]
    public void A_target_presence_byte_outside_zero_and_one_is_refused()
    {

        byte[] encoded = Body() with
        {
            TargetVersionId = Guid.Parse("00000000-0000-4000-8000-0000000000cc"),
            TargetRenderedHash = CovenantTask6Fixture.D(9),
        } is { } body
            ? body.Encode()
            : throw new InvalidOperationException();

        // The version presence byte sits immediately before its sixteen-byte payload, which is
        // followed by the hash presence byte and thirty-two hash bytes.
        encoded[^(1 + 32 + 16 + 1)] = 2;

        Assert.True(CovenantOperatorPreflightBody.TryDecode(encoded).IsFailure);

    }

    private static CovenantOperatorPreflightBody Body() =>
        new(
            CovenantTask6Fixture.D(1),
            OperatorAuthorityEpoch: 7,
            CovenantTask6Fixture.DatasetGeneration,
            ExpectedTargetRevision: 3,
            NormalizedKeyDependencyEpoch: 2,
            KeyReclamationEpoch: 1,
            CampaignRegistryEpoch: 5,
            CovenantTask6Fixture.D(2),
            CovenantTask6Fixture.D(3),
            CovenantTask6Fixture.D(4),
            IssuedAt: 1_700_000_000,
            ExpiresAt: 1_700_000_300);

}
