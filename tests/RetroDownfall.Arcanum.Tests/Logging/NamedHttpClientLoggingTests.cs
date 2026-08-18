using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.CommLink;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.WebResearch;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Resilience;

namespace RetroDownfall.Arcanum.Tests.Logging;

/// <summary>
/// <c>IHttpClientFactory</c>'s default handlers log <c>"Start processing HTTP request {HttpMethod} {Uri}"</c>
/// at Information, and .NET only redacts the query — scheme, authority, and path survive verbatim.
/// Hosted MCP and A2A endpoints routinely carry their bearer token in a path segment, so every named
/// client Arcanum registers must suppress that logging; otherwise the token lands in the rolling JSON
/// log and in the ring buffer that backs <c>GET /api/logs</c>.
/// </summary>
public sealed class NamedHttpClientLoggingTests
{

    public static TheoryData<string> NamedClients() =>
    [
        WebResearchConstants.PerplexityHttpClientName,
        WebResearchConstants.LocalHttpClientName,
        WebhookCommLinkDispatcher.HttpClientName,
        ArcanumBrowseWebConstants.HttpClientName,
        A2AClientService.OutboundHttpClientName,
        McpConnectionManager.McpHttpClientName,
        ProviderHealthProbe.HttpClientName,
    ];

    [Theory]
    [MemberData(nameof(NamedClients))]
    public void Every_named_http_client_suppresses_default_uri_logging(string clientName)
    {

        ServiceCollection services = [];

        services.AddArcanumInfrastructure(new ConfigurationBuilder().Build());

        using ServiceProvider provider = services.BuildServiceProvider();

        using HttpMessageHandler handler =
            provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(clientName);

        List<string> pipeline = [];

        for (HttpMessageHandler? current = handler;
            current is DelegatingHandler delegating;
            current = delegating.InnerHandler)
        {

            pipeline.Add(delegating.GetType().Name);

        }

        Assert.DoesNotContain(pipeline, static name => name.Contains("Logging", StringComparison.Ordinal));

    }

}
