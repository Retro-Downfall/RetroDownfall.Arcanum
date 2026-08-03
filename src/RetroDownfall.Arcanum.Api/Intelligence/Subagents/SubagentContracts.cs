using RetroDownfall.Arcanum.Core.Intelligence.Models;

namespace RetroDownfall.Arcanum.Api.Intelligence.Subagents;

public static class SubagentFailureCodes
{
    public const string BudgetExhausted = "Subagent.BudgetExhausted";

    public const string DurableStartFailed = "Subagent.DurableStartFailed";

    public const string ChildFailed = "Subagent.ChildFailed";

    public const string Cancelled = "Subagent.Cancelled";
}

public sealed record SubagentRunRequest(
    string Prompt,
    string? Model,
    IReadOnlyList<AttachedFileDto> Files,
    long? MaxTokens,
    decimal? MaxCostUsd,
    IReadOnlySet<Guid>? AttachmentAllowlist = null);

public sealed record SubagentRunResult(
    bool Success,
    string Summary,
    Guid RunId,
    DelegatedManaUsage Usage,
    string? FailureCode);

public interface ISubagentRunner
{
    Task<SubagentRunResult> RunAsync(
        SubagentRunRequest request,
        CancellationToken cancellationToken);
}
