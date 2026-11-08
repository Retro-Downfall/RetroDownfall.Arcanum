using System.Collections.Immutable;
using System.Security.Cryptography;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildWorkspaceHostEvidence(string WorkspaceRoot, uint IdentityPolicyVersion,
    long IdentityRevision, byte[] RootIdentity, ImmutableArray<GuildPathScope> OperatorScopes);

/// <summary>Host-issued authentication and current canonical registration; never derived from caller-selected identifiers.</summary>
internal interface IGuildWorkspaceLeaseHostEvidence
{
    Guid InstallationGeneration { get; }

    Guid OperatorPrincipalId { get; }

    bool Authenticates(GuildCallerContext caller);

    Task<Result<GuildWorkspaceHostEvidence>> ResolveAsync(Guid workspaceId, CancellationToken cancellationToken);
}

internal sealed class GuildWorkspaceLeaseSignetPolicy(SignetAuthority authority, IGuildWorkspaceLeaseHostEvidence host)
    : IGuildWorkspaceLeaseHostPolicy
{
    public Task<Result<T>> AuthorizeAsync<T>(GuildCallerContext caller, Guid workspaceId, GuildAuthorityAction action,
        Func<GuildWorkspaceLeaseBinding, CancellationToken, Task<Result<T>>> effect, CancellationToken ct)
    {
        if (caller is null || effect is null || workspaceId == Guid.Empty || action is not
            (GuildAuthorityAction.LeaseAcquire or GuildAuthorityAction.LeaseRenew or GuildAuthorityAction.LeaseRelease or GuildAuthorityAction.WorkspaceRead)
            || !host.Authenticates(caller) || caller.InstallationGeneration != host.InstallationGeneration)
            return Denied<T>();

        return caller.Kind switch
        {
            GuildCallerKind.Run when caller.Execution is not null => RunAsync(caller.Execution, workspaceId, action, caller.PrincipalId, effect, ct),
            GuildCallerKind.Operator => OperatorAsync(caller, workspaceId, action, effect, ct),
            _ => Denied<T>(),
        };
    }

    public Task<Result<T>> RevalidateAsync<T>(GuildVerifiedAuthority proof, Guid workspaceId,
        Func<GuildWorkspaceLeaseBinding, CancellationToken, Task<Result<T>>> effect, CancellationToken ct)
    {
        if (proof is null || effect is null || workspaceId == Guid.Empty) return Denied<T>();

        if (proof.Execution is { } execution)
            return RunAsync(execution, workspaceId, GuildAuthorityAction.WorkspaceMutate, null, effect, ct);

        return proof.OperatorContext is { } caller && host.Authenticates(caller)
            && caller.InstallationGeneration == host.InstallationGeneration
            ? OperatorAsync(caller, workspaceId, GuildAuthorityAction.WorkspaceMutate, effect, ct) : Denied<T>();
    }

    private Task<Result<T>> RunAsync<T>(GuildExecutionHandle execution, Guid workspaceId, GuildAuthorityAction action, Guid? expectedPrincipal,
        Func<GuildWorkspaceLeaseBinding, CancellationToken, Task<Result<T>>> effect, CancellationToken ct) =>
        authority.AdmitWorkspaceAsync(execution, workspaceId, action, async (row, proof, token) =>
        {
            if (row.InstallationGeneration != host.InstallationGeneration
                || expectedPrincipal is { } principal && principal != row.Binding.OriginPrincipalId) return Forbidden;

            Result<GuildWorkspaceHostEvidence> resolved = await host.ResolveAsync(workspaceId, token).ConfigureAwait(false);

            if (resolved.IsFailure) return resolved.Error;

            GuildWorkspaceHostEvidence evidence = resolved.Value;

            if (!Valid(evidence) || row.Owners.WorkspaceIdentityPolicyVersion != evidence.IdentityPolicyVersion
                || row.Owners.WorkspaceIdentityRevision != evidence.IdentityRevision
                || row.Owners.WorkspaceRootIdentity is not { Length: 32 } pin
                || !CryptographicOperations.FixedTimeEquals(pin, evidence.RootIdentity)) return Forbidden;

            ImmutableArray<GuildPathScope> scopes = [.. proof.EffectiveGrant!.Workspaces
                .Where(item => item.WorkspaceId == workspaceId && (action == GuildAuthorityAction.WorkspaceRead || item.Access == GuildWorkspaceAccess.ReadWrite))
                .Select(item => new GuildPathScope(item.RelativeRoot, GuildPathScopeKind.Subtree))];

            GuildWorkspaceLeaseOwner owner = new(GuildWorkspaceLeaseOwnerKind.Quest, row.Binding.OriginPrincipalId,
                row.Binding.QuestId, row.Binding.RootQuestId, row.Owners.SessionId, row.Owners.CampaignId, workspaceId, row.InstallationGeneration);

            return await effect(new(owner, evidence.WorkspaceRoot, proof, scopes), token).ConfigureAwait(false);
        }, ct);

    private async Task<Result<T>> OperatorAsync<T>(GuildCallerContext caller, Guid workspaceId, GuildAuthorityAction action,
        Func<GuildWorkspaceLeaseBinding, CancellationToken, Task<Result<T>>> effect, CancellationToken ct)
    {
        if (workspaceId == Guid.Empty || host.InstallationGeneration == Guid.Empty || host.OperatorPrincipalId == Guid.Empty
            || caller.Kind != GuildCallerKind.Operator || caller.PrincipalId != host.OperatorPrincipalId) return Forbidden;

        Result<GuildWorkspaceHostEvidence> resolved = await host.ResolveAsync(workspaceId, ct).ConfigureAwait(false);

        if (resolved.IsFailure) return resolved.Error;

        if (!Valid(resolved.Value) || resolved.Value.OperatorScopes.IsDefaultOrEmpty) return Forbidden;

        GuildVerifiedAuthority proof = new(caller, action, workspaceId, 1);

        GuildWorkspaceLeaseOwner owner = new(GuildWorkspaceLeaseOwnerKind.Operator, host.OperatorPrincipalId,
            null, null, null, null, workspaceId, host.InstallationGeneration);

        return await effect(new(owner, resolved.Value.WorkspaceRoot, proof, resolved.Value.OperatorScopes), ct).ConfigureAwait(false);
    }

    private static bool Valid(GuildWorkspaceHostEvidence evidence) => evidence is not null
        && !string.IsNullOrWhiteSpace(evidence.WorkspaceRoot) && Path.IsPathFullyQualified(evidence.WorkspaceRoot)
        && evidence.IdentityPolicyVersion > 0 && evidence.IdentityRevision > 0 && evidence.RootIdentity is { Length: 32 };

    private static Task<Result<T>> Denied<T>() => Task.FromResult(Result<T>.Failure(Forbidden));

    private static Error Forbidden => new(ErrorCodes.Guild.Forbidden, "The trusted workspace authority is unavailable or does not match.");
}

/// <summary>Default until authenticated coordinator and installation composition are available.</summary>
internal sealed class UnavailableGuildWorkspaceLeaseHostEvidence : IGuildWorkspaceLeaseHostEvidence
{
    public Guid InstallationGeneration => Guid.Empty;

    public Guid OperatorPrincipalId => Guid.Empty;

    public bool Authenticates(GuildCallerContext caller) => false;

    public Task<Result<GuildWorkspaceHostEvidence>> ResolveAsync(Guid workspaceId, CancellationToken cancellationToken) =>
        Task.FromResult(Result<GuildWorkspaceHostEvidence>.Failure(new(ErrorCodes.Guild.AuthorityUnavailable, "Workspace authority is unavailable.")));
}
