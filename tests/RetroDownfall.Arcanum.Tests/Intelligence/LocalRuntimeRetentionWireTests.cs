using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class LocalRuntimeRetentionWireTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public async Task Actual_chat_request_retains_only_explicit_local_foreground_model(bool streaming, bool local, bool foreground)
    {
        ArcanumSettings settings = Settings(local);

        using Capture capture = new(streaming);

        using ProviderWorkCoordinator coordinator = new();

        using ProviderWorkCoordinator.ForegroundTurn? owner = foreground ? coordinator.BeginForegroundTurn() : null;

        using IDisposable scope = ProviderWorkAmbient.Push(owner);

        using ChatClientLease lease = await new ChatClientFactory(capture, new TestOptionsMonitor<ArcanumSettings>(settings), new MissingCredentials())
            .ResolveClientAsync(null, CancellationToken.None);

        ChatOptions options = new() { MaxOutputTokens = 128, Temperature = 0 };

        ChatMessage[] messages = [new(ChatRole.User, "Continue after the tool result.")];

        if (streaming)
        {
            await foreach (ChatResponseUpdate _ in lease.ChatClient.GetStreamingResponseAsync(messages, options)) { }
        }
        else
        {
            Assert.Equal("OK", (await lease.ChatClient.GetResponseAsync(messages, options)).Text);
        }

        using JsonDocument body = JsonDocument.Parse(Assert.Single(capture.Bodies));

        bool hasRetention = body.RootElement.TryGetProperty("keep_alive", out JsonElement retention);

        Assert.Equal(local && foreground, hasRetention);

        if (hasRetention) Assert.Equal(-1, retention.GetInt64());

        Assert.Equal("fixture-model", body.RootElement.GetProperty("model").GetString());

        Assert.Equal("Continue after the tool result.", body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());

        Assert.Equal(128, options.MaxOutputTokens);

        Assert.Null(options.RawRepresentationFactory);

        Assert.EndsWith("/chat/completions", Assert.Single(capture.Uris).AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expired_foreground_owner_does_not_request_retention()
    {
        using Capture capture = new(false);

        using ProviderWorkCoordinator coordinator = new();

        ProviderWorkCoordinator.ForegroundTurn owner = coordinator.BeginForegroundTurn();

        using IDisposable scope = ProviderWorkAmbient.Push(owner);

        using ChatClientLease lease = await new ChatClientFactory(capture, new TestOptionsMonitor<ArcanumSettings>(Settings(true)), new MissingCredentials())
            .ResolveClientAsync(null, CancellationToken.None);

        owner.Dispose();

        await lease.ChatClient.GetResponseAsync([new(ChatRole.User, "Detached work.")]);

        using JsonDocument body = JsonDocument.Parse(Assert.Single(capture.Bodies));

        Assert.False(body.RootElement.TryGetProperty("keep_alive", out _));
    }

    [Fact]
    public async Task Unknown_runtime_binding_fails_before_credentials_or_transport()
    {
        ArcanumSettings settings = Settings(true);

        settings.Providers[0].LocalRuntimeId = "missing";

        using Capture capture = new(false);

        MissingCredentials credentials = new();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ChatClientFactory(capture, new TestOptionsMonitor<ArcanumSettings>(settings), credentials)
                .ResolveClientAsync(null, CancellationToken.None));

        Assert.Equal(0, credentials.Calls);

        Assert.Empty(capture.Bodies);
    }

    private static ArcanumSettings Settings(bool local) => new()
    {
        DefaultModel = "fixture-model",
        Providers = [new() { Name = "fixture", Models = ["fixture-model"], Endpoint = "http://localhost:11434/v1", LocalRuntimeId = local ? "desktop" : null }],
        LocalRuntimes = [new() { Id = "desktop", Adapter = "ollama", ManagementEndpoint = "http://localhost:11434" }],
    };

    private sealed class MissingCredentials : IProviderApiKeyResolver
    {
        internal int Calls;

        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken = default)
        {
            Calls++;

            return Task.FromResult<string?>(null);
        }
    }

    private sealed class Capture(bool streaming) : HttpMessageHandler, IHttpClientFactory
    {
        internal List<string> Bodies { get; } = [];

        internal List<Uri> Uris { get; } = [];

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));

            Uris.Add(request.RequestUri!);

            string body = streaming
                ? "data: {\"id\":\"fixture\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"
                : "{\"id\":\"fixture\",\"object\":\"chat.completion\",\"created\":0,\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}";

            return new(HttpStatusCode.OK) { Content = ProviderSdkTestTransport.Content(body, streaming ? "text/event-stream" : "application/json") };
        }
    }
}
