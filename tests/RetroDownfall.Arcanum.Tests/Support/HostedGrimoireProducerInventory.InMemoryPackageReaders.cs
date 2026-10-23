using System.IO.Compression;
using System.Reflection;
using System.Xml;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    // In-memory package readers. The Comprehension extractors inflate a ZIP entry with a DeflateStream over a MemoryStream of the
    // snapshot bytes, and parse each part with an XmlReader over a read-only MemoryStream. A DeflateStream or an XmlReader reads,
    // and on disposal releases, whatever stream it was handed, so neither is reviewed by name: a member that reads or releases the
    // input is accepted only where the input is proved to be memory, as a StreamReader's is. The members that only report the state
    // of the node already read (NodeType, LocalName, GetAttribute and the like) are reviewed by name in ReviewedNonProducerExternalMembers.
    private const string BoundedXmlReaderType = "RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.BoundedXmlReader";

    // The analysed production source binds XmlReader in the System.Xml.ReaderWriter reference assembly; a fixture compiled against
    // the runtime binds it where the runtime implements it.
    private static readonly AssemblyName[] XmlReaderAssemblies =
    [
        typeof(XmlReader).Assembly.GetName(),
        Assembly.Load(new AssemblyName("System.Xml.ReaderWriter")).GetName(),
    ];

    private static readonly AssemblyName[] DeflateStreamAssemblies = [typeof(DeflateStream).Assembly.GetName()];

    // The settings a reviewed in-memory reader may set. None of them resolves, fetches or validates anything; a reader whose settings
    // set anything else (a resolver, a validation type, schemas) is not judged by this rule.
    private static readonly IReadOnlySet<string> ReviewedInMemoryXmlReaderSettings = new HashSet<string>(StringComparer.Ordinal)
    {
        "CloseInput",
        "DtdProcessing",
        "IgnoreComments",
        "IgnoreProcessingInstructions",
        "IgnoreWhitespace",
        "MaxCharactersFromEntities",
        "MaxCharactersInDocument",
        "XmlResolver",
    };

    private sealed partial class ProducerGraph
    {
        private readonly Dictionary<ITypeSymbol, bool> closedInMemoryXmlReaderTypes = new(SymbolEqualityComparer.Default);

        private readonly Dictionary<IFieldSymbol, ExpressionSyntax[]?> constructorWrittenFieldSources = new(SymbolEqualityComparer.Default);

        private static bool IsFrameworkTypeIn(ITypeSymbol? type, string expectedType, IReadOnlyList<AssemblyName> trustedAssemblies) =>
            type is not null
            && TypeKey(type) == expectedType
            && !type.Locations.Any(static location => location.IsInSource)
            && trustedAssemblies.Any(trusted => FrameworkAssemblyIdentityMatches(type.ContainingAssembly.Identity, trusted));

        private static ExpressionSyntax? ArgumentExpression(IEnumerable<IArgumentOperation> arguments, int ordinal) =>
            arguments.SingleOrDefault(argument => argument.Parameter?.Ordinal == ordinal)?.Syntax switch
            {
                ArgumentSyntax argument => argument.Expression,
                ExpressionSyntax expression => expression,
                _ => null,
            };

        // A DeflateStream built over an in-memory stream, and a read from one: inflating memory into memory touches no file.
        private bool IsReviewedInMemoryInflaterMember(
            IMethodSymbol method,
            SyntaxNode? node,
            SemanticModel? model,
            AuthoredMember? context)
        {
            if (model is null
                || node is null
                || !IsFrameworkTypeIn(method.ContainingType, "System.IO.Compression.DeflateStream", DeflateStreamAssemblies))
            {
                return false;
            }

            if (method.MethodKind == MethodKind.Constructor)
            {
                return node is BaseObjectCreationExpressionSyntax creation
                    && HasInMemoryInflaterProvenance(creation, model, new HashSet<ISymbol>(SymbolEqualityComparer.Default), context);
            }

            return method.Name.StartsWith("Read", StringComparison.Ordinal)
                && node is InvocationExpressionSyntax call
                && model.GetOperation(call) is IInvocationOperation { Instance.Syntax: ExpressionSyntax receiver }
                && HasInMemoryInflaterProvenance(receiver, model, new HashSet<ISymbol>(SymbolEqualityComparer.Default), context);
        }

        private bool HasInMemoryInflaterProvenance(
            ExpressionSyntax expression,
            SemanticModel model,
            HashSet<ISymbol> path,
            AuthoredMember? context)
        {
            expression = StripTransparentExpression(expression);

            if (expression is BaseObjectCreationExpressionSyntax creation
                && model.GetOperation(creation) is IObjectCreationOperation { Constructor: { } constructor } operation
                && IsFrameworkTypeIn(constructor.ContainingType, "System.IO.Compression.DeflateStream", DeflateStreamAssemblies))
            {
                return constructor.Parameters.FirstOrDefault() is { RefKind: RefKind.None } input
                    && TypeKey(input.Type) == "System.IO.Stream"
                    && ArgumentExpression(operation.Arguments, 0) is { } stream
                    && HasReviewedInMemoryStreamProvenance(stream, model, new HashSet<ISymbol>(SymbolEqualityComparer.Default), context);
            }

            return HasOnlyReviewedValueSources(expression, model, path, context, HasInMemoryInflaterProvenance);
        }

        // XmlReader.Create over an in-memory stream with settings that resolve nothing, and Read, Value and Dispose on the Reader of
        // BoundedXmlReader, whose every instance holds such a reader (IsClosedInMemoryXmlReaderType).
        private bool IsReviewedInMemoryXmlReaderMember(
            ISymbol member,
            SyntaxNode? node,
            SemanticModel? model,
            AuthoredMember? context)
        {
            if (model is null
                || node is null
                || !IsFrameworkTypeIn(member.ContainingType, "System.Xml.XmlReader", XmlReaderAssemblies))
            {
                return false;
            }

            switch (member)
            {
                case IMethodSymbol { Name: "Create", IsStatic: true } create
                    when HasExactParameterTypes(create, "System.IO.Stream", "System.Xml.XmlReaderSettings"):
                    return node is InvocationExpressionSyntax call
                        && IsInMemoryNonResolvingXmlReaderCreation(call, model, context);
                case IMethodSymbol { Name: "Read" or "Dispose", IsStatic: false, Parameters.Length: 0 }:
                    return node is InvocationExpressionSyntax invocation
                        && model.GetOperation(invocation) is IInvocationOperation { Instance.Syntax: ExpressionSyntax receiver }
                        && HasBoundedXmlReaderProvenance(receiver, model, new HashSet<ISymbol>(SymbolEqualityComparer.Default), context);
                case IPropertySymbol { Name: "Value", IsStatic: false, IsIndexer: false }:
                    return node is MemberAccessExpressionSyntax access
                        && HasBoundedXmlReaderProvenance(access.Expression, model, new HashSet<ISymbol>(SymbolEqualityComparer.Default), context);
                default:
                    return false;
            }
        }

        private bool IsInMemoryNonResolvingXmlReaderCreation(
            InvocationExpressionSyntax call,
            SemanticModel model,
            AuthoredMember? context) =>
            model.GetOperation(call) is IInvocationOperation { TargetMethod: { Name: "Create", IsStatic: true } create } operation
            && IsFrameworkTypeIn(create.ContainingType, "System.Xml.XmlReader", XmlReaderAssemblies)
            && HasExactParameterTypes(create, "System.IO.Stream", "System.Xml.XmlReaderSettings")
            && ArgumentExpression(operation.Arguments, 0) is { } input
            && ArgumentExpression(operation.Arguments, 1) is { } settings
            && HasInMemoryXmlInputProvenance(input, model, new HashSet<ISymbol>(SymbolEqualityComparer.Default), context)
            && IsNonResolvingXmlReaderSettings(settings, model);

        // An in-memory stream as the stream rules prove one, or the result of an authored static method whose every return is one.
        private bool HasInMemoryXmlInputProvenance(
            ExpressionSyntax expression,
            SemanticModel model,
            HashSet<ISymbol> path,
            AuthoredMember? context)
        {
            expression = StripTransparentExpression(expression);

            if (HasReviewedInMemoryStreamProvenance(expression, model, new HashSet<ISymbol>(SymbolEqualityComparer.Default), context))
            {
                return true;
            }

            if (expression is InvocationExpressionSyntax call
                && model.GetSymbolInfo(call).Symbol is IMethodSymbol { IsStatic: true, MethodKind: MethodKind.Ordinary, IsAsync: false } method
                && method.DeclaringSyntaxReferences is [{ } reference]
                && reference.GetSyntax() is MethodDeclarationSyntax declaration
                && semanticModels.TryGetValue(declaration.SyntaxTree, out SemanticModel? methodModel))
            {
                if (!path.Add(method))
                {
                    return false;
                }

                try
                {
                    ExpressionSyntax[] returns = declaration switch
                    {
                        { ExpressionBody.Expression: { } result } => [result],
                        { Body: { } body } => [.. body.DescendantNodes(static node => node is not AnonymousFunctionExpressionSyntax
                                    and not LocalFunctionStatementSyntax)
                                .OfType<ReturnStatementSyntax>()
                                .Select(static statement => statement.Expression)
                                .OfType<ExpressionSyntax>()],
                        _ => [],
                    };

                    return returns.Length != 0
                        && returns.All(result => HasInMemoryXmlInputProvenance(result, methodModel, path, null));
                }
                finally
                {
                    path.Remove(method);
                }
            }

            return HasOnlyReviewedValueSources(expression, model, path, context, HasInMemoryXmlInputProvenance);
        }

        // Settings built in place, or a local built so and read nowhere but here, that clear the resolver, refuse or ignore a DOCTYPE,
        // and set nothing outside the reviewed set: such a reader reads its input and nothing else.
        private static bool IsNonResolvingXmlReaderSettings(ExpressionSyntax settings, SemanticModel model)
        {
            settings = StripTransparentExpression(settings);

            if (model.GetSymbolInfo(settings).Symbol is ILocalSymbol local)
            {
                if (local.DeclaringSyntaxReferences is not [{ } reference]
                    || reference.GetSyntax() is not VariableDeclaratorSyntax { Initializer.Value: { } declared } declarator
                    || declarator.Ancestors().FirstOrDefault(static ancestor => ancestor is BaseMethodDeclarationSyntax
                        or LocalFunctionStatementSyntax
                        or AccessorDeclarationSyntax
                        or AnonymousFunctionExpressionSyntax) is not { } scope)
                {
                    return false;
                }

                IdentifierNameSyntax[] uses = [.. scope.DescendantNodes()
                    .OfType<IdentifierNameSyntax>()
                    .Where(name => name.Identifier.ValueText == local.Name
                        && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(name).Symbol, local))];

                if (uses is not [IdentifierNameSyntax only] || only != settings)
                {
                    return false;
                }

                settings = StripTransparentExpression(declared);
            }

            if (model.GetOperation(settings) is not IObjectCreationOperation
                {
                    Constructor: { Parameters.Length: 0 } constructor,
                    Initializer: { } initializer,
                }
                || !IsFrameworkTypeIn(constructor.ContainingType, "System.Xml.XmlReaderSettings", XmlReaderAssemblies))
            {
                return false;
            }

            bool resolverCleared = false;

            bool documentTypeClosed = false;

            foreach (IOperation assignment in initializer.Initializers)
            {
                if (assignment is not ISimpleAssignmentOperation { Target: IPropertyReferenceOperation { Property: { } property } } simple
                    || !ReviewedInMemoryXmlReaderSettings.Contains(property.Name))
                {
                    return false;
                }

                if (property.Name == "XmlResolver")
                {
                    if (simple.Value.ConstantValue is not { HasValue: true, Value: null })
                    {
                        return false;
                    }

                    resolverCleared = true;
                }
                else if (property.Name == "DtdProcessing")
                {
                    if (!IsClosedDocumentTypeProcessing(simple.Value))
                    {
                        return false;
                    }

                    documentTypeClosed = true;
                }
            }

            return resolverCleared && documentTypeClosed;
        }

        // DtdProcessing.Prohibit or DtdProcessing.Ignore, as a constant or as both arms of a choice: a DOCTYPE is refused or skipped
        // unprocessed, never parsed.
        private static bool IsClosedDocumentTypeProcessing(IOperation value)
        {
            if (value is IConditionalOperation { WhenFalse: { } whenFalse } conditional)
            {
                return IsClosedDocumentTypeProcessing(conditional.WhenTrue) && IsClosedDocumentTypeProcessing(whenFalse);
            }

            return value.ConstantValue is { HasValue: true, Value: int raw }
                && (raw == (int)DtdProcessing.Prohibit || raw == (int)DtdProcessing.Ignore);
        }

        // The Reader of a BoundedXmlReader, read directly or through a local or a readonly field that holds only that.
        private bool HasBoundedXmlReaderProvenance(
            ExpressionSyntax expression,
            SemanticModel model,
            HashSet<ISymbol> path,
            AuthoredMember? context)
        {
            expression = StripTransparentExpression(expression);

            ISymbol? symbol = model.GetSymbolInfo(expression).Symbol;

            if (symbol is IPropertySymbol { Name: "Reader", IsStatic: false } reader
                && TypeKey(reader.ContainingType) == BoundedXmlReaderType)
            {
                return IsClosedInMemoryXmlReaderType(reader.ContainingType);
            }

            if (symbol is IFieldSymbol { IsReadOnly: true, IsStatic: false } field
                && ConstructorWrittenFieldSources(field) is { } sources)
            {
                if (!path.Add(field))
                {
                    return false;
                }

                try
                {
                    return sources.All(source => semanticModels.TryGetValue(source.SyntaxTree, out SemanticModel? sourceModel)
                        && HasBoundedXmlReaderProvenance(source, sourceModel, path, null));
                }
                finally
                {
                    path.Remove(field);
                }
            }

            return HasOnlyReviewedValueSources(expression, model, path, context, HasBoundedXmlReaderProvenance);
        }

        // The values a readonly instance field can hold: its initializer and the plain assignments the instance constructors of its
        // type make. Null when anything else writes it (a compound assignment, a deconstruction, a ref or out argument).
        private ExpressionSyntax[]? ConstructorWrittenFieldSources(IFieldSymbol field)
        {
            if (!constructorWrittenFieldSources.TryGetValue(field, out ExpressionSyntax[]? sources))
            {
                sources = ConstructorWrittenFieldSourcesCore(field);

                constructorWrittenFieldSources[field] = sources;
            }

            return sources;
        }

        private ExpressionSyntax[]? ConstructorWrittenFieldSourcesCore(IFieldSymbol field)
        {
            if (field.DeclaringSyntaxReferences is not [{ } reference]
                || reference.GetSyntax() is not VariableDeclaratorSyntax declarator
                || field.ContainingType.DeclaringSyntaxReferences is not [{ } typeReference]
                || typeReference.GetSyntax() is not TypeDeclarationSyntax type
                || !semanticModels.TryGetValue(type.SyntaxTree, out SemanticModel? model))
            {
                return null;
            }

            List<ExpressionSyntax> sources = [];

            if (declarator.Initializer?.Value is { } initializer)
            {
                sources.Add(initializer);
            }

            foreach (SyntaxNode node in type.DescendantNodes())
            {
                if (node is AssignmentExpressionSyntax assignment
                    && (SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(assignment.Left).Symbol, field)
                        || DeconstructsInto(assignment, model, field)))
                {
                    if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                        || assignment.Left is TupleExpressionSyntax
                        || assignment.Ancestors().FirstOrDefault(static ancestor => ancestor is BaseMethodDeclarationSyntax
                            or LocalFunctionStatementSyntax
                            or AnonymousFunctionExpressionSyntax) is not ConstructorDeclarationSyntax constructor
                        || constructor.Modifiers.Any(SyntaxKind.StaticKeyword))
                    {
                        return null;
                    }

                    sources.Add(assignment.Right);
                }
                else if (node is ArgumentSyntax argument
                    && !argument.RefKindKeyword.IsKind(SyntaxKind.None)
                    && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(argument.Expression).Symbol, field))
                {
                    return null;
                }
            }

            return sources.Count == 0 ? null : [.. sources];
        }

        // BoundedXmlReader is closed over an in-memory reader that resolves nothing: a sealed class with one declaration, a get-only
        // auto-property Reader that each of its private constructors (none chaining) sets once from one of its own parameters, and
        // every construction (only the type itself can make one) handing that parameter XmlReader.Create over an in-memory stream with
        // non-resolving settings.
        private bool IsClosedInMemoryXmlReaderType(INamedTypeSymbol type)
        {
            if (closedInMemoryXmlReaderTypes.TryGetValue(type, out bool known))
            {
                return known;
            }

            closedInMemoryXmlReaderTypes[type] = false;

            bool closed = IsClosedInMemoryXmlReaderTypeCore(type);

            closedInMemoryXmlReaderTypes[type] = closed;

            return closed;
        }

        private bool IsClosedInMemoryXmlReaderTypeCore(INamedTypeSymbol type)
        {
            if (type is not { TypeKind: TypeKind.Class, IsSealed: true, IsGenericType: false }
                || type.DeclaringSyntaxReferences is not [{ } reference]
                || reference.GetSyntax() is not ClassDeclarationSyntax { ParameterList: null } declaration
                || !semanticModels.TryGetValue(declaration.SyntaxTree, out SemanticModel? model)
                || type.GetMembers("Reader") is not [IPropertySymbol { SetMethod: null, IsStatic: false } reader]
                || !IsFrameworkTypeIn(reader.Type, "System.Xml.XmlReader", XmlReaderAssemblies)
                || reader.DeclaringSyntaxReferences is not [{ } readerReference]
                || readerReference.GetSyntax() is not PropertyDeclarationSyntax
                {
                    Initializer: null,
                    ExpressionBody: null,
                    AccessorList.Accessors: [{ Body: null, ExpressionBody: null } getter],
                }
                || !getter.IsKind(SyntaxKind.GetAccessorDeclaration)
                || type.InstanceConstructors.Any(static constructor => constructor.IsImplicitlyDeclared
                    || constructor.DeclaredAccessibility != Accessibility.Private)
                || declaration.DescendantNodes().OfType<ConstructorInitializerSyntax>().Any())
            {
                return false;
            }

            Dictionary<IMethodSymbol, int> readerParameters = new(SymbolEqualityComparer.Default);

            foreach (AssignmentExpressionSyntax assignment in declaration.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(assignment.Left).Symbol, reader)
                    && !DeconstructsInto(assignment, model, reader))
                {
                    continue;
                }

                if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                    || assignment.Parent is not ExpressionStatementSyntax { Parent: BlockSyntax { Parent: ConstructorDeclarationSyntax constructorSyntax } }
                    || model.GetDeclaredSymbol(constructorSyntax) is not { } constructor
                    || constructor.IsStatic
                    || model.GetSymbolInfo(assignment.Right).Symbol is not IParameterSymbol parameter
                    || !SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, constructor)
                    || !readerParameters.TryAdd(constructor, parameter.Ordinal))
                {
                    return false;
                }
            }

            if (type.InstanceConstructors.Any(constructor => !readerParameters.ContainsKey(constructor)))
            {
                return false;
            }

            foreach (BaseObjectCreationExpressionSyntax creation in declaration.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
            {
                if (model.GetOperation(creation) is not IObjectCreationOperation { Constructor: { } constructor } operation
                    || !SymbolEqualityComparer.Default.Equals(constructor.ContainingType, type))
                {
                    continue;
                }

                if (!readerParameters.TryGetValue(constructor, out int ordinal)
                    || ArgumentExpression(operation.Arguments, ordinal) is not { } argument
                    || StripTransparentExpression(argument) is not InvocationExpressionSyntax created
                    || !IsInMemoryNonResolvingXmlReaderCreation(created, model, null))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
