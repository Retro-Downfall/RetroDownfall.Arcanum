namespace RetroDownfall.Arcanum.Core.ProvingGrounds;

public enum TrialTargetKind
{

    Spell,

    Prompt,

    ApprenticeGoal,

}

public sealed record Trial(
    TrialTargetKind TargetKind,
    string Target,
    IReadOnlyList<Inquisitor> Inquisitors,
    Dictionary<string, string>? Variables = null,
    string? Model = null,
    string? Workspace = null,
    string? Name = null);
