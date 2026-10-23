using System.Diagnostics;

using System.Net;

using System.Net.Sockets;

using System.Security.Cryptography;

using RetroDownfall.Arcanum.Infrastructure.Platform;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

using RetroDownfall.Arcanum.Tests.Process;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// The process worker against POSIX stand-in children, through the real capped runner, the real jail and the real
/// limits: each stand-in misbehaves in one way, and the worker must turn that into its code-owned result. The real PDF
/// worker is covered by <see cref="ProcessDocumentExtractionWorkerRealWorkerTests"/>.
/// </summary>
[Collection("ChildProcess")]
public sealed class ProcessDocumentExtractionWorkerStandInTests : IDisposable
{
    private const string ExtractedFrame = "ARCANUM-COMPREHENSION/1 extracted\\nHELLO\\fWORLD\\nEND 11 2\\n";

    private readonly string _directory = Directory.CreateTempSubdirectory("arcanum-worker-standin-").FullName;

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public async Task A_child_that_writes_a_valid_frame_and_exits_zero_is_its_verdict()
    {
        DocumentWorkerResult result = await RunAsync($"/bin/cat > /dev/null\nprintf '{ExtractedFrame}'\n");

        Assert.Null(result.FailureReason);

        Assert.Equal(["HELLO", "WORLD"], result.Outcome!.Pages);
    }

    [SkippableFact]
    public async Task The_child_receives_exactly_the_document_bytes_on_stdin()
    {
        byte[] document = new byte[300_000];

        new Random(163).NextBytes(document);

        // The stand-in answers with the digest of what it read, as the text of its one page.
        DocumentWorkerResult result = await RunAsync(
            "digest=$(/sbin/md5 -q)\nprintf 'ARCANUM-COMPREHENSION/1 extracted\\n%s\\nEND 32 1\\n' \"$digest\"\n",
            document);

        Assert.Null(result.FailureReason);

        Assert.Equal(Convert.ToHexStringLower(MD5.HashData(document)), Assert.Single(result.Outcome!.Pages));
    }

    [SkippableFact]
    public async Task A_document_over_the_intake_ceiling_is_refused_without_starting_the_child()
    {
        // The stand-in would answer with a good frame, so a Failed result here can only be the refusal.
        DocumentWorkerResult result = await RunAsync(
            $"/bin/cat > /dev/null\nprintf '{ExtractedFrame}'\n",
            new byte[ComprehensionWorkerEntry.InputCeilingBytes + 1]);

        Assert.Equal(ProcessDocumentExtractionWorker.FailedReason, result.FailureReason);
    }

    [SkippableFact]
    public async Task A_child_that_exits_non_zero_failed_even_after_a_valid_frame()
    {
        DocumentWorkerResult result = await RunAsync($"/bin/cat > /dev/null\nprintf '{ExtractedFrame}'\nexit 3\n");

        Assert.Equal(ProcessDocumentExtractionWorker.FailedReason, result.FailureReason);
    }

    [SkippableFact]
    public async Task A_child_that_reports_its_own_deadline_exceeded_its_deadline()
    {
        DocumentWorkerResult result = await RunAsync("/bin/cat > /dev/null\nexit 5\n");

        Assert.Equal(ProcessDocumentExtractionWorker.DeadlineReason, result.FailureReason);
    }

    [SkippableFact]
    public async Task A_child_that_writes_garbage_failed()
    {
        DocumentWorkerResult result = await RunAsync("/bin/cat > /dev/null\nprintf 'Unhandled exception at /Users/someone/secret.pdf'\n");

        Assert.Equal(ProcessDocumentExtractionWorker.FailedReason, result.FailureReason);
    }

    [SkippableFact]
    public async Task A_child_that_stops_before_its_trailer_failed()
    {
        DocumentWorkerResult result = await RunAsync("/bin/cat > /dev/null\nprintf 'ARCANUM-COMPREHENSION/1 extracted\\nHELLO'\n");

        Assert.Equal(ProcessDocumentExtractionWorker.FailedReason, result.FailureReason);
    }

    [SkippableFact]
    public async Task A_child_whose_output_overflows_its_cap_failed()
    {
        DocumentWorkerResult result = await RunAsync(
            $"/bin/cat > /dev/null\n/usr/bin/perl -e 'print \"x\" x 3000000'\nprintf '{ExtractedFrame}'\n");

        Assert.Equal(ProcessDocumentExtractionWorker.FailedReason, result.FailureReason);
    }

    [SkippableFact]
    public async Task A_child_that_outlives_the_wall_clock_is_killed_and_exceeded_its_deadline()
    {
        Stopwatch elapsed = Stopwatch.StartNew();

        DocumentWorkerResult result = await RunAsync("/bin/sleep 60\n");

        elapsed.Stop();

        Assert.Equal(ProcessDocumentExtractionWorker.DeadlineReason, result.FailureReason);

        Assert.InRange(elapsed.Elapsed, ProcessDocumentExtractionWorker.WallClock, ProcessDocumentExtractionWorker.WallClock + TimeSpan.FromSeconds(15));
    }

