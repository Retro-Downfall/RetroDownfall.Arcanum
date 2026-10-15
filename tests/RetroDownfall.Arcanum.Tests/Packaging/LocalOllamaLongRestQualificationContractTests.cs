using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Packaging;

/// <summary>
/// Deterministic contracts for the opt-in Long Rest qualification: its wrapper, its isolation and refusals, the configuration it
/// writes, the corpus it seeds and the arithmetic it reports. None of them contacts Ollama or starts a host.
/// </summary>
public sealed class LocalOllamaLongRestQualificationContractTests
{
    private const string WrapperName = "verify-local-ollama-long-rest.sh";

    private const string QualificationClass = "PublishedArcanumOllamaLongRestQualificationTests";

    private const string ExactQualificationFilter =
        "FullyQualifiedName=RetroDownfall.Arcanum.Tests.Packaging.PublishedArcanumOllamaLongRestQualificationTests."
        + "Published_native_aot_long_rest_sweeps_recovers_a_killed_host_and_leaves_turns_unharmed";

    private static readonly LongRestQualificationOptions Options = new(
        "/published/RetroDownfall.Arcanum.Cli",
        "gemma4:e4b",
        "ornith1.5:35b",
        "embeddinggemma",
        2048,
        new Uri("http://127.0.0.1:11434/v1/"),
        null,
        null);

    private static void RequirePosixScriptFixture() =>
        Skip.If(
            OperatingSystem.IsWindows(),
            "The Bash behavior fixture requires native POSIX process and filesystem semantics.");

