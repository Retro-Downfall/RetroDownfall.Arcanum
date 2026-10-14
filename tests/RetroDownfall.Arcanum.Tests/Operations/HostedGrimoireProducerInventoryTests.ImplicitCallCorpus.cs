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

        // The authored member runs where the value is consumed, not where it is written, and nothing says where that
        // is: it is traversed, and reported under no frontier at all.
        Detached = 4,
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

        new("params-collection-add", "CollectionExpression", "static void Take(params Bag bag) { _ = bag; } Take(1, 2);",
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
        new("framework-handler-to-string", "ToString", "string text = string.Create(System.Globalization.CultureInfo.InvariantCulture, $\"{new Named()}\"); _ = text;",
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

        new("compound-string-concatenation", "ToString", "string text = \"a\"; text += new Named(); _ = text;",
            "sealed class Named { public override string ToString() { {E} return \"n\"; } }", CensusDisposition.Traversed),
        new("interpolation-format-clause", "ToString", "string text = $\"{new Formatted(),5:x}\"; _ = text;",
            "sealed class Formatted : System.IFormattable { public string ToString(string? format, System.IFormatProvider? provider) { {E} return \"f\"; } }", CensusDisposition.Traversed),
        new("switch-governing-conversion", "Conversion", "switch (new Conv()) { case 1: break; }",
            "sealed class Conv { public static implicit operator int(Conv value) { {E} return 1; } }", CensusDisposition.Traversed),

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
        new("binary-operator-baseline", "Operator", "var v = new Num() + new Num(); _ = v;",
            "sealed class Num { public static Num operator +(Num left, Num right) { {E} return left; } }", CensusDisposition.Traversed),
        new("unary-operator-baseline", "Operator", "var v = -new Num(); _ = v;",
            "sealed class Num { public static Num operator -(Num value) { {E} return value; } }", CensusDisposition.Traversed),
        new("event-accessor-baseline", "Operator", "var source = new EventSource(); source.Changed += static () => { }; _ = source;",
            "sealed class EventSource { public event System.Action Changed { add { {E} } remove { } } }", CensusDisposition.Traversed),
        new("property-getter-baseline", "PatternMember", "_ = new Probe().Value;",
            "sealed class Probe { public int Value { get { {E} return 1; } } }", CensusDisposition.Traversed),
        new("explicit-conversion-baseline", "Conversion", "int number = (int)new Conv(); _ = number;",
            "sealed class Conv { public static explicit operator int(Conv value) { {E} return 1; } }", CensusDisposition.Traversed),
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

        // ---- Pattern members of a fixed statement ---------------------------------------------------
        new("fixed-pinnable", "Pinnable", "unsafe { fixed (int* pointer = new Pinnable()) { _ = *pointer; } }",
            "sealed class Pinnable { private int value; public ref int GetPinnableReference() { {E} return ref value; } }", CensusDisposition.Traversed),

        // ---- Operations that bind at run time or have no target ---------------------------------------
        new("dynamic-member-access", "Unsupported", "dynamic d = 1; _ = d.Prop;",
            "sealed class Unused { }", CensusDisposition.RefusedAlways),
        new("dynamic-object-creation", "Unsupported", "dynamic d = 1; _ = new Created(d);",
            "sealed class Created { public Created(int value) { {E} } }", CensusDisposition.RefusedAlways),
        new("dynamic-invocation", "Unsupported", "dynamic d = 1; d.Run();",
            "sealed class Unused { }", CensusDisposition.RefusedAlways),
        new("dynamic-indexer-access", "Unsupported", "dynamic d = 1; _ = d[0];",
            "sealed class Unused { }", CensusDisposition.RefusedAlways),
        new("dynamic-operator", "Unsupported", "dynamic d = 1; _ = d + 1;",
            "sealed class Unused { }", CensusDisposition.RefusedAlways),
        new("dynamic-conversion", "Unsupported", "dynamic d = 1; int number = d; _ = number;",
            "sealed class Unused { }", CensusDisposition.RefusedAlways),
        new("function-pointer-invocation", "Unsupported", "unsafe { delegate*<int> pointer = null; _ = pointer(); }",
            "sealed class Unused { }", CensusDisposition.RefusedAlways),

        // ---- Other implicit executions ------------------------------------------------------------
        new("query-where", "Query", "var query = from value in new int[] { 1 } where Predicates.Check(value) select value; _ = query;",
            "static class Predicates { public static bool Check(int value) { {E} return true; } }", CensusDisposition.Refused),
        new("query-select-collection-initializer", "Query", "var query = from value in new int[] { 1 } select new Bag { value }; _ = query;",
            "sealed class Bag : System.Collections.IEnumerable { public void Add(int value) { {E} } public System.Collections.IEnumerator GetEnumerator() => null!; }", CensusDisposition.Refused),
        new("type-parameter-creation", "TypeParameterCreation", "_ = Factory.Make<Created>();",
            "sealed class Created { public Created() { {E} } } static class Factory { public static T Make<T>() where T : new() => new T(); }", CensusDisposition.RefusedAlways),
        new("constructor-initializer-base", "ConstructorInitializer", "_ = new Derived();",
            "class Base { public Base() { {E} } } sealed class Derived : Base { public Derived() : base() { } }", CensusDisposition.Traversed),
        new("implicit-base-constructor", "ConstructorInitializer", "_ = new Derived();",
            "class Base { public Base() { {E} } } sealed class Derived : Base { }", CensusDisposition.Traversed),
        new("field-initializer-spread", "FieldInitializer", "_ = new Holder();",
            "sealed class Probe : System.Collections.Generic.IEnumerable<int> { public System.Collections.Generic.IEnumerator<int> GetEnumerator() => new ProbeEnumerator(); System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator(); } sealed class ProbeEnumerator : System.Collections.Generic.IEnumerator<int> { public bool MoveNext() { {E} return false; } public int Current => 0; object System.Collections.IEnumerator.Current => Current; public void Reset() { } public void Dispose() { } } sealed class Holder { private int[] values = [.. new Probe()]; }", CensusDisposition.Traversed),
        new("field-initializer", "FieldInitializer", "_ = new Holder();",
            "sealed class Holder { private int value = Compute(); private static int Compute() { {E} return 1; } }", CensusDisposition.Traversed),

        // ---- The construction entry of a struct ---------------------------------------------------------
        new("struct-primary-constructor-initializer", "FieldInitializer", "_ = new Holder(1);",
            "struct Holder(int seed) { private int value = Compute(seed); public int Value => value; private static int Compute(int seed) { {E} return seed; } }", CensusDisposition.Traversed),
        new("record-struct-primary-constructor-initializer", "FieldInitializer", "_ = new Holder(1);",
            "record struct Holder(int Seed) { private int value = Compute(); public int Value => value; private static int Compute() { {E} return 1; } }", CensusDisposition.Traversed),
        new("struct-declared-constructor-initializer", "FieldInitializer", "_ = new Holder(1);",
            "struct Holder { private int value = Compute(); public Holder(int seed) { } private static int Compute() { {E} return 1; } }", CensusDisposition.Traversed),
        new("implicit-object-creation-initializer", "FieldInitializer", "Holder holder = new(); _ = holder;",
            "sealed class Holder { private int value = Compute(); private static int Compute() { {E} return 1; } }", CensusDisposition.Traversed),

        // ---- Collection expression Add overloads the compiler may select -------------------------------
        new("collection-expression-add-optional-parameter", "CollectionExpression", "Bag bag = [1, 2]; _ = bag;",
            "sealed class Bag : System.Collections.Generic.IEnumerable<int> { public void Add(int value, int extra = 0) { {E} } public System.Collections.Generic.IEnumerator<int> GetEnumerator() => null!; System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => null!; }", CensusDisposition.Traversed),
        new("collection-expression-add-params-parameter", "CollectionExpression", "Bag bag = [1, 2]; _ = bag;",
            "sealed class Bag : System.Collections.Generic.IEnumerable<int> { public void Add(int value, params int[] rest) { {E} } public System.Collections.Generic.IEnumerator<int> GetEnumerator() => null!; System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => null!; }", CensusDisposition.Traversed),
        new("collection-expression-add-generic-extension-optional", "CollectionExpression", "Bag bag = [1, 2]; _ = bag;",
            "sealed class Bag : System.Collections.Generic.IEnumerable<int> { public System.Collections.Generic.IEnumerator<int> GetEnumerator() => null!; System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => null!; } static class BagExt { public static void Add<T>(this Bag bag, T value, int extra = 0) { {E} } }", CensusDisposition.Traversed),

        // ---- Compiler-generated members of anonymous types and records ----------------------------------
        new("anonymous-type-interpolation", "ToString", "string text = $\"{new { A = new Named() }}\"; _ = text;",
            "sealed class Named { public override string ToString() { {E} return \"n\"; } }", CensusDisposition.Traversed),
        new("anonymous-type-local-interpolation", "ToString", "var anon = new { A = new Named() }; string text = $\"{anon}\"; _ = text;",
            "sealed class Named { public override string ToString() { {E} return \"n\"; } }", CensusDisposition.Traversed),
        new("anonymous-type-written-to-string", "ToString", "var anon = new { A = new Named() }; string text = anon.ToString(); _ = text;",
            "sealed class Named { public override string ToString() { {E} return \"n\"; } }", CensusDisposition.Traversed),
        new("anonymous-type-written-equals", "Equality", "bool same = new { A = new Key() }.Equals(new { A = new Key() }); _ = same;",
            "sealed class Key { public override bool Equals(object? other) { {E} return true; } public override int GetHashCode() => 1; }", CensusDisposition.Traversed),
        new("anonymous-type-written-get-hash-code", "Equality", "int hash = new { A = new Key() }.GetHashCode(); _ = hash;",
            "sealed class Key { public override bool Equals(object? other) => true; public override int GetHashCode() { {E} return 1; } }", CensusDisposition.Traversed),
        new("record-written-to-string", "ToString", "string text = new Rec(new Named()).ToString(); _ = text;",
            "sealed record Rec(Named Inner); sealed class Named { public override string ToString() { {E} return \"n\"; } }", CensusDisposition.Traversed),
        new("record-written-property-print", "ToString", "string text = new Rec(1).ToString(); _ = text;",
            "sealed record Rec(int A) { public int A { get { {E} return 1; } init { } } }", CensusDisposition.Traversed),
        new("record-written-equals", "Equality", "bool same = new Rec(new Key()).Equals(new Rec(new Key())); _ = same;",
            "sealed record Rec(Key Inner); sealed class Key { public override bool Equals(object? other) { {E} return true; } public override int GetHashCode() => 1; }", CensusDisposition.Traversed),
        new("record-written-get-hash-code", "Equality", "int hash = new Rec(new Key()).GetHashCode(); _ = hash;",
            "sealed record Rec(Key Inner); sealed class Key { public override bool Equals(object? other) => true; public override int GetHashCode() { {E} return 1; } }", CensusDisposition.Traversed),
        new("record-written-deconstruct", "Deconstruct", "new Rec(1).Deconstruct(out int a); _ = a;",
            "sealed record Rec(int A) { public int A { get { {E} return 1; } init { } } }", CensusDisposition.Traversed),

        // ---- Positional patterns over ITuple ---------------------------------------------------------------
        new("ituple-positional-object", "PatternMember", "object value = new Tup(); if (value is (var a, var b)) { _ = a; _ = b; }",
            TupleHelpers("length"), CensusDisposition.Traversed),
        new("ituple-positional-interface-input", "PatternMember", "System.Runtime.CompilerServices.ITuple value = new Tup(); if (value is (var a, var b)) { _ = a; _ = b; }",
            TupleHelpers("length"), CensusDisposition.Traversed),
        new("ituple-positional-sealed-input", "PatternMember", "if (new Tup() is (var a, var b)) { _ = a; _ = b; }",
            TupleHelpers("length"), CensusDisposition.Traversed),
        new("ituple-positional-indexer", "PatternMember", "object value = new Tup(); if (value is (var a, var b)) { _ = a; _ = b; }",
            TupleHelpers("indexer"), CensusDisposition.Traversed),
        new("ituple-switch-expression", "PatternMember", "object value = new Tup(); int result = value switch { (1, _) => 1, _ => 2 }; _ = result;",
            TupleHelpers("length"), CensusDisposition.Traversed),
        new("ituple-positional-unproven-object", "PatternMember", "object value = Anything.Value; if (value is (var a, var b)) { _ = a; _ = b; }",
            TupleHelpers("length") + " static class Anything { public static object Value { get; set; } = null!; }", CensusDisposition.Traversed),

        // ---- The awaits of an await foreach ----------------------------------------------------------------
        new("await-foreach-movenext-get-awaiter", "Awaiter", "await foreach (var item in new Seq()) { _ = item; }",
            AsyncSequenceHelpers("moveNext-get-awaiter"), CensusDisposition.Traversed),
        new("await-foreach-movenext-is-completed", "Awaiter", "await foreach (var item in new Seq()) { _ = item; }",
            AsyncSequenceHelpers("moveNext-is-completed"), CensusDisposition.Traversed),
        new("await-foreach-movenext-get-result", "Awaiter", "await foreach (var item in new Seq()) { _ = item; }",
            AsyncSequenceHelpers("moveNext-get-result"), CensusDisposition.Traversed),
        new("await-foreach-dispose-get-awaiter", "Awaiter", "await foreach (var item in new Seq()) { _ = item; }",
            AsyncSequenceHelpers("dispose-get-awaiter"), CensusDisposition.Traversed),
        new("await-foreach-dispose-get-result", "Awaiter", "await foreach (var item in new Seq()) { _ = item; }",
            AsyncSequenceHelpers("dispose-get-result"), CensusDisposition.Traversed),

        // ---- Framework formatters that format what they hold or can be subclassed ----------------------
        new("uri-subclass-interpolation", "ToString", "System.Uri uri = new EvilUri(); string text = $\"{uri}\"; _ = text;",
            "sealed class EvilUri : System.Uri { public EvilUri() : base(\"http://a\") { } public override string ToString() { {E} return \"u\"; } }", CensusDisposition.Traversed),
        new("uri-subclass-concatenation", "ToString", "System.Uri uri = Make(); string text = \"x\" + uri; _ = text; static System.Uri Make() => new EvilUri();",
            "sealed class EvilUri : System.Uri { public EvilUri() : base(\"http://a\") { } public override string ToString() { {E} return \"u\"; } }", CensusDisposition.Traversed),
        new("uri-subclass-unproven", "ToString", "string text = $\"{Anything.Link}\"; _ = text;",
            "sealed class EvilUri : System.Uri { public EvilUri() : base(\"http://a\") { } public override string ToString() { {E} return \"u\"; } } static class Anything { public static System.Uri Link { get; set; } = null!; }", CensusDisposition.Traversed),
        new("exception-inner-interpolation", "ToString", "var error = new System.InvalidOperationException(\"x\", new Evil()); string text = $\"{error}\"; _ = text;",
            "sealed class Evil : System.Exception { public override string Message { get { {E} return \"m\"; } } }", CensusDisposition.Traversed),
        new("exception-inner-concatenation", "ToString", "System.Exception error = new System.InvalidOperationException(\"x\", new Evil()); string text = \"e: \" + error; _ = text;",
            "sealed class Evil : System.Exception { public override string Message { get { {E} return \"m\"; } } }", CensusDisposition.Traversed),
        new("aggregate-exception-interpolation", "ToString", "var error = new System.AggregateException(new Evil()); string text = $\"{error}\"; _ = text;",
            "sealed class Evil : System.Exception { public override string Message { get { {E} return \"m\"; } } }", CensusDisposition.Traversed),
        new("exception-unproven-interpolation", "ToString", "string text = $\"{Anything.Failure}\"; _ = text;",
            "sealed class Evil : System.Exception { public override string Message { get { {E} return \"m\"; } } } static class Anything { public static System.InvalidOperationException Failure { get; set; } = null!; }", CensusDisposition.Traversed),
        new("lazy-unproven-interpolation", "ToString", "string text = $\"{Holder.Shared}\"; _ = text;",
            "sealed class Named { public override string ToString() { {E} return \"n\"; } } sealed class Holder { public static System.Lazy<Named> Shared = new System.Lazy<Named>(() => new Named()); }", CensusDisposition.RefusedAlways),
        new("lazy-proven-interpolation", "ToString", "var lazy = new System.Lazy<Named>(() => new Named()); string text = $\"{lazy}\"; _ = text;",
            "sealed class Named { public override string ToString() { {E} return \"n\"; } }", CensusDisposition.RefusedAlways),

        // ---- Interpolated strings whose holes are formatted where the value is consumed -------------
        new("formattable-string-deferred", "ToString", "System.FormattableString text = $\"{new Named()}\"; _ = text;",
            "sealed class Named { public override string ToString() { {E} return \"n\"; } }", CensusDisposition.Detached),
        new("formattable-interface-deferred", "ToString", "System.IFormattable text = $\"{new Named()}\"; _ = text;",
            "sealed class Named { public override string ToString() { {E} return \"n\"; } }", CensusDisposition.Detached),

        // ---- Operators the operator lane refuses -------------------------------------------------------------
        new("conditional-and-user-operator", "Operator", "if (new Flag() && new Flag()) { }",
            "sealed class Flag { public static bool operator true(Flag flag) => true; public static bool operator false(Flag flag) { {E} return false; } public static Flag operator &(Flag left, Flag right) => left; }", CensusDisposition.RefusedAlways),
        new("conditional-or-user-operator", "Operator", "if (new Flag() || new Flag()) { }",
            "sealed class Flag { public static bool operator true(Flag flag) { {E} return true; } public static bool operator false(Flag flag) => false; public static Flag operator |(Flag left, Flag right) => left; }", CensusDisposition.RefusedAlways),
        new("record-equality-operator", "Operator", "bool same = new Rec(new Key()) == new Rec(new Key()); _ = same;",
            "sealed record Rec(Key Inner); sealed class Key { public override bool Equals(object? other) { {E} return true; } public override int GetHashCode() => 1; }", CensusDisposition.RefusedAlways),

        // ---- Values a framework member is handed and calls back into --------------------------------------
        new("framework-concat-object-argument", "FrameworkCallback", "string text = string.Concat(new Named(), \"x\"); _ = text;",
            "sealed class Named { public override string ToString() { {E} return \"n\"; } }", CensusDisposition.Refused),
        new("framework-format-object-argument", "FrameworkCallback", "string text = string.Format(\"{0}\", new Named()); _ = text;",
            "sealed class Named { public override string ToString() { {E} return \"n\"; } }", CensusDisposition.Refused),
        new("framework-string-builder-append-object", "FrameworkCallback", "var builder = new System.Text.StringBuilder(); builder.Append((object)new Named()); _ = builder;",
            "sealed class Named { public override string ToString() { {E} return \"n\"; } }", CensusDisposition.Refused),
        new("framework-hash-set-initializer-hash", "FrameworkCallback", "var set = new System.Collections.Generic.HashSet<Key> { new Key() }; _ = set;",
            "sealed class Key { public override int GetHashCode() { {E} return 1; } public override bool Equals(object? other) => true; }", CensusDisposition.Refused),
        new("framework-hash-set-collection-expression-hash", "FrameworkCallback", "System.Collections.Generic.HashSet<Key> set = [new Key()]; _ = set;",
            "sealed class Key { public override int GetHashCode() { {E} return 1; } public override bool Equals(object? other) => true; }", CensusDisposition.Refused),
        new("framework-list-contains-equals", "FrameworkCallback", "bool found = new System.Collections.Generic.List<Key>().Contains(new Key()); _ = found;",
            "sealed class Key { public override int GetHashCode() => 1; public override bool Equals(object? other) { {E} return true; } }", CensusDisposition.Refused),
        new("framework-sorted-set-comparer", "FrameworkCallback", "var set = new System.Collections.Generic.SortedSet<int>(new Order()); _ = set;",
            "sealed class Order : System.Collections.Generic.IComparer<int> { public int Compare(int x, int y) { {E} return 0; } }", CensusDisposition.Refused),
        new("framework-int-to-string-provider", "FrameworkCallback", "string text = 5.ToString(new Provider()); _ = text;",
            "sealed class Provider : System.IFormatProvider { public object? GetFormat(System.Type? formatType) { {E} return null; } }", CensusDisposition.Refused),
        new("framework-handler-create-provider", "FrameworkCallback", "string text = string.Create(new Provider(), $\"{1}\"); _ = text;",
            "sealed class Provider : System.IFormatProvider { public object? GetFormat(System.Type? formatType) { {E} return null; } }", CensusDisposition.Refused),
        new("framework-string-builder-append-provider-handler", "FrameworkCallback", "var builder = new System.Text.StringBuilder(); builder.Append(new Provider(), $\"{1}\"); _ = builder;",
            "sealed class Provider : System.IFormatProvider { public object? GetFormat(System.Type? formatType) { {E} return null; } }", CensusDisposition.RefusedAlways),

        // ---- Accessors the runtime type of the receiver selects --------------------------------------------
        new("interface-property-read", "Accessor", "IProbe probe = new Probe(); _ = probe.Value;",
            "interface IProbe { int Value { get; } } sealed class Probe : IProbe { public int Value { get { {E} return 1; } } }", CensusDisposition.Traversed),
        new("interface-property-pattern", "Accessor", "IProbe probe = new Probe(); if (probe is { Value: 1 }) { }",
            "interface IProbe { int Value { get; } } sealed class Probe : IProbe { public int Value { get { {E} return 1; } } }", CensusDisposition.Traversed),
        new("interface-property-write", "Accessor", "IProbe probe = new Probe(); probe.Value = 1;",
            "interface IProbe { int Value { get; set; } } sealed class Probe : IProbe { public int Value { get => 0; set { {E} } } }", CensusDisposition.Traversed),
        new("interface-property-parameter", "Accessor", "Use(new Probe()); static void Use(IProbe probe) { _ = probe.Value; }",
            "interface IProbe { int Value { get; } } sealed class Probe : IProbe { public int Value { get { {E} return 1; } } }", CensusDisposition.Traversed),
        new("interface-property-unproven-single", "Accessor", "IProbe probe = Anything.Value; _ = probe.Value;",
            "interface IProbe { int Value { get; } } sealed class Probe : IProbe { public int Value { get { {E} return 1; } } } static class Anything { public static IProbe Value { get; set; } = null!; }", CensusDisposition.Traversed),
        new("interface-property-unproven-many", "Accessor", "IProbe probe = Anything.Value; _ = probe.Value;",
            "interface IProbe { int Value { get; } } sealed class Probe : IProbe { public int Value { get { {E} return 1; } } } sealed class Other : IProbe { public int Value => 2; } static class Anything { public static IProbe Value { get; set; } = null!; }", CensusDisposition.Refused),
        new("interface-indexer-read", "Accessor", "IProbe probe = new Probe(); _ = probe[0];",
            "interface IProbe { int this[int index] { get; } } sealed class Probe : IProbe { public int this[int index] { get { {E} return 1; } } }", CensusDisposition.Traversed),
        new("virtual-property-read", "Accessor", "Base probe = new Derived(); _ = probe.Value;",
            "class Base { public virtual int Value => 0; } sealed class Derived : Base { public override int Value { get { {E} return 1; } } }", CensusDisposition.Traversed),
        new("abstract-property-read", "Accessor", "Base probe = new Derived(); _ = probe.Value;",
            "abstract class Base { public abstract int Value { get; } } sealed class Derived : Base { public override int Value { get { {E} return 1; } } }", CensusDisposition.Traversed),
        new("abstract-property-pattern", "Accessor", "Base probe = new Derived(); if (probe is { Value: 1 }) { }",
            "abstract class Base { public abstract int Value { get; } } sealed class Derived : Base { public override int Value { get { {E} return 1; } } }", CensusDisposition.Traversed),
        new("virtual-property-list-pattern-length", "Accessor", "Base probe = new Derived(); if (probe is [1, ..]) { }",
            "class Base { public virtual int Length => 1; public int this[int index] => 1; } sealed class Derived : Base { public override int Length { get { {E} return 1; } } }", CensusDisposition.Traversed),
        new("virtual-property-unproven-many", "Accessor", "Base probe = Anything.Value; _ = probe.Value;",
            "class Base { public virtual int Value => 0; } sealed class Derived : Base { public override int Value { get { {E} return 1; } } } sealed class Other : Base { public override int Value => 2; } static class Anything { public static Base Value { get; set; } = null!; }", CensusDisposition.Refused),
        new("interface-event-add", "Accessor", "IProbe probe = new Probe(); probe.Changed += static () => { };",
            "interface IProbe { event System.Action Changed; } sealed class Probe : IProbe { public event System.Action Changed { add { {E} } remove { } } }", CensusDisposition.Traversed),
        new("interface-event-remove", "Accessor", "IProbe probe = new Probe(); probe.Changed -= static () => { };",
            "interface IProbe { event System.Action Changed; } sealed class Probe : IProbe { public event System.Action Changed { add { } remove { {E} } } }", CensusDisposition.Traversed),
        new("virtual-event-add", "Accessor", "Base probe = new Derived(); probe.Changed += static () => { };",
            "class Base { public virtual event System.Action? Changed; } sealed class Derived : Base { public override event System.Action? Changed { add { {E} } remove { } } }", CensusDisposition.Traversed),
        new("partial-property-read", "Accessor", "_ = new Probe().Value;",
            "sealed partial class Probe { public partial int Value { get; } } sealed partial class Probe { public partial int Value { get { {E} return 1; } } }", CensusDisposition.Traversed),
        new("partial-property-pattern", "Accessor", "if (new Probe() is { Value: 1 }) { }",
            "sealed partial class Probe { public partial int Value { get; } } sealed partial class Probe { public partial int Value { get { {E} return 1; } } }", CensusDisposition.Traversed),
        new("partial-property-record-print", "Accessor", "string text = $\"{new Probe()}\"; _ = text;",
            "sealed partial record Probe { public partial int Value { get; } } sealed partial record Probe { public partial int Value { get { {E} return 1; } } }", CensusDisposition.Traversed),
        new("partial-indexer-read", "Accessor", "_ = new Probe()[0];",
            "sealed partial class Probe { public partial int this[int index] { get; } } sealed partial class Probe { public partial int this[int index] { get { {E} return 1; } } }", CensusDisposition.Traversed),
        new("record-virtual-printed-override", "Accessor", "Base value = new Derived(); string text = $\"{value}\"; _ = text;",
            "record Base { public virtual int Value => 0; } sealed record Derived : Base { public override int Value { get { {E} return 1; } } }", CensusDisposition.Traversed),

        // ---- A property read as the receiver of a further member ------------------------------------------
        new("receiver-property-implicit-this-call", "Accessor", "new Owner().Run();",
            "sealed class Svc { public void Go() { } } sealed class Owner { Svc Service { get { {E} return new Svc(); } } public void Run() { Service.Go(); } }", CensusDisposition.Traversed),
        new("receiver-property-implicit-this-field", "Accessor", "_ = new Owner().Run();",
            "sealed class Svc { public int Field; } sealed class Owner { Svc Service { get { {E} return new Svc(); } } public int Run() => Service.Field; }", CensusDisposition.Traversed),
        new("receiver-property-implicit-this-chain", "Accessor", "_ = new Owner().Run();",
            "sealed class Inner { public int Count => 1; } sealed class Svc { public Inner Inner => new Inner(); } sealed class Owner { Svc Service { get { {E} return new Svc(); } } public int Run() => Service.Inner.Count; }", CensusDisposition.Traversed),
        new("receiver-property-static", "Accessor", "Owner.Run();",
            "sealed class Svc { public void Go() { } } static class Owner { static Svc Service { get { {E} return new Svc(); } } public static void Run() { Service.Go(); } }", CensusDisposition.Traversed),

        // ---- Static initialization and async method builders -------------------------------------------------
        new("static-constructor", "StaticInitialization", "_ = Holder.Value;",
            "static class Holder { public static int Value; static Holder() { {E} } }", CensusDisposition.Refused),
        new("static-field-initializer", "StaticInitialization", "_ = Holder.Value;",
            "static class Holder { public static int Value = Compute(); private static int Compute() { {E} return 1; } }", CensusDisposition.Refused),
        new("static-property-initializer", "StaticInitialization", "_ = Holder.Value;",
            "static class Holder { public static int Value { get; } = Compute(); private static int Compute() { {E} return 1; } }", CensusDisposition.Refused),
        new("static-initializer-instance-creation", "StaticInitialization", "_ = new Holder();",
            "sealed class Holder { private static readonly int shared = Compute(); private static int Compute() { {E} return 1; } }", CensusDisposition.Refused),
        new("static-initializer-base-class", "StaticInitialization", "_ = new Derived();",
            "class Base { private static readonly int shared = Compute(); private static int Compute() { {E} return 1; } } sealed class Derived : Base { }", CensusDisposition.Refused),
        new("static-initializer-method-call", "StaticInitialization", "Holder.Run();",
            "static class Holder { private static readonly int shared = Compute(); public static void Run() { } private static int Compute() { {E} return 1; } }", CensusDisposition.Refused),
        new("async-method-builder-create", "AsyncBuilder", "await Runner.Run();",
            AsyncBuilderHelpers("create"), CensusDisposition.Refused),
        new("async-method-builder-start", "AsyncBuilder", "await Runner.Run();",
            AsyncBuilderHelpers("start"), CensusDisposition.Refused),
        new("async-method-builder-set-result", "AsyncBuilder", "await Runner.Run();",
            AsyncBuilderHelpers("result"), CensusDisposition.Refused),
        new("async-method-builder-task", "AsyncBuilder", "await Runner.Run();",
            AsyncBuilderHelpers("task"), CensusDisposition.Refused),
    ];

    // An async method whose builder is authored; the effect sits in the builder member named by effectIn.
    private static string AsyncBuilderHelpers(string effectIn) =>
        "static class Runner { [System.Runtime.CompilerServices.AsyncMethodBuilder(typeof(MyBuilder))] public static async System.Threading.Tasks.ValueTask Run() { await System.Threading.Tasks.Task.CompletedTask; } } "
        + "sealed class MyBuilder { public static MyBuilder Create() { " + (effectIn == "create" ? "{E}" : string.Empty) + " return new MyBuilder(); } "
        + "public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : System.Runtime.CompilerServices.IAsyncStateMachine { " + (effectIn == "start" ? "{E}" : string.Empty) + " stateMachine.MoveNext(); } "
        + "public void SetStateMachine(System.Runtime.CompilerServices.IAsyncStateMachine stateMachine) { } "
        + "public void SetResult() { " + (effectIn == "result" ? "{E}" : string.Empty) + " } "
        + "public void SetException(System.Exception exception) { } "
        + "public System.Threading.Tasks.ValueTask Task { get { " + (effectIn == "task" ? "{E}" : string.Empty) + " return default; } } "
        + "public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine) where TAwaiter : System.Runtime.CompilerServices.INotifyCompletion where TStateMachine : System.Runtime.CompilerServices.IAsyncStateMachine { } "
        + "public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine) where TAwaiter : System.Runtime.CompilerServices.ICriticalNotifyCompletion where TStateMachine : System.Runtime.CompilerServices.IAsyncStateMachine { } }";

    // An asynchronous sequence whose MoveNextAsync and DisposeAsync return authored awaitables; the effect sits in the
    // awaitable member named by effectIn.
    private static string AsyncSequenceHelpers(string effectIn) =>
        "sealed class Seq { public Enumr GetAsyncEnumerator() => new Enumr(); } "
        + "sealed class Enumr { public int Current => 0; public MoveLater MoveNextAsync() => new MoveLater(); public DisposeLater DisposeAsync() => new DisposeLater(); } "
        + "sealed class MoveLater { public MoveAwaiter GetAwaiter() { " + (effectIn == "moveNext-get-awaiter" ? "{E}" : string.Empty) + " return default; } } "
        + "struct MoveAwaiter : System.Runtime.CompilerServices.INotifyCompletion { public bool IsCompleted { get { " + (effectIn == "moveNext-is-completed" ? "{E}" : string.Empty) + " return true; } } "
        + "public bool GetResult() { " + (effectIn == "moveNext-get-result" ? "{E}" : string.Empty) + " return false; } public void OnCompleted(System.Action continuation) { } } "
        + "sealed class DisposeLater { public DisposeAwaiter GetAwaiter() { " + (effectIn == "dispose-get-awaiter" ? "{E}" : string.Empty) + " return default; } } "
        + "struct DisposeAwaiter : System.Runtime.CompilerServices.INotifyCompletion { public bool IsCompleted => true; "
        + "public void GetResult() { " + (effectIn == "dispose-get-result" ? "{E}" : string.Empty) + " } public void OnCompleted(System.Action continuation) { } }";

    private static string TupleHelpers(string effectIn) =>
        "sealed class Tup : System.Runtime.CompilerServices.ITuple { "
        + "public int Length { get { " + (effectIn == "length" ? "{E}" : string.Empty) + " return 2; } } "
        + "public object? this[int index] { get { " + (effectIn == "indexer" ? "{E}" : string.Empty) + " return null; } } }";

    private static string HandlerHelpers(string effectIn) =>
        "[System.Runtime.CompilerServices.InterpolatedStringHandler] public ref struct LogHandler { "
        + "public LogHandler(int literalLength, int formattedCount) { " + (effectIn == "ctor" ? "{E}" : string.Empty) + " } "
        + "public void AppendLiteral(string value) { " + (effectIn == "literal" ? "{E}" : string.Empty) + " } "
        + "public void AppendFormatted<T>(T value) { " + (effectIn == "formatted" ? "{E}" : string.Empty) + " } } "
        + "static class Logger { public static void Write(LogHandler handler) { } }";
}
