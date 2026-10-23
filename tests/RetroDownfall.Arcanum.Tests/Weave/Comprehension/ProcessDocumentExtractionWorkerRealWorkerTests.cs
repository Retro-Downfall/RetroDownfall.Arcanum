using System.Runtime.InteropServices;

using Xunit.Abstractions;

using RetroDownfall.Arcanum.Infrastructure.Platform;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

using RetroDownfall.Arcanum.Tests.Process;

using RetroDownfall.Arcanum.Tests.Weave.Comprehension.Pdf;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// The real worker, re-launched as the managed Cli apphost in this test output through the production launch: the hidden
/// mode in the Cli's <c>Main</c>, the applied jail with no network, the cleared environment and the code-owned limits.
/// The test host cannot re-launch itself, so the launch is pointed at the apphost and given what a managed apphost needs
/// beyond the Native AOT binary: its runtime's location, read access to that runtime, and more file descriptors.
/// Everything else is the production path. The worker's verdict must equal what the
/// extractor says in this process for the same bytes.
/// </summary>
[Collection("ChildProcess")]
public sealed class ProcessDocumentExtractionWorkerRealWorkerTests(ITestOutputHelper output)
{
    private const string HomebrewPrefix = "/opt/homebrew/";

    private const int ManagedApphostFileDescriptors = 256;

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
        Skip.IfNot(MacOsSandboxProbe.IsSandboxExecRunnable(), "Needs a macOS host where sandbox-exec can apply a profile.");

        string apphost = Path.Combine(AppContext.BaseDirectory, "RetroDownfall.Arcanum.Cli");

        Skip.IfNot(File.Exists(apphost), "The Cli apphost is not in the test output.");

        // The apphost finds the shared runtime through DOTNET_ROOT, which the jailed child must also be allowed to read
        // and map, together with the libraries that runtime links against (a Homebrew runtime's compression library
        // links Homebrew's brotli; without it every Flate stream decodes to nothing). It also keeps far more files open
        // than the Native AOT binary the descriptor limit is sized for. A Native AOT binary needs none of this.
        string dotnetRoot = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));

        List<string> runtimeRoots = [dotnetRoot];

        if (dotnetRoot.StartsWith(HomebrewPrefix, StringComparison.Ordinal))
        {
            runtimeRoots.Add(HomebrewPrefix);
        }

        using IDisposable launch = ProcessDocumentExtractionWorker.UseExecutableForTests(
            apphost,
            new Dictionary<string, string> { ["DOTNET_ROOT"] = dotnetRoot },
            runtimeRoots,
            ManagedApphostFileDescriptors);

        return await new ProcessDocumentExtractionWorker(new ProcessResourceLimiter())
            .RunAsync(DocumentWorkerFormat.Pdf, document, CancellationToken.None);
    }
}
