using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

// The ImplicitCallResolution fixtures: the resolution half of the Implicit Call Census: which members an implicit call runs. The corpus pins that every
// construct is traversed or refused; these fixtures pin the precision (framework forms stay classified, a virtual
// member runs the override of the exact runtime type, a generated record member runs only the accessors the record
// declares) and the construction entry.
public sealed partial class HostedGrimoireProducerInventoryTests
{
    private static CSharpCompilation CensusCompileSource(string body, string helpers, bool admitted = true)
    {
        CSharpCompilation compilation = Compile("using System.Linq;\n" + R2Source((admitted ? R2Admission : string.Empty) + body + " await Task.CompletedTask;", helpers));

        compilation = compilation.WithOptions(compilation.Options.WithAllowUnsafe(true));

        Diagnostic[] errors = [.. compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)];

        Assert.True(errors.Length == 0, string.Join(" | ", errors.Select(static error => error.ToString())));

        return compilation;
    }

    // The same constructs over framework types run only reviewed framework members: nothing is refused.
    [Theory]
    [InlineData("var list = new System.Collections.Generic.List<int> { 1, 2 }; _ = list;")]
    [InlineData("var map = new System.Collections.Generic.Dictionary<int, int> { { 1, 2 }, { 3, 4 } }; _ = map;")]
    [InlineData("var map = new System.Collections.Generic.Dictionary<int, int> { [1] = 2, [3] = 4 }; _ = map;")]
    [InlineData("var set = new System.Collections.Generic.HashSet<int> { 1, 2 }; _ = set;")]
    [InlineData("System.Collections.Generic.List<int> list = [1, 2]; _ = list;")]
    [InlineData("System.Collections.Immutable.ImmutableArray<int> array = [1, 2]; _ = array;")]
    [InlineData("System.Collections.Generic.IReadOnlyList<int> values = [1, 2]; _ = values;")]
    [InlineData("System.Collections.Generic.IEnumerable<int> values = [.. new System.Collections.Generic.List<int>(), 3]; _ = values;")]
    [InlineData("_ = new System.AggregateException(\"failed\", [new System.InvalidOperationException(), new System.TimeoutException()]);")]
    [InlineData("int[] array = [1, 2]; System.Span<int> span = [1, 2]; _ = array; _ = span;")]
    [InlineData("var (key, value) = new System.Collections.Generic.KeyValuePair<int, int>(1, 2); _ = key + value;")]
    [InlineData("var (a, b) = (1, 2); _ = a + b;")]
    [InlineData("var ((a, b), c) = ((1, 2), 3); _ = a + b + c;")]
    [InlineData("foreach (var (key, value) in new System.Collections.Generic.Dictionary<int, int>()) { _ = key + value; }")]
    [InlineData("await new System.Threading.Tasks.ValueTask(); await System.Threading.Tasks.Task.Yield(); await System.Threading.Tasks.Task.CompletedTask.ConfigureAwait(false);")]
    [InlineData("_ = await System.Threading.Tasks.Task.FromResult(1); _ = await new System.Threading.Tasks.ValueTask<int>(1);")]
    [InlineData("int[] array = [1, 2]; var last = array[^1]; var rest = array[1..]; _ = last; _ = rest;")]
    [InlineData("string text = \"abc\"; var tail = text[1..]; var end = text[^1]; _ = tail; _ = end;")]
    [InlineData("var list = new System.Collections.Generic.List<int>(); var last = list[^1]; _ = last;")]
    [InlineData("string text = $\"{1} {true} {'c'} {2.5} {System.Guid.Empty} {System.DateTimeOffset.UnixEpoch} {System.TimeSpan.Zero} {new System.Uri(\"http://a\")} {System.StringComparison.Ordinal}\"; _ = text;")]
    [InlineData("string text = \"a\" + 1 + 'c' + 2.5 + System.Guid.Empty; _ = text;")]
    [InlineData("int? number = 1; string text = $\"{number}\"; _ = text;")]
    [InlineData("int[] values = [1, 2]; if (values is [1, ..]) { } if (values is [_, .. var rest]) { _ = rest; }")]
    [InlineData("string text = \"ab\"; if (text is ['a', ..]) { }")]
    [InlineData("var number = 1; number += 2; number++; --number; _ = number;")]
    [InlineData("bool same = (1, \"a\") == (1, \"a\"); _ = same;")]
    [InlineData("lock (new object()) { }")]
    [InlineData("string? maybe = null; var chosen = maybe ?? \"x\"; _ = chosen;")]
    [InlineData("var query = from value in new int[] { 1, 2 } where value > 1 select value * 2; _ = query;")]
    [InlineData("unsafe { int* pointer = stackalloc int[2]; pointer[0] = 1; fixed (int* pinned = new int[2]) { _ = *pinned; } fixed (char* characters = \"text\") { _ = *characters; } }")]
    [InlineData("var created = new System.Collections.Generic.List<int>(); var pair = new System.Collections.Generic.KeyValuePair<int, int>(1, 2); _ = created; _ = pair;")]
    [InlineData("Probe probe = new Probe(); _ = probe;")]
    [InlineData("var error = new System.InvalidOperationException(\"x\", new System.TimeoutException()); string text = $\"{error}\"; _ = text;")]
    [InlineData("var error = new System.AggregateException(new System.TimeoutException()); string text = \"e: \" + error; _ = text;")]
    [InlineData("string text = string.Create(System.Globalization.CultureInfo.InvariantCulture, $\"{1} {2.5}\"); _ = text;")]
    [InlineData("string text = 5.ToString(System.Globalization.CultureInfo.InvariantCulture); _ = text;")]
    [InlineData("System.IFormatProvider? provider = null; string text = 5.ToString(provider); _ = text;")]
    [InlineData("var set = new System.Collections.Generic.HashSet<string> { \"a\", \"b\" }; System.Collections.Generic.HashSet<int> numbers = [1, 2]; _ = set; _ = numbers;")]
    [InlineData("string text = string.Concat(\"a\", 1, new Probe().ToString()); _ = text;")]
    [InlineData("object value = (1, 2); if (value is (var a, var b)) { _ = a; _ = b; }")]
    [InlineData("System.FormattableString text = $\"{1} {System.Guid.Empty}\"; _ = text;")]
    [InlineData("var anonymous = new { A = 1, B = \"b\" }; string text = $\"{anonymous}\" + anonymous.ToString() + anonymous.GetHashCode(); _ = text;")]
    public void FrameworkImplicitCallsStayClassified(string statement)
    {
        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(CensusCompileSource(statement, "sealed class Probe { }"));

        Assert.Empty(result.Diagnostics);

        Assert.Empty(EnumerationDeleteSites(result));
    }

    // A generated record member runs only the accessors the record declares itself: positional properties and printed
    // members are auto-properties unless the record declares them, and the copy constructor copies state.
    [Theory]
    [InlineData("var (a, b) = new Pair(1, 2); _ = a + b;")]
    [InlineData("var copy = new Pair(1, 2) with { A = 3 }; _ = copy;")]
    [InlineData("string text = $\"{new Pair(1, 2)}\"; _ = text;")]
    [InlineData("string text = \"p=\" + new Pair(1, 2); _ = text;")]
    [InlineData("if (new Pair(1, 2) is (1, _)) { }")]
    [InlineData("if (new Pair(1, 2) is { A: 1 }) { }")]
    [InlineData("string text = $\"{new Outer(new Pair(1, 2))}\"; _ = text;")]
    public void RecordGeneratedMembersAreAcceptedWhenTheRecordDeclaresNoAccessorBodies(string statement)
    {
        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(CensusCompileSource(statement, "sealed record Pair(int A, int B); sealed record Outer(Pair Inner);"));

        Assert.Empty(result.Diagnostics);

        Assert.Empty(EnumerationDeleteSites(result));
    }

    // ... and when the record declares an accessor, the generated member that calls it is traversed.
    [Theory]
    [InlineData("var (a, b) = new Pair(1, 2); _ = a + b;", "sealed record Pair(int A, int B) { public int B { get { {E} return 1; } init { } } }")]
    [InlineData("string text = $\"{new Pair(1, 2)}\"; _ = text;", "sealed record Pair(int A, int B) { public int B { get { {E} return 1; } init { } } }")]
    [InlineData("string text = \"p=\" + new Pair(1, 2); _ = text;", "sealed record Pair(int A, int B) { public int B { get { {E} return 1; } init { } } }")]
    [InlineData("string text = $\"{new Outer(new Pair(1, 2))}\"; _ = text;", "sealed record Pair(int A, int B) { public int B { get { {E} return 1; } init { } } } sealed record Outer(Pair Inner);")]
    [InlineData("string text = $\"{new Tagged(1)}\"; _ = text;", "record Named { public string Label { get { {E} return \"l\"; } init { } } } sealed record Tagged(int A) : Named;")]
    [InlineData("var copy = new Tagged(1) with { A = 2 }; _ = copy;", "record Named { public Named() { } protected Named(Named original) { {E} } } sealed record Tagged(int A) : Named;")]
    [InlineData("string text = $\"{new Extra()}\"; _ = text;", "sealed record Extra { public int Hidden { get { {E} return 1; } } }")]
    public void RecordDeclaredAccessorsRunWhenAGeneratedMemberCallsThem(string statement, string helpers)
    {
        HostedProducerDiscovery<HostedProducerSite> reported = EnumerationDiscover(CensusCompileSource(statement, helpers.Replace("{E}", EnumerationEffect, StringComparison.Ordinal)));

        HostedProducerSite site = Assert.Single(EnumerationDeleteSites(reported));

        Assert.NotNull(site.WorkFrontierCapsuleId);

        HostedProducerDiscovery<HostedProducerSite> unadmitted = EnumerationDiscover(CensusCompileSource(statement, helpers.Replace("{E}", EnumerationEffect, StringComparison.Ordinal), admitted: false));

        Assert.Null(Assert.Single(EnumerationDeleteSites(unadmitted)).WorkFrontierCapsuleId);

        HostedProducerDiscovery<HostedProducerSite> clean = EnumerationDiscover(CensusCompileSource(statement, helpers.Replace("{E}", string.Empty, StringComparison.Ordinal)));

        Assert.Empty(clean.Diagnostics);

        Assert.Empty(EnumerationDeleteSites(clean));
    }

    // A virtual member runs the override of the exact runtime type, never the base body the static type names.
    [Theory]
    [InlineData("Base source = new Derived(); var (a, b) = source; _ = a + b;", "derived", true)]
    [InlineData("Base source = new Derived(); var (a, b) = source; _ = a + b;", "base", false)]
    [InlineData("Base source = new Base(); var (a, b) = source; _ = a + b;", "base", true)]
    [InlineData("Base source = new Base(); var (a, b) = source; _ = a + b;", "derived", false)]
    [InlineData("var (a, b) = new Derived(); _ = a + b;", "derived", true)]
    public void VirtualDeconstructRunsTheOverrideOfTheExactRuntimeType(string statement, string effectIn, bool reported)
    {
        string helpers = "class Base { public virtual void Deconstruct(out int a, out int b) { " + (effectIn == "base" ? EnumerationEffect : string.Empty) + "a = 1; b = 2; } } "
            + "sealed class Derived : Base { public override void Deconstruct(out int a, out int b) { " + (effectIn == "derived" ? EnumerationEffect : string.Empty) + "a = 1; b = 2; } }";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(CensusCompileSource(statement, helpers));

        Assert.Equal(reported, EnumerationDeleteSites(result).Length == 1);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED");
    }

    // A value nothing proves the runtime type of may run any override, so its virtual Deconstruct is refused, and the
    // positional pattern over an unsealed type is refused for the same reason. The sealed twin is traversed.
    [Theory]
    [InlineData("var (a, b) = Unproved.Value; _ = a + b;", true)]
    [InlineData("if (Unproved.Value is (var a, var b)) { _ = a + b; }", true)]
    [InlineData("var (a, b) = Unproved.Sealed; _ = a + b;", false)]
    [InlineData("if (Unproved.Sealed is (var a, var b)) { _ = a + b; }", false)]
    public void UnprovenRuntimeTypeOfAVirtualImplicitMemberIsRefused(string statement, bool refused)
    {
        string helpers = "class Base { public virtual void Deconstruct(out int a, out int b) { " + EnumerationEffect + "a = 1; b = 2; } } sealed class Closed { public void Deconstruct(out int a, out int b) { a = 1; b = 2; } } "
            + "static class Unproved { public static Base Value { get; set; } = null!; public static Closed Sealed { get; set; } = null!; }";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(CensusCompileSource(statement, helpers));

        Assert.Equal(refused, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"));
    }

    // A static member (an extension method) does not depend on the receiver, so it is traversed whatever the receiver is.
    [Fact]
    public void ExtensionDeconstructIsTraversedWhateverTheReceiverIs()
    {
        string helpers = "interface IBoxed { } sealed class Box : IBoxed { } static class Ext { public static void Deconstruct(this IBoxed box, out int a, out int b) { " + EnumerationEffect + "a = 1; b = 2; } } static class Unproved { public static IBoxed Value { get; set; } = null!; }";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(CensusCompileSource("var (a, b) = Unproved.Value; _ = a + b;", helpers));

        Assert.NotNull(Assert.Single(EnumerationDeleteSites(result)).WorkFrontierCapsuleId);

        Assert.Empty(result.Diagnostics);
    }

    // The construction entry: the instance initializers of a type and its base constructor run before the body of
    // whichever constructor builds it.
    [Theory]
    [InlineData("_ = new Holder();", "sealed class Holder { private int value = Compute(); public Holder() { } private static int Compute() { {E} return 1; } }")]
    [InlineData("_ = new Holder(1);", "sealed class Holder { private int value = Compute(); public Holder(int ignored) { } private static int Compute() { {E} return 1; } }")]
    [InlineData("_ = new Holder();", "sealed class Holder { public int Value { get; } = Compute(); public Holder() { } private static int Compute() { {E} return 1; } }")]
    [InlineData("_ = new Holder();", "sealed class Holder { public int Value { get; } = Compute(); private static int Compute() { {E} return 1; } }")]
    [InlineData("_ = new Holder();", "sealed class Holder { private System.Action? handler = null; public event System.Action Changed = Create(); private static System.Action Create() { {E} return () => { }; } }")]
    [InlineData("_ = new Holder();", "class Base { public Base() { {E} } } sealed class Holder : Base { public Holder() { } }")]
    [InlineData("_ = new Holder();", "class Base { public Base() { {E} } } sealed class Holder : Base { }")]
    [InlineData("_ = new Holder();", "class Base { protected Base(int value = 0) { {E} } } sealed class Holder : Base { }")]
    [InlineData("_ = new Holder();", "class Base { protected Base() { {E} } } class Middle : Base { } sealed class Holder : Middle { }")]
    [InlineData("_ = new Holder(1);", "sealed class Holder(int seed) { private int value = Compute(seed); private static int Compute(int seed) { {E} return seed; } }")]
    [InlineData("_ = new Holder(1);", "class Base { public Base(int seed) { {E} } } sealed class Holder(int seed) : Base(seed) { }")]
    [InlineData("_ = new Holder();", "record Base { public Base() { {E} } } sealed record Holder : Base;")]
    [InlineData("_ = new Holder();", "sealed class Holder { private int value = Compute(); public Holder() : this(1) { } public Holder(int ignored) { } private static int Compute() { {E} return 1; } }")]
    public void ConstructionEntryRunsInitializersAndTheBaseConstructor(string statement, string helpers)
    {
        HostedProducerDiscovery<HostedProducerSite> reported = EnumerationDiscover(CensusCompileSource(statement, helpers.Replace("{E}", EnumerationEffect, StringComparison.Ordinal)));

        HostedProducerSite site = Assert.Single(EnumerationDeleteSites(reported));

        Assert.NotNull(site.WorkFrontierCapsuleId);

        HostedProducerDiscovery<HostedProducerSite> unadmitted = EnumerationDiscover(CensusCompileSource(statement, helpers.Replace("{E}", EnumerationEffect, StringComparison.Ordinal), admitted: false));

        Assert.Null(Assert.Single(EnumerationDeleteSites(unadmitted)).WorkFrontierCapsuleId);

        HostedProducerDiscovery<HostedProducerSite> clean = EnumerationDiscover(CensusCompileSource(statement, helpers.Replace("{E}", string.Empty, StringComparison.Ordinal)));

        Assert.Empty(clean.Diagnostics);

        Assert.Empty(EnumerationDeleteSites(clean));
    }

    // Static initializers and constants are not part of an instance construction.
    [Fact]
    public void StaticInitializersAreNotPartOfTheInstanceConstruction()
    {
        string helpers = "sealed class Holder { private static int shared = Compute(); public const int Constant = 1; private static int Compute() { " + EnumerationEffect + "return 1; } }";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(CensusCompileSource("_ = new Holder();", helpers));

        Assert.Empty(EnumerationDeleteSites(result));
    }

    // A lease disposed before the creation has ended the Work its initializers run under.
    [Fact]
    public void ConstructionEntryIsJudgedAtTheCreation()
    {
        string helpers = "sealed class Holder { private int value = Compute(); private static int Compute() { " + EnumerationEffect + "return 1; } }";

        HostedProducerSite lost = Assert.Single(EnumerationDeleteSites(EnumerationDiscover(CensusCompileSource("lease.Dispose(); _ = new Holder();", helpers))));

        Assert.Null(lost.WorkFrontierCapsuleId);

        HostedProducerSite kept = Assert.Single(EnumerationDeleteSites(EnumerationDiscover(CensusCompileSource("_ = new Holder(); lease.Dispose();", helpers))));

        Assert.NotNull(kept.WorkFrontierCapsuleId);
    }

    // The call inside a local function is analysed where the local function is called.
    [Theory]
    [InlineData("void Run() { var (a, b) = new Box(); _ = a + b; } Run();", true)]
    [InlineData("void Run() { var (a, b) = new Box(); _ = a + b; } lease.Dispose(); Run();", false)]
    [InlineData("void Run() { var (a, b) = new Box(); _ = a + b; }", null)]
    public void ImplicitCallInALocalFunctionIsAnalysedWhereTheLocalFunctionIsCalled(string statement, bool? keepsWork)
    {
        string helpers = "sealed class Box { public void Deconstruct(out int a, out int b) { " + EnumerationEffect + "a = 1; b = 2; } }";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(CensusCompileSource(statement, helpers));

        HostedProducerSite[] sites = EnumerationDeleteSites(result);

        if (keepsWork is null)
        {
            Assert.Empty(sites);
        }
        else
        {
            Assert.Equal(keepsWork, Assert.Single(sites).WorkFrontierCapsuleId is not null);
        }
    }

    // A System.Threading.Lock declared in source is authored code that lock would call, so it is refused; the framework's
    // own lock runs no authored code.
    [Fact]
    public void AuthoredLockLookalikeIsRefused()
    {
        string helpers = "namespace System.Threading { public sealed class Lock { public Scope EnterScope() { " + EnumerationEffect + "return default; } public ref struct Scope { public void Dispose() { } } } }";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(CensusCompileSource("lock (new System.Threading.Lock()) { }", helpers));

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail.Contains("System.Threading.Lock declared in source", StringComparison.Ordinal));
    }

    private static string FormatterHelpers(string effectIn) =>
        "interface IShape { } "
        + "sealed class Square : IShape { public override string ToString() { " + (effectIn == "Square" ? EnumerationEffect : string.Empty) + "return \"s\"; } } "
        + "sealed class Circle : IShape { } "
        + "sealed class Named { public override string ToString() { " + (effectIn == "Named" ? EnumerationEffect : string.Empty) + "return \"n\"; } } "
        + "sealed class Unrelated { public override string ToString() { " + (effectIn == "Unrelated" ? EnumerationEffect : string.Empty) + "return \"u\"; } } "
        + "sealed class Formatted : System.IFormattable { public string ToString(string? format, System.IFormatProvider? provider) { " + (effectIn == "Formatted" ? EnumerationEffect : string.Empty) + "return \"f\"; } } "
        + "sealed record Pair(int A) { public int A { get { " + (effectIn == "Pair" ? EnumerationEffect : string.Empty) + "return 1; } init { } } } "
        + "class Failure : System.Exception { public override string Message { get { " + (effectIn == "Failure.Message" ? EnumerationEffect : string.Empty) + "return \"m\"; } } } "
        + "static class Unproved { public static object Anything { get; set; } = null!; public static IShape Shape { get; set; } = null!; public static System.Exception Error { get; set; } = null!; public static string Text { get; set; } = null!; }";

    // A value whose runtime type nothing proves can be any authored type that formats with code of its own and is
    // assignable to the static type, so each such type is followed, and a type that cannot be the value is not.
    [Theory]
    [InlineData("object value = Unproved.Anything; string text = $\"{value}\"; _ = text;", "Named", true)]
    [InlineData("object value = Unproved.Anything; string text = $\"{value}\"; _ = text;", "Pair", true)]
    [InlineData("object value = Unproved.Anything; string text = $\"{value:x}\"; _ = text;", "Formatted", true)]
    [InlineData("object value = Unproved.Anything; string text = $\"{value}\"; _ = text;", "none", false)]
    [InlineData("string text = \"a\" + Unproved.Anything; _ = text;", "Named", true)]
    [InlineData("string text = \"a\"; text += Unproved.Anything; _ = text;", "Named", true)]
    [InlineData("IShape shape = Unproved.Shape; string text = $\"{shape}\"; _ = text;", "Square", true)]
    [InlineData("IShape shape = Unproved.Shape; string text = $\"{shape}\"; _ = text;", "Unrelated", false)]
    [InlineData("IShape shape = Unproved.Shape; string text = $\"{shape}\"; _ = text;", "Named", false)]
    [InlineData("System.Exception error = Unproved.Error; string text = $\"{error}\"; _ = text;", "Failure.Message", true)]
    [InlineData("System.Exception error = Unproved.Error; string text = $\"{error}\"; _ = text;", "Named", false)]
    [InlineData("string text = $\"{Unproved.Text}\"; _ = text;", "Named", false)]
    public void UnprovenFormattedValueIsFollowedToTheAuthoredTypesThatCanBeIt(string statement, string effectIn, bool reported)
    {
        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(CensusCompileSource(statement, FormatterHelpers(effectIn)));

        HostedProducerSite[] sites = EnumerationDeleteSites(result);

        Assert.Equal(reported, sites.Length == 1);

        if (reported)
        {
            Assert.NotNull(sites[0].WorkFrontierCapsuleId);
        }

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED");
    }

    // The base constructor of an EF context keeps its options and runs no EF service code; the base constructor of
    // another framework class is not reviewed by that rule.
    [Fact]
    public void EfContextBaseConstructorIsReviewedAndAnotherFrameworkBaseConstructorIsNot()
    {
        string context = "sealed class Ctx : Microsoft.EntityFrameworkCore.DbContext { public Ctx(Microsoft.EntityFrameworkCore.DbContextOptions<Ctx> options) : base(options) { } }";

        string source = R2Source(R2Admission + "_ = new Ctx(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Ctx>().Options); await Task.CompletedTask;", context);

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Detail.Contains("DbContext..ctor", StringComparison.Ordinal));

        HostedProducerDiscovery<HostedProducerSite> other = EnumerationDiscover(CensusCompileSource("_ = new Marker();", "sealed class Marker : System.Attribute { }"));

        Assert.Contains(other.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail == "System.Attribute..ctor");
    }

    // Only the parameterless constructors of the reviewed in-memory framework types are reviewed; a lookalike type or
    // a constructor that takes a callback is not.
    [Theory]
    [InlineData("_ = new System.Threading.Lock();", true)]
    [InlineData("_ = new System.Threading.AsyncLocal<int>();", true)]
    [InlineData("_ = new System.Runtime.CompilerServices.ConditionalWeakTable<object, object>();", true)]
    [InlineData("_ = new System.Threading.AsyncLocal<int>(static _ => { });", false)]
    public void InMemoryFrameworkConstructionIsReviewedOnlyWithoutACallback(string statement, bool reviewed)
    {
        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(CensusCompileSource(statement, "sealed class Unused { }"));

        Assert.Equal(!reviewed, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"));
    }

    // The Length read of an index from the end through a framework collection interface runs a framework
    // implementation only once the universe declares no authored enumerable; strict discovery refuses it, and an
    // authored enumerable anywhere withholds the premise.
    [Theory]
    [InlineData("var last = UnprovedEnumerableInput.List[^1]; _ = last;", "System.Collections.Generic.IReadOnlyCollection`1.get_Count")]
    [InlineData("if (UnprovedEnumerableInput.List is [1, ..]) { }", "System.Collections.Generic.IReadOnlyCollection`1.get_Count")]
    public void AuthoredEnumerablesAbsentClassifiesTheLengthReadOfAFrameworkCollectionInterface(string statement, string callee)
    {
        CSharpCompilation compilation = EnumerationPremiseFixture(statement);

        HostedProducerDiscovery<HostedProducerSite> strict = PremiseStrict(compilation);

        Assert.Contains(strict.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail == callee);

        HostedProducerDiscovery<HostedProducerSite> premised = EnumerationPremiseRequested(compilation);

        Assert.DoesNotContain(premised.Diagnostics, diagnostic => diagnostic.Detail == callee);

        CSharpCompilation withheld = EnumerationPremiseFixture(statement, "sealed class Elsewhere : System.Collections.Generic.IEnumerable<int> { public System.Collections.Generic.IEnumerator<int> GetEnumerator() => throw new NotSupportedException(); System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator(); }");

        Assert.Contains(EnumerationPremiseRequested(withheld).Diagnostics, diagnostic => diagnostic.Detail == callee);
    }

    // An awaiter whose type nothing fixes can run any implementation of its members, so it is refused.
    [Fact]
    public void AwaiterOfAnUnfixedTypeIsRefused()
    {
        string helpers = "interface IAwaiterLike : System.Runtime.CompilerServices.INotifyCompletion { bool IsCompleted { get; } void GetResult(); } sealed class Later { public IAwaiterLike GetAwaiter() => null!; }";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(CensusCompileSource("await new Later();", helpers));

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail.Contains("IAwaiterLike", StringComparison.Ordinal));
    }

    // A reviewed framework identity is exact: an authored type that merely shares a framework type's name is judged
    // by its own members, which are traversed.
    [Theory]
    [InlineData("await new Later();", "namespace System.Runtime.CompilerServices { public struct TaskAwaiter : INotifyCompletion { public bool IsCompleted { get { " + EnumerationEffect + "return true; } } public void GetResult() { } public void OnCompleted(System.Action continuation) { } } } sealed class Later { public System.Runtime.CompilerServices.TaskAwaiter GetAwaiter() => default; }")]
    [InlineData("var (key, value) = new System.Collections.Generic.KeyValuePair<int, int>(); _ = key + value;", "namespace System.Collections.Generic { public struct KeyValuePair<TKey, TValue> { public void Deconstruct(out TKey key, out TValue value) { " + EnumerationEffect + "key = default!; value = default!; } } }")]
    [InlineData("string text = $\"{new System.Guid()}\"; _ = text;", "namespace System { public struct Guid { public override string ToString() { " + EnumerationEffect + "return \"g\"; } } }")]
    public void AuthoredTypeSharingAFrameworkNameIsJudgedByItsOwnMembers(string statement, string helpers)
    {
        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(CensusCompileSource(statement, helpers));

        Assert.NotNull(Assert.Single(EnumerationDeleteSites(result)).WorkFrontierCapsuleId);
    }

    // A struct has no base constructor to call, and the construction entry of a record struct runs only its initializers.
    [Fact]
    public void StructConstructionRunsNoBaseConstructor()
    {
        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(CensusCompileSource("_ = new Plain(1); _ = new Shaped(1, 2);", "readonly record struct Plain(int A); struct Shaped { public int A; public Shaped(int a, int b) { A = a + b; } }"));

        Assert.Empty(result.Diagnostics);
    }

    // A framework member that is handed a value whose static type hides that it is enumerable can enumerate any
    // authored enumerable assignable to that type, so the member is judged by those and by no other.
    [Theory]
    [InlineData("object value = Unproved.Anything; System.GC.KeepAlive(value);", "MoveNext", true)]
    [InlineData("object value = Unproved.Anything; System.GC.KeepAlive(value);", "none", false)]
    [InlineData("IShape shape = Unproved.Shape; System.GC.KeepAlive(shape);", "MoveNext", true)]
    [InlineData("System.IDisposable disposable = Unproved.Disposable; System.GC.KeepAlive(disposable);", "MoveNext", false)]
    public void FrameworkMemberHandedAValueThatHidesAnAuthoredEnumerableIsJudgedByIt(string statement, string effectIn, bool refused)
    {
        string helpers = "interface IShape { } sealed class Probe : System.Collections.Generic.IEnumerable<int>, IShape { public System.Collections.Generic.IEnumerator<int> GetEnumerator() => new ProbeEnumerator(); System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator(); } "
            + "sealed class ProbeEnumerator : System.Collections.Generic.IEnumerator<int> { public bool MoveNext() { " + (effectIn == "MoveNext" ? EnumerationEffect : string.Empty) + "return false; } public int Current => 0; object System.Collections.IEnumerator.Current => Current; public void Reset() { } public void Dispose() { } } "
            + "static class Unproved { public static object Anything { get; set; } = null!; public static IShape Shape { get; set; } = null!; public static System.IDisposable Disposable { get; set; } = null!; }";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(CensusCompileSource(statement, helpers));

        Assert.Equal(refused, EnumerationOwnershipRefused(result, "System.GC.KeepAlive"));
    }

    // A nullable value, a tuple, a pair and a record that prints an object member format what they hold; a record
    // whose object member can be the record itself is followed once.
    [Theory]
    [InlineData("Sized? value = new Sized(); string text = $\"{value}\"; _ = text;")]
    [InlineData("(Sized, int) value = (new Sized(), 1); string text = $\"{value}\"; _ = text;")]
    [InlineData("var value = new System.Collections.Generic.KeyValuePair<int, Sized>(1, new Sized()); string text = $\"{value}\"; _ = text;")]
    [InlineData("string text = $\"{new Wrapper(new Sized())}\"; _ = text;")]
    [InlineData("string text = $\"{new Wrapper(new Wrapper(new Sized()))}\"; _ = text;")]
    public void FormattedContainerFormatsWhatItHolds(string statement)
    {
        string helpers = "struct Sized { public override string ToString() { " + EnumerationEffect + "return \"s\"; } } sealed record Wrapper(object Inner);";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(CensusCompileSource(statement, helpers));

        Assert.NotNull(Assert.Single(EnumerationDeleteSites(result)).WorkFrontierCapsuleId);

        HostedProducerDiscovery<HostedProducerSite> clean = EnumerationDiscover(CensusCompileSource(statement, helpers.Replace(EnumerationEffect, string.Empty, StringComparison.Ordinal)));

        Assert.Empty(clean.Diagnostics);
    }

    // A spread over an operand nothing proves, in an initializer that the construction runs, is refused at the spread;
    // the same spread over a closed collection is not.
    [Theory]
    [InlineData("static class Unproved { public static System.Collections.Generic.IEnumerable<int> Items { get; set; } = null!; } sealed class Holder { private int[] values = [.. Unproved.Items]; }", true)]
    [InlineData("sealed class Holder { private int[] values = [.. new System.Collections.Generic.List<int>()]; }", false)]
    public void UnprovenSpreadInAnInitializerIsRefused(string helpers, bool refused)
    {
        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(CensusCompileSource("_ = new Holder();", helpers));

        Assert.Equal(refused, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"));
    }

    // An opaque callback that enumerates an operand nothing proves can run any enumerator, so the callback is refused;
    // the same callback over a closed collection is not.
    [Theory]
    [InlineData("foreach (int item in Unproved.Items) { _ = item; }", true)]
    [InlineData("foreach (int item in new System.Collections.Generic.List<int>()) { _ = item; }", false)]
    public void OpaqueCallbackThatEnumeratesAnUnprovenOperandIsRefused(string loop, bool refused)
    {
        string helpers = "static class Unproved { public static System.Collections.Generic.IEnumerable<int> Items { get; set; } = null!; }";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(CensusCompileSource("GC.KeepAlive((Action)(() => { " + loop + " }));", helpers));

        Assert.Equal(refused, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"));
    }

    // The base constructor of the Serilog file lifecycle hooks keeps nothing and runs nothing; the base constructor of
    // another external class is not reviewed by that rule.
    [Fact]
    public void SerilogFileHooksBaseConstructorIsReviewed()
    {
        string hooks = "sealed class Hooks : Serilog.Sinks.File.FileLifecycleHooks { }";

        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(R2Admission + "_ = new Hooks(); await Task.CompletedTask;", hooks), "RetroDownfall.Arcanum.Infrastructure");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Detail.Contains("FileLifecycleHooks..ctor", StringComparison.Ordinal));
    }

    // A type whose initializer creates the type, or formats a value that can be the type, asks for its own scan while it
    // is being scanned; the request ends where it began and the analysis terminates.
    [Theory]
    [InlineData("_ = new Node();", "sealed class Node { private readonly Node? next = new Node(); }")]
    [InlineData("_ = new Node();", "sealed class Node { private readonly string text = $\"{Unproved.Anything}\"; public override string ToString() => $\"{Unproved.Anything}\" + text; } static class Unproved { public static object Anything { get; set; } = null!; }")]
    [InlineData("string text = $\"{Unproved.Anything}\"; _ = text;", "sealed class Node { public override string ToString() => $\"{Unproved.Anything}\"; } static class Unproved { public static object Anything { get; set; } = null!; }")]
    public void SelfReferentialInitializersAndFormattersTerminate(string statement, string helpers)
    {
        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(CensusCompileSource(statement, helpers));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_TRAVERSAL_STATE_LIMIT_EXCEEDED");
    }

    // An opaque callback that creates a type is judged by the construction the creation runs, an explicit constructor's
    // initializers and base constructor included.
    [Theory]
    [InlineData("sealed class Holder { private int value = Compute(); public Holder() { } private static int Compute() { {E} return 1; } }")]
    [InlineData("sealed class Holder { public int Value { get; } = Compute(); public Holder(int ignored) { } private static int Compute() { {E} return 1; } }")]
    [InlineData("class Base { public Base() { {E} } } sealed class Holder : Base { public Holder() { } }")]
    [InlineData("sealed class Holder { private System.Collections.Generic.List<int> items = [.. Items()]; public Holder() { } private static System.Collections.Generic.IEnumerable<int> Items() { {E} yield return 1; } }")]
    public void OpaqueCallbackThatCreatesATypeIsJudgedByItsConstructionEntry(string helpers)
    {
        string creation = helpers.Contains("int ignored", StringComparison.Ordinal) ? "new Holder(1)" : "new Holder()";

        string statement = "GC.KeepAlive((Action)(() => { _ = " + creation + "; }));";

        HostedProducerDiscovery<HostedProducerSite> refused = EnumerationDiscover(CensusCompileSource(statement, helpers.Replace("{E}", EnumerationEffect, StringComparison.Ordinal)));

        Assert.Contains(refused.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");

        HostedProducerDiscovery<HostedProducerSite> clean = EnumerationDiscover(CensusCompileSource(statement, helpers.Replace("{E}", string.Empty, StringComparison.Ordinal)));

        Assert.DoesNotContain(clean.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    // An erroneous call is refused where the invocation lane meets it (the call has no exact target), so the census does
    // not refuse the same site a second time; any other erroneous operation has no lane and is refused by the census.
    [Theory]
    [InlineData("string[] values = [\"one\"]; _ = values.NoSuchMethod(1);", "HOSTED_CALL_TARGET_UNRESOLVED", false)]
    [InlineData("string[] values = [\"one\"]; _ = values.Skip(0).Contains(\"one\", StringComparer.Ordinal);", "HOSTED_CALL_TARGET_UNRESOLVED", false)]
    [InlineData("_ = new Missing();", "HOSTED_SITE_UNCLASSIFIED", true)]
    [InlineData("_ = Missing.Value;", "HOSTED_SITE_UNCLASSIFIED", true)]
    public void ErroneousOperationIsRefusedOnceByTheLaneThatOwnsIt(string body, string code, bool byCensus)
    {
        CSharpCompilation compilation = Compile(R2Source(R2Admission + body + " await Task.CompletedTask;", string.Empty));

        Assert.Contains(compilation.GetDiagnostics(), static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);

        Assert.Equal(byCensus, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail == "An erroneous operation has no exact target."));
    }

    // The runes of a span are enumerated by a framework enumerator the production reference pack declares in the
    // System.Memory contract assembly; it keeps nothing, runs nothing and cannot be overridden, so the loop is closed.
    // An authored enumerator of the same shape is not: the loop runs its members.
    [Theory]
    [InlineData("_ = Runes.Sum(\"ab\");", "static class Runes { public static int Sum(System.ReadOnlySpan<char> text) { int total = 0; foreach (System.Text.Rune rune in text.EnumerateRunes()) { total += rune.Value; } return total; } }", false)]
    [InlineData("_ = Runes.Sum(\"ab\");", "static class Runes { public static int Sum(string text) { int total = 0; foreach (System.Text.Rune rune in text.EnumerateRunes()) { total += rune.Value; } return total; } }", false)]
    [InlineData("_ = Runes.Sum();", "static class Runes { public static int Sum() { int total = 0; foreach (int rune in new Fake().EnumerateRunes()) { total += rune; } return total; } } sealed class Fake { public Enumerator EnumerateRunes() => new(); public struct Enumerator { public Enumerator GetEnumerator() => this; public bool MoveNext() { {E} return false; } public int Current => 0; } }", true)]
    public void SpanRuneEnumerationIsClosedOnlyForTheFrameworkType(string statement, string helpers, bool reported)
    {
        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(R2Admission + statement + " await Task.CompletedTask;", helpers.Replace("{E}", EnumerationEffect, StringComparison.Ordinal)), "RetroDownfall.Arcanum.Infrastructure");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        string diagnostics = string.Join(" | ", result.Diagnostics.Select(static diagnostic => diagnostic.Code + " " + diagnostic.Identity + " " + diagnostic.Detail));

        Assert.True(reported == result.Items.Any(static site => site.Callee == "System.IO.File.Delete"), diagnostics);

        Assert.True(reported || result.Diagnostics.Count == 0, diagnostics);
    }

    // The in-memory framework calls of the bodies the census reaches are reviewed by exact identity. The base64url
    // encoder is a static transform of the caller's spans; the number formatter writes digits into the caller's span
    // and runs supplied code only through a provider the caller passed. An authored type of the same name as the encoder
    // is not the framework one: its body is traversed and its effect reported.
    [Theory]
    [InlineData("System.Span<byte> encoded = new byte[8]; _ = System.Buffers.Text.Base64Url.EncodeToUtf8(new byte[1], encoded, out _, out int written); _ = written;", "", false)]
    [InlineData("System.Span<char> destination = new char[8]; uint version = 7; _ = version.TryFormat(destination, out int written); _ = written;", "", false)]
    [InlineData("System.Span<byte> encoded = new byte[8]; _ = System.Buffers.Text.Base64Url.EncodeToUtf8(new byte[1], encoded, out _, out int written); _ = written;", "namespace System.Buffers.Text { public static class Base64Url { public static System.Buffers.OperationStatus EncodeToUtf8(System.ReadOnlySpan<byte> source, System.Span<byte> destination, out int consumed, out int written, bool isFinalBlock = true) { {E} consumed = 0; written = 0; return System.Buffers.OperationStatus.Done; } } }", true)]
    public void InMemoryFrameworkFormattingMembersAreReviewedByExactIdentity(string statement, string helpers, bool reported)
    {
        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(R2Admission + statement + " await Task.CompletedTask;", helpers.Replace("{E}", EnumerationEffect, StringComparison.Ordinal)), "RetroDownfall.Arcanum.Infrastructure");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        string diagnostics = string.Join(" | ", result.Diagnostics.Select(static diagnostic => diagnostic.Code + " " + diagnostic.Identity + " " + diagnostic.Detail));

        Assert.Equal(reported, result.Items.Any(static site => site.Callee == "System.IO.File.Delete"));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_SITE_UNCLASSIFIED" or "HOSTED_CALL_TARGET_UNRESOLVED");

        Assert.True(reported || result.Diagnostics.Count == 0, diagnostics);
    }

    // The default-provider rule is exact: a provider the caller passed, even the invariant culture, keeps the call refused.
    [Theory]
    [InlineData("new Provider()", "sealed class Provider : IFormatProvider { public object? GetFormat(Type? formatType) => null; }")]
    [InlineData("System.Globalization.CultureInfo.InvariantCulture", "")]
    public void PassedFormatProviderKeepsUInt32FormattingRefused(string provider, string helpers)
    {
        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(R2Admission + "System.Span<char> destination = new char[8]; uint version = 7; _ = version.TryFormat(destination, out int written, default, " + provider + "); await Task.CompletedTask;", helpers), "RetroDownfall.Arcanum.Infrastructure");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail == "System.UInt32.TryFormat");
    }

    // The ToString of an AI tool returns its virtual Name, so formatting a tool runs the Name of the exact tool. A hole
    // of an exact tool type and a hole nothing proves (it can be any authored tool) both traverse the Name of the tool,
    // and the framework ToString itself is reviewed for no explicit call: a written call, which does not model Name,
    // stays refused.
    [Theory]
    [InlineData("_ = $\"tool:{Anything.Typed}\";", true)]
    [InlineData("_ = $\"tool:{Anything.Value}\";", true)]
    [InlineData("_ = $\"tool:{Anything.Typed}\";", false)]
    [InlineData("_ = $\"tool:{Anything.Value}\";", false)]
    public void FormattingAnAiToolRunsItsNameOverride(string statement, bool effect)
    {
        string helpers = "sealed class Tool : Microsoft.Extensions.AI.AIFunction { public override string Name { get { " + (effect ? EnumerationEffect : string.Empty) + "return \"tool\"; } } protected override System.Threading.Tasks.ValueTask<object?> InvokeCoreAsync(Microsoft.Extensions.AI.AIFunctionArguments arguments, System.Threading.CancellationToken cancellationToken) => default; } static class Anything { public static object Value { get; set; } = null!; public static Tool Typed { get; set; } = null!; }";

        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(R2Admission + statement + " await Task.CompletedTask;", helpers), "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        string diagnostics = string.Join(" | ", result.Diagnostics.Select(static diagnostic => diagnostic.Code + " " + diagnostic.Identity + " " + diagnostic.Detail));

        Assert.Equal(effect, result.Items.Any(static site => site.Callee == "System.IO.File.Delete"));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_SITE_UNCLASSIFIED" or "HOSTED_CALL_TARGET_UNRESOLVED");

        Assert.True(effect || result.Diagnostics.Count == 0, diagnostics);
    }

    [Fact]
    public void WrittenToStringCallOnAnAiToolStaysRefused()
    {
        string helpers = "sealed class Tool : Microsoft.Extensions.AI.AIFunction { public override string Name => \"tool\"; protected override System.Threading.Tasks.ValueTask<object?> InvokeCoreAsync(Microsoft.Extensions.AI.AIFunctionArguments arguments, System.Threading.CancellationToken cancellationToken) => default; } static class Anything { public static Tool Typed { get; set; } = null!; }";

        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(R2Admission + "_ = Anything.Typed.ToString(); await Task.CompletedTask;", helpers), "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        Assert.Contains(EnumerationDiscover(compilation).Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail == "Microsoft.Extensions.AI.AITool.ToString");
    }

    // The base constructor an authored stream chains to, written (`: base(...)`) or implicit, is the framework stream
    // constructor, a file-system site: it is reported under the frontiers live where the creation runs it, and the
    // creation of a stream that chains to nothing of the framework is not.
    [Theory]
    [InlineData("_ = new Reader(default!);", "sealed class Reader : System.IO.FileStream { public Reader(Microsoft.Win32.SafeHandles.SafeFileHandle handle) : base(handle, System.IO.FileAccess.Read) { } }", "System.IO.FileStream..ctor", true)]
    [InlineData("_ = new Plain();", "sealed class Plain : System.IO.Stream { public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => false; public override long Length => 0; public override long Position { get => 0; set { } } public override void Flush() { } public override int Read(byte[] buffer, int offset, int count) => 0; public override long Seek(long offset, System.IO.SeekOrigin origin) => 0; public override void SetLength(long value) { } public override void Write(byte[] buffer, int offset, int count) { } }", "System.IO.Stream..ctor", true)]
    [InlineData("_ = new Quiet();", "sealed class Quiet { }", "", false)]
    public void AuthoredStreamBaseConstructorIsAFileSystemSite(string statement, string helpers, string callee, bool reported)
    {
        HostedProducerDiscovery<HostedProducerSite> admitted = EnumerationDiscover(CensusCompileSource(statement, helpers));

        HostedProducerSite[] sites = [.. admitted.Items.Where(site => site.Callee == callee && callee.Length != 0)];

        Assert.Equal(reported, sites.Length != 0);

        Assert.All(sites, static site => Assert.True(site.Kind == HostedProducerSiteKind.FileSystemEffect && site.WorkFrontierCapsuleId is not null && site.EffectFrontierCapsuleId is not null));

        if (!reported)
        {
            Assert.Empty(admitted.Diagnostics);

            return;
        }

        HostedProducerDiscovery<HostedProducerSite> unadmitted = EnumerationDiscover(CensusCompileSource(statement, helpers, admitted: false));

        Assert.All(unadmitted.Items.Where(site => site.Callee == callee), static site => Assert.Null(site.WorkFrontierCapsuleId));

        Assert.Contains(unadmitted.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING" && diagnostic.Detail == callee);
    }

    private const string AuthoredSequence = "sealed class Seq : System.Collections.Generic.IEnumerable<int> { public System.Collections.Generic.IEnumerator<int> GetEnumerator() { {E} return new System.Collections.Generic.List<int>().GetEnumerator(); } System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator(); } ";

    // A framework member handed an authored sequence enumerates it, so the member that does so is refused wherever it is
    // written: in a body, in an instance initializer, and in the formatter of a value nothing proves, which the
    // construction and the formatting run only when a scan says they can reach a producer site. The scan has to see the
    // refusal the traversal would make, so the pruned member is not dropped; the same member over a closed collection is
    // refused nowhere.
    [Theory]
    [InlineData("var values = new System.Collections.Generic.List<int>(new Seq()); _ = values;", "", true)]
    [InlineData("_ = new Holder();", "sealed class Holder { private System.Collections.Generic.List<int> values = new System.Collections.Generic.List<int>(new Seq()); } ", true)]
    [InlineData("_ = new Holder();", "sealed class Holder { public System.Collections.Generic.List<int> Values { get; } = new System.Collections.Generic.List<int>(new Seq()); } ", true)]
    [InlineData("string text = $\"{new Named()}\"; _ = text;", "sealed class Named { public override string ToString() { var values = new System.Collections.Generic.List<int>(new Seq()); return values.Count.ToString(); } } ", true)]
    [InlineData("IPruned value = Unproved.Value; string text = $\"{value}\"; _ = text;", "interface IPruned { } sealed class Named : IPruned { public override string ToString() { var values = new System.Collections.Generic.List<int>(new Seq()); return values.Count.ToString(); } } static class Unproved { public static IPruned Value { get; set; } = null!; } ", true)]
    [InlineData("_ = new Holder();", "sealed class Holder { private System.Collections.Generic.List<int> values = new System.Collections.Generic.List<int>(new int[] { 1 }); } ", false)]
    [InlineData("IPruned value = Unproved.Value; string text = $\"{value}\"; _ = text;", "interface IPruned { } sealed class Named : IPruned { public override string ToString() { var values = new System.Collections.Generic.List<int>(new int[] { 1 }); return values.Count.ToString(); } } static class Unproved { public static IPruned Value { get; set; } = null!; } ", false)]
    public void FrameworkMemberEnumeratingAnAuthoredSequenceIsRefusedWhereverItIsWritten(string statement, string helpers, bool refused)
    {
        foreach (bool effect in new[] { true, false })
        {
            HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(CensusCompileSource(statement, (AuthoredSequence + helpers).Replace("{E}", effect ? EnumerationEffect : string.Empty, StringComparison.Ordinal)));

            bool owned = result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN" && diagnostic.Detail.Contains("Opaque enumerable", StringComparison.Ordinal));

            Assert.Equal(refused && effect, owned);
        }
    }

    // The scan of a body is computed once, and what it concludes can depend on the bindings the body was reached with (a
    // callback that calls the delegate it was handed is judged by that delegate). A body reached with a clean delegate
    // and then with an effectful one, or the other way round, is refused each time it is reached with the effectful one:
    // the order of the two scans does not decide it.
    [Theory]
    [InlineData("Calls.Clean(); Calls.Dirty();")]
    [InlineData("Calls.Dirty(); Calls.Clean();")]
    [InlineData("Calls.Dirty();")]
    public void ScanOfOneBodyIsNotReusedAcrossDifferentBindings(string statement)
    {
        string helpers = "static class Host { public static void Run(System.Action action) { GC.KeepAlive((System.Action)(() => action())); } } "
            + "static class Calls { public static void Clean() { Host.Run(static () => { }); } public static void Dirty() { Host.Run(static () => { Effect.Run(); }); } } "
            + "static class Effect { public static void Run() { " + EnumerationEffect + " } }";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(CensusCompileSource(statement, helpers));

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    // Roslyn gives an expression written as checked(...), unchecked(...) or expression! no operation of its own, so a member
    // whose whole body is one has no tree for the census to walk from the body itself: the census unwraps it, and the
    // implicit calls inside are judged as they are anywhere else.
    [Theory]
    [InlineData("public int Value => checked(new Box() is (var a, var b) ? a + b : 0);")]
    [InlineData("public int Value => unchecked(new Box() is (var a, var b) ? a + b : 0);")]
    [InlineData("public int Value => (new Box() is (var a, var b) ? a + b : 0);")]
    [InlineData("public string Value => $\"{new Named()}\"!;")]
    [InlineData("public int Value { get => checked(new Box() is (var a, var b) ? a + b : 0); }")]
    public void ImplicitCallInAMemberWhoseWholeBodyIsAWrappedExpressionIsTraversed(string member)
    {
        string helpers = "sealed class Box { public void Deconstruct(out int a, out int b) { {E} a = 1; b = 2; } } sealed class Named { public override string ToString() { {E} return \"n\"; } } sealed class Holder { " + member + " }";

        HostedProducerDiscovery<HostedProducerSite> reported = EnumerationDiscover(CensusCompileSource("_ = new Holder().Value;", helpers.Replace("{E}", EnumerationEffect, StringComparison.Ordinal)));

        Assert.NotNull(Assert.Single(EnumerationDeleteSites(reported)).WorkFrontierCapsuleId);

        HostedProducerDiscovery<HostedProducerSite> clean = EnumerationDiscover(CensusCompileSource("_ = new Holder().Value;", helpers.Replace("{E}", string.Empty, StringComparison.Ordinal)));

        Assert.Empty(clean.Diagnostics);

        Assert.Empty(EnumerationDeleteSites(clean));
    }

    // The override of the runtime type is what runs, so the body of a slot that a runtime type overrides is not run, and the
    // override of a type that is not the runtime type is not run either; the plain member and the property that is not
    // overridden run the body of the slot.
    [Theory]
    [InlineData("Base probe = new Plain(); probe.Run();", "class Base { public virtual void Run() { {E} } } sealed class Derived : Base { public override void Run() { {E} } } sealed class Plain : Base { }", 1)]
    [InlineData("Base probe = new Derived(); probe.Run();", "class Base { public virtual void Run() { {E} } } sealed class Derived : Base { public override void Run() { } }", 0)]
    [InlineData("Base probe = new Plain(); probe.Run();", "class Base { public virtual void Run() { } } sealed class Derived : Base { public override void Run() { {E} } } sealed class Plain : Base { }", 0)]
    [InlineData("Base probe = new Plain(); _ = probe.Value;", "class Base { public virtual int Value => 0; } sealed class Derived : Base { public override int Value { get { {E} return 1; } } } sealed class Plain : Base { }", 0)]
    [InlineData("Base probe = new Plain(); _ = probe.Value;", "class Base { public virtual int Value { get { {E} return 0; } } } sealed class Derived : Base { public override int Value => 1; } sealed class Plain : Base { }", 1)]
    [InlineData("IProbe probe = new Plain(); _ = probe.Value;", "interface IProbe { int Value { get; } } sealed class Probe : IProbe { public int Value { get { {E} return 1; } } } sealed class Plain : IProbe { public int Value => 2; }", 0)]
    public void OverrideOfATypeThatIsNotTheRuntimeTypeIsNotRun(string statement, string helpers, int sites)
    {
        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(CensusCompileSource(statement, helpers.Replace("{E}", EnumerationEffect, StringComparison.Ordinal)));

        Assert.Equal(sites, EnumerationDeleteSites(result).Length);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_CALL_TARGET_UNRESOLVED" or "HOSTED_SITE_UNCLASSIFIED");
    }

    // Only the Add overloads one element can call are followed: an overload that needs a second argument is never selected, so
    // its body is not reported.
    [Fact]
    public void CollectionExpressionAddThatNeedsASecondArgumentIsNotSelected()
    {
        string helpers = "sealed class Bag : System.Collections.Generic.IEnumerable<int> { public void Add(int value) { } public void Add(int key, int value) { " + EnumerationEffect + "} public System.Collections.Generic.IEnumerator<int> GetEnumerator() => null!; System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => null!; }";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(CensusCompileSource("Bag bag = [1, 2]; _ = bag;", helpers));

        Assert.Empty(EnumerationDeleteSites(result));

        Assert.Empty(result.Diagnostics);
    }

    // A positional pattern over a value is judged by the runtime type the value is proved to have: an ITuple type that is
    // not that type is not run, whatever it does.
    [Theory]
    [InlineData("object value = new Plain(); if (value is (var a, var b)) { _ = a; _ = b; }")]
    [InlineData("System.Runtime.CompilerServices.ITuple value = new Plain(); if (value is (var a, var b)) { _ = a; _ = b; }")]
    public void PositionalPatternRunsOnlyTheTupleTypeTheValueHas(string statement)
    {
        string helpers = "sealed class Tup : System.Runtime.CompilerServices.ITuple { public int Length { get { " + EnumerationEffect + "return 2; } } public object? this[int index] => null; } "
            + "sealed class Plain : System.Runtime.CompilerServices.ITuple { public int Length => 2; public object? this[int index] => null; }";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(CensusCompileSource(statement, helpers));

        Assert.Empty(EnumerationDeleteSites(result));

        Assert.Empty(result.Diagnostics);
    }

    // A value handed to a parameter whose declared type admits none of the callback slots is not judged by them: an exception
    // constructor keeps its inner exception, and the formatting of it is the formatting of the exception.
    [Fact]
    public void ValueHandedToAParameterThatAdmitsNoCallbackIsNotJudged()
    {
        string helpers = "sealed class Evil : System.Exception { public override string Message { get { " + EnumerationEffect + "return \"m\"; } } }";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(CensusCompileSource("var error = new System.InvalidOperationException(\"x\", new Evil()); _ = error;", helpers));

        Assert.Empty(EnumerationDeleteSites(result));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN" or "HOSTED_SITE_UNCLASSIFIED");
    }

    // The static initialization of a type is started by a use of the type from another type; the type's own members run after
    // it has started, and a type whose static initialization reaches no site is used freely.
    [Theory]
    [InlineData("Holder.Run();", "static class Holder { private static readonly int shared = Compute(); public static void Run() { _ = shared; } private static int Compute() { {E} return 1; } }", true)]
    [InlineData("Holder.Run();", "static class Holder { private static readonly int shared = 1; public static void Run() { _ = shared; } }", false)]
    [InlineData("_ = Holder.Constant;", "static class Holder { public const int Constant = 1; private static readonly int shared = Compute(); private static int Compute() { {E} return 1; } }", false)]
    [InlineData("Other.Run();", "static class Holder { private static readonly int shared = Compute(); private static int Compute() { {E} return 1; } } static class Other { public static void Run() { } }", false)]
    public void StaticInitializationIsRefusedAtTheFirstUseFromAnotherType(string statement, string helpers, bool refused)
    {
        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(CensusCompileSource(statement, helpers.Replace("{E}", EnumerationEffect, StringComparison.Ordinal)));

        Assert.Equal(refused, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail.Contains(".cctor", StringComparison.Ordinal)));
    }

    // An accessor of a receiver that was constructed with a callable runs that callable, bound the way a written call to a method
    // of the receiver binds it: the getter that invokes the delegate its constructor kept reports the delegate's body.
    [Theory]
    [InlineData("var holder = new Holder(static () => { Effect.Run(); return 1; }); _ = holder.Value;", "sealed class Holder(System.Func<int> read) { public int Value => read(); }")]
    [InlineData("var holder = new Holder(static () => { Effect.Run(); return 1; }); _ = holder.Value;", "abstract class Source { public abstract int Value { get; } } sealed class Holder(System.Func<int> read) : Source { public override int Value => read(); }")]
    [InlineData("Source holder = new Holder(static () => { Effect.Run(); return 1; }); _ = holder.Value;", "abstract class Source { public abstract int Value { get; } } sealed class Holder(System.Func<int> read) : Source { public override int Value => read(); }")]
    public void AccessorOfAConstructedReceiverRunsTheCallableItWasConstructedWith(string statement, string helpers)
    {
        string source = helpers + " static class Effect { public static void Run() { " + EnumerationEffect + " } }";

        HostedProducerDiscovery<HostedProducerSite> reported = EnumerationDiscover(CensusCompileSource(statement, source));

        Assert.NotNull(Assert.Single(EnumerationDeleteSites(reported)).WorkFrontierCapsuleId);

        Assert.DoesNotContain(reported.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN" or "HOSTED_SITE_UNCLASSIFIED" or "HOSTED_CALL_TARGET_UNRESOLVED");

        HostedProducerDiscovery<HostedProducerSite> clean = EnumerationDiscover(CensusCompileSource(statement, source.Replace(EnumerationEffect, string.Empty, StringComparison.Ordinal)));

        Assert.Empty(EnumerationDeleteSites(clean));

        Assert.DoesNotContain(clean.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN" or "HOSTED_SITE_UNCLASSIFIED" or "HOSTED_CALL_TARGET_UNRESOLVED");
    }

    // A slot that several authored classes override, called through a receiver nothing proves, runs the override of the runtime type,
    // which is not known. The census refuses it when one of the overrides can reach a producer site, unless the slot is one whose
    // complete set of overriding classes is reviewed: every override of it is then traversed, and a class added to the set (or the
    // same shape under a name that is not reviewed) is refused again.
    [Theory]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security", "", false)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security", "sealed class ExtraMirror : CredentialMirror { public override Task ReadAsync(CancellationToken token) { System.IO.File.Delete(\"extra-effect\"); return Task.CompletedTask; } }", true)]
    [InlineData("RetroDownfall.Unreviewed", "", true)]
    public void ReviewedClosedClassDispatchTraversesEveryOverrideOrIsRefused(string space, string extra, bool refused)
    {
        string helpers = "namespace " + space + " { abstract class CredentialMirror { public abstract Task ReadAsync(CancellationToken token); } "
            + "sealed class ProtectedFileCredentialMirror : CredentialMirror { public override Task ReadAsync(CancellationToken token) { System.IO.File.Delete(\"mirror-effect\"); return Task.CompletedTask; } } "
            + "sealed class DataProtectionStoreCredentialMirror : CredentialMirror { public override Task ReadAsync(CancellationToken token) { System.IO.File.Exists(\"mirror-effect\"); return Task.CompletedTask; } } "
            + extra + " } static class Anything { public static " + space + ".CredentialMirror Value { get; set; } = null!; }";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(CensusCompileSource("await Anything.Value.ReadAsync(default);", helpers));

        bool unresolved = result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED" && diagnostic.Detail.EndsWith(".CredentialMirror.ReadAsync", StringComparison.Ordinal));

        Assert.Equal(refused, unresolved);

        if (!refused)
        {
            string[] callees = [.. result.Items.Where(static site => site.RootType == "Worker").Select(static site => site.Callee)];

            Assert.Contains("System.IO.File.Delete", callees);

            Assert.Contains("System.IO.File.Exists", callees);
        }
    }

    // A virtual member the framework declares is judged by the classification of the access, whatever the receiver is proved to be:
    // the census has no body of the framework's to follow, so it neither refuses the access as an unknown target nor looks for an
    // override in the source.
    [Theory]
    [InlineData("var stream = new System.IO.MemoryStream(); _ = stream.CanRead;")]
    [InlineData("System.IO.Stream stream = new System.IO.MemoryStream(); _ = stream.CanRead;")]
    [InlineData("var stream = new System.IO.MemoryStream(); _ = stream.Length;")]
    [InlineData("Microsoft.Data.Sqlite.SqliteTransaction? transaction = Anything.Value; _ = transaction?.Connection;")]
    [InlineData("using var connection = new Microsoft.Data.Sqlite.SqliteConnection(); using var transaction = connection.BeginTransaction(); _ = transaction.Connection;")]
    [InlineData("Use(Anything.Value); static void Use(Microsoft.Data.Sqlite.SqliteTransaction? transaction) { _ = transaction?.Connection; }")]
    [InlineData("using var connection = new Microsoft.Data.Sqlite.SqliteConnection(); var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)connection.BeginTransaction(); _ = transaction.Connection;")]
    [InlineData("using var connection = new Microsoft.Data.Sqlite.SqliteConnection(); Use((Microsoft.Data.Sqlite.SqliteTransaction)connection.BeginTransaction()); static void Use(Microsoft.Data.Sqlite.SqliteTransaction transaction) { _ = transaction.Connection; }")]
    [InlineData("await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(); Use((Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync()); static void Use(Microsoft.Data.Sqlite.SqliteTransaction transaction) { _ = transaction.Connection; }")]
    public void FrameworkAccessorIsJudgedByItsClassificationWhateverTheReceiverIsProvedToBe(string statement)
    {
        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(CensusCompileSource(statement, "static class Anything { public static Microsoft.Data.Sqlite.SqliteTransaction? Value { get; set; } }"));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_CALL_TARGET_UNRESOLVED" or "HOSTED_SITE_UNCLASSIFIED");
    }

    // A field or property that generated code declares (a tree under obj/) is not a tree the analysis holds, so a receiver read from one
    // is not followed to where it was assigned. The access is then resolved on the types the universe holds, and nothing throws.
    [Theory]
    [InlineData("private static readonly Base value = new Derived();", "value.Go();")]
    [InlineData("private static Base value { get; } = new Derived();", "value.Go();")]
    [InlineData("private static Base value => new Derived();", "value.Go();")]
    [InlineData("private static Base Make() => new Derived();", "Make().Go();")]
    [InlineData("private static Base Make() => new Derived();", "Base local = Make(); local.Go();")]
    [InlineData("private static readonly Base[] values = [new Derived()];", "values[0].Go();")]
    [InlineData("private static readonly System.Collections.Generic.List<Base> values = [new Derived()];", "foreach (Base item in values) { item.Go(); }")]
    public void ReceiverReadFromAMemberOfGeneratedCodeIsResolvedOnTheUniverse(string generated, string call)
    {
        string source = "abstract class Base { public abstract void Go(); } sealed class Derived : Base { public override void Go() { " + EnumerationEffect + " } } "
            + "static partial class Holder { public static void Run() { " + call + " } }";

        CSharpCompilation compilation = Compile("using System.Linq;\n" + R2Source(R2Admission + "Holder.Run(); await Task.CompletedTask;", source));

        compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("static partial class Holder { " + generated + " }", path: "src/obj/Holder.g.cs"));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        Assert.NotNull(Assert.Single(EnumerationDeleteSites(result)).WorkFrontierCapsuleId);
    }
}
