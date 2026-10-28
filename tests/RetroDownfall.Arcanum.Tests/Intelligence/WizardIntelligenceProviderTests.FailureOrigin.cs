using System.Data.Common;
using System.Diagnostics.Metrics;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;
using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Resilience;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    public static IEnumerable<object[]> FailureOriginCases()
    {
        foreach (bool streamed in new[] { false, true })
        {
            foreach (bool resilient in new[] { false, true })
            {
                foreach (string origin in new[] { "lease", "preparation-http", "preparation-sql", "sdk" })
                {
                    yield return [streamed, resilient, origin];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(FailureOriginCases))]
    public async Task FailureOrigin_only_the_actual_SDK_boundary_may_penalize_or_retry_a_provider(bool streamed, bool resilient, string origin)
    {
        const string canary = "PRIVATE_ORIGIN_ERROR /private/state?credential=HIDDEN";

        ArcanumSettings configuration = DefaultSettings() with
        {
            Providers =
            [
                DefaultProvider() with { Name = "native-first", Endpoint = "https://native-first.invalid" },
                DefaultProvider() with { Name = "native-second", Endpoint = "https://native-second.invalid" },
            ],
        };

        using OriginHttpFactory http = new(origin == "sdk", streamed, canary);

        OriginCredentialResolver credentials = new(origin == "lease", canary);

        ChatClientFactory factory = new(http, new TestOptionsMonitor<ArcanumSettings>(configuration), credentials);

        ProviderHealthTracker? health = resilient ? new(NullLogger<ProviderHealthTracker>.Instance) : null;

        int preparations = 0;

        FakeGrimoireRepository grimoire = new()
        {
            BeforeBegin = async token =>
            {
                preparations++;

                if (origin == "preparation-http")
                {
                    throw OriginTransportFailure(canary);
                }

                if (origin == "preparation-sql")
                {
                    SqliteNativeRuntime.Instance.Initialize();

                    await using ArcanumDbContext db = CreateWorkspaceChunksDbContext(Path.Combine(_workspace.Root, "origin-native.db"));

                    DbConnection connection = db.Database.GetDbConnection();

                    await connection.OpenAsync(token);

                    await using DbCommand command = connection.CreateCommand();

                    command.CommandText = "SELECT Value FROM missing_origin_probe";

                    _ = await command.ExecuteScalarAsync(token);
                }
            },
        };

        TestCapturingLogger<WizardIntelligenceProvider> logger = new();

        WizardIntelligenceProvider wizard = CreateWizard(new(), configuration, factory: factory,
            grimoire: grimoire, healthTracker: health, logger: logger);

        PingRequest request = BaseRequest() with
        {
            Prompt = "observe a bounded inference origin",
            Model = ModelName,
            SkipSpellRouting = true,
            DisableAllTools = true,
            MaxOutputTokens = 128
        };

        bool success;

        string? message;

        if (streamed)
        {
            List<IntelligenceEvent> frames = await CollectStreamAsync(wizard, request);

            IntelligenceEvent? failure = frames.SingleOrDefault(static frame => frame.Type == IntelligenceEventType.Error);

            success = failure is null && frames.Any(static frame => frame.Type == IntelligenceEventType.Result);

            message = failure?.Message;
        }
        else
        {
            Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(request, InvocationContexts.AttendedSession(), CancellationToken.None);

            success = result.IsSuccess;

            message = result.IsFailure ? result.Error.Message : null;

            if (success) Assert.Equal(7, result.Value.Usage?.TotalTokens);
        }

        Assert.Equal(origin == "sdk" ? resilient ? 2 : 1 : 0, http.Sends);

        if (origin != "sdk")
        {
            Assert.False(success);

            if (health is not null) Assert.Empty(health.GetAllStatuses());

            Assert.Equal(origin == "lease" ? 0 : 1, preparations);

            Assert.NotNull(message);

            Assert.DoesNotContain("unreachable", message, StringComparison.OrdinalIgnoreCase);

            Assert.DoesNotContain("Ensure the provider", message, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.True(resilient == success, $"Expected success={resilient}; actual={success}; message={message ?? "<none>"}");

            Assert.Equal(1, preparations);

            if (health is not null)
            {
                Assert.Contains(health.GetAllStatuses(), static status => status.ProviderName == "native-first" && status.ConsecutiveFailures == 1);
            }
        }

        if (origin == "sdk" && resilient)
        {
            Assert.DoesNotContain(logger.Entries, static entry => entry.Level == LogLevel.Error);
        }

        if (origin.StartsWith("preparation-", StringComparison.Ordinal))
        {
            Assert.Single(logger.Entries, static entry => entry.Level == LogLevel.Error);

            Assert.DoesNotContain(logger.Entries, static entry => entry.Message.StartsWith("Provider ", StringComparison.Ordinal));
        }

        Assert.All(logger.Entries, entry =>
        {
            Assert.Null(entry.Exception);

            Assert.DoesNotContain(canary, entry.Message, StringComparison.Ordinal);
        });

        Assert.DoesNotContain(canary, message ?? string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrigin_native_SDK_fixture_returns_the_actual_answer_and_usage(bool streamed)
    {
        ArcanumSettings configuration = DefaultSettings();

        using OriginHttpFactory http = new(false, streamed, "PRIVATE_CONTROL");

        ChatClientFactory factory = new(http, new TestOptionsMonitor<ArcanumSettings>(configuration), new OriginCredentialResolver(false, "PRIVATE_CONTROL"));

        using ChatClientLease lease = await factory.ResolveClientAsync(DefaultProvider(), ModelName, CancellationToken.None);

        ChatResponse response;

        if (streamed)
        {
            List<ChatResponseUpdate> updates = [];

            await foreach (ChatResponseUpdate update in lease.ChatClient.GetStreamingResponseAsync([new(ChatRole.User, "control")])) updates.Add(update);

            response = updates.ToChatResponse();
        }
        else
        {
            response = await lease.ChatClient.GetResponseAsync([new(ChatRole.User, "control")]);
        }

        Assert.Equal("SDK answer", response.Text);

        Assert.Equal(7, response.Usage?.TotalTokenCount);

        Assert.Equal(1, http.Sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrigin_local_finalization_after_actual_response_does_not_penalize_or_retry_provider(bool streamed)
    {
        ArcanumSettings configuration = DefaultSettings() with
        {
            Providers =
            [
                DefaultProvider() with { Name = "finalization-first", Endpoint = "https://native-first.invalid" },
                DefaultProvider() with { Name = "finalization-second", Endpoint = "https://native-second.invalid" },
            ],
        };

        using OriginHttpFactory http = new(false, streamed, "PRIVATE_FINALIZATION");

        ChatClientFactory factory = new(http, new TestOptionsMonitor<ArcanumSettings>(configuration), new OriginCredentialResolver(false, "PRIVATE_FINALIZATION"));

        ProviderHealthTracker health = new(NullLogger<ProviderHealthTracker>.Instance);

        int observed = 0;

        using MeterListener listener = new()
        {
            InstrumentPublished = static (instrument, active) =>
            {
                if (instrument.Name == "arcanum_provider_reported_input_tokens") active.EnableMeasurementEvents(instrument);
            },
        };

        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "provider" && tag.Value is "finalization-first" && Interlocked.Increment(ref observed) == 1)
                {
                    throw OriginTransportFailure("PRIVATE_LOCAL_METRIC /state?credential=HIDDEN");
                }
            }
        });

        listener.Start();

        RecordingTurnRunWriter writer = new();

        RecordingBudgetReservationService reservations = new();

        WizardIntelligenceProvider wizard = CreateWizard(new(), configuration, factory: factory, healthTracker: health,
            turnRunWriter: writer, budgetReservationService: reservations);

        PingRequest request = BaseRequest() with { Prompt = "finalize actual usage", Model = ModelName, SkipSpellRouting = true, DisableAllTools = true };

        TurnEngine engine = new(wizard);

        List<TurnEvent> events = [];

        await foreach (TurnEvent frame in engine.RunTurnAsync(new(request, InvocationContexts.AttendedSession(),
            streamed ? TurnResponseMode.Streaming : TurnResponseMode.Buffered), CancellationToken.None)) events.Add(frame);

        RunFailed terminal = Assert.Single(events.OfType<RunFailed>());

        string message = terminal.Error.Message;

        Assert.Equal("PipelineFailure", terminal.Reason.ToString());

        Assert.True(observed > 0);

        Assert.Equal(1, http.Sends);

        Assert.DoesNotContain(health.GetAllStatuses(), static status => status.ConsecutiveFailures != 0);

        Assert.DoesNotContain("unreachable", message, StringComparison.OrdinalIgnoreCase);

        var operation = Assert.Single(writer.Operations);

        Assert.Equal(5, operation.InputTokens);

        Assert.Equal(2, operation.OutputTokens);

        Assert.False(reservations.WasReleased);

        Assert.Equal(1, reservations.ReconcileCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrigin_actual_tool_compatibility_recovery_is_warning_not_terminal_error(bool streamed)
    {
        ScriptingChatClient chat = new();

        if (streamed)
        {
            chat.EnqueueImmediateStreamFailure(new InvalidOperationException("model does not support tools PRIVATE_PROVIDER_BODY"));

            chat.EnqueueStreamTokens("recovered");
        }
        else
        {
            chat.EnqueueException(new InvalidOperationException("model does not support tools PRIVATE_PROVIDER_BODY"));

            chat.EnqueueText("recovered");
        }

        TestCapturingLogger<WizardIntelligenceProvider> logger = new();

        RecordingTurnRunWriter writer = new();

        WizardIntelligenceProvider wizard = CreateWizard(chat, logger: logger, turnRunWriter: writer);

        PingRequest request = BaseRequest() with { Prompt = "recover once", SkipSpellRouting = true, DisableMcpTools = true };

        if (streamed)
        {
            List<IntelligenceEvent> frames = await CollectStreamAsync(wizard, request);

            Assert.Contains(frames, static frame => frame.Type == IntelligenceEventType.Result);

            Assert.Equal(2, chat.StreamingCallCount);
        }
        else
        {
            Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(request, InvocationContexts.AttendedSession(), CancellationToken.None);

            Assert.True(result.IsSuccess);

            Assert.Equal("recovered", result.Value.Text);

            Assert.Equal(2, chat.BufferedCallCount);
        }

        Assert.DoesNotContain(logger.Entries, static entry => entry.Level == LogLevel.Error);

        TestLogEntry report = Assert.Single(logger.Entries, static entry => entry.Level == LogLevel.Warning && entry.Message.Contains("tools", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(writer.RunId, report.Properties!.Single(static property => property.Key == "RunId").Value);

        Assert.IsType<Guid>(report.Properties!.Single(static property => property.Key == "SessionId").Value);

        Assert.True(Guid.TryParseExact(Assert.IsType<string>(report.Properties!.Single(static property => property.Key == "ModelCallId").Value), "N", out _));

        Assert.Equal(ModelCallExecutor.FailureOrigin.ModelInvocation, report.Properties!.Single(static property => property.Key == "FailureOrigin").Value);

        Assert.Contains("supportsTools=false", report.Message, StringComparison.Ordinal);

        Assert.All(logger.Entries, static entry =>
        {
            Assert.Null(entry.Exception);

            Assert.DoesNotContain("PRIVATE_PROVIDER_BODY", entry.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task FailureOrigin_buffered_recovery_has_a_separate_typed_native_notice_without_schema_warning()
    {
        ScriptingChatClient chat = new();

        chat.EnqueueException(new InvalidOperationException("model does not support tools PRIVATE_NOTICE_BODY"));

        chat.EnqueueText("recovered answer");

        WizardIntelligenceProvider wizard = CreateWizard(chat);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "recover visibly", SkipSpellRouting = true, DisableMcpTools = true },
            InvocationContexts.AttendedSession(), CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Empty(result.Value.Warnings);

        string json = JsonSerializer.Serialize(PromptResponseDto.From(result.Value), ArcanumJsonContext.Default.PromptResponseDto);

        using JsonDocument document = JsonDocument.Parse(json);

        Assert.True(document.RootElement.TryGetProperty("notices", out JsonElement notices));

        JsonElement notice = Assert.Single(notices.EnumerateArray());

        Assert.Equal(1, notice.GetProperty("code").GetInt32());

        Assert.Equal("This model does not support tools; continuing without local tools.", notice.GetProperty("message").GetString());

        Assert.DoesNotContain("PRIVATE_NOTICE_BODY", json, StringComparison.Ordinal);

        Assert.Equal(2, chat.BufferedCallCount);
    }

    [Theory]
    [InlineData(false, "same")]
    [InlineData(true, "same")]
    [InlineData(false, "reload")]
    [InlineData(false, "host")]
    [InlineData(false, "endpoint")]
    [InlineData(false, "model")]
    [InlineData(false, "missing")]
    public async Task FailureOrigin_compatibility_warning_uses_actual_host_snapshot_across_distinct_scopes(bool parallel, string mode)
    {
        ArcanumSettings configuration = DefaultSettings();

        OriginOptionsMonitor monitor = new(configuration);

        ServiceProvider BuildHost()
        {
            ServiceCollection services = new();

            services.AddArcanumApiServices(new ConfigurationBuilder().Build());

            services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(monitor);

            return services.BuildServiceProvider();
        }

        using ServiceProvider host = BuildHost();

        using ServiceProvider secondHost = BuildHost();

        using IServiceScope firstScope = host.CreateScope();

        using IServiceScope secondScope = (mode == "host" ? secondHost : host).CreateScope();

        TestCapturingLogger<WizardIntelligenceProvider> logger = new();

        async Task RecoverAsync(IServiceProvider services)
        {
            ScriptingChatClient chat = new();

            chat.EnqueueException(new InvalidOperationException("model does not support tools PRIVATE_DEDUP_BODY"));

            chat.EnqueueText("recovered");

            WizardIntelligenceProvider wizard = CreateWizard(chat, configuration, logger: logger, diagnosticsHost: services);

            Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
                BaseRequest() with { Prompt = "recover per turn", SkipSpellRouting = true, DisableMcpTools = true },
                InvocationContexts.AttendedSession(), CancellationToken.None);

            Assert.True(result.IsSuccess);

            Assert.Equal(2, chat.BufferedCallCount);

            Assert.Single(result.Value.Notices);

            Assert.Empty(result.Value.Warnings);
        }

        if (mode == "endpoint") monitor.Set(configuration with { Providers = [DefaultProvider() with { Endpoint = "https://other.invalid" }] });

        if (mode == "model") monitor.Set(configuration with { Providers = [DefaultProvider() with { Models = ["different-model"] }] });

        if (mode == "missing") monitor.Set(configuration with { Providers = [] });

        if (parallel)
        {
            await Task.WhenAll(RecoverAsync(firstScope.ServiceProvider), RecoverAsync(secondScope.ServiceProvider));
        }
        else
        {
            await RecoverAsync(firstScope.ServiceProvider);

            if (mode == "reload") monitor.Set(configuration with { Providers = configuration.Providers.Select(static provider => provider with { }).ToArray() });

            await RecoverAsync(secondScope.ServiceProvider);
        }

        Assert.DoesNotContain(logger.Entries, static entry => entry.Level == LogLevel.Error);

        Assert.Equal(mode == "same" ? 1 : 2,
            logger.Entries.Count(static entry => entry.Level == LogLevel.Warning && entry.Message.Contains("tools", StringComparison.OrdinalIgnoreCase)));

        Assert.All(logger.Entries, static entry => Assert.DoesNotContain("PRIVATE_DEDUP_BODY", entry.Message, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrigin_held_recovery_reports_against_its_captured_snapshot_after_reload(bool prewarm)
    {
        ArcanumSettings configuration = DefaultSettings();

        OriginOptionsMonitor monitor = new(configuration);

        ServiceCollection services = new();

        services.AddArcanumApiServices(new ConfigurationBuilder().Build());

        services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(monitor);

        using ServiceProvider host = services.BuildServiceProvider();

        using IServiceScope scope = host.CreateScope();

        TestCapturingLogger<WizardIntelligenceProvider> logger = new();

        PingRequest request = BaseRequest() with { Prompt = "recover captured snapshot", SkipSpellRouting = true, DisableMcpTools = true };

        async Task RecoverAsync()
        {
            ScriptingChatClient ordinary = new();

            ordinary.EnqueueException(new InvalidOperationException("model does not support tools"));

            ordinary.EnqueueText("recovered");

            WizardIntelligenceProvider wizard = CreateWizard(ordinary, configuration, logger: logger, diagnosticsHost: scope.ServiceProvider);

            Assert.True((await wizard.ExecutePromptAsync(request, InvocationContexts.AttendedSession(), CancellationToken.None)).IsSuccess);
        }

        if (prewarm) await RecoverAsync();

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        ScriptingChatClient held = new();

        held.EnqueueBufferedResponder(async token =>
        {
            entered.TrySetResult();

            await release.Task.WaitAsync(token);

            throw new InvalidOperationException("model does not support tools");
        });

        held.EnqueueText("held recovery");

        WizardIntelligenceProvider heldWizard = CreateWizard(held, configuration, logger: logger, diagnosticsHost: scope.ServiceProvider);

        Task<Result<PromptTurnResult>> pending = heldWizard.ExecutePromptAsync(request, InvocationContexts.AttendedSession(), CancellationToken.None);

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            monitor.Set(configuration with { Providers = configuration.Providers.Select(static provider => provider with { }).ToArray() });

            await RecoverAsync();
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.True((await pending.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

        Assert.Equal(2, held.BufferedCallCount);

        Assert.Equal(2, logger.Entries.Count(static entry => entry.Level == LogLevel.Warning && entry.Message.Contains("tools", StringComparison.OrdinalIgnoreCase)));

        Assert.DoesNotContain(logger.Entries, static entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task FailureOrigin_distinct_actual_structured_retry_responses_keep_distinct_numeric_receipts()
    {
        using OriginHttpFactory http = new(false, false, "PRIVATE_STRUCTURED", responses: ["not JSON", "{\"name\":\"accepted\"}"]);

        ArcanumSettings configuration = DefaultSettings();

        ChatClientFactory factory = new(http, new TestOptionsMonitor<ArcanumSettings>(configuration), new OriginCredentialResolver(false, "PRIVATE_STRUCTURED"));

        RecordingTurnRunWriter writer = new();

        RecordingBudgetReservationService reservations = new();

        WizardIntelligenceProvider wizard = CreateWizard(new(), configuration, factory: factory, turnRunWriter: writer, budgetReservationService: reservations);

        using JsonDocument schema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"}},\"required\":[\"name\"],\"additionalProperties\":false}");

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "two physical calls",
                Model = ModelName,
                SkipSpellRouting = true,
                DisableAllTools = true,
                ResponseFormat = "json_schema",
                ResponseFormatJsonSchema = schema.RootElement.Clone()
            },
            InvocationContexts.AttendedSession(), CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(2, http.Sends);

        Assert.Collection(writer.Operations,
            first =>
            {
                Assert.Equal("chat", first.Purpose);

                Assert.Equal((5L, 2L), (first.InputTokens, first.OutputTokens));
            },
            second =>
            {
                Assert.Equal("structured-output-retry", second.Purpose);

                Assert.Equal((5L, 2L), (second.InputTokens, second.OutputTokens));
            });

        Assert.Equal(2, writer.RecordAttempts);

        Assert.Equal(1, reservations.ReconcileCount);

        Assert.False(reservations.WasReleased);
    }

    [Fact]
    public async Task FailureOrigin_cancellation_during_numeric_handoff_retains_actual_usage_once()
    {
        using CancellationTokenSource caller = new();

        using OriginHttpFactory http = new(false, false, "PRIVATE_POST_RESPONSE_CANCEL");

        ArcanumSettings configuration = DefaultSettings();

        ChatClientFactory factory = new(http, new TestOptionsMonitor<ArcanumSettings>(configuration), new OriginCredentialResolver(false, "PRIVATE_POST_RESPONSE_CANCEL"));

        RecordingTurnRunWriter writer = new() { CancelBeforeRecord = caller };

        RecordingBudgetReservationService reservations = new();

        WizardIntelligenceProvider wizard = CreateWizard(new(), configuration, factory: factory, turnRunWriter: writer, budgetReservationService: reservations);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "cancel after actual response", Model = ModelName, SkipSpellRouting = true, DisableAllTools = true },
            InvocationContexts.AttendedSession(), caller.Token));

        var operation = Assert.Single(writer.Operations);

        Assert.Equal((5L, 2L), (operation.InputTokens, operation.OutputTokens));

        Assert.Equal(1, writer.RecordAttempts);

        Assert.Equal(1, http.Sends);

        Assert.True(await WaitUntilAsync(() => reservations.ReconcileCount == 1, TimeSpan.FromSeconds(5)));

        Assert.False(reservations.WasReleased);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrigin_direct_or_decorated_executor_retains_its_own_default_error_reporting(bool decorated)
    {
        TestCapturingLogger<ModelCallExecutor> nativeLogger = new();

        ModelCallExecutor native = decorated
            ? new(new ModelTokenEstimator(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance)), null, (ILogger)nativeLogger)
            : new(logger: (ILogger)nativeLogger);

        ScriptingChatClient chat = new();

        chat.EnqueueException(new InvalidOperationException("model does not support tools PRIVATE_DIRECT_BODY"));

        if (decorated)
        {
            chat.EnqueueText("recovered");

            WizardIntelligenceProvider wizard = CreateWizard(chat, executor: new OriginExecutorDecorator(native));

            Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
                BaseRequest() with { Prompt = "opaque executor", SkipSpellRouting = true, DisableMcpTools = true },
                InvocationContexts.AttendedSession(), CancellationToken.None);

            Assert.True(result.IsSuccess);

            Assert.Equal(2, chat.BufferedCallCount);
        }
        else
        {
            ModelCallOutcome result = await native.ExecuteBufferedAsync(chat, [new(ChatRole.User, "direct")], new(),
                UnrestrictedTurnBudget.Instance, ModelCallPurpose.MainInference, CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(1, chat.BufferedCallCount);
        }

        TestLogEntry entry = Assert.Single(nativeLogger.Entries, static entry => entry.Level == LogLevel.Error);

        Assert.Null(entry.Exception);

        Assert.DoesNotContain("PRIVATE_DIRECT_BODY", entry.Message, StringComparison.Ordinal);
    }

    private sealed class OriginExecutorDecorator(IModelCallExecutor inner) : IModelCallExecutor
    {
        public Task<ModelCallOutcome> ExecuteBufferedAsync(IChatClient client, IList<ChatMessage> messages, ChatOptions options,
            ITurnBudget budget, ModelCallPurpose purpose, CancellationToken cancellationToken, ModelCallContext? context = null) =>
            inner.ExecuteBufferedAsync(client, messages, options, budget, purpose, cancellationToken, context);

        public IAsyncEnumerable<ModelCallUpdate> ExecuteStreamingAsync(IChatClient client, IList<ChatMessage> messages, ChatOptions options,
            ITurnBudget budget, ModelCallPurpose purpose, CancellationToken cancellationToken, ModelCallContext? context = null) =>
            inner.ExecuteStreamingAsync(client, messages, options, budget, purpose, cancellationToken, context);
    }

    private sealed class OriginOptionsMonitor(ArcanumSettings current) : IOptionsMonitor<ArcanumSettings>
    {
        public ArcanumSettings CurrentValue { get; private set; } = current;

        public void Set(ArcanumSettings value) => CurrentValue = value;

        public ArcanumSettings Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<ArcanumSettings, string?> listener) => new OriginNoopDisposable();
    }

    private sealed class OriginNoopDisposable : IDisposable
    {
        public void Dispose() { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrigin_unconfigured_lease_labels_cannot_disclose_caller_text_in_local_diagnostics(bool streamed)
    {
        const string canary = "PRIVATE_FORGED_PROVIDER_LABEL";

        ScriptingChatClient chat = new();

        FakeChatClientFactory factory = new(chat, DefaultProvider() with { Name = canary });

        FakeGrimoireRepository grimoire = new()
        {
            BeforeBegin = _ => throw OriginTransportFailure("PRIVATE_LOCAL_EXCEPTION_BODY"),
        };

        TestCapturingLogger<WizardIntelligenceProvider> logger = new();

        WizardIntelligenceProvider wizard = CreateWizard(chat, grimoire: grimoire, factory: factory, logger: logger);

        PingRequest request = BaseRequest() with { Prompt = "refuse local fault", SkipSpellRouting = true, DisableAllTools = true };

        if (streamed)
        {
            Assert.Contains(await CollectStreamAsync(wizard, request), static frame => frame.Type == IntelligenceEventType.Error);
        }
        else
        {
            Assert.True((await wizard.ExecutePromptAsync(request, InvocationContexts.AttendedSession(), CancellationToken.None)).IsFailure);
        }

        Assert.Equal(0, chat.BufferedCallCount + chat.StreamingCallCount);

        TestLogEntry entry = Assert.Single(logger.Entries, static entry => entry.Level == LogLevel.Error);

        Assert.DoesNotContain(canary, entry.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("PRIVATE_LOCAL_EXCEPTION_BODY", entry.Message, StringComparison.Ordinal);

        Assert.Equal("Unknown", entry.Properties!.Single(static property => property.Key == "Provider").Value);

        Assert.Null(entry.Exception);
    }

    [Fact]
    public async Task FailureOrigin_disposed_native_observation_cannot_be_reactivated()
    {
        ScriptingChatClient chat = new();

        chat.EnqueueText("must not be sent");

        ModelCallExecutor native = new();

        using ModelCallExecutor.FailureObservation observation = new();

        observation.Dispose();

        await Assert.ThrowsAsync<InvalidOperationException>(() => native.ExecuteObservedBufferedAsync(chat,
            [new(ChatRole.User, "refuse disposed observation")], new(), UnrestrictedTurnBudget.Instance,
            ModelCallPurpose.MainInference, CancellationToken.None, null, observation, null));

        Assert.Equal(0, chat.BufferedCallCount);
    }

    [Theory]
    [InlineData(false, "lease")]
    [InlineData(true, "lease")]
    [InlineData(false, "preparation-http")]
    [InlineData(true, "preparation-http")]
    [InlineData(false, "sdk")]
    [InlineData(true, "sdk")]
    public async Task FailureOrigin_caller_cancellation_never_penalizes_or_falls_back(bool streamed, string origin)
    {
        using CancellationTokenSource caller = new();

        ArcanumSettings configuration = DefaultSettings() with
        {
            Providers =
            [
                DefaultProvider() with { Name = "native-first", Endpoint = "https://native-first.invalid" },
                DefaultProvider() with { Name = "native-second", Endpoint = "https://native-second.invalid" },
            ],
        };

        using OriginHttpFactory http = new(false, streamed, "PRIVATE_CANCELLED_ORIGIN", origin == "sdk" ? caller : null);

        OriginCredentialResolver credentials = new(origin == "lease", "PRIVATE_CANCELLED_ORIGIN", origin == "lease" ? caller : null);

        ChatClientFactory factory = new(http, new TestOptionsMonitor<ArcanumSettings>(configuration), credentials);

        ProviderHealthTracker health = new(NullLogger<ProviderHealthTracker>.Instance);

        FakeGrimoireRepository grimoire = new()
        {
            BeforeBegin = token =>
            {
                if (origin == "preparation-http")
                {
                    caller.Cancel();

                    token.ThrowIfCancellationRequested();
                }

                return Task.CompletedTask;
            },
        };

        WizardIntelligenceProvider wizard = CreateWizard(new(), configuration, factory: factory, grimoire: grimoire, healthTracker: health);

        PingRequest request = BaseRequest() with { Prompt = "cancel the actual origin", Model = ModelName, SkipSpellRouting = true, DisableAllTools = true };

        OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (streamed)
            {
                await foreach (IntelligenceEvent _ in wizard.StreamPromptAsync(request, InvocationContexts.AttendedSession(), caller.Token)) { }
            }
            else
            {
                _ = await wizard.ExecutePromptAsync(request, InvocationContexts.AttendedSession(), caller.Token);
            }
        });

        Assert.True(caller.IsCancellationRequested);

        if (origin != "sdk") Assert.Equal(caller.Token, failure.CancellationToken);

        Assert.Equal(origin == "sdk" ? 1 : 0, http.Sends);

        Assert.Empty(health.GetAllStatuses());
    }

    private static HttpRequestException OriginTransportFailure(string canary) =>
        new(canary, new SocketException((int)SocketError.ConnectionRefused));

    private sealed class OriginCredentialResolver(bool refuseFirst, string canary, CancellationTokenSource? caller = null) : IProviderApiKeyResolver
    {
        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken = default)
        {
            if (refuseFirst && provider.Name == "native-first")
            {
                caller?.Cancel();

                cancellationToken.ThrowIfCancellationRequested();

                throw OriginTransportFailure(canary);
            }

            return Task.FromResult<string?>(null);
        }
    }

    private sealed class OriginHttpFactory(bool failFirst, bool streamed, string canary, CancellationTokenSource? caller = null, string[]? responses = null) : IHttpClientFactory, IDisposable
    {
        private readonly List<HttpClient> _clients = [];

        private readonly Queue<string>? _responses = responses is null ? null : new(responses);

        private readonly bool _failFirst = failFirst;

        private readonly bool _streamed = streamed;

        private readonly string _canary = canary;

        private readonly CancellationTokenSource? _caller = caller;

        public int Sends { get; private set; }

        public HttpClient CreateClient(string name)
        {
            HttpClient client = new(new Handler(this));

            _clients.Add(client);

            return client;
        }

        public void Dispose()
        {
            foreach (HttpClient client in _clients) client.Dispose();
        }

        private sealed class Handler(OriginHttpFactory owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Sends++;

                owner._caller?.Cancel();

                cancellationToken.ThrowIfCancellationRequested();

                if (owner._failFirst && request.RequestUri!.Host == "native-first.invalid") throw OriginTransportFailure(owner._canary);

                string completion = """
                    {"id":"origin","object":"chat.completion","created":1,"model":"model","choices":[{"index":0,"message":{"role":"assistant","content":"SDK answer"},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":2,"total_tokens":7}}
                    """;

                const string stream = """
                    data: {"id":"origin","object":"chat.completion.chunk","created":1,"model":"model","choices":[{"index":0,"delta":{"role":"assistant","content":"SDK answer"},"finish_reason":null}]}

                    data: {"id":"origin","object":"chat.completion.chunk","created":1,"model":"model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

                    data: {"id":"origin","object":"chat.completion.chunk","created":1,"model":"model","choices":[],"usage":{"prompt_tokens":5,"completion_tokens":2,"total_tokens":7}}

                    data: [DONE]

                    """;

                if (owner._responses is not null)
                {
                    completion = completion.Replace("\"SDK answer\"", JsonSerializer.Serialize(owner._responses.Dequeue(), AdHocJson.Options), StringComparison.Ordinal);
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = ProviderSdkTestTransport.Content(owner._streamed ? stream : completion, owner._streamed ? "text/event-stream" : "application/json"),
                });
            }
        }
    }
}
