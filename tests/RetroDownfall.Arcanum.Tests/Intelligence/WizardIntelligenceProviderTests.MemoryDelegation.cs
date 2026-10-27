using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Resilience;
using RetroDownfall.Arcanum.Infrastructure.Resilience;
using RetroDownfall.Arcanum.Api.Intelligence.MemoryDelegation;
using RetroDownfall.Arcanum.Api.Intelligence.Subagents;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    [SkippableFact]
    public async Task MemoryDelegation_real_buffered_iterator_executes_only_its_explicit_bound_selector()
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();

        await using var root = await h.PrepareAsync();

        var request = BaseRequest() with { Prompt = "parent", SkipSpellRouting = true, DisableMcpTools = true, StatelessMessages = [new CoreChatMessage("user", "parent")] };

        var binding = new MemoryDelegationRootBinding(request, h.Source.Invocation, h.RootId, root, h.Coordinator);

        ScriptingChatClient chat = new();

        chat.EnqueueToolCall(ArcanumBuiltInToolNames.DelegateTask, "child-1", new()
        {
            ["prompt"] = "actual child",
            ["max_tokens"] = 100,
            ["memory_grant"] = "memory",
        });

        chat.EnqueueText("parent complete");

        MemoryDelegationCapturingRunner runner = new();

        var wizard = CreateWizard(chat, subagentRunner: runner, covenantDispatch: WizardIntelligenceProviderCovenantTests.EmptyDisclosureGate());

        using var scope = MemoryDelegationRootBinding.Enter(binding);

        var result = await wizard.ExecutePromptAsync(request, h.Source.Invocation, default);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.ToString() : "");

        Assert.True(root.IsConsumed);

        Assert.NotNull(runner.Request!.MemoryDelegation);

        Assert.Equal("actual child", runner.Request.Prompt);

        Assert.Equal(2, chat.BufferedCallCount);

        Assert.Contains(chat.LastBufferedMessages, message => message.Role == ChatRole.Tool
            && message.Contents.OfType<FunctionResultContent>().Any(content => content.Result?.ToString() == "child summary"));
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MemoryDelegation_bound_execution_obeys_health_selected_support(bool firstSupportsTools)
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();

        await using var root = await h.PrepareAsync();

        var request = BaseRequest() with { Prompt = "parent", SkipSpellRouting = true, DisableMcpTools = true, StatelessMessages = [new CoreChatMessage("user", "parent")] };

        var first = DelegationProvider("first", firstSupportsTools);

        var healthy = DelegationProvider("healthy", !firstSupportsTools);

        var settings = DefaultSettings() with { Providers = [first, healthy] };

        var health = new ProviderHealthTracker(NullLogger<ProviderHealthTracker>.Instance);

        for (int count = 0; count < 10; count++) health.MarkFailed("first");

        ScriptingChatClient chat = new();

        chat.EnqueueText("parent complete");

        var factory = new DelegationCandidateFactory(first, chat, chat);

        var wizard = CreateWizard(chat, settings, factory: factory, healthTracker: health, subagentRunner: new MemoryDelegationCapturingRunner());

        using var scope = MemoryDelegationRootBinding.Enter(new(request, h.Source.Invocation, h.RootId, root, h.Coordinator));

        var result = await wizard.ExecutePromptAsync(request, h.Source.Invocation, default);

        Assert.Equal(!firstSupportsTools, result.IsSuccess);

        Assert.Equal(firstSupportsTools ? [] : new[] { "healthy" }, factory.Leases);

        Assert.Equal(firstSupportsTools ? 0 : 1, chat.BufferedCallCount);

        if (firstSupportsTools) Assert.Equal(ErrorCodes.MemoryDelegation.Unavailable, result.Error.Code);
    }

    [SkippableTheory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task MemoryDelegation_candidate_fallback_preserves_eligible_bound_and_ordinary_execution(bool bound, bool fallbackSupportsTools)
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();

        await using var root = await h.PrepareAsync();

        var request = BaseRequest() with { Prompt = "parent", SkipSpellRouting = true, DisableMcpTools = true, StatelessMessages = [new CoreChatMessage("user", "parent")] };

        var first = DelegationProvider("first", true);

        var fallback = DelegationProvider("fallback", fallbackSupportsTools);

        var settings = DefaultSettings() with { Providers = [first, fallback] };

        ScriptingChatClient firstChat = new();

        firstChat.EnqueueException(new HttpRequestException("connection refused"));

        ScriptingChatClient fallbackChat = new();

        if (bound && fallbackSupportsTools)
        {
            fallbackChat.EnqueueToolCall(ArcanumBuiltInToolNames.DelegateTask, "child-1", new()
            {
                ["prompt"] = "child after fallback",
                ["max_tokens"] = 100,
                ["memory_grant"] = "memory",
            });
        }

        fallbackChat.EnqueueText("parent complete");

        var factory = new DelegationCandidateFactory(first, firstChat, fallbackChat);

        MemoryDelegationCapturingRunner runner = new();

        var health = new ProviderHealthTracker(NullLogger<ProviderHealthTracker>.Instance);

        var wizard = CreateWizard(firstChat, settings, factory: factory, healthTracker: health, subagentRunner: runner, covenantDispatch: WizardIntelligenceProviderCovenantTests.EmptyDisclosureGate());

        using var scope = bound ? MemoryDelegationRootBinding.Enter(new(request, h.Source.Invocation, h.RootId, root, h.Coordinator)) : null;

        var result = await wizard.ExecutePromptAsync(request, h.Source.Invocation, default);

        Assert.Equal(!bound || fallbackSupportsTools, result.IsSuccess);

        Assert.Equal(bound && !fallbackSupportsTools ? new[] { "first" } : new[] { "first", "fallback" }, factory.Leases);

        Assert.Equal(1, firstChat.BufferedCallCount);

        Assert.Equal(bound && !fallbackSupportsTools ? 0 : bound ? 2 : 1, fallbackChat.BufferedCallCount);

        Assert.Equal(bound && fallbackSupportsTools, root.IsConsumed);

        if (bound && fallbackSupportsTools) Assert.NotNull(runner.Request!.MemoryDelegation);
    }

    [SkippableFact]
    public async Task MemoryDelegation_fallback_configuration_change_refuses_before_lease()
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();

        await using var root = await h.PrepareAsync();

        var request = BaseRequest() with { Prompt = "parent", SkipSpellRouting = true, DisableMcpTools = true, StatelessMessages = [new CoreChatMessage("user", "parent")] };

        var first = DelegationProvider("first", true);

        var fallback = DelegationProvider("fallback", true);

        var settings = DefaultSettings() with { Providers = [first, fallback] };

        ScriptingChatClient firstChat = new() { BeforeCall = () => fallback.Models[0].SupportsTools = false };

        firstChat.EnqueueException(new HttpRequestException("connection refused"));

        ScriptingChatClient fallbackChat = new();

        fallbackChat.EnqueueText("must not send");

        var factory = new DelegationCandidateFactory(first, firstChat, fallbackChat);

        var health = new ProviderHealthTracker(NullLogger<ProviderHealthTracker>.Instance);

        var wizard = CreateWizard(firstChat, settings, factory: factory, healthTracker: health, subagentRunner: new MemoryDelegationCapturingRunner());

        using var scope = MemoryDelegationRootBinding.Enter(new(request, h.Source.Invocation, h.RootId, root, h.Coordinator));

        var result = await wizard.ExecutePromptAsync(request, h.Source.Invocation, default);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.MemoryDelegation.Unavailable, result.Error.Code);

        Assert.Equal(new[] { "first" }, factory.Leases);

        Assert.Equal(0, fallbackChat.BufferedCallCount);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MemoryDelegation_concrete_lease_support_recheck_prevents_provider_send(bool resilience)
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();

        await using var root = await h.PrepareAsync();

        var request = BaseRequest() with { Prompt = "parent", SkipSpellRouting = true, DisableMcpTools = true, StatelessMessages = [new CoreChatMessage("user", "parent")] };

        var provider = DelegationProvider("first", true);

        var settings = DefaultSettings() with { Providers = [provider] };

        ScriptingChatClient chat = new();

        chat.EnqueueText("must not send");

        var factory = new DelegationCandidateFactory(provider, chat, chat) { LeaseProvider = provider with { Models = [new(ModelName, SupportsTools: false)] } };

        var health = new ProviderHealthTracker(NullLogger<ProviderHealthTracker>.Instance);

        var wizard = CreateWizard(chat, settings, factory: factory, healthTracker: resilience ? health : null, subagentRunner: new MemoryDelegationCapturingRunner());

        using var scope = MemoryDelegationRootBinding.Enter(new(request, h.Source.Invocation, h.RootId, root, h.Coordinator));

        var result = await wizard.ExecutePromptAsync(request, h.Source.Invocation, default);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.MemoryDelegation.Unavailable, result.Error.Code);

        Assert.Equal(0, chat.BufferedCallCount);

        Assert.Single(factory.Leases);
    }

    [SkippableFact]
    public async Task MemoryDelegation_runtime_tool_rejection_refuses_toolless_restart()
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();

        await using var root = await h.PrepareAsync();

        var request = BaseRequest() with { Prompt = "parent", SkipSpellRouting = true, DisableMcpTools = true, StatelessMessages = [new CoreChatMessage("user", "parent")] };

        ScriptingChatClient chat = new();

        chat.EnqueueException(new InvalidOperationException("model does not support tools"));

        chat.EnqueueText("must not retry without delegation");

        var wizard = CreateWizard(chat, subagentRunner: new MemoryDelegationCapturingRunner());

        using var scope = MemoryDelegationRootBinding.Enter(new(request, h.Source.Invocation, h.RootId, root, h.Coordinator));

        var result = await wizard.ExecutePromptAsync(request, h.Source.Invocation, default);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.MemoryDelegation.Unavailable, result.Error.Code);

        Assert.Equal(1, chat.BufferedCallCount);

        Assert.False(root.IsConsumed);
    }

    [SkippableTheory]
    [InlineData(false, false, false, false, false, false, true)]
    [InlineData(false, true, false, false, false, false, true)]
    [InlineData(false, true, true, false, false, false, true)]
    [InlineData(true, false, false, false, false, false, true)]
    [InlineData(true, true, false, false, false, false, true)]
    [InlineData(true, true, true, false, false, false, true)]
    [InlineData(false, false, false, true, false, false, true)]
    [InlineData(true, false, false, true, false, false, true)]
    [InlineData(true, false, false, false, true, false, true)]
    [InlineData(true, true, false, false, true, false, true)]
    [InlineData(false, false, false, false, false, false, false)]
    [InlineData(true, false, false, false, false, false, false)]
    [InlineData(true, false, false, false, false, true, true)]
    public async Task MemoryDelegation_known_provider_usage_commits_once_even_when_publication_is_revoked(bool streaming, bool revoke, bool cancelCaller, bool fallback, bool splitUsage, bool failAccounting, bool reportUsage)
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();
        await using var root = await h.PrepareAsync();
        var child = (await h.Coordinator.ConsumeAsync(root, h.RootId, Guid.NewGuid(), h.Digest)).Value;
        await child.DisposeAsync();
        using var caller = new CancellationTokenSource();
        var request = BaseRequest() with
        {
            Prompt = "parent",
            SkipSpellRouting = true,
            DisableMcpTools = true,
            StatelessMessages = [new CoreChatMessage("user", "parent")]
        };
        var binding = new MemoryDelegationRootBinding(request, h.Source.Invocation, h.RootId, root, h.Coordinator);
        var usage = new UsageDetails { InputTokenCount = 11, OutputTokenCount = 7, TotalTokenCount = 18 };
        const string marker = "protected late provider text";
        void CompleteProvider()
        {
            if (revoke) h.Time.Advance(TimeSpan.FromMinutes(5));
            if (cancelCaller) caller.Cancel();
        }
        async IAsyncEnumerable<ChatResponseUpdate> ProviderStream()
        {
            await Task.CompletedTask;
            if (splitUsage)
                yield return new(ChatRole.Assistant, [new UsageContent(new() { InputTokenCount = 11 })]);
            CompleteProvider();
            var update = new ChatResponseUpdate(ChatRole.Assistant, marker);
            if (reportUsage) update.Contents.Add(new UsageContent(splitUsage ? new() { OutputTokenCount = 7, TotalTokenCount = 18 } : usage));
            yield return update;
        }

        var chat = new ScriptingChatClient();
        if (fallback)
        {
            chat.EnqueueException(new HttpRequestException("transport failure"));
            chat.EnqueueImmediateStreamFailure(new HttpRequestException("transport failure"));
        }
        chat.EnqueueBufferedResponder(_ =>
        {
            CompleteProvider();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, marker)) { Usage = reportUsage ? usage : null });
        });
        chat.EnqueueStreamResponder(_ => ProviderStream());
        var runs = new BlockingDelegationUsageWriter(new TurnRunWriter(h.Source.Saga.Context), failAccounting);
        var reservations = new RecordingBudgetReservationService();
        var firstProvider = DelegationProvider("first", true);
        var fallbackProvider = DelegationProvider("fallback", true);
        var factory = new DelegationCandidateFactory(firstProvider, chat, chat);
        var defaults = DefaultSettings();
        var pricedSettings = defaults with
        {
            Providers = [firstProvider, fallbackProvider],
            Cost = defaults.Cost with { Pricing = new PricingSettings { DefaultPricing = new ModelPricingEntry { InputPer1M = 1m, OutputPer1M = 2m } } },
        };
        var wizard = CreateWizard(chat, pricedSettings,
            factory: factory, healthTracker: new ProviderHealthTracker(NullLogger<ProviderHealthTracker>.Instance),
            turnRunWriter: runs, budgetReservationService: reservations,
            covenantDispatch: WizardIntelligenceProviderCovenantTests.EmptyDisclosureGate(), subagentRunner: new MemoryDelegationCapturingRunner());
        using var scope = streaming
            ? SubagentExecutionAmbient.EnterChild(new(10000, null), protection: binding.CaptureOutputProtection())
            : MemoryDelegationRootBinding.Enter(binding);
        List<IntelligenceEvent> frames = [];
        string? returned = null;
        async Task ExecuteAsync()
        {
            try
            {
                if (streaming)
                {
                    await foreach (var frame in wizard.StreamPromptAsync(request, ArcanumInvocationContext.None, caller.Token)) frames.Add(frame);
                }
                else
                {
                    var result = await wizard.ExecutePromptAsync(request, h.Source.Invocation, caller.Token);
                    if (result.IsSuccess) returned = result.Value.Text;
                }
            }
            catch (OperationCanceledException) when (cancelCaller) { }
            catch (InvalidOperationException) when (failAccounting) { }
        }

        var executing = ExecuteAsync();
        Task<RetroDownfall.Arcanum.Core.Intelligence.MemoryDelegation.MemoryDelegationMutation>? mutation = null;
        try
        {
            if (reportUsage)
            {
                await runs.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.False(executing.IsCompleted);
            }

            if (revoke)
            {
                mutation = h.Coordinator.BeginMutationAsync().AsTask();
                Assert.False(mutation.IsCompleted);
            }
        }
        finally { runs.Release.TrySetResult(); }
        await executing.WaitAsync(TimeSpan.FromSeconds(10));
        if (mutation is not null) await (await mutation.WaitAsync(TimeSpan.FromSeconds(10))).DisposeAsync();
        await using var command = h.Source.Connection.CreateCommand();
        command.CommandText = "SELECT InputTokens,OutputTokens,ActualCostUsd FROM BillableOperations";
        await using var rows = await command.ExecuteReaderAsync();
        if (!reportUsage)
        {
            Assert.False(await rows.ReadAsync());
            Assert.Equal(0, runs.Attempts);
            Assert.Equal(0m, reservations.ReconciledUsd);
            return;
        }
        Assert.Equal(1, runs.Attempts);
        Assert.True(await rows.ReadAsync());
        Assert.Equal((11L, 7L), (rows.GetInt64(0), rows.GetInt64(1)));
        Assert.Equal(0.000025m, rows.GetDecimal(2));
        Assert.Equal(failAccounting ? 0 : 1, reservations.ReconcileCount);
        if (!failAccounting) Assert.Equal(rows.GetDecimal(2), reservations.ReconciledUsd);
        Assert.False(await rows.ReadAsync());
        Assert.Equal(fallback ? 2 : 1, streaming ? chat.StreamingCallCount : chat.BufferedCallCount);
        Assert.Equal(fallback ? 2 : 1, factory.Leases.Count);
        if (revoke)
        {
            Assert.Null(returned);
            Assert.DoesNotContain(frames, frame => frame.Data?.Contains(marker, StringComparison.Ordinal) == true || frame.Message?.Contains(marker, StringComparison.Ordinal) == true);
        }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MemoryDelegation_parent_tool_continuation_retains_revoked_usage_without_child_tracker(bool cancelCaller)
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();
        await using var root = await h.PrepareAsync();
        using var caller = new CancellationTokenSource();
        var request = BaseRequest() with
        {
            Prompt = "parent",
            SkipSpellRouting = true,
            DisableMcpTools = true,
            StatelessMessages = [new CoreChatMessage("user", "parent")]
        };
        var binding = new MemoryDelegationRootBinding(request, h.Source.Invocation, h.RootId, root, h.Coordinator);
        var chat = new ScriptingChatClient();
        chat.EnqueueToolCall(ArcanumBuiltInToolNames.DelegateTask, "child", new()
        {
            ["prompt"] = "explicit child input",
            ["max_tokens"] = 100,
            ["memory_grant"] = "memory",
        });
        chat.EnqueueBufferedResponder(_ =>
        {
            Assert.Null(SubagentExecutionAmbient.Tracker);
            Assert.True(root.IsConsumed);
            h.Time.Advance(TimeSpan.FromMinutes(5));
            if (cancelCaller) caller.Cancel();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "revoked continuation secret"))
            {
                Usage = new() { InputTokenCount = 11, OutputTokenCount = 7, TotalTokenCount = 18 },
            });
        });
        var wizard = CreateWizard(chat, turnRunWriter: new TurnRunWriter(h.Source.Saga.Context),
            covenantDispatch: WizardIntelligenceProviderCovenantTests.EmptyDisclosureGate(),
            subagentRunner: new MemoryDelegationCapturingRunner());
        using var scope = MemoryDelegationRootBinding.Enter(binding);
        try
        {
            var result = await wizard.ExecutePromptAsync(request, h.Source.Invocation, caller.Token);
            Assert.True(result.IsFailure);
            Assert.DoesNotContain("revoked continuation secret", result.Error.Message);
        }
        catch (OperationCanceledException) when (cancelCaller) { }
        Assert.Equal(2, chat.BufferedCallCount);
        await using var command = h.Source.Connection.CreateCommand();
        command.CommandText = "SELECT Purpose,InputTokens,OutputTokens FROM BillableOperations";
        await using var rows = await command.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        Assert.Equal("tool-continuation", rows.GetString(0));
        Assert.Equal((11L, 7L), (rows.GetInt64(1), rows.GetInt64(2)));
        Assert.False(await rows.ReadAsync());
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MemoryDelegation_structured_retry_has_separate_physical_usage_acknowledgement(bool revokeRetry)
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();
        await using var root = await h.PrepareAsync();
        await using var child = (await h.Coordinator.ConsumeAsync(root, h.RootId, Guid.NewGuid(), h.Digest)).Value;
        using var schema = System.Text.Json.JsonDocument.Parse("""{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""");
        var request = BaseRequest() with
        {
            Prompt = "parent",
            SkipSpellRouting = true,
            DisableMcpTools = true,
            StatelessMessages = [new CoreChatMessage("user", "parent")],
            ResponseFormat = "json_schema",
            ResponseFormatJsonSchema = StrictJsonSchema(schema.RootElement)
        };
        var chat = new ScriptingChatClient();
        chat.EnqueueResponse(new ChatResponse(new ChatMessage(ChatRole.Assistant, "invalid json"))
        {
            Usage = new() { InputTokenCount = 11, OutputTokenCount = 7, TotalTokenCount = 18 },
        });
        chat.EnqueueBufferedResponder(_ =>
        {
            if (revokeRetry) h.Time.Advance(TimeSpan.FromMinutes(5));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"name":"secret"}"""))
            {
                Usage = new() { InputTokenCount = 13, OutputTokenCount = 5, TotalTokenCount = 18 },
            });
        });
        var wizard = CreateWizard(chat, turnRunWriter: new TurnRunWriter(h.Source.Saga.Context),
            covenantDispatch: WizardIntelligenceProviderCovenantTests.EmptyDisclosureGate(),
            subagentRunner: new MemoryDelegationCapturingRunner());
        using var scope = MemoryDelegationRootBinding.Enter(new(request, h.Source.Invocation, h.RootId, root, h.Coordinator));
        var result = await wizard.ExecutePromptAsync(request, h.Source.Invocation, default);
        Assert.Equal(!revokeRetry, result.IsSuccess);
        Assert.Equal(2, chat.BufferedCallCount);
        await using var command = h.Source.Connection.CreateCommand();
        command.CommandText = "SELECT Purpose,InputTokens,OutputTokens FROM BillableOperations ORDER BY Purpose";
        await using var rows = await command.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        Assert.Equal("chat", rows.GetString(0));
        Assert.Equal((11L, 7L), (rows.GetInt64(1), rows.GetInt64(2)));
        Assert.True(await rows.ReadAsync());
        Assert.Equal("structured-output-retry", rows.GetString(0));
        Assert.Equal((13L, 5L), (rows.GetInt64(1), rows.GetInt64(2)));
        Assert.False(await rows.ReadAsync());
    }

    [SkippableTheory]
    [InlineData("arguments")]
    [InlineData("results")]
    [InlineData("repeat")]
    [InlineData("oscillate")]
    [InlineData("denied-arguments")]
    [InlineData("denied-repeat")]
    public async Task MemoryDelegation_protected_tool_progress_preserves_equality_without_diagnostics(string scenario)
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();
        await using var root = await h.PrepareAsync();
        var request = BaseRequest() with
        {
            Prompt = "parent",
            SkipSpellRouting = true,
            StatelessMessages = [new CoreChatMessage("user", "parent")]
        };
        var binding = new MemoryDelegationRootBinding(request, h.Source.Invocation, h.RootId, root, h.Coordinator);
        var chat = new ScriptingChatClient();
        chat.EnqueueToolCall(ArcanumBuiltInToolNames.DelegateTask, "child", new()
        {
            ["prompt"] = "explicit child input",
            ["max_tokens"] = 100,
            ["memory_grant"] = "memory",
        });
        bool denied = scenario.StartsWith("denied", StringComparison.Ordinal);
        string toolName = denied ? "ask_human" : "read_file_chunk";
        string argumentName = denied ? "question" : "relativePath";
        chat.EnqueueToolCall(toolName, "read-a", new() { [argumentName] = "protected-a" });
        chat.EnqueueToolCall(toolName, "read-b", new() { [argumentName] = scenario is "arguments" or "oscillate" or "denied-arguments" ? "protected-b" : "protected-a" });
        if (scenario == "oscillate")
        {
            chat.EnqueueToolCall("read_file_chunk", "read-a-again", new() { ["relativePath"] = "protected-a" });
        }
        chat.EnqueueText("parent complete");
        int reads = 0;
        FakeMcpConnectionManager mcp = new();
        mcp.Tools.Add(AIFunctionFactory.Create((string relativePath) =>
        {
            reads++;
            return scenario == "results" ? $"protected-result-{reads}" : "protected-result";
        }, "read_file_chunk", "reads protected evidence"));
        TestCapturingLogger<WizardIntelligenceProvider> logger = new();
        var wizard = CreateWizard(chat, mcp: mcp, logger: logger,
            covenantDispatch: WizardIntelligenceProviderCovenantTests.EmptyDisclosureGate(),
            subagentRunner: new MemoryDelegationCapturingRunner());
        using var scope = MemoryDelegationRootBinding.Enter(binding);
        InferenceAuditContext audit = new() { RequestType = "protected-progress" };
        var result = await wizard.ExecutePromptAsync(request, h.Source.Invocation, default, audit);
        Assert.True(root.IsConsumed);
        Assert.Equal(scenario == "oscillate" ? 4 : 3, audit.ToolArgumentsJson.Count);

        Assert.All(audit.ToolArgumentsJson, arguments => Assert.Equal(string.Empty, arguments));
        if (scenario is "repeat" or "oscillate" or "denied-repeat")
        {
            Assert.True(result.IsFailure);
            Assert.Equal(ErrorCodes.Hub.NoProgressDetected, result.Error.Code);
            Assert.Equal(scenario == "oscillate" ? 4 : 3, chat.BufferedCallCount);
            Assert.Contains(logger.Entries, entry => entry.Message.Contains("no progress", StringComparison.Ordinal));
            Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("signature", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(toolName, result.Error.Message, StringComparison.Ordinal);
            Assert.Contains("request", result.Error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(logger.Entries.Where(entry => entry.Message.Contains("no progress", StringComparison.Ordinal)),
                entry => entry.Message.Contains(toolName, StringComparison.Ordinal));
        }
        else
        {
            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.ToString() : "");
            Assert.Equal("parent complete", result.Value.Text);
            Assert.Equal(4, chat.BufferedCallCount);
        }
        Assert.Equal(denied ? 0 : scenario == "oscillate" ? 3 : 2, reads);
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("protected-a", StringComparison.Ordinal)
            || entry.Message.Contains("protected-b", StringComparison.Ordinal) || entry.Message.Contains("protected-result", StringComparison.Ordinal));
    }

    private sealed class BlockingDelegationUsageWriter(ITurnRunWriter inner, bool failAccounting) : ITurnRunWriter
    {
        internal int Attempts;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Guid> StartRunAsync(InferenceRunStart start, CancellationToken cancellationToken = default) => inner.StartRunAsync(start, cancellationToken);
        public Task CompleteRunAsync(Guid runId, InferenceRunStatus status, CancellationToken cancellationToken = default) => inner.CompleteRunAsync(runId, status, cancellationToken);
        public Task<bool> TryAbandonRunAsync(Guid runId, CancellationToken cancellationToken = default) => inner.TryAbandonRunAsync(runId, cancellationToken);
        public async Task<Guid> RecordBillableOperationAsync(BillableOperationRecord operation, CancellationToken cancellationToken = default)
        {
            Attempts++;
            Assert.False(cancellationToken.CanBeCanceled);
            Entered.TrySetResult();
            await Release.Task;
            var id = await inner.RecordBillableOperationAsync(operation, cancellationToken);
            if (failAccounting) throw new InvalidOperationException("ambiguous accounting completion");
            return id;
        }
    }

    private static ProviderSettings DelegationProvider(string name, bool? supportsTools) =>
        DefaultProvider() with { Name = name, Models = [new(ModelName, SupportsTools: supportsTools)] };

    private sealed class DelegationCandidateFactory(ProviderSettings first, ScriptingChatClient firstChat, ScriptingChatClient fallbackChat) : IChatClientFactory
    {
        internal List<string> Leases { get; } = [];

        internal ProviderSettings? LeaseProvider { get; init; }

        public Task<ChatClientLease> ResolveClientAsync(string? targetModel, CancellationToken cancellationToken) =>
            ResolveClientAsync(first, ModelName, cancellationToken);

        public Task<ChatClientLease> ResolveClientAsync(ProviderSettings provider, string resolvedModel, CancellationToken cancellationToken)
        {
            Leases.Add(provider.Name);

            return Task.FromResult(new ChatClientLease(provider.Name == first.Name ? firstChat : fallbackChat,
                LeaseProvider ?? provider, resolvedModel, null));
        }
    }

    private sealed class MemoryDelegationCapturingRunner : ISubagentRunner
    {
        internal SubagentRunRequest? Request;

        public Task<SubagentRunResult> RunAsync(SubagentRunRequest request, CancellationToken cancellationToken)
        {
            Request = request;

            return Task.FromResult(new SubagentRunResult(true, "child summary", request.MemoryDelegation!.RunId, new(1, 0, 1, false), null));
        }
    }
}
