using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension.Pdf;

public sealed class PdfTextExtractorCancellationTests
{
    [Fact]
    public void Extract_CancelledToken_PropagatesTheCancellationInsteadOfAnOutcome()
    {
        // PdfPig cannot be interrupted inside Open or GetPage, so the page boundary is the only cooperative point.
        // Sensitivity: the check at the top of the page loop. Cancellation must also never be mapped to Malformed.
        using CancellationTokenSource cancellation = new();

        cancellation.Cancel();

        PdfTextExtractor extractor = new();

        byte[] bytes = PdfFixtures.Read("multi-page.pdf");

        Assert.Throws<OperationCanceledException>(() => extractor.Extract(bytes, PdfExtractionLimits.Default, cancellation.Token));
    }

    [Fact]
    public void Extract_TokenThatIsNeverCancelled_ReadsEveryPage()
    {
        using CancellationTokenSource cancellation = new();

        DocumentExtractionOutcome outcome = new PdfTextExtractor().Extract(
            PdfFixtures.Read("multi-page.pdf"),
            PdfExtractionLimits.Default,
            cancellation.Token);

        Assert.Equal(4, outcome.Pages.Count);
    }
}
