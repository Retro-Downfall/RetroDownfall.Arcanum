using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class GrimoireDbReadinessTests
{

    [Fact]
    public void MarkReady_sets_IsReady_true()
    {

        GrimoireDbReadiness readiness = new();

        Assert.False(readiness.IsReady);

        readiness.MarkReady();

        Assert.True(readiness.IsReady);

    }

}
