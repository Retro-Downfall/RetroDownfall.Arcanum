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
/// either the frame's verdict or one of four fixed reasons, and nothing the child, the runtime or the OS said reaches one.
/// </summary>
public sealed class ProcessDocumentExtractionWorkerMappingTests
{
    private const string HostileStderr = "Unhandled exception. System.Exception: /Users/someone/secret.pdf at UglyToad.PdfPig";

    private static readonly string[] Reasons =
    [
        ProcessDocumentExtractionWorker.FailedReason,
        ProcessDocumentExtractionWorker.DeadlineReason,
        ProcessDocumentExtractionWorker.ResourceLimitsReason,
        ProcessDocumentExtractionWorker.UnavailableReason,
    ];

    public static TheoryData<string, string> OutcomeReasons() => new()
    {
        { nameof(CappedChildProcessOutcome.TimedOut), ProcessDocumentExtractionWorker.DeadlineReason },
        { nameof(CappedChildProcessOutcome.ResourceLimitExceeded), ProcessDocumentExtractionWorker.ResourceLimitsReason },
        { nameof(CappedChildProcessOutcome.MemoryMonitorStopped), ProcessDocumentExtractionWorker.ResourceLimitsReason },
        { nameof(CappedChildProcessOutcome.FilesystemSandboxUnavailable), ProcessDocumentExtractionWorker.UnavailableReason },
        { nameof(CappedChildProcessOutcome.FilesystemSandboxDeniedByWindowsSanctum), ProcessDocumentExtractionWorker.UnavailableReason },
        { nameof(CappedChildProcessOutcome.ResourceLimitApplyFailed), ProcessDocumentExtractionWorker.UnavailableReason },
        { nameof(CappedChildProcessOutcome.FailedToStart), ProcessDocumentExtractionWorker.UnavailableReason },
        { nameof(CappedChildProcessOutcome.IoErrorOnStart), ProcessDocumentExtractionWorker.UnavailableReason },
        { nameof(CappedChildProcessOutcome.AccessDeniedOnStart), ProcessDocumentExtractionWorker.UnavailableReason },
        { nameof(CappedChildProcessOutcome.PreStartValidationFailed), ProcessDocumentExtractionWorker.UnavailableReason },
        { nameof(CappedChildProcessOutcome.IoErrorReadingOutput), ProcessDocumentExtractionWorker.FailedReason },
        { nameof(CappedChildProcessOutcome.AccessDeniedReadingOutput), ProcessDocumentExtractionWorker.FailedReason },
        { nameof(CappedChildProcessOutcome.OutputPreservationFailed), ProcessDocumentExtractionWorker.FailedReason },
        { nameof(CappedChildProcessOutcome.Canceled), ProcessDocumentExtractionWorker.FailedReason },
        { nameof(CappedChildProcessOutcome.CanceledBeforeStart), ProcessDocumentExtractionWorker.FailedReason },
        { nameof(CappedChildProcessOutcome.CanceledWhileReadingOutput), ProcessDocumentExtractionWorker.FailedReason },
    };

    [Theory]
    [MemberData(nameof(OutcomeReasons))]
    public void Map_RunThatDidNotComplete_IsItsCodeOwnedReason(string outcomeName, string reason)
    {
        // Even a run that carries a perfectly good frame is not believed unless it completed.
        DocumentWorkerResult result = ProcessDocumentExtractionWorker.Map(Run(
            Enum.Parse<CappedChildProcessOutcome>(outcomeName),
            exitCode: 0,
            stdout: ValidFrame()));

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
        DocumentWorkerResult result = ProcessDocumentExtractionWorker.Map(Run(CappedChildProcessOutcome.Completed, 0, ValidFrame()));

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
        DocumentWorkerResult result = ProcessDocumentExtractionWorker.Map(Run(
            CappedChildProcessOutcome.Completed,
            0,
            $"ARCANUM-COMPREHENSION/1 {status}\n\nEND 0 0\n"));

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
        AssertFailed(ProcessDocumentExtractionWorker.FailedReason, Run(CappedChildProcessOutcome.Completed, 0, stdout));
    }

    [Fact]
    public void Map_CompletedWithAValidFrameButTruncatedOutput_Failed()
    {
        // The runner cut stdout at its cap; whatever survived is not the whole frame, even if it happens to parse.
        AssertFailed(ProcessDocumentExtractionWorker.FailedReason, Run(CappedChildProcessOutcome.Completed, 0, ValidFrame(), truncated: true));
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
        AssertFailed(ProcessDocumentExtractionWorker.FailedReason, Run(CappedChildProcessOutcome.Completed, exitCode, ValidFrame()));
    }

