using System.Text.Json;
using RetroDownfall.Arcanum.PromptCache.Benchmarks;
using Xunit;

namespace RetroDownfall.Arcanum.Tests.Benchmarks;

[Collection("ProcessEnvironment")]
public sealed class PromptCacheBenchmarkTests
{
    [Fact]
    public void SDK_empty_required_normalization_is_semantically_equal_but_real_schema_edits_are_not()
    {
        using var source = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}");

        using var empty = JsonDocument.Parse("{\"properties\":{},\"required\":[],\"type\":\"object\"}");

        using var changed = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{},\"required\":[\"unowned\"]}");

        using var changedType = JsonDocument.Parse("{\"type\":\"string\",\"properties\":{},\"required\":[]}");

        Assert.True(BenchmarkWireObserver.SameSchema(source.RootElement, empty.RootElement));

        Assert.False(BenchmarkWireObserver.SameSchema(source.RootElement, changed.RootElement));

        Assert.False(BenchmarkWireObserver.SameSchema(source.RootElement, changedType.RootElement));
    }

    [Fact]
    public async Task Numeric_timing_observer_preserves_response_headers_and_exact_stream_bytes()
    {
        const string body = "data: {\"timings\":{\"prompt_ms\":12.5}}\n\ndata: [DONE]\n\n";

        using var original = new StringContent(body, System.Text.Encoding.UTF8, "text/event-stream");

        double? prompt = null;

        using var observed = new BenchmarkObservedContent(original, (p, _) => prompt = p);

        Assert.Equal("text/event-stream", observed.Headers.ContentType?.MediaType);

        using var stream = await observed.ReadAsStreamAsync();

        using StreamReader reader = new(stream);

        Assert.Equal(body, await reader.ReadToEndAsync());

        Assert.Equal(12.5, prompt);
    }

    [Theory]
    [InlineData("gemma4:e4b")]
    [InlineData("qwen3:4b-instruct-2507-q4_K_M")]
    public async Task Fixture_schedule_executes_both_real_layouts_in_balanced_blocks_with_excluded_setup(string model)
    {
        var manifest = new PromptCacheBenchmarkManifest(1, "public-functions-v1", model, 2, 1, 1, 32, 103, 60, [new("digits", "ASCII digits only.", "Return OK.")]);

        var result = await PromptCacheBenchmarkRunner.CollectFixtureAsync(manifest, CancellationToken.None);

        Assert.Equal("complete", result.FailureCode);

        Assert.Equal(32, result.Samples.Length);

        Assert.Equal(new[] { "A", "B", "B", "A" }, result.Samples.Where(s => s.Phase == "cold").Select(s => s.Role));

        Assert.All(result.Samples, sample => { Assert.True(sample.WireValid); Assert.Equal(64, sample.ToolWireDigest.Length); });

        Assert.Single(result.Samples.Where(s => s.Phase is "warm" or "suffix").Select(s => s.ToolWireDigest).Distinct(StringComparer.Ordinal));

        Assert.Contains(result.Invalidations, i => i.Case == "credentialLocalUnobservedAba" && i.Passed && i.Basis == "actualCredentialStore");
    }

    [Fact]
    public void Operator_arguments_require_explicit_modes_and_local_uncredentialed_endpoint()
    {
        Assert.True(PromptCacheBenchmarkArguments.TryParse(["--fixture", "--record", "out.json", "--source-root", "."], out var fixture));

        Assert.True(fixture!.Fixture);

        Assert.True(PromptCacheBenchmarkArguments.TryParse(["--ollama-endpoint", "http://127.0.0.1:11434/v1", "--model", "qwen3:4b-instruct-2507-q4_K_M", "--cold-start", "reload", "--record", "out.json", "--source-root", "."], out _));

        Assert.True(PromptCacheBenchmarkArguments.TryParse(["--ollama-endpoint", "http://127.0.0.1:11434/v1", "--model", "gemma4:e4b", "--cold-start", "reload", "--record", "out.json", "--source-root", "."], out _));

        foreach (string endpoint in new[] { "https://example.com/v1", "http://user:pass@127.0.0.1:11434/v1", "http://127.0.0.1:11434/v1?key=secret", "http://127.0.0.1:11434/v1#fragment" })
        {
            Assert.False(PromptCacheBenchmarkArguments.TryParse(["--ollama-endpoint", endpoint, "--record", "out.json", "--source-root", "."], out _));
        }

        Assert.False(PromptCacheBenchmarkArguments.TryParse(["--fixture", "--ollama-endpoint", "http://127.0.0.1:11434/v1", "--record", "out.json", "--source-root", "."], out _));

        Assert.False(PromptCacheBenchmarkArguments.TryParse(["--fixture", "--pairs", "0", "--record", "out.json", "--source-root", "."], out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Real_factory_executor_SDK_keeps_native_definitions_without_execution_and_preserves_missing_usage(bool streaming)
    {
        var result = await PromptCacheBenchmarkHarness.ProbeFixtureAsync(streaming, CancellationToken.None);

        Assert.True(result.Optimized);

        Assert.True(result.WireValid);

        Assert.Equal(2, result.ToolCount);

        Assert.Equal("max_tokens", result.TokenLimitWireField);

        Assert.Null(result.CachedTokens);

        Assert.True(result.GeneratedObserved);

        Assert.True(result.AnswerObserved);
    }

    [Fact]
    public async Task Lifecycle_and_payload_mutations_are_rejected_by_production_comparison()
    {
        var results = await PromptCacheBenchmarkHarness.CheckInvalidationsAsync(CancellationToken.None);

        Assert.Equal(new[] { "prefix", "suffix", "toolRemoved", "toolOrder", "endpoint", "model", "tokenizer", "configuration", "outputLimitDialect", "credentialObservedAba", "campaignBinding", "campaignGeneration", "physicalRoot", "profile", "reset", "restore", "protectedState", "hostTools", "featureOffOn", "restart" }.Order(StringComparer.Ordinal), results.Select(r => r.Case).Order(StringComparer.Ordinal));

        Assert.All(results, r => Assert.True(r.Passed, r.Case));

        Assert.Equal("Current", Assert.Single(results, r => r.Case == "suffix").LocalStatus);

        Assert.Equal("canonicalRoot.physicalIdentityAndRevision", Assert.Single(results, r => r.Case == "physicalRoot").ChangedFact);

        Assert.Equal("authority.installationIdentity", Assert.Single(results, r => r.Case == "profile").ChangedFact);

        Assert.Equal("authority.hostToolsStateAndTransition", Assert.Single(results, r => r.Case == "hostTools").ChangedFact);
    }

    [Fact]
    public async Task Successful_local_unobserved_ABA_uses_real_credential_store_mutation_counter()
    {
        Assert.True(await PromptCacheBenchmarkHarness.CheckActualCredentialAbaAsync(CancellationToken.None));
    }

    [Fact]
    public void Alternating_matched_schedule_has_each_role_once_and_reverses_order()
    {
        Assert.Equal(new[] { "AB", "BA", "AB", "BA", "AB", "BA" }, PromptCacheBenchmarkEvidence.Schedule(6));
    }

    [Fact]
    public void Report_with_different_steady_state_tool_wire_or_unexplained_total_latency_cannot_qualify()
    {
        var report = Valid();

        var changedTools = report with { Measurements = report.Measurements.Select(s => s with { ToolWireDigest = new string(s.Role == "A" ? 'a' : 'b', 64) }).ToArray() };

        Assert.False(PromptCacheBenchmarkEvidence.ValidateReport(changedTools));

        Assert.False(PromptCacheBenchmarkEvidence.ValidateReport(report with { Measurements = report.Measurements.Select(s => s with { ToolWireDigest = "" }).ToArray() }));

        var slower = report with { Measurements = report.Measurements.Select(s => s.Role == "B" ? s with { TotalMs = s.TotalMs + 1000 } : s).ToArray() };

        Assert.False(PromptCacheBenchmarkEvidence.Evaluate(slower).LiveUtilityAccepted);
    }

    [Theory]
    [InlineData(33L)]
    [InlineData(null)]
    public async Task First_unbounded_probe_stops_future_provider_calls_and_retains_the_observation(long? output)
    {
        List<PromptCacheBenchmarkSample> probes = [];

        int calls = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() => PromptCacheBenchmarkRunner.CollectProbesAsync(probes, _ => { calls++; return Task.FromResult(Cells(30)[0] with { OutputTokens = output }); }, 32));

        Assert.Equal(1, calls);

        Assert.Single(probes);
    }

    [Fact]
    public void Report_requires_observed_legacy_limit_field_for_the_declared_benchmark_contract()
    {
        var report = Valid();

        Assert.False(PromptCacheBenchmarkEvidence.ValidateReport(report with { Measurements = report.Measurements.Select(s => s with { TokenLimitWireField = "unknown" }).ToArray() }));

        Assert.False(PromptCacheBenchmarkEvidence.ValidateReport(report with { Measurements = report.Measurements.Select(s => s with { TokenLimitWireField = "max_completion_tokens" }).ToArray() }));
    }

    [Fact]
    public void Actual_provider_output_beyond_declared_bound_rejects_correctness_without_discarding_numeric_evidence()
    {
        var report = Valid() with { Measurements = Valid().Measurements.Select(s => s with { OutputTokens = 33 }).ToArray() };

        var verdict = PromptCacheBenchmarkEvidence.Evaluate(report);

        Assert.True(verdict.IntegrityAccepted);

        Assert.False(verdict.CorrectnessAccepted);

        Assert.False(verdict.LiveUtilityAccepted);

        Assert.Contains("providerOutputBoundExceeded", verdict.Reasons);
    }

    [Fact]
    public void Missing_cached_usage_is_unknown_and_cannot_qualify_live_utility()
    {
        var report = Valid(null);

        var verdict = PromptCacheBenchmarkEvidence.Evaluate(report);

        Assert.True(verdict.IntegrityAccepted);

        Assert.False(verdict.LiveUtilityAccepted);

        Assert.All(verdict.Summaries, s => { Assert.Equal(0, s.UsageObserved); Assert.Null(s.HitRate); Assert.Null(s.CachedRatio); });

        string json = JsonSerializer.Serialize(report, PromptCacheBenchmarkJsonContext.Default.PromptCacheBenchmarkReport);

        Assert.Contains("\"cachedTokens\": null", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Known_answer_uses_actual_uncached_processing_and_excludes_warmup_and_load()
    {
        var report = Valid();

        var verdict = PromptCacheBenchmarkEvidence.Evaluate(report);

        Assert.True(verdict.LiveUtilityAccepted);

        var candidate = Assert.Single(verdict.Summaries, s => s.Role == "B" && s.Case == "suffix");

        Assert.Equal(6, candidate.Count);

        Assert.Equal(1d, candidate.HitRate);

        Assert.Equal(0.8d, candidate.CachedRatio!.Value, 5);

        Assert.Equal(20d, candidate.MeanUncachedTokens);

        var pair = Assert.Single(verdict.Paired, s => s.Case == "suffix");

        Assert.Equal(2, pair.PairCount);

        Assert.Equal(50d, pair.MeanSavedUncachedTokens);

        Assert.Equal(-10d, pair.MedianGeneratedDeltaMs);
    }

    [Theory]
    [InlineData("fixture")]
    [InlineData("liveOllama")]
    public void Cached_marker_tokens_without_incremental_processing_savings_do_not_qualify(string mode)
    {
        var report = Valid() with { Mode = mode, Measurements = Cells(30).Select(s => s.Role == "B" ? s with { PromptTokens = 150, CachedTokens = 80 } : s).ToArray() };

        if (mode == "fixture") report = report with { PricingBasis = "fixtureNoProviderCharge", ModelDigest = null, Probes = [] };

        Assert.True(PromptCacheBenchmarkEvidence.ValidateReport(report));

        Assert.False(PromptCacheBenchmarkEvidence.Evaluate(report).LiveUtilityAccepted);
    }

    [Fact]
    public void Fixture_is_correctness_only_and_local_charge_has_explicit_provenance()
    {
        var report = Valid(null) with { Mode = "fixture", PricingBasis = "fixtureNoProviderCharge", ModelDigest = null!, Probes = [] };

        var verdict = PromptCacheBenchmarkEvidence.Evaluate(report);

        Assert.True(verdict.CorrectnessAccepted);

        Assert.False(verdict.LiveUtilityAccepted);

        Assert.False(PromptCacheBenchmarkEvidence.ValidateReport(report with { DirectUsageChargeUsd = 1m }));

        Assert.False(PromptCacheBenchmarkEvidence.ValidateReport(report with { EnergyCostUsd = 0m }));

        Assert.False(PromptCacheBenchmarkEvidence.ValidateReport(report with { PricingBasis = "cloudSavings" }));
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("cached")]
    [InlineData("negative")]
    [InlineData("timing")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("revision")]
    public void Corrupt_or_partial_report_is_rejected(string corruption)
    {
        var report = Valid();

        report = corruption switch
        {
            "digest" => report with { ToolDigest = "wrong" },
            "cached" => report with { Measurements = [.. report.Measurements.Select(s => s with { CachedTokens = 101 })] },
            "negative" => report with { Measurements = [.. report.Measurements.Select(s => s with { PromptTokens = -1 })] },
            "timing" => report with { Measurements = [.. report.Measurements.Select(s => s with { FirstAnswerMs = s.TotalMs + 1 })] },
            "duplicate" => report with { Measurements = [.. report.Measurements, report.Measurements[0]] },
            "missing" => report with { Measurements = report.Measurements.Skip(1).ToArray() },
            _ => report with { Identity = report.Identity with { Revision = "moving-branch" } },
        };

        Assert.False(PromptCacheBenchmarkEvidence.ValidateReport(report));

        Assert.False(PromptCacheBenchmarkEvidence.Evaluate(report).IntegrityAccepted);
    }

    [Fact]
    public void Explicit_second_target_is_inventoried_with_its_own_model_identity()
    {
        Assert.True(PromptCacheBenchmarkEvidence.ValidateReport(Valid() with { Model = "qwen3:4b-instruct-2507-q4_K_M" }));

        Assert.False(PromptCacheBenchmarkEvidence.ValidateReport(Valid() with { Model = "unselected-model" }));
    }

    [Fact]
    public void Missing_lifecycle_rows_and_null_identity_cannot_validate_a_partial_report()
    {
        var report = Valid();

        Assert.False(PromptCacheBenchmarkEvidence.ValidateReport(report with { Invalidations = report.Invalidations.Where(i => i.Case != "restore").ToArray() }));

        Assert.False(PromptCacheBenchmarkEvidence.ValidateReport(report with { Identity = null! }));
    }

    [Fact]
    public void Local_invalidation_pass_can_coexist_with_remote_public_partial_hit()
    {
        var report = Valid() with { Invalidations = Valid().Invalidations.Select(i => i.Case == "reset" ? i with { RemoteCachedTokens = 50 } : i).ToArray() };

        Assert.True(PromptCacheBenchmarkEvidence.Evaluate(report).CorrectnessAccepted);

        Assert.False(PromptCacheBenchmarkEvidence.Evaluate(report with { Invalidations = report.Invalidations.Select(i => i.Case == "reset" ? i with { Passed = false, LocalStatus = "Current" } : i).ToArray() }).CorrectnessAccepted);
    }

    [Fact]
    public void Manifest_rejects_unknown_revision_nonpositive_counts_and_changed_corpus()
    {
        var manifest = new PromptCacheBenchmarkManifest(1, "public-functions-v1", "gemma4:e4b", 6, 3, 1, 32, 103, 1800, [new("digits", "ASCII digits only.", "Return the count.")]);

        Assert.True(PromptCacheBenchmarkEvidence.ValidateManifest(manifest));

        Assert.False(PromptCacheBenchmarkEvidence.ValidateManifest(manifest with { Version = 2 }));

        Assert.False(PromptCacheBenchmarkEvidence.ValidateManifest(manifest with { CorpusRevision = "unknown" }));

        Assert.False(PromptCacheBenchmarkEvidence.ValidateManifest(manifest with { Samples = 0 }));

        Assert.False(PromptCacheBenchmarkEvidence.ValidateManifest(manifest with { Cases = [new("digits", "", "question")] }));

        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", PromptCacheBenchmarkEvidence.Digest("abc"));
    }

    [Fact]
    public void Explicit_source_generated_report_roundtrip_retains_unknowns_and_rejects_extra_fields()
    {
        var report = Valid(null);

        string json = JsonSerializer.Serialize(report, PromptCacheBenchmarkJsonContext.Default.PromptCacheBenchmarkReport);

        var restored = JsonSerializer.Deserialize(json, PromptCacheBenchmarkJsonContext.Default.PromptCacheBenchmarkReport)!;

        Assert.True(PromptCacheBenchmarkEvidence.ValidateReport(restored));

        Assert.Null(restored.Measurements[0].CachedTokens);

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json.Insert(1, "\"promptText\":\"forbidden\","), PromptCacheBenchmarkJsonContext.Default.PromptCacheBenchmarkReport));
    }

    [Theory]
    [InlineData("all")]
    [InlineData("single")]
    [InlineData("case")]
    [InlineData("ordinal")]
    [InlineData("extra")]
    [InlineData("ordinaryCase")]
    public void Complete_report_requires_every_declared_mutation_cell_with_exact_case_and_ordinal(string corruption)
    {
        var report = Valid();

        var target = report.Measurements.Last(s => s.Role == "B" && s.Case == "scope");

        var samples = corruption switch
        {
            "all" => report.Measurements.Where(s => s.Phase != "mutation").ToArray(),
            "single" => report.Measurements.Where(s => s != target).ToArray(),
            "case" => report.Measurements.Select(s => s == target ? s with { Case = "unrecordedMutation" } : s).ToArray(),
            "ordinal" => report.Measurements.Select(s => s == target ? s with { Ordinal = 1 } : s).ToArray(),
            "extra" => [.. report.Measurements, target with { Case = "unrecordedMutation" }],
            _ => report.Measurements.Select(s => s.Phase == "warm" ? s with { Case = "unrecordedWarm" } : s).ToArray(),
        };

        var verdict = PromptCacheBenchmarkEvidence.Evaluate(report with { Measurements = samples });

        Assert.False(verdict.IntegrityAccepted);

        Assert.False(verdict.LiveUtilityAccepted);
    }

    [Theory]
    [InlineData("missingAll")]
    [InlineData("missingOne")]
    [InlineData("cached")]
    [InlineData("wire")]
    [InlineData("basis")]
    [InlineData("status")]
    public void Complete_report_requires_correlated_remote_mutation_observations(string corruption)
    {
        var report = Valid();

        var target = report.Invalidations.Single(i => i.Case == "remoteObservation.scope");

        var observations = corruption switch
        {
            "missingAll" => report.Invalidations.Where(i => !i.Case.StartsWith("remoteObservation.", StringComparison.Ordinal)).ToArray(),
            "missingOne" => report.Invalidations.Where(i => i != target).ToArray(),
            "cached" => report.Invalidations.Select(i => i == target ? i with { RemoteCachedTokens = 1 } : i).ToArray(),
            "wire" => report.Invalidations.Select(i => i == target ? i with { Passed = false } : i).ToArray(),
            "basis" => report.Invalidations.Select(i => i == target ? i with { Basis = "simulatedLifecycle" } : i).ToArray(),
            _ => report.Invalidations.Select(i => i == target ? i with { LocalStatus = "ReplanRequired" } : i).ToArray(),
        };

        Assert.False(PromptCacheBenchmarkEvidence.ValidateReport(report with { Invalidations = observations }));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("short")]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("role")]
    [InlineData("pair")]
    [InlineData("order")]
    [InlineData("phase")]
    [InlineData("case")]
    [InlineData("ordinal")]
    [InlineData("noHit")]
    [InlineData("missingUsage")]
    [InlineData("missingPrompt")]
    [InlineData("missingOutput")]
    [InlineData("nullProbe")]
    [InlineData("negativePrompt")]
    [InlineData("negativeCached")]
    [InlineData("cachedSubset")]
    [InlineData("negativeOutput")]
    [InlineData("outputBound")]
    [InlineData("negativeTime")]
    [InlineData("nonfiniteTime")]
    [InlineData("lateGenerated")]
    [InlineData("answerBeforeGenerated")]
    [InlineData("missingGenerated")]
    [InlineData("promptTime")]
    [InlineData("loadTime")]
    [InlineData("finish")]
    [InlineData("wire")]
    [InlineData("limitField")]
    [InlineData("toolWire")]
    public void Completed_live_report_requires_three_valid_observed_bounded_support_probes_with_a_hit(string corruption)
    {
        var report = Valid();

        var probes = report.Probes!.ToArray();

        var first = probes[0];

        probes[0] = corruption switch
        {
            "role" => first with { Role = "A" },
            "pair" => first with { Pair = 1 },
            "order" => first with { Order = 1 },
            "phase" => first with { Phase = "warm" },
            "case" => first with { Case = "warm" },
            "ordinal" => first with { Ordinal = 3 },
            "missingUsage" => first with { CachedTokens = null },
            "missingPrompt" => first with { PromptTokens = null },
            "missingOutput" => first with { OutputTokens = null },
            "nullProbe" => null!,
            "negativePrompt" => first with { PromptTokens = -1 },
            "negativeCached" => first with { CachedTokens = -1 },
            "cachedSubset" => first with { CachedTokens = 101 },
            "negativeOutput" => first with { OutputTokens = -1 },
            "outputBound" => first with { OutputTokens = 33 },
            "negativeTime" => first with { TotalMs = -1 },
            "nonfiniteTime" => first with { TotalMs = double.NaN },
            "lateGenerated" => first with { FirstGeneratedMs = first.TotalMs + 1 },
            "answerBeforeGenerated" => first with { FirstAnswerMs = 1 },
            "missingGenerated" => first with { FirstGeneratedMs = null },
            "promptTime" => first with { PromptMs = -1 },
            "loadTime" => first with { LoadMs = -1 },
            "finish" => first with { Finish = "toolCall" },
            "wire" => first with { WireValid = false },
            "limitField" => first with { TokenLimitWireField = "max_completion_tokens" },
            "toolWire" => first with { ToolWireDigest = new string('b', 64) },
            _ => first,
        };

        PromptCacheBenchmarkSample[]? changed = corruption switch
        {
            "missing" => null,
            "empty" => [],
            "short" => probes.Take(2).ToArray(),
            "extra" => [.. probes, probes[0] with { Ordinal = 3 }],
            "duplicate" => [probes[0], probes[0], probes[2]],
            "noHit" => probes.Select(p => p with { CachedTokens = 0 }).ToArray(),
            _ => probes,
        };

        var verdict = PromptCacheBenchmarkEvidence.Evaluate(report with { Probes = changed });

        Assert.False(verdict.IntegrityAccepted);

        Assert.False(verdict.LiveUtilityAccepted);
    }

    [Fact]
    public void Accepted_result_fixture_models_the_whole_declared_live_protocol()
    {
        var report = Valid();

        Assert.Equal(48, report.Measurements.Length);

        Assert.Equal(16, report.Measurements.Count(s => s.Phase == "mutation"));

        Assert.Equal(25, report.Invalidations.Length);

        Assert.Equal(3, report.Probes!.Length);

        Assert.True(PromptCacheBenchmarkEvidence.Evaluate(report).LiveUtilityAccepted);
    }

    [Fact]
    public void Fixture_zero_probe_protocol_retains_unknown_usage_and_cannot_qualify_provider_utility()
    {
        var report = Valid(null) with { Mode = "fixture", PricingBasis = "fixtureNoProviderCharge", ModelDigest = null, Probes = [] };

        report = report with { Measurements = report.Measurements.Select(s => s with { PromptTokens = null, OutputTokens = null }).ToArray() };

        var verdict = PromptCacheBenchmarkEvidence.Evaluate(report);

        Assert.True(verdict.IntegrityAccepted);

        Assert.True(verdict.CorrectnessAccepted);

        Assert.False(verdict.LiveUtilityAccepted);

        Assert.True(PromptCacheBenchmarkEvidence.ValidateReport(report with { Probes = null }));

        Assert.False(PromptCacheBenchmarkEvidence.ValidateReport(report with { Probes = Valid().Probes }));
    }

    [Fact]
    public void Failed_partial_run_is_retained_but_never_qualified()
    {
        var report = Valid() with { RunStatus = "providerOutputBoundUnproven", Measurements = [], Invalidations = [], Probes = Valid().Probes!.Take(1).ToArray() };

        string json = JsonSerializer.Serialize(report, PromptCacheBenchmarkJsonContext.Default.PromptCacheBenchmarkReport);

        var retained = JsonSerializer.Deserialize(json, PromptCacheBenchmarkJsonContext.Default.PromptCacheBenchmarkReport)!;

        Assert.Single(retained.Probes!);

        Assert.Equal("providerOutputBoundUnproven", retained.RunStatus);

        Assert.False(PromptCacheBenchmarkEvidence.Evaluate(retained).IntegrityAccepted);
    }

    private static PromptCacheBenchmarkReport Valid(long? cached = 30)
    {
        var cells = Cells(cached);

        var local = RequiredInvalidations.Select(name => new PromptCacheInvalidation(name, name == "credentialLocalUnobservedAba" ? "actualCredentialStore" : "simulatedLifecycle", true, "ReplanRequired", null));

        var remote = new[] { "prefix", "toolRemoved", "toolOrder", "scope" }.Select(name => new PromptCacheInvalidation("remoteObservation." + name, "productionComparison", true, "Current", cells.Last(s => s.Role == "B" && s.Case == name).CachedTokens));

        var probes = Enumerable.Range(0, 3).Select(ordinal => Cells(30).First(s => s.Role == "B") with { Pair = 0, Order = 0, Case = "probe", Phase = "probe", Ordinal = ordinal }).ToArray();

        return new(1, "liveOllama", D, D, D, D, "gemma4:e4b", D, "0.40.6", new(new string('a', 40), new string('b', 40), false, D, "10", "osx-arm64", "macOS", "test", "10.0.101", "OpenAI"), 2, 3, 1, 32, "localOllamaUsageChargeZero", 0m, null, null, true, cells, local.Concat(remote).ToArray(), Probes: probes);
    }

    private static readonly string[] RequiredInvalidations = ["prefix", "suffix", "toolRemoved", "toolOrder", "endpoint", "model", "tokenizer", "configuration", "outputLimitDialect", "credentialObservedAba", "campaignBinding", "campaignGeneration", "physicalRoot", "profile", "reset", "restore", "protectedState", "hostTools", "featureOffOn", "restart", "credentialLocalUnobservedAba"];

    private const string D = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static PromptCacheBenchmarkSample[] Cells(long? cached)
    {
        List<PromptCacheBenchmarkSample> samples = [];

        for (int pair = 0; pair < 2; pair++)
        {
            foreach (string role in new[] { "A", "B" })
            {
                int order = pair % 2 == 0 ? (role == "A" ? 0 : 1) : (role == "B" ? 0 : 1);

                foreach (string phase in new[] { "cold", "warmup", "warm", "suffix" })
                {
                    int count = phase is "warm" or "suffix" ? 3 : 1;

                    for (int ordinal = 0; ordinal < count; ordinal++)
                    {
                        samples.Add(new(pair, order, role, phase, phase, ordinal, 100, cached is null ? null : role == "A" ? cached : 80, 1, role == "A" ? 30 : 20, role == "A" ? 30 : 20, phase is "cold" or "warmup" ? 9000 : 50, null, phase == "cold" ? 8000 : null, 10, true, "stop", D, "max_tokens"));
                    }
                }

                foreach (string mutation in new[] { "prefix", "toolRemoved", "toolOrder", "scope" })
                {
                    samples.Add(new(pair, order, role, mutation, "mutation", 0, 100, cached is null ? null : role == "A" ? cached : 80, 1, 30, 30, 9000, null, null, 10, true, "stop", D, "max_tokens"));
                }
            }
        }

        return samples.ToArray();
    }
}
