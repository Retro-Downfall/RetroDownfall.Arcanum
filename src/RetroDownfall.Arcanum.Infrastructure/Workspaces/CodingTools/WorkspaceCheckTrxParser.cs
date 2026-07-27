using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

internal sealed record WorkspaceCheckTrxParseResult(
    WorkspaceCheckToolResultItem[] Diagnostics,
    int TotalDiagnosticCount,
    int TotalTestCount,
    int PassedTestCount,
    int FailedTestCount,
    int SkippedTestCount,
    bool ParsedAny,
    bool Truncated);

internal sealed record WorkspaceCheckTrxSource(
    string ControlRoot,
    FileHandleIdentity ControlRootIdentity,
    string TestResultsRoot,
    FileHandleIdentity TestResultsRootIdentity)
{
    internal static WorkspaceCheckTrxSource Capture(
        string controlRoot,
        string testResultsRoot)
    {
        string canonicalControlRoot =
            Path.GetFullPath(controlRoot);
        string canonicalTestResultsRoot =
            Path.GetFullPath(testResultsRoot);

        if (!WorkspaceRootPath.IsWithinOrEqual(
                canonicalTestResultsRoot,
                canonicalControlRoot)
            || string.Equals(
                canonicalTestResultsRoot,
                canonicalControlRoot,
                StringComparison.Ordinal)
            || !TryReadDirectoryIdentity(
                canonicalControlRoot,
                out FileHandleIdentity controlIdentity)
            || !TryReadDirectoryIdentity(
                canonicalTestResultsRoot,
                out FileHandleIdentity testResultsIdentity))
        {
            throw new IOException(
                "The workspace-check TRX source could not be captured safely.");
        }

        return new WorkspaceCheckTrxSource(
            canonicalControlRoot,
            controlIdentity,
            canonicalTestResultsRoot,
            testResultsIdentity);
    }

    internal bool Revalidate() =>
        WorkspaceRootPath.IsWithinOrEqual(
            TestResultsRoot,
            ControlRoot)
        && !string.Equals(
            TestResultsRoot,
            ControlRoot,
            StringComparison.Ordinal)
        && TryReadDirectoryIdentity(
            ControlRoot,
            out FileHandleIdentity controlIdentity)
        && FileHandleIdentity.IdentitiesMatch(
            ControlRootIdentity,
            controlIdentity)
        && TryReadDirectoryIdentity(
            TestResultsRoot,
            out FileHandleIdentity testResultsIdentity)
        && FileHandleIdentity.IdentitiesMatch(
            TestResultsRootIdentity,
            testResultsIdentity);

    private static bool TryReadDirectoryIdentity(
        string path,
        out FileHandleIdentity identity)
    {
        identity = default;

        if (!FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                path,
                out FileHandleMetadata metadata)
            || metadata.Kind != FileSystemObjectKind.Directory)
        {
            return false;
        }

        identity = metadata.Identity;
        return true;
    }
}

internal static class WorkspaceCheckTrxParser
{
    private const int MaxTrxFiles = 32;
    private const int MaxDiagnosticMessageChars = 4 * 1024;

