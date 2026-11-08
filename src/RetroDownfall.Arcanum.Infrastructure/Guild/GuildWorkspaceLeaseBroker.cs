using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildWorkspaceLeaseBinding(GuildWorkspaceLeaseOwner Owner, string WorkspaceRoot, GuildVerifiedAuthority Authority,
    ImmutableArray<GuildPathScope> AllowedScopes);

/// <summary>Trusted composition rechecks the exact caller and live lineage while handing off the bounded effect.</summary>
internal interface IGuildWorkspaceLeaseHostPolicy
{
    Task<Result<T>> AuthorizeAsync<T>(GuildCallerContext caller, Guid workspaceId, GuildAuthorityAction action,
        Func<GuildWorkspaceLeaseBinding, CancellationToken, Task<Result<T>>> effect, CancellationToken ct);

    Task<Result<T>> RevalidateAsync<T>(GuildVerifiedAuthority authority, Guid workspaceId,
        Func<GuildWorkspaceLeaseBinding, CancellationToken, Task<Result<T>>> effect, CancellationToken ct);
}

/// <summary>Durable ownership and counted physical permits. The short gate never spans user I/O.</summary>
internal sealed partial class GuildWorkspaceLeaseBroker(GuildWorkspaceLeaseStore store, IWorkspaceLeaseIdentityResolver identities,
    IGuildWorkspaceLeaseHostPolicy policy, TimeProvider time, GuildWorkspaceLeaseHostLock host) : IGuildWorkspaceLeaseService, IAsyncDisposable
{
    internal static readonly TimeSpan NominalDuration = TimeSpan.FromMinutes(15);

    internal static readonly TimeSpan RenewalInterval = TimeSpan.FromMinutes(5);

    private Guid HostInstanceId => host.HostInstanceId;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly Dictionary<Guid, HeldLease> _held = [];

    private readonly Dictionary<GuildMutationPermit, ActivePermit> _permits = new(ReferenceEqualityComparer.Instance);

    private bool _stopping;

    public Task<Result<WorkspaceLeaseDto>> AcquireAsync(GuildCallerContext caller, GuildMutationIdentity mutation,
        AcquireWorkspaceLeaseRequest request, CancellationToken cancellationToken)
    {
        if (caller is null || request is null || !ValidMutation(mutation, request.IdempotencyKey)
            || request.WorkspaceId == Guid.Empty || request.Scopes.IsDefaultOrEmpty)
            return Task.FromResult(Result<WorkspaceLeaseDto>.Failure(Invalid));

        return policy.AuthorizeAsync(caller, request.WorkspaceId, GuildAuthorityAction.LeaseAcquire,
            (binding, ct) => UnderGateAsync(async token =>
            {
                // An accepted request is history, not new physical admission. Authenticate its owner
                // and validate its durable facts before asking the current filesystem to resolve paths.
                GuildWorkspaceLeaseMutationReceipt? historicalAcceptance = null;

                Result<GuildWorkspaceLeaseRecord> history = await store.ReadTransactionAsync<GuildWorkspaceLeaseRecord>(async (connection, transaction, admitted) =>
                {
                    Result<GuildWorkspaceLeaseRecord> prior = await GuildWorkspaceLeaseStore.FindReplayWithinAsync(connection, transaction,
                        binding.Owner, mutation.IdempotencyKey, mutation.CanonicalRequestDigest, admitted).ConfigureAwait(false);

                    if (prior.IsFailure) return prior;

                    Result<GuildWorkspaceLeaseMutationReceipt> accepted = await GuildWorkspaceLeaseStore.FindMutationReplayWithinAsync(connection, transaction,
                        binding.Owner, mutation.IdempotencyKey, mutation.CanonicalRequestDigest, GuildAuthorityAction.LeaseAcquire, prior.Value.Id, admitted).ConfigureAwait(false);

                    if (accepted.IsFailure) return Unavailable;

                    historicalAcceptance = accepted.Value;

                    return prior;
                }, token).ConfigureAwait(false);

                if (history.IsSuccess)
                {
                    if (historicalAcceptance is null) return Result<WorkspaceLeaseDto>.Failure(Unavailable);

                    return ToDto(history.Value, new(historicalAcceptance.Id, mutation.IdempotencyKey, true,
                        historicalAcceptance.AcceptedAt, historicalAcceptance.Result.Revision));
                }

                if (history.Error.Code != ErrorCodes.Guild.NotFound) return Result<WorkspaceLeaseDto>.Failure(history.Error);

                Result<WorkspaceLeaseIdentitySet> resolved = await identities.ResolveAsync(binding.WorkspaceRoot, request.Scopes, token).ConfigureAwait(false);

                if (resolved.IsFailure) return Result<WorkspaceLeaseDto>.Failure(resolved.Error);

                Result coverage = await CoversAllowedAsync(binding, resolved.Value, request.Scopes, token).ConfigureAwait(false);

                if (coverage.IsFailure) return Result<WorkspaceLeaseDto>.Failure(coverage.Error);

                bool inserted = false;

                GuildWorkspaceLeaseMutationReceipt? acceptance = null;

                Result<GuildWorkspaceLeaseRecord> acquired = await store.InTransactionAsync<GuildWorkspaceLeaseRecord>(async (connection, transaction, admitted) =>
                {
                    Result<GuildWorkspaceLeaseRecord> replay = await GuildWorkspaceLeaseStore.FindReplayWithinAsync(connection, transaction,
                        binding.Owner, mutation.IdempotencyKey, mutation.CanonicalRequestDigest, admitted).ConfigureAwait(false);

                    if (replay.IsSuccess)
                    {
                        Result<GuildWorkspaceLeaseMutationReceipt> acceptedReplay = await GuildWorkspaceLeaseStore.FindMutationReplayWithinAsync(connection, transaction,
                            binding.Owner, mutation.IdempotencyKey, mutation.CanonicalRequestDigest, GuildAuthorityAction.LeaseAcquire, replay.Value.Id, admitted).ConfigureAwait(false);

                        if (acceptedReplay.IsFailure) return Unavailable;

                        acceptance = acceptedReplay.Value;

                        return replay;
                    }

                    if (replay.Error.Code != ErrorCodes.Guild.NotFound) return replay;

                    Result<List<ObservedLease>> protectedRows = await ReadProtectedAsync(connection, transaction, resolved.Value, admitted).ConfigureAwait(false);

                    if (protectedRows.IsFailure) return protectedRows.Error;

                    if (protectedRows.Value.Any(row => row.Identity.ConflictsWith(resolved.Value))) return Conflict;

                    DateTimeOffset now = time.GetUtcNow();

                    GuildWorkspaceLeaseRecord row = new(Guid.NewGuid(), binding.Owner, binding.WorkspaceRoot,
                        resolved.Value.ToJson(), resolved.Value.Digest, HostInstanceId, 1, 1,
                        GuildWorkspaceLeaseState.Held, now, now.Add(NominalDuration), 0,
                        GuildWorkspaceLeaseEffectKind.Managed, null, mutation.IdempotencyKey, mutation.CanonicalRequestDigest);

                    Result<GuildWorkspaceLeaseRecord> saved = await GuildWorkspaceLeaseStore.InsertWithinAsync(connection, transaction, row, admitted).ConfigureAwait(false);

                    if (saved.IsFailure) return saved;

                    Result<GuildWorkspaceLeaseMutationReceipt> original = await GuildWorkspaceLeaseStore.FindMutationReplayWithinAsync(connection, transaction,
                        binding.Owner, mutation.IdempotencyKey, mutation.CanonicalRequestDigest, GuildAuthorityAction.LeaseAcquire, saved.Value.Id, admitted).ConfigureAwait(false);

                    if (original.IsFailure) return Unavailable;

                    acceptance = original.Value;

                    inserted = true;

                    return saved;
                }, token).ConfigureAwait(false);

                if (acquired.IsFailure) return Result<WorkspaceLeaseDto>.Failure(acquired.Error);

                if (acceptance is null) return Result<WorkspaceLeaseDto>.Failure(Unavailable);

                // Replay is observation, never reacquisition after a rejected heartbeat.
                if (inserted) _held.Add(acquired.Value.Id, new(acquired.Value, resolved.Value, binding, false));

                return ToDto(acquired.Value, new(acceptance.Id, mutation.IdempotencyKey, !inserted,
                    acceptance.AcceptedAt, acceptance.Result.Revision));
            }, ct), cancellationToken);
    }

    public Task<Result<WorkspaceLeaseDto>> ReleaseAsync(GuildCallerContext caller, GuildMutationIdentity mutation, Guid leaseId,
        ReleaseWorkspaceLeaseRequest request, CancellationToken cancellationToken) => request is null
            ? Task.FromResult(Result<WorkspaceLeaseDto>.Failure(Invalid))
            : ChangeLeaseAsync(caller, mutation, leaseId, request.IdempotencyKey, request.Epoch, request.ExpectedRevision,
                GuildAuthorityAction.LeaseRelease, cancellationToken);

    public Task<Result<GuildMutationPermit>> AdmitMutationAsync(GuildVerifiedAuthority authority, GuildMutationPlan plan,
        CancellationToken cancellationToken) => AdmitMutationCoreAsync(authority, plan, null, cancellationToken);

    private Task<Result<GuildMutationPermit>> AdmitMutationCoreAsync(GuildVerifiedAuthority authority, GuildMutationPlan plan,
        string? writerRoot, CancellationToken cancellationToken)
    {
        if (authority is null || plan is null || plan.WorkspaceId == Guid.Empty || plan.Scopes.IsDefaultOrEmpty
            || string.IsNullOrWhiteSpace(plan.EffectPlanDigest)) return Task.FromResult(Result<GuildMutationPermit>.Failure(Invalid));

        return policy.RevalidateAsync(authority, plan.WorkspaceId, (binding, ct) => UnderGateAsync(async token =>
        {
            if (writerRoot is not null)
            {
                Result<GuildMutationPlan> translated = TranslateWriterPlan(binding.WorkspaceRoot, writerRoot, plan);

                if (translated.IsFailure) return Result<GuildMutationPermit>.Failure(translated.Error);

                plan = translated.Value;
            }

            Result<WorkspaceLeaseIdentitySet> resolved = await identities.ResolveAsync(binding.WorkspaceRoot, plan.Scopes, token).ConfigureAwait(false);

            if (resolved.IsFailure) return Result<GuildMutationPermit>.Failure(resolved.Error);

            Result coverage = await CoversAllowedAsync(binding, resolved.Value, plan.Scopes, token).ConfigureAwait(false);

            if (coverage.IsFailure) return Result<GuildMutationPermit>.Failure(coverage.Error);

            if (_permits.Values.Any(permit => permit.Reservation.ConflictsWith(resolved.Value))) return Result<GuildMutationPermit>.Failure(Conflict);

            Result<IDisposable> pinned = host.Pin();

            if (pinned.IsFailure) return Result<GuildMutationPermit>.Failure(pinned.Error);

            bool transferred = false;

            try
            {
                Result<ImmutableArray<HeldLease>> result = await store.InTransactionAsync<ImmutableArray<HeldLease>>(async (connection, transaction, admitted) =>
                {
                    Result<List<ObservedLease>> protectedRows = await ReadProtectedAsync(connection, transaction, resolved.Value, admitted).ConfigureAwait(false);

                    if (protectedRows.IsFailure) return protectedRows.Error;

                    List<ObservedLease> overlaps = protectedRows.Value;

                    List<HeldLease> reservations = [];

                    foreach (ObservedLease overlap in overlaps)
                    {
                        if (overlap.Row.Owner != binding.Owner || overlap.Row.State != GuildWorkspaceLeaseState.Held
                            || overlap.Row.HostInstanceId != HostInstanceId
                            || !_held.TryGetValue(overlap.Row.Id, out HeldLease? held)
                            || held.Row.Epoch != overlap.Row.Epoch) return Conflict;

                        Result valid = await identities.ValidateAsync(held.Identity, admitted).ConfigureAwait(false);

                        if (valid.IsFailure) return valid.Error;

                        reservations.Add(held with { Row = overlap.Row });
                    }

                    ImmutableArray<WorkspaceLeaseScopeIdentity> uncovered = [.. resolved.Value.Scopes.Where(scope =>
                        !reservations.Any(held => held.Identity.Scopes.Any(covering => covering.Covers(scope))))];

                    // Partial coverage is an upgrade, which requires an explicit release/drain first.
                    if (uncovered.Any(scope => overlaps.Any(row => row.Identity.Scopes.Any(existing => existing.ConflictsWith(scope)))))
                        return Conflict;

                    if (!uncovered.IsEmpty)
                    {
                        WorkspaceLeaseIdentitySet automaticIdentity = resolved.Value with { Scopes = uncovered };

                        DateTimeOffset now = time.GetUtcNow();

                        GuildWorkspaceLeaseRecord automatic = new(Guid.NewGuid(), binding.Owner, binding.WorkspaceRoot,
                            automaticIdentity.ToJson(), automaticIdentity.Digest, HostInstanceId, 1, 1,
                            GuildWorkspaceLeaseState.Held, now, now.Add(NominalDuration), 0,
                            GuildWorkspaceLeaseEffectKind.Managed, null, Guid.NewGuid(), plan.EffectPlanDigest);

                        Result<GuildWorkspaceLeaseRecord> saved = await GuildWorkspaceLeaseStore.InsertWithinAsync(connection, transaction, automatic, admitted).ConfigureAwait(false);

                        if (saved.IsFailure) return saved.Error;

                        reservations.Add(new(saved.Value, automaticIdentity, binding, true));
                    }

                    ImmutableArray<HeldLease>.Builder counted = ImmutableArray.CreateBuilder<HeldLease>();

                    foreach (HeldLease reservation in reservations)
                    {
                        Result<GuildWorkspaceLeaseRecord> countedRow = await GuildWorkspaceLeaseStore.AdmitPermitWithinAsync(connection, transaction,
                            reservation.Row, time.GetUtcNow(), admitted).ConfigureAwait(false);

                        if (countedRow.IsFailure) return countedRow.Error;

                        counted.Add(reservation with { Row = countedRow.Value });
                    }

                    return counted.ToImmutable();
                }, token).ConfigureAwait(false);

                if (result.IsFailure) return Result<GuildMutationPermit>.Failure(result.Error);

                foreach (HeldLease held in result.Value) _held[held.Row.Id] = held;

                HeldLease first = result.Value[0];

                GuildMutationPermit permit = new(first.Row.Id, first.Row.Epoch, authority, plan.EffectPlanDigest);

                _permits.Add(permit, new(result.Value, resolved.Value, plan, pinned.Value));

                transferred = true;

                return Result<GuildMutationPermit>.Success(permit);
            }
            finally
            {
                if (!transferred) pinned.Value.Dispose();
            }
        }, ct), cancellationToken);
    }

    public async Task<Result> ValidateCommitAsync(GuildMutationPermit permit, CancellationToken cancellationToken)
    {
        ActivePermit? active;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!_permits.TryGetValue(permit, out active)) return Forbidden;
        }
        finally
        {
            _gate.Release();
        }

        Result<bool> checkedResult = await policy.RevalidateAsync(permit.Authority, active.Plan.WorkspaceId,
            (binding, ct) => UnderGateAsync(async token =>
            {
                if (!_permits.TryGetValue(permit, out ActivePermit? current) || !ReferenceEquals(current, active)
                    || binding.Owner != active.Leases[0].Binding.Owner) return Result<bool>.Failure(Forbidden);

                Result coverage = await CoversAllowedAsync(binding, active.Identity, active.Plan.Scopes, token).ConfigureAwait(false);

                if (coverage.IsFailure) return Result<bool>.Failure(coverage.Error);

                Result physical = await identities.ValidateAsync(active.Identity, token).ConfigureAwait(false);

                if (physical.IsFailure) return Result<bool>.Failure(physical.Error);

                return await store.ReadTransactionAsync<bool>(async (connection, transaction, admitted) =>
                {
                    if (active.RecoveryRequired) return Result<bool>.Failure(Unavailable);

                    foreach (HeldLease held in active.Leases)
                    {
                        Result<GuildWorkspaceLeaseRecord> read = await GuildWorkspaceLeaseStore.ReadWithinAsync(connection, transaction, held.Row.Id, admitted).ConfigureAwait(false);

                        if (read.IsFailure) return Result<bool>.Failure(read.Error);

                        GuildWorkspaceLeaseRecord row = read.Value;

                        if (row.Epoch != held.Row.Epoch || row.HostInstanceId != HostInstanceId || row.Owner != binding.Owner
                            || row.State != GuildWorkspaceLeaseState.Held || row.ExpiresAt <= time.GetUtcNow()
                            || row.InFlightMutationCount <= 0 || !_held.ContainsKey(row.Id)) return Result<bool>.Failure(Lapsed);
                    }

                    return Result<bool>.Success(true);
                }, token).ConfigureAwait(false);
            }, ct), cancellationToken).ConfigureAwait(false);

        return checkedResult.IsSuccess ? Result.Success() : checkedResult.Error;
    }

    public async Task<Result> CompleteMutationAsync(GuildMutationPermit permit, CancellationToken cancellationToken)
    {
        // Completion is a drain, not new authority. Revocation/cancellation must not prevent it.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!_permits.TryGetValue(permit, out ActivePermit? active)) return Forbidden;

            Result<ImmutableArray<GuildWorkspaceLeaseRecord>> completed = await store.InTransactionAsync<ImmutableArray<GuildWorkspaceLeaseRecord>>(async (connection, transaction, ct) =>
            {
                ImmutableArray<GuildWorkspaceLeaseRecord>.Builder finished = ImmutableArray.CreateBuilder<GuildWorkspaceLeaseRecord>();

                foreach (HeldLease held in active.Leases)
                {
                    Result<GuildWorkspaceLeaseRecord> read = await GuildWorkspaceLeaseStore.ReadWithinAsync(connection, transaction, held.Row.Id, ct).ConfigureAwait(false);

                    if (read.IsFailure) return read.Error;

                    GuildWorkspaceLeaseRecord row = read.Value;

                    if ((held.Automatic || _stopping || row.ExpiresAt <= time.GetUtcNow()) && row.State == GuildWorkspaceLeaseState.Held)
                    {
                        Result<GuildWorkspaceLeaseRecord> draining = await GuildWorkspaceLeaseStore.DrainWithinAsync(connection, transaction, row, ErrorCodes.Guild.LeaseNotHeld, ct).ConfigureAwait(false);

                        if (draining.IsFailure) return draining.Error;
                    }

                    Result<GuildWorkspaceLeaseRecord> result = await GuildWorkspaceLeaseStore.FinishPermitWithinAsync(connection, transaction,
                        held.Row.Id, held.Row.Epoch, HostInstanceId, active.RecoveryRequired, ct).ConfigureAwait(false);

                    if (result.IsFailure) return result.Error;

                    finished.Add(result.Value);
                }

                return finished.ToImmutable();
            }, cancellationToken).ConfigureAwait(false);

            // A failed commit retains every local pin; successful uncertain completion retains
            // its durable reconciliation row even after all admitted I/O has actually drained.
            if (completed.IsFailure) return completed.Error;

            _permits.Remove(permit);

            active.HostPin.Dispose();

            foreach (GuildWorkspaceLeaseRecord row in completed.Value)
            {
                if (row.State == GuildWorkspaceLeaseState.Held && _held.TryGetValue(row.Id, out HeldLease? held))
                    _held[row.Id] = held with { Row = row };
                else
                    _held.Remove(row.Id);
            }

            return Result.Success();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Result<List<ObservedLease>>> ReadProtectedAsync(SqliteConnection connection, SqliteTransaction transaction, WorkspaceLeaseIdentitySet requested, CancellationToken ct)
    {
        List<ObservedLease> observed = [];

        Result scanned = await GuildWorkspaceLeaseStore.VisitProtectedWithinAsync(connection, transaction, async (row, token) =>
        {
            Result<WorkspaceLeaseIdentitySet> identity = WorkspaceLeaseIdentitySet.FromJson(row.IdentityJson);

            if (identity.IsFailure || identity.Value.Digest != row.IdentityDigest) return Unavailable;

            if (row.State == GuildWorkspaceLeaseState.Held && row.ExpiresAt <= time.GetUtcNow())
            {
                Result<GuildWorkspaceLeaseRecord> drained = await GuildWorkspaceLeaseStore.DrainWithinAsync(connection, transaction, row, ErrorCodes.Guild.LeaseLapsed, token).ConfigureAwait(false);

                if (drained.IsFailure) return Result.Failure(drained.Error);

                row = drained.Value;

                _held.Remove(row.Id);
            }

            if ((row.State != GuildWorkspaceLeaseState.Released || row.InFlightMutationCount != 0) && identity.Value.ConflictsWith(requested))
                observed.Add(new(row, identity.Value));

            return Result.Success();
        }, ct).ConfigureAwait(false);

        return scanned.IsSuccess ? observed : scanned.Error;
    }

    private async Task<Result<T>> UnderGateAsync<T>(Func<CancellationToken, Task<Result<T>>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (_stopping || HostInstanceId == Guid.Empty) return Unavailable;

            Result<IDisposable> pin = host.Pin();

            if (pin.IsFailure) return pin.Error;

            using (pin.Value)
            {
                return await action(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static Result<WorkspaceLeaseDto> ToDto(GuildWorkspaceLeaseRecord row, GuildMutationReceiptDto? receipt = null)
    {
        Result<WorkspaceLeaseIdentitySet> identity = WorkspaceLeaseIdentitySet.FromJson(row.IdentityJson);

        if (identity.IsFailure || identity.Value.Digest != row.IdentityDigest) return Unavailable;

        ImmutableArray<GuildPathScope> scopes = [.. identity.Value.Scopes.Select(scope => new GuildPathScope(scope.RelativePath, scope.Kind)).Distinct()];

        return new WorkspaceLeaseDto(row.Id, row.Owner.WorkspaceId, row.Owner.Kind, row.Owner.QuestId, row.Owner.RootQuestId,
            scopes, row.State, row.Epoch, row.Revision, row.ExpiresAt, row.InFlightMutationCount, row.ReasonCode, receipt);
    }

    private static bool ValidMutation(GuildMutationIdentity? mutation, Guid key) => mutation is not null && key != Guid.Empty
        && mutation.IdempotencyKey == key && mutation.CanonicalRequestDigest is { Length: 64 }
        && mutation.CanonicalRequestDigest.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {
            _stopping = true;

            // A retained permit is never silently freed by service disposal. The host lifetime
            // owner must keep its installation lock until CompleteMutationAsync drains it.
            _held.Clear();

            host.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record HeldLease(GuildWorkspaceLeaseRecord Row, WorkspaceLeaseIdentitySet Identity,
        GuildWorkspaceLeaseBinding Binding, bool Automatic);

    private sealed class ActivePermit(ImmutableArray<HeldLease> leases, WorkspaceLeaseIdentitySet identity, GuildMutationPlan plan, IDisposable hostPin)
    {
        internal ImmutableArray<HeldLease> Leases { get; set; } = leases;

        internal WorkspaceLeaseIdentitySet Reservation { get; } = identity;

        internal WorkspaceLeaseIdentitySet Identity { get; set; } = identity;

        internal GuildMutationPlan Plan { get; } = plan;

        internal IDisposable HostPin { get; } = hostPin;

        internal bool RecoveryRequired { get; set; }
    }

    private sealed record ObservedLease(GuildWorkspaceLeaseRecord Row, WorkspaceLeaseIdentitySet Identity);

    private static Error Invalid => new(ErrorCodes.Guild.InvalidRequest, "The workspace lease intent is invalid.");

    private static Error Forbidden => new(ErrorCodes.Guild.Forbidden, "The exact workspace holder or permit is unavailable.");

    private static Error Conflict => new(ErrorCodes.Guild.LeaseConflict, "The physical scope already has protected ownership or an active writer.");

    private static Error RevisionConflict => new(ErrorCodes.Guild.RevisionConflict, "The workspace lease epoch or revision has changed.");

    private static Error Lapsed => new(ErrorCodes.Guild.LeaseLapsed, "Workspace ownership has lapsed; no further forward mutation is admitted.");

    private static Error Unavailable => new(ErrorCodes.Guild.AuthorityUnavailable, "Workspace mutation evidence is unavailable.");
}
