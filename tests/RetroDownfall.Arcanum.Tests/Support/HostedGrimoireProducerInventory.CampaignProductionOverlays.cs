using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RetroDownfall.Arcanum.Infrastructure.Data;
using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace RetroDownfall.Arcanum.Tests.Support;

internal enum HostedProducerProductionOverlayKind
{
    ModelCore,
    ModelFactory,
    EncodingCore,
}

internal sealed record HostedProducerProductionOverlay(
    CSharpCompilation Fixture,
    ImmutableArray<CSharpCompilation> Production,
    HostedProducerDiscovery<HostedProducerSite> Discovery);

internal static partial class HostedGrimoireProducerInventory
{
    private static readonly ConcurrentDictionary<(string Source, HostedProducerProductionOverlayKind Kind),
        Lazy<HostedProducerProductionOverlay>> ProductionOverlays = new();

    internal static HostedProducerProductionOverlay GetProductionOverlay(
        string exactSource,
        HostedProducerProductionOverlayKind kind)
    {
        ArgumentNullException.ThrowIfNull(exactSource);

        return ProductionOverlays.GetOrAdd((exactSource, kind), static key =>
            new Lazy<HostedProducerProductionOverlay>(
                () => BuildProductionOverlay(key.Source, key.Kind),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private static HostedProducerProductionOverlay BuildProductionOverlay(
        string exactSource,
        HostedProducerProductionOverlayKind kind)
    {
        string referenceAssembly = kind switch
        {
            HostedProducerProductionOverlayKind.ModelCore or HostedProducerProductionOverlayKind.EncodingCore =>
                "RetroDownfall.Arcanum.Api",
            HostedProducerProductionOverlayKind.ModelFactory => "RetroDownfall.Arcanum.Cli",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown targeted production graph."),
        };

        CSharpCompilation references = ProductionCompilations.Single(compilation =>
            compilation.AssemblyName == referenceAssembly);

        CSharpCompilation fixture = CSharpCompilation.Create(
            "InventoryReferencePackFixture",
            [CSharpSyntaxTree.ParseText(exactSource, path: "src/Fixture.cs")],
            references.References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        ImmutableArray<CSharpCompilation> production = kind switch
        {
            HostedProducerProductionOverlayKind.ModelCore or HostedProducerProductionOverlayKind.EncodingCore =>
                [ProductionCompilations.Single(static compilation => compilation.AssemblyName == "RetroDownfall.Arcanum.Core")],
            HostedProducerProductionOverlayKind.ModelFactory =>
                [
                    ProductionCompilations.Single(static compilation => compilation.AssemblyName == "RetroDownfall.Arcanum.Core"),
                    ProductionCompilations.Single(static compilation => compilation.AssemblyName == "RetroDownfall.Arcanum.Infrastructure"),
                    ProductionCompilations.Single(static compilation => compilation.AssemblyName == "RetroDownfall.Arcanum.Api"),
                ],
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown targeted production graph."),
        };

        CSharpCompilation[] graph = [fixture, .. production];

        HostedProducerDiscovery<HostedProducerSite> discovery;

        if (kind == HostedProducerProductionOverlayKind.EncodingCore)
        {
            discovery = DiscoverProducerSites(graph, DiscoverApplicationHostedServices(graph), [], []);
        }
        else
        {
            HostedProducerOperationEntry root = new(
                "Worker.StartAsync",
                "src/Fixture.cs",
                "Worker",
                "StartAsync",
                HostedProducerAuthorityKind.OrdinaryHostedWork,
                GrimoireWorkKind.WorkspaceIndexing,
                null,
                []);

            discovery = DiscoverProducerSites(graph, new(["Worker"], []), [new("Worker", [root])], []);
        }

        return new(fixture, production, discovery);
    }
}
