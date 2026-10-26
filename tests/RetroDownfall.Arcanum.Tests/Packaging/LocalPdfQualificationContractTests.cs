namespace RetroDownfall.Arcanum.Tests.Packaging;

public sealed class LocalPdfQualificationContractTests
{
    [Fact]
    public void Pdf_phase_is_required_after_main_assertions_and_before_owned_cleanup_and_receipt()
    {
        string root = FindRoot();

        string source = File.ReadAllText(Path.Combine(root, "tests/RetroDownfall.Arcanum.Tests/Packaging/PublishedArcanumOllamaQualificationTests.cs"));

        int mainEvidence = source.IndexOf("_ = await AssertPersistedTimestampEncodingAsync(configurationDirectory)", StringComparison.Ordinal);

        int pdfPhase = source.IndexOf("await RunPdfQualificationAsync(", StringComparison.Ordinal);

        int completion = source.IndexOf("pdfCompleted = true", StringComparison.Ordinal);

        int shutdown = source.IndexOf("await StopHostAsync(api, restartedHost", StringComparison.Ordinal);

        int receipt = source.LastIndexOf("await File.WriteAllTextAsync(", StringComparison.Ordinal);

        Assert.True(mainEvidence >= 0 && pdfPhase > mainEvidence && completion > pdfPhase && shutdown > completion);

        Assert.True(source.LastIndexOf("isolation.Delete()", StringComparison.Ordinal) < receipt);

        Assert.Contains("PdfQualificationEvidence.Receipt(pdfOptions is not null, perplexity, pdfCompleted)", source, StringComparison.Ordinal);

        string pdf = File.ReadAllText(Path.Combine(root, "tests/RetroDownfall.Arcanum.Tests/Packaging/PublishedArcanumOllamaQualificationTests.Pdf.cs"));

        Assert.Contains("CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)", pdf, StringComparison.Ordinal);

        Assert.Contains("CancellationTokenSource.CreateLinkedTokenSource(token)", pdf, StringComparison.Ordinal);

        Assert.Contains("turnDeadline.CancelAfter(TurnBudget)", pdf, StringComparison.Ordinal);

        Assert.Contains("indexDeadline.CancelAfter(TimeSpan.FromMinutes(3))", pdf, StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData("ollama-pdf-perplexity", false, 0)]
    [InlineData("ollama-pdf", false, 1)]
    [InlineData("ollama-perplexity", false, 1)]
    [InlineData("ollama-pdf-perplexity", true, 1)]
    public async Task Combined_pdf_search_mode_requires_both_phases_and_an_exact_receipt(string receipt, bool trailing, int expected)
    {
        Skip.If(OperatingSystem.IsWindows(), "The wrapper fixture needs native POSIX semantics.");

        using PublishedApphostVerificationScriptTests.ScriptFixture fixture = new(receipt);

        fixture.EnvironmentOverrides["PDF_SEARCH_SOURCE"] = "synthetic-search-canary";

        if (trailing) fixture.AppendReceiptBytesWithoutANewline();

        var result = await fixture.RunAsync(Path.Combine(fixture.RepositoryRoot, "scripts", "verify-local-ollama-aot.sh"),
            "--executable", fixture.Executable, "--image", fixture.Image, "--pdf", "--perplexity",
            "--perplexity-key-variable", "PDF_SEARCH_SOURCE");

        Assert.Equal(expected, result.ExitCode);

        Assert.DoesNotContain("synthetic-search-canary", result.StandardOutput + result.StandardError + File.ReadAllText(fixture.DotnetLog), StringComparison.Ordinal);

        Assert.Contains("pdf-mode=1", File.ReadAllText(fixture.DotnetLog), StringComparison.Ordinal);

        Assert.Contains("--blame-hang-timeout 45m", File.ReadAllText(fixture.DotnetLog), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("baseline")]
    [InlineData("unbounded")]
    [InlineData("unknown")]
    [InlineData("extra")]
    [InlineData("other-branch")]
    [InlineData("invocation")]
    [InlineData("mode")]
    public void Closed_pdf_hang_contract_rejects_unknown_or_unbounded_values_and_other_branches(string defect)
    {
        string source = File.ReadAllText(Path.Combine(FindRoot(), "scripts", "verify-local-ollama-aot.sh"));

        Assert.True(ReleasePipelineTests.HasClosedPdfHangTimeout(source));

        Assert.True(ReleasePipelineTests.HasClosedPdfHangTimeout(source.Replace("\n", "\r\n", StringComparison.Ordinal)));

        string wrong = defect switch
        {
            "baseline" => source.Replace("HANG_TIMEOUT=30m", "HANG_TIMEOUT=45m", StringComparison.Ordinal),
            "unbounded" => source.Replace("HANG_TIMEOUT=45m", "HANG_TIMEOUT=0m", StringComparison.Ordinal),
            "unknown" => source.Replace("HANG_TIMEOUT=45m", "HANG_TIMEOUT=$OPERATOR", StringComparison.Ordinal),
            "extra" => source + "\nHANG_TIMEOUT=90m\n",
            "other-branch" => source.Replace("    --pdf)", "    --perplexity)", StringComparison.Ordinal),
            "invocation" => source.Replace("\"$HANG_TIMEOUT\"", "\"$OTHER_TIMEOUT\"", StringComparison.Ordinal),
            _ => source.Replace("PDF=0\n", "PDF=1\n", StringComparison.Ordinal),
        };

        Assert.False(ReleasePipelineTests.HasClosedPdfHangTimeout(wrong));
    }

    [SkippableTheory]
    [InlineData("runner")]
    [InlineData("cleanup")]
    [InlineData("warning")]
    public async Task Pdf_receipt_cannot_hide_failed_harness_warning_or_owned_cleanup(string failure)
    {
        Skip.If(OperatingSystem.IsWindows(), "The wrapper fixture needs native POSIX semantics.");

        using PublishedApphostVerificationScriptTests.ScriptFixture fixture = new("ollama-pdf");

        if (failure == "runner") fixture.EnvironmentOverrides["FAKE_TEST_EXIT"] = "19";
        else if (failure == "cleanup") fixture.FailRecursiveRemovalFor("arcanum-local-ollama-aot.");
        else fixture.EmitWarningOutput();

        var result = await fixture.RunAsync(Path.Combine(fixture.RepositoryRoot, "scripts", "verify-local-ollama-aot.sh"),
            "--executable", fixture.Executable, "--image", fixture.Image, "--pdf");

        Assert.NotEqual(0, result.ExitCode);

        if (failure == "runner") Assert.Equal(19, result.ExitCode);

        if (failure == "cleanup") Assert.Contains("could not remove", result.StandardError, StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);

        while (current is not null && !File.Exists(Path.Combine(current.FullName, "RetroDownfall.Arcanum.slnx"))) current = current.Parent;

        return current?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    [SkippableTheory]
    [InlineData("ollama-pdf", 0)]
    [InlineData("ollama", 1)]
    [InlineData(null, 1)]
    public async Task Wrapper_pdf_mode_requires_the_distinct_completed_pdf_receipt(string? receipt, int expectedExit)
    {
        Skip.If(OperatingSystem.IsWindows(), "The wrapper fixture needs native POSIX semantics.");

        using PublishedApphostVerificationScriptTests.ScriptFixture fixture = new(receipt);

        var result = await fixture.RunAsync(
            Path.Combine(fixture.RepositoryRoot, "scripts", "verify-local-ollama-aot.sh"),
            "--executable", fixture.Executable, "--image", fixture.Image, "--pdf");

        Assert.Equal(expectedExit, result.ExitCode);

        if (expectedExit == 0)
        {
            Assert.Contains("PDF", result.StandardOutput, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("success receipt", result.StandardError, StringComparison.Ordinal);
        }
    }
}
