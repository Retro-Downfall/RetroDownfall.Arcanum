using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>
/// Host authority participant. The private policy recognizes authenticated coordinator claims;
/// every protected effect rechecks issuer lineage and applicable live Cohort constraints.
/// </summary>
internal sealed partial class SignetAuthority(GuildSignetStore store, IGrimoireConnectionAdmissionGate gate,
    IGuildSignetHostPolicy policy, TimeProvider time) : ISignetAuthority, IGuildToolAuthority, IDisposable
{
    private readonly ConcurrentDictionary<GuildExecutionHandle, Credential> _credentials = new(ReferenceEqualityComparer.Instance);

    private int _disposed;

    public Task<Result<GuildExecutionHandle>> MintAsync(GuildAdmissionContext admission, GuildSignetBinding binding, CancellationToken cancellationToken) =>
        IssueStandaloneAsync(admission, binding, null, null, cancellationToken);

    public Task<Result<GuildExecutionHandle>> AttenuateAsync(GuildAdmissionContext admission, GuildExecutionHandle parent,
        GuildSignetBinding child, GuildRequestedGrant request, CancellationToken cancellationToken) =>
        parent is null || request is null
            ? Task.FromResult(Result<GuildExecutionHandle>.Failure(Mismatch))
            : IssueStandaloneAsync(admission, child, parent, request, cancellationToken);

    private Task<Result<GuildExecutionHandle>> IssueStandaloneAsync(GuildAdmissionContext admission, GuildSignetBinding binding,
        GuildExecutionHandle? parent, GuildRequestedGrant? request, CancellationToken cancellationToken)
    {
        if (!ValidIssuance(admission, binding, parent, request))
        {
            return Task.FromResult(Result<GuildExecutionHandle>.Failure(Mismatch));
        }

        return UnderRootAsync(binding.RootQuestId, true, async ct =>
        {
            Result<GuildSignetAdmissionEvidence> authorized = await AuthorizeIssuanceAsync(admission, binding, parent, request, ct).ConfigureAwait(false);

            if (authorized.IsFailure)
            {
                return Result<GuildExecutionHandle>.Failure(authorized.Error);
            }

            using Credential credential = Credential.Create();

            Result<GuildSignetRecord> committed = await store.ExecuteAsync<GuildSignetRecord>(true,
                (connection, transaction, token) => IssueWithinAsync(connection, transaction, admission, binding, parent,
                    request, authorized.Value, credential, token), ct).ConfigureAwait(false);

            return committed.IsSuccess ? Register(committed.Value, credential.Take()) : Result<GuildExecutionHandle>.Failure(committed.Error);
        }, cancellationToken);
    }

    private bool ValidIssuance(GuildAdmissionContext admission, GuildSignetBinding binding,
        GuildExecutionHandle? parent, GuildRequestedGrant? request)
    {
        if (!ValidBinding(binding) || admission is null || admission.Caller.InstallationGeneration != policy.InstallationGeneration)
        {
            return false;
        }

        return parent is null
            ? request is null && binding.QuestId == binding.RootQuestId && binding.IssuerSignetId is null
                && admission.Caller.Kind is GuildCallerKind.Operator or GuildCallerKind.Peer
                && (admission.Caller.Kind != GuildCallerKind.Operator || admission.Caller.PrincipalId == binding.OriginPrincipalId)
            : request is not null && binding.RootQuestId == parent.RootQuestId && binding.IssuerSignetId == parent.SignetId
                && binding.QuestId != parent.QuestId && binding.RunId != parent.RunId
                && admission.Caller.Kind is GuildCallerKind.Operator or GuildCallerKind.Run
                && (admission.Caller.Kind != GuildCallerKind.Run || ReferenceEquals(admission.Caller.Execution, parent));
    }

    private async Task<Result<GuildSignetAdmissionEvidence>> AuthorizeIssuanceAsync(GuildAdmissionContext admission,
        GuildSignetBinding binding, GuildExecutionHandle? parent, GuildRequestedGrant? request, CancellationToken ct)
    {
        if (!ValidIssuance(admission, binding, parent, request))
        {
            return Mismatch;
        }

        Result<GuildSignetAdmissionEvidence> authorized = await policy.AuthorizeAdmissionAsync(admission, binding, ct).ConfigureAwait(false);

        if (authorized.IsFailure)
        {
            return authorized.Error;
        }

        return parent is null && (!MatchesPeer(admission.Caller, authorized.Value.Owners) || authorized.Value.Edge != GuildSignetIssuanceEdge.Root)
            ? Result<GuildSignetAdmissionEvidence>.Failure(Mismatch) : authorized;
    }

    private Task<Result<GuildSignetRecord>> IssueWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildAdmissionContext admission, GuildSignetBinding binding, GuildExecutionHandle? parent, GuildRequestedGrant? request,
        GuildSignetAdmissionEvidence authorized, Credential credential, CancellationToken ct) => parent is null
            ? MintWithinAsync(connection, transaction, admission, binding, authorized.Owners, credential, ct)
            : AttenuateWithinAsync(connection, transaction, parent, binding, request!, authorized, credential, ct);

    private async Task<Result<GuildSignetRecord>> MintWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildAdmissionContext admission, GuildSignetBinding binding, GuildSignetOwnerBinding owners, Credential credential, CancellationToken ct)
    {
        Result<Guid> epoch = await GuildSignetStore.EpochAsync(connection, transaction, ct).ConfigureAwait(false);

        if (epoch.IsFailure)
        {
            return epoch.Error;
        }

        GuildSignetRecord row = new(binding, CopyOwners(owners), policy.InstallationGeneration, epoch.Value, 1, 1,
            credential.Digest(), 0, false, false, false, false);

        Result live = await GuildSignetStore.OwnersAsync(connection, transaction, row, ct).ConfigureAwait(false);

        if (live.IsFailure)
        {
            return live.Error;
        }

        Result<GuildSignetPolicyEvidence> current = await policy.ResolveCurrentAsync([row], ct).ConfigureAwait(false);

        if (current.IsFailure)
        {
            return current.Error;
        }

        Result<GuildGrant> within = GuildGrantPolicy.Attenuate(current.Value.HostGrant, Request(binding.Grant), current.Value.ScopeEvidence);

        if (within.IsFailure)
        {
            return within.Error;
        }

        if (admission.Caller.Kind == GuildCallerKind.Peer && !EqualGrant(binding.Grant, current.Value.HostGrant))
        {
            return Mismatch;
        }

        if (binding.Grant.ExpiresAtUtc is { } expiry && time.GetUtcNow() >= expiry)
        {
            return Expired;
        }

        Result<bool> inserted = await GuildSignetStore.InsertAsync(connection, transaction, row, ct).ConfigureAwait(false);

        return inserted.IsSuccess ? Result<GuildSignetRecord>.Success(row) : Result<GuildSignetRecord>.Failure(inserted.Error);
    }

    private async Task<Result<GuildSignetRecord>> AttenuateWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildExecutionHandle parent, GuildSignetBinding child, GuildRequestedGrant request,
        GuildSignetAdmissionEvidence authorized, Credential credential, CancellationToken ct)
    {
        Result<Verified> live = await LiveAsync(connection, transaction, parent, ct).ConfigureAwait(false);

        if (live.IsFailure)
        {
            return live.Error;
        }

        return await AttenuateVerifiedWithinAsync(connection, transaction, live.Value, child, request,
            authorized, credential, ct).ConfigureAwait(false);
    }

    private async Task<Result<GuildSignetRecord>> AttenuateVerifiedWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Verified live, GuildSignetBinding child, GuildRequestedGrant request,
        GuildSignetAdmissionEvidence authorized, Credential credential, CancellationToken ct)
    {
        GuildSignetIssuanceEdge edge = authorized.Edge;

        GuildDelegationKind? transport = edge switch
        {
            GuildSignetIssuanceEdge.Local or GuildSignetIssuanceEdge.LocalCohort => GuildDelegationKind.Local,
            GuildSignetIssuanceEdge.Remote or GuildSignetIssuanceEdge.RemoteCohort => GuildDelegationKind.Remote,
            _ => null,
        };

        if (transport is null || !live.Effective.Delegation.Contains(transport.Value)
            || edge is GuildSignetIssuanceEdge.LocalCohort or GuildSignetIssuanceEdge.RemoteCohort
                && !live.Effective.Delegation.Contains(GuildDelegationKind.Cohort))
        {
            return new Error(ErrorCodes.Guild.Forbidden, "The issuing run lacks the exact delegation rights for this host-bound edge.");
        }

        if (live.Lineage.Any(row => row.Binding.AgentId == child.AgentId))
        {
            return new Error(ErrorCodes.Guild.DelegationCycle, "The stable agent already occurs in the issuer lineage.");
        }

        GuildSignetRecord issuer = live.Lineage[0];

        GuildSignetOwnerBinding owners = authorized.Owners;

        if (!SameRootOwners(issuer.Owners, owners) || child.OriginPrincipalId != issuer.Binding.OriginPrincipalId)
        {
            return Mismatch;
        }

        GuildGrantScopeEvidence evidence = authorized.ScopeEvidence is { } admitted
            ? new([.. live.Evidence.WorkspaceContainment, .. admitted.WorkspaceContainment],
                [.. live.Evidence.SessionOwnership, .. admitted.SessionOwnership]) : live.Evidence;

        Result<GuildGrant> attenuated = GuildGrantPolicy.Attenuate(live.Effective, request, evidence);

        if (attenuated.IsFailure)
        {
            return attenuated.Error;
        }

        if (!EqualGrant(child.Grant, attenuated.Value))
        {
            return Mismatch;
        }

        GuildSignetRecord row = new(child with { Grant = attenuated.Value }, CopyOwners(owners), issuer.InstallationGeneration,
            issuer.AuthorityEpoch, 1, 1, credential.Digest(), 0, false, false, false, false);

        Result owned = await GuildSignetStore.OwnersAsync(connection, transaction, row, ct).ConfigureAwait(false);

        if (owned.IsFailure)
        {
            return owned.Error;
        }

        Result<bool> inserted = await GuildSignetStore.InsertAsync(connection, transaction, row, ct).ConfigureAwait(false);

        return inserted.IsSuccess ? Result<GuildSignetRecord>.Success(row) : Result<GuildSignetRecord>.Failure(inserted.Error);
    }

    public Task<Result<GuildVerifiedAuthority>> VerifyAsync(GuildExecutionHandle execution, GuildActionRequest action, CancellationToken cancellationToken)
    {
        if (execution is null || action is null || !Enum.IsDefined(action.Action) || action.ResourceId == Guid.Empty)
        {
            return Task.FromResult(Result<GuildVerifiedAuthority>.Failure(Mismatch));
        }

        return UnderRootAsync(execution.RootQuestId, false, async ct =>
        {
            Result<Verified> live = await ReadLiveAsync(execution, ct).ConfigureAwait(false);

            if (live.IsFailure)
            {
                return Result<GuildVerifiedAuthority>.Failure(live.Error);
            }

            // Resource-specific readers and mutations need their later typed boundary participants.
            // A generic Guid cannot authorize a path, memory store, remote edge or tool identity.
            if (action.ResourceId != execution.QuestId || action.Action is not
                (GuildAuthorityAction.Start or GuildAuthorityAction.Continue or GuildAuthorityAction.Cancel or GuildAuthorityAction.ResultApply))
            {
                return Result<GuildVerifiedAuthority>.Failure(new(ErrorCodes.Guild.AuthorityUnavailable,
                    "The action requires its typed Guild resource boundary."));
            }

            return Result<GuildVerifiedAuthority>.Success(Proof(execution, action.Action, live.Value));
        }, cancellationToken);
    }

    public async Task<Result> VerifyToolAsync(GuildExecutionHandle execution, GuildToolGrant tool, bool boundaryAvailable, CancellationToken cancellationToken)
    {
        Result<bool> result = await ToolAsync(execution, tool, boundaryAvailable, GuildAuthorityAction.ToolAdvertise,
            static (_, _) => Task.FromResult(Result<bool>.Success(true)), cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
    }

    public Task<Result<T>> AdmitToolAsync<T>(GuildExecutionHandle execution, GuildToolGrant tool, bool boundaryAvailable,
        Func<GuildVerifiedAuthority, CancellationToken, Task<Result<T>>> effect, CancellationToken cancellationToken) =>
        ToolAsync(execution, tool, boundaryAvailable, GuildAuthorityAction.ToolDispatch, effect, cancellationToken);

    private Task<Result<T>> ToolAsync<T>(GuildExecutionHandle execution, GuildToolGrant tool, bool boundaryAvailable,
        GuildAuthorityAction action, Func<GuildVerifiedAuthority, CancellationToken, Task<Result<T>>> effect, CancellationToken ct)
    {
        if (execution is null || tool is null || effect is null)
        {
            return Task.FromResult(Result<T>.Failure(Mismatch));
        }

        return UnderRootAsync(execution.RootQuestId, action == GuildAuthorityAction.ToolDispatch, async token =>
        {
            Result<Verified> live = await ReadLiveAsync(execution, token).ConfigureAwait(false);

            if (live.IsFailure)
            {
                return Result<T>.Failure(live.Error);
            }

            if (!live.Value.Effective.Tools.Contains(tool))
            {
                return Result<T>.Failure(new(ErrorCodes.Guild.ToolNotGranted, "The exact capability is not granted to this Guild run."));
            }

            if (!boundaryAvailable)
            {
                return Result<T>.Failure(new(ErrorCodes.Guild.ToolBoundaryUnavailable, "The capability has no available host-owned Guild effect boundary."));
            }

            token.ThrowIfCancellationRequested();

            // The read transaction has committed and disposed before adapter handoff. The root
            // gate and installation effect admission remain held through this bounded callback.
            return await effect(Proof(execution, action, live.Value, tool), token).ConfigureAwait(false);
        }, ct);
    }

    public async Task<Result<SignetRevocationDto>> RevokeAsync(GuildCallerContext caller, GuildMutationIdentity mutation,
        Guid signetId, GuildRevokeSignetRequest request, CancellationToken cancellationToken)
    {
        if (caller is null || mutation is null || request is null || signetId == Guid.Empty || request.IdempotencyKey == Guid.Empty
            || mutation.IdempotencyKey != request.IdempotencyKey || !CanonicalDigest(mutation.CanonicalRequestDigest)
            || request.ExpectedRevision < 1 || !Enum.IsDefined(request.ReasonCode)
            || caller.InstallationGeneration != policy.InstallationGeneration || caller.Kind is not (GuildCallerKind.Operator or GuildCallerKind.Run))
        {
            return Mismatch;
        }

        Result<GuildSignetRecord> found = await FindAsync(signetId, false, cancellationToken).ConfigureAwait(false);

        if (found.IsFailure)
        {
            return found.Error;
        }

        return await UnderRootAsync(found.Value.Binding.RootQuestId, true, ct =>
            store.ExecuteAsync<SignetRevocationDto>(true, async (connection, transaction, token) =>
            {
                Result<GuildSignetRecord> current = await GuildSignetStore.ReadAsync(connection, transaction, signetId, false, token).ConfigureAwait(false);

                if (current.IsFailure)
                {
                    return current.Error;
                }

                if (caller.Kind == GuildCallerKind.Run)
                {
                    Result<Verified> run = await LiveAsync(connection, transaction, caller.Execution!, token).ConfigureAwait(false);

                    Result<ImmutableArray<GuildSignetRecord>> targetLineage = await GuildSignetStore.LineageAsync(connection, transaction, current.Value, token).ConfigureAwait(false);

                    if (run.IsFailure || targetLineage.IsFailure || !targetLineage.Value.Any(row => row.Binding.SignetId == caller.Execution!.SignetId))
                    {
                        return GuildSignetStore.NotFound;
                    }
                }

                Result authorized = await policy.AuthorizeRevocationAsync(caller, current.Value, token).ConfigureAwait(false);

                if (authorized.IsFailure)
                {
                    return GuildSignetStore.NotFound;
                }

                Result<SignetRevocationDto> revoked = await GuildSignetStore.RevokeAsync(
                    connection, transaction, current.Value, mutation, request, token).ConfigureAwait(false);

                if (revoked.IsFailure)
                {
                    return revoked.Error;
                }

                DateTimeOffset now = time.GetUtcNow();

                Result withheld = await GuildPortalCleanupParticipant.WithholdRevokedWithinAsync(
                    connection, transaction, current.Value.Binding.QuestId, now, token).ConfigureAwait(false);

                if (withheld.IsFailure)
                {
                    return withheld.Error;
                }

                Result<long> projected = await GuildPortalEvents.AppendWithinAsync(connection, transaction,
                    current.Value.Binding.RootQuestId, current.Value.Binding.QuestId, GuildEventKind.SignetRevoked,
                    signetId, revoked.Value.Revision, null, now, token, GuildPortalEventSource.Signet).ConfigureAwait(false);

                return projected.IsSuccess
                    ? revoked.Value with { EventWatermark = projected.Value }
                    : projected.Error;
            }, ct), cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<GuildExecutionHandle>> RebindAsync(GuildRecoveryContext recovery, CancellationToken cancellationToken)
    {
        if (recovery is null)
        {
            return Mismatch;
        }

        Result<GuildSignetRecord> found = await FindAsync(recovery.QuestId, true, cancellationToken).ConfigureAwait(false);

        if (found.IsFailure)
        {
            return found.Error;
        }

        return await UnderRootAsync(found.Value.Binding.RootQuestId, true, async ct =>
        {
            using Credential replacement = Credential.Create();

            Result<GuildSignetRecord> committed = await store.ExecuteAsync<GuildSignetRecord>(true, async (connection, transaction, token) =>
            {
                Result<GuildSignetRecord> row = await GuildSignetStore.ReadAsync(connection, transaction, recovery.QuestId, true, token).ConfigureAwait(false);

                if (row.IsFailure)
                {
                    return row.Error;
                }

                Result authorized = await policy.AuthorizeRecoveryAsync(recovery, row.Value, token).ConfigureAwait(false);

                if (authorized.IsFailure)
                {
                    return authorized.Error;
                }

                Result<Verified> live = await LiveRowAsync(connection, transaction, row.Value, token).ConfigureAwait(false);

                if (live.IsFailure)
                {
                    return live.Error;
                }

                if (row.Value.Revision != recovery.ExpectedRevision)
                {
                    return GuildSignetStore.Conflict;
                }

                if (row.Value.CredentialGeneration == long.MaxValue || row.Value.Revision == long.MaxValue)
                {
                    return new Error(ErrorCodes.Guild.SequenceExhausted, "The Guild credential generation is exhausted.");
                }

                byte[] digest = replacement.Digest();

                Result<bool> rotated = await GuildSignetStore.RotateAsync(connection, transaction, row.Value, digest, token).ConfigureAwait(false);

                return rotated.IsSuccess ? Result<GuildSignetRecord>.Success(row.Value with
                {
                    Revision = row.Value.Revision + 1,
                    CredentialGeneration = row.Value.CredentialGeneration + 1,
                    TokenDigest = digest
                }) : Result<GuildSignetRecord>.Failure(rotated.Error);
            }, ct).ConfigureAwait(false);

            return committed.IsSuccess ? Register(committed.Value, replacement.Take()) : Result<GuildExecutionHandle>.Failure(committed.Error);
        }, cancellationToken).ConfigureAwait(false);
    }

    private Task<Result<Verified>> ReadLiveAsync(GuildExecutionHandle execution, CancellationToken ct) =>
        store.ExecuteAsync(false, (connection, transaction, token) => LiveAsync(connection, transaction, execution, token), ct);

    private async Task<Result<Verified>> LiveAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildExecutionHandle execution, CancellationToken ct)
    {
        Result<GuildSignetRecord> row = await GuildSignetStore.ReadAsync(connection, transaction, execution.SignetId, false, ct).ConfigureAwait(false);

        if (row.IsFailure)
        {
            return row.Error;
        }

        Result<Verified> live = await LiveRowAsync(connection, transaction, row.Value, ct).ConfigureAwait(false);

        if (live.IsFailure)
        {
            return live.Error;
        }

        GuildSignetRecord target = row.Value;

        if (execution.AgentId != target.Binding.AgentId || execution.RunId != target.Binding.RunId
            || execution.QuestId != target.Binding.QuestId || execution.RootQuestId != target.Binding.RootQuestId
            || execution.IssuerSignetId != target.Binding.IssuerSignetId || execution.CredentialGeneration != target.CredentialGeneration
            || execution.RevocationGeneration != target.RevocationGeneration || !_credentials.TryGetValue(execution, out Credential? credential)
            || !credential.Matches(target.TokenDigest))
        {
            return Mismatch;
        }

        return live;
    }

    // Frames retain the exact per-lineage policy evidence while dependencies are evaluated.
    // The worklist avoids using the process call stack as an implicit delegation-depth limit.
    private sealed class LiveLineageFrame(Guid signet, ImmutableArray<GuildSignetRecord> path, GuildSignetPolicyEvidence evidence)
    {
        internal Guid Signet { get; } = signet;

        internal ImmutableArray<GuildSignetRecord> Path { get; } = path;

        internal GuildSignetPolicyEvidence Evidence { get; } = evidence;

        internal GuildGrant Effective { get; set; } = evidence.HostGrant;

        internal int Index { get; set; } = path.Length - 1;
    }

    private async Task<Result<Verified>> LiveRowAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildSignetRecord row, CancellationToken ct)
    {
        var initial = await PrepareLiveLineageAsync(connection, transaction, row, ct).ConfigureAwait(false);

        if (initial.IsFailure) return initial.Error;

        Stack<LiveLineageFrame> pending = new();

        HashSet<Guid> active = [];

        Dictionary<Guid, Verified> completed = [];

        pending.Push(initial.Value);

        active.Add(row.Binding.SignetId);

        while (pending.Count > 0)
        {
            LiveLineageFrame frame = pending.Peek();

            if (frame.Index < 0)
            {
                if (frame.Effective.ExpiresAtUtc is { } expiry && time.GetUtcNow() >= expiry) return Expired;

                pending.Pop();

                active.Remove(frame.Signet);

                Verified verified = new(frame.Effective, frame.Path, frame.Evidence.ScopeEvidence);

                completed.Add(frame.Signet, verified);

                if (pending.Count == 0) return verified;

                LiveLineageFrame parent = pending.Peek();

                parent.Effective = GuildSignetGrantComposition.Intersect(parent.Effective, frame.Effective, parent.Evidence.ScopeEvidence);

                continue;
            }

            GuildSignetRecord ancestor = frame.Path[frame.Index--];

            frame.Effective = GuildSignetGrantComposition.Intersect(frame.Effective, ancestor.Binding.Grant, frame.Evidence.ScopeEvidence);

            Result<GuildGrant?> cohort = await GuildCohortStore.ReadAuthorityConstraintWithinAsync(
                connection, transaction, ancestor, ct).ConfigureAwait(false);

            if (cohort.IsFailure) return cohort.Error;

            if (cohort.Value is { } ceiling)
                frame.Effective = GuildSignetGrantComposition.Intersect(frame.Effective, ceiling, frame.Evidence.ScopeEvidence);

            Result<GuildSignetRecord?> primary = await GuildCohortStore.ReadAwardPrimaryWithinAsync(connection, transaction, ancestor, ct).ConfigureAwait(false);

            if (primary.IsFailure) return primary.Error;

            if (primary.Value is not { } primaryRow) continue;

            if (primaryRow.Binding.AgentId != ancestor.Binding.AgentId || primaryRow.Binding.RootQuestId != ancestor.Binding.RootQuestId)
                return GuildSignetStore.Unavailable;

            if (completed.TryGetValue(primaryRow.Binding.SignetId, out Verified? previous))
            {
                frame.Effective = GuildSignetGrantComposition.Intersect(frame.Effective, previous.Effective, frame.Evidence.ScopeEvidence);

                continue;
            }

            if (!active.Add(primaryRow.Binding.SignetId)) return GuildSignetStore.Unavailable;

            var dependency = await PrepareLiveLineageAsync(connection, transaction, primaryRow, ct).ConfigureAwait(false);

            if (dependency.IsFailure) return dependency.Error;

            pending.Push(dependency.Value);
        }

        return GuildSignetStore.Unavailable;
    }

    private async Task<Result<LiveLineageFrame>> PrepareLiveLineageAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildSignetRecord row, CancellationToken ct)
    {
        Result<Guid> epoch = await GuildSignetStore.EpochAsync(connection, transaction, ct).ConfigureAwait(false);

        Result<ImmutableArray<GuildSignetRecord>> path = await GuildSignetStore.LineageAsync(connection, transaction, row, ct).ConfigureAwait(false);

        if (epoch.IsFailure || path.IsFailure) return epoch.IsFailure ? epoch.Error : path.Error;

        foreach (GuildSignetRecord ancestor in path.Value)
        {
            if (ancestor.Revoked || ancestor.RestoredInert || ancestor.Retired || ancestor.AuthorityEpoch != epoch.Value
                || ancestor.InstallationGeneration != policy.InstallationGeneration) return Revoked;

            if (ancestor.Binding.Grant.ExpiresAtUtc is { } expiry && time.GetUtcNow() >= expiry) return Expired;

            Result owners = await GuildSignetStore.OwnersAsync(connection, transaction, ancestor, ct).ConfigureAwait(false);

            if (owners.IsFailure) return owners.Error;
        }

        Result<GuildSignetPolicyEvidence> policyEvidence = await policy.ResolveCurrentAsync(path.Value, ct).ConfigureAwait(false);

        if (policyEvidence.IsFailure) return policyEvidence.Error;

        GuildSignetPolicyEvidence evidence = policyEvidence.Value;

        if (GuildGrantPolicy.Validate(evidence.HostGrant).IsFailure || evidence.ScopeEvidence is null) return GuildSignetStore.Unavailable;

        return new LiveLineageFrame(row.Binding.SignetId, path.Value, evidence);
    }

    private Task<Result<GuildSignetRecord>> FindAsync(Guid id, bool byQuest, CancellationToken ct) =>
        WithInstallationAsync(false, token => store.ExecuteAsync(false,
            (connection, transaction, admitted) => GuildSignetStore.ReadAsync(connection, transaction, id, byQuest, admitted), token), ct);

    private Task<Result<T>> UnderRootAsync<T>(Guid root, bool effect, Func<CancellationToken, Task<Result<T>>> action, CancellationToken ct) =>
        WithInstallationAsync(effect, token => GuildSignetRootGate.RunAsync(gate, root, action, token), ct);

    private async Task<Result<T>> WithInstallationEffectAsync<T>(
        Func<CancellationToken, Task<Result<T>>> action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (Volatile.Read(ref _disposed) != 0 || policy.InstallationGeneration == Guid.Empty)
        {
            return GuildSignetStore.Unavailable;
        }

        if (!gate.TryAcquireWorkLease(GrimoireWorkKind.GuildStorage, out IGrimoireWorkLease? work))
        {
            return Deferred;
        }

        await using IGrimoireWorkLease held = work!;

        if (!held.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? group))
        {
            return Deferred;
        }

        await using IGrimoireExternalEffectGroup heldEffect = group!;

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, held.MaintenanceRevocation);

        try
        {
            return await action(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && held.MaintenanceRevocation.IsCancellationRequested)
        {
            return Deferred;
        }
        catch (OverflowException)
        {
            return new Error(ErrorCodes.Guild.SequenceExhausted, "The Guild authority generation is exhausted.");
        }
    }

    private async Task<Result<T>> WithInstallationAsync<T>(bool effect,
        Func<CancellationToken, Task<Result<T>>> action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (Volatile.Read(ref _disposed) != 0 || policy.InstallationGeneration == Guid.Empty)
        {
            return GuildSignetStore.Unavailable;
        }

        if (!gate.TryAcquireWorkLease(GrimoireWorkKind.GuildStorage, out IGrimoireWorkLease? work))
        {
            return Deferred;
        }

        await using IGrimoireWorkLease held = work!;

        IGrimoireExternalEffectGroup? group = null;

        if (effect && !held.TryBeginExternalEffectGroup(out group))
        {
            return Deferred;
        }

        await using IGrimoireExternalEffectGroup? heldEffect = group;

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, held.MaintenanceRevocation);

        try
        {
            return await action(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && held.MaintenanceRevocation.IsCancellationRequested)
        {
            return Deferred;
        }
        catch (OverflowException)
        {
            return new Error(ErrorCodes.Guild.SequenceExhausted, "The Guild authority generation is exhausted.");
        }
    }

    private Result<GuildExecutionHandle> Register(GuildSignetRecord row, Credential credential)
    {
        GuildSignetBinding binding = row.Binding;

        GuildExecutionHandle handle = new(binding.SignetId, binding.AgentId, binding.RunId, binding.QuestId,
            binding.RootQuestId, row.CredentialGeneration, row.RevocationGeneration, binding.IssuerSignetId);

        if (Volatile.Read(ref _disposed) != 0 || !_credentials.TryAdd(handle, credential))
        {
            credential.Dispose();

            return GuildSignetStore.Unavailable;
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            Release(handle);

            return GuildSignetStore.Unavailable;
        }

        foreach (GuildExecutionHandle previous in _credentials.Keys.Where(previous => !ReferenceEquals(previous, handle)
            && previous.SignetId == handle.SignetId))
        {
            Release(previous);
        }

        return handle;
    }

    internal void Release(GuildExecutionHandle execution)
    {
        if (_credentials.TryRemove(execution, out Credential? credential))
        {
            credential.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            foreach (GuildExecutionHandle execution in _credentials.Keys)
            {
                Release(execution);
            }
        }
    }

    private bool ValidBinding(GuildSignetBinding binding) => binding is not null && binding.SignetId != Guid.Empty
        && binding.AgentId != Guid.Empty && binding.RunId != Guid.Empty && binding.QuestId != Guid.Empty
        && binding.RootQuestId != Guid.Empty && binding.OriginPrincipalId != Guid.Empty
        && binding.IssuedAtUtc.Offset == TimeSpan.Zero && binding.IssuedAtUtc <= time.GetUtcNow()
        && (binding.QuestId == binding.RootQuestId ? binding.IssuerSignetId is null : binding.IssuerSignetId is { } id && id != Guid.Empty)
        && GuildGrantPolicy.Validate(binding.Grant).IsSuccess;

    private static bool MatchesPeer(GuildCallerContext caller, GuildSignetOwnerBinding owners) => caller.Kind == GuildCallerKind.Peer
        ? owners.PeerId == caller.PeerId && owners.PeerGrantRevision == caller.PeerGrantRevision
        : owners.PeerId is null && owners.PeerGrantRevision is null;

    private static bool SameRootOwners(GuildSignetOwnerBinding first, GuildSignetOwnerBinding second) =>
        first.CampaignId == second.CampaignId && first.WorkspaceId == second.WorkspaceId && first.PeerId == second.PeerId
        && first.PeerGrantRevision == second.PeerGrantRevision && first.WorkspaceIdentityPolicyVersion == second.WorkspaceIdentityPolicyVersion
        && first.WorkspaceIdentityRevision == second.WorkspaceIdentityRevision
        && (first.WorkspaceRootIdentity is null ? second.WorkspaceRootIdentity is null
            : second.WorkspaceRootIdentity is not null && CryptographicOperations.FixedTimeEquals(first.WorkspaceRootIdentity, second.WorkspaceRootIdentity));

    private static GuildSignetOwnerBinding CopyOwners(GuildSignetOwnerBinding owners) => owners with { WorkspaceRootIdentity = owners.WorkspaceRootIdentity?.ToArray() };

    private static GuildRequestedGrant Request(GuildGrant grant) => new(new(true, grant.Workspaces), new(true, grant.Tools),
        new(true, grant.Memory), new(true, grant.Budget), new(true, grant.Delegation), new(true, grant.ExpiresAtUtc));

    private static bool EqualGrant(GuildGrant left, GuildGrant right) => left.Workspaces.ToHashSet().SetEquals(right.Workspaces)
        && left.Tools.ToHashSet().SetEquals(right.Tools) && left.Memory.ToHashSet().SetEquals(right.Memory)
        && left.Delegation.ToHashSet().SetEquals(right.Delegation) && left.Budget == right.Budget && left.ExpiresAtUtc == right.ExpiresAtUtc;

    private static bool CanonicalDigest(string value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static GuildVerifiedAuthority Proof(GuildExecutionHandle execution, GuildAuthorityAction action, Verified verified, GuildToolGrant? tool = null) =>
        new(execution, action, execution.QuestId, verified.Lineage[0].Revision, verified.Effective, tool);

    private static Error Mismatch => new(ErrorCodes.Guild.AuthorityMismatch, "The Guild execution or trusted host binding does not match.");

    private static Error Revoked => new(ErrorCodes.Guild.AuthorityRevoked, "The Guild execution or an ancestor is no longer live.");

    private static Error Expired => new(ErrorCodes.Guild.AuthorityExpired, "The Guild execution authority has expired.");

    private static Error Deferred => new(ErrorCodes.Guild.MaintenanceDeferred, "Guild authority is deferred for installation maintenance.");

    private sealed record Verified(GuildGrant Effective, ImmutableArray<GuildSignetRecord> Lineage, GuildGrantScopeEvidence Evidence);

    /// <summary>Only this process retains raw bytes; the database receives SHA-256 of those bytes.</summary>
    private sealed class Credential : IDisposable
    {
        private readonly object _lock = new();

        private byte[]? _raw;

        private char[]? _presentation;

        private Credential(byte[] raw)
        {
            _raw = raw;

            Span<char> encoded = stackalloc char[44];

            if (!Convert.TryToBase64Chars(raw, encoded, out int written) || written != 44)
            {
                encoded.Clear();

                throw new InvalidOperationException("The Signet credential cannot be encoded.");
            }

            _presentation = new char[43];

            for (int index = 0; index < _presentation.Length; index++)
            {
                _presentation[index] = encoded[index] switch { '+' => '-', '/' => '_', var character => character };
            }

            encoded.Clear();
        }

        internal static Credential Create() => new(RandomNumberGenerator.GetBytes(32));

        internal byte[] Digest()
        {
            lock (_lock)
            {
                return _raw is not null ? SHA256.HashData(_raw) : throw new ObjectDisposedException(nameof(Credential));
            }
        }

        internal bool Matches(byte[] digest)
        {
            lock (_lock)
            {
                return _raw is not null && digest.Length == 32 && CryptographicOperations.FixedTimeEquals(SHA256.HashData(_raw), digest);
            }
        }

        internal Credential Take()
        {
            lock (_lock)
            {
                Credential owner = new(_raw ?? throw new ObjectDisposedException(nameof(Credential)));

                _raw = null;

                if (_presentation is not null)
                {
                    Array.Clear(_presentation);

                    _presentation = null;
                }

                return owner;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_raw is not null)
                {
                    CryptographicOperations.ZeroMemory(_raw);

                    _raw = null;
                }

                if (_presentation is not null)
                {
                    Array.Clear(_presentation);

                    _presentation = null;
                }
            }
        }
    }
}
