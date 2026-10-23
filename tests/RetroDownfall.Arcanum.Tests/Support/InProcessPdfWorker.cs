using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// A document worker that runs the production PDF extractor in this process and hands its verdict through the real wire
/// frame, so a production path that reaches the registry's PDF host can be driven end to end without starting a process.
/// The process boundary itself is covered by the process worker tests.
/// </summary>
internal sealed class InProcessPdfWorker : IDocumentExtractionWorker
{
    private int _runs;

    /// <summary>How many documents reached the worker.</summary>
    public int Runs => Volatile.Read(ref _runs);

    /// <summary>Makes this worker the registry's PDF worker for the calling async flow.</summary>
    public static IDisposable Use(out InProcessPdfWorker worker)
    {
        worker = new InProcessPdfWorker();

        return ProcessDocumentExtractionWorker.UseSubstituteForTests(worker);
    }

    /// <inheritdoc cref="Use(out InProcessPdfWorker)"/>
    public static IDisposable Use() => Use(out _);

    public Task<DocumentWorkerResult> RunAsync(DocumentWorkerFormat format, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        Assert.Equal(DocumentWorkerFormat.Pdf, format);

        Interlocked.Increment(ref _runs);

        DocumentExtractionOutcome outcome = new PdfTextExtractor().Extract(bytes, PdfExtractionLimits.Default, cancellationToken);

        ComprehensionWorkerFrameResult frame = ComprehensionWorkerFrame.Decode(
            ComprehensionWorkerFrame.Encode(outcome, DocumentWorkerProfile.Pdf.MaxOutputCharacters),
            DocumentWorkerProfile.Pdf.MaxOutputCharacters);

        Assert.True(frame.IsValid, $"The extractor's verdict did not survive the frame: {frame.Defect}");

        return Task.FromResult(DocumentWorkerResult.Completed(frame.Outcome!));
    }
}
