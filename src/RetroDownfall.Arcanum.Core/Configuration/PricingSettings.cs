namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Runtime projection of <c>Arcanum:Cost:Pricing</c>. Defines per-model pricing and a default
/// fallback used by the hub to accumulate <see cref="CostCalculator"/> results per turn.
/// </summary>
public sealed record PricingSettings
{

    private Dictionary<string, ModelPricingEntry> _modelPricing =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-model pricing keyed by model name (e.g. <c>gpt-4o</c>, <c>mistral:latest</c>). A provider's
    /// resolved model name is the lookup key; if no entry exists, <see cref="DefaultPricing"/> is used.
    /// </summary>
    public Dictionary<string, ModelPricingEntry> ModelPricing
    {

        get => _modelPricing;

        set => _modelPricing = new Dictionary<string, ModelPricingEntry>(
            value,
            StringComparer.OrdinalIgnoreCase);

    }

    /// <summary>
    /// Fallback pricing when a model is not present in <see cref="ModelPricing"/>. Default free
    /// (0.00 USD / 1M tokens) so unknown models do not block inference.
    /// </summary>
    public ModelPricingEntry DefaultPricing { get; set; } = new();

    /// <summary>Resolves configured model pricing, falling back for null, blank, or unmapped names.</summary>
    public ModelPricingEntry ResolveForModel(string? model) =>
        !string.IsNullOrWhiteSpace(model)
        && ModelPricing.TryGetValue(model, out ModelPricingEntry? pricing)
        && pricing is not null
            ? pricing
            : DefaultPricing;

}
