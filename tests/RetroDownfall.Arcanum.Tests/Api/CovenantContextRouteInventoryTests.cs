using Microsoft.AspNetCore.Http;

using Microsoft.AspNetCore.Routing;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Api.Security;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// The routes that declare the Covenant context policy meaningful, and the one that stopped.
/// </summary>
/// <remarks>
/// The declaration is a promise to the caller, not a decoration: a route carrying it accepts
/// <c>X-Arcanum-Context-Policy</c>, and a route without it refuses the header outright. Promising it
/// on a surface that can never inject anything tells a caller that sent <c>none</c> that it suppressed
/// something, when nothing was ever going to be injected there.
///
/// <para>The inventory is exhaustive and pinned by hand, and fails in both directions. Every name
/// listed here builds its invocation context from a resolved Campaign; a tenth route that declares the
/// policy without appearing here has made that promise without anybody checking it can keep it.</para>
/// </remarks>
[Collection("ApiHost")]
public sealed class CovenantContextRouteInventoryTests(ArcanumWebApplicationFactory factory)
{

    /// <summary>Every route that may inject Covenant content. Exhaustive on purpose.</summary>
    private static readonly string[] CovenantBearingRoutes =
    [
        "TestPrompt",
        "Prompt_Execute",
        "Prompt_ExecuteStream",
        "Spell_Execute",
        "Spell_ExecuteStream",
        "Spell_Cast",
        "PostIntelligencePing",
        "PostIntelligencePingStream",
        "PostIntelligenceContextInspect",
    ];

    [Fact]
    public void Only_the_routes_that_resolve_a_campaign_declare_the_covenant_context_policy()
    {

        _ = factory.CreateAuthenticatedClient();

        EndpointDataSource endpoints = factory.Services.GetRequiredService<EndpointDataSource>();

        string[] declared =
        [
            .. endpoints.Endpoints
                .Where(static endpoint =>
                    endpoint.Metadata.GetMetadata<CovenantContextPolicyRequirementMetadata>() is not null)
                .Select(static endpoint =>
                    endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? endpoint.DisplayName ?? "")
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal([.. CovenantBearingRoutes.Order(StringComparer.Ordinal)], declared);

    }

    /// <summary>
    /// The OpenAI-compatible completion surface never resolves a Campaign, so it promises nothing.
    /// </summary>
    /// <remarks>
    /// Both of its handlers build the invocation context with <c>ForStatelessTurn</c> and no Campaign,
    /// which the context provider answers with <c>Absent(NoCampaign)</c> before it reads anything — so
    /// neither Global nor Campaign content can apply there whatever the header says.
    /// </remarks>
    [Fact]
    public void The_openai_completion_route_promises_no_covenant_context_it_cannot_deliver()
    {

        _ = factory.CreateAuthenticatedClient();

        EndpointDataSource endpoints = factory.Services.GetRequiredService<EndpointDataSource>();

        Endpoint completions = Assert.Single(
            endpoints.Endpoints,
            static endpoint => string.Equals(
                endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName,
                "PostOpenAiChatCompletions",
                StringComparison.Ordinal));

        Assert.Null(completions.Metadata.GetMetadata<CovenantContextPolicyRequirementMetadata>());

    }

}
