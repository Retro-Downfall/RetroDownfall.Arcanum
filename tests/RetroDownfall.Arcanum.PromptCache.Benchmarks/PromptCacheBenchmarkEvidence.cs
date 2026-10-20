using System.Security.Cryptography;
using System.Text;

namespace RetroDownfall.Arcanum.PromptCache.Benchmarks;

public static class PromptCacheBenchmarkEvidence
{
    private static readonly string[] RequiredInvalidations = ["prefix", "suffix", "toolRemoved", "toolOrder", "endpoint", "model", "tokenizer", "configuration", "outputLimitDialect", "credentialObservedAba", "campaignBinding", "campaignGeneration", "physicalRoot", "profile", "reset", "restore", "protectedState", "hostTools", "featureOffOn", "restart", "credentialLocalUnobservedAba"];

    private static readonly string[] MutationCases = ["prefix", "toolRemoved", "toolOrder", "scope"];

    public static bool IsSelectedModel(string model) => model is "gemma4:e4b" or "qwen3:4b-instruct-2507-q4_K_M";

    public static bool ValidateManifest(PromptCacheBenchmarkManifest m) =>
        m.Version == 1 && m.CorpusRevision == "public-functions-v1" && IsSelectedModel(m.Model)
        && m.Pairs is >= 2 and <= 12 && m.Samples is >= 1 and <= 10 && m.Warmup is >= 1 and <= 3
        && m.MaxOutputTokens == 32 && m.Seed == 103 && m.MaximumSeconds is >= 1 and <= 3600
        && m.Cases is { Length: > 0 and <= 8 } && m.Cases.All(c => !string.IsNullOrWhiteSpace(c.Id)
            && !string.IsNullOrWhiteSpace(c.Facts) && !string.IsNullOrWhiteSpace(c.Question))
        && m.Cases.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() == m.Cases.Length;

    public static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static string[] Schedule(int pairs) => Enumerable.Range(0, pairs).Select(i => i % 2 == 0 ? "AB" : "BA").ToArray();

    private static bool Hex(string? value, int count) => value?.Length == count && value.All(char.IsAsciiHexDigit);

    private static bool Time(double? value) => value is null || double.IsFinite(value.Value) && value >= 0;

    private static bool ValidSampleFacts(PromptCacheBenchmarkSample? s) =>
        s is not null && s.TokenLimitWireField == "max_tokens" && Hex(s.ToolWireDigest, 64)
        && s.PromptTokens is null or >= 0 && s.CachedTokens is null or >= 0 && s.OutputTokens is null or >= 0
        && (!s.CachedTokens.HasValue || s.PromptTokens.HasValue && s.CachedTokens <= s.PromptTokens)
        && Time(s.FirstGeneratedMs) && Time(s.FirstAnswerMs) && Time(s.PromptMs) && Time(s.LoadMs)
        && Time(s.TotalMs) && !(s.FirstGeneratedMs > s.TotalMs) && !(s.FirstAnswerMs > s.TotalMs)
        && (s.FirstGeneratedMs is not null || s.FirstAnswerMs is null) && !(s.FirstAnswerMs < s.FirstGeneratedMs)
        && s.EligiblePrefixTokens >= 0 && s.Finish is "stop" or "length" or "toolCall" or "unknown";

