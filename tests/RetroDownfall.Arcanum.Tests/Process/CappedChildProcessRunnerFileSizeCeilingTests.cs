using System.Diagnostics;

using RetroDownfall.Arcanum.Core.Platform;

using RetroDownfall.Arcanum.Core.Sanctum;

using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

namespace RetroDownfall.Arcanum.Tests.Process;

/// <summary>
/// A file-size ceiling asked of the runner is a promise to its caller: a run that cannot have it is refused before
/// anything starts, never started without it.
/// </summary>
[Collection("ChildProcess")]
public sealed class CappedChildProcessRunnerFileSizeCeilingTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("arcanum-fsize-ceiling-").FullName;

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    public static TheoryData<string> LimitersThatCannotApplyIt() => new() { "another limiter", "no limiter" };

    [SkippableTheory]
    [MemberData(nameof(LimitersThatCannotApplyIt))]
    public async Task A_file_size_ceiling_no_limiter_here_can_apply_refuses_the_run_before_it_starts(string limiter)
    {
        Skip.If(OperatingSystem.IsWindows(), "The child is a POSIX shell.");

        string ran = Path.Combine(_directory, "ran");

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            new ProcessStartInfo
            {
                FileName = "/bin/sh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-c", ": > \"$1\"", "sh", ran },
            },
            ChildProcessEnvironmentProfile.ToolExec,
            65_536,
            TimeSpan.FromSeconds(30),
            new ResourceLimits { MaxCpuSeconds = 30 },
            limiter == "no limiter" ? null : new PassThroughLimiter(),
            CancellationToken.None,
            maxFileSizeBytes: 1024 * 1024);

        Assert.Equal(CappedChildProcessOutcome.ResourceLimitApplyFailed, result.Outcome);

        Assert.False(File.Exists(ran), "The child ran without its file-size ceiling.");
    }

    private sealed class PassThroughLimiter : IProcessResourceLimiter
    {
        public ProcessResourceLimiterResult Apply(ProcessStartInfo startInfo, ResourceLimits limits) => new(null, null);
    }
}
