using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class ArcanumSettingClampsTests
{

    [Fact]
    public void EffectiveInProcessToolOutputCapBytes_respects_json_rpc_margin()
    {

        long cap = ArcanumSettingClamps.EffectiveInProcessToolOutputCapBytes(
            toolOutputCapBytes: 1_048_576,
            maxJsonRpcLineBytes: 2_228_224);

        Assert.True(cap >= 1_048_576);

    }

    [Fact]
    public void EffectiveInProcessToolOutputCapBytes_clamps_to_line_budget()
    {

        long cap = ArcanumSettingClamps.EffectiveInProcessToolOutputCapBytes(
            toolOutputCapBytes: 8_388_608,
            maxJsonRpcLineBytes: 131_072);

        Assert.True(cap < 8_388_608);

    }

    [Fact]
    public void HostHttpsPort_clamps_to_valid_range()
    {

        Assert.Equal(1, ArcanumSettingClamps.HostHttpsPort(0));

        Assert.Equal(1, ArcanumSettingClamps.HostHttpsPort(-1));

        Assert.Equal(5443, ArcanumSettingClamps.HostHttpsPort(5443));

        Assert.Equal(65_535, ArcanumSettingClamps.HostHttpsPort(70_000));

    }

    [Fact]
    public void JsonSchemaMaxDepth_clamps_to_valid_range()
    {

        Assert.Equal(1, ArcanumSettingClamps.JsonSchemaMaxDepth(0));

        Assert.Equal(10, ArcanumSettingClamps.JsonSchemaMaxDepth(10));

        Assert.Equal(50, ArcanumSettingClamps.JsonSchemaMaxDepth(100));

    }

    [Fact]
    public void StructuredOutputMaxValidationRetries_clamps_to_valid_range()
    {

        Assert.Equal(0, ArcanumSettingClamps.StructuredOutputMaxValidationRetries(-1));

        Assert.Equal(2, ArcanumSettingClamps.StructuredOutputMaxValidationRetries(2));

        Assert.Equal(5, ArcanumSettingClamps.StructuredOutputMaxValidationRetries(10));

    }

    [Fact]
    public void PricingInputPer1M_clamps_to_valid_range()
    {

        Assert.Equal(0.0, ArcanumSettingClamps.PricingInputPer1M(-1.0));

        Assert.Equal(10.0, ArcanumSettingClamps.PricingInputPer1M(10.0));

        Assert.Equal(1_000_000.0, ArcanumSettingClamps.PricingInputPer1M(2_000_000.0));

    }

    [Fact]
    public void PricingOutputPer1M_clamps_to_valid_range()
    {

        Assert.Equal(0.0, ArcanumSettingClamps.PricingOutputPer1M(-1.0));

        Assert.Equal(30.0, ArcanumSettingClamps.PricingOutputPer1M(30.0));

        Assert.Equal(1_000_000.0, ArcanumSettingClamps.PricingOutputPer1M(2_000_000.0));

    }

    [Fact]
    public void BudgetDailyLimitUsd_clamps_to_valid_range()
    {

        Assert.Equal(0.00m, ArcanumSettingClamps.BudgetDailyLimitUsd(-5.00m));

        Assert.Equal(10.00m, ArcanumSettingClamps.BudgetDailyLimitUsd(10.00m));

        Assert.Equal(1_000_000.00m, ArcanumSettingClamps.BudgetDailyLimitUsd(2_000_000.00m));

    }

    [Fact]
    public void BudgetAlertThresholdPercent_clamps_to_valid_range()
    {

        Assert.Equal(1, ArcanumSettingClamps.BudgetAlertThresholdPercent(0));

        Assert.Equal(80, ArcanumSettingClamps.BudgetAlertThresholdPercent(80));

        Assert.Equal(100, ArcanumSettingClamps.BudgetAlertThresholdPercent(150));

    }

    [Fact]
    public void MaxToolInferenceRounds_clamps_to_1_through_100()
    {
        Assert.Equal(1, ArcanumSettingClamps.MaxToolInferenceRounds(0));
        Assert.Equal(50, ArcanumSettingClamps.MaxToolInferenceRounds(50));
        Assert.Equal(100, ArcanumSettingClamps.MaxToolInferenceRounds(100));
        Assert.Equal(100, ArcanumSettingClamps.MaxToolInferenceRounds(250));
    }

}
