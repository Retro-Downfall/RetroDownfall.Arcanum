using System.Collections.Immutable;

using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// The semantic scans behind the checker's limit pins. The hosted-producer inventory states, in docs/Arcanum.DESIGN.md, what it does not
/// follow: authored code that the framework or the runtime runs at a point no operation of the census names. A limit is a claim about the
/// shipped source only while something proves the shipped source holds no instance of the shape the checker cannot see, so each scan here
/// finds one such shape by symbol (a type, a call, a conversion, a subscription), never by text, and returns the findings with the member
/// that holds each. A pin compares the findings in the production source with an exact reviewed list, and runs the same scan on a synthetic
/// source that holds every shape, so that a scan that stopped matching cannot pass for ever.
/// </summary>
internal static class CheckerLimitScans
{
    /// <summary>The trees the analysis reads: under src/ and not generated. Generated code is a limit of its own.</summary>
    internal static bool IsAnalysedTree(SyntaxTree tree) =>
        tree.FilePath.StartsWith("src/", StringComparison.Ordinal) && !tree.FilePath.Contains("/obj/", StringComparison.Ordinal);

    /// <summary>The metadata name of a type, containing types and namespace included.</summary>
    internal static string Key(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named)
        {
            return type.ToDisplayString();
        }

        string container = named.ContainingType is { } parent
            ? Key(parent) + "."
            : named.ContainingNamespace.IsGlobalNamespace ? "" : named.ContainingNamespace.ToDisplayString() + ".";

