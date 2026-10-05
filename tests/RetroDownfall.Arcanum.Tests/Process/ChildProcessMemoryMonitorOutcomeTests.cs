using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Process;

/// <summary>
/// What a tool tells the model when the macOS memory monitor stopped while its child was running
/// (<see cref="CappedChildProcessOutcome.MemoryMonitorStopped"/>). That child started and ran, so the
/// refusals written for a limit that could not be applied — "blocked", "never started" — are false for it,
/// and a model that believes them runs the command again on top of work that already happened.
/// </summary>
public sealed class ChildProcessMemoryMonitorOutcomeTests
{
    [Theory]
    [InlineData("execute_command")]
    [InlineData("run_spell_script")]
    [InlineData("workspace_check")]
    public void Description_names_the_tool_and_says_the_process_ran(string toolName)
    {
        string message = ChildProcessMemoryMonitorMessages.Describe(toolName);

        Assert.StartsWith(toolName + ":", message, StringComparison.Ordinal);

        Assert.Contains("started and ran", message, StringComparison.Ordinal);

        Assert.Contains("partly done", message, StringComparison.Ordinal);

        Assert.DoesNotContain("blocked", message, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("never started", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Workspace_check_reports_a_stopped_monitor_as_a_run_that_happened()
    {
        WorkspaceCheckDiagnosticParseResult parsed = new(
            Diagnostics: [],
            TotalDiagnosticCount: 3,
            ErrorCount: 1,
            WarningCount: 2,
            Truncated: false,
            ParsedAny: true);

        CappedChildProcessRunResult run = new()
        {
            Outcome = CappedChildProcessOutcome.MemoryMonitorStopped,

            Stdout = new CappedStreamOutput("build output so far", Truncated: false),

            Stderr = new CappedStreamOutput("a warning", Truncated: true),
        };

        WorkspaceCheckToolResultEnvelope result = WorkspaceCheckRuntime.MonitorStoppedResult(
            "dotnet-build",
            "10.0.100",
            parsed,
            run);

        Assert.Equal("failed", result.Status);

        Assert.Equal("resource_monitor_stopped", result.Code);

        Assert.Contains("started and ran", result.Message, StringComparison.Ordinal);

        // The partial work is evidence, not something to discard: the counts and the output come through,
        // and a truncated stream makes the whole result truncated.
        Assert.Equal(3, result.TotalDiagnosticCount);

        Assert.Equal(1, result.ErrorCount);

        Assert.Equal("build output so far", result.StandardOutput);

        Assert.Equal("a warning", result.StandardError);

        Assert.True(result.Truncated);
    }

    /// <summary>
    /// Every tool that turns the runner's outcome into a model-facing message also has to handle the stopped
    /// monitor. An outcome a switch does not name lands in its fallback — "failed to start the process" for
    /// <c>execute_command</c>, "could not complete" for <c>workspace_check</c> — which tells the model a
    /// command that ran either never started or left nothing to check.
    /// </summary>
    [Fact]
    public void Every_tool_that_reports_an_apply_failure_also_handles_a_stopped_monitor()
    {
        ProductionSource[] consumers =
        [
            .. ProductionSourceInventory.Sources()
                .Where(static source =>
                    source.Names("CappedChildProcessOutcome.ResourceLimitApplyFailed")
                    && !source.Is("CappedChildProcessRunner.cs")),
        ];

        // execute_command, run_spell_script and workspace_check: fewer means the scan stopped seeing them.
        Assert.True(
            consumers.Length >= 3,
            "Expected at least the three tools that map the runner's outcome, found "
            + string.Join(", ", consumers.Select(static source => source.RelativePath)) + ".");

        foreach (ProductionSource consumer in consumers)
        {
            Assert.True(
                consumer.Names("CappedChildProcessOutcome.MemoryMonitorStopped"),
                $"{consumer.RelativePath} maps ResourceLimitApplyFailed but not MemoryMonitorStopped.");
        }
    }
}
