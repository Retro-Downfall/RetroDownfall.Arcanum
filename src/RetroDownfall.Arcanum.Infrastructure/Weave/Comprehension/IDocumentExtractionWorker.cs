using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>The document formats an isolated worker reads.</summary>
internal enum DocumentWorkerFormat
{
    Pdf,
}

/// <summary>
/// What one isolated extraction concluded: the worker's verdict about the document, or the code-owned reason the worker
/// gave none. Nothing the worker, its runtime or the operating system said reaches <see cref="FailureReason"/>.
/// </summary>
internal sealed class DocumentWorkerResult
{
    private DocumentWorkerResult(PdfExtractionOutcome? outcome, string? failureReason)
    {
        Outcome = outcome;

        FailureReason = failureReason;
    }

    /// <summary>The worker's verdict, or <see langword="null"/> when it gave none.</summary>
    internal PdfExtractionOutcome? Outcome { get; }

    /// <summary>The code-owned reason no verdict was given, or <see langword="null"/> when one was.</summary>
    internal string? FailureReason { get; }

    internal static DocumentWorkerResult Completed(PdfExtractionOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        return new DocumentWorkerResult(outcome, null);
    }

    internal static DocumentWorkerResult Failed(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return new DocumentWorkerResult(null, reason);
    }
}

/// <summary>
/// The one reviewed seam between a document's host and the process that parses it. The host awaits it inline under its
/// caller's admission; the implementation owns the child process, its limits and its teardown, and returns only after
/// the child is gone. Cancelling <c>cancellationToken</c> kills the child and throws <see cref="OperationCanceledException"/>.
/// </summary>
internal interface IDocumentExtractionWorker
{
    Task<DocumentWorkerResult> RunAsync(
        DocumentWorkerFormat format,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken);
}
