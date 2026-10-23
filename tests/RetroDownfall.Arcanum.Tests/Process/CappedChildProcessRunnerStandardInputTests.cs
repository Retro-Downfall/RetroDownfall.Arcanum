using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Infrastructure.Platform;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Process;

/// <summary>
/// Input bytes handed to <see cref="CappedChildProcessRunner.RunAsync"/> reach the child exactly, through
/// every wrapper the runner puts around it, and a child that does not take them is only its own outcome.
/// </summary>
/// <remarks>
/// On macOS the runner never starts the target directly: the process-group supervisor shell backgrounds
/// <c>sandbox-exec</c>, which execs the resource-limit prelude shell, which execs the target. Each hop
/// inherits fd 0, and a backgrounded job without job control would have it replaced by
/// <c>/dev/null</c>, so arrival is proved by a digest the child computes over what it actually read.
/// </remarks>
[Collection("ChildProcess")]
public sealed class CappedChildProcessRunnerStandardInputTests : IAsyncLifetime
{
    private const int OneMebibyte = 1024 * 1024;

    /// <summary>Far beyond any OS pipe buffer, so a writer that waits for the child to read would block.</summary>
    private const int UnreadPayloadBytes = 16 * OneMebibyte;

    /// <summary>A guard against a hang, never waited out unless the behaviour under test regressed.</summary>
    private static readonly TimeSpan ChildCompletionLimit = TimeSpan.FromSeconds(60);

    private const string MultiByteText =
        "Grüße aus Köln — naïve café, 漢字かな交じり文, Ελληνικά, עברית, 🙂🧪𝄞\r\nsecond line\ttab\n";

    public Task InitializeAsync() => WindowsPowerShellWarmUp.EnsureAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Input_bytes_arrive_exactly_through_the_jailed_and_limited_macOS_chain()
    {
        Skip.IfNot(
            MacOsSandboxProbe.IsSandboxExecRunnable(),
            "Needs a macOS host where sandbox-exec can apply a profile.");

        byte[] payload = CreateEveryByteValuePayload(OneMebibyte);

        ProcessStartInfo psi = CreateStandardInputDigestStartInfo();

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.ToolExec,
            totalOutputCapBytes: 65_536,
            ChildCompletionLimit,
            WorkerShapedLimits(),
            new ProcessResourceLimiter(),
            CancellationToken.None,
            WorkerShapedSandbox("/usr/bin"),
            NullLogger.Instance,
            standardInput: payload);

        AssertWholeMacOsChainWrapped(psi, "/usr/bin/shasum");

        Assert.True(result.Outcome == CappedChildProcessOutcome.Completed, Describe(result));

        Assert.True(result.ExitCode == 0, Describe(result));

