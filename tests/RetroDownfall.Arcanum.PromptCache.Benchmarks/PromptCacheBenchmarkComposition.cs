using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.PromptCache.Benchmarks;

public sealed record PromptCacheFixtureProbe(bool Optimized, bool WireValid, long? CachedTokens, int ToolCount, bool GeneratedObserved, bool AnswerObserved, string TokenLimitWireField = "unknown");

public static class PromptCacheBenchmarkHarness
{
    public static async Task<PromptCacheFixtureProbe> ProbeFixtureAsync(bool streaming, CancellationToken ct)
    {
        using var composition = new PromptCacheBenchmarkComposition(true, null);

        var sample = await composition.CallAsync(new("digits", "ASCII digits only.", "Return OK."), 0, 0, "warm", 0, streaming, ct);

        return new(composition.LastOptimized, sample.WireValid, sample.CachedTokens, composition.Observer.ToolCount, sample.FirstGeneratedMs.HasValue, sample.FirstAnswerMs.HasValue, composition.Observer.TokenLimitWireField);
    }

    public static async Task<PromptCacheInvalidation[]> CheckInvalidationsAsync(CancellationToken ct)
    {
        List<PromptCacheInvalidation> observations = [];

        foreach (string mutation in new[] { "prefix", "suffix", "toolRemoved", "toolOrder", "endpoint", "model", "tokenizer", "configuration", "outputLimitDialect", "credentialObservedAba", "campaignBinding", "campaignGeneration", "physicalRoot", "profile", "reset", "restore", "protectedState", "hostTools", "featureOffOn", "restart" })
        {
            using var c = new PromptCacheBenchmarkComposition(true, null);

            if (mutation is "profile" or "hostTools") c.Authority.PublishPublicFixtureProfile("public-profile-a", CovenantHostToolsState.Clean);

            using ChatClientLease lease = await c.Factory.ResolveClientAsync("gemma4:e4b", ct);

            ChatOptions options = c.Options();

            var scope = mutation is "campaignBinding" or "campaignGeneration" or "physicalRoot" ? c.Campaign.Scope : CanonicalCampaignContext.GlobalOnly;

            var ordinary = PromptCacheBenchmarkComposition.Document(new("digits", "ASCII digits only.", "Return OK."));

            var prepared = (await c.Validator.PrepareAsync(lease, ordinary, options, scope, ContentSensitivity.None, ct))!;

            List<ChatMessage> messages = [new(ChatRole.System, prepared.Build.Prompt), new(ChatRole.User, "Return OK.")];

            var context = new ModelCallContext(lease.Provider, lease.ResolvedModel, 32, 0, PromptCachePlan: prepared.CreatePlan(options, c.Estimator), PromptCacheBinding: prepared.Bind(messages, options), SystemPromptAttribution: prepared.Build.Attribution);

            bool markerChanged = false;

            switch (mutation)
            {
                case "prefix": messages[0] = new(ChatRole.System, "Changed public preamble." + prepared.Build.Prompt[1..]); break;
                case "suffix":
                    var next = await c.Validator.PrepareAsync(lease, PromptCacheBenchmarkComposition.Document(new("digits", "Changed public facts.", "Return OK.")), options, scope, ContentSensitivity.None, ct);
                    markerChanged = next!.ScopeMarkerDigest == prepared.ScopeMarkerDigest && PromptCachePlanner.CreatePrefixDecision(next.Build, options, next.ScopeMarkerDigest).PrefixDigest == PromptCachePlanner.CreatePrefixDecision(prepared.Build, options, prepared.ScopeMarkerDigest).PrefixDigest;
                    break;
                case "toolRemoved": options.Tools = [options.Tools![0]]; break;
                case "toolOrder": options.Tools = options.Tools!.Reverse().ToList(); break;
                case "endpoint": c.Settings.Providers[0].Endpoint = "http://127.0.0.1:11435/v1"; break;
                case "outputLimitDialect": c.Settings.Providers[0].OutputTokenLimitWireDialect = OutputTokenLimitWireDialect.MaxCompletionTokens; break;
                case "model": context = context with { Model = "other-model" }; break;
                case "tokenizer": c.Estimator.Changed = true; break;
                case "credentialObservedAba":
                    c.CredentialStore.Value = "public-fixture-b";
                    _ = await c.Credentials.ResolveWithIdentityAsync(c.Settings.Providers[0], ct);
                    c.CredentialStore.Value = null;
                    break;
                case "campaignBinding": c.Campaign.Available = null; break;
                case "campaignGeneration": c.Campaign.Available = 2; break;
                case "physicalRoot": c.Campaign.Root = c.Campaign.Root with { Revision = 2, PhysicalIdentityDigest = new CovenantDigest(Enumerable.Repeat((byte)1, 32).ToArray()) }; break;
                case "profile": c.Authority.PublishPublicFixtureProfile("public-profile-b", CovenantHostToolsState.Clean); break;
                case "hostTools": c.Authority.PublishPublicFixtureProfile("public-profile-a", CovenantHostToolsState.PendingHostToolsTaint); break;
                case "featureOffOn":
                    c.Settings.Features.DynamicContextInjectionV2 = false;
                    _ = c.Epoch.Capture();
                    c.Settings.Features.DynamicContextInjectionV2 = true;
                    break;
                case "restart":
                    using (var restarted = new PromptCacheBenchmarkComposition(true, null))
                    using (var restartedLease = await restarted.Factory.ResolveClientAsync("gemma4:e4b", ct))
                    {
                        var successor = await restarted.Validator.PrepareAsync(restartedLease, ordinary, restarted.Options(), scope, ContentSensitivity.None, ct);
                        markerChanged = successor!.ScopeMarkerDigest != prepared.ScopeMarkerDigest;
                    }
                    c.Authority.Advance(CovenantHealthTransition.Bootstrap);
                    break;
                case "reset": c.Authority.Advance(CovenantHealthTransition.Reset); break;
                case "restore": c.Authority.Advance(CovenantHealthTransition.Restore); break;
                case "protectedState": c.Authority.Advance(CovenantHealthTransition.FeatureConfiguration); break;
                default: c.Settings.Providers[0].ContextWindowLimit++; break;
            }

            Result<PromptCacheDispatchStatus> result = await c.Validator.ValidateAsync(lease.ChatClient, messages, options, context, ct);

            bool pass = result.IsSuccess && result.Value == (mutation == "suffix" ? PromptCacheDispatchStatus.Current : PromptCacheDispatchStatus.ReplanRequired);

            if (mutation is "suffix" or "restart") pass &= markerChanged;

            string basis = mutation is "campaignBinding" or "campaignGeneration" or "physicalRoot" or "profile" or "reset" or "restore" or "protectedState" or "hostTools" or "tokenizer" ? "simulatedLifecycle" : "productionComparison";

            string changedFact = mutation switch
            {
                "physicalRoot" => "canonicalRoot.physicalIdentityAndRevision",
                "profile" => "authority.installationIdentity",
                "hostTools" => "authority.hostToolsStateAndTransition",
                _ => mutation,
            };

            observations.Add(new(mutation, basis, pass, result.IsSuccess ? result.Value.ToString() : "Refused", null, changedFact));
        }

        return observations.ToArray();
    }

