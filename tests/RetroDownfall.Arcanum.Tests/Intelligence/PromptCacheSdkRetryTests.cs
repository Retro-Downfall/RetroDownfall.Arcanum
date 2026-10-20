using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;
using RetroDownfall.Arcanum.Tests.LongRest;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class PromptCacheSdkRetryTests
{
    [Theory]
    [InlineData(false, "epoch")]
    [InlineData(true, "epoch")]
    [InlineData(false, "unchanged")]
    [InlineData(true, "unchanged")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    [InlineData(false, "ordinary")]
    [InlineData(true, "ordinary")]
    [InlineData(false, "connection")]
    [InlineData(true, "connection")]
    public async Task Every_physical_sdk_attempt_requires_current_binding(bool streaming, string transition)
    {
        using Fixture f = new();

        using CancellationTokenSource cancellation = new();

        f.Handler.FirstException = transition == "connection" ? new HttpRequestException("controlled connection reset") : null;

        f.Handler.BeforeFirstResponse = () =>
        {
            if (transition is "epoch" or "ordinary")
            {
                f.Monitor.CurrentValue.Providers[0].Endpoint = "https://changed.test/v1";

                f.Monitor.Set(f.Monitor.CurrentValue);

                Assert.Equal(1, f.Epoch.Capture().Stamp.Epoch);

                Assert.False(cancellation.IsCancellationRequested);
            }

            if (transition == "cancel") cancellation.Cancel();
        };

        using ChatClientLease lease = await f.Factory.ResolveClientAsync(null, CancellationToken.None);

        ChatOptions options = new();

        var prepared = (await f.Validator.PrepareAsync(lease, f.Ordinary, options, CanonicalCampaignContext.GlobalOnly,
            ContentSensitivity.None, CancellationToken.None))!;

        Assert.NotNull(prepared);

        Assert.True(prepared.Optimized);

        List<ChatMessage> messages = [new(ChatRole.System, prepared.Build.Prompt), new(ChatRole.User, "hello")];

        ModelCallContext context = new(lease.Provider, lease.ResolvedModel, 0, 0,
            PromptCacheBinding: transition == "ordinary" ? null : prepared.Bind(messages, options));

        async Task Run()
        {
            if (streaming)
            {
                List<ModelCallUpdate> updates = [];

                await foreach (var update in f.Executor.ExecuteStreamingAsync(lease.ChatClient, messages, options,
                    UnrestrictedTurnBudget.Instance, ModelCallPurpose.MainInference, cancellation.Token, context)) updates.Add(update);

                if (transition == "epoch") Assert.Equal(ErrorCodes.Hub.ContextBudgetExceeded, Assert.Single(updates.OfType<ModelCallFailureUpdate>()).Error.Code);
                else Assert.Empty(updates.OfType<ModelCallFailureUpdate>());
            }
            else
            {
                var result = await f.Executor.ExecuteBufferedAsync(lease.ChatClient, messages, options,
                    UnrestrictedTurnBudget.Instance, ModelCallPurpose.MainInference, cancellation.Token, context);

                if (transition == "epoch") Assert.Equal(ErrorCodes.Hub.ContextBudgetExceeded, result.Error.Code);
                else if (transition == "cancel") Assert.True(result.IsFailure);
                else Assert.True(result.IsSuccess);
            }
        }

        if (transition == "cancel" && streaming) await Assert.ThrowsAnyAsync<OperationCanceledException>(Run);
        else await Run();

        Assert.Equal(transition is "epoch" or "cancel" ? 1 : 2, f.Handler.Sends);

        Assert.Equal(transition == "cancel", cancellation.IsCancellationRequested);

        int completedSends = f.Handler.Sends;

        // Even a canceled/refused invocation cannot leave its callback on this reusable lease.
        await lease.ChatClient.GetResponseAsync([new(ChatRole.User, "after completion")], cancellationToken: CancellationToken.None);

        Assert.Equal(completedSends + 1, f.Handler.Sends);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task Retry_refusal_retains_canonical_error_and_ordinary_directive(bool streaming, bool canonical, bool connectionFailure)
    {
        using Fixture f = new(withCampaign: true);

        using ChatClientLease lease = await f.Factory.ResolveClientAsync(null, CancellationToken.None);

        Error expected = new(ErrorCodes.Covenant.IntegrityFailure, "canonical failure on SDK retry");

        f.Handler.FirstException = connectionFailure ? new HttpRequestException("controlled connection reset") : null;

        f.Handler.BeforeFirstResponse = () =>
        {
            if (canonical) f.Campaign.PathFailure = expected;
            else f.CredentialSource.Currentness = ProviderCredentialCurrentness.Unavailable;
        };

        ChatOptions options = new();

        var campaign = CanonicalCampaignContext.Create(SessionCampaignBinding.ForCampaign(f.Campaign.Id), 1, 1, null, null);

        var prepared = (await f.Validator.PrepareAsync(lease, f.Ordinary, options, campaign, ContentSensitivity.None, CancellationToken.None))!;

        List<ChatMessage> messages = [new(ChatRole.System, prepared.Build.Prompt)];

        ModelCallContext context = new(lease.Provider, lease.ResolvedModel, 0, 0, PromptCacheBinding: prepared.Bind(messages, options));

        Error actual;

        ModelCallRetryDirective directive;

        if (streaming)
        {
            List<ModelCallUpdate> updates = [];

            await foreach (var update in f.Executor.ExecuteStreamingAsync(lease.ChatClient, messages, options,
                UnrestrictedTurnBudget.Instance, ModelCallPurpose.MainInference, CancellationToken.None, context)) updates.Add(update);

            var failure = Assert.Single(updates.OfType<ModelCallFailureUpdate>());

            actual = failure.Error;

            directive = failure.RetryDirective;
        }
        else
        {
            var result = await f.Executor.ExecuteBufferedAsync(lease.ChatClient, messages, options,
                UnrestrictedTurnBudget.Instance, ModelCallPurpose.MainInference, CancellationToken.None, context);

            Assert.True(result.IsFailure);

            actual = result.Error;

            directive = result.Failure.RetryDirective;
        }

        Assert.Equal(1, f.Handler.Sends);

        if (canonical) Assert.Equal(expected, actual);
        else Assert.Equal(ErrorCodes.Hub.ContextBudgetExceeded, actual.Code);

        Assert.Equal(canonical ? ModelCallRetryDirective.None : ModelCallRetryDirective.OrdinaryPromptCacheFallback, directive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_ordinary_call_and_later_calls_do_not_inherit_bound_refusal(bool streaming)
    {
        using Fixture f = new();

        using ChatClientLease lease = await f.Factory.ResolveClientAsync(null, CancellationToken.None);

        TaskCompletionSource firstSent = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        f.Handler.WaitForFirstResponse = async () =>
        {
            firstSent.SetResult();

            await release.Task;
        };

        ChatOptions options = new();

        var prepared = (await f.Validator.PrepareAsync(lease, f.Ordinary, options, CanonicalCampaignContext.GlobalOnly,
            ContentSensitivity.None, CancellationToken.None))!;

        List<ChatMessage> messages = [new(ChatRole.System, prepared.Build.Prompt)];

        ModelCallContext context = new(lease.Provider, lease.ResolvedModel, 0, 0, PromptCacheBinding: prepared.Bind(messages, options));

        async Task Bound()
        {
            if (streaming)
            {
                List<ModelCallUpdate> updates = [];

                await foreach (var update in f.Executor.ExecuteStreamingAsync(lease.ChatClient, messages, options,
                    UnrestrictedTurnBudget.Instance, ModelCallPurpose.MainInference, CancellationToken.None, context)) updates.Add(update);

                Assert.Equal(ErrorCodes.Hub.ContextBudgetExceeded, Assert.Single(updates.OfType<ModelCallFailureUpdate>()).Error.Code);
            }
            else
            {
                var result = await f.Executor.ExecuteBufferedAsync(lease.ChatClient, messages, options,
                    UnrestrictedTurnBudget.Instance, ModelCallPurpose.MainInference, CancellationToken.None, context);

                Assert.Equal(ErrorCodes.Hub.ContextBudgetExceeded, result.Error.Code);
            }
        }

        Task bound = Bound();

        await firstSent.Task.WaitAsync(TimeSpan.FromSeconds(10));

        f.Monitor.CurrentValue.Providers[0].Endpoint = "https://changed.test/v1";

        try
        {
            // Deliberately use the same lease while its other invocation is still in the SDK.
            var ordinary = await f.Executor.ExecuteBufferedAsync(lease.ChatClient, [new(ChatRole.User, "ordinary")], options,
                UnrestrictedTurnBudget.Instance, ModelCallPurpose.MainInference, CancellationToken.None, context with { PromptCacheBinding = null });

            Assert.True(ordinary.IsSuccess);
        }
        finally
        {
            release.TrySetResult();
        }

        await bound;

        Assert.Equal(2, f.Handler.Sends);

        // A fresh stream on the same lease after refused-stream disposal has no inherited callback.
        await foreach (var _ in lease.ChatClient.GetStreamingResponseAsync([new(ChatRole.User, "later")])) { }

        Assert.Equal(3, f.Handler.Sends);
    }

    [Fact]
    public async Task Suspended_stream_does_not_export_its_validation_scope_to_consumer_or_disposal()
    {
        using Fixture f = new();

        using ChatClientLease lease = await f.Factory.ResolveClientAsync(null, CancellationToken.None);

        ChatOptions options = new();

        var prepared = (await f.Validator.PrepareAsync(lease, f.Ordinary, options, CanonicalCampaignContext.GlobalOnly,
            ContentSensitivity.None, CancellationToken.None))!;

        List<ChatMessage> messages = [new(ChatRole.System, prepared.Build.Prompt)];

        ModelCallContext context = new(lease.Provider, lease.ResolvedModel, 0, 0, PromptCacheBinding: prepared.Bind(messages, options));

        await using (var stream = f.Executor.ExecuteStreamingAsync(lease.ChatClient, messages, options,
            UnrestrictedTurnBudget.Instance, ModelCallPurpose.MainInference, CancellationToken.None, context).GetAsyncEnumerator())
        {
            while (await stream.MoveNextAsync())
            {
                if (stream.Current is not ModelCallTextDelta) continue;

                f.Monitor.CurrentValue.Providers[0].Endpoint = "https://changed.test/v1";

                await lease.ChatClient.GetResponseAsync([new(ChatRole.User, "consumer invocation")]);

                Assert.Equal(3, f.Handler.Sends);

                break;
            }
        }

        await lease.ChatClient.GetResponseAsync([new(ChatRole.User, "after abandonment")]);

        Assert.Equal(4, f.Handler.Sends);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Provider_aggregates_without_this_operations_refusal_keep_existing_failure_semantics(bool streaming, bool foreignRefusal)
    {
        using Fixture f = new();

        using ChatClientLease lease = await f.Factory.ResolveClientAsync(null, CancellationToken.None);

        AggregateException providerFailure = new(new HttpRequestException("earlier provider failure"),
            foreignRefusal ? new PromptCacheDispatchException(new Error(ErrorCodes.Covenant.IntegrityFailure, "foreign refusal"),
                ModelCallRetryDirective.OrdinaryPromptCacheFallback) : new InvalidOperationException("terminal provider failure"));

        f.Handler.FirstException = providerFailure;

        ChatOptions options = new();

        var prepared = (await f.Validator.PrepareAsync(lease, f.Ordinary, options, CanonicalCampaignContext.GlobalOnly,
            ContentSensitivity.None, CancellationToken.None))!;

        List<ChatMessage> messages = [new(ChatRole.System, prepared.Build.Prompt)];

        ModelCallContext context = new(lease.Provider, lease.ResolvedModel, 0, 0, PromptCacheBinding: prepared.Bind(messages, options));

        if (streaming)
        {
            var actual = await Assert.ThrowsAsync<AggregateException>(async () =>
            {
                await foreach (var _ in f.Executor.ExecuteStreamingAsync(lease.ChatClient, messages, options,
                    UnrestrictedTurnBudget.Instance, ModelCallPurpose.MainInference, CancellationToken.None, context)) { }
            });

            Assert.Same(providerFailure, actual);
        }
        else
        {
            var result = await f.Executor.ExecuteBufferedAsync(lease.ChatClient, messages, options,
                UnrestrictedTurnBudget.Instance, ModelCallPurpose.MainInference, CancellationToken.None, context);

            Assert.True(result.IsFailure);

            Assert.Equal(ErrorCodes.Hub.Error, result.Error.Code);

            Assert.Equal(ModelCallRetryDirective.None, result.Failure.RetryDirective);

            Assert.Same(providerFailure, result.Failure.Cause);
        }

        Assert.Equal(1, f.Handler.Sends);
    }

    internal sealed class Fixture : IDisposable
    {
        internal LongRestConfigurationEpochTests.NotifyingOptions Monitor { get; } = new(new ArcanumSettings
        {
            Features = new() { DynamicContextInjectionV2 = true },

            DefaultModel = "model",

            Providers = [new() { Name = "provider", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://provider.test/v1", Models = ["model"] }],
        });

        internal RetryHandler Handler { get; } = new();

        internal PromptCacheConfigurationEpoch Epoch { get; }

        internal PromptCacheDispatchValidator Validator { get; }

        internal ChatClientFactory Factory { get; }

        internal ModelCallExecutor Executor { get; }

        internal SystemPromptDocument Ordinary { get; } = SystemPromptBuilder.BuildDocument(new PingRequest("hello") { CliTerminalFormatting = true }, "dynamic codex");

        internal Credentials CredentialSource { get; } = new();

        internal PromptCacheDispatchValidatorTests.CampaignReader Campaign { get; } = new();

        private readonly ServiceProvider services;

        internal Fixture(bool withCampaign = false)
        {
            Epoch = new(Monitor, Guid.NewGuid());

            ServiceCollection registrations = [];

            if (withCampaign)
            {
                registrations.AddScoped<ICampaignAvailabilityReader>(_ => Campaign);

                registrations.AddScoped<ICampaignPathIdentityReader>(_ => Campaign);
            }

            services = registrations.BuildServiceProvider();

            ModelTokenEstimator estimator = new(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance));

            Validator = new(Epoch, CredentialSource, new PromptCacheDispatchValidatorTests.AuthorityProvider(), estimator, services.GetRequiredService<IServiceScopeFactory>());

            Factory = new(new HttpFactory(Handler), Monitor, CredentialSource, promptCacheConfigurationEpoch: Epoch);

            Executor = new(estimator, Monitor, (Microsoft.Extensions.Logging.ILogger?)null, Validator);
        }

        public void Dispose()
        {
            Epoch.Dispose();

            services.Dispose();

            Handler.Dispose();
        }
    }

    internal sealed class Credentials : IProviderApiKeyResolver
    {
        private readonly ProviderCredentialStamp stamp = new(Guid.NewGuid(), Guid.NewGuid(), 1);

        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task<ResolvedProviderCredential> ResolveWithIdentityAsync(ProviderSettings provider, CancellationToken cancellationToken = default) => Task.FromResult(new ResolvedProviderCredential(null, stamp));

        internal ProviderCredentialCurrentness Currentness { get; set; } = ProviderCredentialCurrentness.Current;

        public Task<ProviderCredentialCurrentness> GetCurrentnessAsync(ProviderSettings provider, ProviderCredentialStamp candidate, CancellationToken cancellationToken = default) =>
            Task.FromResult(candidate == stamp ? Currentness : ProviderCredentialCurrentness.ConfirmedStale);
    }

    internal sealed class HttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    internal sealed class RetryHandler : HttpMessageHandler
    {
        internal int Sends { get; private set; }

        internal Action? BeforeFirstResponse { get; set; }

        internal Exception? FirstException { get; set; }

        internal Func<Task>? WaitForFirstResponse { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;

            string body = await request.Content!.ReadAsStringAsync(cancellationToken);

            if (Sends == 1)
            {
                if (WaitForFirstResponse is { } wait) await wait();

                BeforeFirstResponse?.Invoke();

                if (FirstException is { } failure) throw failure;

                HttpResponseMessage retry = new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };

                retry.Headers.Add("retry-after-ms", "1");

                return retry;
            }

            bool streaming = body.Contains("\"stream\":true", StringComparison.Ordinal);

            string json = streaming
                ? "data: {\"id\":\"chat-test\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":null}]}\n\ndata: [DONE]\n\n"
                : """{"id":"chat-test","object":"chat.completion","created":1,"model":"model","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";

            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, streaming ? "text/event-stream" : "application/json") };
        }
    }
}
