using RetroDownfall.Arcanum.Core.LongRest;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed class LongRestEmbeddingPayloadHashTests
{
    [Fact]
    public void Exact_embedding_input_matches_the_independent_framed_digest()
    {
        Assert.Equal("00894FDFAE6AA6E74403838B9AB1A396985635B7AD5249AF432DD0AFA94D1420",
            LongRestEmbeddingPayloadHash.Create("embed-provider", "embed-model", 3, "Accepted summary.\n").Value.ToString());
    }

    [Fact]
    public void Unicode_embedding_input_preserves_exact_codepoints()
    {
        Assert.Equal("CFA4FC0D6A31F2471CA9CECC1194B5D0B4C862F3BD2098CC10EE5D48F2A133C4",
            LongRestEmbeddingPayloadHash.Create("local", "embed/é", 768, "Café e\u0301 \U0001f9d9\n").Value.ToString());
    }

    [Theory]
    [InlineData("provider", "model", 0, "input")]
    [InlineData(" ", "model", 1, "input")]
    [InlineData("provider", " ", 1, "input")]
    [InlineData("provider", "model", 1, " ")]
    public void Invalid_embedding_request_is_refused(string provider, string model, int dimensions, string input)
    {
        Assert.True(LongRestEmbeddingPayloadHash.Create(provider, model, dimensions, input).IsFailure);
    }

    [Fact]
    public void Strict_utf8_limits_use_bytes_without_normalizing_exact_input()
    {
        Assert.True(LongRestEmbeddingPayloadHash.Create("provider", "model", 1, new string('é', 16384)).IsSuccess);

        Assert.True(LongRestEmbeddingPayloadHash.Create("provider", "model", 1, new string('é', 16385)).IsFailure);

        Assert.True(LongRestEmbeddingPayloadHash.Create("provider", "model", 1, "\ud800").IsFailure);

        Assert.True(LongRestEmbeddingPayloadHash.Create(new string('é', 513), "model", 1, "input").IsFailure);

        string original = LongRestEmbeddingPayloadHash.Create("provider", "model", 3, "input").Value.ToString();

        Assert.NotEqual(original, LongRestEmbeddingPayloadHash.Create("provider", "model", 3, "input\n").Value.ToString());

        Assert.NotEqual(original, LongRestEmbeddingPayloadHash.Create("provider", "model", 4, "input").Value.ToString());

        Assert.NotEqual(original, LongRestEmbeddingPayloadHash.Create("Provider", "model", 3, "input").Value.ToString());

        Assert.NotEqual(original, LongRestEmbeddingPayloadHash.Create("provider", "Model", 3, "input").Value.ToString());
    }
}
