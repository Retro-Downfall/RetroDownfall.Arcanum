using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// <c>POST /api/tools/invoke</c> integration tests for the built-in <c>browse_web</c> tool.
/// </summary>
[Collection("ApiHost")]
public sealed class ToolInvokeEndpointTests : IAsyncLifetime
{

    private ArcanumWebApplicationFactory _factory = null!;

    private readonly HttpMessageHandlerStub _handler;

    public ToolInvokeEndpointTests()
    {
        _handler = new HttpMessageHandlerStub((request, _) =>
        {
            string html = """

                <!DOCTYPE html>
                <html>
                <head><title>Invoked Page</title></head>
                <body>
                    <h1>Hello from invoke</h1>
                    <p>Integration test content.</p>
                    <a href="/relative">Relative</a>
                    <a href="https://example.com/absolute">Absolute</a>
                </body>
                </html>

                """;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html),
            });
        });
    }

    public Task InitializeAsync()
    {
        _factory = new ArcanumWebApplicationFactory
        {
            ServiceOverrides = services =>
            {
                services.RemoveAll<IHttpClientFactory>();

                services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(_handler));
            },
            SettingsOverride = settings => settings with
            {
                Features = settings.Features with { WebBrowsing = true },
            },
        };

        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [SkippableFact]
    public async Task PostToolInvoke_BrowseWeb_ReturnsTitleContentAndLinks()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        string payload = """
            {
              "toolName": "browse_web",
              "arguments": { "url": "https://example.com/page", "maxLinks": 10 }
            }
            """;

        HttpResponseMessage response = await client.PostAsync(
            "/api/tools/invoke",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<ToolInvokeResponse>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseToolInvokeResponse);

        Assert.NotNull(body);
        Assert.True(body.IsSuccess);
        Assert.NotNull(body.Data);

        BrowseWebResult? result = body.Data.Result.Deserialize(ArcanumJsonContext.Default.BrowseWebResult);

        Assert.NotNull(result);
        Assert.Equal("Invoked Page", result.Title);
        Assert.Contains("Hello from invoke", result.Content);
        Assert.Contains("https://example.com/absolute", result.Links);
        Assert.Contains("https://example.com/relative", result.Links);
    }

    [SkippableFact]
    public async Task PostToolInvoke_BrowseWeb_SsrfBlockedUrl_ReturnsErrorInResult()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        string payload = """
            {
              "toolName": "browse_web",
              "arguments": { "url": "http://127.0.0.1/secret" }
            }
            """;

        HttpResponseMessage response = await client.PostAsync(
            "/api/tools/invoke",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<ToolInvokeResponse>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseToolInvokeResponse);

        Assert.NotNull(body);
        Assert.True(body.IsSuccess);
        Assert.NotNull(body.Data);

        BrowseWebResult? result = body.Data.Result.Deserialize(ArcanumJsonContext.Default.BrowseWebResult);

        Assert.NotNull(result);
        Assert.Contains("WebBrowsing.SsrfBlocked", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task PostToolInvoke_BrowseWeb_DisabledByDefault_ReturnsError()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory disabledFactory = new()
        {
            ServiceOverrides = services =>
            {
                services.RemoveAll<IHttpClientFactory>();

                services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(_handler));
            },
            SettingsOverride = settings => settings with
            {
                Features = settings.Features with { WebBrowsing = false },
            },
        };

        HttpClient client = disabledFactory.CreateAuthenticatedClient();

        string payload = """
            {
              "toolName": "browse_web",
              "arguments": { "url": "https://example.com/page" }
            }
            """;

        HttpResponseMessage response = await client.PostAsync(
            "/api/tools/invoke",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed class HttpMessageHandlerStub : HttpMessageHandler
    {

        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public HttpMessageHandlerStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request, cancellationToken);
        }

    }

}
