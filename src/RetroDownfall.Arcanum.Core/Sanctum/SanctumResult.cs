namespace RetroDownfall.Arcanum.Core.Sanctum;

public sealed record SanctumResult
{

    public bool Allowed { get; init; }

    public string? DenyReason { get; init; }

    public SanctumBreach? Breach { get; init; }

}
