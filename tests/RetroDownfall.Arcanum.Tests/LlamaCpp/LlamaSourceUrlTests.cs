using RetroDownfall.Arcanum.Core.LlamaCpp;

namespace RetroDownfall.Arcanum.Tests.LlamaCpp;

public sealed class LlamaSourceUrlTests
{

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryValidate_NullOrWhitespace_ReturnsFalse(string? sourceUrl)
    {

        bool valid = LlamaSourceUrl.TryValidate(sourceUrl, out string normalized);

        Assert.False(valid);

        Assert.Equal(string.Empty, normalized);

    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("/relative/path/model.gguf")]
    [InlineData("file:///tmp/model.gguf")]
    [InlineData("ftp://example.com/model.gguf")]
    public void TryValidate_InvalidOrUnsupportedScheme_ReturnsFalse(string sourceUrl)
    {

        bool valid = LlamaSourceUrl.TryValidate(sourceUrl, out string normalized);

        Assert.False(valid);

        Assert.Equal(string.Empty, normalized);

    }

    [Theory]
    [InlineData("http://example.com/model.gguf", "http://example.com/model.gguf")]
    [InlineData("https://example.com/model.gguf", "https://example.com/model.gguf")]
    [InlineData("  https://example.com/model.gguf  ", "https://example.com/model.gguf")]
    public void TryValidate_ValidHttpOrHttps_ReturnsTrue(string sourceUrl, string expectedNormalized)
    {

        bool valid = LlamaSourceUrl.TryValidate(sourceUrl, out string normalized);

        Assert.True(valid);

        Assert.Equal(expectedNormalized, normalized);

    }

}
