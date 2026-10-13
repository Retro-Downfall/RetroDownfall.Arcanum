using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    private const string EnumerationEffect = "System.IO.File.Delete(\"enumeration-effect\"); ";

    private static string EnumerationEffectIn(string effectMember, string member) =>
        effectMember == member ? EnumerationEffect : string.Empty;

    // An authored sealed IEnumerable<int> whose enumerator is an authored sealed class. The effect sits in
    // exactly one of the four members the compiler calls for a foreach, or nowhere ("none").
    private static string InterfaceEnumerable(string effectMember) =>
        "sealed class Probe : System.Collections.Generic.IEnumerable<int> { "
        + "public System.Collections.Generic.IEnumerator<int> GetEnumerator() { " + EnumerationEffectIn(effectMember, "GetEnumerator") + "return new ProbeEnumerator(); } "
        + "System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator(); } "
        + "sealed class ProbeEnumerator : System.Collections.Generic.IEnumerator<int> { "
        + "public bool MoveNext() { " + EnumerationEffectIn(effectMember, "MoveNext") + "return false; } "
        + "public int Current { get { " + EnumerationEffectIn(effectMember, "Current") + "return 0; } } "
        + "object System.Collections.IEnumerator.Current => Current; "
        + "public void Reset() { } "
        + "public void Dispose() { " + EnumerationEffectIn(effectMember, "Dispose") + "} }";

    // The same shape reached by the foreach pattern alone: no interface on the collection or the enumerator.
    private static string PatternEnumerable(string effectMember) =>
        "sealed class Probe { "
        + "public ProbeEnumerator GetEnumerator() { " + EnumerationEffectIn(effectMember, "GetEnumerator") + "return new ProbeEnumerator(); } } "
        + "struct ProbeEnumerator : System.IDisposable { "
        + "public bool MoveNext() { " + EnumerationEffectIn(effectMember, "MoveNext") + "return false; } "
        + "public int Current { get { " + EnumerationEffectIn(effectMember, "Current") + "return 0; } } "
        + "public void Dispose() { " + EnumerationEffectIn(effectMember, "Dispose") + "} }";

    private static string AsyncInterfaceEnumerable(string effectMember) =>
        "sealed class Probe : System.Collections.Generic.IAsyncEnumerable<int> { "
        + "public System.Collections.Generic.IAsyncEnumerator<int> GetAsyncEnumerator(System.Threading.CancellationToken token = default) { " + EnumerationEffectIn(effectMember, "GetAsyncEnumerator") + "return new ProbeEnumerator(); } } "
        + "sealed class ProbeEnumerator : System.Collections.Generic.IAsyncEnumerator<int> { "
        + "public async System.Threading.Tasks.ValueTask<bool> MoveNextAsync() { " + EnumerationEffectIn(effectMember, "MoveNextAsync") + "await Task.CompletedTask; return false; } "
        + "public int Current { get { " + EnumerationEffectIn(effectMember, "Current") + "return 0; } } "
        + "public async System.Threading.Tasks.ValueTask DisposeAsync() { " + EnumerationEffectIn(effectMember, "DisposeAsync") + "await Task.CompletedTask; } }";

    private static HostedProducerDiscovery<HostedProducerSite> EnumerationDiscover(CSharpCompilation compilation) =>
        HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

    private static CSharpCompilation EnumerationCompile(string body, string helpers)
    {
        CSharpCompilation compilation = Compile(R2Source(body, helpers));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        return compilation;
    }

    private static HostedProducerSite[] EnumerationDeleteSites(HostedProducerDiscovery<HostedProducerSite> result) =>
        [.. result.Items.Where(static site => site.RootType == "Worker" && site.Callee == "System.IO.File.Delete")];

    private static bool EnumerationFrontierMissing(HostedProducerDiscovery<HostedProducerSite> result, string code) =>
        result.Diagnostics.Any(diagnostic => diagnostic.Code == code && diagnostic.Detail == "System.IO.File.Delete");

    // The effect inside an authored enumerator member is reported as a physical site of the root, under the
    // frontiers live where the compiler calls the member. Outside an admitted region the same site is reported
    // with no frontier, which the work and effect frontier diagnostics then name.
    private static void AssertEnumerationSite(HostedProducerDiscovery<HostedProducerSite> result, bool admitted)
    {
        HostedProducerSite site = Assert.Single(EnumerationDeleteSites(result));

        Assert.Equal(admitted, site.WorkFrontierCapsuleId is not null);

        Assert.Equal(admitted, site.EffectFrontierCapsuleId is not null);

        Assert.Equal(!admitted, EnumerationFrontierMissing(result, "HOSTED_SITE_WORK_FRONTIER_MISSING"));

        Assert.Equal(!admitted, EnumerationFrontierMissing(result, "HOSTED_SITE_EFFECT_FRONTIER_MISSING"));
    }

    [Theory]
    [InlineData("GetEnumerator", true)]
    [InlineData("GetEnumerator", false)]
    [InlineData("MoveNext", true)]
    [InlineData("MoveNext", false)]
    [InlineData("Current", true)]
    [InlineData("Current", false)]
    [InlineData("Dispose", true)]
    [InlineData("Dispose", false)]
    public void AuthoredForeachEnumeratorMembersAreTraversedAtTheLoop(string effectMember, bool admitted)
    {
        string body = (admitted ? R2Admission : string.Empty) + "foreach (int item in new Probe()) { _ = item; } await Task.CompletedTask;";

        AssertEnumerationSite(EnumerationDiscover(EnumerationCompile(body, InterfaceEnumerable(effectMember))), admitted);
    }

    [Theory]
    [InlineData("GetEnumerator", true)]
    [InlineData("GetEnumerator", false)]
    [InlineData("MoveNext", true)]
    [InlineData("MoveNext", false)]
    [InlineData("Current", true)]
    [InlineData("Current", false)]
    [InlineData("Dispose", true)]
    [InlineData("Dispose", false)]
    public void PatternBasedForeachEnumeratorMembersAreTraversedWithoutAnyInterface(string effectMember, bool admitted)
    {
        string body = (admitted ? R2Admission : string.Empty) + "foreach (int item in new Probe()) { _ = item; } await Task.CompletedTask;";

        AssertEnumerationSite(EnumerationDiscover(EnumerationCompile(body, PatternEnumerable(effectMember))), admitted);
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
    public void AuthoredAwaitForeachEnumeratorMembersAreTraversedAtTheLoop(string effectMember, bool admitted)
    {
        string body = (admitted ? R2Admission : string.Empty) + "await foreach (int item in new Probe()) { _ = item; } await Task.CompletedTask;";

        AssertEnumerationSite(EnumerationDiscover(EnumerationCompile(body, AsyncInterfaceEnumerable(effectMember))), admitted);
    }

    [Theory]
    [InlineData("GetEnumerator", true)]
    [InlineData("GetEnumerator", false)]
    [InlineData("MoveNext", true)]
    [InlineData("MoveNext", false)]
    [InlineData("Current", true)]
    [InlineData("Current", false)]
    [InlineData("Dispose", true)]
    [InlineData("Dispose", false)]
    public void CollectionExpressionSpreadTraversesTheAuthoredEnumeratorAtTheSpread(string effectMember, bool admitted)
    {
        string body = (admitted ? R2Admission : string.Empty) + "int[] values = [.. new Probe()]; _ = values; await Task.CompletedTask;";

        AssertEnumerationSite(EnumerationDiscover(EnumerationCompile(body, InterfaceEnumerable(effectMember))), admitted);
    }

    [Theory]
    [InlineData("GetEnumerator", true)]
    [InlineData("GetEnumerator", false)]
    [InlineData("MoveNext", true)]
    [InlineData("MoveNext", false)]
    public void CollectionExpressionSpreadTraversesThePatternEnumeratorAtTheSpread(string effectMember, bool admitted)
    {
        string body = (admitted ? R2Admission : string.Empty) + "int[] values = [1, .. new Probe(), 2]; _ = values; await Task.CompletedTask;";

        AssertEnumerationSite(EnumerationDiscover(EnumerationCompile(body, PatternEnumerable(effectMember))), admitted);
    }

    // The same authored sequence reached through a local of its own type keeps its members visible.
    [Fact]
    public void AuthoredEnumerableHeldInALocalIsTraversedAtTheLoop()
    {
        string body = R2Admission + "Probe probe = new Probe(); foreach (int item in probe) { _ = item; } await Task.CompletedTask;";

        AssertEnumerationSite(EnumerationDiscover(EnumerationCompile(body, InterfaceEnumerable("MoveNext"))), admitted: true);
    }

    // A foreach reaches every member once per loop, so a loop nested twice still reports one site per
    // authored member occurrence and never a second copy for the same enumeration.
    [Fact]
    public void OneForeachReportsTheAuthoredEnumeratorSiteExactlyOnce()
    {
        string body = R2Admission + "foreach (int item in new Probe()) { _ = item; } await Task.CompletedTask;";

        HostedProducerSite site = Assert.Single(EnumerationDeleteSites(EnumerationDiscover(EnumerationCompile(body, InterfaceEnumerable("MoveNext")))));

        Assert.NotNull(site.WorkFrontierCapsuleId);
    }

    // MoveNext and Current run again after the loop body, so a lease disposed inside the body has ended the
    // Work before the next iteration's calls. GetEnumerator ran once, before the body, and keeps its Work.
    [Theory]
    [InlineData("GetEnumerator", true)]
    [InlineData("MoveNext", false)]
    [InlineData("Current", false)]
    [InlineData("Dispose", false)]
    public void LeaseDisposedInsideTheLoopBodyEndsTheWorkOfTheLaterEnumeratorCalls(string effectMember, bool keepsWork)
    {
        string body = R2Admission + "foreach (int item in new Probe()) { _ = item; lease.Dispose(); } await Task.CompletedTask;";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(body, InterfaceEnumerable(effectMember)));

        HostedProducerSite site = Assert.Single(EnumerationDeleteSites(result));

        Assert.Equal(keepsWork, site.WorkFrontierCapsuleId is not null);

        Assert.Equal(!keepsWork, EnumerationFrontierMissing(result, "HOSTED_SITE_WORK_FRONTIER_MISSING"));
    }

    // An authored GetEnumerator that is itself an iterator runs its whole body lazily, from the first MoveNext
    // to Dispose, so the loop-body disposal reaches it even though the GetEnumerator call preceded the body.
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void IteratorGetEnumeratorRunsItsBodyUnderTheLoopWideFrontier(bool disposesInBody, bool keepsWork)
    {
        string helpers = "sealed class Probe { public System.Collections.Generic.IEnumerator<int> GetEnumerator() { " + EnumerationEffect + "yield return 1; } }";

        string body = R2Admission + "foreach (int item in new Probe()) { _ = item; " + (disposesInBody ? "lease.Dispose(); " : string.Empty) + "} await Task.CompletedTask;";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(body, helpers));

        HostedProducerSite site = Assert.Single(EnumerationDeleteSites(result));

        Assert.Equal(keepsWork, site.WorkFrontierCapsuleId is not null);

        Assert.Equal(!keepsWork, EnumerationFrontierMissing(result, "HOSTED_SITE_WORK_FRONTIER_MISSING"));
    }

    [Fact]
    public void AuthoredIteratorMethodEnumeratedInsideTheRegionReportsItsBodyOnce()
    {
        string helpers = "static class Source { internal static System.Collections.Generic.IEnumerable<int> Items() { " + EnumerationEffect + "yield return 1; } }";

        string body = R2Admission + "foreach (int item in Source.Items()) { _ = item; } await Task.CompletedTask;";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(body, helpers));

        HostedProducerSite site = Assert.Single(EnumerationDeleteSites(result));

        Assert.NotNull(site.WorkFrontierCapsuleId);

        Assert.NotNull(site.EffectFrontierCapsuleId);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void AuthoredIteratorEnumeratedAfterItsLeaseWasDisposedLosesTheWork()
    {
        string helpers = "static class Source { internal static System.Collections.Generic.IEnumerable<int> Items() { " + EnumerationEffect + "yield return 1; } }";

        string body = R2Admission + "var items = Source.Items(); lease.Dispose(); foreach (int item in items) { _ = item; } await Task.CompletedTask;";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(body, helpers));

        HostedProducerSite site = Assert.Single(EnumerationDeleteSites(result));

        Assert.Null(site.WorkFrontierCapsuleId);

        Assert.True(EnumerationFrontierMissing(result, "HOSTED_SITE_WORK_FRONTIER_MISSING"));
    }

    [Fact]
    public void AuthoredAsyncIteratorEnumeratedInsideTheRegionReportsItsBodyOnce()
    {
        string helpers = "static class Source { internal static async System.Collections.Generic.IAsyncEnumerable<int> Items() { " + EnumerationEffect + "await Task.CompletedTask; yield return 1; } }";

        string body = R2Admission + "await foreach (int item in Source.Items()) { _ = item; } await Task.CompletedTask;";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(body, helpers));

        HostedProducerSite site = Assert.Single(EnumerationDeleteSites(result));

        Assert.NotNull(site.WorkFrontierCapsuleId);

        Assert.NotNull(site.EffectFrontierCapsuleId);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void AuthoredAsyncIteratorEnumeratedAfterItsLeaseWasDisposedLosesTheWork()
    {
        string helpers = "static class Source { internal static async System.Collections.Generic.IAsyncEnumerable<int> Items() { " + EnumerationEffect + "await Task.CompletedTask; yield return 1; } }";

        string body = R2Admission + "var items = Source.Items(); lease.Dispose(); await foreach (int item in items) { _ = item; } await Task.CompletedTask;";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(body, helpers));

        HostedProducerSite site = Assert.Single(EnumerationDeleteSites(result));

        Assert.Null(site.WorkFrontierCapsuleId);

        Assert.True(EnumerationFrontierMissing(result, "HOSTED_SITE_WORK_FRONTIER_MISSING"));
    }

    // Framework members that enumerate a sequence run its GetEnumerator themselves. An authored enumerable
    // whose enumerator contains a producer site is refused; the same call over an effect-free one is not.
    [Theory]
    [InlineData("_ = System.Linq.Enumerable.ToList(new Probe());", "System.Linq.Enumerable.ToList")]
    [InlineData("_ = System.Linq.Enumerable.ToArray(new Probe());", "System.Linq.Enumerable.ToArray")]
    [InlineData("_ = System.Linq.Enumerable.Select(new Probe(), item => item);", "System.Linq.Enumerable.Select")]
    [InlineData("_ = System.Linq.Enumerable.Count(new Probe());", "System.Linq.Enumerable.Count")]
    [InlineData("_ = new System.Collections.Generic.List<int>(new Probe());", "System.Collections.Generic.List`1..ctor")]
    [InlineData("_ = string.Join(\",\", new Probe());", "System.String.Join")]
    public void AuthoredEnumerablePassedToAnEnumeratingFrameworkMemberIsRefusedWhenItsEnumeratorHasEffects(string statement, string callee)
    {
        HostedProducerDiscovery<HostedProducerSite> refused = EnumerationDiscover(EnumerationCompile(R2Admission + statement + " await Task.CompletedTask;", InterfaceEnumerable("MoveNext")));

        Assert.Contains(refused.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
            && diagnostic.Detail.StartsWith(callee + ";", StringComparison.Ordinal));

        HostedProducerDiscovery<HostedProducerSite> clean = EnumerationDiscover(EnumerationCompile(R2Admission + statement + " await Task.CompletedTask;", InterfaceEnumerable("none")));

        Assert.DoesNotContain(clean.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    [Theory]
    [InlineData("_ = System.Linq.Enumerable.ToList(new System.Collections.Generic.List<int>());")]
    [InlineData("_ = System.Linq.Enumerable.ToList(new int[] { 1 });")]
    [InlineData("_ = System.Linq.Enumerable.Count(System.Collections.Immutable.ImmutableArray<int>.Empty);")]
    [InlineData("_ = new System.Collections.Generic.List<int>(new int[] { 1 });")]
    [InlineData("_ = string.Join(\",\", new System.Collections.Generic.List<int>());")]
    public void FrameworkCollectionsPassedToAnEnumeratingFrameworkMemberStayClassified(string statement)
    {
        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(R2Admission + statement + " await Task.CompletedTask;", InterfaceEnumerable("MoveNext")));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    [Theory]
    [InlineData("foreach (int item in new System.Collections.Generic.List<int>()) { _ = item; }")]
    [InlineData("foreach (int item in new int[] { 1 }) { _ = item; }")]
    [InlineData("foreach (int item in System.Collections.Immutable.ImmutableArray<int>.Empty) { _ = item; }")]
    [InlineData("foreach (var pair in new System.Collections.Generic.Dictionary<int, int>()) { _ = pair; }")]
    [InlineData("foreach (int item in new System.Collections.Generic.HashSet<int>()) { _ = item; }")]
    [InlineData("foreach (char item in \"text\") { _ = item; }")]
    [InlineData("System.Collections.Generic.IEnumerable<int> items = new System.Collections.Generic.List<int>(); foreach (int item in items) { _ = item; }")]
    [InlineData("int[] values = [.. new System.Collections.Generic.List<int>(), .. new int[] { 1 }]; _ = values;")]
    public void FrameworkCollectionEnumerationStaysClassifiedInsideTheRegion(string statement)
    {
        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(R2Admission + statement + " await Task.CompletedTask;", InterfaceEnumerable("MoveNext")));

        Assert.Empty(result.Diagnostics);

        Assert.Empty(EnumerationDeleteSites(result));
    }

    // A static type that is not a closed framework collection is never classified by its name or shape.
    [Fact]
    public void AuthoredSubclassThatHidesGetEnumeratorIsTraversedNotClassifiedAsTheFrameworkList()
    {
        string helpers = "sealed class Hiding : System.Collections.Generic.List<int> { public new ProbeEnumerator GetEnumerator() { " + EnumerationEffect + "return new ProbeEnumerator(); } } "
            + "struct ProbeEnumerator { public bool MoveNext() => false; public int Current => 0; }";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(R2Admission + "foreach (int item in new Hiding()) { _ = item; } await Task.CompletedTask;", helpers));

        HostedProducerSite site = Assert.Single(EnumerationDeleteSites(result));

        Assert.NotNull(site.WorkFrontierCapsuleId);
    }

    [Fact]
    public void AuthoredSubclassThatKeepsTheFrameworkListEnumeratorStaysClassified()
    {
        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(R2Admission + "foreach (int item in new Inheriting()) { _ = item; } await Task.CompletedTask;", "sealed class Inheriting : System.Collections.Generic.List<int> { }"));

        Assert.Empty(result.Diagnostics);
    }

    // An interface-typed receiver is not assumed to be a framework collection: nothing proves which
    // implementation the interface slot reaches, so the site is refused.
    [Theory]
    [InlineData("foreach (int item in UnprovedEnumerableInput.Items) { _ = item; }")]
    [InlineData("int[] values = [.. UnprovedEnumerableInput.Items]; _ = values;")]
    [InlineData("foreach (int item in UnprovedEnumerableInput.List) { _ = item; }")]
    public void UnprovenInterfaceTypedReceiverIsRefusedAsUnclassified(string statement)
    {
        string helpers = "static class UnprovedEnumerableInput { internal static System.Collections.Generic.IEnumerable<int> Items { get; set; } = null!; internal static System.Collections.Generic.IReadOnlyList<int> List { get; set; } = null!; }";

        CSharpCompilation compilation = EnumerationCompile(R2Admission + statement + " await Task.CompletedTask;", helpers);

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SyntaxNode site = tree.GetRoot().DescendantNodes().First(static node => node is CommonForEachStatementSyntax or SpreadElementSyntax);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Identity == tree.FilePath + ":" + site.SpanStart);
    }

    // The negative twin: the same interface-typed local, but with provenance that proves a framework list.
    [Fact]
    public void InterfaceTypedReceiverWithProvenAuthoredImplementationIsTraversed()
    {
        string body = R2Admission + "System.Collections.Generic.IEnumerable<int> items = new Probe(); foreach (int item in items) { _ = item; } await Task.CompletedTask;";

        AssertEnumerationSite(EnumerationDiscover(EnumerationCompile(body, InterfaceEnumerable("MoveNext"))), admitted: true);
    }

    [Fact]
    public void DynamicReceiverIsRefusedAsUnresolved()
    {
        CSharpCompilation compilation = EnumerationCompile(R2Admission + "dynamic items = new Probe(); foreach (int item in items) { _ = item; } await Task.CompletedTask;", InterfaceEnumerable("none"));

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_SITE_UNCLASSIFIED" or "HOSTED_CALL_TARGET_UNRESOLVED");
    }

    // A foreach element conversion is a user-defined call per iteration, written nowhere in the body.
    [Fact]
    public void AuthoredElementConversionOfAForeachIsTraversedPerIteration()
    {
        string helpers = "readonly struct Wrapped { public static implicit operator Wrapped(int value) { " + EnumerationEffect + "return default; } }";

        string body = R2Admission + "foreach (Wrapped item in new int[] { 1 }) { _ = item; } await Task.CompletedTask;";

        HostedProducerSite site = Assert.Single(EnumerationDeleteSites(EnumerationDiscover(EnumerationCompile(body, helpers))));

        Assert.NotNull(site.WorkFrontierCapsuleId);
    }
}
