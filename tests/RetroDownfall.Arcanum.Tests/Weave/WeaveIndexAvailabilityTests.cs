using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Tests.Weave;

public sealed class WeaveIndexAvailabilityTests
{

    [Fact]
    public void Default_IsManagedMode_WithBudgetConstant()
    {

        WeaveIndexAvailability availability = new();

        Assert.False(availability.IsVecAvailable);

        Assert.Equal(WeaveIndexAvailability.ModeManaged, availability.Mode);

        Assert.Equal(50_000, WeaveIndexAvailability.ManagedSearchRowBudget);

        Assert.Contains("managed", availability.Diagnostic, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void SetAvailable_True_FlipsToVec0()
    {

        WeaveIndexAvailability availability = new();

        availability.SetAvailable(true, "vec loaded");

        Assert.True(availability.IsVecAvailable);

        Assert.Equal(WeaveIndexAvailability.ModeVec0, availability.Mode);

        Assert.Equal("vec loaded", availability.Diagnostic);

    }

}
