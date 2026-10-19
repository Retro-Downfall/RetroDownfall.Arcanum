using RetroDownfall.Arcanum.Api.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Tests.LongRest;

namespace RetroDownfall.Arcanum.Tests.ProvingGrounds;

public sealed class MemoryEvaluationProviderEpochTests
{
    [Fact]
    public void EndpointAbaInvalidatesPreparedSnapshotAndCapturedSettingsAreDetached()
    {
        var options = new LongRestConfigurationEpochTests.NotifyingOptions(LongRestConfigurationEpochTests.Settings());

        using var epoch = new MemoryEvaluationProviderEpoch(options);

        var original = epoch.Capture();

        var changed = LongRestConfigurationEpochTests.Settings();

        changed.Providers[0].Endpoint = "https://changed.test/v1";

        options.Set(changed);

        options.Set(LongRestConfigurationEpochTests.Settings());

        Assert.False(epoch.IsCurrent(original));

        Assert.True(epoch.IsCurrent(epoch.Capture()));

        Assert.Equal(2, epoch.Capture().Generation);

        original.Read().Providers[0].Endpoint = "https://mutated.test";

        Assert.Equal("https://provider.test/v1", original.Read().Providers[0].Endpoint);
    }
}
