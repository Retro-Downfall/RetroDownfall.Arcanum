using RetroDownfall.Arcanum.Cli.Infrastructure.Surface;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// The hidden worker mode is dispatched by both hosts before anything reads configuration, the installation or the
/// command line, and it never appears on the command surface.
/// </summary>
public sealed class ComprehensionWorkerModeWiringTests
{
    private const string WorkerDispatch = "ComprehensionWorkerEntry.TryHandle(args, typeof(Program), out int workerExitCode)";

    private const string BrokerDispatch = "SandboxExecHelper.TryHandle(args, typeof(Program))";

    [Theory]
    [InlineData("src/RetroDownfall.Arcanum.Cli/Program.cs", "RunBeforeConfigurationAsync(")]
    [InlineData("src/RetroDownfall.Arcanum.Cli/Program.cs", "AppContext.SetSwitch(")]
    [InlineData("src/RetroDownfall.Arcanum.Api.DevHost/Program.cs", "WebApplication.CreateSlimBuilder(")]
    public void The_host_dispatches_the_worker_mode_right_after_the_broker_and_before_its_start(string relativePath, string start)
    {
        string source = File.ReadAllText(Path.Combine(TestRepositoryPaths.RepositoryRoot(), relativePath));

        int broker = source.IndexOf(BrokerDispatch, StringComparison.Ordinal);

        int worker = source.IndexOf(WorkerDispatch, StringComparison.Ordinal);

        int started = source.IndexOf(start, StringComparison.Ordinal);

        Assert.True(broker >= 0, $"{relativePath} no longer dispatches the sandbox broker first.");

        Assert.True(worker > broker, $"{relativePath} does not dispatch the worker mode after the broker.");

        Assert.True(started > worker, $"{relativePath} reaches {start} before it dispatches the worker mode.");

        Assert.Contains("return workerExitCode;", source[worker..started], StringComparison.Ordinal);
    }

    [Fact]
    public void The_worker_mode_is_absent_from_the_live_command_surface_and_the_committed_map()
    {
        Assert.DoesNotContain(
            CliSurfaceTests.Walk(CliSurfaceTests.BuildMap()),
            static command => command.Path.Contains(ComprehensionWorkerEntry.ModeArgument, StringComparison.Ordinal));

        string committed = File.ReadAllText(Path.Combine(TestRepositoryPaths.RepositoryRoot(), "docs", "Arcanum.CommandMap.json"));

        Assert.DoesNotContain(ComprehensionWorkerEntry.ModeArgument, committed, StringComparison.Ordinal);
    }

    [Fact]
    public void This_test_host_is_not_a_worker_capable_host()
    {
        // Re-executing the test platform with the worker arguments would start the test platform, so nothing here may
        // claim the worker. Reading the capability writes nothing, unlike calling TryHandle.
        Assert.False(ComprehensionWorkerEntry.IsWorkerCapableHost);
    }
}
