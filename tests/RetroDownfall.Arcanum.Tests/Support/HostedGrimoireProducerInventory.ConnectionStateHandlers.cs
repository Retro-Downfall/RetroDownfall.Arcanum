using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RetroDownfall.Arcanum.Tests.Support;

// A handler subscribed to DbConnection.StateChange is authored code nothing calls: the connection raises the event when it
// opens, closes or changes state, so the handler runs inside that operation. The subscription itself runs no handler and
// holds no work, so it is reviewed wiring only while every handler in the analysed source can be named. The handlers are then
// entered, under the frontiers live there, at the operations that change a connection's state: an authored Open, OpenAsync,
// Close or CloseAsync on a DbConnection, and every EF operation that raises the connection contract.
internal static partial class HostedGrimoireProducerInventory
{
    /// <summary>One <c>+=</c> on <c>DbConnection.StateChange</c> in analysed source.</summary>
    internal sealed record ConnectionStateSubscription(CSharpCompilation Compilation, SyntaxTree Tree, AssignmentExpressionSyntax Assignment);

    /// <summary>The subscriptions to DbConnection.StateChange in the analysed source, whole-tree and deterministic.</summary>
    internal static IReadOnlyList<ConnectionStateSubscription> ProbeConnectionStateSubscriptions(IReadOnlyList<CSharpCompilation> compilations) =>
        new InventoryFacts(compilations).ConnectionStateSubscriptions;

