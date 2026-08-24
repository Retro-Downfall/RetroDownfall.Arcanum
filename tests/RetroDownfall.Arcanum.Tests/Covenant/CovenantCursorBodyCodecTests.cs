using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// The wire form of a pagination cursor: attacker-supplied by construction, so nothing is inferred.
/// </summary>
public sealed class CovenantCursorBodyCodecTests
{

    [Fact]
    public void A_list_cursor_round_trips_every_field()
    {

        CovenantListCursorBody original = ListBody();

        Result<CovenantListCursorBody> decoded =
            CovenantCursorBodyCodec.TryDecodeList(CovenantCursorBodyCodec.Encode(original));

        Assert.True(decoded.IsSuccess, decoded.IsFailure ? decoded.Error.Message : string.Empty);

        Assert.Equal(original, decoded.Value);

    }

    [Fact]
    public void A_version_cursor_round_trips_every_field()
    {

        CovenantVersionCursorBody original = VersionBody();

        Result<CovenantVersionCursorBody> decoded =
            CovenantCursorBodyCodec.TryDecodeVersion(CovenantCursorBodyCodec.Encode(original));

        Assert.True(decoded.IsSuccess, decoded.IsFailure ? decoded.Error.Message : string.Empty);

        Assert.Equal(original, decoded.Value);

    }

    [Fact]
    public void A_key_with_multibyte_characters_survives_the_round_trip()
    {

        CovenantListCursorBody original = ListBody() with
        {
            Keyset = new CovenantListKeyset(1, Guid.Empty, "préférence.builds", Guid.NewGuid(), 1),
        };

        Result<CovenantListCursorBody> decoded =
            CovenantCursorBodyCodec.TryDecodeList(CovenantCursorBodyCodec.Encode(original));

        // The length prefix counts bytes, not characters. Counting characters would truncate exactly
        // the keys an operator writing in their own language would use.
        Assert.Equal("préférence.builds", decoded.Value.Keyset.NormalizedKey);

    }

    [Fact]
    public void A_trailing_byte_makes_the_cursor_unreadable()
    {

        byte[] payload = [.. CovenantCursorBodyCodec.Encode(ListBody()), 0x00];

        Result<CovenantListCursorBody> decoded = CovenantCursorBodyCodec.TryDecodeList(payload);

        Assert.True(decoded.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.InvalidCursor, decoded.Error.Code);

    }

    [Fact]
    public void A_truncated_cursor_is_refused_rather_than_read_short()
    {

        byte[] payload = CovenantCursorBodyCodec.Encode(ListBody());

        Assert.True(CovenantCursorBodyCodec.TryDecodeList(payload.AsSpan(0, payload.Length - 1)).IsFailure);

    }

    [Fact]
    public void A_version_body_cannot_be_read_as_a_list_body()
    {

        // Both are Cursor-purpose envelopes, so the purpose check alone does not separate them. The
        // leading format byte is what stops one endpoint's cursor answering another's page.
        Assert.True(CovenantCursorBodyCodec
            .TryDecodeList(CovenantCursorBodyCodec.Encode(VersionBody()))
            .IsFailure);

        Assert.True(CovenantCursorBodyCodec
            .TryDecodeVersion(CovenantCursorBodyCodec.Encode(ListBody()))
            .IsFailure);

    }

    [Fact]
    public void An_endpoint_this_format_does_not_carry_is_refused()
    {

        byte[] payload = CovenantCursorBodyCodec.Encode(ListBody());

        payload[1] = (byte)CovenantCursorEndpoint.Versions;

        Assert.True(CovenantCursorBodyCodec.TryDecodeList(payload).IsFailure);

    }

    [Fact]
    public void Every_refusal_says_the_same_thing()
    {

        byte[] payload = CovenantCursorBodyCodec.Encode(ListBody());

        string truncated = CovenantCursorBodyCodec
            .TryDecodeList(payload.AsSpan(0, 12)).Error.Message;

        byte[] wrongFormat = CovenantCursorBodyCodec.Encode(ListBody());

        wrongFormat[0] = 9;

        // A decoder that distinguished failure modes would be telling whoever sent these bytes which
        // guess was closer.
        Assert.Equal(truncated, CovenantCursorBodyCodec.TryDecodeList(wrongFormat).Error.Message);

    }

    private static CovenantListCursorBody ListBody() =>
        new(
            CovenantCursorEndpoint.List,
            CovenantTask6Fixture.D(3),
            CovenantTask6Fixture.DatasetGeneration,
            CanonicalSearchSequence: 42,
            CoreCampaignDeletionSequence: 7,
            EnvelopeKeyVersion: 1,
            new CovenantListKeyset(
                1,
                Guid.Parse("22222222-2222-4222-8222-222222222222"),
                "preference.builds",
                Guid.Parse("33333333-3333-4333-8333-333333333333"),
                1));

    private static CovenantVersionCursorBody VersionBody() =>
        new(
            CovenantTask6Fixture.D(4),
            CovenantTask6Fixture.DatasetGeneration,
            CanonicalSearchSequence: 11,
            CoreCampaignDeletionSequence: 2,
            EnvelopeKeyVersion: 1,
            new CovenantVersionKeyset(9, Guid.Parse("44444444-4444-4444-8444-444444444444")));

}