    public static bool ValidateReport(PromptCacheBenchmarkReport r)
    {
        if (r.Identity is null || r.RunStatus != "complete" || r.ColdStart is not ("fixture" or "observed" or "reload") || r.Version != 1 || r.Mode is not ("fixture" or "liveOllama")
            || !Hex(r.ManifestDigest, 64) || !Hex(r.CorpusDigest, 64) || !Hex(r.ToolDigest, 64) || !Hex(r.SettingsDigest, 64)
            || !Hex(r.Identity.Revision, 40) || !Hex(r.Identity.Tree, 40) || !Hex(r.Identity.InputDigest, 64)
            || !IsSelectedModel(r.Model) || (r.Mode == "fixture" ? r.ModelDigest is not null : !Hex(r.ModelDigest, 64))
            || r.Pairs is < 2 or > 12 || r.Samples is < 1 or > 10 || r.Warmup is < 1 or > 3 || r.MaxOutputTokens is < 1 or > 32
            || r.PricingBasis != (r.Mode == "fixture" ? "fixtureNoProviderCharge" : "localOllamaUsageChargeZero")
            || r.DirectUsageChargeUsd != 0 || r.EnergyCostUsd is not null || r.HardwareCostUsd is not null
            || r.Measurements is not { Length: > 0 } || r.Invalidations is not { Length: > 0 })
        {
            return false;
        }

        HashSet<(int, string, string, int)> cells = [];

        foreach (var s in r.Measurements)
        {
            if (!ValidSampleFacts(s) || s.Pair < 0 || s.Pair >= r.Pairs || s.Role is not ("A" or "B")
                || s.Order != (Schedule(r.Pairs)[s.Pair][0].ToString() == s.Role ? 0 : 1)
                || s.Phase is not ("cold" or "warmup" or "warm" or "suffix" or "mutation")
                || s.Ordinal < 0 || (s.Phase == "mutation" ? !MutationCases.Contains(s.Case, StringComparer.Ordinal) || s.Ordinal != 0 : s.Case != s.Phase)
                || !cells.Add((s.Pair, s.Role, s.Phase == "mutation" ? s.Case : s.Phase, s.Ordinal)))
            {
                return false;
            }
        }

        for (int pair = 0; pair < r.Pairs; pair++)
        {
            foreach (string role in new[] { "A", "B" })
            {
                foreach (string phase in new[] { "cold", "warmup", "warm", "suffix" })
                {
                    int count = phase == "cold" ? 1 : phase == "warmup" ? r.Warmup : r.Samples;

                    var subset = r.Measurements.Where(s => s.Pair == pair && s.Role == role && s.Phase == phase).ToArray();

                    if (subset.Length != count || !subset.Select(s => s.Ordinal).Order().SequenceEqual(Enumerable.Range(0, count)))
                    {
                        return false;
                    }
                }

                foreach (string mutation in MutationCases)
                {
                    if (!cells.Contains((pair, role, mutation, 0))) return false;
                }
            }
        }

        string[] wireDigests = r.Measurements.Where(s => s.Phase is "warm" or "suffix").Select(s => s.ToolWireDigest).Distinct(StringComparer.Ordinal).ToArray();

        if (wireDigests.Length != 1 || !Hex(wireDigests[0], 64)) return false;

        if (!r.Invalidations.All(i => i is not null && !string.IsNullOrWhiteSpace(i.Case)
                && i.Basis is "productionComparison" or "simulatedLifecycle" or "actualCredentialStore"
                && i.LocalStatus is "Current" or "ReplanRequired" or "OptimizationUnavailable" or "Ordinary" or "Refused"
                && i.RemoteCachedTokens is null or >= 0)
            || r.Invalidations.Select(i => i.Case).Distinct(StringComparer.Ordinal).Count() != r.Invalidations.Length
            || !RequiredInvalidations.All(name => r.Invalidations.Any(i => i.Case == name))) return false;

        foreach (string mutation in MutationCases)
        {
            var sample = r.Measurements.Single(s => s.Pair == r.Pairs - 1 && s.Role == "B" && s.Phase == "mutation" && s.Case == mutation);

            var remote = r.Invalidations.SingleOrDefault(i => i.Case == "remoteObservation." + mutation);

            if (remote is null || remote.Basis != "productionComparison" || remote.LocalStatus != "Current"
                || remote.Passed != sample.WireValid || remote.RemoteCachedTokens != sample.CachedTokens) return false;
        }

        if (r.Mode == "fixture") return r.Probes is null or { Length: 0 };

        if (r.Probes is not { Length: 3 }) return false;

        for (int index = 0; index < r.Probes.Length; index++)
        {
            var probe = r.Probes[index];

            if (!ValidSampleFacts(probe) || probe.Pair != 0 || probe.Order != 0 || probe.Role != "B"
                || probe.Phase != "probe" || probe.Case != "probe" || probe.Ordinal != index
                || !probe.WireValid || probe.Finish is not ("stop" or "length")
                || probe.PromptTokens is not > 0 || probe.CachedTokens is null || probe.OutputTokens is null
                || probe.OutputTokens > r.MaxOutputTokens || probe.ToolWireDigest != wireDigests[0]) return false;
        }

        return r.Probes.Any(p => p.CachedTokens > 0);
    }

