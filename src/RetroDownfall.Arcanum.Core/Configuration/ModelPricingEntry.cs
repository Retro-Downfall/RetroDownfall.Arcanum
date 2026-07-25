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

    /// <summary>
    /// USD cost per 1,000,000 reasoning tokens. Reasoning tokens are a subset of completion tokens;
    /// when unset, <see cref="OutputPer1M"/> is used.
    /// </summary>
    public decimal? ReasoningPer1M { get; set; }

    /// <summary>USD cost per 1,000,000 cached input tokens. Default 0.00 (not assumed free forever — set explicitly).</summary>
    public decimal CachedPer1M { get; set; } = 0.00m;

}
