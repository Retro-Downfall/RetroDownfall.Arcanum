using RetroDownfall.Arcanum.Core.LlamaCpp;

namespace RetroDownfall.Arcanum.Tests.LlamaCpp;

public sealed class LlamaCacheKeyTests
{

    [Fact]
    public void Normalize_EmptyInput_Throws()
    {

        Assert.Throws<ArgumentException>(() => LlamaCacheKey.Normalize(""));

        Assert.Throws<ArgumentException>(() => LlamaCacheKey.Normalize("   "));

    }

    [Theory]
    [InlineData("my-model", "my-model")]
    [InlineData("  my-model  ", "my-model")]
    [InlineData("org/model-name", "org_model-name")]
    public void NormalizeModelKey_SanitizesInvalidCharacters(string input, string expected)
    {

        string result = LlamaCacheKey.NormalizeModelKey(input);

        Assert.Equal(expected, result);

    }

    [Fact]
    public void NormalizeModelKey_AllInvalidCharacters_Throws()
    {

        Assert.Throws<ArgumentException>(() => LlamaCacheKey.NormalizeModelKey("<>:\"/\\|?*"));

    }

    [Fact]
    public void Normalize_HttpUrl_IncludesSanitizedFilenameAndHashPrefix()
    {

        const string url = "https://example.com/models/tiny.gguf";

        string key = LlamaCacheKey.Normalize(url);

        Assert.StartsWith("tiny.gguf-", key);

        Assert.True(key.Length <= 200);

        Assert.DoesNotContain("/", key);

        Assert.DoesNotContain(":", key);

    }

    [Fact]
    public void Normalize_HttpUrlWithoutFilename_UsesDefaultModelName()
    {

        const string url = "https://example.com/models/";

        string key = LlamaCacheKey.Normalize(url);

        Assert.StartsWith("model.gguf-", key);

    }

    [Fact]
    public void Normalize_SameUrl_ProducesDeterministicKey()
    {

        const string url = "https://example.com/models/stable.gguf";

        string first = LlamaCacheKey.Normalize(url);

        string second = LlamaCacheKey.Normalize(url);

        Assert.Equal(first, second);

    }

    [Fact]
    public void Normalize_DifferentUrls_ProduceDifferentKeys()
    {

        string first = LlamaCacheKey.Normalize("https://example.com/a.gguf");

        string second = LlamaCacheKey.Normalize("https://example.com/b.gguf");

        Assert.NotEqual(first, second);

    }

    [Fact]
    public void Normalize_VeryLongModelKey_TruncatesToMaxLength()
    {

        string longKey = new string('a', 300);

        string result = LlamaCacheKey.NormalizeModelKey(longKey);

        Assert.Equal(200, result.Length);

    }

    [Fact]
    public void Normalize_NonUrlModelKey_DoesNotAddHashSuffix()
    {

        string result = LlamaCacheKey.Normalize("localmodel");

        Assert.Equal("localmodel", result);

    }

}
