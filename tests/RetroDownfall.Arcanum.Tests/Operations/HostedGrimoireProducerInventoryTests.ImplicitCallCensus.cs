using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

// The Implicit Call Census self-check. Every construct of the corpus is compiled three times: inside an admitted
// region with its effect, outside one with its effect, and inside one with the effect removed. A traversed
// construct reports its effect under the frontiers live where the compiler runs the member, reports it with no
// frontier (and the missing-frontier diagnostics) outside an admitted region, and reports nothing when the
// member has no effect. A refused construct is refused with a diagnostic whatever the member does. A construct
// the checker neither reports nor refuses is silently admitted, which the census exists to rule out.
public sealed partial class HostedGrimoireProducerInventoryTests
{
    public static IEnumerable<object[]> CensusConstructNames() => CensusCorpus.Select(static construct => new object[] { construct.Name });

    private static CSharpCompilation CensusCompile(CensusConstruct construct, bool admitted, bool effect, string before = "", string after = "")
    {
        string helpers = construct.Helpers.Replace("{E}", effect ? EnumerationEffect : string.Empty, StringComparison.Ordinal);

        // A construct that is followed by code runs in a block of its own, so that a using declaration ends with it.
        string construction = after.Length == 0 ? construct.Body : "{ " + construct.Body + " }";

        string body = (admitted ? R2Admission : string.Empty) + before + construction + " " + after + " await Task.CompletedTask;";

        CSharpCompilation compilation = Compile("using System.Linq;\n" + R2Source(body, helpers));

        compilation = compilation.WithOptions(compilation.Options.WithAllowUnsafe(true));

        Diagnostic[] errors = [.. compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)];

        Assert.True(errors.Length == 0, construct.Name + ": " + string.Join(" | ", errors.Select(static error => error.ToString())));

