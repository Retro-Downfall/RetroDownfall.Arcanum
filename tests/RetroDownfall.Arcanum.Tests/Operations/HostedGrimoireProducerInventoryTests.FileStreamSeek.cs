using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("none", false, false)]
    [InlineData("work-only", true, false)]
    [InlineData("admitted", true, true)]
    public void FileStream_seek_is_an_effect_that_requires_its_own_authority(
        string admission,
        bool hasWork,
        bool hasEffect)
    {
        const string Callee = "System.IO.FileStream.Seek";

        string prefix = admission switch
        {
            "admitted" => R2Admission,
            "work-only" => AcquireWork.Replace("return Task.CompletedTask;", "return;", StringComparison.Ordinal),
            _ => string.Empty,
        };

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(prefix
            + "System.IO.FileStream input = null!; _ = input.Seek(5L, System.IO.SeekOrigin.Current); await Task.CompletedTask;"));

        HostedProducerSite seek = Assert.Single(result.Items, static site => site.Callee == Callee);

        Assert.Equal(HostedProducerSiteKind.FileSystemEffect, seek.Kind);

        Assert.Equal(hasWork, seek.WorkFrontierCapsuleId is not null);

        Assert.Equal(hasEffect, seek.EffectFrontierCapsuleId is not null);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == Callee);

        Assert.Equal(!hasWork, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
            && diagnostic.Detail == Callee));

        Assert.Equal(!hasEffect, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"
            && diagnostic.Detail == Callee));

        if (hasEffect)
        {
            Assert.Empty(result.Diagnostics);
        }
    }
}
