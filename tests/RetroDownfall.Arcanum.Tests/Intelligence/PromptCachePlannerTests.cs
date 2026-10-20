using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

[Trait("Suite", "Dci")]
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

    [Fact]
    public void Create_StableSuffixAfterVolatileDataDoesNotChangePrefixIdentity()
    {
        PromptCachePlan first = CreateForDocument(Document(
            Stable("preamble"),
            Volatile("dynamic"),
            new(PromptSegmentKind.Codex, PromptSegmentStability.Stable, "codex-one", true)));

        PromptCachePlan changed = CreateForDocument(Document(
            Stable("preamble"),
            Volatile("dynamic"),
            new(PromptSegmentKind.Codex, PromptSegmentStability.Stable, "codex-two", true)));

        Assert.Equal(first.StableSegmentDigest, changed.StableSegmentDigest);

        Assert.Equal(first.CacheKey, changed.CacheKey);
    }

    [Fact]
    public void Create_StableTextBeyondLastBoundaryDoesNotChangePrefixIdentity()
    {
        PromptCachePlan first = CreateForDocument(Document(
            Stable("preamble"),
            new(PromptSegmentKind.ContextHeader, PromptSegmentStability.Stable, "header-one")));

        PromptCachePlan changed = CreateForDocument(Document(
            Stable("preamble"),
            new(PromptSegmentKind.ContextHeader, PromptSegmentStability.Stable, "header-two")));

        Assert.Equal(first.StableSegmentDigest, changed.StableSegmentDigest);

        Assert.Equal(first.CacheKey, changed.CacheKey);
    }

    [Theory]
    [InlineData(PromptSegmentKind.Preamble, true)]
    [InlineData(PromptSegmentKind.CovenantProposed, false)]
    [InlineData(PromptSegmentKind.CovenantGlobalConfirmed, false)]
    [InlineData(PromptSegmentKind.CovenantCampaignConfirmed, false)]
    [InlineData((PromptSegmentKind)int.MaxValue, false)]
    public void Create_SensitiveProtectedOrUnknownStableSourceCannotFormPrefix(
        PromptSegmentKind kind,
        bool sensitive)
    {
        PromptCachePlan plan = CreateForDocument(Document(
            new PromptSegment(kind, PromptSegmentStability.Stable, "untrusted stable claim", true, sensitive)));

        Assert.Equal(PromptCacheEligibility.NonCacheable, plan.Eligibility);

        Assert.Empty(plan.CacheKey);

        Assert.Empty(plan.Boundaries);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Create_CannotCrossIneligibleStableSourceToReachLaterBoundary(bool sensitive)
    {
        PromptCachePlan plan = CreateForDocument(Document(
            Stable("safe public prefix"),
            new(sensitive ? PromptSegmentKind.Preamble : (PromptSegmentKind)int.MaxValue,
                PromptSegmentStability.Stable, "blocked", false, sensitive),
            Stable("later safe text")));

        if (sensitive)
        {
            Assert.Equal(PromptCacheEligibility.NonCacheable, plan.Eligibility);

            Assert.Empty(plan.Boundaries);
        }
        else
        {
            Assert.Equal(0, Assert.Single(plan.Boundaries).SegmentIndex);

            Assert.Equal(CreateForDocument(Document(Stable("safe public prefix"))).StableSegmentDigest,
                plan.StableSegmentDigest);
        }
    }

    [Fact]
    public void CreatePrefixDecision_HashesExactRenderedUtf8RangeIncludingNonboundaryFraming()
    {
        SystemPromptBuildResult build = Document(
            Stable("α🙂"),
            new(PromptSegmentKind.ContextHeader, PromptSegmentStability.Stable, "\n## CONTEXT\n\n"),
            new(PromptSegmentKind.TerminalFormatting, PromptSegmentStability.Stable, "format", true),
            Volatile("dynamic suffix")).BuildResult();

        PromptCachePrefixDecision decision = PromptCachePlanner.CreatePrefixDecision(
            build, new ChatOptions(), new string('A', 64));

        Assert.Equal(PromptCacheEligibility.Eligible, decision.Eligibility);

        Assert.Equal(PromptCacheNonEligibilityReason.None, decision.NonEligibilityReason);

        Assert.Equal("arcanum-dci-cache-policy-v2", decision.PolicyVersion);

        Assert.Equal(0, decision.Utf16Start);

        Assert.Equal(22, decision.Utf16Length);

        // Independently calculated SHA-256 of length-prefixed domain and exact UTF-8 prefix.
        Assert.Equal("85A2739DF5D5AE4C8FD01447839DAA100CB87442F70340EFF2D09923AF60EA7F", decision.PrefixDigest);

        Assert.Equal(new string('A', 64), decision.ScopeMarkerDigest);

        Assert.Empty(decision.ToolSchemaDigest);

        Assert.Equal("α🙂\n## CONTEXT\n\nformatdynamic suffix", build.Prompt);
    }

    [Fact]
    public void CreatePrefixDecision_SuffixChangesAndSegmentationDoNotChangeExactPrefixDigest()
    {
        PromptCachePrefixDecision first = Decide(Document(Stable("prefix"), Volatile("suffix"), Stable("stable-one")));

        PromptCachePrefixDecision changedSuffix = Decide(Document(Stable("prefix"), Volatile("other"), Stable("stable-two")));

        PromptCachePrefixDecision split = Decide(Document(Stable("pre"), Stable("fix"), Volatile("other")));

        PromptCachePrefixDecision changedPrefix = Decide(Document(Stable("prefiX"), Volatile("suffix")));

        Assert.Equal(PromptCacheEligibility.Eligible, first.Eligibility);

        Assert.Equal(first.PrefixDigest, changedSuffix.PrefixDigest);

        Assert.Equal(first.PrefixDigest, split.PrefixDigest);

        Assert.NotEqual(first.PrefixDigest, changedPrefix.PrefixDigest);
    }

    [Theory]
    [InlineData(PromptSegmentKind.CovenantProposed, false)]
    [InlineData(PromptSegmentKind.CovenantGlobalConfirmed, false)]
    [InlineData(PromptSegmentKind.CovenantCampaignConfirmed, false)]
    [InlineData(PromptSegmentKind.Codex, false)]
    [InlineData(PromptSegmentKind.PrimarySpell, false)]
    [InlineData(PromptSegmentKind.ResonantSpells, false)]
    [InlineData(PromptSegmentKind.Data, false)]
    [InlineData(PromptSegmentKind.WorkspaceContext, false)]
    [InlineData(PromptSegmentKind.CampaignSummary, false)]
    [InlineData(PromptSegmentKind.CampaignRollup, false)]
    [InlineData(PromptSegmentKind.SessionRollup, false)]
    [InlineData(PromptSegmentKind.AdditionalInstructions, false)]
    [InlineData((PromptSegmentKind)int.MaxValue, false)]
    [InlineData(PromptSegmentKind.Preamble, true)]
    public void CreatePrefixDecision_ClosesPrefixBeforeForgedStableOrSensitiveSource(
        PromptSegmentKind kind,
        bool sensitive)
    {
        PromptCachePrefixDecision decision = Decide(Document(
            Stable("public"),
            new(kind, PromptSegmentStability.Stable, "excluded", true, sensitive),
            Stable("later public")));

        bool suppressWholeCall = sensitive || kind is PromptSegmentKind.CovenantProposed
            or PromptSegmentKind.CovenantGlobalConfirmed or PromptSegmentKind.CovenantCampaignConfirmed;

        if (suppressWholeCall)
        {
            Assert.Equal(PromptCacheEligibility.NonCacheable, decision.Eligibility);

            Assert.Equal(PromptCacheNonEligibilityReason.SensitiveContent, decision.NonEligibilityReason);

            Assert.Empty(decision.PrefixDigest);
        }
        else
        {
            Assert.Equal(PromptCacheEligibility.Eligible, decision.Eligibility);

            Assert.Equal(6, decision.Utf16Length);

            Assert.Equal(Decide(Document(Stable("public"))).PrefixDigest, decision.PrefixDigest);
        }

        PromptCachePrefixDecision blocked = Decide(Document(
            new(kind, PromptSegmentStability.Stable, "excluded", true, sensitive),
            Stable("later public")));

        Assert.Equal(PromptCacheEligibility.NonCacheable, blocked.Eligibility);

        Assert.Equal(suppressWholeCall
            ? PromptCacheNonEligibilityReason.SensitiveContent
            : PromptCacheNonEligibilityReason.NoStablePrefix, blocked.NonEligibilityReason);

        Assert.Equal(0, blocked.Utf16Length);

        Assert.Empty(blocked.PrefixDigest);
    }

    [Fact]
    public void CreatePrefixDecision_StopsAtLastEligibleBoundaryAndRejectsEmptyPrefix()
    {
        PromptCachePrefixDecision decision = Decide(Document(
            Stable("public"),
            new(PromptSegmentKind.ContextHeader, PromptSegmentStability.Stable, "trailing header"),
            Volatile("suffix")));

        Assert.Equal(6, decision.Utf16Length);

        Assert.Equal(Decide(Document(Stable("public"))).PrefixDigest, decision.PrefixDigest);

        Assert.Equal(PromptCacheEligibility.NonCacheable, Decide(Document(Stable(""))).Eligibility);

        Assert.Equal(PromptCacheEligibility.NonCacheable, Decide(Document()).Eligibility);
    }

    [Fact]
    public void CreatePrefixDecision_OrderedFinalizedToolDefinitionsChangeOnlyToolDigest()
    {
        SystemPromptBuildResult build = Document(Stable("public")).BuildResult();

        PromptCachePrefixDecision first = PromptCachePlanner.CreatePrefixDecision(build,
            new ChatOptions { Tools = [new FakeFunction("alpha"), new FakeFunction("beta")] });

        PromptCachePrefixDecision reordered = PromptCachePlanner.CreatePrefixDecision(build,
            new ChatOptions { Tools = [new FakeFunction("beta"), new FakeFunction("alpha")] });

        PromptCachePrefixDecision changed = PromptCachePlanner.CreatePrefixDecision(build,
            new ChatOptions { Tools = [new FakeFunction("alpha"), new FakeFunction("gamma")] });

        Assert.Equal(PromptCacheEligibility.Eligible, first.Eligibility);

        Assert.Equal(first.PrefixDigest, reordered.PrefixDigest);

        Assert.NotEmpty(first.ToolSchemaDigest);

        Assert.NotEqual(first.ToolSchemaDigest, reordered.ToolSchemaDigest);

        Assert.NotEqual(first.ToolSchemaDigest, changed.ToolSchemaDigest);
    }

    [Fact]
    public void Create_UnsupportedProviderDoesNotEmitExplicitDirectives()
    {
        ProviderSettings provider = Provider();

        provider.Endpoint = "http://localhost:11434/v1";

        provider.Models = ["gemma4:e4b"];

        PromptCachePlan plan = PromptCachePlanner.Create(provider, "gemma4:e4b",
            Document(Stable("public")), new ChatOptions(), PromptCacheSemanticNamespace.Main, 1_024);

        Assert.NotEqual(PromptCacheEligibility.Eligible, plan.Eligibility);

        Assert.Empty(plan.CacheKey);

        Assert.Empty(plan.Boundaries);
    }

    [Theory]
    [InlineData(PromptSegmentKind.Preamble, true)]
    [InlineData(PromptSegmentKind.CovenantProposed, false)]
    [InlineData(PromptSegmentKind.CovenantGlobalConfirmed, false)]
    [InlineData(PromptSegmentKind.CovenantCampaignConfirmed, false)]
    public void Create_ProtectedOrSensitiveSuffixSuppressesWholeCallDirectives(
        PromptSegmentKind kind,
        bool sensitive)
    {
        SystemPromptDocument document = Document(
            Stable("public prefix"),
            Volatile("ordinary data"),
            new(kind, PromptSegmentStability.Volatile, "protected suffix", false, sensitive));

        PromptCachePlan plan = CreateForDocument(document);

        Assert.Equal(PromptCacheEligibility.NonCacheable, plan.Eligibility);

        Assert.Equal(PromptCacheNonEligibilityReason.SensitiveContent, plan.NonEligibilityReason);

        Assert.Empty(plan.CacheKey);

        Assert.Empty(plan.Boundaries);

        Assert.Empty(plan.StableSegmentDigest);

        PromptCachePrefixDecision decision = Decide(document);

        Assert.Equal(PromptCacheEligibility.NonCacheable, decision.Eligibility);

        Assert.Equal(PromptCacheNonEligibilityReason.SensitiveContent, decision.NonEligibilityReason);

        Assert.Empty(decision.PrefixDigest);

        Assert.Empty(decision.ToolSchemaDigest);
    }

    [Theory]
    [InlineData(CovenantPromptAttribution.CovenantConfirmed)]
    [InlineData(CovenantPromptAttribution.CovenantProposed)]
    public void Mixed_protected_attribution_under_public_framing_suppresses_both_planners_and_layout(CovenantPromptAttribution protectedAttribution)
    {
        SystemPromptDocument document = Document(new PromptSegment(PromptSegmentKind.Preamble,
            PromptSegmentStability.Stable, "publicsecret", CacheBoundaryEligible: true, Sensitive: false,
            Parts: [new(CovenantPromptAttribution.Preamble, 6), new(protectedAttribution, 6)]));

        PromptCachePlan legacy = CreateForDocument(document);

        PromptCachePrefixDecision current = Decide(document);

        Assert.Equal(PromptCacheEligibility.NonCacheable, legacy.Eligibility);

        Assert.Equal(PromptCacheNonEligibilityReason.SensitiveContent, legacy.NonEligibilityReason);

        Assert.Empty(legacy.CacheKey);

        Assert.Equal(PromptCacheEligibility.NonCacheable, current.Eligibility);

        Assert.Equal(PromptCacheNonEligibilityReason.SensitiveContent, current.NonEligibilityReason);

        Assert.Empty(current.PrefixDigest);

        Assert.Same(document, SystemPromptBuilder.BuildCacheLayout(document, new string('A', 64)));
    }

    [Fact]
    public void CreatePrefixDecision_RejectsBoundaryThatSplitsRenderedSurrogatePair()
    {
        SystemPromptBuildResult build = Document(Stable("\uD83D"), Volatile("\uDE42")).BuildResult();

        Assert.Equal("🙂", build.Prompt);

        PromptCachePrefixDecision decision = PromptCachePlanner.CreatePrefixDecision(build, new ChatOptions());

        Assert.Equal(PromptCacheEligibility.NonCacheable, decision.Eligibility);

        Assert.Equal(PromptCacheNonEligibilityReason.InvalidPlan, decision.NonEligibilityReason);

        Assert.Empty(decision.PrefixDigest);
    }

    [Fact]
    public void CreatePrefixDecision_RejectsRawTextInScopeDigestMetadata()
    {
        const string privateText = "/private/campaign source text";

        PromptCachePrefixDecision decision = PromptCachePlanner.CreatePrefixDecision(
            Document(Stable("public")).BuildResult(), new ChatOptions(), privateText);

        Assert.Equal(PromptCacheEligibility.NonCacheable, decision.Eligibility);

        Assert.Equal(PromptCacheNonEligibilityReason.InvalidPlan, decision.NonEligibilityReason);

        Assert.Empty(decision.ScopeMarkerDigest);

        Assert.DoesNotContain(privateText, decision.ToString(), StringComparison.Ordinal);
    }

    private static PromptCachePrefixDecision Decide(SystemPromptDocument document) =>
        PromptCachePlanner.CreatePrefixDecision(document.BuildResult(), new ChatOptions());

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