    public static async Task<bool> CheckActualCredentialAbaAsync(CancellationToken ct)
    {
        // Only this process-local isolated fixture writes an encrypted mirror. No user store is read.
        string directory = Directory.CreateTempSubdirectory("arcanum-prompt-cache-credential-").FullName;

        string[] names = ["ARCANUM_TEST_HOME", "ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT"];

        string?[] previous = names.Select(Environment.GetEnvironmentVariable).ToArray();

        try
        {
            Environment.SetEnvironmentVariable(names[0], directory);
            Environment.SetEnvironmentVariable(names[1], "Testing");
            Environment.SetEnvironmentVariable(names[2], "Testing");

            using ProviderCredentialStore store = new(new BenchmarkOsStore(), new EphemeralDataProtectionProvider());

            var provider = new ProviderSettings { Name = "prompt-cache-fixture-aba", CredentialEnvironmentVariable = "ARCANUM_PROMPT_CACHE_BENCHMARK_KEY" };

            if (Environment.GetEnvironmentVariable(provider.CredentialEnvironmentVariable) is not null) throw new InvalidDataException("unexpectedBenchmarkCredential");

            var resolver = new ProviderApiKeyResolver(store);

            await store.SaveApiKeyAsync(provider.Name, "public-fixture-a", ct);

            var first = await resolver.ResolveWithIdentityAsync(provider, ct);

            await store.SaveApiKeyAsync(provider.Name, "public-fixture-b", ct);
            await store.SaveApiKeyAsync(provider.Name, "public-fixture-a", ct);

            return first.Stamp is not null && !await resolver.IsCurrentAsync(provider, first.Stamp, ct);
        }
        finally
        {
            for (int i = 0; i < names.Length; i++) Environment.SetEnvironmentVariable(names[i], previous[i]);

            Directory.Delete(directory, true);
        }
    }

