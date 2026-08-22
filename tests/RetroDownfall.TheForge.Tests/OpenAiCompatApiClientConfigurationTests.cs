using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Models.OpenAi;
using RetroDownfall.TheForge.Core.Serialization;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.ViewModels;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

/// <summary>
/// The OpenAI-shaped client reads the same hand-editable <c>the-forge.json</c> BaseUrl and the same
/// stored master key as <see cref="ArcanumApiClient"/>, so it needs the same guards: a malformed value
/// must arrive as an <c>OpenAiResult</c> failure, never as a raw UriFormatException or FormatException
/// escaping into a fire-and-forget view-model command.
/// </summary>
public sealed class OpenAiCompatApiClientConfigurationTests
{

    [Fact]
    public async Task GetAsync_empty_base_url_returns_config_failure()
    {

        OpenAiCompatApiClient client = CreateClient(baseUrl: string.Empty);

        OpenAiResult<OpenAiFileObject> result = await client.GetAsync(
            "/v1/files/file-1",
            TheForgeJsonContext.Default.OpenAiFileObject,
            CancellationToken.None);

        Assert.False(result.Success);

        Assert.Equal(ArcanumApiClient.InvalidBaseUrlCode, result.ErrorCode);

    }

    [Fact]
    public async Task GetAsync_base_url_without_scheme_returns_config_failure()
    {

        OpenAiCompatApiClient client = CreateClient(baseUrl: "localhost:5001");

        OpenAiResult<OpenAiFileObject> result = await client.GetAsync(
            "/v1/files/file-1",
            TheForgeJsonContext.Default.OpenAiFileObject,
            CancellationToken.None);

        Assert.False(result.Success);

        Assert.Equal(ArcanumApiClient.InvalidBaseUrlCode, result.ErrorCode);

    }

    [Fact]
    public async Task GetAsync_api_key_with_embedded_newline_returns_config_failure()
    {

        OpenAiCompatApiClient client = CreateClient(apiKey: "line-one\nline-two");

        OpenAiResult<OpenAiFileObject> result = await client.GetAsync(
            "/v1/files/file-1",
            TheForgeJsonContext.Default.OpenAiFileObject,
            CancellationToken.None);

        Assert.False(result.Success);

        Assert.Equal(ArcanumApiClient.InvalidApiKeyCode, result.ErrorCode);

    }

    [Fact]
    public async Task OpenContentStreamAsync_empty_base_url_returns_config_failure()
    {

        OpenAiCompatApiClient client = CreateClient(baseUrl: string.Empty);

        OpenAiResult<Stream> result = await client.OpenContentStreamAsync(
            "/v1/files/file-1/content",
            CancellationToken.None);

        Assert.False(result.Success);

        Assert.Equal(ArcanumApiClient.InvalidBaseUrlCode, result.ErrorCode);

    }

    [Fact]
    public async Task DownloadToFileAsync_empty_base_url_returns_config_failure()
    {

        OpenAiCompatApiClient client = CreateClient(baseUrl: string.Empty);

        string destination = Path.Combine(Path.GetTempPath(), $"forge-download-{Guid.NewGuid():N}.jsonl");

        OpenAiResult<bool> result = await client.DownloadToFileAsync(
            "/v1/files/file-1/content",
            destination,
            CancellationToken.None);

        Assert.False(result.Success);

        Assert.Equal(ArcanumApiClient.InvalidBaseUrlCode, result.ErrorCode);

        Assert.False(File.Exists(destination));

    }

    [Fact]
    public async Task PostMultipartFileAsync_empty_base_url_returns_config_failure()
    {

        OpenAiCompatApiClient client = CreateClient(baseUrl: string.Empty);

        string source = Path.Combine(Path.GetTempPath(), $"forge-upload-{Guid.NewGuid():N}.jsonl");

        await File.WriteAllTextAsync(source, "{}\n");

        try
        {

            OpenAiResult<OpenAiFileObject> result = await client.PostMultipartFileAsync(
                "/v1/files",
                source,
                "batch",
                TheForgeJsonContext.Default.OpenAiFileObject,
                CancellationToken.None);

            Assert.False(result.Success);

            Assert.Equal(ArcanumApiClient.InvalidBaseUrlCode, result.ErrorCode);

        }
        finally
        {

            File.Delete(source);

        }

    }

    private static OpenAiCompatApiClient CreateClient(
        string baseUrl = "http://localhost:5001",
        string apiKey = "test-key") =>
        new(
            new StaticHttpClientFactory(new EmptyOkHandler()),
            new StaticTheForgeSettingsMonitor(new TheForgeSettings
            {
                BaseUrl = baseUrl,
            }),
            new StaticApiKeyProvider(apiKey),
            NullLogger<OpenAiCompatApiClient>.Instance,
            ImmediateTheForgeLocalMutationRunner.Instance);

    private sealed class StaticApiKeyProvider(string apiKey) : ITheForgeApiKeyProvider
    {

        public Task<string?> GetApiKeyAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>(apiKey);

        public Task PersistPastedKeyAsync(string key, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void ClearPasteDecline()
        {
        }

    }

    private sealed class StaticHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false);

    }

    private sealed class EmptyOkHandler : HttpMessageHandler
    {

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            });

    }

}
