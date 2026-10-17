using System.Collections.Immutable;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.LongRest;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

// The planner, the packer and the renderer measure one layout. These tests hold the real renderer and the real estimator against it.
public sealed class LongRestPromptLayoutParityTests
{
    private static readonly UTF8Encoding Strict = new(false, true);

    private static readonly CovenantDigest Recipe = new(new byte[32]);

    private static readonly ModelTokenEstimator Estimator = new(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance));

    // Budgets across the whole recipe range, with the 29-32 KiB band where the old planner and renderer disagreed.
    public static TheoryData<int> Budgets => new()
    {
        4096, 4097, 4600, 5328, 6144, 8192, 10000, 12452, 16384, 20000, 26699, 28000, 29000, 29500, 30000, 30500, 31000, 31500, 32000, 32500, 32700, 32767, 32768,
    };

    private static ProviderSettings Provider(int window, bool catalog) => new()
    {
        Name = "provider",
        Type = AiProviderKind.OpenAICompatible,
        Endpoint = catalog ? "https://api.openai.com/v1" : "https://provider.test/v1",
        Models = [catalog ? "gpt-4o" : "model"],
        ContextWindowLimit = window,
    };

    [Theory]
    [MemberData(nameof(Budgets))]
    public void Every_planned_fold_renders_and_fits_its_budget(int budget)
    {
        Random random = new(budget + 1);

        for (int round = 0; round < 25; round++)
        {
            int count = random.Next(2, 60);

            // Summaries are at most 8 KiB; a few sources are larger, as a memory can be.
            int ceiling = Math.Max(2, Math.Min(budget - 1500, round % 3 == 0 ? 12000 : 8192));

            ImmutableArray<LongRestHierarchyInput> page = [.. Enumerable.Range(1, count).Select(index =>
                new LongRestHierarchyInput(LongRestPackingData.Child(index, LongRestPackingData.Content(random, random.Next(1, ceiling))), round % 2 == 0 ? [] : [1, 0]))];

            Result<ImmutableArray<ImmutableArray<LongRestHierarchyChild>>> planned = LongRestHierarchy.PlanPage(page, 1, Recipe, budget, CancellationToken.None);

            if (planned.IsFailure)
            {
                // The only refusal a well-formed page may meet is a child that cannot fit a request alone.
                Assert.Equal(ErrorCodes.LongRest.ModelContextTooSmall, planned.Error.Code);

                Assert.Contains(page, input => LongRestPromptLayout.FixedBytes(LongRestProviderPayloadKind.Summary)
                    + LongRestPromptLayout.SummaryObservationBytes(input.Child.Content) > budget);

                continue;
            }

            Assert.Equal(count, planned.Value.Sum(group => group.Length));

            foreach (ImmutableArray<LongRestHierarchyChild> group in planned.Value.Where(group => group.Length > 1))
            {
                Result<LongRestProviderPayload> payload = LongRestPackingData.SummaryPayload(group, Recipe, budget);

                Assert.True(payload.IsSuccess, "A planned fold must build its summary payload.");

                Result<LongRestChatRequest> rendered = LongRestProviderCallFreezer.RenderChat(payload.Value);

                Assert.True(rendered.IsSuccess, "A planned fold must render.");

                Assert.True(RenderedBytes(rendered.Value) <= budget);

                Assert.Equal(RenderedBytes(rendered.Value), LongRestPromptLayout.FixedBytes(LongRestProviderPayloadKind.Summary) + group.Sum(child => LongRestPromptLayout.SummaryObservationBytes(child.Content)));

                if (round % 5 == 0) _ = Estimator.EstimateLongRestChat(Provider(131072, catalog: false), "model", payload.Value);
            }
        }
    }

    [Fact]
    public void The_payload_factories_refuse_what_the_renderer_would_refuse_instead_of_only_the_raw_content()
    {
        // Raw content of 32,500 bytes is under the 32,768 ceiling, but its request is not.
        LongRestSourceLeaf anchor = LongRestPackingData.Leaf(0, new string('x', 32500));

        ImmutableArray<LongRestSourceLeaf> pack = [anchor, LongRestPackingData.Leaf(1, "n")];

        Assert.True(LongRestPackingData.AdjudicationPayload(pack, LongRestDiscoveryLimits.ProviderInputUtf8Bytes).IsFailure);

        Assert.True(LongRestPackingData.AdjudicationPayload([LongRestPackingData.Leaf(0, new string('x', 30000)), LongRestPackingData.Leaf(1, "n")], LongRestDiscoveryLimits.ProviderInputUtf8Bytes).IsSuccess);

        LongRestHierarchyChild child = LongRestPackingData.Child(1, new string('x', 32720));

        Assert.True(LongRestPackingData.SummaryPayload([child, LongRestPackingData.Child(2, "n")], Recipe, LongRestDiscoveryLimits.ProviderInputUtf8Bytes).IsFailure);

        Assert.True(LongRestPackingData.SummaryPayload([LongRestPackingData.Child(1, new string('x', 100)), LongRestPackingData.Child(2, "n")], Recipe, 4095).IsFailure);
    }

    [Fact]
    public void A_payload_renders_against_the_budget_it_was_built_with()
    {
        ImmutableArray<LongRestSourceLeaf> pack = [LongRestPackingData.Leaf(0, new string('x', 2500)), LongRestPackingData.Leaf(1, new string('y', 2500))];

        Result<LongRestProviderPayload> wide = LongRestPackingData.AdjudicationPayload(pack, 8192);

        Assert.True(wide.IsSuccess);

        Assert.Equal(8192, wide.Value.InputBudgetBytes);

        Assert.True(LongRestProviderCallFreezer.RenderChat(wide.Value).IsSuccess);

        Assert.True(LongRestPackingData.AdjudicationPayload(pack, 4096).IsFailure);
    }

    // The constants the budget is derived from are the estimator's own worst case, not a guess.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_layout_constants_bound_every_shipped_tokenization_profile(bool catalog)
    {
        ResolvedModelTokenizationProfile profile = Estimator.ResolveEffectiveProfile(Provider(8192, catalog), catalog ? "gpt-4o" : "model");

        Assert.Equal(catalog ? ModelTokenizationProfileType.ExactLocalTokenizer : ModelTokenizationProfileType.UnknownFallback, profile.Type);

        Assert.True(profile.SafetyMarginPercent <= LongRestPromptLayout.TokenSafetyMarginPercent);

        Assert.True(2 * profile.PerMessageOverheadTokens + profile.ProviderFramingTokens + profile.StopTokenOverheadTokens <= LongRestPromptLayout.FramingTokens);

        Assert.True(ArcanumRuntimeDefaults.Intelligence.EstimatedTokenSafetyMarginPercent <= LongRestPromptLayout.TokenSafetyMarginPercent);
    }

    [Theory]
    [InlineData(6775, false)]
    [InlineData(8192, false)]
    [InlineData(16384, false)]
    [InlineData(32768, false)]
    [InlineData(65536, false)]
    [InlineData(131072, false)]
    [InlineData(6775, true)]
    [InlineData(8192, true)]
    [InlineData(32768, true)]
    [InlineData(131072, true)]
    public void A_request_filled_to_the_window_derived_budget_passes_the_context_budget_check(int window, bool catalog)
    {
        int budget = LongRestPromptLayout.InputBudgetUtf8Bytes(window);

        ProviderSettings provider = Provider(window, catalog);

        string model = catalog ? "gpt-4o" : "model";

        Random random = new(window);

        // Two hard-to-tokenize sources sized so that the rendered request is exactly the budget.
        long overhead = LongRestPromptLayout.AdjudicationMemoryBytes(LongRestPackingData.Leaf(0, string.Empty));

        int first = (int)((budget - LongRestPromptLayout.FixedBytes(LongRestProviderPayloadKind.Adjudication) - 2 * overhead) / 2);

        int second = (int)(budget - LongRestPromptLayout.FixedBytes(LongRestProviderPayloadKind.Adjudication) - 2 * overhead) - first;

        ImmutableArray<LongRestSourceLeaf> pack = [LongRestPackingData.Leaf(0, LongRestPackingData.Content(random, first)), LongRestPackingData.Leaf(1, LongRestPackingData.Content(random, second))];

        LongRestProviderPayload payload = LongRestPackingData.AdjudicationPayload(pack, budget).Value;

        long rendered = RenderedBytes(LongRestProviderCallFreezer.RenderChat(payload).Value);

        Assert.Equal(budget, rendered);

        ContextTokenBreakdown breakdown = Estimator.EstimateLongRestChat(provider, model, payload);

        Assert.True(TurnContextGuards.CheckContextBudget(breakdown, window).IsSuccess,
            $"{breakdown.TotalTokens} accounted tokens exceed the {window}-token window for a {rendered}-byte request.");
    }

    private static long RenderedBytes(LongRestChatRequest request) => LongRestPromptLayout.RenderedBytes(request.SystemPrompt, request.UserPrompt, request.SchemaJson, request.SchemaName, request.SchemaDescription);
}
