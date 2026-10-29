using Microsoft.CodeAnalysis;

using Xunit;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("reduced", true)]
    [InlineData("static", true)]
    [InlineData("callback-empty", false)]
    [InlineData("callback-clear", false)]
    [InlineData("callback-unknown", false)]
    [InlineData("authored-clear", false)]
    public void HostedContinuationLoggingRegistrationRequiresTheExactNoCallbackOverload(string shape, bool closed)
    {
        string builder = shape switch
        {
            "reduced" => "services.AddLogging();",
            "static" => "Microsoft.Extensions.DependencyInjection.LoggingServiceCollectionExtensions.AddLogging(services);",
            "callback-empty" => "services.AddLogging(static builder => { });",
            "callback-clear" => "services.AddLogging(static builder => builder.Services.Clear());",
            "callback-unknown" => "services.AddLogging(LoggingCompositionInput.Configure);",
            _ => "LoggingCompositionInput.Register(services);",
        };

        const string registration = "services.AddScoped<RetroDownfall.Arcanum.Infrastructure.Operations.ExternalRecoveryHandler>();";

        string source = RecoveryFactoryAliasFixture().Replace(registration, builder + " " + registration, StringComparison.Ordinal)
            + " internal static class LoggingCompositionInput { internal static extern void Configure(Microsoft.Extensions.Logging.ILoggingBuilder builder); internal static void Register(Microsoft.Extensions.DependencyInjection.IServiceCollection services) { services.Clear(); } }";

        Assert.Contains(builder, source, StringComparison.Ordinal);

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverRecoveryMatrixFixture(source);

        Assert.Equal(!closed, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_RECOVERY_HANDLER_UNPROVEN"
            && diagnostic.Detail.Contains("mutate or compete with the final service registration set", StringComparison.Ordinal)));
    }

    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void HostedContinuationActualConfigurationSubscriptionKeepsTheClosedDefaultMonitorComposition()
    {
        Assert.Contains(HostedGrimoireProducerInventory.ProductionCompilations.SelectMany(static compilation => compilation.SyntaxTrees),
            static tree => tree.FilePath.EndsWith("/CovenantFeatureConfigurationPublisher.cs", StringComparison.Ordinal));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.ProductionSiteDiscovery;

        Assert.NotNull(result.AnalysisMetrics);

        Assert.Contains(result.AnalysisMetrics.RootTraversals, static root =>
            root.RootType == "CovenantFeatureConfigurationPublisher" && root.AnalyzedStates > 0);

        HostedProducerInventoryDiagnostic[] failures = [.. result.Diagnostics.Where(static diagnostic =>
            diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
                && diagnostic.Identity.Contains("/CovenantFeatureConfigurationPublisher.cs:", StringComparison.Ordinal))];

        Assert.True(failures.Length == 0, HostedGrimoireProducerInventory.RenderDiagnosticReport(failures));
    }
}
