using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildWorkspaceLeaseSignetPolicyTests
{
    [Fact]
    public async Task Live_signet_resolves_pinned_owner_and_rejects_copied_caller_and_revocation()
    {
        await using GuildSignetAuthorityTests.Fixture f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        Guid workspace = Guid.NewGuid();

        await f.SeedOwnersAsync(workspace, Guid.NewGuid(), Guid.NewGuid());

        byte[] pin = new byte[32];

        GuildGrant grant = f.Grant with { Workspaces = [new(workspace, "allowed", GuildWorkspaceAccess.ReadWrite)] };

        f.Policy.HostGrant = grant;

        f.Policy.ScopeEvidence = new([(grant.Workspaces[0], grant.Workspaces[0])], []);

        f.Policy.NextOwners = new(null, null, workspace, WorkspaceIdentityPolicyVersion: 1, WorkspaceIdentityRevision: 1, WorkspaceRootIdentity: pin);

        GuildExecutionHandle execution = await f.RootAsync(grant);

        GuildCallerContext caller = new(GuildCallerKind.Run, f.Operator.PrincipalId!.Value, Guid.NewGuid(), f.Policy.InstallationGeneration, execution);

        HostEvidence host = new(f.Policy.InstallationGeneration, f.Operator.PrincipalId.Value, workspace, caller, pin);

        GuildWorkspaceLeaseSignetPolicy policy = new(f.Authority, host);

        Result<GuildWorkspaceLeaseBinding> admitted = await policy.AuthorizeAsync(caller, workspace, GuildAuthorityAction.LeaseAcquire,
            static (binding, _) => Task.FromResult(Result<GuildWorkspaceLeaseBinding>.Success(binding)), default);

        Assert.True(admitted.IsSuccess, admitted.Error.Message);

        Assert.Equal(execution.QuestId, admitted.Value.Owner.QuestId);

        Assert.Equal(execution.RootQuestId, admitted.Value.Owner.RootQuestId);

        Assert.Equal(f.Operator.PrincipalId, admitted.Value.Owner.PrincipalId);

        GuildCallerContext copied = new(caller.Kind, caller.PrincipalId!.Value, caller.InvocationId, caller.InstallationGeneration, execution);

        Assert.True((await policy.AuthorizeAsync(copied, workspace, GuildAuthorityAction.LeaseAcquire,
            static (_, _) => Task.FromResult(Result<bool>.Success(true)), default)).IsFailure);

        Guid key = Guid.NewGuid();

        Assert.True((await f.Authority.RevokeAsync(f.Operator, new(key, new string('A', 64)), execution.SignetId,
            new(key, 1, GuildCancellationReason.OperatorRequested), default)).IsSuccess);

        Assert.True((await policy.RevalidateAsync(admitted.Value.Authority, workspace,
            static (_, _) => Task.FromResult(Result<bool>.Success(true)), default)).IsFailure);
    }

    [Fact]
    public async Task Expired_signet_and_changed_workspace_pin_refuse_effects()
    {
        await using GuildSignetAuthorityTests.Fixture f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        Guid workspace = Guid.NewGuid();

        await f.SeedOwnersAsync(workspace, Guid.NewGuid(), Guid.NewGuid());

        byte[] pin = new byte[32];

        GuildGrant grant = f.Grant with { Workspaces = [new(workspace, ".", GuildWorkspaceAccess.ReadWrite)], ExpiresAtUtc = f.Time.GetUtcNow().AddMinutes(1) };

        f.Policy.HostGrant = grant;

        f.Policy.ScopeEvidence = new([(grant.Workspaces[0], grant.Workspaces[0])], []);

        f.Policy.NextOwners = new(null, null, workspace, WorkspaceIdentityPolicyVersion: 1, WorkspaceIdentityRevision: 1, WorkspaceRootIdentity: pin);

        GuildExecutionHandle execution = await f.RootAsync(grant);

        GuildCallerContext caller = new(GuildCallerKind.Run, f.Operator.PrincipalId!.Value, Guid.NewGuid(), f.Policy.InstallationGeneration, execution);

        HostEvidence host = new(f.Policy.InstallationGeneration, f.Operator.PrincipalId.Value, workspace, caller, pin);

        GuildWorkspaceLeaseSignetPolicy policy = new(f.Authority, host);

        Assert.True((await policy.AuthorizeAsync(caller, workspace, GuildAuthorityAction.LeaseAcquire,
            static (_, _) => Task.FromResult(Result<bool>.Success(true)), default)).IsSuccess);

        host.Revision = 2;

        Assert.True((await policy.AuthorizeAsync(caller, workspace, GuildAuthorityAction.LeaseAcquire,
            static (_, _) => Task.FromResult(Result<bool>.Success(true)), default)).IsFailure);

        host.Revision = 1;

        f.Time.Advance(TimeSpan.FromMinutes(2));

        Assert.True((await policy.AuthorizeAsync(caller, workspace, GuildAuthorityAction.LeaseAcquire,
            static (_, _) => Task.FromResult(Result<bool>.Success(true)), default)).IsFailure);
    }

    [Fact]
    public async Task Operator_authority_requires_authenticated_context_and_stable_installation_principal()
    {
        await using GuildSignetAuthorityTests.Fixture f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        Guid workspace = Guid.NewGuid();

        GuildCallerContext caller = f.Operator;

        HostEvidence host = new(f.Policy.InstallationGeneration, caller.PrincipalId!.Value, workspace, caller, new byte[32]);

        GuildWorkspaceLeaseSignetPolicy policy = new(f.Authority, host);

        Result<GuildWorkspaceLeaseBinding> admitted = await policy.AuthorizeAsync(caller, workspace, GuildAuthorityAction.LeaseAcquire,
            static (binding, _) => Task.FromResult(Result<GuildWorkspaceLeaseBinding>.Success(binding)), default);

        Assert.True(admitted.IsSuccess, admitted.Error.Message);

        Assert.Equal(caller.PrincipalId, admitted.Value.Owner.PrincipalId);

        GuildCallerContext copied = new(caller.Kind, caller.PrincipalId.Value, caller.InvocationId, caller.InstallationGeneration);

        Assert.True((await policy.AuthorizeAsync(copied, workspace, GuildAuthorityAction.LeaseAcquire,
            static (_, _) => Task.FromResult(Result<bool>.Success(true)), default)).IsFailure);

        host.Principal = Guid.NewGuid();

        Assert.True((await policy.RevalidateAsync(admitted.Value.Authority, workspace,
            static (_, _) => Task.FromResult(Result<bool>.Success(true)), default)).IsFailure);
    }

    [Fact]
    public async Task Default_host_evidence_never_authenticates_an_operator()
    {
        await using GuildSignetAuthorityTests.Fixture f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        GuildWorkspaceLeaseSignetPolicy policy = new(f.Authority, new UnavailableGuildWorkspaceLeaseHostEvidence());

        bool called = false;

        Result<bool> result = await policy.AuthorizeAsync(f.Operator, Guid.NewGuid(), GuildAuthorityAction.LeaseAcquire, (_, _) =>
        {
            called = true;

            return Task.FromResult(Result<bool>.Success(true));
        }, default);

        Assert.True(result.IsFailure);

        Assert.False(called);
    }

    private sealed class HostEvidence(Guid generation, Guid principal, Guid workspace, GuildCallerContext caller, byte[] pin) : IGuildWorkspaceLeaseHostEvidence
    {
        public Guid InstallationGeneration => generation;

        internal Guid Principal { get; set; } = principal;

        public Guid OperatorPrincipalId => Principal;

        internal long Revision { get; set; } = 1;

        public bool Authenticates(GuildCallerContext candidate) => ReferenceEquals(candidate, caller);

        public Task<Result<GuildWorkspaceHostEvidence>> ResolveAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(id == workspace
                ? Result<GuildWorkspaceHostEvidence>.Success(new(Path.GetTempPath(), 1, Revision, pin, [new(".", GuildPathScopeKind.Subtree)]))
                : Result<GuildWorkspaceHostEvidence>.Failure(new(ErrorCodes.Guild.Forbidden, "Unknown workspace.")));
    }
}
