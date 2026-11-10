using RetroDownfall.Arcanum.Core.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildRemoteInvocationIntentTests
{
    [Fact]
    public void Mode_skill_and_accepted_modes_are_frozen_in_canonical_caller_intent()
    {
        GuildIssueRootRequest request = new(Guid.NewGuid(), Guid.NewGuid(), null, "Compute", new([], [], [],
            new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [GuildDelegationKind.Remote], null));

        string omitted = GuildQuestIntent.Compute(request).Value;

        var blocking = request with { RemoteOptions = new() };

        var continuable = request with { RemoteOptions = new(GuildRemoteDispatchMode.Continuable) };

        Assert.NotEqual(omitted, GuildQuestIntent.Compute(blocking).Value);

        Assert.NotEqual(GuildQuestIntent.Compute(blocking).Value, GuildQuestIntent.Compute(continuable).Value);

        Assert.NotEqual(GuildQuestIntent.Compute(continuable).Value,
            GuildQuestIntent.Compute(continuable with { RemoteOptions = continuable.RemoteOptions! with { SkillId = "compute" } }).Value);

        Assert.NotEqual(GuildQuestIntent.Compute(blocking).Value,
            GuildQuestIntent.Compute(blocking with { RemoteOptions = new(AcceptedOutputModes: ["application/json"]) }).Value);
    }
}
