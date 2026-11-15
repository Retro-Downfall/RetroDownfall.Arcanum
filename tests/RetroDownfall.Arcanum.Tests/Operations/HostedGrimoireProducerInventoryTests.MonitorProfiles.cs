using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    private static CSharpCompilation MonitorProfileCompilation(string body, string helpers = "") =>
        CompileWithProductionReferencePack(R2Source(body + " await Task.CompletedTask;", helpers), "RetroDownfall.Arcanum.Api");

    private static string MonitorProfileBody(string inside) =>
        "if (!System.Threading.Monitor.TryEnter(MonitorProfileInput.Gate)) return; try { " + inside
        + " } finally { System.Threading.Monitor.Exit(MonitorProfileInput.Gate); }";

    private const string MonitorProfileInput = "static class MonitorProfileInput { internal static readonly object Gate = new(); }";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactMonitorProfilesDoNotHideTheGuardedBodyOrGrantAdmission(bool physicalBody)
    {
        CSharpCompilation compilation = MonitorProfileCompilation(
            MonitorProfileBody(physicalBody ? "System.IO.File.Delete(\"guarded-monitor-effect\");" : "_ = 1;"), MonitorProfileInput);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverUnderRegime(compilation, production: false);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail is "System.Threading.Monitor.TryEnter" or "System.Threading.Monitor.Exit");

        if (!physicalBody)
        {
            Assert.Empty(result.Diagnostics);

            return;
        }

        HostedProducerSite effect = Assert.Single(EnumerationDeleteSites(result));

        Assert.Null(effect.WorkFrontierCapsuleId);

        Assert.Null(effect.EffectFrontierCapsuleId);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"
            && diagnostic.Detail == "System.IO.File.Delete");
    }

    [Fact]
    public void MonitorTimeoutOverloadRemainsOutsideTheTwoReviewedProfiles()
    {
        CSharpCompilation compilation = MonitorProfileCompilation(
            "_ = System.Threading.Monitor.TryEnter(MonitorProfileInput.Gate, 0);", MonitorProfileInput);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverUnderRegime(compilation, production: false);

        Assert.True(DispatchRefused(result, "System.Threading.Monitor.TryEnter"));
    }

    [Fact]
    public void ExactMonitorArgumentGetterRetainsItsPhysicalEffect()
    {
        const string input = """
            static class MonitorProfileInput
            {
                internal static object Gate
                {
                    get
                    {
                        System.IO.File.Delete("monitor-gate-getter");

                        return new object();
                    }
                }
            }
            """;

        CSharpCompilation compilation = MonitorProfileCompilation("_ = System.Threading.Monitor.TryEnter(MonitorProfileInput.Gate);", input);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverUnderRegime(compilation, production: false);

        Assert.NotEmpty(EnumerationDeleteSites(result));

        Assert.All(EnumerationDeleteSites(result), static site =>
        {
            Assert.Null(site.WorkFrontierCapsuleId);

            Assert.Null(site.EffectFrontierCapsuleId);
        });

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"
            && diagnostic.Detail == "System.IO.File.Delete");
    }

    [Fact]
    public void AuthoredMonitorSpellingDoesNotHideItsBody()
    {
        const string counterfeit = """
            namespace System.Threading
            {
                static class Monitor
                {
                    internal static bool TryEnter(object gate)
                    {
                        System.IO.File.Delete("authored-monitor");

                        return true;
                    }
                }
            }
            """;

        CSharpCompilation compilation = MonitorProfileCompilation("_ = System.Threading.Monitor.TryEnter(new object());", counterfeit);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverUnderRegime(compilation, production: false);

        HostedProducerSite effect = Assert.Single(EnumerationDeleteSites(result));

        Assert.Null(effect.WorkFrontierCapsuleId);

        Assert.Null(effect.EffectFrontierCapsuleId);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"
            && diagnostic.Detail == "System.IO.File.Delete");
    }

    [Theory]
    [InlineData("TryEnter", "_ = counterfeit::System.Threading.Monitor.TryEnter(new object());")]
    [InlineData("Exit", "counterfeit::System.Threading.Monitor.Exit(new object());")]
    public void ForeignMonitorSpellingDoesNotReceiveTheFrameworkProfile(string method, string body)
    {
        const string counterfeit = """
            namespace System.Threading
            {
                public static class Monitor
                {
                    public static bool TryEnter(object gate)
                    {
                        System.IO.File.Delete("foreign-monitor-enter");

                        return true;
                    }

                    public static void Exit(object gate) => System.IO.File.Delete("foreign-monitor-exit");
                }
            }
            """;

        CSharpCompilation foreign = CompileWithProductionReferencePack(counterfeit, "RetroDownfall.Arcanum.Api")
            .WithAssemblyName("Foreign.Monitor");

        using MemoryStream image = new();

        Assert.True(foreign.Emit(image).Success);

        CSharpCompilation compilation = CompileWithProductionReferencePack("extern alias counterfeit; " + R2Source(body + " await Task.CompletedTask;"),
            "RetroDownfall.Arcanum.Api")
            .AddReferences(MetadataReference.CreateFromImage(image.ToArray(), new MetadataReferenceProperties(aliases: ["counterfeit"])));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverUnderRegime(compilation, production: false);

        Assert.True(DispatchRefused(result, "System.Threading.Monitor." + method));
    }
}
