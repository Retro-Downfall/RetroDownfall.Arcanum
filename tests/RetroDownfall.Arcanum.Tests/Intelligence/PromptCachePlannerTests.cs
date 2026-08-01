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
            "gpt-5",
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

    public void Create_AttachmentPathContentAndHashRemainOutsideStableDigestAndSharedKey()
    {

        PromptCachePlan first = CreateForDocument(Document(
            Stable("preamble"),
            Volatile("path=/private/one content=alpha hash=aaa")));

        PromptCachePlan second = CreateForDocument(Document(
            Stable("preamble"),
            Volatile("path=/private/two content=beta hash=bbb")));

        Assert.Equal(first.CacheKey, second.CacheKey);

        Assert.Equal(first.StableSegmentDigest, second.StableSegmentDigest);

        Assert.DoesNotContain("aaa", first.CacheKey, StringComparison.Ordinal);

        Assert.DoesNotContain("/private/one", first.StableSegmentDigest, StringComparison.Ordinal);

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
            "gpt-5",
            Document(Stable("preamble")),
            firstOptions,
            PromptCacheSemanticNamespace.Main,
            1_024);
        PromptCachePlan changed = PromptCachePlanner.Create(
            Provider(),
            "gpt-5",
            Document(Stable("preamble")),
            changedOptions,
            PromptCacheSemanticNamespace.Main,
            1_024);
        PromptCachePlan reordered = PromptCachePlanner.Create(
            Provider(),
            "gpt-5",
            Document(Stable("preamble")),
            reorderedOptions,
            PromptCacheSemanticNamespace.Main,
            1_024);

        Assert.NotEqual(first.CacheKey, changed.CacheKey);
        Assert.NotEqual(first.CacheKey, reordered.CacheKey);
    }

    [Fact]
    public void Create_UnknownModelEmitsNoKey()
    {
        ProviderSettings provider = Provider();
        provider.Models = ["unknown-model"];

        PromptCachePlan plan = PromptCachePlanner.Create(
            provider,
            "unknown-model",
            Document(Stable("preamble")),
            new ChatOptions(),
            PromptCacheSemanticNamespace.Main,
            10);

        Assert.Equal(PromptCacheEligibility.ProviderManaged, plan.Eligibility);
        Assert.Equal(
            PromptCacheNonEligibilityReason.ProfileAbsent,
            plan.NonEligibilityReason);
        Assert.Equal(string.Empty, plan.CacheKey);
        Assert.Empty(plan.Boundaries);
    }

    private static PromptCachePlan CreateForDocument(SystemPromptDocument document) =>
        PromptCachePlanner.Create(
            Provider(),
            "gpt-5",
            document,
            new ChatOptions(),
            PromptCacheSemanticNamespace.Main,
            1_024);

    private static ProviderSettings Provider() =>
        new()
        {
            Name = "provider",
            Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://api.openai.com/v1",
            Models = ["gpt-5"],
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