    private sealed partial class ProducerGraph
    {
        // Every subscription to the event, found by the name of the event first so that only a handful of names is bound.
        internal static IReadOnlyList<ConnectionStateSubscription> FindConnectionStateSubscriptions(IReadOnlyList<CSharpCompilation> compilations, Func<CSharpCompilation, SyntaxTree, SemanticModel> modelOf)
        {
            List<ConnectionStateSubscription> found = [];

            foreach (CSharpCompilation compilation in compilations)
            {
                foreach (SyntaxTree tree in compilation.SyntaxTrees)
                {
                    // The same trees the graph analyses: its own source, never generated build output.
                    if (!tree.FilePath.StartsWith("src/", StringComparison.Ordinal) || tree.FilePath.Contains("/obj/", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    SemanticModel? model = null;

                    foreach (AssignmentExpressionSyntax assignment in tree.GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>())
                    {
                        if (!assignment.IsKind(SyntaxKind.AddAssignmentExpression)
                            || (assignment.Left switch
                            {
                                MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
                                MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
                                IdentifierNameSyntax name => name.Identifier.ValueText,
                                _ => string.Empty,
                            }) != "StateChange")
                        {
                            continue;
                        }

                        model ??= modelOf(compilation, tree);

                        SymbolInfo info = model.GetSymbolInfo(assignment.Left);

                        // A name that binds to nothing might be the event, so it counts.
                        if (info.Symbol is null && info.CandidateSymbols.Length == 0
                            || (info.Symbol is null ? info.CandidateSymbols : [info.Symbol]).Any(static symbol => symbol is IEventSymbol evt && IsExactDbConnectionStateChange(evt)))
                        {
                            found.Add(new(compilation, tree, assignment));
                        }
                    }
                }
            }

            return found;
        }

        internal static bool IsExactDbConnectionStateChange(IEventSymbol evt)
        {
            IEventSymbol definition = evt.OriginalDefinition;

            while (definition.OverriddenEvent is { } overridden)
            {
                definition = overridden.OriginalDefinition;
            }

            return definition.Name == "StateChange"
                && TypeKey(definition.ContainingType) == "System.Data.Common.DbConnection"
                && !definition.Locations.Any(static location => location.IsInSource)
                && AssemblyIdentityMatches(definition.ContainingAssembly.Identity, typeof(System.Data.Common.DbConnection).Assembly.GetName());
        }

        // An operation whose own work changes the state of the connection it is called on.
        private static bool IsConnectionStateChangingCall(IMethodSymbol method)
        {
            IMethodSymbol definition = method.ReducedFrom ?? method;

            if (definition.Name is not ("Open" or "OpenAsync" or "Close" or "CloseAsync"))
            {
                return false;
            }

            for (INamedTypeSymbol? current = definition.ContainingType; current is not null; current = current.BaseType)
            {
                if (TypeKey(current) == "System.Data.Common.DbConnection"
                    && !current.Locations.Any(static location => location.IsInSource)
                    && AssemblyIdentityMatches(current.ContainingAssembly.Identity, typeof(System.Data.Common.DbConnection).Assembly.GetName()))
                {
                    return true;
                }
            }

            return false;
        }

        private (bool Closed, IReadOnlyList<AuthoredMember> Handlers)? connectionStateHandlers;

        /// <summary>The authored handlers of every subscription; closed only if each subscription names a handler.</summary>
        private (bool Closed, IReadOnlyList<AuthoredMember> Handlers) ConnectionStateHandlers()
        {
            if (connectionStateHandlers is { } cached)
            {
                return cached;
            }

            bool closed = true;

            List<AuthoredMember> handlers = [];

            foreach (ConnectionStateSubscription subscription in inventoryFacts.ConnectionStateSubscriptions)
            {
                SemanticModel model = inventoryFacts.SemanticModelOf(subscription.Compilation, subscription.Tree);

                if (!IsDelegateExpression(model, subscription.Assignment.Right)
                    || ResolveCallable(subscription.Assignment.Right, model) is not { } handler)
                {
                    closed = false;

                    continue;
                }

                handlers.Add(handler);
            }

            connectionStateHandlers = (closed, handlers);

            return connectionStateHandlers.Value;
        }

        /// <summary>
        /// The subscription of a handler to the event, or the removal of one, runs none: while every handler can be named the
        /// wiring is reviewed, and the handlers are entered where the state changes. A handler that cannot be named leaves the
        /// subscription to the generic refusal of an external event.
        /// </summary>
        private bool IsReviewedConnectionStateWiring(IEventSymbol evt) =>
            IsExactDbConnectionStateChange(evt) && ConnectionStateHandlers().Closed;

        /// <summary>
        /// Every authored handler of DbConnection.StateChange, entered under the frontiers live at the operation that changes
        /// a connection's state. When some handler cannot be named the operation is refused: the code it would run is unknown.
        /// </summary>
        private TraversalEvidence TraverseConnectionStateHandlers(
            SyntaxNode node,
            string callee,
            string rootType,
            string operationId,
            HashSet<TraversalStateIdentity> active,
            bool lifecycle,
            string? workAdmitted,
            string? effectGroup,
            string? recoveryEffect)
        {
            TraversalEvidence evidence = TraversalEvidence.NoEvidence;

            if (inventoryFacts.ConnectionStateSubscriptions.Count == 0)
            {
                return evidence;
            }

            (bool closed, IReadOnlyList<AuthoredMember> handlers) = ConnectionStateHandlers();

            if (!closed)
            {
                RefuseTraversalCompletion();

                diagnostics.Add(new(
                    "HOSTED_SITE_UNCLASSIFIED",
                    Location(node),
                    callee + "; a handler of DbConnection.StateChange cannot be named, so the code this state change would run is unknown."));

                return evidence;
            }

            HashSet<GraphMemberIdentity> entered = [];

            foreach (AuthoredMember handler in handlers)
            {
                if (!entered.Add(MemberIdentity(handler)))
                {
                    continue;
                }

                evidence = MergeEvidence(
                    evidence,
                    Traverse(
                        handler,
                        rootType,
                        operationId + "/statechange@" + Location(node) + "/" + handler.Symbol.Name,
                        active,
                        lifecycle,
                        workAdmitted,
                        effectGroup,
                        completionOwned: true,
                        recoveryEffect: workAdmitted is not null && effectGroup is not null ? recoveryEffect : null));
            }

            return evidence;
        }
    }
}
