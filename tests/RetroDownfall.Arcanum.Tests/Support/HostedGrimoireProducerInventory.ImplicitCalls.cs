using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.CodeAnalysis.Operations;

using System.Collections.Immutable;

using System.Reflection;

using RetroDownfall.Arcanum.Infrastructure.Data;

// The collection expression placeholder kind is experimental in the pinned Roslyn; the census classifies every kind
// the enumeration declares, so it names the kind on purpose.
#pragma warning disable RSEXPERIMENTAL006

namespace RetroDownfall.Arcanum.Tests.Support;

// The Implicit Call Census. C# runs user-written code at many places where no invocation is written: a
// deconstruction calls Deconstruct, a collection initializer calls Add, an await calls GetAwaiter, IsCompleted and
// GetResult, an interpolated string handler calls its constructor and Append members, a with-expression calls the
// record's copy constructor, a pattern reads properties and calls Deconstruct, Length and the indexer, and so on.
// Each of those used to need a lane of its own, and every missed one was a silent soundness hole. The census closes
// them by construction: for every member body the traversal analyses it walks the body's IOperation tree and lists
// every operation that binds a method, a property or an event on the compiler's behalf, leaves alone the ones an
// existing syntax lane already traverses at that site, and for the rest either traverses the selected member where
// the compiler runs it, under the frontiers live there, or refuses the site. The list of operation kinds and of
// symbol-carrying operation members is pinned against the Roslyn the tests compile with, so a kind or a member that
// a Roslyn upgrade adds fails loudly instead of being silently admitted.
internal static partial class HostedGrimoireProducerInventory
{
    internal enum OperationCensusRole : byte
    {
        // The operation binds no member of its own, and every operation under it is inspected separately.
        Structure = 1,

        // Every call the operation carries is dispatched by a syntax lane of the traversal at its own node; the
        // census confirms that per instance and takes over the instances the lane does not see.
        Lane = 2,

        // The operation carries calls no syntax lane sees, and the census traverses or refuses them.
        Census = 3,

        // The operation has no exact target to follow, so it is refused.
        Refused = 4,
    }

    internal enum ImplicitCallKind : byte
    {
        CollectionElementAdd = 1,

        CollectionExpressionConstruction = 2,

        InterpolationHandler = 3,

        ConstructorInitializer = 4,

        QueryClause = 5,

        UnnamedInvocation = 6,

        UnnamedObjectCreation = 7,

        Operator = 8,

        Conversion = 9,

        Deconstruct = 10,

        Awaiter = 11,

        PatternMember = 12,

        ImplicitIndexer = 13,

        WithClone = 14,

        FormatHole = 15,

        Pinnable = 16,

        Unsupported = 17,

        ConstructionEntry = 18,
    }

    // One member the compiler calls: the selected method (a property access is its accessor), the value it is
    // called on when that value is written, its static type, the written arguments that bind to its parameters,
    // and the source position at which the call has begun.
    internal sealed record ImplicitCallStep(
        string Label,
        IMethodSymbol Method,
        ITypeSymbol? ReceiverType,
        ExpressionSyntax? Receiver,
        IReadOnlyList<(ExpressionSyntax Expression, IParameterSymbol Parameter)> Arguments,
        int Position);

    // One operation that binds members implicitly. A refusal means the operation has no exact target to follow.
    internal sealed record ImplicitCall(
        ImplicitCallKind Kind,
        OperationKind Source,
        SyntaxNode Anchor,
        IReadOnlyList<ImplicitCallStep> Steps,
        string? RefusalCode = null,
        string? RefusalDetail = null,
        IReadOnlyList<SyntaxNode>? Callbacks = null);

    // The obsolete kind cannot be named in source (its use is an error), so it is read from the enumeration by name.
    private static readonly OperationKind ObsoleteCollectionElementInitializerKind = Enum.Parse<OperationKind>("CollectionElementInitializer");

    private static (OperationCensusRole Role, string Reason) Census(OperationCensusRole role, string reason) => (role, reason);

