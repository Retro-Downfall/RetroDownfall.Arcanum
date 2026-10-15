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
                SemanticModel model = compilation.GetSemanticModel(tree);

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
    };

    /// <summary>
    /// A call that reads, writes or invokes a member by reflection, loads an assembly or a native library, or builds an object from an argument array: each is reported with the
    /// member that holds it, and with <c>carries</c> when an argument it is handed holds an authority handle, which a reflective store could keep.
    /// </summary>
    internal static string[] CarrierReflection(IReadOnlyList<Compilation> compilations)
    {
        List<string> findings = [];

        foreach ((SemanticModel model, SyntaxNode node) in Nodes(compilations))
        {
            if (node is not InvocationExpressionSyntax call
                || model.GetSymbolInfo(call).Symbol is not IMethodSymbol { ContainingType: { } owner } method
                || !ReflectiveInvocations.Contains(Key(owner.OriginalDefinition) + "." + method.Name))
            {
                continue;
            }

            bool carries = model.GetOperation(call) is IInvocationOperation invocation
                && invocation.Arguments.Any(argument => HoldsBeforeConversion(argument.Value));

            findings.Add($"{Key(owner.OriginalDefinition)}.{method.Name} in {Where(model, node)}{(carries ? " carries an authority handle" : "")}");
        }

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

    /// <summary>
    /// A type that implements a System.Text.Json callback contract (<c>IJsonOn*</c>), a type that declares a converter beyond the converters
    /// listed by the pin, and a log message template that destructures its argument (<c>{@name}</c>), which makes a structured logger read the properties of the value.
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

            if (node is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal
                && Regex.IsMatch(literal.Token.ValueText, @"\{@", RegexOptions.CultureInvariant)
                && literal.Parent?.Parent is ArgumentListSyntax { Parent: InvocationExpressionSyntax logging }
                && model.GetSymbolInfo(logging).Symbol is IMethodSymbol { ContainingType: { } loggerType }
                && Key(loggerType).Contains("Log", StringComparison.Ordinal))
            {
                findings.Add($"destructuring template in {Where(model, node)}");
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

    /// <summary>
    /// The types a source-generated serializer can read: the types named by a <c>JsonSerializable</c> attribute and, transitively, the types of
    /// their public instance properties, their base types, their type arguments and their array elements. The closure covers authored types only.
    /// </summary>
    internal static HashSet<string> SerializableClosure(IReadOnlyList<Compilation> compilations)
    {
        HashSet<string> closure = new(StringComparer.Ordinal);

        Queue<ITypeSymbol> pending = [];

        foreach (INamedTypeSymbol context in SourceTypes(compilations))
        {
            foreach (AttributeData attribute in context.GetAttributes().Where(static attribute => attribute.AttributeClass is { } owner && Key(owner) == "System.Text.Json.Serialization.JsonSerializableAttribute"))
            {
                if (attribute.ConstructorArguments is [{ Value: ITypeSymbol named }, ..])
                {
                    pending.Enqueue(named);
                }
            }
        }

        while (pending.Count > 0)
        {
            ITypeSymbol type = pending.Dequeue();

            if (type is IArrayTypeSymbol array)
            {
                pending.Enqueue(array.ElementType);

                continue;
            }

            if (type is not INamedTypeSymbol named)
            {
                continue;
            }

            foreach (ITypeSymbol argument in named.TypeArguments)
            {
                pending.Enqueue(argument);
            }

            INamedTypeSymbol definition = named.OriginalDefinition;

            if (!closure.Add(Key(definition)) || !definition.Locations.Any(static location => location.IsInSource))
            {
                continue;
            }

            if (definition.BaseType is { } baseType)
            {
                pending.Enqueue(baseType);
            }

            foreach (IPropertySymbol property in definition.GetMembers().OfType<IPropertySymbol>().Where(static property => !property.IsStatic && property.DeclaredAccessibility == Accessibility.Public))
            {
                pending.Enqueue(property.Type);
            }
        }

        return closure;
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // Disposal when the container is disposed. The container disposes every disposable it owns when the host's root provider is disposed.
    // ------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The authored types a container registration can build: a type named by a registration call (as a service, an implementation or a
    /// typeof operand) and a type that a factory passed to a registration constructs. The lifetime of the registration is kept, because a
    /// scoped or transient service ends with its scope while a singleton ends with the root provider.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> RegisteredImplementations(IReadOnlyList<Compilation> compilations)
    {
        Dictionary<string, string> registered = new(StringComparer.Ordinal);

        foreach (ContainerRegistration registration in ContainerRegistrationScanner.Find(compilations, static _ => true, IsAnalysedTree))
        {
            registered[Key(registration.Watched.OriginalDefinition)] = registration.Lifetime;

            foreach (SyntaxNode creation in registration.Node.DescendantNodes().Where(static node => node is BaseObjectCreationExpressionSyntax))
            {
                if (registration.Model.GetSymbolInfo(creation).Symbol is IMethodSymbol { ContainingType: { } built } && built.Locations.Any(static location => location.IsInSource))
                {
                    registered[Key(built.OriginalDefinition)] = registration.Lifetime;
                }
            }
        }

        foreach ((SemanticModel model, SyntaxNode node) in Nodes(compilations))
        {
            // A hosted service is a singleton the container builds from its type.
            if (node is InvocationExpressionSyntax call
                && model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "AddHostedService" or "AddInstallationResetRecoveryAwareHostedService", TypeArguments: [{ } service] })
            {
                registered[Key(service.OriginalDefinition)] = "Singleton";
            }
        }

        return registered;
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
