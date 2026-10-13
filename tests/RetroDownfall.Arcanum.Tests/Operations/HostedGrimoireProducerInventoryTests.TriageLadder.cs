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

        // The refusal is at the queue call itself. The authored work in a refused argument is reported
        // separately, so reporting it would otherwise satisfy this assertion whether or not the queue call
        // were refused.
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Collections.Generic.PriorityQueue`2.Enqueue");

        if (shape == "element-boundary-conversion")
        {
            Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete");
        }
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

        public static readonly Histogram<long> NameOnlyFactory = Meter.CreateHistogram<long>("arcanum_estimated_input_tokens");

        public static readonly Histogram<long> TaggedFactory = Meter.CreateHistogram<long>("arcanum_estimated_input_tokens", "{tokens}", "tagged", new KeyValuePair<string, object?>[] { new("kind", "tagged") });

        public static readonly Histogram<long> AdvisedFactory = Meter.CreateHistogram<long>("arcanum_estimated_input_tokens", "{tokens}", "advised", null, new InstrumentAdvice<long> { HistogramBucketBoundaries = [1, 2, 3] });

        public static string DynamicUnit = "{tokens}";

        public static readonly Histogram<long> DynamicUnitFactory = Meter.CreateHistogram<long>("arcanum_estimated_input_tokens", DynamicUnit, "dynamic");
        """;

    // Types a tag value could carry. The first-party Prometheus listener formats every tag value with
    // Convert.ToString while Record runs, so an authored ToString or IFormattable on a tag value is
    // executable work that no traversal of the call site reaches.
    private const string ArcanumTokenTagTypes = """
        internal sealed class TokenTagWithToString
        {
            public override string ToString()
            {
                System.IO.File.Delete("token-tag-tostring");

                return "tag";
            }
        }

        internal struct TokenTagFormattable : IFormattable
        {
            public string ToString(string? format, IFormatProvider? provider)
            {
                System.IO.File.Delete("token-tag-formattable");

                return "tag";
            }
        }

        internal sealed class TokenTagConversion
        {
            public static implicit operator string(TokenTagConversion value)
            {
                System.IO.File.Delete("token-tag-conversion");

                return "tag";
            }
        }

        internal static class TokenTagSource
        {
            public static KeyValuePair<string, object?> Tag() => new("k", "v");

            public static string Key() => "k";

            public static string Text() => "text";

            public static extern void Replace(ref KeyValuePair<string, object?> tag);
        }
        """;

    private static HostedProducerDiscovery<HostedProducerSite> DiscoverArcanumTokenHistogram(string body)
    {
        string helper = "namespace RetroDownfall.Arcanum.Core.Telemetry { public static class ArcanumMetrics { " + ArcanumTokenHistogramFields + " } } "
            + "internal sealed class TokenOperand { public static implicit operator long(TokenOperand value) { System.IO.File.Delete(\"token-operand-conversion\"); return 3; } } "
            + ArcanumTokenTagTypes;

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
    // The first-party listener formats every tag value while Record runs, so a tag value that is not
    // statically a string (or a null or string constant) can run an authored ToString or IFormattable
    // that no traversal of this call site reaches.
    [InlineData("tag-authored-tostring", "ArcanumMetrics.EstimatedInputTokens.Record(1L, new KeyValuePair<string, object?>(\"k\", new TokenTagWithToString()));")]
    [InlineData("tag-formattable-struct", "ArcanumMetrics.EstimatedInputTokens.Record(1L, new KeyValuePair<string, object?>(\"k\", new TokenTagFormattable()));")]
    [InlineData("tag-object-typed", "object value = TokenTagSource.Text(); ArcanumMetrics.EstimatedInputTokens.Record(1L, new KeyValuePair<string, object?>(\"k\", value));")]
    [InlineData("tag-object-typed-local-tag", "object value = TokenTagSource.Text(); KeyValuePair<string, object?> tag = new(\"k\", value); ArcanumMetrics.EstimatedInputTokens.Record(1L, tag);")]
    [InlineData("tag-boxed-value", "ArcanumMetrics.EstimatedInputTokens.Record(1L, new KeyValuePair<string, object?>(\"k\", 5));")]
    [InlineData("tag-user-conversion-to-string", "ArcanumMetrics.EstimatedInputTokens.Record(1L, new KeyValuePair<string, object?>(\"k\", (string)new TokenTagConversion()));")]
    [InlineData("tag-reassigned-local", "KeyValuePair<string, object?> tag = new(\"k\", \"a\"); tag = new(\"k\", new TokenTagWithToString()); ArcanumMetrics.EstimatedInputTokens.Record(1L, tag);")]
    [InlineData("tag-local-from-call", "KeyValuePair<string, object?> tag = TokenTagSource.Tag(); ArcanumMetrics.EstimatedInputTokens.Record(1L, tag);")]
    [InlineData("tag-call-result", "ArcanumMetrics.EstimatedInputTokens.Record(1L, TokenTagSource.Tag());")]
    [InlineData("tag-by-reference-local", "KeyValuePair<string, object?> tag = new(\"k\", \"a\"); TokenTagSource.Replace(ref tag); ArcanumMetrics.EstimatedInputTokens.Record(1L, tag);")]
    [InlineData("tag-non-constant-key", "ArcanumMetrics.EstimatedInputTokens.Record(1L, new KeyValuePair<string, object?>(TokenTagSource.Key(), \"v\"));")]
    [InlineData("tag-default-struct", "ArcanumMetrics.EstimatedInputTokens.Record(1L, default(KeyValuePair<string, object?>));")]
    // The factory must be the exact (name, unit, description) overload with constant arguments; the
    // instrument-tags and advice overloads, a name-only call and a non-constant argument are refused.
    [InlineData("factory-name-only", "ArcanumMetrics.NameOnlyFactory.Record(1L);")]
    [InlineData("factory-instrument-tags", "ArcanumMetrics.TaggedFactory.Record(1L);")]
    [InlineData("factory-advice", "ArcanumMetrics.AdvisedFactory.Record(1L);")]
    [InlineData("factory-non-constant-unit", "ArcanumMetrics.DynamicUnitFactory.Record(1L);")]
    public void ArcanumTokenHistogramsRefuseEveryLookalike(string shape, string body)
    {
        Assert.NotEmpty(shape);

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverArcanumTokenHistogram(body);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Diagnostics.Metrics.Histogram`1.Record");

        if (shape is "boundary-conversion" or "tag-user-conversion-to-string")
        {
            // The authored conversion is still traversed as ordinary executable work where it is written.
            Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete");
        }
    }

    [Theory]
    [InlineData("string-literal", "ArcanumMetrics.EstimatedInputTokens.Record(1L, new KeyValuePair<string, object?>(\"k\", \"v\"));")]
    [InlineData("string-call", "ArcanumMetrics.EstimatedInputTokens.Record(1L, new KeyValuePair<string, object?>(\"k\", TokenTagSource.Text()));")]
    [InlineData("string-local", "string text = TokenTagSource.Text(); ArcanumMetrics.EstimatedInputTokens.Record(1L, new KeyValuePair<string, object?>(\"k\", text));")]
    [InlineData("null-constant", "ArcanumMetrics.EstimatedInputTokens.Record(1L, new KeyValuePair<string, object?>(\"k\", null));")]
    [InlineData("string-conditional", "ArcanumMetrics.EstimatedInputTokens.Record(1L, new KeyValuePair<string, object?>(\"k\", token.CanBeCanceled ? \"a\" : token.IsCancellationRequested ? \"b\" : \"c\"));")]
    [InlineData("constant-key", "const string key = \"k\"; ArcanumMetrics.EstimatedInputTokens.Record(1L, new KeyValuePair<string, object?>(key, \"v\"));")]
    [InlineData("local-tag", "string text = TokenTagSource.Text(); KeyValuePair<string, object?> tag = new(\"k\", text); ArcanumMetrics.EstimatedInputTokens.Record(1L, tag, tag);")]
    [InlineData("explicit-object-cast", "ArcanumMetrics.EstimatedInputTokens.Record(1L, new KeyValuePair<string, object?>(\"k\", (object?)TokenTagSource.Text()));")]
    public void ArcanumTokenHistogramsAcceptTagsWhoseValueIsStaticallyAStringOrNull(string shape, string body)
    {
        Assert.NotEmpty(shape);

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverArcanumTokenHistogram(body);

        Assert.Empty(result.Diagnostics);

        Assert.Empty(result.Items);
    }

    // The exact shape of ModelCallExecutor: string properties as tag values, locals holding the provider and
    // model tags, and a conditional of string literals for the direction tag.
    [Fact]
    public void ArcanumTokenHistogramsAcceptTheProductionRecordingShape()
    {
        const string helper = """
            internal sealed record TokenProvider(string Name);

            internal sealed record TokenCallContext(TokenProvider Provider, string Model);

            internal sealed record TokenReconciliation(long? ProviderReportedInputTokens, long? EstimationVarianceTokens, bool? ProviderReportedInputValid);

            internal static class TokenRecording
            {
                public static void RecordEstimatedInput(long inputTokens, TokenCallContext context) =>
                    RetroDownfall.Arcanum.Core.Telemetry.ArcanumMetrics.EstimatedInputTokens.Record(
                        inputTokens,
                        new KeyValuePair<string, object?>("provider", context.Provider.Name),
                        new KeyValuePair<string, object?>("model", context.Model));

                public static void RecordReconciled(TokenReconciliation reconciled, TokenCallContext context)
                {
                    KeyValuePair<string, object?> providerTag = new("provider", context.Provider.Name);
                    KeyValuePair<string, object?> modelTag = new("model", context.Model);

                    if (reconciled.ProviderReportedInputTokens is long reported && reported >= 0)
                    {
                        RetroDownfall.Arcanum.Core.Telemetry.ArcanumMetrics.ProviderReportedInputTokens.Record(
                            reported,
                            providerTag,
                            modelTag);
                    }

                    long variance = reconciled.EstimationVarianceTokens ?? 0;

                    RetroDownfall.Arcanum.Core.Telemetry.ArcanumMetrics.InputTokenEstimationVariance.Record(
                        variance == long.MinValue ? long.MaxValue : Math.Abs(variance),
                        providerTag,
                        modelTag,
                        new KeyValuePair<string, object?>(
                            "direction",
                            reconciled.ProviderReportedInputValid == false
                                ? "inconsistent"
                                : variance > 0
                                    ? "underestimated"
                                    : variance < 0
                                        ? "overestimated"
                                        : "exact"));
                }
            }
            """;

        string fields = ArcanumTokenHistogramFields;

        string source = "using System.Diagnostics.Metrics; using System.Collections.Generic; "
            + R2Source("TokenRecording.RecordEstimatedInput(3L, new TokenCallContext(new TokenProvider(\"p\"), \"m\")); "
                + "TokenRecording.RecordReconciled(new TokenReconciliation(3L, -2L, true), new TokenCallContext(new TokenProvider(\"p\"), \"m\")); await Task.CompletedTask;",
                "namespace RetroDownfall.Arcanum.Core.Telemetry { public static class ArcanumMetrics { " + fields + " } } " + helper);

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        compilation = compilation.RemoveReferences(compilation.References.Where(reference =>
            compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol { Name: "RetroDownfall.Arcanum.Core" }));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Empty(result.Diagnostics);

        Assert.Empty(result.Items);
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
    [InlineData("object-typed-tag", "object value = \"p\"; ArcanumMetrics.EstimatedInputTokens.Record(7L, new KeyValuePair<string, object?>(\"provider\", value));", false)]
    [InlineData("instrument-tags-factory", "ArcanumMetrics.TaggedFactory.Record(7L);", false)]
    public void ArcanumTokenHistogramsAreRecognizedAcrossTheOwningProjectBoundary(string shape, string body, bool accepted)
    {
        Assert.NotEmpty(shape);

        string core = "using System.Collections.Generic; using System.Diagnostics.Metrics; namespace RetroDownfall.Arcanum.Core.Telemetry { public static class ArcanumMetrics { " + ArcanumTokenHistogramFields + " } }";

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
