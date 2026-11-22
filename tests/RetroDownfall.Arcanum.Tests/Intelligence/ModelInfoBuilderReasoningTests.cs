using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ModelInfoBuilderReasoningTests
{

    [Fact]
    public void A_model_with_a_wire_dialect_advertises_the_dialect()
    {

        List<ModelInfoDto> models = ModelInfoBuilder.BuildModelInfoList(
            SettingsFor(new ModelReasoningSettings
            {
                WireDialect = ReasoningWireDialect.OpenRouter,
            }));

        ModelInfoDto model = Assert.Single(models);

        Assert.Equal(ReasoningWireDialect.OpenRouter, model.WireDialect);

    }

    [Fact]
    public void A_model_without_reasoning_settings_does_not_advertise_a_dialect()
    {

        List<ModelInfoDto> models = ModelInfoBuilder.BuildModelInfoList(SettingsFor(reasoning: null));

        ModelInfoDto model = Assert.Single(models);

        Assert.Null(model.WireDialect);

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
