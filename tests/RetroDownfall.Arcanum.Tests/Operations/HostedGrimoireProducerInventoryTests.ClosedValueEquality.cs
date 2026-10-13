using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    // A CovenantDigest-shaped value: a byte[] field behind an authored typed Equals. The operands
    // come from static getters, so no operand is an exact construction and the compiler-generated
    // operator can only be certified by the closed-value field walk.
    private const string ClosedValueDigestHelper = """
        public readonly record struct DigestShape
        {
            private readonly byte[]? _bytes;

            public DigestShape(byte[] bytes) { _bytes = bytes; }

            public bool Equals(DigestShape other) => (_bytes ?? []).AsSpan().SequenceEqual(other._bytes ?? []);

            public override int GetHashCode() => _bytes is null ? 0 : _bytes.Length;
        }

        static class DigestSource
        {
            public static DigestShape Left => default;

            public static DigestShape Right => default;
        }
        """;

    private const string ClosedValueEffectfulDigestHelper = """
        public readonly record struct DigestShape
        {
            private readonly byte[]? _bytes;

            public DigestShape(byte[] bytes) { _bytes = bytes; }

            public bool Equals(DigestShape other)
            {
                System.IO.File.Delete("closed-value-equals-effect");

                return (_bytes ?? []).AsSpan().SequenceEqual(other._bytes ?? []);
            }

            public override int GetHashCode() => _bytes is null ? 0 : _bytes.Length;
        }

        static class DigestSource
        {
            public static DigestShape Left => default;

            public static DigestShape Right => default;
        }
        """;

    private const string ClosedValueLocals = "DigestShape left = DigestSource.Left; DigestShape right = DigestSource.Right; ";

    private static string ClosedValueRepeat(string statement, int count) =>
        string.Concat(Enumerable.Repeat(statement, count));

    private static (CSharpCompilation Compilation, HostedProducerDiscovery<HostedProducerSite> Result,
        HostedProducerAdmittedStateObservation[] States) ClosedValueDiscover(
            string body, string helper, params HostedProducerOperationEntry[] roots)
    {
        string source = "#nullable enable\n" + R2Source(body + " await Task.CompletedTask;", helper);

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        System.Collections.Concurrent.ConcurrentQueue<HostedProducerAdmittedStateObservation> observed = new();

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation],
            new(["Worker"], []),
            [new("Worker", roots.Length == 0 ? [OrdinaryRoot()] : roots)],
            [],
            admittedStateObserver: observed.Enqueue);

        return (compilation, result, [.. observed]);
    }

    private static HostedProducerAdmittedStateObservation[] ClosedValueStatesOf(
        CSharpCompilation compilation,
        HostedProducerAdmittedStateObservation[] states,
        string typeName,
        string methodName)
    {
        INamedTypeSymbol type = Assert.IsAssignableFrom<INamedTypeSymbol>(compilation.GetTypeByMetadataName(typeName));

        IMethodSymbol method = Assert.Single(type.GetMembers(methodName).OfType<IMethodSymbol>(), candidate =>
            !candidate.IsImplicitlyDeclared
            && candidate.Parameters.Length == 1
            && SymbolEqualityComparer.Default.Equals(candidate.Parameters[0].Type, type));

        string key = method.OriginalDefinition.GetDocumentationCommentId() ?? method.ToDisplayString();

        return states.Where(state => state.Method == key).ToArray();
    }

    [Fact]
    public void ClosedValueStructComparedAtManySitesYieldsOneEqualsStatePerFrontierPair()
    {
        // Locals keep each operand's lifetime provable, so the admitted comparisons keep their work.
        string sites = ClosedValueRepeat("_ = left != right; ", 10);

        var (compilation, result, states) = ClosedValueDiscover(
            ClosedValueLocals + sites + R2Admission + sites, ClosedValueDigestHelper);

        Assert.Empty(result.Diagnostics);

        HostedProducerAdmittedStateObservation[] equals = ClosedValueStatesOf(compilation, states, "DigestShape", "Equals");

        var pairs = equals.Select(static state => (state.WorkFrontier, state.EffectFrontier, state.RecoveryEffectFrontier)).Distinct().ToArray();

        // Ten comparisons before the lease and ten after it: exactly the unadmitted and the admitted frontier.
        Assert.Equal(2, pairs.Length);

        Assert.Equal(pairs.Length, equals.Length);

        Assert.Contains(equals, static state => state.WorkFrontier is null && state.EffectFrontier is null);

        Assert.Contains(equals, static state => state.WorkFrontier is not null && state.EffectFrontier is not null);
    }

    [Fact]
    public void ClosedValueEqualityStatesDoNotGrowWithTheNumberOfComparisonSites()
    {
        var (compilationFew, _, few) = ClosedValueDiscover(
            ClosedValueLocals + R2Admission + ClosedValueRepeat("_ = left == right; ", 2), ClosedValueDigestHelper);

        var (compilationMany, _, many) = ClosedValueDiscover(
            ClosedValueLocals + R2Admission + ClosedValueRepeat("_ = left == right; ", 20), ClosedValueDigestHelper);

        Assert.Equal(
            ClosedValueStatesOf(compilationFew, few, "DigestShape", "Equals").Length,
            ClosedValueStatesOf(compilationMany, many, "DigestShape", "Equals").Length);

        Assert.Equal(few.Length, many.Length);
    }

    [Fact]
    public void ClosedValueEqualityTraversesAuthoredEqualsUnderTheCallersWorkAndEffect()
    {
        var (compilation, result, states) = ClosedValueDiscover(
            ClosedValueLocals + R2Admission + "_ = left != right;", ClosedValueEffectfulDigestHelper);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED");

        HostedProducerSite delete = Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");

        Assert.NotNull(delete.WorkFrontierCapsuleId);

        Assert.NotNull(delete.EffectFrontierCapsuleId);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic =>
            diagnostic.Code is "HOSTED_SITE_WORK_FRONTIER_MISSING" or "HOSTED_SITE_EFFECT_FRONTIER_MISSING");

        Assert.Single(ClosedValueStatesOf(compilation, states, "DigestShape", "Equals"));
    }

    [Fact]
    public void ClosedValueEqualityStillReportsAnEffectInsideEqualsWithoutAdmission()
    {
        var (_, result, _) = ClosedValueDiscover(
            ClosedValueRepeat("_ = DigestSource.Left != DigestSource.Right; ", 3), ClosedValueEffectfulDigestHelper);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED");

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete");

        foreach (string code in new[] { "HOSTED_SITE_WORK_FRONTIER_MISSING", "HOSTED_SITE_EFFECT_FRONTIER_MISSING" })
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code
                && diagnostic.Detail == "System.IO.File.Delete"
                && diagnostic.Identity.Contains("/equals@", StringComparison.Ordinal));
    }

    [Fact]
    public void ClosedValueEqualityReportsTheSameEqualsUnderEachRoot()
    {
        const string helper = ClosedValueEffectfulDigestHelper + """

            static class SharedComparison
            {
                public static bool Differs() => DigestSource.Left != DigestSource.Right;
            }
            """;

        string source = "#nullable enable\n" + R2Source("_ = SharedComparison.Differs(); await Task.CompletedTask;", helper)
            .Replace("public Task StopAsync(CancellationToken token) => Task.CompletedTask;",
                "public async Task StopAsync(CancellationToken token) { _ = SharedComparison.Differs(); await Task.CompletedTask; }",
                StringComparison.Ordinal);

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerOperationEntry stop = OrdinaryRoot("Worker.StopAsync") with { Member = "StopAsync" };

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot(), stop])], []);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED");

        foreach (string root in new[] { "Worker.StartAsync", "Worker.StopAsync" })
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
                && diagnostic.Detail == "System.IO.File.Delete"
                && diagnostic.Identity.StartsWith(root + "/", StringComparison.Ordinal)
                && diagnostic.Identity.Contains("/equals@", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosedValueEqualityReachedFromAnOrdinaryRootStillEmitsDbEffectInsideADbOnlyRecoveryTuple(bool ordinaryFirst)
    {
        const string record = """

            namespace RetroDownfall.Arcanum.Infrastructure.Operations
            {
                public readonly record struct RecoveryDigest
                {
                    private readonly byte[]? _bytes;

                    public bool Equals(RecoveryDigest other)
                    {
                        System.IO.File.Delete("recovery-equals-effect");

                        return (_bytes ?? []).AsSpan().SequenceEqual(other._bytes ?? []);
                    }

                    public override int GetHashCode() => _bytes is null ? 0 : _bytes.Length;
                }

                internal static class RecoveryDigestSource
                {
                    internal static RecoveryDigest Left => default;

                    internal static RecoveryDigest Right => default;
                }
            }
            """;

        string source = RecoveryMatrixFixture()
            .Replace("_ = System.IO.File.Exists(\"db\");",
                "_ = RecoveryDigestSource.Left != RecoveryDigestSource.Right;", StringComparison.Ordinal)
            + record;

        if (ordinaryFirst)
        {
            source = source.Replace(
                " IDisposable effectGroup = null!; try {",
                " _ = RetroDownfall.Arcanum.Infrastructure.Operations.RecoveryDigestSource.Left != RetroDownfall.Arcanum.Infrastructure.Operations.RecoveryDigestSource.Right; IDisposable effectGroup = null!; try {",
                StringComparison.Ordinal);
        }

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverRecoveryMatrixFixture(source);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail.EndsWith(".op_Inequality", StringComparison.Ordinal));

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_RECOVERY_DB_EFFECT"
            && diagnostic.Identity.Contains("/equals@", StringComparison.Ordinal)
            && diagnostic.Detail.Contains("System.IO.File.Delete", StringComparison.Ordinal));
    }

    [Fact]
    public void ClosedValueOuterEqualityTraversesANestedAuthoredEquals()
    {
        const string helper = """
            public readonly record struct NestedDigest
            {
                private readonly int _value;

                public bool Equals(NestedDigest other)
                {
                    System.IO.File.Delete("nested-closed-value-effect");

                    return _value == other._value;
                }

                public override int GetHashCode() => _value;
            }

            public readonly record struct OuterEnvelope(NestedDigest Digest, Guid Id, int? Revision, string Name);

            static class EnvelopeSource
            {
                public static OuterEnvelope Left => default;

                public static OuterEnvelope Right => default;
            }
            """;

        var (_, result, _) = ClosedValueDiscover("_ = EnvelopeSource.Left == EnvelopeSource.Right;", helper);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED");

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete");

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
            && diagnostic.Detail == "System.IO.File.Delete");
    }

    [Theory]
    [InlineData("struct", "bool", true)]
    [InlineData("struct", "int", true)]
    [InlineData("struct", "ulong", true)]
    [InlineData("struct", "char", true)]
    [InlineData("struct", "double", true)]
    [InlineData("struct", "decimal", true)]
    [InlineData("struct", "string", true)]
    [InlineData("struct", "System.Guid", true)]
    [InlineData("struct", "System.DateTime", true)]
    [InlineData("struct", "System.DateTimeOffset", true)]
    [InlineData("struct", "System.TimeSpan", true)]
    [InlineData("struct", "System.Int128", true)]
    [InlineData("struct", "System.Index", true)]
    [InlineData("struct", "System.Range", true)]
    [InlineData("struct", "ProbeKind", true)]
    [InlineData("struct", "int?", true)]
    [InlineData("struct", "System.Guid?", true)]
    [InlineData("struct", "byte[]", true)]
    [InlineData("struct", "System.Collections.Immutable.ImmutableArray<int>", true)]
    [InlineData("struct", "(int, string)", true)]
    [InlineData("struct", "NestedStruct", true)]
    [InlineData("struct", "NestedStruct?", true)]
    [InlineData("struct", "NestedSealed", true)]
    [InlineData("class", "int", true)]
    [InlineData("class", "string", true)]
    [InlineData("class", "System.Guid", true)]
    [InlineData("class", "NestedStruct", true)]
    [InlineData("struct", "System.IDisposable", false)]
    [InlineData("struct", "object", false)]
    [InlineData("struct", "System.Func<int>", false)]
    [InlineData("struct", "System.Collections.Generic.IReadOnlyList<int>", false)]
    [InlineData("struct", "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease", false)]
    [InlineData("struct", "System.Collections.Generic.List<int>", false)]
    [InlineData("struct", "System.Type", false)]
    [InlineData("struct", "NestedUnsealed", false)]
    [InlineData("struct", "NestedPlainClass", false)]
    [InlineData("struct", "NestedPlainStruct", false)]
    [InlineData("struct", "NestedHolder", false)]
    [InlineData("struct", "(int, object)", false)]
    [InlineData("struct", "object[]", false)]
    [InlineData("struct", "System.Collections.Immutable.ImmutableArray<object>", false)]
    [InlineData("class", "System.IDisposable", false)]
    [InlineData("class", "object", false)]
    [InlineData("class", "System.Func<int>", false)]
    [InlineData("class", "System.Collections.Generic.IReadOnlyList<int>", false)]
    [InlineData("class", "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease", false)]
    public void ClosedValueFieldWalkCertifiesOnlyHandleIncapableLeaves(string kind, string fieldType, bool closed)
    {
        string declaration = kind == "struct"
            ? "public readonly record struct Probe(" + fieldType + " Field);"
            : "public sealed record class Probe(" + fieldType + " Field);";

        string getter = kind == "struct" ? "default" : "null!";

        string helper = "public enum ProbeKind { Zero, One } "
            + "public readonly record struct NestedStruct(int Number, System.Guid Id); "
            + "public sealed record class NestedSealed(string Name, ProbeKind Kind); "
            + "public record class NestedUnsealed(int Number); "
            + "public sealed class NestedPlainClass { public int Number; } "
            + "public readonly struct NestedPlainStruct { public readonly int Number; } "
            + "public readonly record struct NestedHolder(NestedUnsealed Inner); "
            + declaration
            + " static class ProbeSource { public static Probe Left => " + getter + "; public static Probe Right => " + getter + "; }";

        string source = "#nullable enable\n"
            + R2Source("_ = ProbeSource.Left == ProbeSource.Right; _ = ProbeSource.Left != ProbeSource.Right; await Task.CompletedTask;", helper);

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        bool unclassified = result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail is "Probe.op_Equality" or "Probe.op_Inequality");

        Assert.Equal(!closed, unclassified);

        if (closed) Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ClosedValueEqualityRefusesAnUnsealedRecordClass()
    {
        const string helper = """
            public record class UnsealedProbe(int Number);

            static class UnsealedSource
            {
                public static UnsealedProbe Left => null!;

                public static UnsealedProbe Right => null!;
            }
            """;

        var (_, result, _) = ClosedValueDiscover("_ = UnsealedSource.Left == UnsealedSource.Right;", helper);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "UnsealedProbe.op_Equality");
    }

    [Fact]
    public void ClosedValueEqualityRefusesAnAuthoredEqualityContract()
    {
        const string helper = """
            public sealed record class ContractProbe
            {
                public int Number { get; init; }

                private System.Type EqualityContract => typeof(string);
            }

            static class ContractSource
            {
                public static ContractProbe Left => null!;

                public static ContractProbe Right => null!;
            }
            """;

        var (_, result, _) = ClosedValueDiscover("_ = ContractSource.Left == ContractSource.Right;", helper);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "ContractProbe.op_Equality");
    }

    [Fact]
    public void ClosedValueEqualityRefusesAGenericRecord()
    {
        const string helper = """
            public readonly record struct GenericProbe<T>(int Number, T Value);

            static class GenericSource
            {
                public static GenericProbe<int> Left => default;

                public static GenericProbe<int> Right => default;
            }
            """;

        var (_, result, _) = ClosedValueDiscover("_ = GenericSource.Left == GenericSource.Right;", helper);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "GenericProbe`1.op_Equality");
    }

    [Fact]
    public void ClosedValueEqualityRefusesAnAuthoredGuidLookalike()
    {
        const string helper = """
            namespace System
            {
                public readonly struct Guid : System.IEquatable<Guid>
                {
                    public bool Equals(Guid other)
                    {
                        System.IO.File.Delete("guid-lookalike-effect");

                        return true;
                    }
                }
            }

            public readonly record struct LookalikeProbe(System.Guid Field);

            static class LookalikeSource
            {
                public static LookalikeProbe Left => default;

                public static LookalikeProbe Right => default;
            }
            """;

        string source = "#nullable enable\n" + R2Source("_ = LookalikeSource.Left == LookalikeSource.Right; await Task.CompletedTask;", helper);

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        INamedTypeSymbol guid = Assert.IsAssignableFrom<INamedTypeSymbol>(compilation.GetTypeByMetadataName("System.Guid"));

        Assert.NotEmpty(guid.DeclaringSyntaxReferences);

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "LookalikeProbe.op_Equality");
    }

    [Fact]
    public void ClosedValueEqualityCertifiesALiftedComparisonOfAClosedValueRecord()
    {
        const string helper = """
            public readonly record struct LiftedProbe
            {
                private readonly int _number;

                public bool Equals(LiftedProbe other)
                {
                    System.IO.File.Delete("lifted-equals-effect");

                    return _number == other._number;
                }

                public override int GetHashCode() => _number;
            }

            static class LiftedSource
            {
                public static LiftedProbe? Left => default;

                public static LiftedProbe? Right => default;
            }
            """;

        var (_, result, _) = ClosedValueDiscover("_ = LiftedSource.Left == LiftedSource.Right; _ = LiftedSource.Left != LiftedSource.Right;", helper);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED");

        // The authored Equals the lifted operator reaches is traversed, not skipped.
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
            && diagnostic.Detail == "System.IO.File.Delete"
            && diagnostic.Identity.Contains("/equals@", StringComparison.Ordinal));
    }

    [Fact]
    public void ClosedValueEqualityDoesNotCertifyALiftedComparisonOfAHandleCapableRecord()
    {
        const string helper = """
            public readonly record struct LiftedHandleProbe(int Number, System.IDisposable Handle);

            static class LiftedHandleSource
            {
                public static LiftedHandleProbe? Left => default;

                public static LiftedHandleProbe? Right => default;
            }
            """;

        var (_, result, _) = ClosedValueDiscover("_ = LiftedHandleSource.Left == LiftedHandleSource.Right;", helper);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "LiftedHandleProbe.op_Equality");
    }

    [Theory]
    [InlineData(6, 10, 10, true)]
    [InlineData(0, 0, 240, true)]
    [InlineData(0, 0, 300, false)]
    public void ClosedValueFieldWalkIsChargedToTheSharedProofBudgetAndFailsClosedWhenItIsSpent(int nested, int fieldsEach, int ownFields, bool certified)
    {
        // The authored typed Equals keeps exact-construction certification out of the way, so the closed-value
        // walk alone decides. It is charged once per type node to the shared per-site budget. Spending that
        // budget never certifies a record: the comparison keeps its unclassified diagnostic.
        string nestedMembers = string.Join(", ", Enumerable.Range(0, fieldsEach).Select(static index => "int F" + index));

        string nestedTypes = string.Concat(Enumerable.Range(0, nested).Select(index => "public sealed record class Nested" + index + "(" + nestedMembers + "); "));

        string members = string.Join(", ", Enumerable.Range(0, nested).Select(static index => "Nested" + index + " N" + index)
            .Concat(Enumerable.Range(0, ownFields).Select(static index => "int O" + index)));

        string helper = nestedTypes
            + "public readonly record struct Outer(" + members + ") { public bool Equals(Outer other) => O0 == other.O0 && N0 is null == other.N0 is null; public override int GetHashCode() => O0; } "
            + "static class WideSource { public static Outer Left => default; public static Outer Right => default; }";

        helper = helper.Replace("O0 == other.O0 && N0 is null == other.N0 is null", ownFields > 0 ? "O0 == other.O0" : "N0 == other.N0", StringComparison.Ordinal);

        var (_, result, _) = ClosedValueDiscover("_ = WideSource.Left == WideSource.Right;", helper);

        bool unclassified = result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "Outer.op_Equality");

        Assert.Equal(!certified, unclassified);
    }

    [Theory]
    [InlineData("System.Guid")]
    [InlineData("System.DateTimeOffset")]
    [InlineData("System.Int128")]
    [InlineData("System.Index")]
    [InlineData("System.TimeSpan")]
    [InlineData("System.UInt128")]
    [InlineData("System.Range")]
    public void ClosedValueEqualityRefusesALeafNamedLikeCoreLibFromAnUnreviewedAssembly(string leaf)
    {
        int split = leaf.LastIndexOf('.');

        string lookalike = "namespace " + leaf[..split] + " { public readonly struct " + leaf[(split + 1)..] + " : System.IEquatable<" + leaf[(split + 1)..] + "> { public bool Equals(" + leaf[(split + 1)..] + " other) => true; } }";

        CSharpCompilation foreign = CompileWithProductionReferencePack(lookalike, "RetroDownfall.Arcanum.Api").WithAssemblyName("Untrusted.Leaves");

        using MemoryStream image = new();

        Assert.True(foreign.Emit(image).Success);

        string source = "extern alias counterfeit;\n#nullable enable\n" + R2Source(
            "_ = LeafSource.Left == LeafSource.Right; await Task.CompletedTask;",
            "public readonly record struct LeafProbe(counterfeit::" + leaf + " Field); static class LeafSource { public static LeafProbe Left => default; public static LeafProbe Right => default; }");

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api")
            .AddReferences(MetadataReference.CreateFromImage(image.ToArray(), new MetadataReferenceProperties(aliases: ["counterfeit"])));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "LeafProbe.op_Equality");
    }

    // The container leaves (ImmutableArray, value tuples, Nullable) are matched by exact framework identity
    // too, so a same-named generic struct from another assembly is not a leaf and its record stays refused.
    [Theory]
    [InlineData("System.Collections.Immutable", "ImmutableArray<T>", "counterfeit::System.Collections.Immutable.ImmutableArray<int>", "")]
    [InlineData("System", "ValueTuple<T1, T2>", "counterfeit::System.ValueTuple<int, int>", "")]
    [InlineData("System", "Nullable<T>", "counterfeit::System.Nullable<int>", " where T : struct")]
    public void ClosedValueEqualityRefusesAContainerNamedLikeCoreLibFromAnUnreviewedAssembly(string container, string declaration, string field, string constraint)
    {
        string simple = declaration[..declaration.IndexOf('<', StringComparison.Ordinal)];

        string lookalike = "namespace " + container + " { public struct " + declaration + " : System.IEquatable<" + declaration + ">" + constraint
            + " { public bool Equals(" + declaration + " other) => true; } }";

        CSharpCompilation foreign = CompileWithProductionReferencePack(lookalike, "RetroDownfall.Arcanum.Api").WithAssemblyName("Untrusted.Containers");

        using MemoryStream image = new();

        Assert.True(foreign.Emit(image).Success, simple);

        string source = "extern alias counterfeit;\n#nullable enable\n" + R2Source(
            "_ = ContainerSource.Left == ContainerSource.Right; await Task.CompletedTask;",
            "public readonly record struct ContainerProbe(" + field + " Field); static class ContainerSource { public static ContainerProbe Left => default; public static ContainerProbe Right => default; }");

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api")
            .AddReferences(MetadataReference.CreateFromImage(image.ToArray(), new MetadataReferenceProperties(aliases: ["counterfeit"])));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "ContainerProbe.op_Equality");
    }

    // The leaves the rule accepts, each with a positive row so the twins above refuse a name, not a shape.
    [Theory]
    [InlineData("System.UInt128")]
    [InlineData("System.TimeSpan")]
    [InlineData("System.Range")]
    [InlineData("System.Collections.Immutable.ImmutableArray<int>")]
    [InlineData("(int, int)")]
    [InlineData("int?")]
    public void ClosedValueEqualityCertifiesTheExactFrameworkLeaves(string field)
    {
        var (_, result, _) = ClosedValueDiscover(
            "_ = ExactLeafSource.Left == ExactLeafSource.Right;",
            "public readonly record struct ExactLeafProbe(" + field + " Field); static class ExactLeafSource { public static ExactLeafProbe Left => default; public static ExactLeafProbe Right => default; }");

        Assert.Empty(result.Diagnostics);
    }

    // A record with static initialization is not a closed value: its static constructor, static field
    // initializers and static property initializers run once, when the type is first used, and nothing
    // traverses them. Closed-Value Equality, nested closed-value records and the record-struct Equals
    // lifetime bypass all rest on TryRecordEqualitySource refusing such a record, so each shape below must
    // keep its generated operators unclassified and must never certify an authored Equals under the
    // admitted Work or Effect. "none" is the control: the same fixture without the static member is certified.
    [Theory]
    [InlineData("struct", "none")]
    [InlineData("struct", "static-constructor")]
    [InlineData("struct", "static-field-initializer")]
    [InlineData("struct", "static-property-initializer")]
    [InlineData("class", "none")]
    [InlineData("class", "static-constructor")]
    [InlineData("class", "static-field-initializer")]
    [InlineData("class", "static-property-initializer")]
    [InlineData("nested-struct", "none")]
    [InlineData("nested-struct", "static-constructor")]
    [InlineData("nested-struct", "static-field-initializer")]
    [InlineData("nested-struct", "static-property-initializer")]
    [InlineData("nested-class", "none")]
    [InlineData("nested-class", "static-constructor")]
    [InlineData("nested-class", "static-field-initializer")]
    [InlineData("nested-class", "static-property-initializer")]
    public void ClosedValueEqualityRefusesARecordWithStaticInitializationAndNeverCertifiesItsEquals(string position, string member)
    {
        bool isClass = position.EndsWith("class", StringComparison.Ordinal);

        string name = position.StartsWith("nested", StringComparison.Ordinal) ? "StaticNested" : "StaticProbe";

        string staticMember = member switch
        {
            "static-constructor" => "static " + name + "() { System.IO.File.Delete(\"static-constructor-effect\"); } ",
            "static-field-initializer" => "private static readonly int Marker = StaticEffects.Touch(); ",
            "static-property-initializer" => "private static int Marker { get; } = StaticEffects.Touch(); ",
            _ => string.Empty,
        };

        // The authored typed Equals is effectful, so a certified comparison would traverse it canonically
        // under the caller's admitted Work and Effect and its File.Delete would carry both capsules.
        string record = isClass
            ? "public sealed record class " + name + " { public int Value { get; init; } " + staticMember
                + "public bool Equals(" + name + "? other) { System.IO.File.Delete(\"static-probe-equals\"); return other is not null && Value == other.Value; } public override int GetHashCode() => Value; }"
            : "public readonly record struct " + name + " { private readonly int _value; " + staticMember
                + "public bool Equals(" + name + " other) { System.IO.File.Delete(\"static-probe-equals\"); return _value == other._value; } public override int GetHashCode() => _value; }";

        bool nested = position.StartsWith("nested", StringComparison.Ordinal);

        string compared = nested ? "StaticOuter" : name;

        string outer = nested ? " public readonly record struct StaticOuter(" + name + " Inner, int Number);" : string.Empty;

        string nullable = isClass && !nested ? "null!" : "default";

        string helper = "static class StaticEffects { public static int Touch() { System.IO.File.Delete(\"static-initializer-effect\"); return 1; } } "
            + record + outer
            + " static class StaticSource { public static " + compared + " Left => " + nullable + "; public static " + compared + " Right => " + nullable + "; }";

        var (_, result, _) = ClosedValueDiscover(
            compared + " left = StaticSource.Left; " + compared + " right = StaticSource.Right; " + R2Admission + "_ = left == right; _ = left != right;", helper);

        bool certified = member == "none";

        foreach (string op in new[] { "op_Equality", "op_Inequality" })
        {
            Assert.Equal(!certified, result.Diagnostics.Any(diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail == compared + "." + op));
        }

        IEnumerable<HostedProducerSite> equalsEffects = result.Items.Where(static site => site.Callee == "System.IO.File.Delete"
            && site.WorkFrontierCapsuleId is not null && site.EffectFrontierCapsuleId is not null);

        if (certified)
        {
            Assert.Contains(equalsEffects, static site => site.Member == "Equals");
        }
        else
        {
            Assert.DoesNotContain(equalsEffects, static site => site.Member == "Equals");
        }
    }
}