    private sealed class BenchmarkOsStore : IOsCredentialStore
    {
        private string? _value;

        public bool IsAvailable => true;

        public OsCredentialStoreResult TryGet(string service, string account) => _value is null ? OsCredentialStoreResult.NotFound() : OsCredentialStoreResult.Ok(_value);

        public OsCredentialStoreResult Set(string service, string account, string secret) { _value = secret; return OsCredentialStoreResult.Ok(string.Empty); }

        public OsCredentialStoreResult Delete(string service, string account) { _value = null; return OsCredentialStoreResult.Ok(string.Empty); }
    }
}

internal sealed class PromptCacheBenchmarkComposition : IDisposable
{
    private readonly bool _enabled;

    private readonly ServiceProvider _services;

    private readonly BenchmarkOptions _monitor;

    internal PromptCacheBenchmarkComposition(bool enabled, string? endpoint, string model = "gemma4:e4b")
    {
        _enabled = enabled;
        Settings = new() { DefaultModel = model, Features = new() { DynamicContextInjectionV2 = enabled }, Providers = [new() { Name = "prompt-cache-local", Type = AiProviderKind.OpenAICompatible, Endpoint = endpoint ?? "http://127.0.0.1:11434/v1", CredentialEnvironmentVariable = "ARCANUM_PROMPT_CACHE_BENCHMARK_KEY", Models = [model], ContextWindowLimit = 8192, OutputTokenLimitWireDialect = OutputTokenLimitWireDialect.MaxTokens }] };

        if (Environment.GetEnvironmentVariable("ARCANUM_PROMPT_CACHE_BENCHMARK_KEY") is not null) throw new InvalidDataException("unexpectedBenchmarkCredential");

        _monitor = new(Settings);
        Epoch = new(_monitor, Guid.NewGuid());
        CredentialStore = new();
        Credentials = new(CredentialStore);
        Estimator = new();
        Authority = new();
        Campaign = new();
        _services = new ServiceCollection().AddSingleton<ICampaignAvailabilityReader>(Campaign).AddSingleton<ICampaignPathIdentityReader>(Campaign).BuildServiceProvider();
        Validator = new(Epoch, Credentials, Authority, Estimator, _services.GetRequiredService<IServiceScopeFactory>());
        Observer = new(endpoint is null);
        Factory = new(new BenchmarkHttpClientFactory(Observer), _monitor, Credentials, promptCacheConfigurationEpoch: Epoch);
        Executor = new(Estimator, _monitor, NullLogger<ModelCallExecutor>.Instance, Validator);
    }

