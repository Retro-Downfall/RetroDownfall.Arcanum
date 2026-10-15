using System.Collections.Concurrent;

using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

namespace RetroDownfall.Arcanum.Tests.Support;

// Facts about the analysed compilations alone. A fact here does not depend on any traversal state, binding
// environment or root, so it is computed at most once per discovery and every isolated root worker graph
// shares that one verdict. Each is computed lazily, by a scan that does not depend on which worker asks first:
// a discovery that never demands a fact never pays for it.
internal static partial class HostedGrimoireProducerInventory
{
    private sealed class InventoryFacts
    {
        private readonly ConcurrentDictionary<SyntaxTree, Lazy<SemanticModel>> semanticModels = new(ReferenceEqualityComparer.Instance);

        private readonly Lazy<bool> dbContextConnectionReplacement;

        private readonly Lazy<string?> authoredLoggerType;

        private readonly Lazy<string?> authoredIdentityResolutionInterceptor;

        private readonly Lazy<IReadOnlyList<ContextConstruction>> contextConstructions;

        private readonly Lazy<IReadOnlyList<ConnectionStateSubscription>> connectionStateSubscriptions;

        private readonly Lazy<bool> efEventDataSpelled;

        internal InventoryFacts(IReadOnlyList<CSharpCompilation> compilations)
        {
            dbContextConnectionReplacement = new(
                () => ProducerGraph.ScanForDbContextConnectionReplacement(compilations, SemanticModelOf),
                LazyThreadSafetyMode.ExecutionAndPublication);

            authoredLoggerType = new(
                () => FindAuthoredLoggerType(compilations),
                LazyThreadSafetyMode.ExecutionAndPublication);

            authoredIdentityResolutionInterceptor = new(
                () => FindAuthoredIdentityResolutionInterceptor(compilations),
                LazyThreadSafetyMode.ExecutionAndPublication);

            contextConstructions = new(
                () => ProducerGraph.FindContextConstructions(compilations, SemanticModelOf),
                LazyThreadSafetyMode.ExecutionAndPublication);

            connectionStateSubscriptions = new(
                () => ProducerGraph.FindConnectionStateSubscriptions(compilations, SemanticModelOf),
                LazyThreadSafetyMode.ExecutionAndPublication);

            efEventDataSpelled = new(
                () => FindSpelledEfEventData(compilations, SemanticModelOf),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        // One semantic model per tree for the whole discovery, shared by every scan and every isolated root worker graph.
        // A model binds lazily and is safe to read from several threads, so what one scan or worker has bound, the next
        // finds already bound instead of binding it again on a model of its own.
        internal SemanticModel SemanticModelOf(CSharpCompilation compilation, SyntaxTree tree) =>
            semanticModels.GetOrAdd(tree, key => new Lazy<SemanticModel>(() => CompilationSemanticModels.GetValue(compilation, static _ => new()).GetOrAdd(key, compilation), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

        // The semantic model of a tree in a compilation is a function of that compilation and that tree alone: the references the
        // compilation was made with and the source of the tree decide every answer the model gives. A model is therefore made once for
        // a compilation object and a tree object, and every discovery over that compilation reads the same one, which finds bound what an
        // earlier discovery bound. A compilation made from another (a replaced tree or reference) is another key and gets models of its
        // own; the models of a compilation the process lets go of are let go with it.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CSharpCompilation, CompilationModels> CompilationSemanticModels = new();

        private sealed class CompilationModels
        {
            private readonly ConcurrentDictionary<SyntaxTree, Lazy<SemanticModel>> models = new(ReferenceEqualityComparer.Instance);

            internal SemanticModel GetOrAdd(SyntaxTree tree, CSharpCompilation compilation) =>
                models.GetOrAdd(tree, key => new Lazy<SemanticModel>(() => compilation.GetSemanticModel(key), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }

        // True when any analysed source name binds a member that can replace a built context's connection or
        // provider services (H3). Whole-tree and deterministic: every simple name of every analysed tree is read.
        internal bool DbContextConnectionReplacement => dbContextConnectionReplacement.Value;

        // The first authored type that implements a logger contract (ILogger, ILoggerProvider, ILoggerFactory), or null.
        // EF reports its operations to the application's loggers, so such a type could receive EF's events.
        internal string? AuthoredLoggerType => authoredLoggerType.Value;

        // The first authored type that implements EF's identity-resolution interceptor contract, or null. EF raises it when it
        // starts tracking an instance whose key is already tracked, which happens at operations this analysis does not enter.
        internal string? AuthoredIdentityResolutionInterceptor => authoredIdentityResolutionInterceptor.Value;

        // Every allocation of an authored DbContext in the analysed source. Whole-tree and deterministic, so every isolated
        // root worker shares the one list rather than reading the trees again.
        internal IReadOnlyList<ContextConstruction> ContextConstructions => contextConstructions.Value;

        // Every subscription to DbConnection.StateChange in the analysed source: the closed set of handlers authored code can
        // attach to a connection.
        internal IReadOnlyList<ConnectionStateSubscription> ConnectionStateSubscriptions => connectionStateSubscriptions.Value;

        // True when any analysed source spells the name of an EF event-data type in a position that could create one or
        // derive from one. When false, every event-data value is an instance Entity Framework itself raised.
        internal bool EfEventDataSpelled => efEventDataSpelled.Value;
    }
}
