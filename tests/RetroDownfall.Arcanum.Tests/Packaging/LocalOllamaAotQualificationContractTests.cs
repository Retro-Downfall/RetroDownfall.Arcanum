using System.Buffers.Binary;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace RetroDownfall.Arcanum.Tests.Packaging;

public sealed class LocalOllamaAotQualificationContractTests
{
    private const string QualifiedImageSha256 =
        "88d2994d07000a2c3bb31f307c536c6a9c45731d9d002c33bd8407817f269cb0";

    private const string RetiredImageSha256 =
        "0495a19041de6db44a4a8eca3b5117e6b1ff0ce14b80a22134bb9d65a7f87b22";

    private const string OptInVariable = "ARCANUM_RUN_LOCAL_OLLAMA_AOT_QUALIFICATION";

    private const string QualificationClass = "PublishedArcanumOllamaQualificationTests";

    private const string WrapperName = "verify-local-ollama-aot.sh";

    private const string ExactQualificationFilter =
        "FullyQualifiedName=RetroDownfall.Arcanum.Tests.Packaging.PublishedArcanumOllamaQualificationTests."
        + "Published_native_aot_preserves_corrected_file_context_and_runs_vision_across_restart";

    private static void RequirePosixScriptFixture() =>
        Skip.If(
            OperatingSystem.IsWindows(),
            "The Bash behavior fixture requires native POSIX process and filesystem semantics.");

