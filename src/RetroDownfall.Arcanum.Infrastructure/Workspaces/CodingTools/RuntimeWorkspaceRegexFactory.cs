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
        TimeSpan matchTimeout)
    {

        ArgumentNullException.ThrowIfNull(pattern);

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

            Regex regex = new(
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
        catch (NotSupportedException exception) when (
            IsNonBacktrackingUnsupportedFeature(exception))
        {

            try
            {

                Regex regex = new(pattern, commonOptions, matchTimeout);

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

    private static bool IsNonBacktrackingUnsupportedFeature(
        NotSupportedException exception) =>
        exception.Message.Contains(
            nameof(RegexOptions.NonBacktracking),
            StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains(
            "non-backtracking",
            StringComparison.OrdinalIgnoreCase);

    private static RuntimeWorkspaceRegexCreationResult InvalidPattern(
        bool fallbackAttempted) =>
        new(
            Regex: null,
            Engine: null,
            ErrorCode: "invalid_pattern",
            FallbackAttempted: fallbackAttempted);

}
