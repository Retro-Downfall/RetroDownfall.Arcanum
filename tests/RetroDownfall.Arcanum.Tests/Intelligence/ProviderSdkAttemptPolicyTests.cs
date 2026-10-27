using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ProviderSdkAttemptPolicyTests
{
    [Theory]
    [InlineData("ordinary-chat")]
    [InlineData("ordinary-embedding")]
    [InlineData("long-rest-chat")]
    [InlineData("long-rest-embedding")]
    [InlineData("campaign")]
    [InlineData("evaluation")]
    public async Task Actual_factory_sdk_pipeline_has_no_hidden_network_deadline(string kind)
    {
        using Harness harness = await Harness.CreateAsync(kind);

        using PipelineMessage message = harness.CreateMessage();

        Assert.Equal(Timeout.InfiniteTimeSpan, message.NetworkTimeout);

        Assert.Equal(0, harness.Handler.Sends);
    }

    [Theory]
    [InlineData("ordinary-chat", false, "429")]
    [InlineData("ordinary-chat", true, "429")]
    [InlineData("ordinary-embedding", false, "429")]
    [InlineData("ordinary-chat", false, "503")]
    [InlineData("ordinary-chat", true, "503")]
    [InlineData("ordinary-embedding", false, "503")]
    [InlineData("ordinary-chat", false, "connection")]
    [InlineData("ordinary-chat", true, "connection")]
    [InlineData("ordinary-embedding", false, "connection")]
    [InlineData("long-rest-chat", false, "503")]
    [InlineData("long-rest-embedding", false, "503")]
    [InlineData("campaign", false, "503")]
    [InlineData("evaluation", false, "503")]
    public async Task One_billable_sdk_call_never_repeats_the_transport_send(string kind, bool streaming, string failure)
    {
        using Harness harness = await Harness.CreateAsync(kind);

        harness.Handler.Failure = failure;

        Exception error = await Assert.ThrowsAnyAsync<Exception>(() => harness.SendAsync(streaming, CancellationToken.None));

        if (failure is "429" or "503")
        {
            Assert.Equal(int.Parse(failure, System.Globalization.CultureInfo.InvariantCulture),
                Assert.IsAssignableFrom<ClientResultException>(error).Status);
        }

        Assert.Equal(1, harness.Handler.Sends);
    }

    [Theory]
    [InlineData("ordinary-chat")]
    [InlineData("ordinary-embedding")]
    [InlineData("long-rest-chat")]
    [InlineData("long-rest-embedding")]
    [InlineData("campaign")]
    [InlineData("evaluation")]
    public async Task Caller_cancellation_joins_a_held_send_and_the_lease_remains_reusable(string kind)
    {
        using Harness harness = await Harness.CreateAsync(kind);

        using CancellationTokenSource caller = new();

        harness.Handler.Hold = true;

        Task send = harness.SendAsync(false, caller.Token);

        await harness.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.True(caller.IsCancellationRequested);

        Assert.Equal(1, harness.Handler.Sends);

        harness.Handler.Hold = false;

        await harness.SendAsync(false, CancellationToken.None);

        Assert.Equal(2, harness.Handler.Sends);
    }

    private sealed class Harness : IDisposable
    {
        internal ControlledHandler Handler { get; } = new();

        private readonly List<IDisposable> _owned = [];

        private ChatClientLease? _chat;

        private EmbeddingGeneratorLease? _embedding;

        internal static async Task<Harness> CreateAsync(string kind)
        {
            Harness harness = new();

            ArcanumSettings settings = new()
            {
                DefaultModel = "model",
                Features = new() { Embeddings = true },
                Integrations = new() { Embeddings = new() { Provider = "provider", Model = "model" } },
                Providers = [new() { Name = "provider", Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "https://provider.invalid/v1", Models = ["model"] }],
            };

            ProviderSettings provider = settings.Providers[0];

            TestOptionsMonitor<ArcanumSettings> monitor = new(settings);

            Keys keys = new();

            switch (kind)
            {
                case "ordinary-chat":
                case "ordinary-embedding":
                    HttpClient http = new(harness.Handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

                    harness._owned.Add(http);

                    if (kind == "ordinary-chat")
                    {
                        ChatClientFactory factory = new(new HttpFactory(http), monitor, keys);

                        harness._chat = await factory.ResolveClientAsync(null, CancellationToken.None);
                    }
                    else
                    {
                        EmbeddingGeneratorFactory factory = new(new HttpFactory(http), monitor, keys);

                        harness._embedding = await factory.ResolveGeneratorAsync(CancellationToken.None);
                    }

                    break;

                case "long-rest-chat":
                case "long-rest-embedding":
                    LongRestProviderClientFactory longRest = new(keys, harness.Handler, NullLogger<LongRestProviderClientFactory>.Instance);

                    harness._owned.Add(longRest);

                    LongRestProviderSelection selection = new(provider, "model", 3, new());

                    if (kind == "long-rest-chat") harness._chat = (await longRest.ResolveChatAsync(selection, CancellationToken.None)).Value;
                    else harness._embedding = (await longRest.ResolveEmbeddingAsync(selection, CancellationToken.None)).Value;

                    break;

                case "campaign":
                    CampaignRollupProviderClientFactory campaign = new(monitor, keys, harness.Handler);

                    harness._owned.Add(campaign);

                    harness._chat = (await campaign.ResolveClientAsync(null, CancellationToken.None)).Value!;

                    break;

                case "evaluation":
                    MemoryEvaluationProviderClientFactory evaluation = new(keys, harness.Handler);

                    harness._owned.Add(evaluation);

                    harness._chat = (await evaluation.ResolveChatAsync(provider, "model", CancellationToken.None)).Value;

                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }

            return harness;
        }

        internal PipelineMessage CreateMessage()
        {
            if (_chat is not null)
            {
                OpenAI.Chat.ChatClient sdk = Assert.IsType<OpenAI.Chat.ChatClient>(_chat.ChatClient.GetService<OpenAI.Chat.ChatClient>());

                return sdk.Pipeline.CreateMessage();
            }

            OpenAI.Embeddings.EmbeddingClient embedding = Assert.IsType<OpenAI.Embeddings.EmbeddingClient>(
                _embedding!.Generator.GetService<OpenAI.Embeddings.EmbeddingClient>());

            return embedding.Pipeline.CreateMessage();
        }

        internal async Task SendAsync(bool streaming, CancellationToken token)
        {
            if (_chat is not null)
            {
                if (streaming)
                {
                    await foreach (ChatResponseUpdate _ in _chat.ChatClient.GetStreamingResponseAsync(
                        [new(ChatRole.User, "synthetic request")], cancellationToken: token)) { }
                }
                else
                {
                    _ = await _chat.ChatClient.GetResponseAsync([new(ChatRole.User, "synthetic request")], cancellationToken: token);
                }
            }
            else
            {
                _ = await _embedding!.Generator.GenerateAsync(["synthetic request"], cancellationToken: token);
            }
        }

        public void Dispose()
        {
            _chat?.Dispose();

            _embedding?.Dispose();

            foreach (IDisposable owned in _owned) owned.Dispose();

            Handler.Dispose();
        }
    }

    private sealed class HttpFactory(HttpClient http) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => http;
    }

    private sealed class Keys : IProviderApiKeyResolver
    {
        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    private sealed class ControlledHandler : HttpMessageHandler
    {
        private int _sends;

        internal int Sends => Volatile.Read(ref _sends);

        internal string? Failure { get; set; }

        internal bool Hold { get; set; }

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sends);

            Entered.TrySetResult();

            if (Hold) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            if (Failure == "connection") throw new HttpRequestException("synthetic connection refusal");

            if (Failure is "429" or "503")
            {
                HttpResponseMessage failure = new((HttpStatusCode)int.Parse(Failure, System.Globalization.CultureInfo.InvariantCulture))
                {
                    Content = new StringContent("{}"),
                };

                failure.Headers.Add("retry-after-ms", "1");

                return failure;
            }

            bool embedding = request.RequestUri!.AbsolutePath.EndsWith("/embeddings", StringComparison.Ordinal);

            string json = embedding
                ? """{"object":"list","model":"model","data":[{"object":"embedding","index":0,"embedding":[1,0,0]}],"usage":{"prompt_tokens":1,"total_tokens":1}}"""
                : """{"id":"test","object":"chat.completion","created":1,"model":"model","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";

            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
