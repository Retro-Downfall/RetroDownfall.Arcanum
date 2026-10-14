using System.Collections.Immutable;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.LongRest;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

// The local context-window gate that runs before an attempt is prepared: it agrees with the planner's budget and refuses with the
// typed code and the remedy, and never with content.
public sealed class LongRestWindowGateTests
{
    private static readonly CovenantDigest Recipe = new(new byte[32]);

    private static readonly ModelTokenEstimator Estimator = new(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance));

    private static ProviderSettings Provider(int window, bool catalog) => new()
    {
        Name = "provider",
        Type = AiProviderKind.OpenAICompatible,
        Endpoint = catalog ? "https://api.openai.com/v1" : "https://provider.test/v1",
        Models = [catalog ? "gpt-4o" : "model"],
        ContextWindowLimit = window,
    };

    [Theory]
    [InlineData(6775, false)]
    [InlineData(8192, false)]
    [InlineData(16384, false)]
    [InlineData(32768, false)]
    [InlineData(131072, false)]
    [InlineData(6775, true)]
    [InlineData(8192, true)]
    [InlineData(131072, true)]
    public void A_request_filled_to_the_window_derived_budget_passes_the_local_window_gate(int window, bool catalog)
    {
        int budget = LongRestPromptLayout.InputBudgetUtf8Bytes(window);

        ProviderSettings provider = Provider(window, catalog);

        string model = catalog ? "gpt-4o" : "model";

        Random random = new(window);

        long overhead = LongRestPromptLayout.AdjudicationMemoryBytes(LongRestPackingData.Leaf(0, string.Empty));

        int first = (int)((budget - LongRestPromptLayout.FixedBytes(LongRestProviderPayloadKind.Adjudication) - 2 * overhead) / 2);

        int second = (int)(budget - LongRestPromptLayout.FixedBytes(LongRestProviderPayloadKind.Adjudication) - 2 * overhead) - first;

        ImmutableArray<LongRestSourceLeaf> pack = [LongRestPackingData.Leaf(0, LongRestPackingData.Content(random, first)), LongRestPackingData.Leaf(1, LongRestPackingData.Content(random, second))];

        LongRestProviderPayload payload = LongRestPackingData.AdjudicationPayload(pack, budget).Value;

        ContextTokenBreakdown breakdown = Estimator.EstimateLongRestChat(provider, model, payload);

        Assert.True(ModelCallExecutor.CheckLongRestWindow(new LongRestProviderAdmission.Chat(new(provider, model, 2048, 0, breakdown))).IsSuccess);
    }

    [Fact]
    public void A_request_one_window_too_small_is_refused_with_the_typed_code_and_the_remedy()
    {
        ImmutableArray<LongRestSourceLeaf> pack = [LongRestPackingData.Leaf(0, new string('x', 12000)), LongRestPackingData.Leaf(1, new string('y', 12000))];

        LongRestProviderPayload payload = LongRestPackingData.AdjudicationPayload(pack, LongRestDiscoveryLimits.ProviderInputUtf8Bytes).Value;

        ProviderSettings provider = Provider(8192, catalog: false);

        ContextTokenBreakdown breakdown = Estimator.EstimateLongRestChat(provider, "model", payload);

        Result refused = ModelCallExecutor.CheckLongRestWindow(new LongRestProviderAdmission.Chat(new(provider, "model", 2048, 0, breakdown)));

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.LongRest.ModelContextTooSmall, refused.Error.Code);

        Assert.Contains("contextWindowLimit", refused.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("xxxx", refused.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_embedding_input_the_provider_window_cannot_hold_is_refused_before_any_effect_row_and_an_unset_window_is_not()
    {
        LongRestProviderAdmission Admit(int tokens, int window) => new LongRestProviderAdmission.Embedding(Provider(window, catalog: false), "embedding", 64, tokens, Recipe);

        Assert.True(ModelCallExecutor.CheckLongRestWindow(Admit(8192, 8192)).IsSuccess);

        Result refused = ModelCallExecutor.CheckLongRestWindow(Admit(8193, 8192));

        Assert.Equal(ErrorCodes.LongRest.ModelContextTooSmall, refused.Error.Code);

        Assert.True(ModelCallExecutor.CheckLongRestWindow(Admit(100000, 0)).IsSuccess);

        Assert.Equal(ErrorCodes.LongRest.InvalidAdmission, ModelCallExecutor.CheckLongRestWindow(Admit(0, 8192)).Error.Code);
    }
}
