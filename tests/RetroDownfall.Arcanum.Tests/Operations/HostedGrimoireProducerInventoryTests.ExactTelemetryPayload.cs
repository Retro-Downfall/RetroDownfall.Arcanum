using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.CodeAnalysis.Operations;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactFrameworkKeyValuePairTelemetryDistinguishesScalarAdmissionAncestryFromHandlePayload(bool actualHandle)
    {
        string helper = """
            static class Telemetry
            {
                private static readonly System.Diagnostics.Metrics.Counter<long> Counter = null!;

                public static void RecordScalar(string kind, string outcome) =>
                    Counter.Add(
                        1,
                        new System.Collections.Generic.KeyValuePair<string, object?>("kind", kind),
                        new System.Collections.Generic.KeyValuePair<string, object?>("outcome", outcome));

                public static void RecordHandle(IDisposable kind, string outcome) =>
                    Counter.Add(
                        1,
                        new System.Collections.Generic.KeyValuePair<string, object?>("kind", kind),
                        new System.Collections.Generic.KeyValuePair<string, object?>("outcome", outcome));
            }
            """;

        string body = R2Admission
            + "string kind = lease is not null ? \"known\" : \"unknown\"; "
            + (actualHandle
                ? "Telemetry.RecordHandle(held, \"cas_lost\");"
                : "Telemetry.RecordScalar(kind, \"cas_lost\");");

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(body, helper));

        Assert.Equal(
            actualHandle,
            result.Diagnostics.Any(static diagnostic =>
                diagnostic.Code == "HOSTED_ADMISSION_HANDLE_ESCAPE"
                && diagnostic.Detail.StartsWith("System.Diagnostics.Metrics.Counter`1.Add;", StringComparison.Ordinal)));
    }

    // The payload is the value a pair carries. A handle payload (the held group) may not be erased by any shape of pair. A
    // string derived from the lease is closed pure data: it cannot be or hold the handle, so in the same shapes it carries
    // no origin at all (Handle-Incapable Values) and the consumer is not an escape.
    [Theory]
    [InlineData("unknown-object-formal", true)]
    [InlineData("pair-alias", true)]
    [InlineData("selected-user-conversion", true)]
    [InlineData("unknown-object-formal", false)]
    [InlineData("pair-alias", false)]
    public void ExactFrameworkPairPayloadRefinementRetainsUnprovedPayloadEscapes(string shape, bool handle)
    {
        string member = shape switch
        {
            "unknown-object-formal" => """
                public static void Record(string kind, object unknown) => Counter.Add(
                    1,
                    new System.Collections.Generic.KeyValuePair<string, object?>(kind, unknown),
                    new System.Collections.Generic.KeyValuePair<string, object?>("outcome", "cas_lost"));
                """,
            "pair-alias" => """
                public static void Record(PAYLOAD kind, string outcome)
                {
                    var pair = new System.Collections.Generic.KeyValuePair<string, object?>("kind", kind);
                    Counter.Add(1, pair, new System.Collections.Generic.KeyValuePair<string, object?>("outcome", outcome));
                }
                """.Replace("PAYLOAD", handle ? "IDisposable" : "string", StringComparison.Ordinal),
            "selected-user-conversion" => """
                public static void Record(Scalar kind, string outcome) => Counter.Add(
                    1,
                    new System.Collections.Generic.KeyValuePair<string, object?>(kind, outcome),
                    new System.Collections.Generic.KeyValuePair<string, object?>("outcome", "cas_lost"));
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        string extra = shape == "selected-user-conversion" ? """
            sealed class Scalar(IDisposable source)
            {
                private readonly IDisposable _source = source;

                public static implicit operator string(Scalar value) => "kind";
            }
            """ : string.Empty;

        string helper = $$"""
            static class Telemetry
            {
                private static readonly System.Diagnostics.Metrics.Counter<long> Counter = null!;

                {{member}}
            }
            {{extra}}
            """;

        string call = shape switch
        {
            "unknown-object-formal" => handle ? "Telemetry.Record(kind, held);" : "Telemetry.Record(kind, (object)kind);",
            "pair-alias" => handle ? "Telemetry.Record(held, \"cas_lost\");" : "Telemetry.Record(kind, \"cas_lost\");",
            "selected-user-conversion" => "Telemetry.Record(new Scalar(held), kind);",
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        string source = R2Source(
            R2Admission + "string kind = lease is not null ? \"known\" : \"unknown\"; " + call,
            helper);

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SemanticModel model = compilation.GetSemanticModel(compilation.SyntaxTrees.Single());

        InvocationExpressionSyntax consumer = compilation.SyntaxTrees.Single().GetRoot().DescendantNodes()
            .OfType<InvocationExpressionSyntax>().Single(static invocation => invocation.Expression.ToString() == "Counter.Add");

        IInvocationOperation selected = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(consumer));

        Assert.Equal("Add", selected.TargetMethod.Name);

        Assert.Equal("Counter`1", selected.TargetMethod.ContainingType.MetadataName);

        Assert.Equal("System.Diagnostics.Metrics", selected.TargetMethod.ContainingNamespace.ToDisplayString());

        Assert.Equal(typeof(System.Diagnostics.Metrics.Counter<>).Assembly.GetName().Name, selected.TargetMethod.ContainingAssembly.Name);

        Assert.All(selected.Arguments, static argument => Assert.Equal(RefKind.None, argument.Parameter!.RefKind));

        if (shape == "pair-alias")
        {
            ILocalReferenceOperation alias = Assert.IsAssignableFrom<ILocalReferenceOperation>(selected.Arguments[1].Value);

            Assert.Equal("pair", alias.Local.Name);

            VariableDeclaratorSyntax declaration = Assert.IsType<VariableDeclaratorSyntax>(alias.Local.DeclaringSyntaxReferences.Single().GetSyntax());

            IObjectCreationOperation pair = Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(declaration.Initializer!.Value));

            AssertExactTelemetryFrameworkPair(pair);

            IConversionOperation payload = Assert.IsAssignableFrom<IConversionOperation>(pair.Arguments[1].Value);

            Assert.False(payload.Conversion.IsUserDefined);

            Assert.Equal(handle ? SpecialType.System_IDisposable : SpecialType.System_String, payload.Operand.Type!.SpecialType);
        }
        else
        {
            IObjectCreationOperation pair = Assert.IsAssignableFrom<IObjectCreationOperation>(selected.Arguments[1].Value);

            AssertExactTelemetryFrameworkPair(pair);

            if (shape == "unknown-object-formal")
            {
                IParameterReferenceOperation unknown = Assert.IsAssignableFrom<IParameterReferenceOperation>(pair.Arguments[1].Value);

                Assert.Equal("unknown", unknown.Parameter.Name);

                Assert.Equal(SpecialType.System_Object, unknown.Type!.SpecialType);
            }
            else
            {
                IConversionOperation conversion = Assert.IsAssignableFrom<IConversionOperation>(pair.Arguments[0].Value);

                Assert.True(conversion.IsImplicit && conversion.Conversion.IsUserDefined);

                Assert.Equal("op_Implicit", conversion.OperatorMethod!.MetadataName);

                Assert.Single(conversion.OperatorMethod.DeclaringSyntaxReferences);

                Assert.Equal(SpecialType.System_String, conversion.Type!.SpecialType);

                IParameterReferenceOperation operand = Assert.IsAssignableFrom<IParameterReferenceOperation>(conversion.Operand);

                Assert.Equal("kind", operand.Parameter.Name);

                Assert.Equal("Scalar", operand.Type!.Name);
            }
        }

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        if (handle)
        {
            AssertExactTelemetryConsumerEscape(result, consumer, "System.Diagnostics.Metrics.Counter`1.Add;");
        }
        else
        {
            AssertExactTelemetryConsumerIsNotAnEscape(result, consumer);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExactNamedAuthoredPairCannotUseFrameworkPayloadRefinement(bool handle)
    {
        const string helper = """
            namespace System.Collections.Generic
            {
                public readonly struct KeyValuePair<TKey, TValue>
                {
                    private readonly TKey _key;

                    private readonly TValue _value;

                    public KeyValuePair(TKey key, TValue value)
                    {
                        _key = key;

                        _value = value;
                    }
                }
            }
            """;

        string source = R2Source(
            R2Admission + "string kind = lease is not null ? \"known\" : \"unknown\"; "
                + "GC.KeepAlive(new System.Collections.Generic.KeyValuePair<string, object?>(\"kind\", " + (handle ? "held" : "kind") + "));",
            helper);

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SemanticModel model = compilation.GetSemanticModel(compilation.SyntaxTrees.Single());

        InvocationExpressionSyntax consumer = compilation.SyntaxTrees.Single().GetRoot().DescendantNodes()
            .OfType<InvocationExpressionSyntax>().Single(static invocation => invocation.Expression.ToString() == "GC.KeepAlive");

        IInvocationOperation selected = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(consumer));

        Assert.Equal("KeepAlive", selected.TargetMethod.Name);

        Assert.Equal(SpecialType.System_Object, selected.TargetMethod.Parameters.Single().Type.SpecialType);

        Assert.Equal(typeof(GC).Assembly.GetName().Name, selected.TargetMethod.ContainingAssembly.Name);

        IConversionOperation boxing = Assert.IsAssignableFrom<IConversionOperation>(selected.Arguments.Single().Value);

        Assert.False(boxing.Conversion.IsUserDefined);

        IObjectCreationOperation pair = Assert.IsAssignableFrom<IObjectCreationOperation>(boxing.Operand);

        Assert.Equal("KeyValuePair`2", pair.Constructor!.ContainingType.MetadataName);

        Assert.Equal("System.Collections.Generic", pair.Constructor.ContainingNamespace.ToDisplayString());

        Assert.Equal(compilation.Assembly.Identity, pair.Constructor.ContainingAssembly.Identity);

        Assert.Single(pair.Constructor.DeclaringSyntaxReferences);

        Assert.Contains(pair.Constructor.Locations, static location => location.IsInSource);

        Assert.Equal(SpecialType.System_String, pair.Constructor.Parameters[0].Type.SpecialType);

        Assert.Equal(SpecialType.System_Object, pair.Constructor.Parameters[1].Type.SpecialType);

        IConversionOperation payload = Assert.IsAssignableFrom<IConversionOperation>(pair.Arguments[1].Value);

        Assert.False(payload.Conversion.IsUserDefined);

        Assert.Equal(handle ? SpecialType.System_IDisposable : SpecialType.System_String, payload.Operand.Type!.SpecialType);

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        // An authored pair named like the framework one earns no payload refinement: a handle payload escapes through it. A
        // string payload needs none, being closed pure data.
        if (handle)
        {
            AssertExactTelemetryConsumerEscape(result, consumer, "System.GC.KeepAlive;");
        }
        else
        {
            AssertExactTelemetryConsumerIsNotAnEscape(result, consumer);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExactPairOuterUserConversionCannotEraseDisposableHandleCarrier(bool handle)
    {
        const string helper = """
            sealed class LeaseBox : IDisposable
            {
                public static IDisposable Captured = null!;

                private readonly IDisposable _handle;

                private readonly System.Collections.Generic.KeyValuePair<string, object?> _payload;

                private LeaseBox(IDisposable handle, System.Collections.Generic.KeyValuePair<string, object?> payload)
                {
                    _handle = handle;

                    _payload = payload;
                }

                public static explicit operator LeaseBox(System.Collections.Generic.KeyValuePair<string, object?> payload) =>
                    new LeaseBox(Captured, payload);

                public void Dispose() => _handle.Dispose();
            }

            static class Telemetry
            {
                public static void Record(string kind, IDisposable held)
                {
                    LeaseBox.Captured = held;

                    GC.KeepAlive((LeaseBox)new System.Collections.Generic.KeyValuePair<string, object?>("kind", PAYLOAD));
                }
            }
            """;

        string source = R2Source(
            R2Admission + "string kind = lease is not null ? \"known\" : \"unknown\"; Telemetry.Record(kind, held); System.IO.File.Delete(\"after-capture\");",
            helper.Replace("PAYLOAD", handle ? "held" : "kind", StringComparison.Ordinal));

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SemanticModel model = compilation.GetSemanticModel(compilation.SyntaxTrees.Single());

        InvocationExpressionSyntax consumer = compilation.SyntaxTrees.Single().GetRoot().DescendantNodes()
            .OfType<InvocationExpressionSyntax>().Single(static invocation => invocation.Expression.ToString() == "GC.KeepAlive");

        IInvocationOperation selected = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(consumer));

        Assert.Equal("KeepAlive", selected.TargetMethod.Name);

        Assert.Equal(typeof(GC).Assembly.GetName().Name, selected.TargetMethod.ContainingAssembly.Name);

        IConversionOperation upcast = Assert.IsAssignableFrom<IConversionOperation>(selected.Arguments.Single().Value);

        Assert.True(upcast.IsImplicit && upcast.Conversion.IsReference && !upcast.Conversion.IsUserDefined);

        IConversionOperation outer = Assert.IsAssignableFrom<IConversionOperation>(upcast.Operand);

        Assert.False(outer.IsImplicit);

        Assert.True(outer.Conversion.IsUserDefined);

        Assert.Equal("op_Explicit", outer.OperatorMethod!.MetadataName);

        Assert.Single(outer.OperatorMethod.DeclaringSyntaxReferences);

        INamedTypeSymbol carrierType = Assert.IsAssignableFrom<INamedTypeSymbol>(outer.Type);

        Assert.Contains(carrierType.AllInterfaces, static contract => contract.SpecialType == SpecialType.System_IDisposable);

        IObjectCreationOperation pair = Assert.IsAssignableFrom<IObjectCreationOperation>(outer.Operand);

        AssertExactTelemetryFrameworkPair(pair);

        IConversionOperation payload = Assert.IsAssignableFrom<IConversionOperation>(pair.Arguments[1].Value);

        Assert.False(payload.Conversion.IsUserDefined);

        Assert.Equal(handle ? SpecialType.System_IDisposable : SpecialType.System_String, payload.Operand.Type!.SpecialType);

        CastExpressionSyntax cast = Assert.IsType<CastExpressionSyntax>(outer.Syntax);

        Assert.Equal("LeaseBox", model.GetTypeInfo(cast).Type!.Name);

        Assert.Same(pair.Syntax.SyntaxTree, cast.Expression.SyntaxTree);

        Assert.Equal(pair.Syntax.Span, cast.Expression.Span);

        AssignmentExpressionSyntax capture = compilation.SyntaxTrees.Single().GetRoot().DescendantNodes()
            .OfType<AssignmentExpressionSyntax>().Single(static assignment => assignment.Left.ToString() == "LeaseBox.Captured");

        IFieldSymbol captured = Assert.IsAssignableFrom<IFieldSymbol>(model.GetSymbolInfo(capture.Left).Symbol);

        IParameterSymbol admittedFormal = Assert.IsAssignableFrom<IParameterSymbol>(model.GetSymbolInfo(capture.Right).Symbol);

        Assert.Equal("held", admittedFormal.Name);

        Assert.Equal(SpecialType.System_IDisposable, admittedFormal.Type.SpecialType);

        InvocationExpressionSyntax handoff = compilation.SyntaxTrees.Single().GetRoot().DescendantNodes()
            .OfType<InvocationExpressionSyntax>().Single(static invocation => invocation.Expression.ToString() == "Telemetry.Record");

        IInvocationOperation handoffCall = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(handoff));

        Assert.True(SymbolEqualityComparer.Default.Equals(handoffCall.TargetMethod.Parameters[1], admittedFormal));

        ILocalReferenceOperation admittedLocal = Assert.IsAssignableFrom<ILocalReferenceOperation>(handoffCall.Arguments[1].Value);

        Assert.Equal("held", admittedLocal.Local.Name);

        Assert.Equal(SpecialType.System_IDisposable, admittedLocal.Type!.SpecialType);

        ObjectCreationExpressionSyntax carrierCreation = outer.OperatorMethod.DeclaringSyntaxReferences.Single().GetSyntax()
            .DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Single();

        IObjectCreationOperation carrier = Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(carrierCreation));

        IFieldReferenceOperation retained = Assert.IsAssignableFrom<IFieldReferenceOperation>(carrier.Arguments[0].Value);

        Assert.True(SymbolEqualityComparer.Default.Equals(captured, retained.Field));

        Assert.Equal("LeaseBox", carrier.Type!.Name);

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        // A handle payload is the pair's own carrier, so the consumer of the converted value is an escape. A string payload is
        // closed pure data and makes no claim about the consumer: the converted LeaseBox is a real carrier, because its
        // operator reads the group back from the static, but it reaches the consumer through the static, not through the
        // payload. That defect is refused where it happens, at the store, for either payload.
        if (handle)
        {
            AssertExactTelemetryConsumerEscape(result, consumer, "System.GC.KeepAlive;");
        }

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_ADMISSION_HANDLE_ESCAPE"
            && diagnostic.Identity.EndsWith("/store@" + capture.SyntaxTree.FilePath + ":" + capture.SpanStart, StringComparison.Ordinal)
            && diagnostic.Detail.StartsWith("LeaseBox.Captured;", StringComparison.Ordinal));

        // The held group stored in a static also ends its lifetime at that store, so the send after the call loses its Effect.
        InvocationExpressionSyntax send = compilation.SyntaxTrees.Single().GetRoot().DescendantNodes()
            .OfType<InvocationExpressionSyntax>().Single(static invocation => invocation.Expression.ToString() == "System.IO.File.Delete");

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"
            && diagnostic.Identity.EndsWith("/site@" + send.SyntaxTree.FilePath + ":" + send.SpanStart, StringComparison.Ordinal));
    }

    private static void AssertExactTelemetryFrameworkPair(IObjectCreationOperation pair)
    {
        Assert.Equal("KeyValuePair`2", pair.Constructor!.ContainingType.MetadataName);

        Assert.Equal("System.Collections.Generic", pair.Constructor.ContainingNamespace.ToDisplayString());

        Assert.Equal(typeof(System.Collections.Generic.KeyValuePair<,>).Assembly.GetName().Name, pair.Constructor.ContainingAssembly.Name);

        Assert.DoesNotContain(pair.Constructor.Locations, static location => location.IsInSource);

        Assert.Equal(SpecialType.System_String, pair.Constructor.Parameters[0].Type.SpecialType);

        Assert.Equal(SpecialType.System_Object, pair.Constructor.Parameters[1].Type.SpecialType);

        Assert.Equal(2, pair.Arguments.Length);

        Assert.All(pair.Arguments, static argument => Assert.Equal(RefKind.None, argument.Parameter!.RefKind));
    }

    private static void AssertExactTelemetryConsumerIsNotAnEscape(
        HostedProducerDiscovery<HostedProducerSite> result,
        InvocationExpressionSyntax consumer) =>
        Assert.DoesNotContain(result.Diagnostics, diagnostic =>
            diagnostic.Code == "HOSTED_ADMISSION_HANDLE_ESCAPE"
            && diagnostic.Identity.EndsWith(
                "/call@" + consumer.SyntaxTree.FilePath + ":" + consumer.SpanStart,
                StringComparison.Ordinal));

    private static void AssertExactTelemetryConsumerEscape(
        HostedProducerDiscovery<HostedProducerSite> result,
        InvocationExpressionSyntax consumer,
        string detailPrefix) =>
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "HOSTED_ADMISSION_HANDLE_ESCAPE"
            && diagnostic.Identity.EndsWith(
                "/call@" + consumer.SyntaxTree.FilePath + ":" + consumer.SpanStart,
                StringComparison.Ordinal)
            && diagnostic.Detail.StartsWith(detailPrefix, StringComparison.Ordinal));
}
