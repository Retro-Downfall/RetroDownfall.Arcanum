using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    internal static (int ParentStates, int Tokens, int Fingerprints, int CacheKeys, bool Complete, bool SameEvidence, HostedProducerDiscovery<HostedProducerSite> Discovery)
        ProbeEmptyCapturedAdmissionIdentity(CSharpCompilation compilation, HostedProducerOperationEntry root, string variant, bool emptyFirst)
    {
        SemanticModel model = compilation.GetSemanticModel(compilation.SyntaxTrees.Single());

        SyntaxNode syntax = model.SyntaxTree.GetRoot();

        MethodDeclarationSyntax parentDeclaration = syntax.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(static method => method.Identifier.ValueText == "Parent");

        MethodDeclarationSyntax outerDeclaration = syntax.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(static method => method.Identifier.ValueText == "Outer");

        AuthoredMember parent = new(model.GetDeclaredSymbol(parentDeclaration)!, parentDeclaration, model);

        AuthoredMember callback;

        if (variant.StartsWith("lambda-", StringComparison.Ordinal))
        {
            VariableDeclaratorSyntax declaration = syntax.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .Single(static variable => variable.Identifier.ValueText == "Callback");

            AnonymousFunctionExpressionSyntax lambda = (AnonymousFunctionExpressionSyntax)declaration.Initializer!.Value;

            IAnonymousFunctionOperation operation = (IAnonymousFunctionOperation)model.GetOperation(lambda)!;

            // Match production ResolveCallableTargets: operation symbol and lambda body,
            // rather than the enclosing declaration or a local-function surrogate.
            callback = new(operation.Symbol, lambda.Body, model);
        }
        else
        {
            LocalFunctionStatementSyntax declaration = syntax.DescendantNodes().OfType<LocalFunctionStatementSyntax>()
                .Single(static method => method.Identifier.ValueText == "Callback");

            callback = new(model.GetDeclaredSymbol(declaration)!, declaration, model);
        }

        IParameterSymbol bound = variant == "future-own"
            ? callback.Symbol.Parameters[0]
            : model.GetDeclaredSymbol(outerDeclaration)!.Parameters[0];

        List<HostedProducerAdmittedStateObservation> observed = [];

        ProducerGraph graph = new([compilation], DefaultEvaluationEnvironmentMaximumMembers, DefaultEvaluationEnvironmentMaximumDepth,
            DefaultTraversalMaximumDepth, DefaultMaximumAnalyzedStatesPerRoot, false, admittedStateObserver: observed.Add);

        return graph.ProbeEmptyCapturedAdmissionIdentity(parent, callback, bound, root, variant, emptyFirst, observed);
    }

    private sealed partial class ProducerGraph
    {
        internal (int ParentStates, int Tokens, int Fingerprints, int CacheKeys, bool Complete, bool SameEvidence, HostedProducerDiscovery<HostedProducerSite> Discovery)
            ProbeEmptyCapturedAdmissionIdentity(AuthoredMember parent, AuthoredMember callback, IParameterSymbol bound,
                HostedProducerOperationEntry root, string variant, bool emptyFirst, List<HostedProducerAdmittedStateObservation> observed)
        {
            _ = PrepareRoots(new(["Worker"], []), [new("Worker", [root])], []);

            List<int?> tokens = [];

            List<string> fingerprints = [];

            List<EvaluationEnvironmentCacheIdentity?> cacheKeys = [];

            bool complete = true;

            string? firstManifest = null;

            string? firstDiagnostics = null;

            foreach (bool entryPresent in new[] { emptyFirst, !emptyFirst })
            {
                AuthoredMember target = callback with
                {
                    AdmissionBindings = entryPresent && variant != "constructor-only" && !variant.StartsWith("context-", StringComparison.Ordinal)
                        ? new Dictionary<ISymbol, IReadOnlySet<string>>(SymbolEqualityComparer.Default)
                        {
                            [bound] = variant == "nonempty" ? new HashSet<string> { "work@captured-control" } : new HashSet<string>(),
                        }
                        : null,
                };

                if (variant == "different-target" && entryPresent)
                {
                    LocalFunctionStatementSyntax alternative = callback.Syntax.SyntaxTree.GetRoot().DescendantNodes()
                        .OfType<LocalFunctionStatementSyntax>().Single(static function => function.Identifier.ValueText == "Alternative");

                    target = target with { Symbol = callback.Model.GetDeclaredSymbol(alternative)!, Syntax = alternative };
                }

                if (variant == "bound-callable")
                {
                    SyntaxNode syntax = callback.Syntax.SyntaxTree.GetRoot();

                    VariableDeclaratorSyntax other = syntax.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                        .Single(static variable => variable.Identifier.ValueText == "other");

                    LocalFunctionStatementSyntax reader = syntax.DescendantNodes().OfType<LocalFunctionStatementSyntax>()
                        .Single(static function => function.Identifier.ValueText == "ReadCaptured");

                    target = target with
                    {
                        CallableBindings = new Dictionary<ISymbol, AuthoredMember>(SymbolEqualityComparer.Default)
                        {
                            [callback.Model.GetDeclaredSymbol(other)!] = new(callback.Model.GetDeclaredSymbol(reader)!, reader, callback.Model),
                        },
                    };
                }

                if (variant is "constructor" or "constructor-only")
                {
                    IMethodSymbol[] constructors = callback.Symbol.ContainingType.InstanceConstructors
                        .Where(static constructor => !constructor.IsImplicitlyDeclared).OrderBy(static constructor => constructor.Parameters.Length).ToArray();

                    target = target with { ConstructorContexts = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default) { constructors[entryPresent ? 1 : 0] } };
                }

                if (variant == "recovery")
                {
                    // DB-only kind/version remains observable in effect diagnostics.
                    // OwnerBound ambient identity is tested separately from live tuples.
                    target = target with { RecoveryContext = new(entryPresent ? "first-owner" : "second-owner", 1, RecoveryDisposition.OrdinaryDbOnly) };
                }

                if (variant.StartsWith("context-", StringComparison.Ordinal))
                {
                    RecoveryTuple? recovery = variant switch
                    {
                        "context-owner-kind" => new(entryPresent ? "first-owner" : "second-owner", 1, RecoveryDisposition.OwnerBound),
                        "context-owner-version" => new("same-owner", entryPresent ? 1 : 2, RecoveryDisposition.OwnerBound),
                        "context-null" => entryPresent ? new("same-owner", 1, RecoveryDisposition.OwnerBound) : null,
                        "context-disposition" => new("same-owner", 1, entryPresent ? RecoveryDisposition.OwnerBound : RecoveryDisposition.OrdinaryDbOnly),
                        "context-db-kind" => new(entryPresent ? "first-owner" : "second-owner", 1, RecoveryDisposition.OrdinaryDbOnly),
                        "context-db-version" => new("same-owner", entryPresent ? 1 : 2, RecoveryDisposition.OrdinaryDbOnly),
                        "context-live-kind" or "context-live-version" => new("ambient-owner", 1, RecoveryDisposition.OwnerBound),
                        _ => throw new InvalidOperationException("Unknown recovery identity control."),
                    };

                    target = target with { RecoveryContext = recovery };

                    if (variant is "context-live-kind" or "context-live-version")
                    {
                        IParameterSymbol operation = ((IMethodSymbol)bound.ContainingSymbol).Parameters.Single(static parameter => parameter.Name == "operation");

                        if (TypeKey(operation.Type) != "RetroDownfall.Arcanum.Core.Operations.LongRunningOperation")
                            throw new InvalidOperationException("The live recovery control requires a real operation parameter.");

                        RecoveryTuple tuple = variant == "context-live-kind"
                            ? new(entryPresent ? "first-operation" : "second-operation", 1, RecoveryDisposition.OwnerBound)
                            : new("same-operation", entryPresent ? 1 : 2, RecoveryDisposition.OwnerBound);

                        target = target with
                        {
                            RecoveryBindings = new Dictionary<ISymbol, RecoveryTuple>(SymbolEqualityComparer.Default)
                            {
                                [operation] = tuple,
                            },
                        };
                    }
                }

                AuthoredMember context = parent with
                {
                    CallableBindings = new Dictionary<ISymbol, AuthoredMember>(SymbolEqualityComparer.Default)
                    {
                        [parent.Symbol.Parameters[0]] = target,
                    },
                };

                tokens.Add(TryRegisterEvaluationEnvironmentToken(context));

                EvaluationEnvironmentFingerprint fingerprint = EvaluationEnvironmentIdentity(context,
                    new EvaluationEnvironmentIdentityContext(evaluationEnvironmentMaximumMembers, evaluationEnvironmentMaximumDepth), 0);

                fingerprints.Add(fingerprint.Identity);

                complete &= fingerprint.Complete && !fingerprint.ContainsBackreference;

                cacheKeys.Add(EvaluationEnvironmentCacheIdentityFor(context,
                    new EvaluationEnvironmentCacheIdentityContext(evaluationEnvironmentMaximumMembers, evaluationEnvironmentMaximumDepth),
                    new EvaluationEnvironmentAnalysisCounter(), 0));

                _ = TraverseRoot(context, "Worker", root.OperationId, lifecycle: false);

                HostedProducerDiscovery<HostedProducerSite> current = CompleteDiscovery();

                firstManifest ??= RenderCapsuleManifest(current.Items);

                firstDiagnostics ??= RenderDiagnosticReport(current.Diagnostics);
            }

            HostedProducerDiscovery<HostedProducerSite> discovery = CompleteDiscovery();

            return (observed.Count(state => state.Method == MemberIdentity(parent).Method), tokens.Distinct().Count(),
                fingerprints.Distinct(StringComparer.Ordinal).Count(), cacheKeys.Distinct().Count(),
                complete && tokens.All(static token => token is not null) && cacheKeys.All(static key => key is not null),
                firstManifest == RenderCapsuleManifest(discovery.Items) && firstDiagnostics == RenderDiagnosticReport(discovery.Diagnostics), discovery);
        }
    }
}
