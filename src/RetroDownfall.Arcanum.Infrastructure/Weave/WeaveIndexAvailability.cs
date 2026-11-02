namespace RetroDownfall.Arcanum.Infrastructure.Weave;

/// <summary>Stable status of the managed semantic search over durable embedding BLOBs.</summary>
public sealed class WeaveIndexAvailability
{
    public const string ModeDisabled = "disabled";

    public const string ModeManaged = "managed";

    public const string ModeUnavailable = "unavailable";

    public string Mode => ModeManaged;

    public string Diagnostic => "Complete streamed managed SIMD search over durable embedding BLOBs; no total row budget.";
}
