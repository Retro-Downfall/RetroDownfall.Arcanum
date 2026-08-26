using RetroDownfall.Arcanum.Core.Annals;

namespace RetroDownfall.Arcanum.Tests.Annals;

/// <summary>
/// The binding between a claim version and the exact bytes it was written about.
/// </summary>
public sealed class AnnalContentDigestTests
{

    [Fact]
    public void A_saga_digest_is_thirty_two_bytes_and_stable_for_the_same_content()
    {

        byte[] first = AnnalContentDigest.ForSagaMemory("the operator prefers tabs");

        byte[] second = AnnalContentDigest.ForSagaMemory("the operator prefers tabs");

        Assert.Equal(32, first.Length);

        Assert.Equal(first, second);

    }

    [Fact]
    public void Different_saga_content_digests_differently()
    {

        Assert.NotEqual(
            AnnalContentDigest.ForSagaMemory("one conclusion"),
            AnnalContentDigest.ForSagaMemory("another conclusion"));

    }

    /// <summary>
    /// The separator is the whole point. Without it a type ending in text a fact set begins with would
    /// hash identically to a different pair, and two distinct Lexicon states would share one binding.
    /// </summary>
    [Fact]
    public void A_lexicon_digest_separates_the_type_from_the_fact_set()
    {

        Assert.NotEqual(
            AnnalContentDigest.ForLexiconEntry("Person", "alpha"),
            AnnalContentDigest.ForLexiconEntry("PersonAlpha", string.Empty));

    }

    [Fact]
    public void A_lexicon_digest_is_stable_for_the_same_type_and_fact_set()
    {

        Assert.Equal(
            AnnalContentDigest.ForLexiconEntry("Project", "ships on Friday\nwritten in C#"),
            AnnalContentDigest.ForLexiconEntry("Project", "ships on Friday\nwritten in C#"));

    }

    [Fact]
    public void A_lexicon_digest_is_thirty_two_bytes()
    {

        Assert.Equal(32, AnnalContentDigest.ForLexiconEntry("Person", "alpha").Length);

    }

}
