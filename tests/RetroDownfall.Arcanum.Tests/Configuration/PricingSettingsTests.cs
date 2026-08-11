using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class PricingSettingsTests
{
    [Fact]
    public void ResolveForModel_UsesCaseInsensitiveOverride()
    {
        ModelPricingEntry configured = new() { InputPer1M = 3m };
        PricingSettings pricing = new()
        {
            ModelPricing = new Dictionary<string, ModelPricingEntry>
            {
                ["Reasoner"] = configured,
            },
        };

        Assert.Same(configured, pricing.ResolveForModel("reasoner"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown")]
    public void ResolveForModel_UsesDefaultForUnmappedModel(string? model)
    {
        ModelPricingEntry fallback = new() { OutputPer1M = 7m };
        PricingSettings pricing = new()
        {
            DefaultPricing = fallback,
        };

        Assert.Same(fallback, pricing.ResolveForModel(model));
    }

    [Fact]
    public void ModelPricing_NullAssignmentKeepsAnEmptyMap()
    {
        ModelPricingEntry fallback = new() { OutputPer1M = 11m };
        PricingSettings pricing = new()
        {
            DefaultPricing = fallback,
            ModelPricing = null!,
        };

        Assert.Empty(pricing.ModelPricing);
        Assert.Same(fallback, pricing.ResolveForModel("mistral:latest"));
    }
}
