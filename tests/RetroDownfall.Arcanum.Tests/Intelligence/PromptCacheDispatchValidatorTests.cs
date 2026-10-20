using RetroDownfall.Arcanum.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using RetroDownfall.Arcanum.Api;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Core.Primitives;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;
using RetroDownfall.Arcanum.Tests.LongRest;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class PromptCacheDispatchValidatorTests
{
    [Fact]
    public void Production_validator_can_resolve_from_root_with_scoped_campaign_readers()
    {
        using Fixture f = new();

        ServiceCollection production = [];

        production.AddArcanumApiServices(new ConfigurationBuilder().Build());

        ServiceCollection services = [];

        services.AddSingleton(f.Epoch);

        services.AddSingleton<IProviderApiKeyResolver>(f.Credentials);

        services.AddSingleton<IPromptCacheAuthoritySnapshotProvider>(f.Authority);

        services.AddSingleton<IModelTokenEstimator>(f.Estimator);

        services.AddScoped<ICampaignAvailabilityReader, CampaignReader>();

        services.AddScoped<ICampaignPathIdentityReader, CampaignReader>();

        ((IServiceCollection)services).Add(production.Single(d => d.ServiceType == typeof(PromptCacheDispatchValidator)));

        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        Assert.NotNull(provider.GetRequiredService<PromptCacheDispatchValidator>());
    }

    [Fact]
    public async Task Same_scope_is_stable_and_runtime_generation_changes_marker()
    {
        using Fixture f = new();

        var first = await f.Prepare();

        var same = await f.Prepare();

        Assert.Equal(64, first!.ScopeMarkerDigest.Length);

        Assert.Equal(first.ScopeMarkerDigest, same!.ScopeMarkerDigest);

        f.Authority.Snapshot = f.Authority.Snapshot with { RuntimeAuthorityGeneration = 2 };

        Assert.NotEqual(first.ScopeMarkerDigest, (await f.Prepare())!.ScopeMarkerDigest);
    }

    [Theory]
    [InlineData("messages")]
    [InlineData("tools")]
    [InlineData("client")]
    [InlineData("configuration")]
    [InlineData("credential")]
    [InlineData("authority")]
    [InlineData("provider")]
    [InlineData("instructions")]
    [InlineData("conversation")]
    [InlineData("plan")]
    [InlineData("response-format")]
    public async Task Changed_materialization_requires_replan(string changed)
    {
        using Fixture f = new();

        var prepared = (await f.Prepare())!;

        List<ChatMessage> messages = [new(ChatRole.System, prepared.Document.Render()), new(ChatRole.User, "hello")];

        var context = new ModelCallContext(f.Provider, "gpt-5", 0, 0, PromptCacheBinding: prepared.Bind(messages, f.Options));

        IChatClient client = f.Chat;

        switch (changed)
        {
            case "messages": messages[0] = new(ChatRole.System, "changed"); break;
            case "tools": f.Options.Tools = [new ArcanumSystemInfoTool()]; break;
            case "client": client = new FakeChat(); break;
            case "configuration": f.Settings.Providers[0].Endpoint = "https://changed.test/v1"; break;
            case "credential": f.Credentials.Current = false; break;
            case "authority": f.Authority.Snapshot = f.Authority.Snapshot with { RuntimeAuthorityGeneration = 2 }; break;
            case "instructions": f.Options.Instructions = "changed"; break;
            case "conversation": f.Options.ConversationId = "changed"; break;
            case "response-format": f.Options.ResponseFormat = ChatResponseFormat.Json; break;
            case "plan": context = context with { PromptCachePlan = prepared.CreatePlan(f.Options, f.Estimator) with { CacheKey = "unbound" } }; break;
            case "provider": f.Provider.Endpoint = "https://changed.test/v1"; break;
        }

        Assert.Equal(PromptCacheDispatchStatus.ReplanRequired,
            (await f.Validator.ValidateAsync(client, messages, f.Options, context, CancellationToken.None)).Value);
    }

    [Theory]
    [InlineData("instructions")]
    [InlineData("raw-factory")]
    [InlineData("schema")]
    [InlineData("model")]
    [InlineData("conversation")]
    [InlineData("background")]
    [InlineData("custom-options")]
    [InlineData("strict-tools")]
    public async Task Options_changed_after_preparation_do_not_gain_binding_or_plan_eligibility(string changed)
    {
        using Fixture f = new();

        var prepared = (await f.Prepare())!;

        List<ChatMessage> messages = [new(ChatRole.System, prepared.Build.Prompt), new(ChatRole.User, "hello")];

        ChatOptions options = MakeOptionsIneligible(f.Options, changed);

        Assert.False(prepared.CanBind(messages, options));

        Assert.NotEqual(PromptCacheEligibility.Eligible, prepared.CreatePlan(options, f.Estimator).Eligibility);
    }

    [Theory]
    [InlineData(false, "instructions")]
    [InlineData(true, "instructions")]
    [InlineData(false, "raw-factory")]
    [InlineData(true, "raw-factory")]
    [InlineData(false, "schema")]
    [InlineData(true, "schema")]
    [InlineData(false, "model")]
    [InlineData(true, "model")]
    [InlineData(false, "strict-tools")]
    [InlineData(true, "strict-tools")]
    public async Task Options_changed_before_binding_cannot_reach_the_physical_provider(bool streaming, string changed)
    {
        using Fixture f = new();

        var prepared = (await f.Prepare())!;

        List<ChatMessage> messages = [new(ChatRole.System, prepared.Build.Prompt), new(ChatRole.User, "hello")];

        ChatOptions options = MakeOptionsIneligible(f.Options, changed);

        // Deliberately bypass CanBind to prove that binding is not an alternate eligibility issuer.
        ModelCallContext context = new(f.Provider, "gpt-5", 0, 0, PromptCacheBinding: prepared.Bind(messages, options));

        ModelCallExecutor executor = new(f.Estimator, null, (Microsoft.Extensions.Logging.ILogger?)null, f.Validator);

        if (streaming)
        {
            List<ModelCallUpdate> updates = [];

            await foreach (var update in executor.ExecuteStreamingAsync(f.Chat, messages, options, UnrestrictedTurnBudget.Instance,
                ModelCallPurpose.MainInference, CancellationToken.None, context)) updates.Add(update);

            Assert.Single(updates.OfType<ModelCallFailureUpdate>());
        }
        else
        {
            Assert.True((await executor.ExecuteBufferedAsync(f.Chat, messages, options, UnrestrictedTurnBudget.Instance,
                ModelCallPurpose.MainInference, CancellationToken.None, context)).IsFailure);
        }

        Assert.Equal(0, f.Chat.Calls);
    }

    [Fact]
    public async Task Binding_created_from_ineligible_options_stays_invalid_when_replaced_with_eligible_options()
    {
        using Fixture f = new();

        var prepared = (await f.Prepare())!;

        List<ChatMessage> messages = [new(ChatRole.System, prepared.Build.Prompt)];

        ChatOptions custom = MakeOptionsIneligible(f.Options, "custom-options");

        ModelCallContext context = new(f.Provider, "gpt-5", 0, 0, PromptCacheBinding: prepared.Bind(messages, custom));

        Assert.Equal(PromptCacheDispatchStatus.ReplanRequired,
            (await f.Validator.ValidateAsync(f.Chat, messages, f.Options, context, CancellationToken.None)).Value);
    }

    private static ChatOptions MakeOptionsIneligible(ChatOptions options, string changed)
    {
        switch (changed)
        {
            case "strict-tools": options.AdditionalProperties = new() { ["strict"] = true }; break;
            case "instructions": options.Instructions = "unowned prefix instructions"; break;
            case "raw-factory": options.RawRepresentationFactory = _ => new object(); break;
            case "schema":
                using (var schema = JsonDocument.Parse("{\"type\":\"object\",\"description\":\"unowned schema\"}"))
                {
                    options.ResponseFormat = ChatResponseFormat.ForJsonSchema(schema.RootElement.Clone());
                }
                break;
            case "model": options.ModelId = "different-model"; break;
            case "conversation": options.ConversationId = "unowned-conversation"; break;
            case "background": options.AllowBackgroundResponses = true; break;
            case "custom-options": return new CustomChatOptions { Tools = options.Tools };
        }

        return options;
    }

    private sealed class CustomChatOptions : ChatOptions;

    [Theory]
    [InlineData("image")]
    [InlineData("split-text")]
    [InlineData("metadata")]
    [InlineData("wrong-role")]
    public async Task Only_the_owned_single_plain_system_content_can_bind(string shape)
    {
        using Fixture f = new();

        var prepared = (await f.Prepare())!;

        ChatMessage system = new(ChatRole.System, prepared.Build.Prompt);

        switch (shape)
        {
            case "image": system.Contents.Insert(0, new DataContent(new byte[] { 1 }, "image/png")); break;
            case "split-text": system.Contents.Add(new TextContent(string.Empty)); break;
            case "metadata": system.AdditionalProperties = new() { ["unowned"] = "instructions" }; break;
            case "wrong-role": system.Role = ChatRole.User; break;
        }

        Assert.False(prepared.CanBind([system, new(ChatRole.User, "hello")], f.Options));
    }

    [Fact]
    public async Task Unrepresented_message_metadata_cannot_admit_an_optimized_candidate()
    {
        using Fixture f = new();

        var prepared = (await f.Prepare())!;

        List<ChatMessage> messages = [new(ChatRole.System, prepared.Build.Prompt), new(ChatRole.User, "hello") { RawRepresentation = new object() }];

        Assert.False(prepared.CanBind(messages, f.Options));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Structured_output_instructions_do_not_establish_prefix_provenance(bool schema)
    {
        using Fixture f = new();

        using var json = JsonDocument.Parse("{\"type\":\"object\",\"description\":\"unowned schema instructions\"}");

        f.Options.ResponseFormat = schema ? ChatResponseFormat.ForJsonSchema(json.RootElement) : ChatResponseFormat.Json;

        Assert.Null(await f.Prepare());

        Assert.Equal(0, f.Authority.Reads);
    }

    [Theory]
    [InlineData("gpt-5", true)]
    [InlineData("different-model", false)]
    public async Task Explicit_model_override_requires_the_actual_client_identity(string model, bool eligible)
    {
        using Fixture f = new();

        f.Options.ModelId = model;

        Assert.Equal(eligible, await f.Prepare() is not null);
    }

    [Fact]
    public async Task Separately_prepared_boolean_strict_tool_options_remain_ordinary()
    {
        using Fixture f = new();

        Assert.NotNull(await f.Prepare());

        f.Options.AdditionalProperties = new() { ["strict"] = false };

        var strictFalse = await f.Prepare();

        f.Options.AdditionalProperties["strict"] = true;

        var strictTrue = await f.Prepare();

        Assert.Null(strictFalse);

        Assert.Null(strictTrue);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task Strict_metadata_without_a_boolean_tool_override_retains_eligibility(object strict, bool tools)
    {
        using Fixture f = new();

        f.Options.AdditionalProperties = new() { ["strict"] = strict };

        if (!tools) f.Options.Tools = [];

        Assert.NotNull(await f.Prepare());
    }

    [Fact]
    public async Task Opaque_raw_wire_factory_has_no_cache_proof()
    {
        using Fixture f = new();

        f.Options.RawRepresentationFactory = _ => new object();

        Assert.Null(await f.Prepare());
    }

    [Fact]
    public async Task Missing_client_or_credential_proof_preserves_ordinary_prompt()
    {
        using Fixture f = new();

        using var ordinaryLease = new ChatClientLease(f.Chat, f.Provider, "gpt-5", null);

        Assert.Null(await f.Validator.PrepareAsync(ordinaryLease, f.Ordinary, f.Options, CanonicalCampaignContext.GlobalOnly, ContentSensitivity.None, CancellationToken.None));
    }

    [Fact]
    public async Task Cancelled_validation_never_reaches_a_current_result()
    {
        using Fixture f = new();

        using var cancelled = new CancellationTokenSource();

        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await f.Validator.ValidateAsync(f.Chat, [], f.Options, null, cancelled.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Physical_executor_refuses_stale_authority_including_after_stream_context_yield(bool streaming)
    {
        using Fixture f = new();

        var prepared = (await f.Prepare())!;

        List<ChatMessage> messages = [new(ChatRole.System, prepared.Build.Prompt), new(ChatRole.User, "hello")];

        ModelCallContext context = new(f.Provider, "gpt-5", 0, 0, PromptCacheBinding: prepared.Bind(messages, f.Options));

        ModelCallExecutor executor = new(f.Estimator, null, (Microsoft.Extensions.Logging.ILogger?)null, f.Validator);

        if (streaming)
        {
            await using var stream = executor.ExecuteStreamingAsync(f.Chat, messages, f.Options, UnrestrictedTurnBudget.Instance,
                ModelCallPurpose.MainInference, CancellationToken.None, context).GetAsyncEnumerator();

            Assert.True(await stream.MoveNextAsync());

            Assert.IsType<ModelCallContextUpdate>(stream.Current);

            f.Authority.Snapshot = f.Authority.Snapshot with { RuntimeAuthorityGeneration = 2 };

            Assert.True(await stream.MoveNextAsync());

            Assert.IsType<ModelCallFailureUpdate>(stream.Current);
        }
        else
        {
            f.Authority.Snapshot = f.Authority.Snapshot with { RuntimeAuthorityGeneration = 2 };

            Assert.True((await executor.ExecuteBufferedAsync(f.Chat, messages, f.Options, UnrestrictedTurnBudget.Instance,
                ModelCallPurpose.MainInference, CancellationToken.None, context)).IsFailure);
        }

        Assert.Equal(0, f.Chat.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Canonical_campaign_is_revalidated_without_path_resolution(bool changed)
    {
        CampaignReader reader = new();

        using Fixture f = new(reader);

        var scope = CanonicalCampaignContext.Create(SessionCampaignBinding.ForCampaign(reader.Id), 1, 1, null, null);

        var prepared = (await f.Validator.PrepareAsync(f.Lease, f.Ordinary, f.Options, scope, ContentSensitivity.None, CancellationToken.None))!;

        List<ChatMessage> messages = [new(ChatRole.System, prepared.Build.Prompt)];

        ModelCallContext context = new(f.Provider, "gpt-5", 0, 0, PromptCacheBinding: prepared.Bind(messages, f.Options));

        reader.Generation = changed ? 2 : 1;

        Assert.Equal(changed ? PromptCacheDispatchStatus.ReplanRequired : PromptCacheDispatchStatus.Current,
            (await f.Validator.ValidateAsync(f.Chat, messages, f.Options, context, CancellationToken.None)).Value);

        Assert.Equal(changed ? 1 : 2, reader.PathReads);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public async Task Canonical_reader_failures_preserve_the_original_error_and_never_send(bool streaming, bool availability, bool integrity)
    {
        Error expected = new(integrity ? ErrorCodes.Covenant.IntegrityFailure : "Campaign.ReaderFailure", "canonical reader failure");

        CampaignReader reader = new();

        if (availability) reader.AvailabilityFailure = expected;
        else reader.PathFailure = expected;

        using Fixture f = new(reader);

        var scope = CanonicalCampaignContext.Create(SessionCampaignBinding.ForCampaign(reader.Id), 1, 1, null, null);

        var prepared = (await f.Validator.PrepareAsync(f.Lease, f.Ordinary, f.Options, scope, ContentSensitivity.CovenantDerived, CancellationToken.None))!;

        List<ChatMessage> messages = [new(ChatRole.System, prepared.Build.Prompt)];

        ModelCallContext context = new(f.Provider, "gpt-5", 0, 0, Sensitivity: ContentSensitivity.CovenantDerived,
            PromptCacheBinding: prepared.Bind(messages, f.Options));

        ModelCallExecutor executor = new(f.Estimator, null, (Microsoft.Extensions.Logging.ILogger?)null, f.Validator);

        Error actual;

        if (streaming)
        {
            List<ModelCallUpdate> updates = [];

            await foreach (var update in executor.ExecuteStreamingAsync(f.Chat, messages, f.Options, UnrestrictedTurnBudget.Instance,
                ModelCallPurpose.MainInference, CancellationToken.None, context)) updates.Add(update);

            actual = Assert.Single(updates.OfType<ModelCallFailureUpdate>()).Error;
        }
        else
        {
            var outcome = await executor.ExecuteBufferedAsync(f.Chat, messages, f.Options, UnrestrictedTurnBudget.Instance,
                ModelCallPurpose.MainInference, CancellationToken.None, context);

            Assert.True(outcome.IsFailure);

            actual = outcome.Error;
        }

        Assert.Equal(0, f.Chat.Calls);

        Assert.Equal(expected, actual);

        if (availability) Assert.Equal(0, reader.PathReads);
    }

    [Theory]
    [InlineData(false, "availability")]
    [InlineData(true, "availability")]
    [InlineData(false, "path")]
    [InlineData(true, "path")]
    [InlineData(false, "integrity")]
    [InlineData(true, "integrity")]
    public async Task Campaign_changes_during_credential_await_cannot_reach_provider(bool streaming, string change)
    {
        CampaignReader reader = new();

        using Fixture f = new(reader);

        var scope = CanonicalCampaignContext.Create(SessionCampaignBinding.ForCampaign(reader.Id), 1, 1, null, null);

        var prepared = (await f.Validator.PrepareAsync(f.Lease, f.Ordinary, f.Options, scope, ContentSensitivity.None, CancellationToken.None))!;

        var originalAuthority = f.Authority.Snapshot;

        var originalConfiguration = f.Epoch.Capture().Stamp;

        Error integrity = new(ErrorCodes.Covenant.IntegrityFailure, "root identity malformed during credential validation");

        f.Credentials.BeforeCurrentReturn = () =>
        {
            Assert.Equal(1, reader.PathReads);

            if (change == "availability") reader.Generation++;
            else if (change == "path") reader.Identity = new(reader.Id, 1, 2, 0, new CovenantDigest(new byte[32]));
            else reader.PathFailure = integrity;
        };

        List<ChatMessage> messages = [new(ChatRole.System, prepared.Build.Prompt)];

        ModelCallContext context = new(f.Provider, "gpt-5", 0, 0, PromptCacheBinding: prepared.Bind(messages, f.Options));

        ModelCallExecutor executor = new(f.Estimator, null, (Microsoft.Extensions.Logging.ILogger?)null, f.Validator);

        Error actual;

        if (streaming)
        {
            List<ModelCallUpdate> updates = [];

            await foreach (var update in executor.ExecuteStreamingAsync(f.Chat, messages, f.Options, UnrestrictedTurnBudget.Instance,
                ModelCallPurpose.MainInference, CancellationToken.None, context)) updates.Add(update);

            Assert.Equal(0, f.Chat.Calls);

            actual = Assert.Single(updates.OfType<ModelCallFailureUpdate>()).Error;
        }
        else
        {
            var outcome = await executor.ExecuteBufferedAsync(f.Chat, messages, f.Options, UnrestrictedTurnBudget.Instance,
                ModelCallPurpose.MainInference, CancellationToken.None, context);

            Assert.Equal(0, f.Chat.Calls);

            Assert.True(outcome.IsFailure);

            actual = outcome.Error;
        }

        Assert.Equal(change == "integrity" ? integrity.Code : ErrorCodes.Hub.ContextBudgetExceeded, actual.Code);

        Assert.Equal(originalAuthority, f.Authority.Snapshot);

        Assert.True(f.Epoch.IsCurrent(originalConfiguration));
    }

    [Fact]
    public async Task Missing_canonical_cache_readers_remain_optimization_unavailable_not_stale()
    {
        using Fixture f = new();

        var scope = CanonicalCampaignContext.Create(SessionCampaignBinding.ForCampaign(Guid.NewGuid()), 1, 1, null, null);

        var prepared = (await f.Validator.PrepareAsync(f.Lease, f.Ordinary, f.Options, scope, ContentSensitivity.None, CancellationToken.None))!;

        List<ChatMessage> messages = [new(ChatRole.System, prepared.Build.Prompt)];

        ModelCallContext context = new(f.Provider, "gpt-5", 0, 0, PromptCacheBinding: prepared.Bind(messages, f.Options));

        Assert.Equal(PromptCacheDispatchStatus.OptimizationUnavailable,
            (await f.Validator.ValidateAsync(f.Chat, messages, f.Options, context, CancellationToken.None)).Value);
    }

    [Theory]
    [InlineData(false, "none")]
    [InlineData(true, "none")]
    [InlineData(false, "credential")]
    [InlineData(true, "credential")]
    [InlineData(false, "authority")]
    [InlineData(true, "authority")]
    [InlineData(false, "options")]
    [InlineData(true, "options")]
    public async Task Optional_reader_absence_requests_ordinary_retry_only_when_other_facts_remain_current(bool streaming, string changed)
    {
        using Fixture f = new();

        var scope = CanonicalCampaignContext.Create(SessionCampaignBinding.ForCampaign(Guid.NewGuid()), 1, 1, null, null);

        var prepared = (await f.Validator.PrepareAsync(f.Lease, f.Ordinary, f.Options, scope, ContentSensitivity.None, CancellationToken.None))!;

        List<ChatMessage> messages = [new(ChatRole.System, prepared.Build.Prompt)];

        ModelCallContext context = new(f.Provider, "gpt-5", 0, 0, PromptCacheBinding: prepared.Bind(messages, f.Options));

        if (changed == "credential") f.Credentials.Current = false;
        else if (changed == "authority") f.Credentials.BeforeCurrentReturn = () => f.Authority.Snapshot = f.Authority.Snapshot with { RuntimeAuthorityGeneration = 2 };
        else if (changed == "options") f.Options.Instructions = "unowned instructions";

        ModelCallExecutor executor = new(f.Estimator, null, (Microsoft.Extensions.Logging.ILogger?)null, f.Validator);

        ModelCallRetryDirective directive;

        if (streaming)
        {
            List<ModelCallUpdate> updates = [];

            await foreach (var update in executor.ExecuteStreamingAsync(f.Chat, messages, f.Options, UnrestrictedTurnBudget.Instance,
                ModelCallPurpose.MainInference, CancellationToken.None, context)) updates.Add(update);

            directive = Assert.Single(updates.OfType<ModelCallFailureUpdate>()).RetryDirective;
        }
        else
        {
            var outcome = await executor.ExecuteBufferedAsync(f.Chat, messages, f.Options, UnrestrictedTurnBudget.Instance,
                ModelCallPurpose.MainInference, CancellationToken.None, context);

            Assert.True(outcome.IsFailure);

            directive = outcome.Failure.RetryDirective;
        }

        Assert.Equal(changed == "none" ? ModelCallRetryDirective.OrdinaryPromptCacheFallback : ModelCallRetryDirective.None, directive);

        Assert.Equal(0, f.Chat.Calls);
    }

    [Fact]
    public async Task Protected_history_retains_ordinary_layout_and_binds_authority()
    {
        using Fixture f = new();

        var prepared = (await f.Validator.PrepareAsync(f.Lease, f.Ordinary, f.Options, CanonicalCampaignContext.GlobalOnly,
            ContentSensitivity.CovenantDerived, CancellationToken.None))!;

        Assert.Same(f.Ordinary, prepared.Document);

        Assert.Equal(PromptCacheNonEligibilityReason.SensitiveContent, prepared.CreatePlan(f.Options, f.Estimator).NonEligibilityReason);
    }

    internal sealed class CampaignReader : ICampaignAvailabilityReader, ICampaignPathIdentityReader
    {
        internal Guid Id { get; } = Guid.NewGuid();

        internal long Generation { get; set; } = 1;

        internal int PathReads { get; private set; }

        internal Error? AvailabilityFailure { get; set; }

        internal Error? PathFailure { get; set; }

        internal RegisteredCampaignIdentity? Identity { get; set; }

        public ValueTask<Result<long?>> FindAvailabilityGenerationAsync(Guid campaignId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(AvailabilityFailure is { } error ? Result<long?>.Failure(error) : Result<long?>.Success(Generation));

        public ValueTask<Result<RegisteredCampaignIdentity?>> FindByCampaignAsync(Guid campaignId, CancellationToken cancellationToken)
        {
            PathReads++;

            return ValueTask.FromResult(PathFailure is { } error ? Result<RegisteredCampaignIdentity?>.Failure(error) : Result<RegisteredCampaignIdentity?>.Success(Identity));
        }

        public ValueTask<Result<RegisteredCampaignIdentity?>> ResolveMostSpecificAsync(string? workingDirectory, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Cache validation must not re-resolve a path.");
    }

    internal sealed class Fixture : IDisposable
    {
        internal ArcanumSettings Settings { get; } = new() { Features = new() { DynamicContextInjectionV2 = true }, Providers = [new() { Name = "provider", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://api.openai.com/v1", Models = ["gpt-5"] }] };

        internal ProviderSettings Provider => Settings.Providers[0];

        internal FakeChat Chat { get; } = new();

        internal CredentialResolver Credentials { get; } = new();

        internal AuthorityProvider Authority { get; } = new();

        internal ChatOptions Options { get; } = new() { Tools = [new ArcanumLocalTimeTool()] };

        internal SystemPromptDocument Ordinary { get; } = SystemPromptBuilder.BuildDocument(new PingRequest("hello") { CliTerminalFormatting = true }, "dynamic codex");

        internal PromptCacheConfigurationEpoch Epoch { get; }

        internal ModelTokenEstimator Estimator { get; } = new(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance));

        internal PromptCacheDispatchValidator Validator { get; }

        internal ChatClientLease Lease { get; }

        private readonly ServiceProvider services;

        internal Fixture(CampaignReader? reader = null)
        {
            Epoch = new(new LongRestConfigurationEpochTests.NotifyingOptions(Settings), Guid.NewGuid());

            ServiceCollection registrations = [];

            if (reader is not null)
            {
                registrations.AddScoped<ICampaignAvailabilityReader>(_ => reader);

                registrations.AddScoped<ICampaignPathIdentityReader>(_ => reader);
            }

            services = registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

            Validator = new(Epoch, Credentials, Authority, Estimator, services.GetRequiredService<IServiceScopeFactory>());

            Lease = new(Chat, Provider, "gpt-5", null, new PromptCacheClientIdentity(Chat, Provider, "gpt-5", Epoch.Capture().Stamp, Credentials.Stamp));
        }

        internal ValueTask<PromptCachePreparedPrompt?> Prepare() => Validator.PrepareAsync(Lease, Ordinary, Options, CanonicalCampaignContext.GlobalOnly, ContentSensitivity.None, CancellationToken.None);

        public void Dispose()
        {
            Epoch.Dispose();

            services.Dispose();
        }
    }

    internal sealed class CredentialResolver : IProviderApiKeyResolver
    {
        internal ProviderCredentialStamp Stamp { get; } = new(Guid.NewGuid(), Guid.NewGuid(), 1);

        internal bool Current { get; set; } = true;

        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        internal Action? BeforeCurrentReturn { get; set; }

        public async Task<bool> IsCurrentAsync(ProviderSettings provider, ProviderCredentialStamp stamp, CancellationToken cancellationToken = default)
        {
            await Task.Yield();

            BeforeCurrentReturn?.Invoke();

            return Current;
        }
    }

    internal sealed class AuthorityProvider : IPromptCacheAuthoritySnapshotProvider
    {
        internal PromptCacheAuthoritySnapshot Snapshot { get; set; } = new(1, null, CovenantRuntimeGenerationState.Initial.Availability);

        internal int Reads { get; private set; }

        public PromptCacheAuthoritySnapshot Capture() { Reads++; return Snapshot; }
    }

    internal sealed class FakeChat : IChatClient
    {
        internal int Calls { get; private set; }

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;

            await Task.CompletedTask;

            yield return new ChatResponseUpdate(ChatRole.Assistant, "answer");
        }
    }
}
