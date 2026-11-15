using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    // Qualification probe AFTER the proof is integrated. Not a production permission:
    // build the particular registered reader context, then exercise the same cleanup hook.
    internal static (bool RegisteredAbsent, bool Handler, bool Client, bool Request, IReadOnlyList<HostedProducerInventoryDiagnostic> Diagnostics)
        ProbeProductionSpendHttpConstruction() => ProbeSpendHttpConstruction(ProductionCompilations);

    internal static (bool RegisteredAbsent, bool Handler, bool Client, bool Request, IReadOnlyList<HostedProducerInventoryDiagnostic> Diagnostics)
        ProbeSpendHttpConstruction(IReadOnlyList<CSharpCompilation> compilations) => HostedProducerRootWorkers.OnAnalysisStack(() =>
        {

            HostedProducerPremiseLedger ledger = VerifyPremises(compilations, ProductionPremises);

            if (ledger.Violations.Count != 0) return (false, false, false, false, (IReadOnlyList<HostedProducerInventoryDiagnostic>)ledger.Violations.ToArray());

            return new ProducerGraph(compilations, DefaultEvaluationEnvironmentMaximumMembers, DefaultEvaluationEnvironmentMaximumDepth,
                DefaultTraversalMaximumDepth, DefaultMaximumAnalyzedStatesPerRoot, false, premises: ledger).ProbeRegisteredSpendHttpConstruction();
        });

    private sealed partial class ProducerGraph
    {
        internal (bool RegisteredAbsent, bool Handler, bool Client, bool Request, IReadOnlyList<HostedProducerInventoryDiagnostic> Diagnostics)
            ProbeRegisteredSpendHttpConstruction()
        {
            const string readerType = "RetroDownfall.Arcanum.Infrastructure.Guild.GuildOpenRouterSpendReader";

            AuthoredMember wrapper = AuthoredMembers.Single(member => TypeKey(member.Symbol.ContainingType) == readerType && member.Symbol.Name == "ReadAsync");

            IParameterSymbol transport = wrapper.Symbol.ContainingType.InstanceConstructors.Single(constructor => constructor.Parameters.Length == 2).Parameters[1];

            if (!TryExactAbsentOptionalDiDefault(wrapper, transport, out BoundValueSource absent))
                return (false, false, false, false, diagnostics.ToArray());

            // This probe explicitly models the verified registered activation. Runtime
            // proof never overwrites a caller-supplied transport with that default.
            wrapper = wrapper with
            {
                ValueBindings = new Dictionary<ISymbol, BoundValueSource>(SymbolEqualityComparer.Default) { [transport] = absent },
            };

            InvocationExpressionSyntax observationCall = wrapper.Syntax.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
                .Single(call => wrapper.Model.GetSymbolInfo(call).Symbol is IMethodSymbol method && method.Name == "ReadAsync"
                    && TypeKey(method.ContainingType) == "RetroDownfall.Arcanum.Infrastructure.Guild.GuildSpendObservation");

            AuthoredMember observation = BindInvocationContext(wrapper, observationCall,
                Resolve((IMethodSymbol)wrapper.Model.GetSymbolInfo(observationCall).Symbol!, wrapper.Model.Compilation)!);

            bool Resource(AuthoredMember owner, string name)
            {
                VariableDeclaratorSyntax declaration = owner.Syntax.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                    .Single(declaration => declaration.Identifier.ValueText == name);

                ILocalSymbol local = (ILocalSymbol)owner.Model.GetDeclaredSymbol(declaration)!;

                return declaration.Initializer is { } initializer && TryResolveClosedHttpConstructionCleanup(owner, initializer.Value, local.Type,
                    "Dispose", out ExternalCleanupResolution? effect) && effect is not null;
            }

            bool handler = Resource(observation, "handler");

            bool client = Resource(observation, "client");

            InvocationExpressionSyntax sendCall = observation.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Single(call => observation.Model.GetSymbolInfo(call).Symbol is IMethodSymbol method && method.Name == "SendAsync"
                    && TypeKey(method.ContainingType) == "RetroDownfall.Arcanum.Infrastructure.Guild.GuildSpendSourceCredential.VerifiedSource");

            AuthoredMember sender = BindInvocationContext(observation, sendCall,
                Resolve((IMethodSymbol)observation.Model.GetSymbolInfo(sendCall).Symbol!, observation.Model.Compilation)!);

            return (true, handler, client, Resource(sender, "request"), diagnostics.ToArray());
        }
    }
}