    [SkippableFact]
    public async Task A_child_that_outgrows_the_memory_limit_is_killed_and_exceeded_its_resource_limits()
    {
        DocumentWorkerResult result = await RunAsync(
            "/bin/cat > /dev/null\n/usr/bin/perl -e '$x = \"a\" x (1200 * 1024 * 1024); sleep 30'\n");

        Assert.Equal(ProcessDocumentExtractionWorker.ResourceLimitsReason, result.FailureReason);
    }

    [SkippableFact]
    public async Task The_caller_cancelling_kills_the_child_and_throws()
    {
        using CancellationTokenSource caller = new(TimeSpan.FromMilliseconds(500));

        Stopwatch elapsed = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync("/bin/sleep 60\n", cancellationToken: caller.Token));

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(9), $"Cancellation took {elapsed.Elapsed}.");
    }

    [SkippableFact]
    public async Task The_child_has_no_network()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);

        listener.Start();

        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        DocumentWorkerResult result = await RunAsync(
            "/bin/cat > /dev/null\n"
            + "/usr/bin/perl -MIO::Socket::INET -e 'exit(IO::Socket::INET->new(PeerAddr => \"127.0.0.1\", PeerPort => " + port + ", Proto => \"tcp\", Timeout => 5) ? 7 : 0)' || exit 7\n"
            + $"printf '{ExtractedFrame}'\n");

        Assert.Null(result.FailureReason);

        Assert.False(listener.Pending(), "A connection reached the listener from the document worker.");
    }

    [SkippableFact]
    public async Task The_child_cannot_read_outside_its_roots_or_write_beside_its_executable()
    {
        string outside = Directory.CreateTempSubdirectory("arcanum-worker-outside-").FullName;

        try
        {
            string secret = Path.Combine(outside, "secret.txt");

            File.WriteAllText(secret, "not for the worker");

            DocumentWorkerResult result = await RunAsync(
                "/bin/cat > /dev/null\n"
                + $"if /bin/cat '{secret}' > /dev/null 2>&1; then exit 7; fi\n"
                + $"if ( : > '{Path.Combine(_directory, "written")}' ) 2>/dev/null; then exit 8; fi\n"
                + $"printf '{ExtractedFrame}'\n");

            Assert.Null(result.FailureReason);

            Assert.False(File.Exists(Path.Combine(_directory, "written")));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [SkippableFact]
    public async Task The_child_inherits_no_host_environment()
    {
        // The stand-in answers with the names of the variables it sees.
        DocumentWorkerResult result = await RunAsync(
            "/bin/cat > /dev/null\n"
            + "names=$(/usr/bin/env | /usr/bin/cut -d= -f1 | /usr/bin/sort | /usr/bin/tr '\\n' ' ')\n"
            + "printf 'ARCANUM-COMPREHENSION/1 extracted\\n%s\\nEND %d 1\\n' \"$names\" \"${#names}\"\n");

        Assert.Null(result.FailureReason);

        string[] names = Assert.Single(result.Outcome!.Pages).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        Assert.Subset(new HashSet<string>(StringComparer.Ordinal) { "TMPDIR", "TMP", "TEMP", "PWD", "SHLVL", "_" }, names.ToHashSet(StringComparer.Ordinal));

        Assert.Contains("TMPDIR", names);
    }

    [SkippableFact]
    public async Task A_replaced_executable_is_refused_before_it_starts()
    {
        Skip.IfNot(MacOsSandboxProbe.IsSandboxExecRunnable(), "Needs a macOS host where sandbox-exec can apply a profile.");

        string executable = WriteStandIn("worker", $"/bin/cat > /dev/null\nprintf '{ExtractedFrame}'\n");

        ProcessDocumentExtractionWorker worker = new(new ProcessResourceLimiter());

        using (ProcessDocumentExtractionWorker.UseExecutableForTests(executable))
        {
            Assert.Null((await worker.RunAsync(DocumentWorkerFormat.Pdf, "%PDF"u8.ToArray(), CancellationToken.None)).FailureReason);

            File.Move(WriteStandIn("worker.new", "exit 0\n"), executable, overwrite: true);

            DocumentWorkerResult replaced = await worker.RunAsync(DocumentWorkerFormat.Pdf, "%PDF"u8.ToArray(), CancellationToken.None);

            Assert.Equal(ProcessDocumentExtractionWorker.UnavailableReason, replaced.FailureReason);
        }
    }

    private async Task<DocumentWorkerResult> RunAsync(
        string script,
        byte[]? document = null,
        CancellationToken cancellationToken = default)
    {
        Skip.IfNot(MacOsSandboxProbe.IsSandboxExecRunnable(), "Needs a macOS host where sandbox-exec can apply a profile.");

        string executable = WriteStandIn("worker-" + Guid.NewGuid().ToString("N"), script);

        using IDisposable scope = ProcessDocumentExtractionWorker.UseExecutableForTests(executable);

        return await new ProcessDocumentExtractionWorker(new ProcessResourceLimiter())
            .RunAsync(DocumentWorkerFormat.Pdf, document ?? "%PDF-1.7 stand-in"u8.ToArray(), cancellationToken);
    }

    private string WriteStandIn(string fileName, string script)
    {
        string path = Path.Combine(_directory, fileName);

        File.WriteAllText(path, "#!/bin/sh\n" + script);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }
}
