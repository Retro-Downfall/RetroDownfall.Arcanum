using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// Every base64url value Arcanum reads is decoded in exactly one place.
/// </summary>
/// <remarks>
/// The framework's decoders disagree about a malformed value: one throws <see cref="FormatException"/>
/// for a length no decoder can read and for a final character that sets bits no encoder emits, one
/// reports a status, and a hand-written re-encode comparison is only as good as its author remembered
/// to make it. Four copies of that logic drifted apart before, and three of them let the exception out
/// of a credential-store read. This is an inventory assertion rather than a behavior test because the
/// failure it prevents is a new call site, not a wrong answer.
/// </remarks>
public sealed class CanonicalBase64UrlStructuralTests
{
    private const string Owner = "src/RetroDownfall.Arcanum.Core/Primitives/CanonicalBase64Url.cs";

    private static readonly string[] DecodingMembers =
    [
        "Base64Url.TryDecodeFromChars",
        "Base64Url.DecodeFromChars",
        "Base64Url.TryDecodeFromUtf8",
        "Base64Url.DecodeFromUtf8",
    ];

    [Fact]
    public void Only_the_canonical_decoder_names_a_base64url_decoding_member()
    {
        List<string> offenders = [];

        foreach (ProductionSource source in ProductionSourceInventory.Sources())
        {
            if (source.IsExactOwner(Owner))
            {
                continue;
            }

            foreach (string member in DecodingMembers)
            {
                if (source.Names(member))
                {
                    offenders.Add($"{source.RelativePath} names {member}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            string.Join("\n", offenders.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void The_canonical_decoder_never_uses_the_overload_that_throws()
    {
        ProductionSource owner = Assert.Single(
            ProductionSourceInventory.Sources(),
            static source => source.IsExactOwner(Owner));

        Assert.False(owner.Names("TryDecodeFromChars"));

        Assert.False(owner.Names("TryDecodeFromUtf8"));

        Assert.True(owner.Names("Base64Url.DecodeFromChars"));
    }
}
