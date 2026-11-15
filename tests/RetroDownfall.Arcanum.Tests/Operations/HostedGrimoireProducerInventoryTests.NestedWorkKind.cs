using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    private const string NestedKindContract = "RetroDownfall.Arcanum.Infrastructure.Data.";

    private static HostedProducerDiscovery<HostedProducerSite> DiscoverNestedWorkKind(string shape, string? caller = null)
    {
        string rootAdmission = R2Admission.Replace(".WorkspaceIndexing", ".LongRunningOperationRecovery", StringComparison.Ordinal);

        if (shape == "group-outlives-inner")
            rootAdmission = rootAdmission[..rootAdmission.IndexOf(" if (!lease.TryBeginExternalEffectGroup", StringComparison.Ordinal)];

        string prefix = shape == "root-expired-before" ? "outer.Dispose(); " : string.Empty;

        string afterAcquire = shape switch
        {
            "root-expired-after-acquire" => "outer.Dispose(); ",
            "inner-expired" => "own.Dispose(); ",
            "inner-escaped" => "AdmissionEscape.Lease = own; ",
            "second-group" or "second-unused" => "if (!gate.TryAcquireWorkLease(" + NestedKindContract + "GrimoireWorkKind.GuildStorage, out var second)) return; using var secondLease = second; ",
            _ => string.Empty,
        };

        string groupReceiver = shape switch
        {
            "outer-group" => "outer",
            "second-group" => "secondLease",
            _ => "own",
        };

        string group = shape == "outer-group-only" ? string.Empty
            : "if (!" + groupReceiver + ".TryBeginExternalEffectGroup(out var group)) return; using var held = group; ";

        string beforeSend = shape == "root-expired-after-group" ? "outer.Dispose(); " : string.Empty;

        string innerKind = shape == "cast-kind"
            ? "(" + NestedKindContract + "GrimoireWorkKind)20"
            : NestedKindContract + "GrimoireWorkKind.GuildStorage";

        string own = "if (!gate.TryAcquireWorkLease(" + innerKind + ", out var admitted)"
            + (shape == "null-guard" ? " || admitted is null" : string.Empty)
            + ") return; using var own = admitted; ";

        string send = shape == "forwarded"
            ? "await SendAsync();"
            : "_ = System.IO.File.Exists(\"nested-kind-read\"); System.IO.File.Delete(\"nested-kind-send\");";

        string inner = prefix + own + afterAcquire + group + beforeSend + send;

        if (shape == "group-outlives-inner")
        {
            inner = "IDisposable group = null!; { " + own
                + "if (!own.TryBeginExternalEffectGroup(out group)) return; } using var held = group; " + send;
        }

        string rootBody = rootAdmission + "await Nested(gate, lease, token);";

        if (shape == "root-missing")
            rootBody = NestedKindContract + "IGrimoireConnectionAdmissionGate gate = null!; await Nested(gate, null!, token);";

        if (shape == "root-wrong-kind")
            rootBody = R2Admission + "await Nested(gate, lease, token);";

        if (shape == "detached")
            rootBody = rootAdmission + "_ = Nested(gate, lease, token);";

        if (caller is not null) rootBody = rootAdmission + caller;

        string source = R2Source(rootBody,
            "static class AdmissionEscape { internal static " + NestedKindContract + "IGrimoireWorkLease? Lease; }")
            .Replace("public enum GrimoireWorkKind { WorkspaceIndexing = 4 }",
                "public enum GrimoireWorkKind { WorkspaceIndexing = 4, LongRunningOperationRecovery = 14, GuildStorage = "
                    + (shape == "wrong-kind-value" ? "999" : "20") + " }", StringComparison.Ordinal)
            .Replace("public Task StopAsync", $$"""
                private static async Task Nested({{NestedKindContract}}IGrimoireConnectionAdmissionGate gate,
                    {{NestedKindContract}}IGrimoireWorkLease outer, CancellationToken token)
                {
                    {{inner}}
                    await Task.CompletedTask;
                }

                private static async Task ValidCaller({{NestedKindContract}}IGrimoireConnectionAdmissionGate gate, CancellationToken token)
                {
                    if (!gate.TryAcquireWorkLease({{NestedKindContract}}GrimoireWorkKind.LongRunningOperationRecovery, out var admitted)) return;
                    using var ownRoot = admitted;
                    if (!ownRoot.TryBeginExternalEffectGroup(out var group)) return;
                    using var heldRoot = group;
                    await Nested(gate, ownRoot, token);
                }

                private static async Task SendAsync()
                {
                    await Task.CompletedTask;
                    _ = System.IO.File.Exists("nested-kind-read");
                    System.IO.File.Delete("nested-kind-send");
                }

                public Task StopAsync
                """, StringComparison.Ordinal);

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        return HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], new(["Worker"], []),
            [new("Worker", [OrdinaryRoot() with { WorkKind = GrimoireWorkKind.LongRunningOperationRecovery }])], []);
    }

    private static HostedProducerInventoryValidation NestedWorkKindCatalog(IReadOnlyList<HostedProducerSite> sites) =>
        HostedGrimoireProducerInventory.Validate(
            [new("Worker", [OrdinaryRoot() with { WorkKind = GrimoireWorkKind.LongRunningOperationRecovery, Sites = sites }])],
            [], new(["Worker"], []), new(sites, []));

    [Theory]
    [InlineData("simple")]
    [InlineData("null-guard")]
    [InlineData("forwarded")]
    [InlineData("second-unused")]
    public void First_nested_known_kind_keeps_its_physical_pair_and_separate_registered_root(string shape)
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverNestedWorkKind(shape);

        Assert.Empty(result.Diagnostics);

        HostedProducerSite outer = Assert.Single(result.Items, static site => site.AdmittedWorkKind == GrimoireWorkKind.LongRunningOperationRecovery);

        // Ordinals are fingerprint-local; source position proves which acquisition came first.
        HostedProducerSite inner = result.Items.Where(static site => site.AdmittedWorkKind == GrimoireWorkKind.GuildStorage)
            .OrderBy(static site => int.Parse(site.OperationId[(site.OperationId.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture))
            .First();

        HostedProducerSite send = Assert.Single(EnumerationDeleteSites(result));

        Assert.Equal(inner.CapsuleId, send.WorkFrontierCapsuleId);

        Assert.Equal(outer.CapsuleId, send.RegisteredRootWorkFrontierCapsuleId);

        HostedProducerSite group = Assert.Single(result.Items, site => site.CapsuleId == send.EffectFrontierCapsuleId);

        Assert.Equal(inner.CapsuleId, group.WorkFrontierCapsuleId);

        Assert.Empty(NestedWorkKindCatalog(result.Items).Diagnostics);

        HostedProducerCapsuleManifest parsed = HostedGrimoireProducerInventory.ParseCapsuleManifest(
            HostedGrimoireProducerInventory.RenderCapsuleManifest(result.Items).Split('\n', StringSplitOptions.RemoveEmptyEntries));

        Assert.Empty(parsed.Diagnostics);

        Assert.Contains(parsed.Sites, site => site.CapsuleId == send.CapsuleId
            && site.WorkFrontierCapsuleId == inner.CapsuleId
            && site.RegisteredRootWorkFrontierCapsuleId == outer.CapsuleId);
    }

    [Theory]
    [InlineData("root-missing")]
    [InlineData("root-wrong-kind")]
    [InlineData("root-expired-before")]
    [InlineData("inner-expired")]
    [InlineData("inner-escaped")]
    [InlineData("outer-group")]
    [InlineData("second-group")]
    [InlineData("wrong-kind-value")]
    [InlineData("cast-kind")]
    [InlineData("outer-group-only")]
    [InlineData("group-outlives-inner")]
    [InlineData("detached")]
    public void Distinct_kind_eligibility_does_not_repair_missing_admission_or_a_different_physical_pair(string shape)
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverNestedWorkKind(shape);

        Assert.NotEmpty(EnumerationDeleteSites(result));

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code is
            "HOSTED_SITE_WORK_FRONTIER_MISSING" or "HOSTED_SITE_EFFECT_FRONTIER_MISSING"
            or "HOSTED_ADMISSION_RECEIVER_MISMATCH" or "HOSTED_ADMISSION_EFFECT_LEASE_MISMATCH"
            or "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");

        Assert.NotEmpty(NestedWorkKindCatalog(result.Items).Diagnostics);
    }

    [Theory]
    [InlineData("root-expired-after-acquire")]
    [InlineData("root-expired-after-group")]
    public void Expired_registered_root_does_not_erase_a_still_live_inner_physical_pair(string shape)
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverNestedWorkKind(shape);

        HostedProducerSite inner = Assert.Single(result.Items, static site => site.AdmittedWorkKind == GrimoireWorkKind.GuildStorage);

        HostedProducerSite send = Assert.Single(EnumerationDeleteSites(result));

        Assert.Equal(inner.CapsuleId, send.WorkFrontierCapsuleId);

        Assert.NotNull(send.EffectFrontierCapsuleId);

        Assert.Null(send.RegisteredRootWorkFrontierCapsuleId);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is
            "HOSTED_ADMISSION_RECEIVER_MISMATCH" or "HOSTED_ADMISSION_EFFECT_LEASE_MISMATCH");

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING");

        Assert.Contains(NestedWorkKindCatalog(result.Items).Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING");

        HostedProducerCapsuleManifest parsed = HostedGrimoireProducerInventory.ParseCapsuleManifest(
            HostedGrimoireProducerInventory.RenderCapsuleManifest(result.Items).Split('\n', StringSplitOptions.RemoveEmptyEntries));

        HostedProducerSite parsedSend = Assert.Single(parsed.Sites, static site => site.Callee == "System.IO.File.Delete");

        Assert.Equal(inner.CapsuleId, parsedSend.WorkFrontierCapsuleId);

        Assert.Null(parsedSend.RegisteredRootWorkFrontierCapsuleId);

        Assert.Contains(NestedWorkKindCatalog(parsed.Sites).Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Same_physical_nested_frontiers_round_trip_under_two_live_root_origins(bool otherFirst)
    {
        string first = "await Nested(gate, lease, token); ";

        string other = "await ValidCaller(gate, token); ";

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverNestedWorkKind("simple", otherFirst ? other + first : first + other);

        Assert.Empty(result.Diagnostics);

        HostedProducerSite[] sends = EnumerationDeleteSites(result);

        Assert.Equal(2, sends.Length);

        Assert.Single(sends.Select(static site => site.WorkFrontierCapsuleId).Distinct());

        Assert.Equal(2, sends.Select(static site => site.RegisteredRootWorkFrontierCapsuleId).Distinct().Count());

        HostedProducerCapsuleManifest parsed = HostedGrimoireProducerInventory.ParseCapsuleManifest(
            HostedGrimoireProducerInventory.RenderCapsuleManifest(result.Items).Split('\n', StringSplitOptions.RemoveEmptyEntries));

        Assert.Empty(parsed.Diagnostics);

        Assert.Equal(2, parsed.Sites.Count(static site => site.Callee == "System.IO.File.Delete"));

        Assert.Empty(NestedWorkKindCatalog(parsed.Sites).Diagnostics);
    }

    [Fact]
    public void A_changed_root_cannot_select_a_physical_frontier_context_with_another_witness()
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverNestedWorkKind("simple",
            "await Nested(gate, lease, token); await ValidCaller(gate, token);");

        Assert.Empty(result.Diagnostics);

        HostedProducerSite[] sends = EnumerationDeleteSites(result);

        Assert.Equal(2, sends.Length);

        HostedProducerSite first = sends[0];

        string other = sends[1].RegisteredRootWorkFrontierCapsuleId!;

        HostedProducerSite[] changed = result.Items
            .Where(site => site.RegisteredRootWorkFrontierCapsuleId != other || site.CapsuleId == other)
            .Select(site => site == first ? site with { RegisteredRootWorkFrontierCapsuleId = other } : site).ToArray();

        Assert.Contains(changed, site => site.CapsuleId == other);

        HostedProducerSite forged = Assert.Single(changed, site => site.Callee == "System.IO.File.Delete");

        Assert.Contains(NestedWorkKindCatalog(changed).Diagnostics, diagnostic => diagnostic.Code == "HOSTED_MANIFEST_FRONTIER_UNRESOLVED"
            && diagnostic.Identity == HostedGrimoireProducerInventory.StableSiteIdentity(forged));

        HostedProducerCapsuleManifest parsed = HostedGrimoireProducerInventory.ParseCapsuleManifest(
            HostedGrimoireProducerInventory.RenderCapsuleManifest(changed).Split('\n', StringSplitOptions.RemoveEmptyEntries));

        Assert.Contains(parsed.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_MANIFEST_FRONTIER_UNRESOLVED"
            && diagnostic.Identity == HostedGrimoireProducerInventory.StableSiteIdentity(forged));
    }

    [Fact]
    public void Effect_context_must_match_the_consumers_current_physical_work()
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverNestedWorkKind("simple");

        Assert.Empty(result.Diagnostics);

        HostedProducerSite send = Assert.Single(EnumerationDeleteSites(result));

        HostedProducerSite root = Assert.Single(result.Items, static site => site.AdmittedWorkKind == GrimoireWorkKind.LongRunningOperationRecovery);

        HostedProducerSite forged = send with { WorkFrontierCapsuleId = root.CapsuleId };

        HostedProducerSite[] changed = result.Items.Select(site => site == send ? forged : site).ToArray();

        Assert.Contains(NestedWorkKindCatalog(changed).Diagnostics, diagnostic => diagnostic.Code == "HOSTED_MANIFEST_FRONTIER_UNRESOLVED"
            && diagnostic.Identity == HostedGrimoireProducerInventory.StableSiteIdentity(forged));

        HostedProducerCapsuleManifest parsed = HostedGrimoireProducerInventory.ParseCapsuleManifest(
            HostedGrimoireProducerInventory.RenderCapsuleManifest(changed)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries));

        Assert.Contains(parsed.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_MANIFEST_FRONTIER_UNRESOLVED"
            && diagnostic.Identity == HostedGrimoireProducerInventory.StableSiteIdentity(forged)
            && diagnostic.Detail.Contains("effect", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Nested_kind_cache_keeps_valid_and_departed_root_callers_distinct(bool expiredFirst)
    {
        string calls = expiredFirst
            ? "lease.Dispose(); await Nested(gate, lease, token);"
            : "await Nested(gate, lease, token); lease.Dispose(); await Nested(gate, lease, token);";

        // A fresh local caller supplies a live witness after the departed-root call,
        // so both orders reach the same physical Nested declaration.
        if (expiredFirst)
            calls += " await ValidCaller(gate, token);";

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverNestedWorkKind("simple", calls);

        Assert.Contains(EnumerationDeleteSites(result), static site => site.WorkFrontierCapsuleId is not null
            && site.EffectFrontierCapsuleId is not null && site.RegisteredRootWorkFrontierCapsuleId is not null);

        Assert.Contains(EnumerationDeleteSites(result), static site => site.RegisteredRootWorkFrontierCapsuleId is null);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING");
    }
}
