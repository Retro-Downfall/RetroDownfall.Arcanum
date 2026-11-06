using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildQuestIntentTests
{
    [Fact]
    public void Goal_normalization_preserves_content_with_canonical_unicode_and_line_endings()
    {
        Assert.Equal(" café\nnext\nlast ", GuildQuestIntent.NormalizeGoal(" cafe\u0301\r\nnext\rlast "));
    }

    [Fact]
    public void Root_intent_produces_a_sha256_digest_and_excludes_the_idempotency_key()
    {
        string digest = Success(Compute(Root()));

        Assert.Matches("^[0-9A-F]{64}$", digest);

        Assert.Equal(digest, Success(Compute(Root() with { IdempotencyKey = Guid.NewGuid() })));
    }

    [Fact]
    public void Canonically_equal_goal_text_has_the_same_intent()
    {
        Assert.Equal(Success(Compute(Root() with { Goal = "café\nsecond\nthird" })),
            Success(Compute(Root() with { Goal = "cafe\u0301\r\nsecond\rthird" })));
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("name")]
    [InlineData("goal")]
    [InlineData("session")]
    [InlineData("campaign")]
    [InlineData("workspace")]
    [InlineData("predecessor")]
    public void Root_intent_includes_each_supplied_owner_and_content_choice(string field)
    {
        GuildIssueRootRequest request = Root();

        GuildIssueRootRequest changed = field switch
        {
            "agent" => request with { AgentId = OtherId },
            "name" => request with { Name = "Other" },
            "goal" => request with { Goal = "another goal" },
            "session" => request with { SessionId = OtherId },
            "campaign" => request with { CampaignId = OtherId },
            "workspace" => request with { WorkspaceId = OtherId },
            "predecessor" => request with { PredecessorQuestId = OtherId },
            _ => throw new InvalidOperationException()
        };

        Assert.NotEqual(Success(Compute(request)), Success(Compute(changed)));
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("revision")]
    [InlineData("agent")]
    [InlineData("name")]
    [InlineData("goal")]
    public void Child_intent_includes_parent_revision_destination_name_and_goal(string field)
    {
        GuildIssueChildRequest request = Child();

        GuildIssueChildRequest changed = field switch
        {
            "revision" => request with { ExpectedRevision = 2 },
            "agent" => request with { AgentId = OtherId },
            "name" => request with { Name = "Other" },
            "goal" => request with { Goal = "another goal" },
            _ => request
        };

        Assert.NotEqual(Success(Compute(Id, request)), Success(Compute(field == "parent" ? OtherId : Id, changed)));
    }

    [Fact]
    public void Child_intent_excludes_the_idempotency_key_and_normalizes_its_goal()
    {
        Assert.Equal(Success(Compute(Id, Child() with { Goal = "café\nnext" })),
            Success(Compute(Id, Child() with { IdempotencyKey = OtherId, Goal = "cafe\u0301\r\nnext" })));
    }

    [Fact]
    public void Every_grant_set_is_order_and_duplicate_independent()
    {
        GuildGrant first = Sets();

        GuildGrant second = first with
        {
            Workspaces = [first.Workspaces[1], first.Workspaces[0], first.Workspaces[0]],
            Tools = [first.Tools[1], first.Tools[0], first.Tools[0]],
            Memory = [first.Memory[1], first.Memory[0], first.Memory[0]],
            Delegation = [GuildDelegationKind.Remote, GuildDelegationKind.Local, GuildDelegationKind.Local]
        };

        Assert.Equal(Success(Compute(Root() with { Grant = first })), Success(Compute(Root() with { Grant = second })));

        Assert.Equal(Success(Compute(Id, Child() with { RequestedGrant = Requested(first) })),
            Success(Compute(Id, Child() with { RequestedGrant = Requested(second) })));
    }

    [Fact]
    public void Root_aliases_decimal_scales_and_expiry_offsets_have_one_canonical_meaning()
    {
        GuildGrant first = Sets() with { ExpiresAtUtc = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero) };

        GuildGrant second = first with
        {
            Workspaces = [first.Workspaces[0] with { RelativeRoot = "." }, first.Workspaces[1]],
            Budget = new(new(GuildBudgetMode.Finite, 1.2500m), new(GuildBudgetMode.Finite, 17)),
            ExpiresAtUtc = new DateTimeOffset(2026, 11, 6, 7, 0, 0, TimeSpan.FromHours(-5))
        };

        Assert.Equal(Success(Compute(Root() with { Grant = first })), Success(Compute(Root() with { Grant = second })));

        Assert.Equal(Success(Compute(Id, Child() with { RequestedGrant = Requested(first) })),
            Success(Compute(Id, Child() with { RequestedGrant = Requested(second) })));
    }

    [Theory]
    [InlineData("workspace-id")]
    [InlineData("workspace-root")]
    [InlineData("workspace-access")]
    [InlineData("tool-source")]
    [InlineData("tool-registration")]
    [InlineData("tool-name")]
    [InlineData("memory-store")]
    [InlineData("memory-operation")]
    [InlineData("memory-scope")]
    [InlineData("memory-id")]
    [InlineData("usd-mode")]
    [InlineData("usd-value")]
    [InlineData("tokens-mode")]
    [InlineData("tokens-value")]
    [InlineData("delegation")]
    [InlineData("expiry")]
    public void Intent_includes_each_grant_dimension(string field)
    {
        GuildGrant grant = Sets();

        GuildGrant changed = field switch
        {
            "workspace-id" => grant with { Workspaces = [grant.Workspaces[0] with { WorkspaceId = OtherId }, grant.Workspaces[1]] },
            "workspace-root" => grant with { Workspaces = [grant.Workspaces[0] with { RelativeRoot = "docs" }, grant.Workspaces[1]] },
            "workspace-access" => grant with { Workspaces = [grant.Workspaces[0] with { Access = GuildWorkspaceAccess.ReadWrite }, grant.Workspaces[1]] },
            "tool-source" => grant with { Tools = [grant.Tools[0] with { SourceKind = GuildToolSourceKind.Adapter }, grant.Tools[1]] },
            "tool-registration" => grant with { Tools = [grant.Tools[0] with { SourceRegistrationId = "other" }, grant.Tools[1]] },
            "tool-name" => grant with { Tools = [grant.Tools[0] with { Name = "other" }, grant.Tools[1]] },
            "memory-store" => grant with { Memory = [grant.Memory[0] with { Store = GuildMemoryStore.Lexicon }, grant.Memory[1]] },
            "memory-operation" => grant with { Memory = [grant.Memory[0] with { Operation = GuildMemoryOperation.Append }, grant.Memory[1]] },
            "memory-scope" => grant with { Memory = [grant.Memory[0] with { Scope = new(GuildMemoryScopeKind.Session, Id) }, grant.Memory[1]] },
            "memory-id" => grant with { Memory = [grant.Memory[0] with { Scope = new(GuildMemoryScopeKind.Campaign, OtherId) }, grant.Memory[1]] },
            "usd-mode" => grant with { Budget = grant.Budget with { Usd = new(GuildBudgetMode.Unlimited) } },
            "usd-value" => grant with { Budget = grant.Budget with { Usd = new(GuildBudgetMode.Finite, 1.26m) } },
            "tokens-mode" => grant with { Budget = grant.Budget with { Tokens = new(GuildBudgetMode.Unlimited) } },
            "tokens-value" => grant with { Budget = grant.Budget with { Tokens = new(GuildBudgetMode.Finite, 18) } },
            "delegation" => grant with { Delegation = [GuildDelegationKind.Cohort] },
            "expiry" => grant with { ExpiresAtUtc = DateTimeOffset.UnixEpoch },
            _ => throw new InvalidOperationException()
        };

        Assert.NotEqual(Success(Compute(Root() with { Grant = grant })), Success(Compute(Root() with { Grant = changed })));
    }

    [Theory]
    [InlineData("workspaces")]
    [InlineData("tools")]
    [InlineData("memory")]
    [InlineData("budget")]
    [InlineData("delegation")]
    [InlineData("expiry")]
    public void Child_grant_omission_explicit_null_and_values_remain_distinct(string axis)
    {
        GuildRequestedGrant empty = new();

        GuildRequestedGrant explicitNull = axis switch
        {
            "workspaces" => empty with { Workspaces = new(true, null) },
            "tools" => empty with { Tools = new(true, null) },
            "memory" => empty with { Memory = new(true, null) },
            "budget" => empty with { Budget = new(true, null) },
            "delegation" => empty with { Delegation = new(true, null) },
            "expiry" => empty with { ExpiresAtUtc = new(true, null) },
            _ => throw new InvalidOperationException()
        };

        GuildRequestedGrant specified = axis switch
        {
            "workspaces" => empty with { Workspaces = new(true, []) },
            "tools" => empty with { Tools = new(true, []) },
            "memory" => empty with { Memory = new(true, []) },
            "budget" => empty with { Budget = new(true, Sets().Budget) },
            "delegation" => empty with { Delegation = new(true, []) },
            "expiry" => empty with { ExpiresAtUtc = new(true, DateTimeOffset.UnixEpoch) },
            _ => throw new InvalidOperationException()
        };

        string omitted = Success(Compute(Id, Child() with { RequestedGrant = empty }));

        string nulled = Success(Compute(Id, Child() with { RequestedGrant = explicitNull }));

        string supplied = Success(Compute(Id, Child() with { RequestedGrant = specified }));

        Assert.NotEqual(omitted, nulled);

        Assert.NotEqual(omitted, supplied);

        Assert.NotEqual(nulled, supplied);
    }

    [Fact]
    public void Null_requested_grant_differs_from_empty_requested_grant()
    {
        Assert.NotEqual(Success(Compute(Id, Child() with { RequestedGrant = null })),
            Success(Compute(Id, Child() with { RequestedGrant = new() })));
    }

    [Theory]
    [InlineData("goal")]
    [InlineData("name")]
    [InlineData("grant")]
    public void Canonical_envelope_size_is_bounded_across_all_text_fields(string field)
    {
        GuildIssueRootRequest request = field switch
        {
            "goal" => Root() with { Goal = new string('λ', 40_000) },
            "name" => Root() with { Name = new string('x', 65_536) },
            "grant" => Root() with { Grant = Sets() with { Tools = [new(GuildToolSourceKind.Adapter, "adapter", new string('x', 65_536))] } },
            _ => throw new InvalidOperationException()
        };

        Assert.Equal("Guild.PayloadTooLarge", Compute(request).Error.Code);
    }

    [Fact]
    public void The_canonical_envelope_accepts_large_content_within_the_existing_byte_boundary()
    {
        Assert.True(Compute(Root() with { Goal = new string('x', 60_000) }).IsSuccess);
    }

    [Theory]
    [InlineData("null-goal")]
    [InlineData("malformed-goal")]
    [InlineData("malformed-name")]
    [InlineData("default-array")]
    [InlineData("unknown-enum")]
    public void Malformed_caller_data_returns_a_domain_error(string field)
    {
        GuildIssueRootRequest request = field switch
        {
            "null-goal" => Root() with { Goal = null! },
            "malformed-goal" => Root() with { Goal = "\ud800" },
            "malformed-name" => Root() with { Name = "\ud800" },
            "default-array" => Root() with { Grant = Sets() with { Tools = default } },
            "unknown-enum" => Root() with { Grant = Sets() with { Delegation = [(GuildDelegationKind)999] } },
            _ => throw new InvalidOperationException()
        };

        Assert.Equal("Guild.InvalidRequest", Compute(request).Error.Code);
    }

    [Fact]
    public void Version_one_digest_has_a_stable_sha256_vector()
    {
        Assert.Equal("CD0C334F3C3669A8F30E81FFFD7AC127901FD253AE3ACECA1CB6ABACE21CF0F7", Success(Compute(Bare())));
    }

    [Fact]
    public void Exactly_65536_canonical_utf8_bytes_are_accepted_and_one_more_is_refused()
    {
        // The reviewed version-one empty-grant envelope uses 302 bytes outside the goal body.
        Assert.True(Compute(Bare() with { Goal = new string('x', 65_234) }).IsSuccess);

        Assert.Equal("Guild.PayloadTooLarge", Compute(Bare() with { Goal = new string('x', 65_235) }).Error.Code);

        Assert.True(Compute(Bare() with { Goal = new string('λ', 32_617) }).IsSuccess);

        Assert.Equal("Guild.PayloadTooLarge", Compute(Bare() with { Goal = new string('λ', 32_617) + "x" }).Error.Code);
    }

    [Fact]
    public void Unspecified_child_axes_ignore_their_in_memory_values()
    {
        GuildRequestedGrant omitted = new();

        GuildRequestedGrant incidentalValues = new(new(false, Sets().Workspaces), new(false, Sets().Tools),
            new(false, Sets().Memory), new(false, Sets().Budget), new(false, Sets().Delegation), new(false, DateTimeOffset.UnixEpoch));

        Assert.Equal(Success(Compute(Id, Child() with { RequestedGrant = omitted })),
            Success(Compute(Id, Child() with { RequestedGrant = incidentalValues })));
    }

    [Fact]
    public void Duplicate_grant_members_do_not_create_an_artificial_input_count_ceiling()
    {
        GuildGrant grant = Sets();

        GuildGrant duplicates = grant with { Tools = Enumerable.Repeat(grant.Tools[0], 10_000).ToImmutableArray() };

        Assert.Equal(Success(Compute(Root() with { Grant = grant with { Tools = [grant.Tools[0]] } })),
            Success(Compute(Root() with { Grant = duplicates })));
    }

    private static GuildIssueRootRequest Bare() => new(Id, null, null, "goal",
        new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [], null));

    private static Result<string> Compute(GuildIssueRootRequest request) => GuildQuestIntent.Compute(request);

    private static Result<string> Compute(Guid parent, GuildIssueChildRequest request) => GuildQuestIntent.Compute(parent, request);

    private static string Success(Result<string> result)
    {
        Assert.True(result.IsSuccess, result.Error.Code);

        return result.Value;
    }

    private static readonly Guid Id = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly Guid OtherId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static GuildIssueRootRequest Root() => new(Id, null, "Smith", "goal", Sets());

    private static GuildIssueChildRequest Child() => new(Id, 1, null, "Smith", "goal", null);

    private static GuildGrant Sets() => new(
        [new(Id, "", GuildWorkspaceAccess.Read), new(OtherId, "src", GuildWorkspaceAccess.ReadWrite)],
        [new(GuildToolSourceKind.BuiltIn, "arcanum", "read_file"), new(GuildToolSourceKind.Adapter, "adapter", "write")],
        [new(GuildMemoryStore.Saga, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Campaign, Id)),
            new(GuildMemoryStore.Transcript, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Session, OtherId))],
        new(new(GuildBudgetMode.Finite, 1.25m), new(GuildBudgetMode.Finite, 17)),
        [GuildDelegationKind.Local, GuildDelegationKind.Remote], null);

    private static GuildRequestedGrant Requested(GuildGrant grant) =>
        new(new(true, grant.Workspaces), new(true, grant.Tools), new(true, grant.Memory),
            new(true, grant.Budget), new(true, grant.Delegation), new(true, grant.ExpiresAtUtc));
}
