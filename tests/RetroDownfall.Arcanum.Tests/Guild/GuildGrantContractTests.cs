using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildGrantContractTests
{
    [Theory]
    [InlineData("GuildGrant")]
    [InlineData("GuildRequestedGrant")]
    [InlineData("GuildWorkspaceGrant")]
    [InlineData("GuildToolGrant")]
    [InlineData("GuildMemoryGrant")]
    [InlineData("GuildMemoryScope")]
    [InlineData("GuildBudgetGrant")]
    [InlineData("GuildUsdBudget")]
    [InlineData("GuildTokenBudget")]
    [InlineData("GuildGrantPolicy")]
    public void Resolved_design_grant_type_exists(string name)
    {
        Assert.NotNull(typeof(Result).Assembly.GetType($"RetroDownfall.Arcanum.Core.Guild.{name}"));
    }
}
