using System.Diagnostics;

using System.Text;

using RetroDownfall.Arcanum.Core.Sanctum;

using RetroDownfall.Arcanum.Infrastructure.Platform;

using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// How the process worker reads its runner's result, and the code-owned launch it hands the runner: every outcome becomes
/// either the frame's verdict or one of the four fixed reasons of the worker kind's profile, and nothing the child, the
/// runtime or the OS said reaches one. Every worker kind has a profile and runs only under the jail.
/// </summary>
public sealed class ProcessDocumentExtractionWorkerMappingTests
{
    private const string HostileStderr = "Unhandled exception. System.Exception: /Users/someone/secret.pdf at UglyToad.PdfPig";

    private static readonly string[] Reasons =
    [
        DocumentWorkerProfile.Pdf.FailedReason,
        DocumentWorkerProfile.Pdf.DeadlineReason,
        DocumentWorkerProfile.Pdf.ResourceLimitsReason,
        DocumentWorkerProfile.Pdf.UnavailableReason,
    ];

    public static TheoryData<string, string> OutcomeReasons() => new()
    {
        { nameof(CappedChildProcessOutcome.TimedOut), DocumentWorkerProfile.Pdf.DeadlineReason },
        { nameof(CappedChildProcessOutcome.ResourceLimitExceeded), DocumentWorkerProfile.Pdf.ResourceLimitsReason },
        { nameof(CappedChildProcessOutcome.MemoryMonitorStopped), DocumentWorkerProfile.Pdf.ResourceLimitsReason },
        { nameof(CappedChildProcessOutcome.FilesystemSandboxUnavailable), DocumentWorkerProfile.Pdf.UnavailableReason },
        { nameof(CappedChildProcessOutcome.FilesystemSandboxDeniedByWindowsSanctum), DocumentWorkerProfile.Pdf.UnavailableReason },
        { nameof(CappedChildProcessOutcome.ResourceLimitApplyFailed), DocumentWorkerProfile.Pdf.UnavailableReason },
        { nameof(CappedChildProcessOutcome.FailedToStart), DocumentWorkerProfile.Pdf.UnavailableReason },
        { nameof(CappedChildProcessOutcome.IoErrorOnStart), DocumentWorkerProfile.Pdf.UnavailableReason },
        { nameof(CappedChildProcessOutcome.AccessDeniedOnStart), DocumentWorkerProfile.Pdf.UnavailableReason },
        { nameof(CappedChildProcessOutcome.PreStartValidationFailed), DocumentWorkerProfile.Pdf.UnavailableReason },
        { nameof(CappedChildProcessOutcome.IoErrorReadingOutput), DocumentWorkerProfile.Pdf.FailedReason },
        { nameof(CappedChildProcessOutcome.AccessDeniedReadingOutput), DocumentWorkerProfile.Pdf.FailedReason },
        { nameof(CappedChildProcessOutcome.OutputPreservationFailed), DocumentWorkerProfile.Pdf.FailedReason },
        { nameof(CappedChildProcessOutcome.Canceled), DocumentWorkerProfile.Pdf.FailedReason },
        { nameof(CappedChildProcessOutcome.CanceledBeforeStart), DocumentWorkerProfile.Pdf.FailedReason },
        { nameof(CappedChildProcessOutcome.CanceledWhileReadingOutput), DocumentWorkerProfile.Pdf.FailedReason },
    };

    [Theory]
    [MemberData(nameof(OutcomeReasons))]
    public void Map_RunThatDidNotComplete_IsItsCodeOwnedReason(string outcomeName, string reason)
    {
        // Even a run that carries a perfectly good frame is not believed unless it completed.
        DocumentWorkerResult result = ProcessDocumentExtractionWorker.Map(
            Run(Enum.Parse<CappedChildProcessOutcome>(outcomeName), exitCode: 0, stdout: ValidFrame()),
            DocumentWorkerProfile.Pdf);

        Assert.Null(result.Outcome);

        Assert.Equal(reason, result.FailureReason);
    }

