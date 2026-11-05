using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildSerializationTests
{
    [Theory]
    [InlineData("Guild.GuildAgentCard")]
    [InlineData("Guild.GuildAgentCardDto")]
    [InlineData("Guild.GuildCommissionDto")]
    [InlineData("Guild.GuildProducerIdentity")]
    [InlineData("Guild.GuildEnvelope")]
    [InlineData("Guild.GuildSpendDto")]
    [InlineData("Serialization.GuildJsonContext")]
    public void Dormant_contract_has_a_named_Core_type(string suffix)
    {
        Assert.NotNull(typeof(Result).Assembly.GetType($"RetroDownfall.Arcanum.Core.{suffix}"));
    }
}
