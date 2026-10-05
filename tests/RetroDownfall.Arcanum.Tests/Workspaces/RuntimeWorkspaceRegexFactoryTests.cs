using System.Text.RegularExpressions;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

namespace RetroDownfall.Arcanum.Tests.Workspaces;

public sealed class RuntimeWorkspaceRegexFactoryTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(75);

    [Fact]
    public void Pattern_with_lookahead_falls_back_to_interpreted()
    {
        RuntimeWorkspaceRegexCreationResult created = RuntimeWorkspaceRegexFactory.Create(
            @"magic(?=\d+)",
            caseSensitive: true,
            Timeout);

        Assert.True(created.Success);

        Assert.True(created.FallbackAttempted);

        Assert.Equal(RuntimeWorkspaceRegexEngine.Interpreted, created.Engine);

        Assert.Matches(created.Regex!, "magic42");
    }

    [Theory]
    [InlineData("Operation is not supported on this runtime.")]
    [InlineData("")]
    [InlineData("Lookarounds are unavailable here.")]
    public void Any_not_supported_failure_from_the_linear_engine_falls_back_whatever_its_wording(
        string message)
    {
        List<RegexOptions> attempts = [];

        RuntimeWorkspaceRegexCreationResult created = RuntimeWorkspaceRegexFactory.Create(
            "magic",
            caseSensitive: true,
            Timeout,
            (pattern, options, timeout) =>
            {
                attempts.Add(options);

                return options.HasFlag(RegexOptions.NonBacktracking)
                    ? throw new NotSupportedException(message)
                    : new Regex(pattern, options, timeout);
            });

        Assert.True(created.Success);

        Assert.True(created.FallbackAttempted);

        Assert.Equal(RuntimeWorkspaceRegexEngine.Interpreted, created.Engine);

        Assert.Equal(2, attempts.Count);

        Assert.True(attempts[0].HasFlag(RegexOptions.NonBacktracking));

        Assert.False(attempts[1].HasFlag(RegexOptions.NonBacktracking));
    }

    [Fact]
    public void Fallback_failure_is_reported_as_invalid_pattern()
    {
        RuntimeWorkspaceRegexCreationResult created = RuntimeWorkspaceRegexFactory.Create(
            "magic",
            caseSensitive: true,
            Timeout,
            (_, options, _) => options.HasFlag(RegexOptions.NonBacktracking)
                ? throw new NotSupportedException("anything")
                : throw new ArgumentException("rejected by the interpreted engine too"));

        Assert.False(created.Success);

        Assert.True(created.FallbackAttempted);

        Assert.Equal("invalid_pattern", created.ErrorCode);

        Assert.Null(created.Regex);
    }

    [Fact]
    public void Syntax_error_is_invalid_pattern_without_a_fallback_attempt()
    {
        int attempts = 0;

        RuntimeWorkspaceRegexCreationResult created = RuntimeWorkspaceRegexFactory.Create(
            "[",
            caseSensitive: true,
            Timeout,
            (pattern, options, timeout) =>
            {
                attempts++;

                return new Regex(pattern, options, timeout);
            });

        Assert.False(created.Success);

        Assert.False(created.FallbackAttempted);

        Assert.Equal("invalid_pattern", created.ErrorCode);

        Assert.Equal(1, attempts);
    }
}
