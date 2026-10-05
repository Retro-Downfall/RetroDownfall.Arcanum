using Microsoft.AspNetCore.Builder;

using Microsoft.AspNetCore.Http;

using Microsoft.AspNetCore.Routing;

using Microsoft.AspNetCore.TestHost;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Api.Intelligence;

using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Tower;
using RetroDownfall.Arcanum.Tests.NativeSqlCipher;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// Issue #117 — the six routes that may delete a Covenant-labelled artifact all declare it.
/// </summary>
/// <remarks>
/// Route names rather than URLs, because a URL is a thing an operator types and a name is the thing the
/// contract is about. The list is exhaustive and pinned by hand: a seventh deletion route added without
/// the declaration would be a raw delete reachable over HTTP, and the only way to notice one is to write
/// the six down and fail when the set changes (§10.20.2).
///
/// <para>The declaration is what makes the pre-binding middleware issue a retention-purge authority for
/// the request. Without it a route can still call the purger, but on an installation with the Covenant
/// on the purger would find a label and no authority behind it, and refuse — so a missing declaration
/// fails closed rather than deleting protected state.</para>
/// </remarks>
public sealed class CovenantSensitivePurgeRouteInventoryTests
{
    /// <summary>Every route name that may reach a labelled artifact. Exhaustive on purpose.</summary>
    private static readonly string[] DeletionRoutes =
    [
        "DeleteSessionEntry",
        "CompactSession",
        "DeleteSagaMemory",
        "DeleteAllSagaMemories",
        "DeleteLexiconEntry",
        "EmbeddingsReset",
    ];

    [Theory]

    [InlineData("DeleteSessionEntry")]

    [InlineData("CompactSession")]

    [InlineData("DeleteSagaMemory")]

    [InlineData("DeleteAllSagaMemories")]

    [InlineData("DeleteLexiconEntry")]

    [InlineData("EmbeddingsReset")]

    public async Task Every_direct_deletion_route_declares_the_conditional_sensitivity_purge(string routeName)
    {
        await using RouteGraph graph = await RouteGraph.CreateAsync();

        Endpoint endpoint = graph.Endpoint(routeName);

        Assert.NotNull(
            endpoint.Metadata.GetMetadata<CovenantConditionalSensitivityPurgeMetadata>());
    }

    /// <summary>
    /// The inventory is the complete set, not a sample of it.
    /// </summary>
    /// <remarks>
    /// Fails in both directions. A new route that declares the conditional purge without being listed
    /// here is a deletion path nobody wrote down, and a listed route that stops declaring it is one that
    /// silently went back to a raw delete.
    /// </remarks>
    [Fact]
    public async Task The_declared_set_is_exactly_the_six_named_deletion_routes()
    {
        await using RouteGraph graph = await RouteGraph.CreateAsync();

        string[] declared =
        [
            .. graph.Endpoints
                .Where(static endpoint =>
                    endpoint.Metadata.GetMetadata<CovenantConditionalSensitivityPurgeMetadata>() is not null)
                .Select(static endpoint =>
                    endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? string.Empty)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal([.. DeletionRoutes.Order(StringComparer.Ordinal)], declared);
    }

    [Fact]
    public async Task Memory_route_inventory_classifies_every_projection_and_keeps_hard_delete_separate()
    {
        await using RouteGraph graph = await RouteGraph.CreateAsync();

        string[] conditionalContent = ["ExplainMemory", "ExplainSessionMemory", "GetLexiconEntry", "GetMemorySources",
            "GetSessionMemorySources", "ListLexiconEntries", "SearchMemory", "ShowLexiconEntry", "CorrectLexiconEntry",
            "RetireLexiconEntry", "ReinstateLexiconEntry", "PinLexiconEntry", "UnpinLexiconEntry",
            "ListLexiconMemoryReviewQueue", "PrepareLexiconMemoryReview", "ApplyLexiconMemoryReview"];

        string[] ordinaryContent = ["ListSagaMemoryReviewQueue", "PrepareSagaMemoryReview", "ApplySagaMemoryReview"];

        string[] protectedContent = ["ListCovenantMemoryReviewQueue", "PrepareCovenantMemoryReview", "ApplyCovenantMemoryReview"];

        Endpoint[] routes = graph.Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith("/api/memory", StringComparison.Ordinal)).ToArray();

        Assert.Equal(conditionalContent.Concat(ordinaryContent).Concat(protectedContent)
            .Concat(["GetMemoryStatus", "GetSessionMemoryStatus", "DeleteLexiconEntry"]).Order(StringComparer.Ordinal),
            routes.Select(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName).Order(StringComparer.Ordinal));

        foreach (Endpoint route in routes)
        {
            string name = route.Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName;

            Assert.Equal(conditionalContent.Contains(name, StringComparer.Ordinal),
                route.Metadata.GetMetadata<CovenantConditionalReadRequirementMetadata>() is not null);

            Assert.Equal(protectedContent.Contains(name, StringComparer.Ordinal),
                route.Metadata.GetMetadata<CovenantAuthorityRequirementMetadata>() is not null);

            Assert.Equal(name == "DeleteLexiconEntry",
                route.Metadata.GetMetadata<CovenantConditionalSensitivityPurgeMetadata>() is not null);
        }
    }