    internal static WorkspaceCheckTrxParseResult Parse(
        string testResultsRoot,
        int maxDiagnostics,
        long maxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(testResultsRoot);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            maxDiagnostics,
            1);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            maxBytes,
            1);

        if (!Directory.Exists(testResultsRoot))
        {
            return Empty(truncated: false);
        }

        WorkspaceCheckTrxSource source;

        try
        {
            source = WorkspaceCheckTrxSource.Capture(
                Path.GetDirectoryName(
                    Path.GetFullPath(testResultsRoot))!,
                testResultsRoot);
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            return Empty(truncated: true);
        }

        return Parse(
            source,
            maxDiagnostics,
            maxBytes);
    }

    internal static WorkspaceCheckTrxParseResult Parse(
        WorkspaceCheckTrxSource source,
        int maxDiagnostics,
        long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            maxDiagnostics,
            1);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            maxBytes,
            1);

        if (!source.Revalidate())
        {
            return Empty(truncated: true);
        }

        List<string> files;

        try
        {
            files = Directory
                .EnumerateFiles(
                    source.TestResultsRoot,
                    "*.trx",
                    SearchOption.TopDirectoryOnly)
                .OrderBy(
                    static path => path,
                    StringComparer.Ordinal)
                .Take(MaxTrxFiles + 1)
                .ToList();
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException)
        {
            return Empty(truncated: true);
        }

        bool truncated = files.Count > MaxTrxFiles;
        long consumedBytes = 0;
        long aggregateTotalTests = 0;
        long aggregatePassedTests = 0;
        long aggregateFailedTests = 0;
        long aggregateSkippedTests = 0;
        long fallbackTotalTests = 0;
        long fallbackPassedTests = 0;
        long fallbackFailedTests = 0;
        long fallbackSkippedTests = 0;
        int totalDiagnostics = 0;
        bool parsedAny = false;
        List<WorkspaceCheckToolResultItem> diagnostics = [];
        HashSet<string> seenDiagnostics =
            new(StringComparer.Ordinal);

        foreach (string path in files.Take(MaxTrxFiles))
        {
            try
            {
                string canonicalPath = Path.GetFullPath(path);

                if (!source.Revalidate()
                    || !WorkspaceRootPath.IsWithinOrEqual(
                        canonicalPath,
                        source.TestResultsRoot)
                    || string.Equals(
                        canonicalPath,
                        source.TestResultsRoot,
                        StringComparison.Ordinal)
                    || !FileHandleIdentityInterop
                        .TryGetPathMetadataNoFollow(
                            canonicalPath,
                            out FileHandleMetadata pathMetadata)
                    || pathMetadata.Kind
                        != FileSystemObjectKind.RegularFile
                    || pathMetadata.HardLinkCount != 1)
                {
                    truncated = true;
                    continue;
                }

                using FileStream stream = new(
                    canonicalPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 16 * 1024,
                    FileOptions.SequentialScan);

                if (!FileHandleIdentityInterop.TryGetHandleMetadata(
                        stream.SafeFileHandle,
                        out FileHandleMetadata handleMetadata)
                    || handleMetadata.Kind
                        != FileSystemObjectKind.RegularFile
                    || handleMetadata.HardLinkCount != 1
                    || !FileHandleIdentity.IdentitiesMatch(
                        pathMetadata.Identity,
                        handleMetadata.Identity)
                    || !FileHandleIdentityInterop
                        .TryGetPathMetadataNoFollow(
                            canonicalPath,
                            out FileHandleMetadata currentPathMetadata)
                    || !FileHandleIdentity.IdentitiesMatch(
                        handleMetadata.Identity,
                        currentPathMetadata.Identity)
                    || stream.Length <= 0
                    || stream.Length > maxBytes - consumedBytes)
                {
                    truncated = true;
                    continue;
                }

                consumedBytes += stream.Length;
                XmlReaderSettings settings = new()
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    IgnoreComments = true,
                    IgnoreProcessingInstructions = true,
                    MaxCharactersFromEntities = 0,
                    MaxCharactersInDocument = stream.Length,
                };
                using XmlReader reader = XmlReader.Create(
                    stream,
                    settings);
                XDocument document = XDocument.Load(
                    reader,
                    LoadOptions.None);

                if (!source.Revalidate()
                    || !FileHandleIdentityInterop
                        .TryGetPathMetadataNoFollow(
                            canonicalPath,
                            out FileHandleMetadata afterParseMetadata)
                    || !FileHandleIdentity.IdentitiesMatch(
                        handleMetadata.Identity,
                        afterParseMetadata.Identity))
                {
                    truncated = true;
                    continue;
                }

                XElement[] results = document
                    .Descendants()
                    .Where(static element =>
                        element.Name.LocalName
                        == "UnitTestResult")
                    .ToArray();
                XElement? counters = document
                    .Descendants()
                    .FirstOrDefault(static element =>
                        element.Name.LocalName == "Counters");
                int total = 0;
                int passed = 0;
                int failed = 0;
                int skipped = 0;
                bool validCounters = counters is not null
                    && TryReadCounters(
                        counters,
                        out total,
                        out passed,
                        out failed,
                        out skipped);
                int resultPassed = 0;
                int resultFailed = 0;
                int resultSkipped = 0;

                foreach (XElement result in results)
                {
                    string outcome =
                        ReadAttribute(result, "outcome");

                    if (IsPassed(outcome))
                    {
                        resultPassed++;
                    }
                    else if (IsSkipped(outcome))
                    {
                        resultSkipped++;
                    }
                    else
                    {
                        resultFailed++;
                    }
                }

                fallbackTotalTests += results.Length;
                fallbackPassedTests += resultPassed;
                fallbackFailedTests += resultFailed;
                fallbackSkippedTests += resultSkipped;

                if (validCounters)
                {
                    aggregateTotalTests += total;
                    aggregatePassedTests += passed;
                    aggregateFailedTests += failed;
                    aggregateSkippedTests += skipped;
                }
                else
                {
                    if (counters is not null)
                    {
                        truncated = true;
                    }

                    aggregateTotalTests += results.Length;
                    aggregatePassedTests += resultPassed;
                    aggregateFailedTests += resultFailed;
                    aggregateSkippedTests += resultSkipped;
                }

                foreach (XElement result in results)
                {
                    string outcome =
                        ReadAttribute(result, "outcome");

                    if (IsPassed(outcome)
                        || IsSkipped(outcome))
                    {
                        continue;
                    }

                    string testName =
                        ReadAttribute(result, "testName");
                    string detail = result
                        .Descendants()
                        .FirstOrDefault(static element =>
                            element.Name.LocalName == "Message")
                        ?.Value.Trim()
                        ?? string.Empty;
                    string message =
                        string.IsNullOrWhiteSpace(detail)
                            ? testName
                            : $"{testName}: {detail}";

                    if (message.Length
                        > MaxDiagnosticMessageChars)
                    {
                        message = message[
                            ..MaxDiagnosticMessageChars];
                        truncated = true;
                    }

                    if (!seenDiagnostics.Add(message))
                    {
                        continue;
                    }

                    totalDiagnostics++;

                    if (diagnostics.Count < maxDiagnostics)
                    {
                        diagnostics.Add(
                            new WorkspaceCheckToolResultItem(
                                null,
                                null,
                                null,
                                "error",
                                "VSTEST_FAIL",
                                message));
                    }
                }

                parsedAny = true;
            }
            catch (Exception ex) when (
                ex is IOException
                    or UnauthorizedAccessException
                    or XmlException
                    or InvalidOperationException)
            {
                truncated = true;
            }
        }

        if (!IsValidAggregate(
                aggregateTotalTests,
                aggregatePassedTests,
                aggregateFailedTests,
                aggregateSkippedTests))
        {
            aggregateTotalTests = fallbackTotalTests;
            aggregatePassedTests = fallbackPassedTests;
            aggregateFailedTests = fallbackFailedTests;
            aggregateSkippedTests = fallbackSkippedTests;
            truncated = true;
        }

        ToBoundedCounts(
            aggregateTotalTests,
            aggregatePassedTests,
            aggregateFailedTests,
            aggregateSkippedTests,
            ref truncated,
            out int totalTests,
            out int passedTests,
            out int failedTests,
            out int skippedTests);

        return new WorkspaceCheckTrxParseResult(
            diagnostics.ToArray(),
            totalDiagnostics,
            totalTests,
            passedTests,
            failedTests,
            skippedTests,
            parsedAny,
            truncated
            || totalDiagnostics > diagnostics.Count);
    }

    private static bool TryReadCount(
        XElement counters,
        string name,
        out int value) =>
        int.TryParse(
            ReadAttribute(counters, name),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out value)
        && value >= 0;

    private static bool TryReadCounters(
        XElement counters,
        out int total,
        out int passed,
        out int failed,
        out int skipped)
    {
        total = 0;
        passed = 0;
        failed = 0;
        skipped = 0;

        if (!TryReadCount(counters, "total", out total)
            || !TryReadCount(counters, "passed", out passed)
            || !TryReadCount(counters, "failed", out failed)
            || !TryReadOptionalCount(
                counters,
                "notExecuted",
                out skipped))
        {
            return false;
        }

        long categorized =
            (long)passed + failed + skipped;

        return passed <= total
            && failed <= total
            && skipped <= total
            && categorized <= total;
    }

    private static bool TryReadOptionalCount(
        XElement counters,
        string name,
        out int value)
    {
        XAttribute? attribute = counters.Attribute(name);

        if (attribute is null)
        {
            value = 0;
            return true;
        }

        return int.TryParse(
                attribute.Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out value)
            && value >= 0;
    }

    private static bool IsValidAggregate(
        long total,
        long passed,
        long failed,
        long skipped)
    {
        if (total < 0
            || total > int.MaxValue
            || passed < 0
            || passed > int.MaxValue
            || failed < 0
            || failed > int.MaxValue
            || skipped < 0
            || skipped > int.MaxValue)
        {
            return false;
        }

        long categorized = passed + failed + skipped;
        return categorized <= total;
    }

    private static void ToBoundedCounts(
        long total,
        long passed,
        long failed,
        long skipped,
        ref bool truncated,
        out int boundedTotal,
        out int boundedPassed,
        out int boundedFailed,
        out int boundedSkipped)
    {
        long safeTotal = Math.Clamp(
            total,
            0,
            int.MaxValue);
        long safePassed = Math.Clamp(
            passed,
            0,
            safeTotal);
        long remaining = safeTotal - safePassed;
        long safeFailed = Math.Clamp(
            failed,
            0,
            remaining);
        remaining -= safeFailed;
        long safeSkipped = Math.Clamp(
            skipped,
            0,
            remaining);

        if (safeTotal != total
            || safePassed != passed
            || safeFailed != failed
            || safeSkipped != skipped)
        {
            truncated = true;
        }

        boundedTotal = (int)safeTotal;
        boundedPassed = (int)safePassed;
        boundedFailed = (int)safeFailed;
        boundedSkipped = (int)safeSkipped;
    }

    private static string ReadAttribute(
        XElement element,
        string name) =>
        element.Attribute(name)?.Value
        ?? string.Empty;

    private static bool IsPassed(string outcome) =>
        string.Equals(
            outcome,
            "Passed",
            StringComparison.OrdinalIgnoreCase)
        || string.Equals(
            outcome,
            "Completed",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsSkipped(string outcome) =>
        string.Equals(
            outcome,
            "NotExecuted",
            StringComparison.OrdinalIgnoreCase)
        || string.Equals(
            outcome,
            "Skipped",
            StringComparison.OrdinalIgnoreCase)
        || string.Equals(
            outcome,
            "Inconclusive",
            StringComparison.OrdinalIgnoreCase);

    private static WorkspaceCheckTrxParseResult Empty(
        bool truncated) =>
        new(
            [],
            0,
            0,
            0,
            0,
            0,
            ParsedAny: false,
            truncated);
}