    // Every OperationKind the pinned Roslyn declares (the Visual Basic and flow-graph kinds included) and the way the
    // census treats it. The test pins this table against Enum.GetValues so that a new kind has to be classified here.
    internal static readonly IReadOnlyDictionary<OperationKind, (OperationCensusRole Role, string Reason)> OperationKindCensus =
        new Dictionary<OperationKind, (OperationCensusRole Role, string Reason)>
        {
            [OperationKind.None] = Census(OperationCensusRole.Census, "An operation Roslyn does not model (fixed, stackalloc, pointer access): a fixed statement calls GetPinnableReference, the rest call nothing, anything else is refused."),
            [OperationKind.Invalid] = Census(OperationCensusRole.Refused, "An erroneous operation has no exact target."),
            [OperationKind.Block] = Census(OperationCensusRole.Structure, "Statement structure."),
            [OperationKind.VariableDeclarationGroup] = Census(OperationCensusRole.Structure, "Declaration structure."),
            [OperationKind.Switch] = Census(OperationCensusRole.Structure, "Statement structure; its case patterns and values are inspected separately."),
            [OperationKind.Loop] = Census(OperationCensusRole.Lane, "foreach binds the enumeration members and the iteration variable's Deconstruct and conversions (Implicit Enumeration); the other loops are structure."),
            [OperationKind.Labeled] = Census(OperationCensusRole.Structure, "Statement structure."),
            [OperationKind.Branch] = Census(OperationCensusRole.Structure, "Statement structure."),
            [OperationKind.Empty] = Census(OperationCensusRole.Structure, "Statement structure."),
            [OperationKind.Return] = Census(OperationCensusRole.Structure, "Statement structure; a contextual conversion is its own operation."),
            [OperationKind.YieldBreak] = Census(OperationCensusRole.Structure, "Iterator structure."),
            [OperationKind.Lock] = Census(OperationCensusRole.Census, "lock calls Monitor or System.Threading.Lock, both framework code; a lookalike System.Threading.Lock declared in source is refused."),
            [OperationKind.Try] = Census(OperationCensusRole.Structure, "Statement structure."),
            [OperationKind.Using] = Census(OperationCensusRole.Lane, "The resource's Dispose or DisposeAsync is resolved by the cleanup lanes of the using statement and the using declaration."),
            [OperationKind.YieldReturn] = Census(OperationCensusRole.Structure, "Iterator structure."),
            [OperationKind.ExpressionStatement] = Census(OperationCensusRole.Structure, "Statement structure."),
            [OperationKind.LocalFunction] = Census(OperationCensusRole.Structure, "Declares a callable whose body runs where it is called."),
            [OperationKind.Stop] = Census(OperationCensusRole.Structure, "Visual Basic only."),
            [OperationKind.End] = Census(OperationCensusRole.Structure, "Visual Basic only."),
            [OperationKind.RaiseEvent] = Census(OperationCensusRole.Structure, "Visual Basic only."),
            [OperationKind.Literal] = Census(OperationCensusRole.Structure, "A constant."),
            [OperationKind.Conversion] = Census(OperationCensusRole.Lane, "A user-defined conversion is dispatched by the conversion lane; one that lane does not see is taken over by the census."),
            [OperationKind.Invocation] = Census(OperationCensusRole.Lane, "A written invocation is dispatched by the invocation lane; an invocation with any other syntax (collection initializer Add, interpolated string append, constructor initializer, query clause) is taken over by the census."),
            [OperationKind.ArrayElementReference] = Census(OperationCensusRole.Structure, "A built-in array access."),
            [OperationKind.LocalReference] = Census(OperationCensusRole.Structure, "A variable read."),
            [OperationKind.ParameterReference] = Census(OperationCensusRole.Structure, "A variable read."),
            [OperationKind.FieldReference] = Census(OperationCensusRole.Structure, "A field read runs no code."),
            [OperationKind.MethodReference] = Census(OperationCensusRole.Structure, "A method group runs no code until the delegate it converts to is invoked."),
            [OperationKind.PropertyReference] = Census(OperationCensusRole.Lane, "An accessor call is dispatched by the property lane at the identifier, member access or element access."),
            [OperationKind.EventReference] = Census(OperationCensusRole.Lane, "An event accessor is dispatched by the event lane."),
            [OperationKind.Unary] = Census(OperationCensusRole.Lane, "A written unary operator is dispatched by the operator lane; the true and false operators of a condition have no operator syntax and are taken over by the census."),
            [OperationKind.Binary] = Census(OperationCensusRole.Lane, "A written binary operator is dispatched by the operator lane; the formatting of a string concatenation operand is taken over by the census."),
            [OperationKind.Conditional] = Census(OperationCensusRole.Structure, "Control structure; a user-defined condition is its own operation."),
            [OperationKind.Coalesce] = Census(OperationCensusRole.Census, "The left operand's user-defined value conversion."),
            [OperationKind.AnonymousFunction] = Census(OperationCensusRole.Structure, "Creating a lambda runs no code."),
            [OperationKind.ObjectCreation] = Census(OperationCensusRole.Lane, "A written creation is dispatched by the creation lane; the construction entry (field and property initializers, the base constructor), an interpolated string handler's constructor and any other implicit creation are taken over by the census."),
            [OperationKind.TypeParameterObjectCreation] = Census(OperationCensusRole.Refused, "new T() runs the constructor of whatever T is."),
            [OperationKind.ArrayCreation] = Census(OperationCensusRole.Structure, "A built-in array allocation."),
            [OperationKind.InstanceReference] = Census(OperationCensusRole.Structure, "A this or implicit receiver reference."),
            [OperationKind.IsType] = Census(OperationCensusRole.Structure, "A type test."),
            [OperationKind.Await] = Census(OperationCensusRole.Census, "GetAwaiter, IsCompleted, GetResult and OnCompleted or UnsafeOnCompleted of the awaitable."),
            [OperationKind.SimpleAssignment] = Census(OperationCensusRole.Structure, "An assignment; a property target is the property lane's and a contextual conversion is its own operation."),
            [OperationKind.CompoundAssignment] = Census(OperationCensusRole.Census, "The operator the compound assignment selected, and its conversions of the operands and of the result."),
            [OperationKind.Parenthesized] = Census(OperationCensusRole.Structure, "Syntax structure."),
            [OperationKind.EventAssignment] = Census(OperationCensusRole.Lane, "An event add or remove is dispatched by the event lane."),
            [OperationKind.ConditionalAccess] = Census(OperationCensusRole.Structure, "Control structure."),
            [OperationKind.ConditionalAccessInstance] = Census(OperationCensusRole.Structure, "Control structure."),
            [OperationKind.InterpolatedString] = Census(OperationCensusRole.Census, "The implicit formatting of every hole."),
            [OperationKind.AnonymousObjectCreation] = Census(OperationCensusRole.Structure, "An anonymous type has only compiler-generated members."),
            [OperationKind.ObjectOrCollectionInitializer] = Census(OperationCensusRole.Structure, "Initializer structure; the Add calls and the member assignments are inspected separately."),
            [OperationKind.MemberInitializer] = Census(OperationCensusRole.Structure, "Initializer structure."),
            [ObsoleteCollectionElementInitializerKind] = Census(OperationCensusRole.Refused, "The obsolete operation Roslyn replaced with an invocation; it is never produced and is refused if it is."),
            [OperationKind.NameOf] = Census(OperationCensusRole.Structure, "A constant."),
            [OperationKind.Tuple] = Census(OperationCensusRole.Structure, "A value tuple construction."),
            [OperationKind.DynamicObjectCreation] = Census(OperationCensusRole.Refused, "A dynamic creation binds at run time."),
            [OperationKind.DynamicMemberReference] = Census(OperationCensusRole.Refused, "A dynamic member access binds at run time."),
            [OperationKind.DynamicInvocation] = Census(OperationCensusRole.Lane, "Refused by the invocation lane."),
            [OperationKind.DynamicIndexerAccess] = Census(OperationCensusRole.Lane, "Refused by the indexer lane."),
            [OperationKind.TranslatedQuery] = Census(OperationCensusRole.Census, "A query expression calls the framework query operators with lazy callbacks."),
            [OperationKind.DelegateCreation] = Census(OperationCensusRole.Structure, "Creating a delegate runs no code."),
            [OperationKind.DefaultValue] = Census(OperationCensusRole.Structure, "A constant."),
            [OperationKind.TypeOf] = Census(OperationCensusRole.Structure, "A constant."),
            [OperationKind.SizeOf] = Census(OperationCensusRole.Structure, "A constant."),
            [OperationKind.AddressOf] = Census(OperationCensusRole.Structure, "A pointer operation."),
            [OperationKind.IsPattern] = Census(OperationCensusRole.Structure, "A pattern test; the pattern is inspected separately."),
            [OperationKind.Increment] = Census(OperationCensusRole.Census, "The increment operator the operation selected."),
            [OperationKind.Throw] = Census(OperationCensusRole.Structure, "A throw; the exception creation is its own operation."),
            [OperationKind.Decrement] = Census(OperationCensusRole.Census, "The decrement operator the operation selected."),
            [OperationKind.DeconstructionAssignment] = Census(OperationCensusRole.Census, "The Deconstruct member of each deconstructed value, nested ones included, and the conversions of the deconstructed elements."),
            [OperationKind.DeclarationExpression] = Census(OperationCensusRole.Structure, "Declaration structure."),
            [OperationKind.OmittedArgument] = Census(OperationCensusRole.Structure, "Visual Basic only."),
            [OperationKind.FieldInitializer] = Census(OperationCensusRole.Structure, "A field initializer is analysed with the construction that runs it."),
            [OperationKind.VariableInitializer] = Census(OperationCensusRole.Structure, "Declaration structure."),
            [OperationKind.PropertyInitializer] = Census(OperationCensusRole.Structure, "A property initializer is analysed with the construction that runs it."),
            [OperationKind.ParameterInitializer] = Census(OperationCensusRole.Structure, "A default argument is a constant."),
            [OperationKind.ArrayInitializer] = Census(OperationCensusRole.Structure, "Initializer structure."),
            [OperationKind.VariableDeclarator] = Census(OperationCensusRole.Structure, "Declaration structure."),
            [OperationKind.VariableDeclaration] = Census(OperationCensusRole.Structure, "Declaration structure."),
            [OperationKind.Argument] = Census(OperationCensusRole.Census, "An argument's in and out conversions (Visual Basic only); the argument itself is structure."),
            [OperationKind.CatchClause] = Census(OperationCensusRole.Structure, "Statement structure."),
            [OperationKind.SwitchCase] = Census(OperationCensusRole.Structure, "Statement structure."),
            [OperationKind.CaseClause] = Census(OperationCensusRole.Structure, "Case structure; a pattern clause's pattern is inspected separately."),
            [OperationKind.InterpolatedStringText] = Census(OperationCensusRole.Structure, "A literal part."),
            [OperationKind.Interpolation] = Census(OperationCensusRole.Census, "The implicit formatting of the hole's value."),
            [OperationKind.ConstantPattern] = Census(OperationCensusRole.Structure, "A constant comparison."),
            [OperationKind.DeclarationPattern] = Census(OperationCensusRole.Structure, "A type test and a variable."),
            [OperationKind.TupleBinary] = Census(OperationCensusRole.Census, "A tuple comparison calls the operator of every pair of elements."),
            [OperationKind.MethodBody] = Census(OperationCensusRole.Structure, "Member structure."),
            [OperationKind.ConstructorBody] = Census(OperationCensusRole.Structure, "Member structure; the constructor initializer is an invocation inspected separately."),
            [OperationKind.Discard] = Census(OperationCensusRole.Structure, "A discard."),
            [OperationKind.FlowCapture] = Census(OperationCensusRole.Structure, "A control-flow graph operation that never appears in an operation tree."),
            [OperationKind.FlowCaptureReference] = Census(OperationCensusRole.Structure, "A control-flow graph operation that never appears in an operation tree."),
            [OperationKind.IsNull] = Census(OperationCensusRole.Structure, "A control-flow graph operation that never appears in an operation tree."),
            [OperationKind.CaughtException] = Census(OperationCensusRole.Structure, "A control-flow graph operation that never appears in an operation tree."),
            [OperationKind.StaticLocalInitializationSemaphore] = Census(OperationCensusRole.Structure, "A control-flow graph operation that never appears in an operation tree."),
            [OperationKind.FlowAnonymousFunction] = Census(OperationCensusRole.Structure, "A control-flow graph operation that never appears in an operation tree."),
            [OperationKind.CoalesceAssignment] = Census(OperationCensusRole.Structure, "A null-coalescing assignment; the right operand's conversion is its own operation."),
            [OperationKind.Range] = Census(OperationCensusRole.Census, "The System.Range member that builds the range."),
            [OperationKind.ReDim] = Census(OperationCensusRole.Structure, "Visual Basic only."),
            [OperationKind.ReDimClause] = Census(OperationCensusRole.Structure, "Visual Basic only."),
            [OperationKind.RecursivePattern] = Census(OperationCensusRole.Census, "A positional pattern calls Deconstruct; its property subpatterns are dispatched by the property lane."),
            [OperationKind.DiscardPattern] = Census(OperationCensusRole.Structure, "A pattern that matches anything."),
            [OperationKind.SwitchExpression] = Census(OperationCensusRole.Structure, "Control structure; its arm patterns are inspected separately."),
            [OperationKind.SwitchExpressionArm] = Census(OperationCensusRole.Structure, "Control structure."),
            [OperationKind.PropertySubpattern] = Census(OperationCensusRole.Lane, "The property read is dispatched by the property lane at the member name."),
            [OperationKind.UsingDeclaration] = Census(OperationCensusRole.Lane, "The resource's Dispose or DisposeAsync is resolved by the cleanup lane of the using declaration."),
            [OperationKind.NegatedPattern] = Census(OperationCensusRole.Structure, "Pattern structure."),
            [OperationKind.BinaryPattern] = Census(OperationCensusRole.Structure, "Pattern structure."),
            [OperationKind.TypePattern] = Census(OperationCensusRole.Structure, "A type test."),
            [OperationKind.RelationalPattern] = Census(OperationCensusRole.Structure, "A built-in comparison."),
            [OperationKind.With] = Census(OperationCensusRole.Census, "A with-expression calls the record's clone, which calls its copy constructor."),
            [OperationKind.InterpolatedStringHandlerCreation] = Census(OperationCensusRole.Census, "The handler's constructor and Append members are operations under it, inspected separately."),
            [OperationKind.InterpolatedStringAddition] = Census(OperationCensusRole.Structure, "Interpolated string structure."),
            [OperationKind.InterpolatedStringAppendLiteral] = Census(OperationCensusRole.Census, "The handler's AppendLiteral call is an invocation inspected separately."),
            [OperationKind.InterpolatedStringAppendFormatted] = Census(OperationCensusRole.Census, "The handler's AppendFormatted call is an invocation inspected separately; a framework handler formats the value itself."),
            [OperationKind.InterpolatedStringAppendInvalid] = Census(OperationCensusRole.Refused, "An erroneous append has no exact target."),
            [OperationKind.InterpolatedStringHandlerArgumentPlaceholder] = Census(OperationCensusRole.Structure, "A handler argument placeholder."),
            [OperationKind.FunctionPointerInvocation] = Census(OperationCensusRole.Census, "A function pointer call has no exact target."),
            [OperationKind.ListPattern] = Census(OperationCensusRole.Census, "A list pattern reads Length and the indexer of its input."),
            [OperationKind.SlicePattern] = Census(OperationCensusRole.Census, "A slice pattern calls Slice or the range indexer of its input."),
            [OperationKind.ImplicitIndexerReference] = Census(OperationCensusRole.Census, "An index or range access reads Length and calls the indexer or Slice."),
            [OperationKind.Utf8String] = Census(OperationCensusRole.Structure, "A constant."),
            [OperationKind.Attribute] = Census(OperationCensusRole.Structure, "Attribute syntax is not executed."),
            [OperationKind.InlineArrayAccess] = Census(OperationCensusRole.Structure, "An access into a compiler-synthesized inline array span."),
            [OperationKind.CollectionExpression] = Census(OperationCensusRole.Census, "A collection expression calls the target's constructor or builder and its Add."),
            [OperationKind.Spread] = Census(OperationCensusRole.Lane, "A spread enumerates its operand (Implicit Enumeration)."),
            [OperationKind.CollectionExpressionElementsPlaceholder] = Census(OperationCensusRole.Structure, "A collection expression placeholder."),
        };

