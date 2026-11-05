using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildGrantPolicyTests
{
    private static readonly GuildGrantScopeEvidence NoScopeEvidence = new([], []);

    private static GuildGrant Empty => new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [], null);

    [Fact]
    public void Every_grant_axis_has_an_explicit_semantic_test_family()
    {
        string[] testedAxes = ["Budget", "Delegation", "ExpiresAtUtc", "Memory", "Tools", "Workspaces"];

        Assert.Equal(testedAxes, typeof(GuildGrant).GetProperties().Select(property => property.Name).Order().ToArray());

        Assert.Equal(testedAxes, typeof(GuildRequestedGrant).GetProperties().Select(property => property.Name).Order().ToArray());
    }

    [Fact]
    public void Omitted_axes_inherit_and_empty_sets_have_no_authority()
    {
        GuildGrant parent = Empty;

        Result<GuildGrant> result = GuildGrantPolicy.Attenuate(parent, new(), NoScopeEvidence);

        Assert.True(result.IsSuccess);

        Assert.Equal(parent, result.Value);

        Assert.Empty(result.Value.Tools);

        Assert.Empty(result.Value.Delegation);
    }

    [Fact]
    public void Explicit_null_nonexpiry_axes_are_invalid()
    {
        GuildRequestedGrant[] requests =
        [
            new(Workspaces: new(true, null)),
            new(Tools: new(true, null)),
            new(Memory: new(true, null)),
            new(Budget: new(true, null)),
            new(Delegation: new(true, null)),
        ];

        Assert.All(requests, request => Assert.True(GuildGrantPolicy.Attenuate(Empty, request, NoScopeEvidence).IsFailure));
    }

    [Fact]
    public void Required_root_axes_reject_uninitialized_or_null_values()
    {
        GuildGrant[] invalid =
        [
            Empty with { Workspaces = default },
            Empty with { Tools = default },
            Empty with { Memory = default },
            Empty with { Budget = null! },
            Empty with { Delegation = default },
        ];

        Assert.All(invalid, grant => Assert.True(GuildGrantPolicy.Validate(grant).IsFailure));
    }

    [Theory]
    [InlineData(GuildBudgetMode.Unlimited, null, GuildBudgetMode.Unlimited, null, true)]
    [InlineData(GuildBudgetMode.Unlimited, null, GuildBudgetMode.Finite, 0L, true)]
    [InlineData(GuildBudgetMode.Finite, 10L, GuildBudgetMode.Finite, 10L, true)]
    [InlineData(GuildBudgetMode.Finite, 10L, GuildBudgetMode.Finite, 9L, true)]
    [InlineData(GuildBudgetMode.Finite, 0L, GuildBudgetMode.Finite, 1L, false)]
    [InlineData(GuildBudgetMode.Finite, 10L, GuildBudgetMode.Finite, 11L, false)]
    [InlineData(GuildBudgetMode.Finite, 10L, GuildBudgetMode.Unlimited, null, false)]
    public void Budget_compares_both_dimensions_without_reinterpreting_zero(GuildBudgetMode parentMode, long? parentValue,
        GuildBudgetMode childMode, long? childValue, bool allowed)
    {
        GuildGrant parent = Empty with { Budget = new(new(parentMode, parentValue), new(parentMode, parentValue)) };

        GuildBudgetGrant child = new(new(childMode, childValue), new(childMode, childValue));

        Result<GuildGrant> result = GuildGrantPolicy.Attenuate(parent, new(Budget: new(true, child)), NoScopeEvidence);

        Assert.Equal(allowed, result.IsSuccess);

        if (!allowed)
        {
            Assert.Equal(ErrorCodes.Guild.GrantWidening, result.Error.Code);
        }
    }

    [Fact]
    public void Budget_checks_usd_and_tokens_independently()
    {
        GuildGrant parent = Empty with { Budget = new(new(GuildBudgetMode.Finite, 1.25m), new(GuildBudgetMode.Finite, 10)) };

        GuildBudgetGrant[] widening =
        [
            new(new(GuildBudgetMode.Finite, 1.26m), new(GuildBudgetMode.Finite, 0)),
            new(new(GuildBudgetMode.Finite, 0), new(GuildBudgetMode.Finite, 11)),
        ];

        Assert.All(widening, budget => Assert.True(GuildGrantPolicy.Attenuate(parent, new(Budget: new(true, budget)), NoScopeEvidence).IsFailure));
    }

    [Fact]
    public void Budget_rejects_missing_negative_and_inconsistent_variants()
    {
        GuildBudgetGrant[] invalid =
        [
            new(null!, new(GuildBudgetMode.Unlimited)),
            new(new(GuildBudgetMode.Unlimited), null!),
            new(new(GuildBudgetMode.Finite), new(GuildBudgetMode.Unlimited)),
            new(new(GuildBudgetMode.Finite, -0.01m), new(GuildBudgetMode.Unlimited)),
            new(new(GuildBudgetMode.Unlimited, 0), new(GuildBudgetMode.Unlimited)),
            new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Finite)),
            new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Finite, -1)),
            new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited, 0)),
            new(new((GuildBudgetMode)999), new(GuildBudgetMode.Unlimited)),
        ];

        Assert.All(invalid, budget => Assert.True(GuildGrantPolicy.Validate(Empty with { Budget = budget }).IsFailure));

        Assert.True(GuildGrantPolicy.Validate(Empty with { Budget = new(new(GuildBudgetMode.Finite, decimal.MaxValue), new(GuildBudgetMode.Finite, long.MaxValue)) }).IsSuccess);
    }

    [Fact]
    public void Tools_use_exact_registration_and_name_identity()
    {
        GuildToolGrant tool = new(GuildToolSourceKind.Adapter, "server-a", "read");

        GuildGrant parent = Empty with { Tools = [tool] };

        Assert.True(GuildGrantPolicy.Attenuate(parent, new(Tools: new(true, [tool])), NoScopeEvidence).IsSuccess);

        Assert.True(GuildGrantPolicy.Attenuate(parent, new(Tools: new(true, [])), NoScopeEvidence).IsSuccess);

        Assert.True(GuildGrantPolicy.Attenuate(parent, new(Tools: new(true, [tool with { SourceRegistrationId = "server-b" }])), NoScopeEvidence).IsFailure);

        Assert.True(GuildGrantPolicy.Attenuate(parent, new(Tools: new(true, [tool with { Name = "Read" }])), NoScopeEvidence).IsFailure);

        Assert.True(GuildGrantPolicy.Attenuate(Empty, new(Tools: new(true, [tool])), NoScopeEvidence).IsFailure);
    }

    [Theory]
    [InlineData(GuildToolSourceKind.BuiltIn, "other", "read")]
    [InlineData(GuildToolSourceKind.InternalMcp, "other", "read")]
    [InlineData(GuildToolSourceKind.Adapter, "", "read")]
    [InlineData(GuildToolSourceKind.Adapter, "server", "*")]
    [InlineData((GuildToolSourceKind)999, "server", "read")]
    public void Tools_reject_wildcards_unknown_sources_and_nonhost_builtin_registrations(GuildToolSourceKind source, string registration, string name)
    {
        Assert.True(GuildGrantPolicy.Validate(Empty with { Tools = [new(source, registration, name)] }).IsFailure);
    }

    [Fact]
    public void Delegation_is_an_explicit_subset_and_cohort_does_not_imply_transport()
    {
        GuildGrant parent = Empty with { Delegation = [GuildDelegationKind.Cohort] };

        Assert.True(GuildGrantPolicy.Attenuate(parent, new(Delegation: new(true, [GuildDelegationKind.Cohort])), NoScopeEvidence).IsSuccess);

        Assert.True(GuildGrantPolicy.Attenuate(parent, new(Delegation: new(true, [])), NoScopeEvidence).IsSuccess);

        Assert.True(GuildGrantPolicy.Attenuate(parent, new(Delegation: new(true, [GuildDelegationKind.Local])), NoScopeEvidence).IsFailure);

        Assert.True(GuildGrantPolicy.Attenuate(parent, new(Delegation: new(true, [GuildDelegationKind.Remote])), NoScopeEvidence).IsFailure);

        Assert.True(GuildGrantPolicy.Validate(Empty with { Delegation = [(GuildDelegationKind)999] }).IsFailure);
    }

    [Fact]
    public void Expiry_distinguishes_inherit_equal_narrow_and_explicit_removal()
    {
        DateTimeOffset expiry = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        GuildGrant parent = Empty with { ExpiresAtUtc = expiry };

        Assert.Equal(expiry, GuildGrantPolicy.Attenuate(parent, new(), NoScopeEvidence).Value.ExpiresAtUtc);

        Assert.True(GuildGrantPolicy.Attenuate(parent, new(ExpiresAtUtc: new(true, expiry)), NoScopeEvidence).IsSuccess);

        Assert.True(GuildGrantPolicy.Attenuate(parent, new(ExpiresAtUtc: new(true, expiry.AddTicks(-1))), NoScopeEvidence).IsSuccess);

        Assert.True(GuildGrantPolicy.Attenuate(parent, new(ExpiresAtUtc: new(true, expiry.AddTicks(1))), NoScopeEvidence).IsFailure);

        Assert.True(GuildGrantPolicy.Attenuate(parent, new(ExpiresAtUtc: new(true, null)), NoScopeEvidence).IsFailure);

        Assert.True(GuildGrantPolicy.Attenuate(Empty, new(ExpiresAtUtc: new(true, null)), NoScopeEvidence).IsSuccess);

        Assert.True(GuildGrantPolicy.Attenuate(Empty, new(ExpiresAtUtc: new(true, expiry)), NoScopeEvidence).IsSuccess);

        Assert.True(GuildGrantPolicy.Validate(parent with { ExpiresAtUtc = expiry.ToOffset(TimeSpan.FromHours(1)) }).IsFailure);
    }

    [Fact]
    public void Workspaces_require_trusted_physical_evidence_even_when_spelling_looks_contained()
    {
        GuildWorkspaceGrant parentRoot = new(Guid.NewGuid(), "a/b", GuildWorkspaceAccess.ReadWrite);

        GuildWorkspaceGrant childRoot = parentRoot with { RelativeRoot = "a/b/c", Access = GuildWorkspaceAccess.Read };

        GuildGrant parent = Empty with { Workspaces = [parentRoot] };

        GuildRequestedGrant child = new(Workspaces: new(true, [childRoot]));

        Assert.True(GuildGrantPolicy.Attenuate(parent, child, NoScopeEvidence).IsFailure);

        GuildGrantScopeEvidence proof = new([(parentRoot, childRoot)], []);

        Assert.True(GuildGrantPolicy.Attenuate(parent, child, proof).IsSuccess);

        Assert.True(GuildGrantPolicy.Attenuate(parent, new(Workspaces: new(true, [])), NoScopeEvidence).IsSuccess);

        Assert.True(GuildGrantPolicy.Attenuate(parent, new(Workspaces: new(true, [childRoot with { RelativeRoot = "a/b2" }])), proof).IsFailure);
    }

    [Fact]
    public void Workspace_physical_alias_proof_is_separate_from_access_attenuation()
    {
        GuildWorkspaceGrant parentRoot = new(Guid.NewGuid(), "src", GuildWorkspaceAccess.Read);

        GuildWorkspaceGrant alias = new(Guid.NewGuid(), "alias", GuildWorkspaceAccess.Read);

        GuildGrant parent = Empty with { Workspaces = [parentRoot] };

        GuildGrantScopeEvidence proof = new([(parentRoot, alias)], []);

        Assert.True(GuildGrantPolicy.Attenuate(parent, new(Workspaces: new(true, [alias])), proof).IsSuccess);

        GuildWorkspaceGrant write = alias with { Access = GuildWorkspaceAccess.ReadWrite };

        Assert.True(GuildGrantPolicy.Attenuate(parent, new(Workspaces: new(true, [write])), new([(parentRoot, write)], [])).IsFailure);

        Assert.True(GuildGrantPolicy.Attenuate(parent, new(Workspaces: new(true, [parentRoot])), new([(parentRoot, parentRoot)], [])).IsSuccess);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("a/../b")]
    [InlineData("a/*")]
    public void Workspaces_reject_noncanonical_or_wildcard_data(string root)
    {
        Assert.True(GuildGrantPolicy.Validate(Empty with { Workspaces = [new(Guid.NewGuid(), root, GuildWorkspaceAccess.Read)] }).IsFailure);
    }

    [Fact]
    public void Memory_campaign_to_session_narrowing_requires_canonical_ownership()
    {
        Guid campaign = Guid.NewGuid();

        Guid session = Guid.NewGuid();

        GuildMemoryGrant original = new(GuildMemoryStore.Transcript, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Campaign, campaign));

        GuildMemoryGrant narrowed = original with { Scope = new(GuildMemoryScopeKind.Session, session) };

        GuildGrant parent = Empty with { Memory = [original] };

        Assert.True(GuildGrantPolicy.Attenuate(parent, new(Memory: new(true, [narrowed])), NoScopeEvidence).IsFailure);

        Assert.True(GuildGrantPolicy.Attenuate(parent, new(Memory: new(true, [narrowed])), new([], [(campaign, session)])).IsSuccess);

        Assert.True(GuildGrantPolicy.Attenuate(parent, new(Memory: new(true, [narrowed])), new([], [(Guid.NewGuid(), session)])).IsFailure);

        Assert.True(GuildGrantPolicy.Attenuate(parent, new(Memory: new(true, [])), NoScopeEvidence).IsSuccess);

        Assert.True(GuildGrantPolicy.Attenuate(parent, new(Memory: new(true, [original])), NoScopeEvidence).IsSuccess);

        Assert.True(GuildGrantPolicy.Attenuate(parent, new(Memory: new(true, [original with { Scope = new(GuildMemoryScopeKind.Global) }])), NoScopeEvidence).IsFailure);
    }

    [Theory]
    [InlineData(GuildMemoryStore.Transcript, GuildMemoryOperation.Append, GuildMemoryScopeKind.Campaign)]
    [InlineData(GuildMemoryStore.Attachment, GuildMemoryOperation.Delete, GuildMemoryScopeKind.Campaign)]
    [InlineData(GuildMemoryStore.Tapestry, GuildMemoryOperation.Append, GuildMemoryScopeKind.Campaign)]
    [InlineData(GuildMemoryStore.Saga, GuildMemoryOperation.Delete, GuildMemoryScopeKind.Campaign)]
    [InlineData(GuildMemoryStore.Saga, GuildMemoryOperation.Read, GuildMemoryScopeKind.Session)]
    [InlineData(GuildMemoryStore.Lexicon, GuildMemoryOperation.Read, GuildMemoryScopeKind.Session)]
    public void Memory_rejects_unimplemented_operation_and_scope_combinations(GuildMemoryStore store, GuildMemoryOperation operation, GuildMemoryScopeKind scope)
    {
        Assert.True(GuildGrantPolicy.Validate(Empty with { Memory = [new(store, operation, new(scope, Guid.NewGuid()))] }).IsFailure);
    }

    [Fact]
    public void Memory_scope_ids_are_closed_and_required()
    {
        GuildMemoryScope[] invalid = [new(GuildMemoryScopeKind.Global, Guid.NewGuid()), new(GuildMemoryScopeKind.Campaign), new(GuildMemoryScopeKind.Session, Guid.Empty), new((GuildMemoryScopeKind)999)];

        Assert.All(invalid, scope => Assert.True(GuildGrantPolicy.Validate(Empty with { Memory = [new(GuildMemoryStore.Transcript, GuildMemoryOperation.Read, scope)] }).IsFailure));
    }
}
