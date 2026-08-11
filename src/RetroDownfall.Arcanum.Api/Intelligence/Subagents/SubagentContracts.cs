using RetroDownfall.Arcanum.Core.Intelligence.Models;

namespace RetroDownfall.Arcanum.Api.Intelligence.Subagents;

public static class SubagentFailureCodes
{
    public const string BudgetExhausted = "Subagent.BudgetExhausted";

    public const string DurableStartFailed = "Subagent.DurableStartFailed";

    public const string ChildFailed = "Subagent.ChildFailed";

    public const string Cancelled = "Subagent.Cancelled";
}

/// <summary>
/// The delegated child turn. Parent attachment authority is enforced at parse time by
/// <c>ArcanumDelegateTaskTool</c> — a file naming an attachment id outside the parent turn's
/// materialized allowlist fails the whole call before the runner is reached — so no allowlist
/// travels with the request and the runner has nothing left to filter.
/// </summary>
public sealed record SubagentRunRequest(
    string Prompt,
    string? Model,
    IReadOnlyList<AttachedFileDto> Files,
    long? MaxTokens,
    decimal? MaxCostUsd);

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