    // Every public property of a Roslyn operation interface that carries a symbol or a conversion, with the way the
    // census treats it. The test pins this table against reflection over the interfaces, so a member Roslyn adds has
    // to be reviewed here before it can carry a call nobody modeled.
    internal static readonly IReadOnlyDictionary<string, string> OperationSymbolCarriers = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["IAnonymousFunctionOperation.Symbol"] = "NoCall: the lambda itself.",
        ["IArgumentOperation.Parameter"] = "NoCall: the bound parameter.",
        ["IArgumentOperation.InConversion"] = "Census: a user-defined conversion of an argument (Visual Basic).",
        ["IArgumentOperation.OutConversion"] = "Census: a user-defined conversion of an argument (Visual Basic).",
        ["IBinaryOperation.OperatorMethod"] = "Lane: BinaryExpression syntax; Census: any other syntax.",
        ["IBinaryOperation.ConstrainedToType"] = "NoCall: the type of a static abstract operator's constraint.",
        ["IBranchOperation.Target"] = "NoCall: a label.",
        ["ICaseClauseOperation.Label"] = "NoCall: a label.",
        ["ICatchClauseOperation.ExceptionType"] = "NoCall: a type.",
        ["ICoalesceOperation.ValueConversion"] = "Census: the left operand's value conversion.",
        ["ICollectionElementInitializerOperation.AddMethod"] = "Refused: the obsolete operation.",
        ["ICollectionExpressionOperation.ConstructMethod"] = "Census: the constructor or builder of the target.",
        ["ICompoundAssignmentOperation.InConversion"] = "Census: the conversion of the target before the operator.",
        ["ICompoundAssignmentOperation.OutConversion"] = "Census: the conversion of the result after the operator.",
        ["ICompoundAssignmentOperation.OperatorMethod"] = "Census: the compound operator.",
        ["ICompoundAssignmentOperation.ConstrainedToType"] = "NoCall: the type of a static abstract operator's constraint.",
        ["IConversionOperation.OperatorMethod"] = "Lane: the conversion lane; Census: a conversion that lane does not see.",
        ["IConversionOperation.ConstrainedToType"] = "NoCall: the type of a static abstract operator's constraint.",
        ["IConversionOperation.Conversion"] = "Lane: the conversion lane; Census: a conversion that lane does not see.",
        ["IDeclarationPatternOperation.MatchedType"] = "NoCall: a type.",
        ["IDeclarationPatternOperation.DeclaredSymbol"] = "NoCall: a variable.",
        ["IDiscardOperation.DiscardSymbol"] = "NoCall: a discard.",
        ["IDynamicMemberReferenceOperation.ContainingType"] = "Refused: a dynamic member access binds at run time.",
        ["IEventReferenceOperation.Event"] = "Lane: the event lane.",
        ["IFieldReferenceOperation.Field"] = "NoCall: a field read runs no code.",
        ["IImplicitIndexerReferenceOperation.LengthSymbol"] = "Census: the Length or Count read.",
        ["IImplicitIndexerReferenceOperation.IndexerSymbol"] = "Lane: the element access lane resolves the indexer or Slice; Census: the length read.",
        ["IIncrementOrDecrementOperation.OperatorMethod"] = "Census: the increment or decrement operator.",
        ["IIncrementOrDecrementOperation.ConstrainedToType"] = "NoCall: the type of a static abstract operator's constraint.",
        ["IInvocationOperation.TargetMethod"] = "Lane: InvocationExpression syntax; Census: any other syntax.",
        ["IInvocationOperation.ConstrainedToType"] = "NoCall: the type of a constrained call.",
        ["IIsTypeOperation.TypeOperand"] = "NoCall: a type.",
        ["ILabeledOperation.Label"] = "NoCall: a label.",
        ["IListPatternOperation.LengthSymbol"] = "Census: the Length or Count read.",
        ["IListPatternOperation.IndexerSymbol"] = "Census: the indexer read.",
        ["IListPatternOperation.DeclaredSymbol"] = "NoCall: a variable.",
        ["ILocalFunctionOperation.Symbol"] = "NoCall: the local function itself.",
        ["ILocalReferenceOperation.Local"] = "NoCall: a variable.",
        ["ILoopOperation.ContinueLabel"] = "NoCall: a label.",
        ["ILoopOperation.ExitLabel"] = "NoCall: a label.",
        ["IMemberReferenceOperation.Member"] = "NoCall: the member a derived reference names; each derived kind is classified on its own.",
        ["IMemberReferenceOperation.ConstrainedToType"] = "NoCall: the type of a constrained call.",
        ["IMethodReferenceOperation.Method"] = "NoCall: a method group runs no code.",
        ["IObjectCreationOperation.Constructor"] = "Lane: ObjectCreationExpression syntax; Census: any other syntax.",
        ["IParameterInitializerOperation.Parameter"] = "NoCall: a default argument.",
        ["IParameterReferenceOperation.Parameter"] = "NoCall: a variable.",
        ["IPatternCaseClauseOperation.Label"] = "NoCall: a label.",
        ["IPatternOperation.InputType"] = "NoCall: a type.",
        ["IPatternOperation.NarrowedType"] = "NoCall: a type.",
        ["IPropertyReferenceOperation.Property"] = "Lane: the property lane.",
        ["IRangeOperation.Method"] = "Census: the System.Range factory.",
        ["IRecursivePatternOperation.MatchedType"] = "NoCall: a type.",
        ["IRecursivePatternOperation.DeconstructSymbol"] = "Census: the Deconstruct member.",
        ["IRecursivePatternOperation.DeclaredSymbol"] = "NoCall: a variable.",
        ["ISizeOfOperation.TypeOperand"] = "NoCall: a type.",
        ["ISlicePatternOperation.SliceSymbol"] = "Census: the Slice member.",
        ["ISpreadOperation.ElementType"] = "NoCall: a type.",
        ["ISpreadOperation.ElementConversion"] = "Lane: Implicit Enumeration.",
        ["ISwitchOperation.ExitLabel"] = "NoCall: a label.",
        ["ITryOperation.ExitLabel"] = "NoCall: a label.",
        ["ITupleOperation.NaturalType"] = "NoCall: a type.",
        ["ITypeOfOperation.TypeOperand"] = "NoCall: a type.",
        ["ITypePatternOperation.MatchedType"] = "NoCall: a type.",
        ["IUnaryOperation.OperatorMethod"] = "Lane: PrefixUnaryExpression syntax; Census: any other syntax.",
        ["IUnaryOperation.ConstrainedToType"] = "NoCall: the type of a static abstract operator's constraint.",
        ["IVariableDeclaratorOperation.Symbol"] = "NoCall: a variable.",
        ["IWithOperation.CloneMethod"] = "Census: the record clone.",
    };

    // The symbol-carrying properties of the Roslyn operation interfaces, read by reflection, for the pin test.
    internal static IReadOnlyList<string> RoslynOperationSymbolCarriers() =>
    [
        .. typeof(IOperation).Assembly.GetExportedTypes()
            .Where(static type => type.IsInterface && type.Namespace == "Microsoft.CodeAnalysis.Operations" && typeof(IOperation).IsAssignableFrom(type))
            .SelectMany(static type => type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(static property => typeof(ISymbol).IsAssignableFrom(property.PropertyType) || property.PropertyType == typeof(CommonConversion))
                .Select(property => type.Name + "." + property.Name))
            .Order(StringComparer.Ordinal),
    ];

    internal static IReadOnlyList<ImplicitCall> CollectImplicitCalls(SemanticModel model, SyntaxNode region) =>
        ProducerGraph.CollectImplicitCalls(model, region);

    private sealed partial class ProducerGraph
    {
        private static int After(SyntaxNode node) => node.Span.End + 1;

        private static IOperation UnwrapImplicitConversions(IOperation operation)
        {
            while (operation is IConversionOperation { IsImplicit: true, OperatorMethod: null } conversion && !conversion.Conversion.IsUserDefined)
            {
                operation = conversion.Operand;
            }

            return operation;
        }

        private static IReadOnlyList<(ExpressionSyntax Expression, IParameterSymbol Parameter)> WrittenArguments(IEnumerable<IArgumentOperation> arguments) =>
        [
            .. arguments
                .Where(static argument => !argument.IsImplicit && argument.Parameter is not null && argument.Value.Syntax is ExpressionSyntax)
                .Select(static argument => ((ExpressionSyntax)argument.Value.Syntax, argument.Parameter!)),
        ];

        // True when a conversion operation is one the conversion lane reaches from the conversion's own syntax node.
        private static bool ConversionLaneSees(SemanticModel model, IConversionOperation conversion)
        {
            if (conversion.Syntax is not ExpressionSyntax)
            {
                return false;
            }

            for (IOperation? operation = model.GetOperation(conversion.Syntax); operation is not null; operation = operation.Parent)
            {
                if (ReferenceEquals(operation, conversion))
                {
                    return true;
                }

                if (operation.Parent is not (IConversionOperation or IParenthesizedOperation))
                {
                    return false;
                }
            }

            return false;
        }

        private static bool HasAncestor<TOperation>(IOperation operation)
            where TOperation : IOperation
        {
            for (IOperation? ancestor = operation.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                if (ancestor is TOperation)
                {
                    return true;
                }
            }

            return false;
        }

        private static ImplicitCallStep Step(string label, IMethodSymbol method, ITypeSymbol? receiverType, ExpressionSyntax? receiver, int position, IReadOnlyList<(ExpressionSyntax Expression, IParameterSymbol Parameter)>? arguments = null) =>
            new(label, method, receiverType, receiver, arguments ?? [], position);

        // The walk of one member body, with the pruning of the syntax walk: a lambda or a local function written in
        // the body is not entered, because its body runs where it is called and is analysed there.
        internal static IReadOnlyList<ImplicitCall> CollectImplicitCalls(SemanticModel model, SyntaxNode region)
        {
            List<ImplicitCall> calls = [];

            if (model.GetOperation(region) is not { } root)
            {
                return calls;
            }

            void Add(ImplicitCallKind kind, IOperation operation, SyntaxNode anchor, IReadOnlyList<ImplicitCallStep> steps) =>
                calls.Add(new(kind, operation.Kind, anchor, steps));

            void Refuse(IOperation operation, string code, string detail, ImplicitCallKind kind = ImplicitCallKind.Unsupported) =>
                calls.Add(new(kind, operation.Kind, operation.Syntax, [], code, detail));

            // The implicit lambdas a query expression passes to the framework query operators: their bodies are the
            // clause expressions, which run when the query result is consumed.
            static IEnumerable<IOperation> ImplicitLambdas(IOperation query) =>
                query.ChildOperations.SelectMany(static child => child.DescendantsAndSelf()).OfType<IAnonymousFunctionOperation>().Where(static lambda => lambda.Syntax is not AnonymousFunctionExpressionSyntax);

            void Walk(IOperation operation)
            {
                if (operation is ITranslatedQueryOperation query)
                {
                    calls.Add(new(ImplicitCallKind.QueryClause, query.Kind, query.Syntax, [], null, null, [.. ImplicitLambdas(query).Select(static lambda => lambda.Syntax)]));
                }

                Inspect(model, operation, Add, Refuse);

                // The argument of nameof is never evaluated.
                if (operation is INameOfOperation)
                {
                    return;
                }

                foreach (IOperation child in operation.ChildOperations)
                {
                    if (child is IAnonymousFunctionOperation { Syntax: AnonymousFunctionExpressionSyntax } or ILocalFunctionOperation)
                    {
                        continue;
                    }

                    Walk(child);
                }
            }

            Walk(root);

            return calls;
        }

        private static void Inspect(
            SemanticModel model,
            IOperation operation,
            Action<ImplicitCallKind, IOperation, SyntaxNode, IReadOnlyList<ImplicitCallStep>> add,
            Action<IOperation, string, string, ImplicitCallKind> refuse)
        {
            void RefuseSite(string detail) => refuse(operation, "HOSTED_SITE_UNCLASSIFIED", detail, ImplicitCallKind.Unsupported);

            void RefuseTarget(string detail) => refuse(operation, "HOSTED_CALL_TARGET_UNRESOLVED", detail, ImplicitCallKind.Unsupported);

            // An operator or a conversion over a dynamic operand binds at run time, and the one it binds can be any
            // authored one.
            if (BindsAtRunTime(operation))
            {
                RefuseTarget("A dynamic operand binds " + operation.Kind + " at run time.");
            }

            switch (operation)
            {
                case IInvocationOperation invocation:
                    InspectInvocation(model, invocation, add, RefuseSite);

                    break;

                case IObjectCreationOperation creation:
                    InspectCreation(creation, add);

                    break;

                case IUnaryOperation unary:
                    if (unary.OperatorMethod is { MethodKind: not MethodKind.BuiltinOperator } unaryMethod
                        && !(unary.Syntax is PrefixUnaryExpressionSyntax && !unary.IsImplicit))
                    {
                        add(ImplicitCallKind.Operator, operation, unary.Syntax, [Step("operator", unaryMethod, null, null, After(unary.Syntax), OperandArguments(unaryMethod, unary.Operand))]);
                    }

                    break;

                case IBinaryOperation binary:
                    if (binary.OperatorMethod is { MethodKind: not MethodKind.BuiltinOperator } binaryMethod
                        && !(binary.Syntax is BinaryExpressionSyntax && !binary.IsImplicit))
                    {
                        add(ImplicitCallKind.Operator, operation, binary.Syntax, [Step("operator", binaryMethod, null, null, After(binary.Syntax), OperandArguments(binaryMethod, binary.LeftOperand, binary.RightOperand))]);
                    }

                    if (binary.OperatorKind == BinaryOperatorKind.Add && binary.Type?.SpecialType == SpecialType.System_String)
                    {
                        foreach (IOperation concatenated in new[] { binary.LeftOperand, binary.RightOperand })
                        {
                            FormatHole(model, concatenated, binary.Syntax, add, RefuseSite);
                        }
                    }

                    break;

                case ICompoundAssignmentOperation compound:
                    InspectCompoundAssignment(model, compound, add, RefuseSite);

                    break;

                case IIncrementOrDecrementOperation increment:
                    if (increment.OperatorMethod is { MethodKind: not MethodKind.BuiltinOperator } incrementMethod)
                    {
                        add(ImplicitCallKind.Operator, operation, increment.Syntax, [Step("operator", incrementMethod, null, null, After(increment.Syntax))]);
                    }

                    break;

                case ITupleBinaryOperation tupleBinary:
                    InspectTupleBinary(model, tupleBinary, add, RefuseTarget);

                    break;

                case ICoalesceOperation coalesce:
                    if (coalesce.ValueConversion.MethodSymbol is { } coalesceConversion)
                    {
                        add(ImplicitCallKind.Conversion, operation, coalesce.Value.Syntax, [Step("conversion", coalesceConversion, null, null, After(coalesce.Value.Syntax), OperandArguments(coalesceConversion, coalesce.Value))]);
                    }

                    break;

                case IConversionOperation conversion:
                    if ((conversion.OperatorMethod ?? conversion.Conversion.MethodSymbol) is { MethodKind: not MethodKind.BuiltinOperator } conversionMethod
                        && !ConversionLaneSees(model, conversion))
                    {
                        add(ImplicitCallKind.Conversion, operation, conversion.Syntax, [Step("conversion", conversionMethod, null, null, After(conversion.Operand.Syntax), OperandArguments(conversionMethod, conversion.Operand))]);
                    }

                    break;

                case IArgumentOperation argument:
                    foreach (CommonConversion conversionOfArgument in new[] { argument.InConversion, argument.OutConversion })
                    {
                        if (conversionOfArgument.MethodSymbol is { } argumentConversion)
                        {
                            add(ImplicitCallKind.Conversion, operation, argument.Syntax, [Step("conversion", argumentConversion, null, null, After(argument.Syntax), OperandArguments(argumentConversion, argument.Value))]);
                        }
                    }

                    break;

                case IDeconstructionAssignmentOperation deconstruction:
                    if (deconstruction.Syntax is AssignmentExpressionSyntax assignment)
                    {
                        List<ImplicitCallStep> steps = [];

                        DeconstructionSteps(model.GetDeconstructionInfo(assignment), model.GetTypeInfo(assignment.Right).Type, assignment.Right, After(assignment.Right), steps);

                        if (steps.Count != 0)
                        {
                            add(ImplicitCallKind.Deconstruct, operation, assignment, steps);
                        }
                    }
                    else
                    {
                        RefuseSite("A deconstruction written other than an assignment has no exact target.");
                    }

                    break;

                case IAwaitOperation await:
                    InspectAwait(model, await, add, RefuseTarget);

                    break;

                case ILockOperation lockOperation:
                    if (lockOperation.LockedValue.Type is { } lockedType
                        && lockedType.SpecialType == SpecialType.None
                        && TypeKey(lockedType) == "System.Threading.Lock"
                        && lockedType.Locations.Any(static location => location.IsInSource))
                    {
                        RefuseSite("System.Threading.Lock declared in source; its EnterScope and Dispose are authored code.");
                    }

                    break;

                case IWithOperation with:
                    if (with.CloneMethod is { } clone)
                    {
                        add(ImplicitCallKind.WithClone, operation, with.Syntax, [Step("clone", clone, with.Operand.Type, with.Operand.Syntax as ExpressionSyntax, After(with.Operand.Syntax))]);
                    }

                    break;

                case IRecursivePatternOperation recursive:
                    if (recursive.DeconstructSymbol is IMethodSymbol deconstruct)
                    {
                        add(ImplicitCallKind.PatternMember, operation, recursive.Syntax, [Step("deconstruct", deconstruct.ReducedFrom ?? deconstruct, recursive.MatchedType, null, After(recursive.Syntax))]);
                    }

                    break;

                case IListPatternOperation list:
                    {
                        List<ImplicitCallStep> steps = [];

                        AccessorStep(steps, "length", list.LengthSymbol, list.InputType, After(list.Syntax), write: false);

                        AccessorStep(steps, "indexer", list.IndexerSymbol, list.InputType, After(list.Syntax), write: false);

                        if (steps.Count != 0)
                        {
                            add(ImplicitCallKind.PatternMember, operation, list.Syntax, steps);
                        }
                    }

                    break;

                case ISlicePatternOperation slice:
                    {
                        List<ImplicitCallStep> steps = [];

                        AccessorStep(steps, "slice", slice.SliceSymbol, (slice.Parent as IListPatternOperation)?.InputType, After(slice.Syntax), write: false);

                        if (steps.Count != 0)
                        {
                            add(ImplicitCallKind.PatternMember, operation, slice.Syntax, steps);
                        }
                    }

                    break;

                case IImplicitIndexerReferenceOperation indexer:
                    {
                        List<ImplicitCallStep> steps = [];

                        AccessorStep(steps, "length", indexer.LengthSymbol, indexer.Instance.Type, After(indexer.Instance.Syntax), write: false);

                        if (steps.Count != 0)
                        {
                            add(ImplicitCallKind.ImplicitIndexer, operation, indexer.Syntax, steps);
                        }
                    }

                    break;

                case IRangeOperation range:
                    if (range.Method is { } rangeMethod)
                    {
                        add(ImplicitCallKind.ImplicitIndexer, operation, range.Syntax, [Step("range", rangeMethod, null, null, After(range.Syntax))]);
                    }

                    break;

                case IInterpolationOperation interpolation:
                    FormatHole(model, interpolation.Expression, interpolation.Syntax, add, RefuseSite);

                    break;

                case ICollectionExpressionOperation collection:
                    InspectCollectionExpression(model, collection, add);

                    break;

                case ITypeParameterObjectCreationOperation:
                    RefuseSite("new T() runs the constructor of whatever type argument the caller supplies.");

                    break;

                case IDynamicObjectCreationOperation:
                    RefuseTarget("Dynamic object creation has no exact call target.");

                    break;

                case IDynamicMemberReferenceOperation dynamicMember:
                    // The callee of a dynamic invocation is refused by the invocation lane at the invocation.
                    if (!(dynamicMember.Parent is IDynamicInvocationOperation dynamicInvocation && ReferenceEquals(dynamicInvocation.Operation, dynamicMember)))
                    {
                        RefuseTarget("Dynamic member access has no exact call target.");
                    }

                    break;

                case IFunctionPointerInvocationOperation:
                    RefuseTarget("A function pointer call has no exact call target.");

                    break;

                case ITranslatedQueryOperation:
                    // The callbacks of a query expression are collected by the walk, which sees the whole subtree.
                    break;

                case IInvalidOperation invalid:
                    // An invocation that binds to no method is refused by the invocation lane at the invocation itself
                    // (HOSTED_CALL_TARGET_UNRESOLVED); every other erroneous operation has no lane and is refused here.
                    if (!(invalid.Syntax is InvocationExpressionSyntax unresolvedCall && model.GetSymbolInfo(unresolvedCall).Symbol is null))
                    {
                        RefuseSite("An erroneous operation has no exact target.");
                    }

                    break;

                default:
                    InspectUnmodeled(model, operation, add, RefuseSite);

                    break;
            }

            if (operation.Kind == OperationKind.InterpolatedStringAppendInvalid)
            {
                RefuseSite("An erroneous interpolated string append has no exact target.");
            }

            if (operation.Kind == ObsoleteCollectionElementInitializerKind)
            {
                RefuseSite("The obsolete collection element initializer operation has no exact target.");
            }
        }

        // The written operands of an operator or a conversion, paired with the parameters of the method they bind to.
        private static bool IsDynamic(IOperation? operation) => operation?.Type?.TypeKind == TypeKind.Dynamic;

        // Whether the operation selects its operator or conversion at run time because an operand is dynamic (C# does
        // not deconstruct a dynamic value). The dynamic invocation, member access, indexer and creation have lanes or refusals of their own.
        private static bool BindsAtRunTime(IOperation operation) => operation switch
        {
            IUnaryOperation { OperatorMethod: null } unary => IsDynamic(unary.Operand),
            IBinaryOperation { OperatorMethod: null } binary => IsDynamic(binary.LeftOperand) || IsDynamic(binary.RightOperand),
            ICompoundAssignmentOperation { OperatorMethod: null } compound => IsDynamic(compound.Target) || IsDynamic(compound.Value),
            IIncrementOrDecrementOperation { OperatorMethod: null } increment => IsDynamic(increment.Target),
            IConversionOperation { OperatorMethod: null } conversion => IsDynamic(conversion.Operand)
                && conversion.Type is { } target
                && target.TypeKind != TypeKind.Dynamic
                && target.SpecialType != SpecialType.System_Object,
            _ => false,
        };

        private static IReadOnlyList<(ExpressionSyntax Expression, IParameterSymbol Parameter)> OperandArguments(IMethodSymbol method, params IOperation[] operands)
        {
            IMethodSymbol selected = method.ReducedFrom ?? method;

            List<(ExpressionSyntax Expression, IParameterSymbol Parameter)> arguments = [];

            for (int index = 0; index < operands.Length && index < selected.Parameters.Length; index++)
            {
                if (operands[index].Syntax is ExpressionSyntax expression)
                {
                    arguments.Add((expression, selected.Parameters[index]));
                }
            }

            return arguments;
        }

        private static void InspectInvocation(
            SemanticModel model,
            IInvocationOperation invocation,
            Action<ImplicitCallKind, IOperation, SyntaxNode, IReadOnlyList<ImplicitCallStep>> add,
            Action<string> refuse)
        {
            // A written invocation is the invocation lane's, and the framework query operator a query clause calls is
            // judged with the query's callbacks; an Add or an append written inside a clause is an invocation of its own.
            if (invocation.Syntax is InvocationExpressionSyntax or QueryClauseSyntax or SelectOrGroupClauseSyntax or QueryBodySyntax or QueryExpressionSyntax)
            {
                return;
            }

            ImplicitCallKind kind;

            string label;

            if (invocation.Syntax is ConstructorInitializerSyntax)
            {
                kind = ImplicitCallKind.ConstructorInitializer;

                label = "constructor-initializer";
            }
            else if (invocation.Parent is IInterpolatedStringAppendOperation)
            {
                kind = ImplicitCallKind.InterpolationHandler;

                label = "append";
            }
            else if (invocation.Parent is IObjectOrCollectionInitializerOperation)
            {
                kind = ImplicitCallKind.CollectionElementAdd;

                label = "add";
            }
            else
            {
                kind = ImplicitCallKind.UnnamedInvocation;

                label = "invoke";
            }

            // A framework handler formats the value it appends itself, so the formatting is a call of the append.
            if (kind == ImplicitCallKind.InterpolationHandler
                && invocation.TargetMethod.Name == "AppendFormatted"
                && !invocation.TargetMethod.ContainingType.Locations.Any(static location => location.IsInSource)
                && invocation.Arguments.FirstOrDefault() is { } appended)
            {
                FormatHole(model, appended.Value, invocation.Syntax, add, refuse);
            }

            ExpressionSyntax? receiver = invocation.Instance is { IsImplicit: false } instance ? instance.Syntax as ExpressionSyntax : null;

            add(kind, invocation, invocation.Syntax, [Step(label, invocation.TargetMethod, invocation.Instance?.Type, receiver, After(invocation.Syntax), WrittenArguments(invocation.Arguments))]);
        }

        private static void InspectCreation(IObjectCreationOperation creation, Action<ImplicitCallKind, IOperation, SyntaxNode, IReadOnlyList<ImplicitCallStep>> add)
        {
            if (creation.Constructor is not { } constructor)
            {
                return;
            }

            // A written creation of a class or record whose constructor has no declaration of its own (the implicit
            // one, or the primary constructor) runs the construction entry: the instance initializers and the base
            // constructor. A constructor declared with a body gets the same entry when it is traversed.
            if (creation.Syntax is BaseObjectCreationExpressionSyntax)
            {
                if ((constructor.IsImplicitlyDeclared || constructor.DeclaringSyntaxReferences.Any(static reference => reference.GetSyntax() is TypeDeclarationSyntax))
                    && constructor.ContainingType is { TypeKind: TypeKind.Class } type
                    && type.Locations.Any(static location => location.IsInSource))
                {
                    add(ImplicitCallKind.ConstructionEntry, creation, creation.Syntax, [Step("construct", constructor, creation.Type, null, After(creation.Syntax), WrittenArguments(creation.Arguments))]);
                }

                return;
            }

            ImplicitCallKind kind = creation.Parent is IInterpolatedStringHandlerCreationOperation ? ImplicitCallKind.InterpolationHandler : ImplicitCallKind.UnnamedObjectCreation;

            add(kind, creation, creation.Syntax, [Step("construct", constructor, creation.Type, null, creation.Syntax.SpanStart, WrittenArguments(creation.Arguments))]);
        }

        private static void InspectCompoundAssignment(
            SemanticModel model,
            ICompoundAssignmentOperation compound,
            Action<ImplicitCallKind, IOperation, SyntaxNode, IReadOnlyList<ImplicitCallStep>> add,
            Action<string> refuse)
        {
            // s += value concatenates the formatting of the value.
            if (compound.OperatorKind == BinaryOperatorKind.Add && compound.Type?.SpecialType == SpecialType.System_String)
            {
                FormatHole(model, compound.Value, compound.Syntax, add, refuse);
            }

            List<ImplicitCallStep> steps = [];

            int position = After(compound.Syntax);

            if (compound.InConversion.MethodSymbol is { } inConversion)
            {
                steps.Add(Step("conversion", inConversion, null, null, position));
            }

            if (compound.OperatorMethod is { MethodKind: not MethodKind.BuiltinOperator } method)
            {
                steps.Add(Step("operator", method, null, null, position));
            }

            if (compound.OutConversion.MethodSymbol is { } outConversion)
            {
                steps.Add(Step("conversion", outConversion, null, null, position));
            }

            if (steps.Count != 0)
            {
                add(ImplicitCallKind.Operator, compound, compound.Syntax, steps);
            }
        }

        // A tuple comparison calls the operator of every pair of elements. Roslyn does not expose them, so every
        // user-defined equality operator an element type declares, along its base types, is listed: an operator the
        // comparison does not select is a conservative addition, never an omission.
        private static void InspectTupleBinary(
            SemanticModel model,
            ITupleBinaryOperation tuple,
            Action<ImplicitCallKind, IOperation, SyntaxNode, IReadOnlyList<ImplicitCallStep>> add,
            Action<string> refuse)
        {
            string name = tuple.OperatorKind == BinaryOperatorKind.NotEquals ? "op_Inequality" : "op_Equality";

            List<ImplicitCallStep> steps = [];

            bool unresolved = false;

            void Pair(ITypeSymbol? left, ITypeSymbol? right)
            {
                if (left is null || right is null || left.TypeKind is TypeKind.Error or TypeKind.Dynamic or TypeKind.TypeParameter || right.TypeKind is TypeKind.Error or TypeKind.Dynamic or TypeKind.TypeParameter)
                {
                    unresolved = true;

                    return;
                }

                if (left is INamedTypeSymbol { IsTupleType: true } leftTuple && right is INamedTypeSymbol { IsTupleType: true } rightTuple && leftTuple.TupleElements.Length == rightTuple.TupleElements.Length)
                {
                    for (int index = 0; index < leftTuple.TupleElements.Length; index++)
                    {
                        Pair(leftTuple.TupleElements[index].Type, rightTuple.TupleElements[index].Type);
                    }

                    return;
                }

                foreach (ITypeSymbol side in new[] { left, right })
                {
                    ITypeSymbol type = side is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable ? nullable.TypeArguments[0] : side;

                    for (ITypeSymbol? current = type; current is not null; current = current.BaseType)
                    {
                        foreach (IMethodSymbol candidate in current.GetMembers(name).OfType<IMethodSymbol>())
                        {
                            if (candidate.MethodKind == MethodKind.UserDefinedOperator
                                && candidate.Parameters.Length == 2
                                && model.Compilation.ClassifyConversion(left, candidate.Parameters[0].Type).IsImplicit
                                && model.Compilation.ClassifyConversion(right, candidate.Parameters[1].Type).IsImplicit
                                && steps.All(existing => !SymbolEqualityComparer.Default.Equals(existing.Method, candidate)))
                            {
                                steps.Add(Step("operator", candidate, null, null, After(tuple.Syntax)));
                            }
                        }
                    }
                }
            }

            Pair(tuple.LeftOperand.Type, tuple.RightOperand.Type);

            if (unresolved)
            {
                refuse("A tuple comparison over an element type that cannot be followed has no exact operator targets.");
            }

            if (steps.Count != 0)
            {
                add(ImplicitCallKind.Operator, tuple, tuple.Syntax, steps);
            }
        }

        // The members an await calls on its awaitable and on the awaiter it returns.
        private static void InspectAwait(
            SemanticModel model,
            IAwaitOperation await,
            Action<ImplicitCallKind, IOperation, SyntaxNode, IReadOnlyList<ImplicitCallStep>> add,
            Action<string> refuse)
        {
            if (await.Syntax is not AwaitExpressionSyntax syntax)
            {
                refuse("An await written other than an await expression has no exact awaiter.");

                return;
            }

            AwaitExpressionInfo info = model.GetAwaitExpressionInfo(syntax);

            if (info.IsDynamic)
            {
                refuse("A dynamic await binds its awaiter at run time.");

                return;
            }

            if (info.GetAwaiterMethod is not { } getAwaiter || info.IsCompletedProperty?.GetMethod is not { } isCompleted || info.GetResultMethod is not { } getResult)
            {
                refuse("The await pattern members could not be resolved.");

                return;
            }

            ITypeSymbol awaiter = getAwaiter.ReturnType;

            int beforeSuspension = After(syntax.Expression);

            List<ImplicitCallStep> steps =
            [
                Step("get-awaiter", getAwaiter.ReducedFrom ?? getAwaiter, model.GetTypeInfo(syntax.Expression).Type, syntax.Expression, beforeSuspension),
                Step("is-completed", isCompleted, awaiter, null, beforeSuspension),
                Step("get-result", getResult, awaiter, null, After(syntax)),
            ];

            INamedTypeSymbol? critical = awaiter.AllInterfaces.FirstOrDefault(static contract => TypeKey(contract) == "System.Runtime.CompilerServices.ICriticalNotifyCompletion");

            INamedTypeSymbol? notify = awaiter.AllInterfaces.FirstOrDefault(static contract => TypeKey(contract) == "System.Runtime.CompilerServices.INotifyCompletion");

            if ((critical ?? notify) is { } completion && completion.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(static method => method.Name is "OnCompleted" or "UnsafeOnCompleted") is { } onCompleted)
            {
                steps.Add(Step("on-completed", onCompleted, awaiter, null, beforeSuspension));
            }

            add(ImplicitCallKind.Awaiter, await, syntax, steps);
        }

        // Deconstruct and the conversions of a deconstruction, nested deconstructions included.
        internal static void DeconstructionSteps(
            DeconstructionInfo info,
            ITypeSymbol? type,
            ExpressionSyntax? value,
            int position,
            List<ImplicitCallStep> steps)
        {
            IMethodSymbol? method = info.Method is { } found ? found.ReducedFrom ?? found : null;

            if (method is not null)
            {
                steps.Add(Step("deconstruct", method, type, value, position));
            }

            if (info.Conversion is { MethodSymbol: { } conversion })
            {
                steps.Add(Step("conversion", conversion, null, null, position));
            }

            for (int index = 0; index < info.Nested.Length; index++)
            {
                ITypeSymbol? elementType = null;

                ExpressionSyntax? element = null;

                if (method is not null)
                {
                    int ordinal = index + (method.IsStatic ? 1 : 0);

                    if (ordinal < method.Parameters.Length)
                    {
                        elementType = method.Parameters[ordinal].Type;
                    }
                }
                else if (type is INamedTypeSymbol { IsTupleType: true } tuple && index < tuple.TupleElements.Length)
                {
                    elementType = tuple.TupleElements[index].Type;

                    if (value is TupleExpressionSyntax literal && literal.Arguments.Count == tuple.TupleElements.Length)
                    {
                        element = literal.Arguments[index].Expression;
                    }
                }

                DeconstructionSteps(info.Nested[index], elementType, element, position, steps);
            }
        }

        // A property read or write or a method the compiler calls: a property is its accessor.
        private static void AccessorStep(List<ImplicitCallStep> steps, string label, ISymbol? symbol, ITypeSymbol? receiverType, int position, bool write)
        {
            IMethodSymbol? method = symbol switch
            {
                IMethodSymbol direct => direct,
                IPropertySymbol property => write ? property.SetMethod : property.GetMethod,
                _ => null,
            };

            if (method is not null)
            {
                steps.Add(Step(label, method, receiverType, null, position));
            }
        }

        private static void InspectCollectionExpression(
            SemanticModel model,
            ICollectionExpressionOperation collection,
            Action<ImplicitCallKind, IOperation, SyntaxNode, IReadOnlyList<ImplicitCallStep>> add)
        {
            List<ImplicitCallStep> steps = [];

            int position = After(collection.Syntax);

            if (collection.ConstructMethod is { } construct)
            {
                steps.Add(Step("construct", construct, collection.Type, null, position));
            }

            // A target built by constructor and Add calls its Add for every element. Roslyn does not expose the
            // selected Add, so every Add the target type and the extension methods in scope offer is listed.
            if (collection.ConstructMethod is { MethodKind: MethodKind.Constructor } && collection.Type is INamedTypeSymbol target && collection.Elements.Length != 0)
            {
                foreach (IMethodSymbol candidate in model.LookupSymbols(collection.Syntax.SpanStart, target, "Add", includeReducedExtensionMethods: true).OfType<IMethodSymbol>())
                {
                    IMethodSymbol selected = candidate.ReducedFrom ?? candidate;

                    if (selected.Parameters.Length - (selected.IsStatic ? 1 : 0) == 1)
                    {
                        steps.Add(Step("add", selected, target, null, position));
                    }
                }
            }

            if (steps.Count != 0)
            {
                add(ImplicitCallKind.CollectionExpressionConstruction, collection, collection.Syntax, steps);
            }
        }

        // The formatting of a value that an interpolation hole or a string concatenation operand makes: ToString,
        // IFormattable.ToString and ISpanFormattable.TryFormat of the runtime type. A value whose type is an
        // intrinsic framework type formats without any authored code.
        private static void FormatHole(
            SemanticModel model,
            IOperation hole,
            SyntaxNode anchor,
            Action<ImplicitCallKind, IOperation, SyntaxNode, IReadOnlyList<ImplicitCallStep>> add,
            Action<string> refuse)
        {
            IOperation value = UnwrapImplicitConversions(hole);

            if (value.Type is null || value.Type.TypeKind == TypeKind.Error)
            {
                return;
            }

            if (value.Type.TypeKind == TypeKind.Dynamic)
            {
                refuse("A dynamic interpolation hole binds its formatting at run time.");

                return;
            }

            List<ImplicitCallStep> steps = [];

            FormatSteps(model, value.Type, value.Syntax as ExpressionSyntax, After(value.Syntax), steps, 0);

            if (steps.Count != 0)
            {
                add(ImplicitCallKind.FormatHole, hole, anchor, steps);
            }
        }

        // The steps that format a value of the type. A nullable value formats the value it holds, and a tuple, a
        // KeyValuePair or a System.Tuple formats each of its elements.
        private static void FormatSteps(SemanticModel model, ITypeSymbol type, ExpressionSyntax? value, int position, List<ImplicitCallStep> steps, int depth)
        {
            if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            {
                type = nullable.TypeArguments[0];
            }

            if (IsIntrinsicFormatType(type) || type.TypeKind == TypeKind.Error)
            {
                return;
            }

            if (depth < 4 && type is INamedTypeSymbol container
                && (container.IsTupleType
                    || !container.Locations.Any(static location => location.IsInSource)
                        && TypeKey(container) is "System.Collections.Generic.KeyValuePair`2" or "System.Tuple`1" or "System.Tuple`2" or "System.Tuple`3" or "System.Tuple`4" or "System.Tuple`5" or "System.Tuple`6" or "System.Tuple`7"))
            {
                foreach (ITypeSymbol element in container.IsTupleType ? container.TupleElements.Select(static field => field.Type) : container.TypeArguments)
                {
                    FormatSteps(model, element, null, position, steps, depth + 1);
                }

                return;
            }

            if (model.Compilation.GetSpecialType(SpecialType.System_Object).GetMembers("ToString").OfType<IMethodSymbol>().FirstOrDefault(static method => method.Parameters.Length == 0) is not { } toString)
            {
                return;
            }

            steps.Add(Step("to-string", toString, type, value, position));

            // A value whose type fixes its runtime type formats through the contracts that type implements. Any other value
            // can be a type that implements them, so every format contract is a step and the candidates decide.
            bool fixedType = type is INamedTypeSymbol { TypeKind: TypeKind.Struct } or INamedTypeSymbol { TypeKind: TypeKind.Class, IsSealed: true };

            foreach (string metadataName in new[] { "System.IFormattable", "System.ISpanFormattable", "System.IUtf8SpanFormattable" })
            {
                INamedTypeSymbol? contract = fixedType
                    ? type.AllInterfaces.FirstOrDefault(candidate => TypeKey(candidate) == metadataName)
                    : model.Compilation.GetTypeByMetadataName(metadataName);

                if (contract?.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(static method => method.Name is "ToString" or "TryFormat") is { } slot)
                {
                    steps.Add(Step("format", slot, type, value, position));
                }
            }
        }

        // Framework types whose ToString and format members run no authored code whatever the value is: the
        // primitives, enums and the sealed value types of the framework. A type outside this table is judged by the
        // members it declares.
        private static bool IsIntrinsicFormatType(ITypeSymbol type)
        {
            if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            {
                return IsIntrinsicFormatType(nullable.TypeArguments[0]);
            }

            if (type.TypeKind == TypeKind.Enum)
            {
                return true;
            }

            if (type.SpecialType is SpecialType.System_Boolean or SpecialType.System_Char or SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_Int16 or SpecialType.System_UInt16
                or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Decimal or SpecialType.System_Single
                or SpecialType.System_Double or SpecialType.System_String or SpecialType.System_IntPtr or SpecialType.System_UIntPtr or SpecialType.System_DateTime)
            {
                return true;
            }

            return type.TypeKind is TypeKind.Struct or TypeKind.Class
                && !type.Locations.Any(static location => location.IsInSource)
                && IntrinsicFormatTypes.Contains(TypeKey(type));
        }

        private static readonly IReadOnlySet<string> IntrinsicFormatTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "System.Guid",
            "System.DateTimeOffset",
            "System.TimeSpan",
            "System.DateOnly",
            "System.TimeOnly",
            "System.Version",
            "System.Uri",
            "System.Int128",
            "System.UInt128",
            "System.Half",
            "System.Numerics.BigInteger",
            "System.Text.Rune",
        };

        // An operation Roslyn does not model: a fixed statement calls GetPinnableReference on a value that is not an
        // array or a string, the pointer and stackalloc forms call nothing, and anything else is refused.
        private static void InspectUnmodeled(
            SemanticModel model,
            IOperation operation,
            Action<ImplicitCallKind, IOperation, SyntaxNode, IReadOnlyList<ImplicitCallStep>> add,
            Action<string> refuse)
        {
            if (operation.Kind != OperationKind.None)
            {
                return;
            }

            // The compiler's own conversion of a fixed initializer to its pointer type, and the like, are implicit.
            if (operation.IsImplicit && operation.Syntax is ExpressionSyntax)
            {
                return;
            }

            switch (operation.Syntax)
            {
                case FixedStatementSyntax fixedStatement:
                    {
                        List<ImplicitCallStep> steps = [];

                        foreach (VariableDeclaratorSyntax variable in fixedStatement.Declaration.Variables)
                        {
                            if (variable.Initializer?.Value is not { } value || model.GetTypeInfo(value).Type is not { } type || type is IArrayTypeSymbol or IPointerTypeSymbol || type.SpecialType == SpecialType.System_String)
                            {
                                continue;
                            }

                            IMethodSymbol[] pinnable = [.. model.LookupSymbols(value.SpanStart, type, "GetPinnableReference", includeReducedExtensionMethods: true).OfType<IMethodSymbol>()];

                            if (pinnable.Length == 0)
                            {
                                refuse("A fixed statement over " + type.ToDisplayString() + " has no GetPinnableReference to follow.");

                                continue;
                            }

                            foreach (IMethodSymbol candidate in pinnable)
                            {
                                steps.Add(Step("pinnable", candidate.ReducedFrom ?? candidate, type, value, After(value)));
                            }
                        }

                        if (steps.Count != 0)
                        {
                            add(ImplicitCallKind.Pinnable, operation, fixedStatement, steps);
                        }
                    }

                    break;

                case StackAllocArrayCreationExpressionSyntax
                    or ImplicitStackAllocArrayCreationExpressionSyntax
                    or PrefixUnaryExpressionSyntax
                    or PostfixUnaryExpressionSyntax
                    or BinaryExpressionSyntax
                    or CastExpressionSyntax
                    or ElementAccessExpressionSyntax
                    or MemberAccessExpressionSyntax
                    or IdentifierNameSyntax
                    or ParenthesizedExpressionSyntax
                    or ArgumentSyntax
                    or RefExpressionSyntax
                    or LiteralExpressionSyntax
                    or SizeOfExpressionSyntax
                    or DefaultExpressionSyntax:
                    break;

                default:
                    refuse("An operation Roslyn does not model (" + operation.Syntax.Kind() + ") has no exact target.");

                    break;
            }
        }
    }
}
