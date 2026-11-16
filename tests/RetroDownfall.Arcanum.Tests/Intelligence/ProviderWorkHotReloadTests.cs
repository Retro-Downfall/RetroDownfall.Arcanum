using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ProviderWorkHotReloadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Earlier_no_send_proof_cannot_classify_a_later_transport_or_paid_observer(bool paidObserver)
    {
        ArcanumSettings settings = Settings();

        MutableTestOptionsMonitor<ArcanumSettings> options = new(settings);

        using Capture capture = new(false, reportUsage: true);

        using CampaignRollupProviderClientFactory factory = new(options, new MissingCredentials(), capture);

        using ChatClientLease lease = (await factory.ResolveClientAsync(null, CancellationToken.None)).Value!;

        options.CurrentValue = settings with { LocalRuntimes = [] };

        Exception prior = Assert.IsAssignableFrom<Exception>(await Record.ExceptionAsync(async () =>
            await lease.ChatClient.GetResponseAsync([new(ChatRole.User, "An earlier stale request.")])));

        Assert.Empty(capture.Bodies);

        options.CurrentValue = settings;

        ModelTokenEstimator estimator = new(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance));

        ModelCallExecutor executor = new(estimator, options);

        CampaignMaintenancePayload payload = new("Summarize the source.", "A bounded source paragraph.");

        ModelCallContext context = new(lease.Provider, lease.ResolvedModel, CampaignMaintenancePayload.MaxOutputTokens, 0,
            estimator.EstimateCampaignMaintenance(lease.Provider, lease.ResolvedModel, payload));

        AsyncLocal<bool> observing = new();

        using MeterListener listener = new();

        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (instrument.Meter.Name == "Arcanum" && instrument.Name == "arcanum_provider_reported_input_tokens") observer.EnableMeasurementEvents(instrument);
        };

        listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
        {
            if (observing.Value) throw prior;
        });

        listener.Start();

        if (!paidObserver) capture.Failure = prior;

        observing.Value = paidObserver;

        Result<CampaignMaintenanceCallResult> result;

        try
        {
            result = await executor.ExecuteCampaignMaintenanceAsync(lease.ChatClient, payload,
                UnrestrictedTurnBudget.Instance, _ => Task.CompletedTask, CancellationToken.None, context);
        }
        finally
        {
            observing.Value = false;
        }

        Assert.Single(capture.Bodies);

        Assert.True(result.IsFailure);

        Assert.NotEqual(ErrorCodes.Covenant.StaleSnapshot, result.Error.Code);
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("streaming")]
    [InlineData("embedding")]
    [InlineData("long-rest-chat")]
    [InlineData("long-rest-embedding")]
    [InlineData("campaign")]
    [InlineData("memory-evaluation")]
    public async Task Actual_transport_revalidates_a_resolved_route_after_later_awaits(string kind)
    {
        ArcanumSettings settings = Settings();

        MutableTestOptionsMonitor<ArcanumSettings> options = new(settings);

        using Capture capture = new(kind == "streaming");

        using LongRestProviderClientFactory longRest = new(new MissingCredentials(), capture, NullLogger<LongRestProviderClientFactory>.Instance, options);

        using CampaignRollupProviderClientFactory campaign = new(options, new MissingCredentials(), capture);

        using MemoryEvaluationProviderClientFactory evaluation = new(new MissingCredentials(), capture, options);

        LongRestProviderSelection selection = new(settings.Providers[0], "fixture-model", 3, new());

        void ChangeRoute() => options.CurrentValue = settings with
        {
            LocalRuntimes = [settings.LocalRuntimes[0] with { ResourceGroup = "changed-group" }, settings.LocalRuntimes[1]],
        };

        Exception? failure;

        if (kind is "embedding" or "long-rest-embedding")
        {
            using EmbeddingGeneratorLease lease = kind == "embedding"
                ? await new EmbeddingGeneratorFactory(capture, options).ResolveGeneratorAsync(CancellationToken.None)
                : (await longRest.ResolveEmbeddingAsync(selection, CancellationToken.None)).Value;

            ChangeRoute();

            failure = await Record.ExceptionAsync(async () => await lease.Generator.GenerateAsync(["A source paragraph."]));
        }
        else
        {
            using ChatClientLease lease = kind switch
            {
                "long-rest-chat" => (await longRest.ResolveChatAsync(selection, CancellationToken.None)).Value,
                "campaign" => (await campaign.ResolveClientAsync(null, CancellationToken.None)).Value!,
                "memory-evaluation" => (await evaluation.ResolveChatAsync(settings.Providers[0], "fixture-model", CancellationToken.None)).Value,
                _ => await new ChatClientFactory(capture, options).ResolveClientAsync(null, CancellationToken.None),
            };

            ChangeRoute();

            failure = await Record.ExceptionAsync(async () =>
            {
                if (kind == "streaming")
                {
                    await foreach (ChatResponseUpdate _ in lease.ChatClient.GetStreamingResponseAsync([new(ChatRole.User, "A source paragraph.")])) { }
                }
                else await lease.ChatClient.GetResponseAsync([new(ChatRole.User, "A source paragraph.")]);
            });
        }

        Assert.Empty(capture.Bodies);

        Assert.NotNull(failure);
    }

    [Fact]
    public async Task Campaign_route_change_during_before_send_is_a_stale_refusal_without_transport()
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));

        ArcanumSettings settings = Settings();

        MutableTestOptionsMonitor<ArcanumSettings> options = new(settings);

        using Capture capture = new(false);

        using CampaignRollupProviderClientFactory factory = new(options, new MissingCredentials(), capture);

        using ChatClientLease lease = (await factory.ResolveClientAsync(null, deadline.Token)).Value!;

        ModelTokenEstimator estimator = new(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance));

        ModelCallExecutor executor = new(estimator, options);

        CampaignMaintenancePayload payload = new("Summarize the source.", "A bounded source paragraph.");

        ModelCallContext context = new(lease.Provider, lease.ResolvedModel, CampaignMaintenancePayload.MaxOutputTokens, 0,
            estimator.EstimateCampaignMaintenance(lease.Provider, lease.ResolvedModel, payload));

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task Before(CancellationToken token)
        {
            entered.TrySetResult();

            await release.Task.WaitAsync(token);
        }

        Task<Result<CampaignMaintenanceCallResult>> sending = executor.ExecuteCampaignMaintenanceAsync(lease.ChatClient, payload,
            UnrestrictedTurnBudget.Instance, Before, deadline.Token, context);

        await entered.Task.WaitAsync(deadline.Token);

        options.CurrentValue = settings with { LocalRuntimes = [] };

        release.TrySetResult();

        Result<CampaignMaintenanceCallResult> result = await sending.WaitAsync(deadline.Token);

        Assert.Empty(capture.Bodies);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, result.Error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unsent_unbound_chat_refuses_a_new_runtime_binding_after_credentials_yield(bool streaming)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));

        ArcanumSettings configured = Settings();

        ArcanumSettings initial = configured with { Providers = [configured.Providers[0] with { LocalRuntimeId = null }] };

        MutableTestOptionsMonitor<ArcanumSettings> options = new(initial);

        using Capture capture = new(streaming);

        HeldCredentials credentials = new();

        Task<ChatClientLease> resolving = new ChatClientFactory(capture, options, credentials).ResolveClientAsync(null, deadline.Token);

        await credentials.Entered.Task.WaitAsync(deadline.Token);

        options.CurrentValue = configured;

        using ProviderWorkCoordinator coordinator = new();

        using ProviderWorkCoordinator.ForegroundTurn foreground = coordinator.BeginForegroundTurn();

        await foreground.ReserveAsync(ProviderResourceKey.FromProvider(configured.Providers[0], configured)!.Value, deadline.Token);

        credentials.Release.TrySetResult();

        using ChatClientLease lease = await resolving.WaitAsync(deadline.Token);

        ModelCallExecutor executor = new(new ModelTokenEstimator(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance)), options, coordinator);

        await SendAsync(executor, lease, streaming, deadline.Token);

        Assert.Empty(capture.Bodies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_route_is_reserved_before_credentials_yield_after_runtime_reload_or_fallback(bool explicitProvider)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));

        ArcanumSettings initial = Settings();

        MutableTestOptionsMonitor<ArcanumSettings> options = new(initial);

        using Capture capture = new(false);

        using ProviderWorkCoordinator coordinator = new();

        using ProviderWorkCoordinator.ForegroundTurn foreground = coordinator.BeginForegroundTurn();

        await foreground.ReserveAsync(ProviderResourceKey.FromProvider(initial.Providers[0], initial)!.Value, deadline.Token);

        options.CurrentValue = explicitProvider
            ? initial with
            {
                Providers = [initial.Providers[0], initial.Providers[0] with { Name = "fallback", LocalRuntimeId = "replacement", Endpoint = "http://localhost:11435/v1" }],
                LocalRuntimes = [initial.LocalRuntimes[0], initial.LocalRuntimes[1] with { ManagementEndpoint = "http://localhost:11435", ResourceGroup = "changed-group" }],
            }
            : initial with { LocalRuntimes = [initial.LocalRuntimes[0] with { ResourceGroup = "changed-group" }, initial.LocalRuntimes[1]] };

        ProviderSettings selected = options.CurrentValue.Providers[explicitProvider ? 1 : 0];

        HeldCredentials credentials = new();

        ChatClientFactory factory = new(capture, options, credentials);

        Task<ChatClientLease> resolving;

        using (RetroDownfall.Arcanum.Core.Intelligence.ProviderWorkAmbient.Push(foreground))
        {
            resolving = explicitProvider
                ? factory.ResolveClientAsync(selected, "fixture-model", deadline.Token)
                : factory.ResolveClientAsync(null, deadline.Token);
        }

        Task<IDisposable>? background = null;

        try
        {
            await credentials.Entered.Task.WaitAsync(deadline.Token);

            background = coordinator.EnterCallAsync(ProviderResourceKey.FromProvider(selected, options.CurrentValue)!.Value,
                null, deadline.Token).AsTask();

            Assert.False(background.IsCompleted, "Maintenance acquired the actual selected runtime while foreground credential lookup was still pending.");

            credentials.Release.TrySetResult();

            using ChatClientLease lease = await resolving.WaitAsync(deadline.Token);

            Assert.False(background.IsCompleted);

            foreground.Dispose();

            (await background.WaitAsync(deadline.Token)).Dispose();

            Assert.Empty(capture.Bodies);
        }
        finally
        {
            credentials.Release.TrySetResult();

            foreground.Dispose();

            if (background is not null) (await background.WaitAsync(deadline.Token)).Dispose();

            using ChatClientLease lease = await resolving.WaitAsync(deadline.Token);
        }
    }

    [Theory]
    [InlineData(false, "endpoint")]
    [InlineData(true, "endpoint")]
    [InlineData(false, "binding")]
    [InlineData(true, "binding")]
    [InlineData(false, "group")]
    [InlineData(true, "group")]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    [InlineData(false, "unbound")]
    [InlineData(true, "unbound")]
    [InlineData(false, "unchanged")]
    [InlineData(true, "unchanged")]
    [InlineData(false, "case")]
    [InlineData(true, "case")]
    public async Task Queued_chat_revalidates_its_frozen_physical_route_before_transport(bool streaming, string change)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));

        ArcanumSettings initial = Settings();

        MutableTestOptionsMonitor<ArcanumSettings> options = new(initial);

        using Capture capture = new(streaming);

        using ProviderWorkCoordinator coordinator = new();

        using ProviderWorkCoordinator.ForegroundTurn foreground = coordinator.BeginForegroundTurn();

        await foreground.ReserveAsync(ProviderResourceKey.FromProvider(initial.Providers[0], initial)!.Value, deadline.Token);

        using ChatClientLease lease = await new ChatClientFactory(capture, options).ResolveClientAsync(null, deadline.Token);

        TaskCompletionSource waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);

        AsyncLocal<bool> observing = new();

        using MeterListener listener = ObserveWaiting(waiting, observing);

        ModelCallExecutor executor = new(new ModelTokenEstimator(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance)), options, coordinator);

        observing.Value = true;

        Task pending = SendAsync(executor, lease, streaming, deadline.Token);

        observing.Value = false;

        try
        {
            await waiting.Task.WaitAsync(deadline.Token);

            Assert.Empty(capture.Bodies);

            options.CurrentValue = change switch
            {
                "endpoint" => initial with { Providers = [initial.Providers[0] with { Endpoint = "http://localhost:11434/other/v1" }] },
                "binding" => initial with { Providers = [initial.Providers[0] with { LocalRuntimeId = "replacement" }] },
                "group" => initial with { LocalRuntimes = [initial.LocalRuntimes[0] with { ResourceGroup = "new-group" }, initial.LocalRuntimes[1]] },
                "missing" => initial with { LocalRuntimes = [] },
                "unbound" => initial with { Providers = [initial.Providers[0] with { LocalRuntimeId = null }] },
                "case" => initial with { Providers = [initial.Providers[0] with { LocalRuntimeId = "DESKTOP" }] },
                _ => initial,
            };

            foreground.Dispose();

            await pending.WaitAsync(deadline.Token);

            Assert.Equal(change is "unchanged" or "case" ? 1 : 0, capture.Bodies.Count);
        }
        finally
        {
            foreground.Dispose();

            await deadline.CancelAsync();

            try { await pending; }
            catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("embedding")]
    [InlineData("long-rest-chat")]
    [InlineData("long-rest-embedding")]
    [InlineData("campaign")]
    [InlineData("memory-evaluation")]
    public async Task Runtime_route_is_frozen_before_asynchronous_credentials_yield(string kind)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));

        ArcanumSettings initial = Settings();

        MutableTestOptionsMonitor<ArcanumSettings> options = new(initial);

        using Capture capture = new(false);

        HeldCredentials credentials = new();

        bool embedding = kind is "embedding" or "long-rest-embedding";

        using LongRestProviderClientFactory longRest = new(credentials, capture, NullLogger<LongRestProviderClientFactory>.Instance, options);

        using CampaignRollupProviderClientFactory campaign = new(options, credentials, capture);

        using MemoryEvaluationProviderClientFactory memoryEvaluation = new(credentials, capture, options);

        LongRestProviderSelection selection = new(initial.Providers[0], "fixture-model", 3, new());

        async Task<EmbeddingGeneratorLease> ResolveEmbeddingAsync() => kind == "long-rest-embedding"
            ? (await longRest.ResolveEmbeddingAsync(selection, deadline.Token)).Value
            : await new EmbeddingGeneratorFactory(capture, options, credentials).ResolveGeneratorAsync(deadline.Token);

        async Task<ChatClientLease> ResolveChatAsync() => kind switch
        {
            "long-rest-chat" => (await longRest.ResolveChatAsync(selection, deadline.Token)).Value,
            "campaign" => (await campaign.ResolveClientAsync(null, deadline.Token)).Value!,
            "memory-evaluation" => (await memoryEvaluation.ResolveChatAsync(initial.Providers[0], "fixture-model", deadline.Token)).Value,
            _ => await new ChatClientFactory(capture, options, credentials).ResolveClientAsync(null, deadline.Token),
        };

        Task<EmbeddingGeneratorLease>? embeddingResolution = embedding ? ResolveEmbeddingAsync() : null;

        Task<ChatClientLease>? chatResolution = embedding ? null : ResolveChatAsync();

        await credentials.Entered.Task.WaitAsync(deadline.Token);

        initial.Providers[0].Endpoint = "http://localhost:11435/changed/v1";

        initial.LocalRuntimes[0].ResourceGroup = "new-group";

        credentials.Release.TrySetResult();

        if (embedding)
        {
            using EmbeddingGeneratorLease lease = await embeddingResolution!.WaitAsync(deadline.Token);

            Assert.NotNull(lease.Endpoint);

            Assert.Equal("http://localhost:11434/v1", lease.Endpoint.AbsoluteUri.TrimEnd('/'));

            Assert.Equal(new ProviderResourceKey("runtime-group", "ORIGINAL-GROUP", 0), lease.Resource);

            options.CurrentValue = Settings();

            GeneratedEmbeddings<Embedding<float>> vectors = await lease.Generator.GenerateAsync(["A frozen embedding route."], options: null, deadline.Token);

            Assert.Single(vectors);

            Assert.Equal("http://localhost:11434/v1/embeddings", Assert.Single(capture.Uris).AbsoluteUri);
        }
        else
        {
            using ChatClientLease lease = await chatResolution!.WaitAsync(deadline.Token);

            Assert.Equal("http://localhost:11434/v1", lease.Provider.Endpoint.TrimEnd('/'));

            using ProviderWorkCoordinator coordinator = new();

            using ProviderWorkCoordinator.ForegroundTurn foreground = coordinator.BeginForegroundTurn();

            await foreground.ReserveAsync(new("runtime-group", "ORIGINAL-GROUP", 0), deadline.Token);

            TaskCompletionSource waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);

            AsyncLocal<bool> observing = new();

            using MeterListener listener = ObserveWaiting(waiting, observing);

            ModelCallExecutor executor = new(new ModelTokenEstimator(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance)), options, coordinator);

            observing.Value = true;

            Task send = SendAsync(executor, lease, false, deadline.Token);

            observing.Value = false;

            try
            {
                Task first = await Task.WhenAny(waiting.Task, send).WaitAsync(deadline.Token);

                Assert.Same(waiting.Task, first);

                Assert.Empty(capture.Bodies);
            }
            finally
            {
                foreground.Dispose();

                await send.WaitAsync(deadline.Token);
            }

            Assert.Empty(capture.Bodies);
        }
    }

    private static async Task SendAsync(ModelCallExecutor executor, ChatClientLease lease, bool streaming, CancellationToken token)
    {
        ModelCallContext context = new(lease.Provider, lease.ResolvedModel, 0, 0);

        try
        {
            if (streaming)
            {
                await foreach (var _ in executor.ExecuteStreamingAsync(lease.ChatClient, [new(ChatRole.User, "Queued corpus summary.")], new(),
                    UnrestrictedTurnBudget.Instance, ModelCallPurpose.MainInference, token, context)) { }
            }
            else
            {
                await executor.ExecuteBufferedAsync(lease.ChatClient, [new(ChatRole.User, "Queued corpus summary.")], new(),
                    UnrestrictedTurnBudget.Instance, ModelCallPurpose.MainInference, token, context);
            }
        }
        catch (InvalidOperationException) { }
    }

    private static MeterListener ObserveWaiting(TaskCompletionSource waiting, AsyncLocal<bool> observing)
    {
        MeterListener listener = new();

        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (instrument.Meter.Name == "RetroDownfall.Arcanum.ProviderWork" && instrument.Name == "arcanum.provider_work.waiting") observer.EnableMeasurementEvents(instrument);
        };

        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            if (!observing.Value) return;

            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "kind" && Equals(tag.Value, "background")) waiting.TrySetResult();
            }
        });

        listener.Start();

        return listener;
    }

    private static ArcanumSettings Settings() => new()
    {
        DefaultModel = "fixture-model",
        Features = new() { Embeddings = true },
        Integrations = new() { Embeddings = new() { Provider = "fixture", Model = "fixture-model" } },
        Providers = [new() { Name = "fixture", Models = ["fixture-model"], Endpoint = "http://localhost:11434/v1", LocalRuntimeId = "desktop" }],
        LocalRuntimes =
        [
            new() { Id = "desktop", Adapter = "ollama", ManagementEndpoint = "http://localhost:11434", ResourceGroup = "original-group" },
            new() { Id = "replacement", Adapter = "ollama", ManagementEndpoint = "http://localhost:11434", ResourceGroup = "original-group" },
        ],
    };

    private sealed class HeldCredentials : RetroDownfall.Arcanum.Core.Security.IProviderApiKeyResolver
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();

            await Release.Task.WaitAsync(cancellationToken);

            return null;
        }
    }

    private sealed class MissingCredentials : RetroDownfall.Arcanum.Core.Security.IProviderApiKeyResolver
    {
        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class Capture(bool streaming, bool reportUsage = false) : HttpMessageHandler, IHttpClientFactory
    {
        internal List<string> Bodies { get; } = [];

        internal List<Uri> Uris { get; } = [];

        internal Exception? Failure { get; set; }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));

            Uris.Add(request.RequestUri!);

            if (Failure is { } failure) throw failure;

            string body = request.RequestUri!.AbsolutePath.EndsWith("/embeddings", StringComparison.Ordinal)
                ? "{\"object\":\"list\",\"data\":[{\"object\":\"embedding\",\"embedding\":[0.1,0.2,0.3],\"index\":0}],\"model\":\"fixture-model\",\"usage\":{\"prompt_tokens\":1,\"total_tokens\":1}}"
                : streaming
                ? "data: {\"id\":\"fixture\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"
                : "{\"id\":\"fixture\",\"object\":\"chat.completion\",\"created\":0,\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}";

            if (reportUsage && !streaming) body = body[..^1] + ",\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":1,\"total_tokens\":4}}";

            return new(HttpStatusCode.OK) { Content = ProviderSdkTestTransport.Content(body, streaming ? "text/event-stream" : "application/json") };
        }
    }
}
