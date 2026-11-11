using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cohort_physical_and_memory_ceilings_compose_into_member_and_descendant(bool descendant)
    {
        await using Fixture f = await Fixture.CreateAsync();

        Guid workspace = await f.AddWorkspaceAsync();

        Directory.CreateDirectory(Path.Combine(f.WorkspaceRoot, "zone", "inner"));

        GuildCallerContext owner = await f.OperatorAsync();

        GuildWorkspaceGrant whole = new(workspace, ".", GuildWorkspaceAccess.ReadWrite);

        GuildMemoryGrant campaign = new(GuildMemoryStore.Transcript, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Campaign, workspace));

        GuildGrant rootGrant = f.Request.Grant with { Workspaces = [whole], Tools = [CohortAuthorityTool], Memory = [campaign],
            Delegation = [GuildDelegationKind.Local, GuildDelegationKind.Cohort] };

        var root = await f.IssueAsync(owner, f.Request with { WorkspaceId = workspace, Grant = rootGrant });

        Assert.True(root.IsSuccess, root.Error.Message);

        GuildCallerContext rootRun = await BindCohortRunAsync(f, owner, root.Value);

        Guid session = Assert.IsType<Guid>(root.Value.Quest.SessionId);

        GuildWorkspaceGrant ceilingPath = new(workspace, "zone", GuildWorkspaceAccess.ReadWrite);

        GuildMemoryGrant sessionMemory = campaign with { Scope = new(GuildMemoryScopeKind.Session, session) };

        GuildRequestedGrant ceiling = new(Workspaces: new(true, [ceilingPath]), Memory: new(true, [sessionMemory]));

        GuildCohortStore cohorts = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority,
            f.Store, new GuildBudgetLedger(), f.Policy, TimeProvider.System);

        var cohort = await cohorts.CreateAsync(rootRun, new(Guid.NewGuid(), root.Value.Quest.Id, "Scoped",
            new(GuildConvergenceKind.All, null), new("text/plain"), ceiling), default);

        Assert.True(cohort.IsSuccess, cohort.Error.Message);

        GuildWorkspaceGrant inner = new(workspace, "zone/inner", GuildWorkspaceAccess.Read);

        Guid key = Guid.NewGuid();

        var member = await f.Store.IssueCohortMemberAsync(rootRun, new(f.Generation, root.Value.Quest.Id, null, key, key),
            cohort.Value.Id, cohort.Value.Revision,
            new(key, root.Value.Quest.Revision, null, "Member", "Contribute", new(Workspaces: new(true, [inner]))), default);

        Assert.True(member.IsSuccess, member.Error.Message);

        GuildCallerContext memberRun = await BindCohortRunAsync(f, owner, member.Value);

        GuildExecutionHandle execution = memberRun.Execution!;

        if (descendant)
        {
            key = Guid.NewGuid();

            var child = await f.Store.IssueAsync(memberRun, new(f.Generation, member.Value.Quest.Id, null, key, key),
                member.Value.Quest.Id, new(key, member.Value.Quest.Revision, null, "Descendant", "Assist", new()), default);

            Assert.True(child.IsSuccess, child.Error.Message);

            execution = (await BindCohortRunAsync(f, owner, child.Value)).Execution!;
        }

        var admitted = await f.Authority.AdmitToolAsync(execution, CohortAuthorityTool, true,
            (proof, _) => Task.FromResult(Result<GuildGrant>.Success(proof.EffectiveGrant!)), default);

        Assert.True(admitted.IsSuccess, admitted.Error.Message);

        Assert.Equal(inner, Assert.Single(admitted.Value.Workspaces));

        Assert.Equal(sessionMemory, Assert.Single(admitted.Value.Memory));

        Assert.Contains(CohortAuthorityTool, admitted.Value.Tools);
    }

    [Fact]
    public async Task Cohort_ceiling_rejects_a_physical_symlink_escape()
    {
        await using Fixture f = await Fixture.CreateAsync();

        Guid workspace = await f.AddWorkspaceAsync();

        Directory.CreateSymbolicLink(Path.Combine(f.WorkspaceRoot, "outside"), Path.GetTempPath());

        GuildCallerContext owner = await f.OperatorAsync();

        var root = await f.IssueAsync(owner, f.Request with { WorkspaceId = workspace, Grant = f.Request.Grant with
        {
            Workspaces = [new(workspace, ".", GuildWorkspaceAccess.ReadWrite)],
            Delegation = [GuildDelegationKind.Local, GuildDelegationKind.Cohort],
        } });

        Assert.True(root.IsSuccess, root.Error.Message);

        GuildCallerContext run = await BindCohortRunAsync(f, owner, root.Value);

        GuildCohortStore cohorts = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority,
            f.Store, new GuildBudgetLedger(), f.Policy, TimeProvider.System);

        GuildRequestedGrant escape = new(Workspaces: new(true, [new(workspace, "outside", GuildWorkspaceAccess.Read)]));

        var refused = await cohorts.CreateAsync(run, new(Guid.NewGuid(), root.Value.Quest.Id, "Escape",
            new(GuildConvergenceKind.All, null), new("text/plain"), escape), default);

        Assert.True(refused.IsFailure);

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_cohorts"));
    }
}