    [Fact]
    public void Map_CompletedWithTheWorkersDeadlineExit_ExceededItsDeadline()
    {
        AssertFailed(
            ProcessDocumentExtractionWorker.DeadlineReason,
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

        Assert.Contains(ProcessDocumentExtractionWorker.Map(run).FailureReason, Reasons);
    }

    [Fact]
    public void CreateStartInfo_RelaunchesTheExecutableInWorkerModeWithUtf8PipesAndOnlyTheLaunchEnvironment()
    {
        using TemporaryExecutable executable = new();

        DocumentWorkerLaunch launch = DocumentWorkerLaunch.ForTests(executable.Path, new Dictionary<string, string> { ["DOTNET_ROOT"] = "/runtime" });

        ProcessStartInfo startInfo = ProcessDocumentExtractionWorker.CreateStartInfo(launch);

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

        ProcessStartInfo startInfo = ProcessDocumentExtractionWorker.CreateStartInfo(DocumentWorkerLaunch.ForHostExecutable(executable.Path, nativeAot: true)!);

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
    public void CreateSandbox_ForATestLaunch_AddsOnlyItsOwnRoots()
    {
        using TemporaryExecutable executable = new();

        ChildProcessSandboxRequest sandbox = ProcessDocumentExtractionWorker.CreateSandbox(
            DocumentWorkerLaunch.ForTests(executable.Path, additionalReadExecuteRoots: ["/runtime"]));

        Assert.Equal([Path.GetDirectoryName(Path.GetFullPath(executable.Path))!, "/runtime"], sandbox.ReadExecuteRoots);
    }

    [Fact]
    public void Limits_AreTheCodeOwnedProcessBounds()
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
            ProcessDocumentExtractionWorker.Limits);

        Assert.Equal(TimeSpan.FromSeconds(10), ProcessDocumentExtractionWorker.WallClock);

        Assert.Equal(2L * 1024 * 1024, ProcessDocumentExtractionWorker.TotalOutputCapBytes);

        // The worker gives up on its own before the host has to kill it.
        Assert.True(ComprehensionWorkerEntry.SelfDeadline < ProcessDocumentExtractionWorker.WallClock);

        // Each stream gets half the cap, and stdout must hold the largest frame the output ceiling allows.
        Assert.True(
            ProcessDocumentExtractionWorker.TotalOutputCapBytes / 2
            >= ComprehensionWorkerFrame.MaximumFrameBytes(PdfExtractionLimits.Default.MaxOutputCharacters));
    }

    [Fact]
    public void LimitsFor_TheHostBinary_IsExactlyTheCodeOwnedLimits()
    {
        using TemporaryExecutable executable = new();

        Assert.Same(ProcessDocumentExtractionWorker.Limits, ProcessDocumentExtractionWorker.LimitsFor(DocumentWorkerLaunch.ForHostExecutable(executable.Path, nativeAot: true)!));

        Assert.Equal(
            ProcessDocumentExtractionWorker.Limits with { MaxFileDescriptors = 256 },
            ProcessDocumentExtractionWorker.LimitsFor(DocumentWorkerLaunch.ForTests(executable.Path, maxFileDescriptors: 256)));
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

        Assert.Equal(ProcessDocumentExtractionWorker.UnavailableReason, result.FailureReason);
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
            ProcessDocumentExtractionWorker.UnavailableReason,
            (await new ProcessDocumentExtractionWorker(new ProcessResourceLimiter()).RunAsync(DocumentWorkerFormat.Pdf, bytes, CancellationToken.None)).FailureReason);
    }

    private static void AssertFailed(string reason, CappedChildProcessRunResult run)
    {
        DocumentWorkerResult result = ProcessDocumentExtractionWorker.Map(run);

        Assert.Null(result.Outcome);

        Assert.Equal(reason, result.FailureReason);
    }

    private static string ValidFrame() =>
        Encoding.UTF8.GetString(ComprehensionWorkerFrame.Encode(
            new PdfExtractionOutcome(PdfExtractionKind.Extracted, ["PAGE ONE", string.Empty, "PAGE THREE é\U0001F600"], Truncated: true)));

    private static CappedChildProcessRunResult Run(
        CappedChildProcessOutcome outcome,
        int exitCode,
        string stdout,
        bool truncated = false) => new()
        {
            Outcome = outcome,
            ExitCode = exitCode,
            Stdout = new CappedStreamOutput(stdout, truncated),
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
