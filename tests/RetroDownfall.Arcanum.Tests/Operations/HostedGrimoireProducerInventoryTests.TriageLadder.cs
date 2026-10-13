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
}
