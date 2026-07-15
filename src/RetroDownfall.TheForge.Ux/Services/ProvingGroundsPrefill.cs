using RetroDownfall.Arcanum.Core.ProvingGrounds;

namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>Optional prefill payload when opening the singleton Proving Grounds Workbench tab.</summary>
public sealed record ProvingGroundsPrefill(
    TrialTargetKind Kind,
    string Target,
    string? Workspace = null,
    string? Model = null,
    Dictionary<string, string>? Variables = null);
