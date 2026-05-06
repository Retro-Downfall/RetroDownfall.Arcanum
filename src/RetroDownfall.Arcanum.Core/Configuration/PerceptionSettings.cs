namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record PerceptionSettings
{

    public int MaxEnumerationSteps { get; init; } = 50_000;

    public int MaxTableOfContentsLines { get; init; } = 20;

}