    internal ArcanumSettings Settings { get; }
    internal PromptCacheConfigurationEpoch Epoch { get; }
    internal BenchmarkCredentialStore CredentialStore { get; }
    internal ProviderApiKeyResolver Credentials { get; }
    internal BenchmarkEstimator Estimator { get; }
    internal BenchmarkAuthority Authority { get; }
    internal BenchmarkCampaign Campaign { get; }
    internal PromptCacheDispatchValidator Validator { get; }
    internal ChatClientFactory Factory { get; }
    internal ModelCallExecutor Executor { get; }
    internal BenchmarkWireObserver Observer { get; }
    internal bool LastOptimized { get; private set; }

    internal IList<AITool> Tools { get; set; } = [new ArcanumLocalTimeTool(), new ArcanumSystemInfoTool()];

    internal ChatOptions Options() => new() { ModelId = Settings.DefaultModel, MaxOutputTokens = 32, Temperature = 0, Seed = 103, ToolMode = ChatToolMode.None, Tools = Tools.ToList(), Reasoning = new() { Effort = ReasoningEffort.None, Output = ReasoningOutput.None } };

    internal static SystemPromptDocument Document(PromptCacheCorpusCase item, bool terminalFormatting = true) => SystemPromptBuilder.BuildDocument(new PingRequest(item.Question, CliTerminalFormatting: terminalFormatting), null, semanticContext: [new SemanticContextChunk("public-function.txt", 0, 1, 1, item.Facts)]);

    internal async Task<PromptCacheBenchmarkSample> CallAsync(PromptCacheCorpusCase item, int pair, int order, string phase, int ordinal, bool streaming, CancellationToken ct, bool terminalFormatting = true)
    {
        long started = Stopwatch.GetTimestamp();

        using ChatClientLease lease = await Factory.ResolveClientAsync(Settings.DefaultModel, ct);

        ChatOptions options = Options();

        var ordinary = Document(item, terminalFormatting);

        var prepared = _enabled ? await Validator.PrepareAsync(lease, ordinary, options, CanonicalCampaignContext.GlobalOnly, ContentSensitivity.None, ct) : null;

        var build = prepared?.Build ?? ordinary.BuildResult();

        List<ChatMessage> messages = [new(ChatRole.System, build.Prompt), new(ChatRole.User, item.Question)];

        if (prepared is not null && !prepared.CanBind(messages, options)) throw new InvalidDataException("candidateBindingUnavailable");

        LastOptimized = prepared?.Optimized == true;

        var decision = PromptCachePlanner.CreatePrefixDecision(build, options, prepared?.ScopeMarkerDigest ?? string.Empty);

        var plan = prepared?.CreatePlan(options, Estimator);

        var context = new ModelCallContext(lease.Provider, lease.ResolvedModel, 32, 0, PromptCachePlan: plan, PromptCacheBinding: prepared?.Bind(messages, options), SystemPromptAttribution: build.Attribution);

        Observer.Reset(_enabled, options.Tools!, build.Prompt, lease.ResolvedModel);

        UsageDetails? usage = null;

        double? generated = null;

        double? answer = null;

        string finish = "unknown";

        if (streaming)
        {
            await foreach (var update in Executor.ExecuteStreamingAsync(lease.ChatClient, messages, options, UnrestrictedTurnBudget.Instance, ModelCallPurpose.MainInference, ct, context))
            {
                switch (update)
                {
                    case ModelCallFailureUpdate: throw new InvalidDataException("executorRefused");
                    case ModelCallUsageUpdate u: usage = u.Usage ?? usage; break;
                    case ModelCallResponseUpdate raw:
                        foreach (AIContent content in raw.Update.Contents)
                        {
                            if (content is FunctionCallContent) throw new InvalidDataException("unexpectedToolOutput");

                            if (content is TextReasoningContent { Text.Length: > 0 } || content is TextContent { Text.Length: > 0 }) generated ??= Stopwatch.GetElapsedTime(started).TotalMilliseconds;

                            if (content is TextContent { Text.Length: > 0 }) answer ??= Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        }

                        if (raw.Update.FinishReason is { } reason) finish = Finish(reason);
                        break;
                }
            }
        }
        else
        {
            var outcome = await Executor.ExecuteBufferedAsync(lease.ChatClient, messages, options, UnrestrictedTurnBudget.Instance, ModelCallPurpose.MainInference, ct, context);

            if (outcome.IsFailure) throw new InvalidDataException("executorRefused");

            usage = outcome.Value.Usage;

            if (outcome.Value.Response.Messages.SelectMany(m => m.Contents).Any(c => c is FunctionCallContent)) throw new InvalidDataException("unexpectedToolOutput");

            generated = answer = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            finish = outcome.Value.Response.FinishReason is { } reason ? Finish(reason) : "unknown";
        }

        double total = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        int prefix = decision.Eligibility == PromptCacheEligibility.Eligible ? Estimator.EstimateText(lease.Provider, lease.ResolvedModel, build.Prompt[..decision.Utf16Length]).TokenCount : 0;

        return new(pair, order, _enabled ? "B" : "A", phase, phase, ordinal, usage?.InputTokenCount, usage?.CachedInputTokenCount, usage?.OutputTokenCount, generated, answer, total, Observer.PromptMs, Observer.LoadMs, prefix, Observer.Valid && Observer.Requests == 1, finish, Observer.ToolWireDigest, Observer.TokenLimitWireField);
    }

