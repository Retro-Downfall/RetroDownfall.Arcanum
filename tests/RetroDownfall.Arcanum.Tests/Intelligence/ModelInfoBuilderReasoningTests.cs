using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// <c>maxBudgetTokens</c> is only meaningful next to the wire dialect that says how a budget is sent.
/// Advertising the number on its own tells a client it may request a reasoning budget on a model with
/// no way to carry one, which is a promise the turn cannot keep.
/// </summary>
/// <remarks>
/// Startup validation now refuses that pairing, so this is defence in depth rather than the only
/// guard — but <c>/api/models</c> and <c>/v1/models</c> are the published capability surface, and the
/// builder projecting the two fields independently is what let them disagree in the first place.
/// </remarks>
public sealed class ModelInfoBuilderReasoningTests
{

    [Fact]
    public void A_model_with_a_wire_dialect_advertises_its_budget_ceiling()
    {

        List<ModelInfoDto> models = ModelInfoBuilder.BuildModelInfoList(
            SettingsFor(new ModelReasoningSettings
            {
                WireDialect = ReasoningWireDialect.OpenRouter,
                MaxBudgetTokens = 65_536,
            }));

        ModelInfoDto model = Assert.Single(models);

        Assert.Equal(ReasoningWireDialect.OpenRouter, model.WireDialect);

        Assert.Equal(65_536, model.MaxBudgetTokens);

    }

    [Fact]
    public void A_budget_ceiling_without_a_wire_dialect_is_not_advertised()
    {

        List<ModelInfoDto> models = ModelInfoBuilder.BuildModelInfoList(
            SettingsFor(new ModelReasoningSettings
            {
                WireDialect = null,
                MaxBudgetTokens = 65_536,
            }));

        ModelInfoDto model = Assert.Single(models);

        Assert.Null(model.WireDialect);

        Assert.Null(model.MaxBudgetTokens);

    }

    [Fact]
    public void A_model_without_reasoning_settings_advertises_neither()
    {

        List<ModelInfoDto> models = ModelInfoBuilder.BuildModelInfoList(SettingsFor(reasoning: null));

        ModelInfoDto model = Assert.Single(models);

        Assert.Null(model.WireDialect);

        Assert.Null(model.MaxBudgetTokens);

    }

    private static ArcanumSettings SettingsFor(ModelReasoningSettings? reasoning) =>
        new()
        {

            Providers =
            [

                new ProviderSettings
                {

                    Name = "provider",

                    Type = AiProviderKind.OpenAICompatible,

                    Endpoint = "https://example.test/v1",

                    Models =
                    [

                        new ModelEntry("reasoner")
                        {

                            Reasoning = reasoning,

                        },

                    ],

                },

            ],

        };

}
