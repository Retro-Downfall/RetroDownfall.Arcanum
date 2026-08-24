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

        payload[0] = 2;

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
