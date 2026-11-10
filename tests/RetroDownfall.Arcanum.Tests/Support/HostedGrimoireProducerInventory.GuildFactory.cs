using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    internal static (bool Complete, IReadOnlyList<HostedProducerInventoryDiagnostic> Diagnostics) ProbeNamedHttpFactoryCreation(
        IReadOnlyList<Microsoft.CodeAnalysis.CSharp.CSharpCompilation> compilations, string type, string member) =>
        new ProducerGraph(compilations, DefaultEvaluationEnvironmentMaximumMembers, DefaultEvaluationEnvironmentMaximumDepth,
            DefaultTraversalMaximumDepth, DefaultMaximumAnalyzedStatesPerRoot, false).ProbeNamedHttpFactoryCreation(type, member);

    internal static (bool Complete, IReadOnlyList<HostedProducerInventoryDiagnostic> Diagnostics) ProbeProductionNamedHttpFactoryCreation(string type, string member) =>
        HostedProducerRootWorkers.OnAnalysisStack(() =>
        {
            IReadOnlyList<Microsoft.CodeAnalysis.CSharp.CSharpCompilation> compilations = ProductionCompilations;

            HostedProducerPremiseLedger ledger = VerifyPremises(compilations, ProductionPremises);

            if (ledger.Violations.Count != 0) return (false, (IReadOnlyList<HostedProducerInventoryDiagnostic>)ledger.Violations.ToArray());

            return new ProducerGraph(compilations, DefaultEvaluationEnvironmentMaximumMembers, DefaultEvaluationEnvironmentMaximumDepth,
                DefaultTraversalMaximumDepth, DefaultMaximumAnalyzedStatesPerRoot, false, premises: ledger).ProbeNamedHttpFactoryCreation(type, member);
        });

    private sealed partial class ProducerGraph
    {
        internal (bool Complete, IReadOnlyList<HostedProducerInventoryDiagnostic> Diagnostics) ProbeNamedHttpFactoryCreation(string type, string name)
        {
            AuthoredMember member = AuthoredMembers.Single(candidate => TypeKey(candidate.Symbol.ContainingType) == type && candidate.Symbol.Name == name);

            InvocationExpressionSyntax call = member.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Single(invocation => member.Model.GetSymbolInfo(invocation).Symbol is IMethodSymbol method && IsExactLongRestHttpFactoryMethod(method));

            IMethodSymbol target = (IMethodSymbol)member.Model.GetSymbolInfo(call).Symbol!;

            bool complete = IsReviewedNamedHttpFactoryCreation(target, call, member);

            return (complete, diagnostics.ToArray());
        }

        private readonly Dictionary<(string Name, BoundTypeIdentity Consumer), bool> namedHttpFactoryCertificates = [];

        private bool IsReviewedNamedHttpFactoryCreation(IMethodSymbol method, SyntaxNode? node, AuthoredMember? member)
        {
            if (!IsExactLongRestHttpFactoryMethod(method)) return false;

            if (httpConnectCertificate || member is null
                || node is not InvocationExpressionSyntax call
                || member.Model.GetOperation(call) is not IInvocationOperation { Arguments: [{ Value.ConstantValue: { HasValue: true, Value: string name } }] }
                || AdmissionReceiver(member, call) is not { } receiver
                || !IsExactNamedFactoryDependency(member, receiver, new(SymbolEqualityComparer.Default))) return false;

            var key = (name, TypeIdentity(member.Symbol.ContainingType, member.Model.Compilation));

            if (!namedHttpFactoryCertificates.TryGetValue(key, out bool complete))
            {
                complete = HasClosedNamedHttpFactoryComposition(name, member);

                namedHttpFactoryCertificates.Add(key, complete);
            }

            return complete;
        }

        private sealed class HttpFactoryProofWork(int remaining)
        {
            internal bool TryTake()
            {
                if (remaining <= 0) return false;

                remaining--;

                return true;
            }
        }

        private HttpFactoryProofWork? httpConnectManualWork;

        private bool IsExactNamedFactoryDependency(AuthoredMember owner, ExpressionSyntax expression, HashSet<ISymbol> path, HttpFactoryProofWork? work = null)
        {
            work ??= new(maximumAnalyzedStatesPerRoot);

            if (path.Count >= traversalMaximumDepth || !work.TryTake()) return false;

            if (!TryNormalizeBuiltInCleanupValue(owner.Model, expression, out expression, requireIdentityPreservingCasts: true)
                || owner.Model.GetSymbolInfo(expression).Symbol is not { } symbol || !path.Add(symbol)) return false;

            try
            {
                if (symbol is IFieldSymbol { IsStatic: false, IsReadOnly: true, DeclaredAccessibility: Accessibility.Private } field
                    && CanUseCurrentInstanceStorageBinding(field, expression)
                    && SameBoundType(field.ContainingType, owner.Model.Compilation, owner.Symbol.ContainingType, owner.Model.Compilation))
                {
                    // Capturing the current service in an owned lifecycle wrapper may
                    // carry this slot as a bound source. Follow that exact readonly
                    // slot to its constructor formal; it is not a supplied factory.
                    if (owner.ValueBindings?.TryGetValue(field, out BoundValueSource? bound) == true && bound is not null)
                        return IsExactNamedFactoryDependency(bound.Caller, bound.Expression, path, work);

                    BoundValueSource[] sources = ExactReadOnlyFieldValues(owner, expression, field);

                    return sources.Length != 0 && sources.All(source => IsExactNamedFactoryDependency(source.Caller, source.Expression, path, work));
                }

                // A bound constructor input still names an actual caller-supplied
                // factory and cannot borrow an independent default DI registration.
                if (owner.ValueBindings?.ContainsKey(symbol) == true) return false;

                return symbol is IParameterSymbol { RefKind: RefKind.None, ContainingSymbol: IMethodSymbol { MethodKind: MethodKind.Constructor } } parameter
                    && IsExactFrameworkType(parameter.Type, "System.Net.Http.IHttpClientFactory", typeof(IHttpClientFactory).Assembly.GetName())
                    && !HasConstructorDependencyMutation(owner, parameter)
                    && !objectCreations.Any(candidate => candidate.Model.GetSymbolInfo(candidate.Creation).Symbol is IMethodSymbol constructed
                        && SameBoundType(constructed.ContainingType, candidate.Model.Compilation, owner.Symbol.ContainingType, owner.Model.Compilation))
                    && IsExactRegisteredConstructorDependency(owner, parameter, parameter.Type);
            }
            finally
            {
                path.Remove(symbol);
            }
        }

        private static bool IsHttpFactoryCompositionType(ITypeSymbol type)
        {
            HashSet<INamedTypeSymbol> expandedInterfaces = new(SymbolEqualityComparer.Default);

            static bool Protected(ITypeSymbol candidate) => TypeKey(candidate) is
                "System.Net.Http.IHttpClientFactory" or "System.Net.Http.IHttpMessageHandlerFactory"
                or "Microsoft.Extensions.Http.HttpClientFactoryOptions" or "Microsoft.Extensions.Http.HttpMessageHandlerBuilder"
                or "Microsoft.Extensions.Http.IHttpMessageHandlerBuilderFilter";

            bool Search(ITypeSymbol candidate)
            {
                if (candidate is IArrayTypeSymbol array) return Search(array.ElementType);

                if (candidate is not INamedTypeSymbol named) return false;

                if (Protected(named) || named.TypeArguments.Any(Search)) return true;

                // IEquatable<T> and authored recursive generic interfaces can return
                // to the same definition with ever-larger constructed type arguments.
                // Actual arguments are inspected above; expand each interface definition
                // once, without an arbitrary depth or state allowance.
                return expandedInterfaces.Add(named.OriginalDefinition)
                    && named.AllInterfaces.Any(contract => Protected(contract) || contract.TypeArguments.Any(Search));
            }

            return Search(type);
        }

        private static bool IsExactHttpBuilderMethod(IMethodSymbol method, string name) =>
            IsExactFrameworkMethod(method, "Microsoft.Extensions.DependencyInjection.HttpClientBuilderExtensions", name, "Microsoft.Extensions.Http");

        private bool HasClosedNamedHttpFactoryComposition(string name, AuthoredMember consumer)
        {
            if (ServiceCollectionValueEscapes()) return false;

            List<(InvocationExpressionSyntax Call, SemanticModel Model)> selected = [];

            foreach (var candidate in invocations)
            {
                if (candidate.Model.GetSymbolInfo(candidate.Call).Symbol is not IMethodSymbol method) continue;

                IMethodSymbol definition = method.ReducedFrom ?? method;

                if (IsExactFrameworkMethod(method, "Microsoft.Extensions.DependencyInjection.HttpClientFactoryServiceCollectionExtensions", "AddHttpClient", "Microsoft.Extensions.Http"))
                {
                    if (candidate.Model.GetOperation(candidate.Call) is not IInvocationOperation invocation) return false;

                    IArgumentOperation? named = invocation.Arguments.FirstOrDefault(argument => argument.Parameter?.Type.SpecialType == SpecialType.System_String);

                    // An unnamed default registration does not configure named options. Any
                    // dynamic name may select this pipeline and therefore remains unknown.
                    if (named is null) continue;

                    if (named.Value.ConstantValue is not { HasValue: true, Value: string registered }) return false;

                    if (registered == name) selected.Add(candidate);

                    continue;
                }

                if (method.Name == "ConfigureHttpClientDefaults"
                    && IsExactFrameworkMethod(method, "Microsoft.Extensions.DependencyInjection.HttpClientFactoryServiceCollectionExtensions", method.Name, "Microsoft.Extensions.Http")) return false;

                if (IsExactServiceDescriptorCollectionMutation(method)) return false;

                bool buildsProvider = method.Name == "BuildServiceProvider"
                    && IsExactFrameworkType(definition.ContainingType, "Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions",
                        typeof(Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions).Assembly.GetName());

                if (buildsProvider) continue;

                bool serviceCollectionMutation = definition.Parameters.Any(parameter => IsExactFrameworkType(parameter.Type,
                    "Microsoft.Extensions.DependencyInjection.IServiceCollection", typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection).Assembly.GetName()));

                if (serviceCollectionMutation && (method.IsExtern || method.IsAbstract
                    || (method.IsVirtual || method.IsOverride) && !method.IsSealed && !method.ContainingType.IsSealed)) return false;

                if (serviceCollectionMutation && candidate.Model.GetOperation(candidate.Call) is IInvocationOperation mutation)
                {
                    foreach (IArgumentOperation argument in mutation.Arguments)
                    {
                        if (argument.Parameter?.Type is not { } argumentType) continue;

                        IOperation value = argument.Value;

                        while (value is IConversionOperation { OperatorMethod: null } conversion) value = conversion.Operand;

                        if (IsExactFrameworkType(argumentType, "Microsoft.Extensions.DependencyInjection.ServiceDescriptor",
                            typeof(Microsoft.Extensions.DependencyInjection.ServiceDescriptor).Assembly.GetName()))
                        {
                            if (value is not IInvocationOperation descriptor
                                || descriptor.TargetMethod.Name is not ("Scoped" or "Singleton" or "Transient")
                                || !IsExactFrameworkType(descriptor.TargetMethod.ContainingType, "Microsoft.Extensions.DependencyInjection.ServiceDescriptor",
                                    typeof(Microsoft.Extensions.DependencyInjection.ServiceDescriptor).Assembly.GetName())
                                || descriptor.TargetMethod.TypeArguments.Length == 0
                                || descriptor.TargetMethod.TypeArguments.Any(type => type is ITypeParameterSymbol || IsHttpFactoryCompositionType(type))) return false;
                        }

                        if (IsExactFrameworkType(argumentType, "System.Type", typeof(Type).Assembly.GetName())
                            && value is not ITypeOfOperation) return false;
                    }
                }

                if (serviceCollectionMutation && (method.TypeArguments.Any(IsHttpFactoryCompositionType)
                        || candidate.Call.DescendantNodes().OfType<TypeOfExpressionSyntax>().Any(typeOf => candidate.Model.GetTypeInfo(typeOf.Type).Type is { } type && IsHttpFactoryCompositionType(type)))) return false;

                if (serviceCollectionMutation && method.Name is "Replace" or "RemoveAll" or "Remove" or "RemoveAt" or "Clear") return false;

                if (serviceCollectionMutation && method.Locations.All(static location => !location.IsInSource)
                    && !IsExactProductionFrameworkServiceBuilder(method)
                    && !IsExactServiceCollectionFluentMethod(method)
                    && !IsExactCompositionRegistrationMethod(method)
                    && !IsExactTryAddEnumerableMutation(method)
                    && !IsExactFrameworkMethod(method, "Microsoft.Extensions.DependencyInjection.OptionsServiceCollectionExtensions", "AddOptions", "Microsoft.Extensions.Options"))
                {
                    return false;
                }
            }

            if (semanticModels.Any(pair => pair.Key.GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Any(assignment => pair.Value.GetOperation(assignment.Left) is IPropertyReferenceOperation
                    { Property.IsIndexer: true, Instance.Type: { } collection } && IsRecoveryServiceCollectionType(collection)))) return false;

            if (selected is not [(InvocationExpressionSyntax registration, SemanticModel model)]
                || model.GetOperation(registration) is not IInvocationOperation add
                || add.Arguments.Where(argument => argument.Parameter?.Type.TypeKind == TypeKind.Delegate).ToArray()
                    is not [{ Value.Syntax: AnonymousFunctionExpressionSyntax configure }]
                || ResolveCallable(configure, model, null, null) is not { } configureBody
                || configureBody.Symbol.IsAsync || !configure.Modifiers.Any(SyntaxKind.StaticKeyword)
                || !HasOnlyExactHttpClientConfigurationStores(configureBody)
                || !NamedHttpRegistrationImmediatelyPrecedesConsumer(registration, model, consumer)) return false;

            if (registration.Parent is not MemberAccessExpressionSyntax { Expression: var firstReceiver, Parent: InvocationExpressionSyntax removeLoggers } first
                || firstReceiver != registration || model.GetSymbolInfo(removeLoggers).Symbol is not IMethodSymbol remove
                || !IsExactHttpBuilderMethod(remove, "RemoveAllLoggers")
                || removeLoggers.ArgumentList.Arguments.Count != 0
                || removeLoggers.Parent is not MemberAccessExpressionSyntax { Expression: var secondReceiver, Parent: InvocationExpressionSyntax configureHandler }
                || secondReceiver != removeLoggers || model.GetSymbolInfo(configureHandler).Symbol is not IMethodSymbol handlerMethod
                || !IsExactHttpBuilderMethod(handlerMethod, "ConfigurePrimaryHttpMessageHandler")
                || configureHandler.ArgumentList.Arguments is not [{ Expression: AnonymousFunctionExpressionSyntax factory }]
                || factory is not { } || !factory.Modifiers.Any(SyntaxKind.StaticKeyword)
                || configureHandler.Parent is not ExpressionStatementSyntax
                || ResolveCallable(factory, model, null, null) is not { } handlerFactory
                || handlerFactory.Symbol.IsAsync || handlerFactory.Symbol.Parameters.Length != 0) return false;

            return HasExactOwnedSocketsHandlerFactory(handlerFactory, new HashSet<GraphMemberIdentity>());
        }

        private bool NamedHttpRegistrationImmediatelyPrecedesConsumer(InvocationExpressionSyntax registration, SemanticModel model, AuthoredMember consumer)
        {
            if (registration.Expression is not MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax collection }
                || model.GetSymbolInfo(collection).Symbol is not IParameterSymbol and not ILocalSymbol
                || registration.Ancestors().OfType<ExpressionStatementSyntax>().FirstOrDefault() is not { Parent: BlockSyntax block } configured
                || !diActivationRegistrations.TryGetValue(new(consumer.Symbol.ContainingAssembly.Identity, TypeKey(consumer.Symbol.ContainingType)), out var candidates)) return false;

            ResolvedRegistration[] exact = candidates.Where(candidate => candidate.Method.TypeArguments is [ITypeSymbol service]
                    && SameBoundType(service, candidate.Model.Compilation, consumer.Symbol.ContainingType, consumer.Model.Compilation)
                    && candidate.Call.ArgumentList.Arguments.Count == 0
                    && candidate.Method.Name is "AddSingleton" or "AddScoped" or "AddTransient" or "TryAddSingleton" or "TryAddScoped" or "TryAddTransient"
                    && IsExactServiceCollectionFluentMethod(candidate.Method))
                .DistinctBy(static candidate => candidate.Call, ReferenceEqualityComparer.Instance).ToArray();

            return exact is [ResolvedRegistration activation]
                && ReferenceEquals(activation.Model.Compilation, model.Compilation)
                && activation.Call.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax actual }
                && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(collection).Symbol, activation.Model.GetSymbolInfo(actual).Symbol)
                && activation.Call.Parent is ExpressionStatementSyntax created
                && created.Parent is BlockSyntax createdBlock && createdBlock.SyntaxTree == block.SyntaxTree && createdBlock.Span == block.Span
                && block.Statements.IndexOf(configured) is int index && index >= 0
                && index + 1 < block.Statements.Count && block.Statements[index + 1].Span == created.Span;
        }

        private static bool HasOnlyExactHttpClientConfigurationStores(AuthoredMember body)
        {
            if (body.Symbol.Parameters is not [IParameterSymbol client]
                || !IsExactFrameworkType(client.Type, "System.Net.Http.HttpClient", typeof(HttpClient).Assembly.GetName())
                || body.Syntax is not BlockSyntax { Statements.Count: 2 } block) return false;

            HashSet<string> assigned = new(StringComparer.Ordinal);

            foreach (StatementSyntax statement in block.Statements)
            {
                if (statement is not ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax assignment }
                    || !assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                    || body.Model.GetOperation(assignment) is not ISimpleAssignmentOperation
                    { Target: IPropertyReferenceOperation { Instance: IParameterReferenceOperation receiver, Property: { } property }, Value: { } value }
                    || !SymbolEqualityComparer.Default.Equals(receiver.Parameter, client)
                    || !IsExactFrameworkType(property.ContainingType, "System.Net.Http.HttpClient", typeof(HttpClient).Assembly.GetName())
                    || !assigned.Add(property.Name)) return false;

                while (value is IConversionOperation { OperatorMethod: null } conversion) value = conversion.Operand;

                if (property.Name == "Timeout")
                {
                    if (value is not IFieldReferenceOperation { Field: { IsStatic: true, IsReadOnly: true, Name: "InfiniteTimeSpan" } timeout }
                        || !IsExactFrameworkType(timeout.ContainingType, "System.Threading.Timeout", typeof(Timeout).Assembly.GetName())) return false;
                }
                else if (property.Name == "MaxResponseContentBufferSize")
                {
                    if (value.ConstantValue is not { HasValue: true, Value: int or long }) return false;
                }
                else return false;
            }

            return true;
        }

        private readonly Dictionary<INamedTypeSymbol, bool> httpSetupStaticTypes = new(SymbolEqualityComparer.Default);

        private bool IsStableHttpFactorySetupArgument(IOperation value)
        {
            while (value is IConversionOperation { OperatorMethod: null } conversion) value = conversion.Operand;

            if (value.ConstantValue.HasValue || value is IDefaultValueOperation or IParameterReferenceOperation) return true;

            return value is IFieldReferenceOperation { Instance: null, Field: { IsStatic: true, IsReadOnly: true } field }
                && HasClosedHttpSetupStaticInitialization(field.ContainingType);
        }

        private bool HasClosedHttpSetupStaticInitialization(INamedTypeSymbol type)
        {
            if (httpSetupStaticTypes.TryGetValue(type, out bool cached)) return cached;

            // A field read can trigger every initializer of its declaring type. The
            // supported profile contains only constants, default storage, exact BCL
            // TimeSpan factories with constant inputs, and an implicit sealed data
            // constructor. Arbitrary helpers, other field dependencies, explicit
            // constructors and finalizers retain their ordinary setup obligation.
            httpSetupStaticTypes[type] = false;

            if (!type.Locations.Any(static location => location.IsInSource)
                || type.StaticConstructors.Any(static constructor => !constructor.IsImplicitlyDeclared)) return false;

            foreach (ISymbol storage in type.GetMembers().Where(static member => member.IsStatic && !member.IsImplicitlyDeclared
                && member is IFieldSymbol { HasConstantValue: false } or IPropertySymbol or IEventSymbol))
            {
                foreach (SyntaxReference reference in storage.DeclaringSyntaxReferences)
                {
                    SyntaxNode syntax = reference.GetSyntax();

                    ExpressionSyntax? expression = syntax switch
                    {
                        VariableDeclaratorSyntax variable => variable.Initializer?.Value,
                        PropertyDeclarationSyntax property => property.Initializer?.Value,
                        _ => null,
                    };

                    if (expression is null) continue;

                    if (AnalysedModel(expression) is not { } model || model.GetOperation(expression) is not { } operation) return false;

                    if (operation.ConstantValue.HasValue || operation is IDefaultValueOperation) continue;

                    if (operation is IInvocationOperation factory
                        && factory.TargetMethod is { IsStatic: true, Arity: 0, Name: "FromSeconds" or "FromMilliseconds" or "FromMinutes" or "FromHours" or "FromTicks" }
                        && IsExactFrameworkType(factory.TargetMethod.ContainingType, "System.TimeSpan", typeof(TimeSpan).Assembly.GetName())
                        && IsExactFrameworkType(factory.TargetMethod.ReturnType, "System.TimeSpan", typeof(TimeSpan).Assembly.GetName())
                        && factory.Arguments.All(static argument => argument.Parameter?.RefKind == RefKind.None
                            && argument.InConversion.MethodSymbol is null && argument.OutConversion.MethodSymbol is null && argument.Value.ConstantValue.HasValue)) continue;

                    if (operation is IObjectCreationOperation { Constructor: { IsImplicitlyDeclared: true, Parameters.Length: 0 } constructor, Initializer: null }
                        && constructor.ContainingType is { IsSealed: true, BaseType.SpecialType: SpecialType.System_Object } allocated
                        && !allocated.GetMembers().OfType<IMethodSymbol>().Any(static method => method.MethodKind == MethodKind.Destructor)
                        && !allocated.GetMembers().Where(static member => member is IFieldSymbol or IPropertySymbol or IEventSymbol)
                            .SelectMany(static member => member.DeclaringSyntaxReferences).Any(reference => reference.GetSyntax() is VariableDeclaratorSyntax { Initializer: not null }
                                or PropertyDeclarationSyntax { Initializer: not null })
                        && HasClosedHttpSetupStaticInitialization(allocated)) continue;

                    return false;
                }
            }

            httpSetupStaticTypes[type] = true;

            return true;
        }

        private bool HasExactOwnedSocketsHandlerFactory(AuthoredMember body, HashSet<GraphMemberIdentity> path, HttpFactoryProofWork? work = null)
        {
            work ??= new(maximumAnalyzedStatesPerRoot);

            if (path.Count >= traversalMaximumDepth || !work.TryTake() || !path.Add(MemberIdentity(body))) return false;

            try
            {
                // Setup consists only of direct source forwarding, argument validation,
                // and returning the one fresh handler. No handler alias can be retained.
                foreach (StatementSyntax statement in body.Syntax.DescendantNodesAndSelf(node => node == body.Syntax
                    || node is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax).OfType<StatementSyntax>())
                {
                    if (statement is BlockSyntax or ReturnStatementSyntax) continue;

                    if (statement is not ExpressionStatementSyntax { Expression: InvocationExpressionSyntax validation }
                        || body.Model.GetSymbolInfo(validation).Symbol is not IMethodSymbol method
                        || !IsExactFrameworkType(method.ContainingType, "System.ArgumentNullException", typeof(ArgumentNullException).Assembly.GetName())
                        || method.Name != "ThrowIfNull"
                        || body.Model.GetOperation(validation) is not IInvocationOperation checkedCall
                        || checkedCall.Arguments.Any(argument => argument.Parameter?.RefKind != RefKind.None
                            || argument.InConversion.MethodSymbol is not null || argument.OutConversion.MethodSymbol is not null
                            || !IsStableHttpFactorySetupArgument(argument.Value))) return false;
                }

                ExpressionSyntax[] returned = ReturnedExpressions(body.Syntax);

                if (returned is not [ExpressionSyntax expression]) return false;

                if (body.Model.GetOperation(expression) is IInvocationOperation forwarding
                    && Resolve(forwarding.TargetMethod, body.Model.Compilation) is { } target
                    && !forwarding.TargetMethod.IsVirtual && forwarding.TargetMethod.IsStatic
                    && forwarding.Arguments.All(argument => argument.Parameter?.RefKind == RefKind.None
                        && argument.InConversion.MethodSymbol is null && argument.OutConversion.MethodSymbol is null
                        && IsStableHttpFactorySetupArgument(argument.Value)))
                {
                    return HasExactOwnedSocketsHandlerFactory(BindCleanupInvocationContext(body, (InvocationExpressionSyntax)expression, target, new CleanupProvenanceContext()), path, work);
                }

                if (body.Model.GetOperation(expression) is not IObjectCreationOperation
                    { Constructor: { Parameters.Length: 0 } constructor, Initializer.Initializers: { Length: 3 } initializers }
                    || !IsExactFrameworkType(constructor.ContainingType, "System.Net.Http.SocketsHttpHandler", typeof(SocketsHttpHandler).Assembly.GetName())) return false;

                HashSet<string> names = new(StringComparer.Ordinal);

                foreach (IOperation initializer in initializers)
                {
                    if (initializer is not ISimpleAssignmentOperation { Target: IPropertyReferenceOperation { Property: { } property }, Value: { } value }
                        || !IsExactFrameworkType(property.ContainingType, "System.Net.Http.SocketsHttpHandler", typeof(SocketsHttpHandler).Assembly.GetName())
                        || !names.Add(property.Name)) return false;

                    if (property.Name is "AllowAutoRedirect" or "UseProxy")
                    {
                        if (value.ConstantValue is not { HasValue: true, Value: false }) return false;
                    }
                    else if (property.Name == "ConnectCallback")
                    {
                        if (value.Syntax is not AnonymousFunctionExpressionSyntax callback
                            || ResolveCallable(callback, body.Model, body.CallableBindings, body.AbsentCallables) is not { } callbackBody
                            || !HasClosedHttpConnectCallback(BindStableCapturedCleanupContext(body, callback, callbackBody))) return false;
                    }
                    else return false;
                }

                return true;
            }
            finally
            {
                path.Remove(MemberIdentity(body));
            }
        }

        private bool HasClosedHttpConnectCallback(AuthoredMember callback)
        {
            if (callback.Symbol.ReturnsVoid || !IsAwaitable(callback.Symbol.ReturnType)) return false;

            // This separate analysis is a certificate about the framework's deferred
            // ConnectCallback contract. It emits no application sites and does not run
            // the callback at client creation. SocketsHttpHandler awaits its ValueTask
            // inside the real HTTP provider call; only that real call owns admission.
            ProducerGraph certificate = new(analysedCompilations, evaluationEnvironmentMaximumMembers,
                evaluationEnvironmentMaximumDepth, traversalMaximumDepth, maximumAnalyzedStatesPerRoot,
                false, premises: premises, facts: inventoryFacts);

            certificate.httpConnectCertificate = true;

            certificate.httpConnectManualWork = new(maximumAnalyzedStatesPerRoot);

            certificate.admissionCapsules["http-connect-contract/work"] = "http-connect-contract-work";

            certificate.admissionCapsules["http-connect-contract/effect"] = "http-connect-contract-effect";

            _ = certificate.Traverse(callback, "http-connect-contract", "http-connect-contract", [], false,
                "http-connect-contract/work", "http-connect-contract/effect", completionOwned: true);

            bool complete = certificate.diagnostics.Count == 0
                && certificate.sites.Values.All(static site => site.Kind == HostedProducerSiteKind.ProviderCall
                    && site.WorkFrontierCapsuleId == "http-connect-contract-work" && site.EffectFrontierCapsuleId == "http-connect-contract-effect")
                && !certificate.HasEscapingDeferredWork(callback, [], root: false)
                && certificate.HasOnlyObservedHttpConnectAwaitables(callback, [])
                && ReturnedExpressions(callback.Syntax) is { Length: > 0 } returned
                && returned.All(value => certificate.HasOwnedHttpConnectResult(callback, value, []));

            if (!complete) diagnostics.Add(new("HOSTED_HTTP_CONNECT_CERTIFICATE_UNPROVEN", Location(callback.Syntax),
                string.Join("; ", certificate.diagnostics.Take(10).Select(static diagnostic => diagnostic.Code + ": " + diagnostic.Detail))));

            return complete;
        }

        private bool httpConnectCertificate;

        private bool HasOwnedHttpSocketTransfer(AuthoredMember member, ILocalSymbol local, ExpressionSyntax transfer)
        {
            if (local.DeclaringSyntaxReferences.SingleOrDefault()?.GetSyntax() is not VariableDeclaratorSyntax
                { Initializer.Value: { } initializer, Parent: VariableDeclarationSyntax { Variables.Count: 1, Parent: LocalDeclarationStatementSyntax declaration } }
                || declaration.UsingKeyword.RawKind != 0
                || declaration.Parent is not BlockSyntax scope
                || member.Model.GetOperation(initializer) is not IObjectCreationOperation { Constructor: { } constructor, Initializer: null } creation
                || !IsExactFrameworkType(constructor.ContainingType, "System.Net.Sockets.Socket", typeof(System.Net.Sockets.Socket).Assembly.GetName())
                || !HasExactParameterTypes(constructor, "System.Net.Sockets.AddressFamily", "System.Net.Sockets.SocketType", "System.Net.Sockets.ProtocolType")
                || creation.Arguments is not [_, { Value.ConstantValue: { HasValue: true, Value: 1 } }, { Value.ConstantValue: { HasValue: true, Value: 6 } }]
                || creation.Arguments.Any(static argument => argument.InConversion.MethodSymbol is not null || argument.OutConversion.MethodSymbol is not null)) return false;

            int index = scope.Statements.IndexOf(declaration);

            if (index < 0 || index + 1 >= scope.Statements.Count
                || scope.Statements[index + 1] is not TryStatementSyntax { Finally: null, Catches.Count: > 0 } protector
                || !protector.Block.Span.Contains(transfer.Span)
                || protector.Catches.Last() is not { Declaration: null, Filter: null }) return false;

            if (protector.Block.Statements.LastOrDefault() is not ReturnStatementSyntax { Expression: { } returned } terminal
                || StripTransparentExpression(returned).Span != transfer.Span) return false;

            SyntaxNode[] direct = protector.Block.DescendantNodes(node => node is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax).ToArray();

            if (direct.OfType<ReturnStatementSyntax>().Any(statement => statement.Span != terminal.Span)
                || direct.Any(static node => node is BreakStatementSyntax or ContinueStatementSyntax or GotoStatementSyntax or YieldStatementSyntax)) return false;

            bool Dispose(InvocationExpressionSyntax call) =>
                member.Model.GetOperation(call) is IInvocationOperation { Instance: ILocalReferenceOperation receiver, TargetMethod: { Name: "Dispose", Parameters.Length: 0, ReturnsVoid: true } method }
                && SymbolEqualityComparer.Default.Equals(receiver.Local, local)
                && IsExactFrameworkType(method.ContainingType, "System.Net.Sockets.Socket", typeof(System.Net.Sockets.Socket).Assembly.GetName());

            if (protector.Catches.Any(handler => handler.Block.Statements.FirstOrDefault() is not
                ExpressionStatementSyntax { Expression: InvocationExpressionSyntax cleanup } || !Dispose(cleanup))) return false;

            InvocationExpressionSyntax[] connections = protector.Block.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(call => member.Model.GetOperation(call) is IInvocationOperation
                    { Instance: ILocalReferenceOperation receiver, TargetMethod.Name: "ConnectAsync" }
                    && SymbolEqualityComparer.Default.Equals(receiver.Local, local)).ToArray();

            if (connections is not [InvocationExpressionSyntax connect]
                || CompletionPoint(member, connect, true) is not { } completed
                || completed.Span.End >= transfer.SpanStart) return false;

            return SymbolReferences(member, local).All(reference =>
            {
                if (reference.Parent is ArgumentSyntax { Parent.Parent: BaseObjectCreationExpressionSyntax stream }
                    && stream.Span == transfer.Span && stream.SyntaxTree == transfer.SyntaxTree) return true;

                if (reference.Parent is not MemberAccessExpressionSyntax { Expression: var receiver } access || receiver != reference) return false;

                if (access.Parent is InvocationExpressionSyntax call
                    && member.Model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "ConnectAsync" or "Dispose" } method
                    && IsExactFrameworkType(method.ContainingType, "System.Net.Sockets.Socket", typeof(System.Net.Sockets.Socket).Assembly.GetName())) return true;

                return access.Parent is AssignmentExpressionSyntax { Left: var target, Right: var assigned } assignment
                    && target == access && assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                    && protector.Block.Span.Contains(assignment.Span)
                    && member.Model.GetSymbolInfo(access).Symbol is IPropertySymbol { Name: "NoDelay" } property
                    && IsExactFrameworkType(property.ContainingType, "System.Net.Sockets.Socket", typeof(System.Net.Sockets.Socket).Assembly.GetName())
                    && member.Model.GetConstantValue(assigned) is { HasValue: true, Value: true };
            });
        }

        private bool IsReviewedHttpConnectProviderConstruction(IMethodSymbol method, SyntaxNode node, AuthoredMember member)
        {
            if (!httpConnectCertificate || method.MethodKind != MethodKind.Constructor || node is not BaseObjectCreationExpressionSyntax creation) return false;

            if (IsExactFrameworkType(method.ContainingType, "System.Net.Sockets.NetworkStream", typeof(System.Net.Sockets.NetworkStream).Assembly.GetName()))
                return HasOwnedHttpConnectResult(member, creation, []);

            if (!IsExactFrameworkType(method.ContainingType, "System.Net.Sockets.Socket", typeof(System.Net.Sockets.Socket).Assembly.GetName())
                || creation.Parent is not EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax variable }
                || member.Model.GetDeclaredSymbol(variable) is not ILocalSymbol local) return false;

            BaseObjectCreationExpressionSyntax[] transfers = member.Syntax.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>()
                .Where(stream => member.Model.GetOperation(stream) is IObjectCreationOperation { Constructor: { } constructor }
                    && IsExactFrameworkType(constructor.ContainingType, "System.Net.Sockets.NetworkStream", typeof(System.Net.Sockets.NetworkStream).Assembly.GetName())
                    && stream.ArgumentList?.Arguments.FirstOrDefault() is { } socket
                    && SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(socket.Expression).Symbol, local)).ToArray();

            return transfers is [BaseObjectCreationExpressionSyntax transfer] && HasOwnedHttpSocketTransfer(member, local, transfer);
        }

        private bool IsReviewedHttpConnectStorageProperty(IPropertySymbol property, bool mutation) =>
            httpConnectCertificate && !mutation && !property.IsStatic && property.Parameters.IsEmpty
            && property.GetMethod is { IsVirtual: false, IsAbstract: false }
            && (property.Name == "DnsEndPoint" && IsExactFrameworkType(property.ContainingType,
                    "System.Net.Http.SocketsHttpConnectionContext", typeof(SocketsHttpConnectionContext).Assembly.GetName())
                || property.Name is "Host" or "Port" && IsExactFrameworkType(property.ContainingType,
                    "System.Net.DnsEndPoint", typeof(System.Net.DnsEndPoint).Assembly.GetName()));

        private bool IsHttpConnectSocketOption(IPropertySymbol property) =>
            httpConnectCertificate && !property.IsStatic && property.Name == "NoDelay" && property.Type.SpecialType == SpecialType.System_Boolean
            && IsExactFrameworkType(property.ContainingType, "System.Net.Sockets.Socket", typeof(System.Net.Sockets.Socket).Assembly.GetName());

        private bool HasOwnedHttpConnectResult(AuthoredMember member, ExpressionSyntax expression, HashSet<GraphMemberIdentity> path, int depth = 0, HttpFactoryProofWork? work = null)
        {
            work ??= httpConnectManualWork ?? new(maximumAnalyzedStatesPerRoot);

            if (depth >= traversalMaximumDepth || !work.TryTake()) return false;

            if (!TryNormalizeBuiltInCleanupValue(member.Model, expression, out expression, requireIdentityPreservingCasts: true)) return false;

            if (member.Model.GetOperation(expression) is IFieldReferenceOperation { Field: { IsStatic: true, IsReadOnly: true, Name: "Null" } field }
                && IsExactFrameworkType(field.ContainingType, "System.IO.Stream", typeof(Stream).Assembly.GetName())) return true;

            if (member.Model.GetOperation(expression) is IInvocationOperation call)
            {
                if (call.TargetMethod is { IsStatic: true, Name: "FromResult", Arity: 1 }
                    && IsExactFrameworkType(call.TargetMethod.ContainingType, "System.Threading.Tasks.ValueTask", typeof(ValueTask).Assembly.GetName())
                    && call.Arguments is [{ Value.Syntax: ExpressionSyntax value }]) return HasOwnedHttpConnectResult(member, value, path, depth + 1, work);

                if (ResolveCleanupInvocationTarget(call.TargetMethod, member, (InvocationExpressionSyntax)expression, new CleanupProvenanceContext()) is not { } target
                    || !path.Add(MemberIdentity(target))) return false;

                try
                {
                    AuthoredMember bound = BindCleanupInvocationContext(member, (InvocationExpressionSyntax)expression, target, new CleanupProvenanceContext());

                    ExpressionSyntax[] returned = ReturnedExpressions(bound.Syntax);

                    return returned.Length != 0 && returned.All(value => HasOwnedHttpConnectResult(bound, value, path, depth + 1, work));
                }
                finally
                {
                    path.Remove(MemberIdentity(target));
                }
            }

            if (member.Model.GetOperation(expression) is not IObjectCreationOperation { Constructor: { } constructor, Initializer: null } stream
                || !IsExactFrameworkType(constructor.ContainingType, "System.Net.Sockets.NetworkStream", typeof(System.Net.Sockets.NetworkStream).Assembly.GetName())
                || !HasExactParameterTypes(constructor, "System.Net.Sockets.Socket", "System.Boolean")
                || stream.Arguments is not [{ Value.Syntax: ExpressionSyntax socket }, { Value.ConstantValue: { HasValue: true, Value: true } }]) return false;

            return member.Model.GetSymbolInfo(socket).Symbol is ILocalSymbol local
                && HasOwnedHttpSocketTransfer(member, local, expression);
        }


        private bool HasOnlyObservedHttpConnectAwaitables(
            AuthoredMember member, HashSet<(GraphMemberIdentity Member, bool OwnsReturn)> visited, bool ownsReturn = true, HttpFactoryProofWork? work = null)
        {
            work ??= httpConnectManualWork ?? new(maximumAnalyzedStatesPerRoot);

            var identity = (MemberIdentity(member), ownsReturn);

            if (visited.Count >= traversalMaximumDepth || !work.TryTake() || !visited.Add(identity)) return false;

            try
            {
                if (member.Symbol.IsAsync && member.Symbol.ReturnsVoid) return false;

                bool returnedAwaitableOwned = ownsReturn && IsAwaitable(member.Symbol.ReturnType);

                foreach (InvocationExpressionSyntax call in member.Syntax.DescendantNodesAndSelf(node => node == member.Syntax
                    || node is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax).OfType<InvocationExpressionSyntax>())
                {
                    if (member.Model.GetSymbolInfo(call).Symbol is not IMethodSymbol method) return false;

                    ExpressionSyntax? receiver = method.MethodKind == MethodKind.DelegateInvoke ? DelegateReceiver(member, call) : null;

                    if (receiver is not null && member.Model.GetSymbolInfo(receiver).Symbol is { } callable
                        && IsDormantHttpConnectTestCallable(callable)) continue;

                    bool awaitable = IsAwaitable(method.ReturnType);

                    bool joined = CompletionPoint(member, call, returnedAwaitableOwned) is not null;

                    if (method.IsAsync && method.ReturnsVoid || awaitable && !joined) return false;

                    if (receiver is not null)
                    {
                        AuthoredMember[] callbacks = ResolveCallableTargets(receiver, member.Model, member.CallableBindings, member.AbsentCallables);

                        if (callbacks.Length == 0) return false;

                        foreach (AuthoredMember callback in callbacks)
                        {
                            AuthoredMember bound = BindStableCapturedCleanupContext(member, receiver,
                                BindResolvedCallableInvocationContext(member, call, callback));

                            if (!HasOnlyObservedHttpConnectAwaitables(bound, visited, ownsReturn: awaitable && joined, work)) return false;
                        }

                        continue;
                    }

                    AuthoredMember? exact = ResolveInvocationTarget(method, member, call);

                    AuthoredMember[] targets = (exact is null ? [] : new[] { exact })
                        .Concat(method.IsVirtual || method.IsAbstract || method.ContainingType.TypeKind == TypeKind.Interface
                            ? ResolveReceiverTargets(method, member.Model.Compilation) : [])
                        .DistinctBy(MemberIdentity).ToArray();

                    foreach (AuthoredMember target in targets)
                    {
                        AuthoredMember bound = BindCleanupInvocationContext(member, call, target, new CleanupProvenanceContext());

                        if (!HasOnlyObservedHttpConnectAwaitables(bound, visited, ownsReturn: awaitable && joined, work)) return false;
                    }
                }

                return true;
            }
            finally
            {
                visited.Remove(identity);
            }
        }

        private bool TrySelectDormantHttpConnectConditional(AuthoredMember member, ConditionalExpressionSyntax conditional, out ExpressionSyntax selected)
        {
            selected = conditional.WhenTrue;

            return httpConnectCertificate
                && member.Model.GetOperation(conditional.Condition) is IIsPatternOperation
                {
                    Value: IFieldReferenceOperation { Field: { } field },
                    Pattern: IConstantPatternOperation { Value.ConstantValue: { HasValue: true, Value: null } },
                }
                && IsDormantHttpConnectTestCallable(field);
        }

        private bool IsDormantHttpConnectTestCallable(ISymbol symbol)
        {
            if (!httpConnectCertificate || symbol is not IFieldSymbol
                { IsStatic: true, DeclaredAccessibility: Accessibility.Private, Type.TypeKind: TypeKind.Delegate } field
                || field.DeclaringSyntaxReferences is not [SyntaxReference declaration]
                || declaration.GetSyntax() is not VariableDeclaratorSyntax variable
                || variable.Initializer is not null && AnalysedModel(variable)?.GetConstantValue(variable.Initializer.Value) is not { HasValue: true, Value: null }) return false;

            if (semanticModels.Any(pair => pair.Key.GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Any(assignment => DeconstructsInto(assignment, pair.Value, field)))) return false;

            List<IMethodSymbol> mutators = [];

            foreach (var pair in semanticModels)
            {
                foreach (ExpressionSyntax use in pair.Key.GetRoot().DescendantNodes().OfType<ExpressionSyntax>()
                    .Where(expression => SymbolEqualityComparer.Default.Equals(pair.Value.GetSymbolInfo(expression).Symbol, field)))
                {
                    if (use.Parent is ArgumentSyntax { RefKindKeyword.RawKind: not 0 } or RefExpressionSyntax) return false;

                    if (use.Parent is not AssignmentExpressionSyntax { Left: var left } assignment || left != use) continue;

                    if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                        || pair.Value.GetEnclosingSymbol(assignment.SpanStart) is not IMethodSymbol
                        { MethodKind: MethodKind.Ordinary, IsStatic: true, DeclaredAccessibility: Accessibility.Internal or Accessibility.Private } mutator
                        || Resolve(mutator, pair.Value.Compilation) is not { } source
                        || source.Syntax is not MethodDeclarationSyntax { Body.Statements: [ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax only }] }
                        || only != assignment
                        || pair.Value.GetConstantValue(assignment.Right) is not { HasValue: true, Value: null }
                            && pair.Value.GetSymbolInfo(assignment.Right).Symbol is not IParameterSymbol { RefKind: RefKind.None }) return false;

                    mutators.Add(mutator);
                }
            }

            // Every setter reference counts, including method-group escapes and calls
            // in initializers. Tests are outside the authored production compilation;
            // reflection and loader mutation remain the inventory's explicit limits.
            return semanticModels.All(pair => !pair.Key.GetRoot().DescendantNodes().OfType<ExpressionSyntax>()
                .Any(expression => new[] { pair.Value.GetSymbolInfo(expression).Symbol }
                    .Concat(pair.Value.GetSymbolInfo(expression).CandidateSymbols).OfType<IMethodSymbol>()
                    .Any(method => mutators.Any(mutator => SymbolEqualityComparer.Default.Equals(method.OriginalDefinition, mutator.OriginalDefinition)))));
        }

        private bool IsReviewedHttpConnectStorageMethod(IMethodSymbol method) =>
            httpConnectCertificate
            && (method.MethodKind == MethodKind.Constructor
                    && IsExactFrameworkType(method.ContainingType, "System.Net.IPEndPoint", typeof(System.Net.IPEndPoint).Assembly.GetName())
                    && HasExactParameterTypes(method, "System.Net.IPAddress", "System.Int32")
                || method.MethodKind == MethodKind.Constructor
                    && IsExactFrameworkType(method.ContainingType, "System.Net.Http.HttpRequestException", typeof(HttpRequestException).Assembly.GetName())
                    && (HasExactParameterTypes(method, "System.String") || HasExactParameterTypes(method, "System.String", "System.Exception"))
                || method is { IsStatic: true, Name: "FromResult", Arity: 1, Parameters.Length: 1 }
                    && IsExactFrameworkType(method.ContainingType, "System.Threading.Tasks.ValueTask", typeof(ValueTask).Assembly.GetName())
                || method is { IsStatic: true, MethodKind: MethodKind.PropertyGet, Name: "get_Null", Parameters.Length: 0 }
                    && IsExactFrameworkType(method.ContainingType, "System.IO.Stream", typeof(Stream).Assembly.GetName()));



        private static bool IsExactProgressContract(ITypeSymbol? type) =>
            IsExactFrameworkType(type, "System.IProgress`1", typeof(IProgress<>).Assembly.GetName());

        private bool IsReviewedAbsentProgressReport(IMethodSymbol method, SyntaxNode? node, AuthoredMember? context)
        {
            if (method is not { IsStatic: false, Name: "Report", Arity: 0, ReturnsVoid: true,
                    Parameters: [{ RefKind: RefKind.None } parameter] }
                || !IsExactProgressContract(method.ContainingType)
                || method.ContainingType.TypeArguments is not [ITypeSymbol payload]
                || !SymbolEqualityComparer.Default.Equals(parameter.Type, payload)
                || node is not InvocationExpressionSyntax { Expression: MemberBindingExpressionSyntax } call
                || call.Parent is not ConditionalAccessExpressionSyntax conditional
                || conditional.WhenNotNull != call || context is null)
            {
                return false;
            }

            // IProgress.Report can enqueue detached callbacks (Progress<T> does), so a live
            // receiver is never an in-process or synchronous provider exemption. Only the
            // compiler's exact null-conditional form on a completely bound null cannot run.
            return HasExactAbsentProgressReceiver(context, conditional.Expression,
                new HashSet<ISymbol>(SymbolEqualityComparer.Default));
        }

        private bool HasExactAbsentProgressReceiver(AuthoredMember member, ExpressionSyntax expression, HashSet<ISymbol> path, int depth = 0)
        {
            if (depth >= traversalMaximumDepth) return false;

            IOperation? operation = member.Model.GetOperation(expression);

            while (operation is IConversionOperation conversion)
            {
                if (conversion.OperatorMethod is not null) return false;

                operation = conversion.Operand;
            }

            expression = UnwrapCampaignLeaseNullSyntax(expression);

            if (expression is CastExpressionSyntax cast)
            {
                return member.Model.GetOperation(cast) is IConversionOperation { OperatorMethod: null, Type: { } castType }
                    && IsExactProgressContract(castType)
                    && HasExactAbsentProgressReceiver(member, cast.Expression, path, depth + 1);
            }

            ITypeSymbol? type = member.Model.GetTypeInfo(expression).Type;

            if (type is not null && !IsExactProgressContract(type)) return false;

            if (member.Model.GetConstantValue(expression) is { HasValue: true, Value: null }
                || type is not null && (expression is DefaultExpressionSyntax || expression.IsKind(SyntaxKind.DefaultLiteralExpression))) return true;

            ISymbol? symbol = member.Model.GetSymbolInfo(expression).Symbol;

            if (symbol is not IParameterSymbol { RefKind: RefKind.None } and not ILocalSymbol
                || HasCollectionValueReplacement(member, symbol)
                || !path.Add(symbol)) return false;

            try
            {
                if (TryResolveStableBoundValueSource(member, expression, out BoundValueSource? source) && source is not null)
                    return HasExactAbsentProgressReceiver(source.Caller, source.Expression, path, depth + 1);

                return symbol is ILocalSymbol local
                    && local.DeclaringSyntaxReferences.SingleOrDefault()?.GetSyntax() is VariableDeclaratorSyntax { Initializer.Value: { } initializer }
                    && HasExactAbsentProgressReceiver(member, initializer, path, depth + 1);
            }
            finally
            {
                path.Remove(symbol);
            }
        }
    }
}
