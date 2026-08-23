using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Conclave;
using RetroDownfall.Arcanum.Api.Primitives;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Tower;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// The Api endpoint families are filed by the domain they serve: Apprentice orchestration and its
/// Chronicle stream under <c>Api.Conclave</c>, the authored resources under <c>Api.Tower</c>, the Ward
/// and Sanctum surfaces under <c>Api.Security</c>, and the shared error mapper under
/// <c>Api.Primitives</c>. The last two are the point of the split: neither was ever a domain endpoint,
/// and filing a security surface under an authoring name is how a reader concludes it is one.
/// </summary>
/// <remarks>
/// The route inventory is the part that earns its keep. Endpoints are mapped by explicit route string,
/// so moving a class between namespaces cannot alter a route — but nothing in the build says so, and
/// "cannot" is the claim the epic rests on. Written down, the whole surface of every moved family is a
/// single assertion that fails on a changed path, a changed method, a renamed endpoint, a route lost
/// during the move, or one added under cover of it.
/// </remarks>
public sealed class ApiDomainSplitContractTests
{

    private const string RetiredNamespace = "RetroDownfall.Arcanum.Api.TheForge";

    [Fact]
    public void Api_declares_no_type_in_the_retired_namespace()
    {

        string[] strays = typeof(ArcanumErrorMapper).Assembly
            .GetTypes()
            .Where(static type => type.Namespace is string ns
                && (string.Equals(ns, RetiredNamespace, StringComparison.Ordinal)
                    || ns.StartsWith(RetiredNamespace + ".", StringComparison.Ordinal)))
            .Select(static type => type.FullName ?? type.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(strays);

    }

    [Fact]
    public void No_api_type_is_named_for_the_desktop_application()
    {

        string[] offenders = typeof(ArcanumErrorMapper).Assembly
            .GetTypes()
            .Where(static type => !type.Name.StartsWith('<'))
            .Where(static type => type.Name.Contains("Forge", StringComparison.Ordinal))
            .Select(static type => type.FullName ?? type.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);

    }

    /// <summary>
    /// A Ward resolves an operator's allow or deny for a high-risk tool and a Sanctum is a Campaign's
    /// execution isolation policy. Both are security surfaces, and neither is an authored resource.
    /// </summary>
    [Fact]
    public void Security_surfaces_are_not_filed_under_an_authoring_namespace()
    {

        Assert.Equal("RetroDownfall.Arcanum.Api.Security", typeof(WardEndpoints).Namespace);

        Assert.Equal("RetroDownfall.Arcanum.Api.Security", typeof(SanctumEndpoints).Namespace);

    }

    /// <summary>
    /// The error mapper is used by every endpoint family in the Api and belongs to none of them, which
    /// is why it sits with the shared primitives rather than in whichever domain happened to hold it.
    /// </summary>
    [Fact]
    public void The_shared_error_mapper_is_not_filed_under_a_domain_namespace()
    {

        Assert.Equal("RetroDownfall.Arcanum.Api.Primitives", typeof(ArcanumErrorMapper).Namespace);

    }

    [Fact]
    public async Task Every_moved_endpoint_family_maps_the_routes_it_always_mapped()
    {

        string[] expected =
        [
            "DELETE /api/apprentices/{id:guid} DeleteApprentice",
            "DELETE /api/campaigns/{id:guid} DeleteCampaign",
            "DELETE /api/campaigns/{id:guid}/codex DeleteCampaignCodex",
            "DELETE /api/codex DeleteGlobalCodex",
            "DELETE /api/lore/{key} DeleteLore",
            "DELETE /api/memory/lexicon/{**name} DeleteLexiconEntry",
            "DELETE /api/prompts/{id:guid} DeletePrompt",
            "DELETE /api/saga DeleteAllSagaMemories",
            "DELETE /api/saga/{id} DeleteSagaMemory",
            "DELETE /api/sessions/{id:guid} ArchiveSession",
            "DELETE /api/sessions/{id:guid}/context-pins/{pinId:guid} DeleteSessionContextPin",
            "DELETE /api/sessions/{id:guid}/entries/{entryId:guid} DeleteSessionEntry",
            "DELETE /api/sessions/{id:guid}/entries/{entryId:guid}/pin UnpinSessionEntry",
            "GET /api/apprentices ListApprentices",
            "GET /api/apprentices/{id:guid} GetApprentice",
            "GET /api/apprentices/{id:guid}/chronicle GetApprenticeChronicle",
            "GET /api/campaigns ListCampaigns",
            "GET /api/campaigns/by-path GetCampaignByPath",
            "GET /api/campaigns/{campaignId:guid}/sanctum GetCampaignSanctum",
            "GET /api/campaigns/{campaignId:guid}/sanctum/breaches GetCampaignSanctumBreaches",
            "GET /api/campaigns/{id:guid} GetCampaign",
            "GET /api/campaigns/{id:guid}/codex GetCampaignCodex",
            "GET /api/campaigns/{id:guid}/prompts GetCampaignPrompts",
            "GET /api/campaigns/{id:guid}/sessions GetCampaignSessions",
            "GET /api/campaigns/{id:guid}/spells GetCampaignSpells",
            "GET /api/codex GetGlobalCodex",
            "GET /api/lore GetLore",
            "GET /api/lore/{key} GetLoreByKey",
            "GET /api/memory/explain ExplainMemory",
            "GET /api/memory/explain/{sessionId:guid} ExplainSessionMemory",
            "GET /api/memory/lexicon ListLexiconEntries",
            "GET /api/memory/lexicon/{**name} GetLexiconEntry",
            "GET /api/memory/sources GetMemorySources",
            "GET /api/memory/sources/{sessionId:guid} GetSessionMemorySources",
            "GET /api/memory/status GetMemoryStatus",
            "GET /api/memory/status/{sessionId:guid} GetSessionMemoryStatus",
            "GET /api/prompts ListPrompts",
            "GET /api/prompts/by-name/{name}/versions ListPromptVersions",
            "GET /api/prompts/{id:guid} GetPrompt",
            "GET /api/saga ListSagaMemories",
            "GET /api/saga/stats GetSagaStats",
            "GET /api/sessions QuerySessions",
            "GET /api/sessions/analytics GetSessionAnalytics",
            "GET /api/sessions/{id:guid} GetSession",
            "GET /api/sessions/{id:guid}/attachments GetSessionAttachments",
            "GET /api/sessions/{id:guid}/attachments/{attachmentId:guid}/content DownloadSessionAttachment",
            "GET /api/sessions/{id:guid}/context-pins GetSessionContextPins",
            "GET /api/sessions/{id:guid}/entries GetSessionEntries",
            "GET /api/sessions/{id:guid}/export ExportSession",
            "GET /api/sessions/{id:guid}/stream StreamSession",
            "GET /api/spells/search SearchSpells",
            "GET /api/spells/{name}/versions Spell_ListVersions",
            "GET /api/spells/{name}/versions/{version} Spell_GetVersionDetail",
            "GET /api/wards ListWards",
            "GET /api/wards/{id} GetWard",
            "PATCH /api/sessions/{id:guid} UpdateSession",
            "POST /api/apprentices CreateApprentice",
            "POST /api/apprentices/{id:guid}/cancel CancelApprentice",
            "POST /api/apprentices/{id:guid}/cast CastApprentice",
            "POST /api/apprentices/{id:guid}/intervene InterveneApprentice",
            "POST /api/apprentices/{id:guid}/pause PauseApprentice",
            "POST /api/apprentices/{id:guid}/resume ResumeApprentice",
            "POST /api/apprentices/{id:guid}/reweave ReweaveApprentice",
            "POST /api/apprentices/{id:guid}/start StartApprentice",
            "POST /api/campaigns RegisterCampaign",
            "POST /api/campaigns/{id:guid}/export ExportCampaign",
            "POST /api/campaigns/{id:guid}/import ImportCampaign",
            "POST /api/lore UpsertLore",
            "POST /api/memory/search SearchMemory",
            "POST /api/prompts CreatePrompt",
            "POST /api/prompts/import ImportPrompt",
            "POST /api/prompts/{id:guid}/clone ClonePrompt",
            "POST /api/prompts/{id:guid}/execute Prompt_Execute",
            "POST /api/prompts/{id:guid}/execute-stream Prompt_ExecuteStream",
            "POST /api/prompts/{id:guid}/export ExportPrompt",
            "POST /api/prompts/{id:guid}/render RenderPrompt",
            "POST /api/prompts/{id:guid}/test TestPrompt",
            "POST /api/providers/test PostProviderTest",
            "POST /api/saga/divine SagaDivination",
            "POST /api/sessions CreateSession",
            "POST /api/sessions/divine SessionDivination",
            "POST /api/sessions/{id:guid}/attachments CreateSessionAttachmentSnapshot",
            "POST /api/sessions/{id:guid}/attachments/reference CreateSessionAttachmentReference",
            "POST /api/sessions/{id:guid}/attachments/{attachmentId:guid}/refresh RefreshSessionAttachment",
            "POST /api/sessions/{id:guid}/compact CompactSession",
            "POST /api/sessions/{id:guid}/context-pins CreateSessionContextPin",
            "POST /api/sessions/{id:guid}/entries AppendSessionEntry",
            "POST /api/sessions/{id:guid}/entries/{entryId:guid}/pin PinSessionEntry",
            "POST /api/sessions/{id:guid}/fork ForkSession",
            "POST /api/sessions/{id:guid}/rest PostSessionRest",
            "POST /api/spells/import ImportSpell",
            "POST /api/spells/{name}/cast Spell_Cast",
            "POST /api/spells/{name}/clone Spell_Clone",
            "POST /api/spells/{name}/execute Spell_Execute",
            "POST /api/spells/{name}/execute-stream Spell_ExecuteStream",
            "POST /api/spells/{name}/export ExportSpell",
            "POST /api/spells/{name}/validate ValidateSpell",
            "POST /api/spells/{name}/versions Spell_CreateVersion",
            "POST /api/spells/{name}/versions/{version}/activate Spell_ActivateVersion",
            "POST /api/wards/{id} ResolveWard",
            "PUT /api/campaigns/{campaignId:guid}/sanctum UpdateCampaignSanctum",
            "PUT /api/campaigns/{id:guid} UpdateCampaign",
            "PUT /api/campaigns/{id:guid}/codex PutCampaignCodex",
            "PUT /api/codex PutGlobalCodex",
            "PUT /api/prompts/{id:guid} UpdatePrompt",
            "PUT /api/spells/{name}/versions/{version} Spell_UpdateVersion",
        ];

        await using RouteGraph graph = await RouteGraph.CreateAsync();

        string[] actual = graph.Routes();

        Assert.Equal(expected, actual);

    }

    private sealed class RouteGraph : IAsyncDisposable
    {

        private WebApplication _app = null!;

        internal static async Task<RouteGraph> CreateAsync()
        {

            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

            builder.WebHost.UseTestServer();

            RouteGraph graph = new();

            graph._app = builder.Build();

            RouteGroupBuilder api = graph._app.MapGroup("/api");

            _ = api.MapApprenticeEndpoints();

            _ = api.MapCampaignEndpoints();

            _ = api.MapCodexEndpoints();

            _ = api.MapLoreEndpoints();

            _ = api.MapMemoryEndpoints();

            _ = api.MapPromptEndpoints();

            _ = api.MapProviderTestEndpoints();

            _ = api.MapSagaEndpoints();

            _ = api.MapSanctumEndpoints();

            _ = api.MapSessionDivinationEndpoints();

            _ = api.MapSessionEndpoints();

            _ = api.MapSpellAuthoringEndpoints();

            _ = api.MapSpellExecutionEndpoints();

            _ = api.MapWardEndpoints();

            await graph._app.StartAsync();

            return graph;

        }

        internal string[] Routes() =>
            [.. _app.Services
                .GetRequiredService<EndpointDataSource>()
                .Endpoints
                .OfType<RouteEndpoint>()
                .Select(static endpoint => string.Join(
                    ' ',
                    string.Join(
                        ',',
                        (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                            .Order(StringComparer.Ordinal)),
                    endpoint.RoutePattern.RawText,
                    endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? "-"))
                .Order(StringComparer.Ordinal)];

        public async ValueTask DisposeAsync()
        {

            await _app.StopAsync();

            await _app.DisposeAsync();

        }

    }

}
