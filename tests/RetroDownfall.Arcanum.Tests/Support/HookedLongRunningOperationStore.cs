using RetroDownfall.Arcanum.Core.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// A forwarding operation store that lets a test act at two exact points of a coordinator's run: right after it wins its lease,
/// and right after a checkpoint it saved is durable. Everything else is the wrapped store, unchanged.
/// </summary>
internal sealed class HookedLongRunningOperationStore(ILongRunningOperationStore inner) : ILongRunningOperationStore
{
    internal Action? AfterLeaseAcquired { get; set; }

    internal Func<Guid, byte[]?, Task>? AfterCheckpointSaved { get; set; }

    public async Task<LongRunningOperationLeaseResult> TryAcquireLeaseAsync(Guid id, string owner, DateTimeOffset now, DateTimeOffset expires, CancellationToken ct = default)
    {
        LongRunningOperationLeaseResult leased = await inner.TryAcquireLeaseAsync(id, owner, now, expires, ct);

        if (leased.Acquired) AfterLeaseAcquired?.Invoke();

        return leased;
    }

    public async Task<bool> SaveCheckpointAsync(Guid id, string owner, int expected, int version, byte[]? payload, string? reference,
        string summary, DateTimeOffset now, CancellationToken ct = default)
    {
        bool saved = await inner.SaveCheckpointAsync(id, owner, expected, version, payload, reference, summary, now, ct);

        if (saved && AfterCheckpointSaved is { } hook) await hook(id, payload);

        return saved;
    }

    public Task<LongRunningOperation?> GetAsync(Guid id, CancellationToken ct = default) => inner.GetAsync(id, ct);

    public Task<LongRunningOperation> CreateAsync(LongRunningOperationCreateRequest request, CancellationToken ct = default) => inner.CreateAsync(request, ct);

    public Task<LongRunningOperationRequestIdentityResult> ResolveOrCreateAsync(LongRunningOperationCreateRequest request, LongRunningOperationRequestIdentity identity, CancellationToken ct = default) => inner.ResolveOrCreateAsync(request, identity, ct);

    public Task<LongRunningOperation?> TryStartSingleFlightAsync(LongRunningOperationCreateRequest request, string owner, DateTimeOffset now, DateTimeOffset expires, CancellationToken ct = default) => inner.TryStartSingleFlightAsync(request, owner, now, expires, ct);

    public Task<LongRunningOperationRequestIdentity?> FindRequestIdentityAsync(Guid id, CancellationToken ct = default) => inner.FindRequestIdentityAsync(id, ct);

    public Task<LongRunningOperationRequestIdentityMatch?> FindByRequestedOperationIdAsync(Guid id, CancellationToken ct = default) => inner.FindByRequestedOperationIdAsync(id, ct);

    public Task<IReadOnlyList<LongRunningOperation>> ListAsync(LongRunningOperationQuery query, CancellationToken ct = default) => inner.ListAsync(query, ct);

    public Task<IReadOnlyList<LongRunningOperation>> FindExpiredAsync(DateTimeOffset now, int limit, CancellationToken ct = default) => inner.FindExpiredAsync(now, limit, ct);

    public Task<bool> HeartbeatAsync(Guid id, string owner, DateTimeOffset now, DateTimeOffset expires, CancellationToken ct = default) => inner.HeartbeatAsync(id, owner, now, expires, ct);

    public Task<bool> RenewLeaseAsync(Guid id, string owner, DateTimeOffset now, DateTimeOffset expires, CancellationToken ct = default) => inner.RenewLeaseAsync(id, owner, now, expires, ct);

    public Task<bool> TryTransitionAsync(Guid id, long revision, string? owner, LongRunningOperationState state, DateTimeOffset now, string? code = null, CancellationToken ct = default) => inner.TryTransitionAsync(id, revision, owner, state, now, code, ct);

    public Task<bool> RequestCancellationAsync(Guid id, long revision, DateTimeOffset now, CancellationToken ct = default) => inner.RequestCancellationAsync(id, revision, now, ct);

    public Task<bool> ResetForRetryAsync(Guid id, long revision, DateTimeOffset now, CancellationToken ct = default) => inner.ResetForRetryAsync(id, revision, now, ct);

    public Task<IReadOnlyList<LongRunningOperationCount>> GetCountsAsync(CancellationToken ct = default) => inner.GetCountsAsync(ct);
}
