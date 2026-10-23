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
    public async Task Under_the_host_launch_the_child_can_write_nowhere_but_dev_null()
    {
        // The stand-in tries one byte at each place a worker could leave a file: its private temp directory, the shared
        // temp directory, the operator's home, its own executable's directory and a writable device. Only /dev/null, which
        // keeps nothing, takes it.
        string name = "arcanum-worker-write-probe-" + Guid.NewGuid().ToString("N");

        string sharedTemp = Path.Combine("/private/tmp", name);

        string home = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "." + name);

        string beside = Path.Combine(_directory, name);

        try
        {
            DocumentWorkerResult result = await RunUnderTheHostLaunchAsync(
                "exec /usr/bin/perl -e 'my @r; "
                + $"for my $p ([\"temp\", \"$ENV{{TMPDIR}}/probe\"], [\"tmp\", \"{sharedTemp}\"], [\"home\", \"{home}\"], [\"exe\", \"{beside}\"], "
                + "[\"zero\", \"/dev/zero\"], [\"null\", \"/dev/null\"]) { my ($k, $f) = @$p; my $w = 0; "
                + "if (open(my $h, \">\", $f)) { $w = syswrite($h, \"x\") // 0; close $h } push @r, \"$k=\" . ($w ? \"WROTE\" : \"REFUSED\") } "
                + "push @r, \"tempdir=\" . (mkdir(\"$ENV{TMPDIR}/dir\") ? \"MADE\" : \"REFUSED\"); "
                + "my $r = join(\" \", @r); print \"ARCANUM-COMPREHENSION/1 extracted\\n$r\\nEND \", length($r), \" 1\\n\"'\n");

            Assert.Null(result.FailureReason);

            Assert.Equal(
                "temp=REFUSED tmp=REFUSED home=REFUSED exe=REFUSED zero=REFUSED null=WROTE tempdir=REFUSED",
                Assert.Single(result.Outcome!.Pages));

            Assert.False(File.Exists(sharedTemp));

            Assert.False(File.Exists(home));

            Assert.False(File.Exists(beside));
        }
        finally
        {
            File.Delete(sharedTemp);

            File.Delete(home);

            File.Delete(beside);
        }
    }

    [SkippableFact]
    public async Task Under_the_host_launch_a_child_writing_many_files_writes_none()
    {
        // The per-file size ceiling does not bound how many files a worker creates. The stand-in ignores SIGXFSZ and tries
        // sixteen files of 1 MiB, the most the ceiling lets each one hold, in its private temp directory.
        DocumentWorkerResult result = await RunUnderTheHostLaunchAsync(
            "exec /usr/bin/perl -e '$SIG{XFSZ} = \"IGNORE\"; my ($n, $b) = (0, 0); my $chunk = \"\\0\" x 65536; "
            + "for my $i (1 .. 16) { open(my $f, \">\", \"$ENV{TMPDIR}/fill-$i\") or next; $n++; "
            + "for (1 .. 16) { my $w = syswrite($f, $chunk); last unless defined $w && $w > 0; $b += $w } close $f } "
            + "my $r = \"FILES=$n BYTES=$b\"; print \"ARCANUM-COMPREHENSION/1 extracted\\n$r\\nEND \", length($r), \" 1\\n\"'\n");

        Assert.Null(result.FailureReason);

        Assert.Equal("FILES=0 BYTES=0", Assert.Single(result.Outcome!.Pages));
    }

    [SkippableFact]
    public async Task Under_the_host_launch_the_file_size_ceiling_still_applies()
    {
        // Nothing is writable, and the kernel's per-file ceiling stays as defence in depth. The stand-in's shell reports
        // the ceiling it runs under with builtins alone, since it may not fork.
        DocumentWorkerResult result = await RunUnderTheHostLaunchAsync(
            "printf 'ARCANUM-COMPREHENSION/1 extracted\\n'\nulimit -f\nprintf 'END 4 1\\n'\n");

        Assert.Null(result.FailureReason);

        Assert.Equal(
            (ProcessDocumentExtractionWorker.MaxFileSizeBytes / 1024).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Assert.Single(result.Outcome!.Pages));
    }

    [SkippableFact]
    public async Task Under_the_host_launch_a_child_ended_by_the_file_size_signal_exceeded_its_resource_limits()
    {
        // A write past the ceiling ends the worker by SIGXFSZ. With nowhere to write, the stand-in raises it itself.
        DocumentWorkerResult result = await RunUnderTheHostLaunchAsync("exec /usr/bin/perl -e 'kill \"XFSZ\", $$; sleep 5'\n");

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
    public async Task Under_the_host_launch_the_child_cannot_recover_a_path_from_an_object_id()
    {
        // The jail refuses a stat, a listing and an open outside its roots, but no file operation mediates fsgetpath and
        // fsgetpath_ext: with the volume's id (its own temp directory's statfs supplies it) and an object id they name any
        // file on the volume, and a sweep of ids would carry every file name out in its frame. The stand-in asks for the
        // path of a canary outside every root by both calls; outside the jail the same program is told it, so the refusal
        // below is the jail's and not a mistake in the program.
        Skip.IfNot(MacOsSandboxProbe.IsSandboxExecRunnable(), "Needs a macOS host where sandbox-exec can apply a profile.");

        string outside = Directory.CreateTempSubdirectory("arcanum-worker-outside-").FullName;

        try
        {
            string canary = Path.Combine(outside, "id_ed25519-canary");

            File.WriteAllText(canary, "not for the worker");

            string program = ObjectIdProbeProgram(await ObjectIdOfAsync(canary));

            string control = (await RunUnjailedAsync("/usr/bin/perl", ["-e", program])).Split('\n')[1];

            Assert.Matches(@"^FSGETPATH=\S+/id_ed25519-canary FSGETPATH_EXT=\S+/id_ed25519-canary$", control);

            DocumentWorkerResult result = await RunUnderTheHostLaunchAsync($"exec /usr/bin/perl -e '{program}'\n");

            Assert.Null(result.FailureReason);

            Assert.Equal("FSGETPATH=REFUSED FSGETPATH_EXT=REFUSED", Assert.Single(result.Outcome!.Pages));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [SkippableFact]
    public async Task Under_the_host_launch_the_child_cannot_read_another_process_or_list_the_processes()
    {
        // The user's other processes carry secrets in their argument vectors and environments: an API key on a command line,
        // or in the environment of a shell or of this host. The kernel hands both to any process of the same user through
        // KERN_PROCARGS2 (the environment too, unless the target is a platform binary), names each process's executable
        // through proc_pidpath, and lists every process through KERN_PROC_ALL and listpids. The stand-in asks each of them
        // about this test process, a .NET host that is not a platform binary, and reads its own arguments as a control inside
        // the jail; outside the jail the same program is told everything, so each refusal below is the jail's.
        Skip.IfNot(MacOsSandboxProbe.IsSandboxExecRunnable(), "Needs a macOS host where sandbox-exec can apply a profile.");

        string program = ProcessInformationProbeProgram(System.Environment.ProcessId);

        string control = (await RunUnjailedAsync("/usr/bin/perl", ["-e", program])).Split('\n')[1];

        Assert.Equal("ARGV=SEEN ENV=SEEN OLDARGS=SEEN PATH=SEEN PID=SEEN ALL=SEEN LISTPIDS=SEEN SELF=SEEN", control);

        DocumentWorkerResult result = await RunUnderTheHostLaunchAsync($"exec /usr/bin/perl -e '{program}'\n");

        Assert.Null(result.FailureReason);

        Assert.Equal(
            "ARGV=REFUSED ENV=REFUSED OLDARGS=REFUSED PATH=REFUSED PID=REFUSED ALL=REFUSED LISTPIDS=REFUSED SELF=SEEN",
            Assert.Single(result.Outcome!.Pages));
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

    /// <summary>
    /// A perl program, free of single quotes so a shell can carry it, that asks fsgetpath and fsgetpath_ext (by number, since
    /// perl has no binding for them) for the path of the object whose id it is given, on the volume of its own temp directory,
    /// and answers as the frame of one page. A volume id it cannot get is reported as such, so a refusal never hides a program
    /// that never reached the call.
    /// </summary>
    private static string ObjectIdProbeProgram(long objectId) =>
        """
        my $id = <ID> + 0;
        my $sfs = "\0" x 2168;
        my $r;
        if (syscall(345, $ENV{TMPDIR}, $sfs) != 0) {
          $r = "STATFS-FAILED";
        } else {
          my $fsid = substr($sfs, 48, 8);
          my @r;
          for my $call ("FSGETPATH", "FSGETPATH_EXT") {
            my $buf = "\0" x 1024;
            my $n = $call eq "FSGETPATH" ? syscall(427, $buf, 1024, $fsid, $id) : syscall(217, $buf, 1024, $fsid, $id, 0);
            my $path = $buf;
            $path =~ s/\0.*//s;
            push @r, "$call=" . ($n > 0 ? $path : "REFUSED");
          }
          $r = join(" ", @r);
        }
        print "ARCANUM-COMPREHENSION/1 extracted\n$r\nEND ", length($r), " 1\n";
        """.Replace("<ID>", objectId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

    /// <summary>
    /// A perl program, free of single quotes so a shell can carry it, that asks the kernel about the process whose id it is
    /// given, through raw sysctl (202) and proc_info (336) calls since perl binds neither, and answers as the frame of one page:
    /// its arguments and environment (KERN_PROCARGS2, 49), its arguments the older way (KERN_PROCARGS, 38), its executable
    /// path (proc_pidpath: PROC_INFO_CALL_PIDINFO, PROC_PIDPATHINFO), its process entry (KERN_PROC_PID), every process's
    /// entry (KERN_PROC_ALL), every process id (proc_listallpids: PROC_INFO_CALL_LISTPIDS, PROC_ALL_PIDS), and, as a control,
    /// its own arguments.
    /// </summary>
    private static string ProcessInformationProbeProgram(int processId) =>
        """
        my $pid = <PID>;
        sub sysctl { my ($size, @mib) = @_; my $buf = "\0" x $size; my $len = pack("Q", $size); return syscall(202, pack("i*", @mib), scalar(@mib), $buf, $len, 0, 0) == 0 ? substr($buf, 0, unpack("Q", $len)) : undef; }
        my @r;
        my $args = sysctl(1048576, 1, 49, $pid);
        if (defined $args) {
          my $argc = unpack("i", $args);
          my @strings = grep { length } split(/\0+/, substr($args, 4));
          push @r, "ARGV=" . ($argc >= 1 && @strings > $argc ? "SEEN" : "EMPTY");
          push @r, "ENV=" . ((grep { /=/ } @strings[($argc + 1) .. $#strings]) ? "SEEN" : "EMPTY");
        } else {
          push @r, "ARGV=REFUSED", "ENV=REFUSED";
        }
        my $old = sysctl(1048576, 1, 38, $pid);
        push @r, "OLDARGS=" . (defined $old ? (length($old) > 0 ? "SEEN" : "EMPTY") : "REFUSED");
        my $path = "\0" x 4096;
        my $n = syscall(336, 2, $pid, 11, 0, $path, 4096);
        $path =~ s/\0.*//s;
        push @r, "PATH=" . ($n >= 0 && length($path) > 0 ? "SEEN" : "REFUSED");
        my $entry = sysctl(648, 1, 14, 1, $pid);
        push @r, "PID=" . (defined $entry && length($entry) == 648 ? "SEEN" : "REFUSED");
        my $all = sysctl(8388608, 1, 14, 0);
        push @r, "ALL=" . (defined $all && length($all) > 648 ? "SEEN" : "REFUSED");
        push @r, "LISTPIDS=" . (syscall(336, 1, 1, 0, 0, 0, 0) > 0 ? "SEEN" : "REFUSED");
        my $self = sysctl(1048576, 1, 49, $$);
        push @r, "SELF=" . (defined $self && unpack("i", $self) >= 1 ? "SEEN" : "REFUSED");
        my $r = join(" ", @r);
        print "ARCANUM-COMPREHENSION/1 extracted\n$r\nEND ", length($r), " 1\n";
        """.Replace("<PID>", processId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static async Task<long> ObjectIdOfAsync(string path) =>
        long.Parse(
            (await RunUnjailedAsync("/usr/bin/stat", ["-f", "%i", path])).Trim(),
            System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Runs a program as the test process itself, outside every jail: the control a refusal under the jail is read against.</summary>
    private static async Task<string> RunUnjailedAsync(string executable, string[] arguments)
    {
        ProcessStartInfo start = new(executable) { RedirectStandardOutput = true, UseShellExecute = false };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["TMPDIR"] = Path.GetTempPath();

        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!;

        string output = await process.StandardOutput.ReadToEndAsync();

        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);

        return output;
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