        Assert.Equal(ExpectedDigest(payload), ParseDigest(result.Stdout.Text));
    }

    [Fact]
    public async Task Input_bytes_arrive_exactly_without_a_jail_or_limits()
    {
        byte[] payload = CreateEveryByteValuePayload(OneMebibyte);

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            CreateStandardInputDigestStartInfo(),
            ChildProcessEnvironmentProfile.ToolExec,
            totalOutputCapBytes: 65_536,
            ChildCompletionLimit,
            resourceLimits: null,
            resourceLimiter: null,
            CancellationToken.None,
            standardInput: payload);

        Assert.True(result.Outcome == CappedChildProcessOutcome.Completed, Describe(result));

        Assert.True(result.ExitCode == 0, Describe(result));

        Assert.Equal(ExpectedDigest(payload), ParseDigest(result.Stdout.Text));
    }

    [Fact]
    public async Task Empty_input_reaches_the_child_as_an_immediate_end_of_stream()
    {
        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            CreateStandardInputDigestStartInfo(),
            ChildProcessEnvironmentProfile.ToolExec,
            totalOutputCapBytes: 65_536,
            ChildCompletionLimit,
            resourceLimits: null,
            resourceLimiter: null,
            CancellationToken.None,
            standardInput: ReadOnlyMemory<byte>.Empty);

        Assert.True(result.Outcome == CappedChildProcessOutcome.Completed, Describe(result));

        Assert.Equal(ExpectedDigest([]), ParseDigest(result.Stdout.Text));
    }

    [Fact]
    public async Task An_input_encoding_with_a_preamble_does_not_prefix_the_bytes()
    {
        // The bytes are the input, not text: a byte-order mark from whatever StandardInputEncoding the
        // caller left on the start would arrive ahead of them and change what the child parses.
        byte[] payload = CreateEveryByteValuePayload(64 * 1024);

        ProcessStartInfo psi = CreateStandardInputDigestStartInfo();

        psi.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.ToolExec,
            totalOutputCapBytes: 65_536,
            ChildCompletionLimit,
            resourceLimits: null,
            resourceLimiter: null,
            CancellationToken.None,
            standardInput: payload);

        Assert.True(result.Outcome == CappedChildProcessOutcome.Completed, Describe(result));

        Assert.Equal(ExpectedDigest(payload), ParseDigest(result.Stdout.Text));
    }

    [Fact]
    public async Task Without_input_bytes_the_start_keeps_the_callers_input_encoding()
    {
        // The preamble-free override belongs to the input-bytes path alone; every model-directed tool
        // passes no bytes and must reach Process.Start exactly as before.
        ProcessStartInfo psi = CreateStandardInputDigestStartInfo();

        Encoding callers = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

        psi.StandardInputEncoding = callers;

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.ToolExec,
            totalOutputCapBytes: 65_536,
            ChildCompletionLimit,
            resourceLimits: null,
            resourceLimiter: null,
            CancellationToken.None);

        Assert.True(result.Outcome == CappedChildProcessOutcome.Completed, Describe(result));

        Assert.Same(callers, psi.StandardInputEncoding);
    }

    [SkippableFact]
    public async Task A_jailed_child_that_exits_without_reading_its_input_completes_with_its_own_result()
    {
        Skip.IfNot(
            MacOsSandboxProbe.IsSandboxExecRunnable(),
            "Needs a macOS host where sandbox-exec can apply a profile.");

        ProcessStartInfo psi = new()
        {
            FileName = "/usr/bin/true",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        Stopwatch stopwatch = Stopwatch.StartNew();

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.ToolExec,
            totalOutputCapBytes: 65_536,
            ChildCompletionLimit,
            WorkerShapedLimits(),
            new ProcessResourceLimiter(),
            CancellationToken.None,
            WorkerShapedSandbox("/usr/bin"),
            NullLogger.Instance,
            standardInput: new byte[UnreadPayloadBytes]);

        stopwatch.Stop();

        AssertWholeMacOsChainWrapped(psi, "/usr/bin/true");

        Assert.True(result.Outcome == CappedChildProcessOutcome.Completed, Describe(result));

        Assert.True(result.ExitCode == 0, Describe(result));

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(20),
            $"A child that never read its input took {stopwatch.Elapsed} to complete.");
    }

    [Fact]
    public async Task A_child_that_exits_without_reading_its_input_completes_with_its_own_result()
    {
        ProcessStartInfo psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                ArgumentList = { "/c", "exit 3" },
            }
            : new ProcessStartInfo
            {
                FileName = "/bin/sh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-c", "exit 3" },
            };

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.ToolExec,
            totalOutputCapBytes: 65_536,
            ChildCompletionLimit,
            resourceLimits: null,
            resourceLimiter: null,
            CancellationToken.None,
            standardInput: new byte[UnreadPayloadBytes]);

        Assert.True(result.Outcome == CappedChildProcessOutcome.Completed, Describe(result));

        Assert.True(result.ExitCode == 3, Describe(result));
    }

    [SkippableFact]
    public async Task A_child_that_closes_its_input_early_keeps_running_and_completes()
    {
        Skip.If(OperatingSystem.IsWindows(), "Closing fd 0 from a POSIX shell is the probe here.");

        ProcessStartInfo psi = new()
        {
            FileName = "/bin/sh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-c", "head -c 10 >/dev/null; exec 0<&-; sleep 1; echo closed-early" },
        };

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.ToolExec,
            totalOutputCapBytes: 65_536,
            ChildCompletionLimit,
            resourceLimits: null,
            resourceLimiter: null,
            CancellationToken.None,
            standardInput: new byte[UnreadPayloadBytes]);

        Assert.True(result.Outcome == CappedChildProcessOutcome.Completed, Describe(result));

        Assert.True(result.ExitCode == 0, Describe(result));

        Assert.Equal("closed-early", result.Stdout.Text.Trim());
    }

    [Fact]
    public async Task The_deadline_ends_a_run_whose_child_never_reads_its_input()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            CreateNonReadingSleeperStartInfo(),
            ChildProcessEnvironmentProfile.ToolExec,
            totalOutputCapBytes: 65_536,
            TimeSpan.FromSeconds(2),
            resourceLimits: null,
            resourceLimiter: null,
            CancellationToken.None,
            standardInput: new byte[UnreadPayloadBytes]);

        stopwatch.Stop();

        Assert.True(result.Outcome == CappedChildProcessOutcome.TimedOut, Describe(result));

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(20),
            $"The 2 s deadline ended the run only after {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task Cancellation_ends_a_run_whose_child_never_reads_its_input()
    {
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(2));

        Stopwatch stopwatch = Stopwatch.StartNew();

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            CreateNonReadingSleeperStartInfo(),
            ChildProcessEnvironmentProfile.ToolExec,
            totalOutputCapBytes: 65_536,
            Timeout.InfiniteTimeSpan,
            resourceLimits: null,
            resourceLimiter: null,
            cancellation.Token,
            standardInput: new byte[UnreadPayloadBytes]);

        stopwatch.Stop();

        Assert.True(result.Outcome == CappedChildProcessOutcome.Canceled, Describe(result));

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(20),
            $"Cancellation after 2 s ended the run only after {stopwatch.Elapsed}.");
    }

    [SkippableFact]
    public async Task A_descendant_holding_the_input_open_unread_does_not_hold_the_run()
    {
        Skip.IfNot(
            OperatingSystem.IsMacOS() && File.Exists("/usr/bin/ruby"),
            "Needs a POSIX host with fork/setsid available to strand an inherited pipe.");

        // Containment is best-effort (DESIGN §11.15): a descendant that double-forks out of the process
        // group, escapes the supervisor and keeps fd 0 open without reading leaves the input writer blocked
        // on a full pipe for as long as it lives. The run's result must not wait for that writer.
        string pidDirectory = Path.Combine(
            Path.GetTempPath(),
            "arcanum-stdin-holder-test-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(pidDirectory);

        string pidFile = Path.Combine(pidDirectory, "descendant.pid");

        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = "/usr/bin/ruby",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            psi.ArgumentList.Add("-e");

            psi.ArgumentList.Add(
                "require 'timeout'; fork { fork { Process.setsid; STDOUT.reopen('/dev/null'); STDERR.reopen('/dev/null'); File.write('"
                + pidFile
                + "', Process.pid.to_s); sleep 30 }; exit! 0 }; Timeout.timeout(10) { sleep 0.01 until File.size?('"
                + pidFile
                + "') }; exit 0");

            Stopwatch stopwatch = Stopwatch.StartNew();

            Task<CappedChildProcessRunResult> run = CappedChildProcessRunner.RunAsync(
                psi,
                ChildProcessEnvironmentProfile.ToolExec,
                totalOutputCapBytes: 65_536,
                Timeout.InfiniteTimeSpan,
                resourceLimits: null,
                resourceLimiter: null,
                CancellationToken.None,
                descendantSupervisorFactory: static (_, _) => null,
                standardInput: new byte[UnreadPayloadBytes]);

            Task finished = await Task.WhenAny(
                run,
                Task.Delay(TimeSpan.FromSeconds(20)));

            Assert.True(
                ReferenceEquals(finished, run),
                "RunAsync never returned: it waited on an input writer blocked by a descendant that holds fd 0 open.");

            CappedChildProcessRunResult result = await run;

            stopwatch.Stop();

            Assert.True(result.Outcome == CappedChildProcessOutcome.Completed, Describe(result));

            Assert.True(result.ExitCode == 0, Describe(result));
        }
        finally
        {
            TestDescendantProcess.KillRecorded(pidFile);

            Directory.Delete(pidDirectory, recursive: true);
        }
    }

    [SkippableFact]
    public async Task Input_is_not_delivered_past_the_end_of_the_run()
    {
        Skip.IfNot(
            OperatingSystem.IsMacOS() && File.Exists("/usr/bin/ruby"),
            "Needs a POSIX host with fork/setsid available to strand an inherited pipe.");

        // A descendant that escaped containment and keeps reading fd 0 must not go on receiving the input
        // once the run is over: the writer stops at its next chunk, closes the pipe, and the escapee sees
        // end of input having read a small part. It reads 64 KiB every 50 ms, so taking the whole payload
        // would need about thirteen seconds.
        string pidDirectory = Path.Combine(
            Path.GetTempPath(),
            "arcanum-stdin-reader-test-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(pidDirectory);

        string pidFile = Path.Combine(pidDirectory, "descendant.pid");

        string countFile = Path.Combine(pidDirectory, "read.count");

        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = "/usr/bin/ruby",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            psi.ArgumentList.Add("-e");

            psi.ArgumentList.Add(
                "require 'timeout'; fork { fork { Process.setsid; STDOUT.reopen('/dev/null'); STDERR.reopen('/dev/null'); File.write('"
                + pidFile
                + "', Process.pid.to_s); n = 0; while (chunk = STDIN.read(65536)); n += chunk.bytesize; sleep 0.05; end; File.write('"
                + countFile
                + "', n.to_s) }; exit! 0 }; Timeout.timeout(10) { sleep 0.01 until File.size?('"
                + pidFile
                + "') }; exit 0");

            CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
                psi,
                ChildProcessEnvironmentProfile.ToolExec,
                totalOutputCapBytes: 65_536,
                Timeout.InfiniteTimeSpan,
                resourceLimits: null,
                resourceLimiter: null,
                CancellationToken.None,
                descendantSupervisorFactory: static (_, _) => null,
                standardInput: new byte[UnreadPayloadBytes]);

            Assert.True(result.Outcome == CappedChildProcessOutcome.Completed, Describe(result));

            Stopwatch waited = Stopwatch.StartNew();

            while (!File.Exists(countFile) && waited.Elapsed < TimeSpan.FromSeconds(30))
            {
                await Task.Delay(50);
            }

            Assert.True(File.Exists(countFile), "The escaped reader never saw the end of its input.");

            long delivered = long.Parse(
                await File.ReadAllTextAsync(countFile),
                System.Globalization.CultureInfo.InvariantCulture);

            Assert.True(
                delivered < UnreadPayloadBytes / 4,
                $"The escaped reader received {delivered} of {UnreadPayloadBytes} bytes after the run had ended.");
        }
        finally
        {
            TestDescendantProcess.KillRecorded(pidFile);

            Directory.Delete(pidDirectory, recursive: true);
        }
    }

    [SkippableFact]
    public async Task Utf8_output_survives_the_jailed_and_limited_macOS_chain()
    {
        Skip.IfNot(
            MacOsSandboxProbe.IsSandboxExecRunnable(),
            "Needs a macOS host where sandbox-exec can apply a profile.");

        string text = string.Concat(Enumerable.Repeat(MultiByteText, 2_000));

        ProcessStartInfo psi = CreateStandardInputEchoStartInfo();

        psi.StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        Encoding requested = psi.StandardOutputEncoding;

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.ToolExec,
            totalOutputCapBytes: 4 * OneMebibyte,
            ChildCompletionLimit,
            WorkerShapedLimits(),
            new ProcessResourceLimiter(),
            CancellationToken.None,
            WorkerShapedSandbox("/bin"),
            NullLogger.Instance,
            standardInput: Encoding.UTF8.GetBytes(text));

        AssertWholeMacOsChainWrapped(psi, "/bin/cat");

        Assert.Same(requested, psi.StandardOutputEncoding);

        Assert.True(result.Outcome == CappedChildProcessOutcome.Completed, Describe(result));

        Assert.False(result.Stdout.Truncated);

        Assert.Equal(text, result.Stdout.Text);
    }

    [Fact]
    public async Task Output_is_decoded_with_the_encoding_the_caller_chose()
    {
        // UTF-8 is the platform default on macOS, so a UTF-8 round trip alone cannot tell a preserved
        // choice from a default. Decoding the same bytes as Latin-1 can: the runner must read with the
        // caller's encoding (Windows otherwise decodes with the console code page).
        byte[] bytes = Encoding.UTF8.GetBytes(MultiByteText);

        CappedChildProcessRunResult utf8 = await EchoAsync(bytes, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        CappedChildProcessRunResult latin1 = await EchoAsync(bytes, Encoding.Latin1);

        Assert.True(utf8.Outcome == CappedChildProcessOutcome.Completed, Describe(utf8));

        Assert.Equal(MultiByteText, utf8.Stdout.Text);

        Assert.True(latin1.Outcome == CappedChildProcessOutcome.Completed, Describe(latin1));

        Assert.Equal(Encoding.Latin1.GetString(bytes), latin1.Stdout.Text);
    }

    private static async Task<CappedChildProcessRunResult> EchoAsync(
        byte[] input,
        Encoding outputEncoding)
    {
        ProcessStartInfo psi = CreateStandardInputEchoStartInfo();

        psi.StandardOutputEncoding = outputEncoding;

        return await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.ToolExec,
            totalOutputCapBytes: OneMebibyte,
            ChildCompletionLimit,
            resourceLimits: null,
            resourceLimiter: null,
            CancellationToken.None,
            standardInput: input);
    }

    /// <summary>
    /// The limits the PDF worker runs under: code-owned, with the memory ceiling enforced by the macOS
    /// footprint monitor and CPU and descriptors by the prelude.
    /// </summary>
    private static ResourceLimits WorkerShapedLimits() => new(
        MaxProcessMemoryMb: 768,
        MaxProcessCount: 1,
        MaxFileWriteMb: 0,
        ProcessTimeoutSeconds: 10,
        MaxCpuSeconds: 15,
        MaxMemoryMb: 768,
        MaxFileDescriptors: 64);

    private static ChildProcessSandboxRequest WorkerShapedSandbox(params string[] readExecuteRoots) => new()
    {
        ReadWriteRoots = [],
        ReadExecuteRoots = readExecuteRoots,
        RequireAppliedFilesystemJail = true,
        ToolName = "stdin-probe",
    };

    /// <summary>
    /// Pins that the run really went through supervisor shell, sandbox-exec and the limit prelude: the
    /// runner rewrites the start in place, so a chain that was silently skipped shows here.
    /// </summary>
    private static void AssertWholeMacOsChainWrapped(
        ProcessStartInfo psi,
        string target)
    {
        List<string> argv = [.. psi.ArgumentList];

        Assert.Equal("/bin/sh", psi.FileName);

        Assert.Equal("arcanum-process-group", argv[2]);

        Assert.Equal("/usr/bin/sandbox-exec", argv[3]);

        Assert.Equal("-f", argv[4]);

        Assert.Equal("/bin/sh", argv[6]);

        Assert.Contains("ulimit -t 15", argv[8], StringComparison.Ordinal);

        Assert.Contains("ulimit -n 64", argv[8], StringComparison.Ordinal);

        Assert.Equal(target, argv[10]);
    }

    private static byte[] CreateEveryByteValuePayload(int length)
    {
        byte[] payload = new byte[length];

        new Random(163).NextBytes(payload);

        for (int value = 0; value < 256; value++)
        {
            payload[value] = (byte)value;
        }

        // CR LF pairs, a lone LF and CR, a NUL run, Ctrl-Z and Ctrl-D: every byte a text-mode hop could
        // translate or treat as end of input, at the start, the middle and the very end.
        byte[] markers = [0x0D, 0x0A, 0x00, 0x00, 0x0A, 0x0D, 0x1A, 0x04];

        markers.CopyTo(payload, 256);

        markers.CopyTo(payload, length / 2);

        markers.CopyTo(payload, length - markers.Length);

        Assert.Equal(256, payload.Distinct().Count());

        return payload;
    }

    private static string ExpectedDigest(byte[] payload) =>
        Convert.ToHexStringLower(SHA256.HashData(payload));

    private static string ParseDigest(string output)
    {
        string token = output.Trim().Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?? string.Empty;

        Assert.Matches("^[0-9a-f]{64}$", token);

        return token;
    }

    private static string Describe(CappedChildProcessRunResult result) =>
        $"Outcome {result.Outcome}; exit {result.ExitCode}; stdout: {result.Stdout.Text}; stderr: {result.Stderr.Text}; "
        + $"sandbox: {result.FilesystemSandboxDenialMessage}; limits: {result.ResourceLimitApplyError}; fault: {result.FaultException}";

    private static ProcessStartInfo CreateStandardInputDigestStartInfo()
    {
        if (OperatingSystem.IsWindows())
        {
            return new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                ArgumentList =
                {
                    "-NoProfile",
                    "-Command",
                    "$s = [Console]::OpenStandardInput(); "
                    + "$h = [System.Security.Cryptography.SHA256]::Create().ComputeHash($s); "
                    + "[Console]::Out.Write(([BitConverter]::ToString($h) -replace '-', '').ToLowerInvariant())",
                },
            };
        }

        return new ProcessStartInfo
        {
            FileName = "/usr/bin/shasum",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-a", "256" },
        };
    }

    private static ProcessStartInfo CreateStandardInputEchoStartInfo()
    {
        if (OperatingSystem.IsWindows())
        {
            return new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                ArgumentList =
                {
                    "-NoProfile",
                    "-Command",
                    "$i = [Console]::OpenStandardInput(); $o = [Console]::OpenStandardOutput(); $i.CopyTo($o); $o.Flush()",
                },
            };
        }

        return new ProcessStartInfo
        {
            FileName = "/bin/cat",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
    }

    private static ProcessStartInfo CreateNonReadingSleeperStartInfo()
    {
        if (OperatingSystem.IsWindows())
        {
            return new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                ArgumentList = { "-NoProfile", "-Command", "Start-Sleep -Seconds 60" },
            };
        }

        return new ProcessStartInfo
        {
            FileName = "/bin/sleep",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "60" },
        };
    }
}
