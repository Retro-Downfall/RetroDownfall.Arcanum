using System.Collections.Immutable;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.LongRest;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

// A pack the packer builds is a request the renderer renders: the packer and the renderer measure one layout.
public sealed class LongRestPackedGroupParityTests
{
    public static TheoryData<int> Budgets => LongRestPromptLayoutParityTests.Budgets;

    [Theory]
    [MemberData(nameof(Budgets))]
    public void Every_packed_group_renders_and_fits_its_budget(int budget)
    {
        Random random = new(budget);

        for (int round = 0; round < 25; round++)
        {
            LongRestSourceLeaf anchor = LongRestPackingData.Leaf(0, LongRestPackingData.Content(random, random.Next(1, budget / 2)), 0);

            ImmutableArray<LongRestSourceLeaf> neighbors = [.. Enumerable.Range(1, random.Next(1, 33))
                .Select(index => LongRestPackingData.Leaf(index, LongRestPackingData.Content(random, random.Next(1, Math.Max(2, budget / (round % 5 + 1)))), 0))];

            foreach (ImmutableArray<LongRestSourceLeaf> pack in LongRestGroupPacker.Pack(anchor, neighbors, budget, 920).Value.Packs)
            {
                Result<LongRestProviderPayload> payload = LongRestPackingData.AdjudicationPayload(pack, budget);

                Assert.True(payload.IsSuccess, "A packed group must build its adjudication payload.");

                Result<LongRestChatRequest> rendered = LongRestProviderCallFreezer.RenderChat(payload.Value);

                Assert.True(rendered.IsSuccess, "A packed group must render.");

                Assert.True(RenderedBytes(rendered.Value) <= budget);

                Assert.Equal(RenderedBytes(rendered.Value), LongRestPromptLayout.FixedBytes(LongRestProviderPayloadKind.Adjudication) + pack.Sum(LongRestPromptLayout.AdjudicationMemoryBytes));

                // The estimator can only take what the renderer produced.
                if (round % 5 == 0) _ = Estimator.EstimateLongRestChat(new ProviderSettings { Name = "provider", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://provider.test/v1", Models = ["model"], ContextWindowLimit = 131072 }, "model", payload.Value);
            }
        }
    }

    private static readonly ModelTokenEstimator Estimator = new(new InferenceTokenizerResolver(Microsoft.Extensions.Logging.Abstractions.NullLogger<InferenceTokenizerResolver>.Instance));

    private static long RenderedBytes(LongRestChatRequest request) => LongRestPromptLayout.RenderedBytes(request.SystemPrompt, request.UserPrompt, request.SchemaJson, request.SchemaName, request.SchemaDescription);
}