    private static string Finish(ChatFinishReason reason) => reason == ChatFinishReason.Stop ? "stop" : reason == ChatFinishReason.Length ? "length" : reason == ChatFinishReason.ToolCalls ? "toolCall" : "unknown";

    internal string ToolDigest => PromptCacheBenchmarkEvidence.Digest(string.Join("\n", Tools.Cast<AIFunctionDeclaration>().Select(t => t.Name + "\n" + t.Description + "\n" + t.JsonSchema.GetRawText())));

    internal string SettingsDigest => PromptCacheBenchmarkEvidence.Digest(JsonSerializer.Serialize(Settings, ConfigurationJsonContext.Default.ArcanumSettings) + "|maxOutputTokens=32|temperature=0|seed=103|toolMode=none|reasoning=none|streamUsage=true");

    public void Dispose() { Epoch.Dispose(); _services.Dispose(); Observer.Dispose(); }
}

internal sealed class BenchmarkOptions(ArcanumSettings settings) : IOptionsMonitor<ArcanumSettings>
{
    public ArcanumSettings CurrentValue => settings;

    public ArcanumSettings Get(string? name) => settings;

    public IDisposable? OnChange(Action<ArcanumSettings, string?> listener) => null;
}

internal sealed class BenchmarkCredentialStore : IProviderCredentialStore
{
    internal string? Value { get; set; }
    public Task<SecretStoreReadResult> GetApiKeyReadResultAsync(string providerName, CancellationToken cancellationToken = default) => Task.FromResult(Value is null ? SecretStoreReadResult.Missing() : SecretStoreReadResult.Ok(Value));
    public Task SaveApiKeyAsync(string providerName, string apiKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeleteApiKeyAsync(string providerName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class BenchmarkAuthority : IPromptCacheAuthoritySnapshotProvider
{
    internal PromptCacheAuthoritySnapshot Snapshot { get; private set; } = new(1, null, CovenantRuntimeGenerationState.Initial.Availability);
    public PromptCacheAuthoritySnapshot Capture() => Snapshot;
    internal void PublishPublicFixtureProfile(string identity, CovenantHostToolsState state)
    {
        long generation = Snapshot.RuntimeAuthorityGeneration + 1;

        Snapshot = Snapshot with { RuntimeAuthorityGeneration = generation, Authority = new CovenantAuthoritySnapshot(generation, identity, 1, 1, 1, state, state == CovenantHostToolsState.Clean ? null : "public-fixture-transition") };
    }

    internal void Advance(CovenantHealthTransition transition) => Snapshot = Snapshot with { RuntimeAuthorityGeneration = Snapshot.RuntimeAuthorityGeneration + 1, Availability = Snapshot.Availability with { Generation = Snapshot.Availability.Generation + 1, DatasetGeneration = transition is CovenantHealthTransition.Reset or CovenantHealthTransition.Restore ? Guid.NewGuid() : Snapshot.Availability.DatasetGeneration, LastHealthTransition = transition, FeatureEnabled = transition == CovenantHealthTransition.FeatureConfiguration ? !Snapshot.Availability.FeatureEnabled : Snapshot.Availability.FeatureEnabled } };
}

internal sealed class BenchmarkCampaign : ICampaignAvailabilityReader, ICampaignPathIdentityReader
{
    private readonly Guid _id = Guid.NewGuid();
    internal long? Available { get; set; } = 1;
    internal RegisteredCampaignIdentity Root { get; set; }
    internal BenchmarkCampaign() => Root = new(_id, 1, 1, 0, new CovenantDigest(new byte[32]));
    internal CanonicalCampaignContext Scope => CanonicalCampaignContext.Create(SessionCampaignBinding.ForCampaign(_id), 1, 1, 1, Root.PhysicalIdentityDigest);
    public ValueTask<Result<long?>> FindAvailabilityGenerationAsync(Guid campaignId, CancellationToken cancellationToken) => ValueTask.FromResult(Result<long?>.Success(Available));
    public ValueTask<Result<RegisteredCampaignIdentity?>> ResolveMostSpecificAsync(string? workingDirectory, CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<Result<RegisteredCampaignIdentity?>> FindByCampaignAsync(Guid campaignId, CancellationToken cancellationToken) => ValueTask.FromResult(Result<RegisteredCampaignIdentity?>.Success(Root));
}

internal sealed class BenchmarkEstimator : IModelTokenEstimator
{
    private readonly ModelTokenEstimator _inner = new(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance));
    internal bool Changed { get; set; }
    public ResolvedModelTokenizationProfile ResolveProfile(ProviderSettings provider, string model) => _inner.ResolveProfile(provider, model);
    public ResolvedModelTokenizationProfile ResolveEffectiveProfile(ProviderSettings provider, string model) => Changed ? _inner.ResolveEffectiveProfile(provider, model) with { ProfileId = "simulated-tokenizer-successor" } : _inner.ResolveEffectiveProfile(provider, model);
    public TokenEstimate EstimateText(ProviderSettings provider, string model, string? text) => _inner.EstimateText(provider, model, text);
    public ContextTokenBreakdown EstimateContext(ModelTokenizationRequest request) => _inner.EstimateContext(request);
}

internal sealed class BenchmarkHttpClientFactory(BenchmarkWireObserver observer) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new OpenAiRequestAugmentingHandler(NullLogger<OpenAiRequestAugmentingHandler>.Instance) { InnerHandler = observer }, false) { Timeout = Timeout.InfiniteTimeSpan };
}

internal sealed class BenchmarkWireObserver(bool fixture) : HttpMessageHandler
{
    private readonly HttpMessageInvoker? _live = fixture ? null : new(RetroDownfall.Arcanum.Infrastructure.Security.OutboundUrlGuard.CreateProviderEgressHandler());
    internal bool Valid { get; private set; }
    internal int Requests { get; private set; }
    internal int ToolCount { get; private set; }
    internal double? PromptMs { get; private set; }
    internal double? LoadMs { get; private set; }

