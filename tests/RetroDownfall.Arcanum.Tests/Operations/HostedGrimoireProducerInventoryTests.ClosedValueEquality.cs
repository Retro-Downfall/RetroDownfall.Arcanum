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
}
