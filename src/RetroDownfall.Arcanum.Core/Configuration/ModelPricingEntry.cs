namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Per-model pricing expressed as USD per 1,000,000 tokens. Used by <see cref="CostCalculator"/> to
/// accumulate spend in a decimal-safe way.
/// </summary>
public sealed record ModelPricingEntry
{

    /// <summary>USD cost per 1,000,000 input (prompt) tokens. Default 0.00.</summary>
    public decimal InputPer1M { get; set; } = 0.00m;

    /// <summary>USD cost per 1,000,000 output (completion) tokens. Default 0.00.</summary>
    public decimal OutputPer1M { get; set; } = 0.00m;

}