    public static PromptCacheBenchmarkVerdict Evaluate(PromptCacheBenchmarkReport r)
    {
        if (!ValidateReport(r))
        {
            return new(false, false, false, ["reportIntegrityFailed"], [], []);
        }

        List<string> reasons = [];

        bool outputBounded = r.Measurements.Concat(r.Probes ?? []).All(s => s.OutputTokens is null || s.OutputTokens <= r.MaxOutputTokens);

        bool correct = outputBounded && r.WireAssertionsPassed && r.Measurements.All(s => s.WireValid && s.Finish is "stop" or "length") && r.Invalidations.All(i => i.Passed);

        if (!outputBounded) reasons.Add("providerOutputBoundExceeded");

        if (!correct)
        {
            reasons.Add("correctnessFailed");
        }

        var observed = r.Measurements.Where(s => s.Phase is "warm" or "suffix").ToArray();

        PromptCacheRoleSummary[] summaries = observed.GroupBy(s => (s.Role, s.Phase)).Select(g =>
        {
            var usage = g.Where(s => s.PromptTokens is > 0 && s.CachedTokens.HasValue).ToArray();

            int hits = usage.Count(s => s.CachedTokens > 0);

            return new PromptCacheRoleSummary(g.Key.Role, g.Key.Phase, g.Count(), usage.Length, hits,
                usage.Length == 0 ? null : (double)hits / usage.Length,
                usage.Length == 0 ? null : (double)usage.Sum(s => s.CachedTokens!.Value) / usage.Sum(s => s.PromptTokens!.Value),
                usage.Length == 0 ? null : usage.Average(s => (double)(s.PromptTokens!.Value - s.CachedTokens!.Value)),
                Median(g.Select(s => s.FirstGeneratedMs)), Median(g.Select(s => s.FirstAnswerMs)), Median(g.Select(s => (double?)s.TotalMs))!.Value,
                Median(g.Select(s => s.PromptMs)));
        }).ToArray();

        List<PromptCachePairedSummary> paired = [];

        foreach (string phase in new[] { "warm", "suffix" })
        {
            List<double> saved = [];

            List<double?> generated = [];

            List<double?> prompt = [];

            List<double> totals = [];

            for (int pair = 0; pair < r.Pairs; pair++)
            {
                var a = observed.Where(s => s.Pair == pair && s.Role == "A" && s.Phase == phase).ToArray();

                var b = observed.Where(s => s.Pair == pair && s.Role == "B" && s.Phase == phase).ToArray();

                if (a.Concat(b).Any(s => s.PromptTokens is null || s.CachedTokens is null || s.OutputTokens is null))
                {
                    continue;
                }

                saved.Add(a.Average(s => (double)(s.PromptTokens!.Value - s.CachedTokens!.Value)) - b.Average(s => (double)(s.PromptTokens!.Value - s.CachedTokens!.Value)));

                generated.Add(Delta(b.Select(s => s.FirstGeneratedMs), a.Select(s => s.FirstGeneratedMs)));

                prompt.Add(Delta(b.Select(s => s.PromptMs), a.Select(s => s.PromptMs)));

                totals.Add(Delta(b.Select(s => (double?)s.TotalMs), a.Select(s => (double?)s.TotalMs))!.Value);
            }

            if (saved.Count > 0)
            {
                paired.Add(new(phase, saved.Count, saved.Average(), saved.Min(), saved.Max(), Median(generated), Median(prompt), Median(totals.Select(t => (double?)t))!.Value));
            }
        }

        bool known = observed.All(s => s.PromptTokens is > 0 && s.CachedTokens.HasValue && s.OutputTokens.HasValue && s.FirstGeneratedMs.HasValue);

        bool useful = paired.Any(p => p.Case == "suffix" && p.PairCount == r.Pairs && p.MeanSavedUncachedTokens > 0);

        bool hitsObserved = summaries.Any(s => s.Role == "B" && s.Hits > 0);

        bool explained = paired.Where(p => p.Case == "suffix").Any(p => (p.MedianGeneratedDeltaMs <= 0 || p.MedianPromptDeltaMs < 0) && p.MedianTotalDeltaMs <= 0);

        if (!known) reasons.Add("requiredUsageOrTimingUnavailable");

        if (!useful) reasons.Add("noIncrementalSavedPromptProcessing");

        if (!hitsObserved) reasons.Add("noObservedCandidateHits");

        if (!explained) reasons.Add("latencyBenefitUnexplained");

        if (r.Identity.Dirty) reasons.Add("investigativeDirtyTree");

        if (r.Mode == "fixture") reasons.Add("fixtureCannotQualifyProviderUtility");

        return new(true, correct, correct && known && useful && hitsObserved && explained && !r.Identity.Dirty && r.Mode == "liveOllama", reasons.ToArray(), summaries, paired.ToArray());
    }

    private static double? Delta(IEnumerable<double?> b, IEnumerable<double?> a) => Median(b) is { } bm && Median(a) is { } am ? bm - am : null;

    private static double? Median(IEnumerable<double?> values)
    {
        double[] sorted = values.Where(v => v.HasValue).Select(v => v!.Value).Order().ToArray();

        return sorted.Length == 0 ? null : sorted.Length % 2 == 0 ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2 : sorted[sorted.Length / 2];
    }
}
