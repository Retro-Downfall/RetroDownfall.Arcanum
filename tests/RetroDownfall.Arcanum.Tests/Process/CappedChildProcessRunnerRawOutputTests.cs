using System.Diagnostics;

using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

namespace RetroDownfall.Arcanum.Tests.Process;

/// <summary>
/// Raw standard output: the runner hands back exactly the bytes the child wrote, bounded by the same per-stream cap,
/// with nothing detected, stripped or replaced, while every caller that does not ask for it keeps its text output.
/// </summary>
[Collection("ChildProcess")]
public sealed class CappedChildProcessRunnerRawOutputTests
{
    /// <summary>A UTF-16LE byte-order mark, an invalid UTF-8 byte, a NUL, then a UTF-8 byte-order mark.</summary>
    private static readonly byte[] Written = [0xFF, 0xFE, 0x41, 0x00, 0xFF, 0x0A, 0xEF, 0xBB, 0xBF, 0x42];

    private const string WriteScript = "printf '\\377\\376A\\000\\377\\n\\357\\273\\277B'";

    [SkippableFact]
    public async Task Raw_standard_output_is_exactly_the_bytes_the_child_wrote()
    {
        Skip.If(OperatingSystem.IsWindows(), "The child is a POSIX shell.");

        CappedChildProcessRunResult result = await RunAsync(WriteScript, rawStandardOutput: true);

        Assert.Equal(CappedChildProcessOutcome.Completed, result.Outcome);

        Assert.Equal(Written, result.Stdout.Bytes);

        Assert.Empty(result.Stdout.Text);

        Assert.False(result.Stdout.Truncated);

        Assert.Equal(Written.Length, result.Stdout.TotalBytes);
    }

    [SkippableFact]
    public async Task Raw_standard_output_past_its_cap_keeps_exactly_the_cap_and_is_truncated()
    {
        Skip.If(OperatingSystem.IsWindows(), "The child is a POSIX shell.");

        // A 2 KiB total cap is 1 KiB a stream; the child writes three times that and is still drained to its end.
        CappedChildProcessRunResult result = await RunAsync("/usr/bin/head -c 3072 /dev/zero", rawStandardOutput: true, totalOutputCapBytes: 2048);

        Assert.Equal(CappedChildProcessOutcome.Completed, result.Outcome);

        Assert.Equal(0, result.ExitCode);

        Assert.True(result.Stdout.Truncated);

        Assert.Equal(new byte[1024], result.Stdout.Bytes);

        Assert.Equal(3072, result.Stdout.TotalBytes);
    }

    [SkippableFact]
    public async Task Standard_output_stays_text_unless_raw_output_is_asked_for()
    {
        Skip.If(OperatingSystem.IsWindows(), "The child is a POSIX shell.");

        CappedChildProcessRunResult result = await RunAsync(WriteScript, rawStandardOutput: false);

        Assert.Equal(CappedChildProcessOutcome.Completed, result.Outcome);

        Assert.Null(result.Stdout.Bytes);

        Assert.NotEmpty(result.Stdout.Text);
    }

    private static Task<CappedChildProcessRunResult> RunAsync(string script, bool rawStandardOutput, long totalOutputCapBytes = 65_536) =>
        CappedChildProcessRunner.RunAsync(
            new ProcessStartInfo
            {
                FileName = "/bin/sh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-c", script },
            },
            ChildProcessEnvironmentProfile.ToolExec,
            totalOutputCapBytes,
            TimeSpan.FromSeconds(30),
            resourceLimits: null,
            resourceLimiter: null,
            CancellationToken.None,
            rawStandardOutput: rawStandardOutput);
}
