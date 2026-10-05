using System.Text.RegularExpressions;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

internal enum RuntimeWorkspaceRegexEngine
{
    NonBacktracking,
    Interpreted,
}

internal sealed record RuntimeWorkspaceRegexCreationResult(
    Regex? Regex,
    RuntimeWorkspaceRegexEngine? Engine,
    string? ErrorCode,
    bool FallbackAttempted)
{
    internal bool Success => Regex is not null;
}

/// <summary>
/// Creates bounded runtime regexes without dynamic compilation or an input-derived cache.
/// Model-supplied patterns use the linear-time engine when possible and fall back to the
/// interpreted engine only when that otherwise-valid syntax is unsupported by NonBacktracking.
/// </summary>
internal static class RuntimeWorkspaceRegexFactory
{
    internal static RuntimeWorkspaceRegexCreationResult Create(
        string pattern,
        bool caseSensitive,
        TimeSpan matchTimeout) =>
        Create(
            pattern,
            caseSensitive,
            matchTimeout,
            static (candidate, options, timeout) => new Regex(candidate, options, timeout));

    /// <param name="construct">
    /// Builds the regex for one attempt. Production uses the plain constructor; a test supplies its own to
    /// prove the fallback does not depend on the wording of the engine's exception.
    /// </param>
    internal static RuntimeWorkspaceRegexCreationResult Create(
        string pattern,
        bool caseSensitive,
        TimeSpan matchTimeout,
        Func<string, RegexOptions, TimeSpan, Regex> construct)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        ArgumentNullException.ThrowIfNull(construct);

        if (matchTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(matchTimeout),
                "The regex match timeout must be positive.");
        }

        RegexOptions commonOptions = RegexOptions.CultureInvariant;

        if (!caseSensitive)
        {
            commonOptions |= RegexOptions.IgnoreCase;
        }

        try
        {
            Regex regex = construct(
                pattern,
                commonOptions | RegexOptions.NonBacktracking,
                matchTimeout);

            return new RuntimeWorkspaceRegexCreationResult(
                regex,
                RuntimeWorkspaceRegexEngine.NonBacktracking,
                ErrorCode: null,
                FallbackAttempted: false);
        }
        catch (ArgumentException)
        {
            return InvalidPattern(fallbackAttempted: false);
        }
        catch (NotSupportedException)
        {
            // Any NotSupportedException from the linear-time construction means that engine cannot
            // take this syntax. Deciding that from the exception's message text broke whenever the
            // wording changed; the interpreted attempt below is the real test of validity.

            try
            {
                Regex regex = construct(pattern, commonOptions, matchTimeout);

                return new RuntimeWorkspaceRegexCreationResult(
                    regex,
                    RuntimeWorkspaceRegexEngine.Interpreted,
                    ErrorCode: null,
                    FallbackAttempted: true);
            }
            catch (ArgumentException)
            {
                return InvalidPattern(fallbackAttempted: true);
            }
        }
    }

    private static RuntimeWorkspaceRegexCreationResult InvalidPattern(
        bool fallbackAttempted) =>
        new(
            Regex: null,
            Engine: null,
            ErrorCode: "invalid_pattern",
            FallbackAttempted: fallbackAttempted);
}
