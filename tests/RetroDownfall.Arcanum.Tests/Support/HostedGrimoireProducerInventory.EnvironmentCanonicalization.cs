using System.Globalization;

using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    // The identity a constant expression has in an evaluation environment, or null when the expression is not a constant
    // this analysis can spell exactly.
    internal static string? ProbeConstantValueIdentity(CSharpCompilation compilation, ExpressionSyntax expression)
    {
        SemanticModel model = compilation.GetSemanticModel(expression.SyntaxTree);

        IMethodSymbol owner = model.GetEnclosingSymbol(expression.SpanStart) as IMethodSymbol
            ?? throw new InvalidOperationException("The expression must sit in a method body.");

        SyntaxNode body = expression.Ancestors().First(static ancestor => ancestor is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax);

        return ProducerGraph.TryConstantValueIdentity(new(new(owner, body, model), expression), out string identity) ? identity : null;
    }

    // Whether the member declared at the node, a method, an accessor, a lambda or a local function, can be declared inside
    // a method and so can name that method's parameters.
    internal static bool ProbeMemberCanBeNestedInAMethod(CSharpCompilation compilation, SyntaxNode declaration)
    {
        SemanticModel model = compilation.GetSemanticModel(declaration.SyntaxTree);

        ISymbol? declared = declaration switch
        {
            AnonymousFunctionExpressionSyntax => model.GetSymbolInfo(declaration).Symbol,
            ArrowExpressionClauseSyntax { Parent: PropertyDeclarationSyntax property } => (model.GetDeclaredSymbol(property) as IPropertySymbol)?.GetMethod,
            _ => model.GetDeclaredSymbol(declaration),
        };

        IMethodSymbol symbol = declared as IMethodSymbol ?? throw new InvalidOperationException("The node must declare a method.");

        return !ProducerGraph.MemberCannotBeNestedInAMethod(new(symbol, declaration, model));
    }

    // Whether a member the traversal enters with an admitted origin bound to the given parameter holds an admission seed, and whether
    // it may contain an admission origin. The two existential questions that read every admission binding of a member.
    internal static (bool Seed, bool MayContain) ProbeAdmissionBinding(CSharpCompilation compilation, SyntaxNode memberDeclaration, ParameterSyntax boundParameter)
    {
        SemanticModel model = compilation.GetSemanticModel(memberDeclaration.SyntaxTree);

        ISymbol? declared = memberDeclaration switch
        {
            AnonymousFunctionExpressionSyntax => model.GetSymbolInfo(memberDeclaration).Symbol,
            ArrowExpressionClauseSyntax { Parent: PropertyDeclarationSyntax property } => (model.GetDeclaredSymbol(property) as IPropertySymbol)?.GetMethod,
            _ => model.GetDeclaredSymbol(memberDeclaration),
        };

        IMethodSymbol symbol = declared as IMethodSymbol ?? throw new InvalidOperationException("The node must declare a method.");

        IParameterSymbol parameter = model.GetDeclaredSymbol(boundParameter) ?? throw new InvalidOperationException("The node must declare a parameter.");

        AuthoredMember member = new(symbol, memberDeclaration, model)
        {
            AdmissionBindings = new Dictionary<ISymbol, IReadOnlySet<string>>(SymbolEqualityComparer.Default)
            {
                [parameter] = new HashSet<string> { "work@probe" },
            },
        };

        ProducerGraph graph = new([compilation], DefaultEvaluationEnvironmentMaximumMembers, DefaultEvaluationEnvironmentMaximumDepth, DefaultTraversalMaximumDepth, DefaultMaximumAnalyzedStatesPerRoot, false);

        return graph.ProbeAdmissionBinding(member);
    }

    private sealed partial class ProducerGraph
    {
        internal (bool Seed, bool MayContain) ProbeAdmissionBinding(AuthoredMember member) =>
            (HasAdmissionSeed(member), MayContainAdmissionOrigin(member));

        // Constant Value Identity. A bound value whose normalized source is a compile-time constant stands for its value
        // wherever the constant was written, because every consumer of such a source evaluates it (a branch condition, a
        // selected overload, a finite enumeration) and none reads where it came from: a constant has no side effects, depends
        // on no caller binding, and folds to the same value from any call site. Two environments that differ only in the
        // position of a constant therefore describe the same evaluation, and the identity of the environment names the
        // constant's value and type instead of the position and the caller. A source that is not a constant keeps the
        // position, the syntax fingerprint and the caller's projected environment, exactly as before, and so does a
        // constant whose value this method cannot spell exactly. The environment itself still holds the first caller's
        // source, so a walk that follows a bound value to its caller must not follow a constant: the search for the owner of
        // an inherited per-unit group (PerUnitPhysicalGroupSharesWork) skips it, because a constant carries no instance.
        internal static bool TryConstantValueIdentity(BoundValueSource source, out string identity)
        {
            identity = "";

            SemanticModel model = source.Caller.Model;

            if (source.Expression.SyntaxTree != model.SyntaxTree)
            {
                return false;
            }

            Optional<object?> constant = model.GetConstantValue(source.Expression);

            if (!constant.HasValue)
            {
                return false;
            }

            string? value = constant.Value switch
            {
                null => "null",
                string text => "string:" + text,
                char character => "char:" + ((int)character).ToString(CultureInfo.InvariantCulture),
                bool flag => flag ? "bool:true" : "bool:false",
                float single => "float:" + single.ToString("R", CultureInfo.InvariantCulture),
                double number => "double:" + number.ToString("R", CultureInfo.InvariantCulture),
                sbyte or byte or short or ushort or int or uint or long or ulong or decimal =>
                    constant.Value.GetType().Name + ":" + Convert.ToString(constant.Value, CultureInfo.InvariantCulture),
                _ => null,
            };

            if (value is null)
            {
                return false;
            }

            TypeInfo type = model.GetTypeInfo(source.Expression);

            identity = value
                + "|"
                + (type.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? "none")
                + "|"
                + (type.ConvertedType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? "none");

            return true;
        }

        private readonly Dictionary<GraphMemberIdentity, IReadOnlySet<ISymbol>> inertPayloadParameters = [];

        // Inert Payload Parameter. A parameter whose type is a type parameter carries a payload the member only moves: every
        // use of it in the member is the argument of a call to an authored member that moves it in turn, or the right side of a
        // plain assignment to a field of this. A payload that is only moved is never the receiver of a call, never converted,
        // tested, cast, disposed or compared, so what the traversal could learn from the concrete type a caller bound for it
        // is nothing: the type decides no dispatch and no cleanup anywhere below. Its concrete binding would still split the
        // member into one state per payload type, and `Result<T>.Success` into one per type a result carries. The concrete
        // binding is dropped from the environment; the admission, callable and value bindings of the parameter are not touched.
        private IReadOnlySet<ISymbol> InertPayloadParameters(AuthoredMember member)
        {
            if (member.ConcreteBindings is not { Count: > 0 })
            {
                return EmptySymbolSet;
            }

            GraphMemberIdentity identity = MemberIdentity(member);

            if (inertPayloadParameters.TryGetValue(identity, out IReadOnlySet<ISymbol>? known))
            {
                return known;
            }

            HashSet<ISymbol> inert = new(SymbolEqualityComparer.Default);

            foreach (IParameterSymbol parameter in member.Symbol.Parameters)
            {
                if (parameter is { Type: ITypeParameterSymbol, RefKind: RefKind.None }
                    && ParameterIsOnlyMoved(member, parameter, [], 0))
                {
                    inert.Add(parameter);
                }
            }

            inertPayloadParameters[identity] = inert;

            return inert;
        }

        private static readonly IReadOnlySet<ISymbol> EmptySymbolSet = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        private bool ParameterIsOnlyMoved(AuthoredMember member, IParameterSymbol parameter, HashSet<(GraphMemberIdentity Member, int Parameter)> path, int depth)
        {
            if (depth > 4 || member.Syntax is not (MethodDeclarationSyntax or ConstructorDeclarationSyntax or ConversionOperatorDeclarationSyntax or OperatorDeclarationSyntax)
                || !path.Add((MemberIdentity(member), parameter.Ordinal)))
            {
                return false;
            }

            try
            {
                bool referenced = false;

                foreach (IdentifierNameSyntax name in member.Syntax.DescendantNodes().OfType<IdentifierNameSyntax>())
                {
                    if (!SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(name).Symbol, parameter))
                    {
                        continue;
                    }

                    referenced = true;

                    if (member.Model.GetOperation(name) is not IParameterReferenceOperation reference || !UseMovesOnly(member, reference, path, depth))
                    {
                        return false;
                    }
                }

                return referenced;
            }
            finally
            {
                path.Remove((MemberIdentity(member), parameter.Ordinal));
            }
        }

        private bool UseMovesOnly(AuthoredMember member, IParameterReferenceOperation reference, HashSet<(GraphMemberIdentity Member, int Parameter)> path, int depth)
        {
            switch (reference.Parent)
            {
                case ISimpleAssignmentOperation { Target: IFieldReferenceOperation { Field.IsStatic: false, Instance: IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance } }, Parent: IExpressionStatementOperation } assignment:
                    return ReferenceEquals(assignment.Value, reference);

                case IArgumentOperation { ArgumentKind: ArgumentKind.Explicit, Parameter: { RefKind: RefKind.None } formal } argument
                    when ReferenceEquals(argument.Value, reference) && argument.InConversion.IsIdentity && argument.OutConversion.IsIdentity:
                    IMethodSymbol? called = argument.Parent switch
                    {
                        IInvocationOperation { IsVirtual: false } invocation => invocation.TargetMethod,
                        IObjectCreationOperation creation => creation.Constructor,
                        _ => null,
                    };

                    return called is not null
                        && formal.Ordinal < called.Parameters.Length
                        && Resolve(called, member.Model.Compilation) is { } callee
                        && ParameterIsOnlyMoved(callee, callee.Symbol.Parameters[formal.Ordinal], path, depth + 1);

                default:
                    return false;
            }
        }

        // Token Identity. The exact System.Threading.CancellationToken carries no cleanup obligation, no ownership and no
        // authority. Copying one hands on a reference to a source that only the source itself can dispose, so a token is
        // never what a cleanup resolves against, a lambda that captures one needs no more than the lambda already has, and
        // where a token came from decides nothing the traversal reads. A parameter of this type is therefore never bound to its
        // source, which would split the states of every helper that forwards a token by every call chain that supplied it.
        // A source (CancellationTokenSource) and a registration (CancellationTokenRegistration) are disposable, are different
        // types and keep their provenance.
        private static bool IsExactCancellationToken(ITypeSymbol? type) =>
            IsExactFrameworkType(type, "System.Threading.CancellationToken", typeof(CancellationToken).Assembly.GetName());

        // Non-Owning Provider Observation. A call on a disposable parameter makes the parameter's source part of its callee's
        // environment, because the call could be the disposal itself or hand the parameter's ownership on. DbCommand.CreateParameter
        // is neither: it returns a fresh parameter that belongs to no command until the caller adds it, and the framework body
        // of the slot (DbCommand's own, or SqliteCommand's) neither disposes nor keeps its receiver. An authored override could
        // do either, so the observation is trusted only while the DbProviderTypesClosedByUse premise holds, which proves that
        // no authored or referenced type derives from a provider type. Without the premise the call keeps its demand.
        private bool IsTrustedNonOwningProviderObservation(IMethodSymbol method)
        {
            IMethodSymbol definition = method.ReducedFrom ?? method;

            return DbProviderTypesClosedByUse
                && definition is { MethodKind: MethodKind.Ordinary, IsStatic: false, Name: "CreateParameter", Parameters.Length: 0 }
                && TypeKey(definition.ContainingType) is "System.Data.Common.DbCommand" or "Microsoft.Data.Sqlite.SqliteCommand"
                && !definition.Locations.Any(static location => location.IsInSource)
                && IsTrustedProviderAssembly(definition.ContainingAssembly);
        }

        // Foreign Parameter Projection. A parameter is in scope only in the method that declares it and in the lambdas and
        // local functions declared inside that method. A member that cannot be nested in a method (an accessor, a
        // constructor, an operator, a conversion, an explicit interface implementation) therefore can neither name a
        // parameter of an ordinary method nor run code that does, so a binding for such a parameter is dead in its
        // environment. It reaches the member only because an instance call passes its caller's whole context on to the
        // receiver's members, and keeping it multiplies the member's states by every distinct binding of every caller
        // parameter above it. A lambda or a local function keeps every binding, since it may be nested in the owner. Two
        // questions read every admission binding of a member and not the one a name selects (HasAdmissionSeed and
        // MayContainAdmissionOrigin); they ignore this binding by the same rule (HasLiveAdmissionOrigin), so the answer does
        // not depend on whether the projection ran.
        internal static bool MemberCannotBeNestedInAMethod(AuthoredMember member) =>
            member.Symbol.MethodKind is not (MethodKind.LocalFunction or MethodKind.AnonymousFunction);

        // The two questions that read every admission binding of a member (whether it holds an admission seed and whether it may
        // contain an admission origin) are existential, so a binding the member cannot reach must not answer them. A parameter of
        // an ordinary method other than the member itself is such a binding in a member that cannot be nested in a method: the
        // projection drops it before the traversal asks, and asking by this rule gives the same answer for a member that was
        // not projected, which also covers a callback body that inherited the context of the member it was declared in.
        private static bool IsForeignOrdinaryParameter(AuthoredMember member, ISymbol symbol) =>
            MemberCannotBeNestedInAMethod(member)
            && symbol is IParameterSymbol { ContainingSymbol: IMethodSymbol { MethodKind: MethodKind.Ordinary } owner }
            && !SymbolEqualityComparer.Default.Equals(owner.OriginalDefinition, member.Symbol.OriginalDefinition);

        private static bool HasLiveAdmissionOrigin(AuthoredMember member) =>
            member.AdmissionBindings?.Any(pair => pair.Value.Count > 0 && !IsForeignOrdinaryParameter(member, pair.Key)) == true;
    }
}
