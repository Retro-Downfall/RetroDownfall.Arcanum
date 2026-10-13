using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RetroDownfall.Arcanum.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    public void ClaudeScratchStates()
    {
        string sites = ClosedValueRepeat("_ = DigestSource.Left != DigestSource.Right; ", 3);

        var (compilation, result, states) = ClosedValueDiscover(sites + R2Admission + sites, ClosedValueDigestHelper);

        output.WriteLine("DIAG " + string.Join(" | ", result.Diagnostics.Select(d => d.Code + ":" + d.Identity + ":" + d.Detail)));

        foreach (var s in states) output.WriteLine($"STATE {s.RootOperation} {s.Method} work={s.WorkFrontier} effect={s.EffectFrontier} rec={s.RecoveryEffectFrontier} env={s.EvaluationEnvironmentToken}");
    }

    [Fact]
    public void ClaudeScratchTuple()
    {
        string source = "#nullable enable\n" + R2Source("await Task.CompletedTask;", "public readonly record struct Probe((int, string) Field);");

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        INamedTypeSymbol probe = compilation.GetTypeByMetadataName("Probe")!;

        IFieldSymbol field = probe.GetMembers().OfType<IFieldSymbol>().First();

        var named = (INamedTypeSymbol)field.Type;

        output.WriteLine($"TUPLE istuple={named.IsTupleType} locs={named.Locations.Length} insrc={named.Locations.Any(l => l.IsInSource)} under={named.TupleUnderlyingType} underlocs={named.TupleUnderlyingType?.Locations.Length} asm={named.ContainingAssembly.Identity} key={named.OriginalDefinition.MetadataName} ns={named.ContainingNamespace}");
    }
}
