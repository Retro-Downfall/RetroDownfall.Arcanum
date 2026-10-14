using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

// A context nobody can follow to its source is still an instance of a type the program can make. Authored code makes a
// context in exactly two ways: the container builds it from the registrations of its type, and an allocation in analysed
// source builds it from options that source supplies (EF's design-time factory is run by EF tooling, never by the
// analysed process). The configuration of such an instance, whether EF is given a compiled model and which interceptors
// it holds, is therefore bounded by the union of the registrations and the allocations of its type, which is what a
// receiver of unknown provenance is judged by. A type with neither has no instance the analysis can speak for.
internal static partial class HostedGrimoireProducerInventory
{
    /// <summary>One allocation of an authored context in analysed source.</summary>
    internal sealed record ContextConstruction(CSharpCompilation Compilation, SyntaxTree Tree, BaseObjectCreationExpressionSyntax Creation, INamedTypeSymbol Type, bool DesignTimeOnly);

    /// <summary>What every way of making a context of one type supplies to its options.</summary>
    internal sealed record ContextUniverseResult(
        bool ModelSupplied,
        bool InterceptorsClosed,
        bool Configured,
        IReadOnlyList<(INamedTypeSymbol Type, Compilation Compilation)> Interceptors,
        int Registrations,
        int Constructions);

    /// <summary>What the universe of one context declared in an analysed compilation supplies, as the traversal reads it.</summary>
    internal static ContextUniverseResult ProbeContextUniverse(IReadOnlyList<CSharpCompilation> compilations, string contextMetadataName)
    {
        ProducerGraph graph = new(compilations, DefaultEvaluationEnvironmentMaximumMembers, DefaultEvaluationEnvironmentMaximumDepth, DefaultTraversalMaximumDepth, DefaultMaximumAnalyzedStatesPerRoot, false);

        foreach (CSharpCompilation compilation in compilations)
        {
            if (compilation.GetTypeByMetadataName(contextMetadataName) is { } context && context.Locations.Any(static location => location.IsInSource))
            {
                return graph.ContextUniverse(context, compilation);
            }
        }

        throw new InvalidOperationException("No analysed compilation declares " + contextMetadataName + ".");
    }

    /// <summary>The allocations of authored contexts in analysed source, and which of them only EF design-time tooling runs.</summary>
    internal static IReadOnlyList<ContextConstruction> ProbeContextConstructions(IReadOnlyList<CSharpCompilation> compilations) =>
        new InventoryFacts(compilations).ContextConstructions;

