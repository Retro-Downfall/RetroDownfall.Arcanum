namespace RetroDownfall.Arcanum.PromptCache.Benchmarks;

public sealed record PromptCacheCorpusCase(string Id, string Facts, string Question);

public sealed record PromptCacheBenchmarkManifest(int Version, string CorpusRevision, string Model, int Pairs, int Samples, int Warmup, int MaxOutputTokens, long Seed, int MaximumSeconds, PromptCacheCorpusCase[] Cases);

public sealed record PromptCacheBenchmarkIdentity(string Revision, string Tree, bool Dirty, string InputDigest, string Runtime, string Rid, string OperatingSystem, string Hardware, string Sdk, string Libraries);

public sealed record PromptCacheBenchmarkSample(int Pair, int Order, string Role, string Case, string Phase, int Ordinal, long? PromptTokens, long? CachedTokens, long? OutputTokens, double? FirstGeneratedMs, double? FirstAnswerMs, double TotalMs, double? PromptMs, double? LoadMs, int EligiblePrefixTokens, bool WireValid, string Finish, string ToolWireDigest = "", string TokenLimitWireField = "unknown");

public sealed record PromptCacheInvalidation(string Case, string Basis, bool Passed, string LocalStatus, long? RemoteCachedTokens, string ChangedFact = "");

public sealed record PromptCacheBenchmarkReport(int Version, string Mode, string ManifestDigest, string CorpusDigest, string ToolDigest, string SettingsDigest, string Model, string? ModelDigest, string DaemonVersion, PromptCacheBenchmarkIdentity Identity, int Pairs, int Samples, int Warmup, int MaxOutputTokens, string PricingBasis, decimal? DirectUsageChargeUsd, decimal? EnergyCostUsd, decimal? HardwareCostUsd, bool WireAssertionsPassed, PromptCacheBenchmarkSample[] Measurements, PromptCacheInvalidation[] Invalidations, string ColdStart = "observed", PromptCacheBenchmarkSample[]? Probes = null, string RunStatus = "complete");

public sealed record PromptCacheRoleSummary(string Role, string Case, int Count, int UsageObserved, int Hits, double? HitRate, double? CachedRatio, double? MeanUncachedTokens, double? MedianFirstGeneratedMs, double? MedianFirstAnswerMs, double MedianTotalMs, double? MedianPromptMs);

public sealed record PromptCachePairedSummary(string Case, int PairCount, double MeanSavedUncachedTokens, double MinimumPairSavedTokens, double MaximumPairSavedTokens, double? MedianGeneratedDeltaMs, double? MedianPromptDeltaMs, double MedianTotalDeltaMs);

public sealed record PromptCacheBenchmarkVerdict(bool IntegrityAccepted, bool CorrectnessAccepted, bool LiveUtilityAccepted, string[] Reasons, PromptCacheRoleSummary[] Summaries, PromptCachePairedSummary[] Paired);
