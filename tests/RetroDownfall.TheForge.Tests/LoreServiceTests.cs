using System.Net;
using Xunit;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Services;

namespace RetroDownfall.TheForge.Tests;

public sealed class LoreServiceTests
{
    [Theory]
    [InlineData("team/standards", "team%2Fstandards")]
    [InlineData(".", ".")]
    [InlineData("..", "..")]
    [InlineData("team\\standards", "team%5Cstandards")]
    [InlineData("percent%2Fkey", "percent%252Fkey")]
    [InlineData("hash#key", "hash%23key")]
    [InlineData("question?key", "question%3Fkey")]
    [InlineData("plus+ space", "plus%2B%20space")]
    [InlineData("規則/é", "%E8%A6%8F%E5%89%87%2F%C3%A9")]
    [InlineData("entries", "entries")]
    public async Task Get_and_delete_address_the_exact_key_in_the_query(string key, string encoded)
    {
        RecordingHandler handler = new();

        LoreService service = CreateService(handler);

        Assert.True((await service.GetAsync(key, CancellationToken.None))?.IsSuccess);

        Assert.True((await service.DeleteAsync(key, CancellationToken.None))?.Data);

        Assert.Equal([HttpMethod.Get, HttpMethod.Delete], handler.Methods);

        Assert.All(handler.Addresses, address => Assert.Equal("/api/lore/entries/by-key?key=" + encoded, address));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_cancellation_propagates(bool delete)
    {
        using CancellationTokenSource cancellation = new();

        cancellation.Cancel();

        LoreService service = CreateService(new RecordingHandler());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (delete)
            {
                await service.DeleteAsync("team/standards", cancellation.Token);
            }
            else
            {
                await service.GetAsync("team/standards", cancellation.Token);
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Typed_errors_are_preserved(bool delete)
    {
        LoreService service = CreateService(new RecordingHandler(HttpStatusCode.NotFound));

        string? code = delete
            ? (await service.DeleteAsync("missing/key", CancellationToken.None))?.Error?.Code
            : (await service.GetAsync("missing/key", CancellationToken.None))?.Error?.Code;

        Assert.Equal("Grimoire.LoreNotFound", code);
    }

    private static LoreService CreateService(HttpMessageHandler handler) => new(new ArcanumApiClient(
        new ClientFactory(handler),
        new StaticTheForgeSettingsMonitor(new TheForgeSettings { BaseUrl = "http://localhost:5001" }),
        new KeyProvider(),
        NullLogger<ArcanumApiClient>.Instance));

    private sealed class KeyProvider : ITheForgeApiKeyProvider
    {
        public Task<string?> GetApiKeyAsync(CancellationToken cancellationToken) => Task.FromResult<string?>("test-key");

        public Task PersistPastedKeyAsync(string key, CancellationToken cancellationToken) => Task.CompletedTask;

        public void ClearPasteDecline()
        {
        }
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<string> Addresses { get; } = [];

        public List<HttpMethod> Methods { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Addresses.Add(request.RequestUri!.PathAndQuery);

            Methods.Add(request.Method);

            string body = status == HttpStatusCode.NotFound
                ? """{"data":null,"isSuccess":false,"error":{"code":"Grimoire.LoreNotFound","message":"Missing"}}"""
                : request.Method == HttpMethod.Delete
                    ? """{"data":true,"isSuccess":true,"error":null}"""
                    : """{"data":{"key":"key","value":"value","updatedAtUtc":"2026-10-25T00:00:00Z"},"isSuccess":true,"error":null}""";

            if (status == HttpStatusCode.NotFound && request.Method == HttpMethod.Delete)
            {
                body = body.Replace("\"data\":null", "\"data\":false", StringComparison.Ordinal);
            }

            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