    private sealed partial class ProducerGraph
    {
        // Every allocation, explicit or target-typed, of an authored context in the analysed source. A property of the
        // analysed compilations alone, computed once per discovery (see InventoryFacts).
        internal static IReadOnlyList<ContextConstruction> FindContextConstructions(IReadOnlyList<CSharpCompilation> compilations, Func<CSharpCompilation, SyntaxTree, SemanticModel> modelOf)
        {
            List<ContextConstruction> found = [];

            Dictionary<INamedTypeSymbol, bool> factoryReferenced = new(SymbolEqualityComparer.Default);

            foreach (CSharpCompilation compilation in compilations)
            {
                foreach (SyntaxTree tree in compilation.SyntaxTrees)
                {
                    // The same trees the graph analyses: its own source, never generated build output.
                    if (!tree.FilePath.StartsWith("src/", StringComparison.Ordinal) || tree.FilePath.Contains("/obj/", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    SemanticModel model = modelOf(compilation, tree);

                    foreach (BaseObjectCreationExpressionSyntax creation in tree.GetRoot().DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
                    {
                        if (model.GetTypeInfo(creation).Type is not INamedTypeSymbol { TypeKind: TypeKind.Class } type
                            || !type.Locations.Any(static location => location.IsInSource)
                            || !IsExactEfDbContext(type))
                        {
                            continue;
                        }

                        found.Add(new(compilation, tree, creation, type, IsDesignTimeFactoryCreation(compilations, modelOf, model, creation, factoryReferenced)));
                    }
                }
            }

            return found;
        }

        // EF's design-time tooling instantiates an IDesignTimeDbContextFactory<T> by reflection, in its own process, to
        // scaffold and migrate. The factory's allocation makes a context the analysed host never sees only while nothing in
        // the analysed source names the factory: a reference to it is a way for analysed code to run the allocation.
        private static bool IsDesignTimeFactoryCreation(
            IReadOnlyList<CSharpCompilation> compilations,
            Func<CSharpCompilation, SyntaxTree, SemanticModel> modelOf,
            SemanticModel model,
            BaseObjectCreationExpressionSyntax creation,
            Dictionary<INamedTypeSymbol, bool> factoryReferenced)
        {
            if (creation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() is not { Identifier.ValueText: "CreateDbContext" } method
                || model.GetDeclaredSymbol(method) is not IMethodSymbol implementation
                || implementation.ContainingType is not { TypeKind: TypeKind.Class } factory)
            {
                return false;
            }

            bool implementsContract = false;

            foreach (INamedTypeSymbol contract in factory.AllInterfaces.Where(static contract => TypeKey(contract) == "Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory`1"
                && !contract.Locations.Any(static location => location.IsInSource)
                && AssemblyIdentityMatches(contract.ContainingAssembly.Identity, typeof(Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<>).Assembly.GetName())))
            {
                implementsContract |= contract.GetMembers("CreateDbContext").OfType<IMethodSymbol>().Any(member =>
                    SymbolEqualityComparer.Default.Equals(factory.FindImplementationForInterfaceMember(member), implementation));
            }

            if (!implementsContract)
            {
                return false;
            }

            if (!factoryReferenced.TryGetValue(factory, out bool referenced))
            {
                factoryReferenced[factory] = referenced = IsReferencedOutsideItself(compilations, modelOf, factory);
            }

            return !referenced;
        }

        // True when any analysed source names the type, or a member of it, outside the type's own declaration.
        private static bool IsReferencedOutsideItself(IReadOnlyList<CSharpCompilation> compilations, Func<CSharpCompilation, SyntaxTree, SemanticModel> modelOf, INamedTypeSymbol type)
        {
            foreach (CSharpCompilation compilation in compilations)
            {
                foreach (SyntaxTree tree in compilation.SyntaxTrees)
                {
                    if (!tree.FilePath.StartsWith("src/", StringComparison.Ordinal) || tree.FilePath.Contains("/obj/", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    SemanticModel? model = null;

                    foreach (SyntaxToken token in tree.GetRoot().DescendantTokens())
                    {
                        if (!token.IsKind(SyntaxKind.IdentifierToken)
                            || token.ValueText != type.Name
                            || token.Parent is not SimpleNameSyntax name
                            || type.DeclaringSyntaxReferences.Any(reference => reference.SyntaxTree == tree && reference.Span.Contains(name.Span)))
                        {
                            continue;
                        }

                        model ??= modelOf(compilation, tree);

                        SymbolInfo info = model.GetSymbolInfo(name);

                        // A name that binds to nothing might be the type, so it counts as a reference.
                        if (info.Symbol is null && info.CandidateSymbols.Length == 0)
                        {
                            return true;
                        }

                        foreach (ISymbol symbol in info.Symbol is null ? info.CandidateSymbols : [info.Symbol])
                        {
                            if (SymbolEqualityComparer.Default.Equals(symbol, type)
                                || SymbolEqualityComparer.Default.Equals(symbol.ContainingType, type))
                            {
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        private readonly Dictionary<BoundTypeIdentity, ContextUniverseResult> contextUniverses = [];

        /// <summary>
        /// The registrations and the allocations of one context type, together: the compiled model is supplied only if each
        /// of them supplies it, the interceptor set is the union of what each of them adds and is closed only if each of
        /// them is, and the provider is configured only if each of them configures it. An allocation the analysis cannot
        /// follow, in a place no member owns, leaves nothing proven; so does a type nothing can make.
        /// </summary>
        internal ContextUniverseResult ContextUniverse(INamedTypeSymbol type, Compilation compilation)
        {
            BoundTypeIdentity identity = TypeIdentity(type, compilation);

            if (contextUniverses.TryGetValue(identity, out ContextUniverseResult? cached))
            {
                return cached;
            }

            RegistrationFactsResult registrations = RegistrationFacts(type, compilation);

            bool modelSupplied = true;

            bool interceptorsClosed = true;

            bool configured = true;

            List<(INamedTypeSymbol Type, Compilation Compilation)> interceptors = [];

            int constructions = 0;

            if (registrations.Count != 0)
            {
                modelSupplied &= registrations.ModelSupplied;

                interceptorsClosed &= registrations.InterceptorsClosed;

                interceptors.AddRange(registrations.Interceptors);
            }

            foreach (ContextConstruction construction in inventoryFacts.ContextConstructions)
            {
                if (construction.DesignTimeOnly || !SameBoundType(construction.Type, construction.Compilation, type, compilation))
                {
                    continue;
                }

                constructions++;

                if (ConstructionMember(construction) is not { } caller
                    || AnalyzeContextCreation(caller, construction.Creation, type, compilation) is not { Understood: true } created)
                {
                    modelSupplied = false;

                    interceptorsClosed = false;

                    configured = false;

                    continue;
                }

                modelSupplied &= created.Builder.ModelSupplied;

                interceptorsClosed &= created.Builder.InterceptorsClosed;

                configured &= created.Configured;

                interceptors.AddRange(created.Builder.Interceptors);
            }

            ContextUniverseResult result = registrations.Count + constructions == 0
                ? new(false, false, false, [], 0, 0)
                : new(modelSupplied, interceptorsClosed, configured, interceptors, registrations.Count, constructions);

            contextUniverses.Add(identity, result);

            return result;
        }

        // The member whose body holds the allocation; none when it sits in a field, property or other initializer.
        private AuthoredMember? ConstructionMember(ContextConstruction construction)
        {
            ISymbol? symbol = inventoryFacts.SemanticModelOf(construction.Compilation, construction.Tree).GetEnclosingSymbol(construction.Creation.SpanStart);

            while (symbol is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction })
            {
                symbol = symbol.ContainingSymbol;
            }

            return symbol is IMethodSymbol method ? Resolve(method, construction.Compilation) : null;
        }

        // ---- The options a context constructor hands EF.

        private readonly Dictionary<ISymbol, bool> optionsForwarding = new(SymbolEqualityComparer.Default);

        /// <summary>
        /// EF sees the options the container or the allocation supplied only if every constructor of the context that takes
        /// them hands them, unchanged, to the base context. A constructor that builds other options, or rewrites its
        /// parameter, makes what the registration or the chain supplied say nothing about what EF holds. A context with no
        /// constructor that takes options proves nothing either.
        /// </summary>
        private bool ContextOptionsForwarded(INamedTypeSymbol type)
        {
            if (optionsForwarding.TryGetValue(type, out bool cached))
            {
                return cached;
            }

            IMethodSymbol[] constructors = [.. type.InstanceConstructors.Where(static constructor => constructor.Parameters.Any(static parameter => IsDbContextOptions(parameter.Type, null)))];

            bool forwarded = constructors.Length != 0
                && constructors.All(constructor => constructor.Parameters
                    .Where(static parameter => IsDbContextOptions(parameter.Type, null))
                    .All(parameter => ConstructorForwardsOptions(constructor, parameter, 0)));

            optionsForwarding[type] = forwarded;

            return forwarded;
        }
    }
}
