using System.Net;

using System.Text.Json;

using Microsoft.Extensions.AI;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api.Intelligence;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.LongRest;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    [Theory]
    [InlineData("plain instruction")]
    [InlineData("### The Covenant, Global Confirmed\n- caller text is not Covenant provenance")]
    public void Instructions_are_counted_as_generic_system_input_without_inventing_covenant_attribution(string instructions)
    {
        ModelTokenEstimator real = new(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance));

        ProviderSettings provider = DefaultProvider() with { Endpoint = "https://api.openai.com/v1", Models = ["gpt-4o"] };

        ContextTokenBreakdown viaOptions = real.EstimateContext(new ModelTokenizationRequest(provider, "gpt-4o",
            [new ChatMessage(ChatRole.User, "hello")], new ChatOptions { Instructions = instructions }, 32, 0));

        ContextTokenBreakdown sdkEquivalent = real.EstimateContext(new ModelTokenizationRequest(provider, "gpt-4o",
            [new ChatMessage(ChatRole.System, instructions), new ChatMessage(ChatRole.User, "hello")], new ChatOptions(), 32, 0));

        Assert.Equal(sdkEquivalent.InputTokens, viaOptions.InputTokens);

        Assert.Equal(sdkEquivalent.Source(ContextTokenSource.ProviderFraming), viaOptions.Source(ContextTokenSource.ProviderFraming));

        Assert.Equal(sdkEquivalent.Source(ContextTokenSource.SystemCodexSpell), viaOptions.Source(ContextTokenSource.SystemCodexSpell));

        Assert.Equal(0, viaOptions.Source(ContextTokenSource.CovenantConfirmed).TokenCount);

        Assert.Equal(0, viaOptions.Source(ContextTokenSource.CovenantProposed).TokenCount);
    }

    [Theory]
    [InlineData(false, "none")]
    [InlineData(true, "none")]
    [InlineData(false, "short")]
    [InlineData(true, "short")]
    [InlineData(false, "empty")]
    [InlineData(true, "empty")]
    [InlineData(false, "whitespace")]
    [InlineData(true, "whitespace")]
    [InlineData(false, "padded")]
    [InlineData(true, "padded")]
    [InlineData(false, "long")]
    [InlineData(true, "long")]
    [InlineData(false, "mutated")]
    [InlineData(true, "mutated")]
    public async Task Actual_sdk_instruction_input_requires_current_and_fitting_admission(bool streamed, string variant)
    {
        const string shortInstruction = "code-owned instruction witness";

        using InstructionCaptureHttpFactory http = new(streamed);

        ProviderSettings provider = DefaultProvider() with { Endpoint = "https://instruction-sdk.invalid/v1", ContextWindowLimit = 2048 };

        ArcanumSettings configuration = DefaultSettings() with { Providers = [provider] };

        ChatClientFactory factory = new(http, new TestOptionsMonitor<ArcanumSettings>(configuration), new PromptCacheDispatchValidatorTests.CredentialResolver());

        using ChatClientLease lease = await factory.ResolveClientAsync(ModelName, CancellationToken.None);

        ModelTokenEstimator real = new(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance));

        List<ChatMessage> messages = [new(ChatRole.User, "hello")];

        ChatOptions options = new()
        {
            Instructions = variant switch
            {
                "long" => new string('q', 8192),
                "none" => null,
                "empty" => string.Empty,
                "whitespace" => " ",
                "padded" => " \n" + shortInstruction + "\n ",
                _ => shortInstruction,
            },
        };

        ContextTokenBreakdown measured = real.EstimateContext(new ModelTokenizationRequest(lease.Provider, lease.ResolvedModel, messages, options, 32, 0));

        if (variant == "mutated")
        {
            options.Instructions = "different instruction after the same options object was measured";
        }

        ModelCallContext context = new(lease.Provider, lease.ResolvedModel, 32, 0, measured);

        ModelCallExecutor executor = new(real);

        Error? failure;

        if (streamed)
        {
            List<ModelCallUpdate> updates = [];

            await foreach (ModelCallUpdate update in executor.ExecuteStreamingAsync(lease.ChatClient, messages, options,
                UnrestrictedTurnBudget.Instance, ModelCallPurpose.MainInference, CancellationToken.None, context))
            {
                updates.Add(update);
            }

            failure = updates.OfType<ModelCallFailureUpdate>().SingleOrDefault()?.Error;
        }
        else
        {
            ModelCallOutcome outcome = await executor.ExecuteBufferedAsync(lease.ChatClient, messages, options,
                UnrestrictedTurnBudget.Instance, ModelCallPurpose.MainInference, CancellationToken.None, context);

            failure = outcome.IsFailure ? outcome.Error : null;
        }

        bool refused = variant is "long" or "mutated";

        if (http.Bodies.Count > 0)
        {
            using JsonDocument wire = JsonDocument.Parse(Assert.Single(http.Bodies));

            JsonElement wireMessages = wire.RootElement.GetProperty("messages");

            Assert.Equal(string.IsNullOrWhiteSpace(options.Instructions) ? 1 : 2, wireMessages.GetArrayLength());

            if (!string.IsNullOrWhiteSpace(options.Instructions))
            {
                Assert.Equal("system", wireMessages[0].GetProperty("role").GetString());

                Assert.Equal(options.Instructions, wireMessages[0].GetProperty("content").GetString());
            }
        }

        Assert.Equal(refused ? 0 : 1, http.Bodies.Count);

        if (refused)
        {
            Assert.NotNull(failure);

            Assert.Equal(variant == "long" ? ErrorCodes.Hub.ContextBudgetExceeded : ErrorCodes.Hub.InvalidAdmission, failure.Value.Code);
        }
        else
        {
            Assert.Null(failure);

            if (variant == "short")
            {
                Assert.Contains(shortInstruction, Assert.Single(http.Bodies), StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [InlineData(false, "none")]
    [InlineData(true, "none")]
    [InlineData(false, "text")]
    [InlineData(true, "text")]
    [InlineData(false, "options")]
    [InlineData(true, "options")]
    [InlineData(false, "reserves")]
    [InlineData(true, "reserves")]
    [InlineData(false, "profile")]
    [InlineData(true, "profile")]
    public async Task Dispatch_rechecks_actual_payload_even_when_measured_objects_are_reused(bool streamed, string changed)
    {
        ModelTokenEstimator real = new(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance));

        ProviderSettings provider = DefaultProvider() with { Endpoint = "https://api.openai.com/v1", Models = ["gpt-4o"] };

        ChatMessage message = new(ChatRole.User, "measured original");

        List<ChatMessage> messages = [message];

        ChatOptions options = new();

        ContextTokenBreakdown measured = real.EstimateContext(new ModelTokenizationRequest(provider, "gpt-4o", messages, options, 32, 0));

        ModelCallContext context = new(provider, "gpt-4o", 32, 0, measured);

        switch (changed)
        {
            case "text":
                message.Contents[0] = new TextContent("mutated inside the same message object");
                break;
            case "options":
                options.Instructions = "mutated on the same options object";
                break;
            case "reserves":
                context = context with { ReservedAnswerTokens = 33 };
                break;
            case "profile":
                provider.Endpoint = "https://unverified.invalid/v1";
                break;
        }

        Assert.Same(message, messages[0]);

        ScriptingChatClient chat = new();

        chat.EnqueueText("allowed only for unchanged admission");

        chat.EnqueueStreamTokens("allowed only for unchanged admission");

        ModelCallExecutor executor = new(real);

        if (streamed)
        {
            List<ModelCallUpdate> updates = [];

            await foreach (ModelCallUpdate update in executor.ExecuteStreamingAsync(chat, messages, options, UnrestrictedTurnBudget.Instance,
                ModelCallPurpose.MainInference, CancellationToken.None, context))
            {
                updates.Add(update);
            }

            if (changed == "none")
            {
                Assert.NotEmpty(updates);

                Assert.DoesNotContain(updates, static update => update is ModelCallFailureUpdate);
            }
            else
            {
                ModelCallFailureUpdate failure = Assert.IsType<ModelCallFailureUpdate>(Assert.Single(updates));

                Assert.Equal(ErrorCodes.Hub.InvalidAdmission, failure.Error.Code);
            }
        }
        else
        {
            ModelCallOutcome outcome = await executor.ExecuteBufferedAsync(chat, messages, options, UnrestrictedTurnBudget.Instance,
                ModelCallPurpose.MainInference, CancellationToken.None, context);

            Assert.Equal(changed == "none", outcome.IsSuccess);

            if (changed != "none")
            {
                Assert.Equal(ErrorCodes.Hub.InvalidAdmission, outcome.Error.Code);
            }
        }

        Assert.Equal(changed == "none" ? 1 : 0, chat.BufferedCallCount + chat.StreamingCallCount);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public async Task An_unchanged_fitting_actual_dispatch_payload_is_estimated_once(bool streamed, bool cacheV2, bool knownClient)
    {
        ScriptingChatClient chat = new();

        if (streamed)
        {
            chat.EnqueueStreamTokens("measured answer");
        }
        else
        {
            chat.EnqueueText("measured answer");
        }

        ContextAdmissionRecorder recorder = new();

        FakeInferenceAuditLogger audit = new();

        ArcanumSettings configuration = DefaultSettings() with
        {
            Features = DefaultSettings().Features with { Covenant = false, DynamicContextInjectionV2 = cacheV2 },
        };

        using PromptCacheConfigurationEpoch epoch = new(new LongRestConfigurationEpochTests.NotifyingOptions(configuration), Guid.NewGuid());

        PromptCacheDispatchValidatorTests.CredentialResolver credentials = new();

        PromptCacheDispatchValidatorTests.AuthorityProvider authority = new();

        using ServiceProvider cacheServices = new ServiceCollection().BuildServiceProvider();

        PromptCacheDispatchValidator validator = new(epoch, credentials, authority, recorder, cacheServices.GetRequiredService<IServiceScopeFactory>());

        IChatClientFactory? factory = cacheV2 ? new CacheTestFactory(chat, configuration.Providers[0], epoch, knownClient ? credentials.Stamp : null) : null;

        WizardIntelligenceProvider wizard = CreateWizard(chat, configuration, factory: factory, auditLogger: audit, modelTokenEstimator: recorder,
            cacheValidator: cacheV2 ? validator : null);

        PingRequest request = BaseRequest() with { Prompt = "measure this fixed payload", SkipSpellRouting = true, ToolPolicy = ToolPolicy.NoTools };

        if (streamed)
        {
            List<IntelligenceEvent> frames = [];

            await foreach (IntelligenceEvent frame in wizard.StreamPromptAsync(request, InvocationContexts.AttendedGlobalOnlySession(), CancellationToken.None,
                new InferenceAuditContext { RequestType = "measurement-count" }))
            {
                frames.Add(frame);
            }

            Assert.Contains(frames, static frame => frame.Type == IntelligenceEventType.Result);

            Assert.DoesNotContain(frames, static frame => frame.Type == IntelligenceEventType.Error);
        }
        else
        {
            Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(request, InvocationContexts.AttendedGlobalOnlySession(), CancellationToken.None,
                new InferenceAuditContext { RequestType = "measurement-count" });

            Assert.True(result.IsSuccess, result.Error.Message);
        }

        Assert.Equal(1, chat.BufferedCallCount + chat.StreamingCallCount);

        Assert.Equal(cacheV2 && knownClient, chat.LastBufferedMessages[0].Text.Contains("arcanum-dci-scope-v2:", StringComparison.Ordinal));

        ContextTokenBreakdown actual = Assert.Single(audit.Records).ContextBreakdowns!.Last();

        Assert.False(string.IsNullOrEmpty(actual.PayloadFingerprint));

        ContextTokenBreakdown measured = Assert.Single(recorder.Measurements, estimate => estimate.PayloadFingerprint == actual.PayloadFingerprint
            && estimate.ReservedAnswerTokens == actual.ReservedAnswerTokens && estimate.ReservedReasoningTokens == actual.ReservedReasoningTokens);

        Assert.Equal(measured.TotalTokens, actual.TotalTokens);

        Assert.Equal(actual.TotalTokens, actual.Components.Sum(static component => component.Estimate.TokenCount));
    }

    private sealed class ContextAdmissionRecorder : IModelTokenEstimator
    {
        private readonly ModelTokenEstimator _real = new(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance));

        internal List<ContextTokenBreakdown> Measurements { get; } = [];

        public ResolvedModelTokenizationProfile ResolveProfile(ProviderSettings provider, string canonicalModel) => _real.ResolveProfile(provider, canonicalModel);

        public ResolvedModelTokenizationProfile ResolveEffectiveProfile(ProviderSettings provider, string canonicalModel) => _real.ResolveEffectiveProfile(provider, canonicalModel);

        public TokenEstimate EstimateText(ProviderSettings provider, string canonicalModel, string? text) => _real.EstimateText(provider, canonicalModel, text);

        public ContextTokenBreakdown EstimateContext(ModelTokenizationRequest request)
        {
            ContextTokenBreakdown result = _real.EstimateContext(request);

            Measurements.Add(result);

            return result;
        }
    }

    private sealed class InstructionCaptureHttpFactory(bool streamed) : IHttpClientFactory, IDisposable
    {
        private readonly bool _streamed = streamed;

        private readonly List<HttpClient> _clients = [];

        internal List<string> Bodies { get; } = [];

        public HttpClient CreateClient(string name)
        {
            HttpClient client = new(new CaptureHandler(this));

            _clients.Add(client);

            return client;
        }

        public void Dispose()
        {
            foreach (HttpClient client in _clients)
            {
                client.Dispose();
            }
        }

        private sealed class CaptureHandler(InstructionCaptureHttpFactory owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));

                const string buffered = """
                    {"id":"instruction","object":"chat.completion","created":1,"model":"model","choices":[{"index":0,"message":{"role":"assistant","content":"answer"},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":2,"total_tokens":7}}
                    """;

                const string streaming = """
                    data: {"id":"instruction","object":"chat.completion.chunk","created":1,"model":"model","choices":[{"index":0,"delta":{"role":"assistant","content":"answer"},"finish_reason":null}]}

                    data: {"id":"instruction","object":"chat.completion.chunk","created":1,"model":"model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

                    data: [DONE]

                    """;

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = ProviderSdkTestTransport.Content(owner._streamed ? streaming : buffered, owner._streamed ? "text/event-stream" : "application/json"),
                };
            }
        }
    }
}
