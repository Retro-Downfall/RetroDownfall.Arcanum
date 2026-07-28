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

    [Fact]
    public void NormalizeWorkspacePatchSettings_clamps_every_scalar_to_lower_bounds()
    {
        WorkspacePatchSettings normalized =
            ArcanumSettingClamps.NormalizeWorkspacePatchSettings(
                ExtremePatchSettings(long.MinValue, int.MinValue));

        Assert.Equal(1_024L, normalized.MaxPatchBytes);
        Assert.Equal(1_024L, normalized.MaxInputBytesPerFile);
        Assert.Equal(1_024L, normalized.MaxTotalInputBytes);
        Assert.Equal(1_024L, normalized.MaxOutputBytesPerFile);
        Assert.Equal(1_024L, normalized.MaxTotalOutputBytes);
        Assert.Equal(1_024L, normalized.MaxStagingBytesPerFile);
        Assert.Equal(1_024L, normalized.MaxTotalStagingBytes);
        Assert.Equal(100, normalized.MaxElapsedMilliseconds);
        Assert.Equal(50, normalized.RollbackReserveMilliseconds);
        Assert.Equal(1, normalized.MaxFiles);
        Assert.Equal(1, normalized.MaxHunks);
        Assert.Equal(1, normalized.MaxLinesPerHunk);
        Assert.Equal(0, normalized.FuzzyMatchWindowLines);
        Assert.Equal(1, normalized.MaxResultItems);
    }

    [Fact]
    public void NormalizeWorkspacePatchSettings_clamps_every_scalar_to_upper_bounds()
    {
        WorkspacePatchSettings normalized =
            ArcanumSettingClamps.NormalizeWorkspacePatchSettings(
                ExtremePatchSettings(long.MaxValue, int.MaxValue));

        Assert.Equal(64L * 1024L * 1024L, normalized.MaxPatchBytes);
        Assert.Equal(256L * 1024L * 1024L, normalized.MaxInputBytesPerFile);
        Assert.Equal(1L * 1024L * 1024L * 1024L, normalized.MaxTotalInputBytes);
        Assert.Equal(256L * 1024L * 1024L, normalized.MaxOutputBytesPerFile);
        Assert.Equal(1L * 1024L * 1024L * 1024L, normalized.MaxTotalOutputBytes);
        Assert.Equal(512L * 1024L * 1024L, normalized.MaxStagingBytesPerFile);
        Assert.Equal(2L * 1024L * 1024L * 1024L, normalized.MaxTotalStagingBytes);
        Assert.Equal(300_000, normalized.MaxElapsedMilliseconds);
        Assert.Equal(60_000, normalized.RollbackReserveMilliseconds);
        Assert.Equal(1_000, normalized.MaxFiles);
        Assert.Equal(10_000, normalized.MaxHunks);
        Assert.Equal(100_000, normalized.MaxLinesPerHunk);
        Assert.Equal(1_000, normalized.FuzzyMatchWindowLines);
        Assert.Equal(10_000, normalized.MaxResultItems);
    }

    [Fact]
    public void NormalizeWorkspacePatchSettings_enforces_reserve_relation_without_mutating_source()
    {
        WorkspacePatchSettings source = new()
        {
            MaxElapsedMilliseconds = 100,
            RollbackReserveMilliseconds = 60_000,
        };

        WorkspacePatchSettings normalized =
            ArcanumSettingClamps.NormalizeWorkspacePatchSettings(source);

        Assert.NotSame(source, normalized);
        Assert.Equal(100, normalized.MaxElapsedMilliseconds);
        Assert.Equal(99, normalized.RollbackReserveMilliseconds);
        Assert.True(
            normalized.RollbackReserveMilliseconds
            < normalized.MaxElapsedMilliseconds);
        Assert.Equal(60_000, source.RollbackReserveMilliseconds);
    }

    private static WorkspacePatchSettings ExtremePatchSettings(
        long longValue,
        int intValue) =>
        new()
        {
            MaxPatchBytes = longValue,
            MaxInputBytesPerFile = longValue,
            MaxTotalInputBytes = longValue,
            MaxOutputBytesPerFile = longValue,
            MaxTotalOutputBytes = longValue,
            MaxStagingBytesPerFile = longValue,
            MaxTotalStagingBytes = longValue,
            MaxElapsedMilliseconds = intValue,
            RollbackReserveMilliseconds = intValue,
            MaxFiles = intValue,
            MaxHunks = intValue,
            MaxLinesPerHunk = intValue,
            FuzzyMatchWindowLines = intValue,
            MaxResultItems = intValue,
        };

}
