using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed class FtsMatchQuerySanitizerTests
{

    [Fact]
    public void Sanitize_keeps_alphanumeric_tokens_and_underscores()
    {

        string sanitized = FtsMatchQuerySanitizer.Sanitize("hello_world 42");

        Assert.Equal("hello_world 42", sanitized);

    }

    [Fact]
    public void Sanitize_strips_special_characters_and_collapses_whitespace()
    {

        string sanitized = FtsMatchQuerySanitizer.Sanitize("  foo!!  bar@@  ");

        Assert.Equal("foo bar", sanitized);

    }

    [Fact]
    public void Sanitize_returns_empty_for_only_symbols()
    {

        string sanitized = FtsMatchQuerySanitizer.Sanitize("!!!");

        Assert.Equal(string.Empty, sanitized);

    }

    [Theory]
    [InlineData("a AND b", "a \"AND\" b")]
    [InlineData("foo OR bar", "foo \"OR\" bar")]
    [InlineData("NOT hello", "\"NOT\" hello")]
    [InlineData("NEAR token", "\"NEAR\" token")]
    public void Sanitize_quotes_fts_reserved_tokens(string input, string expected)
    {

        string sanitized = FtsMatchQuerySanitizer.Sanitize(input);

        Assert.Equal(expected, sanitized);

    }

}