    [Fact]
    public void Wrapper_requires_an_existing_executable_and_never_builds_the_product_or_touches_the_operator_home()
    {
        string root = FindRepositoryRoot();

        string wrapperPath = Path.Combine(root, "scripts", WrapperName);

        Assert.True(File.Exists(wrapperPath), $"Missing local qualification wrapper: {wrapperPath}");

        string wrapper = File.ReadAllText(wrapperPath);

        Assert.Contains("set -euo pipefail", wrapper, StringComparison.Ordinal);
        Assert.Contains("--executable", wrapper, StringComparison.Ordinal);
        Assert.Contains("--chat-model", wrapper, StringComparison.Ordinal);
        Assert.Contains("--default-model", wrapper, StringComparison.Ordinal);
        Assert.Contains("--embedding-model", wrapper, StringComparison.Ordinal);
        Assert.Contains("--embedding-window", wrapper, StringComparison.Ordinal);
        Assert.Contains("--endpoint", wrapper, StringComparison.Ordinal);
        Assert.Contains("--report-dir", wrapper, StringComparison.Ordinal);
        Assert.Contains(LongRestQualificationOptions.OptInVariable, wrapper, StringComparison.Ordinal);
        Assert.Contains(ExactQualificationFilter, wrapper, StringComparison.Ordinal);
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
        Assert.Contains("exit \"$final_status\"", wrapper, StringComparison.Ordinal);
        Assert.Contains("could not remove local qualification temporary directory", wrapper, StringComparison.Ordinal);
        Assert.Contains("--blame-hang-timeout 90m", wrapper, StringComparison.Ordinal);
        Assert.Contains("--blame-hang-dump-type none", wrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet publish", wrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("ollama serve", wrapper, StringComparison.Ordinal);
        Assert.DoesNotContain(".config/arcanum", wrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("security ", wrapper, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Wrapper_fails_closed_when_the_exact_test_reports_success_without_a_receipt()
    {
        RequirePosixScriptFixture();

        using PublishedApphostVerificationScriptTests.ScriptFixture fixture = new();

        PublishedApphostVerificationScriptTests.ScriptResult result = await fixture.RunAsync(
            Path.Combine(fixture.RepositoryRoot, "scripts", WrapperName),
            "--executable",
            fixture.Executable);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "selected local qualification test did not execute successfully",
            result.StandardError,
            StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Wrapper_runs_only_the_exact_qualification_passes_it_the_models_and_prints_the_calibration()
    {
        RequirePosixScriptFixture();

        using PublishedApphostVerificationScriptTests.ScriptFixture fixture = new(receiptKind: "ollama-long-rest");

        string reportDirectory = Path.Combine(fixture.Root, "report");

        Directory.CreateDirectory(reportDirectory);

        PublishedApphostVerificationScriptTests.ScriptResult result = await fixture.RunAsync(
            Path.Combine(fixture.RepositoryRoot, "scripts", WrapperName),
            "--executable",
            Path.GetFileName(fixture.Executable),
            "--chat-model",
            "chat-model:1b",
            "--default-model",
            "default-model:2b",
            "--embedding-model",
            "embed-model",
            "--embedding-window",
            "4096",
            "--report-dir",
            reportDirectory);

        Assert.Equal(0, result.ExitCode);

        string dotnetLog = File.ReadAllText(fixture.DotnetLog);

        Assert.Contains($"--filter {ExactQualificationFilter}", dotnetLog, StringComparison.Ordinal);

        string executable = PublishedApphostVerificationScriptTests.ReadLoggedValue(dotnetLog, "published-executable=");

        Assert.True(Path.IsPathFullyQualified(executable));
        Assert.Equal(Path.GetFileName(fixture.Executable), Path.GetFileName(executable));
        Assert.Equal("1", PublishedApphostVerificationScriptTests.ReadLoggedValue(dotnetLog, "long-rest-opt-in="));
        Assert.Equal("chat-model:1b", PublishedApphostVerificationScriptTests.ReadLoggedValue(dotnetLog, "long-rest-chat-model="));
        Assert.Equal("default-model:2b", PublishedApphostVerificationScriptTests.ReadLoggedValue(dotnetLog, "long-rest-default-model="));
        Assert.Equal("embed-model", PublishedApphostVerificationScriptTests.ReadLoggedValue(dotnetLog, "long-rest-embedding-model="));
        Assert.Equal("4096", PublishedApphostVerificationScriptTests.ReadLoggedValue(dotnetLog, "long-rest-embedding-window="));
        Assert.Equal("http://127.0.0.1:11434/v1/", PublishedApphostVerificationScriptTests.ReadLoggedValue(dotnetLog, "long-rest-endpoint="));
        Assert.EndsWith(Path.DirectorySeparatorChar + "report", PublishedApphostVerificationScriptTests.ReadLoggedValue(dotnetLog, "long-rest-results="), StringComparison.Ordinal);

        Assert.Contains("CALIBRATION-REPORT-FIXTURE", result.StandardOutput, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(reportDirectory, "long-rest-calibration.txt")));
    }

    [SkippableFact]
    public async Task Wrapper_accepts_an_exact_windows_style_success_receipt()
    {
        RequirePosixScriptFixture();

        using PublishedApphostVerificationScriptTests.ScriptFixture fixture = new(receiptKind: "ollama-long-rest-crlf");

        PublishedApphostVerificationScriptTests.ScriptResult result = await fixture.RunAsync(
            Path.Combine(fixture.RepositoryRoot, "scripts", WrapperName),
            "--executable",
            fixture.Executable);

        Assert.Equal(0, result.ExitCode);
    }

    [SkippableTheory]
    [InlineData("ollama-long-rest")]
    [InlineData("ollama-long-rest-crlf")]
    public async Task Wrapper_rejects_receipt_bytes_after_the_required_line(string receiptKind)
    {
        RequirePosixScriptFixture();

        using PublishedApphostVerificationScriptTests.ScriptFixture fixture = new(receiptKind);

        fixture.AppendReceiptBytesWithoutANewline();

        PublishedApphostVerificationScriptTests.ScriptResult result = await fixture.RunAsync(
            Path.Combine(fixture.RepositoryRoot, "scripts", WrapperName),
            "--executable",
            fixture.Executable);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("invalid success receipt", result.StandardError, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Wrapper_rejects_the_vision_qualification_receipt()
    {
        RequirePosixScriptFixture();

        using PublishedApphostVerificationScriptTests.ScriptFixture fixture = new(receiptKind: "ollama");

        PublishedApphostVerificationScriptTests.ScriptResult result = await fixture.RunAsync(
            Path.Combine(fixture.RepositoryRoot, "scripts", WrapperName),
            "--executable",
            fixture.Executable);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("selected local qualification test did not execute successfully", result.StandardError, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Wrapper_fails_when_its_private_temporary_directory_cannot_be_removed()
    {
        RequirePosixScriptFixture();

        using PublishedApphostVerificationScriptTests.ScriptFixture fixture = new(receiptKind: "ollama-long-rest");

        fixture.FailRecursiveRemovalFor("arcanum-local-ollama-long-rest.");

        fixture.CaptureBackoffDelays();

        fixture.DisableErrexitBeforeSuccessfulExit("Native AOT Long Rest sweep");

        PublishedApphostVerificationScriptTests.ScriptResult result = await fixture.RunAsync(
            Path.Combine(fixture.RepositoryRoot, "scripts", WrapperName),
            "--executable",
            fixture.Executable);

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

        using PublishedApphostVerificationScriptTests.ScriptFixture fixture = new(receiptKind: "ollama-long-rest");

        fixture.FailSearches();

        PublishedApphostVerificationScriptTests.ScriptResult result = await fixture.RunAsync(
            Path.Combine(fixture.RepositoryRoot, "scripts", WrapperName),
            "--executable",
            fixture.Executable);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("local qualification warning scan failed", result.StandardError, StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData("--endpoint", "http://example.test:11434/v1/", "literal loopback")]
    [InlineData("--endpoint", "http://127.0.0.1:11434/", "literal loopback")]
    [InlineData("--chat-model", "bad model", "model tags")]
    [InlineData("--default-model", "-leading-dash", "model tags")]
    [InlineData("--embedding-model", "tag;rm", "model tags")]
    [InlineData("--embedding-window", "0", "positive token count")]
    [InlineData("--embedding-window", "2k", "positive token count")]
    public async Task Wrapper_rejects_unsafe_options_before_building_anything(string option, string value, string expected)
    {
        RequirePosixScriptFixture();

        using PublishedApphostVerificationScriptTests.ScriptFixture fixture = new(receiptKind: "ollama-long-rest");

        PublishedApphostVerificationScriptTests.ScriptResult result = await fixture.RunAsync(
            Path.Combine(fixture.RepositoryRoot, "scripts", WrapperName),
            "--executable",
            fixture.Executable,
            option,
            value);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains(expected, result.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.DotnetLog), "The wrapper built or ran something before rejecting its arguments.");
    }

    [SkippableFact]
    public async Task Wrapper_refuses_github_actions_and_a_missing_executable_and_report_directory()
    {
        RequirePosixScriptFixture();

        using PublishedApphostVerificationScriptTests.ScriptFixture fixture = new(receiptKind: "ollama-long-rest");

        string wrapper = Path.Combine(fixture.RepositoryRoot, "scripts", WrapperName);

        fixture.EnvironmentOverrides["GITHUB_ACTIONS"] = "true";

        PublishedApphostVerificationScriptTests.ScriptResult actions = await fixture.RunAsync(wrapper, "--executable", fixture.Executable);

        Assert.Equal(2, actions.ExitCode);
        Assert.Contains("cannot run in GitHub Actions", actions.StandardError, StringComparison.Ordinal);

        fixture.EnvironmentOverrides.Remove("GITHUB_ACTIONS");

        PublishedApphostVerificationScriptTests.ScriptResult none = await fixture.RunAsync(wrapper);

        Assert.Equal(2, none.ExitCode);
        Assert.Contains("--executable is required", none.StandardError, StringComparison.Ordinal);

        PublishedApphostVerificationScriptTests.ScriptResult missing = await fixture.RunAsync(
            wrapper,
            "--executable",
            Path.Combine(fixture.Root, "no-such-apphost"));

        Assert.Equal(2, missing.ExitCode);

        PublishedApphostVerificationScriptTests.ScriptResult report = await fixture.RunAsync(
            wrapper,
            "--executable",
            fixture.Executable,
            "--report-dir",
            Path.Combine(fixture.Root, "no-such-directory"));

        Assert.Equal(2, report.ExitCode);
        Assert.Contains("--report-dir must name an existing writable directory", report.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void Qualification_is_compiled_but_skips_without_exact_local_opt_in_and_keeps_the_isolation_of_the_vision_qualification()
    {
        string root = FindRepositoryRoot();

        string packaging = Path.Combine(root, "tests", "RetroDownfall.Arcanum.Tests", "Packaging");

        string test = File.ReadAllText(Path.Combine(packaging, $"{QualificationClass}.cs"));

        string run = string.Concat(
            Directory.EnumerateFiles(packaging, "LongRestQualificationRun*.cs").Order(StringComparer.Ordinal).Select(File.ReadAllText));

        string host = File.ReadAllText(Path.Combine(packaging, "LongRestQualificationHost.cs"));

        string grimoire = File.ReadAllText(Path.Combine(packaging, "LongRestQualificationGrimoire.cs"));

        string proxy = File.ReadAllText(Path.Combine(packaging, "LongRestQualificationProxy.cs"));

        Assert.Contains("[SkippableFact]", test, StringComparison.Ordinal);
        Assert.Contains("[Trait(\"Category\", \"LocalOllama\")]", test, StringComparison.Ordinal);
        Assert.Contains("Skip.IfNot", test, StringComparison.Ordinal);
        Assert.Contains("string.Equals(optIn, \"1\", StringComparison.Ordinal)", test, StringComparison.Ordinal);
        Assert.Contains("Real-model qualification is local-only", test, StringComparison.Ordinal);

        const string githubActionsGuard = "LocalOllamaQualificationGuards.IsGitHubActions";

        Assert.True(
            test.IndexOf(githubActionsGuard, StringComparison.Ordinal) is var guard and >= 0
            && guard < test.IndexOf("LongRestQualificationOptions.Read", StringComparison.Ordinal)
            && guard < test.IndexOf("AssertModelsAreInstalledAsync(options)", StringComparison.Ordinal),
            "The qualification must reject GitHub Actions before reading its inputs or contacting Ollama.");

        Assert.Contains("[Collection(LocalNativeAotOllamaQualificationCollection.Name)]", test, StringComparison.Ordinal);
        Assert.Contains("AssertNativeAotPublish", test, StringComparison.Ordinal);
        Assert.Contains("LocalOllamaQualificationGuards.IsNativeBinary", test, StringComparison.Ordinal);
        Assert.Contains("PublishedTestProcessIsolation.Create(\"ollama-long-rest\")", test, StringComparison.Ordinal);
        Assert.Contains("isolation.Delete()", test, StringComparison.Ordinal);
        Assert.Contains("AggregateException", test, StringComparison.Ordinal);
        Assert.Contains("cleanupFailures.Insert(0, primaryFailure)", test, StringComparison.Ordinal);
        Assert.Contains("AssertNoPlaintextCredentialFiles", test, StringComparison.Ordinal);
        Assert.Contains("LongRestQualificationOptions.SuccessReceipt", test, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet publish", test + run + host, StringComparison.Ordinal);
        Assert.DoesNotContain("ollama serve", test + run + host + proxy, StringComparison.OrdinalIgnoreCase);

        // The same synthetic-credential, testing-environment, rebased-home posture as the vision qualification, never the keychain.
        Assert.Contains("ARCANUM_TEST_IN_MEMORY_CREDENTIALS", host, StringComparison.Ordinal);
        Assert.Contains("\"ARCANUM_TEST_HOME\"", host, StringComparison.Ordinal);
        Assert.Contains("\"DOTNET_ENVIRONMENT\"] = \"Testing\"", host, StringComparison.Ordinal);
        Assert.Contains("\"ASPNETCORE_ENVIRONMENT\"] = \"Testing\"", host, StringComparison.Ordinal);
        Assert.Contains("start.Environment.Clear()", host, StringComparison.Ordinal);
        Assert.Contains("Isolation.ApplyTemporaryEnvironment(start)", host, StringComparison.Ordinal);
        Assert.Contains("\"HOME\"] = TestHome", host, StringComparison.Ordinal);

        // The Grimoire reader opens only the isolated home it is given, read-only, and never the operating-system credential store.
        Assert.Contains("SqliteOpenMode.ReadOnly", grimoire, StringComparison.Ordinal);
        Assert.Contains("configurationDirectory", grimoire, StringComparison.Ordinal);
        Assert.DoesNotContain("ArcanumPaths", grimoire, StringComparison.Ordinal);
        Assert.DoesNotContain("OsCredentialStore", grimoire, StringComparison.Ordinal);
        Assert.DoesNotContain("IOsCredentialStore", grimoire, StringComparison.Ordinal);

        // The forwarder reaches only the one validated literal-loopback endpoint, without redirects or an ambient proxy.
        Assert.Contains("LocalOllamaQualificationGuards.CreateLoopbackOnlyHttpHandler()", proxy, StringComparison.Ordinal);
        Assert.Contains("IPAddress.Loopback", proxy, StringComparison.Ordinal);

        Assert.Contains("SIGKILL", run, StringComparison.Ordinal);
        Assert.Contains("KillNow()", run, StringComparison.Ordinal);
        Assert.Contains("RunCleanSweepAsync", test, StringComparison.Ordinal);
        Assert.Contains("RunKilledHostSweepAsync", test, StringComparison.Ordinal);
        Assert.Contains("RunOrdinaryTurnAsync", test, StringComparison.Ordinal);
    }

    [Fact]
    public void Continuous_integration_never_enables_or_invokes_the_long_rest_qualification()
    {
        string root = FindRepositoryRoot();

        string githubDirectory = Path.Combine(root, ".github");

        string scriptsDirectory = Path.Combine(root, "scripts");

        string wrapperPath = Path.Combine(scriptsDirectory, WrapperName);

        string[] forbidden = [LongRestQualificationOptions.OptInVariable, QualificationClass, WrapperName];

        IEnumerable<string> guardedFiles = Directory
            .EnumerateFiles(githubDirectory, "*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(scriptsDirectory, "*", SearchOption.AllDirectories))
            .Where(path => !string.Equals(path, wrapperPath, StringComparison.Ordinal))
            .Where(path => !string.Equals(Path.GetExtension(path), ".icns", StringComparison.OrdinalIgnoreCase));

        foreach (string path in guardedFiles)
        {
            string contents = File.ReadAllText(path);

            foreach (string value in forbidden)
            {
                Assert.DoesNotContain(value, contents, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Options_default_to_the_documented_models_window_and_endpoint_and_reject_unsafe_values()
    {
        Dictionary<string, string> environment = new(StringComparer.Ordinal)
        {
            [LongRestQualificationOptions.ExecutableVariable] = "/published/arcanum",
        };

        LongRestQualificationOptions options = LongRestQualificationOptions.Read(name => environment.GetValueOrDefault(name));

        Assert.Equal("gemma4:e4b", options.ChatModel);
        Assert.Equal("ornith1.5:35b", options.DefaultModel);
        Assert.Equal("embeddinggemma", options.EmbeddingModel);
        Assert.Equal(2048, options.EmbeddingWindow);
        Assert.Equal("http://127.0.0.1:11434/v1/", options.Endpoint.AbsoluteUri);
        Assert.Null(options.ResultsDirectory);
        Assert.Null(options.ReceiptPath);

        foreach ((string variable, string value) in new[]
        {
            (LongRestQualificationOptions.EndpointVariable, "http://example.test:11434/v1/"),
            (LongRestQualificationOptions.EndpointVariable, "https://127.0.0.1:11434/v1/"),
            (LongRestQualificationOptions.EndpointVariable, "http://user:pw@127.0.0.1:11434/v1/"),
            (LongRestQualificationOptions.ChatModelVariable, "bad model"),
            (LongRestQualificationOptions.DefaultModelVariable, "tag\nnewline"),
            (LongRestQualificationOptions.EmbeddingWindowVariable, "0"),
            (LongRestQualificationOptions.EmbeddingWindowVariable, "-5"),
            (LongRestQualificationOptions.EmbeddingWindowVariable, "abc"),
        })
        {
            Dictionary<string, string> unsafeEnvironment = new(environment, StringComparer.Ordinal) { [variable] = value };

            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
                () => LongRestQualificationOptions.Read(name => unsafeEnvironment.GetValueOrDefault(name)));

            Assert.Contains(variable, failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(value.Trim(), failure.Message, StringComparison.Ordinal);
        }

        Assert.Throws<InvalidOperationException>(() => LongRestQualificationOptions.Read(static _ => null));
    }

    [Fact]
    public void Configuration_uses_two_provider_entries_so_the_embedding_window_is_the_embedding_models_own()
    {
        // The validator requires the workspace to exist.
        DirectoryInfo workspace = Directory.CreateTempSubdirectory("arcanum-long-rest-config-");

        try
        {
            AssertConfiguration(workspace.FullName);
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    private static void AssertConfiguration(string workspace)
    {
        ArcanumSettings settings = LongRestQualificationConfiguration.Create(
            Options,
            hostPort: 51234,
            new Uri("http://127.0.0.1:50001/v1/"),
            workspace,
            sagaExtraction: false,
            longRest: true).Arcanum;

        ProviderSettings chat = Assert.Single(settings.Providers, static provider => provider.Name == "Ollama");

        ProviderSettings embeddings = Assert.Single(settings.Providers, static provider => provider.Name == "Ollama Embeddings");

        Assert.Equal(2, settings.Providers.Length);
        Assert.Equal("http://127.0.0.1:50001/v1/", chat.Endpoint);
        Assert.Equal(chat.Endpoint, embeddings.Endpoint);
        Assert.Equal(131_072, chat.ContextWindowLimit);
        Assert.Equal(2048, embeddings.ContextWindowLimit);
        Assert.Equal(["gemma4:e4b", "ornith1.5:35b"], chat.Models.Select(static model => model.Name));
        Assert.Equal(["embeddinggemma"], embeddings.Models.Select(static model => model.Name));
        Assert.NotEqual(
            chat.CredentialEnvironmentVariable,
            embeddings.CredentialEnvironmentVariable,
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal("ornith1.5:35b", settings.DefaultModel);
        Assert.Equal("gemma4:e4b", settings.FastModel);
        Assert.Equal("Ollama Embeddings", settings.Integrations.Embeddings.Provider);
        Assert.Equal("embeddinggemma", settings.Integrations.Embeddings.Model);
        Assert.Equal(768, settings.Integrations.Embeddings.Dimensions);
        Assert.True(settings.Features.Saga);
        Assert.True(settings.Features.Embeddings);
        Assert.True(settings.Features.Annals);
        Assert.True(settings.Features.LongRest);
        Assert.True(settings.Features.Metrics);
        Assert.False(settings.Features.SagaExtraction);
        Assert.False(settings.Features.Covenant);
        Assert.False(settings.Features.CampaignScopedMemory);
        Assert.False(settings.Host.ListenAny);
        Assert.True(settings.Cost.Budget.Enabled);
        Assert.Equal(10_000m, settings.Cost.Budget.DailyLimitUsd);
        Assert.Equal(1000m, settings.Cost.Pricing.DefaultPricing.InputPer1M);
        Assert.Equal(250m, settings.Cost.Pricing.DefaultPricing.CachedPer1M);

        Result validation = new ConfigurationValidator().Validate(settings);

        Assert.True(
            validation.IsSuccess,
            validation.IsSuccess ? string.Empty : string.Join("; ", (validation.Error.Details ?? []).Select(static detail => $"{detail.Pointer}: {detail.Detail}")));

        Assert.Empty(new ConfigurationValidator().ValidateLongRest(settings));

        Result<LongRestRecipeIdentity> recipe = LongRestRecipeResolver.Resolve(settings);

        Assert.True(recipe.IsSuccess);
        Assert.Equal("Ollama", recipe.Value.SummaryProvider);
        Assert.Equal("gemma4:e4b", recipe.Value.SummaryModel);
        Assert.Equal("Ollama Embeddings", recipe.Value.EmbeddingProvider);
        Assert.Equal(1766, recipe.Value.SummaryUtf8Bytes);

        ArcanumSettings seeding = LongRestQualificationConfiguration.Create(
            Options,
            51234,
            new Uri("http://127.0.0.1:50001/v1/"),
            workspace,
            sagaExtraction: true,
            longRest: false).Arcanum;

        Assert.True(seeding.Features.SagaExtraction);
        Assert.False(seeding.Features.LongRest);
    }

    [Fact]
    public void Configuration_with_the_embedding_model_on_the_chat_entry_would_misstate_the_summary_bound()
    {
        ArcanumSettings settings = LongRestQualificationConfiguration.Create(
            Options,
            51234,
            new Uri("http://127.0.0.1:50001/v1/"),
            "/workspace",
            sagaExtraction: false,
            longRest: true).Arcanum;

        // The reason the qualification writes a second entry: a shared entry hands the embedding model the chat window.
        ProviderSettings chat = settings.Providers.Single(static provider => provider.Name == "Ollama");

        chat.Models = [.. chat.Models, new ModelEntry("embeddinggemma")];

        settings.Integrations.Embeddings.Provider = "Ollama";

        Result<LongRestRecipeIdentity> shared = LongRestRecipeResolver.Resolve(settings);

        Assert.True(shared.IsSuccess);
        Assert.Equal(LongRestDiscoveryLimits.SummaryUtf8Bytes, shared.Value.SummaryUtf8Bytes);
    }

    [Fact]
    public void Corpus_is_labelled_so_that_no_session_holds_two_facts_of_one_cluster_and_the_operator_roles_sit_inside_clusters()
    {
        IReadOnlyList<LongRestSeedFact> facts = LongRestQualificationCorpus.Facts;

        Assert.InRange(facts.Count, 30, 60);

        foreach (IGrouping<int, LongRestSeedFact> session in facts.GroupBy(static fact => fact.Session))
        {
            string[] clustered = [.. session.Where(static fact => fact.Label != LongRestQualificationCorpus.SingleLabel).Select(static fact => fact.Label)];

            Assert.Equal(clustered.Length, clustered.Distinct(StringComparer.Ordinal).Count());
        }

        Assert.Equal(Enumerable.Range(1, LongRestQualificationCorpus.SessionCount), facts.Select(static fact => fact.Session).Distinct().Order());

        Assert.Equal("dog", LongRestQualificationCorpus.RetiredFact.Label);
        Assert.Equal("coffee", LongRestQualificationCorpus.PinnedFact.Label);
        Assert.True(facts.Count(static fact => fact.Label == "dog") >= 4);
        Assert.True(facts.Count(static fact => fact.Label == "coffee") >= 4);

        // Two exact-duplicate pairs, unrelated singles, and confusable neighbours.
        Assert.Equal(2, facts.Count(static fact => fact.Label == "teal"));
        Assert.Single(facts.Where(static fact => fact.Label == "teal").Select(static fact => fact.Text).Distinct());
        Assert.Equal(2, facts.Count(static fact => fact.Label == "stagingdb"));
        Assert.True(facts.Count(static fact => fact.Label == LongRestQualificationCorpus.SingleLabel) >= 5);
        Assert.Contains(facts, static fact => fact.Label == "cat");
        Assert.Contains(facts, static fact => fact.Label == "python");
        Assert.Contains(facts, static fact => fact.Label == "csharp");

        Assert.Equal(1, facts.Count(static fact => fact.Role == LongRestCorpusRole.Retired));
        Assert.Equal(1, facts.Count(static fact => fact.Role == LongRestCorpusRole.Pinned));

        string prompt = LongRestQualificationCorpus.PromptFor(1);

        Assert.Contains("just reply with the single word Noted", prompt, StringComparison.Ordinal);
        Assert.All(LongRestQualificationCorpus.FactsOfSession(1), fact => Assert.Contains(fact.Text, prompt, StringComparison.Ordinal));
    }

    [Fact]
    public void Corpus_matching_pairs_restated_memories_with_their_facts_and_leaves_inventions_unmatched()
    {
        LongRestSeedFact[] told =
        [
            new("dog", "The user's dog is a beagle named Biscuit.", 1),
            new("cat", "The user's cat is a grey tabby named Mochi.", 1),
            new("peanuts", "The user is allergic to peanuts.", 1),
            new("single", "The user wears size 43 shoes.", 1),
        ];

        IReadOnlyList<LongRestSeededMemory> matched = LongRestQualificationCorpus.Match(
            [
                ("m1", "The user owns a beagle named Biscuit."),
                ("m2", "The user has a grey tabby cat named Mochi."),
                ("m3", "Something entirely different about invoices."),
                ("m4", "The user is allergic to peanuts."),
            ],
            told);

        Assert.Equal(["dog", "cat", "unmatched", "peanuts"], matched.Select(static memory => memory.Label));
        Assert.Same(told[0], matched[0].Fact);
        Assert.Null(matched[2].Fact);

        // A fact is paired at most once: two restatements of one fact leave the second unmatched.
        IReadOnlyList<LongRestSeededMemory> duplicated = LongRestQualificationCorpus.Match(
            [("a", "The user is allergic to peanuts."), ("b", "The user is allergic to peanuts.")],
            told);

        Assert.Equal(1, duplicated.Count(static memory => memory.Fact is not null));

        Assert.Empty(LongRestQualificationCorpus.Match([], told));
        Assert.All(LongRestQualificationCorpus.Match([("x", "word")], []), static memory => Assert.Null(memory.Fact));
    }

    [Fact]
    public void Corpus_roles_follow_the_told_fact()
    {
        IReadOnlyList<LongRestSeededMemory> matched = LongRestQualificationCorpus.Match(
            [("r", "Biscuit, a beagle, is the user's loyal dog and walking companion."), ("p", "The user always orders black coffee with no sugar.")],
            [LongRestQualificationCorpus.RetiredFact, LongRestQualificationCorpus.PinnedFact]);

        Assert.Equal(LongRestCorpusRole.Retired, matched[0].Role);
        Assert.Equal(LongRestCorpusRole.Pinned, matched[1].Role);
    }

    [Theory]
    [InlineData("/v1/chat/completions", "{\"model\":\"m\",\"response_format\":{\"type\":\"json_schema\",\"json_schema\":{\"name\":\"long_rest_summary\"}}}", "Summary")]
    [InlineData("/v1/chat/completions", "{\"model\":\"m\",\"response_format\":{\"type\":\"json_schema\",\"json_schema\":{\"name\":\"long_rest_adjudication\"}}}", "Adjudication")]
    [InlineData("/v1/chat/completions", "{\"model\":\"m\",\"messages\":[]}", "Chat")]
    [InlineData("/v1/chat/completions", "{\"model\":\"m\",\"response_format\":{\"type\":\"json_object\"}}", "Chat")]
    [InlineData("/v1/embeddings", "{\"model\":\"e\",\"input\":\"x\"}", "Embedding")]
    [InlineData("/v1/models", null, "Models")]
    [InlineData("/v1/something", "{}", "Other")]
    public void Forwarder_tells_a_summary_from_an_adjudication_from_the_hosts_other_calls(string path, string? body, string expected)
    {
        using JsonDocument? document = body is null ? null : JsonDocument.Parse(body);

        Assert.Equal(Enum.Parse<LongRestProviderRequestKind>(expected), LongRestQualificationProxy.Classify(path, document?.RootElement));
    }

    [Fact]
    public void Forwarder_reads_the_text_the_provider_was_shown_without_json_escaping()
    {
        using JsonDocument chat = JsonDocument.Parse(
            "{\"messages\":[{\"role\":\"system\",\"content\":\"be brief\"},{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"The user\\u0027s dog\"}]}]}");

        string text = LongRestQualificationProxy.ProviderVisibleText(chat.RootElement);

        Assert.Contains("be brief", text, StringComparison.Ordinal);
        Assert.Contains("The user's dog", text, StringComparison.Ordinal);

        using JsonDocument embedding = JsonDocument.Parse("{\"input\":[\"first\",\"second\"]}");

        Assert.Contains("second", LongRestQualificationProxy.ProviderVisibleText(embedding.RootElement), StringComparison.Ordinal);

        Assert.Equal(string.Empty, LongRestQualificationProxy.ProviderVisibleText(null));
    }

    [Fact]
    public void Forwarder_reads_the_providers_usage_from_a_response_and_from_the_last_chunk_of_a_stream()
    {
        Assert.Equal(
            (472L, 434L),
            LongRestQualificationProxy.ReadUsage(
                Encoding.UTF8.GetBytes("{\"usage\":{\"prompt_tokens\":472,\"completion_tokens\":434,\"total_tokens\":906}}"),
                streaming: false));

        Assert.Equal(
            (21L, null),
            LongRestQualificationProxy.ReadUsage(Encoding.UTF8.GetBytes("{\"usage\":{\"prompt_tokens\":21}}"), streaming: false));

        string stream = "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n"
            + "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":9,\"completion_tokens\":3}}\n\n"
            + "data: [DONE]\n\n";

        Assert.Equal((9L, 3L), LongRestQualificationProxy.ReadUsage(Encoding.UTF8.GetBytes(stream), streaming: true));

        Assert.Equal(1364L, LongRestQualificationProxy.ReadCachedTokens(Encoding.UTF8.GetBytes("{\"usage\":{\"prompt_tokens\":1369,\"prompt_tokens_details\":{\"cached_tokens\":1364},\"completion_tokens\":9}}"), streaming: false));
        Assert.Null(LongRestQualificationProxy.ReadCachedTokens(Encoding.UTF8.GetBytes("{\"usage\":{\"prompt_tokens\":21}}"), streaming: false));
        Assert.Equal(7L, LongRestQualificationProxy.ReadCachedTokens(Encoding.UTF8.GetBytes("data: {\"usage\":{\"prompt_tokens_details\":{\"cached_tokens\":7}}}\n\ndata: [DONE]\n\n"), streaming: true));

        Assert.Equal("length", LongRestQualificationProxy.ReadFinishReason(Encoding.UTF8.GetBytes("{\"choices\":[{\"finish_reason\":\"length\",\"message\":{}}]}")));
        Assert.Equal("stop", LongRestQualificationProxy.ReadFinishReason(Encoding.UTF8.GetBytes("{\"choices\":[{\"finish_reason\":\"stop\"}]}")));
        Assert.Null(LongRestQualificationProxy.ReadFinishReason(Encoding.UTF8.GetBytes("{\"choices\":[]}")));
        Assert.Null(LongRestQualificationProxy.ReadFinishReason(Encoding.UTF8.GetBytes("not json")));

        Assert.Equal((null, null), LongRestQualificationProxy.ReadUsage([], streaming: false));
        Assert.Equal((null, null), LongRestQualificationProxy.ReadUsage(Encoding.UTF8.GetBytes("not json"), streaming: false));
    }

    [Theory]
    [InlineData("{\"kind\":\"NoChange\",\"survivorVersionId\":null}", "valid NoChange")]
    [InlineData("{\"kind\":\"EquivalentObservations\",\"survivorVersionId\":null}", "valid EquivalentObservations")]
    [InlineData("{\"kind\":\"EquivalentObservations\",\"survivorVersionId\":\"e3783769-efaf-482e-b247-ee20691725dc\"}", "invalid: EquivalentObservations names a survivor")]
    [InlineData("{\"kind\":\"Supersession\",\"survivorVersionId\":\"e3783769-efaf-482e-b247-ee20691725dc\"}", "valid Supersession")]
    [InlineData("{\"kind\":\"Supersession\",\"survivorVersionId\":\"11111111-1111-1111-1111-111111111111\"}", "invalid: Supersession without a listed survivor")]
    [InlineData("{\"kind\":\"Supersession\",\"survivorVersionId\":null}", "invalid: Supersession without a listed survivor")]
    [InlineData("{\"kind\":\"Merge\",\"survivorVersionId\":null}", "invalid: unknown kind")]
    [InlineData("{\"kind\":\"NoCha", "invalid: not JSON")]
    public void Answer_grading_applies_the_rule_the_adjudication_prompt_states(string answer, string expected)
    {
        LongRestRecordedRequest request = new()
        {
            Kind = LongRestProviderRequestKind.Adjudication,
            ProviderVisibleText = "Memory 0125f9cf-684e-4371-8f0e-bc1090f71e0e\nVersion e3783769-efaf-482e-b247-ee20691725dc\nContentHash AA\nA fact.\n",
            ResponseContent = answer,
        };

        Assert.Equal(expected, LongRestQualificationRun.GradeAdjudication(request));
    }

    [Fact]
    public void Answer_grading_reports_a_cut_off_answer_a_missing_answer_and_an_oversized_summary()
    {
        Assert.Equal(
            "invalid: cut off at the output cap",
            LongRestQualificationRun.GradeAdjudication(new LongRestRecordedRequest { ResponseContent = "{\"kind\":\"No", FinishReason = "length" }));

        Assert.Equal("invalid: no content", LongRestQualificationRun.GradeAdjudication(new LongRestRecordedRequest()));
        Assert.Equal("invalid: no content", LongRestQualificationRun.GradeSummary(null, 1766));
        Assert.Equal("valid", LongRestQualificationRun.GradeSummary("{\"summary\":\"short\"}", 1766));
        Assert.Equal("valid but over the byte bound", LongRestQualificationRun.GradeSummary("{\"summary\":\"" + new string('x', 2000) + "\"}", 1766));
        Assert.Equal("invalid: no summary", LongRestQualificationRun.GradeSummary("{\"text\":\"x\"}", 1766));
        Assert.Equal("invalid: not JSON", LongRestQualificationRun.GradeSummary("plain text", 1766));

        Assert.Equal("hi", LongRestQualificationProxy.ReadMessageContent(Encoding.UTF8.GetBytes("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"hi\"}}]}")));
        Assert.Null(LongRestQualificationProxy.ReadMessageContent(Encoding.UTF8.GetBytes("{\"choices\":[]}")));
        Assert.Equal(16_384, LongRestQualificationProxy.ReadMessageContent(Encoding.UTF8.GetBytes("{\"choices\":[{\"message\":{\"content\":\"" + new string('y', 20_000) + "\"}}]}"))!.Length);
    }

    [Fact]
    public void Pack_grading_reads_the_memories_an_adjudication_request_names()
    {
        string request = "Compare the supplied exact observations.\nMemory 0125f9cf-684e-4371-8f0e-bc1090f71e0e\nVersion 54017d53-070d-4d68-ad82-b7a583a1746d\n"
            + "ContentHash B6F6\nThe user owns a grey tabby cat named Mochi.\n\nMemory 0bc1ab43-124a-44c6-ae12-a7efcfee9873\nVersion 1a9c6943-8da5-4605-bc64-b2f47a6eef45\n"
            + "ContentHash AA00\nMemory of the user's childhood home.\n";

        Assert.Equal(
            ["0125f9cf-684e-4371-8f0e-bc1090f71e0e", "0bc1ab43-124a-44c6-ae12-a7efcfee9873"],
            LongRestQualificationRun.ReadPackMemories(request));

        Assert.Empty(LongRestQualificationRun.ReadPackMemories("Observation 01\nA summary child.\n"));
    }

    [Fact]
    public void Api_reads_operation_states_written_as_numbers_or_names_and_the_prometheus_text_format()
    {
        using JsonDocument numeric = JsonDocument.Parse(
            "{\"id\":\"6f6c6f0f-0f0f-4f0f-8f0f-0f0f0f0f0f0f\",\"state\":7,\"terminalErrorCode\":\"long_rest.interrupted\",\"attemptCount\":1,\"publicSummary\":\"p\",\"revision\":3,\"completedAt\":null}");

        LongRestOperationSnapshot parked = LongRestQualificationApi.ReadOperation(numeric.RootElement);

        Assert.Equal(LongRestOperationState.ReconciliationRequired, parked.State);
        Assert.Equal("long_rest.interrupted", parked.TerminalErrorCode);
        Assert.False(parked.IsTerminal);

        using JsonDocument named = JsonDocument.Parse(
            "{\"id\":\"6f6c6f0f-0f0f-4f0f-8f0f-0f0f0f0f0f0f\",\"state\":\"Completed\",\"terminalErrorCode\":null,\"attemptCount\":2,\"publicSummary\":\"\",\"revision\":9,\"completedAt\":\"2026-10-15T13:00:00+00:00\"}");

        LongRestOperationSnapshot completed = LongRestQualificationApi.ReadOperation(named.RootElement);

        Assert.True(completed.IsTerminal);
        Assert.NotNull(completed.CompletedAt);

        IReadOnlyDictionary<string, double> metrics = LongRestQualificationApi.ParseMetrics(
            "# HELP arcanum_long_rest_sweep_parked_total x\n# TYPE x counter\narcanum_long_rest_sweep_parked_total{class=\"resume_later\"} 2\narcanum_other 1.5\nmalformed\n");

        Assert.Equal(2, metrics["arcanum_long_rest_sweep_parked_total{class=\"resume_later\"}"]);
        Assert.Equal(1.5, metrics["arcanum_other"]);
        Assert.Equal(2, metrics.Count);
    }

    [Fact]
    public void Calibration_separates_true_pairs_from_other_pairs_with_the_products_own_lexical_score()
    {
        CalibrationMemory[] corpus =
        [
            Memory("a1", "dog", "The user's dog is a beagle named Biscuit.", 1f, 0f),
            Memory("a2", "dog", "The user owns a beagle called Biscuit.", 0.98f, 0.1f),
            Memory("a3", "dog", "Biscuit, the user's beagle, is the family dog.", 0.97f, 0.12f),
            Memory("b1", "cat", "The user's cat is a grey tabby named Mochi.", 0f, 1f),
            Memory("b2", "cat", "The user has a grey tabby cat called Mochi.", 0.1f, 0.98f),
            Memory("c1", LongRestQualificationCorpus.SingleLabel, "The internal build server listens on port 8443.", -1f, 0f),
            Memory("c2", LongRestQualificationCorpus.SingleLabel, "The user wears size 43 shoes.", 0f, -1f),
        ];

        CalibrationReport report = LongRestQualificationCalibration.Calibrate(corpus, [], new Dictionary<string, string?>(), 1766);

        Assert.Equal(7, report.CorpusMemories);
        Assert.Equal(2, report.LabelledClusters);
        Assert.True(report.Lexical.Within.Count > 0);
        Assert.True(report.Lexical.Cross.Count > 0);
        Assert.True(report.Lexical.Within.Median > report.Lexical.Cross.Median);
        Assert.True(report.Vector.Within.Median > 0.9);
        Assert.True(report.Vector.Cross.Median < 0.5);
        Assert.Equal(3 + 1, report.Vector.Within.Count);
        Assert.Equal(21 - 4, report.Vector.Cross.Count);
        Assert.Equal(0.92, report.Vector.ConfiguredThreshold);
        Assert.Equal(12, report.Lexical.ConfiguredThreshold);
        Assert.Null(report.SummaryGate);

        // Singles never pair with one another: an unrelated memory is no cluster.
        Assert.False(LongRestQualificationCalibration.SameCluster(corpus[5], corpus[6]));
        Assert.True(LongRestQualificationCalibration.SameCluster(corpus[0], corpus[1]));

        string text = LongRestQualificationCalibration.RenderText(report, Options);

        Assert.Contains("LONG REST CALIBRATION REPORT", text, StringComparison.Ordinal);
        Assert.Contains("<- configured", text, StringComparison.Ordinal);
        Assert.Contains("Recommendation", text, StringComparison.Ordinal);

        using JsonDocument json = JsonDocument.Parse(LongRestQualificationCalibration.RenderJson(report));

        Assert.Equal(7, json.RootElement.GetProperty("corpusMemories").GetInt32());
        Assert.Equal(12, json.RootElement.GetProperty("lexical").GetProperty("configuredThreshold").GetDouble());
    }

    [Fact]
    public void Calibration_measures_summaries_against_their_children_and_against_foreign_summaries()
    {
        CalibrationMemory[] corpus =
        [
            Memory("m1", "dog", "The user's dog is a beagle named Biscuit.", 1f, 0f),
            Memory("m2", "dog", "The user owns a beagle called Biscuit.", 0.99f, 0.05f),
            Memory("m3", "cat", "The user's cat is a grey tabby named Mochi.", 0f, 1f),
            Memory("m4", "cat", "The user has a grey tabby cat called Mochi.", 0.05f, 0.99f),
        ];

        CalibrationSummary[] summaries =
        [
            new("s-dog", 1, "A beagle named Biscuit.", [1f, 0.02f], ["n1", "n2"]),
            new("s-cat", 1, "A grey tabby named Mochi.", [0.02f, 1f], ["n3", "n4"]),
            new("s-top", 2, "Pets.", [0.7f, 0.7f], ["s-dog", "s-cat"]),
        ];

        Dictionary<string, string?> leaves = new()
        {
            ["n1"] = "m1",
            ["n2"] = "m2",
            ["n3"] = "m3",
            ["n4"] = "m4",
        };

        CalibrationReport report = LongRestQualificationCalibration.Calibrate(corpus, summaries, leaves, 1766);

        CalibrationSummaryGate gate = Assert.IsType<CalibrationSummaryGate>(report.SummaryGate);

        Assert.Equal(3, gate.SummaryCount);
        Assert.Equal(0.92, gate.ConfiguredGate);
        Assert.Equal(6, gate.OwnChildren.Count);
        Assert.True(gate.OwnLeavesAtOrAboveGate >= 4);
        Assert.Equal(0, gate.ForeignSummariesAtOrAboveGate);
        Assert.Equal(2, gate.ForeignSummaries.Count);
        Assert.Contains(gate.Composition, static line => line.Contains("dogx2", StringComparison.Ordinal));
        Assert.Contains(gate.Composition, static line => line.Contains("layer 2", StringComparison.Ordinal) && line.Contains("covers 4 memories", StringComparison.Ordinal));
        Assert.Equal(summaries.Select(static summary => Encoding.UTF8.GetByteCount(summary.Text)), gate.SummaryBytes);
    }

    [Fact]
    public void Calibration_distributions_and_thresholds_handle_empty_and_degenerate_input()
    {
        CalibrationDistribution empty = CalibrationDistribution.Of([]);

        Assert.Equal(0, empty.Count);
        Assert.True(double.IsNaN(empty.Median));

        CalibrationDistribution one = CalibrationDistribution.Of([4d]);

        Assert.Equal(4, one.Minimum);
        Assert.Equal(4, one.P90);

        CalibrationDistribution ten = CalibrationDistribution.Of([.. Enumerable.Range(1, 10).Select(static value => (double)value)]);

        Assert.Equal(5.5, ten.Median);
        Assert.Equal(1, ten.Minimum);
        Assert.Equal(10, ten.Maximum);

        CalibrationReport report = LongRestQualificationCalibration.Calibrate(
            [Memory("only", "dog", "The user's dog is a beagle.", 1f, 0f)],
            [],
            new Dictionary<string, string?>(),
            1766);

        Assert.Equal(0, report.Vector.Within.Count);
        Assert.Equal(0, report.Lexical.Cross.Count);
        Assert.Contains("no pairs", LongRestQualificationCalibration.RenderText(report, Options), StringComparison.Ordinal);

        using JsonDocument json = JsonDocument.Parse(LongRestQualificationCalibration.RenderJson(report));

        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("vector").GetProperty("within").GetProperty("median").ValueKind);
    }

    [Fact]
    public async Task Grimoire_diagnostic_dump_accepts_only_a_select()
    {
        LongRestQualificationGrimoire grimoire = (LongRestQualificationGrimoire)RuntimeHelpers.GetUninitializedObject(typeof(LongRestQualificationGrimoire));

        await Assert.ThrowsAsync<ArgumentException>(() => grimoire.DumpAsync("DELETE FROM saga_memories", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => grimoire.DumpAsync("  UPDATE saga_memories SET Content = ''", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => grimoire.CountAsync("saga_memories", CancellationToken.None));
    }

    [Fact]
    public async Task Polling_logs_each_distinct_observation_once_and_names_the_last_one_on_timeout()
    {
        LongRestQualificationLog log = new(null, null);

        int reads = 0;

        int result = await LongRestQualificationPolling.UntilAsync(
            "phase",
            _ => Task.FromResult(++reads),
            static value => value >= 3,
            static value => value < 3 ? "waiting" : "done",
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(1),
            log,
            CancellationToken.None);

        Assert.Equal(3, result);
        Assert.Equal(1, log.Transcript.Split("phase: waiting").Length - 1);
        Assert.Contains("phase: done", log.Transcript, StringComparison.Ordinal);

        TimeoutException timeout = await Assert.ThrowsAsync<TimeoutException>(() => LongRestQualificationPolling.UntilAsync(
            "stuck",
            _ => Task.FromResult(0),
            static _ => false,
            static _ => "still nothing",
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(5),
            log,
            CancellationToken.None));

        Assert.Contains("still nothing", timeout.Message, StringComparison.Ordinal);
    }

    private static CalibrationMemory Memory(string id, string label, string text, float x, float y) =>
        new(id, label, text, [x, y]);

    private static string FindRepositoryRoot([CallerFilePath] string sourceFile = "")
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
}
