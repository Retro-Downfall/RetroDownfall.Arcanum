using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

// The completeness claim of Implicit Enumeration: every authored member that a foreach, an await foreach, a
// spread or an enumerating framework member runs without a written call is either reported as a site under the
// frontiers live where it runs, or refused. Each shape here ran authored code unseen before it was modeled.
public sealed partial class HostedGrimoireProducerInventoryTests
{
    private static bool EnumerationRefusedAt(HostedProducerDiscovery<HostedProducerSite> result, CSharpCompilation compilation, string code = "HOSTED_SITE_UNCLASSIFIED")
    {
        SyntaxTree tree = compilation.SyntaxTrees.First(static candidate => candidate.FilePath == "src/Fixture.cs");

        SyntaxNode site = tree.GetRoot().DescendantNodes().First(static node => node is CommonForEachStatementSyntax or SpreadElementSyntax);

        return result.Diagnostics.Any(diagnostic => diagnostic.Code == code && diagnostic.Identity == tree.FilePath + ":" + site.SpanStart);
    }

    private static bool EnumerationOwnershipRefused(HostedProducerDiscovery<HostedProducerSite> result, string callee) =>
        result.Diagnostics.Any(diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
            && diagnostic.Detail.StartsWith(callee + ";", StringComparison.Ordinal));

    private static bool EnumerationAnyRefusal(HostedProducerDiscovery<HostedProducerSite> result) =>
        result.Diagnostics.Any(static diagnostic => diagnostic.Code is "HOSTED_SITE_UNCLASSIFIED" or "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN" or "HOSTED_CALL_TARGET_UNRESOLVED");

