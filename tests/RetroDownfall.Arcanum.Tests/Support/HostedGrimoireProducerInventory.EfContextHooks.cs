using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

// Authored DbContext code that Entity Framework runs on its own schedule. OnConfiguring and the model hooks
// (OnModelCreating, ConfigureConventions) run at the first use of a context's services; the constructor and the field
// and property initializers run when the context is activated. None of them is called by the code that triggers it, so
// the traversal enters them at the use, with no work, effect or recovery admission inherited: the use that really
// triggers them can be a later consumer of a value read there, after the admission current at the read has ended.
internal static partial class HostedGrimoireProducerInventory
{
    /// <summary>What the registrations of one context supply to its options, as the inventory reads them.</summary>
    internal sealed record EfRegistrationProbe(bool ModelSupplied, bool InterceptorsClosed, IReadOnlyList<string> Interceptors);

    // Reads the registrations of a context declared in an analysed compilation, exactly as the traversal does, so a test
    // can pin what the shipped registrations supply (the compiled model, the interceptor set).
    internal static EfRegistrationProbe ProbeEfRegistration(IReadOnlyList<CSharpCompilation> compilations, string contextMetadataName)
    {
        ProducerGraph graph = new(compilations, DefaultEvaluationEnvironmentMaximumMembers, DefaultEvaluationEnvironmentMaximumDepth, DefaultTraversalMaximumDepth, DefaultMaximumAnalyzedStatesPerRoot, false);

        foreach (CSharpCompilation compilation in compilations)
        {
            if (compilation.GetTypeByMetadataName(contextMetadataName) is { } context && context.Locations.Any(static location => location.IsInSource))
            {
                ProducerGraph.RegistrationFactsResult facts = graph.RegistrationFacts(context, compilation);

                return new(facts.ModelSupplied, facts.InterceptorsClosed, [.. facts.Interceptors.Select(static interceptor => interceptor.Type.ToDisplayString()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
            }
        }

        throw new InvalidOperationException("No analysed compilation declares " + contextMetadataName + ".");
    }

    private sealed partial class ProducerGraph
    {
        /// <summary>
        /// One authored member EF runs because of a context use, and the step it is traversed under; or, with no member, the
        /// refusal of something EF runs at that use that the analysis cannot enter.
        /// </summary>
        private readonly record struct DbContextHook(AuthoredMember? Member, string Step, string? Refusal = null);

        private enum ContextOrigin : byte
        {
            // Built by the container from the registrations alone.
            Container = 1,

            // Allocated in analysed code from options that code supplies.
            Created = 2,

            // Neither could be followed: the static type is all that is known. The instance is one the program can make, so
            // it is judged by everything that can make one (the registrations of its type and every allocation of it in
            // analysed source), see ContextUniverse.
            Universe = 3,
        }

        /// <summary>
        /// The context types a receiver can hold at run time. When the value cannot be followed, every authored
        /// context the static type allows is a candidate: authored code can only be reached through a type declared in
        /// analysed source, so enumerating those types is a closed set. A sealed static type is exactly that type.
        /// </summary>
        private sealed record ContextUse(IReadOnlyList<INamedTypeSymbol> Types, ContextOrigin Origin);

        private INamedTypeSymbol[]? authoredContextTypes;

        private INamedTypeSymbol[] AuthoredContextTypes()
        {
            if (authoredContextTypes is { } cached)
            {
                return cached;
            }

            List<INamedTypeSymbol> types = [];

            HashSet<string> seen = new(StringComparer.Ordinal);

            foreach (CSharpCompilation compilation in compilationIdentities.Keys.OfType<CSharpCompilation>())
            {
                foreach (INamedTypeSymbol type in SourceTypes(compilation.Assembly.GlobalNamespace))
                {
                    if (type.TypeKind == TypeKind.Class
                        && !type.IsAbstract
                        && IsAuthoredSymbol(type)
                        && IsExactEfDbContext(type)
                        && seen.Add(compilation.Assembly.Identity.Name + "|" + type.ToDisplayString()))
                    {
                        types.Add(type);
                    }
                }
            }

            authoredContextTypes = [.. types];

            return authoredContextTypes;
        }

        private static bool ContextDerivesFrom(INamedTypeSymbol context, INamedTypeSymbol bound)
        {
            if (bound.TypeKind == TypeKind.Interface)
            {
                return context.AllInterfaces.Any(contract => TypeKey(contract) == TypeKey(bound));
            }

            for (INamedTypeSymbol? current = context; current is not null; current = current.BaseType)
            {
                if (TypeKey(current) == TypeKey(bound)
                    && current.ContainingAssembly?.Identity.Name == bound.ContainingAssembly?.Identity.Name)
                {
                    return true;
                }
            }

            return false;
        }

        // The class a static type bounds a value by: the type itself, a type parameter's class constraint, or nothing.
        private static INamedTypeSymbol? ContextBound(ITypeSymbol? type) => type switch
        {
            INamedTypeSymbol named => named,
            ITypeParameterSymbol parameter => parameter.ConstraintTypes.OfType<INamedTypeSymbol>()
                .FirstOrDefault(static constraint => constraint.TypeKind == TypeKind.Class),
            _ => null,
        };

        private ContextUse ClassifyContextUse(AuthoredMember member, ExpressionSyntax receiver)
        {
            receiver = StripTransparentExpression(receiver);

            CleanupProvenance provenance = CleanupProvenanceOf(member, receiver, new CleanupProvenanceContext());

            if (provenance.Complete && provenance.Candidates.Count != 0)
            {
                INamedTypeSymbol[] exact = [.. provenance.Candidates
                    .Select(static candidate => candidate.Type)
                    .OfType<INamedTypeSymbol>()
                    .Where(static type => type.TypeKind == TypeKind.Class && IsExactEfDbContext(type))];

                if (exact.Length == provenance.Candidates.Count)
                {
                    // Only a value every source of which is an allocation is a created context; an exact type known from a
                    // sealed declaration or a binding says what the value is, never where it came from.
                    return new(
                        exact,
                        IsContainerResolvedDbContextInstance(member, receiver)
                            ? ContextOrigin.Container
                            : IsAllocatedContext(member, receiver)
                                ? ContextOrigin.Created
                                : ContextOrigin.Universe);
                }
            }

            INamedTypeSymbol? bound = ContextBound(member.Model.GetTypeInfo(receiver).Type);

            if (bound is { TypeKind: TypeKind.Class, IsSealed: true } && IsExactEfDbContext(bound))
            {
                return new([bound], ContextOrigin.Universe);
            }

            // Every authored context the static type allows. A bound that no authored context derives from has no
            // authored code behind it.
            INamedTypeSymbol[] candidates = bound is null
                ? AuthoredContextTypes()
                : [.. AuthoredContextTypes().Where(context => ContextDerivesFrom(context, bound))];

            return new(candidates, ContextOrigin.Universe);
        }

        private bool IsAllocatedContext(AuthoredMember member, ExpressionSyntax receiver)
        {
            CleanupValueFlow flow = CleanupValueFlowOf(member, receiver, new CleanupProvenanceContext());

            return flow.Complete
                && flow.Values.Count != 0
                && flow.Values.All(static source => StripTransparentExpression(source.Expression) is BaseObjectCreationExpressionSyntax);
        }

        private AuthoredMember? AuthoredContextOverride(INamedTypeSymbol type, string name, Compilation compilation)
        {
            for (INamedTypeSymbol? current = type;
                current is not null && TypeKey(current) != "Microsoft.EntityFrameworkCore.DbContext";
                current = current.BaseType)
            {
                foreach (IMethodSymbol method in current.GetMembers(name).OfType<IMethodSymbol>())
                {
                    if (method.IsOverride && Resolve(method, compilation) is { } authored)
                    {
                        return authored;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Every authored hook EF runs at the first use of the context's services, and the activation of the context:
        /// the most-derived OnConfiguring unless the registration made it provider-complete, the model hooks unless a
        /// compiled model is always supplied, and the constructors and initializers unless the creation already
        /// ran them where it is written.
        /// </summary>
        private List<DbContextHook> DbContextHooksFor(AuthoredMember member, ContextUse use, ExpressionSyntax? receiver, bool implicitThis)
        {
            List<DbContextHook> hooks = [];

            Compilation compilation = member.Model.Compilation;

            foreach (INamedTypeSymbol type in use.Types)
            {
                // A guard-closed OnConfiguring returns first for a container-built instance, and for the instance behind
                // an implied `this` it is judged at the call that has a receiver.
                if (!((use.Origin == ContextOrigin.Container || implicitThis) && HasClosedDbContextOnConfiguring(type))
                    && AuthoredOnConfiguring(type, compilation) is { } configuring)
                {
                    hooks.Add(new(configuring, "call"));
                }

                // The instance behind an implied `this` is judged where the member is called on a known receiver, so
                // the model hooks and the activation are not entered a second time at its own body.
                if (implicitThis)
                {
                    continue;
                }

                if (!CompiledModelSupplied(member, receiver, use, type))
                {
                    foreach (string hook in new[] { "OnModelCreating", "ConfigureConventions" })
                    {
                        if (AuthoredContextOverride(type, hook, compilation) is { } model)
                        {
                            hooks.Add(new(model, "model"));
                        }
                    }

                    // EF builds the model at this use, and building it raises the instantiation-binding interceptors of the
                    // context, a contract the analysis does not enter.
                    if (InterceptorSetFor(member, use, receiver, type) is { Closed: true } modelSet
                        && modelSet.Types.Select(static interceptor => interceptor.Type).Distinct(SymbolEqualityComparer.Default).OfType<INamedTypeSymbol>()
                            .FirstOrDefault(static interceptor => interceptor.AllInterfaces.Any(static contract => TypeKey(contract) == "Microsoft.EntityFrameworkCore.Diagnostics.IInstantiationBindingInterceptor")) is { } binder)
                    {
                        hooks.Add(new(null, "model", "the interceptor " + binder.ToDisplayString() + " implements IInstantiationBindingInterceptor, which EF raises while it builds the model of " + type.ToDisplayString() + ", an interceptor contract this analysis does not enter."));
                    }
                }

                if (use.Origin != ContextOrigin.Created)
                {
                    foreach (AuthoredMember activation in ActivationMembers(type, compilation, includeConstructors: true))
                    {
                        hooks.Add(new(activation, "activate"));
                    }
                }
            }

            return [.. hooks.DistinctBy(static hook => hook.Member is { } hooked
                ? ((SyntaxTree?)hooked.Syntax.SyntaxTree, hooked.Syntax.SpanStart, hook.Step, string.Empty)
                : ((SyntaxTree?)null, 0, hook.Step, hook.Refusal ?? string.Empty))];
        }

        /// <summary>
        /// The authored code a context activation runs: the instance constructors with a body, the field and property
        /// initializers of the type and of every authored base, and the arguments of a primary constructor's base call.
        /// An explicit creation already traverses the constructor it names, so it asks for the initializers alone.
        /// </summary>
        private List<AuthoredMember> ActivationMembers(INamedTypeSymbol type, Compilation compilation, bool includeConstructors)
        {
            List<AuthoredMember> members = [];

            for (INamedTypeSymbol? current = type;
                current is not null && TypeKey(current) != "Microsoft.EntityFrameworkCore.DbContext";
                current = current.BaseType)
            {
                if (!IsAuthoredSymbol(current))
                {
                    continue;
                }

                IMethodSymbol? anchor = current.InstanceConstructors.FirstOrDefault(static constructor => !constructor.IsStatic);

                foreach (SyntaxReference reference in current.DeclaringSyntaxReferences)
                {
                    if (reference.GetSyntax() is not TypeDeclarationSyntax declaration
                        || !semanticModels.TryGetValue(declaration.SyntaxTree, out SemanticModel? model)
                        || anchor is null)
                    {
                        continue;
                    }

                    foreach (MemberDeclarationSyntax memberDeclaration in declaration.Members)
                    {
                        switch (memberDeclaration)
                        {
                            case FieldDeclarationSyntax field when !field.Modifiers.Any(SyntaxKind.StaticKeyword) && !field.Modifiers.Any(SyntaxKind.ConstKeyword):
                                foreach (VariableDeclaratorSyntax variable in field.Declaration.Variables)
                                {
                                    if (variable.Initializer?.Value is { } value)
                                    {
                                        members.Add(new(anchor, value, model));
                                    }
                                }

                                break;

                            case PropertyDeclarationSyntax { Initializer.Value: { } initializer } property when !property.Modifiers.Any(SyntaxKind.StaticKeyword):
                                members.Add(new(anchor, initializer, model));

                                break;

                            case ConstructorDeclarationSyntax constructor when includeConstructors
                                && !constructor.Modifiers.Any(SyntaxKind.StaticKeyword)
                                && (constructor.Body is not null || constructor.ExpressionBody is not null)
                                && model.GetDeclaredSymbol(constructor) is { } constructorSymbol
                                && Resolve(constructorSymbol, compilation) is { } authored:
                                members.Add(authored);

                                break;
                        }
                    }

                    // `class C(options) : DbContext(Transform(options))` evaluates the base arguments at construction.
                    if (declaration.BaseList?.Types.OfType<PrimaryConstructorBaseTypeSyntax>().FirstOrDefault() is { ArgumentList.Arguments.Count: > 0 } primaryBase)
                    {
                        members.Add(new(anchor, primaryBase.ArgumentList, model));
                    }
                }
            }

            // Syntax that only names the constructor's own parameters or literals runs nothing: traversing it would add a
            // state per use for no effect.
            return [.. members.Where(activation => !IsInertActivationSyntax(activation.Syntax, activation.Model))];
        }

        private static bool IsInertActivationSyntax(SyntaxNode syntax, SemanticModel model)
        {
            foreach (SyntaxNode node in syntax.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    case ArgumentListSyntax or ArgumentSyntax or LiteralExpressionSyntax or ParenthesizedExpressionSyntax or ThisExpressionSyntax:
                        continue;

                    case IdentifierNameSyntax name when model.GetSymbolInfo(name).Symbol is IParameterSymbol:
                        continue;

                    default:
                        return false;
                }
            }

            return true;
        }

        /// <summary>
        /// The first use of a context's services: the hooks EF runs because of it, entered with no inherited admission.
        /// A use is any non-static member of <c>DbContext</c>, or of an interface the framework context implements
        /// (<c>IDbContextDependencies</c>, <c>IDbSetCache</c>, ...), other than disposal, whether the receiver is spelled
        /// out, reached through <c>?.</c> or implied (<c>this</c> inside a derived context); an authored instance member of
        /// a derived context called on a known receiver; a delegate to such a member; or one of EF's accessor extensions.
        /// </summary>
        private IReadOnlyList<DbContextHook> DbContextFirstUseHooks(AuthoredMember member, SyntaxNode node, ISymbol used)
        {
            ISymbol definition = used is IMethodSymbol method
                ? method.ReducedFrom ?? method
                : used;

            string owner = TypeKey(definition.ContainingType);

            bool contextMember = (owner == "Microsoft.EntityFrameworkCore.DbContext"
                    || definition.ContainingType is { TypeKind: TypeKind.Interface }
                        && DbContextInterfaceKeys.Contains(owner))
                && !definition.IsStatic
                && definition.Name is not ("Dispose" or "DisposeAsync");

            bool derivedMember = !definition.IsStatic
                && definition.ContainingType is { } derivedOwner
                && IsExactEfDbContext(derivedOwner)
                && definition.DeclaringSyntaxReferences.Length != 0;

            bool accessor = owner == "Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions"
                || owner == "Microsoft.EntityFrameworkCore.Infrastructure.IInfrastructure`1"
                    && definition.Name == "Instance";

            if (!contextMember && !derivedMember && !accessor)
            {
                return [];
            }

            ExpressionSyntax? receiver;

            bool implicitThis;

            if (accessor && member.Model.GetOperation(node) is IInvocationOperation accessorCall)
            {
                receiver = ExactInvocationReceiverOrFirstArgument(accessorCall);

                implicitThis = false;
            }
            else
            {
                (receiver, implicitThis) = DbContextUseReceiver(node);
            }

            if (receiver is BaseExpressionSyntax)
            {
                receiver = null;

                implicitThis = true;
            }

            if (receiver is not null && IsEventRaisedContext(member, receiver))
            {
                return [];
            }

            if (implicitThis)
            {
                // A member the derived context declares is judged where it is called on a known receiver; the implied
                // instance of its own body is not known here. A guard-closed fallback only runs for an unconfigured
                // instance and is judged at the call that has a receiver too.
                if (derivedMember
                    || member.Symbol.IsStatic
                    || !IsExactEfDbContext(member.Symbol.ContainingType))
                {
                    return [];
                }

                return DbContextHooksFor(member, new([member.Symbol.ContainingType], ContextOrigin.Universe), null, implicitThis: true);
            }

            return receiver is null
                ? []
                : DbContextHooksFor(member, ClassifyContextUse(member, receiver), receiver, implicitThis: false);
        }

        /// <summary>
        /// The <c>Context</c> of an EF event is the context Entity Framework was working for when it raised the event, so
        /// its first use, and the hooks that use runs, were judged where that work was done. This holds only while every
        /// event-data value is EF's own: an analysed source that spells an event-data type could create one over a
        /// context nothing has used, so then the read is a use like any other.
        /// </summary>
        private bool IsEventRaisedContext(AuthoredMember member, ExpressionSyntax receiver) =>
            StripTransparentExpression(receiver) is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Context" } access
                && member.Model.GetSymbolInfo(access).Symbol is IPropertySymbol { IsStatic: false } property
                && TypeKey(property.ContainingType) == "Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData"
                && !property.Locations.Any(static location => location.IsInSource)
                && AssemblyIdentityMatches(property.ContainingAssembly.Identity, typeof(Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData).Assembly.GetName())
                && !inventoryFacts.EfEventDataSpelled;

        private TraversalEvidence TraverseDbContextHooks(
            IReadOnlyList<DbContextHook> hooks,
            string rootType,
            string operationId,
            SyntaxNode node,
            HashSet<TraversalStateIdentity> active,
            bool lifecycle)
        {
            TraversalEvidence evidence = TraversalEvidence.NoEvidence;

            foreach (DbContextHook hook in hooks)
            {
                if (hook.Member is not { } hooked)
                {
                    RefuseTraversalCompletion();

                    diagnostics.Add(new("HOSTED_SITE_UNCLASSIFIED", Location(node), hook.Refusal ?? "An EF hook the analysis cannot enter."));

                    continue;
                }

                evidence = MergeEvidence(
                    evidence,
                    Traverse(
                        hooked,
                        rootType,
                        operationId + "/" + hook.Step + "@" + Location(node),
                        active,
                        lifecycle,
                        null,
                        null,
                        recoveryEffect: null));
            }

            return evidence;
        }

        // ---- Compiled model.

        /// <summary>
        /// EF does not build a model it was given: when the options carry a compiled model (<c>UseModel</c> on every path
        /// that can configure the instance) neither <c>OnModelCreating</c> nor <c>ConfigureConventions</c> can run. Only
        /// an instance whose configuration can be followed is proven; anything else leaves the hooks to be judged.
        /// </summary>
        private bool CompiledModelSupplied(AuthoredMember member, ExpressionSyntax? receiver, ContextUse use, INamedTypeSymbol type)
        {
            if (!ContextOptionsForwarded(type))
            {
                return false;
            }

            switch (use.Origin)
            {
                case ContextOrigin.Container:
                    return RegistrationFacts(type, member.Model.Compilation) is { ModelSupplied: true };

                case ContextOrigin.Created when receiver is not null:
                    return CreatedContextSuppliesModel(member, receiver, type);

                case ContextOrigin.Universe:
                    return ContextUniverse(type, member.Model.Compilation) is { ModelSupplied: true };

                default:
                    return false;
            }
        }

        private bool CreatedContextSuppliesModel(AuthoredMember member, ExpressionSyntax receiver, INamedTypeSymbol type)
        {
            CleanupValueFlow flow = CleanupValueFlowOf(member, receiver, new CleanupProvenanceContext());

            if (!flow.Complete || flow.Values.Count == 0)
            {
                return false;
            }

            foreach (BoundValueSource source in flow.Values)
            {
                if (StripTransparentExpression(source.Expression) is not BaseObjectCreationExpressionSyntax creation
                    || AnalyzeContextCreation(source.Caller, creation, type, member.Model.Compilation) is not { Understood: true, Builder.ModelSupplied: true })
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>What the options one allocation of a context was built from supply, when they can be followed.</summary>
        private sealed record CreationFacts(bool Understood, BuilderFacts Builder, bool Configured);

        /// <summary>
        /// The options an allocation hands the context constructor, followed through the fluent chain that built them.
        /// Understood only when the constructor takes exactly one options value and forwards it unchanged to the base
        /// context, so that what the chain supplied is what EF sees.
        /// </summary>
        private CreationFacts AnalyzeContextCreation(AuthoredMember caller, BaseObjectCreationExpressionSyntax creation, INamedTypeSymbol type, Compilation typeCompilation)
        {
            BuilderFacts facts = new();

            bool configured = false;

            if (caller.Model.GetOperation(creation) is not IObjectCreationOperation operation
                || operation.Constructor is not { } constructor
                || !SameBoundType(constructor.ContainingType, caller.Model.Compilation, type, typeCompilation))
            {
                return new(false, facts, false);
            }

            IArgumentOperation[] options = [.. operation.Arguments.Where(static argument => argument.Parameter is { Type: { } parameterType }
                && IsDbContextOptions(parameterType, null))];

            if (options.Length != 1
                || options[0].Parameter is not { } optionsParameter
                || options[0].Value.Syntax is not ExpressionSyntax optionsExpression
                || !ConstructorForwardsOptions(constructor, optionsParameter, 0)
                || !AnalyzeOptionsExpression(caller, optionsExpression, facts, ref configured, 0))
            {
                return new(false, facts, false);
            }

            return new(true, facts, configured);
        }

        // The options expression is `.Options` of a fluent chain over a fresh builder (`new DbContextOptionsBuilder<T>()`
        // followed by reviewed builder members), or a local or parameter every value of which is such an expression. A
        // chain is one expression, so every link executes. What the chain does to the options is recorded in the facts;
        // a value that is not such a chain is not understood, and nothing about it is claimed.
        private bool AnalyzeOptionsExpression(AuthoredMember member, ExpressionSyntax options, BuilderFacts facts, ref bool configured, int depth)
        {
            options = StripTransparentExpression(options);

            if (options is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Options" } access
                && member.Model.GetSymbolInfo(access).Symbol is IPropertySymbol { IsStatic: false } property
                && IsOptionsBuilderType(property.ContainingType))
            {
                return AnalyzeBuilderChain(member, access.Expression, facts, ref configured);
            }

            if (depth >= 3 || options is not (IdentifierNameSyntax or MemberAccessExpressionSyntax))
            {
                return false;
            }

            if (FollowedValues(member, options) is not { } values)
            {
                return false;
            }

            // The options are whichever value reaches the use, so what holds of the options holds of every value: the model is
            // supplied only if each value supplies it and the provider is configured only if each configures it, while an
            // interceptor of any value can be raised.
            bool modelSupplied = true;

            bool allConfigured = true;

            foreach ((AuthoredMember caller, ExpressionSyntax value) in values)
            {
                BuilderFacts valueFacts = new();

                bool valueConfigured = false;

                if (ReferenceEquals(value, options) || !AnalyzeOptionsExpression(caller, value, valueFacts, ref valueConfigured, depth + 1))
                {
                    return false;
                }

                modelSupplied &= valueFacts.ModelSupplied;

                allConfigured &= valueConfigured;

                facts.InterceptorsClosed &= valueFacts.InterceptorsClosed;

                facts.Interceptors.AddRange(valueFacts.Interceptors);
            }

            facts.ModelSupplied |= modelSupplied;

            configured |= allConfigured;

            return true;
        }

        private bool AnalyzeBuilderChain(AuthoredMember member, ExpressionSyntax chain, BuilderFacts facts, ref bool configured)
        {
            bool model = false;

            ExpressionSyntax current = StripTransparentExpression(chain);

            while (true)
            {
                switch (current)
                {
                    case BaseObjectCreationExpressionSyntax creation
                        when member.Model.GetTypeInfo(creation).Type is { } built && IsOptionsBuilderType(built):
                        // A builder made from existing options carries everything those options carry, so the options are
                        // followed too; an argument nobody can follow leaves the whole chain not understood.
                        if (creation.ArgumentList is { Arguments.Count: > 0 } copied)
                        {
                            BuilderFacts inner = new();

                            bool innerConfigured = false;

                            if (copied.Arguments.Count != 1
                                || !AnalyzeOptionsExpression(member, copied.Arguments[0].Expression, inner, ref innerConfigured, 0))
                            {
                                return false;
                            }

                            facts.ModelSupplied |= inner.ModelSupplied;

                            facts.InterceptorsClosed &= inner.InterceptorsClosed;

                            facts.Interceptors.AddRange(inner.Interceptors);

                            configured |= innerConfigured;
                        }

                        facts.ModelSupplied |= model;

                        return true;

                    case InvocationExpressionSyntax call
                        when member.Model.GetOperation(call) is IInvocationOperation link
                            && ExactInvocationReceiverOrFirstArgument(link) is { } receiver
                            && IsOptionsBuilderValue(link.Instance ?? link.Arguments.OrderBy(static argument => argument.Parameter?.Ordinal ?? int.MaxValue).FirstOrDefault()?.Value):
                        IMethodSymbol method = link.TargetMethod.ReducedFrom ?? link.TargetMethod;

                        if (IsExactSqliteOptionsBuilderCall(method))
                        {
                            configured = true;
                        }
                        else if (!IsReviewedBuilderChainLink(method))
                        {
                            return false;
                        }
                        else if (method.Name == "UseModel" && link.Arguments.LastOrDefault() is { } suppliedModel
                            && suppliedModel.Value.ConstantValue is not { HasValue: true, Value: null })
                        {
                            model = true;
                        }
                        else if (method.Name == "AddInterceptors")
                        {
                            CollectInterceptors(link, member.Model, facts);
                        }

                        current = StripTransparentExpression(receiver);

                        break;

                    default:
                        return false;
                }
            }
        }

        // A builder member that records state and runs no code of its own: the exact connection-string UseSqlite, or a
        // provider-neutral setting of EF's own builder (matched by exact assembly identity, never by name alone).
        private static bool IsReviewedBuilderChainLink(IMethodSymbol method) =>
            TypeKey(method.ContainingType) is "Microsoft.EntityFrameworkCore.DbContextOptionsBuilder" or "Microsoft.EntityFrameworkCore.DbContextOptionsBuilder`1"
                && NonProviderOptionsBuilderMembers.Contains(method.Name)
                && !method.Locations.Any(static location => location.IsInSource)
                && AssemblyIdentityMatches(method.ContainingAssembly.Identity, typeof(Microsoft.EntityFrameworkCore.DbContextOptionsBuilder).Assembly.GetName());

        // ---- What the registrations of a context supply.

        /// <summary>What the builder callbacks of a context's registrations do to its options, walked like the provider scan.</summary>
        private sealed class BuilderFacts
        {
            // UseModel with a model is called on every path through the scope.
            internal bool ModelSupplied { get; set; }

            // Every AddInterceptors argument is an allocation, or a static readonly singleton, of a concrete type.
            internal bool InterceptorsClosed { get; set; } = true;

            internal List<(INamedTypeSymbol Type, Compilation Compilation)> Interceptors { get; } = [];
        }

        internal sealed record RegistrationFactsResult(
            bool ModelSupplied,
            bool InterceptorsClosed,
            IReadOnlyList<(INamedTypeSymbol Type, Compilation Compilation)> Interceptors,
            int Count);

        private readonly Dictionary<BoundTypeIdentity, RegistrationFactsResult> registrationFacts = [];

        /// <summary>
        /// Every registration of the context is walked: the compiled model is supplied only if each of them supplies it,
        /// and the interceptor set is the union of what each of them adds. The interceptor set is closed only if every
        /// registration is understood, every argument resolves to a concrete type, and no other descriptor can hand EF
        /// an interceptor.
        /// </summary>
        internal RegistrationFactsResult RegistrationFacts(INamedTypeSymbol dbContext, Compilation consumingCompilation)
        {
            BoundTypeIdentity identity = TypeIdentity(dbContext, consumingCompilation);

            if (registrationFacts.TryGetValue(identity, out RegistrationFactsResult? cached))
            {
                return cached;
            }

            ResolvedRegistration[] registrations = SqliteDbContextRegistrations(dbContext, consumingCompilation);

            bool modelSupplied = registrations.Length != 0;

            bool interceptorsClosed = registrations.Length != 0;

            List<(INamedTypeSymbol Type, Compilation Compilation)> interceptors = [];

            foreach (ResolvedRegistration registration in registrations)
            {
                AnonymousFunctionExpressionSyntax[] callbacks = [.. registration.Call.ArgumentList.Arguments
                    .SelectMany(static argument => argument.Expression.DescendantNodesAndSelf().OfType<AnonymousFunctionExpressionSyntax>())];

                BuilderFacts facts = new();

                if (callbacks is not [AnonymousFunctionExpressionSyntax callback]
                    || !BuilderScopeCannotChangeProvider(callback, registration.Model, [], facts))
                {
                    modelSupplied = false;

                    interceptorsClosed = false;

                    continue;
                }

                modelSupplied &= facts.ModelSupplied;

                interceptorsClosed &= facts.InterceptorsClosed;

                interceptors.AddRange(facts.Interceptors);
            }

            interceptorsClosed = interceptorsClosed
                && !HasCompetingDbContextServiceDescriptor(dbContext, consumingCompilation)
                && !ServiceCollectionValueEscapes();

            RegistrationFactsResult result = new(modelSupplied, interceptorsClosed, interceptors, registrations.Length);

            registrationFacts.Add(identity, result);

            return result;
        }

        private static bool IsInterceptorType(INamedTypeSymbol type) =>
            type.TypeKind == TypeKind.Class
            && type.AllInterfaces.Any(static contract => TypeKey(contract) == EfInterceptorMarker
                && !IsAuthoredSymbol(contract)
                && IsEfAssembly(contract.ContainingAssembly));

        // True when a static constructor of the type, in any part of its declaration, can assign the type's static storage.
        private static bool DeclaresStaticConstructor(INamedTypeSymbol? type) =>
            type is null
            || type.DeclaringSyntaxReferences.Any(static reference => reference.GetSyntax() is TypeDeclarationSyntax declaration
                && declaration.Members.OfType<ConstructorDeclarationSyntax>().Any(static constructor => constructor.Modifiers.Any(SyntaxKind.StaticKeyword)));

        // The interceptors one AddInterceptors call hands EF: inline allocations of a type that implements EF's interceptor
        // contract, and static readonly singletons of such a type whose initializer is an allocation and that no static
        // constructor can reassign. A collection, a parameter, a service lookup or any other value is an interceptor of a
        // type this analysis cannot name, so the set is not closed.
        private void CollectInterceptors(IInvocationOperation invocation, SemanticModel model, BuilderFacts facts)
        {
            foreach (IOperation argument in invocation.Arguments
                .SelectMany(static argument => argument.Value is IArrayCreationOperation array
                    ? array.Initializer?.ElementValues ?? []
                    : [argument.Value]))
            {
                IOperation value = argument;

                while (value is IConversionOperation conversion)
                {
                    value = conversion.Operand;
                }

                if (value is IObjectCreationOperation { Type: INamedTypeSymbol created } && IsInterceptorType(created))
                {
                    facts.Interceptors.Add((created, model.Compilation));

                    continue;
                }

                ISymbol? storage = value switch
                {
                    IFieldReferenceOperation { Field: { IsStatic: true, IsReadOnly: true } field } => field,
                    IPropertyReferenceOperation { Property: { IsStatic: true, SetMethod: null } property } => property,
                    _ => null,
                };

                SyntaxReference? storageReference = storage?.DeclaringSyntaxReferences is [{ } only] ? only : null;

                BaseObjectCreationExpressionSyntax? singleton = storageReference?.GetSyntax() switch
                {
                    VariableDeclaratorSyntax { Initializer.Value: BaseObjectCreationExpressionSyntax allocation } => allocation,
                    PropertyDeclarationSyntax { Initializer.Value: BaseObjectCreationExpressionSyntax allocation } => allocation,
                    _ => null,
                };

                // The declaring tree can belong to another analysed compilation than the registration's.
                if (singleton is not null
                    && storageReference is not null
                    && !DeclaresStaticConstructor(storage?.ContainingType)
                    && semanticModels.TryGetValue(storageReference.SyntaxTree, out SemanticModel? storageModel)
                    && storageModel.GetTypeInfo(singleton).Type is INamedTypeSymbol singletonType
                    && IsInterceptorType(singletonType))
                {
                    facts.Interceptors.Add((singletonType, storageModel.Compilation));

                    continue;
                }

                facts.InterceptorsClosed = false;
            }
        }
    }
}
