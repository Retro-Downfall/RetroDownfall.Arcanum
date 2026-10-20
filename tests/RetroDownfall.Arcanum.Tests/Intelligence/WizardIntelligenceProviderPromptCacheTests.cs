using Microsoft.AspNetCore.DataProtection;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Tests.LongRest;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Infrastructure.Resilience;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CacheV2_operator_dispatch_uses_candidate_only_with_bound_client(bool streaming, bool knownClient)
    {
        ArcanumSettings settings = DefaultSettings();

        settings.Features.DynamicContextInjectionV2 = true;

        ScriptingChatClient chat = new();

        chat.EnqueueText("answer");

        chat.EnqueueStreamTokens("answer");

        using PromptCacheConfigurationEpoch epoch = new(new LongRestConfigurationEpochTests.NotifyingOptions(settings), Guid.NewGuid());

        var credentials = new PromptCacheDispatchValidatorTests.CredentialResolver();

        var authority = new PromptCacheDispatchValidatorTests.AuthorityProvider();

        using ServiceProvider cacheServices = new ServiceCollection().BuildServiceProvider();

        PromptCacheDispatchValidator validator = new(epoch, credentials, authority,
            new ModelTokenEstimator(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance)),
            cacheServices.GetRequiredService<IServiceScopeFactory>());

        var factory = new CacheTestFactory(chat, settings.Providers[0], epoch, knownClient ? credentials.Stamp : null);

        var wizard = CreateWizard(chat, settings, factory: factory, cacheValidator: validator);

        var request = BaseRequest() with { Prompt = "hello", SkipSpellRouting = true, DisableMcpTools = true, CliTerminalFormatting = true };

        if (streaming)
        {
            await foreach (var _ in wizard.StreamPromptAsync(request, InvocationContexts.AttendedGlobalOnlySession(), CancellationToken.None)) { }
        }
        else
        {
            Assert.True((await wizard.ExecutePromptAsync(request, InvocationContexts.AttendedGlobalOnlySession(), CancellationToken.None)).IsSuccess);
        }

        var messages = chat.LastBufferedMessages;

        Assert.True(knownClient == messages[0].Text.Contains("arcanum-dci-scope-v2:", StringComparison.Ordinal),
            $"authority reads {authority.Reads}; tools {string.Join(',', chat.LastChatOptions?.Tools?.Select(t => t.Name + ':' + RetroDownfall.Arcanum.Infrastructure.Intelligence.PromptCacheToolProvenance.IsCurrent(t)) ?? [])}");

        if (knownClient)
        {
            Assert.True(messages[0].Text.IndexOf("## INSTRUCTIONS", StringComparison.Ordinal) < messages[0].Text.IndexOf("## DATA", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(false, "tools")]
    [InlineData(true, "tools")]
    [InlineData(false, "restart")]
    [InlineData(true, "restart")]
    [InlineData(false, "structured")]
    [InlineData(false, "compression")]
    [InlineData(false, "background")]
    [InlineData(true, "background")]
    [InlineData(false, "unknown-tools")]
    [InlineData(true, "unknown-tools")]
    public async Task CacheV2_rebuilds_each_attempt_and_excludes_unproven_surfaces(bool streaming, string path)
    {
        ArcanumSettings settings = DefaultSettings();

        settings.Features.DynamicContextInjectionV2 = true;

        settings.Features.WebBrowsing = path == "unknown-tools";

        if (path == "compression") settings.Providers[0].ContextWindowLimit = 32_768;

        ScriptingChatClient chat = new();

        if (path == "tools")
        {
            if (streaming) chat.EnqueueStreamToolCall(ArcanumLocalTimeTool.ToolName);
            else chat.EnqueueToolCall(ArcanumLocalTimeTool.ToolName);
        }
        else if (path == "restart")
        {
            if (streaming) chat.EnqueueImmediateStreamFailure(new InvalidOperationException("model does not support tools"));
            else chat.EnqueueException(new InvalidOperationException("model does not support tools"));
        }
        else if (path == "structured")
        {
            chat.EnqueueText("{}");
        }

        chat.EnqueueText(path == "structured" ? "{\"name\":\"answer\"}" : "answer");

        chat.EnqueueStreamTokens("answer");

        using PromptCacheConfigurationEpoch epoch = new(new LongRestConfigurationEpochTests.NotifyingOptions(settings), Guid.NewGuid());

        var credentials = new PromptCacheDispatchValidatorTests.CredentialResolver();

        var authority = new PromptCacheDispatchValidatorTests.AuthorityProvider();

        using ServiceProvider cacheServices = new ServiceCollection().BuildServiceProvider();

        PromptCacheDispatchValidator validator = new(epoch, credentials, authority,
            new ModelTokenEstimator(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance)),
            cacheServices.GetRequiredService<IServiceScopeFactory>());

        var factory = new CacheTestFactory(chat, settings.Providers[0], epoch, credentials.Stamp);

        var request = BaseRequest() with { Prompt = "hello", SkipSpellRouting = true, DisableMcpTools = true, CliTerminalFormatting = true };

        FakeGrimoireRepository? grimoire = null;

        if (path == "structured")
        {
            using var schema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"}},\"required\":[\"name\"],\"additionalProperties\":false}");

            request = request with { ResponseFormat = "json_schema", ResponseFormatJsonSchema = schema.RootElement.Clone() };
        }
        else if (path == "compression")
        {
            Guid session = Guid.NewGuid();

            grimoire = new() { Session = BuildHeavySession(session) };

            request = request with { SessionId = session };
        }

        var wizard = CreateWizard(chat, settings, grimoire, factory: factory, cacheValidator: validator);

        var invocation = path == "background" ? ArcanumInvocationContext.None : InvocationContexts.AttendedGlobalOnlySession();

        if (streaming)
        {
            await foreach (var _ in wizard.StreamPromptAsync(request, invocation, CancellationToken.None)) { }
        }
        else
        {
            var result = await wizard.ExecutePromptAsync(request, invocation, CancellationToken.None);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        }

        var calls = streaming ? chat.AllStreamingCalls : chat.AllBufferedCalls;

        Assert.NotEmpty(calls);

        bool eligible = path is not "background" and not "unknown-tools" and not "structured";

        Assert.All(calls, messages => Assert.Equal(eligible, messages[0].Text.Contains("arcanum-dci-scope-v2:", StringComparison.Ordinal)));

        if (path is "tools" or "restart" or "structured") Assert.Equal(2, calls.Count);

        if (!eligible) Assert.Equal(0, authority.Reads);

        if (path == "compression")
        {
            Assert.Contains(calls.SelectMany(static messages => messages), static message =>
                message.Text.Contains("### Campaign Summary (compressed context)", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CacheV2_unavailable_proof_preserves_ordinary_bytes_without_legacy_cache_directive(bool streaming, bool unknownTools)
    {
        await File.WriteAllTextAsync(Path.Combine(_workspace.Root, "CODEX.md"), new string('x', 8_192));

        string? ordinary = null;

        foreach (bool enabled in new[] { false, true })
        {
            ArcanumSettings settings = DefaultSettings() with
            {
                DefaultModel = "gpt-5",
                FastModel = "gpt-5",
                Providers = [DefaultProvider() with { Endpoint = "https://api.openai.com/v1", Models = ["gpt-5"] }],
            };

            settings.Features.DynamicContextInjectionV2 = enabled;

            settings.Features.WebBrowsing = unknownTools;

            ScriptingChatClient chat = new();

            chat.EnqueueText("answer");

            chat.EnqueueStreamTokens("answer");

            using PromptCacheConfigurationEpoch epoch = new(new LongRestConfigurationEpochTests.NotifyingOptions(settings), Guid.NewGuid());

            var credentials = new PromptCacheDispatchValidatorTests.CredentialResolver();

            var authority = new PromptCacheDispatchValidatorTests.AuthorityProvider();

            using ServiceProvider cacheServices = new ServiceCollection().BuildServiceProvider();

            PromptCacheDispatchValidator validator = new(epoch, credentials, authority,
                new ModelTokenEstimator(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance)),
                cacheServices.GetRequiredService<IServiceScopeFactory>());

            var factory = new CacheTestFactory(chat, settings.Providers[0], epoch, unknownTools ? credentials.Stamp : null);

            var wizard = CreateWizard(chat, settings, factory: factory, cacheValidator: validator);

            var request = BaseRequest() with { Model = "gpt-5", Prompt = "hello", SkipSpellRouting = true, DisableMcpTools = true };

            if (streaming)
            {
                await foreach (var _ in wizard.StreamPromptAsync(request, InvocationContexts.AttendedGlobalOnlySession(), CancellationToken.None)) { }
            }
            else
            {
                Assert.True((await wizard.ExecutePromptAsync(request, InvocationContexts.AttendedGlobalOnlySession(), CancellationToken.None)).IsSuccess);
            }

            Assert.Equal(0, authority.Reads);

            Assert.DoesNotContain("arcanum-dci-scope-v2:", chat.LastBufferedMessages[0].Text, StringComparison.Ordinal);

            if (enabled)
            {
                Assert.Null(chat.LastChatOptions!.RawRepresentationFactory);

                Assert.Equal(ordinary, chat.LastBufferedMessages[0].Text);
            }
            else
            {
                Assert.NotNull(chat.LastChatOptions!.RawRepresentationFactory);

                ordinary = chat.LastBufferedMessages[0].Text;
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CacheV2_provider_failover_binds_each_actual_client_scope(bool streaming)
    {
        ArcanumSettings settings = DefaultSettings();

        settings.Features.DynamicContextInjectionV2 = true;

        settings.Providers = [DefaultProvider() with { Name = "first" }, DefaultProvider() with { Name = "second" }];

        ScriptingChatClient first = new();

        if (streaming) first.EnqueueImmediateStreamFailure(new HttpRequestException("connection refused"));
        else first.EnqueueException(new HttpRequestException("connection refused"));

        ScriptingChatClient second = new();

        second.EnqueueText("answer");

        second.EnqueueStreamTokens("answer");

        using PromptCacheConfigurationEpoch epoch = new(new LongRestConfigurationEpochTests.NotifyingOptions(settings), Guid.NewGuid());

        var credentials = new PromptCacheDispatchValidatorTests.CredentialResolver();

        var authority = new PromptCacheDispatchValidatorTests.AuthorityProvider();

        using ServiceProvider cacheServices = new ServiceCollection().BuildServiceProvider();

        PromptCacheDispatchValidator validator = new(epoch, credentials, authority,
            new ModelTokenEstimator(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance)),
            cacheServices.GetRequiredService<IServiceScopeFactory>());

        CacheFallbackFactory factory = new(first, second, epoch, credentials.Stamp);

        var wizard = CreateWizard(first, settings, factory: factory, healthTracker: new ProviderHealthTracker(NullLogger<ProviderHealthTracker>.Instance), cacheValidator: validator);

        var request = BaseRequest() with { Prompt = "hello", SkipSpellRouting = true, DisableMcpTools = true, CliTerminalFormatting = true };

        if (streaming)
        {
            await foreach (var _ in wizard.StreamPromptAsync(request, InvocationContexts.AttendedGlobalOnlySession(), CancellationToken.None)) { }
        }
        else
        {
            Assert.True((await wizard.ExecutePromptAsync(request, InvocationContexts.AttendedGlobalOnlySession(), CancellationToken.None)).IsSuccess);
        }

        Assert.Equal(new[] { "first", "second" }, factory.Candidates);

        var firstCalls = streaming ? first.AllStreamingCalls : first.AllBufferedCalls;

        var secondCalls = streaming ? second.AllStreamingCalls : second.AllBufferedCalls;

        string firstPrompt = Assert.Single(firstCalls)[0].Text;

        string secondPrompt = Assert.Single(secondCalls)[0].Text;

        Assert.Contains("arcanum-dci-scope-v2:", firstPrompt, StringComparison.Ordinal);

        Assert.Contains("arcanum-dci-scope-v2:", secondPrompt, StringComparison.Ordinal);

        Assert.NotEqual(firstPrompt, secondPrompt);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public async Task CacheV2_late_optional_proof_loss_readmits_exact_ordinary_bytes(bool streaming, bool rejectOrdinary, bool continuation)
    {
        string? ordinary = null;

        foreach (bool enabled in new[] { false, true })
        {
            ArcanumSettings settings = DefaultSettings() with
            {
                DefaultModel = "gpt-5",
                FastModel = "gpt-5",
                Providers = [DefaultProvider() with { Endpoint = "https://api.openai.com/v1", Models = ["gpt-5"] }],
            };

            settings.Features.DynamicContextInjectionV2 = enabled;

            ScriptingChatClient chat = new();

            if (continuation)
            {
                if (streaming) chat.EnqueueStreamToolCall(ArcanumLocalTimeTool.ToolName);
                else chat.EnqueueToolCall(ArcanumLocalTimeTool.ToolName);
            }

            chat.EnqueueText("answer");

            chat.EnqueueStreamTokens("answer");

            using PromptCacheConfigurationEpoch epoch = new(new LongRestConfigurationEpochTests.NotifyingOptions(settings), Guid.NewGuid());

            var credentials = new PromptCacheDispatchValidatorTests.CredentialResolver();

            var reader = new PromptCacheDispatchValidatorTests.CampaignReader();

            bool proofAvailable = true;

            int credentialChecks = 0;

            credentials.BeforeCurrentReturn = () =>
            {
                credentialChecks++;

                if (credentialChecks == (continuation ? 2 : 1)) proofAvailable = false;
            };

            ServiceCollection registrations = [];

            registrations.AddScoped<ICampaignAvailabilityReader>(_ => proofAvailable ? reader : null!);

            registrations.AddScoped<ICampaignPathIdentityReader>(_ => proofAvailable ? reader : null!);

            using ServiceProvider cacheServices = registrations.BuildServiceProvider();

            LateFallbackEstimator estimator = new(() => !proofAvailable, rejectOrdinary);

            PromptCacheDispatchValidator validator = new(epoch, credentials, new PromptCacheDispatchValidatorTests.AuthorityProvider(),
                estimator, cacheServices.GetRequiredService<IServiceScopeFactory>());

            var factory = new CacheTestFactory(chat, settings.Providers[0], epoch, credentials.Stamp);

            var wizard = CreateWizard(chat, settings, factory: factory, cacheValidator: validator, modelTokenEstimator: estimator);

            var request = BaseRequest() with { Model = "gpt-5", Prompt = "hello", SkipSpellRouting = true, DisableMcpTools = true, CliTerminalFormatting = true };

            if (streaming)
            {
                await foreach (var _ in wizard.StreamPromptAsync(request, InvocationContexts.AttendedSession(reader.Id), CancellationToken.None)) { }
            }
            else
            {
                var result = await wizard.ExecutePromptAsync(request, InvocationContexts.AttendedSession(reader.Id), CancellationToken.None);

                Assert.Equal(!(enabled && rejectOrdinary), result.IsSuccess);
            }

            var calls = streaming ? chat.AllStreamingCalls : chat.AllBufferedCalls;

            if (!enabled)
            {
                Assert.Equal(continuation ? 2 : 1, calls.Count);

                ordinary = calls[^1][0].Text;

                continue;
            }

            Assert.False(proofAvailable);

            Assert.Equal(continuation ? 2 : 1, credentialChecks);

            Assert.NotEmpty(estimator.AfterProofLoss);

            Assert.All(estimator.AfterProofLoss, text => Assert.Equal(ordinary, text));

            if (rejectOrdinary)
            {
                Assert.Empty(calls);
            }
            else
            {
                Assert.Equal(continuation ? 2 : 1, calls.Count);

                Assert.Equal(ordinary, calls[^1][0].Text);

                if (continuation) Assert.Contains("arcanum-dci-scope-v2:", calls[0][0].Text, StringComparison.Ordinal);

                Assert.Null(chat.LastChatOptions!.RawRepresentationFactory);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CacheV2_public_wizard_preserves_canonical_integrity_failure_without_ordinary_retry(bool streaming)
    {
        ArcanumSettings settings = DefaultSettings();

        settings.Features.DynamicContextInjectionV2 = true;

        ScriptingChatClient chat = new();

        chat.EnqueueText("unexpected ordinary retry");

        chat.EnqueueStreamTokens("unexpected ordinary retry");

        using PromptCacheConfigurationEpoch epoch = new(new LongRestConfigurationEpochTests.NotifyingOptions(settings), Guid.NewGuid());

        var credentials = new PromptCacheDispatchValidatorTests.CredentialResolver();

        Error expected = new(ErrorCodes.Covenant.IntegrityFailure, "registered Campaign root identity is malformed");

        var reader = new PromptCacheDispatchValidatorTests.CampaignReader { PathFailure = expected };

        // These readers are isolated to cache validation; ordinary invocation preparation succeeds.
        ServiceCollection registrations = [];

        registrations.AddScoped<ICampaignAvailabilityReader>(_ => reader);

        registrations.AddScoped<ICampaignPathIdentityReader>(_ => reader);

        using ServiceProvider cacheServices = registrations.BuildServiceProvider();

        PromptCacheDispatchValidator validator = new(epoch, credentials, new PromptCacheDispatchValidatorTests.AuthorityProvider(),
            new ModelTokenEstimator(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance)),
            cacheServices.GetRequiredService<IServiceScopeFactory>());

        var factory = new CacheTestFactory(chat, settings.Providers[0], epoch, credentials.Stamp);

        var wizard = CreateWizard(chat, settings, factory: factory, cacheValidator: validator);

        var request = BaseRequest() with { Prompt = "hello", SkipSpellRouting = true, DisableMcpTools = true, CliTerminalFormatting = true };

        if (streaming)
        {
            List<IntelligenceEvent> events = [];

            await foreach (var frame in wizard.StreamPromptAsync(request, InvocationContexts.AttendedSession(reader.Id), CancellationToken.None))
            {
                events.Add(frame);
            }

            IntelligenceEvent failure = Assert.Single(events, frame => frame.Type == IntelligenceEventType.Error);

            Assert.Equal(expected.Message, failure.Message);

            Assert.Equal(expected.Code, failure.Data);
        }
        else
        {
            var result = await wizard.ExecutePromptAsync(request, InvocationContexts.AttendedSession(reader.Id), CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(expected, result.Error);
        }

        Assert.Equal(1, reader.PathReads);

        Assert.Empty(chat.AllBufferedCalls);

        Assert.Empty(chat.AllStreamingCalls);
    }

    [Theory]
    [InlineData(false, "outage")]
    [InlineData(true, "outage")]
    [InlineData(false, "rotation")]
    [InlineData(true, "rotation")]
    [InlineData(false, "local-rotation-outage")]
    [InlineData(true, "local-rotation-outage")]
    [InlineData(false, "canonical-outage")]
    [InlineData(true, "canonical-outage")]
    [InlineData(false, "configuration-outage")]
    [InlineData(true, "configuration-outage")]
    [InlineData(false, "initial-outage")]
    [InlineData(true, "initial-outage")]
    public async Task CacheV2_concrete_credential_proof_outage_preserves_ordinary_wizard_dispatch(bool streaming, string transition)
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcanum-cache-proof-" + Guid.NewGuid().ToString("N"));

        string[] names = ["ARCANUM_TEST_HOME", "ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT"];

        string?[] original = names.Select(global::System.Environment.GetEnvironmentVariable).ToArray();

        global::System.Environment.SetEnvironmentVariable(names[0], directory);

        global::System.Environment.SetEnvironmentVariable(names[1], "Testing");

        global::System.Environment.SetEnvironmentVariable(names[2], "Testing");

        Directory.CreateDirectory(directory);

        try
        {
            string? ordinary = null;

            foreach (bool enabled in new[] { false, true })
            {
                ArcanumSettings settings = DefaultSettings();

                settings.Features.DynamicContextInjectionV2 = enabled;

                settings.Providers[0].CredentialEnvironmentVariable = "ARCANUM_UNUSED_" + Guid.NewGuid().ToString("N");

                ControlledCacheOsStore os = new();

                using ProviderCredentialStore store = new(os, new EphemeralDataProtectionProvider());

                await store.SaveApiKeyAsync(settings.Providers[0].Name, "original-test-secret");

                ProviderApiKeyResolver credentials = new(store);

                if (transition == "initial-outage") os.Fail = true;

                var captured = await credentials.ResolveWithIdentityAsync(settings.Providers[0]);

                Assert.Equal("original-test-secret", captured.Secret);

                Assert.Equal(transition == "initial-outage", captured.Stamp is null);

                if (transition == "rotation") os.Secret = "rotated-test-secret";

                if (transition == "local-rotation-outage") await store.SaveApiKeyAsync(settings.Providers[0].Name, "rotated-test-secret");

                if (transition is "outage" or "local-rotation-outage" or "canonical-outage" or "configuration-outage") os.Fail = true;

                int readsBeforeDispatch = os.Reads;

                ScriptingChatClient chat = new();

                chat.EnqueueText("answer");

                chat.EnqueueStreamTokens("answer");

                using PromptCacheConfigurationEpoch epoch = new(new LongRestConfigurationEpochTests.NotifyingOptions(settings), Guid.NewGuid());

                var reader = new PromptCacheDispatchValidatorTests.CampaignReader();

                Error canonicalError = new(ErrorCodes.Covenant.IntegrityFailure, "canonical failure during credential outage");

                os.OnRead = () =>
                {
                    if (transition == "canonical-outage") reader.PathFailure = canonicalError;

                    if (transition == "configuration-outage") settings.Providers[0].Endpoint = "https://changed.test/v1";
                };

                ServiceCollection registrations = [];

                registrations.AddScoped<ICampaignAvailabilityReader>(_ => reader);

                registrations.AddScoped<ICampaignPathIdentityReader>(_ => reader);

                using ServiceProvider services = registrations.BuildServiceProvider();

                LateFallbackEstimator estimator = new(() => os.Reads > readsBeforeDispatch, false);

                PromptCacheDispatchValidator validator = new(epoch, credentials, new PromptCacheDispatchValidatorTests.AuthorityProvider(),
                    estimator, services.GetRequiredService<IServiceScopeFactory>());

                var factory = new CacheTestFactory(chat, settings.Providers[0], epoch, captured.Stamp);

                var wizard = CreateWizard(chat, settings, factory: factory, cacheValidator: validator, modelTokenEstimator: estimator);

                var request = BaseRequest() with { Prompt = "hello", SkipSpellRouting = true, DisableMcpTools = true, CliTerminalFormatting = true };

                bool refused = enabled && transition is "rotation" or "local-rotation-outage" or "canonical-outage" or "configuration-outage";

                var invocation = transition == "canonical-outage" ? InvocationContexts.AttendedSession(reader.Id) : InvocationContexts.AttendedGlobalOnlySession();

                if (streaming)
                {
                    List<IntelligenceEvent> frames = [];

                    await foreach (var frame in wizard.StreamPromptAsync(request, invocation, CancellationToken.None)) frames.Add(frame);

                    Assert.Equal(refused, frames.Any(frame => frame.Type == IntelligenceEventType.Error));

                    if (enabled && transition == "canonical-outage")
                    {
                        var failure = Assert.Single(frames, frame => frame.Type == IntelligenceEventType.Error);

                        Assert.Equal(canonicalError.Code, failure.Data);

                        Assert.Equal(canonicalError.Message, failure.Message);
                    }
                }
                else
                {
                    var result = await wizard.ExecutePromptAsync(request, invocation, CancellationToken.None);

                    Assert.Equal(!refused, result.IsSuccess);

                    if (enabled && transition == "canonical-outage") Assert.Equal(canonicalError, result.Error);
                }

                var calls = streaming ? chat.AllStreamingCalls : chat.AllBufferedCalls;

                if (refused) Assert.Empty(calls);
                else
                {
                    string sent = Assert.Single(calls)[0].Text;

                    Assert.DoesNotContain("arcanum-dci-scope-v2:", sent, StringComparison.Ordinal);

                    if (!enabled) ordinary = sent;
                    else Assert.Equal(ordinary, sent);
                }

                Assert.Equal(enabled && captured.Stamp is not null ? 1 : 0, os.Reads - readsBeforeDispatch);

                if (enabled && transition == "outage")
                {
                    Assert.NotEmpty(estimator.AfterProofLoss);

                    Assert.All(estimator.AfterProofLoss, text => Assert.Equal(ordinary, text));

                    Assert.Equal("original-test-secret", await credentials.ResolveAsync(settings.Providers[0]));
                }
            }
        }
        finally
        {
            for (int i = 0; i < names.Length; i++) global::System.Environment.SetEnvironmentVariable(names[i], original[i]);

            Directory.Delete(directory, true);
        }
    }

    private sealed class ControlledCacheOsStore : IOsCredentialStore
    {
        internal string? Secret { get; set; }

        internal bool Fail { get; set; }

        internal Action? OnRead { get; set; }

        internal int Reads { get; private set; }

        public bool IsAvailable => true;

        public OsCredentialStoreResult TryGet(string service, string account)
        {
            Reads++;

            OnRead?.Invoke();

            return Fail ? OsCredentialStoreResult.Failed("test backend unavailable")
                : Secret is null ? OsCredentialStoreResult.NotFound() : OsCredentialStoreResult.Ok(Secret);
        }

        public OsCredentialStoreResult Set(string service, string account, string secret)
        {
            Secret = secret;

            return OsCredentialStoreResult.Ok(secret);
        }

        public OsCredentialStoreResult Delete(string service, string account)
        {
            Secret = null;

            return OsCredentialStoreResult.Ok(string.Empty);
        }
    }

    private sealed class LateFallbackEstimator(Func<bool> proofLost, bool rejectOrdinary) : IModelTokenEstimator
    {
        private readonly ModelTokenEstimator inner = new(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance));

        internal List<string> AfterProofLoss { get; } = [];

        public ResolvedModelTokenizationProfile ResolveProfile(ProviderSettings provider, string canonicalModel) => inner.ResolveProfile(provider, canonicalModel);

        public ResolvedModelTokenizationProfile ResolveEffectiveProfile(ProviderSettings provider, string canonicalModel) => inner.ResolveEffectiveProfile(provider, canonicalModel);

        public TokenEstimate EstimateText(ProviderSettings provider, string canonicalModel, string? text) => inner.EstimateText(provider, canonicalModel, text);

        public ContextTokenBreakdown EstimateContext(ModelTokenizationRequest request)
        {
            ContextTokenBreakdown result = inner.EstimateContext(request);

            if (!proofLost()) return result;

            AfterProofLoss.Add(request.Messages[0].Text);

            return rejectOrdinary ? result with { InputTokens = 1_000_000, TotalTokens = 1_000_000 } : result;
        }
    }

    private sealed class CacheFallbackFactory(ScriptingChatClient first, ScriptingChatClient second,
        PromptCacheConfigurationEpoch epoch, RetroDownfall.Arcanum.Core.Security.ProviderCredentialStamp credential) : IChatClientFactory
    {
        internal List<string> Candidates { get; } = [];

        public Task<ChatClientLease> ResolveClientAsync(string? targetModel, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The health-tracked provider candidate path is required.");

        public Task<ChatClientLease> ResolveClientAsync(ProviderSettings provider, string resolvedModel, CancellationToken cancellationToken)
        {
            Candidates.Add(provider.Name);

            IChatClient client = provider.Name == "first" ? first : second;

            return Task.FromResult(new ChatClientLease(client, provider, resolvedModel, null,
                new PromptCacheClientIdentity(client, provider, resolvedModel, epoch.Capture().Stamp, credential)));
        }
    }

    private sealed class CacheTestFactory(IChatClient chat, ProviderSettings provider, PromptCacheConfigurationEpoch epoch,
        RetroDownfall.Arcanum.Core.Security.ProviderCredentialStamp? credential) : IChatClientFactory
    {
        public Task<ChatClientLease> ResolveClientAsync(string? targetModel, CancellationToken cancellationToken) =>
            Task.FromResult(new ChatClientLease(chat, provider, provider.Models[0].Name, null,
                credential is null ? null : new PromptCacheClientIdentity(chat, provider, provider.Models[0].Name, epoch.Capture().Stamp, credential)));

        public Task<ChatClientLease> ResolveClientAsync(ProviderSettings provider, string resolvedModel, CancellationToken cancellationToken) => ResolveClientAsync(resolvedModel, cancellationToken);
    }
}
