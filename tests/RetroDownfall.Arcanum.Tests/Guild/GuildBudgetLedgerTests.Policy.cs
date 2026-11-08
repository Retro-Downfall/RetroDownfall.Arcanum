using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildBudgetLedgerTests
{
    [Fact]
    public void Omitted_hint_selects_remaining_authority_subject_to_operator_policy()
    {
        Result<GuildBudgetGrant> result = GuildBudgetAllocationPolicy.ResolveHint(null, Grant(8m, 80), Grant(6m, null));

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(Grant(6m, 80), result.Value);
    }

    [Fact]
    public void Explicit_allocation_refuses_oversize_while_hints_clamp_each_axis_and_return_the_resolved_grant()
    {
        GuildBudgetGrant remaining = Grant(8m, 80), policy = Grant(6m, 60);

        Assert.Equal(ErrorCodes.Guild.GrantExceeded,
            GuildBudgetAllocationPolicy.ResolveStrict(Grant(7m, 50), remaining, policy).Error.Code);

        Result<GuildBudgetGrant> result = GuildBudgetAllocationPolicy.ResolveHint(
            new(new(GuildBudgetMode.Finite, 9m), new(GuildBudgetMode.Finite, 20)), remaining, policy);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(Grant(6m, 20), result.Value);

        Assert.Equal(Grant(1m, 60), GuildBudgetAllocationPolicy.ResolveHint(new(new(GuildBudgetMode.Finite, 1m)), remaining, policy).Value);

        Assert.Equal(ErrorCodes.Guild.InvalidRequest, GuildBudgetAllocationPolicy.ResolveHint(
            new(new(GuildBudgetMode.Finite, null)), remaining, policy).Error.Code);

        Assert.Equal(ErrorCodes.Guild.InvalidRequest,
            GuildBudgetAllocationPolicy.ResolveStrict(Grant(-1m, 1), remaining, policy).Error.Code);
    }
}
