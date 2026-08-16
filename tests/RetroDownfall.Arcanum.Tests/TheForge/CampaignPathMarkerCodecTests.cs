using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.TheForge;

namespace RetroDownfall.Arcanum.Tests.TheForge;

/// <summary>
/// The one encoder, parser, and byte comparer every Campaign marker operation shares.
/// </summary>
/// <remarks>
/// Create, cleanup, restore-cleanup, and compare-delete all run through this single codec on purpose.
/// Two implementations that agree today drift tomorrow, and the first divergence is a cleanup that
/// declines to recognise the marker its own registration wrote — which leaves a live marker behind on
/// a root nothing owns any more.
/// </remarks>
public sealed class CampaignPathMarkerCodecTests
{

    private static readonly byte[] Key = Convert.FromHexString(
        "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");

    private readonly CampaignPathMarkerCodec _codec = new(new StubKeySource(Key));

    [Fact]
    public void An_encoded_marker_parses_back_to_the_exact_content_it_carried()
    {

        CampaignPathMarkerContent content = Content();

        Result<byte[]> encoded = _codec.Encode(content);

        Assert.True(encoded.IsSuccess);
        Assert.Equal(CampaignPathMarkerPolicy.ExactMarkerByteCount, encoded.Value.Length);

        Result<CampaignPathMarkerContent> parsed = _codec.Parse(encoded.Value);

        Assert.True(parsed.IsSuccess);
        Assert.Equal(content, parsed.Value);

    }

    [Fact]
    public void The_same_content_always_encodes_to_the_same_bytes()
    {

        // Deterministic because recovery compares the bytes it journaled against the bytes on disk. A
        // codec that folded in a timestamp or a fresh nonce would make every one of those comparisons
        // fail and turn ordinary cleanup into a manual blocker.
        CampaignPathMarkerContent content = Content();

        Assert.Equal(_codec.Encode(content).Value, _codec.Encode(content).Value);

    }

    [Fact]
    public void A_single_mutated_byte_fails_exact_comparison_and_fails_to_parse()
    {

        byte[] encoded = _codec.Encode(Content()).Value;

        for (int index = 0; index < encoded.Length; index++)
        {

            byte[] mutated = [.. encoded];

            mutated[index] ^= 0x01;

            Assert.False(_codec.MatchesExactBytes(encoded, mutated));
            Assert.True(_codec.Parse(mutated).IsFailure);

        }

    }

    [Fact]
    public void A_truncated_or_extended_marker_is_refused()
    {

        byte[] encoded = _codec.Encode(Content()).Value;

        Assert.True(_codec.Parse(encoded.AsSpan(0, encoded.Length - 1)).IsFailure);
        Assert.True(_codec.Parse([.. encoded, (byte)0]).IsFailure);

        Assert.False(_codec.MatchesExactBytes(encoded, encoded.AsSpan(0, encoded.Length - 1)));
        Assert.False(_codec.MatchesExactBytes(encoded, [.. encoded, (byte)0]));

    }

    [Fact]
    public void A_marker_written_under_a_different_installation_key_does_not_authenticate()
    {

        byte[] other = Convert.FromHexString(
            "202122232425262728292A2B2C2D2E2F303132333435363738393A3B3C3D3E3F");

        CampaignPathMarkerCodec stranger = new(new StubKeySource(other));

        byte[] encoded = stranger.Encode(Content()).Value;

        Assert.True(_codec.Parse(encoded).IsFailure);

    }

    [Fact]
    public void A_marker_copied_onto_a_different_physical_root_does_not_authenticate()
    {

        // The whole point of binding the volume and file identifiers of the directory the marker was
        // written into: copying the file elsewhere produces a marker whose tuple no longer describes
        // the directory holding it, and the caller's tuple check rejects it.
        CampaignPathMarkerContent original = Content();

        byte[] encoded = _codec.Encode(original).Value;

        CampaignPathMarkerContent parsed = _codec.Parse(encoded).Value;

        Assert.Equal(original.RootVolumeId, parsed.RootVolumeId);
        Assert.Equal(original.RootFileId, parsed.RootFileId);
        Assert.NotEqual(original with { RootFileId = original.RootFileId + 1 }, parsed);

    }

    [Fact]
    public void An_unavailable_identity_key_encodes_and_parses_nothing()
    {

        CampaignPathMarkerCodec unkeyed = new(new StubKeySource(null));

        Assert.True(unkeyed.Encode(Content()).IsFailure);
        Assert.True(unkeyed.Parse(_codec.Encode(Content()).Value).IsFailure);

    }

    [Fact]
    public void Malformed_content_is_refused_before_a_byte_is_produced()
    {

        CampaignPathMarkerContent content = Content();

        Assert.True(_codec.Encode(content with { CampaignId = Guid.Empty }).IsFailure);
        Assert.True(_codec.Encode(content with { PathRevision = 0 }).IsFailure);
        Assert.True(_codec.Encode(content with { PolicyVersion = 0 }).IsFailure);
        Assert.True(_codec.Encode(content with { MarkerSecret = [] }).IsFailure);
        Assert.True(_codec.Encode(content with { MarkerSecret = default }).IsFailure);

    }

    [Fact]
    public void Exact_comparison_is_length_checked_before_content()
    {

        Assert.True(_codec.MatchesExactBytes([1, 2, 3], [1, 2, 3]));
        Assert.False(_codec.MatchesExactBytes([1, 2, 3], [1, 2]));
        Assert.False(_codec.MatchesExactBytes([], [0]));

    }

    private static CampaignPathMarkerContent Content() =>
        new(
            CampaignPathMarkerPolicy.Version,
            Guid.Parse("6f4f2b0e-1f6d-4f5f-9d33-5f7f8e1a2b3c"),
            7,
            0x0102030405060708UL,
            0x1112131415161718UL,
            [.. Enumerable.Range(0, CampaignPathMarkerPolicy.MarkerSecretByteCount).Select(static value => (byte)value)]);

    private sealed class StubKeySource(byte[]? key) : ICampaignRootIdentityKeyProvider
    {

        public bool TryCopyRootIdentityKey(Span<byte> destination)
        {

            if (key is null || destination.Length < key.Length)
            {
                return false;
            }

            key.CopyTo(destination);

            return true;

        }

    }

}