    [Fact]
    public void Operator_Lexicon_read_sources_are_closed_to_verified_inspection_and_the_status_only_count()
    {
        string root = Path.Combine(NativeSqlCipherTestPaths.RepositoryRoot(), "src", "RetroDownfall.Arcanum.Api");

        List<string> consumers = [];

        List<string> sql = [];

        List<string> calls = [];

        foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")))
        {
            CompilationUnitSyntax unit = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetCompilationUnitRoot();

            string Location(Microsoft.CodeAnalysis.SyntaxNode node) => Path.GetFileName(path) + ":"
                + (node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText ?? "outside-method");

            foreach (IdentifierNameSyntax type in unit.DescendantNodes().OfType<IdentifierNameSyntax>()
                .Where(type => type.Identifier.ValueText is "ILexiconService" or "ILexiconCurationService" or "ILexiconErasureService"))
            {
                consumers.Add(Location(type) + ":" + type.Identifier.ValueText);
            }

            foreach (LiteralExpressionSyntax literal in unit.DescendantNodes().OfType<LiteralExpressionSyntax>()
                .Where(literal => literal.Token.ValueText.Contains("lexicon_", StringComparison.OrdinalIgnoreCase)))
            {
                sql.Add(Location(literal) + ":" + literal.Token.ValueText);
            }

            foreach (InvocationExpressionSyntax invocation in unit.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is MemberAccessExpressionSyntax member && member.Expression.ToString() == "lexicon")
                {
                    calls.Add(Location(invocation) + ":" + member.Name.Identifier.ValueText);
                }
            }
        }

        string[] expectedConsumers =
        [
            "LexiconCurationEndpoints.cs:HandleShowAsync:ILexiconCurationService",
            "LexiconCurationEndpoints.cs:MapLexiconCurationEndpoints:ILexiconCurationService",
            "LexiconCurationEndpoints.cs:MapLexiconCurationEndpoints:ILexiconCurationService",
            "LexiconCurationEndpoints.cs:MapLexiconCurationEndpoints:ILexiconCurationService",
            "LexiconCurationEndpoints.cs:MapLexiconCurationEndpoints:ILexiconCurationService",
            "LexiconCurationEndpoints.cs:MapLexiconCurationEndpoints:ILexiconCurationService",
            "MemoryErasureEndpoints.cs:HandleLexiconErasePrepareAsync:ILexiconErasureService",
            "MemoryErasureEndpoints.cs:HandleLexiconEraseAsync:ILexiconErasureService",
            "MemoryEndpoints.cs:HandleSourcesAsync:ILexiconCurationService",
            "MemoryEndpoints.cs:HandleExplainAsync:ILexiconCurationService",
            "MemoryEndpoints.cs:HandleSearchAsync:ILexiconCurationService",
            "MemoryEndpoints.cs:HandleLexiconListAsync:ILexiconCurationService",
            "MemoryEndpoints.cs:HandleLexiconShowAsync:ILexiconCurationService",
            "MemoryEndpoints.cs:HandleLexiconDeleteAsync:ILexiconService",
            "MemoryEndpoints.cs:RespondToLexiconInspectionAsync:ILexiconCurationService",
            "MemoryEndpoints.cs:RespondToLexiconInspectionAsync:ILexiconCurationService",
            "MemoryEndpoints.cs:RespondToLexiconCountsAsync:ILexiconCurationService",
            "WizardIntelligenceProvider.cs:outside-method:ILexiconService",
        ];

