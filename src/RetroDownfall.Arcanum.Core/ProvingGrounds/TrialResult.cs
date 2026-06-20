using RetroDownfall.Arcanum.Core.Intelligence.Models;

namespace RetroDownfall.Arcanum.Core.ProvingGrounds;

public sealed record InquisitorVerdict(string Kind, string? Label, bool Passed, string Detail);

public sealed record TrialResult(
    string TrialName,
    TrialTargetKind TargetKind,
    string Target,
    bool Passed,
    string Output,
    IReadOnlyList<InquisitorVerdict> Verdicts,
    int InquisitorsPassed,
    int InquisitorsTotal,
    ChatCompletionUsage? Usage);
