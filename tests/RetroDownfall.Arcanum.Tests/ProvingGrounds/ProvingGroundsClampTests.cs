using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.ProvingGrounds;

public sealed class ProvingGroundsClampTests
{

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(20, 20)]
    [InlineData(200, 200)]
    [InlineData(500, 200)]
    public void MaxInquisitorsPerTrial_ClampsToOneThroughTwoHundred(int value, int expected)
    {

        Assert.Equal(expected, ArcanumSettingClamps.MaxInquisitorsPerTrial(value));

    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(8, 8)]
    [InlineData(256, 256)]
    [InlineData(1000, 256)]
    public void SemanticJudgeMaxTokens_ClampsToOneThroughTwoFiftySix(int value, int expected)
    {

        Assert.Equal(expected, ArcanumSettingClamps.SemanticJudgeMaxTokens(value));

    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(60, 60)]
    [InlineData(600, 600)]
    [InlineData(900, 600)]
    public void SemanticJudgeTimeoutSeconds_ClampsToOneThroughSixHundred(int value, int expected)
    {

        Assert.Equal(expected, ArcanumSettingClamps.SemanticJudgeTimeoutSeconds(value));

    }

    [Fact]
    public void ProvingGroundsSettings_HasExpectedDefaults()
    {

        ProvingGroundsSettings settings = new();

        Assert.Equal(20, settings.MaxInquisitorsPerTrial);

        Assert.Equal(8, settings.SemanticJudgeMaxTokens);

        Assert.Equal(60, settings.SemanticJudgeTimeoutSeconds);

    }

}