    // ---------------------------------------------------------------------------------------------------
    // An iterator method used directly as the loop operand runs its body on every MoveNext, so a lease
    // disposed inside the loop body ends its Work.

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, true, false)]
    public void IteratorOperandKeepsItsWorkOnlyWhileTheLeaseOutlivesTheLoop(bool asynchronous, bool disposesInBody, bool throughLocal, bool keepsWork)
    {
        string helpers = asynchronous
            ? "static class Source { internal static async System.Collections.Generic.IAsyncEnumerable<int> Items() { yield return 1; await Task.CompletedTask; " + EnumerationEffect + "yield return 2; } }"
            : "static class Source { internal static System.Collections.Generic.IEnumerable<int> Items() { yield return 1; " + EnumerationEffect + "yield return 2; } }";

        string loop = (asynchronous ? "await " : string.Empty) + "foreach (int item in " + (throughLocal ? "items" : "Source.Items()") + ") { _ = item; " + (disposesInBody ? "lease.Dispose(); " : string.Empty) + "}";

        string body = R2Admission + (throughLocal ? "var items = Source.Items(); " : string.Empty) + loop + " await Task.CompletedTask;";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(body, helpers));

        HostedProducerSite site = Assert.Single(EnumerationDeleteSites(result));

        Assert.Equal(keepsWork, site.WorkFrontierCapsuleId is not null);

        Assert.Equal(!keepsWork, EnumerationFrontierMissing(result, "HOSTED_SITE_WORK_FRONTIER_MISSING"));
    }

    [Fact]
    public void IteratorOperandKeepsItsWorkWhenTheLeaseIsDisposedAfterTheLoop()
    {
        string helpers = "static class Source { internal static System.Collections.Generic.IEnumerable<int> Items() { yield return 1; " + EnumerationEffect + "yield return 2; } }";

        string body = R2Admission + "foreach (int item in Source.Items()) { _ = item; } lease.Dispose(); await Task.CompletedTask;";

        HostedProducerSite site = Assert.Single(EnumerationDeleteSites(EnumerationDiscover(EnumerationCompile(body, helpers))));

        Assert.NotNull(site.WorkFrontierCapsuleId);
    }

    // ---------------------------------------------------------------------------------------------------
    // The members the compiler selected run whatever the closure of the operand is: an authored extension or
    // static GetEnumerator is chosen by the operand's static type, not by what the operand can be at run time.

    private const string ExtensionAsyncSource = "static class Source { internal static async System.Collections.Generic.IAsyncEnumerable<int> Items() { await Task.CompletedTask; yield return 1; } } ";

    private static string ExtensionSyncEnumerator(string effectMember) =>
        "struct ExtEnumerator : System.IDisposable { "
        + "public bool MoveNext() { " + EnumerationEffectIn(effectMember, "MoveNext") + "return false; } "
        + "public int Current { get { " + EnumerationEffectIn(effectMember, "Current") + "return 0; } } "
        + "public void Dispose() { " + EnumerationEffectIn(effectMember, "Dispose") + "} }";

    [Theory]
    [InlineData("GetEnumerator", true)]
    [InlineData("GetEnumerator", false)]
    [InlineData("MoveNext", true)]
    [InlineData("MoveNext", false)]
    [InlineData("Current", true)]
    [InlineData("Current", false)]
    [InlineData("Dispose", true)]
    [InlineData("Dispose", false)]
    public void ExtensionGetEnumeratorSelectedForAnIteratorOperandIsTraversed(string effectMember, bool admitted)
    {
        string helpers = ExtensionAsyncSource
            + "static class Ext { public static ExtEnumerator GetEnumerator(this System.Collections.Generic.IAsyncEnumerable<int> source) { " + EnumerationEffectIn(effectMember, "GetEnumerator") + "return default; } } "
            + ExtensionSyncEnumerator(effectMember);

        string body = (admitted ? R2Admission : string.Empty) + "foreach (int item in Source.Items()) { _ = item; } await Task.CompletedTask;";

        AssertEnumerationSite(EnumerationDiscover(EnumerationCompile(body, helpers)), admitted);
    }

    [Theory]
    [InlineData("GetEnumerator", true)]
    [InlineData("GetEnumerator", false)]
    [InlineData("MoveNext", true)]
    [InlineData("MoveNext", false)]
    public void ExtensionGetEnumeratorSelectedForAConfiguredAsyncSequenceIsTraversed(string effectMember, bool admitted)
    {
        string helpers = ExtensionAsyncSource
            + "static class Ext { public static ExtEnumerator GetEnumerator(this System.Runtime.CompilerServices.ConfiguredCancelableAsyncEnumerable<int> source) { " + EnumerationEffectIn(effectMember, "GetEnumerator") + "return default; } } "
            + ExtensionSyncEnumerator(effectMember);

        string body = (admitted ? R2Admission : string.Empty) + "foreach (int item in Source.Items().ConfigureAwait(false)) { _ = item; } await Task.CompletedTask;";

        AssertEnumerationSite(EnumerationDiscover(EnumerationCompile(body, helpers)), admitted);
    }

    [Theory]
    [InlineData("GetAsyncEnumerator", true)]
    [InlineData("GetAsyncEnumerator", false)]
    [InlineData("MoveNextAsync", true)]
    [InlineData("MoveNextAsync", false)]
    [InlineData("Current", true)]
    [InlineData("Current", false)]
    [InlineData("DisposeAsync", true)]
    [InlineData("DisposeAsync", false)]
    public void ExtensionGetAsyncEnumeratorSelectedForAClosedListIsTraversed(string effectMember, bool admitted)
    {
        string helpers = "static class Ext { public static ExtAsyncEnumerator GetAsyncEnumerator(this System.Collections.Generic.List<int> source) { " + EnumerationEffectIn(effectMember, "GetAsyncEnumerator") + "return default; } } "
            + "struct ExtAsyncEnumerator : System.IAsyncDisposable { "
            + "public async System.Threading.Tasks.ValueTask<bool> MoveNextAsync() { " + EnumerationEffectIn(effectMember, "MoveNextAsync") + "await Task.CompletedTask; return false; } "
            + "public int Current { get { " + EnumerationEffectIn(effectMember, "Current") + "return 0; } } "
            + "public async System.Threading.Tasks.ValueTask DisposeAsync() { " + EnumerationEffectIn(effectMember, "DisposeAsync") + "await Task.CompletedTask; } }";

        string body = (admitted ? R2Admission : string.Empty) + "await foreach (int item in new System.Collections.Generic.List<int>()) { _ = item; } await Task.CompletedTask;";

        AssertEnumerationSite(EnumerationDiscover(EnumerationCompile(body, helpers)), admitted);
    }

    // The twin: an extension the compiler does not select (the list has its own GetEnumerator, and the extension is
    // declared for another receiver) adds nothing, and a closed operand without one stays classified.
    [Theory]
    [InlineData("foreach (int item in new System.Collections.Generic.List<int>()) { _ = item; }")]
    [InlineData("foreach (int item in new int[] { 1 }) { _ = item; }")]
    [InlineData("foreach (int item in System.Collections.Immutable.ImmutableArray<int>.Empty) { _ = item; }")]
    [InlineData("foreach (int item in System.Linq.Enumerable.Range(0, 1)) { _ = item; }")]
    public void ExtensionDeclaredForAnotherReceiverIsNotSelectedForAClosedOperand(string statement)
    {
        string helpers = ExtensionAsyncSource
            + "static class Ext { public static ExtEnumerator GetEnumerator(this System.Collections.Generic.IAsyncEnumerable<int> source) { " + EnumerationEffect + "return default; } } "
            + ExtensionSyncEnumerator("none");

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(R2Admission + statement + " await Task.CompletedTask;", helpers));

        Assert.Empty(EnumerationDeleteSites(result));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_SITE_UNCLASSIFIED" or "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    // ---------------------------------------------------------------------------------------------------
    // Default and null leaves: a default struct runs the struct's authored members, and a null receiver runs a
    // static or extension GetEnumerator that accepts it.

    private const string DefaultStructHelpers =
        "struct ProbeStruct { public ProbeEnumerator GetEnumerator() { " + EnumerationEffect + "return default; } } "
        + "struct ProbeEnumerator { public bool MoveNext() => false; public int Current => 0; } "
        + "static class Make { internal static ProbeStruct Empty() => default; internal static System.Collections.Immutable.ImmutableArray<int> EmptyArray() => default; }";

    [Theory]
    [InlineData("foreach (int item in default(ProbeStruct)) { _ = item; }")]
    [InlineData("foreach (int item in Make.Empty()) { _ = item; }")]
    [InlineData("int[] values = [.. Make.Empty()]; _ = values;")]
    [InlineData("bool flag = token.IsCancellationRequested; foreach (int item in flag ? default : default(ProbeStruct)) { _ = item; }")]
    [InlineData("ProbeStruct probe = default; foreach (int item in probe) { _ = item; }")]
    [InlineData("ProbeStruct probe = default(ProbeStruct); int[] values = [.. probe]; _ = values;")]
    public void DefaultOfAnAuthoredStructEnumerableIsTraversed(string statement)
    {
        HostedProducerSite site = Assert.Single(EnumerationDeleteSites(EnumerationDiscover(EnumerationCompile(R2Admission + statement + " await Task.CompletedTask;", DefaultStructHelpers))));

        Assert.NotNull(site.WorkFrontierCapsuleId);
    }

    [Theory]
    [InlineData("foreach (int item in default(System.Collections.Immutable.ImmutableArray<int>)) { _ = item; }")]
    [InlineData("foreach (int item in Make.EmptyArray()) { _ = item; }")]
    [InlineData("foreach (int item in System.Collections.Immutable.ImmutableArray<int>.Empty) { _ = item; }")]
    public void DefaultOfAClosedFrameworkStructStaysClassified(string statement)
    {
        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(R2Admission + statement + " await Task.CompletedTask;", DefaultStructHelpers));

        Assert.Empty(EnumerationDeleteSites(result));

        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData("foreach (int item in Make.None()) { _ = item; }")]
    [InlineData("foreach (int item in (Bag?)null) { _ = item; }")]
    public void NullReceiverReachesAnExtensionGetEnumeratorThatAcceptsIt(string statement)
    {
        string helpers = "sealed class Bag { } static class Ext { public static ExtEnumerator GetEnumerator(this Bag? bag) { " + EnumerationEffect + "return default; } } "
            + ExtensionSyncEnumerator("none") + " static class Make { internal static Bag? None() => null; }";

        HostedProducerSite site = Assert.Single(EnumerationDeleteSites(EnumerationDiscover(EnumerationCompile(R2Admission + statement + " await Task.CompletedTask;", helpers))));

        Assert.NotNull(site.WorkFrontierCapsuleId);
    }

    // ---------------------------------------------------------------------------------------------------
    // An implicit user-defined conversion at an initializer, a return or an argument hands the loop whatever the
    // operator returns, not the converted expression, exactly as a written cast does.

    private const string ConversionHelpers =
        "class Base : System.Collections.Generic.IEnumerable<int> { "
        + "public virtual System.Collections.Generic.IEnumerator<int> GetEnumerator() { return new BaseEnumerator(); } "
        + "System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator(); } "
        + "sealed class Derived : Base { public override System.Collections.Generic.IEnumerator<int> GetEnumerator() { " + EnumerationEffect + "return new BaseEnumerator(); } } "
        + "sealed class Convertible { public static implicit operator Base(Convertible value) => new Derived(); } "
        + "static class Maker { internal static Base Make() => new Convertible(); internal static Base Upcast() => new Derived(); } "
        + "static class Runner { internal static void Run(Base items) { foreach (int item in items) { _ = item; } } } "
        + "sealed class BaseEnumerator : System.Collections.Generic.IEnumerator<int> { public bool MoveNext() => false; public int Current => 0; object System.Collections.IEnumerator.Current => Current; public void Reset() { } public void Dispose() { } }";

    [Theory]
    [InlineData("Base source = new Convertible(); foreach (int item in source) { _ = item; }")]
    [InlineData("foreach (int item in Maker.Make()) { _ = item; }")]
    [InlineData("Runner.Run(new Convertible());")]
    [InlineData("Base source = new Convertible(); int[] values = [.. source]; _ = values;")]
    public void ImplicitUserDefinedConversionHidesTheRuntimeTypeAndIsRefused(string statement)
    {
        CSharpCompilation compilation = EnumerationCompile(R2Admission + statement + " await Task.CompletedTask;", ConversionHelpers);

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        Assert.True(EnumerationRefusedAt(result, compilation), string.Join(global::System.Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Code + " " + diagnostic.Identity)));
    }

    [Theory]
    [InlineData("Base source = new Derived(); foreach (int item in source) { _ = item; }")]
    [InlineData("foreach (int item in Maker.Upcast()) { _ = item; }")]
    [InlineData("Runner.Run(new Derived());")]
    [InlineData("Base source = new Derived(); int[] values = [.. source]; _ = values;")]
    public void BuiltInReferenceConversionKeepsTheRuntimeTypeAndIsTraversed(string statement)
    {
        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(R2Admission + statement + " await Task.CompletedTask;", ConversionHelpers));

        HostedProducerSite site = Assert.Single(EnumerationDeleteSites(result));

        Assert.NotNull(site.WorkFrontierCapsuleId);

        Assert.Empty(result.Diagnostics);
    }

    // ---------------------------------------------------------------------------------------------------
    // Spread lowering and enumerating framework members call Count, CopyTo, Contains and the indexer of a
    // collection, not the enumerator, so an authored collection's members are followed too.

    private static string CollectionProbe(string effectIn) =>
        "sealed class ProbeCollection : System.Collections.Generic.ICollection<int> { "
        + "public int Count { get { " + EnumerationEffectIn(effectIn, "Count") + "return 0; } } public bool IsReadOnly => true; "
        + "public void Add(int item) { " + EnumerationEffectIn(effectIn, "Add") + "} public void Clear() { } public bool Contains(int item) { " + EnumerationEffectIn(effectIn, "Contains") + "return false; } public bool Remove(int item) => false; "
        + "public void CopyTo(int[] array, int index) { " + EnumerationEffectIn(effectIn, "CopyTo") + "} "
        + "public System.Collections.Generic.IEnumerator<int> GetEnumerator() => new System.Collections.Generic.List<int>().GetEnumerator(); "
        + "System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator(); }";

    [Theory]
    [InlineData("int[] values = [.. new ProbeCollection()]; _ = values;", "Count", true)]
    [InlineData("int[] values = [.. new ProbeCollection()]; _ = values;", "Count", false)]
    [InlineData("int[] values = [.. new ProbeCollection()]; _ = values;", "CopyTo", true)]
    [InlineData("int[] values = [.. new ProbeCollection()]; _ = values;", "CopyTo", false)]
    [InlineData("System.Collections.Generic.List<int> values = [.. new ProbeCollection()]; _ = values;", "Count", true)]
    [InlineData("System.Collections.Generic.List<int> values = [.. new ProbeCollection()]; _ = values;", "CopyTo", true)]
    [InlineData("System.Collections.Generic.IEnumerable<int> values = [.. new ProbeCollection()]; _ = values;", "Count", true)]
    [InlineData("System.Collections.Generic.IEnumerable<int> values = [.. new ProbeCollection()]; _ = values;", "CopyTo", true)]
    [InlineData("int[] values = [0, .. new ProbeCollection()]; _ = values;", "Count", true)]
    [InlineData("System.Collections.Generic.IEnumerable<int> source = new ProbeCollection(); int[] values = [.. source]; _ = values;", "CopyTo", true)]
    public void SpreadOverAnAuthoredCollectionTraversesCountAndCopyTo(string statement, string effectIn, bool admitted)
    {
        AssertEnumerationSite(EnumerationDiscover(EnumerationCompile((admitted ? R2Admission : string.Empty) + statement + " await Task.CompletedTask;", CollectionProbe(effectIn))), admitted);
    }

    [Theory]
    [InlineData("_ = System.Linq.Enumerable.ToList(new ProbeCollection());", "CopyTo", "System.Linq.Enumerable.ToList")]
    [InlineData("_ = System.Linq.Enumerable.ToList(new ProbeCollection());", "Count", "System.Linq.Enumerable.ToList")]
    [InlineData("_ = System.Linq.Enumerable.ToArray(new ProbeCollection());", "CopyTo", "System.Linq.Enumerable.ToArray")]
    [InlineData("_ = new System.Collections.Generic.List<int>(new ProbeCollection());", "CopyTo", "System.Collections.Generic.List`1..ctor")]
    [InlineData("_ = System.Linq.Enumerable.Count(new ProbeCollection());", "Count", "System.Linq.Enumerable.Count")]
    [InlineData("_ = System.Linq.Enumerable.Contains(new ProbeCollection(), 1);", "Contains", "System.Linq.Enumerable.Contains")]
    public void EnumeratingFrameworkMemberOverAnAuthoredCollectionIsRefusedWhenItsCollectionMembersHaveEffects(string statement, string effectIn, string callee)
    {
        HostedProducerDiscovery<HostedProducerSite> refused = EnumerationDiscover(EnumerationCompile(R2Admission + statement + " await Task.CompletedTask;", CollectionProbe(effectIn)));

        Assert.True(EnumerationOwnershipRefused(refused, callee), string.Join(global::System.Environment.NewLine, refused.Diagnostics.Select(static diagnostic => diagnostic.Code + " " + diagnostic.Detail)));

        HostedProducerDiscovery<HostedProducerSite> clean = EnumerationDiscover(EnumerationCompile(R2Admission + statement + " await Task.CompletedTask;", CollectionProbe("none")));

        Assert.DoesNotContain(clean.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    [Fact]
    public void EnumeratingFrameworkMemberOverAnAuthoredListIsRefusedWhenItsIndexerHasAnEffect()
    {
        string helpers = "sealed class ProbeList : System.Collections.Generic.IList<int> { "
            + "public int this[int index] { get { " + EnumerationEffect + "return 0; } set { } } public int Count => 1; public bool IsReadOnly => true; "
            + "public void Add(int item) { } public void Clear() { } public bool Contains(int item) => false; public void CopyTo(int[] array, int index) { } "
            + "public int IndexOf(int item) => -1; public void Insert(int index, int item) { } public bool Remove(int item) => false; public void RemoveAt(int index) { } "
            + "public System.Collections.Generic.IEnumerator<int> GetEnumerator() => new System.Collections.Generic.List<int>().GetEnumerator(); "
            + "System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator(); }";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(R2Admission + "_ = System.Linq.Enumerable.ElementAt(new ProbeList(), 0); await Task.CompletedTask;", helpers));

        Assert.True(EnumerationOwnershipRefused(result, "System.Linq.Enumerable.ElementAt"));
    }

    // The twin: framework collections and an authored collection whose only effect sits in a member no spread or
    // enumerating operator calls (Add) are not claimed.
    [Theory]
    [InlineData("int[] values = [.. new System.Collections.Generic.List<int>()]; _ = values;")]
    [InlineData("int[] values = [.. new int[] { 1 }]; _ = values;")]
    [InlineData("_ = System.Linq.Enumerable.ToList(new System.Collections.Generic.List<int>());")]
    [InlineData("_ = System.Linq.Enumerable.Count(new System.Collections.Generic.HashSet<int>());")]
    public void FrameworkCollectionsDoNotRunAuthoredCollectionMembers(string statement)
    {
        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(R2Admission + statement + " await Task.CompletedTask;", CollectionProbe("Count")));

        Assert.Empty(EnumerationDeleteSites(result));

        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData("int[] values = [.. new ProbeCollection()]; _ = values;")]
    [InlineData("_ = System.Linq.Enumerable.ToList(new ProbeCollection());")]
    public void AuthoredCollectionMutatorsAreNotRunByEnumeratingOrSpreading(string statement)
    {
        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(R2Admission + statement + " await Task.CompletedTask;", CollectionProbe("Add")));

        Assert.Empty(EnumerationDeleteSites(result));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    // ---------------------------------------------------------------------------------------------------
    // A framework call that returns an element or a stored value hands back a sequence the operand's proof never
    // saw. Only reviewed wrapping operators over proven inputs are closed.

    private const string NestedSequenceHelpers =
        "sealed class Inner : System.Collections.Generic.IEnumerable<int> { public System.Collections.Generic.IEnumerator<int> GetEnumerator() => new InnerEnumerator(); System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator(); } "
        + "sealed class InnerEnumerator : System.Collections.Generic.IEnumerator<int> { public bool MoveNext() { " + EnumerationEffect + "return false; } public int Current => 0; object System.Collections.IEnumerator.Current => Current; public void Reset() { } public void Dispose() { } } "
        + "static class Holder { internal static System.IServiceProvider Provider = null!; internal static System.Collections.Generic.Dictionary<int, System.Collections.Generic.IEnumerable<int>> Map = new() { [1] = new Inner() }; internal static System.Collections.Generic.IEnumerable<int> Field = new Inner(); }";

    [Theory]
    [InlineData("foreach (int item in System.Linq.Enumerable.First(new System.Collections.Generic.List<System.Collections.Generic.IEnumerable<int>> { new Inner() })) { _ = item; }")]
    [InlineData("foreach (int item in System.Linq.Enumerable.SelectMany(new System.Collections.Generic.List<System.Collections.Generic.IEnumerable<int>> { new Inner() }, s => s)) { _ = item; }")]
    [InlineData("System.Collections.Generic.IEnumerable<int>[] nested = [new Inner()]; foreach (int item in System.Linq.Enumerable.First(nested)) { _ = item; }")]
    [InlineData("foreach (int item in System.Linq.Enumerable.First(Holder.Map.Values)) { _ = item; }")]
    [InlineData("foreach (int item in Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<System.Collections.Generic.IEnumerable<int>>(Holder.Provider)) { _ = item; }")]
    [InlineData("foreach (int item in System.Threading.Volatile.Read(ref Holder.Field)) { _ = item; }")]
    public void FrameworkCallReturningAStoredSequenceIsNotClosedUnderStrictDiscovery(string statement)
    {
        CSharpCompilation compilation = EnumerationCompile(R2Admission + statement + " await Task.CompletedTask;", NestedSequenceHelpers);

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        Assert.True(EnumerationRefusedAt(result, compilation) || EnumerationAnyRefusal(result), string.Join(global::System.Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Code + " " + diagnostic.Identity)));
    }

    [Theory]
    [InlineData("_ = System.Linq.Enumerable.ToList(System.Linq.Enumerable.SelectMany(new System.Collections.Generic.List<System.Collections.Generic.IEnumerable<int>> { new Inner() }, s => s));")]
    [InlineData("_ = System.Linq.Enumerable.ToList(System.Linq.Enumerable.SelectMany(new int[] { 1 }, _ => new Inner()));")]
    [InlineData("foreach (int item in System.Linq.Enumerable.SelectMany(new int[] { 1 }, _ => new Inner())) { _ = item; }")]
    [InlineData("foreach (int item in System.Linq.Enumerable.SelectMany(new int[] { 1 }, NestedSource.Nested)) { _ = item; }")]
    public void SelectManyOverAnAuthoredSequenceIsRefused(string statement)
    {
        string helpers = NestedSequenceHelpers + " static class NestedSource { internal static System.Collections.Generic.IEnumerable<int> Nested(int value) => new Inner(); }";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(R2Admission + statement + " await Task.CompletedTask;", helpers));

        Assert.True(EnumerationAnyRefusal(result), string.Join(global::System.Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Code + " " + diagnostic.Identity)));
    }

    // The twins: the same operators over elements that are closed, or that return a closed concrete value, add
    // no refusal, and the wrappers the reviewed table names stay closed over closed inputs.
    [Theory]
    [InlineData("foreach (int item in System.Linq.Enumerable.First(new System.Collections.Generic.List<System.Collections.Generic.List<int>> { new() })) { _ = item; }")]
    [InlineData("foreach (int item in System.Linq.Enumerable.SelectMany(new int[] { 1 }, _ => new System.Collections.Generic.List<int>())) { _ = item; }")]
    [InlineData("foreach (int item in System.Linq.Enumerable.SelectMany(new System.Collections.Generic.List<int[]> { new int[] { 1 } }, s => s)) { _ = item; }")]
    [InlineData("_ = System.Linq.Enumerable.ToList(System.Linq.Enumerable.SelectMany(new int[] { 1 }, _ => new int[] { 2 }));")]
    [InlineData("foreach (int item in System.Linq.Enumerable.Range(0, 2)) { _ = item; }")]
    [InlineData("foreach (int item in System.Linq.Enumerable.Empty<int>()) { _ = item; }")]
    [InlineData("foreach (int item in System.Linq.Enumerable.Reverse(new int[] { 1 })) { _ = item; }")]
    [InlineData("foreach (int item in System.Linq.Enumerable.Distinct(System.Linq.Enumerable.Where(new System.Collections.Generic.List<int>(), value => value > 0))) { _ = item; }")]
    public void FrameworkOperatorsOverClosedInputsStayClosed(string statement)
    {
        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(R2Admission + statement + " await Task.CompletedTask;", NestedSequenceHelpers));

        Assert.Empty(EnumerationDeleteSites(result));

        Assert.Empty(result.Diagnostics);
    }

    // The premise run accepts the stored-value shapes exactly because no authored enumerable can be behind a
    // framework collection interface there; the same loop is refused under strict discovery.
    [Theory]
    [InlineData("foreach (int item in System.Linq.Enumerable.First(new System.Collections.Generic.List<System.Collections.Generic.IEnumerable<int>> { new int[] { 1 } })) { _ = item; }")]
    [InlineData("foreach (int item in Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<System.Collections.Generic.IEnumerable<int>>(PremiseHolder.Provider)) { _ = item; }")]
    public void StoredSequenceIsAcceptedOnlyUnderTheAuthoredEnumerablesAbsentPremise(string statement)
    {
        CSharpCompilation compilation = EnumerationPremiseFixture(statement, "static class PremiseHolder { internal static System.IServiceProvider Provider = null!; }");

        Assert.True(EnumerationRefusedAt(PremiseStrict(compilation), compilation));

        HostedProducerDiscovery<HostedProducerSite> premised = EnumerationPremiseRequested(compilation);

        Assert.False(EnumerationRefusedAt(premised, compilation));

        Assert.Empty(premised.Diagnostics);
    }

    // ---------------------------------------------------------------------------------------------------
    // An enumerating framework member needs positive evidence about the sequence it enumerates. An argument whose
    // runtime type nothing proves is refused under strict discovery, exactly as a foreach over it is.

    private const string UnprovedHelpers =
        "static class Unproved { internal static System.Collections.Generic.IEnumerable<int> Items { get; set; } = null!; internal static System.Collections.Generic.List<int> Closed { get; set; } = null!; }";

    [Theory]
    [InlineData("_ = System.Linq.Enumerable.ToList(Unproved.Items);", "System.Linq.Enumerable.ToList")]
    [InlineData("_ = System.Linq.Enumerable.ToArray(Unproved.Items);", "System.Linq.Enumerable.ToArray")]
    [InlineData("_ = new System.Collections.Generic.List<int>(Unproved.Items);", "System.Collections.Generic.List`1..ctor")]
    [InlineData("_ = string.Join(\",\", Unproved.Items);", "System.String.Join")]
    [InlineData("_ = System.Linq.Enumerable.Count(Unproved.Items);", "System.Linq.Enumerable.Count")]
    public void UnprovenArgumentToAnEnumeratingFrameworkMemberIsRefusedUnderStrictDiscovery(string statement, string callee)
    {
        HostedProducerDiscovery<HostedProducerSite> refused = EnumerationDiscover(EnumerationCompile(R2Admission + statement + " await Task.CompletedTask;", UnprovedHelpers));

        Assert.True(EnumerationOwnershipRefused(refused, callee), string.Join(global::System.Environment.NewLine, refused.Diagnostics.Select(static diagnostic => diagnostic.Code + " " + diagnostic.Detail)));

        string closed = statement.Replace("Unproved.Items", "Unproved.Closed", StringComparison.Ordinal);

        HostedProducerDiscovery<HostedProducerSite> clean = EnumerationDiscover(EnumerationCompile(R2Admission + closed + " await Task.CompletedTask;", UnprovedHelpers));

        Assert.Empty(clean.Diagnostics);
    }

    [Theory]
    [InlineData("_ = System.Linq.Enumerable.ToList(UnprovedEnumerableInput.Items);")]
    [InlineData("_ = new System.Collections.Generic.List<int>(UnprovedEnumerableInput.Items);")]
    [InlineData("_ = string.Join(\",\", UnprovedEnumerableInput.Items);")]
    public void UnprovenInterfaceArgumentIsAcceptedOnlyUnderTheAuthoredEnumerablesAbsentPremise(string statement)
    {
        CSharpCompilation compilation = EnumerationPremiseFixture(statement);

        Assert.Contains(PremiseStrict(compilation).Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");

        Assert.Empty(EnumerationPremiseRequested(compilation).Diagnostics);
    }

    // ---------------------------------------------------------------------------------------------------
    // An iterator that returns an enumerator is lazy too: its body runs on the MoveNext calls that follow the
    // call, after the lease may have been disposed.

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void GetEnumeratorReturningAnIteratorRunsItsBodyUnderTheLoopWideFrontier(bool disposesInBody, bool keepsWork)
    {
        string helpers = "sealed class Probe { public System.Collections.Generic.IEnumerator<int> GetEnumerator() => Iterate(); "
            + "private static System.Collections.Generic.IEnumerator<int> Iterate() { yield return 1; " + EnumerationEffect + "yield return 2; } }";

        string body = R2Admission + "foreach (int item in new Probe()) { _ = item; " + (disposesInBody ? "lease.Dispose(); " : string.Empty) + "} await Task.CompletedTask;";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(body, helpers));

        HostedProducerSite[] sites = EnumerationDeleteSites(result);

        if (keepsWork)
        {
            Assert.NotNull(Assert.Single(sites).WorkFrontierCapsuleId);

            Assert.Empty(result.Diagnostics);
        }
        else
        {
            Assert.True(EnumerationAnyRefusal(result) || sites.Length != 0 && sites.All(static site => site.WorkFrontierCapsuleId is null));
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void GetAsyncEnumeratorReturningAnAsyncIteratorRunsItsBodyUnderTheLoopWideFrontier(bool disposesInBody, bool keepsWork)
    {
        string helpers = "sealed class Probe { public System.Collections.Generic.IAsyncEnumerator<int> GetAsyncEnumerator(System.Threading.CancellationToken token = default) => Iterate(); "
            + "private static async System.Collections.Generic.IAsyncEnumerator<int> Iterate() { await Task.CompletedTask; yield return 1; " + EnumerationEffect + "yield return 2; } }";

        string body = R2Admission + "await foreach (int item in new Probe()) { _ = item; " + (disposesInBody ? "lease.Dispose(); " : string.Empty) + "} await Task.CompletedTask;";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(body, helpers));

        HostedProducerSite[] sites = EnumerationDeleteSites(result);

        if (keepsWork)
        {
            Assert.NotNull(Assert.Single(sites).WorkFrontierCapsuleId);

            Assert.Empty(result.Diagnostics);
        }
        else
        {
            Assert.True(EnumerationAnyRefusal(result) || sites.Length != 0 && sites.All(static site => site.WorkFrontierCapsuleId is null));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitIteratorEnumeratorCalledAfterTheLeaseWasDisposedLosesTheWork(bool asynchronous)
    {
        string helpers = asynchronous
            ? "static class Source { internal static async System.Collections.Generic.IAsyncEnumerator<int> Items() { " + EnumerationEffect + "await Task.CompletedTask; yield return 1; } }"
            : "static class Source { internal static System.Collections.Generic.IEnumerator<int> Items() { " + EnumerationEffect + "yield return 1; } }";

        string body = R2Admission + "var e = Source.Items(); lease.Dispose(); _ = " + (asynchronous ? "await e.MoveNextAsync();" : "e.MoveNext();");

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(body + " await Task.CompletedTask;", helpers));

        HostedProducerSite[] sites = EnumerationDeleteSites(result);

        Assert.True(EnumerationAnyRefusal(result) || sites.Length != 0 && sites.All(static site => site.WorkFrontierCapsuleId is null));
    }

    // The twin: a method that merely returns an enumerator runs its body at the call, under the frontier live there.
    [Fact]
    public void NonIteratorMethodReturningAnEnumeratorRunsAtTheCall()
    {
        string helpers = "static class Source { internal static System.Collections.Generic.IEnumerator<int> Items() { " + EnumerationEffect + "return new System.Collections.Generic.List<int>().GetEnumerator(); } }";

        HostedProducerSite site = Assert.Single(EnumerationDeleteSites(EnumerationDiscover(EnumerationCompile(R2Admission + "var e = Source.Items(); _ = e.MoveNext(); await Task.CompletedTask;", helpers))));

        Assert.NotNull(site.WorkFrontierCapsuleId);
    }

    // ---------------------------------------------------------------------------------------------------
    // The exact-type-only framework implementers are trusted for the exact framework type, never for an authored
    // subclass of the type that declares the member.

    [Fact]
    public void AuthoredGenericAclSubclassIsNotTheExactFrameworkAcl()
    {
        string helpers = "sealed class EvilAcl : System.Security.AccessControl.GenericAcl { "
            + "public override byte Revision => 2; public override int Count { get { " + EnumerationEffect + "return 0; } } public override int BinaryLength => 8; "
            + "public override System.Security.AccessControl.GenericAce this[int index] { get => null!; set { } } "
            + "public override void GetBinaryForm(byte[] binaryForm, int offset) { } }";

        CSharpCompilation compilation = EnumerationCompile(R2Admission + "foreach (System.Security.AccessControl.GenericAce ace in new EvilAcl()) { _ = ace; } await Task.CompletedTask;", helpers);

        Assert.True(EnumerationRefusedAt(EnumerationDiscover(compilation), compilation));
    }

    [Fact]
    public void AuthoredReadOnlyCollectionBaseSubclassIsNotTheExactFrameworkCollection()
    {
        string helpers = "sealed class EvilCollection : System.Collections.ReadOnlyCollectionBase { public EvilCollection() { " + EnumerationEffect + "} }";

        CSharpCompilation compilation = EnumerationCompile(R2Admission + "foreach (object item in new EvilCollection()) { _ = item; } await Task.CompletedTask;", helpers);

        Assert.True(EnumerationRefusedAt(EnumerationDiscover(compilation), compilation));
    }

    // ---------------------------------------------------------------------------------------------------
    // Framework members that enumerate a sequence through a callback result.

    [Fact]
    public void SelectManyLambdaReturningAnAuthoredEnumerableIsRefusedAtTheCall()
    {
        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(
            R2Admission + "_ = System.Linq.Enumerable.ToList(System.Linq.Enumerable.SelectMany(new int[] { 1 }, _ => new Probe())); await Task.CompletedTask;",
            InterfaceEnumerable("MoveNext")));

        Assert.True(EnumerationOwnershipRefused(result, "System.Linq.Enumerable.SelectMany"), string.Join(global::System.Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Code + " " + diagnostic.Detail)));

        HostedProducerDiscovery<HostedProducerSite> clean = EnumerationDiscover(EnumerationCompile(
            R2Admission + "_ = System.Linq.Enumerable.ToList(System.Linq.Enumerable.SelectMany(new int[] { 1 }, _ => new Probe())); await Task.CompletedTask;",
            InterfaceEnumerable("none")));

        Assert.DoesNotContain(clean.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    // ---------------------------------------------------------------------------------------------------
    // A lazy sequence handed to an authored consumer runs in the consumer's loop, so a consumer that also receives
    // the lease can end the Work the body ran under. The sequence alone is consumed under the caller's frontier.

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void IteratorHandedToAConsumerThatAlsoReceivesTheLeaseDoesNotKeepItsWork(bool passesLease, bool keepsWork)
    {
        string helpers = "static class Source { internal static System.Collections.Generic.IEnumerable<int> Items() { yield return 1; " + EnumerationEffect + "yield return 2; } } "
            + "static class ConsumerHost { "
            + (passesLease
                ? "internal static void Consume(System.Collections.Generic.IEnumerable<int> items, RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease lease) { foreach (int item in items) { _ = item; lease.Dispose(); } }"
                : "internal static void Consume(System.Collections.Generic.IEnumerable<int> items) { foreach (int item in items) { _ = item; } }")
            + " }";

        string body = R2Admission + (passesLease ? "ConsumerHost.Consume(Source.Items(), lease);" : "ConsumerHost.Consume(Source.Items());") + " await Task.CompletedTask;";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(body, helpers));

        HostedProducerSite[] sites = EnumerationDeleteSites(result);

        if (keepsWork)
        {
            Assert.NotNull(Assert.Single(sites).WorkFrontierCapsuleId);

            Assert.Empty(result.Diagnostics);
        }
        else
        {
            Assert.True(EnumerationAnyRefusal(result) || sites.Length != 0 && sites.All(static site => site.WorkFrontierCapsuleId is null));
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // A value reassigned by a tuple deconstruction is no longer the value its declaration shows.

    [Theory]
    [InlineData("System.Collections.Generic.IEnumerable<int> e = new System.Collections.Generic.List<int>(); (e, int n) = (new Probe(), 1); _ = n; foreach (int item in e) { _ = item; }", false)]
    [InlineData("System.Collections.Generic.IEnumerable<int> e = new System.Collections.Generic.List<int>(); int n; (n, e) = (1, new Probe()); _ = n; foreach (int item in e) { _ = item; }", false)]
    [InlineData("System.Collections.Generic.IEnumerable<int> e = new System.Collections.Generic.List<int>(); e = new Probe(); foreach (int item in e) { _ = item; }", true)]
    [InlineData("System.Collections.Generic.IEnumerable<int> e = new System.Collections.Generic.List<int>(); int n; (n, _) = (1, new Probe()); _ = n; foreach (int item in e) { _ = item; }", true)]
    public void LocalReassignedByDeconstructionIsNotFollowedToItsDeclaration(string statement, bool followed)
    {
        CSharpCompilation compilation = EnumerationCompile(R2Admission + statement + " await Task.CompletedTask;", InterfaceEnumerable("MoveNext"));

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        if (followed)
        {
            Assert.Equal(statement.Contains("e = new Probe()", StringComparison.Ordinal) ? 1 : 0, EnumerationDeleteSites(result).Length);

            Assert.False(EnumerationRefusedAt(result, compilation));
        }
        else
        {
            Assert.True(EnumerationRefusedAt(result, compilation) || EnumerationDeleteSites(result).Length != 0, string.Join(global::System.Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Code)));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadonlyFieldReassignedByDeconstructionInTheConstructorIsNotFollowedToItsInitializer(bool deconstructs)
    {
        string helpers = InterfaceEnumerable("MoveNext")
            + " sealed class Holder { private readonly System.Collections.Generic.IEnumerable<int> items = new System.Collections.Generic.List<int>(); "
            + "internal Holder() { " + (deconstructs ? "(items, _) = (new Probe(), 1);" : string.Empty) + " } "
            + "internal void Run() { foreach (int item in items) { _ = item; } } }";

        CSharpCompilation compilation = EnumerationCompile(R2Admission + "new Holder().Run(); await Task.CompletedTask;", helpers);

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        Assert.Equal(deconstructs, EnumerationRefusedAt(result, compilation) || EnumerationDeleteSites(result).Length != 0);

        if (!deconstructs)
        {
            Assert.Empty(result.Diagnostics);
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // A framework member enumerates through the contract, so an explicit implementation with a body of its own is
    // what it runs, not the public pattern member a loop over the same value would bind.

    [Theory]
    [InlineData("_ = System.Linq.Enumerable.ToList(new Probe());", true, "System.Linq.Enumerable.ToList")]
    [InlineData("_ = new System.Collections.Generic.List<int>(new Probe());", true, "System.Collections.Generic.List`1..ctor")]
    [InlineData("_ = System.Linq.Enumerable.ToList(new Probe());", false, "System.Linq.Enumerable.ToList")]
    public void ExplicitContractImplementationIsWhatAFrameworkMemberRuns(string statement, bool explicitHasEffect, string callee)
    {
        string helpers = "sealed class Probe : System.Collections.Generic.IEnumerable<int> { "
            + "public System.Collections.Generic.List<int>.Enumerator GetEnumerator() => new System.Collections.Generic.List<int>().GetEnumerator(); "
            + "System.Collections.Generic.IEnumerator<int> System.Collections.Generic.IEnumerable<int>.GetEnumerator() { " + (explicitHasEffect ? EnumerationEffect : string.Empty) + "return new System.Collections.Generic.List<int>().GetEnumerator(); } "
            + "System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => new System.Collections.Generic.List<int>().GetEnumerator(); }";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(R2Admission + statement + " await Task.CompletedTask;", helpers));

        Assert.Equal(explicitHasEffect, EnumerationOwnershipRefused(result, callee));
    }

    [Fact]
    public void ExplicitContractImplementationIsNotWhatALoopRuns()
    {
        string helpers = "sealed class Probe : System.Collections.Generic.IEnumerable<int> { "
            + "public System.Collections.Generic.List<int>.Enumerator GetEnumerator() => new System.Collections.Generic.List<int>().GetEnumerator(); "
            + "System.Collections.Generic.IEnumerator<int> System.Collections.Generic.IEnumerable<int>.GetEnumerator() { " + EnumerationEffect + "return new System.Collections.Generic.List<int>().GetEnumerator(); } "
            + "System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => null!; }";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(R2Admission + "foreach (int item in new Probe()) { _ = item; } await Task.CompletedTask;", helpers));

        Assert.Empty(EnumerationDeleteSites(result));

        Assert.Empty(result.Diagnostics);
    }

    // ---------------------------------------------------------------------------------------------------
    // Shapes that run an authored enumerator by a route other than the plain loop. Each is either reported as a site
    // of the root or refused; none may pass silently.

    private const string InnerEnumerable =
        "sealed class Inner : System.Collections.Generic.IEnumerable<int> { public System.Collections.Generic.IEnumerator<int> GetEnumerator() => new InnerEnumerator(); System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator(); } "
        + "sealed class InnerEnumerator : System.Collections.Generic.IEnumerator<int> { public bool MoveNext() { " + EnumerationEffect + "return false; } public int Current => 0; object System.Collections.IEnumerator.Current => Current; public void Reset() { } public void Dispose() { } } ";

    [Theory]
    [InlineData("foreach (int item in new Outer()) { _ = item; }", "sealed class Outer : System.Collections.Generic.IEnumerable<int> { private readonly System.Collections.Generic.IEnumerable<int> inner = new Inner(); public System.Collections.Generic.IEnumerator<int> GetEnumerator() => inner.GetEnumerator(); System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator(); }")]
    [InlineData("foreach (int item in new Outer()) { _ = item; }", "sealed class Outer : System.Collections.Generic.IEnumerable<int> { public System.Collections.Generic.IEnumerator<int> GetEnumerator() => ((System.Collections.Generic.IEnumerable<int>)new Inner()).GetEnumerator(); System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator(); }")]
    [InlineData("foreach (int item in new Outer()) { _ = item; }", "sealed class Outer : System.Collections.Generic.IEnumerable<int> { public System.Collections.Generic.IEnumerator<int> GetEnumerator() => Items().GetEnumerator(); System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator(); private static System.Collections.Generic.IEnumerable<int> Items() { " + EnumerationEffect + "yield return 1; } }")]
    [InlineData("foreach (int item in new Probe()) { _ = item; }", "sealed class Probe { public ProbeEnumerator GetEnumerator() => new DisposingEnumerator(); } class ProbeEnumerator { public bool MoveNext() => false; public int Current => 0; } sealed class DisposingEnumerator : ProbeEnumerator, System.IDisposable { public void Dispose() { " + EnumerationEffect + "} }")]
    [InlineData("Runner.Run();", "ref struct Probe { public ProbeEnumerator GetEnumerator() => new ProbeEnumerator(); } ref struct ProbeEnumerator { public bool MoveNext() => false; public int Current => 0; public void Dispose() { " + EnumerationEffect + "} } static class Runner { internal static void Run() { foreach (int item in new Probe()) { _ = item; } } }")]
    [InlineData("Runner.Run(new Inner());", "static class Runner { internal static void Run<T>(T items) where T : System.Collections.Generic.IEnumerable<int> { foreach (int item in items) { _ = item; } } }")]
    [InlineData("foreach (int item in new Probe()) { _ = item; }", "sealed class Probe : System.Collections.Generic.IEnumerable<int> { System.Collections.Generic.IEnumerator<int> System.Collections.Generic.IEnumerable<int>.GetEnumerator() { " + EnumerationEffect + "return new System.Collections.Generic.List<int>().GetEnumerator(); } System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => null!; }")]
    [InlineData("foreach (int item in new Probe(1)) { _ = item; }", "sealed record Probe(int Seed) : System.Collections.Generic.IEnumerable<int> { public System.Collections.Generic.IEnumerator<int> GetEnumerator() { " + EnumerationEffect + "return new System.Collections.Generic.List<int>().GetEnumerator(); } System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator(); }")]
    [InlineData("foreach (int item in new Bag()) { _ = item; }", "interface IBag : System.Collections.Generic.IEnumerable<int> { System.Collections.Generic.IEnumerator<int> System.Collections.Generic.IEnumerable<int>.GetEnumerator() { " + EnumerationEffect + "return new System.Collections.Generic.List<int>().GetEnumerator(); } System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => null!; } sealed class Bag : IBag { }")]
    [InlineData("await Task.Run(() => { foreach (int item in new Inner()) { _ = item; } });", "")]
    [InlineData("void Local() { foreach (int item in new Inner()) { _ = item; } } Local();", "")]
    [InlineData("_ = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Select(new int[] { 1 }, x => { foreach (int item in new Inner()) { _ = item; } return x; }));", "")]
    [InlineData("new Holder(new Inner()).Run();", "sealed class Holder { private readonly System.Collections.Generic.IEnumerable<int> items; internal Holder(System.Collections.Generic.IEnumerable<int> items) { this.items = items; } internal void Run() { foreach (int item in items) { _ = item; } } }")]
    public void AuthoredEnumeratorReachedByAnotherRouteIsReportedOrRefused(string body, string helpers)
    {
        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(R2Admission + body + " await Task.CompletedTask;", InnerEnumerable + helpers));

        Assert.True(EnumerationDeleteSites(result).Length != 0 || EnumerationAnyRefusal(result), string.Join(global::System.Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Code)));
    }

    // ---------------------------------------------------------------------------------------------------
    // AggregateException.InnerExceptions is a read-only view over the array the exception copied from its
    // constructor arguments. It is framework-owned and its elements are never enumerated as sequences, so it is the
    // one reviewed read-only view; any other view, which can wrap an authored list, is not.

    [Theory]
    [InlineData("_ = System.Linq.Enumerable.Any(new System.AggregateException().InnerExceptions, e => e is System.OperationCanceledException);")]
    [InlineData("var failures = new System.Collections.Generic.List<System.Exception>(); failures.AddRange(new System.AggregateException().Flatten().InnerExceptions);")]
    [InlineData("foreach (System.Exception inner in new System.AggregateException().InnerExceptions) { _ = inner; }")]
    [InlineData("System.AggregateException aggregate = new System.AggregateException(); _ = System.Linq.Enumerable.ToList(aggregate.InnerExceptions);")]
    [InlineData("System.Exception failure = new System.AggregateException(); if (failure is System.AggregateException aggregate) { _ = System.Linq.Enumerable.Count(aggregate.InnerExceptions); }")]
    public void AggregateExceptionInnerExceptionsIsAFrameworkOwnedSequence(string statement)
    {
        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(R2Admission + statement + " await Task.CompletedTask;", InnerEnumerable));

        Assert.Empty(EnumerationDeleteSites(result));

        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData("var failures = new System.Collections.Generic.List<System.Exception>(); failures.AddRange(new System.Collections.ObjectModel.ReadOnlyCollection<System.Exception>(new System.Collections.Generic.List<System.Exception>()));", "System.Collections.Generic.List`1.AddRange")]
    [InlineData("_ = System.Linq.Enumerable.Any(new System.Collections.ObjectModel.ReadOnlyCollection<System.Exception>(new System.Collections.Generic.List<System.Exception>()), e => e is System.OperationCanceledException);", "System.Linq.Enumerable.Any")]
    [InlineData("_ = System.Linq.Enumerable.Any(new Lookalike().InnerExceptions, e => e is System.OperationCanceledException);", "System.Linq.Enumerable.Any")]
    public void AnyOtherReadOnlyViewIsNotAFrameworkOwnedSequence(string statement, string callee)
    {
        string helpers = InnerEnumerable + "sealed class Lookalike { public System.Collections.ObjectModel.ReadOnlyCollection<System.Exception> InnerExceptions { get; } = new(new System.Collections.Generic.List<System.Exception>()); }";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(R2Admission + statement + " await Task.CompletedTask;", helpers));

        Assert.True(EnumerationOwnershipRefused(result, callee), string.Join(global::System.Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Code + " " + diagnostic.Detail)));
    }
}