    internal string ToolWireDigest { get; private set; } = string.Empty;

    internal string TokenLimitWireField { get; private set; } = "unknown";

    private bool _candidate;

    private AIFunctionDeclaration[] _expectedTools = [];

    private string _expectedSystem = string.Empty;

    private string _expectedModel = string.Empty;

    internal void Reset(bool candidate, IList<AITool> tools, string system, string model)
    {
        Valid = true;
        Requests = 0;
        ToolCount = 0;
        PromptMs = null;
        LoadMs = null;
        _candidate = candidate;
        _expectedTools = tools.Cast<AIFunctionDeclaration>().ToArray();
        _expectedSystem = system;
        _expectedModel = model;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests++;

        byte[] bytes = await request.Content!.ReadAsByteArrayAsync(ct);

        using JsonDocument document = JsonDocument.Parse(bytes);

        var root = document.RootElement;

        ToolCount = root.TryGetProperty("tools", out var tools) ? tools.GetArrayLength() : 0;

        ToolWireDigest = PromptCacheBenchmarkEvidence.Digest(tools.GetRawText());

        TokenLimitWireField = root.TryGetProperty("max_completion_tokens", out _) ? "max_completion_tokens" : root.TryGetProperty("max_tokens", out _) ? "max_tokens" : "absent";

        string system = root.GetProperty("messages")[0].GetProperty("content").GetString()!;

        Valid &= ToolCount == _expectedTools.Length && system == _expectedSystem && root.GetProperty("model").GetString() == _expectedModel
            && system.Contains("[arcanum-dci-scope-v2:", StringComparison.Ordinal) == _candidate
            && root.TryGetProperty("tool_choice", out var choice) && choice.GetString() == "none"
            && !root.TryGetProperty("prompt_cache_key", out _) && !root.TryGetProperty("prompt_cache_retention", out _)
            && !root.TryGetProperty("cache_control", out _) && !root.TryGetProperty("options", out _);

        for (int index = 0; index < ToolCount; index++)
        {
            var function = tools[index].GetProperty("function");

            var expected = _expectedTools[index];

            Valid &= function.GetProperty("name").GetString() == expected.Name
                && function.GetProperty("description").GetString() == expected.Description
                && SameSchema(expected.JsonSchema, function.GetProperty("parameters"));
        }

        Valid &= root.GetProperty("temperature").GetDouble() == 0 && root.GetProperty("seed").GetInt64() == 103
            && TokenLimitWireField == "max_tokens" && !root.TryGetProperty("max_completion_tokens", out _)
            && root.TryGetProperty("max_tokens", out var cap) && cap.GetInt32() == 32;

        if (!fixture)
        {
            var response = await _live!.SendAsync(request, ct);

            response.Content = new BenchmarkObservedContent(response.Content, (prompt, load) => { PromptMs = prompt ?? PromptMs; LoadMs = load ?? LoadMs; });

            return response;
        }

        bool streaming = root.TryGetProperty("stream", out var stream) && stream.GetBoolean();

        string payload = streaming ? "data: {\"id\":\"fixture\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gemma4:e4b\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\"},\"finish_reason\":null}]}\n\ndata: {\"id\":\"fixture\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gemma4:e4b\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"OK\"},\"finish_reason\":null}]}\n\ndata: {\"id\":\"fixture\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gemma4:e4b\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":1,\"total_tokens\":101}}\n\ndata: [DONE]\n\n" : "{\"id\":\"fixture\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"gemma4:e4b\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"OK\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":1,\"total_tokens\":101}}";

        // Match the live observer's forward-only response stream. The pinned SDK must not
        // treat consumed fixture content as a seekable replay buffer during disposal.
        return new(HttpStatusCode.OK)
        {
            Content = new BenchmarkObservedContent(
                new StringContent(payload.Replace("gemma4:e4b", _expectedModel, StringComparison.Ordinal), Encoding.UTF8, streaming ? "text/event-stream" : "application/json"),
                (prompt, load) => { PromptMs = prompt ?? PromptMs; LoadMs = load ?? LoadMs; }),
        };
    }

    // The pinned adapter materializes an empty required array. JSON Schema treats absent and
    // empty required identically. No other structural/value difference is accepted.
    internal static bool SameSchema(JsonElement source, JsonElement wire) => JsonElement.DeepEquals(source, wire)
        || source.ValueKind == JsonValueKind.Object && wire.ValueKind == JsonValueKind.Object
        && !source.TryGetProperty("required", out _)
        && wire.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array && required.GetArrayLength() == 0
        && wire.EnumerateObject().Count() == source.EnumerateObject().Count() + 1
        && source.EnumerateObject().All(p => wire.TryGetProperty(p.Name, out var actual) && JsonElement.DeepEquals(p.Value, actual));

    protected override void Dispose(bool disposing) { if (disposing) _live?.Dispose(); base.Dispose(disposing); }
}
