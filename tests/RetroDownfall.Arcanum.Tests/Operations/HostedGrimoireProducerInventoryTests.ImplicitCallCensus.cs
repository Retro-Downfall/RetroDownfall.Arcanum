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

    private static CSharpCompilation CensusCompile(CensusConstruct construct, bool admitted, bool effect)
    {
        string helpers = construct.Helpers.Replace("{E}", effect ? EnumerationEffect : string.Empty, StringComparison.Ordinal);

        string body = (admitted ? R2Admission : string.Empty) + construct.Body + " await Task.CompletedTask;";

        CSharpCompilation compilation = Compile("using System.Linq;\n" + R2Source(body, helpers));

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
}
