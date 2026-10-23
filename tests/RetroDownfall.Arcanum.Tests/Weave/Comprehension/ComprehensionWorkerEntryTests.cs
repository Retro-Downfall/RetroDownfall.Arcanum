using System.Diagnostics;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Tests.Weave.Comprehension.Pdf;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// The worker side of <c>arcanum __comprehension-worker &lt;kind&gt;</c>, run in this process against streams for the PDF
/// kind: what it reads from stdin, the frame it writes to stdout, and the exit code it ends with. That every kind runs by
/// its own profile is pinned for each kind in <see cref="ProcessDocumentExtractionWorkerMappingTests"/>; the real re-launch
/// is covered by the process worker tests.
/// </summary>
public sealed class ComprehensionWorkerEntryTests
{
    private static readonly string[] PdfArguments = [ComprehensionWorkerEntry.ModeArgument, DocumentWorkerProfile.Pdf.FormatArgument];

    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    [Fact]
    public void Run_TextPdfOnStdin_WritesTheFrameOfItsPagesAndExitsZero()
    {
        // Through the arguments, as the host's binary runs it: "pdf" runs the PDF extractor under the PDF profile.
        using MemoryStream input = new(PdfFixtures.Read("multi-page.pdf"), writable: false);

        using MemoryStream output = new();

        ComprehensionWorkerExitCode code = ComprehensionWorkerEntry.Run(PdfArguments, input, output);

        byte[] written = output.ToArray();

        Assert.Equal(ComprehensionWorkerExitCode.FrameWritten, code);

        DocumentExtractionOutcome outcome = Decode(written);

        Assert.Equal(DocumentExtractionKind.Extracted, outcome.Kind);

        Assert.False(outcome.Truncated);

        Assert.Equal(["PAGE-ONE-ALPHA", string.Empty, "PAGE-THREE-MARKER-GAMMA", "PAGE-FOUR-DELTA"], outcome.Pages);
    }

    [Theory]
    [InlineData("encrypted-aes-password.pdf", nameof(DocumentExtractionKind.PasswordRequired))]
    [InlineData("garbage.pdf", nameof(DocumentExtractionKind.Malformed))]
    [InlineData("truncated.pdf", nameof(DocumentExtractionKind.Malformed))]
    [InlineData("image-only.pdf", nameof(DocumentExtractionKind.NoText))]
    public void Run_DocumentWithoutText_WritesTheFrameOfItsVerdictAndExitsZero(string fileName, string kindName)
    {
        DocumentExtractionKind kind = Enum.Parse<DocumentExtractionKind>(kindName);

        // A verdict about the document is not a worker failure: it travels in the frame, and the exit code stays zero.
        (ComprehensionWorkerExitCode code, byte[] written) = Run(PdfFixtures.Read(fileName));

        Assert.Equal(ComprehensionWorkerExitCode.FrameWritten, code);

        Assert.Equal(DocumentExtractionOutcome.Without(kind), Decode(written), OutcomeComparer.Instance);
    }

    [Fact]
    public void Run_EmptyStdin_WritesAMalformedFrame()
    {
        (ComprehensionWorkerExitCode code, byte[] written) = Run([]);

        Assert.Equal(ComprehensionWorkerExitCode.FrameWritten, code);

        Assert.Equal(DocumentExtractionKind.Malformed, Decode(written).Kind);
    }

    [Fact]
    public void Run_HandsTheExtractorExactlyTheBytesItRead()
    {
        byte[] input = PdfFixtures.Read("cups-text.pdf");

        byte[]? seen = null;

        (ComprehensionWorkerExitCode code, _) = Run(input, extract: (bytes, _) =>
        {
            seen = bytes.ToArray();

            return DocumentExtractionOutcome.Without(DocumentExtractionKind.NoText);
        });

        Assert.Equal(ComprehensionWorkerExitCode.FrameWritten, code);

        Assert.Equal(input, seen);
    }

    [Fact]
    public void Run_InputOfExactlyTheIntakeCeiling_IsRead()
    {
        int extractions = 0;

        byte[] input = new byte[checked((int)DocumentWorkerProfile.Pdf.InputCeilingBytes)];

        (ComprehensionWorkerExitCode code, byte[] written) = Run(input, extract: (bytes, _) =>
        {
            extractions++;

            Assert.Equal(input.Length, bytes.Length);

            return DocumentExtractionOutcome.Without(DocumentExtractionKind.Malformed);
        });

        Assert.Equal(ComprehensionWorkerExitCode.FrameWritten, code);

        Assert.Equal(1, extractions);

        Assert.Equal(DocumentExtractionKind.Malformed, Decode(written).Kind);
    }

    [Fact]
    public void Run_InputOneByteOverTheIntakeCeiling_IsRefusedBeforeExtractionWithNothingWritten()
    {
        int extractions = 0;

        byte[] input = new byte[checked((int)DocumentWorkerProfile.Pdf.InputCeilingBytes + 1)];

        (ComprehensionWorkerExitCode code, byte[] written) = Run(input, extract: (_, _) =>
        {
            extractions++;

            return DocumentExtractionOutcome.Without(DocumentExtractionKind.Malformed);
        });

        Assert.Equal(ComprehensionWorkerExitCode.InputTooLarge, code);

        Assert.Equal(0, extractions);

        Assert.Empty(written);
    }

