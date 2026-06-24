using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.LlamaCpp;

namespace RetroDownfall.Arcanum.Tests.LlamaCpp;

public sealed class GgufModelHashPolicyTests
{

    [Fact]
    public void ResolveExpectedSha256_PrefersRequestBodyOverMap()
    {

        LlamaCppSettings settings = new()
        {

            ModelSha256Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {

                ["model-a"] = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",

            },

        };

        string? resolved = GgufModelHashPolicy.ResolveExpectedSha256(
            "model-a",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            settings);

        Assert.Equal("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", resolved);

    }

    [Fact]
    public void ResolveExpectedSha256_FallsBackToModelSha256Map()
    {

        LlamaCppSettings settings = new()
        {

            ModelSha256Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {

                ["model-a"] = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",

            },

        };

        string? resolved = GgufModelHashPolicy.ResolveExpectedSha256("model-a", null, settings);

        Assert.Equal("cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc", resolved);

    }

    [Fact]
    public void ShouldRejectUnverified_WhenRequireModelHashTrueAndNoDigest()
    {

        bool reject = GgufModelHashPolicy.ShouldRejectUnverified(null, requireModelHash: true);

        Assert.True(reject);

    }

    [Fact]
    public void ShouldRejectUnverified_WhenRequireModelHashFalseAndNoDigest()
    {

        bool reject = GgufModelHashPolicy.ShouldRejectUnverified(null, requireModelHash: false);

        Assert.False(reject);

    }

    [Fact]
    public void IsVerifiedDownload_TrueWhenDigestPresent()
    {

        Assert.True(GgufModelHashPolicy.IsVerifiedDownload("deadbeef"));

        Assert.False(GgufModelHashPolicy.IsVerifiedDownload(null));

        Assert.False(GgufModelHashPolicy.IsVerifiedDownload("   "));

    }

}
