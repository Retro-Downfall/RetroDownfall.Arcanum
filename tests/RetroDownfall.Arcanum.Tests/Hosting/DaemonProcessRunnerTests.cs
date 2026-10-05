using System.Diagnostics;
using System.Globalization;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class DaemonProcessRunnerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "arcanum-daemon-runner-" + Guid.NewGuid().ToString("N"));

    public DaemonProcessRunnerTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [SkippableFact]
    public async Task HungChild_IsKilledAtTimeout()
    {
        Skip.If(OperatingSystem.IsWindows(), "Uses /bin/sh and sleep.");

        string pidFile = Path.Combine(_directory, "child.pid");

        DaemonProcessRunner runner = new(TimeSpan.FromMilliseconds(200));

        Stopwatch elapsed = Stopwatch.StartNew();

        DaemonProcessOutcome outcome = await runner.RunAsync(
            "/bin/sh",
            ["-c", $"echo $$ > '{pidFile}'; exec sleep 60"],
            CancellationToken.None);

        elapsed.Stop();

        Assert.True(outcome.FatalError.HasValue);

        Assert.Equal(DaemonProcessRunner.TimeoutErrorCode, outcome.FatalError.Value.Code);

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(20), $"The runner waited {elapsed.Elapsed}.");

        await AssertChildGoneAsync(pidFile);
    }

    [SkippableFact]
    public async Task Cancellation_KillsTheChildAndPropagates()
    {
        Skip.If(OperatingSystem.IsWindows(), "Uses /bin/sh and sleep.");

        string pidFile = Path.Combine(_directory, "cancelled.pid");

        DaemonProcessRunner runner = new(TimeSpan.FromSeconds(60));

        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync(
                "/bin/sh",
                ["-c", $"echo $$ > '{pidFile}'; exec sleep 60"],
                cancellation.Token));

        await AssertChildGoneAsync(pidFile);
    }

    [Fact]
    public async Task MissingExecutable_ReturnsFatalErrorInsteadOfThrowing()
    {
        string missing = "arcanum-missing-" + Guid.NewGuid().ToString("N");

        DaemonProcessOutcome outcome = await DaemonProcessRunner.Default.RunAsync(
            missing,
            [],
            CancellationToken.None);

        Assert.True(outcome.FatalError.HasValue);

        Assert.Equal(DaemonProcessRunner.StartErrorCode, outcome.FatalError.Value.Code);

        Assert.Contains(missing, outcome.FatalError.Value.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task FinishedChild_ReturnsItsExitCodeAndStreams()
    {
        Skip.If(OperatingSystem.IsWindows(), "Uses /bin/sh.");

        DaemonProcessOutcome outcome = await DaemonProcessRunner.Default.RunAsync(
            "/bin/sh",
            ["-c", "echo out; echo err >&2; exit 7"],
            CancellationToken.None);

        Assert.Null(outcome.FatalError);

        Assert.Equal(7, outcome.ExitCode);

        Assert.Equal("out", outcome.StdOut.Trim());

        Assert.Equal("err", outcome.StdErr.Trim());
    }

    private static async Task AssertChildGoneAsync(string pidFile)
    {
        Assert.True(File.Exists(pidFile), "The child never recorded its pid.");

        int pid = int.Parse(File.ReadAllText(pidFile).Trim(), CultureInfo.InvariantCulture);

        DateTime deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            if (!IsAlive(pid))
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Child process {pid} is still running.");
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(pid);

            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