    [Fact]
    public void Wrapper_requires_existing_inputs_and_never_builds_the_shipping_product()
    {
        string root = FindRepositoryRoot();
        string wrapperPath = Path.Combine(root, "scripts", WrapperName);

        Assert.True(File.Exists(wrapperPath), $"Missing local qualification wrapper: {wrapperPath}");

        string wrapper = File.ReadAllText(wrapperPath);

        Assert.Contains("set -euo pipefail", wrapper, StringComparison.Ordinal);
        Assert.Contains("--executable", wrapper, StringComparison.Ordinal);
        Assert.Contains("--image", wrapper, StringComparison.Ordinal);
        Assert.Contains("--model", wrapper, StringComparison.Ordinal);
        Assert.Contains("--endpoint", wrapper, StringComparison.Ordinal);
        Assert.Contains(OptInVariable, wrapper, StringComparison.Ordinal);
        Assert.Contains(QualificationClass, wrapper, StringComparison.Ordinal);
        Assert.Contains("GITHUB_ACTIONS", wrapper, StringComparison.Ordinal);
        Assert.True(
            wrapper.IndexOf("GITHUB_ACTIONS", StringComparison.Ordinal)
                < wrapper.IndexOf("dotnet test", StringComparison.Ordinal),
            "The wrapper must reject GitHub Actions before invoking the test runner.");
        Assert.Contains("reject_warnings", wrapper, StringComparison.Ordinal);
        Assert.Contains("canonical_existing_file", wrapper, StringComparison.Ordinal);
        Assert.Contains("cygpath -u", wrapper, StringComparison.Ordinal);
        Assert.Contains("cygpath -w", wrapper, StringComparison.Ordinal);
        Assert.Contains("mktemp -d", wrapper, StringComparison.Ordinal);
        Assert.Contains("trap cleanup EXIT", wrapper, StringComparison.Ordinal);
        Assert.Contains("for attempt in 1 2 3", wrapper, StringComparison.Ordinal);
        Assert.Contains("local original_status=$?", wrapper, StringComparison.Ordinal);
        Assert.Contains("local final_status=\"$original_status\"", wrapper, StringComparison.Ordinal);
        Assert.Contains("trap - EXIT", wrapper, StringComparison.Ordinal);
        Assert.Contains("exit \"$final_status\"", wrapper, StringComparison.Ordinal);
        Assert.Contains("could not remove local qualification temporary directory", wrapper, StringComparison.Ordinal);
        Assert.Contains(QualifiedImageSha256, wrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet publish", wrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("ollama serve", wrapper, StringComparison.Ordinal);
    }

    [Fact]
    public void Qualified_vision_fixture_digest_is_rebaselined_consistently()
    {
        string root = FindRepositoryRoot();
        string[] governedPaths =
        [
            Path.Combine(root, "scripts", WrapperName),
            Path.Combine(
                root,
                "tests",
                "RetroDownfall.Arcanum.Tests",
                "Packaging",
                "PublishedArcanumOllamaQualificationTests.cs"),
            Path.Combine(
                root,
                "tests",
                "RetroDownfall.Arcanum.Tests",
                "Packaging",
                "PublishedApphostVerificationScriptTests.cs"),
            Path.Combine(root, "docs", "Arcanum.DESIGN.md"),
        ];

        foreach (string governedPath in governedPaths)
        {
            string contents = File.ReadAllText(governedPath);

            Assert.Contains(QualifiedImageSha256, contents, StringComparison.Ordinal);
            Assert.DoesNotContain(RetiredImageSha256, contents, StringComparison.Ordinal);
        }
    }

    [SkippableFact]
    public async Task Wrapper_fails_closed_when_the_exact_test_reports_success_without_a_receipt()
    {
        RequirePosixScriptFixture();

        using PublishedApphostVerificationScriptTests.ScriptFixture fixture = new();

        PublishedApphostVerificationScriptTests.ScriptResult result = await fixture.RunAsync(
            Path.Combine(fixture.RepositoryRoot, "scripts", WrapperName),
            "--executable",
            fixture.Executable,
            "--image",
            fixture.Image);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "selected local qualification test did not execute successfully",
            result.StandardError,
            StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Wrapper_runs_only_the_exact_qualification_and_accepts_its_receipt()
    {
        RequirePosixScriptFixture();

        using PublishedApphostVerificationScriptTests.ScriptFixture fixture = new(
            receiptKind: "ollama");

        PublishedApphostVerificationScriptTests.ScriptResult result = await fixture.RunAsync(
            Path.Combine(fixture.RepositoryRoot, "scripts", WrapperName),
            "--executable",
            Path.GetFileName(fixture.Executable),
            "--image",
            Path.GetFileName(fixture.Image));

        Assert.Equal(0, result.ExitCode);
        string dotnetLog = File.ReadAllText(fixture.DotnetLog);

        Assert.Contains($"--filter {ExactQualificationFilter}", dotnetLog, StringComparison.Ordinal);
        string publishedExecutable = PublishedApphostVerificationScriptTests.ReadLoggedValue(
            dotnetLog,
            "published-executable=");
        string image = PublishedApphostVerificationScriptTests.ReadLoggedValue(
            dotnetLog,
            "ollama-image=");

        Assert.True(Path.IsPathFullyQualified(publishedExecutable));
        Assert.True(Path.IsPathFullyQualified(image));
        Assert.Equal(Path.GetFileName(fixture.Executable), Path.GetFileName(publishedExecutable));
        Assert.Equal(Path.GetFileName(fixture.Image), Path.GetFileName(image));
    }

    [SkippableFact]
    public async Task Wrapper_fails_when_its_private_temporary_directory_cannot_be_removed()
    {
        RequirePosixScriptFixture();

        using PublishedApphostVerificationScriptTests.ScriptFixture fixture = new(
            receiptKind: "ollama");
        fixture.FailRecursiveRemovalFor("arcanum-local-ollama-aot.");
        fixture.CaptureBackoffDelays();
        fixture.DisableErrexitBeforeSuccessfulExit("Native AOT first-session");

        PublishedApphostVerificationScriptTests.ScriptResult result = await fixture.RunAsync(
            Path.Combine(fixture.RepositoryRoot, "scripts", WrapperName),
            "--executable",
            fixture.Executable,
            "--image",
            fixture.Image);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "could not remove local qualification temporary directory after three attempts",
            result.StandardError,
            StringComparison.Ordinal);
        Assert.Equal(["0.1", "0.2"], fixture.ReadBackoffDelays());
    }

    [SkippableFact]
    public async Task Wrapper_fails_when_its_warning_scan_cannot_run()
    {
        RequirePosixScriptFixture();

        using PublishedApphostVerificationScriptTests.ScriptFixture fixture = new(
            receiptKind: "ollama");
        fixture.FailSearches();

        PublishedApphostVerificationScriptTests.ScriptResult result = await fixture.RunAsync(
            Path.Combine(fixture.RepositoryRoot, "scripts", WrapperName),
            "--executable",
            fixture.Executable,
            "--image",
            fixture.Image);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("local qualification warning scan failed", result.StandardError, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Wrapper_rejects_receipt_bytes_after_the_required_line()
    {
        RequirePosixScriptFixture();

        using PublishedApphostVerificationScriptTests.ScriptFixture fixture = new(
            receiptKind: "ollama");
        fixture.AppendReceiptBytesWithoutANewline();

        PublishedApphostVerificationScriptTests.ScriptResult result = await fixture.RunAsync(
            Path.Combine(fixture.RepositoryRoot, "scripts", WrapperName),
            "--executable",
            fixture.Executable,
            "--image",
            fixture.Image);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("invalid success receipt", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void Wrapper_and_test_share_an_independent_exact_success_receipt_contract()
    {
        string root = FindRepositoryRoot();
        string wrapper = File.ReadAllText(Path.Combine(root, "scripts", WrapperName));
        string qualification = File.ReadAllText(
            Path.Combine(
                root,
                "tests",
                "RetroDownfall.Arcanum.Tests",
                "Packaging",
                $"{QualificationClass}.cs"));

        Assert.Contains(ExactQualificationFilter, wrapper, StringComparison.Ordinal);
        Assert.Contains("ARCANUM_OLLAMA_QUALIFICATION_RECEIPT", wrapper, StringComparison.Ordinal);
        Assert.Contains("ARCANUM_OLLAMA_QUALIFICATION_RECEIPT", qualification, StringComparison.Ordinal);
        Assert.Contains("local-ollama-aot-qualification:v1", wrapper, StringComparison.Ordinal);
        Assert.Contains("local-ollama-aot-qualification:v1", qualification, StringComparison.Ordinal);
        Assert.Contains(
            "SuccessReceipt + global::System.Environment.NewLine",
            qualification,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ARCANUM_PUBLISHED_SMOKE_RECEIPT", wrapper, StringComparison.Ordinal);
    }

    [Fact]
    public void Qualification_is_compiled_but_skips_without_exact_local_opt_in()
    {
        string root = FindRepositoryRoot();
        string testPath = Path.Combine(
            root,
            "tests",
            "RetroDownfall.Arcanum.Tests",
            "Packaging",
            $"{QualificationClass}.cs");

        Assert.True(File.Exists(testPath), $"Missing local qualification test: {testPath}");

        string test = File.ReadAllText(testPath);
        string isolation = File.ReadAllText(
            Path.Combine(
                root,
                "tests",
                "RetroDownfall.Arcanum.Tests",
                "Packaging",
                "PublishedTestProcessIsolation.cs"));

        Assert.Contains("[SkippableFact]", test, StringComparison.Ordinal);
        Assert.Contains(OptInVariable, test, StringComparison.Ordinal);
        Assert.Contains("Skip.IfNot", test, StringComparison.Ordinal);
        Assert.Contains("string.Equals(optIn, \"1\", StringComparison.Ordinal)", test, StringComparison.Ordinal);
        Assert.Contains("GITHUB_ACTIONS", test, StringComparison.Ordinal);
        Assert.Contains("Real-model qualification is local-only", test, StringComparison.Ordinal);
        const string githubActionsGuard = "LocalOllamaQualificationGuards.IsGitHubActions";
        Assert.Contains(githubActionsGuard, test, StringComparison.Ordinal);
        Assert.True(
            test.IndexOf(githubActionsGuard, StringComparison.Ordinal)
                < test.IndexOf("RequireEnvironmentPath", StringComparison.Ordinal)
            && test.IndexOf(githubActionsGuard, StringComparison.Ordinal)
                < test.IndexOf("AssertModelIsInstalledAsync", StringComparison.Ordinal),
            "The executable qualification path must reject GitHub Actions before reading inputs or contacting Ollama.");
        Assert.Contains("ARCANUM_PUBLISHED_EXECUTABLE", test, StringComparison.Ordinal);
        Assert.Contains("ARCANUM_OLLAMA_QUALIFICATION_IMAGE", test, StringComparison.Ordinal);
        Assert.Contains(QualifiedImageSha256, test, StringComparison.Ordinal);
        Assert.Contains("AssertNativeBinary", test, StringComparison.Ordinal);
        string guards = File.ReadAllText(
            Path.Combine(root, "tests", "RetroDownfall.Arcanum.Tests", "Packaging", "LocalOllamaQualificationGuards.cs"));
        Assert.Contains("FileAttributes.ReparsePoint", guards, StringComparison.Ordinal);
        Assert.Contains("Redact", test, StringComparison.Ordinal);
        Assert.Contains("Select(Redact)", test, StringComparison.Ordinal);
        Assert.Contains("_knownMasterApiKey", test, StringComparison.Ordinal);
        Assert.Contains("DisclosedMasterApiKey", test, StringComparison.Ordinal);
        Assert.Contains("environment.Redact(SafeCommand", test, StringComparison.Ordinal);
        Assert.Contains("PublishedTestProcessIsolation.Create", test, StringComparison.Ordinal);
        Assert.Contains("isolation.Delete()", test, StringComparison.Ordinal);
        Assert.Contains("DeleteDirectoryWithRetries", isolation, StringComparison.Ordinal);
        Assert.Contains("ApplyTemporaryEnvironment", isolation, StringComparison.Ordinal);
        Assert.Contains("TestRootDirectoryName", isolation, StringComparison.Ordinal);
        Assert.Contains("VisionAttachmentName", test, StringComparison.Ordinal);
        Assert.Contains("VisionPrompt", test, StringComparison.Ordinal);
        Assert.Contains("ContainsVisionAnswerLeakage", test, StringComparison.Ordinal);
        Assert.Contains("CreateLoopbackOnlyHttpHandler", test, StringComparison.Ordinal);
        Assert.Contains("ReadCappedResponseBytesAsync", test, StringComparison.Ordinal);
        Assert.Contains("opaque-input.dat", guards, StringComparison.Ordinal);
        Assert.DoesNotContain("dominant sign color", guards, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AssertEncryptedAttachmentPayloads", test, StringComparison.Ordinal);

        const string snapshotCopy = "File.Copy(sourceImage, isolatedImage, overwrite: false);";
        const string snapshotVerification = "AssertStopSignImage(isolatedImage);";
        const string snapshotRead = "File.ReadAllBytesAsync(isolatedImage)";

        Assert.Contains(snapshotCopy, test, StringComparison.Ordinal);
        Assert.Contains(snapshotVerification, test, StringComparison.Ordinal);
        Assert.Contains(snapshotRead, test, StringComparison.Ordinal);
        Assert.True(
            test.IndexOf(snapshotCopy, StringComparison.Ordinal)
                < test.IndexOf(snapshotVerification, StringComparison.Ordinal)
            && test.IndexOf(snapshotVerification, StringComparison.Ordinal)
                < test.IndexOf(snapshotRead, StringComparison.Ordinal),
            "The private image snapshot must be reverified before it becomes the sole vision input.");
        Assert.DoesNotContain("ReadAllBytesAsync(sourceImage)", test, StringComparison.Ordinal);
        Assert.Contains("AggregateException", test, StringComparison.Ordinal);
        Assert.Contains("cleanup", test, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HostCleanupAttemptBudget", test, StringComparison.Ordinal);
        Assert.Contains("CliCleanupAttemptBudget", test, StringComparison.Ordinal);
        Assert.Contains("CleanupCliProcessAsync", test, StringComparison.Ordinal);
        Assert.Contains("CliProcessCleanupResult", test, StringComparison.Ordinal);
        Assert.Contains("cleanupFailures.Insert(0, primaryFailure)", test, StringComparison.Ordinal);
        Assert.Contains("return cleanupFailures", test, StringComparison.Ordinal);
        Assert.DoesNotContain("await using PublishedHost", test, StringComparison.Ordinal);
        Assert.DoesNotContain("await process.WaitForExitAsync();", test, StringComparison.Ordinal);
        Assert.DoesNotContain("throw new IOException", test, StringComparison.Ordinal);
        Assert.Contains("ReadDurableReply(", test, StringComparison.Ordinal);
        Assert.Contains("IsExpectedVisionAnswer(", test, StringComparison.Ordinal);
        Assert.Contains("entries.EnumerateArray().Reverse().ToArray()", test, StringComparison.Ordinal);
        Assert.Contains("FirstTurnPrompt(conversationMarker)", test, StringComparison.Ordinal);
        Assert.Contains("SecondTurnPrompt", test, StringComparison.Ordinal);
        Assert.Contains("conversationMarker", test, StringComparison.Ordinal);
        Assert.Contains("STOP SIGN", guards, StringComparison.Ordinal);
        Assert.Contains("RED", guards, StringComparison.Ordinal);
        Assert.Contains("OCTAGON", guards, StringComparison.Ordinal);
        Assert.Contains("\"SIDES\", \"8\"", guards, StringComparison.Ordinal);
        Assert.Contains("\"TEXT\", \"STOP\"", guards, StringComparison.Ordinal);
        Assert.DoesNotContain("RIGHT-BETA-42", test, StringComparison.Ordinal);
    }

    [Fact]
    public void Real_model_output_budgets_cannot_truncate_the_required_opaque_evidence()
    {
        string test = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "RetroDownfall.Arcanum.Tests",
            "Packaging",
            $"{QualificationClass}.cs"));

        Assert.Contains(
            "private const string InitialTurnMaxTokens = \"96\";",
            test,
            StringComparison.Ordinal);

        Assert.Contains(
            "private const string ContextRecallMaxTokens = \"96\";",
            test,
            StringComparison.Ordinal);

        Assert.Contains(
            "private const string VisionRecallMaxTokens = \"160\";",
            test,
            StringComparison.Ordinal);

        Assert.Contains(
            "\"--max-tokens\",\n                InitialTurnMaxTokens",
            test,
            StringComparison.Ordinal);

        Assert.Contains(
            "\"--max-tokens\",\n                ContextRecallMaxTokens",
            test,
            StringComparison.Ordinal);

        Assert.Contains(
            "\"--max-tokens\",\n                VisionRecallMaxTokens",
            test,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Continuous_integration_never_enables_or_explicitly_invokes_real_model_qualification()
    {
        string root = FindRepositoryRoot();
        string githubDirectory = Path.Combine(root, ".github");
        string scriptsDirectory = Path.Combine(root, "scripts");
        string wrapperPath = Path.Combine(scriptsDirectory, WrapperName);
        string[] forbidden = [OptInVariable, QualificationClass, WrapperName];

        IEnumerable<string> guardedFiles = Directory
            .EnumerateFiles(githubDirectory, "*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(scriptsDirectory, "*", SearchOption.AllDirectories))
            .Where(path => !string.Equals(path, wrapperPath, StringComparison.Ordinal))
            .Where(path => !string.Equals(
                Path.GetExtension(path),
                ".icns",
                StringComparison.OrdinalIgnoreCase));

        foreach (string path in guardedFiles)
        {
            string contents = File.ReadAllText(path);

            foreach (string value in forbidden)
            {
                Assert.DoesNotContain(value, contents, StringComparison.Ordinal);
            }
        }

        foreach (string path in Directory.EnumerateFiles(githubDirectory, "*", SearchOption.AllDirectories))
        {
            string contents = File.ReadAllText(path);

            Assert.DoesNotContain("127.0.0.1:11434", contents, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("localhost:11434", contents, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ollama serve", contents, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Deterministic_guards_reject_remote_endpoints_and_wrong_image_hashes()
    {
        Assert.True(
            LocalOllamaQualificationGuards.IsLocalOllamaEndpoint(
                "http://127.0.0.1:11434/v1/",
                out Uri? endpoint));
        Assert.Equal("http://127.0.0.1:11434/v1/", endpoint!.AbsoluteUri);
        Assert.False(
            LocalOllamaQualificationGuards.IsLocalOllamaEndpoint(
                "http://example.test:11434/v1/",
                out _));
        Assert.False(
            LocalOllamaQualificationGuards.IsLocalOllamaEndpoint(
                "http://127.0.0.1:0/v1/",
                out _));
        Assert.False(LocalOllamaQualificationGuards.IsLocalOllamaEndpoint(null, out _));

        string root = Path.Combine(Path.GetTempPath(), $"arcanum-ollama-guard-{Guid.NewGuid():N}");
        string path = Path.Combine(root, "candidate.jpg");

        try
        {
            Directory.CreateDirectory(root);
            byte[] bytes = [0xFF, 0xD8, 0xFF, 0x00];
            File.WriteAllBytes(path, bytes);

            string expectedSha256 = Convert.ToHexString(SHA256.HashData(bytes));

            Assert.True(LocalOllamaQualificationGuards.IsQualifiedImage(path, expectedSha256));
            Assert.False(LocalOllamaQualificationGuards.IsQualifiedImage(path, "00"));
            Assert.False(LocalOllamaQualificationGuards.IsQualifiedImage(null, expectedSha256));
            Assert.False(LocalOllamaQualificationGuards.IsQualifiedImage(string.Empty, expectedSha256));
            Assert.False(LocalOllamaQualificationGuards.IsQualifiedImage(root, expectedSha256));
            Assert.False(LocalOllamaQualificationGuards.IsQualifiedImage(
                Path.Combine(root, "missing.jpg"),
                expectedSha256));
            Assert.False(LocalOllamaQualificationGuards.IsQualifiedImage("\0", expectedSha256));
        }
        finally
        {
            LocalOllamaQualificationGuards.DeleteDirectoryWithRetries(root);
        }
    }

    [Fact]
    public void Model_catalog_handler_forbids_redirects_proxies_and_cookies()
    {
        using SocketsHttpHandler handler =
            LocalOllamaQualificationGuards.CreateLoopbackOnlyHttpHandler();

        Assert.False(handler.AllowAutoRedirect);

        Assert.False(handler.UseProxy);

        Assert.Null(handler.Proxy);

        Assert.False(handler.UseCookies);
    }

    [Fact]
    public async Task Model_catalog_reader_enforces_its_byte_cap_without_content_length()
    {
        const int cap = 32;

        using UnknownLengthContent exact = new(new byte[cap]);

        using UnknownLengthContent oversized = new(new byte[cap + 1]);

        Assert.Equal(
            cap,
            (await LocalOllamaQualificationGuards.ReadCappedResponseBytesAsync(
                exact,
                cap,
                CancellationToken.None))!.Length);

        Assert.Null(await LocalOllamaQualificationGuards.ReadCappedResponseBytesAsync(
            oversized,
            cap,
            CancellationToken.None));
    }

    [Fact]
    public void Vision_inputs_are_answer_neutral_and_known_leaks_are_rejected()
    {
        Assert.Equal("opaque-input.dat", LocalOllamaQualificationGuards.VisionAttachmentName);
        Assert.Equal(
            "The current pinned durable-fact file supersedes the older token in this transcript. "
            + "Inspect the explicitly attached image and recall the conversation marker. "
            + "Reply with exactly MARKER=<conversation marker>; TOKEN=<corrected file token>; "
            + "OBJECT=<object>; COLOR=<dominant object color>; SHAPE=<shape>; "
            + "SIDES=<number of sides>; TEXT=<visible text> and no other text.",
            LocalOllamaQualificationGuards.VisionPrompt);
        Assert.False(
            LocalOllamaQualificationGuards.ContainsVisionAnswerLeakage(
                LocalOllamaQualificationGuards.VisionAttachmentName,
                LocalOllamaQualificationGuards.VisionPrompt));
        Assert.False(LocalOllamaQualificationGuards.ContainsVisionAnswerLeakage(
            "The corrected file token is authoritative."));

        string[] leakedInputs =
        [
            "stop-sign.jpg",
            "Describe the sign.",
            "The object is red.",
            "SHAPE=OCTAGON",
            "It is octagonal.",
            "It has eight sides.",
            "SIDES=8",
            "TEXT=STOP",
        ];

        foreach (string leakedInput in leakedInputs)
        {
            Assert.True(LocalOllamaQualificationGuards.ContainsVisionAnswerLeakage(leakedInput));
        }
    }

    [Theory]
    [InlineData("OBJECT=STOP SIGN; COLOR=RED; SHAPE=OCTAGON; SIDES=8; TEXT=STOP", true)]
    [InlineData("OBJECT=Stop Sign; COLOR=Red; SHAPE=Octagon; SIDES=8; TEXT=Stop", true)]
    [InlineData("OBJECT=STOP sign; COLOR=Red; SHAPE=Octagonal; SIDES=8; TEXT=STOP", true)]
    [InlineData("OBJECT=stop sign; COLOR=red; SHAPE=octagon; SIDES=8; TEXT=stop", true)]
    [InlineData("object=STOP SIGN; COLOR=RED; SHAPE=OCTAGON; SIDES=8; TEXT=STOP", false)]
    [InlineData("OBJECT=YIELD SIGN; COLOR=RED; SHAPE=OCTAGON; SIDES=8; TEXT=STOP", false)]
    [InlineData("OBJECT=STOP SIGN; COLOR=RED; SHAPE=OCTAGON; SIDES=8; TEXT=STOP; EXTRA=1", false)]
    public void Vision_answer_preserves_exact_structure_and_opaque_values(
        string semanticFields,
        bool expected)
    {
        const string marker = "MARKER-CaSe";
        const string token = "TOKEN-CaSe";
        string answer = $"MARKER={marker}; TOKEN={token}; {semanticFields}";

        Assert.Equal(
            expected,
            LocalOllamaQualificationGuards.IsExpectedVisionAnswer(answer, marker, token));
        Assert.False(LocalOllamaQualificationGuards.IsExpectedVisionAnswer(answer, marker.ToLowerInvariant(), token));
        Assert.False(LocalOllamaQualificationGuards.IsExpectedVisionAnswer(answer, marker, token.ToLowerInvariant()));
    }

    [Fact]
    public void Deterministic_guards_reject_scripts_and_redact_both_credentials()
    {
        string root = Path.Combine(Path.GetTempPath(), $"arcanum-ollama-guard-{Guid.NewGuid():N}");
        string path = Path.Combine(root, "candidate");

        try
        {
            Directory.CreateDirectory(root);
            Assert.False(LocalOllamaQualificationGuards.IsNativeBinary(null));
            Assert.False(LocalOllamaQualificationGuards.IsNativeBinary(Path.Combine(root, "missing")));
            File.WriteAllText(path, "#!/bin/sh\necho not native\n");
            Assert.False(LocalOllamaQualificationGuards.IsNativeBinary(path));

            File.WriteAllBytes(path, [0xCF, 0xFA, 0xED, 0xFE]);
            Assert.True(LocalOllamaQualificationGuards.IsNativeBinary(path));

            byte[] pe = new byte[68];
            pe[0] = (byte)'M';
            pe[1] = (byte)'Z';
            BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(0x3C), 64);
            pe[64] = (byte)'P';
            pe[65] = (byte)'E';
            File.WriteAllBytes(path, pe);
            Assert.True(LocalOllamaQualificationGuards.IsNativeBinary(path));

            if (!OperatingSystem.IsWindows())
            {
                string link = Path.Combine(root, "candidate-link");
                File.CreateSymbolicLink(link, path);
                Assert.False(LocalOllamaQualificationGuards.IsNativeBinary(link));
            }
        }
        finally
        {
            LocalOllamaQualificationGuards.DeleteDirectoryWithRetries(root);
        }

        string diagnostic = LocalOllamaQualificationGuards.Redact(
            "provider-key master-key",
            "provider-key",
            "master-key");

        Assert.DoesNotContain("provider-key", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("master-key", diagnostic, StringComparison.Ordinal);

        string markerCollision = LocalOllamaQualificationGuards.Redact(
            "redacted",
            "redact",
            masterApiKey: null);

        Assert.DoesNotContain("redact", markerCollision, StringComparison.Ordinal);

        const string overlappingProviderKey = "shared-secret";
        const string overlappingMasterKey = "shared-secret-with-suffix";
        InvalidOperationException original = new(
            $"provider={overlappingProviderKey}; master={overlappingMasterKey}");
        InvalidOperationException sanitized = LocalOllamaQualificationGuards.CreateSanitizedFailure(
            $"Qualification {overlappingMasterKey} failed.",
            original,
            overlappingProviderKey,
            overlappingMasterKey);

        Assert.Null(sanitized.InnerException);
        Assert.DoesNotContain(overlappingProviderKey, sanitized.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(overlappingMasterKey, sanitized.ToString(), StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", sanitized.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("  true  ", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData(null, false)]
    public void Github_actions_guard_is_deterministic(
        string? value,
        bool expected)
    {
        Assert.Equal(expected, LocalOllamaQualificationGuards.IsGitHubActions(value));
    }

    [Fact]
    public void Deterministic_cleanup_removes_owned_temporary_tree()
    {
        string root = Path.Combine(Path.GetTempPath(), $"arcanum-ollama-cleanup-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "nested"));
            File.WriteAllText(Path.Combine(root, "nested", "payload"), "owned");
        }
        finally
        {
            LocalOllamaQualificationGuards.DeleteDirectoryWithRetries(root);
        }

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Deterministic_cleanup_backs_off_between_transient_failures()
    {
        const string root = "owned-temporary-tree";

        bool exists = true;

        int deleteAttempts = 0;

        List<TimeSpan> delays = [];

        LocalOllamaQualificationGuards.DeleteDirectoryWithRetries(
            root,
            _ => exists,
            (_, _) =>
            {
                deleteAttempts++;

                if (deleteAttempts < 3)
                {
                    throw new IOException("transient handle contention");
                }

                exists = false;
            },
            delays.Add);

        Assert.Equal(3, deleteAttempts);

        Assert.Equal(
            [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100)],
            delays);
    }

    private static string FindRepositoryRoot(
        [CallerFilePath] string sourceFile = "")
    {
        DirectoryInfo? directory = new(Path.GetDirectoryName(sourceFile)!);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RetroDownfall.Arcanum.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;

            return false;
        }
    }
}
