using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildBudgetAllocationPolicyTests
{
    private static GuildBudgetGrant Grant(decimal? usd, long? tokens) => new(
        usd is null ? new(GuildBudgetMode.Unlimited) : new(GuildBudgetMode.Finite, usd),
        tokens is null ? new(GuildBudgetMode.Unlimited) : new(GuildBudgetMode.Finite, tokens));

    [Fact]
    public void Hints_clamp_each_dimension_independently_to_remaining_and_policy()
    {
        decimal?[] usd = [null, 0m, 2m];
        long?[] tokens = [null, 0L, 3L];

        foreach (decimal? proposedUsd in usd)
        foreach (decimal? remainingUsd in usd)
        foreach (decimal? policyUsd in usd)
        foreach (long? proposedTokens in tokens)
        foreach (long? remainingTokens in tokens)
        foreach (long? policyTokens in tokens)
        {
            GuildBudgetGrant requested = Grant(proposedUsd, proposedTokens);
            GuildBudgetGrant remaining = Grant(remainingUsd, remainingTokens);
            GuildBudgetGrant policy = Grant(policyUsd, policyTokens);
            GuildBudgetHint hint = new(requested.Usd, requested.Tokens);
            Result<GuildBudgetGrant> result = GuildBudgetAllocationPolicy.ResolveHint(hint, remaining, policy);
            decimal? expectedUsd = new[] { proposedUsd, remainingUsd, policyUsd }.Where(value => value.HasValue).Min();
            long? expectedTokens = new[] { proposedTokens, remainingTokens, policyTokens }.Where(value => value.HasValue).Min();

            Assert.True(result.IsSuccess, result.Error.Message);
            Assert.Equal(Grant(expectedUsd, expectedTokens), result.Value);

            Result<GuildBudgetGrant> strict = GuildBudgetAllocationPolicy.ResolveStrict(requested, remaining, policy);
            Assert.Equal(requested == Grant(expectedUsd, expectedTokens), strict.IsSuccess);

            if (strict.IsFailure) Assert.Equal(ErrorCodes.Guild.GrantExceeded, strict.Error.Code);
        }
    }

    [Fact]
    public void Missing_hints_inherit_remaining_and_invalid_authority_is_never_clamped_into_validity()
    {
        GuildBudgetGrant remaining = Grant(5m, 9);
        GuildBudgetGrant policy = Grant(4m, 10);
        GuildBudgetGrant invalid = Grant(-1m, 2);

        Assert.Equal(Grant(4m, 9), GuildBudgetAllocationPolicy.ResolveHint(null, remaining, policy).Value);
        Assert.Equal(Grant(2m, 9), GuildBudgetAllocationPolicy.ResolveHint(new(new(GuildBudgetMode.Finite, 2)), remaining, policy).Value);
        Assert.Equal(Grant(4m, 1), GuildBudgetAllocationPolicy.ResolveHint(new(Tokens: new(GuildBudgetMode.Finite, 1)), remaining, policy).Value);
        Assert.Equal(ErrorCodes.Guild.InvalidRequest, GuildBudgetAllocationPolicy.ResolveHint(null, null!, policy).Error.Code);
        Assert.Equal(ErrorCodes.Guild.InvalidRequest, GuildBudgetAllocationPolicy.ResolveHint(null, invalid, policy).Error.Code);
        Assert.Equal(ErrorCodes.Guild.InvalidRequest, GuildBudgetAllocationPolicy.ResolveHint(null, remaining, invalid).Error.Code);
        Assert.Equal(ErrorCodes.Guild.InvalidRequest, GuildBudgetAllocationPolicy.ResolveHint(new(new(GuildBudgetMode.Finite, -1)), remaining, policy).Error.Code);
        Assert.Equal(ErrorCodes.Guild.InvalidRequest, GuildBudgetAllocationPolicy.ResolveStrict(invalid, remaining, policy).Error.Code);
        Assert.Equal(ErrorCodes.Guild.InvalidRequest, GuildBudgetAllocationPolicy.ResolveStrict(remaining, invalid, policy).Error.Code);
        Assert.Equal(ErrorCodes.Guild.InvalidRequest, GuildBudgetAllocationPolicy.ResolveStrict(remaining, remaining, invalid).Error.Code);
    }
}
