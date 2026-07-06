using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// Task 11 — HTTP response compression. Verifies Gzip kicks in for JSON responses when the client
/// advertises support, and stays off for NDJSON streaming (which sets its own anti-buffering
/// headers, §8.9, and would have those defeated by a buffering compressor).
/// </summary>
[Collection("ApiHost")]
public sealed class ResponseCompressionTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public ResponseCompressionTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task GetHealth_WithGzipAcceptEncoding_ReturnsGzipCompressedBody()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "/api/health");

        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Contains(response.Content.Headers.ContentEncoding, e => string.Equals(e, "gzip", StringComparison.OrdinalIgnoreCase));

        await using Stream compressed = await response.Content.ReadAsStreamAsync();

        await using GZipStream gzip = new(compressed, CompressionMode.Decompress);

        using StreamReader reader = new(gzip, Encoding.UTF8);

        string json = await reader.ReadToEndAsync();

        using JsonDocument document = JsonDocument.Parse(json);

        Assert.True(document.RootElement.TryGetProperty("isSuccess", out _));

    }

    [SkippableFact]
    public async Task GetHealth_WithoutAcceptEncoding_ReturnsUncompressedBody()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "/api/health");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Empty(response.Content.Headers.ContentEncoding);

    }

    [SkippableFact]
    public async Task PostPingStream_WithGzipAcceptEncoding_StaysUncompressed()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _factory.FakeIntelligence.NextText = "streamed-pong";

        HttpClient client = _factory.CreateAuthenticatedClient();

        PingRequest pingRequest = new(Prompt: "ping");

        using HttpRequestMessage request = new(HttpMethod.Post, "/api/intelligence/ping-stream")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(pingRequest, ArcanumJsonContext.Default.PingRequest),
                Encoding.UTF8,
                "application/json"),
        };

        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);

        Assert.Empty(response.Content.Headers.ContentEncoding);

    }

}
