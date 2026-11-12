using RetroDownfall.Arcanum.Core.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildSignetGrantCompositionTests
{
    private static readonly DateTimeOffset Expiry = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Equal_grants_preserve_every_current_authority_axis()
    {
        string[] axes = ["Budget", "Delegation", "ExpiresAtUtc", "Memory", "Tools", "Workspaces"];

        Assert.Equal(axes, typeof(GuildGrant).GetProperties().Select(static property => property.Name).Order().ToArray());

        Guid workspace = Guid.NewGuid();

        GuildWorkspaceGrant path = new(workspace, "dir", GuildWorkspaceAccess.ReadWrite);

        GuildToolGrant tool = new(GuildToolSourceKind.InternalMcp, "arcanum", "read_file");

        GuildMemoryGrant memory = new(GuildMemoryStore.Lexicon, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Global));

        GuildGrant grant = new([path], [tool], [memory], new(new(GuildBudgetMode.Finite, 10m),
            new(GuildBudgetMode.Finite, 100)), [GuildDelegationKind.Local, GuildDelegationKind.Cohort], Expiry);

        GuildGrant result = GuildSignetGrantComposition.Intersect(grant, grant, new([], []));

        Assert.Equal([path], result.Workspaces.ToArray());

        Assert.Equal([tool], result.Tools.ToArray());

        Assert.Equal([memory], result.Memory.ToArray());

        Assert.Equal(grant.Budget, result.Budget);

        Assert.Equal(grant.Delegation.ToArray(), result.Delegation.ToArray());

        Assert.Equal(Expiry, result.ExpiresAtUtc);
    }

    [Fact]
    public void Narrower_ceiling_intersects_every_axis_with_host_scope_evidence()
    {
        Guid workspace = Guid.NewGuid();

        Guid campaign = Guid.NewGuid();

        Guid session = Guid.NewGuid();

        GuildWorkspaceGrant whole = new(workspace, "dir", GuildWorkspaceAccess.ReadWrite);

        GuildWorkspaceGrant child = new(workspace, "dir/child", GuildWorkspaceAccess.Read);

        GuildToolGrant retained = new(GuildToolSourceKind.InternalMcp, "arcanum", "read_file");

        GuildToolGrant extra = new(GuildToolSourceKind.InternalMcp, "arcanum", "edit_file");

        GuildMemoryGrant broad = new(GuildMemoryStore.Transcript, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Campaign, campaign));

        GuildMemoryGrant narrow = broad with { Scope = new(GuildMemoryScopeKind.Session, session) };

        GuildGrant issuer = new([whole], [retained, extra], [broad], new(new(GuildBudgetMode.Finite, 10m),
            new(GuildBudgetMode.Finite, 100)), [GuildDelegationKind.Local, GuildDelegationKind.Remote], Expiry);

        GuildGrant ceiling = new([child], [retained], [narrow], new(new(GuildBudgetMode.Finite, 4m),
            new(GuildBudgetMode.Finite, 25)), [GuildDelegationKind.Local], Expiry.AddDays(-1));

        GuildGrant result = GuildSignetGrantComposition.Intersect(issuer, ceiling, new([(whole, child)], [(campaign, session)]));

        Assert.Equal([child], result.Workspaces.ToArray());

        Assert.Equal([retained], result.Tools.ToArray());

        Assert.Equal([narrow], result.Memory.ToArray());

        Assert.Equal(ceiling.Budget, result.Budget);

        Assert.Equal([GuildDelegationKind.Local], result.Delegation.ToArray());

        Assert.Equal(ceiling.ExpiresAtUtc, result.ExpiresAtUtc);

        GuildGrant reversed = GuildSignetGrantComposition.Intersect(ceiling, issuer, new([(whole, child)], [(campaign, session)]));

        Assert.Equal(result.Workspaces.ToArray(), reversed.Workspaces.ToArray());

        Assert.Equal(result.Memory.ToArray(), reversed.Memory.ToArray());

        Assert.Equal(result.Budget, reversed.Budget);

        Assert.Equal(result.ExpiresAtUtc, reversed.ExpiresAtUtc);

        GuildGrant unproved = GuildSignetGrantComposition.Intersect(issuer, ceiling, new([], []));

        Assert.Empty(unproved.Workspaces);

        Assert.Empty(unproved.Memory);
    }

    [Theory]
    [InlineData(GuildWorkspaceAccess.Read, GuildWorkspaceAccess.Read, GuildWorkspaceAccess.Read)]
    [InlineData(GuildWorkspaceAccess.Read, GuildWorkspaceAccess.ReadWrite, GuildWorkspaceAccess.Read)]
    [InlineData(GuildWorkspaceAccess.ReadWrite, GuildWorkspaceAccess.Read, GuildWorkspaceAccess.Read)]
    [InlineData(GuildWorkspaceAccess.ReadWrite, GuildWorkspaceAccess.ReadWrite, GuildWorkspaceAccess.ReadWrite)]
    public void Workspace_intersection_preserves_only_mutually_granted_access(
        GuildWorkspaceAccess leftAccess, GuildWorkspaceAccess rightAccess, GuildWorkspaceAccess expected)
    {
        Guid workspace = Guid.NewGuid();

        GuildWorkspaceGrant whole = new(workspace, "root", leftAccess);

        GuildWorkspaceGrant child = new(workspace, "root/child", rightAccess);

        GuildGrant left = Empty() with { Workspaces = [whole] };

        GuildGrant right = Empty() with { Workspaces = [child] };

        GuildGrantScopeEvidence evidence = new([(whole, child)], []);

        Assert.Equal([child with { Access = expected }], GuildSignetGrantComposition.Intersect(left, right, evidence).Workspaces.ToArray());

        Assert.Equal([child with { Access = expected }], GuildSignetGrantComposition.Intersect(right, left, evidence).Workspaces.ToArray());
    }

    [Fact]
    public void Unrelated_memory_or_workspace_and_mismatched_memory_operations_never_compose()
    {
        GuildWorkspaceGrant first = new(Guid.NewGuid(), "root", GuildWorkspaceAccess.Read);

        GuildWorkspaceGrant second = new(Guid.NewGuid(), "root", GuildWorkspaceAccess.Read);

        GuildMemoryGrant memory = new(GuildMemoryStore.Lexicon, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Global));

        GuildMemoryGrant otherStore = memory with { Store = GuildMemoryStore.Transcript };

        GuildMemoryGrant otherOperation = memory with { Operation = GuildMemoryOperation.Append };

        GuildGrant left = Empty() with { Workspaces = [first], Memory = [memory] };

        GuildGrant right = Empty() with { Workspaces = [second], Memory = [otherStore, otherOperation] };

        GuildGrant result = GuildSignetGrantComposition.Intersect(left, right, new([], []));

        Assert.Empty(result.Workspaces);

        Assert.Empty(result.Memory);

        Assert.Null(result.ExpiresAtUtc);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Unlimited_budget_and_missing_expiry_never_widen_the_finite_other_side(bool reverse)
    {
        GuildGrant unlimited = Empty();

        GuildGrant limited = Empty() with { Budget = new(new(GuildBudgetMode.Finite, 7m), new(GuildBudgetMode.Finite, 11)), ExpiresAtUtc = Expiry };

        GuildGrant result = reverse ? GuildSignetGrantComposition.Intersect(limited, unlimited, new([], []))
            : GuildSignetGrantComposition.Intersect(unlimited, limited, new([], []));

        Assert.Equal(limited.Budget, result.Budget);

        Assert.Equal(Expiry, result.ExpiresAtUtc);
    }

    private static GuildGrant Empty() => new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [], null);
}
