using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class WorkspaceCheckTrxParserTests
{
    [Fact]
    public void Parse_derives_authoritative_counts_and_failure_diagnostics()
    {
        string root = CreateRoot();

        try
        {
            File.WriteAllText(
                Path.Combine(root, "results.trx"),
                """
                <?xml version="1.0" encoding="utf-8"?>
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                  <Results>
                    <UnitTestResult testName="Sample.Passes" outcome="Passed" />
                    <UnitTestResult testName="Sample.Fails" outcome="Failed">
                      <Output>
                        <ErrorInfo>
                          <Message>Expected true but found false.</Message>
                        </ErrorInfo>
                      </Output>
                    </UnitTestResult>
                    <UnitTestResult testName="Sample.Skips" outcome="NotExecuted" />
                  </Results>
                  <ResultSummary outcome="Failed">
                    <Counters total="3" executed="2" passed="1" failed="1" notExecuted="1" />
                  </ResultSummary>
                </TestRun>
                """);

            WorkspaceCheckTrxParseResult parsed =
                WorkspaceCheckTrxParser.Parse(
                    root,
                    maxDiagnostics: 10,
                    maxBytes: 1024 * 1024);

            Assert.True(parsed.ParsedAny);
            Assert.Equal(3, parsed.TotalTestCount);
            Assert.Equal(1, parsed.PassedTestCount);
            Assert.Equal(1, parsed.FailedTestCount);
            Assert.Equal(1, parsed.SkippedTestCount);
            Assert.False(parsed.Truncated);
            Assert.Collection(
                parsed.Diagnostics,
                diagnostic =>
                {
                    Assert.Equal("error", diagnostic.Severity);
                    Assert.Equal("VSTEST_FAIL", diagnostic.Code);
                    Assert.Contains(
                        "Sample.Fails",
                        diagnostic.Message,
                        StringComparison.Ordinal);
                    Assert.Contains(
                        "Expected true",
                        diagnostic.Message,
                        StringComparison.Ordinal);
                });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Parse_refuses_oversized_trx_and_allows_console_fallback()
    {
        string root = CreateRoot();

        try
        {
            File.WriteAllText(
                Path.Combine(root, "oversized.trx"),
                new string('x', 4096));

            WorkspaceCheckTrxParseResult parsed =
                WorkspaceCheckTrxParser.Parse(
                    root,
                    maxDiagnostics: 10,
                    maxBytes: 1024);

            Assert.False(parsed.ParsedAny);
            Assert.Empty(parsed.Diagnostics);
            Assert.True(parsed.Truncated);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Parse_missing_results_root_returns_empty_without_truncation()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "arcanum-trx-missing-" + Guid.NewGuid().ToString("N"));

        WorkspaceCheckTrxParseResult parsed =
            WorkspaceCheckTrxParser.Parse(
                root,
                maxDiagnostics: 10,
                maxBytes: 1024);

        Assert.False(parsed.ParsedAny);
        Assert.False(parsed.Truncated);
        Assert.Empty(parsed.Diagnostics);
        Assert.Equal(0, parsed.TotalTestCount);
    }

    [Fact]
    public void Parse_without_counters_classifies_results_and_continues_after_malformed_file()
    {
        string root = CreateRoot();

        try
        {
            File.WriteAllText(
                Path.Combine(root, "a-malformed.trx"),
                "<TestRun><Results>");
            File.WriteAllText(
                Path.Combine(root, "b-results.trx"),
                """
                <TestRun>
                  <Results>
                    <UnitTestResult testName="Sample.Completed" outcome="Completed" />
                    <UnitTestResult testName="Sample.Skipped" outcome="Skipped" />
                    <UnitTestResult testName="Sample.Inconclusive" outcome="Inconclusive" />
                    <UnitTestResult testName="Sample.Fails" outcome="Failed" />
                    <UnitTestResult testName="Sample.Fails" outcome="Failed" />
                  </Results>
                </TestRun>
                """);

            WorkspaceCheckTrxParseResult parsed =
                WorkspaceCheckTrxParser.Parse(
                    root,
                    maxDiagnostics: 10,
                    maxBytes: 1024 * 1024);

            Assert.True(parsed.ParsedAny);
            Assert.True(parsed.Truncated);
            Assert.Equal(5, parsed.TotalTestCount);
            Assert.Equal(1, parsed.PassedTestCount);
            Assert.Equal(2, parsed.SkippedTestCount);
            Assert.Equal(2, parsed.FailedTestCount);
            WorkspaceCheckToolResultItem diagnostic =
                Assert.Single(parsed.Diagnostics);
            Assert.Equal("Sample.Fails", diagnostic.Message);
            Assert.Equal(1, parsed.TotalDiagnosticCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Parse_caps_failure_messages_and_retained_diagnostics()
    {
        string root = CreateRoot();

        try
        {
            string detail = new('x', 8 * 1024);
            File.WriteAllText(
                Path.Combine(root, "results.trx"),
                $"""
                 <TestRun>
                   <Results>
                     <UnitTestResult testName="Sample.First" outcome="Failed">
                       <Output><ErrorInfo><Message>{detail}</Message></ErrorInfo></Output>
                     </UnitTestResult>
                     <UnitTestResult testName="Sample.Second" outcome="Failed" />
                   </Results>
                 </TestRun>
                 """);

            WorkspaceCheckTrxParseResult parsed =
                WorkspaceCheckTrxParser.Parse(
                    root,
                    maxDiagnostics: 1,
                    maxBytes: 64 * 1024);

            Assert.True(parsed.ParsedAny);
            Assert.True(parsed.Truncated);
            Assert.Equal(2, parsed.TotalDiagnosticCount);
            WorkspaceCheckToolResultItem retained =
                Assert.Single(parsed.Diagnostics);
            Assert.Equal(4 * 1024, retained.Message.Length);
            Assert.StartsWith(
                "Sample.First: ",
                retained.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Parse_rejects_multi_file_max_counter_overflow_without_negative_counts()
    {
        string root = CreateRoot();

        try
        {
            string trx =
                $"""
                 <TestRun>
                   <ResultSummary>
                     <Counters total="{int.MaxValue}" passed="{int.MaxValue}" failed="0" notExecuted="0" />
                   </ResultSummary>
                 </TestRun>
                 """;
            File.WriteAllText(
                Path.Combine(root, "a-results.trx"),
                trx);
            File.WriteAllText(
                Path.Combine(root, "b-results.trx"),
                trx);

            WorkspaceCheckTrxParseResult parsed =
                WorkspaceCheckTrxParser.Parse(
                    root,
                    maxDiagnostics: 10,
                    maxBytes: 1024 * 1024);

            Assert.True(parsed.ParsedAny);
            Assert.True(parsed.Truncated);
            Assert.Equal(0, parsed.TotalTestCount);
            Assert.Equal(0, parsed.PassedTestCount);
            Assert.Equal(0, parsed.FailedTestCount);
            Assert.Equal(0, parsed.SkippedTestCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Parse_rejects_inconsistent_max_counters_and_uses_nonnegative_result_counts()
    {
        string root = CreateRoot();

        try
        {
            string trx =
                $"""
                 <TestRun>
                   <Results>
                     <UnitTestResult testName="Sample.Passes" outcome="Passed" />
                   </Results>
                   <ResultSummary>
                     <Counters total="{int.MaxValue}" passed="{int.MaxValue}" failed="{int.MaxValue}" notExecuted="{int.MaxValue}" />
                   </ResultSummary>
                 </TestRun>
                 """;
            File.WriteAllText(
                Path.Combine(root, "a-results.trx"),
                trx);
            File.WriteAllText(
                Path.Combine(root, "b-results.trx"),
                trx);

            WorkspaceCheckTrxParseResult parsed =
                WorkspaceCheckTrxParser.Parse(
                    root,
                    maxDiagnostics: 10,
                    maxBytes: 1024 * 1024);

            Assert.True(parsed.ParsedAny);
            Assert.True(parsed.Truncated);
            Assert.Equal(2, parsed.TotalTestCount);
            Assert.Equal(2, parsed.PassedTestCount);
            Assert.Equal(0, parsed.FailedTestCount);
            Assert.Equal(0, parsed.SkippedTestCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Parse_rejects_out_of_range_aggregate_and_uses_all_result_fallback_counts()
    {
        string root = CreateRoot();

        try
        {
            File.WriteAllText(
                Path.Combine(root, "a-results.trx"),
                $"""
                 <TestRun>
                   <Results>
                     <UnitTestResult testName="Sample.Passes" outcome="Passed" />
                   </Results>
                   <ResultSummary>
                     <Counters total="{int.MaxValue}" passed="{int.MaxValue}" failed="0" notExecuted="0" />
                   </ResultSummary>
                 </TestRun>
                 """);
            File.WriteAllText(
                Path.Combine(root, "b-results.trx"),
                $"""
                 <TestRun>
                   <Results>
                     <UnitTestResult testName="Sample.Fails" outcome="Failed" />
                   </Results>
                   <ResultSummary>
                     <Counters total="{int.MaxValue}" passed="0" failed="{int.MaxValue}" notExecuted="0" />
                   </ResultSummary>
                 </TestRun>
                 """);

            WorkspaceCheckTrxParseResult parsed =
                WorkspaceCheckTrxParser.Parse(
                    root,
                    maxDiagnostics: 10,
                    maxBytes: 1024 * 1024);

            Assert.True(parsed.ParsedAny);
            Assert.True(parsed.Truncated);
            Assert.Equal(2, parsed.TotalTestCount);
            Assert.Equal(1, parsed.PassedTestCount);
            Assert.Equal(1, parsed.FailedTestCount);
            Assert.Equal(0, parsed.SkippedTestCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Parse_refuses_results_root_replaced_by_symlink()
    {
        string controlRoot = CreateRoot();
        string resultsRoot = Path.Combine(controlRoot, "results");
        string externalRoot = CreateRoot();
        Directory.CreateDirectory(resultsRoot);
        File.WriteAllText(
            Path.Combine(externalRoot, "escaped.trx"),
            """
            <TestRun>
              <Results>
                <UnitTestResult testName="Escaped.Test" outcome="Failed" />
              </Results>
            </TestRun>
            """);
        WorkspaceCheckTrxSource source =
            WorkspaceCheckTrxSource.Capture(
                controlRoot,
                resultsRoot);

        try
        {
            Directory.Delete(resultsRoot);
            Directory.CreateSymbolicLink(
                resultsRoot,
                externalRoot);

            WorkspaceCheckTrxParseResult parsed =
                WorkspaceCheckTrxParser.Parse(
                    source,
                    maxDiagnostics: 10,
                    maxBytes: 1024 * 1024);

            Assert.False(parsed.ParsedAny);
            Assert.True(parsed.Truncated);
            Assert.Empty(parsed.Diagnostics);
            Assert.Equal(0, parsed.TotalTestCount);
        }
        finally
        {
            Directory.Delete(controlRoot, recursive: true);
            Directory.Delete(externalRoot, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "arcanum-trx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