        return container + named.MetadataName;
    }

    internal static bool DerivesFrom(ITypeSymbol type, Func<INamedTypeSymbol, bool> match)
    {
        for (INamedTypeSymbol? current = type as INamedTypeSymbol; current is not null; current = current.BaseType)
        {
            if (match(current.OriginalDefinition))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool DerivesFrom(ITypeSymbol type, string key) => DerivesFrom(type, candidate => Key(candidate) == key);

    internal static bool Implements(ITypeSymbol type, Func<INamedTypeSymbol, bool> match) => type.AllInterfaces.Any(candidate => match(candidate.OriginalDefinition));

    /// <summary>The types the source declares, nested ones included.</summary>
    internal static IEnumerable<INamedTypeSymbol> SourceTypes(IReadOnlyList<Compilation> compilations) =>
        HostedGrimoireProducerInventory.DeclaredTypesOf(compilations).Where(static type => type.Locations.Any(static location => location.IsInSource && IsAnalysedTree(location.SourceTree!)));

    /// <summary>Every syntax node of the analysed trees with the model that binds it.</summary>
    internal static IEnumerable<(SemanticModel Model, SyntaxNode Node)> Nodes(IReadOnlyList<Compilation> compilations)
    {
        foreach (Compilation compilation in compilations)
        {
            foreach (SyntaxTree tree in compilation.SyntaxTrees.Where(IsAnalysedTree))
            {
                SemanticModel model = SharedSemanticModels.Of(compilation, tree);

                foreach (SyntaxNode node in tree.GetRoot().DescendantNodes())
                {
                    yield return (model, node);
                }
            }
        }
    }

    // The member that holds a node: a finding names the member and not a line, so a pin does not move when code above it does.
    internal static string Where(SemanticModel model, SyntaxNode node) =>
        AuthorityCarrierScanner.Qualified(ContainerRegistrationScanner.EnclosingMember(model.GetEnclosingSymbol(node.SpanStart) ?? model.Compilation.Assembly));

    private static string[] Ordered(IEnumerable<string> findings) => [.. findings.Order(StringComparer.Ordinal)];

    /// <summary>The members that construct a type with <c>new</c>, once for each construction.</summary>
    internal static string[] ConstructionSites(IReadOnlyList<Compilation> compilations, string typeKey) =>
        Ordered(Nodes(compilations)
            .Where(item => item.Node is BaseObjectCreationExpressionSyntax
                && item.Model.GetSymbolInfo(item.Node).Symbol is IMethodSymbol { ContainingType: { } created }
                && Key(created.OriginalDefinition) == typeKey)
            .Select(item => Where(item.Model, item.Node)));

    // ------------------------------------------------------------------------------------------------------------------------------
    // EF value converters, value generators, comparers, model customizers and convention plug-ins. They run authored code at points
    // no operation of the EF lane names (when a model is built, when a value is read or written, when a key is generated).
    // ------------------------------------------------------------------------------------------------------------------------------

    private static readonly Regex EfExtensionPoint = new(
        @"^(ValueConverter(`\d)?|ValueGenerator(`\d)?|ValueComparer(`\d)?|IValueConverterSelector|ValueConverterSelector|IModelCustomizer|ModelCustomizer|IConvention|I\w+Convention|\w+Convention|IConventionSetPlugin|IProviderConventionSetBuilder|ProviderConventionSetBuilder|IDbContextOptionsExtension|IModelRuntimeInitializer)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static bool IsEfExtensionPoint(INamedTypeSymbol type) =>
        type.ContainingNamespace.ToDisplayString().StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
        && EfExtensionPoint.IsMatch(type.MetadataName)
        && !type.Name.EndsWith("Interceptor", StringComparison.Ordinal);

    private static readonly HashSet<string> EfCustomizingCalls = new(StringComparer.Ordinal)
    {
        "HasConversion", "HaveConversion", "HasValueGenerator", "HaveValueGenerator", "HasValueComparer", "HaveValueComparer", "UseValueConverterForType",
        "HasTranslation", "ReplaceService", "ConfigureConventions",
    };

    /// <summary>
    /// A type that derives from or implements an EF extension point, a call that supplies authored conversion, generation, comparison or
    /// translation code to a model (a conversion by a lambda or a converter type rather than by the built-in conversion of a CLR type), a call
    /// that replaces an EF service, and an override of <c>ConfigureConventions</c>.
    /// </summary>
    internal static string[] EfModelCustomization(IReadOnlyList<Compilation> compilations)
    {
        List<string> findings = [];

        foreach (INamedTypeSymbol type in SourceTypes(compilations))
        {
            for (INamedTypeSymbol? current = type.BaseType; current is not null; current = current.BaseType)
            {
                if (IsEfExtensionPoint(current.OriginalDefinition))
                {
                    findings.Add($"type {Key(type)} derives from {Key(current.OriginalDefinition)}");

                    break;
                }
            }

            foreach (INamedTypeSymbol contract in type.AllInterfaces.Where(static contract => IsEfExtensionPoint(contract.OriginalDefinition)))
            {
                findings.Add($"type {Key(type)} implements {Key(contract.OriginalDefinition)}");
            }

            foreach (IMethodSymbol method in type.GetMembers().OfType<IMethodSymbol>().Where(static method => method.IsOverride && method.Name == "ConfigureConventions"))
            {
                findings.Add($"override {Key(type)}.ConfigureConventions");
            }
        }

        foreach ((SemanticModel model, SyntaxNode node) in Nodes(compilations))
        {
            if (node is not InvocationExpressionSyntax call
                || model.GetSymbolInfo(call).Symbol is not IMethodSymbol method
                || !EfCustomizingCalls.Contains(method.Name)
                || !method.ContainingNamespace.ToDisplayString().StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal))
            {
                continue;
            }

            // The built-in conversion of a CLR type: HasConversion<int>() with no argument, where the type is not a converter.
            bool builtIn = method.Name is "HasConversion" or "HaveConversion"
                && call.ArgumentList.Arguments.Count == 0
                && method.TypeArguments is [{ } target]
                && !DerivesFrom(target, static candidate => EfExtensionPoint.IsMatch(candidate.MetadataName) && candidate.ContainingNamespace.ToDisplayString().StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));

            if (!builtIn)
            {
                findings.Add($"call {Key(method.ContainingType)}.{method.Name} in {Where(model, call)}");
            }
        }

        return Ordered(findings);
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // Contexts the container owns beyond a scope. A context that the container disposes when the host's root provider is disposed is not
    // disposed by any hosted root, and a disposable of unknown provenance is not assumed to be a context.
    // ------------------------------------------------------------------------------------------------------------------------------

    private const string ContextType = "Microsoft.EntityFrameworkCore.DbContext";

    /// <summary>The authored context types.</summary>
    internal static string[] AuthoredContexts(IReadOnlyList<Compilation> compilations) =>
        Ordered(SourceTypes(compilations).Where(static type => DerivesFrom(type, ContextType)).Select(Key));

    /// <summary>Every registration that names an authored context, with its lifetime: a scoped context ends with its scope, a pool and a singleton end with the root provider.</summary>
    internal static string[] ContextRegistrations(IReadOnlyList<Compilation> compilations) =>
        Ordered(ContainerRegistrationScanner.Find(compilations, static type => DerivesFrom(type, ContextType) && type.Locations.Any(static location => location.IsInSource), IsAnalysedTree)
            .Select(static registration => $"{registration.Lifetime} {Key(registration.Watched)} by {registration.Method.Name} in {registration.Enclosing}"));

    /// <summary>
    /// A conversion of an authored context to a type it could be disposed through that is not a context: <c>object</c>, <c>IDisposable</c>,
    /// <c>IAsyncDisposable</c> or any other type that is disposable. A conversion for a call to an extension method or an interface member that
    /// disposes nothing is not a finding.
    /// </summary>
    internal static string[] ContextConversions(IReadOnlyList<Compilation> compilations)
    {
        List<string> findings = [];

        foreach ((SemanticModel model, SyntaxNode node) in Nodes(compilations))
        {
            if (node is not ExpressionSyntax expression)
            {
                continue;
            }

            Microsoft.CodeAnalysis.TypeInfo info = model.GetTypeInfo(expression);

            if (info is { Type: { } from, ConvertedType: { } to }
                && !SymbolEqualityComparer.Default.Equals(from, to)
                && DerivesFrom(from, ContextType)
                && from.Locations.Any(static location => location.IsInSource)
                && !DerivesFrom(to, ContextType)
                && IsDisposalPath(to)
                && !IsReviewedPassThrough(model, expression))
            {
                findings.Add($"{Key(from)} to {Key(to)} in {Where(model, node)}");
            }
        }

        return Ordered(findings);
    }

    // The framework callees that only look at the value they are handed (a null guard, a reference comparison), as the Authority Carrier Ban reviews them.
    private static bool IsReviewedPassThrough(SemanticModel model, ExpressionSyntax expression) =>
        expression.Parent is ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax call }
        && model.GetSymbolInfo(call).Symbol is IMethodSymbol { ContainingType: { } owner } method
        && AuthorityCarrierScanner.ReviewedPassThroughs.Contains((owner.SpecialType == SpecialType.System_Object ? "object" : Key(owner)) + "." + method.Name);

    private static bool IsDisposalPath(ITypeSymbol type) =>
        type.SpecialType == SpecialType.System_Object
        || Key(type) is "System.IDisposable" or "System.IAsyncDisposable"
        || type.AllInterfaces.Any(static contract => Key(contract) is "System.IDisposable" or "System.IAsyncDisposable");

    // ------------------------------------------------------------------------------------------------------------------------------
    // The scheduling context an awaiter captures. A continuation runs on whatever SynchronizationContext, TaskScheduler or ExecutionContext
    // the awaiter captured; authored code in any of them, or an authored awaiter, runs at points no operation names.
    // ------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A type that derives from SynchronizationContext or TaskScheduler, an authored awaiter (a type with an instance <c>GetAwaiter</c>, or a
    /// <c>GetAwaiter</c> extension), an <c>AsyncLocal</c> built with a change handler, and a call that installs a synchronization context or
    /// subscribes to unobserved task exceptions.
    /// </summary>
    internal static string[] SchedulingContext(IReadOnlyList<Compilation> compilations)
    {
        List<string> findings = [];

        foreach (INamedTypeSymbol type in SourceTypes(compilations))
        {
            foreach (string contextual in new[] { "System.Threading.SynchronizationContext", "System.Threading.Tasks.TaskScheduler" })
            {
                if (DerivesFrom(type, contextual))
                {
                    findings.Add($"type {Key(type)} derives from {contextual}");
                }
            }

            foreach (IMethodSymbol awaiter in type.GetMembers("GetAwaiter").OfType<IMethodSymbol>().Where(static method => method.Parameters.Length == (method.IsExtensionMethod ? 1 : 0)))
            {
                findings.Add($"awaiter {Key(type)}.GetAwaiter");
            }
        }

        foreach ((SemanticModel model, SyntaxNode node) in Nodes(compilations))
        {
            switch (node)
            {
                case BaseObjectCreationExpressionSyntax creation
                    when model.GetSymbolInfo(creation).Symbol is IMethodSymbol { ContainingType: { } created } constructor
                        && Key(created.OriginalDefinition) == "System.Threading.AsyncLocal`1"
                        && constructor.Parameters.Length > 0:
                    findings.Add($"AsyncLocal with a change handler in {Where(model, node)}");

                    break;

                case InvocationExpressionSyntax call
                    when model.GetSymbolInfo(call).Symbol is IMethodSymbol { ContainingType: { } owner } method
                        && Key(owner) == "System.Threading.SynchronizationContext"
                        && method.Name == "SetSynchronizationContext":
                    findings.Add($"SetSynchronizationContext in {Where(model, node)}");

                    break;

                case AssignmentExpressionSyntax assignment
                    when assignment.IsKind(SyntaxKind.AddAssignmentExpression)
                        && model.GetSymbolInfo(assignment.Left).Symbol is IEventSymbol { ContainingType: { } eventOwner } subscribed
                        && Key(eventOwner) == "System.Threading.Tasks.TaskScheduler":
                    findings.Add($"{subscribed.Name} subscription in {Where(model, node)}");

                    break;
            }
        }

        return Ordered(findings);
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // The Authority Carrier Ban reads the source. A lease or a group that a compiler-generated state machine hoists into a field of the
    // machine is not in the source, and nothing in the source says what a reflective access does.
    // ------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A member that can hoist an authority handle into a state machine the awaiter does not own for the whole of the handle's life: an
    /// iterator (the machine is the sequence, enumerated at the consumer's pace), an <c>async void</c> method (nothing awaits it) and an
    /// asynchronous method whose return type is not a task, each declaring a parameter or a local that holds an authority.
    /// </summary>
    internal static string[] CarrierStateMachineHoisting(IReadOnlyList<Compilation> compilations)
    {
        List<string> findings = [];

        foreach ((SemanticModel model, SyntaxNode node) in Nodes(compilations))
        {
            if (node is not (MethodDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax or AccessorDeclarationSyntax or ArrowExpressionClauseSyntax))
            {
                continue;
            }

            IMethodSymbol? method = node switch
            {
                AnonymousFunctionExpressionSyntax lambda => model.GetSymbolInfo(lambda).Symbol as IMethodSymbol,
                ArrowExpressionClauseSyntax => null,
                _ => model.GetDeclaredSymbol(node) as IMethodSymbol,
            };

            if (method is null)
            {
                continue;
            }

            bool iterator = node.DescendantNodes(candidate => candidate == node || candidate is not (LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax))
                .OfType<YieldStatementSyntax>().Any();

            bool asyncVoid = method.IsAsync && method.ReturnsVoid;

            bool asyncOtherwise = method.IsAsync && !method.ReturnsVoid && !IsTaskLike(method.ReturnType) && !iterator;

            if (!iterator && !asyncVoid && !asyncOtherwise)
            {
                continue;
            }

            string kind = iterator ? "iterator" : asyncVoid ? "async void" : "async " + Key(method.ReturnType);

            foreach (ITypeSymbol held in DeclaredHandleTypes(model, node, method))
            {
                findings.Add($"{kind} {AuthorityCarrierScanner.Qualified(method)} declares {Key(held)}");
            }
        }

        return Ordered(findings);
    }

    private static bool IsTaskLike(ITypeSymbol type) => type is INamedTypeSymbol named
        && Key(named.OriginalDefinition) is "System.Threading.Tasks.Task" or "System.Threading.Tasks.Task`1" or "System.Threading.Tasks.ValueTask" or "System.Threading.Tasks.ValueTask`1";

    // The parameters of a member and the locals its body declares, each of a type that holds an authority handle.
    private static IEnumerable<ITypeSymbol> DeclaredHandleTypes(SemanticModel model, SyntaxNode body, IMethodSymbol method)
    {
        foreach (IParameterSymbol parameter in method.Parameters.Where(static parameter => AuthorityCarrierScanner.Holds(parameter.Type) is not null))
        {
            yield return parameter.Type;
        }

        HashSet<ILocalSymbol> locals = new(SymbolEqualityComparer.Default);

        foreach (SyntaxNode node in body.DescendantNodes(candidate => candidate == body || candidate is not (LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax)))
        {
            if (model.GetOperation(node) is IVariableDeclaratorOperation declarator)
            {
                locals.Add(declarator.Symbol);
            }
            else if (model.GetOperation(node) is ILocalReferenceOperation reference)
            {
                locals.Add(reference.Local);
            }
        }

        foreach (ILocalSymbol local in locals.Where(static local => AuthorityCarrierScanner.Holds(local.Type) is not null))
        {
            yield return local.Type;
        }
    }

    private static readonly HashSet<string> ReflectiveInvocations = new(StringComparer.Ordinal)
    {
        "System.Reflection.FieldInfo.GetValue", "System.Reflection.FieldInfo.SetValue", "System.Reflection.PropertyInfo.GetValue", "System.Reflection.PropertyInfo.SetValue",
        "System.Reflection.MethodBase.Invoke", "System.Reflection.MethodInfo.Invoke", "System.Reflection.ConstructorInfo.Invoke", "System.Type.InvokeMember",
        "System.Activator.CreateInstance", "System.Delegate.DynamicInvoke", "System.Delegate.CreateDelegate", "System.Reflection.Assembly.CreateInstance",
        "System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject", "System.Runtime.Serialization.FormatterServices.GetUninitializedObject",
        "System.Linq.Expressions.LambdaExpression.Compile", "System.Linq.Expressions.Expression`1.Compile",
        "Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance", "Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateFactory",
        "System.Reflection.Assembly.Load", "System.Reflection.Assembly.LoadFrom", "System.Reflection.Assembly.LoadFile", "System.Runtime.Loader.AssemblyLoadContext.LoadFromAssemblyPath",
        "System.Runtime.Loader.AssemblyLoadContext.LoadFromStream", "System.Runtime.InteropServices.NativeLibrary.Load",
        "System.Reflection.MethodInfo.CreateDelegate", "System.Reflection.EventInfo.AddEventHandler", "System.Reflection.Emit.DynamicMethod.CreateDelegate",
    };

    // An extern method that the runtime binds to a member of another type, whatever that member's accessibility (UnsafeAccessorAttribute).
    private static bool IsUnsafeAccessor(IMethodSymbol method) => method.IsExtern
        && method.GetAttributes().Any(static attribute => attribute.AttributeClass is { } owner && Key(owner) == "System.Runtime.CompilerServices.UnsafeAccessorAttribute");

    /// <summary>
    /// A call that reads, writes or invokes a member by reflection, builds a delegate from a method object or adds a handler through an event
    /// object, loads an assembly or a native library, builds an object from an argument array, or calls an unsafe accessor (an extern method the
    /// runtime binds to a field or member of another type): each is reported with the member that holds it, and with <c>carries</c> when an
    /// argument it is handed, or the type it reads or returns, holds an authority handle, which a reflective store could keep.
    /// </summary>
    internal static string[] CarrierReflection(IReadOnlyList<Compilation> compilations)
    {
        List<string> findings = [];

        // The accessors of one class that one member calls are one finding: a compiled model calls each accessor of an entity type many times.
        Dictionary<string, bool> accessors = new(StringComparer.Ordinal);

        foreach ((SemanticModel model, SyntaxNode node) in Nodes(compilations))
        {
            if (node is not InvocationExpressionSyntax call || model.GetSymbolInfo(call).Symbol is not IMethodSymbol { ContainingType: { } owner } method)
            {
                continue;
            }

            bool unsafeAccessor = IsUnsafeAccessor(method);

            if (!unsafeAccessor && !ReflectiveInvocations.Contains(Key(owner.OriginalDefinition) + "." + method.Name))
            {
                continue;
            }

            bool carries = model.GetOperation(call) is IInvocationOperation invocation
                && (invocation.Arguments.Any(argument => HoldsBeforeConversion(argument.Value))
                    || unsafeAccessor && (AuthorityCarrierScanner.Holds(method.ReturnType) is not null || method.Parameters.Any(static parameter => AuthorityCarrierScanner.Holds(parameter.Type) is not null)));

            if (unsafeAccessor)
            {
                string head = $"UnsafeAccessor {Key(owner.OriginalDefinition)} in {Where(model, node)}";

                accessors[head] = accessors.GetValueOrDefault(head) || carries;
            }
            else
            {
                findings.Add($"{Key(owner.OriginalDefinition)}.{method.Name} in {Where(model, node)}{(carries ? " carries an authority handle" : "")}");
            }
        }

        findings.AddRange(accessors.Select(static pair => pair.Key + (pair.Value ? " carries an authority handle" : "")));

        return Ordered(findings);
    }

    // The type of a value before it was converted to object or to the element of an argument array.
    private static bool HoldsBeforeConversion(IOperation operation)
    {
        switch (operation)
        {
            case IConversionOperation conversion:
                return HoldsBeforeConversion(conversion.Operand);

            case IArrayCreationOperation { Initializer: { } initializer }:
                return initializer.ElementValues.Any(HoldsBeforeConversion);

            default:
                return operation.Type is { } type && AuthorityCarrierScanner.Holds(type) is not null;
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // Serializers and loggers. A serializer calls converters, callbacks and getters of the value it is handed; a logger formats it, and a
    // structured logger that destructures it reads its properties.
    // ------------------------------------------------------------------------------------------------------------------------------

    // The parameters of a logging method that carry the message template: Serilog's messageTemplate, the message of a Microsoft.Extensions.Logging
    // call and the format string LoggerMessage.Define is handed.
    private static readonly HashSet<string> TemplateParameters = new(StringComparer.Ordinal) { "messageTemplate", "message", "formatString" };

    private static bool IsLoggingMethod(IMethodSymbol method)
    {
        string space = method.ContainingNamespace.ToDisplayString();

        return space == "Serilog" || space.StartsWith("Serilog.", StringComparison.Ordinal)
            || space == "Microsoft.Extensions.Logging" || space.StartsWith("Microsoft.Extensions.Logging.", StringComparison.Ordinal);
    }

    /// <summary>
    /// The values a string argument can have where it is read, when every one of them is a constant the source writes: a constant (a literal, a
    /// <c>const</c>, a concatenation of constants, whatever the compiler folds), either branch of a conditional, and a parameter of a private
    /// method, whose values are those of the argument every call of that method passes (followed through the callers' own parameters). A value
    /// that is read from anywhere else, and a parameter of a method that something other than a direct call can reach (a method group, an override,
    /// an implementation of an interface member, a lambda or a local function) cannot be listed, so the answer is then <c>null</c>.
    /// </summary>
    internal sealed class StringValues(IReadOnlyList<Compilation> compilations)
    {
        // What reaches a private method is written inside its containing type (the declarations of a partial type, nested types included), so
        // that is all the scan of one method reads.
        private readonly Dictionary<IMethodSymbol, (List<ImmutableArray<IArgumentOperation>> Calls, bool ReachedOtherwise)> scanned = new(SymbolEqualityComparer.Default);

        private (List<ImmutableArray<IArgumentOperation>> Calls, bool ReachedOtherwise) Scan(IMethodSymbol owner)
        {
            if (scanned.TryGetValue(owner, out (List<ImmutableArray<IArgumentOperation>>, bool) known))
            {
                return known;
            }

            List<ImmutableArray<IArgumentOperation>> calls = [];

            bool reachedOtherwise = false;

            foreach (SyntaxReference declaration in owner.ContainingType.DeclaringSyntaxReferences)
            {
                if (compilations.FirstOrDefault(candidate => candidate.ContainsSyntaxTree(declaration.SyntaxTree)) is not { } compilation)
                {
                    reachedOtherwise = true;

                    continue;
                }

                SemanticModel model = SharedSemanticModels.Of(compilation, declaration.SyntaxTree);

                foreach (SyntaxNode node in declaration.GetSyntax().DescendantNodes())
                {
                    switch (node)
                    {
                        case InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax:
                            (IMethodSymbol? target, ImmutableArray<IArgumentOperation> arguments) = model.GetOperation(node) switch
                            {
                                IInvocationOperation invocation => (invocation.TargetMethod, invocation.Arguments),
                                IObjectCreationOperation { Constructor: { } constructor } creation => (constructor, creation.Arguments),
                                _ => (null, default),
                            };

                            if (target is not null && SymbolEqualityComparer.Default.Equals(target.OriginalDefinition, owner))
                            {
                                calls.Add(arguments);
                            }

                            break;

                        case IdentifierNameSyntax or GenericNameSyntax or MemberAccessExpressionSyntax or MemberBindingExpressionSyntax
                            when model.GetOperation(node) is IMethodReferenceOperation reference && SymbolEqualityComparer.Default.Equals(reference.Method.OriginalDefinition, owner):
                            reachedOtherwise = true;

                            break;
                    }
                }
            }

            return scanned[owner] = (calls, reachedOtherwise);
        }

        /// <summary>Every value the operation can have, or <c>null</c> when some value is not a constant the source writes.</summary>
        internal IReadOnlyList<string>? Of(IOperation value) => Resolve(value, new HashSet<IParameterSymbol>(SymbolEqualityComparer.Default));

        private List<string>? Resolve(IOperation value, HashSet<IParameterSymbol> visiting)
        {
            if (value.ConstantValue is { HasValue: true } constant)
            {
                return constant.Value is string text ? [text] : [];
            }

            switch (value)
            {
                case IConversionOperation conversion:
                    return Resolve(conversion.Operand, visiting);

                case IParenthesizedOperation parenthesized:
                    return Resolve(parenthesized.Operand, visiting);

                case IConditionalOperation { WhenFalse: { } whenFalse } conditional:
                    return Join(Resolve(conditional.WhenTrue, visiting), Resolve(whenFalse, visiting));

                case IParameterReferenceOperation { Parameter: { ContainingSymbol: IMethodSymbol owner } parameter }:
                    return visiting.Add(parameter) ? FromCallers(owner.OriginalDefinition, parameter, visiting) : [];

                default:
                    return null;
            }
        }

        private List<string>? FromCallers(IMethodSymbol owner, IParameterSymbol parameter, HashSet<IParameterSymbol> visiting)
        {
            // Only a private method that nothing but a direct call reaches has the values its callers pass.
            if (owner.DeclaredAccessibility != Accessibility.Private
                || owner.MethodKind is not (MethodKind.Ordinary or MethodKind.Constructor)
                || owner.IsVirtual || owner.IsOverride || owner.IsAbstract
                || !owner.ExplicitInterfaceImplementations.IsEmpty
                || !owner.Locations.Any(static location => location.IsInSource)
                || Scan(owner) is not (var calls, false))
            {
                return null;
            }

            List<string> values = [];

            foreach (ImmutableArray<IArgumentOperation> arguments in calls)
            {
                IArgumentOperation? passed = arguments.FirstOrDefault(argument => SymbolEqualityComparer.Default.Equals(argument.Parameter?.OriginalDefinition, parameter.OriginalDefinition));

                if (passed is null || Join(values, Resolve(passed.Value, visiting)) is not { } joined)
                {
                    return null;
                }

                values = joined;
            }

            return values;
        }

        private static List<string>? Join(List<string>? first, List<string>? second) => first is null || second is null ? null : [.. first.Concat(second).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The message template of every call to a Serilog or Microsoft.Extensions.Logging method: each value the argument bound to the method's
    /// template parameter can have (see <see cref="StringValues"/>), and the templates a <c>LoggerMessage</c> attribute declares. A template whose
    /// values cannot be listed is returned with <c>null</c> as its text, because nothing proves it holds no destructuring operator.
    /// </summary>
    internal static IEnumerable<(string Where, string? Template)> LogTemplates(IReadOnlyList<Compilation> compilations)
    {
        Lazy<StringValues> values = new(() => new StringValues(compilations));

        foreach ((SemanticModel model, SyntaxNode node) in Nodes(compilations))
        {
            if (node is InvocationExpressionSyntax call
                && model.GetOperation(call) is IInvocationOperation { TargetMethod: { } method } invocation
                && IsLoggingMethod(method))
            {
                foreach (IArgumentOperation argument in invocation.Arguments.Where(static argument => argument.Parameter is { Type.SpecialType: SpecialType.System_String } parameter && TemplateParameters.Contains(parameter.Name)))
                {
                    IReadOnlyList<string>? templates = argument.Value.ConstantValue.HasValue ? [argument.Value.ConstantValue.Value as string ?? ""] : values.Value.Of(argument.Value);

                    if (templates is null)
                    {
                        yield return (Where(model, node), null);
                    }
                    else
                    {
                        foreach (string template in templates)
                        {
                            yield return (Where(model, node), template);
                        }
                    }
                }
            }
        }

        foreach (INamedTypeSymbol type in SourceTypes(compilations))
        {
            foreach (IMethodSymbol method in type.GetMembers().OfType<IMethodSymbol>())
            {
                foreach (AttributeData attribute in method.GetAttributes().Where(static attribute => attribute.AttributeClass is { } owner && Key(owner) == "Microsoft.Extensions.Logging.LoggerMessageAttribute"))
                {
                    foreach (string template in attribute.ConstructorArguments.Concat(attribute.NamedArguments.Select(static named => named.Value)).Select(static value => value.Value).OfType<string>())
                    {
                        yield return (AuthorityCarrierScanner.Qualified(method), template);
                    }
                }
            }
        }
    }

    /// <summary>
    /// A type that implements a System.Text.Json callback contract (<c>IJsonOn*</c>), a destructuring configuration, and a log message template
    /// that destructures its argument (<c>{@name}</c>), which makes a structured logger read the properties of the value. The template is the
    /// constant value of the argument a logging call binds to its template parameter, so a literal, a constant, a concatenation and a call
    /// through Microsoft.Extensions.Logging (whose provider here is Serilog) are one thing, and so is a <c>LoggerMessage</c> attribute's message.
    /// A template that is not a constant is a finding of its own, because nothing proves it holds no operator.
    /// </summary>
    internal static string[] SerializerAndLoggerCallbacks(IReadOnlyList<Compilation> compilations)
    {
        List<string> findings = [];

        foreach (INamedTypeSymbol type in SourceTypes(compilations))
        {
            foreach (INamedTypeSymbol contract in type.AllInterfaces.Where(static contract => Key(contract.OriginalDefinition) is "System.Text.Json.Serialization.IJsonOnSerializing" or "System.Text.Json.Serialization.IJsonOnSerialized" or "System.Text.Json.Serialization.IJsonOnDeserializing" or "System.Text.Json.Serialization.IJsonOnDeserialized"))
            {
                findings.Add($"type {Key(type)} implements {Key(contract.OriginalDefinition)}");
            }
        }

        foreach ((SemanticModel model, SyntaxNode node) in Nodes(compilations))
        {
            if (node is InvocationExpressionSyntax destructuring
                && model.GetSymbolInfo(destructuring).Symbol is IMethodSymbol { ContainingType: { } configuration }
                && Key(configuration) == "Serilog.Configuration.LoggerDestructuringConfiguration")
            {
                findings.Add($"destructuring configuration in {Where(model, node)}");
            }
        }

        foreach ((string where, string? template) in LogTemplates(compilations))
        {
            if (template is null)
            {
                findings.Add($"template without a constant value in {where}");
            }
            else if (template.Contains("{@", StringComparison.Ordinal))
            {
                findings.Add($"destructuring template in {where}");
            }
        }

        return Ordered(findings);
    }

    /// <summary>The authored converters, as the types that derive from <c>JsonConverter</c> (the pin compares them with a reviewed list).</summary>
    internal static string[] JsonConverters(IReadOnlyList<Compilation> compilations) =>
        Ordered(SourceTypes(compilations).Where(static type => DerivesFrom(type, "System.Text.Json.Serialization.JsonConverter")).Select(Key));

    // ------------------------------------------------------------------------------------------------------------------------------
    // Runtime observers and ambient switches: authored code the runtime runs when something happens, outside any root.
    // ------------------------------------------------------------------------------------------------------------------------------

    private static readonly HashSet<string> RuntimeObserverTypes = new(StringComparer.Ordinal)
    {
        "System.Diagnostics.Tracing.EventListener", "System.Diagnostics.Metrics.MeterListener", "System.Diagnostics.ActivityListener", "System.Diagnostics.DiagnosticListener",
    };

    /// <summary>
    /// A type that derives from a listener of the runtime's telemetry, a construction of one, a subscription to a runtime event that runs
    /// authored code when the process is signalled, unloaded or fails (reported with its handler's owner), a signal registration, and a
    /// change of an ambient framework switch.
    /// </summary>
    internal static string[] RuntimeObservers(IReadOnlyList<Compilation> compilations)
    {
        List<string> findings = [];

        foreach (INamedTypeSymbol type in SourceTypes(compilations))
        {
            if (DerivesFrom(type, candidate => RuntimeObserverTypes.Contains(Key(candidate))))
            {
                findings.Add($"type {Key(type)} is a runtime listener");
            }
        }

        foreach ((SemanticModel model, SyntaxNode node) in Nodes(compilations))
        {
            switch (node)
            {
                case BaseObjectCreationExpressionSyntax creation
                    when model.GetSymbolInfo(creation).Symbol is IMethodSymbol { ContainingType: { } created }
                        && RuntimeObserverTypes.Contains(Key(created.OriginalDefinition)):
                    findings.Add($"{Key(created.OriginalDefinition)} constructed in {Where(model, node)}");

                    break;

                case AssignmentExpressionSyntax assignment
                    when assignment.Kind() is SyntaxKind.AddAssignmentExpression
                        && model.GetSymbolInfo(assignment.Left).Symbol is IEventSymbol { ContainingType: { } eventOwner } subscribed
                        && IsRuntimeEventOwner(eventOwner)
                        && !subscribed.ContainingNamespace.ToDisplayString().StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal):
                    findings.Add($"{Key(eventOwner)}.{subscribed.Name} subscription in {Where(model, node)}");

                    break;

                case InvocationExpressionSyntax call
                    when model.GetSymbolInfo(call).Symbol is IMethodSymbol { ContainingType: { } owner } method
                        && (Key(owner) == "System.Runtime.InteropServices.PosixSignalRegistration" && method.Name == "Create"
                            || Key(owner) == "System.AppContext" && method.Name == "SetSwitch"
                            || Key(owner) == "System.Diagnostics.DiagnosticListener" && method.Name == "Subscribe"):
                    findings.Add($"{Key(owner)}.{method.Name} in {Where(model, node)}");

                    break;
            }
        }

        return Ordered(findings);
    }

    private static bool IsRuntimeEventOwner(INamedTypeSymbol owner) => Key(owner) is "System.Console" or "System.AppDomain" or "System.Runtime.Loader.AssemblyLoadContext"
        or "System.Diagnostics.Process" or "System.IO.FileSystemWatcher" or "System.Net.NetworkInformation.NetworkChange";

    // ------------------------------------------------------------------------------------------------------------------------------
    // What a serializer can read. Source-generated System.Text.Json is the only serializer (reflection-based serialization is off under
    // Native AOT), and it reads exactly the properties of the types a JsonSerializable attribute names, and of the types those reach.
    // ------------------------------------------------------------------------------------------------------------------------------

    private static bool Is(AttributeData attribute, string key) => attribute.AttributeClass is { } owner && Key(owner) == key;

    /// <summary>
    /// The types a source-generated serializer can read: the types named by a <c>JsonSerializable</c> attribute and, transitively, the types of
    /// the members it reads, their base types, their type arguments, their array elements and the subtypes a base type names with
    /// <c>JsonDerivedType</c>. The members it reads are the public instance properties, every member marked <c>JsonInclude</c> whatever its
    /// accessibility (a field included), and the public instance fields of the types a context reads when that context includes fields. The
    /// closure covers authored types only.
    /// </summary>
    internal static HashSet<string> SerializableClosure(IReadOnlyList<Compilation> compilations)
    {
        HashSet<string> closure = new(StringComparer.Ordinal);

        // A type is read once with fields and once without: a context that includes fields reads the fields of the types it reaches.
        HashSet<(string, bool)> visited = [];

        Queue<(ITypeSymbol Type, bool IncludeFields)> pending = [];

        foreach (INamedTypeSymbol context in SourceTypes(compilations))
        {
            bool includeFields = context.GetAttributes().Any(static attribute => Is(attribute, "System.Text.Json.Serialization.JsonSourceGenerationOptionsAttribute")
                && attribute.NamedArguments.Any(static argument => argument is { Key: "IncludeFields", Value.Value: true }));

            foreach (AttributeData attribute in context.GetAttributes().Where(static attribute => Is(attribute, "System.Text.Json.Serialization.JsonSerializableAttribute")))
            {
                if (attribute.ConstructorArguments is [{ Value: ITypeSymbol named }, ..])
                {
                    pending.Enqueue((named, includeFields));
                }
            }
        }

        while (pending.Count > 0)
        {
            (ITypeSymbol type, bool includeFields) = pending.Dequeue();

            if (type is IArrayTypeSymbol array)
            {
                pending.Enqueue((array.ElementType, includeFields));

                continue;
            }

            if (type is not INamedTypeSymbol named)
            {
                continue;
            }

            foreach (ITypeSymbol argument in named.TypeArguments)
            {
                pending.Enqueue((argument, includeFields));
            }

            INamedTypeSymbol definition = named.OriginalDefinition;

            closure.Add(Key(definition));

            if (!visited.Add((Key(definition), includeFields)) || !definition.Locations.Any(static location => location.IsInSource))
            {
                continue;
            }

            if (definition.BaseType is { } baseType)
            {
                pending.Enqueue((baseType, includeFields));
            }

            foreach (AttributeData derived in definition.GetAttributes().Where(static attribute => Is(attribute, "System.Text.Json.Serialization.JsonDerivedTypeAttribute")))
            {
                if (derived.ConstructorArguments is [{ Value: ITypeSymbol subtype }, ..])
                {
                    pending.Enqueue((subtype, includeFields));
                }
            }

            foreach (ISymbol member in definition.GetMembers().Where(static member => !member.IsStatic && member is IPropertySymbol or IFieldSymbol))
            {
                bool included = member.GetAttributes().Any(static attribute => Is(attribute, "System.Text.Json.Serialization.JsonIncludeAttribute"));

                ITypeSymbol? read = member switch
                {
                    IPropertySymbol property when property.DeclaredAccessibility == Accessibility.Public || included => property.Type,
                    IFieldSymbol field when !field.IsImplicitlyDeclared && (included || includeFields && field.DeclaredAccessibility == Accessibility.Public) => field.Type,
                    _ => null,
                };

                if (read is not null)
                {
                    pending.Enqueue((read, includeFields));
                }
            }
        }

        return closure;
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // Disposal when the container is disposed. The container disposes every disposable it owns when the host's root provider is disposed.
    // ------------------------------------------------------------------------------------------------------------------------------

    // How long a registration's lifetime keeps what the container builds: a singleton, a pool and a context factory end with the root provider.
    private static int Rank(string lifetime) => lifetime switch
    {
        "Singleton" => 5,
        "Pool" => 4,
        "Factory" => 3,
        "Scoped" => 2,
        "Transient" => 1,
        _ => 0,
    };

    /// <summary>
    /// What the factories passed to container registrations build. A factory is a lambda or a method group written at the registration; it is
    /// followed through every authored method it calls (every authored override and implementation of a virtual or interface member, a local
    /// function and a method group included) to the authored types those methods construct, with <c>new</c> or with the generic form of the
    /// container's activator. A delegate that is invoked, a delegate that is passed as a value and a build by reflection have a product that is
    /// not listed.
    /// </summary>
    internal sealed class FactoryProducts(IReadOnlyList<Compilation> compilations)
    {
        private readonly Dictionary<SyntaxNode, (HashSet<INamedTypeSymbol> Types, HashSet<string> Unlisted)> byRegistration = [];

        private readonly Dictionary<IMethodSymbol, IReadOnlyList<IMethodSymbol>> implementations = new(SymbolEqualityComparer.Default);

        /// <summary>The authored types the registration's factories construct, and why some product is not listed (one finding per kind).</summary>
        internal (IReadOnlyCollection<INamedTypeSymbol> Types, IReadOnlyCollection<string> Unlisted) Of(ContainerRegistration registration)
        {
            if (!byRegistration.TryGetValue(registration.Node, out (HashSet<INamedTypeSymbol> Types, HashSet<string> Unlisted) known))
            {
                known = ([], []);

                // Only the arguments are read: the receiver of a chained registration is a registration of its own.
                ImmutableArray<IArgumentOperation> arguments = registration.Model.GetOperation(registration.Node) switch
                {
                    IInvocationOperation invocation => invocation.Arguments,
                    IObjectCreationOperation creation => creation.Arguments,
                    _ => default,
                };

                HashSet<IMethodSymbol> followed = new(SymbolEqualityComparer.Default);

                foreach (IArgumentOperation argument in arguments.IsDefault ? [] : arguments)
                {
                    IOperation value = Unconverted(argument.Value);

                    if (value.Type is { TypeKind: TypeKind.Delegate } && value is not (IAnonymousFunctionOperation or IMethodReferenceOperation))
                    {
                        known.Unlisted.Add("factory delegate passed as a value");
                    }

                    Walk(value, followed, known.Types, known.Unlisted);
                }

                byRegistration[registration.Node] = known;
            }

            return (known.Types, known.Unlisted);
        }

        // A lambda or a method group is converted to its delegate type: the operation that was written is the target of the delegate creation.
        private static IOperation Unconverted(IOperation value) => value switch
        {
            IConversionOperation conversion => Unconverted(conversion.Operand),
            IDelegateCreationOperation creation => Unconverted(creation.Target),
            _ => value,
        };

        private void Walk(IOperation root, HashSet<IMethodSymbol> followed, HashSet<INamedTypeSymbol> types, HashSet<string> unlisted)
        {
            foreach (IOperation operation in root.DescendantsAndSelf())
            {
                switch (operation)
                {
                    case IObjectCreationOperation { Type: INamedTypeSymbol created } when created.Locations.Any(static location => location.IsInSource):
                        types.Add(created.OriginalDefinition);

                        break;

                    // A delegate the factory invokes has a body that is written elsewhere, so what it returns is unlisted, unless no authored
                    // disposable could be what it returns.
                    case IInvocationOperation { TargetMethod: { MethodKind: MethodKind.DelegateInvoke } invoked } when CouldReturnAnAuthoredDisposable(invoked.ReturnType):
                        unlisted.Add($"factory delegate invoked by {Holder(operation)}");

                        break;

                    case IInvocationOperation { TargetMethod: { MethodKind: MethodKind.DelegateInvoke } }:
                        break;

                    case IInvocationOperation { TargetMethod: { } target } when IsReflectiveBuild(target):
                        if (target.TypeArguments is [INamedTypeSymbol built] && built.Locations.Any(static location => location.IsInSource))
                        {
                            types.Add(built.OriginalDefinition);
                        }
                        else if (target.TypeArguments.IsEmpty)
                        {
                            unlisted.Add($"factory built by reflection by {Holder(operation)}");
                        }

                        break;

                    case IInvocationOperation { TargetMethod: { } target }:
                        Follow(target, followed, types, unlisted);

                        break;

                    case IMethodReferenceOperation reference:
                        Follow(reference.Method, followed, types, unlisted);

                        break;
                }
            }
        }

        // The authored types that the container disposes when it owns an instance: the classes and structs that declare IDisposable or IAsyncDisposable.
        private readonly Lazy<IReadOnlyList<INamedTypeSymbol>> authoredDisposables = new(() =>
            [.. SourceTypes(compilations).Where(static type => type.TypeKind is TypeKind.Class or TypeKind.Struct
                && type.AllInterfaces.Any(static contract => Key(contract) is "System.IDisposable" or "System.IAsyncDisposable"))]);

        // Whether an authored disposable is, derives from or implements the type (or a type it is made of): then a value of that type may be one.
        private bool CouldReturnAnAuthoredDisposable(ITypeSymbol type)
        {
            if (type.SpecialType == SpecialType.System_Void)
            {
                return false;
            }

            if (type.SpecialType == SpecialType.System_Object || Key(type) is "System.IDisposable" or "System.IAsyncDisposable" or "System.ValueType")
            {
                return true;
            }

            if (type is INamedTypeSymbol { IsGenericType: true } generic && generic.TypeArguments.Any(CouldReturnAnAuthoredDisposable))
            {
                return true;
            }

            string key = Key(type.OriginalDefinition);

            return authoredDisposables.Value.Any(disposable => Key(disposable.OriginalDefinition) == key
                || DerivesFrom(disposable, key)
                || disposable.AllInterfaces.Any(contract => Key(contract.OriginalDefinition) == key));
        }

        // The member that holds an operation, a closure collapsed into the member that wrote it.
        private static string Holder(IOperation operation) =>
            operation.SemanticModel?.GetEnclosingSymbol(operation.Syntax.SpanStart) is { } enclosing
                ? AuthorityCarrierScanner.Qualified(ContainerRegistrationScanner.EnclosingMember(enclosing))
                : "<unknown>";

        private static bool IsReflectiveBuild(IMethodSymbol method) =>
            Key(method.ContainingType.OriginalDefinition) is "System.Activator" or "Microsoft.Extensions.DependencyInjection.ActivatorUtilities"
            && method.Name is "CreateInstance" or "CreateFactory";

        private void Follow(IMethodSymbol method, HashSet<IMethodSymbol> followed, HashSet<INamedTypeSymbol> types, HashSet<string> unlisted)
        {
            IMethodSymbol definition = method.OriginalDefinition;

            if (!followed.Add(definition) || definition.DeclaringSyntaxReferences.IsEmpty)
            {
                return;
            }

            foreach (SyntaxReference declaration in definition.DeclaringSyntaxReferences)
            {
                if (compilations.FirstOrDefault(candidate => candidate.ContainsSyntaxTree(declaration.SyntaxTree)) is { } compilation
                    && SharedSemanticModels.Of(compilation, declaration.SyntaxTree).GetOperation(declaration.GetSyntax()) is { } body)
                {
                    Walk(body, followed, types, unlisted);
                }
            }

            // A virtual or interface member runs whichever authored override or implementation the receiver has.
            if (definition.IsVirtual || definition.IsAbstract || definition.IsOverride || definition.ContainingType.TypeKind == TypeKind.Interface)
            {
                if (!implementations.TryGetValue(definition, out IReadOnlyList<IMethodSymbol>? related))
                {
                    implementations[definition] = related = HostedGrimoireProducerInventory.MethodsACallCanRun(compilations, definition);
                }

                foreach (IMethodSymbol implementation in related)
                {
                    Follow(implementation, followed, types, unlisted);
                }
            }
        }
    }

    /// <summary>
    /// The authored types a container registration can build: a type named by a registration call (as a service, an implementation or a
    /// typeof operand) and a type that a factory passed to a registration constructs (see <see cref="FactoryProducts"/>). The lifetime of the
    /// registration is kept, and a type registered more than once keeps the longest, because a scoped or transient service ends with its scope
    /// while a singleton ends with the root provider.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> RegisteredImplementations(IReadOnlyList<Compilation> compilations)
    {
        Dictionary<string, string> registered = new(StringComparer.Ordinal);

        void Register(string type, string lifetime)
        {
            if (!registered.TryGetValue(type, out string? known) || Rank(lifetime) > Rank(known))
            {
                registered[type] = lifetime;
            }
        }

        FactoryProducts products = new(compilations);

        foreach (ContainerRegistration registration in ContainerRegistrationScanner.Find(compilations, static _ => true, IsAnalysedTree))
        {
            Register(Key(registration.Watched.OriginalDefinition), registration.Lifetime);

            foreach (INamedTypeSymbol built in products.Of(registration).Types)
            {
                Register(Key(built), registration.Lifetime);
            }
        }

        foreach ((SemanticModel model, SyntaxNode node) in Nodes(compilations))
        {
            // A hosted service is a singleton the container builds from its type.
            if (node is InvocationExpressionSyntax call
                && model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "AddHostedService" or "AddInstallationResetRecoveryAwareHostedService", TypeArguments: [{ } service] })
            {
                Register(Key(service.OriginalDefinition), "Singleton");
            }
        }

        return registered;
    }

    /// <summary>
    /// The registrations whose factory builds something the scan cannot list: a delegate the factory invokes, a delegate registered as a value
    /// instead of being written at the registration, and an object built by reflection from a type that is not a type argument. What such a
    /// factory builds may be an authored type that the container owns, so the pin that lists the owned types refuses it.
    /// </summary>
    internal static string[] UnresolvedFactoryProducts(IReadOnlyList<Compilation> compilations)
    {
        FactoryProducts products = new(compilations);

        return Ordered(ContainerRegistrationScanner.Find(compilations, static _ => true, IsAnalysedTree)
            .SelectMany(registration => products.Of(registration).Unlisted.Select(unlisted => $"{unlisted} for a registration in {registration.Enclosing}"))
            .Distinct(StringComparer.Ordinal));
    }

    /// <summary>The calls a lambda or a local function makes, as the containing type and name of each method it invokes.</summary>
    internal static string[] CallsOfHandler(IReadOnlyList<Compilation> compilations, Func<SemanticModel, SyntaxNode, bool> handler)
    {
        foreach ((SemanticModel model, SyntaxNode node) in Nodes(compilations))
        {
            if (handler(model, node))
            {
                return Ordered(node.DescendantNodes().OfType<InvocationExpressionSyntax>()
                    .Select(call => model.GetSymbolInfo(call).Symbol)
                    .OfType<IMethodSymbol>()
                    .Select(static method => Key(method.ContainingType.OriginalDefinition) + "." + method.Name));
            }
        }

        return ["<handler not found>"];
    }
}
