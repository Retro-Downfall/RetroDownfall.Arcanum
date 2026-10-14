using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    private const string ActualLongRestHolderType = "RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission";

    private const string ActualLongRestParkHandlerType = "RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.LongRestSweepRecoveryHandler";

    private const string ActualLongRestCoordinatorType = "RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.LongRestSweepCoordinator";

    private const string ActualLongRestSweepPort = "RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.ILongRestAdmittedSweep";

    private const string ActualLongRestMaintenanceType = "RetroDownfall.Arcanum.Api.Intelligence.LongRestMaintenance";

    private const string ActualLongRestGateType = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate";

    private const string ActualLongRestLeaseType = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease";

    private const string ActualLongRestWorkerType = "RetroDownfall.Arcanum.Infrastructure.Hosting.LongRestSweepHostedService";

    private readonly record struct ActualLongRestMember(SemanticModel Model, IMethodSymbol Symbol, MethodDeclarationSyntax Syntax);

    /// <summary>The real members that carry the one Long Rest lease from its mint to the physical send, with their key calls.</summary>
    private sealed record ActualLongRestSpine(
        ActualLongRestMember Worker, InvocationExpressionSyntax WorkerAcquisition,
        ActualLongRestMember Owned, InvocationExpressionSyntax HandoffCall,
        ActualLongRestMember Paid, InvocationExpressionSyntax GroupCall,
        ActualLongRestMember Chat, InvocationExpressionSyntax ChatSend,
        ActualLongRestMember Embedding, InvocationExpressionSyntax EmbeddingSend);

    [Fact]
    public void ActualLongRestSpineCarriesOneLeaseByValueFromMintToExternalEffectGroup() => ReadActualLongRestSpine();

    // The Pure Park Handler. Durable recovery never runs a sweep: the registered handler decodes the checkpoint and parks the row,
    // and it cannot even name the coordinator, a store, a provider or a lease. Its constructor takes nothing, it keeps nothing,
    // and every symbol its body mentions is Core vocabulary or the framework.
    [Fact]
    public void ThePureParkHandlerTakesNothingKeepsNothingAndMentionsNoInfrastructureOrApi()
    {
        CSharpCompilation infrastructure = Assert.Single(HostedGrimoireProducerInventory.ProductionCompilations,
            static compilation => compilation.AssemblyName == "RetroDownfall.Arcanum.Infrastructure");

        INamedTypeSymbol parkHandler = Assert.IsAssignableFrom<INamedTypeSymbol>(infrastructure.GetTypeByMetadataName(ActualLongRestParkHandlerType));

        IMethodSymbol constructor = Assert.Single(parkHandler.InstanceConstructors);

        Assert.Empty(constructor.Parameters);

        Assert.True(constructor.IsImplicitlyDeclared);

        Assert.True(parkHandler.IsSealed);

        Assert.Equal("object", parkHandler.BaseType!.ToDisplayString());

        Assert.Empty(parkHandler.GetMembers().OfType<IFieldSymbol>());

        Assert.Equal(["RetroDownfall.Arcanum.Core.Operations.ILongRunningOperationRecoveryHandler"], parkHandler.AllInterfaces.Select(static contract => contract.ToDisplayString()));

        // The one Infrastructure type it may mention besides itself is the checkpoint codec, a static decoder of bytes.
        INamedTypeSymbol codec = Assert.IsAssignableFrom<INamedTypeSymbol>(infrastructure.GetTypeByMetadataName("RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.LongRestSweepCheckpointCodec"));

        Assert.True(codec.IsStatic);

        Assert.All(codec.GetMembers().OfType<IFieldSymbol>(), static field => Assert.True(field.IsConst || field.IsReadOnly));

        // Nothing it mentions lives in Infrastructure or Api, apart from itself and the codec: no coordinator, store, gate, lease or provider.
        // That is every name it writes (a generic name included), every member and type the compiler binds for it without a written name
        // (the enumerator of a foreach, the awaiter of an await, the operator of an expression, the type of a using resource), and the type of
        // every expression in it.
        AssertParkHandlerMentionsOnlyCoreAndFramework(infrastructure, parkHandler, codec);
    }

    // Every name the handler writes (a generic name included), every member and type the compiler binds for it without a written name
    // (the enumerator of a foreach, the awaiter of an await, the operator of an expression), and the type of every expression in it
    // must be Core vocabulary, the framework, the handler itself or its checkpoint codec.
    private static void AssertParkHandlerMentionsOnlyCoreAndFramework(CSharpCompilation compilation, INamedTypeSymbol parkHandler, INamedTypeSymbol codec)
    {
        foreach (SyntaxReference reference in parkHandler.DeclaringSyntaxReferences)
        {
            SemanticModel model = compilation.GetSemanticModel(reference.SyntaxTree);

            SyntaxNode declaration = reference.GetSyntax();

            foreach (SimpleNameSyntax name in declaration.DescendantNodes().OfType<SimpleNameSyntax>())
            {
                AssertParkHandlerMention(model.GetSymbolInfo(name).Symbol, parkHandler, codec);
            }

            foreach (SyntaxNode node in declaration.DescendantNodes())
            {
                switch (node)
                {
                    case ForEachStatementSyntax loop:
                        ForEachStatementInfo enumeration = model.GetForEachStatementInfo(loop);

                        foreach (ISymbol? bound in new ISymbol?[] { enumeration.GetEnumeratorMethod, enumeration.MoveNextMethod, enumeration.CurrentProperty, enumeration.DisposeMethod })
                        {
                            AssertParkHandlerMention(bound, parkHandler, codec);
                        }

                        break;

                    case AwaitExpressionSyntax awaited:
                        AwaitExpressionInfo awaiting = model.GetAwaitExpressionInfo(awaited);

                        foreach (ISymbol? bound in new ISymbol?[] { awaiting.GetAwaiterMethod, awaiting.IsCompletedProperty, awaiting.GetResultMethod })
                        {
                            AssertParkHandlerMention(bound, parkHandler, codec);
                        }

                        break;
                }

                if (model.GetOperation(node) is not { } operation)
                {
                    continue;
                }

                AssertParkHandlerMention(operation.Type, parkHandler, codec);

                switch (operation)
                {
                    case IInvocationOperation invocation:
                        AssertParkHandlerMention(invocation.TargetMethod, parkHandler, codec);

                        break;

                    case IObjectCreationOperation creation:
                        AssertParkHandlerMention(creation.Constructor, parkHandler, codec);

                        break;

                    case IMemberReferenceOperation memberReference:
                        AssertParkHandlerMention(memberReference.Member, parkHandler, codec);

                        break;

                    case IBinaryOperation binary:
                        AssertParkHandlerMention(binary.OperatorMethod, parkHandler, codec);

                        break;

                    case IUnaryOperation unary:
                        AssertParkHandlerMention(unary.OperatorMethod, parkHandler, codec);

                        break;

                    case IConversionOperation conversion:
                        AssertParkHandlerMention(conversion.OperatorMethod, parkHandler, codec);

                        break;

                    // The disposal of a using is a member of the resource's type, which is checked here as the type of the declared local.
                    case IVariableDeclaratorOperation declarator:
                        AssertParkHandlerMention(declarator.Symbol.Type, parkHandler, codec);

                        break;
                }
            }
        }
    }

    // A symbol the Pure Park Handler may mention: Core vocabulary, the framework, itself and its checkpoint codec. A type counts through
    // its type arguments and its element type too, so a Core generic closed over an Infrastructure type is a mention of that type.
    private static void AssertParkHandlerMention(ISymbol? symbol, INamedTypeSymbol parkHandler, INamedTypeSymbol codec)
    {
        switch (symbol)
        {
            case null or ILocalSymbol or IParameterSymbol or INamespaceSymbol or ITypeParameterSymbol or IRangeVariableSymbol or ILabelSymbol:
                return;

            case IArrayTypeSymbol array:
                AssertParkHandlerMention(array.ElementType, parkHandler, codec);

                return;
        }

        INamedTypeSymbol? owner = symbol.ContainingType ?? symbol as INamedTypeSymbol;

        if (symbol is INamedTypeSymbol named)
        {
            foreach (ITypeSymbol argument in named.TypeArguments)
            {
                AssertParkHandlerMention(argument, parkHandler, codec);
            }
        }

        if (SymbolEqualityComparer.Default.Equals(owner, parkHandler) || SymbolEqualityComparer.Default.Equals(owner, codec) || symbol.ContainingAssembly is null)
        {
            return;
        }

        Assert.True(symbol.ContainingAssembly.Name == "RetroDownfall.Arcanum.Core" || symbol.ContainingAssembly.Name.StartsWith("System", StringComparison.Ordinal)
            || symbol.ContainingAssembly.Name == "mscorlib" || symbol.ContainingAssembly.Name == "netstandard",
            $"The Pure Park Handler mentions {symbol.ToDisplayString()} from {symbol.ContainingAssembly.Name}.");
    }

    // The mention scan is only as good as what it refuses: a handler that mentions anything foreign, however the mention is spelled
    // (a qualified type, a generic name that only a using static spells, an extension call, the type of an expression), fails it, and a
    // handler that mentions only the framework passes.
    [Theory]
    [InlineData("framework-only", true)]
    [InlineData("qualified-type", false)]
    [InlineData("generic-name-spelled-by-using-static", false)]
    [InlineData("extension-call", false)]
    [InlineData("type-of-an-expression", false)]
    public void ThePureParkMentionScanRefusesAForeignMentionHoweverItIsSpelled(string shape, bool accepted)
    {
        string body = shape switch
        {
            "framework-only" => "return \"park\".Length + System.Math.Abs(1);",
            "qualified-type" => "return Foreign.Count;",
            "generic-name-spelled-by-using-static" => "return Make<int>();",
            "extension-call" => "return \"park\".Shout();",
            _ => "foreach (var item in Make<int[]>()) { return item; } return 0;",
        };

        CSharpCompilation compilation = Compile($$"""
            using System;
            using static Foreign;

            public static class Foreign
            {
                public static T Make<T>() => default!;

                public static int Count => 1;

                public static string Shout(this string text) => text;
            }

            public static class Codec
            {
            }

            public sealed class Handler
            {
                public object Run()
                {
                    {{body}}
                }
            }
            """);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        INamedTypeSymbol handler = Assert.IsAssignableFrom<INamedTypeSymbol>(compilation.GetTypeByMetadataName("Handler"));

        INamedTypeSymbol codec = Assert.IsAssignableFrom<INamedTypeSymbol>(compilation.GetTypeByMetadataName("Codec"));

        Action scan = () => AssertParkHandlerMentionsOnlyCoreAndFramework(compilation, handler, codec);

        if (accepted)
        {
            scan();
        }
        else
        {
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(scan);
        }
    }

    // Over the shared production discovery, which the production lane computes once per process: this test never runs a second
    // one. The hosted worker's kind-17 acquisition is the one paid door, and every physical dispatch below it carries that
    // acquisition's Work and the group begun on the lease parameter. Recovery only parks, so the readiness root no longer reaches
    // a send and is not part of this subset.
    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void ActualLongRestHostedRootRetainsPhysicalDispatchCapsules() =>
        AssertLongRestHostedRootRetainsPhysicalDispatchCapsules(HostedGrimoireProducerInventory.ProductionSiteDiscovery);

    // The anti-vacuity the required-callee test used to give Long Rest: the chat and the embedding sends of the paid door are
    // discovered under the hosted root at all, so the capsule assertions above are about sends that exist.
    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void TheLongRestChatAndEmbeddingCalleesAreDiscoveredUnderTheHostedRoot() =>
        AssertLongRestChatAndEmbeddingCalleesAreDiscovered(HostedGrimoireProducerInventory.ProductionSiteDiscovery);

    private void AssertLongRestHostedRootRetainsPhysicalDispatchCapsules(HostedProducerDiscovery<HostedProducerSite> discovery)
    {
        // The readiness root and the recovery handlers reach no Long Rest send any more: the worker's root is the whole subset. The
        // worker holds its Work down to the admitted maintenance, which is a Lease-Handoff Root: its closure is judged under its own
        // operation, with the lease formal as its Work, and a handoff-binding row in the worker's operation ties that formal to the
        // acquisition.
        HostedProducerServiceEntry service = Assert.Single(HostedGrimoireProducerInventory.Catalog, static entry => entry.ServiceType == LongRestHostedRootType);

        HostedProducerOperationEntry sweep = Assert.Single(service.Operations,
            static operation => operation.Authority == HostedProducerAuthorityKind.OrdinaryHostedWork && operation.LeaseFormal is null);

        Assert.Equal(GrimoireWorkKind.LongRestSweep, sweep.WorkKind);

        Assert.Equal(LongRestHostedRootOperation, sweep.OperationId);

        HostedProducerOperationEntry process = Assert.Single(service.Operations, static operation => operation.LeaseFormal is not null);

        Assert.Equal(ActualLongRestMaintenanceType + ".ProcessAdmittedAsync", process.OperationId);

        Assert.Equal("admission", process.LeaseFormal);

        Assert.Equal(GrimoireWorkKind.LongRestSweep, process.WorkKind);

        ActualLongRestSpine spine = ReadActualLongRestSpine();

        HostedProducerAnalysisMetrics metrics = Assert.IsType<HostedProducerAnalysisMetrics>(discovery.AnalysisMetrics);

        foreach (string line in RenderRootTraversalMetrics(metrics.RootTraversals)) output.WriteLine(line);

        Assert.Contains(metrics.RootTraversals, static metric => metric.RootType == LongRestHostedRootType && metric.AnalyzedStates > 0);

        // The diagnostics this test owns: a refused frontier, escape, receiver, handoff or unowned callback under either root.
        HostedProducerInventoryDiagnostic[] failures = [.. discovery.Diagnostics.Where(diagnostic =>
            (diagnostic.Identity.StartsWith(sweep.OperationId, StringComparison.Ordinal) || diagnostic.Identity.StartsWith(process.OperationId, StringComparison.Ordinal))
            && (diagnostic.Code.StartsWith("HOSTED_RECOVERY_", StringComparison.Ordinal)
                || diagnostic.Code is "HOSTED_ROOT_UNRESOLVED" or "HOSTED_ADMISSION_HANDLE_ESCAPE" or "HOSTED_ADMISSION_RECEIVER_MISMATCH"
                    or "HOSTED_SITE_WORK_FRONTIER_MISSING" or "HOSTED_SITE_EFFECT_FRONTIER_MISSING"
                    or "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN" or "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
                    or "HOSTED_TRAVERSAL_STATE_LIMIT_EXCEEDED" or "HOSTED_EVALUATION_ENVIRONMENT_LIMIT_EXCEEDED"
                    or HostedGrimoireProducerInventory.LeaseHandoffUnprovenCode or HostedGrimoireProducerInventory.LeaseHandoffRootUnboundCode))];

        Assert.True(failures.Length == 0, string.Join(global::System.Environment.NewLine,
            failures.Take(20).Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Identity}: {diagnostic.Detail}")));

        // Every reference to the admitted maintenance's method in production is a call some root proved.
        Assert.DoesNotContain(discovery.Diagnostics, static diagnostic => diagnostic.Code == HostedGrimoireProducerInventory.LeaseHandoffCallerUnanalysedCode);

        // Nothing is excused: the actual discovery reports no diagnostic of any code, under either root or anywhere else. The Histogram.Record
        // sites of the hierarchy traversal are classified by the reviewed storage contract (HierarchyFrontierObservationRequiresReachedCompleteScalarOrigins),
        // so no diagnostic is left for this test to name and let pass.
        Assert.True(discovery.Diagnostics.Count == 0, string.Join(global::System.Environment.NewLine,
            discovery.Diagnostics.Take(20).Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Identity}: {diagnostic.Detail}")));

        // The acquisition in RunOneAsync is a kind-17 Work, and it is one capsule.
        HostedProducerSite[] work = LongRestDispatchSites(discovery, sweep, spine.Worker, spine.WorkerAcquisition, HostedProducerSiteKind.EffectFrontier);

        string acquisition = Assert.Single(work.Select(static site => site.CapsuleId).Distinct(StringComparer.Ordinal));

        Assert.Equal(17, (int)GrimoireWorkKind.LongRestSweep);

        // The acquisition is its own Work: it is the admission the lease spine starts from, not a use of one.
        Assert.All(work, site =>
        {
            Assert.Equal(GrimoireWorkKind.LongRestSweep, site.AdmittedWorkKind);

            Assert.Equal(acquisition, site.WorkFrontierCapsuleId);
        });

        // The handoff root's own Work row: the lease formal the admitted maintenance is handed, admitting the same work kind.
        HostedProducerSite formal = Assert.Single(discovery.Items, site => site.RootType == LongRestHostedRootType
            && site.AuthorityOperationId == process.OperationId && site.Kind == HostedProducerSiteKind.EffectFrontier
            && site.Callee == HostedGrimoireProducerInventory.RootLeaseFormalCallee);

        Assert.Equal(GrimoireWorkKind.LongRestSweep, formal.AdmittedWorkKind);

        Assert.Equal(formal.CapsuleId, formal.WorkFrontierCapsuleId);

        Assert.NotEqual(acquisition, formal.CapsuleId);

        // The one handoff-binding row ties that formal to the worker's acquisition, at the coordinator's awaited call.
        HostedProducerSite binding = Assert.Single(LongRestDispatchSites(discovery, sweep, spine.Owned, spine.HandoffCall, HostedProducerSiteKind.LeaseHandoff));

        Assert.Equal(acquisition, binding.WorkFrontierCapsuleId);

        Assert.Equal(process.OperationId, binding.Callee);

        IReadOnlyDictionary<string, string> bindings = LongRestLeaseHandoffBindings(discovery, service, sweep);

        Assert.Equal(acquisition, Assert.Single(bindings, entry => entry.Key == formal.CapsuleId).Value);

        // The lease reaches the group's receiver by parameter: the group's Work is the bound formal, no group is refused as a
        // receiver mismatch, and the syntax pins the receiver to the paid door's lease formal.
        HostedProducerSite[] groups = LongRestDispatchSites(discovery, process, spine.Paid, spine.GroupCall, HostedProducerSiteKind.EffectFrontier);

        string group = Assert.Single(groups.Select(static site => site.CapsuleId).Distinct(StringComparer.Ordinal));

        Assert.All(groups, site =>
        {
            Assert.Equal(formal.CapsuleId, site.WorkFrontierCapsuleId);

            Assert.True(LongRestWorkIsTheWorkersAdmission(site.WorkFrontierCapsuleId, acquisition, bindings), "The group's Work is neither the acquisition nor a bound formal of it.");

            Assert.Equal(group, site.EffectFrontierCapsuleId);
        });

        string groupCall = "/call@" + spine.GroupCall.SyntaxTree.FilePath + ":" + spine.GroupCall.SpanStart;

        Assert.DoesNotContain(discovery.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_ADMISSION_RECEIVER_MISMATCH"
            && diagnostic.Identity.EndsWith(groupCall, StringComparison.Ordinal));

        // Both sends carry the bound formal's Work and the group.
        foreach (HostedProducerSite send in LongRestDispatchSites(discovery, process, spine.Chat, spine.ChatSend, HostedProducerSiteKind.ProviderCall)
            .Concat(LongRestDispatchSites(discovery, process, spine.Embedding, spine.EmbeddingSend, HostedProducerSiteKind.ProviderCall)))
        {
            Assert.Equal(formal.CapsuleId, send.WorkFrontierCapsuleId);

            Assert.True(LongRestWorkIsTheWorkersAdmission(send.WorkFrontierCapsuleId, acquisition, bindings), $"{send.MemberIdentity} carries Work that is neither the acquisition nor a bound formal of it.");

            Assert.Equal(group, send.EffectFrontierCapsuleId);

            output.WriteLine($"ACTUAL_LONG_REST_HOSTED_ROOT authority={process.OperationId} source={send.SourcePath} member={send.MemberIdentity} capsule={send.CapsuleId} work={send.WorkFrontierCapsuleId} group={group} acquisition={acquisition}");
        }
    }

    private static void AssertLongRestChatAndEmbeddingCalleesAreDiscovered(HostedProducerDiscovery<HostedProducerSite> discovery)
    {
        ActualLongRestSpine spine = ReadActualLongRestSpine();

        foreach ((ActualLongRestMember member, InvocationExpressionSyntax send) in new[] { (spine.Chat, spine.ChatSend), (spine.Embedding, spine.EmbeddingSend) })
        {
            string identity = Assert.IsType<string>(member.Symbol.GetDocumentationCommentId());

            IMethodSymbol called = Assert.IsAssignableFrom<IMethodSymbol>(member.Model.GetSymbolInfo(send).Symbol);

            HostedProducerSite[] found = [.. discovery.Items.Where(site => site.RootType == LongRestHostedRootType
                && site.Kind == HostedProducerSiteKind.ProviderCall && site.MemberIdentity == identity
                && site.SourcePath == send.SyntaxTree.FilePath && site.Callee.EndsWith("." + called.Name, StringComparison.Ordinal))];

            Assert.True(found.Length > 0, $"The Long Rest send {identity} is not discovered under {LongRestHostedRootType}.");

            Assert.All(found, site => Assert.StartsWith("Microsoft.Extensions.AI.", site.Callee, StringComparison.Ordinal));
        }
    }

    // The actual source sites of one call in a member of the worker's closure, from the shared discovery.
    private static HostedProducerSite[] LongRestDispatchSites(HostedProducerDiscovery<HostedProducerSite> discovery, HostedProducerOperationEntry authority,
        ActualLongRestMember member, InvocationExpressionSyntax call, HostedProducerSiteKind kind)
    {
        string identity = Assert.IsType<string>(member.Symbol.GetDocumentationCommentId());

        string fingerprint = HostedGrimoireProducerInventory.Fingerprint(call);

        HostedProducerSite[] matches = [.. discovery.Items.Where(site => site.RootType == LongRestHostedRootType
            && site.AuthorityOperationId == authority.OperationId && site.SourcePath == call.SyntaxTree.FilePath
            && site.MemberIdentity == identity && site.Kind == kind && site.SyntaxFingerprint == fingerprint)];

        Assert.True(matches.Length > 0, $"Missing actual source site: {LongRestHostedRootType}; {authority.OperationId}; {identity}; {call}");

        IMethodSymbol called = Assert.IsAssignableFrom<IMethodSymbol>(member.Model.GetSymbolInfo(call).Symbol);

        Assert.All(matches, site => Assert.True(site.Callee.EndsWith("." + called.Name, StringComparison.Ordinal), site.Callee));

        return matches;
    }

    // The Work a Long Rest dispatch may carry is exactly one of two things: the capsule of the worker's kind-17 acquisition, or,
    // only under a declared Lease-Handoff Root, a formal capsule that a handoff binding ties to that acquisition. A formal that
    // nothing binds, a formal bound to some other acquisition, a capsule that is neither, and no Work at all are all refused.
    internal static bool LongRestWorkIsTheWorkersAdmission(string? work, string acquisition, IReadOnlyDictionary<string, string> formalToAcquisition) =>
        work is not null && (work == acquisition || formalToAcquisition.TryGetValue(work, out string? bound) && bound == acquisition);

    // The binding rows the Lease-Handoff Roots add: each declared handoff operation's formal capsule, tied to the acquisition capsule
    // of the worker's call that hands it the lease. A handoff root with no formal row, no binding, or a formal two acquisitions bind
    // is not one admission, so the reader fails on it.
    private static IReadOnlyDictionary<string, string> LongRestLeaseHandoffBindings(HostedProducerDiscovery<HostedProducerSite> discovery,
        HostedProducerServiceEntry service, HostedProducerOperationEntry worker)
    {
        Dictionary<string, string> bindings = new(StringComparer.Ordinal);

        foreach (HostedProducerOperationEntry handoff in service.Operations.Where(static operation => operation.LeaseFormal is not null))
        {
            string formal = Assert.Single(discovery.Items.Where(site => site.RootType == service.ServiceType
                && site.AuthorityOperationId == handoff.OperationId && site.Kind == HostedProducerSiteKind.EffectFrontier
                && site.Callee == HostedGrimoireProducerInventory.RootLeaseFormalCallee).Select(static site => site.CapsuleId).Distinct(StringComparer.Ordinal));

            string? acquisition = Assert.Single(discovery.Items.Where(site => site.RootType == service.ServiceType
                && site.AuthorityOperationId == worker.OperationId && site.Kind == HostedProducerSiteKind.LeaseHandoff
                && site.Callee == handoff.OperationId).Select(static site => site.WorkFrontierCapsuleId).Distinct(StringComparer.Ordinal));

            bindings.Add(formal, Assert.IsType<string>(acquisition));
        }

        return bindings;
    }

    [Theory]
    [InlineData("acquisition", true)]
    [InlineData("bound-formal", true)]
    [InlineData("unbound-formal", false)]
    [InlineData("formal-bound-to-another-acquisition", false)]
    [InlineData("other-capsule", false)]
    [InlineData("no-work", false)]
    [InlineData("acquisition-as-a-binding-key", false)]
    public void TheDispatchWorkShapeAcceptsExactlyTheAcquisitionOrAFormalBoundToIt(string shape, bool accepted)
    {
        Dictionary<string, string> bindings = new(StringComparer.Ordinal)
        {
            ["formal-a"] = "acquisition-a",
            ["formal-b"] = "acquisition-b",
            ["acquisition-b"] = "acquisition-other",
        };

        string? work = shape switch
        {
            "acquisition" => "acquisition-a",
            "bound-formal" => "formal-a",
            "unbound-formal" => "formal-z",
            "formal-bound-to-another-acquisition" => "formal-b",
            "other-capsule" => "capsule-q",
            "no-work" => null,
            // A capsule that is a binding key for some other acquisition is not the acquisition the caller named.
            _ => "acquisition-b",
        };

        Assert.Equal(accepted, LongRestWorkIsTheWorkersAdmission(work, "acquisition-a", bindings));
    }

    // The lease is minted twice in production and never stored: by the hosted worker, and inline by the Session shell.
    // Both hand the acquired local by value to an admitted helper, and every later use takes the lease as a parameter.
    // Recovery mints none: the Pure Park Handler has no dependencies, so no recovery entry reaches the coordinator.
    private static ActualLongRestSpine ReadActualLongRestSpine()
    {
        IReadOnlyList<CSharpCompilation> compilations = HostedGrimoireProducerInventory.ProductionCompilations;

        CSharpCompilation core = Assert.Single(compilations, static compilation => compilation.AssemblyName == "RetroDownfall.Arcanum.Core");

        CSharpCompilation infrastructure = Assert.Single(compilations, static compilation => compilation.AssemblyName == "RetroDownfall.Arcanum.Infrastructure");

        CSharpCompilation api = Assert.Single(compilations, static compilation => compilation.AssemblyName == "RetroDownfall.Arcanum.Api");

        Assert.Equal(17, (int)GrimoireWorkKind.LongRestSweep);

        // The holder and its public Core process member are gone.
        Assert.Null(infrastructure.GetTypeByMetadataName(ActualLongRestHolderType));

        INamedTypeSymbol corePort = Assert.IsAssignableFrom<INamedTypeSymbol>(core.GetTypeByMetadataName("RetroDownfall.Arcanum.Core.LongRest.ILongRestMaintenance"));

        Assert.Empty(corePort.GetMembers("ProcessAsync"));

        // The hosted worker: acquire, retain with await using, hand the retained local to the port. Nothing is bound.
        ActualLongRestMember worker = ActualLongRestMemberOf(infrastructure, ActualLongRestWorkerType, "RunOneAsync");

        InvocationExpressionSyntax workerAcquisition = ActualLongRestCallOf(worker, ActualLongRestGateType, "TryAcquireWorkLease");

        AssertLongRestSweepKind(worker, workerAcquisition);

        ILocalSymbol retained = AssertRetainedLease(worker, workerAcquisition);

        InvocationExpressionSyntax handoff = ActualLongRestCallOf(worker, ActualLongRestSweepPort, "ResumeAdmittedAsync");

        Assert.True(SymbolEqualityComparer.Default.Equals(retained, worker.Model.GetSymbolInfo(handoff.ArgumentList.Arguments[1].Expression).Symbol));

        Assert.Contains(handoff.Ancestors(), static ancestor => ancestor is AwaitExpressionSyntax);

        Assert.DoesNotContain(worker.Syntax.DescendantNodes().OfType<IdentifierNameSyntax>(), static name => name.Identifier.ValueText is "Bind" or "LongRestWorkAdmission");

        // The Session shell mints its own lease inline and joins the helper under it.
        ActualLongRestMember resume = ActualLongRestMemberOf(infrastructure, ActualLongRestCoordinatorType, "ResumeAsync");

        AssertSelfAdmittingShell(resume, "ResumeCoreAsync");

        // Recovery never runs a sweep: the coordinator has no recovery entry, and the registered handler cannot name it.
        INamedTypeSymbol coordinatorType = Assert.IsAssignableFrom<INamedTypeSymbol>(infrastructure.GetTypeByMetadataName(ActualLongRestCoordinatorType));

        Assert.Empty(coordinatorType.GetMembers("RecoverAsync"));

        Assert.Empty(coordinatorType.GetMembers("RecoverAdmittedAsync"));

        INamedTypeSymbol parkHandler = Assert.IsAssignableFrom<INamedTypeSymbol>(infrastructure.GetTypeByMetadataName(ActualLongRestParkHandlerType));

        Assert.Empty(Assert.Single(parkHandler.InstanceConstructors).Parameters);

        Assert.Empty(parkHandler.GetMembers().OfType<IFieldSymbol>());

        // The hosted port is a guarded forwarder to the same core, with no capability.
        ActualLongRestMember port = ActualLongRestMemberOf(infrastructure, ActualLongRestCoordinatorType, "ResumeAdmittedAsync");

        AssertAdmissionKindGuard(port);

        IParameterSymbol portLease = LeaseParameter(port);

        InvocationExpressionSyntax forwarded = Assert.Single(port.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            call => port.Model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "ResumeCoreAsync" });

        Assert.Equal("null", forwarded.ArgumentList.Arguments[1].Expression.ToString());

        Assert.True(SymbolEqualityComparer.Default.Equals(portLease, port.Model.GetSymbolInfo(forwarded.ArgumentList.Arguments[2].Expression).Symbol));

        // The owned run takes the lease formal and passes only that formal to every lease-bearing call.
        ActualLongRestMember owned = ActualLongRestMemberOf(infrastructure, ActualLongRestCoordinatorType, "RunOwnedAsync");

        AssertLeaseOnlyFlowsFromTheFormal(owned, expectedCalls: 5, callees: ["AttestCleanAdmission", "AttestCleanAdmission",
            "ReconcileCompletedCheckpointAsync", "ReadmitCleanAsync", "ProcessAdmittedAsync"]);

        // The admitted maintenance does the same and begins each group on its own parameter.
        ActualLongRestMember process = ActualLongRestMemberOf(api, ActualLongRestMaintenanceType, "ProcessAdmittedAsync");

        AssertAdmissionKindGuard(process);

        AssertLeaseOnlyFlowsFromTheFormal(process, expectedCalls: 2, callees: ["FoldAsync", "AdjudicateAsync"]);

        AssertLeaseOnlyFlowsFromTheFormal(ActualLongRestMemberOf(api, ActualLongRestMaintenanceType, "FoldAsync"), expectedCalls: 2,
            callees: ["ExecutePaidAsync", "ExecutePaidAsync"]);

        AssertLeaseOnlyFlowsFromTheFormal(ActualLongRestMemberOf(api, ActualLongRestMaintenanceType, "AdjudicateAsync"), expectedCalls: 1,
            callees: ["ExecutePaidAsync"]);

        ActualLongRestMember paid = ActualLongRestMemberOf(api, ActualLongRestMaintenanceType, "ExecutePaidAsync");

        InvocationExpressionSyntax groupCall = ActualLongRestCallOf(paid, ActualLongRestLeaseType, "TryBeginExternalEffectGroup");

        IParameterSymbol paidLease = LeaseParameter(paid);

        Assert.True(SymbolEqualityComparer.Default.Equals(paidLease, paid.Model.GetSymbolInfo(
            Assert.IsType<MemberAccessExpressionSyntax>(groupCall.Expression).Expression).Symbol));

        // The local function that runs before each send and every other closure leave the lease alone.
        AssertNoClosureCapturesLease(paid, paidLease);

        SingleVariableDesignationSyntax groupVariable = Assert.Single(groupCall.ArgumentList.Arguments[0].DescendantNodes().OfType<SingleVariableDesignationSyntax>());

        ISymbol groupSymbol = paid.Model.GetDeclaredSymbol(groupVariable)!;

        LocalDeclarationStatementSyntax retainedGroup = Assert.Single(paid.Syntax.DescendantNodes().OfType<LocalDeclarationStatementSyntax>(), declaration =>
            declaration.AwaitKeyword.RawKind != 0 && declaration.UsingKeyword.RawKind != 0
            && declaration.Declaration.Variables.Any(variable => variable.Initializer is { } initializer
                && SymbolEqualityComparer.Default.Equals(paid.Model.GetSymbolInfo(initializer.Value).Symbol, groupSymbol)));

        Assert.Same(paid.Syntax.Body, retainedGroup.Parent);
        Assert.True(groupCall.SpanStart < retainedGroup.SpanStart);

        var chat = ActualLongRestMemberOf(api, "RetroDownfall.Arcanum.Api.Intelligence.ModelCallExecutor", "ExecuteLongRestChatAsync");

        InvocationExpressionSyntax chatSend = ActualLongRestCallOf(chat, "Microsoft.Extensions.AI.IChatClient", "GetResponseAsync");

        var embedding = ActualLongRestMemberOf(api, "RetroDownfall.Arcanum.Api.Intelligence.ModelCallExecutor", "ExecuteLongRestEmbeddingAsync");

        InvocationExpressionSyntax embeddingSend = ActualLongRestCallOf(embedding, "Microsoft.Extensions.AI.IEmbeddingGenerator`2", "GenerateAsync");

        InvocationExpressionSyntax handoffCall = ActualLongRestCallOf(owned, "RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.ILongRestAdmittedMaintenance", "ProcessAdmittedAsync");

        return new(worker, workerAcquisition, owned, handoffCall, paid, groupCall, chat, chatSend, embedding, embeddingSend);
    }

    private static ActualLongRestMember ActualLongRestMemberOf(Compilation compilation, string typeName, string methodName)
    {
        INamedTypeSymbol type = Assert.IsAssignableFrom<INamedTypeSymbol>(compilation.GetTypeByMetadataName(typeName));

        IMethodSymbol method = Assert.Single(type.GetMembers(methodName).OfType<IMethodSymbol>());

        MethodDeclarationSyntax syntax = Assert.IsType<MethodDeclarationSyntax>(Assert.Single(method.DeclaringSyntaxReferences).GetSyntax());

        return new(compilation.GetSemanticModel(syntax.SyntaxTree), method, syntax);
    }

    private static InvocationExpressionSyntax ActualLongRestCallOf(ActualLongRestMember member, string typeName, string methodName)
    {
        INamedTypeSymbol type = Assert.IsAssignableFrom<INamedTypeSymbol>(member.Model.Compilation.GetTypeByMetadataName(typeName));

        return Assert.Single(member.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>(), call =>
            member.Model.GetSymbolInfo(call).Symbol is IMethodSymbol called && called.Name == methodName
            && SymbolEqualityComparer.Default.Equals(called.ContainingType.OriginalDefinition, type));
    }

    private static IParameterSymbol LeaseParameter(ActualLongRestMember member) => Assert.Single(member.Symbol.Parameters,
        parameter => parameter.Type.ToDisplayString() == ActualLongRestLeaseType && parameter.Name == "admission");

    private static void AssertLongRestSweepKind(ActualLongRestMember member, InvocationExpressionSyntax acquisition) =>
        Assert.Equal(GrimoireWorkKind.LongRestSweep, (GrimoireWorkKind)Convert.ToInt32(
            Assert.IsAssignableFrom<IFieldSymbol>(member.Model.GetSymbolInfo(acquisition.ArgumentList.Arguments[0].Expression).Symbol).ConstantValue));

    // `await using IGrimoireWorkLease x = <the acquisition's out value>`, directly in the member body, after the acquisition.
    private static ILocalSymbol AssertRetainedLease(ActualLongRestMember member, InvocationExpressionSyntax acquisition)
    {
        SingleVariableDesignationSyntax admittedVariable = Assert.Single(acquisition.ArgumentList.Arguments[1].DescendantNodes().OfType<SingleVariableDesignationSyntax>());

        ISymbol admitted = member.Model.GetDeclaredSymbol(admittedVariable)!;

        LocalDeclarationStatementSyntax declaration = Assert.Single(member.Syntax.DescendantNodes().OfType<LocalDeclarationStatementSyntax>(), candidate =>
            candidate.AwaitKeyword.RawKind != 0 && candidate.UsingKeyword.RawKind != 0
            && candidate.Declaration.Variables.Single().Initializer!.Value.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                .Any(name => SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(name).Symbol, admitted)));

        Assert.True(acquisition.SpanStart < declaration.SpanStart);

        return Assert.IsAssignableFrom<ILocalSymbol>(member.Model.GetDeclaredSymbol(declaration.Declaration.Variables.Single()));
    }

    private static InvocationExpressionSyntax AssertSelfAdmittingShell(ActualLongRestMember entry, string helperName)
    {
        InvocationExpressionSyntax acquisition = ActualLongRestCallOf(entry, ActualLongRestGateType, "TryAcquireWorkLease");

        AssertLongRestSweepKind(entry, acquisition);

        // The acquisition is the condition of a statement of the entry's own body, so the gate's ambient lifetime is this context's.
        IfStatementSyntax gate = Assert.IsType<IfStatementSyntax>(acquisition.Ancestors().OfType<StatementSyntax>().First());

        Assert.Same(entry.Syntax.Body, gate.Parent);

        ILocalSymbol acquired = AssertRetainedLease(entry, acquisition);

        var helper = ActualLongRestMemberOf(entry.Model.Compilation, ActualLongRestCoordinatorType, helperName);

        IParameterSymbol lease = Assert.Single(helper.Symbol.Parameters, parameter => parameter.Type.ToDisplayString() == ActualLongRestLeaseType);

        InvocationExpressionSyntax ownedJoin = Assert.Single(entry.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>(), call =>
            SymbolEqualityComparer.Default.Equals(entry.Model.GetSymbolInfo(call).Symbol, helper.Symbol)
            && SymbolEqualityComparer.Default.Equals(entry.Model.GetSymbolInfo(call.ArgumentList.Arguments[lease.Ordinal].Expression).Symbol, acquired));

        Assert.Contains(ownedJoin.Ancestors(), static ancestor => ancestor is AwaitExpressionSyntax);
        Assert.Contains(ownedJoin.Ancestors(), static ancestor => ancestor is ReturnStatementSyntax);
        Assert.DoesNotContain(entry.Syntax.DescendantNodes().OfType<IdentifierNameSyntax>(), static name => name.Identifier.ValueText is "Bind" or "LongRestWorkAdmission");

        return acquisition;
    }

    // Both admitted entries refuse a missing lease and any lease that is not a Long Rest sweep lease before doing work.
    private static void AssertAdmissionKindGuard(ActualLongRestMember entry)
    {
        Assert.Equal(RefKind.None, LeaseParameter(entry).RefKind);

        Assert.Equal("if (admission is null) throw new ArgumentNullException(nameof(admission));", entry.Syntax.Body!.Statements[0].ToString());

        Assert.StartsWith("if (admission.Kind != GrimoireWorkKind.LongRestSweep) throw new ArgumentException(", entry.Syntax.Body.Statements[1].ToString(), StringComparison.Ordinal);
    }

    // Every call in the member that receives a lease receives its own lease formal, and no function inside it closes over the lease.
    private static void AssertLeaseOnlyFlowsFromTheFormal(ActualLongRestMember member, int expectedCalls, string[] callees)
    {
        IParameterSymbol formal = LeaseParameter(member);

        // `nameof(admission)` in the guard is a constant, not a use of the lease.
        InvocationExpressionSyntax[] carrying = [.. member.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call =>
            !member.Model.GetConstantValue(call).HasValue && call.ArgumentList.Arguments.Any(argument => member.Model.GetTypeInfo(argument.Expression).Type?.ToDisplayString() == ActualLongRestLeaseType))];

        Assert.True(carrying.Length == expectedCalls, $"{member.Symbol.Name} passes a lease in {carrying.Length} calls, expected {expectedCalls}: "
            + string.Join(" | ", carrying.Select(static call => call.Expression.ToString())));

        Assert.Equal(callees.Order(StringComparer.Ordinal),
            carrying.Select(call => Assert.IsAssignableFrom<IMethodSymbol>(member.Model.GetSymbolInfo(call).Symbol).Name).Order(StringComparer.Ordinal));

        foreach (InvocationExpressionSyntax call in carrying)
        {
            ArgumentSyntax[] leaseArguments = [.. call.ArgumentList.Arguments.Where(argument =>
                member.Model.GetTypeInfo(argument.Expression).Type?.ToDisplayString() == ActualLongRestLeaseType)];

            ArgumentSyntax leaseArgument = Assert.Single(leaseArguments);

            Assert.True(SymbolEqualityComparer.Default.Equals(formal, member.Model.GetSymbolInfo(leaseArgument.Expression).Symbol),
                $"{member.Symbol.Name} passes {leaseArgument.Expression} instead of its lease formal.");
        }

        AssertNoClosureCapturesLease(member, formal);
    }

    private static void AssertNoClosureCapturesLease(ActualLongRestMember member, IParameterSymbol formal)
    {
        foreach (SyntaxNode closure in member.Syntax.DescendantNodes().Where(static node => node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
        {
            Assert.DoesNotContain(closure.DescendantNodes().OfType<IdentifierNameSyntax>(), name =>
                SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(name).Symbol, formal));
        }
    }
}