        return compilation;
    }

    private static bool CensusRefused(HostedProducerDiscovery<HostedProducerSite> result) =>
        result.Diagnostics.Any(static diagnostic => diagnostic.Code is "HOSTED_SITE_UNCLASSIFIED" or "HOSTED_CALL_TARGET_UNRESOLVED" or "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
            && diagnostic.Identity.Contains("src/Fixture.cs", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(CensusConstructNames))]
    public void EveryImplicitCallConstructIsTraversedOrRefused(string name)
    {
        CensusConstruct construct = CensusCorpus.Single(candidate => candidate.Name == name);

        HostedProducerDiscovery<HostedProducerSite> admitted = EnumerationDiscover(CensusCompile(construct, admitted: true, effect: true));

        HostedProducerSite[] sites = EnumerationDeleteSites(admitted);

        if (construct.Expected == CensusDisposition.Detached)
        {
            AssertDetached(construct, admittedRegion => EnumerationDiscover(CensusCompile(construct, admitted: admittedRegion, effect: true)), EnumerationDiscover(CensusCompile(construct, admitted: true, effect: false)));

            return;
        }

        if (construct.Expected != CensusDisposition.Traversed)
        {
            Assert.True(CensusRefused(admitted), construct.Name + ": the construct is neither reported nor refused. Sites: " + sites.Length + "; diagnostics: " + string.Join(" | ", admitted.Diagnostics.Select(static diagnostic => diagnostic.Code + " " + diagnostic.Detail)));

            HostedProducerDiscovery<HostedProducerSite> unadmittedRefusal = EnumerationDiscover(CensusCompile(construct, admitted: false, effect: true));

            Assert.True(CensusRefused(unadmittedRefusal), construct.Name + ": the construct outside an admitted region is not refused.");

            HostedProducerDiscovery<HostedProducerSite> twin = EnumerationDiscover(CensusCompile(construct, admitted: true, effect: false));

            Assert.Equal(construct.Expected == CensusDisposition.RefusedAlways, CensusRefused(twin));

            return;
        }

        Assert.True(sites.Length != 0, construct.Name + ": the authored member's effect is silently admitted. Diagnostics: " + string.Join(" | ", admitted.Diagnostics.Select(static diagnostic => diagnostic.Code + " " + diagnostic.Detail)));

        Assert.True(sites.Length == 1, construct.Name + ": the member the compiler calls is reported " + sites.Length + " times, not once.");

        Assert.All(sites, site => Assert.True(site.WorkFrontierCapsuleId is not null && site.EffectFrontierCapsuleId is not null, construct.Name + ": the effect is reported without the frontier live at the construct."));

        Assert.False(CensusRefused(admitted), construct.Name + ": a traversed construct is also refused. " + string.Join(" | ", admitted.Diagnostics.Select(static diagnostic => diagnostic.Code + " " + diagnostic.Identity + " " + diagnostic.Detail)));

        HostedProducerDiscovery<HostedProducerSite> unadmitted = EnumerationDiscover(CensusCompile(construct, admitted: false, effect: true));

        HostedProducerSite[] unadmittedSites = EnumerationDeleteSites(unadmitted);

        Assert.True(unadmittedSites.Length != 0, construct.Name + ": the effect outside an admitted region is silently admitted.");

        Assert.All(unadmittedSites, site => Assert.Null(site.WorkFrontierCapsuleId));

        Assert.True(EnumerationFrontierMissing(unadmitted, "HOSTED_SITE_WORK_FRONTIER_MISSING"), construct.Name + ": the missing Work frontier outside an admitted region is not reported.");

        HostedProducerDiscovery<HostedProducerSite> clean = EnumerationDiscover(CensusCompile(construct, admitted: true, effect: false));

        Assert.Empty(EnumerationDeleteSites(clean));

        Assert.False(CensusRefused(clean), construct.Name + ": the effect-free twin is refused. " + string.Join(" | ", clean.Diagnostics.Select(static diagnostic => diagnostic.Code + " " + diagnostic.Identity + " " + diagnostic.Detail)));
    }

    // A construct whose member runs where the value is consumed is reported, once, under no frontier, whether or not the
    // region that writes it is admitted, and is not refused; the twin without the effect reports and refuses nothing.
    private static void AssertDetached(CensusConstruct construct, Func<bool, HostedProducerDiscovery<HostedProducerSite>> run, HostedProducerDiscovery<HostedProducerSite> clean)
    {
        foreach (bool admittedRegion in new[] { true, false })
        {
            HostedProducerDiscovery<HostedProducerSite> result = run(admittedRegion);

            HostedProducerSite site = Assert.Single(EnumerationDeleteSites(result));

            Assert.True(site.WorkFrontierCapsuleId is null && site.EffectFrontierCapsuleId is null, construct.Name + ": a member that runs where the value is consumed is reported under a frontier.");

            Assert.False(CensusRefused(result), construct.Name + ": a detached construct is also refused. " + string.Join(" | ", result.Diagnostics.Select(static diagnostic => diagnostic.Code + " " + diagnostic.Detail)));
        }

        Assert.Empty(EnumerationDeleteSites(clean));

        Assert.False(CensusRefused(clean), construct.Name + ": the effect-free twin is refused. " + string.Join(" | ", clean.Diagnostics.Select(static diagnostic => diagnostic.Code + " " + diagnostic.Detail)));
    }

    public static IEnumerable<object[]> CensusTraversedConstructNames() =>
        CensusCorpus.Where(static construct => construct.Expected == CensusDisposition.Traversed).Select(static construct => new object[] { construct.Name });

    // The frontier a census item is traversed under is the one live where the compiler runs the member: a lease
    // disposed before the construct has ended its Work, and one disposed after the construct has not.
    [Theory]
    [MemberData(nameof(CensusTraversedConstructNames))]
    public void ImplicitCallFrontierFollowsTheLeaseLifetime(string name)
    {
        CensusConstruct construct = CensusCorpus.Single(candidate => candidate.Name == name);

        HostedProducerDiscovery<HostedProducerSite> after = EnumerationDiscover(CensusCompile(construct, admitted: true, effect: true, after: "lease.Dispose();"));

        HostedProducerSite kept = Assert.Single(EnumerationDeleteSites(after));

        Assert.True(kept.WorkFrontierCapsuleId is not null, construct.Name + ": a lease disposed after the construct ended the Work of the member the compiler called.");

        HostedProducerDiscovery<HostedProducerSite> before = EnumerationDiscover(CensusCompile(construct, admitted: true, effect: true, before: "lease.Dispose();"));

        HostedProducerSite lost = Assert.Single(EnumerationDeleteSites(before));

        Assert.True(lost.WorkFrontierCapsuleId is null, construct.Name + ": a lease disposed before the construct left the member the compiler called under the ended Work.");

        Assert.True(EnumerationFrontierMissing(before, "HOSTED_SITE_WORK_FRONTIER_MISSING"), construct.Name + ": the missing Work frontier after the disposal is not reported.");
    }

    private static CSharpCompilation CensusCompileInCallback(CensusConstruct construct, bool effect)
    {
        string helpers = construct.Helpers.Replace("{E}", effect ? EnumerationEffect : string.Empty, StringComparison.Ordinal);

        string body = R2Admission + "GC.KeepAlive((Func<Task>)(async () => { " + construct.Body + " await Task.CompletedTask; })); await Task.CompletedTask;";

        CSharpCompilation compilation = Compile("using System.Linq;\n" + R2Source(body, helpers));

        compilation = compilation.WithOptions(compilation.Options.WithAllowUnsafe(true));

        Diagnostic[] errors = [.. compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)];

        Assert.True(errors.Length == 0, construct.Name + ": " + string.Join(" | ", errors.Select(static error => error.ToString())));

        return compilation;
    }

    // An opaque callback is judged by everything the traversal would follow in its body, so an implicit call in the body
    // that reaches a producer site refuses the callback, and the same body without the effect does not.
    public static IEnumerable<object[]> CensusAllConstructNames() =>
        CensusCorpus.Select(static construct => new object[] { construct.Name });

    [Theory]
    [MemberData(nameof(CensusAllConstructNames))]
    public void ImplicitCallInsideAnOpaqueCallbackIsJudgedByTheCallbackScan(string name)
    {
        CensusConstruct construct = CensusCorpus.Single(candidate => candidate.Name == name);

        HostedProducerDiscovery<HostedProducerSite> refused = EnumerationDiscover(CensusCompileInCallback(construct, effect: true));

        Assert.True(refused.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN" && diagnostic.Detail.Contains("callback", StringComparison.Ordinal)), construct.Name + ": an opaque callback whose body runs the effect through the compiler is not refused. " + string.Join(" | ", refused.Diagnostics.Select(static diagnostic => diagnostic.Code + " " + diagnostic.Detail)));

        HostedProducerDiscovery<HostedProducerSite> clean = EnumerationDiscover(CensusCompileInCallback(construct, effect: false));

        // The scan may refuse more than the traversal does, never less: only a construct the traversal follows precisely
        // has a callback that is not refused when the member it calls has no effect.
        if (construct.Expected == CensusDisposition.Traversed)
        {
            Assert.DoesNotContain(clean.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
        }
    }

    private static readonly string[] CensusMemberKinds = ["static-method", "constructor", "property-getter", "local-function", "lambda"];

    public static IEnumerable<object[]> CensusMemberKindCases() =>
        CensusCorpus
            .Where(static construct => construct.Expected == CensusDisposition.Traversed)
            .SelectMany(static construct => CensusMemberKinds
                .Where(kind => !(kind is "constructor" or "property-getter" && construct.Body.Contains("await", StringComparison.Ordinal)))
                .Select(kind => new object[] { construct.Name, kind }));

    private static CSharpCompilation CensusCompileInMember(CensusConstruct construct, string kind, bool admitted, bool effect)
    {
        string helpers = construct.Helpers.Replace("{E}", effect ? EnumerationEffect : string.Empty, StringComparison.Ordinal);

        (string host, string call) = kind switch
        {
            "static-method" => ("static class Host { public static async Task Run() { " + construct.Body + " await Task.CompletedTask; } } ", "await Host.Run();"),
            "constructor" => ("sealed class Host { public Host() { " + construct.Body + " } } ", "_ = new Host();"),
            "property-getter" => ("sealed class Host { public int Value { get { " + construct.Body + " return 0; } } } ", "_ = new Host().Value;"),
            "local-function" => (string.Empty, "async Task Local() { " + construct.Body + " await Task.CompletedTask; } await Local();"),
            _ => (string.Empty, "Func<Task> run = async () => { " + construct.Body + " await Task.CompletedTask; }; await run();"),
        };

        string body = (admitted ? R2Admission : string.Empty) + call + " await Task.CompletedTask;";

        CSharpCompilation compilation = Compile("using System.Linq;\n" + R2Source(body, helpers + host));

        compilation = compilation.WithOptions(compilation.Options.WithAllowUnsafe(true));

        Diagnostic[] errors = [.. compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)];

        Assert.True(errors.Length == 0, construct.Name + " in " + kind + ": " + string.Join(" | ", errors.Select(static error => error.ToString())));

        return compilation;
    }

    // The census runs on every member body the traversal analyses: the same construct is traversed whether it sits in a
    // method, a constructor, a property getter, a local function or a lambda that the admitted region calls.
    [Theory]
    [MemberData(nameof(CensusMemberKindCases))]
    public void ImplicitCallIsTraversedInEveryKindOfMemberBody(string name, string kind)
    {
        CensusConstruct construct = CensusCorpus.Single(candidate => candidate.Name == name);

        HostedProducerDiscovery<HostedProducerSite> admitted = EnumerationDiscover(CensusCompileInMember(construct, kind, admitted: true, effect: true));

        HostedProducerSite site = Assert.Single(EnumerationDeleteSites(admitted));

        Assert.True(site.WorkFrontierCapsuleId is not null && site.EffectFrontierCapsuleId is not null, construct.Name + " in " + kind + ": the effect is reported without the frontier live at the call.");

        HostedProducerDiscovery<HostedProducerSite> unadmitted = EnumerationDiscover(CensusCompileInMember(construct, kind, admitted: false, effect: true));

        Assert.Null(Assert.Single(EnumerationDeleteSites(unadmitted)).WorkFrontierCapsuleId);

        HostedProducerDiscovery<HostedProducerSite> clean = EnumerationDiscover(CensusCompileInMember(construct, kind, admitted: true, effect: false));

        Assert.Empty(EnumerationDeleteSites(clean));

        Assert.False(CensusRefused(clean), construct.Name + " in " + kind + ": the effect-free twin is refused. " + string.Join(" | ", clean.Diagnostics.Select(static diagnostic => diagnostic.Code + " " + diagnostic.Detail)));
    }

    // The checker prunes what it traverses by a scan: the members an authored formatter runs for a value nothing proves,
    // and the initializers a construction runs, are traversed only when the scan says they can reach a producer site. The
    // scan has to say so wherever the traversal would report a site or refuse, so every construct of the corpus is run
    // from the body of such a member and is judged as it is where it is written.
    public static IEnumerable<object[]> CensusPrunedContextCases() =>
        CensusCorpus
            .Where(static construct => !construct.Body.Contains("await", StringComparison.Ordinal))
            .SelectMany(static construct => new[] { "formatter", "initializer" }.Select(context => new object[] { construct.Name, context }));

    private static CSharpCompilation CensusCompileInPrunedContext(CensusConstruct construct, string context, bool effect)
    {
        string helpers = construct.Helpers.Replace("{E}", effect ? EnumerationEffect : string.Empty, StringComparison.Ordinal);

        (string host, string call) = context switch
        {
            "formatter" => ("interface IPruned { } sealed class Pruned : IPruned { public override string ToString() { " + construct.Body + " return string.Empty; } } static class Unproven { public static IPruned Value { get; set; } = null!; } ", "IPruned value = Unproven.Value; string text = $\"{value}\"; _ = text;"),
            _ => ("sealed class Pruned { private int sink = Run(); private static int Run() { " + construct.Body + " return 0; } } ", "_ = new Pruned();"),
        };

        CSharpCompilation compilation = Compile("using System.Linq;\n" + R2Source(R2Admission + call + " await Task.CompletedTask;", helpers + host));

        compilation = compilation.WithOptions(compilation.Options.WithAllowUnsafe(true));

        Diagnostic[] errors = [.. compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)];

        Assert.True(errors.Length == 0, construct.Name + " in " + context + ": " + string.Join(" | ", errors.Select(static error => error.ToString())));

        return compilation;
    }

    [Theory]
    [MemberData(nameof(CensusPrunedContextCases))]
    public void ImplicitCallInsideAPrunedMemberIsStillTraversedOrRefused(string name, string context)
    {
        CensusConstruct construct = CensusCorpus.Single(candidate => candidate.Name == name);

        HostedProducerDiscovery<HostedProducerSite> reported = EnumerationDiscover(CensusCompileInPrunedContext(construct, context, effect: true));

        HostedProducerSite[] sites = EnumerationDeleteSites(reported);

        HostedProducerDiscovery<HostedProducerSite> clean = EnumerationDiscover(CensusCompileInPrunedContext(construct, context, effect: false));

        string diagnostics = string.Join(" | ", reported.Diagnostics.Select(static diagnostic => diagnostic.Code + " " + diagnostic.Identity + " " + diagnostic.Detail));

        switch (construct.Expected)
        {
            case CensusDisposition.Traversed:
                HostedProducerSite site = Assert.Single(sites);

                Assert.True(site.WorkFrontierCapsuleId is not null && site.EffectFrontierCapsuleId is not null, construct.Name + " in " + context + ": the member the compiler calls is reported without the frontier live where the pruned member runs.");

                Assert.False(CensusRefused(reported), construct.Name + " in " + context + ": a traversed construct is also refused. " + diagnostics);

                Assert.Empty(EnumerationDeleteSites(clean));

                Assert.False(CensusRefused(clean), construct.Name + " in " + context + ": the effect-free twin is refused.");

                break;

            case CensusDisposition.Detached:
                Assert.True(Assert.Single(sites).WorkFrontierCapsuleId is null, construct.Name + " in " + context + ": a member that runs where the value is consumed is reported under a frontier.");

                Assert.Empty(EnumerationDeleteSites(clean));

                break;

            default:
                Assert.True(CensusRefused(reported), construct.Name + " in " + context + ": the construct is neither reported nor refused once the scan prunes the member that holds it. Sites: " + sites.Length + "; diagnostics: " + diagnostics);

                Assert.Equal(construct.Expected == CensusDisposition.RefusedAlways, CensusRefused(clean));

                break;
        }
    }
}
