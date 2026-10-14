using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

// Interceptor and logger callbacks. EF hands its events to the interceptors registered on a context at the operations
// that raise them, and to the loggers of the application's logger factory while it works. Those callbacks are authored
// code that nothing calls, so each authored callback is traversed at the EF operation that raises its contract, under the
// frontiers live at that operation, using the interceptor set the registration closes. An interceptor set that cannot be
// closed, a context whose registration cannot be followed, and an authored logger that EF's events could reach are
// refused rather than assumed empty.
internal static partial class HostedGrimoireProducerInventory
{
    [Flags]
    private enum EfInterception : byte
    {
        None = 0,

        // ConnectionCreating and ConnectionCreated: raised when EF creates the DbConnection object.
        ConnectionCreation = 1,

        // The rest of IDbConnectionInterceptor that an operation raises while it works: opening, opened, closing, closed,
        // failed, canceled. Disposing and disposed are raised when the context is disposed, never by an operation.
        Connection = 2,

        Command = 4,

        Transaction = 8,

        SaveChanges = 16,

        Query = 32,

        // Raised when a context is disposed: the connection is closed if it is still open, then disposed.
        ConnectionDisposal = 64,
    }

    private static readonly IReadOnlyDictionary<EfInterception, string[]> EfInterceptionContracts = new Dictionary<EfInterception, string[]>
    {
        [EfInterception.ConnectionCreation] = ["Microsoft.EntityFrameworkCore.Diagnostics.IDbConnectionInterceptor"],
        [EfInterception.Connection] = ["Microsoft.EntityFrameworkCore.Diagnostics.IDbConnectionInterceptor"],
        [EfInterception.ConnectionDisposal] = ["Microsoft.EntityFrameworkCore.Diagnostics.IDbConnectionInterceptor"],
        [EfInterception.Command] = ["Microsoft.EntityFrameworkCore.Diagnostics.IDbCommandInterceptor"],
        [EfInterception.Transaction] = ["Microsoft.EntityFrameworkCore.Diagnostics.IDbTransactionInterceptor"],
        [EfInterception.SaveChanges] = ["Microsoft.EntityFrameworkCore.Diagnostics.ISaveChangesInterceptor"],
        [EfInterception.Query] =
        [
            "Microsoft.EntityFrameworkCore.Diagnostics.IMaterializationInterceptor",
            "Microsoft.EntityFrameworkCore.Diagnostics.IQueryExpressionInterceptor",
        ],
    };

    private const string EfInterceptorMarker = "Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor";

    // Every interceptor contract Entity Framework ships, by what this analysis does with an interceptor that implements it:
    // "Entered" contracts have their authored callbacks traversed at the operations that raise them; "Marker" contracts
    // declare no callback; "Refused" contracts are raised by triggers the analysis does not enumerate (the model is built,
    // an instance already tracked is tracked again), so an interceptor implementing one is refused at the operations that
    // use its context. Anything else is "Unknown", which is refused the same way: the census fixture reads the contracts
    // from the assemblies, so a contract a later version adds cannot be ignored.
    internal static string EfInterceptorContractVerdict(string contract) => contract switch
    {
        "Microsoft.EntityFrameworkCore.Diagnostics.IDbConnectionInterceptor"
            or "Microsoft.EntityFrameworkCore.Diagnostics.IDbCommandInterceptor"
            or "Microsoft.EntityFrameworkCore.Diagnostics.IDbTransactionInterceptor"
            or "Microsoft.EntityFrameworkCore.Diagnostics.ISaveChangesInterceptor"
            or "Microsoft.EntityFrameworkCore.Diagnostics.IMaterializationInterceptor"
            or "Microsoft.EntityFrameworkCore.Diagnostics.IQueryExpressionInterceptor" => "Entered",
        EfInterceptorMarker or "Microsoft.EntityFrameworkCore.Diagnostics.ISingletonInterceptor" => "Marker",
        "Microsoft.EntityFrameworkCore.Diagnostics.IInstantiationBindingInterceptor"
            or "Microsoft.EntityFrameworkCore.Diagnostics.IIdentityResolutionInterceptor" => "Refused",
        _ => "Unknown",
    };

    private const string EfIdentityResolutionContract = "Microsoft.EntityFrameworkCore.Diagnostics.IIdentityResolutionInterceptor";

    // The first authored type in the analysed source that implements EF's identity-resolution contract, or null. A property
    // of the analysed compilations alone, computed once per discovery (see InventoryFacts).
    internal static string? FindAuthoredIdentityResolutionInterceptor(IReadOnlyList<CSharpCompilation> compilations)
    {
        foreach (CSharpCompilation compilation in compilations)
        {
            foreach (INamedTypeSymbol type in SourceTypes(compilation.Assembly.GlobalNamespace).OrderBy(static type => type.ToDisplayString(), StringComparer.Ordinal))
            {
                if (type.TypeKind is TypeKind.Class or TypeKind.Struct && type.AllInterfaces.Any(static contract => TypeKey(contract) == EfIdentityResolutionContract))
                {
                    return type.ToDisplayString();
                }
            }
        }

        return null;
    }

