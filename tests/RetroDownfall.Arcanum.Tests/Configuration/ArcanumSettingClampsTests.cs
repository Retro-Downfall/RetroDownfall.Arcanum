using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class ArcanumSettingClampsTests
{

    [Theory]
    [InlineData(-1, 50)]
    [InlineData(300, 300)]
    [InlineData(10_000, 5_000)]
    public void EmbeddingsCodebaseWatcherDebounceMilliseconds_ClampsToSafeRange(int value, int expected)
    {

        Assert.Equal(expected, ArcanumSettingClamps.EmbeddingsCodebaseWatcherDebounceMilliseconds(value));

    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(32, 32)]
    [InlineData(10_000, 128)]
    public void EmbeddingsCodebaseMaxWatchers_ClampsToSafeRange(int value, int expected)
    {

        Assert.Equal(expected, ArcanumSettingClamps.EmbeddingsCodebaseMaxWatchers(value));

    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(60, 60)]
    [InlineData(10_000, 1_440)]
    public void EmbeddingsCodebaseReconciliationIntervalMinutes_ClampsToSafeRange(int value, int expected)
    {

        Assert.Equal(expected, ArcanumSettingClamps.EmbeddingsCodebaseReconciliationIntervalMinutes(value));

    }

    [Fact]
    public void ResolveEmbeddings_UsesAutomaticCodebaseIndexingMechanics()
    {

        ArcanumSettings settings = new()
        {
            Integrations = new IntegrationSettings
            {
                Embeddings = new EmbeddingIntegrationSettings
                {
                    Provider = "embedding-provider",
                },
            },
        };

        CodebaseEmbeddingSettings codebase = settings.ResolveEmbeddings().Codebase;

        CodebaseEmbeddingSettings expected = ArcanumRuntimeDefaults.Embeddings.Codebase;

        Assert.Equal(expected.MaxFilesToIndex, codebase.MaxFilesToIndex);

        Assert.Equal(expected.FileExtensions, codebase.FileExtensions);

        Assert.Equal(expected.IndexingIntervalMinutes, codebase.IndexingIntervalMinutes);

        Assert.Equal(expected.WatcherDebounceMilliseconds, codebase.WatcherDebounceMilliseconds);

        Assert.Equal(expected.MaxWatchers, codebase.MaxWatchers);

        Assert.Equal(expected.ReconciliationIntervalMinutes, codebase.ReconciliationIntervalMinutes);

        Assert.Equal(expected.MaxRetrievedChunks, codebase.MaxRetrievedChunks);

    }

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

    [Theory]
    [InlineData("MaxToolInferenceRounds")]
    [InlineData("MaxRunSteps")]
    [InlineData("MaxStepRetries")]
    [InlineData("RetryBackoffSeconds")]
    [InlineData("RetryBackoffMaxSeconds")]
    [InlineData("MaxReweavesPerRun")]
    [InlineData("MaxPlanSteps")]
    [InlineData("MaxFallbackAttempts")]
    [InlineData("StructuredOutputMaxValidationRetries")]
    [InlineData("ExternalTaskTimeoutMinutes")]
    public void Workflow_termination_limits_have_no_configuration_clamp(string methodName)
    {
        Assert.Null(typeof(ArcanumSettingClamps).GetMethod(methodName));
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
    public void NormalizeWorkspacePatchSettings_clamps_every_scalar_to_lower_bounds()
    {
        WorkspacePatchSettings normalized =
            ArcanumSettingClamps.NormalizeWorkspacePatchSettings(
                ExtremePatchSettings(long.MinValue, int.MinValue));

        Assert.Equal(1_024L, normalized.MaxPatchBytes);
        Assert.Equal(1_024L, normalized.MaxInputBytesPerFile);
        Assert.Equal(1_024L, normalized.MaxOutputBytesPerFile);
        Assert.Equal(1_024L, normalized.MaxTotalOutputBytes);
        Assert.Equal(1_024L, normalized.MaxStagingBytesPerFile);
        Assert.Equal(1_024L, normalized.MaxTotalStagingBytes);
        Assert.Equal(50, normalized.RecoveryTimeoutMilliseconds);
        Assert.Equal(0, normalized.FuzzyMatchWindowLines);
    }

    [Fact]
    public void NormalizeWorkspacePatchSettings_clamps_every_scalar_to_upper_bounds()
    {
        WorkspacePatchSettings normalized =
            ArcanumSettingClamps.NormalizeWorkspacePatchSettings(
                ExtremePatchSettings(long.MaxValue, int.MaxValue));

        Assert.Equal(64L * 1024L * 1024L, normalized.MaxPatchBytes);
        Assert.Equal(256L * 1024L * 1024L, normalized.MaxInputBytesPerFile);
        Assert.Equal(256L * 1024L * 1024L, normalized.MaxOutputBytesPerFile);
        Assert.Equal(1L * 1024L * 1024L * 1024L, normalized.MaxTotalOutputBytes);
        Assert.Equal(512L * 1024L * 1024L, normalized.MaxStagingBytesPerFile);
        Assert.Equal(2L * 1024L * 1024L * 1024L, normalized.MaxTotalStagingBytes);
        Assert.Equal(60_000, normalized.RecoveryTimeoutMilliseconds);
        Assert.Equal(1_000, normalized.FuzzyMatchWindowLines);
    }

    [Fact]
    public void NormalizeWorkspacePatchSettings_clamps_recovery_cleanup_without_mutating_source()
    {
        WorkspacePatchSettings source = new()
        {
            RecoveryTimeoutMilliseconds = int.MaxValue,
        };

        WorkspacePatchSettings normalized =
            ArcanumSettingClamps.NormalizeWorkspacePatchSettings(source);

        Assert.NotSame(source, normalized);
        Assert.Equal(60_000, normalized.RecoveryTimeoutMilliseconds);
        Assert.Equal(int.MaxValue, source.RecoveryTimeoutMilliseconds);
    }

    private static WorkspacePatchSettings ExtremePatchSettings(
        long longValue,
        int intValue) =>
        new()
        {
            MaxPatchBytes = longValue,
            MaxInputBytesPerFile = longValue,
            MaxOutputBytesPerFile = longValue,
            MaxTotalOutputBytes = longValue,
            MaxStagingBytesPerFile = longValue,
            MaxTotalStagingBytes = longValue,
            RecoveryTimeoutMilliseconds = intValue,
            FuzzyMatchWindowLines = intValue,
        };
}