        Assert.Equal(expectedConsumers.Order(StringComparer.Ordinal), consumers.Order(StringComparer.Ordinal));

        Assert.Equal(["MemoryEndpoints.cs:BuildStatusAsync:SELECT COUNT(*) FROM lexicon_entries"], sql);

        Assert.Equal(new[]
        {
            "LexiconCurationEndpoints.cs:HandleShowAsync:ShowExactAsync",
            "LexiconCurationEndpoints.cs:MapLexiconCurationEndpoints:CorrectAsync",
            "LexiconCurationEndpoints.cs:MapLexiconCurationEndpoints:PinAsync",
            "LexiconCurationEndpoints.cs:MapLexiconCurationEndpoints:ReinstateAsync",
            "LexiconCurationEndpoints.cs:MapLexiconCurationEndpoints:RetireAsync",
            "LexiconCurationEndpoints.cs:MapLexiconCurationEndpoints:UnpinAsync",
            "MemoryEndpoints.cs:HandleLexiconDeleteAsync:DeleteByNameAsync",
            "MemoryEndpoints.cs:HandleLexiconDeleteAsync:FindAllLifecycleIdentityForDeletionAsync",
            "MemoryEndpoints.cs:HandleLexiconShowAsync:ShowEffectiveAsync",
            "MemoryEndpoints.cs:RespondToLexiconCountsAsync:CountInspectionAsync",
            "MemoryEndpoints.cs:RespondToLexiconInspectionAsync:ListInspectionAsync",
            "MemoryEndpoints.cs:RespondToLexiconInspectionAsync:SearchInspectionAsync",
            "MemoryErasureEndpoints.cs:HandleLexiconEraseAsync:ApplyAsync",
            "MemoryErasureEndpoints.cs:HandleLexiconErasePrepareAsync:PrepareAsync",
        }, calls.Order(StringComparer.Ordinal));
    }

    private sealed class RouteGraph : IAsyncDisposable
    {
        private WebApplication _app = null!;

        internal IReadOnlyList<Endpoint> Endpoints =>
            _app.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        internal static async Task<RouteGraph> CreateAsync()
        {
            WebApplicationBuilder builder = RouteGraphHost.CreateBuilder();

            builder.Services.AddScoped<RetroDownfall.Arcanum.Core.Lexicon.ILexiconCurationService>(_ => throw new NotSupportedException());

            RouteGraph graph = new();

            graph._app = builder.Build();

            RouteGroupBuilder api = graph._app.MapGroup("/api");

            _ = api.MapSessionEndpoints();

            _ = api.MapSagaEndpoints();

            _ = api.MapMemoryEndpoints();

            _ = api.MapLexiconCurationEndpoints();

            _ = api.MapEmbeddingsResetEndpoints();

            await graph._app.StartAsync();

            return graph;
        }

        internal Endpoint Endpoint(string name) =>
            Assert.Single(
                Endpoints,
                endpoint => string.Equals(
                    endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName,
                    name,
                    StringComparison.Ordinal));

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();

            await _app.DisposeAsync();
        }
    }
}
