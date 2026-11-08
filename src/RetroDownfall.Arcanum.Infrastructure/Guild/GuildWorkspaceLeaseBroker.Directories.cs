using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildWorkspaceLeaseBroker
{
    internal Task<Result> ValidateDirectoryCreationAsync(GuildMutationPermit permit, WorkspaceLeaseDirectoryCreation preparation,
        bool rollback, CancellationToken cancellationToken) => DirectoryTransitionAsync(permit, rollback, postEffect: false,
            (active, ct) => identities.ValidateDirectoryCreationAsync(active.Identity, preparation, ct).AsTask(), cancellationToken);

    internal Task<Result> AdoptCreatedDirectoryAsync(GuildMutationPermit permit, WorkspaceLeaseCreatedDirectory proof,
        bool rollback, CancellationToken cancellationToken) => DirectoryTransitionAsync(permit, rollback, postEffect: true,
            (active, ct) => AdoptDirectoryIdentityAsync(active,
                (set, token) => identities.AdoptCreatedDirectoryAsync(set, proof, token),
                set => IsCreationRelevant(set, proof), ct), cancellationToken);

    internal Task<Result> ValidateDirectoryRemovalAsync(GuildMutationPermit permit, WorkspaceLeaseDirectoryRemoval preparation,
        bool rollback, CancellationToken cancellationToken) => DirectoryTransitionAsync(permit, rollback, postEffect: false,
            (active, ct) => identities.ValidateDirectoryRemovalAsync(active.Identity, preparation, ct).AsTask(), cancellationToken);

    internal Task<Result> AdoptRemovedDirectoryAsync(GuildMutationPermit permit, WorkspaceLeaseRemovedDirectory proof,
        bool rollback, CancellationToken cancellationToken) => DirectoryTransitionAsync(permit, rollback, postEffect: true,
            (active, ct) => AdoptDirectoryIdentityAsync(active,
                (set, token) => identities.AdoptRemovedDirectoryAsync(set, proof, token),
                set => set.Scopes.Any(scope => scope.ObjectIdentity == proof.Identity || scope.Ancestors.Any(node => node.Identity == proof.Identity)), ct), cancellationToken);

    private async Task<Result> DirectoryTransitionAsync(GuildMutationPermit permit, bool rollback, bool postEffect,
        Func<ActivePermit, CancellationToken, Task<Result>> effect, CancellationToken cancellationToken)
    {
        // Observing an already admitted native effect is pinned drain bookkeeping.
        // Expiry, revocation, and caller cancellation must not discard the physical fact.
        if (postEffect) cancellationToken = CancellationToken.None;

        bool drain = rollback || postEffect;

        bool accepted = false;

        try
        {
            ActivePermit? expected;

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                if (!_permits.TryGetValue(permit, out expected)) return Forbidden;
            }
            finally
            {
                _gate.Release();
            }

            Result<bool> result;

            if (drain)
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

                try
                {
                    result = await ApplyDirectoryTransitionUnderGateAsync(permit, expected, drain, effect, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }
            }
            else
            {
                result = await policy.RevalidateAsync(permit.Authority, expected.Plan.WorkspaceId,
                    (binding, ct) => UnderGateAsync(async token =>
                    {
                        if (binding.Owner != expected.Leases[0].Binding.Owner) return Result<bool>.Failure(Forbidden);

                        Result coverage = await CoversAllowedAsync(binding, expected.Identity, expected.Plan.Scopes, token).ConfigureAwait(false);

                        if (coverage.IsFailure) return Result<bool>.Failure(coverage.Error);

                        return await ApplyDirectoryTransitionUnderGateAsync(permit, expected, drain, effect, token).ConfigureAwait(false);
                    }, ct), cancellationToken).ConfigureAwait(false);
            }

            accepted = result.IsSuccess;

            return accepted ? Result.Success() : result.Error;
        }
        finally
        {
            if (postEffect && !accepted)
            {
                // A failed or cancelled observation after the native effect must never free its
                // reservation. The existing retention lane sets the in-memory hold before storage.
                _ = await RetainRecoveryAsync(permit, ErrorCodes.Guild.WorkspaceIdentityUnavailable, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task<Result<bool>> ApplyDirectoryTransitionUnderGateAsync(GuildMutationPermit permit, ActivePermit expected,
        bool rollback, Func<ActivePermit, CancellationToken, Task<Result>> effect, CancellationToken cancellationToken)
    {
        if (!_permits.TryGetValue(permit, out ActivePermit? current) || !ReferenceEquals(current, expected)) return Forbidden;

        Result<bool> rows = await store.ReadTransactionAsync<bool>(async (connection, transaction, ct) =>
        {
            if (!rollback && current.RecoveryRequired) return Result<bool>.Failure(Unavailable);

            foreach (HeldLease held in current.Leases)
            {
                Result<GuildWorkspaceLeaseRecord> read = await GuildWorkspaceLeaseStore.ReadWithinAsync(connection, transaction, held.Row.Id, ct).ConfigureAwait(false);

                if (read.IsFailure) return Result<bool>.Failure(read.Error);

                GuildWorkspaceLeaseRecord row = read.Value;

                if (row.HostInstanceId != HostInstanceId || row.Epoch != held.Row.Epoch || row.Owner != held.Row.Owner
                    || row.InFlightMutationCount <= 0 || row.State == GuildWorkspaceLeaseState.Released) return Result<bool>.Failure(Forbidden);

                if (!rollback && (row.State != GuildWorkspaceLeaseState.Held || row.ExpiresAt <= time.GetUtcNow() || !_held.ContainsKey(row.Id)))
                    return Result<bool>.Failure(Lapsed);
            }

            return true;
        }, cancellationToken).ConfigureAwait(false);

        if (rows.IsFailure) return rows;

        Result changed = await effect(current, cancellationToken).ConfigureAwait(false);

        return changed.IsSuccess ? Result<bool>.Success(true) : changed.Error;
    }

    private async Task<Result> AdoptDirectoryIdentityAsync(ActivePermit active,
        Func<WorkspaceLeaseIdentitySet, CancellationToken, ValueTask<Result<WorkspaceLeaseIdentitySet>>> adopt,
        Func<WorkspaceLeaseIdentitySet, bool> relevant, CancellationToken cancellationToken)
    {
        Result<WorkspaceLeaseIdentitySet> next = await adopt(active.Identity, cancellationToken).ConfigureAwait(false);

        if (next.IsFailure) return next.Error;

        if (!active.Reservation.Covers(next.Value)) return Forbidden;

        var refreshed = ImmutableArray.CreateBuilder<HeldLease>();

        foreach (HeldLease held in active.Leases)
        {
            HeldLease previous = _held.TryGetValue(held.Row.Id, out HeldLease? live) && live.Row.Epoch == held.Row.Epoch ? live : held;

            if (!relevant(previous.Identity))
            {
                refreshed.Add(previous);

                continue;
            }

            Result<WorkspaceLeaseIdentitySet> updated = await adopt(previous.Identity, cancellationToken).ConfigureAwait(false);

            if (updated.IsFailure) return updated.Error;

            // A broad held lease keeps its original declarations. Only the active exact plan needs
            // extra ephemeral-child observations; directory facts on the original declarations advance.
            WorkspaceLeaseIdentitySet retained = updated.Value with
            {
                Scopes = [.. updated.Value.Scopes.Where(scope => previous.Identity.Scopes.Any(original => original.RelativePath == scope.RelativePath && original.Kind == scope.Kind))],
            };

            refreshed.Add(previous with { Identity = retained });
        }

        active.Identity = next.Value;

        active.Leases = refreshed.ToImmutable();

        foreach (HeldLease held in active.Leases)
        {
            if (_held.ContainsKey(held.Row.Id)) _held[held.Row.Id] = held;
        }

        return Result.Success();
    }

    private static bool IsCreationRelevant(WorkspaceLeaseIdentitySet set, WorkspaceLeaseCreatedDirectory proof)
    {
        WorkspaceLeaseScopeIdentity? target = WorkspaceLeaseIdentityResolver.DirectoryTarget(set, proof.Preparation, proof.Identity);

        return target is not null && set.Scopes.Any(scope => scope.Covers(target))
            || proof.SourceIdentity is { } source && set.Scopes.Any(scope => scope.ObjectIdentity == source || scope.Ancestors.Any(node => node.Identity == source));
    }
}
