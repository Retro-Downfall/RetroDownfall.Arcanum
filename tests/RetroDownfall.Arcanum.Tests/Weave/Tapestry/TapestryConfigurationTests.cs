using System.Text.Json;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

/// <summary>
/// The Tapestry is a single opt-in feature gate that derives the shared embedding substrate, exactly
/// like every other §21 RAG capability. Its tree-shaping bounds stay code-owned mechanics (§21.3);
/// only the retrieval mode and summary model are operator policy.
/// </summary>
public sealed class TapestryConfigurationTests
{

    [Fact]
    public void ResolveEmbeddings_TapestryDefaultsOff()
    {

        EmbeddingSettings embeddings = new ArcanumSettings().ResolveEmbeddings();

        Assert.False(embeddings.TapestryEnabled);

        Assert.False(embeddings.Enabled);

    }

    [Fact]
    public void ResolveEmbeddings_TapestryGateDerivesTheEmbeddingSubstrate()
    {

        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { Tapestry = true },
        };

        EmbeddingSettings embeddings = settings.ResolveEmbeddings();

        Assert.True(embeddings.TapestryEnabled);

        Assert.True(embeddings.Enabled);

    }

    [Fact]
    public void ResolveEmbeddings_ProjectsOperatorRetrievalModeAndSummaryModel()
    {

        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { Tapestry = true },
            Integrations = new IntegrationSettings
            {
                Embeddings = new EmbeddingIntegrationSettings
                {
                    Tapestry = new TapestryIntegrationSettings
                    {
                        RetrievalMode = TapestryRetrievalMode.TreeTraversal,
                        SummaryModel = " fast-model ",
                    },
                },
            },
        };

        TapestryEmbeddingSettings tapestry = settings.ResolveEmbeddings().Tapestry;

        Assert.Equal(TapestryRetrievalMode.TreeTraversal, tapestry.RetrievalMode);

        Assert.Equal("fast-model", tapestry.SummaryModel);

    }

    [Fact]
    public void ResolveEmbeddings_DefaultsToCollapsedTreeRetrieval()
    {

        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { Tapestry = true },
        };

        TapestryEmbeddingSettings tapestry = settings.ResolveEmbeddings().Tapestry;

        Assert.Equal(TapestryRetrievalMode.CollapsedTree, tapestry.RetrievalMode);

        Assert.Null(tapestry.SummaryModel);

        Assert.True(tapestry.WorkspaceTreesEnabled);

        Assert.True(tapestry.SessionAttachmentTreesEnabled);

        Assert.True(tapestry.SessionTreesEnabled);

    }

    [Fact]
    public void Validation_RequiresProviderFactsWhenTapestryIsEnabled()
    {

        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { Tapestry = true },
        };

        Result result = new ConfigurationValidator().Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(
            result.Error.Details!,
            static error => error.Pointer == "integrations.embeddings.provider");

        Assert.Contains(
            result.Error.Details!,
            static error => error.Pointer == "integrations.embeddings.model");

    }

    [Theory]
    [InlineData("CollapsedTree", TapestryRetrievalMode.CollapsedTree)]
    [InlineData("TreeTraversal", TapestryRetrievalMode.TreeTraversal)]
    [InlineData("treeTraversal", TapestryRetrievalMode.TreeTraversal)]
    public void RetrievalMode_ReadsTheDocumentedNamedValues(
        string wire,
        TapestryRetrievalMode expected)
    {

        TapestryIntegrationSettings? tapestry = JsonSerializer.Deserialize(
            $$"""{"retrievalMode":"{{wire}}"}""",
            ConfigurationJsonContext.Default.TapestryIntegrationSettings);

        Assert.NotNull(tapestry);

        Assert.Equal(expected, tapestry.RetrievalMode);

    }

    [Theory]
    [InlineData(0, TapestryRetrievalMode.CollapsedTree)]
    [InlineData(1, TapestryRetrievalMode.TreeTraversal)]
    public void RetrievalMode_StillReadsAlreadyPersistedNumericValues(
        int wire,
        TapestryRetrievalMode expected)
    {

        TapestryIntegrationSettings? tapestry = JsonSerializer.Deserialize(
            $$"""{"retrievalMode":{{wire}}}""",
            ConfigurationJsonContext.Default.TapestryIntegrationSettings);

        Assert.NotNull(tapestry);

        Assert.Equal(expected, tapestry.RetrievalMode);

    }

    [Fact]
    public void RetrievalMode_WritesTheDocumentedNamedValue()
    {

        string json = JsonSerializer.Serialize(
            new TapestryIntegrationSettings { RetrievalMode = TapestryRetrievalMode.TreeTraversal },
            ConfigurationJsonContext.Default.TapestryIntegrationSettings);

        Assert.Contains("\"TreeTraversal\"", json, StringComparison.Ordinal);

    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(99, 16)]
    [InlineData(4, 4)]
    public void Clamps_BoundTreeDepth(int input, int expected) =>
        Assert.Equal(expected, ArcanumSettingClamps.EmbeddingsTapestryMaxTreeDepth(input));

    [Theory]
    [InlineData(0, 2)]
    [InlineData(9_999, 64)]
    [InlineData(8, 8)]
    public void Clamps_BoundTargetChildrenPerSummary(int input, int expected) =>
        Assert.Equal(expected, ArcanumSettingClamps.EmbeddingsTapestryTargetChildrenPerSummary(input));

    [Fact]
    public void Clamps_BoundEveryTapestryMechanic()
    {

        Assert.Equal(2, ArcanumSettingClamps.EmbeddingsTapestryMaxChildrenPerSummary(0));

        Assert.Equal(256, ArcanumSettingClamps.EmbeddingsTapestryMaxChildrenPerSummary(int.MaxValue));

        Assert.Equal(2, ArcanumSettingClamps.EmbeddingsTapestryMaxClustersPerLayer(0));

        Assert.Equal(4_096, ArcanumSettingClamps.EmbeddingsTapestryMaxClustersPerLayer(int.MaxValue));

        Assert.Equal(64, ArcanumSettingClamps.EmbeddingsTapestryMaxSummaryTokens(0));

        Assert.Equal(8_192, ArcanumSettingClamps.EmbeddingsTapestryMaxSummaryTokens(int.MaxValue));

        Assert.Equal(1, ArcanumSettingClamps.EmbeddingsTapestryRebuildIntervalMinutes(0));

        Assert.Equal(1_440, ArcanumSettingClamps.EmbeddingsTapestryRebuildIntervalMinutes(int.MaxValue));

        Assert.Equal(1, ArcanumSettingClamps.EmbeddingsTapestryMaxRetrievedNodes(0));

        Assert.Equal(50, ArcanumSettingClamps.EmbeddingsTapestryMaxRetrievedNodes(int.MaxValue));

        Assert.Equal(1_024, ArcanumSettingClamps.EmbeddingsTapestryMaxRetrievedBytes(0));

        Assert.Equal(16 * 1024 * 1024, ArcanumSettingClamps.EmbeddingsTapestryMaxRetrievedBytes(int.MaxValue));

        Assert.Equal(128, ArcanumSettingClamps.EmbeddingsTapestryMaxRetrievedTokens(0));

        Assert.Equal(1024 * 1024, ArcanumSettingClamps.EmbeddingsTapestryMaxRetrievedTokens(int.MaxValue));

    }

    [Fact]
    public void SettingsFingerprint_ChangesWithEveryTreeShapingBound()
    {

        string baseline = TapestryHash.OfSettings(5, 8, 24, 256, 512, 768);

        Assert.NotEqual(baseline, TapestryHash.OfSettings(6, 8, 24, 256, 512, 768));

        Assert.NotEqual(baseline, TapestryHash.OfSettings(5, 9, 24, 256, 512, 768));

        Assert.NotEqual(baseline, TapestryHash.OfSettings(5, 8, 25, 256, 512, 768));

        Assert.NotEqual(baseline, TapestryHash.OfSettings(5, 8, 24, 257, 512, 768));

        Assert.NotEqual(baseline, TapestryHash.OfSettings(5, 8, 24, 256, 513, 768));

        Assert.NotEqual(baseline, TapestryHash.OfSettings(5, 8, 24, 256, 512, 1_536));

        Assert.Equal(baseline, TapestryHash.OfSettings(5, 8, 24, 256, 512, 768));

    }

    [Fact]
    public void ChildMembershipHash_IsOrderIndependentButModelAndRecipeSensitive()
    {

        string forward = TapestryHash.OfChildMembership(["b", "a", "c"], TapestryHash.SummaryRecipeVersion, "fast");

        string reverse = TapestryHash.OfChildMembership(["c", "b", "a"], TapestryHash.SummaryRecipeVersion, "fast");

        Assert.Equal(forward, reverse);

        Assert.NotEqual(forward, TapestryHash.OfChildMembership(["a", "b"], TapestryHash.SummaryRecipeVersion, "fast"));

        Assert.NotEqual(forward, TapestryHash.OfChildMembership(["a", "b", "c"], "tapestry-summary-v2", "fast"));

        Assert.NotEqual(forward, TapestryHash.OfChildMembership(["a", "b", "c"], TapestryHash.SummaryRecipeVersion, "slow"));

    }

    [Fact]
    public void CorpusFingerprint_ReactsToEveryLeafEditAndIsOrderIndependent()
    {

        TapestryLeafSource[] leaves =
        [
            new("s1", "a.cs", "alpha", TapestryHash.OfContent("alpha"), null),
            new("s2", "b.cs", "beta", TapestryHash.OfContent("beta"), null),
        ];

        string baseline = TapestryHash.OfCorpus(leaves);

        Assert.Equal(baseline, TapestryHash.OfCorpus([.. leaves.Reverse()]));

        Assert.NotEqual(
            baseline,
            TapestryHash.OfCorpus(
            [
                leaves[0],
                new("s2", "b.cs", "beta edited", TapestryHash.OfContent("beta edited"), null),
            ]));

        Assert.NotEqual(baseline, TapestryHash.OfCorpus([leaves[0]]));

    }

}