    // The logger contracts EF's events reach through the application's logger factory.
    private static readonly IReadOnlySet<string> EfLoggerContracts = new HashSet<string>(StringComparer.Ordinal)
    {
        "Microsoft.Extensions.Logging.ILogger",
        "Microsoft.Extensions.Logging.ILogger`1",
        "Microsoft.Extensions.Logging.ILoggerProvider",
        "Microsoft.Extensions.Logging.ILoggerFactory",
    };

    // The first authored type in the analysed source that implements a logger contract, or null. A property of the
    // analysed compilations alone, computed once per discovery (see InventoryFacts).
    internal static string? FindAuthoredLoggerType(IReadOnlyList<CSharpCompilation> compilations)
    {
        foreach (CSharpCompilation compilation in compilations)
        {
            foreach (INamedTypeSymbol type in SourceTypes(compilation.Assembly.GlobalNamespace).OrderBy(static type => type.ToDisplayString(), StringComparer.Ordinal))
            {
                if (type.TypeKind is TypeKind.Class or TypeKind.Struct && type.AllInterfaces.Any(static contract => EfLoggerContracts.Contains(TypeKey(contract))))
                {
                    return type.ToDisplayString();
                }
            }
        }

        return null;
    }

    // The simple names of every type Entity Framework ships that is, or derives from, EventData: the base itself, the
    // context event data and every other event data. A value of such a type is created by `new` on a spelled type name,
    // so a tree that never spells one of these names, and never derives from one, cannot make an event-data value of its
    // own.
    private static readonly Lazy<IReadOnlySet<string>> EfEventDataTypeNames = new(static () =>
    {
        HashSet<string> names = new(StringComparer.Ordinal);

        foreach (global::System.Reflection.Assembly assembly in new[]
        {
            typeof(Microsoft.EntityFrameworkCore.DbContext).Assembly,
            typeof(Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions).Assembly,
            typeof(Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions).Assembly,
        }.Distinct())
        {
            foreach (Type type in assembly.GetTypes())
            {
                if (typeof(Microsoft.EntityFrameworkCore.Diagnostics.EventData).IsAssignableFrom(type) && !type.IsInterface)
                {
                    int arity = type.Name.IndexOf('`', StringComparison.Ordinal);

                    _ = names.Add(arity < 0 ? type.Name : type.Name[..arity]);
                }
            }
        }

        return names;
    });

    internal static IReadOnlyCollection<string> EfEventDataNames => EfEventDataTypeNames.Value;

    // True when any analysed source spells an EF event-data type name anywhere but as the declared type of a parameter (an
    // override's signature names the type to receive a value, which creates none). A property of the analysed
    // compilations alone, computed once per discovery (see InventoryFacts). A name that resolves to something else (a
    // method or member that happens to share the spelling) is not a spelling of the type.
    internal static bool FindSpelledEfEventData(IReadOnlyList<CSharpCompilation> compilations) =>
        FindSpelledEfEventData(compilations, static (compilation, tree) => compilation.GetSemanticModel(tree));

