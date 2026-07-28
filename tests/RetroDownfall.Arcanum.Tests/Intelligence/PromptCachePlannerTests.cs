using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class PromptCachePlannerTests
{
    [Fact]
    public void Create_MainPlan_UsesOpaqueStableKeyAndStopsBoundaryBeforeVolatileSegment()
    {
        const string privateCodex = "private codex Alice@example.test";
        SystemPromptDocument document = Document(
            new(
                PromptSegmentKind.Preamble,
                PromptSegmentStability.Stable,
                "stable preamble",
                CacheBoundaryEligible: true),
            new(
                PromptSegmentKind.Data,
                PromptSegmentStability.Volatile,
                "attachment secret",
                CacheBoundaryEligible: false),
            new(
                PromptSegmentKind.Codex,
                PromptSegmentStability.Stable,
                privateCodex,
                CacheBoundaryEligible: true));

        PromptCachePlan plan = PromptCachePlanner.Create(
            Provider(),
            "cache-model",
            ExplicitProfile(),
            document,
            new ChatOptions(),
            PromptCacheSemanticNamespace.Main,
            eligiblePrefixTokenEstimate: 1_024);

        Assert.Equal(PromptCacheEligibility.Eligible, plan.Eligibility);
        Assert.Equal(PromptCacheSemanticNamespace.Main, plan.Namespace);
        Assert.Equal(1_024, plan.EligiblePrefixTokenEstimate);
        PromptCacheBoundary boundary = Assert.Single(plan.Boundaries);
        Assert.Equal(0, boundary.SegmentIndex);
        Assert.StartsWith("arcanum-pc-v1-", plan.CacheKey, StringComparison.Ordinal);
        Assert.DoesNotContain("stable preamble", plan.CacheKey, StringComparison.Ordinal);
        Assert.DoesNotContain(privateCodex, plan.CacheKey, StringComparison.Ordinal);
        Assert.DoesNotContain("attachment secret", plan.CacheKey, StringComparison.Ordinal);
        Assert.DoesNotContain(privateCodex, plan.StableSegmentDigest, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_MainPlan_AppendedVolatileContentDoesNotChangeKeyButStableContentDoes()
    {
        PromptCachePlan first = CreateForDocument(Document(
            Stable("preamble-v1"),
            Volatile("turn one")));
        PromptCachePlan appended = CreateForDocument(Document(
            Stable("preamble-v1"),
            Volatile("turn one plus appended history and tool results")));
        PromptCachePlan changedStatic = CreateForDocument(Document(
            Stable("preamble-v2"),
            Volatile("turn one")));

        Assert.Equal(first.CacheKey, appended.CacheKey);
        Assert.NotEqual(first.CacheKey, changedStatic.CacheKey);
    }

    [Fact]
    public void Create_MainPlan_FinalizedToolDefinitionsParticipateWithoutReordering()
    {
        ChatOptions firstOptions = new()
        {
            Tools = [new FakeFunction("alpha"), new FakeFunction("beta")],
        };
        ChatOptions changedOptions = new()
        {
            Tools = [new FakeFunction("alpha"), new FakeFunction("gamma")],
        };
        ChatOptions reorderedOptions = new()
        {
            Tools = [new FakeFunction("beta"), new FakeFunction("alpha")],
        };

        PromptCachePlan first = PromptCachePlanner.Create(
            Provider(),
            "cache-model",
            ExplicitProfile() with { ToolSchemasParticipate = true },
            Document(Stable("preamble")),
            firstOptions,
            PromptCacheSemanticNamespace.Main,
            1_024);
        PromptCachePlan changed = PromptCachePlanner.Create(
            Provider(),
            "cache-model",
            ExplicitProfile() with { ToolSchemasParticipate = true },
            Document(Stable("preamble")),
            changedOptions,
            PromptCacheSemanticNamespace.Main,
            1_024);
        PromptCachePlan reordered = PromptCachePlanner.Create(
            Provider(),
            "cache-model",
            ExplicitProfile() with { ToolSchemasParticipate = true },
            Document(Stable("preamble")),
            reorderedOptions,
            PromptCacheSemanticNamespace.Main,
            1_024);

        Assert.NotEqual(first.CacheKey, changed.CacheKey);
        Assert.NotEqual(first.CacheKey, reordered.CacheKey);
    }

    [Theory]
    [InlineData(PromptCachingControlMode.ProviderManaged, PromptCacheEligibility.ProviderManaged)]
    [InlineData(PromptCachingControlMode.None, PromptCacheEligibility.NonCacheable)]
    public void Create_NonExplicitModesEmitNoKey(
        PromptCachingControlMode mode,
        PromptCacheEligibility expected)
    {
        PromptCachePlan plan = PromptCachePlanner.Create(
            Provider(),
            "cache-model",
            new PromptCachingProfile { ControlMode = mode },
            Document(Stable("preamble")),
            new ChatOptions(),
            PromptCacheSemanticNamespace.Main,
            10);

        Assert.Equal(expected, plan.Eligibility);
        Assert.Equal(string.Empty, plan.CacheKey);
        Assert.Empty(plan.Boundaries);
    }

    private static PromptCachePlan CreateForDocument(SystemPromptDocument document) =>
        PromptCachePlanner.Create(
            Provider(),
            "cache-model",
            ExplicitProfile(),
            document,
            new ChatOptions(),
            PromptCacheSemanticNamespace.Main,
            1_024);

    private static ProviderSettings Provider() =>
        new()
        {
            Name = "provider",
            Type = AiProviderKind.OpenAICompatible,
            Models = ["cache-model"],
        };

    private static PromptCachingProfile ExplicitProfile() =>
        new()
        {
            ControlMode = PromptCachingControlMode.Explicit,
            WireDialect = PromptCachingWireDialect.OpenAiPromptCacheRetention,
            CacheKeysSupported = true,
            EmitCacheKey = true,
            ReportsCachedInputUsage = true,
        };

    private static SystemPromptDocument Document(params PromptSegment[] segments) => new(segments);

    private static PromptSegment Stable(string text) =>
        new(
            PromptSegmentKind.Preamble,
            PromptSegmentStability.Stable,
            text,
            CacheBoundaryEligible: true);

    private static PromptSegment Volatile(string text) =>
        new(
            PromptSegmentKind.Data,
            PromptSegmentStability.Volatile,
            text,
            CacheBoundaryEligible: false);

    private sealed class FakeFunction(string name) : AIFunction
    {
        public override string Name { get; } = name;

        public override string Description => $"Description for {Name}";

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<object?>(null);
    }
}
