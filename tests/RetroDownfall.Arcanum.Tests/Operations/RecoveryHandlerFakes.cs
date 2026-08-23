using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Conclave;

namespace RetroDownfall.Arcanum.Tests.Operations;

/// <summary>
/// Records inference-run transitions with the same compare-and-set rule the SQL writer applies, so a
/// second recovery pass cannot silently downgrade a run that already completed.
/// </summary>
internal sealed class FakeTurnRunWriter : ITurnRunWriter
{
    private readonly Dictionary<Guid, InferenceRunStatus> _runs = [];

    public List<Guid> AbandonAttempts { get; } = [];

    public List<BillableOperationRecord> Billed { get; } = [];

    public void SeedRun(Guid runId, InferenceRunStatus status) => _runs[runId] = status;

    public InferenceRunStatus? StatusOf(Guid runId) =>
        _runs.TryGetValue(runId, out InferenceRunStatus status) ? status : null;

    public Task<Guid> StartRunAsync(InferenceRunStart start, CancellationToken cancellationToken = default)
    {
        Guid id = Guid.NewGuid();

        _runs[id] = InferenceRunStatus.Running;

        return Task.FromResult(id);
    }

    public Task CompleteRunAsync(Guid runId, InferenceRunStatus status, CancellationToken cancellationToken = default)
    {
        _runs[runId] = status;

        return Task.CompletedTask;
    }

    public Task<bool> TryAbandonRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        AbandonAttempts.Add(runId);

        if (!_runs.TryGetValue(runId, out InferenceRunStatus status)
            || status != InferenceRunStatus.Running)
        {
            return Task.FromResult(false);
        }

        _runs[runId] = InferenceRunStatus.Abandoned;

        return Task.FromResult(true);
    }

    public Task<Guid> RecordBillableOperationAsync(
        BillableOperationRecord operation,
        CancellationToken cancellationToken = default)
    {
        Billed.Add(operation);

        return Task.FromResult(Guid.NewGuid());
    }
}

internal sealed class FakeBudgetReservationService : IBudgetReservationService
{
    public List<Guid> Released { get; } = [];

    public Task<Result<BudgetReservation>> ReserveAsync(
        BudgetReservationRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Result> AdjustAsync(
        Guid reservationId,
        decimal reservedUsd,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task ReconcileAsync(Guid reservationId, decimal actualCostUsd, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task ReleaseAsync(Guid reservationId, CancellationToken cancellationToken = default)
    {
        Released.Add(reservationId);

        return Task.CompletedTask;
    }

    public Task<decimal> GetTodayCommittedSpendAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(0m);

    public Task<decimal> GetTodayOutstandingReservationsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(0m);

    public Task<int> SweepExpiredAsync(DateTimeOffset utcNow, CancellationToken cancellationToken = default) =>
        Task.FromResult(0);
}

internal sealed class FakeIdempotencyClaimStore : IIdempotencyClaimStore
{
    private readonly Dictionary<Guid, IdempotencyClaim> _claims = [];

    public List<Guid> AbandonedClaims { get; } = [];

    public IdempotencyClaim Seed(
        IdempotencyClaimState state,
        bool terminalStreamComplete,
        string ownerId = "dead-process")
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        IdempotencyClaim claim = new(
            Guid.NewGuid(),
            ClaimKeyHash: "claim-hash",
            FingerprintHash: "fingerprint-hash",
            state,
            ownerId,
            LeaseExpiresAt: now,
            HeartbeatAt: now,
            RunId: null,
            StatusCode: state == IdempotencyClaimState.Completed ? 200 : null,
            ContentType: state == IdempotencyClaimState.Completed ? "application/json" : null,
            ResponseBody: state == IdempotencyClaimState.Completed ? "{}" : null,
            terminalStreamComplete,
            CreatedAt: now,
            UpdatedAt: now);

        _claims[claim.Id] = claim;

        return claim;
    }

    public IdempotencyClaim? StateOf(Guid claimId) =>
        _claims.TryGetValue(claimId, out IdempotencyClaim? claim) ? claim : null;

    public Task<IdempotencyClaim?> TryGetAsync(string claimKeyHash, CancellationToken cancellationToken = default) =>
        Task.FromResult(_claims.Values.FirstOrDefault(claim => claim.ClaimKeyHash == claimKeyHash));

    public Task<IdempotencyClaim?> GetByIdAsync(Guid claimId, CancellationToken cancellationToken = default) =>
        Task.FromResult(StateOf(claimId));

    public Task<IdempotencyClaimAcquireResult> TryAcquireAsync(
        IdempotencyClaimAcquireRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<bool> HeartbeatAsync(
        Guid claimId,
        string ownerId,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    public Task CompleteAsync(
        Guid claimId,
        string ownerId,
        int statusCode,
        string? contentType,
        string responseBody,
        bool terminalStreamValid,
        Guid? runId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task MarkFailedAsync(Guid claimId, string ownerId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task MarkAbandonedAsync(Guid claimId, string ownerId, CancellationToken cancellationToken = default)
    {
        AbandonedClaims.Add(claimId);

        if (_claims.TryGetValue(claimId, out IdempotencyClaim? claim) && claim.OwnerId == ownerId)
        {
            _claims[claimId] = claim with { State = IdempotencyClaimState.Abandoned };
        }

        return Task.CompletedTask;
    }

    public Task<bool> TryReclaimAsync(
        Guid claimId,
        string newOwnerId,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default)
    {
        if (!_claims.TryGetValue(claimId, out IdempotencyClaim? claim)
            || claim.State is not (IdempotencyClaimState.Claimed or IdempotencyClaimState.Running))
        {
            return Task.FromResult(false);
        }

        _claims[claimId] = claim with { OwnerId = newOwnerId, LeaseExpiresAt = leaseExpiresAt };

        return Task.FromResult(true);
    }

    public Task LinkRunAsync(Guid claimId, Guid runId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<int> DeleteExpiredAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default) =>
        Task.FromResult(0);
}

internal sealed class FakeApprenticeRepository : IApprenticeRepository
{
    private readonly Dictionary<Guid, Apprentice> _apprentices = [];

    public Apprentice Seed(Guid id, string status, string? checkpointData)
    {
        Apprentice apprentice = new()
        {
            Id = id,
            Name = "Test apprentice",
            Goal = "Recover",
            Status = status,
            CheckpointData = checkpointData,
        };

        _apprentices[id] = apprentice;

        return apprentice;
    }

    public Task<Apprentice?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_apprentices.TryGetValue(id, out Apprentice? apprentice) ? apprentice : null);

    public Task<ListPageResult<Apprentice>> ListAsync(
        Guid? campaignId,
        string? status,
        int? limit = null,
        DateTimeOffset? beforeUpdatedAt = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Apprentice> AddAsync(Apprentice apprentice, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Apprentice> UpdateAsync(Apprentice apprentice, CancellationToken cancellationToken = default)
    {
        _apprentices[apprentice.Id] = apprentice;

        return Task.FromResult(apprentice);
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_apprentices.Remove(id));

    public Task<IReadOnlyList<Apprentice>> GetResumableAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Apprentice>>([.. _apprentices.Values]);

    public Task<IReadOnlyList<Apprentice>> GetInterruptedPlanningAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Apprentice>>([]);
}

