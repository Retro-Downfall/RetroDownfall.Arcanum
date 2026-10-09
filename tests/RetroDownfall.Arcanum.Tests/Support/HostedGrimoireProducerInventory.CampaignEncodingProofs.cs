using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        private readonly HashSet<GraphMemberIdentity> campaignComparerPropertyReview = [];

        // RequiresExternalBoundaryClassification must consult this before its older generic comparer
        // shortcut. A known null, authored conversion or callback effect cannot be waived by that shortcut.
        private bool HasRejectedCampaignEncodingArgument(
            IMethodSymbol method,
            SyntaxNode? node,
            SemanticModel? model,
            AuthoredMember? context) =>
            IsCampaignComparerSortOverload(method)
            && node is InvocationExpressionSyntax call
            && model?.GetOperation(call) is IInvocationOperation invocation
            && invocation.Arguments.SingleOrDefault(static argument => argument.Parameter?.Ordinal == 1) is { } argument
            && (HasCampaignAuthoredValueConversion(argument.Value)
                || argument.Value.Syntax is ExpressionSyntax expression
                    && HasRejectedCampaignEncodingValueSource(expression, model,
                        new HashSet<ISymbol>(SymbolEqualityComparer.Default), context, inspectComparer: true));

        private bool IsReviewedCampaignEncodingMethod(
            IMethodSymbol method,
            SyntaxNode? node,
            SemanticModel? model,
            AuthoredMember? context)
        {
            // These typed primitive overloads cannot dispatch IComparable or IFormatProvider callbacks.
            // They are the scalar leaves of the authored occurrence comparer, whose full body remains reviewed.
            // The callback scan also visits method-group symbols, so this receiver-independent leaf applies there.
            if (method.Name == "CompareTo"
                && !method.IsStatic
                && method.Parameters is [IParameterSymbol { RefKind: RefKind.None } parameter]
                && method.ContainingType.SpecialType is SpecialType.System_Byte or SpecialType.System_UInt32
                && SymbolEqualityComparer.Default.Equals(parameter.Type, method.ContainingType)
                && FrameworkAssemblyIdentityMatches(method.ContainingAssembly.Identity, typeof(byte).Assembly.GetName()))
            {
                return true;
            }

            // Type's runtime v10.0.12 equality operator can invoke an arbitrary Type.Equals
            // override. A typeof operand is a runtime type; requiring both operands to be
            // typeof expressions excludes caller-provided Type implementations and conversions.
            // https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Private.CoreLib/src/System/Type.cs
            if (method.IsStatic
                && method.Name is "op_Equality" or "op_Inequality"
                && method.ReturnType.SpecialType == SpecialType.System_Boolean
                && IsExactFrameworkType(method.ContainingType, "System.Type", typeof(Type).Assembly.GetName())
                && HasExactParameterTypes(method, "System.Type", "System.Type")
                && node is BinaryExpressionSyntax binary
                && model?.GetOperation(binary) is IBinaryOperation comparison
                && SymbolEqualityComparer.Default.Equals(comparison.OperatorMethod, method)
                && comparison.LeftOperand.Syntax is ExpressionSyntax left
                && comparison.RightOperand.Syntax is ExpressionSyntax right
                && TryStripCampaignEncodingValue(left, model, out left)
                && TryStripCampaignEncodingValue(right, model, out right)
                && model.GetOperation(left) is ITypeOfOperation
                && model.GetOperation(right) is ITypeOfOperation)
            {
                return true;
            }

            if (node is not InvocationExpressionSyntax call
                || model?.GetOperation(call) is not IInvocationOperation invocation)
            {
                return false;
            }

            if (IsExactCampaignEncoderConvert(method)
                && invocation.Instance?.Syntax is ExpressionSyntax encoder
                && !HasCampaignAuthoredValueConversion(invocation.Instance)
                && invocation.Arguments.SingleOrDefault(static argument => argument.Parameter?.Ordinal == 2)
                    ?.Value.ConstantValue is { HasValue: true, Value: true })
            {
                return HasOwnedCampaignEncoderValue(encoder, model,
                    new HashSet<ISymbol>(SymbolEqualityComparer.Default), context);
            }

            if (method.Name == "Read"
                && !method.IsStatic
                && method.Parameters.Length == 0
                && IsExactFrameworkType(method.ContainingType, "System.Text.Json.Utf8JsonReader",
                    typeof(System.Text.Json.Utf8JsonReader).Assembly.GetName())
                && invocation.Instance?.Syntax is ExpressionSyntax reader)
            {
                return HasOwnedCampaignSpanReader(reader, model);
            }

            if (method.IsStatic
                && method.Name == "TryParse"
                && IsExactFrameworkType(method.ContainingType, "System.Double", typeof(double).Assembly.GetName())
                && HasCampaignEncodingParameterTypes(method, "System.String", "System.Globalization.NumberStyles",
                    "System.IFormatProvider", "System.Double")
                && method.Parameters.Take(3).All(static parameter => parameter.RefKind == RefKind.None)
                && method.Parameters[3].RefKind == RefKind.Out
                && invocation.Arguments.SingleOrDefault(static argument => argument.Parameter?.Ordinal == 2) is { } providerArgument
                && !HasCampaignAuthoredValueConversion(providerArgument.Value)
                && providerArgument.Value.Syntax is ExpressionSyntax provider)
            {
                return HasExactCampaignInvariantProvider(provider, model,
                    new HashSet<ISymbol>(SymbolEqualityComparer.Default), context);
            }

            return IsExactCampaignArraySort(method, invocation, model, context);
        }

        private static bool HasCampaignEncodingParameterTypes(IMethodSymbol method, params string[] types) =>
            method.Parameters.Select(static parameter => TypeKey(parameter.Type)).SequenceEqual(types, StringComparer.Ordinal);

        private static bool IsExactCampaignEncoderConvert(IMethodSymbol method) =>
            method.Name == "Convert"
            && !method.IsStatic
            && IsExactFrameworkType(method.ContainingType, "System.Text.Encoder", typeof(System.Text.Encoder).Assembly.GetName())
            && HasCampaignEncodingParameterTypes(method, "System.ReadOnlySpan`1", "System.Span`1", "System.Boolean",
                "System.Int32", "System.Int32", "System.Boolean")
            && method.Parameters.Take(3).All(static parameter => parameter.RefKind == RefKind.None)
            && method.Parameters[0].Type is INamedTypeSymbol { TypeArguments: [{ SpecialType: SpecialType.System_Char }] }
            && method.Parameters[1].Type is INamedTypeSymbol { TypeArguments: [{ SpecialType: SpecialType.System_Byte }] }
            && method.Parameters.Skip(3).All(static parameter => parameter.RefKind == RefKind.Out);

        private bool HasOwnedCampaignEncoderValue(
            ExpressionSyntax expression,
            SemanticModel model,
            HashSet<ISymbol> path,
            AuthoredMember? context)
        {
            if (!TryStripCampaignEncodingValue(expression, model, out expression))
            {
                return false;
            }

            if (model.GetOperation(expression) is IInvocationOperation factory
                && factory.TargetMethod.Name == "GetEncoder"
                && factory.TargetMethod.Parameters.Length == 0
                && IsExactCampaignUtf8Type(factory.TargetMethod.ContainingType)
                && factory.Instance?.Syntax is ExpressionSyntax encoding)
            {
                return !HasCampaignAuthoredValueConversion(factory.Instance)
                    && HasStrictCampaignUtf8Value(encoding, model, path, context);
            }

            if (model.GetSymbolInfo(expression).Symbol is not ILocalSymbol local
                || !HasOnlyCampaignStorageUses(local, model, IsCampaignEncoderUse))
            {
                return false;
            }

            return HasOnlyReviewedValueSources(expression, model, path, context, HasOwnedCampaignEncoderValue);
        }

        private bool HasStrictCampaignUtf8Value(
            ExpressionSyntax expression,
            SemanticModel model,
            HashSet<ISymbol> path,
            AuthoredMember? context)
        {
            if (!TryStripCampaignEncodingValue(expression, model, out expression))
            {
                return false;
            }

            // Runtime v10.0.12 UTF8Encoding(bool,bool) installs exception fallbacks when the second
            // argument is true, and GetEncoder constructs its own EncoderNLS. The encoding and encoder
            // fallback setters still permit arbitrary callbacks, so every use must preserve ownership.
            // https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Private.CoreLib/src/System/Text/UTF8Encoding.cs
            if (model.GetOperation(expression) is IObjectCreationOperation { Constructor: { } constructor } creation
                && IsExactCampaignUtf8Type(constructor.ContainingType)
                && HasExactParameterTypes(constructor, "System.Boolean", "System.Boolean")
                && creation.Initializer is null
                && creation.Arguments.SingleOrDefault(static argument => argument.Parameter?.Ordinal == 0)
                    ?.Value.ConstantValue is { HasValue: true, Value: false }
                && creation.Arguments.SingleOrDefault(static argument => argument.Parameter?.Ordinal == 1)
                    ?.Value.ConstantValue is { HasValue: true, Value: true })
            {
                return true;
            }

            ISymbol? storage = model.GetSymbolInfo(expression).Symbol;

            if (storage is not ILocalSymbol
                && storage is not IFieldSymbol { IsReadOnly: true, DeclaredAccessibility: Accessibility.Private }
                || storage is null
                || !HasOnlyCampaignStorageUses(storage, model, IsCampaignStrictEncodingUse))
            {
                return false;
            }

            return HasOnlyReviewedValueSources(expression, model, path, context, HasStrictCampaignUtf8Value);
        }

        private static bool IsCampaignEncoderUse(IdentifierNameSyntax reference, SemanticModel model) =>
            CampaignStorageReceiverCall(reference, model) is { } invocation
            && IsExactCampaignEncoderConvert(invocation.TargetMethod)
            && invocation.Arguments.SingleOrDefault(static argument => argument.Parameter?.Ordinal == 2)
                ?.Value.ConstantValue is { HasValue: true, Value: true };

        private static bool IsCampaignStrictEncodingUse(IdentifierNameSyntax reference, SemanticModel model) =>
            CampaignStorageReceiverCall(reference, model) is { } invocation
            && invocation.TargetMethod.Name is "GetEncoder" or "GetByteCount" or "GetBytes"
            && (IsExactCampaignUtf8Type(invocation.TargetMethod.ContainingType)
                || IsExactFrameworkType(invocation.TargetMethod.ContainingType, "System.Text.Encoding",
                    typeof(System.Text.Encoding).Assembly.GetName()));

        private static bool IsExactCampaignUtf8Type(ITypeSymbol type) =>
            IsExactFrameworkType(type, "System.Text.UTF8Encoding", typeof(System.Text.UTF8Encoding).Assembly.GetName())
            // The evaluated .NET 10 reference pack declares this exact type in the facade,
            // while the runtime forwards it to CoreLib. Keep this alias local to UTF8Encoding;
            // source-defined names and subclass receivers still fail the exact-type check.
            || IsExactFrameworkType(type, "System.Text.UTF8Encoding",
                System.Reflection.Assembly.Load(new System.Reflection.AssemblyName("System.Text.Encoding.Extensions")).GetName());

        private bool HasOwnedCampaignSpanReader(ExpressionSyntax expression, SemanticModel model)
        {
            if (!TryStripCampaignEncodingValue(expression, model, out expression))
            {
                return false;
            }

            if (model.GetSymbolInfo(expression).Symbol is not ILocalSymbol local
                || local.DeclaringSyntaxReferences is not [SyntaxReference declaration]
                || declaration.GetSyntax() is not VariableDeclaratorSyntax { Initializer.Value: { } initializer }
                || !IsExactCampaignSpanReaderCreation(initializer, semanticModels[initializer.SyntaxTree])
                || !HasOnlyCampaignStorageUses(local, model, IsCampaignReaderUse))
            {
                return false;
            }

            return model.SyntaxTree.GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Where(assignment => SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(assignment.Left).Symbol, local))
                .All(assignment => assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                    && IsExactCampaignSpanReaderCreation(assignment.Right, model));
        }

        private static bool IsExactCampaignSpanReaderCreation(ExpressionSyntax expression, SemanticModel model) =>
            TryStripCampaignEncodingValue(expression, model, out expression)
            && model.GetOperation(expression) is IObjectCreationOperation
            {
                Constructor: { Parameters: [IParameterSymbol { Type: INamedTypeSymbol span }, ..] } constructor,
                Initializer: null,
            }
            && IsExactFrameworkType(constructor.ContainingType, "System.Text.Json.Utf8JsonReader",
                typeof(System.Text.Json.Utf8JsonReader).Assembly.GetName())
            && IsExactFrameworkType(span, "System.ReadOnlySpan`1", typeof(ReadOnlySpan<byte>).Assembly.GetName())
            && span.TypeArguments is [{ SpecialType: SpecialType.System_Byte }];

        private static bool IsCampaignReaderUse(IdentifierNameSyntax reference, SemanticModel model)
        {
            // The span constructor in runtime v10.0.12 sets _isInputSequence and _isMultiSegment false.
            // ParseValue advances that reader without replacing its backing span. A sequence constructor,
            // arbitrary ref mutation or escaping copy could instead introduce MemoryManager callbacks.
            // https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Text.Json/src/System/Text/Json/Reader/Utf8JsonReader.cs
            if (CampaignStorageReceiverCall(reference, model) is { TargetMethod: { } method })
            {
                return method.Name == "Read"
                    && method.Parameters.Length == 0
                    && IsExactFrameworkType(method.ContainingType, "System.Text.Json.Utf8JsonReader",
                        typeof(System.Text.Json.Utf8JsonReader).Assembly.GetName());
            }

            return reference.Parent is ArgumentSyntax { RefKindKeyword.RawKind: (int)SyntaxKind.RefKeyword } argument
                && argument.Parent?.Parent is InvocationExpressionSyntax call
                && model.GetOperation(call) is IInvocationOperation invocation
                && invocation.TargetMethod.Name == "ParseValue"
                && IsExactFrameworkType(invocation.TargetMethod.ContainingType, "System.Text.Json.JsonDocument",
                    typeof(System.Text.Json.JsonDocument).Assembly.GetName())
                && invocation.TargetMethod.Parameters is [IParameterSymbol { RefKind: RefKind.Ref } parameter]
                && IsExactFrameworkType(parameter.Type, "System.Text.Json.Utf8JsonReader",
                    typeof(System.Text.Json.Utf8JsonReader).Assembly.GetName());
        }

        private bool HasExactCampaignInvariantProvider(
            ExpressionSyntax expression,
            SemanticModel model,
            HashSet<ISymbol> path,
            AuthoredMember? context)
        {
            if (!TryStripCampaignEncodingValue(expression, model, out expression))
            {
                return false;
            }

            // NumberFormatInfo.GetInstance invokes an unknown provider's GetFormat. The framework's
            // invariant singleton is a read-only CultureInfo with its own stored NumberFormatInfo.
            // https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Private.CoreLib/src/System/Globalization/NumberFormatInfo.cs
            if (model.GetSymbolInfo(expression).Symbol is IPropertySymbol { IsStatic: true, Name: "InvariantCulture" } property
                && IsExactFrameworkType(property.ContainingType, "System.Globalization.CultureInfo",
                    typeof(System.Globalization.CultureInfo).Assembly.GetName()))
            {
                return true;
            }

            return !HasRejectedCampaignEncodingValueSource(expression, model,
                    new HashSet<ISymbol>(SymbolEqualityComparer.Default), context, inspectComparer: false)
                && HasOnlyReviewedValueSources(expression, model, path, context, HasExactCampaignInvariantProvider);
        }

        private static bool IsCampaignComparerSortOverload(IMethodSymbol method) =>
            method.IsStatic
            && method.Name == "Sort"
            && method.TypeArguments.Length == 1
            && IsExactFrameworkType(method.ContainingType, "System.Array", typeof(Array).Assembly.GetName())
            && method.Parameters is [IParameterSymbol { Type: IArrayTypeSymbol }, IParameterSymbol { Type: INamedTypeSymbol comparer }]
            && TypeKey(comparer) == "System.Collections.Generic.IComparer`1";

        private bool IsExactCampaignArraySort(
            IMethodSymbol method,
            IInvocationOperation invocation,
            SemanticModel model,
            AuthoredMember? context)
        {
            // ArraySortHelper in runtime v10.0.12 calls comparer.Compare; no caller comparer, default
            // IComparable, or virtual element getter is justified by the Array.Sort name alone.
            // https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Private.CoreLib/src/System/Collections/Generic/ArraySortHelper.cs
            return IsCampaignComparerSortOverload(method)
                && method.TypeArguments is [ITypeSymbol element]
                && method.Parameters is [IParameterSymbol { Type: IArrayTypeSymbol array }, IParameterSymbol { Type: INamedTypeSymbol comparer }]
                && SymbolEqualityComparer.Default.Equals(array.ElementType, element)
                && TypeKey(comparer) == "System.Collections.Generic.IComparer`1"
                && IsCampaignScalarSortElement(element)
                && invocation.Arguments.SingleOrDefault(static argument => argument.Parameter?.Ordinal == 1) is { } argument
                && !HasCampaignAuthoredValueConversion(argument.Value)
                && argument.Value.Syntax is ExpressionSyntax expression
                && !HasRejectedCampaignEncodingValueSource(expression, model,
                    new HashSet<ISymbol>(SymbolEqualityComparer.Default), context, inspectComparer: true)
                && HasOwnedCampaignComparer(expression, model, new HashSet<ISymbol>(SymbolEqualityComparer.Default), context, element);
        }

        private bool IsCampaignScalarSortElement(ITypeSymbol element) =>
            IsCampaignScalarSortElement(element, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default));

        private bool IsCampaignScalarSortElement(ITypeSymbol element, HashSet<ITypeSymbol> path)
        {
            if (element.TypeKind == TypeKind.Enum
                || element.SpecialType is SpecialType.System_Boolean or SpecialType.System_Byte or SpecialType.System_SByte
                    or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32
                    or SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Char or SpecialType.System_String
                || IsExactFrameworkType(element, "System.Guid", typeof(Guid).Assembly.GetName()))
            {
                return true;
            }

            if (!path.Add(element))
            {
                return false;
            }

            try
            {
                if (element is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T, TypeArguments: [ITypeSymbol underlying] })
                {
                    return IsCampaignScalarSortElement(underlying, path);
                }

                if (element is INamedTypeSymbol { IsTupleType: true } tuple)
                {
                    return tuple.TupleElements.All(field => IsCampaignScalarSortElement(field.Type, path));
                }

                return element is INamedTypeSymbol { IsRecord: true, IsSealed: true, TypeKind: TypeKind.Class } record
                    && record.GetMembers().OfType<IPropertySymbol>()
                        .Where(static property => !property.IsStatic && property.DeclaredAccessibility == Accessibility.Public)
                        .All(property => PositionalRecordConstructorParameter(property) is not null
                            && IsCampaignScalarSortElement(property.Type, path));
            }
            finally
            {
                path.Remove(element);
            }
        }

        private bool HasOwnedCampaignComparer(
            ExpressionSyntax expression,
            SemanticModel model,
            HashSet<ISymbol> path,
            AuthoredMember? context,
            ITypeSymbol element)
        {
            if (!TryStripCampaignEncodingValue(expression, model, out expression))
            {
                return false;
            }

            if (model.GetOperation(expression) is IObjectCreationOperation
                {
                    Constructor.ContainingType: { IsSealed: true } comparer,
                    Initializer: null,
                }
                && comparer.Locations.Any(static location => location.IsInSource)
                && comparer.AllInterfaces.SingleOrDefault(contract => TypeKey(contract) == "System.Collections.Generic.IComparer`1"
                    && SymbolEqualityComparer.Default.Equals(contract.TypeArguments[0], element)) is { } contract
                && contract.GetMembers("Compare").OfType<IMethodSymbol>().SingleOrDefault() is { } comparison
                && comparer.FindImplementationForInterfaceMember(comparison) is IMethodSymbol implementation
                && Resolve(implementation, model.Compilation) is { } authored)
            {
                return !ContainsPotentialProducerSite(authored)
                    && !HasCampaignComparerPropertyEffects(authored);
            }

            return HasOnlyReviewedValueSources(expression, model, path, context,
                (source, sourceModel, sourcePath, sourceContext) =>
                    HasOwnedCampaignComparer(source, sourceModel, sourcePath, sourceContext, element));
        }

        private bool HasRejectedCampaignEncodingValueSource(
            ExpressionSyntax expression,
            SemanticModel model,
            HashSet<ISymbol> path,
            AuthoredMember? context,
            bool inspectComparer)
        {
            if (!TryStripCampaignEncodingValue(expression, model, out expression)
                || model.GetOperation(expression)?.ConstantValue is { HasValue: true, Value: null })
            {
                return true;
            }

            if (inspectComparer
                && model.GetTypeInfo(expression).Type is INamedTypeSymbol { IsSealed: true } comparer)
            {
                foreach (INamedTypeSymbol contract in comparer.AllInterfaces
                    .Where(static contract => TypeKey(contract) == "System.Collections.Generic.IComparer`1"))
                {
                    if (contract.GetMembers("Compare").OfType<IMethodSymbol>().SingleOrDefault() is { } comparison
                        && comparer.FindImplementationForInterfaceMember(comparison) is IMethodSymbol implementation
                        && Resolve(implementation, model.Compilation) is { } authored
                        && HasCampaignComparerPropertyEffects(authored))
                    {
                        return true;
                    }
                }
            }

            ISymbol? storage = model.GetSymbolInfo(expression).Symbol;

            if (storage is null || !path.Add(storage))
            {
                return false;
            }

            try
            {
                if (storage is IParameterSymbol
                    && context?.ValueBindings?.TryGetValue(storage, out BoundValueSource? bound) == true
                    && bound is not null)
                {
                    return HasRejectedCampaignEncodingValueSource(bound.Expression, bound.Caller.Model,
                        path, bound.Caller, inspectComparer);
                }

                ExpressionSyntax[] sources = storage.DeclaringSyntaxReferences
                    .Select(static reference => reference.GetSyntax())
                    .Select(static declaration => declaration switch
                    {
                        VariableDeclaratorSyntax variable => variable.Initializer?.Value,
                        PropertyDeclarationSyntax property => property.Initializer?.Value ?? property.ExpressionBody?.Expression,
                        _ => null,
                    })
                    .OfType<ExpressionSyntax>()
                    .ToArray();

                if (storage is ILocalSymbol)
                {
                    sources = sources.Concat(model.SyntaxTree.GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>()
                        .Where(assignment => SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(assignment.Left).Symbol, storage))
                        .Select(static assignment => assignment.Right)).ToArray();
                }

                // Unlike the generic source tracer, retain null/default initializers. For Sort they
                // activate default IComparable callbacks; for TryParse they select CurrentCulture.
                return sources.Any(source => HasRejectedCampaignEncodingValueSource(source,
                    semanticModels[source.SyntaxTree], path, context, inspectComparer));
            }
            finally
            {
                path.Remove(storage);
            }
        }

        private bool HasCampaignComparerPropertyEffects(AuthoredMember member)
        {
            GraphMemberIdentity identity = MemberIdentity(member);

            if (!campaignComparerPropertyReview.Add(identity))
            {
                return false;
            }

            try
            {
                foreach (SyntaxNode node in member.Syntax.DescendantNodesAndSelf(candidate => candidate == member.Syntax
                    || candidate is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax))
                {
                    if (member.Model.GetSymbolInfo(node).Symbol is IMethodSymbol method)
                    {
                        // Resolving a pure authored base body does not resolve its runtime override.
                        // The Campaign comparers have no such opaque helpers: sealed containing types
                        // and sealed overrides provide exact dispatch; an open slot remains unknown.
                        if (method.Locations.Any(static location => location.IsInSource)
                            && (method.IsAbstract
                                || (method.IsVirtual || method.IsOverride)
                                    && !method.IsSealed
                                    && !method.ContainingType.IsSealed))
                        {
                            return true;
                        }

                        if (Resolve(method, member.Model.Compilation) is { } target
                            && HasCampaignComparerPropertyEffects(target))
                        {
                            return true;
                        }
                    }

                    if (member.Model.GetSymbolInfo(node).Symbol is not IPropertySymbol property)
                    {
                        continue;
                    }

                    foreach (IMethodSymbol accessor in new[] { property.GetMethod, property.SetMethod }.OfType<IMethodSymbol>())
                    {
                        if (accessor.Locations.Any(static location => location.IsInSource)
                            && (accessor.IsAbstract
                                || accessor.IsVirtual && !property.ContainingType.IsSealed))
                        {
                            return true;
                        }

                        if (Resolve(accessor, member.Model.Compilation) is { } body
                            && body.Syntax is not AccessorDeclarationSyntax { Body: null, ExpressionBody: null }
                            && (ContainsPotentialProducerSite(body) || HasCampaignComparerPropertyEffects(body)))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
            finally
            {
                campaignComparerPropertyReview.Remove(identity);
            }
        }

        private static bool HasCampaignAuthoredValueConversion(IOperation operation)
        {
            while (operation is IConversionOperation conversion)
            {
                if (conversion.OperatorMethod is not null)
                {
                    return true;
                }

                operation = conversion.Operand;
            }

            return false;
        }

        private static bool TryStripCampaignEncodingValue(ExpressionSyntax expression, SemanticModel model, out ExpressionSyntax value)
        {
            value = expression;

            while (true)
            {
                if (model.GetConversion(value).IsUserDefined
                    || model.GetOperation(value) is { } operation && HasCampaignAuthoredValueConversion(operation))
                {
                    return false;
                }

                ExpressionSyntax next = StripTransparentExpression(value);

                if (next == value)
                {
                    return true;
                }

                // Inspect each cast individually: recursively stripping the full chain would hide
                // an intermediate authored conversion that changes the actual callback receiver.
                value = value switch
                {
                    ParenthesizedExpressionSyntax parenthesized => parenthesized.Expression,
                    CastExpressionSyntax cast => cast.Expression,
                    PostfixUnaryExpressionSyntax suppression => suppression.Operand,
                    BinaryExpressionSyntax coalesce => coalesce.Left,
                    _ => next,
                };
            }
        }

        private bool HasOnlyCampaignStorageUses(
            ISymbol storage,
            SemanticModel model,
            Func<IdentifierNameSyntax, SemanticModel, bool> reviewedUse)
        {
            IEnumerable<SyntaxNode> roots = storage is ILocalSymbol
                ? [model.SyntaxTree.GetRoot()]
                : storage.ContainingType?.DeclaringSyntaxReferences.Select(static reference => reference.GetSyntax()) ?? [];

            foreach (SyntaxNode root in roots)
            {
                SemanticModel partModel = semanticModels[root.SyntaxTree];

                foreach (IdentifierNameSyntax reference in root.DescendantNodes().OfType<IdentifierNameSyntax>()
                    .Where(reference => SymbolEqualityComparer.Default.Equals(partModel.GetSymbolInfo(reference).Symbol, storage)))
                {
                    if (reference.Parent is AssignmentExpressionSyntax assignment
                        && assignment.Left == reference
                        && assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                        && storage is ILocalSymbol)
                    {
                        continue;
                    }

                    if (!reviewedUse(reference, partModel))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static IInvocationOperation? CampaignStorageReceiverCall(IdentifierNameSyntax reference, SemanticModel model)
        {
            ExpressionSyntax expression = reference;

            if (expression.Parent is MemberAccessExpressionSyntax qualification
                && qualification.Name == expression)
            {
                expression = qualification;
            }

            while (expression.Parent is ParenthesizedExpressionSyntax
                or CastExpressionSyntax
                or PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression })
            {
                expression = (ExpressionSyntax)expression.Parent;
            }

            return expression.Parent is MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax call } access
                && access.Expression == expression
                && model.GetOperation(call) is IInvocationOperation invocation
                    ? invocation
                    : null;
        }
    }
}
