using System.Collections.Immutable;
using System.Reflection;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildDelegationPolicyTests
{
    [Fact]
    public void Stable_ancestor_agent_identity_refuses_even_when_a_new_run_would_be_assigned()
    {
        Guid ancestor = Guid.Parse("11111111-1111-1111-1111-111111111111");

        var result = Validate(ancestor, [ancestor]);

        Assert.True(result.IsFailure);

        Assert.Equal("Guild.DelegationCycle", result.Error.Code);
    }

    [Fact]
    public void Distinct_agents_are_not_rejected_by_an_arbitrary_depth_ceiling()
    {
        var ancestors = Enumerable.Range(1, 2048)
            .Select(value => new Guid(value, 0, 0, new byte[8]))
            .ToImmutableArray();

        Assert.True(Validate(Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"), ancestors).IsSuccess);
    }

    [Fact]
    public void Missing_target_or_ancestor_identity_does_not_claim_a_cycle_free_path()
    {
        Assert.Equal("Guild.InvalidRequest", Validate(Guid.Empty, []).Error.Code);

        Assert.Equal("Guild.InvalidRequest", Validate(Guid.NewGuid(), [Guid.Empty]).Error.Code);
    }

    private static Result Validate(Guid target, ImmutableArray<Guid> ancestors)
    {
        var type = typeof(Result).Assembly.GetType("RetroDownfall.Arcanum.Core.Guild.GuildDelegationPolicy");

        Assert.NotNull(type);

        var method = Assert.IsAssignableFrom<MethodInfo>(type.GetMethod("ValidateAncestorPath"));

        return Assert.IsAssignableFrom<Result>(method.Invoke(null, [target, ancestors]));
    }
}
