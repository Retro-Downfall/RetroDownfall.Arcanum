using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildWorkspaceLeaseBroker
{
    public Task<Result<WorkspaceLeaseDto>> RenewAsync(GuildCallerContext caller, GuildMutationIdentity mutation, Guid leaseId,
        RenewWorkspaceLeaseRequest request, CancellationToken cancellationToken) => request is null
            ? Task.FromResult(Result<WorkspaceLeaseDto>.Failure(Invalid))
            : ChangeLeaseAsync(caller, mutation, leaseId, request.IdempotencyKey, request.Epoch, request.ExpectedRevision,
                GuildAuthorityAction.LeaseRenew, cancellationToken);

    private async Task<Result<WorkspaceLeaseDto>> ChangeLeaseAsync(GuildCallerContext caller, GuildMutationIdentity mutation,
        Guid leaseId, Guid key, long epoch, long revision, GuildAuthorityAction action, CancellationToken cancellationToken)
    {
        if (caller is null || leaseId == Guid.Empty || !ValidMutation(mutation, key)) return Invalid;

        Result<GuildWorkspaceLeaseRecord> located = await store.ReadTransactionAsync((connection, transaction, ct) =>
            GuildWorkspaceLeaseStore.ReadWithinAsync(connection, transaction, leaseId, ct), cancellationToken).ConfigureAwait(false);

        if (located.IsFailure) return located.Error;

        return await policy.AuthorizeAsync(caller, located.Value.Owner.WorkspaceId, action,
            (binding, ct) => UnderGateAsync(async token =>
            {
                bool replayed = false;

                Result<GuildWorkspaceLeaseMutationReceipt> result = await store.InTransactionAsync<GuildWorkspaceLeaseMutationReceipt>(async (connection, transaction, admitted) =>
                {
                    Result<GuildWorkspaceLeaseMutationReceipt> replay = await GuildWorkspaceLeaseStore.FindMutationReplayWithinAsync(connection, transaction,
                        binding.Owner, key, mutation.CanonicalRequestDigest, action, leaseId, admitted).ConfigureAwait(false);

                    if (replay.IsSuccess)
                    {
                        replayed = true;

                        return replay;
                    }

                    if (replay.Error.Code != ErrorCodes.Guild.NotFound) return replay.Error;

                    Result<GuildWorkspaceLeaseRecord> read = await GuildWorkspaceLeaseStore.ReadWithinAsync(connection, transaction, leaseId, admitted).ConfigureAwait(false);

                    if (read.IsFailure) return read.Error;

                    GuildWorkspaceLeaseRecord row = read.Value;

                    if (row.Owner != binding.Owner) return Forbidden;

                    if (row.Epoch != epoch || row.Revision != revision) return RevisionConflict;

                    Result<GuildWorkspaceLeaseRecord> changed;

                    DateTimeOffset now = time.GetUtcNow();

                    if (action == GuildAuthorityAction.LeaseRelease)
                    {
                        changed = await GuildWorkspaceLeaseStore.DrainWithinAsync(connection, transaction, row, ErrorCodes.Guild.LeaseNotHeld, admitted).ConfigureAwait(false);
                    }
                    else
                    {
                        if (row.State != GuildWorkspaceLeaseState.Held || row.ExpiresAt <= now)
                        {
                            _held.Remove(leaseId);

                            return Lapsed;
                        }

                        if (!_held.TryGetValue(leaseId, out HeldLease? held) || held.Row.Epoch != row.Epoch || row.HostInstanceId != HostInstanceId)
                            return new Error(ErrorCodes.Guild.LeaseNotHeld, "This host no longer holds the exact lease epoch.");

                        Result coverage = await CoversAllowedAsync(binding, held.Identity,
                            [.. held.Identity.Scopes.Select(scope => new GuildPathScope(scope.RelativePath, scope.Kind))], admitted).ConfigureAwait(false);

                        if (coverage.IsFailure)
                        {
                            _held.Remove(leaseId);

                            return coverage.Error;
                        }

                        Result physical = await identities.ValidateAsync(held.Identity, admitted).ConfigureAwait(false);

                        if (physical.IsFailure)
                        {
                            _held.Remove(leaseId);

                            return physical.Error;
                        }

                        changed = await GuildWorkspaceLeaseStore.RenewWithinAsync(connection, transaction, row, now, now.Add(NominalDuration), admitted).ConfigureAwait(false);

                        if (changed.IsFailure) _held.Remove(leaseId);
                    }

                    if (changed.IsFailure) return changed.Error;

                    return await GuildWorkspaceLeaseStore.RecordMutationWithinAsync(connection, transaction, changed.Value, mutation, action, now, admitted).ConfigureAwait(false);
                }, token).ConfigureAwait(false);

                if (result.IsFailure) return Result<WorkspaceLeaseDto>.Failure(result.Error);

                if (!replayed)
                {
                    if (action == GuildAuthorityAction.LeaseRelease) _held.Remove(leaseId);
                    else if (_held.TryGetValue(leaseId, out HeldLease? current)) _held[leaseId] = current with { Row = result.Value.Result };
                }

                GuildWorkspaceLeaseMutationReceipt receipt = result.Value;

                return ToDto(receipt.Result, new(receipt.Id, key, replayed, receipt.AcceptedAt, receipt.Result.Revision));
            }, ct), cancellationToken).ConfigureAwait(false);
    }

    internal async Task<Result> RenewHeldAsync(CancellationToken cancellationToken)
    {
        HeldLease[] candidates;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_stopping) return Unavailable;

            candidates = [.. _held.Values];
        }
        finally
        {
            _gate.Release();
        }

        Error? firstError = null;

        foreach (HeldLease candidate in candidates)
        {
            Result<bool> result = await policy.RevalidateAsync(candidate.Binding.Authority, candidate.Row.Owner.WorkspaceId,
                (binding, ct) => UnderGateAsync(async token =>
                {
                    if (!_held.TryGetValue(candidate.Row.Id, out HeldLease? current)
                        || current.Row.Epoch != candidate.Row.Epoch) return Result<bool>.Success(true);

                    if (binding.Owner != current.Row.Owner) return Result<bool>.Failure(Forbidden);

                    Result coverage = await CoversAllowedAsync(binding, current.Identity,
                        [.. current.Identity.Scopes.Select(scope => new GuildPathScope(scope.RelativePath, scope.Kind))], token).ConfigureAwait(false);

                    if (coverage.IsFailure) return Result<bool>.Failure(coverage.Error);

                    Result physical = await identities.ValidateAsync(current.Identity, token).ConfigureAwait(false);

                    if (physical.IsFailure) return Result<bool>.Failure(physical.Error);

                    Result<GuildWorkspaceLeaseRecord> renewed = await store.InTransactionAsync<GuildWorkspaceLeaseRecord>(async (connection, transaction, admitted) =>
                    {
                        Result<GuildWorkspaceLeaseRecord> read = await GuildWorkspaceLeaseStore.ReadWithinAsync(connection, transaction, current.Row.Id, admitted).ConfigureAwait(false);

                        if (read.IsFailure) return read.Error;

                        if (read.Value.Epoch != current.Row.Epoch || read.Value.HostInstanceId != HostInstanceId
                            || read.Value.Owner != current.Row.Owner || read.Value.State != GuildWorkspaceLeaseState.Held
                            || read.Value.ExpiresAt <= time.GetUtcNow()) return Lapsed;

                        DateTimeOffset now = time.GetUtcNow();

                        return await GuildWorkspaceLeaseStore.RenewWithinAsync(connection, transaction, read.Value, now, now.Add(NominalDuration), admitted).ConfigureAwait(false);
                    }, token).ConfigureAwait(false);

                    if (renewed.IsFailure) return Result<bool>.Failure(renewed.Error);

                    _held[current.Row.Id] = current with { Row = renewed.Value };

                    return Result<bool>.Success(true);
                }, ct), cancellationToken).ConfigureAwait(false);

            if (result.IsFailure)
            {
                firstError ??= result.Error;

                await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);

                try
                {
                    // Do not retry acquisition. Any existing permit still pins its physical set
                    // and must finish through the separate completion/reconciliation lane.
                    if (_held.TryGetValue(candidate.Row.Id, out HeldLease? current) && current.Row.Epoch == candidate.Row.Epoch)
                        _held.Remove(candidate.Row.Id);
                }
                finally
                {
                    _gate.Release();
                }
            }
        }

        return firstError is { } error ? Result.Failure(error) : Result.Success();
    }

    public Task<Result<WorkspaceLeasePageDto>> ListAsync(GuildCallerContext caller, Guid? workspaceId, Guid? questId,
        GuildWorkspaceLeaseState? state, GuildPageQuery query, CancellationToken cancellationToken)
    {
        if (caller is null || workspaceId is null || workspaceId == Guid.Empty || questId == Guid.Empty || query is null
            || query.PageSize is < 1 or > 100 || state is { } selected && !Enum.IsDefined(selected))
            return Task.FromResult(Result<WorkspaceLeasePageDto>.Failure(Invalid));

        return policy.AuthorizeAsync(caller, workspaceId.Value, GuildAuthorityAction.WorkspaceRead,
            (binding, ct) =>
            {
                if (!GuildWorkspaceLeaseCursor.TryDecode(query.Cursor, binding.Owner, questId, state, out Guid? afterId))
                    return Task.FromResult(Result<WorkspaceLeasePageDto>.Failure(new Error(ErrorCodes.Guild.CursorInvalid,
                        "The workspace lease continuation does not match this owner and query.")));

                string? after = afterId?.ToString("D").ToUpperInvariant();

                return store.ReadTransactionAsync<WorkspaceLeasePageDto>(async (connection, transaction, token) =>
                {
                    ImmutableArray<WorkspaceLeaseDto>.Builder items = ImmutableArray.CreateBuilder<WorkspaceLeaseDto>();

                    string? next = null;

                    Result read = await GuildWorkspaceLeaseStore.VisitAllWithinAsync(connection, transaction, (row, _) =>
                    {
                        if (row.Owner != binding.Owner || questId is { } quest && row.Owner.QuestId != quest
                            || state is { } selectedState && row.State != selectedState
                            || after is not null && string.CompareOrdinal(row.Id.ToString("D").ToUpperInvariant(), after) <= 0)
                            return Task.FromResult(Result.Success());

                        if (items.Count == query.PageSize)
                        {
                            next ??= GuildWorkspaceLeaseCursor.Encode(binding.Owner, questId, state, items[^1].Id);

                            return Task.FromResult(Result.Success());
                        }

                        Result<WorkspaceLeaseDto> dto = ToDto(row);

                        if (dto.IsFailure) return Task.FromResult(Result.Failure(dto.Error));

                        items.Add(dto.Value);

                        return Task.FromResult(Result.Success());
                    }, token).ConfigureAwait(false);

                    return read.IsSuccess ? new WorkspaceLeasePageDto(items.ToImmutable(), next, 0) : read.Error;
                }, ct);
            }, cancellationToken);
    }
}
