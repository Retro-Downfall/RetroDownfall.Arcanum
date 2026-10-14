using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

// The Implicit Call Census corpus: one instance of every construct that runs authored code without a written
// invocation. Each construct puts a physical effect (File.Delete) in the member the compiler calls, so the
// checker either reports that site under the frontiers live where the compiler executes the member, or refuses
// the construct. The self-check in HostedGrimoireProducerInventoryTests.ImplicitCallCensus runs every entry.
public sealed partial class HostedGrimoireProducerInventoryTests
{
    internal enum CensusDisposition : byte
    {
        // The authored member is traversed at the construct: its effect is reported under the live frontiers.
        Traversed = 1,

        // The checker cannot follow the construct and refuses it when the member the compiler calls reaches a
        // producer site, and does not refuse the effect-free twin.
        Refused = 2,

        // The checker has no exact target for the construct and refuses it whatever the member does.
        RefusedAlways = 3,
    }

    internal sealed record CensusConstruct(string Name, string Category, string Body, string Helpers, CensusDisposition Expected);

    // {E} marks where the physical effect sits; the effect-free twin removes it.
    internal static readonly IReadOnlyList<CensusConstruct> CensusCorpus =
    [
        // ---- Deconstruct --------------------------------------------------------------------------
        new("deconstruct-assignment", "Deconstruct", "int a, b; (a, b) = new Box(); _ = a + b;",
            "sealed class Box { public void Deconstruct(out int a, out int b) { {E} a = 1; b = 2; } }", CensusDisposition.Traversed),
        new("deconstruct-declaration", "Deconstruct", "var (a, b) = new Box(); _ = a + b;",
            "sealed class Box { public void Deconstruct(out int a, out int b) { {E} a = 1; b = 2; } }", CensusDisposition.Traversed),
        new("deconstruct-extension", "Deconstruct", "var (a, b) = new Box(); _ = a + b;",
            "sealed class Box { } static class BoxExt { public static void Deconstruct(this Box box, out int a, out int b) { {E} a = 1; b = 2; } }", CensusDisposition.Traversed),
        new("deconstruct-nested", "Deconstruct", "var ((a, b), c) = (new Box(), 3); _ = a + b + c;",
            "sealed class Box { public void Deconstruct(out int a, out int b) { {E} a = 1; b = 2; } }", CensusDisposition.Traversed),
        new("deconstruct-foreach", "Deconstruct", "foreach (var (a, b) in new System.Collections.Generic.List<Box>()) { _ = a + b; }",
            "sealed class Box { public void Deconstruct(out int a, out int b) { {E} a = 1; b = 2; } }", CensusDisposition.Traversed),
        new("deconstruct-positional-pattern", "Deconstruct", "if (new Box() is (var a, var b)) { _ = a + b; }",
            "sealed class Box { public void Deconstruct(out int a, out int b) { {E} a = 1; b = 2; } }", CensusDisposition.Traversed),
        new("deconstruct-switch-expression", "Deconstruct", "int r = new Box() switch { (1, _) => 1, _ => 2 }; _ = r;",
            "sealed class Box { public void Deconstruct(out int a, out int b) { {E} a = 1; b = 2; } }", CensusDisposition.Traversed),
        new("deconstruct-record", "Deconstruct", "var (a, b) = new Pair(1, 2); _ = a + b;",
            "sealed record Pair(int A, int B) { public int A { get { {E} return 1; } init { } } }", CensusDisposition.Traversed),

        // ---- Collection initializers and collection expressions -----------------------------------
        new("collection-initializer-add", "CollectionAdd", "var bag = new Bag { 1, 2 }; _ = bag;",
            "sealed class Bag : System.Collections.IEnumerable { public void Add(int value) { {E} } public System.Collections.IEnumerator GetEnumerator() => null!; }", CensusDisposition.Traversed),
        new("collection-initializer-add-extension", "CollectionAdd", "var bag = new Bag { 1, 2 }; _ = bag;",
            "sealed class Bag : System.Collections.IEnumerable { public System.Collections.IEnumerator GetEnumerator() => null!; } static class BagExt { public static void Add(this Bag bag, int value) { {E} } }", CensusDisposition.Traversed),
        new("collection-initializer-add-pair", "CollectionAdd", "var bag = new Bag { { 1, 2 } }; _ = bag;",
            "sealed class Bag : System.Collections.IEnumerable { public void Add(int key, int value) { {E} } public System.Collections.IEnumerator GetEnumerator() => null!; }", CensusDisposition.Traversed),
        new("collection-initializer-nested", "CollectionAdd", "var holder = new Holder { Items = { 1, 2 } }; _ = holder;",
            "sealed class Bag : System.Collections.IEnumerable { public void Add(int value) { {E} } public System.Collections.IEnumerator GetEnumerator() => null!; } sealed class Holder { public Bag Items { get; } = new Bag(); }", CensusDisposition.Traversed),
        new("collection-expression-add", "CollectionExpression", "Bag bag = [1, 2]; _ = bag;",
            "sealed class Bag : System.Collections.Generic.IEnumerable<int> { public void Add(int value) { {E} } public System.Collections.Generic.IEnumerator<int> GetEnumerator() => null!; System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => null!; }", CensusDisposition.Traversed),
        new("collection-expression-builder", "CollectionExpression", "Bag bag = [1, 2]; _ = bag;",
            "[System.Runtime.CompilerServices.CollectionBuilder(typeof(BagBuilder), \"Create\")] sealed class Bag : System.Collections.Generic.IEnumerable<int> { public System.Collections.Generic.IEnumerator<int> GetEnumerator() => null!; System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => null!; } static class BagBuilder { public static Bag Create(System.ReadOnlySpan<int> values) { {E} return new Bag(); } }", CensusDisposition.Traversed),
        new("collection-expression-constructor", "CollectionExpression", "Bag bag = []; _ = bag;",
            "sealed class Bag : System.Collections.Generic.IEnumerable<int> { public Bag() { {E} } public void Add(int value) { } public System.Collections.Generic.IEnumerator<int> GetEnumerator() => null!; System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => null!; }", CensusDisposition.Traversed),
        new("collection-expression-spread-add", "CollectionExpression", "Bag bag = [.. new int[] { 1 }]; _ = bag;",
            "sealed class Bag : System.Collections.Generic.IEnumerable<int> { public void Add(int value) { {E} } public System.Collections.Generic.IEnumerator<int> GetEnumerator() => null!; System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => null!; }", CensusDisposition.Traversed),

        // ---- Awaiters -----------------------------------------------------------------------------
        new("await-get-awaiter", "Awaiter", "await new Later();",
            "sealed class Later { public LaterAwaiter GetAwaiter() { {E} return default; } } struct LaterAwaiter : System.Runtime.CompilerServices.INotifyCompletion { public bool IsCompleted => true; public void GetResult() { } public void OnCompleted(System.Action continuation) { } }", CensusDisposition.Traversed),
        new("await-is-completed", "Awaiter", "await new Later();",
            "sealed class Later { public LaterAwaiter GetAwaiter() => default; } struct LaterAwaiter : System.Runtime.CompilerServices.INotifyCompletion { public bool IsCompleted { get { {E} return true; } } public void GetResult() { } public void OnCompleted(System.Action continuation) { } }", CensusDisposition.Traversed),
        new("await-get-result", "Awaiter", "await new Later();",
            "sealed class Later { public LaterAwaiter GetAwaiter() => default; } struct LaterAwaiter : System.Runtime.CompilerServices.INotifyCompletion { public bool IsCompleted => true; public void GetResult() { {E} } public void OnCompleted(System.Action continuation) { } }", CensusDisposition.Traversed),
        new("await-on-completed", "Awaiter", "await new Later();",
            "sealed class Later { public LaterAwaiter GetAwaiter() => default; } struct LaterAwaiter : System.Runtime.CompilerServices.INotifyCompletion { public bool IsCompleted => true; public void GetResult() { } public void OnCompleted(System.Action continuation) { {E} } }", CensusDisposition.Traversed),
        new("await-extension-get-awaiter", "Awaiter", "await 5;",
            "static class AwaitExt { public static System.Runtime.CompilerServices.TaskAwaiter GetAwaiter(this int milliseconds) { {E} return System.Threading.Tasks.Task.CompletedTask.GetAwaiter(); } }", CensusDisposition.Traversed),

        // ---- Interpolated string handlers and implicit ToString -----------------------------------
        new("handler-constructor", "InterpolationHandler", "Logger.Write($\"a{1}b\");",
            HandlerHelpers("ctor"), CensusDisposition.Traversed),
        new("handler-append-literal", "InterpolationHandler", "Logger.Write($\"a{1}b\");",
            HandlerHelpers("literal"), CensusDisposition.Traversed),
        new("handler-append-formatted", "InterpolationHandler", "Logger.Write($\"a{1}b\");",
            HandlerHelpers("formatted"), CensusDisposition.Traversed),
        new("interpolation-to-string", "ToString", "string text = $\"{new Named()}\"; _ = text;",
            "sealed class Named { public override string ToString() { {E} return \"n\"; } }", CensusDisposition.Traversed),
        new("concatenation-to-string", "ToString", "string text = \"a\" + new Named(); _ = text;",
            "sealed class Named { public override string ToString() { {E} return \"n\"; } }", CensusDisposition.Traversed),
        new("interpolation-formattable", "ToString", "string text = $\"{new Formatted()}\"; _ = text;",
            "sealed class Formatted : System.IFormattable { public string ToString(string? format, System.IFormatProvider? provider) { {E} return \"f\"; } }", CensusDisposition.Traversed),
        new("interpolation-record-to-string", "ToString", "string text = $\"{new Rec(1)}\"; _ = text;",
            "sealed record Rec(int A) { public int A { get { {E} return 1; } init { } } }", CensusDisposition.Traversed),

        // ---- Disposal -----------------------------------------------------------------------------
        new("using-pattern-statement", "Dispose", "using (new RefDisposable()) { }",
            "ref struct RefDisposable { public void Dispose() { {E} } }", CensusDisposition.Traversed),
        new("using-pattern-declaration", "Dispose", "using var resource = new RefDisposable(); _ = 1;",
            "ref struct RefDisposable { public void Dispose() { {E} } }", CensusDisposition.Traversed),
        new("await-using-pattern", "Dispose", "await using (new AsyncPattern()) { }",
            "sealed class AsyncPattern { public System.Threading.Tasks.ValueTask DisposeAsync() { {E} return default; } }", CensusDisposition.Traversed),
        new("using-interface-baseline", "Dispose", "using (new Disposable()) { }",
            "sealed class Disposable : System.IDisposable { public void Dispose() { {E} } }", CensusDisposition.Traversed),

        // ---- With expressions ---------------------------------------------------------------------
        new("with-record-copy-constructor", "Clone", "var original = new Pt(); var copy = original with { X = 2 }; _ = copy;",
            "sealed record Pt { public int X { get; init; } public Pt() { } private Pt(Pt original) { {E} X = original.X; } }", CensusDisposition.Traversed),
        new("with-init-setter", "Clone", "var original = new Pt(); var copy = original with { X = 2 }; _ = copy;",
            "sealed record Pt { public int X { get => 0; init { {E} } } }", CensusDisposition.Traversed),

        // ---- Patterns -----------------------------------------------------------------------------
        new("property-pattern", "PatternMember", "if (new Probe() is { Value: 1 }) { }",
            "sealed class Probe { public int Value { get { {E} return 1; } } }", CensusDisposition.Traversed),
        new("property-pattern-extended", "PatternMember", "if (new Outer() is { Inner.Value: 1 }) { }",
            "sealed class Inner { public int Value { get { {E} return 1; } } } sealed class Outer { public Inner Inner { get; } = new Inner(); }", CensusDisposition.Traversed),
        new("list-pattern-length", "PatternMember", "if (new Listing() is [1, ..]) { }",
            "sealed class Listing { public int Length { get { {E} return 1; } } public int this[int index] => 1; }", CensusDisposition.Traversed),
        new("list-pattern-indexer", "PatternMember", "if (new Listing() is [1, ..]) { }",
            "sealed class Listing { public int Length => 1; public int this[int index] { get { {E} return 1; } } }", CensusDisposition.Traversed),
        new("list-pattern-slice", "PatternMember", "if (new Listing() is [1, .. var rest]) { _ = rest; }",
            "sealed class Listing { public int Length => 1; public int this[int index] => 1; public int[] Slice(int start, int length) { {E} return new int[0]; } }", CensusDisposition.Traversed),

        // ---- Index and range ----------------------------------------------------------------------
        new("index-from-end-length", "ImplicitIndexer", "var item = new Indexed()[^1]; _ = item;",
            "sealed class Indexed { public int Length { get { {E} return 1; } } public int this[int index] => 1; }", CensusDisposition.Traversed),
        new("index-from-end-indexer", "ImplicitIndexer", "var item = new Indexed()[^1]; _ = item;",
            "sealed class Indexed { public int Length => 1; public int this[int index] { get { {E} return 1; } } }", CensusDisposition.Traversed),
        new("range-slice", "ImplicitIndexer", "var part = new Indexed()[1..]; _ = part;",
            "sealed class Indexed { public int Length => 1; public int[] Slice(int start, int length) { {E} return new int[0]; } }", CensusDisposition.Traversed),

        // ---- Operators the syntax lane does not select --------------------------------------------
        new("compound-assignment-operator", "Operator", "var v = new Num(); v += new Num(); _ = v;",
            "sealed class Num { public static Num operator +(Num left, Num right) { {E} return left; } }", CensusDisposition.Traversed),
        new("increment-operator", "Operator", "var v = new Num(); v++; _ = v;",
            "sealed class Num { public static Num operator ++(Num value) { {E} return value; } }", CensusDisposition.Traversed),
        new("prefix-increment-operator", "Operator", "var v = new Num(); ++v; _ = v;",
            "sealed class Num { public static Num operator ++(Num value) { {E} return value; } }", CensusDisposition.Traversed),
        new("true-operator-condition", "Operator", "if (new Flag()) { }",
            "sealed class Flag { public static bool operator true(Flag flag) { {E} return true; } public static bool operator false(Flag flag) => false; }", CensusDisposition.Traversed),
        new("true-operator-conditional", "Operator", "int r = new Flag() ? 1 : 2; _ = r;",
            "sealed class Flag { public static bool operator true(Flag flag) { {E} return true; } public static bool operator false(Flag flag) => false; }", CensusDisposition.Traversed),
        new("tuple-equality-operator", "Operator", "bool same = (new Num(), 1) == (new Num(), 1); _ = same;",
            "sealed class Num { public static bool operator ==(Num left, Num right) { {E} return true; } public static bool operator !=(Num left, Num right) => false; public override bool Equals(object? other) => true; public override int GetHashCode() => 0; }", CensusDisposition.Traversed),
        new("coalesce-conversion", "Conversion", "string text = new Conv() ?? \"x\"; _ = text;",
            "sealed class Conv { public static implicit operator string?(Conv? value) { {E} return null; } }", CensusDisposition.Traversed),

        // ---- Other implicit executions ------------------------------------------------------------
        new("query-where", "Query", "var query = from value in new int[] { 1 } where Predicates.Check(value) select value; _ = query;",
            "static class Predicates { public static bool Check(int value) { {E} return true; } }", CensusDisposition.Refused),
        new("type-parameter-creation", "TypeParameterCreation", "_ = Factory.Make<Created>();",
            "sealed class Created { public Created() { {E} } } static class Factory { public static T Make<T>() where T : new() => new T(); }", CensusDisposition.RefusedAlways),
        new("constructor-initializer-base", "ConstructorInitializer", "_ = new Derived();",
            "class Base { public Base() { {E} } } sealed class Derived : Base { public Derived() : base() { } }", CensusDisposition.Traversed),
        new("implicit-base-constructor", "ConstructorInitializer", "_ = new Derived();",
            "class Base { public Base() { {E} } } sealed class Derived : Base { }", CensusDisposition.Traversed),
        new("field-initializer", "FieldInitializer", "_ = new Holder();",
            "sealed class Holder { private int value = Compute(); private static int Compute() { {E} return 1; } }", CensusDisposition.Traversed),
    ];

    private static string HandlerHelpers(string effectIn) =>
        "[System.Runtime.CompilerServices.InterpolatedStringHandler] public ref struct LogHandler { "
        + "public LogHandler(int literalLength, int formattedCount) { " + (effectIn == "ctor" ? "{E}" : string.Empty) + " } "
        + "public void AppendLiteral(string value) { " + (effectIn == "literal" ? "{E}" : string.Empty) + " } "
        + "public void AppendFormatted<T>(T value) { " + (effectIn == "formatted" ? "{E}" : string.Empty) + " } } "
        + "static class Logger { public static void Write(LogHandler handler) { } }";
}
