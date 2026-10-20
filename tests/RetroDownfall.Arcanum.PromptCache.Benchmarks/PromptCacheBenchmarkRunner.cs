using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace RetroDownfall.Arcanum.PromptCache.Benchmarks;

public sealed record PromptCacheBenchmarkCollection(PromptCacheBenchmarkSample[] Samples, PromptCacheBenchmarkSample[] Probes, PromptCacheInvalidation[] Invalidations, string ToolDigest, string SettingsDigest, string FailureCode);

public static class PromptCacheBenchmarkRunner
{
    public static Task<PromptCacheBenchmarkCollection> CollectFixtureAsync(PromptCacheBenchmarkManifest manifest, CancellationToken ct) => CollectAsync(manifest, null, "fixture", null, ct);

    internal static async Task CollectProbesAsync(List<PromptCacheBenchmarkSample> probes, Func<int, Task<PromptCacheBenchmarkSample>> call, int maximum)
    {
        for (int probe = 0; probe < 3; probe++)
        {
            var sample = await call(probe);

            probes.Add(sample);

            if (sample.OutputTokens is null || sample.OutputTokens > maximum || !sample.WireValid) throw new InvalidDataException("providerOutputBoundUnproven");
        }
    }

    internal static async Task<PromptCacheBenchmarkCollection> CollectAsync(PromptCacheBenchmarkManifest manifest, string? endpoint, string coldStart, BenchmarkOllamaMetadata? metadata, CancellationToken ct)
    {
        if (!PromptCacheBenchmarkEvidence.ValidateManifest(manifest)) throw new InvalidDataException("manifestInvalid");

        using var baseline = new PromptCacheBenchmarkComposition(false, endpoint, manifest.Model);

        using var candidate = new PromptCacheBenchmarkComposition(true, endpoint, manifest.Model);

        List<PromptCacheBenchmarkSample> samples = [];

        List<PromptCacheBenchmarkSample> probes = [];

        var invalidations = (await PromptCacheBenchmarkHarness.CheckInvalidationsAsync(ct)).ToList();

        invalidations.Add(new("credentialLocalUnobservedAba", "actualCredentialStore", await PromptCacheBenchmarkHarness.CheckActualCredentialAbaAsync(ct), "ReplanRequired", null));

        string failure = "complete";

        try
        {
            if (metadata is not null)
            {
                await CollectProbesAsync(probes, probe => candidate.CallAsync(manifest.Cases[0], 0, 0, "probe", probe, true, ct), manifest.MaxOutputTokens);

                if (probes.Any(p => p.PromptTokens is null || p.CachedTokens is null || p.OutputTokens is null)
                    || !probes.Any(p => p.CachedTokens > 0)) throw new InvalidDataException("cachedUsageSupportUnproven");
            }

            string[] schedule = PromptCacheBenchmarkEvidence.Schedule(manifest.Pairs);

            for (int pair = 0; pair < manifest.Pairs; pair++)
            {
                for (int order = 0; order < 2; order++)
                {
                    string role = schedule[pair][order].ToString();

                    var c = role == "A" ? baseline : candidate;

                    if (metadata is not null)
                    {
                        await metadata.VerifyAsync(ct);

                        if (coldStart == "reload") await metadata.UnloadSelectedModelAsync(ct);
                    }

                    samples.Add(await c.CallAsync(manifest.Cases[0], pair, order, "cold", 0, true, ct));

                    for (int warmup = 0; warmup < manifest.Warmup; warmup++) samples.Add(await c.CallAsync(manifest.Cases[0], pair, order, "warmup", warmup, true, ct));

                    for (int repeat = 0; repeat < manifest.Samples; repeat++) samples.Add(await c.CallAsync(manifest.Cases[0], pair, order, "warm", repeat, true, ct));

                    for (int suffix = 0; suffix < manifest.Samples; suffix++) samples.Add(await c.CallAsync(manifest.Cases[(suffix + 1) % manifest.Cases.Length], pair, order, "suffix", suffix, true, ct));

                    // Each mutation is explicit and excluded from the steady-state comparisons.
                    var prefix = await c.CallAsync(manifest.Cases[0], pair, order, "mutation", 0, true, ct, terminalFormatting: false);

                    samples.Add(prefix with { Case = "prefix" });

                    var original = c.Tools;

                    c.Tools = [original[0]];
                    samples.Add((await c.CallAsync(manifest.Cases[0], pair, order, "mutation", 0, true, ct)) with { Case = "toolRemoved" });
                    c.Tools = original.Reverse().ToList();
                    samples.Add((await c.CallAsync(manifest.Cases[0], pair, order, "mutation", 0, true, ct)) with { Case = "toolOrder" });
                    c.Tools = original;
                    c.Authority.Advance(RetroDownfall.Arcanum.Core.Covenant.CovenantHealthTransition.Restore);
                    var scope = await c.CallAsync(manifest.Cases[0], pair, order, "mutation", 0, true, ct);
                    samples.Add(scope with { Case = "scope" });

                    if (metadata is not null && samples.Any(s => !s.WireValid || s.PromptTokens is null || s.CachedTokens is null || s.OutputTokens is null)) throw new InvalidDataException("liveSampleUnmeasurable");
                }

                Console.Error.WriteLine($"Completed prompt-cache pair {pair + 1}/{manifest.Pairs}.");
            }

            if (metadata is not null) await metadata.VerifyAsync(ct);
        }
        catch (InvalidDataException ex)
        {
            failure = ex.Message is "providerOutputBoundUnproven" or "cachedUsageSupportUnproven" or "liveSampleUnmeasurable" or "modelIdentityDrift" or "daemonVersionDrift" or "executorRefused" or "unexpectedToolOutput" or "contextTruncationObserved" or "providerFrameBoundExceeded" ? ex.Message : "measurementFailed";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            failure = "cancelledOrDeadlineExceeded";
        }
        catch (Exception)
        {
            failure = "providerOrTransportFailed";
        }

        foreach (string mutation in new[] { "prefix", "toolRemoved", "toolOrder", "scope" })
        {
            var remote = samples.LastOrDefault(s => s.Case == mutation && s.Role == "B");

            if (remote is not null) invalidations.Add(new("remoteObservation." + mutation, "productionComparison", remote.WireValid, "Current", remote.CachedTokens));
        }

        return new(samples.ToArray(), probes.ToArray(), invalidations.ToArray(), candidate.ToolDigest, PromptCacheBenchmarkEvidence.Digest(baseline.SettingsDigest + candidate.SettingsDigest), failure);
    }
}

