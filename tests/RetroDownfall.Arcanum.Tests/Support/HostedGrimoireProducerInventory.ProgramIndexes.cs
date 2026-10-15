using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RetroDownfall.Arcanum.Tests.Support;

// Indexes of the program a graph holds, built once from sites and members that do not change after the graph is made. A query that wants the
// sites of one method, or the constructors of one type, reads a group instead of binding every site or member and comparing, and it reads the group
// in the order the pass it replaces would have met them.
internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        private readonly Dictionary<IMethodSymbol, MethodIdentity> methodIdentities = new(ReferenceEqualityComparer.Instance);

        private MethodIdentity Identity(IMethodSymbol method)
        {
            if (!methodIdentities.TryGetValue(method, out MethodIdentity identity))
            {
                methodIdentities.Add(method, identity = new(method.ContainingAssembly.Identity, MethodKey(method)));
            }

            return identity;
        }

        // The invocations, object creations and constructor initializers of the program grouped by the identity of the method they bind to,
        // each group in the order the graph read the sites. The sites are read once, when the graph is made, and never change, so a group is
        // what a pass over all of them that compares the identity of each bound method would select, in the order it would select it.
        private Dictionary<MethodIdentity, List<(InvocationExpressionSyntax Call, SemanticModel Model)>>? invocationsByTarget;

        private Dictionary<MethodIdentity, List<(BaseObjectCreationExpressionSyntax Creation, SemanticModel Model)>>? creationsByTarget;

        private Dictionary<MethodIdentity, List<(ConstructorInitializerSyntax Initializer, SemanticModel Model)>>? initializersByTarget;

        private Dictionary<MethodIdentity, List<(TSite Site, SemanticModel Model)>> GroupByTarget<TSite>(List<(TSite Site, SemanticModel Model)> sites)
            where TSite : SyntaxNode
        {
            Dictionary<MethodIdentity, List<(TSite Site, SemanticModel Model)>> groups = [];

            foreach ((TSite site, SemanticModel model) in sites)
            {
                if (model.GetSymbolInfo(site).Symbol is not IMethodSymbol target)
                {
                    continue;
                }

                MethodIdentity identity = Identity(target);

                if (!groups.TryGetValue(identity, out List<(TSite Site, SemanticModel Model)>? group))
                {
                    groups.Add(identity, group = []);
                }

                group.Add((site, model));
            }

            return groups;
        }

        private IReadOnlyList<(InvocationExpressionSyntax Call, SemanticModel Model)> InvocationsOf(IMethodSymbol method) =>
            (invocationsByTarget ??= GroupByTarget(invocations)).TryGetValue(Identity(method), out List<(InvocationExpressionSyntax Call, SemanticModel Model)>? group) ? group : [];

        private IReadOnlyList<(BaseObjectCreationExpressionSyntax Creation, SemanticModel Model)> CreationsOf(IMethodSymbol method) =>
            (creationsByTarget ??= GroupByTarget(objectCreations)).TryGetValue(Identity(method), out List<(BaseObjectCreationExpressionSyntax Creation, SemanticModel Model)>? group) ? group : [];

        private IReadOnlyList<(ConstructorInitializerSyntax Initializer, SemanticModel Model)> InitializersOf(IMethodSymbol method) =>
            (initializersByTarget ??= GroupByTarget(constructorInitializers)).TryGetValue(Identity(method), out List<(ConstructorInitializerSyntax Initializer, SemanticModel Model)>? group) ? group : [];

        // The constructors of the program by the type that declares them, in the order the graph holds its members. The members are fixed once
        // the graph is made.
        private Dictionary<INamedTypeSymbol, List<AuthoredMember>>? constructorsByType;

        private IReadOnlyList<AuthoredMember> ConstructorsOf(INamedTypeSymbol type)
        {
            if (constructorsByType is null)
            {
                constructorsByType = new(SymbolEqualityComparer.Default);

                foreach (AuthoredMember candidate in AuthoredMembers)
                {
                    if (candidate.Symbol.MethodKind != MethodKind.Constructor)
                    {
                        continue;
                    }

                    if (!constructorsByType.TryGetValue(candidate.Symbol.ContainingType, out List<AuthoredMember>? group))
                    {
                        constructorsByType.Add(candidate.Symbol.ContainingType, group = []);
                    }

                    group.Add(candidate);
                }
            }

            return constructorsByType.TryGetValue(type, out List<AuthoredMember>? constructors) ? constructors : [];
        }

        // What a constructor does to a field: whether it makes a ref alias of the field or passes it by ref or out, and the values it assigns to
        // the field outside any lambda or local function. The constructor's syntax, its model and the field decide both.
        private readonly Dictionary<(SyntaxNode Constructor, IFieldSymbol Field), (bool Aliased, ExpressionSyntax[] Assigned)> constructorFieldWrites = new(ConstructorFieldWriteComparer.Instance);

        private sealed class ConstructorFieldWriteComparer : IEqualityComparer<(SyntaxNode Constructor, IFieldSymbol Field)>
        {
            internal static readonly ConstructorFieldWriteComparer Instance = new();

            public bool Equals((SyntaxNode Constructor, IFieldSymbol Field) left, (SyntaxNode Constructor, IFieldSymbol Field) right) =>
                ReferenceEquals(left.Constructor, right.Constructor) && SymbolEqualityComparer.Default.Equals(left.Field, right.Field);

            public int GetHashCode((SyntaxNode Constructor, IFieldSymbol Field) key) =>
                HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(key.Constructor), SymbolEqualityComparer.Default.GetHashCode(key.Field));
        }

        private (bool Aliased, ExpressionSyntax[] Assigned) ConstructorFieldWrites(AuthoredMember constructor, IFieldSymbol field)
        {
            if (constructorFieldWrites.TryGetValue((constructor.Syntax, field), out (bool Aliased, ExpressionSyntax[] Assigned) known))
            {
                return known;
            }

            bool aliased = constructor.Syntax.DescendantNodesAndSelf().Any(node =>
                node is RefExpressionSyntax alias
                    && SymbolEqualityComparer.Default.Equals(
                        constructor.Model.GetSymbolInfo(alias.Expression).Symbol,
                        field)
                || node is ArgumentSyntax argument
                    && argument.RefKindKeyword.Kind() is SyntaxKind.RefKeyword or SyntaxKind.OutKeyword
                    && SymbolEqualityComparer.Default.Equals(
                        constructor.Model.GetSymbolInfo(argument.Expression).Symbol,
                        field));

            ExpressionSyntax[] assigned = aliased
                ? []
                : [.. constructor.Syntax.DescendantNodesAndSelf(
                        node => node == constructor.Syntax
                            || node is not AnonymousFunctionExpressionSyntax
                                and not LocalFunctionStatementSyntax)
                    .OfType<AssignmentExpressionSyntax>()
                    .Where(assignment => assignment.IsKind(
                            SyntaxKind.SimpleAssignmentExpression)
                        && SymbolEqualityComparer.Default.Equals(
                            constructor.Model.GetSymbolInfo(
                                assignment.Left).Symbol,
                            field))
                    .Select(static assignment => assignment.Right)];

            constructorFieldWrites.Add((constructor.Syntax, field), (aliased, assigned));

            return (aliased, assigned);
        }

        // Fixture seam: a sample of the queries the indexes and the per-graph memos answer, each beside the pass or the computation it replaces.
        // The call sites of a sample of the methods the program binds, the constructors of a sample of types, the implementers of a sample
        // of interface methods and the written-ness of a sample of parameters, with the queries that differ.
        internal (int Calls, int Constructors, int Implementers, int Parameters, int Mutations, IReadOnlyList<string> Mismatches) ProbeProgramIndexes(int sample)
        {
            List<string> mismatches = [];

            static string Sites<TSite>(IEnumerable<(TSite Site, SemanticModel Model)> sites) where TSite : SyntaxNode =>
                string.Join(";", sites.Select(static site => site.Site.SyntaxTree.FilePath + ":" + site.Site.SpanStart));

            MethodIdentity Defined(IMethodSymbol target) => new(target.ContainingAssembly.Identity, MethodKey(target));

            IEnumerable<IMethodSymbol> Targets<TSite>(IEnumerable<(TSite Site, SemanticModel Model)> sites) where TSite : SyntaxNode =>
                sites.Select(static site => site.Model.GetSymbolInfo(site.Site).Symbol).OfType<IMethodSymbol>();

            int calls = 0;

            IMethodSymbol[] invoked = [.. Targets(invocations).DistinctBy(static target => target, SymbolEqualityComparer.Default)];

            foreach (IMethodSymbol target in invoked.Where((_, position) => position % Math.Max(1, invoked.Length / sample) == 0))
            {
                calls++;

                string indexed = Sites(InvocationsOf(target));

                string defined = Sites(invocations.Where(site => site.Model.GetSymbolInfo(site.Call).Symbol is IMethodSymbol bound && Defined(bound) == Defined(target)));

                if (indexed != defined)
                {
                    mismatches.Add("invocations of " + Normalize(target));
                }
            }

            foreach (IMethodSymbol target in Targets(objectCreations).DistinctBy(static target => target, SymbolEqualityComparer.Default).Take(sample))
            {
                calls++;

                if (Sites(CreationsOf(target)) != Sites(objectCreations.Where(site => site.Model.GetSymbolInfo(site.Creation).Symbol is IMethodSymbol bound && Defined(bound) == Defined(target))))
                {
                    mismatches.Add("creations of " + Normalize(target));
                }
            }

            foreach (IMethodSymbol target in Targets(constructorInitializers).DistinctBy(static target => target, SymbolEqualityComparer.Default).Take(sample))
            {
                calls++;

                if (Sites(InitializersOf(target)) != Sites(constructorInitializers.Where(site => site.Model.GetSymbolInfo(site.Initializer).Symbol is IMethodSymbol bound && Defined(bound) == Defined(target))))
                {
                    mismatches.Add("initializers of " + Normalize(target));
                }
            }

            int constructors = 0;

            INamedTypeSymbol[] declaring = [.. AuthoredMembers.Where(static member => member.Symbol.MethodKind == MethodKind.Constructor).Select(static member => member.Symbol.ContainingType).Distinct(SymbolEqualityComparer.Default).OfType<INamedTypeSymbol>()];

            foreach (INamedTypeSymbol type in declaring.Where((_, position) => position % Math.Max(1, declaring.Length / sample) == 0))
            {
                constructors++;

                IEnumerable<string> Keys(IEnumerable<AuthoredMember> members) => members.Select(member => MemberIdentity(member).ToString());

                if (!Keys(ConstructorsOf(type)).SequenceEqual(Keys(AuthoredMembers.Where(member => member.Symbol.MethodKind == MethodKind.Constructor && SymbolEqualityComparer.Default.Equals(member.Symbol.ContainingType, type)))))
                {
                    mismatches.Add("constructors of " + TypeKey(type));
                }
            }

            int implementers = 0;

            IMethodSymbol[] interfaceMethods = [.. invoked.Where(static method => method.ContainingType.TypeKind == TypeKind.Interface)];

            foreach (IMethodSymbol method in interfaceMethods.Where((_, position) => position % Math.Max(1, interfaceMethods.Length / sample) == 0))
            {
                implementers++;

                IEnumerable<string> Keys(IEnumerable<AuthoredMember> members) => members.Select(member => MemberIdentity(member).ToString());

                if (!Keys(ResolveReceiverTargets(method, analysedCompilations[0])).SequenceEqual(Keys(InterfaceReceiverTargetsOf(method))))
                {
                    mismatches.Add("implementers of " + Normalize(method));
                }
            }

            int parameters = 0;

            IParameterSymbol[] declared = [.. AuthoredMembers.SelectMany(static member => member.Symbol.Parameters).DistinctBy(static parameter => parameter, SymbolEqualityComparer.Default)];

            foreach (IParameterSymbol parameter in declared.Where((_, position) => position % Math.Max(1, declared.Length / sample) == 0))
            {
                parameters++;

                if (ParameterNeverWritten(parameter) != ComputeParameterNeverWritten(parameter)
                    || ConstructorParameterNeverWritten(parameter) != ComputeConstructorParameterNeverWritten(parameter))
                {
                    mismatches.Add("parameter " + parameter.Name + " of " + Normalize(parameter.ContainingSymbol));
                }
            }

            int mutations = 0;

            AuthoredMember[] withLocals = [.. AuthoredMembers.Where(static member => member.Syntax.DescendantNodes().OfType<VariableDeclaratorSyntax>().Any())];

            foreach (AuthoredMember member in withLocals.Where((_, position) => position % Math.Max(1, withLocals.Length / (sample * 4)) == 0))
            {
                foreach (ILocalSymbol local in member.Syntax.DescendantNodes().OfType<VariableDeclaratorSyntax>().Select(declarator => member.Model.GetDeclaredSymbol(declarator)).OfType<ILocalSymbol>().Take(3))
                {
                    mutations++;

                    int before = member.Syntax.Span.End;

                    if (InvokedCallableMayMutateSymbol(member, local, before) != ComputeInvokedCallableMayMutateSymbol(member, local, before, int.MinValue)
                        || InvokedCallableMayMutateSymbol(member, local, before, member.Syntax.SpanStart) != ComputeInvokedCallableMayMutateSymbol(member, local, before, member.Syntax.SpanStart))
                    {
                        mismatches.Add("mutation of " + local.Name + " in " + Normalize(member.Symbol));
                    }
                }
            }

            return (calls, constructors, implementers, parameters, mutations, mismatches);
        }
    }
}
