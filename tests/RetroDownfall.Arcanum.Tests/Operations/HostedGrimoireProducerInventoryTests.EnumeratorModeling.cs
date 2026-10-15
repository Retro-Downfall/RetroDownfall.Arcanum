using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

// The EnumeratorModeling fixtures: the implicit enumerator calls of foreach, await foreach and spread elements, the
// Implicit Enumeration contract of docs/Arcanum.DESIGN.md. EnumeratorCompleteness holds the adversarial twins.
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

    // The production compilations resolve the framework through its reference assemblies, not the runtime
    // ones, so the closed set must be recognised through the same identity mapping the other reviewed
    // framework types use.
    [Theory]
    [InlineData("foreach (int item in new System.Collections.Generic.List<int>()) { _ = item; }")]
    [InlineData("foreach (int item in new int[] { 1 }) { _ = item; }")]
    [InlineData("foreach (int item in System.Collections.Immutable.ImmutableArray<int>.Empty) { _ = item; }")]
    [InlineData("foreach (var pair in new System.Collections.Generic.Dictionary<int, int>()) { _ = pair; }")]
    [InlineData("foreach (int item in new System.Collections.Generic.Dictionary<int, int>().Values) { _ = item; }")]
    [InlineData("foreach (int item in new System.Collections.Generic.HashSet<int>()) { _ = item; }")]
    [InlineData("foreach (var pair in new System.Collections.Concurrent.ConcurrentDictionary<int, int>()) { _ = pair; }")]
    [InlineData("foreach (char item in \"text\") { _ = item; }")]
    [InlineData("foreach (var item in System.Text.Json.JsonDocument.Parse(\"[]\").RootElement.EnumerateArray()) { _ = item; }")]
    [InlineData("foreach (int item in System.Linq.Enumerable.Range(0, 1)) { _ = item; }")]
    [InlineData("System.Collections.Generic.IReadOnlyList<int> items = new System.Collections.Generic.List<int>(); foreach (int item in items) { _ = item; }")]
    [InlineData("var builder = System.Collections.Immutable.ImmutableArray.CreateBuilder<int>(); foreach (int item in builder) { _ = item; }")]
    [InlineData("var builder = System.Collections.Immutable.ImmutableArray.CreateBuilder<int>(); System.Collections.Generic.IReadOnlyList<int> view = builder; foreach (int item in view) { _ = item; }")]
    [InlineData("var set = new System.Collections.Generic.SortedSet<string>(System.StringComparer.Ordinal); foreach (string item in set) { _ = item; }")]
    [InlineData("foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(\"a\", \"a\")) { _ = match; }")]
    public void ClosedFrameworkCollectionsAreRecognisedAgainstTheProductionReferencePack(string statement)
    {
        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(R2Admission + statement + " await Task.CompletedTask;"), "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_SITE_UNCLASSIFIED" or "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    // A value followed through a field or property initializer in another file is proved against that file's
    // semantic model, never the model of the member that reads it.
    [Theory]
    [InlineData("internal static readonly System.Collections.Generic.IEnumerable<int> Items = System.Linq.Enumerable.ToList(new int[] { 1 });", true)]
    [InlineData("internal static System.Collections.Generic.IEnumerable<int> Items => Make(); private static System.Collections.Generic.List<int> Make() => new System.Collections.Generic.List<int>();", true)]
    [InlineData("internal static readonly System.Collections.Generic.IEnumerable<int> Items = System.Linq.Enumerable.Select(new int[] { 1 }, value => value);", true)]
    [InlineData("internal static readonly System.Collections.Generic.IEnumerable<int> Items = Unproved.Items;", false)]
    [InlineData("internal static System.Collections.Generic.IEnumerable<int> Items => Make(); private static System.Collections.Generic.IEnumerable<int> Make() => Unproved.Items;", false)]
    public void ValueFollowedThroughAnotherSyntaxTreeIsProvedInThatTree(string declaration, bool closed)
    {
        string other = "static class Unproved { internal static System.Collections.Generic.IEnumerable<int> Items { get; set; } = null!; } static class Shared { " + declaration + " }";

        CSharpCompilation compilation = Compile(R2Source(R2Admission + "foreach (int item in Shared.Items) { _ = item; } await Task.CompletedTask;"))
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(other, path: "src/Shared.cs"));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        SyntaxTree tree = compilation.SyntaxTrees.Single(static candidate => candidate.FilePath == "src/Fixture.cs");

        SyntaxNode loop = tree.GetRoot().DescendantNodes().First(static node => node is CommonForEachStatementSyntax);

        Assert.Equal(!closed, result.Diagnostics.Any(diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Identity == tree.FilePath + ":" + loop.SpanStart));
    }

    // A virtual GetEnumerator runs the override of the exact runtime type, never the base body the static type names.
    [Theory]
    [InlineData("foreach (int item in new Derived()) { _ = item; }", "derived", true)]
    [InlineData("Base source = new Derived(); foreach (int item in source) { _ = item; }", "derived", true)]
    [InlineData("Base source = new Derived(); foreach (int item in source) { _ = item; }", "base", false)]
    [InlineData("Base source = new Base(); foreach (int item in source) { _ = item; }", "base", true)]
    [InlineData("Base source = new Base(); foreach (int item in source) { _ = item; }", "derived", false)]
    public void VirtualGetEnumeratorRunsTheOverrideOfTheExactRuntimeType(string statement, string effectIn, bool reported)
    {
        string helpers = "class Base : System.Collections.Generic.IEnumerable<int> { "
            + "public virtual System.Collections.Generic.IEnumerator<int> GetEnumerator() { " + (effectIn == "base" ? EnumerationEffect : string.Empty) + "return new ProbeEnumerator(); } "
            + "System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator(); } "
            + "sealed class Derived : Base { public override System.Collections.Generic.IEnumerator<int> GetEnumerator() { " + (effectIn == "derived" ? EnumerationEffect : string.Empty) + "return new ProbeEnumerator(); } } "
            + "sealed class ProbeEnumerator : System.Collections.Generic.IEnumerator<int> { public bool MoveNext() => false; public int Current => 0; object System.Collections.IEnumerator.Current => Current; public void Reset() { } public void Dispose() { } }";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(R2Admission + statement + " await Task.CompletedTask;", helpers));

        Assert.Equal(reported, EnumerationDeleteSites(result).Length == 1);
    }

    // A sealed class that is its own enumerator is followed through GetEnumerator's returned `this`.
    [Theory]
    [InlineData("MoveNext")]
    [InlineData("Current")]
    [InlineData("Dispose")]
    public void SelfEnumeratingSealedClassIsTraversedThroughItsReturnedThis(string effectMember)
    {
        string helpers = "sealed class Probe : System.Collections.Generic.IEnumerable<int>, System.Collections.Generic.IEnumerator<int> { "
            + "public System.Collections.Generic.IEnumerator<int> GetEnumerator() => this; "
            + "System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => this; "
            + "public bool MoveNext() { " + EnumerationEffectIn(effectMember, "MoveNext") + "return false; } "
            + "public int Current { get { " + EnumerationEffectIn(effectMember, "Current") + "return 0; } } "
            + "object System.Collections.IEnumerator.Current => Current; public void Reset() { } "
            + "public void Dispose() { " + EnumerationEffectIn(effectMember, "Dispose") + "} }";

        AssertEnumerationSite(EnumerationDiscover(EnumerationCompile(R2Admission + "foreach (int item in new Probe()) { _ = item; } await Task.CompletedTask;", helpers)), admitted: true);
    }

    // Closed is a reviewed list, not "a framework type": wrappers that forward to a caller-supplied list run that
    // list's enumerator, so a framework collection outside the list is refused even with a known allocation.
    [Theory]
    [InlineData("foreach (int item in new System.Collections.ObjectModel.ReadOnlyCollection<int>(new System.Collections.Generic.List<int>())) { _ = item; }")]
    [InlineData("foreach (int item in new System.Collections.ObjectModel.Collection<int>()) { _ = item; }")]
    [InlineData("int[] values = [.. new System.Collections.Generic.LinkedList<int>()]; _ = values;")]
    public void FrameworkCollectionOutsideTheClosedSetIsRefusedEvenWithAKnownAllocation(string statement)
    {
        CSharpCompilation compilation = EnumerationCompile(R2Admission + statement + " await Task.CompletedTask;", InterfaceEnumerable("none"));

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SyntaxNode site = tree.GetRoot().DescendantNodes().First(static node => node is CommonForEachStatementSyntax or SpreadElementSyntax);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Identity == tree.FilePath + ":" + site.SpanStart);
    }

    // Types whose enumeration is safe only for their exact runtime type: an allocation or a sealed static type
    // is accepted, a storage location typed as the unsealed collection is not (it could hold a comparer-calling view).
    [Theory]
    [InlineData("var set = new System.Collections.Generic.SortedSet<string>(System.StringComparer.Ordinal); foreach (string item in set) { _ = item; }", true)]
    [InlineData("var set = new System.Collections.Generic.SortedSet<string>(System.StringComparer.Ordinal); string[] values = [.. set]; _ = values;", true)]
    [InlineData("foreach (int item in new System.Collections.Generic.SortedSet<int>().GetViewBetween(1, 2)) { _ = item; }", false)]
    [InlineData("foreach (int item in UnprovedSets.Set) { _ = item; }", false)]
    [InlineData("foreach (System.Security.AccessControl.GenericAce ace in new System.Security.AccessControl.RawAcl(2, 0)) { _ = ace; }", true)]
    [InlineData("foreach (System.Security.AccessControl.AuthorizationRule rule in new System.Security.AccessControl.DirectorySecurity().GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))) { _ = rule; }", true)]
    [InlineData("foreach (System.Security.AccessControl.GenericAce ace in UnprovedSets.Acl) { _ = ace; }", false)]
    [InlineData("foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(\"a\", \"a\")) { _ = match; }", true)]
    [InlineData("var builder = System.Collections.Immutable.ImmutableArray.CreateBuilder<int>(); foreach (int item in builder) { _ = item; }", true)]
    [InlineData("var builder = System.Collections.Immutable.ImmutableArray.CreateBuilder<int>(); System.Collections.Generic.IReadOnlyList<int> view = builder; foreach (int item in view) { _ = item; }", true)]
    [InlineData("var builder = System.Collections.Immutable.ImmutableList.CreateBuilder<int>(); foreach (int item in builder) { _ = item; }", false)]
    public void ExactTypeOnlyFrameworkCollectionsAreAcceptedOnlyForTheirExactType(string statement, bool accepted)
    {
        string helpers = "static class UnprovedSets { internal static System.Collections.Generic.SortedSet<int> Set { get; set; } = null!; internal static System.Security.AccessControl.GenericAcl Acl { get; set; } = null!; }";

        CSharpCompilation compilation = EnumerationCompile(R2Admission + statement + " await Task.CompletedTask;", helpers);

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SyntaxNode site = tree.GetRoot().DescendantNodes().First(static node => node is CommonForEachStatementSyntax or SpreadElementSyntax);

        Assert.Equal(!accepted, result.Diagnostics.Any(diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Identity == tree.FilePath + ":" + site.SpanStart));
    }

    // A scoped enumerator is the whole consumption of a lazy sequence: the body runs between its declaration and
    // the end of its scope, so the Work live at that end is what the body ran under.
    [Theory]
    [InlineData("await using var e = Source.Items().GetAsyncEnumerator(token); while (await e.MoveNextAsync().ConfigureAwait(false)) { _ = e.Current; }", true)]
    [InlineData("await using (var e = Source.Items().GetAsyncEnumerator(token)) { while (await e.MoveNextAsync()) { _ = e.Current; } }", true)]
    [InlineData("await using var e = Source.Items().GetAsyncEnumerator(token); while (await e.MoveNextAsync()) { lease.Dispose(); }", false)]
    [InlineData("await using var e = Source.Items().GetAsyncEnumerator(token); Keep(e);", false)]
    [InlineData("await using var e = Source.Items().GetAsyncEnumerator(token); Func<System.Threading.Tasks.ValueTask<bool>> next = () => e.MoveNextAsync(); await next();", false)]
    [InlineData("await using var e = Source.Items().GetAsyncEnumerator(token); await e.DisposeAsync();", false)]
    [InlineData("var e = Source.Items().GetAsyncEnumerator(token); while (await e.MoveNextAsync()) { _ = e.Current; }", false)]
    public void AsyncIteratorConsumedThroughAUsingScopedEnumeratorKeepsItsWorkOnlyWhileTheScopeIsProven(string statement, bool owned)
    {
        string helpers = "static class Source { internal static async System.Collections.Generic.IAsyncEnumerable<int> Items() { " + EnumerationEffect + "await Task.CompletedTask; yield return 1; } } "
            + "static class Sink { internal static void Keep(System.Collections.Generic.IAsyncEnumerator<int> value) { } }";

        string body = R2Admission + statement.Replace("Keep(e)", "Sink.Keep(e)", StringComparison.Ordinal) + " await Task.CompletedTask;";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(body, helpers));

        HostedProducerSite site = Assert.Single(EnumerationDeleteSites(result));

        Assert.Equal(owned, site.WorkFrontierCapsuleId is not null);

        Assert.Equal(!owned, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"));
    }

    [Theory]
    [InlineData("using var e = Source.Items().GetEnumerator(); while (e.MoveNext()) { _ = e.Current; }", true)]
    [InlineData("using var e = Source.Items().GetEnumerator(); while (e.MoveNext()) { lease.Dispose(); }", false)]
    [InlineData("using var e = Source.Items().GetEnumerator(); Sink.Keep(e);", false)]
    public void IteratorConsumedThroughAUsingScopedEnumeratorKeepsItsWorkOnlyWhileTheScopeIsProven(string statement, bool owned)
    {
        string helpers = "static class Source { internal static System.Collections.Generic.IEnumerable<int> Items() { " + EnumerationEffect + "yield return 1; } } "
            + "static class Sink { internal static void Keep(System.Collections.Generic.IEnumerator<int> value) { } }";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(R2Admission + statement + " await Task.CompletedTask;", helpers));

        HostedProducerSite site = Assert.Single(EnumerationDeleteSites(result));

        Assert.Equal(owned, site.WorkFrontierCapsuleId is not null);
    }

    // A returned asynchronous sequence is consumed by the caller of the returning method. That matters only when
    // its body can reach a producer site; a body that cannot is not refused.
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ReturnedAsyncSequenceIsRefusedOnlyWhenItsBodyCanReachAProducerSite(bool effect, bool refused)
    {
        string helpers = "static class Source { internal static async System.Collections.Generic.IAsyncEnumerable<int> Items() { " + (effect ? EnumerationEffect : string.Empty) + "await Task.CompletedTask; yield return 1; } "
            + "internal static System.Collections.Generic.IAsyncEnumerable<int> Forward() => Items(); }";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(R2Admission + "_ = Source.Forward(); await Task.CompletedTask;", helpers));

        Assert.Equal(refused, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"));
    }

    // The synchronous rule is unchanged: a returned lazy sequence is refused whatever its body does.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReturnedSynchronousSequenceIsStillRefusedWhateverItsBodyDoes(bool effect)
    {
        string helpers = "static class Source { internal static System.Collections.Generic.IEnumerable<int> Items() { " + (effect ? EnumerationEffect : string.Empty) + "yield return 1; } "
            + "internal static System.Collections.Generic.IEnumerable<int> Forward() => Items(); }";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(R2Admission + "_ = Source.Forward(); await Task.CompletedTask;", helpers));

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    // A sequence given to a framework operator is enumerated by the framework value the operator returns, through
    // the given sequence's own members. The loop over that value is closed only when what it wraps is acceptable:
    // an authored one is the enumerating-member rule's concern at the call, a framework one must be closed.
    [Theory]
    [InlineData("MoveNext", true)]
    [InlineData("none", false)]
    public void LoopOverAFrameworkOperatorWrappingAnAuthoredSequenceIsJudgedAtTheOperatorCall(string effectMember, bool refused)
    {
        CSharpCompilation compilation = EnumerationCompile(R2Admission + "foreach (int item in System.Linq.Enumerable.Select(new Probe(), value => value)) { _ = item; } await Task.CompletedTask;", InterfaceEnumerable(effectMember));

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        Assert.Equal(refused, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED");
    }

    [Fact]
    public void LoopOverAFrameworkOperatorWrappingAnUnreviewedFrameworkCollectionIsRefused()
    {
        CSharpCompilation compilation = EnumerationCompile(R2Admission + "foreach (int item in System.Linq.Enumerable.Select(new System.Collections.ObjectModel.ReadOnlyCollection<int>(new System.Collections.Generic.List<int>()), value => value)) { _ = item; } await Task.CompletedTask;", InterfaceEnumerable("none"));

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SyntaxNode loop = tree.GetRoot().DescendantNodes().First(static node => node is CommonForEachStatementSyntax);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Identity == tree.FilePath + ":" + loop.SpanStart);
    }

    // A value followed through a caller in another project carries that project's symbols. Roslyn does not equate
    // symbols of different compilations, so the callee's loop slots are matched to the candidate by identity.
    [Theory]
    [InlineData("MoveNext", true)]
    [InlineData("none", false)]
    public void AuthoredSequenceHandedToALoopInAnotherCompilationIsStillTraversed(string effectMember, bool reported)
    {
        // Fresh metadata references, so the library's framework symbols are not the application's (the project
        // compilations of the production universe load their references separately in the same way).
        MetadataReference[] separateReferences = [.. TrustedPlatformReferences.Separate()];

        CSharpCompilation library = CSharpCompilation.Create(
            "Library",
            [CSharpSyntaxTree.ParseText("public static class Policy { public static void Prepare(System.Collections.Generic.IReadOnlyList<int> items) { foreach (int item in items) { _ = item; } } }", path: "src/Library.cs")],
            separateReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        string probe = "sealed class Probe : System.Collections.Generic.IReadOnlyList<int> { public int Count => 0; public int this[int index] => 0; "
            + "public System.Collections.Generic.IEnumerator<int> GetEnumerator() { return new ProbeEnumerator(); } "
            + "System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator(); } "
            + "sealed class ProbeEnumerator : System.Collections.Generic.IEnumerator<int> { public bool MoveNext() { " + EnumerationEffectIn(effectMember, "MoveNext") + "return false; } public int Current => 0; object System.Collections.IEnumerator.Current => Current; public void Reset() { } public void Dispose() { } }";

        CSharpCompilation app = Compile(R2Source(R2Admission + "Policy.Prepare(new Probe()); await Task.CompletedTask;", probe))
            .AddReferences(library.ToMetadataReference());

        Assert.Empty(app.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [library, app], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Equal(reported, EnumerationDeleteSites(result).Length == 1);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED");
    }

    // A ConfigureAwait wrapper kept in a local is followed to the sequence it wraps, not trusted by its own type.
    [Theory]
    [InlineData("var configured = Source.Items().ConfigureAwait(false); await foreach (int item in configured) { _ = item; }", true)]
    [InlineData("var configured = Unproved.Items.ConfigureAwait(false); await foreach (int item in configured) { _ = item; }", false)]
    public void StoredConfigureAwaitWrapperIsFollowedToTheSequenceItWraps(string statement, bool closed)
    {
        string helpers = "static class Source { internal static async System.Collections.Generic.IAsyncEnumerable<int> Items() { " + EnumerationEffect + "await Task.CompletedTask; yield return 1; } } "
            + "static class Unproved { internal static System.Collections.Generic.IAsyncEnumerable<int> Items { get; set; } = null!; }";

        CSharpCompilation compilation = EnumerationCompile(R2Admission + statement + " await Task.CompletedTask;", helpers);

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SyntaxNode loop = tree.GetRoot().DescendantNodes().First(static node => node is CommonForEachStatementSyntax);

        Assert.Equal(!closed, result.Diagnostics.Any(diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Identity == tree.FilePath + ":" + loop.SpanStart));

        if (closed)
        {
            Assert.NotNull(Assert.Single(EnumerationDeleteSites(result)).WorkFrontierCapsuleId);

            Assert.Empty(result.Diagnostics);
        }
    }

    // A cast through a user-defined conversion hands the loop whatever the operator returns, not the cast operand.
    [Theory]
    [InlineData("foreach (int item in (Base)new Convertible()) { _ = item; }", false)]
    [InlineData("foreach (int item in (Base)new Derived()) { _ = item; }", true)]
    public void CastThroughAUserDefinedConversionIsNotFollowedToItsOperand(string statement, bool followed)
    {
        string helpers = "class Base : System.Collections.Generic.IEnumerable<int> { "
            + "public virtual System.Collections.Generic.IEnumerator<int> GetEnumerator() { return new ProbeEnumerator(); } "
            + "System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator(); } "
            + "sealed class Derived : Base { public override System.Collections.Generic.IEnumerator<int> GetEnumerator() { " + EnumerationEffect + "return new ProbeEnumerator(); } } "
            + "sealed class Convertible { public static explicit operator Base(Convertible value) => new Derived(); } "
            + "sealed class ProbeEnumerator : System.Collections.Generic.IEnumerator<int> { public bool MoveNext() => false; public int Current => 0; object System.Collections.IEnumerator.Current => Current; public void Reset() { } public void Dispose() { } }";

        CSharpCompilation compilation = EnumerationCompile(R2Admission + statement + " await Task.CompletedTask;", helpers);

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SyntaxNode loop = tree.GetRoot().DescendantNodes().First(static node => node is CommonForEachStatementSyntax);

        Assert.Equal(!followed, result.Diagnostics.Any(diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Identity == tree.FilePath + ":" + loop.SpanStart));

        Assert.Equal(followed, EnumerationDeleteSites(result).Length == 1);
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

    [Fact]
    public void AuthoredGenericEnumerableIsTraversedThroughItsConstructedInterfaceMembers()
    {
        string helpers = "sealed class Box<T> : System.Collections.Generic.IEnumerable<T> { "
            + "public System.Collections.Generic.IEnumerator<T> GetEnumerator() { " + EnumerationEffect + "return new BoxEnumerator<T>(); } "
            + "System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator(); } "
            + "sealed class BoxEnumerator<T> : System.Collections.Generic.IEnumerator<T> { "
            + "public bool MoveNext() => false; public T Current => default!; object System.Collections.IEnumerator.Current => Current!; public void Reset() { } public void Dispose() { } }";

        HostedProducerSite site = Assert.Single(EnumerationDeleteSites(EnumerationDiscover(EnumerationCompile(R2Admission + "foreach (int item in new Box<int>()) { _ = item; } await Task.CompletedTask;", helpers))));

        Assert.NotNull(site.WorkFrontierCapsuleId);
    }

    [Fact]
    public void AuthoredAsyncIteratorBehindConfigureAwaitIsOwnedByTheAwaitForeach()
    {
        string helpers = "static class Source { internal static async System.Collections.Generic.IAsyncEnumerable<int> Items() { " + EnumerationEffect + "await Task.CompletedTask; yield return 1; } }";

        string body = R2Admission + "await foreach (int item in Source.Items().ConfigureAwait(false)) { _ = item; } await Task.CompletedTask;";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(body, helpers));

        HostedProducerSite site = Assert.Single(EnumerationDeleteSites(result));

        Assert.NotNull(site.WorkFrontierCapsuleId);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void AuthoredAsyncIteratorBehindConfigureAwaitEnumeratedAfterDisposalLosesTheWork()
    {
        string helpers = "static class Source { internal static async System.Collections.Generic.IAsyncEnumerable<int> Items() { " + EnumerationEffect + "await Task.CompletedTask; yield return 1; } }";

        string body = R2Admission + "var items = Source.Items(); lease.Dispose(); await foreach (int item in items.ConfigureAwait(false)) { _ = item; } await Task.CompletedTask;";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(body, helpers));

        HostedProducerSite site = Assert.Single(EnumerationDeleteSites(result));

        Assert.Null(site.WorkFrontierCapsuleId);
    }

    // An interface-typed local fed by an authored iterator is the iterator's own enumerator: the body is
    // traversed once, where the iterator is called, never a second time at the loop.
    [Fact]
    public void InterfaceTypedLocalFedByAnAuthoredIteratorIsNotTraversedTwice()
    {
        string helpers = "static class Source { internal static System.Collections.Generic.IEnumerable<int> Items() { " + EnumerationEffect + "yield return 1; } }";

        string body = R2Admission + "System.Collections.Generic.IEnumerable<int> items = Source.Items(); foreach (int item in items) { _ = item; } await Task.CompletedTask;";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(body, helpers));

        HostedProducerSite site = Assert.Single(EnumerationDeleteSites(result));

        Assert.NotNull(site.WorkFrontierCapsuleId);

        Assert.Empty(result.Diagnostics);
    }

    // A framework sequence built over a closed collection stays closed; the same operator over an unproven
    // sequence is refused, so the closed shape is the only thing accepted.
    [Theory]
    [InlineData("foreach (int item in System.Linq.Enumerable.Select(new int[] { 1 }, value => value)) { _ = item; }", true)]
    [InlineData("foreach (int item in System.Linq.Enumerable.Where(new System.Collections.Generic.List<int>(), value => value > 0)) { _ = item; }", true)]
    [InlineData("foreach (int item in System.Linq.Enumerable.Select(UnprovedEnumerableInput.Items, value => value)) { _ = item; }", false)]
    [InlineData("foreach (int item in System.Linq.Enumerable.Concat(new int[] { 1 }, UnprovedEnumerableInput.Items)) { _ = item; }", false)]
    public void FrameworkSequenceOperatorIsClosedOnlyOverClosedSequences(string statement, bool closed)
    {
        string helpers = "static class UnprovedEnumerableInput { internal static System.Collections.Generic.IEnumerable<int> Items { get; set; } = null!; }";

        CSharpCompilation compilation = EnumerationCompile(R2Admission + statement + " await Task.CompletedTask;", helpers);

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SyntaxNode loop = tree.GetRoot().DescendantNodes().First(static node => node is CommonForEachStatementSyntax);

        Assert.Equal(!closed, result.Diagnostics.Any(diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Identity == tree.FilePath + ":" + loop.SpanStart));
    }

    // A type that only shares a framework collection's name is not that collection: the unreviewed assembly's
    // members run unseen, so the loop is refused.
    [Fact]
    public void ForeignTypeSharingAClosedCollectionNameIsNotClosed()
    {
        CSharpCompilation foreign = Compile("namespace System.Collections.Generic { public sealed class Queue<T> { public Enumerator GetEnumerator() => default; public struct Enumerator { public bool MoveNext() { System.IO.File.Delete(\"foreign-enumeration\"); return false; } public T Current => default!; } } } "
            + "namespace Unreviewed { public static class ForeignFactory { public static System.Collections.Generic.Queue<int> Make() => new System.Collections.Generic.Queue<int>(); } }")
            .WithAssemblyName("Unreviewed.Collections");

        using MemoryStream image = new();

        Microsoft.CodeAnalysis.Emit.EmitResult emitted = foreign.Emit(image);

        Assert.True(emitted.Success, string.Join(global::System.Environment.NewLine, emitted.Diagnostics));

        CSharpCompilation compilation = Compile(R2Source(R2Admission + "foreach (int item in Unreviewed.ForeignFactory.Make()) { _ = item; } await Task.CompletedTask;"))
            .AddReferences(MetadataReference.CreateFromImage(image.ToArray()));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SyntaxNode loop = tree.GetRoot().DescendantNodes().First(static node => node is CommonForEachStatementSyntax);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Identity == tree.FilePath + ":" + loop.SpanStart);
    }

    // An opaque callback that enumerates an authored sequence inside its body is judged by what that
    // enumerator can do, exactly as if the enumerator's call were written in the body.
    [Theory]
    [InlineData("MoveNext", true)]
    [InlineData("GetEnumerator", true)]
    [InlineData("none", false)]
    public void OpaqueCallbackBodyThatEnumeratesAnAuthoredSequenceIsJudgedByThatEnumerator(string effectMember, bool refused)
    {
        string body = R2Admission + "GC.KeepAlive((Action)(() => { foreach (int item in new Probe()) { _ = item; } })); await Task.CompletedTask;";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(body, InterfaceEnumerable(effectMember)));

        Assert.Equal(refused, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"));
    }

    [Theory]
    [InlineData("foreach (long item in new int[] { 1 }) { _ = item; }")]
    [InlineData("foreach (object item in new System.Collections.Generic.List<int>()) { _ = item; }")]
    public void BuiltInForeachElementConversionsAddNoCall(string statement)
    {
        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(EnumerationCompile(R2Admission + statement + " await Task.CompletedTask;", InterfaceEnumerable("none")));

        Assert.Empty(result.Diagnostics);
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