internal sealed class BenchmarkOllamaMetadata(string endpoint, string selectedModel) : IDisposable
{
    private string ExpectedModelDigest => selectedModel switch
    {
        "gemma4:e4b" => "a3d2b95350da03ff9b1943a753bb6617c49a3ee462b632a758518ec817743986",
        "qwen3:4b-instruct-2507-q4_K_M" => "0edcdef34593eac1aa2be9c7d06c432dcf81945adca5eca2f27662c18f168ba0",
        _ => throw new InvalidDataException("unselectedModel"),
    };

    private readonly HttpClient _http = new(RetroDownfall.Arcanum.Infrastructure.Security.OutboundUrlGuard.CreateProviderEgressHandler()) { Timeout = TimeSpan.FromSeconds(30) };

    private readonly string _origin = new Uri(endpoint).GetLeftPart(UriPartial.Authority);

    internal string ModelDigest { get; private set; } = string.Empty;

    internal string Version { get; private set; } = string.Empty;

    internal async Task VerifyAsync(CancellationToken ct)
    {
        using var version = JsonDocument.Parse(await _http.GetByteArrayAsync(_origin + "/api/version", ct));

        Version = version.RootElement.GetProperty("version").GetString() ?? string.Empty;

        if (Version != "0.40.6") throw new InvalidDataException("daemonVersionDrift");

        using var tags = JsonDocument.Parse(await _http.GetByteArrayAsync(_origin + "/api/tags", ct));

        var model = tags.RootElement.GetProperty("models").EnumerateArray().Single(m => m.GetProperty("name").GetString() == selectedModel);

        ModelDigest = model.GetProperty("digest").GetString() ?? string.Empty;

        if (ModelDigest != ExpectedModelDigest) throw new InvalidDataException("modelIdentityDrift");
    }

    internal async Task UnloadSelectedModelAsync(CancellationToken ct)
    {
        // Unloads only this operator-selected model; never stops the daemon or touches other models.
        using var request = new StringContent("{\"model\":\"" + selectedModel + "\",\"keep_alive\":0,\"stream\":false}", Encoding.UTF8, "application/json");

        using var response = await _http.PostAsync(_origin + "/api/generate", request, ct);

        response.EnsureSuccessStatusCode();

        using var loaded = JsonDocument.Parse(await _http.GetByteArrayAsync(_origin + "/api/ps", ct));

        if (loaded.RootElement.GetProperty("models").EnumerateArray().Any(m => m.GetProperty("name").GetString() == selectedModel)) throw new InvalidDataException("selectedModelUnloadUnconfirmed");
    }

    public void Dispose() => _http.Dispose();
}
