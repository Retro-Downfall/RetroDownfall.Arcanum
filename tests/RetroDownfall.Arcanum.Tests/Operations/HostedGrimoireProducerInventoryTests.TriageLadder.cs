using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    // Triage Ladder rung D1: the exact default primitive PriorityQueue is a pure ordered store, so its
    // arguments need only the argument-boundary rule. They are evaluated, and any authored conversion or
    // call inside them is traversed, where they are written.
    [Theory]
    [InlineData("computed-components", "var pending = new PriorityQueue<Guid, (double Score, ulong ReverseHigh, ulong ReverseLow)>(); byte[] canonical = Guid.Empty.ToByteArray(bigEndian: true); pending.Enqueue(Guid.Empty, (1d, ~System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(canonical.AsSpan(0, 8)), ~System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(canonical.AsSpan(8, 8)))); _ = pending.Dequeue();", true)]
    [InlineData("record-property-priority", "var pending = new PriorityQueue<QueueRank, (double Score, ulong ReverseHigh, ulong ReverseLow)>(); QueueRank rank = new(Guid.Empty, 2d); pending.Enqueue(rank, (rank.Score, 0UL, 0UL)); _ = pending.Count;", true)]
    [InlineData("element-boundary-conversion", "var pending = new PriorityQueue<Guid, (double Score, ulong ReverseHigh, ulong ReverseLow)>(); pending.Enqueue(new QueueBoundaryProbe(), (1d, 0UL, 0UL));", false)]
    [InlineData("comparer-receiver", "var pending = new PriorityQueue<Guid, (double Score, ulong ReverseHigh, ulong ReverseLow)>(Comparer<(double, ulong, ulong)>.Create((left, right) => { System.IO.File.Delete(\"triage-comparer\"); return 0; })); pending.Enqueue(Guid.Empty, (1d, 0UL, 0UL));", false)]
    public void PrimitivePriorityQueueArgumentsNeedOnlyTheArgumentBoundaryRule(string shape, string body, bool accepted)
    {
        Assert.NotEmpty(shape);

        const string helper = """
            sealed record QueueRank(Guid Node, double Score);

            sealed class QueueBoundaryProbe
            {
                public static implicit operator Guid(QueueBoundaryProbe value)
                {
                    System.IO.File.Delete("triage-boundary-conversion");

                    return Guid.Empty;
                }
            }
            """;

        string source = "using System.Collections.Generic; " + R2Source(body + " await Task.CompletedTask;", helper);

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(source);

        if (accepted)
        {
            Assert.Empty(result.Diagnostics);

            Assert.Empty(result.Items);

            return;
        }

        // A refusal stays at the queue call, or the authored work it would have hidden is reported.
        Assert.True(result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail == "System.Collections.Generic.PriorityQueue`2.Enqueue")
            || result.Items.Any(static site => site.Callee == "System.IO.File.Delete")
                && result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
                    && diagnostic.Detail == "System.IO.File.Delete"));
    }

    [Fact]
    public void PrimitivePriorityQueueBoundaryConversionStaysUnclassifiedAtTheQueueCall()
    {
        const string helper = """
            sealed class QueueBoundaryProbe
            {
                public static implicit operator Guid(QueueBoundaryProbe value) => Guid.Empty;
            }
            """;

        string source = "using System.Collections.Generic; " + R2Source(
            "var pending = new PriorityQueue<Guid, (double Score, ulong ReverseHigh, ulong ReverseLow)>(); pending.Enqueue(new QueueBoundaryProbe(), (1d, 0UL, 0UL)); await Task.CompletedTask;", helper);

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(source);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Collections.Generic.PriorityQueue`2.Enqueue");
    }

    // Triage Ladder rung D4: an exact reviewed contract for the three Arcanum token-count histograms.
    private const string ArcanumTokenHistogramFields = """
        public static readonly Meter Meter = new("Arcanum", "0.1.0");

        public static readonly Histogram<long> EstimatedInputTokens = Meter.CreateHistogram<long>("arcanum_estimated_input_tokens", "{tokens}", "estimated");

        public static readonly Histogram<long> ProviderReportedInputTokens = Meter.CreateHistogram<long>("arcanum_provider_reported_input_tokens", "{tokens}", "reported");

        public static readonly Histogram<long> InputTokenEstimationVariance = Meter.CreateHistogram<long>("arcanum_input_token_estimation_variance", "{tokens}", "variance");

        public static readonly Histogram<long> Unreviewed = Meter.CreateHistogram<long>("arcanum_other_tokens", "{tokens}", "other");

        public static Histogram<long> Mutable = Meter.CreateHistogram<long>("arcanum_estimated_input_tokens", "{tokens}", "mutable");
        """;

    private static HostedProducerDiscovery<HostedProducerSite> DiscoverArcanumTokenHistogram(string body)
    {
        string helper = "namespace RetroDownfall.Arcanum.Core.Telemetry { public static class ArcanumMetrics { " + ArcanumTokenHistogramFields + " } } "
            + "internal sealed class TokenOperand { public static implicit operator long(TokenOperand value) { System.IO.File.Delete(\"token-operand-conversion\"); return 3; } }";

        string source = "using System.Diagnostics.Metrics; using System.Collections.Generic; using RetroDownfall.Arcanum.Core.Telemetry; "
            + R2Source(body + " await Task.CompletedTask;", helper);

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        compilation = compilation.RemoveReferences(compilation.References.Where(reference =>
            compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol { Name: "RetroDownfall.Arcanum.Core" }));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        return HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);
    }

    [Fact]
    public void ArcanumTokenHistogramsAcceptTheirExactOwnedRecordOverloads()
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverArcanumTokenHistogram(
            "KeyValuePair<string, object?> provider = new(\"provider\", \"p\"); KeyValuePair<string, object?> model = new(\"model\", \"m\"); "
            + "ArcanumMetrics.EstimatedInputTokens.Record(12L, provider, model); ArcanumMetrics.ProviderReportedInputTokens.Record(3L); "
            + "ArcanumMetrics.InputTokenEstimationVariance.Record(Math.Abs(-4L), provider, model, new KeyValuePair<string, object?>(\"direction\", \"exact\"));");

        Assert.Empty(result.Diagnostics);

        Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData("unreviewed-name", "ArcanumMetrics.Unreviewed.Record(1L);")]
    [InlineData("mutable-field", "ArcanumMetrics.Mutable.Record(1L);")]
    [InlineData("foreign-instrument", "Histogram<long> own = new Meter(\"Other\").CreateHistogram<long>(\"arcanum_estimated_input_tokens\"); own.Record(1L);")]
    [InlineData("tag-span-overload", "ArcanumMetrics.EstimatedInputTokens.Record(1L, new ReadOnlySpan<KeyValuePair<string, object?>>());")]
    [InlineData("boundary-conversion", "ArcanumMetrics.EstimatedInputTokens.Record(new TokenOperand());")]
    public void ArcanumTokenHistogramsRefuseEveryLookalike(string shape, string body)
    {
        Assert.NotEmpty(shape);

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverArcanumTokenHistogram(body);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Diagnostics.Metrics.Histogram`1.Record");

        if (shape == "boundary-conversion")
        {
            Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete");
        }
    }

    [Fact]
    public void ArcanumTokenHistogramTagsStillCarryTheAdmissionHandleEscapeProof()
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverArcanumTokenHistogram(
            R2Admission + "ArcanumMetrics.EstimatedInputTokens.Record(1L, new KeyValuePair<string, object?>(\"lease\", held));");

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_ADMISSION_HANDLE_ESCAPE"
            && diagnostic.Detail.StartsWith("System.Diagnostics.Metrics.Histogram`1.Record", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("owned", "ArcanumMetrics.EstimatedInputTokens.Record(7L, new KeyValuePair<string, object?>(\"provider\", \"p\"));", true)]
    [InlineData("unreviewed-name", "ArcanumMetrics.Unreviewed.Record(7L);", false)]
    public void ArcanumTokenHistogramsAreRecognizedAcrossTheOwningProjectBoundary(string shape, string body, bool accepted)
    {
        Assert.NotEmpty(shape);

        string core = "using System.Diagnostics.Metrics; namespace RetroDownfall.Arcanum.Core.Telemetry { public static class ArcanumMetrics { " + ArcanumTokenHistogramFields + " } }";

        // The platform set carries the real first-party assemblies, whose ArcanumMetrics would be ambiguous.
        MetadataReference[] platform = [.. FixturePlatformReferences.Value.Where(static reference =>
            Path.GetFileNameWithoutExtension(reference.Display)?.StartsWith("RetroDownfall.", StringComparison.Ordinal) != true)];

        // The owning project compiles against its own reference instances, so its framework symbols are
        // distinct from the consumer's, as the Core and Api compilations are in the production graph.
        MetadataReference[] ownerPlatform = [.. platform.Cast<PortableExecutableReference>().Select(static reference => MetadataReference.CreateFromFile(reference.FilePath!))];

        CSharpCompilation owner = CSharpCompilation.Create(
            "InventoryFixture.Owner",
            [CSharpSyntaxTree.ParseText(core, path: "src/Owner.cs")],
            ownerPlatform,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        Assert.Empty(owner.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        string source = "using System.Collections.Generic; using RetroDownfall.Arcanum.Core.Telemetry; " + R2Source(body + " await Task.CompletedTask;");

        CSharpCompilation consumer = CSharpCompilation.Create(
            "InventoryFixture",
            [CSharpSyntaxTree.ParseText(source, path: "src/Fixture.cs")],
            platform.Append(owner.ToMetadataReference()),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        Assert.Empty(consumer.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        // The instrument is declared in one compilation and recorded from another, as ModelCallExecutor
        // in Api records ArcanumMetrics from Core, so framework type identity is compared structurally.
        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [owner, consumer], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Equal(!accepted, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Diagnostics.Metrics.Histogram`1.Record"));
    }
}
