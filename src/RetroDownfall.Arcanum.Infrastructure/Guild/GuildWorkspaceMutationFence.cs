using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Host-only invocation adapter. Missing context never becomes operator authority.</summary>
internal sealed class GuildWorkspaceMutationFence(GuildWorkspaceLeaseBroker broker) : IWorkspaceMutationFence
{
    // Explicit durable leases and direct broker permits outlive composite writer leases.
    // No artifact-owner/quiescence capability can remove this hold in the dormant slice.
    public bool RequiresRetirementHold => true;

    private static readonly AsyncLocal<Invocation?> Current = new();

    internal IDisposable EnterInvocation(GuildVerifiedAuthority authority, Guid workspaceId,
        GuildChildToolAdmission? toolAdmission = null, WorkspaceLeaseIdentitySet? initialTargets = null,
        string? callDigest = null, GuildOperatorWorkspaceWrite.Call? operatorCall = null)
    {
        ArgumentNullException.ThrowIfNull(authority);

        Invocation? previous = Current.Value;

        Invocation invocation = new(this, authority, workspaceId, toolAdmission, initialTargets, callDigest, operatorCall);

        Current.Value = invocation;

        return new InvocationScope(invocation, previous);
    }

    public async ValueTask<Result<IWorkspaceMutationLease>> AdmitAsync(string workspaceRoot, ImmutableArray<GuildPathScope> scopes,
        CancellationToken cancellationToken)
    {
        Invocation? invocation = Current.Value;

        if (invocation is null || !invocation.Active || !ReferenceEquals(invocation.Owner, this)) return Forbidden;

        if (invocation.ToolAdmission is { } call)
        {
            Result live = await call.Registry.Parent.Store.RevalidateWorkspaceMutationToolAsync(call, cancellationToken).ConfigureAwait(false);

            if (live.IsFailure) return live.Error;

            if (!invocation.Active || !ReferenceEquals(Current.Value, invocation)) return Forbidden;
        }

        if (invocation.OperatorCall is { } operatorCall)
        {
            Result live = await operatorCall.ValidateAsync(cancellationToken).ConfigureAwait(false);

            if (live.IsFailure) return live.Error;

            if (!invocation.Active || !operatorCall.IsActive || !ReferenceEquals(Current.Value, invocation)) return Forbidden;
        }

        GuildMutationPlan plan = new(invocation.WorkspaceId, scopes, string.Empty);

        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workspaceRoot + "\0" + invocation.CallDigest + "\0"
            + JsonSerializer.Serialize(plan, GuildJsonContext.Default.GuildMutationPlan))));

        Result<GuildMutationPermit> admitted = await broker.AdmitWriterMutationAsync(invocation.Authority,
            plan with { EffectPlanDigest = digest }, workspaceRoot, cancellationToken, invocation.InitialTargets,
            invocation.OperatorCall?.PhysicalScope).ConfigureAwait(false);

        return admitted.IsSuccess ? Result<IWorkspaceMutationLease>.Success(new Lease(this, invocation, admitted.Value)) : admitted.Error;
    }

    private sealed class Invocation(GuildWorkspaceMutationFence owner, GuildVerifiedAuthority authority, Guid workspaceId,
        GuildChildToolAdmission? toolAdmission, WorkspaceLeaseIdentitySet? initialTargets, string? callDigest,
        GuildOperatorWorkspaceWrite.Call? operatorCall)
    {
        internal GuildWorkspaceMutationFence Owner { get; } = owner;

        internal GuildVerifiedAuthority Authority { get; } = authority;

        internal Guid WorkspaceId { get; } = workspaceId;

        internal GuildChildToolAdmission? ToolAdmission { get; } = toolAdmission;

        internal WorkspaceLeaseIdentitySet? InitialTargets { get; } = initialTargets;

        internal string? CallDigest { get; } = callDigest;

        internal GuildOperatorWorkspaceWrite.Call? OperatorCall { get; } = operatorCall;

        internal volatile bool Active = true;
    }

    private sealed class InvocationScope(Invocation invocation, Invocation? previous) : IDisposable
    {
        public void Dispose()
        {
            invocation.Active = false;

            if (ReferenceEquals(Current.Value, invocation)) Current.Value = previous;
        }
    }

    private sealed class Lease(GuildWorkspaceMutationFence owner, Invocation invocation, GuildMutationPermit permit) : IWorkspaceMutationLease
    {
        private int _disposed;

        public async ValueTask<Result> ValidateAsync(bool rollback, CancellationToken cancellationToken)
        {
            if (!CanUse(rollback)) return Forbidden;

            if (!rollback && invocation.ToolAdmission is { } call)
            {
                Result live = await call.Registry.Parent.Store.RevalidateWorkspaceMutationToolAsync(call, cancellationToken).ConfigureAwait(false);

                if (live.IsFailure) return live;

                if (!CanUse(rollback)) return Forbidden;
            }

            if (!rollback && invocation.OperatorCall is { } operatorCall)
            {
                Result live = await operatorCall.ValidateAsync(cancellationToken).ConfigureAwait(false);

                if (live.IsFailure) return live;

                if (!CanUse(rollback)) return Forbidden;
            }

            return rollback
                ? await owner.Broker.ValidateRollbackAsync(permit, cancellationToken).ConfigureAwait(false)
                : await owner.Broker.ValidateCommitAsync(permit, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<Result> RetainRecoveryAsync(string reasonCode, CancellationToken cancellationToken) =>
            await owner.Broker.RetainRecoveryAsync(permit, reasonCode, cancellationToken).ConfigureAwait(false);

        public ValueTask<Result> ValidateDirectoryCreationAsync(WorkspaceLeaseDirectoryCreation preparation, bool rollback, CancellationToken cancellationToken) =>
            CanUse(rollback) ? new(owner.Broker.ValidateDirectoryCreationAsync(permit, preparation, rollback, cancellationToken)) : ValueTask.FromResult(Result.Failure(Forbidden));

        public ValueTask<Result> AdoptCreatedDirectoryAsync(WorkspaceLeaseCreatedDirectory proof, bool rollback, CancellationToken cancellationToken) =>
            CanUse(true) ? new(owner.Broker.AdoptCreatedDirectoryAsync(permit, proof, rollback, cancellationToken)) : ValueTask.FromResult(Result.Failure(Forbidden));

        public ValueTask<Result> ValidateDirectoryRemovalAsync(WorkspaceLeaseDirectoryRemoval preparation, bool rollback, CancellationToken cancellationToken) =>
            CanUse(rollback) ? new(owner.Broker.ValidateDirectoryRemovalAsync(permit, preparation, rollback, cancellationToken)) : ValueTask.FromResult(Result.Failure(Forbidden));

        public ValueTask<Result> AdoptRemovedDirectoryAsync(WorkspaceLeaseRemovedDirectory proof, bool rollback, CancellationToken cancellationToken) =>
            CanUse(true) ? new(owner.Broker.AdoptRemovedDirectoryAsync(permit, proof, rollback, cancellationToken)) : ValueTask.FromResult(Result.Failure(Forbidden));

        private bool CanUse(bool rollback) => Volatile.Read(ref _disposed) == 0
            && (rollback || invocation.Active && invocation.OperatorCall?.IsActive != false && ReferenceEquals(Current.Value, invocation));

        public IDisposable Enter()
        {
            if (Volatile.Read(ref _disposed) != 0) throw new WorkspaceMutationFenceException(Forbidden);

            return EmptyScope.Instance;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            Result completed = await owner.Broker.CompleteMutationAsync(permit, CancellationToken.None).ConfigureAwait(false);

            if (completed.IsFailure) throw new WorkspaceMutationFenceException(completed.Error);
        }
    }

    private sealed class EmptyScope : IDisposable
    {
        internal static EmptyScope Instance { get; } = new();

        public void Dispose() { }
    }

    private GuildWorkspaceLeaseBroker Broker => broker;

    private static Error Forbidden => new(ErrorCodes.Guild.Forbidden, "The writer has no active authenticated workspace invocation.");
}
