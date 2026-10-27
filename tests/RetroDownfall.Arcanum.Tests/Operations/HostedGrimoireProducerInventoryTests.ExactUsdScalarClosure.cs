using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    public void ExactUsdScalarActualArithmeticRetainsClosedFrameworkSemantics()
    {
        string path = Path.Combine(TestRepositoryPaths.RepositoryRoot(),
            "src/RetroDownfall.Arcanum.Infrastructure/Data/ExactUsdText.cs");

        string actual = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot()
            .DescendantNodes().OfType<ClassDeclarationSyntax>().Single().ToFullString();

        string helper = "namespace RetroDownfall.Arcanum.Infrastructure.Data { using System; using System.Data; "
            + "using System.Data.Common; using System.Globalization; using System.Numerics; using Microsoft.Data.Sqlite; " + actual + " }";

        CSharpCompilation compilation = CompileWithProductionReferencePack(
            R2Source("_ = RetroDownfall.Arcanum.Infrastructure.Data.ExactUsdText.CheckedAdd(1m, 0.00000001m); await Task.CompletedTask;", helper),
            "RetroDownfall.Arcanum.Infrastructure");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Empty(result.Diagnostics);

        Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData("compare", "System.Int128 value = 3; _ = value > 0; _ = value < 1; _ = value == 0;")]
    [InlineData("modulo", "System.Int128 value = 3; _ = value % 10;")]
    [InlineData("shifts", "System.Int128 value = 3; _ = value << 32; _ = value >> 32;")]
    [InlineData("bits", "System.Int128 value = 3; _ = value & 255; _ = value | 255;")]
    [InlineData("negate", "System.Int128 value = 3; _ = -value;")]
    [InlineData("unsigned-cast", "System.Int128 value = 3; _ = (uint)value;")]
    [InlineData("limits", "_ = System.Int128.MaxValue; _ = System.Int128.MinValue;")]
    [InlineData("compound", "System.Int128 value = 30; value *= 10; value /= 10;")]
    public void ExactUsdScalarFrameworkProfilesPreservePrimitiveOperands(string shape, string body)
    {
        Assert.NotEmpty(shape);

        CSharpCompilation compilation = Compile(R2Source(body + " await Task.CompletedTask;"));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Empty(result.Diagnostics);

        Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData("_ = value > (int)new ScalarConversionOperand();", "System.Int128.op_GreaterThan")]
    [InlineData("_ = value % (int)new ScalarConversionOperand();", "System.Int128.op_Modulus")]
    [InlineData("_ = value << (int)new ScalarConversionOperand();", "System.Int128.op_LeftShift")]
    [InlineData("_ = value & (int)new ScalarConversionOperand();", "System.Int128.op_BitwiseAnd")]
    [InlineData("_ = -((System.Int128)(int)new ScalarConversionOperand());", "System.Int128.op_UnaryNegation")]
    [InlineData("value *= (int)new ScalarConversionOperand();", "System.Int128.op_Multiply")]
    public void ExactUsdScalarAuthoredOperandConversionRemainsUnproved(string body, string callee)
    {
        CSharpCompilation compilation = Compile("using System.Linq; " + R2Source(
            "System.Int128 value = 3; " + body + " await Task.CompletedTask;", ScalarOperandHelper));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail == callee);

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete");
    }

    [Theory]
    [InlineData("_ = value > value;", "System.Int128.op_GreaterThan")]
    [InlineData("_ = value % value;", "System.Int128.op_Modulus")]
    [InlineData("_ = value << 1;", "System.Int128.op_LeftShift")]
    [InlineData("_ = value & value;", "System.Int128.op_BitwiseAnd")]
    [InlineData("_ = -value;", "System.Int128.op_UnaryNegation")]
    [InlineData("_ = (uint)value;", "System.Int128.op_Explicit")]
    [InlineData("_ = counter::System.Int128.MaxValue;", "System.Int128.MaxValue")]
    [InlineData("value *= value;", "System.Int128.op_Multiply")]
    public void ExactUsdScalarForeignMetadataIdentityNeverGrantsAFrameworkProfile(string body, string callee)
    {
        CSharpCompilation foreign = Compile("""
            namespace System
            {
                public readonly struct Int128
                {
                    private static Int128 Effect() { IO.File.Delete("NEVER-EXECUTED-COUNTERFEIT"); return default; }
                    public static bool operator >(Int128 left, Int128 right) { Effect(); return false; }
                    public static bool operator <(Int128 left, Int128 right) { Effect(); return false; }
                    public static Int128 operator %(Int128 left, Int128 right) => Effect();
                    public static Int128 operator <<(Int128 left, int right) => Effect();
                    public static Int128 operator &(Int128 left, Int128 right) => Effect();
                    public static Int128 operator -(Int128 value) => Effect();
                    public static Int128 operator *(Int128 left, Int128 right) => Effect();
                    public static explicit operator uint(Int128 value) { Effect(); return 0; }
                    public static Int128 MaxValue { get { return Effect(); } }
                }
            }
            """).WithAssemblyName("UntrustedScalarImage");

        using MemoryStream bytes = new();

        Assert.True(foreign.Emit(bytes).Success);

        PortableExecutableReference reference = MetadataReference.CreateFromImage(
            bytes.ToArray(), new MetadataReferenceProperties(aliases: ["counter"]));

        CSharpCompilation compilation = Compile("extern alias counter; " + R2Source(
            "counter::System.Int128 value = default; " + body + " await Task.CompletedTask;")).AddReferences(reference);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail == callee);
    }

    [Fact]
    public void ExactUsdScalarExplicitCastRetainsAnAuthoredIncomingConversion()
    {
        const string helper = "struct AccountingPayload { public static implicit operator System.Int128(AccountingPayload value) { System.IO.File.Delete(\"conversion-effect\"); return 1; } }";

        CSharpCompilation compilation = Compile(R2Source(
            "_ = (uint)(System.Int128)new AccountingPayload(); await Task.CompletedTask;", helper));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Int128.op_Explicit");

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete");
    }

    [Fact]
    public void ExactUsdScalarCompoundTargetRetainsAuthoredGetterAndSetterEffects()
    {
        const string helper = "static class AccountingTarget { public static System.Int128 Value { get { System.IO.File.Delete(\"getter-effect\"); return 1; } set { System.IO.File.Delete(\"setter-effect\"); } } }";

        CSharpCompilation compilation = Compile(R2Source(
            "AccountingTarget.Value *= 10; await Task.CompletedTask;", helper));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete" && site.EnclosingType == "AccountingTarget" && site.Member == "get_Value");

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete" && site.EnclosingType == "AccountingTarget" && site.Member == "set_Value");
    }
}
