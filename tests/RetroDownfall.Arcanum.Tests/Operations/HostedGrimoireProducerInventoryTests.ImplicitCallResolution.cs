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
}
