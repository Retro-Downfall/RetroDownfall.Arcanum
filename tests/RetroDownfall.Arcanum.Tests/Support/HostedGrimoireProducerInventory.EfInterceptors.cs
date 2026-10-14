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

        // The rest of IDbConnectionInterceptor: opening, opened, closing, closed, failed, canceled, disposing, disposed.
        Connection = 2,

        Command = 4,

        Transaction = 8,

        SaveChanges = 16,

        Query = 32,
    }

    private static readonly IReadOnlyDictionary<EfInterception, string[]> EfInterceptionContracts = new Dictionary<EfInterception, string[]>
    {
        [EfInterception.ConnectionCreation] = ["Microsoft.EntityFrameworkCore.Diagnostics.IDbConnectionInterceptor"],
        [EfInterception.Connection] = ["Microsoft.EntityFrameworkCore.Diagnostics.IDbConnectionInterceptor"],
        [EfInterception.Command] = ["Microsoft.EntityFrameworkCore.Diagnostics.IDbCommandInterceptor"],
        [EfInterception.Transaction] = ["Microsoft.EntityFrameworkCore.Diagnostics.IDbTransactionInterceptor"],
        [EfInterception.SaveChanges] = ["Microsoft.EntityFrameworkCore.Diagnostics.ISaveChangesInterceptor"],
        [EfInterception.Query] =
        [
            "Microsoft.EntityFrameworkCore.Diagnostics.IMaterializationInterceptor",
            "Microsoft.EntityFrameworkCore.Diagnostics.IQueryExpressionInterceptor",
            "Microsoft.EntityFrameworkCore.Diagnostics.IIdentityResolutionInterceptor",
        ],
    };

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

    // The simple names of every type Entity Framework ships that derives from DbContextEventData. A value of such a type
    // is created by `new` on a spelled type name, so a tree that never spells one of these names, and never derives from
    // one, cannot make an event-data value of its own.
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
                if (typeof(Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData).IsAssignableFrom(type))
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

        // ---- The authored callbacks.

        private static IMethodSymbol[] InterceptionSlots(INamedTypeSymbol interceptor, EfInterception kinds)
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
                        // The creation events alone are raised when the connection object is merely created.
                        if (kind == EfInterception.ConnectionCreation && !slot.Name.StartsWith("ConnectionCreat", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (kind == EfInterception.Connection && slot.Name.StartsWith("ConnectionCreat", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        slots.Add(slot);
                    }
                }
            }

            return [.. slots.Distinct(SymbolEqualityComparer.Default).OfType<IMethodSymbol>()];
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
            string? recoveryEffect)
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

            if (contexts is null || contexts.Count == 0)
            {
                RefuseTraversalCompletion();

                diagnostics.Add(new(
                    "HOSTED_SITE_UNCLASSIFIED",
                    Location(node),
                    callee + "; the context this operation runs on cannot be followed, so the interceptors EF would raise are unknown."));

                return evidence;
            }

            HashSet<GraphMemberIdentity> entered = [];

            foreach ((AuthoredMember caller, ExpressionSyntax context) in contexts)
            {
                ContextUse use = context is ThisExpressionSyntax
                    ? new([caller.Symbol.ContainingType], ContextOrigin.Universe)
                    : ClassifyContextUse(caller, context);

                if (use.Types.Count == 0)
                {
                    // No authored context can hold the instance: it has no authored interceptor to raise.
                    continue;
                }

                foreach (INamedTypeSymbol type in use.Types)
                {
                    InterceptorSet set = InterceptorSetFor(caller, use, context is ThisExpressionSyntax ? null : context, type);

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
                        foreach (IMethodSymbol slot in InterceptionSlots(interceptor, kinds))
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
            if ((kinds & EfInterception.Connection) != 0)
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
