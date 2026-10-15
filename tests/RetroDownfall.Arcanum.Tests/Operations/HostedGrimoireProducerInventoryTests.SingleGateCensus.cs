using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    // The Single Gate Census. A Work origin is an acquisition on the reviewed admission gate. The analysis follows every acquisition
    // as a call through the gate contract's slot, which reaches whichever implementation its receiver holds, so it grants a Work
    // origin only while the analysed source declares no implementation of the contract other than the reviewed one. A fake gate
    // that returns a lease the real gate never admitted would otherwise be the proof of admission for every later send.
    private const string GateCensusAcquisition = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease";

    private const string GateCensusSend = "System.IO.File.Delete";

    // The paid body every row shares, after the lease is acquired and named `lease`.
    private const string GateCensusPaidBody = """
        if (!lease.TryBeginExternalEffectGroup(out IDisposable group)) return;

        using var held = group;

        await Task.CompletedTask;

        System.IO.File.Delete("gate-census-send");
        """;

    private static string GateCensusBody(string receiver, string declaration = "") => $$"""
        {{declaration}}

        if (!{{receiver}}.TryAcquireWorkLease(GrimoireWorkKind.WorkspaceIndexing, out IGrimoireWorkLease? admitted)) return;

        using IGrimoireWorkLease lease = admitted!;

        {{GateCensusPaidBody}}
        """;

    private static string GateCensusSource(string body, string types = "") => $$"""
        #nullable enable
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Hosting;
        using RetroDownfall.Arcanum.Infrastructure.Data;

        public class Worker(IGrimoireConnectionAdmissionGate gate) : IHostedService
        {
            public async Task StartAsync(CancellationToken token)
            {
                {{body}}
            }

            public Task StopAsync(CancellationToken token) => Task.CompletedTask;
        }

        public sealed class FakeLease : IGrimoireWorkLease
        {
            public bool TryBeginExternalEffectGroup(out IDisposable group)
            {
                group = new FakeGroup();

                return true;
            }

            public void Dispose() { }

            public ValueTask DisposeAsync() => default;
        }

        public sealed class FakeGroup : IDisposable
        {
            public void Dispose() { }
        }

        public static class Composition
        {
            public static void Configure(IServiceCollection services) { services.AddHostedService<Worker>(); }
        }

        {{types}}
        """ + AdmissionTypes;

    private const string FakeGateType = """
        public sealed class FakeGate : IGrimoireConnectionAdmissionGate
        {
            public bool TryAcquireWorkLease(GrimoireWorkKind kind, out IGrimoireWorkLease lease)
            {
                lease = new FakeLease();

                return true;
            }
        }
        """;

    private const string ReviewedGateMembers = """
            public bool TryAcquireWorkLease(GrimoireWorkKind kind, out IGrimoireWorkLease lease)
            {
                lease = null!;

                return false;
            }
        """;

    private static string GateInNamespace(string @namespace, string declaration) => $$"""
        namespace {{@namespace}}
        {
            using System;

            {{declaration}}
        }
        """;

    private static readonly string ReviewedGateDeclaration = GateInNamespace(
        "RetroDownfall.Arcanum.Infrastructure.Data",
        "internal sealed class GrimoireConnectionAdmissionGate : IGrimoireConnectionAdmissionGate\n{\n" + ReviewedGateMembers + "\n}");

    private static (string Body, string Types) GateCensusShape(string shape) => shape switch
    {
        // Positives: no gate beside the reviewed one, whichever way the source spells the reviewed one.
        "interface-only" => (GateCensusBody("gate"), ""),
        "reviewed-only" => (GateCensusBody("gate"), ReviewedGateDeclaration),
        "abstract-implementer-only" => (GateCensusBody("gate"), GateInNamespace("Fixture.Gates",
            "public abstract class AbstractGate : RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate\n{\n    public abstract bool TryAcquireWorkLease(RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind kind, out RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease lease);\n}")),
        "same-slot-name-not-the-contract" => (GateCensusBody("gate"), GateInNamespace("Fixture.Gates",
            "public sealed class Unrelated\n{\n    public bool TryAcquireWorkLease(RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind kind, out RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease lease) { lease = null!; return false; }\n}")),
        // Negatives: a second gate. Each keeps the injected gate's acquisition or replaces it with one on the second gate, and
        // every one grants no Work.
        "forged-interface-local" => (GateCensusBody("other", "IGrimoireConnectionAdmissionGate other = new FakeGate();"), FakeGateType),
        "forged-concrete-local" => (GateCensusBody("other", "FakeGate other = new FakeGate();"), FakeGateType),
        "forged-var-local" => (GateCensusBody("other", "var other = new FakeGate();"), FakeGateType),
        "second-gate-present-injected-used" => (GateCensusBody("gate"), FakeGateType),
        "reviewed-and-second-gate" => (GateCensusBody("gate"), ReviewedGateDeclaration + FakeGateType),
        "reviewed-name-in-another-namespace" => (GateCensusBody("gate"), GateInNamespace("Fixture.Gates",
            "public sealed class GrimoireConnectionAdmissionGate : RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate\n{\n" + ReviewedGateMembers.Replace("GrimoireWorkKind", "RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind", StringComparison.Ordinal).Replace("IGrimoireWorkLease", "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease", StringComparison.Ordinal) + "\n}")),
        "derived-from-reviewed" => (GateCensusBody("gate"), GateInNamespace("RetroDownfall.Arcanum.Infrastructure.Data",
            "internal class GrimoireConnectionAdmissionGate : IGrimoireConnectionAdmissionGate\n{\n" + ReviewedGateMembers + "\n}\n\ninternal sealed class DerivedGate : GrimoireConnectionAdmissionGate\n{\n}")),
        "struct-gate" => (GateCensusBody("gate"), GateInNamespace("Fixture.Gates",
            "public struct StructGate : RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate\n{\n    public bool TryAcquireWorkLease(RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind kind, out RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease lease) { lease = null!; return false; }\n}")),
        "extended-contract" => (GateCensusBody("gate"), GateInNamespace("Fixture.Gates",
            "public interface IExtendedGate : RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate\n{\n}\n\npublic sealed class ExtendedGate : IExtendedGate\n{\n    public bool TryAcquireWorkLease(RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind kind, out RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease lease) { lease = null!; return false; }\n}")),
        "nested-second-gate" => (GateCensusBody("gate"), GateInNamespace("Fixture.Gates",
            "public static class Holder\n{\n    private sealed class Hidden : RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate\n    {\n        public bool TryAcquireWorkLease(RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind kind, out RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease lease) { lease = null!; return false; }\n    }\n}")),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
    };

    private HostedProducerDiscovery<HostedProducerSite> DiscoverGateCensus(string shape, params string[] extraSources)
    {
        (string body, string types) = GateCensusShape(shape);

        CSharpCompilation[] compilations = [Compile(GateCensusSource(body, types)), .. extraSources.Select(Compile)];

        foreach (CSharpCompilation compilation in compilations)
        {
            Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        }

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            compilations, new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        foreach (HostedProducerInventoryDiagnostic diagnostic in result.Diagnostics)
        {
            output.WriteLine($"GATE_CENSUS_DIAGNOSTIC {shape} {diagnostic.Code} {diagnostic.Identity}");
        }

        foreach (HostedProducerSite site in result.Items)
        {
            output.WriteLine($"GATE_CENSUS_SITE {shape} {site.Kind} {site.Callee} capsule={site.CapsuleId[..6]} work={site.WorkFrontierCapsuleId?[..6]} effect={site.EffectFrontierCapsuleId?[..6]}");
        }

        return result;
    }

    [Theory]
    [InlineData("interface-only")]
    [InlineData("reviewed-only")]
    [InlineData("abstract-implementer-only")]
    [InlineData("same-slot-name-not-the-contract")]
    public void ASourceWithNoGateBesideTheReviewedOneGrantsTheWorkAndTheEffectOfItsAcquisition(string shape)
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGateCensus(shape);

        Assert.Empty(result.Diagnostics);

        HostedProducerSite acquisition = Assert.Single(result.Items, static site => site.Callee == GateCensusAcquisition);

        HostedProducerSite send = Assert.Single(result.Items, static site => site.Callee == GateCensusSend);

        Assert.Equal(acquisition.CapsuleId, send.WorkFrontierCapsuleId);

        Assert.NotNull(send.EffectFrontierCapsuleId);
    }

    [Theory]
    [InlineData("forged-interface-local")]
    [InlineData("second-gate-present-injected-used")]
    [InlineData("reviewed-and-second-gate")]
    [InlineData("reviewed-name-in-another-namespace")]
    [InlineData("derived-from-reviewed")]
    [InlineData("struct-gate")]
    [InlineData("extended-contract")]
    [InlineData("nested-second-gate")]
    public void ASourceWithASecondGateRefusesEveryAcquisitionAndGrantsNoWork(string shape)
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGateCensus(shape);

        // The refusal names the acquisition's coordinate and the gate that makes it untrustworthy.
        HostedProducerInventoryDiagnostic refusal = Assert.Single(result.Diagnostics, static diagnostic => diagnostic.Code == HostedGrimoireProducerInventory.GateImplementationUnreviewedCode);

        Assert.StartsWith("src/Fixture.cs:", refusal.Identity, StringComparison.Ordinal);

        Assert.Contains("implements the admission gate and is not the reviewed gate", refusal.Detail, StringComparison.Ordinal);

        Assert.DoesNotContain(result.Items, static site => site.Callee == GateCensusAcquisition);

        // The send is judged without a Work, and the forged lease's group is no Effect.
        HostedProducerSite send = Assert.Single(result.Items, static site => site.Callee == GateCensusSend);

        Assert.Null(send.WorkFrontierCapsuleId);

        Assert.Null(send.EffectFrontierCapsuleId);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING" && diagnostic.Detail == GateCensusSend);
    }

    // A receiver whose static type is the second gate is not a call of the contract's slot at all: the implementation is not the
    // reviewed slot, so it is an authored method like any other and returns no Work.
    [Theory]
    [InlineData("forged-concrete-local")]
    [InlineData("forged-var-local")]
    public void AnAcquisitionOnTheSecondGatesOwnTypeIsNoAcquisitionAndGrantsNoWork(string shape)
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGateCensus(shape);

        Assert.DoesNotContain(result.Items, static site => site.Callee == GateCensusAcquisition);

        HostedProducerSite send = Assert.Single(result.Items, static site => site.Callee == GateCensusSend);

        Assert.Null(send.WorkFrontierCapsuleId);

        Assert.Null(send.EffectFrontierCapsuleId);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING" && diagnostic.Detail == GateCensusSend);
    }

    // The reviewed gate declared in two analysed assemblies is two gates: the type key is not an identity of one declaration.
    [Fact]
    public void TheReviewedGateDeclaredInASecondAssemblyIsASecondGate()
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGateCensus(
            "reviewed-only",
            GateInNamespace("RetroDownfall.Arcanum.Infrastructure.Data", "internal sealed class GrimoireConnectionAdmissionGate : IGrimoireConnectionAdmissionGate\n{\n" + ReviewedGateMembers + "\n}") + AdmissionTypes);

        HostedProducerInventoryDiagnostic refusal = Assert.Single(result.Diagnostics, static diagnostic => diagnostic.Code == HostedGrimoireProducerInventory.GateImplementationUnreviewedCode);

        Assert.Contains("is declared 2 times", refusal.Detail, StringComparison.Ordinal);

        Assert.DoesNotContain(result.Items, static site => site.Callee == GateCensusAcquisition);
    }

    // The census is a property of the analysed compilations alone: it reads them once and says the same thing to every caller.
    [Fact]
    public void TheCensusListsEachSecondGateOnceInAStableOrder()
    {
        (string _, string types) = GateCensusShape("reviewed-and-second-gate");

        CSharpCompilation compilation = Compile(GateCensusSource(GateCensusBody("gate"), types));

        string[] findings = HostedGrimoireProducerInventory.FindUnreviewedGateImplementations([compilation]);

        Assert.Equal(["'FakeGate' of 'InventoryFixture' implements the admission gate and is not the reviewed gate."], findings);

        Assert.Equal(findings, HostedGrimoireProducerInventory.FindUnreviewedGateImplementations([compilation]));

        Assert.Empty(HostedGrimoireProducerInventory.FindUnreviewedGateImplementations([Compile(GateCensusSource(GateCensusBody("gate"), ReviewedGateDeclaration))]));
    }
}
