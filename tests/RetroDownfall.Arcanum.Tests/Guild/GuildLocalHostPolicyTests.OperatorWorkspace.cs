using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Native_operator_workspace_authority_uses_current_registration_without_minting_canonical_Run_pins()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        Guid workspace = await fixture.AddWorkspaceAsync();

        fixture.Settings.CurrentValue.Security.CampaignRoots = [fixture.WorkspaceRoot];

        await fixture.ExecuteAsync("DELETE FROM campaign_path_identities");

        GuildCallerContext caller = await fixture.OperatorAsync();

        GuildWorkspaceLeaseSignetPolicy leasePolicy = new(fixture.Authority, fixture.Policy);

        Result<GuildWorkspaceLeaseBinding> admitted = await leasePolicy.AuthorizeAsync(caller, workspace, GuildAuthorityAction.LeaseAcquire,
            static (binding, _) => Task.FromResult(Result<GuildWorkspaceLeaseBinding>.Success(binding)), default);

        Assert.True(admitted.IsSuccess, admitted.Error.Message);

        Assert.Equal(GuildWorkspaceLeaseOwnerKind.Operator, admitted.Value.Owner.Kind);

        Assert.Equal(caller.PrincipalId, admitted.Value.Owner.PrincipalId);

        Assert.Equal(fixture.WorkspaceRoot, admitted.Value.WorkspaceRoot);

        Assert.Equal(0L, await fixture.ScalarAsync("SELECT count(*) FROM campaign_path_identities"));

        Assert.True((await fixture.Policy.ResolveAsync(workspace, default)).IsFailure);

        GuildIssueRootRequest request = fixture.Request with
        {
            WorkspaceId = workspace,

            CampaignId = workspace,

            Grant = fixture.Request.Grant with { Workspaces = [new(workspace, ".", GuildWorkspaceAccess.ReadWrite)] },
        };

        Assert.True((await fixture.IssueAsync(caller, request)).IsFailure);

        GuildCallerContext copied = new(caller.Kind, caller.PrincipalId!.Value, caller.InvocationId, caller.InstallationGeneration);

        bool effectCalled = false;

        Result<bool> forged = await leasePolicy.AuthorizeAsync(copied, workspace, GuildAuthorityAction.LeaseAcquire,
            (_, _) => { effectCalled = true; return Task.FromResult(Result<bool>.Success(true)); }, default);

        Assert.True(forged.IsFailure);

        Assert.False(effectCalled);
    }

    [Fact]
    public async Task Native_operator_workspace_authority_rechecks_current_path_policy_and_owner_liveness()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        Guid workspace = await fixture.AddWorkspaceAsync();

        fixture.Settings.CurrentValue.Security.CampaignRoots = [fixture.WorkspaceRoot];

        await fixture.ExecuteAsync("DELETE FROM campaign_path_identities");

        GuildCallerContext caller = await fixture.OperatorAsync();

        GuildWorkspaceLeaseSignetPolicy leasePolicy = new(fixture.Authority, fixture.Policy);

        Result<GuildWorkspaceLeaseBinding> original = await leasePolicy.AuthorizeAsync(caller, workspace, GuildAuthorityAction.LeaseAcquire,
            static (binding, _) => Task.FromResult(Result<GuildWorkspaceLeaseBinding>.Success(binding)), default);

        Assert.True(original.IsSuccess, original.Error.Message);

        fixture.Settings.CurrentValue.Security.CampaignRoots = [Path.Combine(fixture.WorkspaceRoot, "different-root")];

        bool effectCalled = false;

        Result<bool> changed = await leasePolicy.RevalidateAsync(original.Value.Authority, workspace,
            (_, _) => { effectCalled = true; return Task.FromResult(Result<bool>.Success(true)); }, default);

        Assert.True(changed.IsFailure);

        Assert.False(effectCalled);

        fixture.Settings.CurrentValue.Security.CampaignRoots = [fixture.WorkspaceRoot];

        await fixture.ExecuteAsync($"DELETE FROM Campaigns WHERE Id='{workspace.ToString("D").ToUpperInvariant()}'");

        Result<bool> deleted = await leasePolicy.RevalidateAsync(original.Value.Authority, workspace,
            (_, _) => { effectCalled = true; return Task.FromResult(Result<bool>.Success(true)); }, default);

        Assert.True(deleted.IsFailure);

        Assert.False(effectCalled);
    }
    [Fact]
    public async Task Native_operator_leases_conflict_across_registered_aliases_without_canonical_Run_records()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        Guid workspace = await fixture.AddWorkspaceAsync();

        fixture.Settings.CurrentValue.Security.CampaignRoots = [fixture.WorkspaceRoot];

        await fixture.ExecuteAsync("DELETE FROM campaign_path_identities");

        Guid alias = Guid.NewGuid();

        await fixture.AddCampaignAsync(alias);

        string aliasPath = Path.Combine(fixture.WorkspaceRoot, ".").Replace("'", "''");

        await fixture.ExecuteAsync($"UPDATE Campaigns SET Path='{aliasPath}' WHERE Id='{alias.ToString("D").ToUpperInvariant()}'");

        string lockPath = Path.Combine(Path.GetTempPath(), "guild-operator-evidence-" + Guid.NewGuid().ToString("N") + ".lock");

        try
        {
            Result<GuildWorkspaceLeaseHostLock> host = GuildWorkspaceLeaseHostLock.TryAcquire(lockPath);

            Assert.True(host.IsSuccess, host.Error.Message);

            await using GuildWorkspaceLeaseBroker broker = new(new(fixture.Connections), new WorkspaceLeaseIdentityResolver(),
                new GuildWorkspaceLeaseSignetPolicy(fixture.Authority, fixture.Policy), TimeProvider.System, host.Value);

            GuildCallerContext caller = await fixture.OperatorAsync();

            AcquireWorkspaceLeaseRequest request = new(Guid.NewGuid(), workspace, [new("target.txt", GuildPathScopeKind.File)]);

            Result<WorkspaceLeaseDto> first = await broker.AcquireAsync(caller, new(request.IdempotencyKey, new string('A', 64)), request, default);

            Assert.True(first.IsSuccess, first.Error.Message);

            AcquireWorkspaceLeaseRequest second = new(Guid.NewGuid(), alias, [new("target.txt", GuildPathScopeKind.File)]);

            Result<WorkspaceLeaseDto> conflict = await broker.AcquireAsync(caller, new(second.IdempotencyKey, new string('B', 64)), second, default);

            Assert.True(conflict.IsFailure);

            Assert.Equal(ErrorCodes.Guild.LeaseConflict, conflict.Error.Code);

            Assert.Equal(0L, await fixture.ScalarAsync("SELECT count(*) FROM campaign_path_identities"));
        }
        finally
        {
            File.Delete(lockPath);
        }
    }

    [Fact]
    public async Task Native_operator_lease_renewal_refuses_a_replacement_physical_root()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        Guid workspace = await fixture.AddWorkspaceAsync();

        fixture.Settings.CurrentValue.Security.CampaignRoots = [fixture.WorkspaceRoot];

        await fixture.ExecuteAsync("DELETE FROM campaign_path_identities");

        string lockPath = Path.Combine(Path.GetTempPath(), "guild-operator-evidence-" + Guid.NewGuid().ToString("N") + ".lock");

        try
        {
            Result<GuildWorkspaceLeaseHostLock> host = GuildWorkspaceLeaseHostLock.TryAcquire(lockPath);

            Assert.True(host.IsSuccess, host.Error.Message);

            await using GuildWorkspaceLeaseBroker broker = new(new(fixture.Connections), new WorkspaceLeaseIdentityResolver(),
                new GuildWorkspaceLeaseSignetPolicy(fixture.Authority, fixture.Policy), TimeProvider.System, host.Value);

            GuildCallerContext caller = await fixture.OperatorAsync();

            AcquireWorkspaceLeaseRequest request = new(Guid.NewGuid(), workspace, [new("target.txt", GuildPathScopeKind.File)]);

            Result<WorkspaceLeaseDto> acquired = await broker.AcquireAsync(caller, new(request.IdempotencyKey, new string('A', 64)), request, default);

            Assert.True(acquired.IsSuccess, acquired.Error.Message);

            Directory.Move(fixture.WorkspaceRoot, fixture.WorkspaceRoot + "-moved");

            Directory.CreateDirectory(fixture.WorkspaceRoot);

            RenewWorkspaceLeaseRequest renewal = new(Guid.NewGuid(), acquired.Value.Revision, acquired.Value.Epoch);

            Result<WorkspaceLeaseDto> replaced = await broker.RenewAsync(caller, new(renewal.IdempotencyKey, new string('B', 64)),
                acquired.Value.Id, renewal, default);

            Assert.True(replaced.IsFailure);

            Assert.Equal(0L, await fixture.ScalarAsync("SELECT count(*) FROM campaign_path_identities"));
        }
        finally
        {
            File.Delete(lockPath);
        }
    }

}
