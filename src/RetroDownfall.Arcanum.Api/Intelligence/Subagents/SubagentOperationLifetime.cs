using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Operations;

namespace RetroDownfall.Arcanum.Api.Intelligence.Subagents;

/// <summary>Observes only the protected child's durable operation; carries no memory authority.</summary>
internal sealed class SubagentOperationLifetime : IAsyncDisposable
{
    private readonly IServiceScopeFactory _scopes;

    private readonly Guid _id;

    private readonly string _owner;

    private readonly TimeProvider _clock;

    private readonly CancellationToken _outer;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _reads;

    private readonly CancellationTokenSource _dispatch;

    private readonly Task _observer;

    private string? _failure;

    internal SubagentOperationLifetime(IServiceScopeFactory scopes, Guid id, string owner, TimeProvider clock, CancellationToken outer)
    {
        _scopes = scopes;
        _id = id;
        _owner = owner;
        _clock = clock;
        _outer = outer;
        _reads = CancellationTokenSource.CreateLinkedTokenSource(outer, _stop.Token);
        _dispatch = CancellationTokenSource.CreateLinkedTokenSource(outer);
        _observer = ObserveAsync();
    }

    internal CancellationToken Token => _dispatch.Token;

    internal string FailureCode => Volatile.Read(ref _failure) ?? SubagentFailureCodes.ChildFailed;

    internal async Task CancelAsync(string failureCode)
    {
        Interlocked.CompareExchange(ref _failure, failureCode, null);
        try { await _dispatch.CancelAsync().ConfigureAwait(false); }
        catch (AggregateException) { /* Cancellation is set even when a consumer callback fails. */ }
    }

    private async Task<LongRunningOperation?> ReadOwnedAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
        var row = await scope.ServiceProvider.GetRequiredService<ILongRunningOperationStore>().GetAsync(_id, ct).ConfigureAwait(false);
        return row is not null && row.Kind == LongRunningOperationKinds.Subagent && row.LeaseOwner == _owner
            && row.LeaseExpiresAt > _clock.GetUtcNow()
            && row.State is LongRunningOperationState.Running or LongRunningOperationState.Cancelling ? row : null;
    }

    internal async Task CheckAsync()
    {
        try
        {
            var row = await ReadOwnedAsync(_reads.Token).ConfigureAwait(false);
            if (row is null || row.State != LongRunningOperationState.Running)
                await CancelAsync(row?.State == LongRunningOperationState.Cancelling ? SubagentFailureCodes.Cancelled : SubagentFailureCodes.ChildFailed).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_reads.IsCancellationRequested) { }
        catch (Exception) { await CancelAsync(SubagentFailureCodes.ChildFailed).ConfigureAwait(false); }
    }

    private async Task ObserveAsync()
    {
        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromSeconds(1), _clock);
            while (await timer.WaitForNextTickAsync(_reads.Token).ConfigureAwait(false))
            {
                await CheckAsync().ConfigureAwait(false);
                if (Token.IsCancellationRequested) return;
            }
        }
        catch (OperationCanceledException) when (_reads.IsCancellationRequested) { }
        catch (Exception) { await CancelAsync(SubagentFailureCodes.ChildFailed).ConfigureAwait(false); }
    }

    internal async Task StopAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _observer.ConfigureAwait(false);
    }

    /// <returns>Null only after a successful owned completion; otherwise a content-free failure code.</returns>
    internal async Task<string?> SettleAsync(ILongRunningOperationCoordinator operations, bool success, string failureCode)
    {
        try
        {
            var row = await ReadOwnedAsync(_outer).ConfigureAwait(false);
            if (row is null) return FailureCode;
            if (row.State == LongRunningOperationState.Cancelling) failureCode = SubagentFailureCodes.Cancelled;
            else if (Volatile.Read(ref _failure) is { } stopped) failureCode = stopped;
            else if (success && await operations.CompleteAsync(_id, _owner, row.Revision, _outer).ConfigureAwait(false)) return null;

            // A cancellation can win the completion CAS after the read. Re-read its exact revision
            // without adopting another owner's operation or overwriting a completed/recovered row.
            row = await ReadOwnedAsync(_outer).ConfigureAwait(false);
            if (row is null) return failureCode;
            if (row.State == LongRunningOperationState.Cancelling) failureCode = SubagentFailureCodes.Cancelled;
            _ = await operations.FailAsync(_id, _owner, row.Revision, failureCode, _outer).ConfigureAwait(false);
            return failureCode;
        }
        catch (OperationCanceledException) when (_outer.IsCancellationRequested) { throw; }
        catch (Exception) { return SubagentFailureCodes.ChildFailed; }
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        finally
        {
            _dispatch.Dispose();
            _reads.Dispose();
            _stop.Dispose();
        }
    }
}