    [Fact]
    public void Run_InputFarOverTheIntakeCeiling_StopsReadingAtOneBytePastIt()
    {
        // The refusal must not drain an unbounded stdin first: one byte past the ceiling is proof enough.
        CountingStream input = new(totalBytes: 64L * 1024 * 1024);

        (ComprehensionWorkerExitCode code, byte[] written) = Run(input);

        Assert.Equal(ComprehensionWorkerExitCode.InputTooLarge, code);

        Assert.Empty(written);

        Assert.Equal(DocumentWorkerProfile.Pdf.InputCeilingBytes + 1, input.BytesRead);
    }

    [Fact]
    public void Run_StdinThatFailsToRead_IsUnreadableWithNothingWritten()
    {
        (ComprehensionWorkerExitCode code, byte[] written) = Run(new FailingStream());

        Assert.Equal(ComprehensionWorkerExitCode.InputUnreadable, code);

        Assert.Empty(written);
    }

    public static TheoryData<string[]> MalformedArguments() =>
    [
        [],
        [ComprehensionWorkerEntry.ModeArgument],
        [ComprehensionWorkerEntry.ModeArgument, "docx"],
        [ComprehensionWorkerEntry.ModeArgument, "PDF"],
        [ComprehensionWorkerEntry.ModeArgument, " pdf"],
        [ComprehensionWorkerEntry.ModeArgument, "pdf", "extra"],
        ["__sandbox-exec", "pdf"],
    ];

    [Theory]
    [MemberData(nameof(MalformedArguments))]
    public void Run_MalformedArguments_IsUsageWithoutReadingOrWriting(string[] arguments)
    {
        using MemoryStream output = new();

        ComprehensionWorkerExitCode code = ComprehensionWorkerEntry.Run(arguments, new FailingStream(), output);

        Assert.Equal(ComprehensionWorkerExitCode.Usage, code);

        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void Run_ExtractionThatOutlivesTheDeadline_EndsAtTheDeadlineWithNothingWritten()
    {
        using ManualResetEventSlim release = new(false);

        CancellationToken observed = default;

        Stopwatch elapsed = Stopwatch.StartNew();

        try
        {
            (ComprehensionWorkerExitCode code, byte[] written) = Run(
                PdfFixtures.Read("cups-text.pdf"),
                TimeSpan.FromMilliseconds(200),
                (_, token) =>
                {
                    observed = token;

                    // A parser that never looks at its token, as PdfPig does inside one page.
                    release.Wait(TimeSpan.FromSeconds(30));

                    return DocumentExtractionOutcome.Without(DocumentExtractionKind.NoText);
                });

            elapsed.Stop();

            Assert.Equal(ComprehensionWorkerExitCode.Deadline, code);

            Assert.Empty(written);

            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), $"The worker waited {elapsed.Elapsed} past a 200 ms deadline.");

            Assert.True(observed.IsCancellationRequested, "The extraction's token was not cancelled at the deadline.");
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void Run_StdinThatNeverEnds_EndsAtTheDeadlineWithNothingWritten()
    {
        // The deadline covers the read as well: a host that never closes stdin cannot keep the worker alive.
        using BlockingStream input = new();

        (ComprehensionWorkerExitCode code, byte[] written) = Run(input, TimeSpan.FromMilliseconds(200));

        Assert.Equal(ComprehensionWorkerExitCode.Deadline, code);

        Assert.Empty(written);
    }

    [Fact]
    public void Run_ExtractionCancelledByTheDeadline_IsADeadlineNotAFault()
    {
        // PdfPig checks the token between pages; the extractor then throws, and that is the deadline, not a crash.
        (ComprehensionWorkerExitCode code, byte[] written) = Run(
            PdfFixtures.Read("cups-text.pdf"),
            TimeSpan.FromMilliseconds(100),
            (_, token) =>
            {
                token.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));

                token.ThrowIfCancellationRequested();

                return DocumentExtractionOutcome.Without(DocumentExtractionKind.NoText);
            });

        Assert.Equal(ComprehensionWorkerExitCode.Deadline, code);

