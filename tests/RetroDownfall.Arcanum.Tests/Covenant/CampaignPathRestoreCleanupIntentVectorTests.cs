using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// The canonical commitment a restore publishes before it displaces the live root.
/// </summary>
/// <remarks>
/// The empty vector is the interesting case. A restore that observed no Campaign marker at all and a
/// restore that never got as far as preparing its cleanup children look identical if "no children" is
/// encoded as absence, so the zero arm has to produce a positive, frozen digest that only a proven
/// empty inventory can present (§10.12).
/// </remarks>
public sealed class CampaignPathRestoreCleanupIntentVectorTests
{

    [Fact]
    public void The_empty_vector_has_the_frozen_literal_digest()
    {

        Result<CovenantDigest> computed =
            CampaignPathRestoreCleanupIntentVector.Compute(ImmutableArray<Guid>.Empty);

        Assert.True(computed.IsSuccess);

        Assert.Equal(
            CampaignPathRestoreCleanupIntentVector.EmptyDigestHex,
            Convert.ToHexString(computed.Value.Bytes).ToLowerInvariant());

    }

    [Fact]
    public void The_preimage_is_the_domain_a_separator_the_checked_count_and_the_ordered_uuid_bytes()
    {

        Guid first = Guid.Parse("11111111-2222-3333-4444-555555555555");

        Guid second = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa");

        byte[] expected = SHA256.HashData(
        [
            .. Encoding.ASCII.GetBytes(CampaignPathRestoreCleanupIntentVector.Domain),
            0x00,
            .. BitConverter.GetBytes((ulong)2).Reverse(),
            .. UuidBytes(first),
            .. UuidBytes(second),
        ]);

        Result<CovenantDigest> computed =
            CampaignPathRestoreCleanupIntentVector.Compute([first, second]);

        Assert.True(computed.IsSuccess);
        Assert.Equal(expected, computed.Value.Bytes);

    }

    [Fact]
    public void Order_and_membership_both_change_the_digest()
    {

        Guid first = Guid.Parse("11111111-2222-3333-4444-555555555555");

        Guid second = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa");

        CovenantDigest forward = CampaignPathRestoreCleanupIntentVector.Compute([first, second]).Value;

        CovenantDigest reversed = CampaignPathRestoreCleanupIntentVector.Compute([second, first]).Value;

        CovenantDigest shorter = CampaignPathRestoreCleanupIntentVector.Compute([first]).Value;

        Assert.NotEqual(forward, reversed);
        Assert.NotEqual(forward, shorter);
        Assert.NotEqual(reversed, shorter);

    }

    [Fact]
    public void A_default_duplicate_or_empty_member_vector_is_refused_before_hashing()
    {

        Guid item = Guid.Parse("11111111-2222-3333-4444-555555555555");

        // Default is not the same as empty. An uninitialized array reaching the digest would let
        // "nobody built this vector" hash to the same value as "this inventory is proven empty".
        Assert.True(CampaignPathRestoreCleanupIntentVector.Compute(default).IsFailure);

        Assert.True(CampaignPathRestoreCleanupIntentVector.Compute([item, item]).IsFailure);

        Assert.True(CampaignPathRestoreCleanupIntentVector.Compute([item, Guid.Empty]).IsFailure);

        Assert.True(
            CampaignPathRestoreCleanupIntentVector
                .Compute([.. Enumerable.Range(0, CampaignPathRestoreCleanupIntentVector.MaximumIntents + 1)
                    .Select(static _ => Guid.NewGuid())])
                .IsFailure);

    }

    private static byte[] UuidBytes(Guid value)
    {

        byte[] bytes = new byte[16];

        _ = value.TryWriteBytes(bytes, bigEndian: true, out _);

        return bytes;

    }

}
