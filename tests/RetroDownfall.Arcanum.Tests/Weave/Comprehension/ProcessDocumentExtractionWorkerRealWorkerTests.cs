using Xunit.Abstractions;

using RetroDownfall.Arcanum.Infrastructure.Platform;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

using RetroDownfall.Arcanum.Tests.Support;

using RetroDownfall.Arcanum.Tests.Weave.Comprehension.Pdf;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// The real worker, re-launched out of process as the managed Cli apphost through the production launch
/// (<see cref="ManagedApphostDocumentWorker"/>). The worker's verdict must equal what the extractor says in this process
/// for the same bytes, and a hostile document must end in its own process without affecting the next one.
/// </summary>
[Collection("ChildProcess")]
public sealed class ProcessDocumentExtractionWorkerRealWorkerTests(ITestOutputHelper output)
{
    [SkippableTheory]
    [InlineData("cups-text.pdf")]
    [InlineData("multi-page.pdf")]
    [InlineData("cups-multi.pdf")]
    [InlineData("hidden.pdf")]
    [InlineData("astral-plane.pdf")]
    [InlineData("encrypted-aes-empty.pdf")]
    [InlineData("encrypted-rc4-empty.pdf")]
    [InlineData("encrypted-aes-password.pdf")]
    [InlineData("encrypted-rc4-password.pdf")]
    [InlineData("truncated.pdf")]
    [InlineData("missing-root.pdf")]
    [InlineData("garbage.pdf")]
    [InlineData("image-only.pdf")]
    [InlineData("active-content.pdf")]
    [InlineData("non-embedded-font.pdf")]
    [InlineData("cjk-predefined-cmap.pdf")]
    [InlineData("cyclic-xobject.pdf")]
    [InlineData("deep-array-content.pdf")]
    public async Task The_worker_reaches_the_same_verdict_as_the_extractor(string fileName)
    {
        byte[] document = PdfFixtures.Read(fileName);

        PdfExtractionOutcome expected = new PdfTextExtractor().Extract(document, PdfExtractionLimits.Default, CancellationToken.None);

        DocumentWorkerResult result = await RunAsync(document);

        Assert.True(result.FailureReason is null, result.FailureReason);

        Assert.Equal(expected.Kind, result.Outcome!.Kind);

        Assert.Equal(expected.Truncated, result.Outcome.Truncated);

        Assert.Equal(expected.Pages, result.Outcome.Pages);
    }

    [SkippableFact]
    public async Task The_page_three_marker_arrives_on_page_three()
    {
        DocumentWorkerResult result = await RunAsync(PdfFixtures.Read("multi-page.pdf"));

        Assert.Equal(PdfExtractionKind.Extracted, result.Outcome!.Kind);

        Assert.Equal(["PAGE-ONE-ALPHA", string.Empty, "PAGE-THREE-MARKER-GAMMA", "PAGE-FOUR-DELTA"], result.Outcome.Pages);
    }

    [SkippableFact]
    public async Task An_empty_password_document_is_extracted_and_a_password_document_is_not()
    {
        DocumentWorkerResult open = await RunAsync(PdfFixtures.Read("encrypted-aes-empty.pdf"));

        Assert.Equal(PdfExtractionKind.Extracted, open.Outcome!.Kind);

        DocumentWorkerResult locked = await RunAsync(PdfFixtures.Read("encrypted-aes-password.pdf"));

        Assert.Equal(PdfExtractionKind.PasswordRequired, locked.Outcome!.Kind);

        Assert.Empty(locked.Outcome.Pages);
    }

    [SkippableTheory]
    [InlineData("flate-letters-5m.pdf")]
    [InlineData("flate-letters-20m.pdf")]
    public async Task A_gigabyte_scale_document_ends_failed_by_the_limits_and_a_healthy_document_after_it_still_extracts(string fileName)
    {
        // Gigabytes and tens of seconds in an unlimited parser: the worker's memory ceiling or its deadline ends it, in its
        // own process, and the next document is unaffected.
        DocumentWorkerResult hostile = await RunAsync(PdfFixtures.Read(fileName));

        output.WriteLine($"{fileName}: {hostile.FailureReason ?? hostile.Outcome?.Kind.ToString()}");

        Assert.Null(hostile.Outcome);

        Assert.Contains(
            hostile.FailureReason,
            new[] { ProcessDocumentExtractionWorker.ResourceLimitsReason, ProcessDocumentExtractionWorker.DeadlineReason });

        DocumentWorkerResult healthy = await RunAsync(PdfFixtures.Read("multi-page.pdf"));

        Assert.Equal(PdfExtractionKind.Extracted, healthy.Outcome!.Kind);

        Assert.Contains("PAGE-THREE-MARKER-GAMMA", healthy.Outcome.Pages);
    }

    [SkippableTheory]
    [InlineData("flate-bomb.pdf")]
    [InlineData("flate-letters-1m.pdf")]
    public async Task A_hostile_document_under_the_limits_ends_in_a_verdict_or_a_bounded_failure(string fileName)
    {
        // These fit the ceilings in an unlimited process (hundreds of megabytes, a few seconds). Whatever the worker makes
        // of them, it is a bounded result in its own process, and the next document is unaffected.
        DocumentWorkerResult hostile = await RunAsync(PdfFixtures.Read(fileName));

        output.WriteLine($"{fileName}: {hostile.FailureReason ?? hostile.Outcome?.Kind.ToString()}");

        Assert.True(
            hostile.Outcome is not null
            || hostile.FailureReason is ProcessDocumentExtractionWorker.ResourceLimitsReason or ProcessDocumentExtractionWorker.DeadlineReason,
            hostile.FailureReason);

        DocumentWorkerResult healthy = await RunAsync(PdfFixtures.Read("cups-text.pdf"));

        Assert.Equal(PdfExtractionKind.Extracted, healthy.Outcome!.Kind);
    }

    private static async Task<DocumentWorkerResult> RunAsync(byte[] document)
    {
        using IDisposable launch = ManagedApphostDocumentWorker.Use();

        return await new ProcessDocumentExtractionWorker(new ProcessResourceLimiter())
            .RunAsync(DocumentWorkerFormat.Pdf, document, CancellationToken.None);
    }
}
