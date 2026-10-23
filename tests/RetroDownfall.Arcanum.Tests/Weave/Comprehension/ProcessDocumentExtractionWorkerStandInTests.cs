using System.Diagnostics;

using System.Net;

using System.Net.Sockets;

using System.Security.Cryptography;

using System.Text;

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

    public static TheoryData<string, byte[]> FramesThatAreNotStrictUtf8() => new()
    {
        // The frame in UTF-16LE behind its byte-order mark: a text reader would detect the mark and decode it as UTF-16.
        { "utf-16le-with-bom", [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("ARCANUM-COMPREHENSION/1 extracted\nHELLO\nEND 5 1\n")] },

        // One byte that is not UTF-8 inside a page: a replacing decoder would turn it into U+FFFD and accept the frame.
        { "invalid-utf-8", [.. "ARCANUM-COMPREHENSION/1 extracted\nA"u8, 0xFF, .. "B\nEND 3 1\n"u8] },

        // A UTF-8 byte-order mark before the header: a text reader would strip it and accept the frame.
        { "utf-8-bom", [0xEF, 0xBB, 0xBF, .. "ARCANUM-COMPREHENSION/1 extracted\nHELLO\nEND 5 1\n"u8] },
    };

    [SkippableTheory]
    [MemberData(nameof(FramesThatAreNotStrictUtf8))]
    public async Task A_frame_that_is_not_strict_utf8_without_a_preamble_failed(string name, byte[] frame)
    {
        // The frame grammar is checked against the bytes the child wrote, never against a text decoding of them.
        Assert.False(ComprehensionWorkerFrame.Decode(frame, PdfExtractionLimits.Default.MaxOutputCharacters).IsValid, name);

        string written = Path.Combine(_directory, name + ".frame");

        File.WriteAllBytes(written, frame);

        DocumentWorkerResult result = await RunAsync($"exec /bin/cat '{written}'\n");

        Assert.Null(result.Outcome);

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
    public async Task Under_the_host_launch_the_child_cannot_start_a_second_process()
    {
        // The production launch of the host binary, with this stand-in in its place: the worker is one process. The
        // stand-in execs perl, which tries one fork and reports what happened in the frame; the launch chain itself only
        // execs, so the stand-in starts at all.
        DocumentWorkerResult result = await RunUnderTheHostLaunchAsync(
            "exec /usr/bin/perl -e 'my $r; my $pid = fork(); if (!defined $pid) { $r = \"REFUSED\" } elsif ($pid == 0) { exit 0 } "
            + "else { waitpid($pid, 0); $r = \"FORKED\" } print \"ARCANUM-COMPREHENSION/1 extracted\\n$r\\nEND \", length($r), \" 1\\n\"'\n");

        Assert.Null(result.FailureReason);

        Assert.Equal("REFUSED", Assert.Single(result.Outcome!.Pages));
    }

    [SkippableFact]
    public async Task Under_the_host_launch_a_file_cannot_grow_past_the_worker_ceiling()
    {
        // Perl ignores SIGXFSZ so the refused write comes back as an error instead of ending it, then reports how much of
        // the 8 MiB it tried to write reached the file in its private temp directory.
        DocumentWorkerResult result = await RunUnderTheHostLaunchAsync(
            "exec /usr/bin/perl -e '$SIG{XFSZ} = \"IGNORE\"; open(my $f, \">\", \"$ENV{TMPDIR}/fill\") or die; my $chunk = \"\\0\" x 65536; "
            + "my $w = 0; for (1 .. 128) { my $n = syswrite($f, $chunk); last unless defined $n && $n > 0; $w += $n } close $f; "
            + "my $r = \"WROTE=$w\"; print \"ARCANUM-COMPREHENSION/1 extracted\\n$r\\nEND \", length($r), \" 1\\n\"'\n");

        Assert.Null(result.FailureReason);

        long written = long.Parse(Assert.Single(result.Outcome!.Pages)["WROTE=".Length..], System.Globalization.CultureInfo.InvariantCulture);

        Assert.InRange(written, 0, ProcessDocumentExtractionWorker.MaxFileSizeBytes);
    }

    [SkippableFact]
    public async Task Under_the_host_launch_a_child_killed_for_writing_past_the_ceiling_exceeded_its_resource_limits()
    {
        DocumentWorkerResult result = await RunUnderTheHostLaunchAsync("exec /bin/dd if=/dev/zero of=\"$TMPDIR/fill\" bs=1048576 count=8\n");

        Assert.Equal(ProcessDocumentExtractionWorker.ResourceLimitsReason, result.FailureReason);
    }

    [SkippableFact]
    public async Task Under_the_host_launch_the_child_can_neither_list_nor_stat_outside_its_roots()
    {
        // Its stdout becomes model context, so a listing would carry the operator's file names, sizes and owners out.
        string outside = Directory.CreateTempSubdirectory("arcanum-worker-outside-").FullName;

        try
        {
            string canary = Path.Combine(outside, "id_ed25519-canary");

            File.WriteAllText(canary, "not for the worker");

            DocumentWorkerResult result = await RunUnderTheHostLaunchAsync(
                "exec /usr/bin/perl -e 'my $l = opendir(my $d, \"/Users\") ? \"LISTED\" : \"REFUSED\"; "
                + $"my $o = opendir(my $e, \"{outside}\") ? \"LISTED\" : \"REFUSED\"; my $s = (stat \"{canary}\") ? \"STATED\" : \"REFUSED\"; "
                + "my $r = \"$l $o $s\"; print \"ARCANUM-COMPREHENSION/1 extracted\\n$r\\nEND \", length($r), \" 1\\n\"'\n");

            Assert.Null(result.FailureReason);

            Assert.Equal("REFUSED REFUSED REFUSED", Assert.Single(result.Outcome!.Pages));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [SkippableFact]
    public async Task A_replaced_executable_is_refused_before_it_starts()
    {
        Skip.IfNot(MacOsSandboxProbe.IsSandboxExecRunnable(), "Needs a macOS host where sandbox-exec can apply a profile.");

        string executable = WriteStandIn("worker", $"/bin/cat > /dev/null\nprintf '{ExtractedFrame}'\n");

        ProcessDocumentExtractionWorker worker = new(new ProcessResourceLimiter());

        using (ProcessDocumentExtractionWorker.UseExecutableForTests(executable, allowProcessFork: true))
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

        // These stand-ins are shell scripts that run their utilities as children, so they may fork; the fork denial the
        // worker's own launch carries is proved under the host launch below.
        using IDisposable scope = ProcessDocumentExtractionWorker.UseExecutableForTests(executable, allowProcessFork: true);

        return await new ProcessDocumentExtractionWorker(new ProcessResourceLimiter())
            .RunAsync(DocumentWorkerFormat.Pdf, document ?? "%PDF-1.7 stand-in"u8.ToArray(), cancellationToken);
    }

    /// <summary>
    /// Runs a stand-in through exactly the launch the host binary gets: the document-worker environment profile, the
    /// code-owned limits with no test relaxation, and the worker's jail. The stand-in must not fork.
    /// </summary>
    private async Task<DocumentWorkerResult> RunUnderTheHostLaunchAsync(string script, byte[]? document = null)
    {
        Skip.IfNot(MacOsSandboxProbe.IsSandboxExecRunnable(), "Needs a macOS host where sandbox-exec can apply a profile.");

        string executable = WriteStandIn("worker-" + Guid.NewGuid().ToString("N"), script);

        using IDisposable scope = ProcessDocumentExtractionWorker.UseHostLaunchForTests(executable);

        return await new ProcessDocumentExtractionWorker(new ProcessResourceLimiter())
            .RunAsync(DocumentWorkerFormat.Pdf, document ?? "%PDF-1.7 stand-in"u8.ToArray(), CancellationToken.None);
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
