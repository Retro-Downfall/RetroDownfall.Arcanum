using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed class SqlLikePatternsTests
{

    [Fact]
    public void EscapeLiteral_escapes_wildcards_and_escape_character()
    {

        string escaped = SqlLikePatterns.EscapeLiteral(@"100%_done\path");

        Assert.Equal(@"100\%\_done\\path", escaped);

    }

    [Fact]
    public void Contains_wraps_escaped_literal_with_percent_wildcards()
    {

        string pattern = SqlLikePatterns.Contains("a_b%");

        Assert.Equal(@"%a\_b\%%", pattern);

    }

    [Fact]
    public void EscapeLiteral_returns_empty_for_null_or_empty()
    {

        Assert.Equal(string.Empty, SqlLikePatterns.EscapeLiteral(string.Empty));

    }

}
