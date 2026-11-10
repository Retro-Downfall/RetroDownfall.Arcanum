using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("ArcanumMetrics.ConclaveSendingDuration.Record(1d, new KeyValuePair<string, object?>(\"outcome\", \"completed\"));", true)]
    [InlineData("KeyValuePair<string, object?> outcome = new(\"outcome\", \"failed\"); ArcanumMetrics.ConclaveSendingDuration.Record(1d, outcome);", true)]
    [InlineData("ArcanumMetrics.Other.Record(1d);", false)]
    [InlineData("ArcanumMetrics.Mutable.Record(1d);", false)]
    [InlineData("new Meter(\"Other\").CreateHistogram<double>(\"arcanum_conclave_sending_duration_seconds\").Record(1d);", false)]
    [InlineData("ArcanumMetrics.ConclaveSendingDuration.Record(1d, new KeyValuePair<string, object?>(\"outcome\", new object()));", false)]
    [InlineData("ArcanumMetrics.ConclaveSendingDuration.Record(1d, new ReadOnlySpan<KeyValuePair<string, object?>>());", false)]
    public void Guild_transport_duration_metric_requires_exact_owned_instrument_and_string_tags(string body, bool accepted)
    {
        const string helper = """
            namespace RetroDownfall.Arcanum.Core.Telemetry
            {
                public static class ArcanumMetrics
                {
                    public static readonly Meter Meter = new("Arcanum", "0.1.0");
                    public static readonly Histogram<double> ConclaveSendingDuration = Meter.CreateHistogram<double>("arcanum_conclave_sending_duration_seconds", "s", "duration");
                    public static readonly Histogram<double> Other = Meter.CreateHistogram<double>("other", "s", "duration");
                    public static Histogram<double> Mutable = Meter.CreateHistogram<double>("arcanum_conclave_sending_duration_seconds", "s", "duration");
                }
            }
            """;

        string source = "using System.Diagnostics.Metrics; using System.Collections.Generic; using RetroDownfall.Arcanum.Core.Telemetry; "
            + R2Source(body + " await Task.CompletedTask;", helper);

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        compilation = compilation.RemoveReferences(compilation.References.Where(reference =>
            compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol { Name: "RetroDownfall.Arcanum.Core" }));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        if (accepted) Assert.Empty(result.Diagnostics);
        else Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Diagnostics.Metrics.Histogram`1.Record");
    }
}
