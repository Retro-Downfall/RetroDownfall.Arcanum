using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Host-only runtime entry. Its ids are authenticated against committed authority at every effect.</summary>
internal sealed class GuildLocalInferenceExecution(GuildQuestStore store, GuildQuestDriver driver, GuildQuestDispatchRecord dispatch)
{
    private GuildQuestDriver _driver = driver;

    private readonly SemaphoreSlim _physicalCalls = new(1, 1);

    internal async ValueTask<IDisposable> AcquirePhysicalCallAsync(CancellationToken cancellationToken)
    {
        await _physicalCalls.WaitAsync(cancellationToken).ConfigureAwait(false);

        return new PhysicalCallLease(_physicalCalls);
    }

    private sealed class PhysicalCallLease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }

    internal GuildQuestStore Store { get; } = store;

    internal GuildQuestDriver Driver => Volatile.Read(ref _driver);

    internal GuildQuestDispatchRecord Dispatch { get; } = dispatch;

    // Scheduling cooperation only: this host callback conveys no execution or disclosure authority.
    internal Func<GuildLocalInferenceExecution, CancellationToken, ValueTask<IAsyncDisposable>>? ChildWait { get; set; }

    internal ValueTask<IAsyncDisposable> SuspendForChildAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ChildWait is { } suspend ? suspend(this, cancellationToken)
            : ValueTask.FromResult<IAsyncDisposable>(UnconstrainedChildWait.Instance);
    }

    private sealed class UnconstrainedChildWait : IAsyncDisposable
    {
        internal static readonly UnconstrainedChildWait Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal bool TryRefreshDriver(GuildQuestDriver next)
    {
        if (next is null) return false;

        while (true)
        {
            GuildQuestDriver current = Driver;

            if (ReferenceEquals(current, next)) return true;

            if (!ReferenceEquals(current.Execution, next.Execution) || !ReferenceEquals(current.Caller, next.Caller)
                || current.LeaseOwner != next.LeaseOwner || next.OperationRevision <= current.OperationRevision) return false;

            if (ReferenceEquals(Interlocked.CompareExchange(ref _driver, next, current), current)) return true;
        }
    }
}

internal sealed record GuildLocalInferenceFile(string Name, string Text);

internal sealed record GuildLocalInferenceMaterialization(string Goal, ImmutableArray<GuildLocalInferenceFile> Files,
    GuildLocalExecutionBinding Binding, GuildQuestDispatchRecord Dispatch)
{
    internal Guid? CampaignId { get; init; }

    internal RetroDownfall.Arcanum.Core.Guild.GuildAuthorityKind GoalAuthority { get; init; } = RetroDownfall.Arcanum.Core.Guild.GuildAuthorityKind.Operator;
}

/// <summary>Host-assembled planner/unit input. It cannot select a provider, model or grant.</summary>
internal sealed record GuildLocalInferenceTurn(string Prompt, bool DisableAllTools = true);

internal interface IGuildLocalInferenceRuntime
{
    bool IsAvailable => false;

    Task<Result<PromptTurnResult>> ExecuteAsync(GuildLocalInferenceExecution execution, GuildLocalExecutionInput input, CancellationToken cancellationToken);

    Task<Result<PromptTurnResult>> ExecuteTurnAsync(GuildLocalInferenceExecution execution, GuildLocalExecutionInput input,
        GuildLocalInferenceTurn turn, CancellationToken cancellationToken);
}
