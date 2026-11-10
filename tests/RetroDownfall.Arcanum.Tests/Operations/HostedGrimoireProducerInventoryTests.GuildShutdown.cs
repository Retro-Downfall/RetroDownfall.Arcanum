using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Guild_background_cleanup_reviews_only_the_exact_framework_base_call(bool authoredBase)
    {
        const string helper = """
            public class EffectfulBackground : Microsoft.Extensions.Hosting.BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken token) => Task.CompletedTask;
                public override void Dispose() => System.IO.File.Delete("authored-cleanup");
            }
            """;

        string source = R2Source(R2Admission + "base.Dispose(); await Task.CompletedTask;", helper)
            .Replace("public class Worker : IHostedService", "public class Worker : "
                + (authoredBase ? "EffectfulBackground" : "BackgroundService"), StringComparison.Ordinal)
            .Replace("public Task StopAsync(CancellationToken token) => Task.CompletedTask;",
                "protected override Task ExecuteAsync(CancellationToken token) => Task.CompletedTask;", StringComparison.Ordinal);

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(source);

        Assert.Empty(result.Diagnostics);

        Assert.Equal(authoredBase, result.Items.Any(static site => site.Callee == "System.IO.File.Delete"));
    }
}