    [Fact]
    public void Map_EveryRunnerOutcome_HasARowAbove()
    {
        // A runner outcome added later must be placed deliberately, not fall into whatever the default happens to say.
        string[] mapped = [.. OutcomeReasons().Select(static row => (string)row[0]), nameof(CappedChildProcessOutcome.Completed)];

        Assert.Equal(Enum.GetNames<CappedChildProcessOutcome>().Order(StringComparer.Ordinal), mapped.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Map_CompletedWithExitZeroAndAValidFrame_IsTheFramesVerdict()
    {
        DocumentWorkerResult result = ProcessDocumentExtractionWorker.Map(Run(CappedChildProcessOutcome.Completed, 0, ValidFrame()), DocumentWorkerProfile.Pdf);

        Assert.Null(result.FailureReason);

        PdfExtractionOutcome outcome = Assert.IsType<PdfExtractionOutcome>(result.Outcome);

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.Equal(["PAGE ONE", string.Empty, "PAGE THREE é\U0001F600"], outcome.Pages);

        Assert.True(outcome.Truncated);
    }

    [Theory]
    [InlineData("password-required")]
    [InlineData("malformed")]
    [InlineData("bounds")]
    [InlineData("no-text")]
    public void Map_CompletedWithAVerdictFrame_IsThatVerdict(string status)
    {
        DocumentWorkerResult result = ProcessDocumentExtractionWorker.Map(
            Run(CappedChildProcessOutcome.Completed, 0, $"ARCANUM-COMPREHENSION/1 {status}\n\nEND 0 0\n"),
            DocumentWorkerProfile.Pdf);

        Assert.NotNull(result.Outcome);

        Assert.Empty(result.Outcome.Pages);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a frame")]
    [InlineData("ARCANUM-COMPREHENSION/1 extracted\nPAGE ONE")]
    [InlineData("ARCANUM-COMPREHENSION/1 extracted\nPAGE ONE\nEND 9 1\n")]
    [InlineData("ARCANUM-COMPREHENSION/2 extracted\nPAGE ONE\nEND 8 1\n")]
    public void Map_CompletedWithExitZeroButNoValidFrame_Failed(string stdout)
    {
        AssertFailed(DocumentWorkerProfile.Pdf.FailedReason, Run(CappedChildProcessOutcome.Completed, 0, stdout));
    }

    public static TheoryData<string, byte[]> BytesThatAreNotStrictUtf8() => new()
    {
        { "utf-16le-with-bom", [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("ARCANUM-COMPREHENSION/1 extracted\nHELLO\nEND 5 1\n")] },
        { "invalid-utf-8", [.. "ARCANUM-COMPREHENSION/1 extracted\nA"u8, 0xFF, .. "B\nEND 3 1\n"u8] },
        { "utf-8-bom", [0xEF, 0xBB, 0xBF, .. "ARCANUM-COMPREHENSION/1 extracted\nHELLO\nEND 5 1\n"u8] },
    };

    [Theory]
    [MemberData(nameof(BytesThatAreNotStrictUtf8))]
    public void Map_CompletedWithAFrameThatIsNotStrictUtf8WithoutAPreamble_Failed(string name, byte[] stdout)
    {
        _ = name;

        AssertFailed(DocumentWorkerProfile.Pdf.FailedReason, Run(CappedChildProcessOutcome.Completed, 0, stdout));
    }

    [Fact]
    public void Map_CompletedWithAValidFrameAsTextButNoRawBytes_Failed()
    {
        // Only the bytes the worker wrote are judged; a text decoding of them, however valid, is not a frame.
        CappedChildProcessRunResult run = new()
        {
            Outcome = CappedChildProcessOutcome.Completed,
            ExitCode = 0,
            Stdout = new CappedStreamOutput(ValidFrame(), false),
            Stderr = new CappedStreamOutput(string.Empty, false),
        };

        AssertFailed(DocumentWorkerProfile.Pdf.FailedReason, run);
    }

    [Fact]
    public void Map_CompletedWithAValidFrameButTruncatedOutput_Failed()
    {
        // The runner cut stdout at its cap; whatever survived is not the whole frame, even if it happens to parse.
        AssertFailed(DocumentWorkerProfile.Pdf.FailedReason, Run(CappedChildProcessOutcome.Completed, 0, ValidFrame(), truncated: true));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(126)]
    [InlineData(134)]
    [InlineData(137)]
    [InlineData(139)]
    [InlineData(-1)]
    public void Map_CompletedWithAnotherExitCode_FailedEvenWithAValidFrame(int exitCode)
    {
        AssertFailed(DocumentWorkerProfile.Pdf.FailedReason, Run(CappedChildProcessOutcome.Completed, exitCode, ValidFrame()));
    }

    [Theory]
    [InlineData(128 + 25)]
    [InlineData(-25)]
    public void Map_CompletedByTheFileSizeSignal_ExceededItsResourceLimits(int exitCode)
    {
        // SIGXFSZ: the worker wrote a file past its size ceiling and the kernel ended it, reported through the launch
        // chain's shell (128 + 25) or directly (-25). Whatever it printed first is not a verdict.
        AssertFailed(DocumentWorkerProfile.Pdf.ResourceLimitsReason, Run(CappedChildProcessOutcome.Completed, exitCode, ValidFrame()));
    }

    [Fact]
    public void Map_CompletedWithTheWorkersDeadlineExit_ExceededItsDeadline()
    {
        AssertFailed(
            DocumentWorkerProfile.Pdf.DeadlineReason,
            Run(CappedChildProcessOutcome.Completed, (int)ComprehensionWorkerExitCode.Deadline, string.Empty));
    }

    [Theory]
    [MemberData(nameof(OutcomeReasons))]
    public void Map_NeverCarriesWhatTheChildOrTheRunnerSaid(string outcomeName, string reason)
    {
        _ = reason;

        CappedChildProcessRunResult run = new()
        {
            Outcome = Enum.Parse<CappedChildProcessOutcome>(outcomeName),
            ExitCode = 1,
            Stdout = new CappedStreamOutput(HostileStderr, false),
            Stderr = new CappedStreamOutput(HostileStderr, false),
            FaultException = new InvalidOperationException(HostileStderr),
            ResourceLimitApplyError = HostileStderr,
            FilesystemSandboxDenialMessage = HostileStderr,
            PreStartValidationError = HostileStderr,
        };

        Assert.Contains(ProcessDocumentExtractionWorker.Map(run, DocumentWorkerProfile.Pdf).FailureReason, Reasons);
    }

    [Fact]
    public void CreateStartInfo_RelaunchesTheExecutableInWorkerModeWithUtf8PipesAndOnlyTheLaunchEnvironment()
    {
        using TemporaryExecutable executable = new();

        DocumentWorkerLaunch launch = DocumentWorkerLaunch.ForTests(executable.Path, new Dictionary<string, string> { ["DOTNET_ROOT"] = "/runtime" });

        ProcessStartInfo startInfo = ProcessDocumentExtractionWorker.CreateStartInfo(launch, DocumentWorkerProfile.Pdf);

        Assert.Equal(launch.Executable, startInfo.FileName);

        Assert.Equal([ComprehensionWorkerEntry.ModeArgument, ComprehensionWorkerEntry.PdfFormatArgument], startInfo.ArgumentList);

        Assert.False(startInfo.UseShellExecute);

        Assert.True(startInfo.RedirectStandardOutput);

        Assert.True(startInfo.RedirectStandardError);

        foreach (Encoding? encoding in new[] { startInfo.StandardOutputEncoding, startInfo.StandardErrorEncoding })
        {
            Assert.NotNull(encoding);

            Assert.Equal("utf-8", encoding.WebName);

            Assert.Empty(encoding.GetPreamble());
        }

        Assert.Equal(["DOTNET_ROOT"], startInfo.Environment.Keys);

        Assert.Equal("/runtime", startInfo.Environment["DOTNET_ROOT"]);
    }

    [Fact]
    public void CreateStartInfo_ForTheHostBinary_InheritsNothing()
    {
        using TemporaryExecutable executable = new();

        ProcessStartInfo startInfo = ProcessDocumentExtractionWorker.CreateStartInfo(
            DocumentWorkerLaunch.ForHostExecutable(executable.Path, nativeAot: true)!,
            DocumentWorkerProfile.Pdf);

        Assert.Empty(startInfo.Environment);
    }

    [Fact]
    public void CreateSandbox_RequiresAnAppliedJailWithNoNetworkNoWritesAndOnlyTheExecutablesDirectory()
    {
        using TemporaryExecutable executable = new();

        ChildProcessSandboxRequest sandbox = ProcessDocumentExtractionWorker.CreateSandbox(DocumentWorkerLaunch.ForHostExecutable(executable.Path, nativeAot: true)!);

        Assert.True(sandbox.RequireAppliedFilesystemJail);

        Assert.True(sandbox.DenyNetwork);

        Assert.False(sandbox.AllowUnsandboxed);

        Assert.Empty(sandbox.ReadWriteRoots);

        Assert.Empty(sandbox.ReadOnlyRoots);

        Assert.Equal([Path.GetDirectoryName(Path.GetFullPath(executable.Path))!], sandbox.ReadExecuteRoots);
    }

    [Fact]
    public void CreateSandbox_DeniesForkingForTheHostBinaryAndForATestLaunchUnlessItAllowsIt()
    {
        using TemporaryExecutable executable = new();

        Assert.True(ProcessDocumentExtractionWorker.CreateSandbox(DocumentWorkerLaunch.ForHostExecutable(executable.Path, nativeAot: true)!).DenyProcessFork);

        Assert.True(ProcessDocumentExtractionWorker.CreateSandbox(DocumentWorkerLaunch.ForTests(executable.Path)).DenyProcessFork);

        // Only a shell stand-in, which runs its utilities as children, opts out; the fork denial has its own tests.
        Assert.False(ProcessDocumentExtractionWorker.CreateSandbox(DocumentWorkerLaunch.ForTests(executable.Path, allowProcessFork: true)).DenyProcessFork);
    }

    [Fact]
    public void CreateSandbox_ConfinesMetadataToTheRootsForEveryLaunch()
    {
        using TemporaryExecutable executable = new();

        Assert.True(ProcessDocumentExtractionWorker.CreateSandbox(DocumentWorkerLaunch.ForHostExecutable(executable.Path, nativeAot: true)!).ConfineMetadataToRoots);

        Assert.True(ProcessDocumentExtractionWorker.CreateSandbox(DocumentWorkerLaunch.ForTests(executable.Path, allowProcessFork: true)).ConfineMetadataToRoots);
    }

    [Fact]
    public void CreateSandbox_ConfinesProcessInformationForEveryLaunch()
    {
        // A worker's frame reaches model context, so no launch may read another process's arguments, environment or path,
        // or list the processes running.
        using TemporaryExecutable executable = new();

        Assert.True(ProcessDocumentExtractionWorker.CreateSandbox(DocumentWorkerLaunch.ForHostExecutable(executable.Path, nativeAot: true)!).ConfineProcessInformation);

        Assert.True(ProcessDocumentExtractionWorker.CreateSandbox(DocumentWorkerLaunch.ForTests(executable.Path)).ConfineProcessInformation);

        Assert.True(ProcessDocumentExtractionWorker.CreateSandbox(DocumentWorkerLaunch.ForTests(executable.Path, allowProcessFork: true)).ConfineProcessInformation);
    }

    [Fact]
    public void CreateSandbox_DeniesFileWritesForEveryLaunch()
    {
        // The worker writes no file, and its file-size ceiling does not bound how many it could create, so no launch gets
        // anywhere to write, not even its private temp directory.
        using TemporaryExecutable executable = new();

        Assert.True(ProcessDocumentExtractionWorker.CreateSandbox(DocumentWorkerLaunch.ForHostExecutable(executable.Path, nativeAot: true)!).DenyFileWrites);

        Assert.True(ProcessDocumentExtractionWorker.CreateSandbox(DocumentWorkerLaunch.ForTests(executable.Path)).DenyFileWrites);

        Assert.True(ProcessDocumentExtractionWorker.CreateSandbox(DocumentWorkerLaunch.ForTests(executable.Path, allowProcessFork: true)).DenyFileWrites);
    }

    [Fact]
    public void CreateSandbox_ForATestLaunch_AddsOnlyItsOwnRoots()
    {
        using TemporaryExecutable executable = new();

        ChildProcessSandboxRequest sandbox = ProcessDocumentExtractionWorker.CreateSandbox(
            DocumentWorkerLaunch.ForTests(executable.Path, additionalReadExecuteRoots: ["/runtime"]));

        Assert.Equal([Path.GetDirectoryName(Path.GetFullPath(executable.Path))!, "/runtime"], sandbox.ReadExecuteRoots);
    }

    [Fact]
    public void PdfProfile_HoldsTheCodeOwnedProcessBounds()
    {
        Assert.Equal(
            new ResourceLimits(
                MaxProcessMemoryMb: 768,
                MaxProcessCount: 1,
                MaxFileWriteMb: 0,
                ProcessTimeoutSeconds: 10,
                MaxCpuSeconds: 15,
                MaxMemoryMb: 768,
                MaxFileDescriptors: 64),
            DocumentWorkerProfile.Pdf.Limits);

        Assert.Equal(TimeSpan.FromSeconds(10), DocumentWorkerProfile.Pdf.WallClock);

        // The worker writes no file, so the one it could write is small: a compromised worker cannot fill the disk.
        Assert.Equal(1024L * 1024, DocumentWorkerProfile.Pdf.MaxFileSizeBytes);

        Assert.Equal(2L * 1024 * 1024, DocumentWorkerProfile.Pdf.TotalOutputCapBytes);

        // The worker gives up on its own before the host has to kill it.
        Assert.True(DocumentWorkerProfile.Pdf.SelfDeadline < DocumentWorkerProfile.Pdf.WallClock);

        Assert.Equal(ComprehensionWorkerEntry.SelfDeadline, DocumentWorkerProfile.Pdf.SelfDeadline);

        // The host refuses a document the worker would refuse at stdin, and decodes a frame against the extractor's own ceiling.
        Assert.Equal(ComprehensionWorkerEntry.InputCeilingBytes, DocumentWorkerProfile.Pdf.InputCeilingBytes);

        Assert.Equal(PdfExtractionLimits.Default.MaxOutputCharacters, DocumentWorkerProfile.Pdf.MaxOutputCharacters);

        // Each stream gets half the cap, and stdout must hold the largest frame the output ceiling allows.
        Assert.True(
            DocumentWorkerProfile.Pdf.TotalOutputCapBytes / 2
            >= ComprehensionWorkerFrame.MaximumFrameBytes(DocumentWorkerProfile.Pdf.MaxOutputCharacters));
    }

    [Fact]
    public void LimitsFor_TheHostBinary_IsExactlyItsProfilesLimits()
    {
        using TemporaryExecutable executable = new();

        Assert.Same(
            DocumentWorkerProfile.Pdf.Limits,
            ProcessDocumentExtractionWorker.LimitsFor(DocumentWorkerLaunch.ForHostExecutable(executable.Path, nativeAot: true)!, DocumentWorkerProfile.Pdf));

        Assert.Equal(
            DocumentWorkerProfile.Pdf.Limits with { MaxFileDescriptors = 256 },
            ProcessDocumentExtractionWorker.LimitsFor(DocumentWorkerLaunch.ForTests(executable.Path, maxFileDescriptors: 256), DocumentWorkerProfile.Pdf));
    }

    public static TheoryData<string> WorkerFormats()
    {
        TheoryData<string> formats = new();

        foreach (string format in Enum.GetNames<DocumentWorkerFormat>())
        {
            formats.Add(format);
        }

        return formats;
    }

    [Theory]
    [MemberData(nameof(WorkerFormats))]
    public void EveryWorkerFormat_HasAProfileASelfDeadlineUnderItsWallClockAndAJailedLaunch(string formatName)
    {
        // A worker kind added later cannot run without a profile, a deadline of its own that ends it before the host's wall
        // clock has to, and the jail: every confinement the jail can ask for, applied or nothing runs.
        DocumentWorkerFormat format = Enum.Parse<DocumentWorkerFormat>(formatName);

        DocumentWorkerProfile profile = DocumentWorkerProfile.For(format);

        Assert.Equal(format, profile.Format);

        Assert.InRange(profile.SelfDeadline, TimeSpan.FromTicks(1), profile.WallClock - TimeSpan.FromTicks(1));

        using TemporaryExecutable executable = new();

        DocumentWorkerRunRequest request = ProcessDocumentExtractionWorker.CreateRunRequest(
            DocumentWorkerLaunch.ForHostExecutable(executable.Path, nativeAot: true)!,
            profile);

        Assert.False(request.Sandbox.AllowUnsandboxed);

        Assert.True(request.Sandbox.RequireAppliedFilesystemJail);

        Assert.True(request.Sandbox.DenyNetwork);

        Assert.True(request.Sandbox.DenyProcessFork);

        Assert.True(request.Sandbox.ConfineMetadataToRoots);

        Assert.True(request.Sandbox.DenyFileWrites);

        Assert.True(request.Sandbox.ConfineProcessInformation);

        Assert.Empty(request.Sandbox.ReadWriteRoots);

        // The run is the profile's: its argument, its clock, its limits and its output bounds.
        Assert.Equal([ComprehensionWorkerEntry.ModeArgument, profile.FormatArgument], request.StartInfo.ArgumentList);

        Assert.Equal(profile.WallClock, request.WallClock);

        Assert.Same(profile.Limits, request.Limits);

        Assert.Equal(profile.TotalOutputCapBytes, request.TotalOutputCapBytes);

        Assert.Equal(profile.MaxFileSizeBytes, request.MaxFileSizeBytes);
    }

    [Fact]
    public void For_AFormatWithoutAProfile_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DocumentWorkerProfile.For((DocumentWorkerFormat)int.MaxValue));
    }

    [Fact]
    public async Task RunAsync_AFormatWithoutAProfile_IsRefusedWithoutStartingAnything()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new ProcessDocumentExtractionWorker(new ProcessResourceLimiter())
            .RunAsync((DocumentWorkerFormat)int.MaxValue, "%PDF-1.7"u8.ToArray(), CancellationToken.None));
    }

    [Fact]
    public void Reasons_AreTheFourCodeOwnedSentences()
    {
        Assert.Equal(
            [
                "PDF extraction failed.",
                "PDF extraction exceeded its deadline.",
                "PDF extraction exceeded its resource limits.",
                "PDF extraction is unavailable on this host.",
            ],
            Reasons);
    }

    [Fact]
    public async Task RunAsync_InAHostThatCannotRelaunchItself_IsUnavailableWithoutStartingAnything()
    {
        // The test platform is not a worker-capable host, and nothing here overrides the executable.
        DocumentWorkerResult result = await new ProcessDocumentExtractionWorker(new ProcessResourceLimiter())
            .RunAsync(DocumentWorkerFormat.Pdf, "%PDF-1.7"u8.ToArray(), CancellationToken.None);

        Assert.Null(result.Outcome);

        Assert.Equal(DocumentWorkerProfile.Pdf.UnavailableReason, result.FailureReason);
    }

    [Fact]
    public async Task RunAsync_CancelledBeforeItStarts_ThrowsWithoutStartingAnything()
    {
        using CancellationTokenSource cancelled = new();

        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessDocumentExtractionWorker(new ProcessResourceLimiter())
            .RunAsync(DocumentWorkerFormat.Pdf, "%PDF-1.7"u8.ToArray(), cancelled.Token));
    }

    [Fact]
    public async Task RunAsync_WithASubstitute_HandsItTheFormatTheBytesAndTheToken()
    {
        RecordingWorker substitute = new();

        using CancellationTokenSource lifetime = new();

        byte[] bytes = "%PDF-1.7"u8.ToArray();

        DocumentWorkerResult result;

        using (ProcessDocumentExtractionWorker.UseSubstituteForTests(substitute))
        {
            result = await new ProcessDocumentExtractionWorker(new ProcessResourceLimiter())
                .RunAsync(DocumentWorkerFormat.Pdf, bytes, lifetime.Token);
        }

        Assert.Same(substitute.Result, result);

        Assert.Equal(bytes, substitute.Bytes);

        Assert.Equal(lifetime.Token, substitute.Token);

        // The substitute is scoped: once disposed, this host is back to having no worker.
        Assert.Equal(
            DocumentWorkerProfile.Pdf.UnavailableReason,
            (await new ProcessDocumentExtractionWorker(new ProcessResourceLimiter()).RunAsync(DocumentWorkerFormat.Pdf, bytes, CancellationToken.None)).FailureReason);
    }

    private static void AssertFailed(string reason, CappedChildProcessRunResult run)
    {
        DocumentWorkerResult result = ProcessDocumentExtractionWorker.Map(run, DocumentWorkerProfile.Pdf);

        Assert.Null(result.Outcome);

        Assert.Equal(reason, result.FailureReason);
    }

    private static string ValidFrame() =>
        Encoding.UTF8.GetString(ComprehensionWorkerFrame.Encode(
            new PdfExtractionOutcome(PdfExtractionKind.Extracted, ["PAGE ONE", string.Empty, "PAGE THREE é\U0001F600"], Truncated: true)));

    /// <summary>A run whose stdout is the raw bytes the worker wrote, as the runner hands them back for this caller.</summary>
    private static CappedChildProcessRunResult Run(
        CappedChildProcessOutcome outcome,
        int exitCode,
        string stdout,
        bool truncated = false) => Run(outcome, exitCode, Encoding.UTF8.GetBytes(stdout), truncated);

    private static CappedChildProcessRunResult Run(
        CappedChildProcessOutcome outcome,
        int exitCode,
        byte[] stdout,
        bool truncated = false) => new()
        {
            Outcome = outcome,
            ExitCode = exitCode,
            Stdout = new CappedStreamOutput(string.Empty, truncated, Bytes: stdout),
            Stderr = new CappedStreamOutput(HostileStderr, false),
        };

    private sealed class RecordingWorker : IDocumentExtractionWorker
    {
        public DocumentWorkerResult Result { get; } = DocumentWorkerResult.Completed(PdfExtractionOutcome.Without(PdfExtractionKind.NoText));

        public byte[]? Bytes { get; private set; }

        public CancellationToken Token { get; private set; }

        public Task<DocumentWorkerResult> RunAsync(DocumentWorkerFormat format, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            Assert.Equal(DocumentWorkerFormat.Pdf, format);

            Bytes = bytes.ToArray();

            Token = cancellationToken;

            return Task.FromResult(Result);
        }
    }

    /// <summary>An executable file in a directory of its own, deleted with it.</summary>
    private sealed class TemporaryExecutable : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("arcanum-worker-map-").FullName;

        public TemporaryExecutable()
        {
            Path = System.IO.Path.Combine(_directory, "arcanum");

            File.WriteAllText(Path, "#!/bin/sh\nexit 0\n");
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
