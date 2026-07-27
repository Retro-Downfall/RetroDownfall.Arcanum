using System.Globalization;
using System.Text.RegularExpressions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

internal sealed record WorkspaceCheckDiagnosticParseResult(
    WorkspaceCheckToolResultItem[] Diagnostics,
    int TotalDiagnosticCount,
    int ErrorCount,
    int WarningCount,
    bool Truncated,
    bool ParsedAny,
    int? TotalTestCount = null,
    int? PassedTestCount = null,
    int? FailedTestCount = null,
    int? SkippedTestCount = null);

/// <summary>
/// Parses the stable text diagnostic shapes emitted by MSBuild, VSTest compiler output, and
/// dotnet-format. Unknown output remains available to the caller as capped stdout/stderr fallback.
/// </summary>
internal static partial class WorkspaceCheckDiagnosticParser
{
    private const int MaxDiagnosticLineChars = 16 * 1024;

    internal static WorkspaceCheckDiagnosticParseResult Parse(
        WorkspaceCheckDiagnosticParserKind parser,
        string standardOutput,
        string standardError,
        string workspaceRoot,
        int maxDiagnostics,
        bool includeVsTestFailures = true)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDiagnostics, 1);

        if (!Enum.IsDefined(parser))
        {

            return new WorkspaceCheckDiagnosticParseResult([], 0, 0, 0, false, false);
        }

        List<WorkspaceCheckToolResultItem> retained = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        int total = 0;
        int errors = 0;
        int warnings = 0;
        bool resourceLimited = false;
        int? totalTests = null;
        int? passedTests = null;
        int? failedTests = null;
        int? skippedTests = null;

        ParseText(standardOutput);
        ParseText(standardError);

        return new WorkspaceCheckDiagnosticParseResult(
            retained.ToArray(),
            total,
            errors,
            warnings,
            total > retained.Count || resourceLimited,
            total > 0,
            totalTests,
            passedTests,
            failedTests,
            skippedTests);

        void ParseText(string text)
        {

            if (string.IsNullOrEmpty(text))
            {

                return;
            }

            string normalizedLines = text
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');

            foreach (string rawLine in normalizedLines.Split('\n'))
            {

                if (rawLine.Length > MaxDiagnosticLineChars)
                {

                    resourceLimited = true;
                    continue;
                }

                string line = rawLine.Trim();

                if (line.Length == 0)
                {

                    continue;
                }

                if (parser == WorkspaceCheckDiagnosticParserKind.VsTest)
                {
                    Match summary =
                        VsTestSummaryRegex().Match(line);

                    if (summary.Success)
                    {
                        failedTests = ParseNullableNonNegativeInt(
                            summary.Groups["failed"].Value);
                        passedTests = ParseNullableNonNegativeInt(
                            summary.Groups["passed"].Value);
                        skippedTests = ParseNullableNonNegativeInt(
                            summary.Groups["skipped"].Value);
                        totalTests = ParseNullableNonNegativeInt(
                            summary.Groups["total"].Value);
                        continue;
                    }

                    Match testFailure = VsTestFailureRegex().Match(line);

                    if (testFailure.Success)
                    {
                        if (!includeVsTestFailures)
                        {
                            continue;
                        }

                        WorkspaceCheckToolResultItem testDiagnostic = new(
                            null,
                            null,
                            null,
                            "error",
                            "VSTEST_FAIL",
                            testFailure.Groups["name"].Value.Trim());
                        string testIdentity = string.Join(
                            '\u001f',
                            testDiagnostic.Severity,
                            testDiagnostic.Code,
                            testDiagnostic.Message);

                        if (seen.Add(testIdentity))
                        {

                            errors++;
                            total++;

                            if (retained.Count < maxDiagnostics)
                            {

                                retained.Add(testDiagnostic);

                            }

                        }

                        continue;

                    }

                }

                Match match = FileDiagnosticRegex().Match(line);

                if (!match.Success)
                {

                    match = GlobalDiagnosticRegex().Match(line);
                }

                if (!match.Success)
                {

                    continue;
                }

                string severity = match.Groups["severity"].Value.ToLowerInvariant();
                WorkspaceCheckToolResultItem diagnostic = new(
                    NormalizeFile(
                        match.Groups["file"].Value,
                        workspaceRoot),
                    ParseNullablePositiveInt(
                        match.Groups["line"].Value),
                    ParseNullablePositiveInt(
                        match.Groups["column"].Value),
                    severity,
                    EmptyToNull(match.Groups["code"].Value),
                    match.Groups["message"].Value.Trim());
                string identity = string.Join(
                    '\u001f',
                    diagnostic.File,
                    diagnostic.Line,
                    diagnostic.Column,
                    diagnostic.Severity,
                    diagnostic.Code,
                    diagnostic.Message);

                if (!seen.Add(identity))
                {

                    continue;
                }

                if (severity == "error")
                {

                    errors++;
                }
                else
                {

                    warnings++;
                }

                total++;

                if (retained.Count >= maxDiagnostics)
                {

                    continue;
                }

                retained.Add(diagnostic);
            }

        }
    }

    internal static WorkspaceCheckDiagnosticParseResult
        MergeAuthoritativeTrx(
            WorkspaceCheckDiagnosticParseResult console,
            WorkspaceCheckTrxParseResult trx,
            int maxDiagnostics)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(trx);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            maxDiagnostics,
            1);

        List<WorkspaceCheckToolResultItem> retained = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (WorkspaceCheckToolResultItem diagnostic in
                 console.Diagnostics.Concat(trx.Diagnostics))
        {
            if (seen.Add(DiagnosticIdentity(diagnostic))
                && retained.Count < maxDiagnostics)
            {
                retained.Add(diagnostic);
            }
        }

        int total = console.TotalDiagnosticCount
            + trx.TotalDiagnosticCount;

        return new WorkspaceCheckDiagnosticParseResult(
            retained.ToArray(),
            total,
            console.ErrorCount + trx.TotalDiagnosticCount,
            console.WarningCount,
            console.Truncated
            || trx.Truncated
            || total > retained.Count,
            total > 0,
            trx.TotalTestCount,
            trx.PassedTestCount,
            trx.FailedTestCount,
            trx.SkippedTestCount);
    }

    private static string DiagnosticIdentity(
        WorkspaceCheckToolResultItem diagnostic) =>
        string.Join(
            '\u001f',
            diagnostic.File,
            diagnostic.Line,
            diagnostic.Column,
            diagnostic.Severity,
            diagnostic.Code,
            diagnostic.Message);

    private static string? NormalizeFile(string value, string workspaceRoot)
    {

        if (string.IsNullOrWhiteSpace(value))
        {

            return null;
        }

        try
        {

            string normalizedRoot = CanonicalizeExistingPath(
                    workspaceRoot,
                    expectDirectory: true)
                ?? Path.GetFullPath(workspaceRoot);
            string candidate = Path.IsPathFullyQualified(value)
                ? Path.GetFullPath(value)
                : Path.GetFullPath(value, normalizedRoot);
            candidate = CanonicalizeExistingPath(
                    candidate,
                    expectDirectory: false)
                ?? candidate;
            string relative = Path.GetRelativePath(normalizedRoot, candidate);

            if (Path.IsPathRooted(relative)
                || string.Equals(relative, "..", StringComparison.Ordinal)
                || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            {

                return null;
            }

            return relative.Replace(Path.DirectorySeparatorChar, '/');
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {

            return null;
        }
    }

    private static int? ParseNullablePositiveInt(string value) =>
        int.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out int parsed)
        && parsed > 0
            ? parsed
            : null;

    private static int? ParseNullableNonNegativeInt(string value) =>
        int.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out int parsed)
        && parsed >= 0
            ? parsed
            : null;

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? CanonicalizeExistingPath(
        string path,
        bool expectDirectory)
    {

        string fullPath = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(fullPath);

        if (string.IsNullOrEmpty(root))
        {

            return null;
        }

        string current = root;
        string[] components = fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        for (int index = 0; index < components.Length; index++)
        {

            bool final = index == components.Length - 1;
            string candidate = Path.Combine(current, components[index]);
            FileSystemInfo entry = final && !expectDirectory
                ? new FileInfo(candidate)
                : new DirectoryInfo(candidate);

            if (!entry.Exists)
            {

                return null;
            }

            FileSystemInfo? resolved = entry.ResolveLinkTarget(
                returnFinalTarget: true);
            current = Path.GetFullPath(
                resolved?.FullName ?? candidate);
        }

        return Path.TrimEndingDirectorySeparator(current);
    }

    [GeneratedRegex(
        @"^(?<file>.+?)\((?<line>\d+)(?:,(?<column>\d+))?(?:,\d+,\d+)?\):\s*(?<severity>error|warning)\s*(?<code>[A-Za-z0-9_.-]+)?\s*:\s*(?<message>.*?)(?:\s+\[[^\]]+\])?$",
        RegexOptions.CultureInvariant
        | RegexOptions.IgnoreCase
        | RegexOptions.NonBacktracking)]
    private static partial Regex FileDiagnosticRegex();

    [GeneratedRegex(
        @"^(?:(?<file>[^:]+):\s*)?(?<severity>error|warning)\s*(?<code>[A-Za-z0-9_.-]+)?\s*:\s*(?<message>.*)$",
        RegexOptions.CultureInvariant
        | RegexOptions.IgnoreCase
        | RegexOptions.NonBacktracking)]
    private static partial Regex GlobalDiagnosticRegex();

    [GeneratedRegex(
        @"^Failed\s+(?<name>.+?)(?:\s+\[[^\]]+\])?$",
        RegexOptions.CultureInvariant
        | RegexOptions.NonBacktracking)]
    private static partial Regex VsTestFailureRegex();

    [GeneratedRegex(
        @"^(?:Passed|Failed)!\s*-\s*Failed:\s*(?<failed>\d+),\s*Passed:\s*(?<passed>\d+),\s*Skipped:\s*(?<skipped>\d+),\s*Total:\s*(?<total>\d+)(?:,.*)?$",
        RegexOptions.CultureInvariant
        | RegexOptions.NonBacktracking)]
    private static partial Regex VsTestSummaryRegex();
}