        Assert.Empty(written);
    }

    [Fact]
    public void Run_ExtractionThatThrows_IsAFaultWithNothingWritten()
    {
        (ComprehensionWorkerExitCode code, byte[] written) = Run(
            PdfFixtures.Read("cups-text.pdf"),
            extract: (_, _) => throw new InvalidOperationException("parser exploded at /secret/path"));

        Assert.Equal(ComprehensionWorkerExitCode.Fault, code);

        Assert.Empty(written);
    }

    [Fact]
    public void Run_OutcomeThatNoFrameCanCarry_IsAFaultWithNothingWritten()
    {
        (ComprehensionWorkerExitCode code, byte[] written) = Run(
            PdfFixtures.Read("cups-text.pdf"),
            extract: (_, _) => new DocumentExtractionOutcome(DocumentExtractionKind.NoText, ["text a no-text frame cannot carry"], false));

        Assert.Equal(ComprehensionWorkerExitCode.Fault, code);

        Assert.Empty(written);
    }

    [Fact]
    public void Run_StdoutThatFailsToWrite_IsAFault()
    {
        using MemoryStream input = new(PdfFixtures.Read("multi-page.pdf"), writable: false);

        ComprehensionWorkerExitCode code = ComprehensionWorkerEntry.Run(
            DocumentWorkerProfile.Pdf with { SelfDeadline = Generous },
            input,
            new FailingStream(),
            ComprehensionWorkerEntry.ExtractorFor(DocumentWorkerFormat.Pdf)!);

        Assert.Equal(ComprehensionWorkerExitCode.Fault, code);
    }

    [Fact]
    public void ExitCodes_AreTheCodeOwnedTableThatAvoidsTheShellAndSignalRanges()
    {
        // 126 is the resource-limit prelude's refusal, and 128 and above are a shell's report of a signal, so a worker
        // code there would be read as something the worker never said.
        Assert.Equal(
            [
                (ComprehensionWorkerExitCode.FrameWritten, 0),
                (ComprehensionWorkerExitCode.Usage, 2),
                (ComprehensionWorkerExitCode.InputTooLarge, 3),
                (ComprehensionWorkerExitCode.InputUnreadable, 4),
                (ComprehensionWorkerExitCode.Deadline, 5),
                (ComprehensionWorkerExitCode.Fault, 6),
            ],
            Enum.GetValues<ComprehensionWorkerExitCode>().Select(static code => (code, (int)code)));

        Assert.All(Enum.GetValues<ComprehensionWorkerExitCode>(), static code => Assert.InRange((int)code, 0, 125));
    }

    [Fact]
    public void ModeArgument_IsTheHiddenMode()
    {
        // A kind's argument, input ceiling and self-deadline are its profile's, pinned with the profile.
        Assert.Equal("__comprehension-worker", ComprehensionWorkerEntry.ModeArgument);
    }

    private static (ComprehensionWorkerExitCode Code, byte[] Written) Run(
        byte[] input,
        TimeSpan? deadline = null,
        Func<ReadOnlyMemory<byte>, CancellationToken, DocumentExtractionOutcome>? extract = null)
    {
        using MemoryStream stream = new(input, writable: false);

        return Run(stream, deadline, extract);
    }

    private static (ComprehensionWorkerExitCode Code, byte[] Written) Run(
        Stream input,
        TimeSpan? deadline = null,
        Func<ReadOnlyMemory<byte>, CancellationToken, DocumentExtractionOutcome>? extract = null)
    {
        using MemoryStream output = new();

        // The PDF profile with its self-deadline replaced, so a test can end it sooner or give a slow host time.
        ComprehensionWorkerExitCode code = ComprehensionWorkerEntry.Run(
            DocumentWorkerProfile.Pdf with { SelfDeadline = deadline ?? Generous },
            input,
            output,
            extract ?? ComprehensionWorkerEntry.ExtractorFor(DocumentWorkerFormat.Pdf)!);

        return (code, output.ToArray());
    }

    private static DocumentExtractionOutcome Decode(byte[] frame)
    {
        ComprehensionWorkerFrameResult decoded = ComprehensionWorkerFrame.Decode(frame, DocumentWorkerProfile.Pdf.MaxOutputCharacters);

        Assert.True(decoded.IsValid, $"The worker wrote a frame the host refuses: {decoded.Defect}");

        return decoded.Outcome!;
    }

    private sealed class OutcomeComparer : IEqualityComparer<DocumentExtractionOutcome>
    {
        public static OutcomeComparer Instance { get; } = new();

        public bool Equals(DocumentExtractionOutcome? x, DocumentExtractionOutcome? y) =>
            x is not null && y is not null && x.Kind == y.Kind && x.Truncated == y.Truncated && x.Pages.SequenceEqual(y.Pages);

        public int GetHashCode(DocumentExtractionOutcome obj) => obj.Kind.GetHashCode();
    }

    /// <summary>A stream that serves zero bytes up to a length and counts what it served.</summary>
    private sealed class CountingStream(long totalBytes) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int served = (int)Math.Min(count, totalBytes - BytesRead);

            Array.Clear(buffer, offset, served);

            BytesRead += served;

            return served;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A stream whose every read and write fails as a broken pipe would.</summary>
    private sealed class FailingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Broken pipe.");

        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("Broken pipe.");

        public override void Flush() => throw new IOException("Broken pipe.");

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>A stdin whose writer never writes and never closes.</summary>
    private sealed class BlockingStream : Stream
    {
        private readonly ManualResetEventSlim _closed = new(false);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            _closed.Wait(TimeSpan.FromSeconds(30));

            return 0;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _closed.Set();

            base.Dispose(disposing);
        }
    }
}
