using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Tower;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Local host authentication and current canonical authority. Issued objects, never copied ids, authenticate callers.</summary>
internal sealed partial class GuildLocalHostPolicy(IGrimoireOrdinaryConnectionFactory connections, IGrimoireConnectionAdmissionGate gate,
    IOptionsMonitor<ArcanumSettings> settings, PhysicalCampaignRootOpener opener, TimeProvider time, GuildInboundPeerRegistry? inboundPeers = null)
    : IGuildQuestHostPolicy, IGuildSignetHostPolicy, IGuildWorkspaceLeaseHostEvidence
{
    private readonly GuildSignetStore _store = new(connections);

    private readonly ConditionalWeakTable<GuildCallerContext, object> _callers = new();

    private readonly ConditionalWeakTable<GuildAdmissionContext, Admission> _admissions = new();

    private readonly ConditionalWeakTable<GuildRecoveryContext, GuildCallerContext> _recoveries = new();

    private readonly ConcurrentDictionary<Guid, Admission> _pending = new();

    private Identity? _identity;

    internal bool IsExecutionEnabled => settings.CurrentValue.Features.Guild;

    internal bool IsConfiguredRemoteDestination(string destination) => IsExecutionEnabled
        && settings.CurrentValue.Features.Conclave && settings.CurrentValue.ResolveA2A().ClientEnabled
        && RetroDownfall.Arcanum.Infrastructure.A2A.A2AClientService.IsConfiguredGuildDestination(destination,
            settings.CurrentValue.ResolveA2A().AllowedRemoteAgents);

    public Guid InstallationGeneration => Volatile.Read(ref _identity)?.Generation ?? Guid.Empty;

    public Guid OperatorPrincipalId => Volatile.Read(ref _identity)?.Principal ?? Guid.Empty;

    public bool Authenticates(GuildCallerContext caller)
    {
        if (caller is null || !_callers.TryGetValue(caller, out object? credential) || caller.InstallationGeneration != InstallationGeneration)
            return false;

        return caller.Kind == GuildCallerKind.Peer
            ? settings.CurrentValue.Features.Guild && _inboundPersistenceAvailable && credential is GuildInboundPeer peer && inboundPeers?.Authenticates(peer) == true
                && caller.PeerId == peer.PeerId && caller.PeerGrantRevision == peer.GrantRevision
            : caller.PrincipalId == OperatorPrincipalId && caller.Kind is GuildCallerKind.Operator or GuildCallerKind.Run;
    }

    internal async Task<Result<GuildCallerContext>> CreateNativeOperatorAsync(Guid invocationId, CancellationToken ct)
    {
        if (invocationId == Guid.Empty) return Forbidden;

        Result initialized = await InitializeIdentityAsync(ct).ConfigureAwait(false);

        if (initialized.IsFailure) return initialized.Error;

        Identity identity = Volatile.Read(ref _identity)!;

        GuildCallerContext caller = new(GuildCallerKind.Operator, identity.Principal, invocationId, identity.Generation);

        _callers.Add(caller, new());

        return caller;
    }

    /// <summary>Initializes committed installation evidence without issuing an operator capability.</summary>
    internal async Task<Result> InitializeIdentityAsync(CancellationToken ct)
    {
        Result<Identity> committed = await ExecuteAsync<Identity>(true, async (connection, transaction, token) =>
        {
            Result<Guid> generation = await ReadGenerationAsync(connection, transaction, token).ConfigureAwait(false);

            if (generation.IsFailure) return generation.Error;

            Identity? current = Volatile.Read(ref _identity);

            if (current is not null && current.Generation != generation.Value) return Unavailable;

            Result<Guid> principal = await GuildWorkspaceLeaseStore.GetNativeOperatorPrincipalWithinAsync(connection, transaction, generation.Value, token).ConfigureAwait(false);

            if (principal.IsFailure || current is not null && current.Principal != principal.Value) return Unavailable;

            return new Identity(generation.Value, principal.Value);
        }, ct).ConfigureAwait(false);

        if (committed.IsFailure) return committed.Error;

        Identity identity = committed.Value;

        Identity? original = Interlocked.CompareExchange(ref _identity, identity, null);

        if (original is not null && original != identity) return Unavailable;

        return Result.Success();
    }

    internal async Task<Result<GuildCallerContext>> CreateRunCallerAsync(SignetAuthority authority, GuildExecutionHandle execution,
        Guid invocationId, CancellationToken ct)
    {
        if (authority is null || execution is null || invocationId == Guid.Empty || OperatorPrincipalId == Guid.Empty) return Forbidden;

        Result<GuildVerifiedAuthority> live = await authority.VerifyAsync(execution, new(GuildAuthorityAction.Start, execution.QuestId), ct).ConfigureAwait(false);

        if (live.IsFailure) return live.Error;

        return await ExecuteAsync<GuildCallerContext>(false, async (connection, transaction, token) =>
        {
            Result current = await CurrentIdentityAsync(connection, transaction, token).ConfigureAwait(false);

            if (current.IsFailure) return current.Error;

            Result<GuildSignetRecord> row = await GuildSignetStore.ReadAsync(connection, transaction, execution.SignetId, false, token).ConfigureAwait(false);

            if (row.IsFailure || row.Value.Binding.OriginPrincipalId != OperatorPrincipalId
                || row.Value.InstallationGeneration != InstallationGeneration || row.Value.RestoredInert || row.Value.Revoked || row.Value.Retired)
                return Forbidden;

            GuildCallerContext caller = new(GuildCallerKind.Run, OperatorPrincipalId, invocationId, InstallationGeneration, execution);

            _callers.Add(caller, new());

            return Result<GuildCallerContext>.Success(caller);
        }, ct).ConfigureAwait(false);
    }

    internal Task<Result<GuildRecoveryContext>> CreateRecoveryAsync(GuildCallerContext caller, Guid questId, Guid operationId,
        long expectedRevision, CancellationToken ct) => ExecuteAsync<GuildRecoveryContext>(false, async (connection, transaction, token) =>
        {
            if (!Authenticates(caller) || caller.Kind != GuildCallerKind.Operator || expectedRevision < 1) return Forbidden;

            Result current = await CurrentIdentityAsync(connection, transaction, token).ConfigureAwait(false);

            if (current.IsFailure) return current.Error;

            Result<GuildSignetRecord> row = await GuildSignetStore.ReadAsync(connection, transaction, questId, true, token).ConfigureAwait(false);

            if (row.IsFailure || row.Value.Revision != expectedRevision || row.Value.RestoredInert || row.Value.Retired
                || row.Value.Revoked || row.Value.Binding.OriginPrincipalId != caller.PrincipalId) return Forbidden;

            if (!await OperationMatchesAsync(connection, transaction, questId, operationId, token).ConfigureAwait(false)) return Forbidden;

            GuildRecoveryContext recovery = new(questId, operationId, expectedRevision);

            _recoveries.Add(recovery, caller);

            return Result<GuildRecoveryContext>.Success(recovery);
        }, ct);

    public async Task<Result> AuthorizeLocateAsync(GuildCallerContext caller, SqliteConnection connection, SqliteTransaction transaction,
        Guid? questId, Guid? sessionId, Guid? campaignId, CancellationToken ct)
    {
        if (!Authenticates(caller)) return Forbidden;

        Result identity = await CurrentIdentityAsync(connection, transaction, ct).ConfigureAwait(false);

        if (identity.IsFailure) return identity;

        if (questId is { } quest)
        {
            if (caller.Kind == GuildCallerKind.Peer)
                return await RequireInboundRootForPeerWithinAsync(connection, transaction, caller, quest, ct).ConfigureAwait(false);

            await using SqliteCommand command = Command(connection, transaction, "SELECT OriginPrincipalId FROM guild_signets WHERE QuestId=$quest");

            Add(command, "$quest", Id(quest));

            if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string principal || principal != Id(OperatorPrincipalId)) return Forbidden;

            if (caller.Kind == GuildCallerKind.Run)
            {
                if (caller.Execution is not { } execution) return Forbidden;

                command.CommandText = """
                    WITH RECURSIVE lineage(Id,ParentQuestId) AS (
                        SELECT Id,ParentQuestId FROM guild_quests WHERE Id=$quest AND RootQuestId=$root
                        UNION SELECT q.Id,q.ParentQuestId FROM guild_quests q JOIN lineage p ON q.Id=p.ParentQuestId WHERE q.RootQuestId=$root)
                    SELECT EXISTS(SELECT 1 FROM lineage WHERE Id=$issuer)
                    """;

                Add(command, "$issuer", Id(execution.QuestId));

                Add(command, "$root", Id(execution.RootQuestId));

                if (!Equals(1L, await command.ExecuteScalarAsync(ct).ConfigureAwait(false))) return Forbidden;
            }
        }

        return Result.Success();
    }

    public async Task<Result<GuildQuestAdmissionPlan>> ResolveAsync(GuildCallerContext caller, GuildQuestProposal proposal,
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        if (!settings.CurrentValue.Features.Guild || !Authenticates(caller)) return Forbidden;

        Result identity = await CurrentIdentityAsync(connection, transaction, ct).ConfigureAwait(false);

        if (identity.IsFailure) return identity.Error;

        bool root = proposal.Root is not null;

        if (root ? caller.Kind is not (GuildCallerKind.Operator or GuildCallerKind.Peer) || proposal.ParentQuestId is not null
            : caller.Kind != GuildCallerKind.Run || caller.Execution?.QuestId != proposal.ParentQuestId || proposal.Child is null) return Forbidden;

        Result ownersValid = await ValidateOwnersAsync(connection, transaction, root ? proposal.Root!.SessionId : null,
            proposal.CampaignId, proposal.WorkspaceId, ct).ConfigureAwait(false);

        if (ownersValid.IsFailure) return ownersValid.Error;

        GuildInboundPeer? peer = null;

        if (caller.Kind == GuildCallerKind.Peer)
        {
            if (!_callers.TryGetValue(caller, out object? peerCredential) || peerCredential is not GuildInboundPeer currentPeer
                || proposal.Root is not { } peerRoot || peerRoot.CampaignId != currentPeer.CampaignId
                || peerRoot.SessionId != currentPeer.SessionId || peerRoot.WorkspaceId != currentPeer.WorkspaceId
                || !SameGrant(peerRoot.Grant, currentPeer.Grant)) return Forbidden;

            peer = currentPeer;

            Result<Guid> epoch = await EnsureInboundPeerWithinAsync(connection, transaction, peer, ct).ConfigureAwait(false);

            if (epoch.IsFailure) return epoch.Error;
        }

        GuildGrant grant;

        if (root)
        {
            grant = proposal.Root!.Grant;

            Result<GuildGrantScopeEvidence> scope = await ScopeAsync(connection, transaction, grant, grant, ct).ConfigureAwait(false);

            if (scope.IsFailure) return scope.Error;
        }
        else
        {
            Result<GuildSignetRecord> parent = await GuildSignetStore.ReadAsync(connection, transaction, proposal.ParentQuestId!.Value, true, ct).ConfigureAwait(false);

            if (parent.IsFailure || parent.Value.Owners.CampaignId != proposal.CampaignId || parent.Value.Owners.WorkspaceId != proposal.WorkspaceId
                || parent.Value.Binding.OriginPrincipalId != caller.PrincipalId) return Forbidden;

            GuildRequestedGrant request = proposal.Child!.RequestedGrant ?? new();

            if (request.Workspaces.IsSpecified && request.Workspaces.Value is null
                || request.Tools.IsSpecified && request.Tools.Value is null || request.Memory.IsSpecified && request.Memory.Value is null
                || request.Budget.IsSpecified && request.Budget.Value is null || request.Delegation.IsSpecified && request.Delegation.Value is null) return Forbidden;

            GuildGrant parentGrant = parent.Value.Binding.Grant;

            GuildGrant requested = new(request.Workspaces.IsSpecified ? request.Workspaces.Value!.Value : parentGrant.Workspaces,
                request.Tools.IsSpecified ? request.Tools.Value!.Value : parentGrant.Tools,
                request.Memory.IsSpecified ? request.Memory.Value!.Value : parentGrant.Memory,
                request.Budget.IsSpecified ? request.Budget.Value! : parentGrant.Budget,
                request.Delegation.IsSpecified ? request.Delegation.Value!.Value : parentGrant.Delegation,
                request.ExpiresAtUtc.IsSpecified ? request.ExpiresAtUtc.Value : parentGrant.ExpiresAtUtc);

            Result<GuildGrantScopeEvidence> scope = await ScopeAsync(connection, transaction, parentGrant, requested, ct).ConfigureAwait(false);

            if (scope.IsFailure) return scope.Error;

            Result<GuildGrant> narrowed = GuildGrantPolicy.Attenuate(parentGrant, request, scope.Value);

            if (narrowed.IsFailure) return narrowed.Error;

            grant = narrowed.Value;
        }

        if (GuildGrantPolicy.Validate(grant).IsFailure || grant.Workspaces.Any(item => item.WorkspaceId != proposal.WorkspaceId) || grant.Delegation.Any(item => item is not (GuildDelegationKind.Local or GuildDelegationKind.Remote))) return Forbidden;

        GuildRemoteTransportDescriptor? remote = null;

        if ((proposal.Root?.AgentId ?? proposal.Child?.AgentId) is { } selectedAgent)
        {
            var destination = await GuildRemoteExecutionStore.ResolveTransportWithinAsync(connection, transaction,
                selectedAgent, new(proposal.WorkspaceId, proposal.SessionId, proposal.CampaignId), ct).ConfigureAwait(false);

            if (destination.IsFailure) return destination.Error;

            remote = destination.Value;

            if (remote is not null)
            {
                if (grant.Budget.Usd.Mode != GuildBudgetMode.Unlimited || grant.Budget.Tokens.Mode != GuildBudgetMode.Unlimited)
                    return new Error(ErrorCodes.Guild.BudgetCapabilityUnavailable, "The remote transport cannot enforce a finite Guild spending grant.");

                if (root && !grant.Delegation.Contains(GuildDelegationKind.Remote)) return Forbidden;

                if (!root)
                {
                    var parent = await GuildSignetStore.ReadAsync(connection, transaction, proposal.ParentQuestId!.Value, true, ct).ConfigureAwait(false);

                    if (parent.IsFailure || !parent.Value.Binding.Grant.Delegation.Contains(GuildDelegationKind.Remote)) return Forbidden;
                }
            }
        }

        GuildSignetOwnerBinding owners = new(proposal.SessionId, proposal.CampaignId, proposal.WorkspaceId, peer?.PeerId, peer?.GrantRevision);

        if (!root)
        {
            Result<GuildSignetRecord> parent = await GuildSignetStore.ReadAsync(connection, transaction, proposal.ParentQuestId!.Value, true, ct).ConfigureAwait(false);

            if (parent.IsFailure) return parent.Error;

            owners = owners with { PeerId = parent.Value.Owners.PeerId, PeerGrantRevision = parent.Value.Owners.PeerGrantRevision };
        }

        if (proposal.WorkspaceId is { } workspace)
        {
            Result<GuildWorkspaceRegistration> registration = await RegistrationAsync(connection, transaction, workspace, ct).ConfigureAwait(false);

            if (registration.IsFailure) return registration.Error;

            owners = owners with { WorkspaceIdentityPolicyVersion = registration.Value.PolicyVersion,
                WorkspaceIdentityRevision = registration.Value.Revision, WorkspaceRootIdentity = registration.Value.RootIdentity.Bytes };
        }

        GuildAdmissionContext admission = new(caller, Guid.NewGuid(), proposal.Mutation);

        Admission pending = new(this, admission, proposal, grant, owners, connection, transaction, remote);

        _admissions.Add(admission, pending);

        if (!_pending.TryAdd(proposal.QuestId, pending)) return Forbidden;

        GuildAgentCard card = new(Guid.Empty, Guid.Empty, 0, proposal.Root?.Name ?? proposal.Child?.Name ?? "Apprentice", "Local Guild Apprentice", "1", [], ["text/plain"], ["text/plain"],
            new(false, false, false), new(GuildAgentSourceKind.Derived, null, null, true, null));

        return new GuildQuestAdmissionPlan(admission, grant, owners, OperatorPrincipalId,
            remote is null ? new(card, "", []) : null, Lifetime: pending, RemoteTransport: remote);
    }

    public async Task<Result<GuildSignetAdmissionEvidence>> AuthorizeAdmissionAsync(GuildAdmissionContext admission,
        GuildSignetBinding binding, CancellationToken ct)
    {
        if (!settings.CurrentValue.Features.Guild || admission is null || !_admissions.TryGetValue(admission, out Admission? scope)
            || !Authenticates(admission.Caller) || scope.Transaction.Connection is null
            || binding.QuestId != scope.Proposal.QuestId || binding.RunId != scope.Proposal.RunId
            || binding.IssuerSignetId != admission.Caller.Execution?.SignetId || binding.OriginPrincipalId != OperatorPrincipalId
            || binding.RootQuestId != (admission.Caller.Execution?.RootQuestId ?? scope.Proposal.QuestId)
            || !ReferenceEquals(binding.Grant, scope.Grant) || Interlocked.CompareExchange(ref scope.Consumed, 1, 0) != 0) return Forbidden;

        Result identity = await CurrentIdentityAsync(scope.Connection, scope.Transaction, ct).ConfigureAwait(false);

        return identity.IsFailure ? identity.Error : new GuildSignetAdmissionEvidence(scope.Owners,
            scope.Proposal.Root is not null ? GuildSignetIssuanceEdge.Root
                : scope.RemoteTransport is null ? GuildSignetIssuanceEdge.Local : GuildSignetIssuanceEdge.Remote);
    }

    public Task<Result<GuildSignetPolicyEvidence>> ResolveCurrentAsync(ImmutableArray<GuildSignetRecord> lineage, CancellationToken ct)
    {
        if (lineage.IsDefaultOrEmpty) return Task.FromResult(Result<GuildSignetPolicyEvidence>.Failure(Forbidden));

        if (_pending.TryGetValue(lineage[0].Binding.QuestId, out Admission? pending) && pending.Transaction.Connection is not null)
            return ResolveCurrentWithinAsync(lineage, pending.Connection, pending.Transaction, ct);

        return ExecuteAsync<GuildSignetPolicyEvidence>(false, (connection, transaction, token) => ResolveCurrentWithinAsync(lineage, connection, transaction, token), ct);
    }

    private async Task<Result<GuildSignetPolicyEvidence>> ResolveCurrentWithinAsync(ImmutableArray<GuildSignetRecord> lineage,
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        Result identity = await CurrentIdentityAsync(connection, transaction, ct).ConfigureAwait(false);

        if (identity.IsFailure) return identity.Error;

        GuildGrant rootGrant = lineage[^1].Binding.Grant;

        GuildGrantScopeEvidence evidence = new([], []);

        foreach (GuildSignetRecord row in lineage)
        {
            if (row.InstallationGeneration != InstallationGeneration || row.Binding.OriginPrincipalId != OperatorPrincipalId
                || row.Revoked || row.RestoredInert || row.Retired
                || row.Binding.Grant.ExpiresAtUtc is { } expiry && expiry <= time.GetUtcNow()) return Forbidden;

            if (row.Owners.PeerId is { } peerId)
            {
                Result peerLive = await RequireInboundRootWithinAsync(connection, transaction, row.Binding.RootQuestId,
                    peerId, row.Owners.PeerGrantRevision!, ct).ConfigureAwait(false);

                if (peerLive.IsFailure) return peerLive.Error;
            }

            Result owners = await ValidateOwnersAsync(connection, transaction, row.Owners.SessionId, row.Owners.CampaignId, row.Owners.WorkspaceId, ct).ConfigureAwait(false);

            if (owners.IsFailure) return owners.Error;

            if (row.Owners.WorkspaceId is { } workspace)
            {
                Result<GuildWorkspaceRegistration> current = await RegistrationAsync(connection, transaction, workspace, ct).ConfigureAwait(false);

                if (current.IsFailure) return current.Error;

                if (row.Owners.WorkspaceIdentityPolicyVersion != current.Value.PolicyVersion || row.Owners.WorkspaceIdentityRevision != current.Value.Revision
                    || row.Owners.WorkspaceRootIdentity is not { Length: 32 } pin || !CryptographicOperations.FixedTimeEquals(pin, current.Value.RootIdentity.Bytes)) return Forbidden;
            }

            Result<GuildGrantScopeEvidence> scope = await ScopeAsync(connection, transaction, rootGrant, row.Binding.Grant, ct).ConfigureAwait(false);

            if (scope.IsFailure) return scope.Error;

            evidence = new([.. evidence.WorkspaceContainment, .. scope.Value.WorkspaceContainment], [.. evidence.SessionOwnership, .. scope.Value.SessionOwnership]);
        }

        return new GuildSignetPolicyEvidence(rootGrant, evidence);
    }

    public async Task<Result> AuthorizeRevocationAsync(GuildCallerContext caller, GuildSignetRecord target, CancellationToken ct)
    {
        if (!Authenticates(caller) || target.Binding.OriginPrincipalId != OperatorPrincipalId
            || caller.Kind == GuildCallerKind.Run && caller.Execution?.RootQuestId != target.Binding.RootQuestId) return Forbidden;

        Result<bool> current = await ExecuteAsync<bool>(false, async (connection, transaction, token) =>
        {
            Result identity = await CurrentIdentityAsync(connection, transaction, token).ConfigureAwait(false);

            return identity.IsFailure ? identity.Error : Result<bool>.Success(true);
        }, ct).ConfigureAwait(false);

        return current.IsFailure ? current.Error : Result.Success();
    }

    public async Task<Result> AuthorizeRecoveryAsync(GuildRecoveryContext recovery, GuildSignetRecord target, CancellationToken ct)
    {
        if (recovery is null || !_recoveries.TryGetValue(recovery, out GuildCallerContext? caller) || !Authenticates(caller)
            || caller.Kind != GuildCallerKind.Operator || target.Binding.QuestId != recovery.QuestId
            || target.Revision != recovery.ExpectedRevision || target.RestoredInert || target.Retired || target.Revoked
            || target.InstallationGeneration != InstallationGeneration || target.Binding.OriginPrincipalId != OperatorPrincipalId) return Forbidden;

        Result<bool> current = await ExecuteAsync<bool>(false, async (connection, transaction, token) =>
        {
            Result identity = await CurrentIdentityAsync(connection, transaction, token).ConfigureAwait(false);

            if (identity.IsFailure) return identity.Error;

            return await OperationMatchesAsync(connection, transaction, recovery.QuestId, recovery.OperationId, token).ConfigureAwait(false)
                ? Result<bool>.Success(true) : Forbidden;
        }, ct).ConfigureAwait(false);

        return current.IsFailure ? current.Error : Result.Success();
    }

    public Task<Result<GuildWorkspaceHostEvidence>> ResolveAsync(Guid workspaceId, CancellationToken ct) => ExecuteAsync<GuildWorkspaceHostEvidence>(false,
        async (connection, transaction, token) =>
        {
            Result identity = await CurrentIdentityAsync(connection, transaction, token).ConfigureAwait(false);

            if (identity.IsFailure) return identity.Error;

            Result<GuildWorkspaceRegistration> registration = await RegistrationAsync(connection, transaction, workspaceId, token).ConfigureAwait(false);

            return registration.IsFailure ? registration.Error : new GuildWorkspaceHostEvidence(registration.Value.RootPath,
                registration.Value.PolicyVersion, registration.Value.Revision, registration.Value.RootIdentity.Bytes, [new(".", GuildPathScopeKind.Subtree)]);
        }, ct);

    private sealed record Identity(Guid Generation, Guid Principal);

    private sealed class Admission(GuildLocalHostPolicy owner, GuildAdmissionContext context, GuildQuestProposal proposal,
        GuildGrant grant, GuildSignetOwnerBinding owners, SqliteConnection connection, SqliteTransaction transaction,
        GuildRemoteTransportDescriptor? remoteTransport) : IDisposable
    {
        internal GuildQuestProposal Proposal { get; } = proposal;

        internal GuildGrant Grant { get; } = grant;

        internal GuildSignetOwnerBinding Owners { get; } = owners;

        internal SqliteConnection Connection { get; } = connection;

        internal SqliteTransaction Transaction { get; } = transaction;

        internal GuildRemoteTransportDescriptor? RemoteTransport { get; } = remoteTransport;

        internal int Consumed;

        public void Dispose()
        {
            owner._pending.TryRemove(Proposal.QuestId, out _);

            owner._admissions.Remove(context);
        }
    }

    private static Error Forbidden => new(ErrorCodes.Guild.Forbidden, "The local Guild caller or authority does not match.");

    private static Error Unavailable => new(ErrorCodes.Guild.AuthorityUnavailable, "The durable local Guild installation authority is unavailable.");
}