    internal static bool FindSpelledEfEventData(IReadOnlyList<CSharpCompilation> compilations, Func<CSharpCompilation, SyntaxTree, SemanticModel> modelOf)
    {
        IReadOnlySet<string> names = EfEventDataTypeNames.Value;

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

                foreach (SyntaxToken token in tree.GetRoot().DescendantTokens())
                {
                    if (!token.IsKind(SyntaxKind.IdentifierToken)
                        || !names.Contains(token.ValueText)
                        || token.Parent is not SimpleNameSyntax name
                        || IsParameterTypeName(name))
                    {
                        continue;
                    }

                    model ??= modelOf(compilation, tree);

                    SymbolInfo info = model.GetSymbolInfo(name);

                    bool resolvesToOtherThanAType = info.Symbol is { } and not INamedTypeSymbol
                        || info.Symbol is null && info.CandidateSymbols.Length != 0 && info.CandidateSymbols.All(static candidate => candidate is not INamedTypeSymbol);

                    if (!resolvesToOtherThanAType)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    // The name is the whole declared type of a parameter, or the right-hand part of a qualified one.
    private static bool IsParameterTypeName(SimpleNameSyntax name)
    {
        SyntaxNode current = name;

        while (current.Parent is QualifiedNameSyntax or AliasQualifiedNameSyntax or NullableTypeSyntax)
        {
            current = current.Parent;
        }

        return current.Parent is ParameterSyntax parameter && ReferenceEquals(parameter.Type, current);
    }

    private sealed partial class ProducerGraph
    {
        // What the EF operation a reviewed database site names raises. Only operations that reach the database or hand
        // EF a connection, command or transaction raise anything.
        private static EfInterception InterceptionOf(IMethodSymbol definition)
        {
            string type = TypeKey(definition.ContainingType);

            string name = definition.Name;

            if (!AssemblyIdentityMatches(definition.ContainingAssembly.Identity, typeof(Microsoft.EntityFrameworkCore.DbContext).Assembly.GetName())
                && !AssemblyIdentityMatches(definition.ContainingAssembly.Identity, typeof(Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions).Assembly.GetName()))
            {
                return EfInterception.None;
            }

            return type switch
            {
                "Microsoft.EntityFrameworkCore.DbContext" when name.StartsWith("SaveChanges", StringComparison.Ordinal) =>
                    EfInterception.Connection | EfInterception.ConnectionCreation | EfInterception.Command | EfInterception.Transaction | EfInterception.SaveChanges,
                "Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade" when name.StartsWith("BeginTransaction", StringComparison.Ordinal) =>
                    EfInterception.Connection | EfInterception.ConnectionCreation | EfInterception.Transaction,
                "Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions" when name.StartsWith("OpenConnection", StringComparison.Ordinal)
                    || name is "CloseConnection" or "CloseConnectionAsync" =>
                    EfInterception.Connection | EfInterception.ConnectionCreation,
                "Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction" when name is "Commit" or "CommitAsync" or "Rollback" or "RollbackAsync" =>
                    EfInterception.Transaction,
                _ => EfInterception.None,
            };
        }

        /// <summary>
        /// The operations that start Entity Framework tracking an instance can raise the identity-resolution interceptor of the
        /// context, whose triggers this analysis does not enter. While the analysed source authors one, none of them is a
        /// reviewed member, so a use is refused instead of being taken for effect-free.
        /// </summary>
        private bool TrackingBlockedByIdentityResolution(ISymbol member)
        {
            if (inventoryFacts.AuthoredIdentityResolutionInterceptor is null)
            {
                return false;
            }

            ISymbol definition = member is IMethodSymbol method ? method.ReducedFrom ?? method : member;

            return TypeKey(definition.ContainingType) switch
            {
                "Microsoft.EntityFrameworkCore.DbSet`1" => DbSetNeutralMembers.Contains(definition.Name),
                "Microsoft.EntityFrameworkCore.ChangeTracking.ChangeTracker" => definition.Name is "Entries" or "DetectChanges" or "TrackGraph",
                "Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry" or "Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry`1" => definition.Name == "State",
                _ => false,
            };
        }

        private sealed record InterceptorSet(bool Closed, IReadOnlyList<(INamedTypeSymbol Type, Compilation Compilation)> Types);

        /// <summary>
        /// The interceptors EF can hand an event to for one context: for a container-built instance, the registrations
        /// and, unless the provider-complete guard returns first, what its OnConfiguring adds; for an instance the
        /// analysis allocated, the options chain it was built from and, unless the guard returns first, what its
        /// OnConfiguring adds. Any other instance has an interceptor set no one can name.
        /// </summary>
        private InterceptorSet InterceptorSetFor(AuthoredMember member, ContextUse use, ExpressionSyntax? receiver, INamedTypeSymbol type)
        {
            Compilation compilation = member.Model.Compilation;

            List<(INamedTypeSymbol Type, Compilation Compilation)> types = [];

            bool closed = true;

            bool configured = true;

            if (!ContextOptionsForwarded(type))
            {
                return new(false, []);
            }

            switch (use.Origin)
            {
                case ContextOrigin.Container:
                    RegistrationFactsResult registrations = RegistrationFacts(type, compilation);

                    closed = registrations.InterceptorsClosed;

                    types.AddRange(registrations.Interceptors);

                    break;

                case ContextOrigin.Universe:
                    ContextUniverseResult universe = ContextUniverse(type, compilation);

                    closed = universe.InterceptorsClosed;

                    configured = universe.Configured;

                    types.AddRange(universe.Interceptors);

                    break;

                case ContextOrigin.Created when receiver is not null:
                    (bool understood, bool chainConfigured, List<(INamedTypeSymbol Type, Compilation Compilation)> chain) = CreatedContextInterceptors(member, receiver, type);

                    closed = understood;

                    configured = chainConfigured;

                    types.AddRange(chain);

                    break;

                default:
                    return new(false, []);
            }

            // OnConfiguring runs after the options are built. A guard that returns for a configured instance keeps it out;
            // anything else adds whatever it hands the builder.
            if (AuthoredOnConfiguring(type, compilation) is { } configuring
                && !(configured && HasClosedDbContextOnConfiguring(type)))
            {
                BuilderFacts facts = new();

                if (!BuilderScopeCannotChangeProvider(configuring.Syntax, configuring.Model, [], facts))
                {
                    closed = false;
                }

                closed &= facts.InterceptorsClosed;

                types.AddRange(facts.Interceptors);
            }

            return new(closed, types);
        }

        private (bool Understood, bool Configured, List<(INamedTypeSymbol Type, Compilation Compilation)> Interceptors) CreatedContextInterceptors(
            AuthoredMember member,
            ExpressionSyntax receiver,
            INamedTypeSymbol type)
        {
            List<(INamedTypeSymbol Type, Compilation Compilation)> interceptors = [];

            CleanupValueFlow flow = CleanupValueFlowOf(member, receiver, new CleanupProvenanceContext());

            if (!flow.Complete || flow.Values.Count == 0)
            {
                return (false, false, interceptors);
            }

            bool allConfigured = true;

            foreach (BoundValueSource source in flow.Values)
            {
                if (StripTransparentExpression(source.Expression) is not BaseObjectCreationExpressionSyntax creation
                    || AnalyzeContextCreation(source.Caller, creation, type, member.Model.Compilation) is not { Understood: true } created
                    || !created.Builder.InterceptorsClosed)
                {
                    return (false, false, interceptors);
                }

                allConfigured &= created.Configured;

                interceptors.AddRange(created.Builder.Interceptors);
            }

            return (true, allConfigured, interceptors);
        }

        // ---- The context an EF operation runs on.

        /// <summary>
        /// The context expressions an EF operation runs on: the receiver of a context member, the context a
        /// <c>Database</c> facade was read from, or the context that began a transaction. Null when any of them cannot
        /// be followed.
        /// </summary>
        private List<(AuthoredMember Caller, ExpressionSyntax Context)>? EfOperationContexts(AuthoredMember member, InvocationExpressionSyntax call, IMethodSymbol definition)
        {
            if (member.Model.GetOperation(call) is not IInvocationOperation operation
                || ExactInvocationReceiverOrFirstArgument(operation) is not { } receiver)
            {
                return null;
            }

            string type = TypeKey(definition.ContainingType);

            List<(AuthoredMember Caller, ExpressionSyntax Context)> contexts = [];

            return type switch
            {
                "Microsoft.EntityFrameworkCore.DbContext" => [(member, receiver)],
                "Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade" or "Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions" =>
                    FacadeContexts(member, receiver, contexts, 0) ? contexts : null,
                "Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction" =>
                    TransactionContexts(member, receiver, contexts, 0) ? contexts : null,
                _ => null,
            };
        }

        private bool FacadeContexts(AuthoredMember member, ExpressionSyntax facade, List<(AuthoredMember Caller, ExpressionSyntax Context)> contexts, int depth)
        {
            facade = StripTransparentExpression(facade);

            if (facade is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Database" } access
                && member.Model.GetSymbolInfo(access).Symbol is IPropertySymbol { Name: "Database" } property
                && TypeKey(property.ContainingType) == "Microsoft.EntityFrameworkCore.DbContext")
            {
                contexts.Add((member, access.Expression));

                return true;
            }

            if (facade is IdentifierNameSyntax { Identifier.ValueText: "Database" }
                && member.Model.GetSymbolInfo(facade).Symbol is IPropertySymbol { Name: "Database" } implicitProperty
                && TypeKey(implicitProperty.ContainingType) == "Microsoft.EntityFrameworkCore.DbContext"
                && member.Symbol.ContainingType is { } owner
                && IsExactEfDbContext(owner))
            {
                // The facade of the instance behind an implied `this`.
                contexts.Add((member, SyntaxFactory.ThisExpression()));

                return true;
            }

            if (depth >= 3 || facade is not (IdentifierNameSyntax or MemberAccessExpressionSyntax))
            {
                return false;
            }

            return FollowedValues(member, facade) is { } values
                && values.All(value => !ReferenceEquals(value.Expression, facade) && FacadeContexts(value.Caller, value.Expression, contexts, depth + 1));
        }

        private bool TransactionContexts(AuthoredMember member, ExpressionSyntax transaction, List<(AuthoredMember Caller, ExpressionSyntax Context)> contexts, int depth)
        {
            transaction = StripTransparentExpression(transaction);

            ExpressionSyntax inner = transaction;

            if (inner is AwaitExpressionSyntax awaited)
            {
                inner = StripTransparentExpression(awaited.Expression);

                if (inner is InvocationExpressionSyntax configured
                    && member.Model.GetSymbolInfo(configured).Symbol is IMethodSymbol configure
                    && IsExactFrameworkConfigureAwait(configure)
                    && configured.Expression is MemberAccessExpressionSyntax { Expression: { } unconfigured })
                {
                    inner = StripTransparentExpression(unconfigured);
                }
            }

            if (inner is InvocationExpressionSyntax begin
                && member.Model.GetSymbolInfo(begin).Symbol is IMethodSymbol { Name: "BeginTransaction" or "BeginTransactionAsync" } beginMethod
                && TypeKey(beginMethod.ContainingType) == "Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade"
                && member.Model.GetOperation(begin) is IInvocationOperation beginOperation
                && ExactInvocationReceiverOrFirstArgument(beginOperation) is { } facade)
            {
                return FacadeContexts(member, facade, contexts, depth + 1);
            }

            if (depth >= 3 || transaction is not (IdentifierNameSyntax or MemberAccessExpressionSyntax))
            {
                return false;
            }

            return FollowedValues(member, transaction) is { } values
                && values.All(value => !ReferenceEquals(value.Expression, transaction) && TransactionContexts(value.Caller, value.Expression, contexts, depth + 1));
        }

        /// <summary>
        /// Every value an identifier or member read can hold, or null when they cannot all be named. A local holds exactly
        /// its initializer and the assignments to it within the member that declares it; anything else (a parameter, field
        /// or property) is followed through the values its callers and writers supply.
        /// </summary>
        private List<(AuthoredMember Caller, ExpressionSyntax Expression)>? FollowedValues(AuthoredMember member, ExpressionSyntax expression)
        {
            if (member.Model.GetSymbolInfo(expression).Symbol is ILocalSymbol local)
            {
                List<ExpressionSyntax> held = LocalQueryValues(member, local);

                return held.Count == 0 ? null : [.. held.Select(value => (member, value))];
            }

            CleanupValueFlow flow = CleanupValueFlowOf(member, expression, new CleanupProvenanceContext());

            return flow.Complete && flow.Values.Count != 0 ? [.. flow.Values.Select(static value => (value.Caller, value.Expression))] : null;
        }

        private static bool IsExactServiceScopeType(ITypeSymbol? type) =>
            type is INamedTypeSymbol named
            && (IsExactFrameworkType(named, "Microsoft.Extensions.DependencyInjection.IServiceScope", typeof(Microsoft.Extensions.DependencyInjection.IServiceScope).Assembly.GetName())
                || IsExactFrameworkType(named, "Microsoft.Extensions.DependencyInjection.AsyncServiceScope", typeof(Microsoft.Extensions.DependencyInjection.AsyncServiceScope).Assembly.GetName()));

        /// <summary>
        /// Disposing a context closes the connection if it is still open and disposes it, which raises the closing and
        /// disposing callbacks of the connection contract and the StateChange handlers of the connection. They run where the
        /// disposal is, under the frontiers live there, which can be after the work that used the context has ended. The
        /// disposal is located three ways: an explicit call or a using over a value that is a context; a using, or an explicit
        /// disposal, of a container scope, which disposes the contexts the container built (every registered authored
        /// context is a candidate, because the scope's services are not followed); and nowhere else, so the disposal of a
        /// context by the container at the end of the host's own life is outside the traversal of any hosted root.
        /// </summary>
        private TraversalEvidence TraverseDisposalCallbacks(
            AuthoredMember member,
            ExpressionSyntax resource,
            ITypeSymbol? resourceType,
            bool asynchronous,
            SyntaxNode node,
            string rootType,
            string operationId,
            HashSet<TraversalStateIdentity> active,
            bool lifecycle,
            string? workAdmitted,
            string? effectGroup,
            string? recoveryEffect)
        {
            if (resourceType is null)
            {
                return TraversalEvidence.NoEvidence;
            }

            string disposal = asynchronous ? "DisposeAsync" : "Dispose";

            if (IsExactServiceScopeType(resourceType))
            {
                ContextUse[] built =
                [
                    .. AuthoredContextTypes()
                        .Where(context => RegistrationFacts(context, member.Model.Compilation).Count != 0)
                        .Select(static context => new ContextUse([context], ContextOrigin.Container)),
                ];

                // A scope with no registered context to dispose raises no EF event.
                if (built.Length == 0)
                {
                    return TraversalEvidence.NoEvidence;
                }

                return TraverseInterceptorsForOperation(
                    member,
                    node,
                    null,
                    EfInterception.ConnectionDisposal,
                    "Microsoft.Extensions.DependencyInjection." + (resourceType.TypeKind == TypeKind.Interface ? "IServiceScope" : "AsyncServiceScope") + "." + disposal,
                    rootType,
                    operationId,
                    active,
                    lifecycle,
                    workAdmitted,
                    effectGroup,
                    recoveryEffect,
                    asynchronous,
                    built);
            }

            if (!IsContextResource(member, resource, resourceType))
            {
                return TraversalEvidence.NoEvidence;
            }

            return TraverseInterceptorsForOperation(
                member,
                node,
                [(member, resource)],
                EfInterception.ConnectionDisposal,
                "Microsoft.EntityFrameworkCore.DbContext." + disposal,
                rootType,
                operationId,
                active,
                lifecycle,
                workAdmitted,
                effectGroup,
                recoveryEffect,
                asynchronous);
        }

        // A value disposed is a context when its static type is one, or when it is held as a disposable interface (or object)
        // and every source it can have is an allocation or a closed value of a context type.
        private bool IsContextResource(AuthoredMember member, ExpressionSyntax resource, ITypeSymbol resourceType)
        {
            if (ContextBound(resourceType) is { TypeKind: TypeKind.Class } bound
                && (IsExactEfDbContext(bound) || TypeKey(bound) == "Microsoft.EntityFrameworkCore.DbContext" && IsEfAssembly(bound.ContainingAssembly)))
            {
                return true;
            }

            if (TypeKey(resourceType) is not ("System.IDisposable" or "System.IAsyncDisposable" or "System.Object"))
            {
                return false;
            }

            CleanupProvenance provenance = CleanupProvenanceOf(member, resource, new CleanupProvenanceContext());

            return provenance.Complete
                && provenance.Candidates.Count != 0
                && provenance.Candidates.All(static candidate => candidate.Type is INamedTypeSymbol { TypeKind: TypeKind.Class } named && IsExactEfDbContext(named));
        }

        /// <summary>
        /// The instance behind <c>this</c> inside a member of a context is the containing context or, when that is not
        /// sealed, any authored context derived from it, so the set is every one of those and no other context of the program.
        /// </summary>
        private ContextUse ThisContextUse(AuthoredMember caller)
        {
            INamedTypeSymbol containing = caller.Symbol.ContainingType;

            INamedTypeSymbol[] types = [.. AuthoredContextTypes().Where(context => containing.IsSealed
                ? SymbolEqualityComparer.Default.Equals(context, containing)
                : ContextDerivesFrom(context, containing))];

            return new(types.Length != 0 ? types : [containing], ContextOrigin.Universe);
        }

        // ---- The authored callbacks.

        private static bool IsConnectionDisposalSlot(string name) =>
            name.StartsWith("ConnectionDispos", StringComparison.Ordinal);

        private static bool IsConnectionClosingSlot(string name) =>
            name.StartsWith("ConnectionClos", StringComparison.Ordinal);

        // The slots of the contracts the interceptor implements that the kinds raise. A synchronous operation raises the
        // synchronous slots and an asynchronous one the asynchronous slots, when the caller says which it is.
        private static IMethodSymbol[] InterceptionSlots(INamedTypeSymbol interceptor, EfInterception kinds, bool? asynchronous = null)
        {
            List<IMethodSymbol> slots = [];

            foreach (INamedTypeSymbol contract in interceptor.AllInterfaces)
            {
                string key = TypeKey(contract);

                foreach ((EfInterception kind, string[] contracts) in EfInterceptionContracts)
                {
                    if ((kinds & kind) == 0 || !contracts.Contains(key, StringComparer.Ordinal))
                    {
                        continue;
                    }

                    foreach (IMethodSymbol slot in contract.GetMembers().OfType<IMethodSymbol>().Where(static slot => slot.MethodKind == MethodKind.Ordinary))
                    {
                        bool creation = slot.Name.StartsWith("ConnectionCreat", StringComparison.Ordinal);

                        // The creation events alone are raised when the connection object is merely created.
                        if (kind == EfInterception.ConnectionCreation && !creation)
                        {
                            continue;
                        }

                        // An operation raises the rest of the connection contract but never the disposal of the connection:
                        // that is raised by disposing the context, which also closes a connection still open.
                        if (kind == EfInterception.Connection && (creation || IsConnectionDisposalSlot(slot.Name)))
                        {
                            continue;
                        }

                        if (kind == EfInterception.ConnectionDisposal && !(IsConnectionDisposalSlot(slot.Name) || IsConnectionClosingSlot(slot.Name)))
                        {
                            continue;
                        }

                        if (asynchronous is { } wanted && slot.Name.EndsWith("Async", StringComparison.Ordinal) != wanted)
                        {
                            continue;
                        }

                        slots.Add(slot);
                    }
                }
            }

            return [.. slots.Distinct(SymbolEqualityComparer.Default).OfType<IMethodSymbol>()];
        }

        // The EF interceptor contracts an interceptor implements that the analysis does not enter. An interceptor of such a
        // contract is raised by triggers nobody enumerates here, so its callbacks are unknown.
        private static List<string> UnenteredInterceptorContracts(INamedTypeSymbol interceptor)
        {
            List<string> contracts = [];

            foreach (INamedTypeSymbol contract in interceptor.AllInterfaces)
            {
                if (!contract.AllInterfaces.Any(static inherited => TypeKey(inherited) == EfInterceptorMarker))
                {
                    continue;
                }

                string key = TypeKey(contract);

                bool exact = !IsAuthoredSymbol(contract) && IsEfAssembly(contract.ContainingAssembly);

                if (!exact || EfInterceptorContractVerdict(key) is not ("Entered" or "Marker"))
                {
                    contracts.Add(exact ? key : contract.ToDisplayString());
                }
            }

            return contracts;
        }

        // The authored body that runs for an interface slot: the implementation itself when it is declared in source, or
        // the most-derived authored override of the framework virtual the interface maps to.
        private AuthoredMember? AuthoredInterceptorCallback(INamedTypeSymbol interceptor, IMethodSymbol slot, Compilation compilation)
        {
            if (interceptor.FindImplementationForInterfaceMember(slot) is not IMethodSymbol implementation)
            {
                return null;
            }

            if (IsAuthoredSymbol(implementation))
            {
                return Resolve(implementation, compilation);
            }

            for (INamedTypeSymbol? current = interceptor;
                current is not null && current.SpecialType != SpecialType.System_Object;
                current = current.BaseType)
            {
                foreach (IMethodSymbol candidate in current.GetMembers(implementation.Name).OfType<IMethodSymbol>())
                {
                    if (!candidate.IsOverride || !IsAuthoredSymbol(candidate))
                    {
                        continue;
                    }

                    for (IMethodSymbol? overridden = candidate.OverriddenMethod; overridden is not null; overridden = overridden.OverriddenMethod)
                    {
                        if (SymbolEqualityComparer.Default.Equals(overridden.OriginalDefinition, implementation.OriginalDefinition))
                        {
                            return Resolve(candidate, compilation);
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Called after a database site is added for an EF operation named by its own identity (SaveChanges, BeginTransaction,
        /// OpenConnection, Commit, ...): the callbacks of the interceptors its context raises.
        /// </summary>
        private TraversalEvidence TraverseEfOperationCallbacks(
            AuthoredMember member,
            InvocationExpressionSyntax call,
            IMethodSymbol definition,
            string callee,
            string rootType,
            string operationId,
            HashSet<TraversalStateIdentity> active,
            bool lifecycle,
            string? workAdmitted,
            string? effectGroup,
            string? recoveryEffect)
        {
            EfInterception kinds = InterceptionOf(definition);

            if (kinds == EfInterception.None)
            {
                return TraversalEvidence.NoEvidence;
            }

            return TraverseInterceptorsForOperation(
                member,
                call,
                EfOperationContexts(member, call, definition),
                kinds,
                callee,
                rootType,
                operationId,
                active,
                lifecycle,
                workAdmitted,
                effectGroup,
                recoveryEffect);
        }

        /// <summary>
        /// Every authored callback of the interceptors EF can hand an operation's events to, entered under the frontiers live
        /// at the operation, and the refusal of whatever cannot be closed: a context nobody can follow, an interceptor set no
        /// registration closes, an authored logger EF's events could reach.
        /// </summary>
        private TraversalEvidence TraverseInterceptorsForOperation(
            AuthoredMember member,
            SyntaxNode node,
            List<(AuthoredMember Caller, ExpressionSyntax Context)>? contexts,
            EfInterception kinds,
            string callee,
            string rootType,
            string operationId,
            HashSet<TraversalStateIdentity> active,
            bool lifecycle,
            string? workAdmitted,
            string? effectGroup,
            string? recoveryEffect,
            bool? asynchronous = null,
            IReadOnlyList<ContextUse>? explicitUses = null)
        {
            TraversalEvidence evidence = TraversalEvidence.NoEvidence;

            if (inventoryFacts.AuthoredLoggerType is { } logger)
            {
                RefuseTraversalCompletion();

                diagnostics.Add(new(
                    "HOSTED_SITE_UNCLASSIFIED",
                    Location(node),
                    callee + "; EF reports this operation to the application's loggers, and the authored logger " + logger + " would receive its events."));
            }

            // The contexts the operation runs on: the ones the caller already classified, or the ones a context expression names.
            List<(AuthoredMember Caller, ContextUse Use, ExpressionSyntax? Receiver)> resolved = [];

            if (explicitUses is not null)
            {
                resolved.AddRange(explicitUses.Select(use => (member, use, (ExpressionSyntax?)null)));
            }
            else if (contexts is null || contexts.Count == 0)
            {
                RefuseTraversalCompletion();

                diagnostics.Add(new(
                    "HOSTED_SITE_UNCLASSIFIED",
                    Location(node),
                    callee + "; the context this operation runs on cannot be followed, so the interceptors EF would raise are unknown."));

                return evidence;
            }
            else
            {
                foreach ((AuthoredMember caller, ExpressionSyntax context) in contexts)
                {
                    resolved.Add(context is ThisExpressionSyntax
                        ? (caller, ThisContextUse(caller), null)
                        : (caller, ClassifyContextUse(caller, context), context));
                }
            }

            HashSet<GraphMemberIdentity> entered = [];

            foreach ((AuthoredMember caller, ContextUse use, ExpressionSyntax? receiver) in resolved)
            {
                if (use.Types.Count == 0)
                {
                    // No authored context can hold the instance: it has no authored interceptor to raise.
                    continue;
                }

                foreach (INamedTypeSymbol type in use.Types)
                {
                    InterceptorSet set = InterceptorSetFor(caller, use, receiver, type);

                    if (!set.Closed)
                    {
                        RefuseTraversalCompletion();

                        diagnostics.Add(new(
                            "HOSTED_SITE_UNCLASSIFIED",
                            Location(node),
                            callee + "; the interceptors registered on " + type.ToDisplayString() + " are not a closed set, so the callbacks EF would raise are unknown."));

                        continue;
                    }

                    foreach ((INamedTypeSymbol interceptor, Compilation interceptorCompilation) in set.Types.Distinct())
                    {
                        foreach (string unentered in UnenteredInterceptorContracts(interceptor))
                        {
                            RefuseTraversalCompletion();

                            diagnostics.Add(new(
                                "HOSTED_SITE_UNCLASSIFIED",
                                Location(node),
                                callee + "; the interceptor " + interceptor.ToDisplayString() + " implements " + unentered + ", an EF interceptor contract this analysis does not enter, so the callbacks EF would raise are unknown."));
                        }

                        foreach (IMethodSymbol slot in InterceptionSlots(interceptor, kinds, asynchronous))
                        {
                            if (AuthoredInterceptorCallback(interceptor, slot, interceptorCompilation) is not { } callback
                                || !entered.Add(MemberIdentity(callback)))
                            {
                                continue;
                            }

                            evidence = MergeEvidence(
                                evidence,
                                Traverse(
                                    callback,
                                    rootType,
                                    operationId + "/interceptor@" + Location(node) + "/" + slot.Name,
                                    active,
                                    lifecycle,
                                    workAdmitted,
                                    effectGroup,
                                    completionOwned: true,
                                    recoveryEffect: workAdmitted is not null && effectGroup is not null ? recoveryEffect : null));
                        }
                    }
                }
            }

            // An operation that raises the connection contract opens or closes the connection, which raises its StateChange.
            if ((kinds & (EfInterception.Connection | EfInterception.ConnectionDisposal)) != 0)
            {
                evidence = MergeEvidence(
                    evidence,
                    TraverseConnectionStateHandlers(node, callee, rootType, operationId, active, lifecycle, workAdmitted, effectGroup, recoveryEffect));
            }

            return evidence;
        }

        /// <summary>
        /// A getter of the data EF or the framework hands a callback when it raises an event holds what was stored when the
        /// event was raised. EF declares most of them virtual, so the read is reviewed only while every event-data value is
        /// EF's own: an analysed source that spells an event-data type could hand a callback an override.
        /// </summary>
        private bool IsReviewedFrameworkEventDataRead(IPropertySymbol property)
        {
            if (property.IsStatic || property.Locations.Any(static location => location.IsInSource))
            {
                return false;
            }

            if (IsExactFrameworkType(property.ContainingType, "System.Data.StateChangeEventArgs", typeof(System.Data.StateChangeEventArgs).Assembly.GetName()))
            {
                return property.Name is "CurrentState" or "OriginalState";
            }

            if (!AssemblyIdentityMatches(property.ContainingAssembly.Identity, typeof(Microsoft.EntityFrameworkCore.Diagnostics.EventData).Assembly.GetName())
                && !AssemblyIdentityMatches(property.ContainingAssembly.Identity, typeof(Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData).Assembly.GetName()))
            {
                return false;
            }

            for (INamedTypeSymbol? current = property.ContainingType; current is not null; current = current.BaseType)
            {
                if (TypeKey(current) == "Microsoft.EntityFrameworkCore.Diagnostics.EventData"
                    && !current.Locations.Any(static location => location.IsInSource)
                    && AssemblyIdentityMatches(current.ContainingAssembly.Identity, typeof(Microsoft.EntityFrameworkCore.Diagnostics.EventData).Assembly.GetName()))
                {
                    return !(property.IsVirtual || property.IsOverride || property.IsAbstract) || !inventoryFacts.EfEventDataSpelled;
                }
            }

            return false;
        }

        /// <summary>
        /// The base implementations of EF's interceptor classes do nothing but return the result they were handed. A call
        /// made through <c>base</c> selects that framework body directly and cannot reach an override.
        /// </summary>
        private static bool IsReviewedEfInterceptorBaseCall(IMethodSymbol method, SyntaxNode? node, SemanticModel? model)
        {
            if (node is not InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax } })
            {
                return false;
            }

            IMethodSymbol definition = method.ReducedFrom ?? method;

            return TypeKey(definition.ContainingType) is
                    "Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor"
                    or "Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor"
                    or "Microsoft.EntityFrameworkCore.Diagnostics.DbTransactionInterceptor"
                    or "Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor"
                    or "Microsoft.EntityFrameworkCore.Diagnostics.MaterializationInterceptor"
                    or "Microsoft.EntityFrameworkCore.Diagnostics.IdentityResolutionInterceptor"
                && !definition.Locations.Any(static location => location.IsInSource)
                && AssemblyIdentityMatches(definition.ContainingAssembly.Identity, typeof(Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor).Assembly.GetName());
        }
    }
}
