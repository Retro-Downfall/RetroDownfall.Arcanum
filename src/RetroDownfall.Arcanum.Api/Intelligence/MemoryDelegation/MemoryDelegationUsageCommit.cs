using Microsoft.Extensions.AI;

namespace RetroDownfall.Arcanum.Api.Intelligence.MemoryDelegation;

/// <summary>One physical call's acknowledged handoff to the existing turn accounting owner.</summary>
internal sealed class MemoryDelegationUsageCommit(Func<UsageDetails, Task> commit)
{
    private Task? _commit;

    internal Task? Attempt => _commit;

    internal Task CommitAsync(UsageDetails? usage)
    {
        if (usage is null) return Task.CompletedTask;

        // Only numeric provider-reported usage crosses the handoff, never response content,
        // SDK additional metadata, read authority, or a fresh independently-owned ledger.
        return _commit ??= CommitCoreAsync(new UsageDetails
        {
            InputTokenCount = usage.InputTokenCount,
            OutputTokenCount = usage.OutputTokenCount,
            TotalTokenCount = usage.TotalTokenCount,
            CachedInputTokenCount = usage.CachedInputTokenCount,
            ReasoningTokenCount = usage.ReasoningTokenCount,
        });
    }

    private async Task CommitCoreAsync(UsageDetails usage) => await commit(usage).ConfigureAwait(false);
}
