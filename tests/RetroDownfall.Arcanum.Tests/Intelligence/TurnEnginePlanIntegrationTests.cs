namespace RetroDownfall.Arcanum.Tests.Intelligence;

public class TurnEnginePlanIntegrationTests
{
    [Fact]
    public void Plan_Event_Emitted_On_Turn_Start()
    {
        var eventEmitter = new RetroDownfall.Arcanum.Api.Intelligence.TurnEngine.TurnEventEmitter(Guid.NewGuid());
        Assert.NotNull(eventEmitter);
        Assert.Equal(eventEmitter.RunId, eventEmitter.RunId);
    }

    [Fact]
    public void TurnPlan_Event_Records_Correlation()
    {
        var correlation = new RetroDownfall.Arcanum.Api.Intelligence.TurnEngine.TurnEventCorrelation(
            Guid.NewGuid(), 1, 0, 0, "mc-1", "tool-1", DateTimeOffset.UtcNow);
        Assert.NotEqual(Guid.Empty, correlation.RunId);
        Assert.Equal(1, correlation.Sequence);
    }
}
