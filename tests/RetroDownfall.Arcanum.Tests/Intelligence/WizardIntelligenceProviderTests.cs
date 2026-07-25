using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence.Guardrails;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.CommLink;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Generated;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Platform;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;
using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

[Collection("ProcessEnvironment")]
public sealed class WizardIntelligenceProviderTests : IAsyncLifetime
{

    private readonly TempWorkspace _workspace = new();

    private const string ModelName = "wizard-test-model";

    private const string WardTimeoutReason =
        "The ward held until timeout — action was not allowed";

    public Task InitializeAsync() => _workspace.InitializeAsync();

    public Task DisposeAsync() => _workspace.DisposeAsync();

    [Fact]
    public async Task Scenario01_SingleTurnBuffered_ReturnsAssistantText()
    {
        ScriptingChatClient chat = new();

        chat.EnqueueText("buffered answer");

        WizardIntelligenceProvider wizard = CreateWizard(chat);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "hello", SkipSpellRouting = true, DisableMcpTools = true },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal("buffered answer", result.Value!.Text);
    }

    [Fact]
    public async Task Scenario02_SingleTurnStreaming_EmitsTokensAndResult()
    {
        ScriptingChatClient chat = new();

        chat.EnqueueStreamTokens("he", "llo");

        WizardIntelligenceProvider wizard = CreateWizard(chat);

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with { Prompt = "stream", SkipSpellRouting = true, DisableMcpTools = true });

        Assert.Contains(events, static e => e.Type == IntelligenceEventType.Token && e.Data == "he");

        Assert.Contains(events, static e => e.Type == IntelligenceEventType.Token && e.Data == "llo");

        Assert.Contains(events, static e => e.Type == IntelligenceEventType.Result);
    }

    [Fact]
    public async Task StreamingWizardThroughNativeProjection_GivesApprenticeFinalAnswer()
    {
        ScriptingChatClient chat = new();
        chat.EnqueueStreamTokens("real ", "answer");
        ArcanumSettings settings = DefaultSettings();
        WizardIntelligenceProvider wizard = CreateWizard(chat, settings);
        TestOptionsMonitor<ArcanumSettings> options = new(settings);
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        using ApprenticeService apprenticeService = new(
            services.GetRequiredService<IServiceScopeFactory>(),
            options,
            new ChronicleHub(options),
            NullLogger<ApprenticeService>.Instance);
        Apprentice apprentice = new()
        {
            Id = Guid.NewGuid(),
            Name = "Projection integration",
            Goal = "Consume the real streamed answer.",
            WorkspacePath = _workspace.Root,
            Status = ApprenticeStatus.Running.ToString(),
        };
        System.Reflection.MethodInfo executeStep = typeof(ApprenticeService).GetMethod(
            "ExecuteStepStreamAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        using CancellationTokenSource linkedCts = new();

        Task task = Assert.IsAssignableFrom<Task>(executeStep.Invoke(
            apprenticeService,
            [wizard, apprentice, "Complete this step.", 1, linkedCts, apprentice.Id, false]));
        await task.WaitAsync(TimeSpan.FromSeconds(15));

        object outcome = task.GetType().GetProperty("Result")!.GetValue(task)!;
        string? resultText = (string?)outcome.GetType().GetProperty("ResultText")!.GetValue(outcome);
        Assert.Equal("real answer", resultText);
    }

    [Fact]
    public async Task StreamPromptAsync_BudgetExceeded_YieldsErrorEventAndSkipsInference()
    {

        ScriptingChatClient chat = new();

        ArcanumSettings settings = DefaultSettings() with
        {
            Budget = new BudgetSettings { Enabled = true, DailyLimitUsd = 10m, AlertThresholdPercent = 80 }
        };

        FakeGrimoireRepository budgetGrimoire = new() { TodaySpend = 15m };

        BudgetMonitor monitor = new(
            CreateBudgetMonitorScopeFactory(budgetGrimoire, new FakeBudgetAlertRepository()),
            new FakeCommLinkDispatcher(),
            new TestOptionsMonitor<ArcanumSettings>(settings),
            NullLogger<BudgetMonitor>.Instance);

        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            settings,
            budgetMonitor: monitor);

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with { Prompt = "anything", SkipSpellRouting = true, DisableMcpTools = true });

        IntelligenceEvent error = Assert.Single(events);

        Assert.Equal(IntelligenceEventType.Error, error.Type);

        Assert.Contains("Daily budget limit", error.Message);

        Assert.Equal(0, chat.BufferedCallCount);

    }

    [Fact]
    public async Task StreamPromptAsync_StructuredOutputStrictMode_InvalidJson_YieldsErrorEventAndNoResult()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueStreamTokens("not", " ", "json");

        ArcanumSettings settings = DefaultSettings() with
        {
            StructuredOutput = new StructuredOutputSettings
            {
                Enabled = true,
                StrictMode = true,
                MaxValidationRetries = 0,
            }
        };

        JsonElement schema = JsonSerializer.Deserialize<JsonElement>("""
            {
              "type": "object",
              "properties": { "name": { "type": "string" } },
              "required": ["name"],
              "additionalProperties": false
            }
            """);

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings);

        PingRequest request = BaseRequest() with
        {
            Prompt = "stream structured output",
            ResponseFormat = "json_schema",
            ResponseFormatJsonSchema = schema,
            SkipSpellRouting = true,
            DisableMcpTools = true
        };

        List<IntelligenceEvent> events = await CollectStreamAsync(wizard, request);

        IntelligenceEvent error = Assert.Single(events, e => e.Type == IntelligenceEventType.Error);

        Assert.Equal(ErrorCodes.StructuredOutput.ValidationFailed, error.Data);

        Assert.Contains(
            "failed JSON schema validation",
            error.Message,
            StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(events, e => e.Type == IntelligenceEventType.Token);

        Assert.DoesNotContain(events, e => e.Type == IntelligenceEventType.Result);

    }

    [Fact]
    public async Task StreamPromptAsync_StructuredOutputBestEffort_InvalidJson_WarningsOnResult()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueStreamTokens("not", " ", "json");

        ArcanumSettings settings = DefaultSettings() with
        {
            StructuredOutput = new StructuredOutputSettings
            {
                Enabled = true,
                StrictMode = false,
            }
        };

        JsonElement schema = JsonSerializer.Deserialize<JsonElement>("""
            {
              "type": "object",
              "properties": { "name": { "type": "string" } },
              "required": ["name"],
              "additionalProperties": false
            }
            """);

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings);

        PingRequest request = BaseRequest() with
        {
            Prompt = "stream structured output",
            ResponseFormat = "json_schema",
            ResponseFormatJsonSchema = schema,
            SkipSpellRouting = true,
            DisableMcpTools = true
        };

        List<IntelligenceEvent> events = await CollectStreamAsync(wizard, request);

        IntelligenceEvent result = Assert.Single(events, e => e.Type == IntelligenceEventType.Result);

        Assert.NotEmpty(result.Warnings);

        Assert.Contains(result.Warnings, w => w.Contains("JSON schema validation", StringComparison.OrdinalIgnoreCase));

    }

    [Fact]
    public async Task Guardrails_PiiInInput_BlocksBeforeInference()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueText("should not be reached");

        ArcanumSettings settings = DefaultSettings() with
        {
            Guardrails = new GuardrailsSettings { Enabled = true, DetectPii = true },
        };

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, guardrailsPipeline: CreateGuardrailsPipeline(settings));

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "Email me at alice@example.com", SkipSpellRouting = true, DisableMcpTools = true },
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Guardrails.PiiDetected, result.Error.Code);

        Assert.Equal(0, chat.BufferedCallCount);

    }

    [Fact]
    public async Task Guardrails_ToxicityInOutput_BlocksAndDoesNotPersistAssistantReply()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueText("the model says bad-word here");

        ArcanumSettings settings = DefaultSettings() with
        {
            Guardrails = new GuardrailsSettings
            {
                Enabled = true,
                DetectPii = false,
                BlockToxicity = true,
                ToxicityBlocklist = ["bad-word"],
            },
        };

        FakeGrimoireRepository grimoire = new();

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, grimoire: grimoire, guardrailsPipeline: CreateGuardrailsPipeline(settings));

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "say something", SkipSpellRouting = true, DisableMcpTools = true },
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Guardrails.Blocked, result.Error.Code);

    }

    [Fact]
    public async Task Guardrails_Disabled_PassesThrough()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueText("alice@example.com is fine when guardrails are off");

        ArcanumSettings settings = DefaultSettings() with
        {
            Guardrails = new GuardrailsSettings { Enabled = false, DetectPii = true },
        };

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "alice@example.com", SkipSpellRouting = true, DisableMcpTools = true },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Contains("alice@example.com", result.Value!.Text);

    }

    [Fact]
    public async Task Guardrails_PiiInStatelessInput_BlocksBeforeInference()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueText("should not be reached");

        ArcanumSettings settings = DefaultSettings() with
        {
            Guardrails = new GuardrailsSettings { Enabled = true, DetectPii = true },
        };

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, guardrailsPipeline: CreateGuardrailsPipeline(settings));

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = string.Empty,
                StatelessMessages = [new CoreChatMessage("user", "My SSN is 123-45-6789")],
                SkipSpellRouting = true,
                DisableMcpTools = true,
            },
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Guardrails.PiiDetected, result.Error.Code);

        Assert.Equal(0, chat.BufferedCallCount);

    }

    [Fact]
    public async Task Guardrails_Streaming_BufferedToxicity_DeliversNoTokenBeforeFilter()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueStreamTokens("the model says ", "bad-word here");

        ArcanumSettings settings = DefaultSettings() with
        {
            Guardrails = new GuardrailsSettings
            {
                Enabled = true,
                DetectPii = false,
                BlockToxicity = true,
                ToxicityBlocklist = ["bad-word"],
                StreamingMode = GuardrailsStreamingMode.Buffered,
            },
        };

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, guardrailsPipeline: CreateGuardrailsPipeline(settings));

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with { Prompt = "stream something", SkipSpellRouting = true, DisableMcpTools = true });

        Assert.DoesNotContain(events, e => e.Type == IntelligenceEventType.Token);

        IntelligenceEvent error = Assert.Single(events, e => e.Type == IntelligenceEventType.Error);

        Assert.Contains("content matched a guardrail policy", error.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task Guardrails_Streaming_BufferedToxicity_DiscardsAssistantEntry()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueStreamTokens("the model says ", "bad-word here");

        ArcanumSettings settings = DefaultSettings() with
        {
            Guardrails = new GuardrailsSettings
            {
                Enabled = true,
                DetectPii = false,
                BlockToxicity = true,
                ToxicityBlocklist = ["bad-word"],
                StreamingMode = GuardrailsStreamingMode.Buffered,
            },
        };

        FakeGrimoireRepository grimoire = new();

        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            settings,
            grimoire: grimoire,
            guardrailsPipeline: CreateGuardrailsPipeline(settings));

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with
            {
                Prompt = "stream something",
                SessionId = Guid.NewGuid(),
                SkipSpellRouting = true,
                DisableMcpTools = true,
            });

        Assert.DoesNotContain(events, e => e.Type == IntelligenceEventType.Result);

        IntelligenceEvent error = Assert.Single(events, e => e.Type == IntelligenceEventType.Error);

        Assert.Contains("content matched a guardrail policy", error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(1, grimoire.DiscardCallCount);

        Assert.Equal(0, grimoire.FinalizeCallCount);

    }

    [Fact]
    public async Task Guardrails_Streaming_PassthroughToxicity_DeliversTokensThenError()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueStreamTokens("the model says ", "bad-word here");

        ArcanumSettings settings = DefaultSettings() with
        {
            Guardrails = new GuardrailsSettings
            {
                Enabled = true,
                DetectPii = false,
                BlockToxicity = true,
                ToxicityBlocklist = ["bad-word"],
                StreamingMode = GuardrailsStreamingMode.Passthrough,
            },
        };

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, guardrailsPipeline: CreateGuardrailsPipeline(settings));

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with { Prompt = "stream something", SkipSpellRouting = true, DisableMcpTools = true });

        Assert.Contains(events, e => e.Type == IntelligenceEventType.Token);

        IntelligenceEvent error = Assert.Single(events, e => e.Type == IntelligenceEventType.Error);

        Assert.Contains("content matched a guardrail policy", error.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task Guardrails_Streaming_BufferedMode_DisabledGuardrails_Passthrough()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueStreamTokens("the model says ", "bad-word here");

        ArcanumSettings settings = DefaultSettings() with
        {
            Guardrails = new GuardrailsSettings
            {
                Enabled = false,
                BlockToxicity = true,
                ToxicityBlocklist = ["bad-word"],
                StreamingMode = GuardrailsStreamingMode.Buffered,
            },
        };

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, guardrailsPipeline: CreateGuardrailsPipeline(settings));

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with { Prompt = "stream something", SkipSpellRouting = true, DisableMcpTools = true });

        Assert.Contains(events, e => e.Type == IntelligenceEventType.Token);

        Assert.Contains(events, e => e.Type == IntelligenceEventType.Result);

    }

    [Fact]
    public async Task Scenario03_ToolLoop_OneRound_ThenAnswers()
    {
        ScriptingChatClient chat = new();

        chat.EnqueueToolCall(ArcanumLocalTimeTool.ToolName);

        chat.EnqueueText("time retrieved");

        WizardIntelligenceProvider wizard = CreateWizard(chat);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "what time is it?", SkipSpellRouting = true, DisableMcpTools = true },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal("time retrieved", result.Value!.Text);

        Assert.NotNull(result.Value.ToolCalls);

        Assert.Single(result.Value.ToolCalls!);
    }

    [Fact]
    public async Task Scenario04_ToolLoop_ReachesMaxRounds_FailsCleanly()
    {
        ScriptingChatClient chat = new();

        chat.EnqueueToolCall(ArcanumLocalTimeTool.ToolName);

        chat.EnqueueToolCall(ArcanumLocalTimeTool.ToolName);

        ArcanumSettings settings = DefaultSettings() with
        {
            Intelligence = DefaultSettings().Intelligence with { MaxToolInferenceRounds = 1 },
        };

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "loop", SkipSpellRouting = true, DisableMcpTools = true },
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Hub.ToolLoop", result.Error.Code);
    }

    [Fact]
    public async Task Scenario05_ToolPatternRejected_RetriesWithoutTools()
    {
        ScriptingChatClient chat = new();

        chat.EnqueueException(new InvalidOperationException("model does not support tools"));

        chat.EnqueueText("no tools needed");

        WizardIntelligenceProvider wizard = CreateWizard(chat);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "retry", SkipSpellRouting = true, DisableMcpTools = true },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal("no tools needed", result.Value!.Text);

        Assert.Equal(2, chat.BufferedCallCount);
    }

    [Fact]
    public async Task Scenario06_WardGate_Allowed_ExecutesForbiddenArt()
    {
        ScriptingChatClient chat = new();

        chat.EnqueueToolCall("execute_command");

        chat.EnqueueText("done");

        FakeWard ward = new() { NextResolution = new WardResolution(true, null, DateTimeOffset.UtcNow) };

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(CreateMcpTool("execute_command"));

        WizardIntelligenceProvider wizard = CreateWizard(chat, ward: ward, mcp: mcp);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "run",
                SkipSpellRouting = true,
                UnattendedMode = false,
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(1, ward.WardCallCount);
    }

    [Fact]
    public async Task Scenario07_WardGate_Denied_BlocksForbiddenArt()
    {
        ScriptingChatClient chat = new();

        chat.EnqueueToolCall("execute_command");

        chat.EnqueueText("done");

        FakeWard ward = new()
        {
            NextResolution = new WardResolution(false, "operator said no", DateTimeOffset.UtcNow),
        };

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(CreateMcpTool("execute_command"));

        WizardIntelligenceProvider wizard = CreateWizard(chat, ward: ward, mcp: mcp);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "run", SkipSpellRouting = true, UnattendedMode = false },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(1, ward.WardCallCount);
    }

    [Fact]
    public async Task Scenario08_WardGate_Timeout_BlocksForbiddenArt()
    {
        ScriptingChatClient chat = new();

        chat.EnqueueToolCall("execute_command");

        chat.EnqueueText("done");

        FakeWard ward = new()
        {
            NextResolution = new WardResolution(false, WardTimeoutReason, DateTimeOffset.UtcNow),
        };

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(CreateMcpTool("execute_command"));

        WizardIntelligenceProvider wizard = CreateWizard(chat, ward: ward, mcp: mcp);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "run", SkipSpellRouting = true, UnattendedMode = false },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(1, ward.WardCallCount);
    }

    [Fact]
    public async Task Scenario09_WardGate_UnattendedMode_AutoDenies()
    {
        ScriptingChatClient chat = new();

        chat.EnqueueToolCall("execute_command");

        chat.EnqueueText("done");

        FakeWard ward = new();

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(CreateMcpTool("execute_command"));

        WizardIntelligenceProvider wizard = CreateWizard(chat, ward: ward, mcp: mcp);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "run",
                SkipSpellRouting = true,
                UnattendedMode = true,
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(0, ward.WardCallCount);

        Assert.Equal(2, chat.BufferedCallCount);
    }

    [Fact]
    public async Task StreamPromptAsync_ForbiddenArt_YieldsWarded_BeforeWardResolves()
    {
        ScriptingChatClient chat = new();
        chat.EnqueueStreamToolCall("execute_command");
        chat.EnqueueStreamTokens("done");

        TaskCompletionSource wardEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<WardResolution> wardRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        FakeWard ward = new()
        {
            WardHandler = async _ =>
            {
                wardEntered.TrySetResult();
                return await wardRelease.Task;
            },
        };

        FakeMcpConnectionManager mcp = new();
        mcp.Tools.Add(CreateMcpTool("execute_command"));

        WizardIntelligenceProvider wizard = CreateWizard(chat, ward: ward, mcp: mcp);

        List<IntelligenceEvent> seen = [];
        TaskCompletionSource wardedSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task streamTask = Task.Run(async () =>
        {
            await foreach (IntelligenceEvent evt in wizard.StreamPromptAsync(
                               BaseRequest() with
                               {
                                   Prompt = "run",
                                   SkipSpellRouting = true,
                                   UnattendedMode = false,
                               },
                               CancellationToken.None))
            {
                seen.Add(evt);
                if (evt.Type == IntelligenceEventType.Warded)
                {
                    wardedSeen.TrySetResult();
                }
            }
        });

        await wardEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // While the server is blocked in WardAsync, the client must already have the warded frame
        // (with wardId) so an operator can POST /api/wards/{id}.
        Task completed = await Task.WhenAny(wardedSeen.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(wardedSeen.Task, completed);
        Assert.Contains(seen, static e => e.Type == IntelligenceEventType.Warded && !string.IsNullOrEmpty(e.WardId));

        wardRelease.SetResult(new WardResolution(true, null, DateTimeOffset.UtcNow));
        await streamTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(seen, static e => e.Type == IntelligenceEventType.WardResolved);
    }

    [Fact]
    public async Task Scenario10_ContextCompression_TriggersWhenOverThreshold()
    {
        Guid sessionId = Guid.NewGuid();

        Session session = BuildHeavySession(sessionId);

        FakeGrimoireRepository grimoire = new() { Session = session };

        ScriptingChatClient chat = new();

        chat.EnqueueText("compressed ok");

        ArcanumSettings settings = DefaultSettings() with
        {
            Providers =
            [
                DefaultProvider() with { ContextWindowLimit = 4096 },
            ],
            Intelligence = DefaultSettings().Intelligence with
            {
                EnableContextCompression = true,
                CompressionPreflightMinMessages = 2,
                ContextWindowCompressionThreshold = 50,
                PerMessageTemplateOverheadTokens = 1,
                ReservedOutputTokens = 256,
            },
        };

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, grimoire);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "continue",
                SessionId = sessionId,
                SkipSpellRouting = true,
                DisableMcpTools = true,
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        bool compressedPrompt = chat.AllBufferedCalls
            .SelectMany(static batch => batch)
            .Any(static m => m.Role == ChatRole.System
                && m.Text.Contains("### Campaign Summary (compressed context)", StringComparison.Ordinal));

        Assert.True(compressedPrompt);
    }

    [Fact]
    public async Task Scenario11_ContextCompression_SkippedWhenDisabled()
    {
        Guid sessionId = Guid.NewGuid();

        Session session = BuildHeavySession(sessionId);

        FakeGrimoireRepository grimoire = new() { Session = session };

        ScriptingChatClient chat = new();

        chat.EnqueueText("uncompressed");

        ArcanumSettings settings = DefaultSettings() with
        {
            Providers =
            [
                DefaultProvider() with { ContextWindowLimit = 32_768 },
            ],
            Intelligence = DefaultSettings().Intelligence with { EnableContextCompression = false },
        };

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, grimoire);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "continue",
                SessionId = sessionId,
                SkipSpellRouting = true,
                DisableMcpTools = true,
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.DoesNotContain(
            chat.LastBufferedMessages,
            static m => m.Role == ChatRole.System && m.Text.Contains("### Campaign Summary (compressed context)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Scenario12_Attunement_FiltersMcpTools()
    {
        await CreateSpellWithDeclaredToolsAsync("primary", ["allowed_tool"]);

        ScriptingChatClient chat = new();

        chat.EnqueueText("attuned");

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(CreateMcpTool("allowed_tool"));

        mcp.Tools.Add(CreateMcpTool("blocked_tool"));

        WizardIntelligenceProvider wizard = CreateWizard(chat, mcp: mcp);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "cast",
                WorkingDirectory = _workspace.Root,
                OverrideSpellName = "primary",
                SkipSpellRouting = false,
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        HashSet<string> toolNames = ToolNames(chat.LastChatOptions);

        Assert.Contains("allowed_tool", toolNames);

        Assert.DoesNotContain("blocked_tool", toolNames);

        Assert.Contains(ArcanumLocalTimeTool.ToolName, toolNames);
    }

    [Fact]
    public async Task Scenario13_Attunement_EmptyDeclaredTools_KeepsFullMcpSet()
    {
        await CreateSpellWithDeclaredToolsAsync("open", []);

        ScriptingChatClient chat = new();

        chat.EnqueueText("full set");

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(CreateMcpTool("alpha_tool"));

        mcp.Tools.Add(CreateMcpTool("beta_tool"));

        WizardIntelligenceProvider wizard = CreateWizard(chat, mcp: mcp);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "cast",
                WorkingDirectory = _workspace.Root,
                OverrideSpellName = "open",
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        HashSet<string> toolNames = ToolNames(chat.LastChatOptions);

        Assert.Contains("alpha_tool", toolNames);

        Assert.Contains("beta_tool", toolNames);
    }

    [Fact]
    public async Task Scenario14_SpellDependencyResolution_PlumbsResonanceIntoPrompt()
    {
        await CreateSpellAsync("primary", "Primary", ["DepSpell"]);

        await CreateSpellAsync("dep-spell", "DepSpell", dependencies: null, body: "dependency body");

        ScriptingChatClient chat = new();

        chat.EnqueueText("resonant");

        WizardIntelligenceProvider wizard = CreateWizard(chat);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "invoke",
                WorkingDirectory = _workspace.Root,
                OverrideSpellName = "Primary",
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        MeAiChatMessage system = chat.LastBufferedMessages.First(static m => m.Role == ChatRole.System);

        Assert.Contains("Resonant Spells (Dependencies)", system.Text, StringComparison.Ordinal);

        Assert.Contains("dependency body", system.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scenario15_ModelNotFound_ReturnsHubModelError()
    {
        WizardIntelligenceProvider wizard = CreateWizard(
            new ScriptingChatClient(),
            factory: new ThrowingChatClientFactory());

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "hello", Model = "missing", SkipSpellRouting = true },
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Hub.Model", result.Error.Code);
    }

    [Fact]
    public async Task Scenario16_StreamInferenceFailure_EmitsError()
    {
        ScriptingChatClient chat = new();

        chat.EnqueueStreamFailure(new InvalidOperationException("stream broke"));

        WizardIntelligenceProvider wizard = CreateWizard(chat);

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with { Prompt = "stream fail", SkipSpellRouting = true, DisableMcpTools = true });

        Assert.Contains(events, static e => e.Type == IntelligenceEventType.Error);
    }

    [Fact]
    public async Task Scenario16_CancellationDuringStream_CancelsCleanly()
    {
        ScriptingChatClient chat = new();

        chat.EnqueueSlowStream(TimeSpan.FromSeconds(5), "tok");

        WizardIntelligenceProvider wizard = CreateWizard(chat);

        using CancellationTokenSource cts = new();

        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (IntelligenceEvent _ in wizard.StreamPromptAsync(
                BaseRequest() with { Prompt = "cancel", SkipSpellRouting = true, DisableMcpTools = true },
                cts.Token))
            {
            }
        });
    }

    [Fact]
    public async Task Scenario16_InferenceTimeoutDuringBuffered_ReturnsHubTimeout()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueSlowBuffered(TimeSpan.FromSeconds(30), "late");

        ArcanumSettings settings = DefaultSettings() with
        {
            Intelligence = new IntelligenceSettings { InferenceTimeoutSeconds = 1, EnableLexiconSystem = false },
        };

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "timeout", SkipSpellRouting = true, DisableMcpTools = true },
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Hub.Timeout", result.Error.Code);

    }

    [Fact]
    public async Task Scenario16_InferenceTimeoutDuringStream_EmitsError()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueSlowStream(TimeSpan.FromSeconds(30), "tok");

        ArcanumSettings settings = DefaultSettings() with
        {
            Intelligence = new IntelligenceSettings { InferenceTimeoutSeconds = 1, EnableLexiconSystem = false },
        };

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings);

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with { Prompt = "timeout", SkipSpellRouting = true, DisableMcpTools = true });

        IntelligenceEvent error = Assert.Single(events, static e => e.Type == IntelligenceEventType.Error);

        Assert.Contains("timed out", error.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task Scenario17_EmptyPrompt_ReturnsValidationError()
    {

        WizardIntelligenceProvider wizard = CreateWizard(new ScriptingChatClient());

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "   ", SkipSpellRouting = true, DisableMcpTools = true },
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Validation.InvalidPrompt", result.Error.Code);

    }

    [Fact]
    public async Task Scenario18_BufferedInferenceFailure_ReturnsHubError()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueException(new InvalidOperationException("upstream inference failed"));

        WizardIntelligenceProvider wizard = CreateWizard(chat);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "fail", SkipSpellRouting = true, DisableMcpTools = true },
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Hub.Error", result.Error.Code);

    }

    [Fact]
    public async Task Scenario19_StreamToolLoopLimit_EmitsErrorEvent()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueStreamToolCall(ArcanumLocalTimeTool.ToolName);

        chat.EnqueueStreamToolCall(ArcanumLocalTimeTool.ToolName);

        ArcanumSettings settings = DefaultSettings() with
        {
            Intelligence = DefaultSettings().Intelligence with { MaxToolInferenceRounds = 1 },
        };

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings);

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with { Prompt = "loop", SkipSpellRouting = true, DisableMcpTools = true });

        Assert.Contains(events, static e => e.Type == IntelligenceEventType.Error);

        Assert.Contains(
            events,
            static e => e.Type == IntelligenceEventType.Error
                && e.Message.Contains("Tool invocation limit reached.", StringComparison.Ordinal)
                && e.Message.Contains(ErrorCodes.Hub.ToolLoop, StringComparison.Ordinal));

    }

    [Fact]
    public async Task Scenario20_AttachedFilesTooMany_ReturnsValidationError()
    {

        ArcanumSettings settings = DefaultSettings() with
        {
            Cli = DefaultSettings().Cli with { MaxAttachedFilesPerRequest = 1 },
        };

        WizardIntelligenceProvider wizard = CreateWizard(new ScriptingChatClient(), settings);

        List<AttachedFileDto> files =
        [
            new("a.txt", "one"),
            new("b.txt", "two"),
        ];

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "files",
                SkipSpellRouting = true,
                DisableMcpTools = true,
                AttachedFiles = files,
            },
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Validation.AttachedFiles", result.Error.Code);

    }

    [Fact]
    public async Task Scenario21_OverrideSpellNameNotFound_ReturnsValidationError()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueText("unused");

        WizardIntelligenceProvider wizard = CreateWizard(chat);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "cast",
                WorkingDirectory = _workspace.Root,
                OverrideSpellName = "missing-spell",
            },
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Validation.SpellOverride", result.Error.Code);

    }

    [Fact]
    public async Task Scenario22_OverrideSpellPathOutsideWorkspace_ReturnsPathNotAllowed()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueText("unused");

        WizardIntelligenceProvider wizard = CreateWizard(chat);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "cast",
                WorkingDirectory = _workspace.Root,
                OverrideSpellPath = "/tmp/outside/SPELL.md",
            },
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Spell.PathNotAllowed", result.Error.Code);

    }

    [Fact]
    public async Task Scenario23_OverrideSpellPathInvalidFileName_ReturnsPathNotAllowed()
    {

        await CreateSpellAsync("valid", "Valid", dependencies: null);

        string badPath = Path.Combine(_workspace.Root, "valid", "README.md");

        ScriptingChatClient chat = new();

        chat.EnqueueText("unused");

        WizardIntelligenceProvider wizard = CreateWizard(chat);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "cast",
                WorkingDirectory = _workspace.Root,
                OverrideSpellPath = badPath,
            },
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Spell.PathNotAllowed", result.Error.Code);

    }

    [Fact]
    public async Task Scenario24_OverrideSpellPathValid_LoadsSpell()
    {

        await CreateSpellAsync("by-path", "ByPath", dependencies: null, body: "path routed body");

        string spellPath = Path.Combine(_workspace.Root, "by-path", "SPELL.md");

        ScriptingChatClient chat = new();

        chat.EnqueueText("path ok");

        WizardIntelligenceProvider wizard = CreateWizard(chat);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "cast",
                WorkingDirectory = _workspace.Root,
                OverrideSpellPath = spellPath,
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        MeAiChatMessage system = chat.LastBufferedMessages.First(static m => m.Role == ChatRole.System);

        Assert.Contains("path routed body", system.Text, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Scenario25_AttachedFileNullEntry_ReturnsValidationError()
    {

        WizardIntelligenceProvider wizard = CreateWizard(new ScriptingChatClient());

        List<AttachedFileDto> files = [null!];

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "files",
                SkipSpellRouting = true,
                DisableMcpTools = true,
                AttachedFiles = files,
            },
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Validation.AttachedFiles", result.Error.Code);

    }

    [Fact]
    public async Task Scenario26_AttachedFileEmptyPath_ReturnsValidationError()
    {

        WizardIntelligenceProvider wizard = CreateWizard(new ScriptingChatClient());

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "files",
                SkipSpellRouting = true,
                DisableMcpTools = true,
                AttachedFiles = [new("  ", "content")],
            },
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Validation.AttachedFiles", result.Error.Code);

    }

    [Fact]
    public async Task Scenario27_AttachedFileOversizedContent_ReturnsValidationError()
    {

        ArcanumSettings settings = DefaultSettings() with
        {
            Cli = DefaultSettings().Cli with { MaxAttachFileSizeBytes = 1024 },
        };

        WizardIntelligenceProvider wizard = CreateWizard(new ScriptingChatClient(), settings);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "files",
                SkipSpellRouting = true,
                DisableMcpTools = true,
                AttachedFiles = [new("big.txt", new string('x', 1025))],
            },
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Validation.AttachedFiles", result.Error.Code);

    }

    [Fact]
    public async Task Scenario28_StreamAttachedFilesValidation_EmitsError()
    {

        ArcanumSettings settings = DefaultSettings() with
        {
            Cli = DefaultSettings().Cli with { MaxAttachedFilesPerRequest = 1 },
        };

        WizardIntelligenceProvider wizard = CreateWizard(new ScriptingChatClient(), settings);

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with
            {
                Prompt = "files",
                SkipSpellRouting = true,
                DisableMcpTools = true,
                AttachedFiles = [new("a.txt", "one"), new("b.txt", "two")],
            });

        Assert.Contains(events, static e => e.Type == IntelligenceEventType.Error);

    }

    [Fact]
    public async Task Scenario29_ContextCompressionWithoutSummary_SkipsCompression()
    {

        Guid sessionId = Guid.NewGuid();

        Session session = BuildHeavySessionWithoutSummary(sessionId);

        FakeGrimoireRepository grimoire = new() { Session = session };

        ScriptingChatClient chat = new();

        chat.EnqueueText("no summary");

        ArcanumSettings settings = DefaultSettings() with
        {
            Providers =
            [
                DefaultProvider() with { ContextWindowLimit = 32_768 },
            ],
            Intelligence = DefaultSettings().Intelligence with
            {
                EnableContextCompression = true,
                CompressionPreflightMinMessages = 2,
                ContextWindowCompressionThreshold = 10,
                PerMessageTemplateOverheadTokens = 1,
            },
        };

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, grimoire);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "continue",
                SessionId = sessionId,
                SkipSpellRouting = true,
                DisableMcpTools = true,
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.DoesNotContain(
            chat.LastBufferedMessages,
            static m => m.Role == ChatRole.System
                && m.Text.Contains("### Campaign Summary (compressed context)", StringComparison.Ordinal));

    }

    [Fact]
    public async Task Scenario30_StreamContextCompression_EmitsStatusNotice()
    {

        Guid sessionId = Guid.NewGuid();

        Session session = BuildHeavySession(sessionId);

        FakeGrimoireRepository grimoire = new() { Session = session };

        ScriptingChatClient chat = new();

        chat.EnqueueStreamTokens("ok");

        ArcanumSettings settings = DefaultSettings() with
        {
            Providers =
            [
                DefaultProvider() with { ContextWindowLimit = 128 },
            ],
            Intelligence = DefaultSettings().Intelligence with
            {
                EnableContextCompression = true,
                CompressionPreflightMinMessages = 2,
                ContextWindowCompressionThreshold = 10,
                PerMessageTemplateOverheadTokens = 1,
            },
        };

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, grimoire);

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with
            {
                Prompt = "continue",
                SessionId = sessionId,
                SkipSpellRouting = true,
                DisableMcpTools = true,
            });

        Assert.Contains(
            events,
            static e => e.Type == IntelligenceEventType.Status
                && e.Message == IntelligenceStatusMessages.MemoryCompressionNotice);

    }

    [Fact]
    public async Task Scenario31_StreamModelResolutionFailure_EmitsError()
    {

        WizardIntelligenceProvider wizard = CreateWizard(
            new ScriptingChatClient(),
            factory: new ThrowingChatClientFactory());

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with { Prompt = "hello", Model = "missing", SkipSpellRouting = true });

        Assert.Contains(events, static e => e.Type == IntelligenceEventType.Error);

        Assert.Contains(
            events,
            static e => e.Type == IntelligenceEventType.Error
                && e.Message.Contains("not configured", StringComparison.OrdinalIgnoreCase));

    }

    [Fact]
    public async Task Scenario32_StatelessMessages_AllowsEmptyPrompt()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueText("stateless ok");

        WizardIntelligenceProvider wizard = CreateWizard(chat);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "   ",
                SkipSpellRouting = true,
                DisableMcpTools = true,
                StatelessMessages =
                [
                    new CoreChatMessage("user", "prior question"),
                ],
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal("stateless ok", result.Value!.Text);

    }

    [Fact]
    public async Task Scenario33_SessionBeginFailure_StillReturnsInference()
    {

        FakeGrimoireRepository grimoire = new() { ThrowOnBegin = true };

        ScriptingChatClient chat = new();

        chat.EnqueueText("inference despite begin failure");

        WizardIntelligenceProvider wizard = CreateWizard(chat, grimoire: grimoire);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "hello",
                SessionId = Guid.NewGuid(),
                SkipSpellRouting = true,
                DisableMcpTools = true,
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal("inference despite begin failure", result.Value!.Text);

    }

    [Fact]
    public async Task Scenario34_SessionFinalizeFailure_ReturnsHubError()
    {

        FakeGrimoireRepository grimoire = new() { ThrowOnFinalize = true };

        ScriptingChatClient chat = new();

        chat.EnqueueText("finalize failure ok");

        WizardIntelligenceProvider wizard = CreateWizard(chat, grimoire: grimoire);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "hello",
                SessionId = Guid.NewGuid(),
                SkipSpellRouting = true,
                DisableMcpTools = true,
            },
            CancellationToken.None);

        // Phase 0: finalize failure is a hard turn failure (GrimoireTurnWriter contract).
        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Hub.Error, result.Error.Code);

    }

    [Fact]
    public async Task Scenario35_TokenTracking_IncrementsSessionTokens()
    {

        Guid sessionId = Guid.NewGuid();

        FakeGrimoireRepository grimoire = new();

        ScriptingChatClient chat = new() { UsageTotalTokens = 30 };

        chat.EnqueueText("tracked");

        ArcanumSettings settings = DefaultSettings() with
        {
            Intelligence = DefaultSettings().Intelligence with { EnableTokenTracking = true },
        };

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, grimoire);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "hello",
                SessionId = sessionId,
                SkipSpellRouting = true,
                DisableMcpTools = true,
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(sessionId, grimoire.LastIncrementedSessionId);

        Assert.Equal(30, grimoire.LastIncrementedTokens);

    }

    [Fact]
    public async Task TokenTracking_DoesNotAddReasoningSubsetToSessionTotal()
    {
        Guid sessionId = Guid.NewGuid();
        FakeGrimoireRepository grimoire = new();
        ChatResponse response = new(new MeAiChatMessage(ChatRole.Assistant, "tracked"))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 10,
                OutputTokenCount = 20,
                TotalTokenCount = 30,
                ReasoningTokenCount = 15,
            },
        };
        ScriptingChatClient chat = new();
        chat.EnqueueResponse(response);
        ArcanumSettings settings = DefaultSettings() with
        {
            Intelligence = DefaultSettings().Intelligence with { EnableTokenTracking = true },
        };
        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, grimoire);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "hello",
                SessionId = sessionId,
                SkipSpellRouting = true,
                DisableMcpTools = true,
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(30, grimoire.LastIncrementedTokens);
        Assert.Equal(30, result.Value.Usage?.TotalTokens);
        Assert.Equal(15, result.Value.Usage?.ReasoningTokens);
    }

    [Fact]
    public async Task Scenario35b_TokenTracking_IncrementsCostUsingModelPricing()
    {

        Guid sessionId = Guid.NewGuid();

        FakeGrimoireRepository grimoire = new();

        // 30 total tokens, split 15 in / 15 out by ScriptingChatClient.
        ScriptingChatClient chat = new() { UsageTotalTokens = 30 };

        chat.EnqueueText("tracked");

        ArcanumSettings settings = DefaultSettings() with
        {
            Intelligence = DefaultSettings().Intelligence with { EnableTokenTracking = true },
            Pricing = new PricingSettings
            {
                ModelPricing = new Dictionary<string, ModelPricingEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [ModelName] = new() { InputPer1M = 10.00m, OutputPer1M = 30.00m },
                },
            },
        };

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, grimoire);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "hello",
                SessionId = sessionId,
                SkipSpellRouting = true,
                DisableMcpTools = true,
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(sessionId, grimoire.LastIncrementedSessionId);

        Assert.Equal(30, grimoire.LastIncrementedTokens);

        // 15 * 10 / 1_000_000 + 15 * 30 / 1_000_000
        decimal expected = (15m * 10.00m / 1_000_000m) + (15m * 30.00m / 1_000_000m);

        Assert.Equal(expected, grimoire.LastIncrementedCostUsd);

    }

    [Fact]
    public async Task Scenario35c_CachedInputTokens_RecordsPromptCacheMetric()
    {

        Guid sessionId = Guid.NewGuid();

        FakeGrimoireRepository grimoire = new();

        ScriptingChatClient chat = new()
        {
            UsageTotalTokens = 30,
            UsageCachedInputTokens = 12,
        };

        chat.EnqueueText("cached");

        ArcanumSettings settings = DefaultSettings() with
        {
            Intelligence = DefaultSettings().Intelligence with { EnableTokenTracking = true },
        };

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, grimoire);

        string marker = DefaultProvider().Name;

        System.Collections.Concurrent.ConcurrentQueue<long> captured = new();

        using System.Diagnostics.Metrics.MeterListener listener = new()
        {
            InstrumentPublished = static (instrument, activeListener) => activeListener.EnableMeasurementEvents(instrument),
        };

        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {

            if (instrument.Name == "arcanum_prompt_cache_tokens_total")
            {

                foreach (KeyValuePair<string, object?> tag in tags)
                {

                    if (tag.Key == "provider" && tag.Value is string s && s == marker)
                    {

                        captured.Enqueue(measurement);

                    }

                }

            }

        });

        listener.Start();

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "hello",
                SessionId = sessionId,
                SkipSpellRouting = true,
                DisableMcpTools = true,
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(12, Assert.Single(captured));

    }

    [Fact]
    public async Task ReasoningUsage_RecordsDedicatedLowCardinalityMetric()
    {
        ChatResponse response = new(new MeAiChatMessage(ChatRole.Assistant, "answer"))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 10,
                OutputTokenCount = 20,
                TotalTokenCount = 30,
                ReasoningTokenCount = 7,
            },
        };
        ScriptingChatClient chat = new();
        chat.EnqueueResponse(response);
        WizardIntelligenceProvider wizard = CreateWizard(chat);
        string providerMarker = DefaultProvider().Name;
        System.Collections.Concurrent.ConcurrentQueue<long> captured = new();
        using System.Diagnostics.Metrics.MeterListener listener = new()
        {
            InstrumentPublished = static (instrument, activeListener) =>
                activeListener.EnableMeasurementEvents(instrument),
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            if (instrument.Name != "arcanum_inference_reasoning_tokens_total")
            {
                return;
            }

            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "provider"
                    && tag.Value is string provider
                    && provider == providerMarker)
                {
                    captured.Enqueue(measurement);
                }
            }
        });
        listener.Start();

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "reason",
                SkipSpellRouting = true,
                DisableMcpTools = true,
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(7, Assert.Single(captured));
    }

    [Fact]
    public async Task Scenario36_StreamSessionBound_EmitsSessionEvents()
    {

        Guid sessionId = Guid.NewGuid();

        FakeGrimoireRepository grimoire = new() { FixedSessionId = sessionId };

        ScriptingChatClient chat = new();

        chat.EnqueueStreamTokens("bound");

        WizardIntelligenceProvider wizard = CreateWizard(chat, grimoire: grimoire);

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with
            {
                Prompt = "stream session",
                SessionId = sessionId,
                SkipSpellRouting = true,
                DisableMcpTools = true,
            });

        Assert.Contains(events, static e => e.Type == IntelligenceEventType.SessionBound);

        Assert.Contains(events, static e => e.Type == IntelligenceEventType.ConversationBound);

        Assert.Contains(events, static e => e.Type == IntelligenceEventType.Result);

    }

    [Fact]
    public async Task Scenario37_StreamToolExecutionFailure_EmitsToolResultWithFailureMessage()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueStreamToolCall("failing_tool");

        chat.EnqueueStreamTokens("after tool");

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(CreateThrowingMcpTool("failing_tool"));

        WizardIntelligenceProvider wizard = CreateWizard(chat, mcp: mcp);

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with { Prompt = "tool fail", SkipSpellRouting = true });

        Assert.Contains(events, static e => e.Type == IntelligenceEventType.ToolCall);

        // A distinct toolError event is emitted (in addition to, and before, the ToolResult carrying
        // the same synthesized message) so streaming clients can observe the tolerated failure.
        Assert.Contains(events, static e => e.Type == IntelligenceEventType.ToolError);

        int toolErrorIndex = events.FindIndex(static e => e.Type == IntelligenceEventType.ToolError);

        int toolResultIndexForFailure = events.FindIndex(static e => e.Type == IntelligenceEventType.ToolResult);

        Assert.True(toolErrorIndex < toolResultIndexForFailure, "toolError must be emitted before the corresponding toolResult.");

        Assert.Contains(
            events,
            static e => e.Type == IntelligenceEventType.ToolResult
                && e.Data!.Contains(
                    "[Tool error: failing_tool failed with an internal error. The operator has been notified.]",
                    StringComparison.Ordinal));

    }

    [Fact]
    public async Task Scenario38_StreamToolUnsupported_RetriesWithoutTools()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueImmediateStreamFailure(new InvalidOperationException("model does not support tools"));

        chat.EnqueueStreamTokens("retried");

        WizardIntelligenceProvider wizard = CreateWizard(chat);

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with { Prompt = "retry stream", SkipSpellRouting = true, DisableMcpTools = true });

        Assert.Contains(
            events,
            static e => e.Type == IntelligenceEventType.Status
                && e.Message.Contains("does not support tools", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(events, static e => e.Type == IntelligenceEventType.Result);

        Assert.Contains(events, static e => e.Type == IntelligenceEventType.Token && e.Data == "retried");

    }

    [Fact]
    public async Task Scenario39_ReadOnlyToolPolicy_FiltersWriteTools()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueText("readonly");

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(CreateMcpTool("read_file_chunk"));

        mcp.Tools.Add(CreateMcpTool("write_file"));

        WizardIntelligenceProvider wizard = CreateWizard(chat, mcp: mcp);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "list",
                SkipSpellRouting = true,
                ToolPolicy = ToolPolicy.ReadOnlyTools,
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        HashSet<string> toolNames = ToolNames(chat.LastChatOptions);

        Assert.Contains("read_file_chunk", toolNames);

        Assert.DoesNotContain("write_file", toolNames);

    }

    [Fact]
    public async Task Scenario40_OverrideSpellByFolderName_ResolvesSpell()
    {

        await CreateSpellAsync("folder-spell", "FolderSpell", dependencies: null, body: "folder matched");

        ScriptingChatClient chat = new();

        chat.EnqueueText("folder ok");

        WizardIntelligenceProvider wizard = CreateWizard(chat);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "cast",
                WorkingDirectory = _workspace.Root,
                OverrideSpellName = "folder-spell",
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        MeAiChatMessage system = chat.LastBufferedMessages.First(static m => m.Role == ChatRole.System);

        Assert.Contains("folder matched", system.Text, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Scenario41_StreamSpellRoutingFailure_EmitsError()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueStreamTokens("unused");

        WizardIntelligenceProvider wizard = CreateWizard(chat);

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with
            {
                Prompt = "cast",
                WorkingDirectory = _workspace.Root,
                OverrideSpellName = "no-such-spell",
            });

        Assert.Contains(events, static e => e.Type == IntelligenceEventType.Error);

        Assert.DoesNotContain(events, static e => e.Type == IntelligenceEventType.Result);

    }

    [Fact]
    public async Task Scenario50_SanctumStrict_BlocksWriteFile()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueToolCall(
            "write_file",
            "wf1",
            new Dictionary<string, object?>
            {
                ["relativePath"] = "secret.txt",
                ["content"] = "nope",
            });

        chat.EnqueueText("blocked");

        Campaign campaign = BuildSanctumCampaign(_workspace.Root, enabled: true, SanctumMode.Strict);

        ConfigurableSanctumGuard sanctum = new()
        {
            PathValidator = (_, path, _, _, _) =>
                Task.FromResult(
                    path.Contains("secret.txt", StringComparison.Ordinal)
                        ? new SanctumResult
                        {
                            Allowed = false,
                            DenyReason = "path denied",
                            Breach = new SanctumBreach { BreachType = "PathViolation" },
                        }
                        : new SanctumResult { Allowed = true }),
        };

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(CreateMcpTool("write_file"));

        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            campaignRepository: new FakeCampaignRepository(campaign),
            sanctumGuard: sanctum,
            mcp: mcp);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "write",
                WorkingDirectory = _workspace.Root,
                SkipSpellRouting = true,
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(2, chat.BufferedCallCount);

        Assert.Contains(
            chat.AllBufferedCalls.SelectMany(static batch => batch),
            static m => m.Role == ChatRole.Tool
                && GetMessageText(m).Contains("Sanctum Guard has blocked", StringComparison.Ordinal));

    }

    [Fact]
    public async Task Scenario51_SanctumStrict_BlocksForbiddenToolByName()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueToolCall(
            "read_file_chunk",
            "rf2",
            new Dictionary<string, object?>
            {
                ["relativePath"] = "notes.txt",
                ["startLine"] = 1,
                ["endLine"] = 1,
            });

        chat.EnqueueText("tool blocked");

        Campaign campaign = BuildSanctumCampaign(_workspace.Root, enabled: true, SanctumMode.Strict);

        ConfigurableSanctumGuard sanctum = new()
        {
            ToolValidator = (_, toolName, _) =>
                Task.FromResult(
                    string.Equals(toolName, "read_file_chunk", StringComparison.Ordinal)
                        ? new SanctumResult
                        {
                            Allowed = false,
                            DenyReason = "tool denied",
                            Breach = new SanctumBreach { BreachType = "ToolViolation" },
                        }
                        : new SanctumResult { Allowed = true }),
        };

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(CreateMcpTool("read_file_chunk"));

        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            campaignRepository: new FakeCampaignRepository(campaign),
            sanctumGuard: sanctum,
            mcp: mcp);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "read",
                WorkingDirectory = _workspace.Root,
                SkipSpellRouting = true,
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Contains(
            chat.AllBufferedCalls.SelectMany(static batch => batch),
            static m => m.Role == ChatRole.Tool
                && GetMessageText(m).Contains("Sanctum Guard has blocked", StringComparison.Ordinal));

    }

    [Fact]
    public async Task Scenario52_SanctumAuditOnly_AllowsDespitePathDenial()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueToolCall(
            "read_file_chunk",
            "rf1",
            new Dictionary<string, object?>
            {
                ["relativePath"] = "notes.txt",
                ["startLine"] = 1,
                ["endLine"] = 1,
            });

        chat.EnqueueText("audit allowed");

        Campaign campaign = BuildSanctumCampaign(_workspace.Root, enabled: true, SanctumMode.AuditOnly);

        ConfigurableSanctumGuard sanctum = new()
        {
            PathValidator = (_, _, _, _, _) =>
                Task.FromResult(new SanctumResult
                {
                    Allowed = false,
                    DenyReason = "would block in strict",
                }),
        };

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(CreateMcpTool("read_file_chunk"));

        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            campaignRepository: new FakeCampaignRepository(campaign),
            sanctumGuard: sanctum,
            mcp: mcp);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "read",
                WorkingDirectory = _workspace.Root,
                SkipSpellRouting = true,
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.DoesNotContain(
            chat.AllBufferedCalls.SelectMany(static batch => batch),
            static m => m.Role == ChatRole.Tool
                && GetMessageText(m).Contains("Sanctum Guard has blocked", StringComparison.Ordinal));

    }

    [Fact]
    public async Task Scenario53_NoForbiddenArtsPolicy_ExcludesForbiddenTools()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueText("no forbidden");

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(CreateMcpTool("read_file_chunk"));

        mcp.Tools.Add(CreateMcpTool("execute_command"));

        WizardIntelligenceProvider wizard = CreateWizard(chat, mcp: mcp);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "tools",
                SkipSpellRouting = true,
                ToolPolicy = ToolPolicy.NoForbiddenArts,
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        HashSet<string> toolNames = ToolNames(chat.LastChatOptions);

        Assert.Contains("read_file_chunk", toolNames);

        Assert.DoesNotContain("execute_command", toolNames);

    }

    [Fact]
    public async Task Scenario54_UnattendedMode_FiltersAskHumanTool()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueText("unattended tools");

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(CreateMcpTool("ask_human"));

        mcp.Tools.Add(CreateMcpTool("read_file_chunk"));

        WizardIntelligenceProvider wizard = CreateWizard(chat, mcp: mcp);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "tools",
                SkipSpellRouting = true,
                UnattendedMode = true,
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        HashSet<string> toolNames = ToolNames(chat.LastChatOptions);

        Assert.Contains("read_file_chunk", toolNames);

        Assert.DoesNotContain("ask_human", toolNames);

    }

    [Fact]
    public async Task Scenario54b_BufferedAttended_FiltersAskHumanTool()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueText("buffered attended");

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(CreateMcpTool("ask_human"));

        mcp.Tools.Add(CreateMcpTool("read_file_chunk"));

        WizardIntelligenceProvider wizard = CreateWizard(chat, mcp: mcp);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "tools",
                SkipSpellRouting = true,
                UnattendedMode = false,
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        HashSet<string> toolNames = ToolNames(chat.LastChatOptions);

        Assert.Contains("read_file_chunk", toolNames);

        Assert.DoesNotContain("ask_human", toolNames);

    }

    [Fact]
    public async Task Scenario54c_StreamingAttended_AdvertisesAskHumanTool()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueStreamTokens("streamed");

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(CreateMcpTool("ask_human"));

        mcp.Tools.Add(CreateMcpTool("read_file_chunk"));

        WizardIntelligenceProvider wizard = CreateWizard(chat, mcp: mcp);

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with
            {
                Prompt = "tools",
                SkipSpellRouting = true,
                UnattendedMode = false,
            });

        Assert.Contains(events, static e => e.Type == IntelligenceEventType.Result);

        HashSet<string> toolNames = ToolNames(chat.LastChatOptions);

        Assert.Contains("ask_human", toolNames);

        Assert.Contains("read_file_chunk", toolNames);

    }

    [Fact]
    public async Task Scenario54d_StreamingAskHuman_PreparesHostPromptIdBeforeToolCall()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueStreamToolCall(
            "ask_human",
            callId: "call_ask_1",
            arguments: new Dictionary<string, object?>
            {
                ["question"] = "What is the passphrase?",
                ["promptId"] = "model-supplied-id",
            });

        chat.EnqueueStreamTokens("done");

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(CreateMcpTool("ask_human"));

        WizardIntelligenceProvider wizard = CreateWizard(chat, mcp: mcp);

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with
            {
                Prompt = "ask",
                SkipSpellRouting = true,
                UnattendedMode = false,
            });

        IntelligenceEvent toolCall = Assert.Single(events, static e => e.Type == IntelligenceEventType.ToolCall);

        Assert.NotNull(toolCall.ToolCall);

        Assert.Equal("call_ask_1", toolCall.ToolCall.CallId);

        Assert.Equal("ask_human", toolCall.ToolCall.Name);

        Assert.Contains("What is the passphrase?", toolCall.ToolCall.ArgumentsJson, StringComparison.Ordinal);

        Assert.DoesNotContain("model-supplied-id", toolCall.ToolCall.ArgumentsJson, StringComparison.Ordinal);

        Assert.Contains("\"promptId\":", toolCall.ToolCall.ArgumentsJson, StringComparison.Ordinal);

        Assert.DoesNotContain(
            events,
            static e => e.Type == IntelligenceEventType.ToolError
                && (e.Message?.Contains("Too many ask_human", StringComparison.Ordinal) ?? false));

    }

    [Fact]
    public async Task Scenario55_StreamStatelessToolOnlyUpdate_CompletesWithResult()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueStreamToolCallOnly(ArcanumLocalTimeTool.ToolName);

        chat.EnqueueStreamTokens("done");

        WizardIntelligenceProvider wizard = CreateWizard(chat);

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with
            {
                Prompt = "   ",
                SkipSpellRouting = true,
                DisableMcpTools = true,
                StatelessMessages =
                [
                    new CoreChatMessage("user", "prior"),
                ],
            });

        Assert.Contains(events, static e => e.Type == IntelligenceEventType.ToolCall);

        Assert.Contains(events, static e => e.Type == IntelligenceEventType.ToolResult);

        Assert.Contains(events, static e => e.Type == IntelligenceEventType.Result);

    }

    [Fact]
    public async Task Scenario56_AttunementExecuteCommand_StillAdvertisedAndWardFires()
    {
        string? previousAllow = global::System.Environment.GetEnvironmentVariable(HostProcessToolPolicy.AllowHostProcessToolsEnvVar);

        string? previousEdition = global::System.Environment.GetEnvironmentVariable("ARCANUM_EDITION");

        try
        {
            global::System.Environment.SetEnvironmentVariable(HostProcessToolPolicy.AllowHostProcessToolsEnvVar, "1");

            global::System.Environment.SetEnvironmentVariable("ARCANUM_EDITION", "development");

            await CreateSpellWithDeclaredToolsAsync("exec-spell", ["execute_command"]);

            ScriptingChatClient chat = new();

            chat.EnqueueToolCall("execute_command");

            chat.EnqueueText("done");

            FakeWard ward = new() { NextResolution = new WardResolution(true, null, DateTimeOffset.UtcNow) };

            FakeMcpConnectionManager mcp = new();

            mcp.Tools.Add(CreateMcpTool("execute_command"));

            ArcanumSettings settings = DefaultSettings() with { Edition = ArcanumEdition.Development };

            WizardIntelligenceProvider wizard = CreateWizard(chat, settings, ward: ward, mcp: mcp);

            Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
                BaseRequest() with
                {
                    Prompt = "run",
                    WorkingDirectory = _workspace.Root,
                    OverrideSpellName = "exec-spell",
                    SkipSpellRouting = false,
                    UnattendedMode = false,
                },
                CancellationToken.None);

            Assert.True(result.IsSuccess);

            HashSet<string> toolNames = ToolNames(chat.LastChatOptions);

            Assert.Contains("execute_command", toolNames);

            Assert.Equal(1, ward.WardCallCount);
        }
        finally
        {
            global::System.Environment.SetEnvironmentVariable(HostProcessToolPolicy.AllowHostProcessToolsEnvVar, previousAllow);

            global::System.Environment.SetEnvironmentVariable("ARCANUM_EDITION", previousEdition);
        }
    }

    // === Execute/Stream tool-round loop contract (W6.15c guard) ===
    // Characterization tests pinning the OBSERVABLE behavior shared and divergent between
    // ExecutePromptAsync (buffered) and StreamPromptAsync (streaming) so a future unification of
    // the two tool-round loops (deferred W6.15c) can be verified green-to-green. These assert
    // end-state behavior (events/Result/grimoire side-effects), not internal control flow, so the
    // future merge is free to pick a shared implementation strategy.

    [Fact]
    public async Task LoopContract_StreamingToolRound_EmitsOrderedToolCallResultTokensResult()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueStreamToolCall(ArcanumLocalTimeTool.ToolName);

        chat.EnqueueStreamTokens("the", " answer");

        WizardIntelligenceProvider wizard = CreateWizard(chat);

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with { Prompt = "what time?", SkipSpellRouting = true, DisableMcpTools = true });

        int toolCallIndex = events.FindIndex(static e => e.Type == IntelligenceEventType.ToolCall);

        int toolResultIndex = events.FindIndex(static e => e.Type == IntelligenceEventType.ToolResult);

        int resultIndex = events.FindIndex(static e => e.Type == IntelligenceEventType.Result);

        Assert.True(toolCallIndex >= 0, "expected a ToolCall event");

        Assert.True(toolResultIndex > toolCallIndex, "ToolResult must follow its ToolCall");

        Assert.True(resultIndex > toolResultIndex, "Result must be emitted after the tool round");

        Assert.Contains(events.Skip(toolResultIndex + 1), static e => e.Type == IntelligenceEventType.Token);

    }

    [Fact]
    public async Task LoopContract_BufferedToolInvocationFailure_StrictMode_PropagatesAsHubError()
    {

        // Arcanum:Intelligence:TolerateToolFailures = false (opt-in strict mode) restores the
        // pre-existing behavior: suppressInvocationFailures=false, so a throwing tool aborts the
        // whole turn with Hub.Error.
        ScriptingChatClient chat = new();

        chat.EnqueueToolCall("failing_tool");

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(CreateThrowingMcpTool("failing_tool"));

        ArcanumSettings settings = DefaultSettings() with
        {
            Intelligence = DefaultSettings().Intelligence with { TolerateToolFailures = false },
        };

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings: settings, mcp: mcp);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "tool fail", SkipSpellRouting = true },
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Hub.Error", result.Error.Code);

    }

    [Fact]
    public async Task LoopContract_BufferedToolInvocationFailure_DefaultTolerates_ReturnsSyntheticResultAndContinues()
    {

        // Arcanum:Intelligence:TolerateToolFailures defaults to true: a throwing tool is caught and
        // synthesized into a tool result the model can see and react to, instead of failing the
        // whole turn with Hub.Error — the buffered counterpart to streaming's pre-existing tolerant
        // behavior (Scenario37).
        ScriptingChatClient chat = new();

        chat.EnqueueToolCall("failing_tool");

        chat.EnqueueText("recovered after tool failure");

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(CreateThrowingMcpTool("failing_tool"));

        WizardIntelligenceProvider wizard = CreateWizard(chat, mcp: mcp);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "tool fail", SkipSpellRouting = true },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal("recovered after tool failure", result.Value!.Text);

        Assert.NotNull(result.Value.ToolCalls);

        Assert.Single(result.Value.ToolCalls!);

        MeAiChatMessage toolMessage = chat.LastBufferedMessages.Single(static m => m.Role == ChatRole.Tool);

        Assert.Equal(
            "[Tool error: failing_tool failed with an internal error. The operator has been notified.]",
            GetMessageText(toolMessage));

    }

    [Fact]
    public async Task AuditLog_BufferedTurn_WithAuditContext_RecordsCompletedTurn()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueToolCall(ArcanumLocalTimeTool.ToolName);

        chat.EnqueueText("time retrieved");

        FakeInferenceAuditLogger auditLogger = new();

        WizardIntelligenceProvider wizard = CreateWizard(chat, auditLogger: auditLogger);

        InferenceAuditContext auditContext = new() { RequestType = "ping", ClientIp = "127.0.0.1" };

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "what time is it?", SkipSpellRouting = true, DisableMcpTools = true },
            CancellationToken.None,
            auditContext);

        Assert.True(result.IsSuccess);

        InferenceAuditRecord record = Assert.Single(auditLogger.Records);

        Assert.Equal("ping", record.RequestType);

        Assert.Equal("127.0.0.1", record.ClientIp);

        Assert.Equal(ModelName, record.Model);

        Assert.Equal(1, record.ToolCalls);

        Assert.Contains(ArcanumLocalTimeTool.ToolName, record.ToolNames);

        Assert.Equal("stop", record.FinishReason);

        Assert.True(record.LatencyMs >= 0);

    }

    [Fact]
    public async Task AuditLog_ReasoningUsage_RecordsCountWithoutReasoningText()
    {
        const string sensitiveReasoning = "sensitive provider reasoning";
        ChatResponse response = new(new MeAiChatMessage(
            ChatRole.Assistant,
            [
                new TextReasoningContent(sensitiveReasoning),
                new TextContent("answer"),
            ]))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 10,
                OutputTokenCount = 8,
                TotalTokenCount = 18,
                ReasoningTokenCount = 7,
            },
        };
        ScriptingChatClient chat = new();
        chat.EnqueueResponse(response);
        FakeInferenceAuditLogger auditLogger = new();
        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            SettingsWithReasoning(),
            auditLogger: auditLogger);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "reason",
                SkipSpellRouting = true,
                DisableMcpTools = true,
                Reasoning = new ReasoningRequestOptions(Output: ReasoningOutputMode.Summary),
            },
            CancellationToken.None,
            new InferenceAuditContext { RequestType = "reasoning-audit" });

        Assert.True(result.IsSuccess);
        InferenceAuditRecord record = Assert.Single(auditLogger.Records);
        Assert.Equal(7, record.ReasoningTokens);

        string persisted = JsonSerializer.Serialize(
            record,
            Core.Serialization.AuditJsonContext.Default.InferenceAuditRecord);
        Assert.DoesNotContain(sensitiveReasoning, persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuditLog_BufferedTurn_WithoutAuditContext_DoesNotRecord()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueText("done");

        FakeInferenceAuditLogger auditLogger = new();

        WizardIntelligenceProvider wizard = CreateWizard(chat, auditLogger: auditLogger);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "hello", SkipSpellRouting = true },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Empty(auditLogger.Records);

    }

    [Fact]
    public async Task AuditLog_StreamingTurn_WithAuditContext_RecordsCompletedTurn()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueStreamToolCall(ArcanumLocalTimeTool.ToolName);

        chat.EnqueueStreamTokens("time retrieved");

        FakeInferenceAuditLogger auditLogger = new();

        WizardIntelligenceProvider wizard = CreateWizard(chat, auditLogger: auditLogger);

        InferenceAuditContext auditContext = new() { RequestType = "ping-stream", ClientIp = "10.0.0.5" };

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with { Prompt = "what time is it?", SkipSpellRouting = true, DisableMcpTools = true },
            auditContext);

        Assert.Contains(events, static e => e.Type == IntelligenceEventType.Result);

        InferenceAuditRecord record = Assert.Single(auditLogger.Records);

        Assert.Equal("ping-stream", record.RequestType);

        Assert.Equal("10.0.0.5", record.ClientIp);

        Assert.Contains(ArcanumLocalTimeTool.ToolName, record.ToolNames);

    }

    [Fact]
    public async Task LoopContract_FinishReasonParity_BufferedAndStreaming_DefaultStop()
    {

        ScriptingChatClient bufferedChat = new();

        bufferedChat.EnqueueText("done");

        WizardIntelligenceProvider bufferedWizard = CreateWizard(bufferedChat);

        Result<PromptTurnResult> buffered = await bufferedWizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "hi", SkipSpellRouting = true, DisableMcpTools = true },
            CancellationToken.None);

        Assert.True(buffered.IsSuccess);

        Assert.Equal("stop", buffered.Value!.FinishReason);

        ScriptingChatClient streamChat = new();

        streamChat.EnqueueStreamTokens("done");

        WizardIntelligenceProvider streamWizard = CreateWizard(streamChat);

        List<IntelligenceEvent> events = await CollectStreamAsync(
            streamWizard,
            BaseRequest() with { Prompt = "hi", SkipSpellRouting = true, DisableMcpTools = true });

        IntelligenceEvent result = Assert.Single(events, static e => e.Type == IntelligenceEventType.Result);

        Assert.Equal("stop", result.FinishReason);

    }

    [Fact]
    public async Task LoopContract_BufferedUsage_AccumulatesAcrossToolRounds()
    {

        Guid sessionId = Guid.NewGuid();

        FakeGrimoireRepository grimoire = new();

        ScriptingChatClient chat = new() { UsageTotalTokens = 30 };

        chat.EnqueueToolCall(ArcanumLocalTimeTool.ToolName);

        chat.EnqueueText("answered");

        ArcanumSettings settings = DefaultSettings() with
        {
            Intelligence = DefaultSettings().Intelligence with { EnableTokenTracking = true },
        };

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, grimoire);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "time then answer",
                SessionId = sessionId,
                SkipSpellRouting = true,
                DisableMcpTools = true,
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        // tool-call response (30) + final text response (30), summed by AccumulateUsage across rounds.
        Assert.Equal(60, grimoire.LastIncrementedTokens);

    }

    [Fact]
    public async Task UsageMapping_PreservesProviderTotalsAndInconsistentSubsetCounts()
    {
        ChatResponse response = new(new MeAiChatMessage(ChatRole.Assistant, "answer"))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 10,
                OutputTokenCount = 8,
                TotalTokenCount = 17,
                CachedInputTokenCount = 14,
                ReasoningTokenCount = 11,
            },
        };
        ScriptingChatClient chat = new();
        chat.EnqueueResponse(response);
        WizardIntelligenceProvider wizard = CreateWizard(chat);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "usage",
                SkipSpellRouting = true,
                DisableMcpTools = true,
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        ChatCompletionUsage usage = Assert.IsType<ChatCompletionUsage>(result.Value.Usage);
        Assert.Equal(10, usage.PromptTokens);
        Assert.Equal(8, usage.CompletionTokens);
        Assert.Equal(17, usage.TotalTokens);
        Assert.Equal(14, usage.CachedTokens);
        Assert.Equal(11, usage.ReasoningTokens);
    }

    [Fact]
    public async Task UsageMapping_AccumulatesReasoningAndProviderTotalsAcrossToolRounds()
    {
        ChatResponse toolRound = new(new MeAiChatMessage(
            ChatRole.Assistant,
            [
                new FunctionCallContent(
                    "call-usage",
                    ArcanumLocalTimeTool.ToolName,
                    new Dictionary<string, object?>()),
            ]))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 10,
                OutputTokenCount = 8,
                TotalTokenCount = 25,
                CachedInputTokenCount = 4,
                ReasoningTokenCount = 3,
            },
        };
        ChatResponse finalRound = new(new MeAiChatMessage(ChatRole.Assistant, "answer"))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 5,
                OutputTokenCount = 7,
                TotalTokenCount = 20,
                CachedInputTokenCount = 1,
                ReasoningTokenCount = 4,
            },
        };
        ScriptingChatClient chat = new();
        chat.EnqueueResponse(toolRound);
        chat.EnqueueResponse(finalRound);
        WizardIntelligenceProvider wizard = CreateWizard(chat);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "usage",
                SkipSpellRouting = true,
                DisableMcpTools = true,
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        ChatCompletionUsage usage = Assert.IsType<ChatCompletionUsage>(result.Value.Usage);
        Assert.Equal(15, usage.PromptTokens);
        Assert.Equal(15, usage.CompletionTokens);
        Assert.Equal(45, usage.TotalTokens);
        Assert.Equal(5, usage.CachedTokens);
        Assert.Equal(7, usage.ReasoningTokens);
    }

    [Fact]
    public async Task UsageMapping_MissingUsageIsSafe()
    {
        ScriptingChatClient chat = new();
        chat.EnqueueText("answer");
        WizardIntelligenceProvider wizard = CreateWizard(chat);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "usage",
                SkipSpellRouting = true,
                DisableMcpTools = true,
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        ChatCompletionUsage usage = Assert.IsType<ChatCompletionUsage>(result.Value.Usage);
        Assert.Equal(0, usage.PromptTokens);
        Assert.Equal(0, usage.CompletionTokens);
        Assert.Equal(0, usage.TotalTokens);
        Assert.Equal(0, usage.CachedTokens);
        Assert.Equal(0, usage.ReasoningTokens);
    }

    [Fact]
    public async Task UsageMapping_MissingTotalFallsBackToNormalizedPromptAndCompletion()
    {
        ChatResponse response = new(new MeAiChatMessage(ChatRole.Assistant, "answer"))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = -10,
                OutputTokenCount = 20,
                TotalTokenCount = null,
            },
        };
        ScriptingChatClient chat = new();
        chat.EnqueueResponse(response);
        WizardIntelligenceProvider wizard = CreateWizard(chat);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "usage",
                SkipSpellRouting = true,
                DisableMcpTools = true,
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value.Usage?.PromptTokens);
        Assert.Equal(20, result.Value.Usage?.CompletionTokens);
        Assert.Equal(20, result.Value.Usage?.TotalTokens);
    }

    [Fact]
    public async Task AccountingReservation_UsesTypedRequestOutputAndReasoningBudgets()
    {
        ScriptingChatClient chat = new();
        chat.EnqueueText("answer");
        ArcanumSettings settings = SettingsWithReasoning() with
        {
            Pricing = new PricingSettings
            {
                DefaultPricing = new ModelPricingEntry
                {
                    OutputPer1M = 20m,
                    ReasoningPer1M = 80m,
                },
            },
        };
        settings.Providers[0].Models[0].Reasoning!.WireDialect = ReasoningWireDialect.OpenRouter;
        RecordingTurnRunWriter turnRuns = new();
        RecordingBudgetReservationService reservations = new();
        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            settings,
            turnRunWriter: turnRuns,
            budgetReservationService: reservations);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "budget",
                SkipSpellRouting = true,
                DisableMcpTools = true,
                MaxOutputTokens = 1_000,
                Reasoning = new ReasoningRequestOptions(BudgetTokens: 600),
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        BudgetReservationRequest request = Assert.IsType<BudgetReservationRequest>(reservations.LastRequest);
        Assert.Equal(
            BudgetReservationService.EstimateWorstCaseTurnUsd(
                settings.Pricing.DefaultPricing,
                maxOutputTokens: 1_000,
                reasoningBudgetTokens: 600),
            request.ReservedUsd);
    }

    [Fact]
    public void EnsureContextBudget_ExactReasoningReservationBoundaryFits()
    {
        const int maxOutputTokens = 100;
        const int reasoningBudgetTokens = 300;
        ArcanumSettings settings = DefaultSettings();
        List<MeAiChatMessage> messages = [new(ChatRole.User, "boundary prompt")];
        int messageTokens = CountContextTokens(settings, messages);
        ScriptingChatClient chat = new();
        WizardIntelligenceProvider wizard = CreateWizard(chat, settings);
        using ChatClientLease lease = new(
            chat,
            DefaultProvider() with { ContextWindowLimit = messageTokens + reasoningBudgetTokens },
            ModelName,
            ownedHttpClient: null);

        Result result = InvokeEnsureContextBudget(
            wizard,
            messages,
            lease,
            BaseRequest() with
            {
                MaxOutputTokens = maxOutputTokens,
                Reasoning = new ReasoningRequestOptions(BudgetTokens: reasoningBudgetTokens),
            });

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void EnsureContextBudget_RejectsOneTokenPastReasoningReservationBoundary()
    {
        const int maxOutputTokens = 100;
        const int reasoningBudgetTokens = 300;
        ArcanumSettings settings = DefaultSettings();
        List<MeAiChatMessage> messages = [new(ChatRole.User, "boundary prompt")];
        int messageTokens = CountContextTokens(settings, messages);
        ScriptingChatClient chat = new();
        WizardIntelligenceProvider wizard = CreateWizard(chat, settings);
        using ChatClientLease lease = new(
            chat,
            DefaultProvider() with { ContextWindowLimit = messageTokens + reasoningBudgetTokens - 1 },
            ModelName,
            ownedHttpClient: null);

        Result result = InvokeEnsureContextBudget(
            wizard,
            messages,
            lease,
            BaseRequest() with
            {
                MaxOutputTokens = maxOutputTokens,
                Reasoning = new ReasoningRequestOptions(BudgetTokens: reasoningBudgetTokens),
            });

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Hub.ContextBudgetExceeded, result.Error.Code);
    }

    [Fact]
    public async Task AccountingReconciliation_UsesProviderCountsWhenReportedTotalIsZero()
    {
        ChatResponse response = new(new MeAiChatMessage(ChatRole.Assistant, "answer"))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 10,
                OutputTokenCount = 8,
                TotalTokenCount = 0,
                ReasoningTokenCount = 11,
            },
        };
        ScriptingChatClient chat = new();
        chat.EnqueueResponse(response);
        ArcanumSettings settings = DefaultSettings() with
        {
            Pricing = new PricingSettings
            {
                DefaultPricing = new ModelPricingEntry
                {
                    OutputPer1M = 20m,
                    ReasoningPer1M = 80m,
                },
            },
        };
        RecordingTurnRunWriter turnRuns = new();
        RecordingBudgetReservationService reservations = new();
        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            settings,
            turnRunWriter: turnRuns,
            budgetReservationService: reservations);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "account",
                SkipSpellRouting = true,
                DisableMcpTools = true,
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value.Usage?.TotalTokens);
        BillableOperationRecord operation =
            Assert.IsType<BillableOperationRecord>(turnRuns.LastOperation);
        Assert.Equal(8, operation.OutputTokens);
        Assert.Equal(11, operation.ReasoningTokens);
        Assert.Equal(0.00064m, operation.ActualCostUsd);
        Assert.Equal(operation.ActualCostUsd, reservations.ReconciledUsd);
    }

    [Fact]
    public async Task Accounting_OutputGuardrailFailure_RetainsCompletedProviderUsage()
    {
        ChatResponse response = new(new MeAiChatMessage(ChatRole.Assistant, "bad-word"))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 10,
                OutputTokenCount = 5,
                TotalTokenCount = 15,
            },
        };
        ScriptingChatClient chat = new();
        chat.EnqueueResponse(response);
        ArcanumSettings settings = DefaultSettings() with
        {
            Guardrails = new GuardrailsSettings
            {
                Enabled = true,
                DetectPii = false,
                BlockToxicity = true,
                ToxicityBlocklist = ["bad-word"],
            },
            Pricing = new PricingSettings
            {
                DefaultPricing = new ModelPricingEntry { InputPer1M = 1m, OutputPer1M = 2m },
            },
        };
        RecordingTurnRunWriter turnRuns = new();
        RecordingBudgetReservationService reservations = new();
        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            settings,
            guardrailsPipeline: CreateGuardrailsPipeline(settings),
            turnRunWriter: turnRuns,
            budgetReservationService: reservations);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "guard", SkipSpellRouting = true, DisableMcpTools = true },
            CancellationToken.None);

        Assert.True(result.IsFailure);
        BillableOperationRecord operation = Assert.Single(turnRuns.Operations);
        Assert.Equal(10, operation.InputTokens);
        Assert.Equal(5, operation.OutputTokens);
        Assert.Equal(operation.ActualCostUsd, reservations.ReconciledUsd);
        Assert.Equal(1, reservations.ReconcileCount);
    }

    [Fact]
    public async Task Accounting_ToolRounds_RecordEachProviderCallWithoutFinalAggregate()
    {
        ChatResponse toolRound = new(new MeAiChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent("clock", ArcanumLocalTimeTool.ToolName, new Dictionary<string, object?>())]))
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 2, TotalTokenCount = 12 },
        };
        ChatResponse finalRound = new(new MeAiChatMessage(ChatRole.Assistant, "answer"))
        {
            Usage = new UsageDetails { InputTokenCount = 15, OutputTokenCount = 3, TotalTokenCount = 18 },
        };
        ScriptingChatClient chat = new();
        chat.EnqueueResponse(toolRound);
        chat.EnqueueResponse(finalRound);
        RecordingTurnRunWriter turnRuns = new();
        WizardIntelligenceProvider wizard = CreateWizard(chat, turnRunWriter: turnRuns);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "tools", SkipSpellRouting = true, DisableMcpTools = true },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Collection(
            turnRuns.Operations,
            first => Assert.Equal((10L, 2L), (first.InputTokens, first.OutputTokens)),
            second => Assert.Equal((15L, 3L), (second.InputTokens, second.OutputTokens)));
    }

    [Fact]
    public async Task Accounting_ToolLimitFailure_RetainsEveryCompletedProviderCallAndReconcilesOnce()
    {
        ChatResponse firstRound = new(new MeAiChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent("clock-1", ArcanumLocalTimeTool.ToolName, new Dictionary<string, object?>())]))
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 2, TotalTokenCount = 12 },
        };
        ChatResponse limitRound = new(new MeAiChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent("clock-2", ArcanumLocalTimeTool.ToolName, new Dictionary<string, object?>())]))
        {
            Usage = new UsageDetails { InputTokenCount = 15, OutputTokenCount = 3, TotalTokenCount = 18 },
        };
        ScriptingChatClient chat = new();
        chat.EnqueueResponse(firstRound);
        chat.EnqueueResponse(limitRound);
        ArcanumSettings settings = DefaultSettings() with
        {
            Intelligence = DefaultSettings().Intelligence with { MaxToolInferenceRounds = 1 },
        };
        RecordingTurnRunWriter turnRuns = new();
        RecordingBudgetReservationService reservations = new();
        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            settings,
            turnRunWriter: turnRuns,
            budgetReservationService: reservations);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "loop", SkipSpellRouting = true, DisableMcpTools = true },
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Hub.ToolLoop, result.Error.Code);
        Assert.Collection(
            turnRuns.Operations,
            first => Assert.Equal((10L, 2L), (first.InputTokens, first.OutputTokens)),
            second => Assert.Equal((15L, 3L), (second.InputTokens, second.OutputTokens)));
        Assert.Equal(1, reservations.ReconcileCount);
        Assert.Equal(turnRuns.Operations.Sum(static operation => operation.ActualCostUsd), reservations.ReconciledUsd);
    }

    [Fact]
    public async Task Accounting_SessionFinalizeFailure_RetainsProviderUsageAndReconcilesOnce()
    {
        ChatResponse response = new(new MeAiChatMessage(ChatRole.Assistant, "answer"))
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5, TotalTokenCount = 15 },
        };
        ScriptingChatClient chat = new();
        chat.EnqueueResponse(response);
        FakeGrimoireRepository grimoire = new() { ThrowOnFinalize = true };
        RecordingTurnRunWriter turnRuns = new();
        RecordingBudgetReservationService reservations = new();
        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            grimoire: grimoire,
            turnRunWriter: turnRuns,
            budgetReservationService: reservations);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "finalize",
                SessionId = Guid.NewGuid(),
                SkipSpellRouting = true,
                DisableMcpTools = true,
            },
            CancellationToken.None);

        Assert.True(result.IsFailure);
        BillableOperationRecord operation = Assert.Single(turnRuns.Operations);
        Assert.Equal((10L, 5L), (operation.InputTokens, operation.OutputTokens));
        Assert.Equal(1, reservations.ReconcileCount);
        Assert.Equal(operation.ActualCostUsd, reservations.ReconciledUsd);
    }

    [Fact]
    public async Task Accounting_StructuredRetryFailure_RetainsEveryCompletedProviderCall()
    {
        ChatResponse initial = new(new MeAiChatMessage(ChatRole.Assistant, "not json"))
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 2, TotalTokenCount = 12 },
        };
        ChatResponse retry = new(new MeAiChatMessage(ChatRole.Assistant, "still not json"))
        {
            Usage = new UsageDetails { InputTokenCount = 15, OutputTokenCount = 3, TotalTokenCount = 18 },
        };
        ScriptingChatClient chat = new();
        chat.EnqueueResponse(initial);
        chat.EnqueueResponse(retry);
        ArcanumSettings settings = DefaultSettings() with
        {
            StructuredOutput = new StructuredOutputSettings
            {
                Enabled = true,
                StrictMode = true,
                MaxValidationRetries = 1,
            },
        };
        JsonElement schema = JsonSerializer.Deserialize<JsonElement>("""
            {
              "type": "object",
              "required": ["answer"],
              "properties": { "answer": { "type": "string" } }
            }
            """);
        RecordingTurnRunWriter turnRuns = new();
        RecordingBudgetReservationService reservations = new();
        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            settings,
            turnRunWriter: turnRuns,
            budgetReservationService: reservations);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "structured",
                SkipSpellRouting = true,
                DisableMcpTools = true,
                ResponseFormat = "json_schema",
                ResponseFormatJsonSchema = schema,
            },
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Collection(
            turnRuns.Operations,
            first => Assert.Equal(BillableOperationType.Chat, first.OperationType),
            second => Assert.Equal(BillableOperationType.Retry, second.OperationType));
        Assert.Equal(1, reservations.ReconcileCount);
    }

    [Fact]
    public async Task Accounting_DurableWriteFailure_PropagatesAndKeepsReservation()
    {
        ChatResponse response = new(new MeAiChatMessage(ChatRole.Assistant, "answer"))
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5, TotalTokenCount = 15 },
        };
        ScriptingChatClient chat = new();
        chat.EnqueueResponse(response);
        RecordingTurnRunWriter turnRuns = new() { RecordException = new IOException("ledger unavailable") };
        RecordingBudgetReservationService reservations = new();
        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            turnRunWriter: turnRuns,
            budgetReservationService: reservations);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "account", SkipSpellRouting = true, DisableMcpTools = true },
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(0, reservations.ReconcileCount);
        Assert.False(reservations.WasReleased);
    }

    [Fact]
    public void AuxiliaryAccountingIdentity_WithoutSeparateLease_UsesActiveMainLease()
    {
        (string provider, string model) = WizardIntelligenceProvider.ResolveAuxiliaryAccountingIdentity(
            auxiliaryProvider: null,
            auxiliaryModel: null,
            mainProvider: "main-provider",
            mainModel: "resolved-main-model");

        Assert.Equal("main-provider", provider);
        Assert.Equal("resolved-main-model", model);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"entities":[]}""")]
    public async Task Accounting_RouterParseFailure_RetainsCompletedProviderUsage(string routerText)
    {
        await CreateSpellAsync("router-spell", "RouterSpell", dependencies: null);
        ChatResponse routerResponse = new(new MeAiChatMessage(ChatRole.Assistant, routerText))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 10,
                OutputTokenCount = 4,
                TotalTokenCount = 14,
            },
        };
        ScriptingChatClient chat = new();
        chat.EnqueueResponse(routerResponse);
        chat.EnqueueText("answer");
        ArcanumSettings settings = DefaultSettings() with
        {
            Intelligence = DefaultSettings().Intelligence with { EnableLexiconSystem = false },
            Pricing = new PricingSettings
            {
                DefaultPricing = new ModelPricingEntry { InputPer1M = 1m, OutputPer1M = 2m },
            },
        };
        RecordingTurnRunWriter turnRuns = new();
        RecordingBudgetReservationService reservations = new();
        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            settings,
            turnRunWriter: turnRuns,
            budgetReservationService: reservations);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "route this",
                WorkingDirectory = _workspace.Root,
                SkipSpellRouting = false,
                DisableMcpTools = true,
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        BillableOperationRecord operation = Assert.Single(turnRuns.Operations);
        Assert.Equal(BillableOperationType.Routing, operation.OperationType);
        Assert.Equal((10L, 4L), (operation.InputTokens, operation.OutputTokens));
        Assert.Equal(0.000018m, operation.ActualCostUsd);
        Assert.Equal(operation.ActualCostUsd, reservations.ReconciledUsd);
    }

    [Fact]
    public async Task Accounting_RoutingUsage_IgnoresCancellationAfterProviderCompletion()
    {
        await CreateSpellAsync("router-spell", "RouterSpell", dependencies: null);
        using CancellationTokenSource callerCancellation = new();
        ChatResponse routerResponse = new(new MeAiChatMessage(
            ChatRole.Assistant,
            """{"spellName":"NONE","entities":[]}"""))
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 4, TotalTokenCount = 14 },
        };
        ScriptingChatClient chat = new();
        chat.EnqueueResponse(routerResponse);
        ArcanumSettings settings = DefaultSettings() with
        {
            Intelligence = DefaultSettings().Intelligence with { EnableLexiconSystem = false },
            Pricing = new PricingSettings
            {
                DefaultPricing = new ModelPricingEntry { InputPer1M = 1m, OutputPer1M = 2m },
            },
        };
        RecordingTurnRunWriter turnRuns = new() { CancelBeforeRecord = callerCancellation };
        RecordingBudgetReservationService reservations = new();
        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            settings,
            turnRunWriter: turnRuns,
            budgetReservationService: reservations);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            wizard.ExecutePromptAsync(
                BaseRequest() with
                {
                    Prompt = "route this",
                    WorkingDirectory = _workspace.Root,
                    SkipSpellRouting = false,
                    DisableMcpTools = true,
                },
                callerCancellation.Token));

        BillableOperationRecord operation = Assert.Single(turnRuns.Operations);
        Assert.True(await WaitUntilAsync(() => reservations.ReconcileCount == 1, TimeSpan.FromSeconds(5)));
        Assert.Equal(BillableOperationType.Routing, operation.OperationType);
        Assert.Equal(operation.ActualCostUsd, reservations.ReconciledUsd);
        Assert.Equal(1, reservations.ReconcileCount);
        Assert.False(reservations.WasReleased);
    }

    [Fact]
    public async Task Accounting_ExtractionUsage_IgnoresCancellationAfterProviderCompletion()
    {
        await CreateSpellAsync("selected-spell", "SelectedSpell", dependencies: null);
        using CancellationTokenSource callerCancellation = new();
        ChatResponse extractionResponse = new(new MeAiChatMessage(
            ChatRole.Assistant,
            """{"entities":[]}"""))
        {
            Usage = new UsageDetails { InputTokenCount = 8, OutputTokenCount = 2, TotalTokenCount = 10 },
        };
        ScriptingChatClient chat = new();
        chat.EnqueueResponse(extractionResponse);
        ArcanumSettings settings = DefaultSettings() with
        {
            Intelligence = DefaultSettings().Intelligence with { EnableLexiconSystem = true },
            Pricing = new PricingSettings
            {
                DefaultPricing = new ModelPricingEntry { InputPer1M = 1m, OutputPer1M = 2m },
            },
        };
        RecordingTurnRunWriter turnRuns = new() { CancelBeforeRecord = callerCancellation };
        RecordingBudgetReservationService reservations = new();
        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            settings,
            turnRunWriter: turnRuns,
            budgetReservationService: reservations);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            wizard.ExecutePromptAsync(
                BaseRequest() with
                {
                    Prompt = "extract entities",
                    WorkingDirectory = _workspace.Root,
                    OverrideSpellName = "SelectedSpell",
                    SkipSpellRouting = false,
                    DisableMcpTools = true,
                },
                callerCancellation.Token));

        BillableOperationRecord operation = Assert.Single(turnRuns.Operations);
        Assert.True(await WaitUntilAsync(() => reservations.ReconcileCount == 1, TimeSpan.FromSeconds(5)));
        Assert.Equal(BillableOperationType.Extraction, operation.OperationType);
        Assert.Equal(operation.ActualCostUsd, reservations.ReconciledUsd);
        Assert.Equal(1, reservations.ReconcileCount);
        Assert.False(reservations.WasReleased);
    }

    [Fact]
    public async Task LoopContract_BufferedTimeout_DiscardsInFlightRow_NoOrphan()
    {

        FakeGrimoireRepository grimoire = new();

        ScriptingChatClient chat = new();

        chat.EnqueueSlowBuffered(TimeSpan.FromSeconds(30), "late");

        ArcanumSettings settings = DefaultSettings() with
        {
            Intelligence = new IntelligenceSettings { InferenceTimeoutSeconds = 1, EnableLexiconSystem = false },
        };

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, grimoire);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "timeout",
                SessionId = Guid.NewGuid(),
                SkipSpellRouting = true,
                DisableMcpTools = true,
            },
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Hub.Timeout", result.Error.Code);

        // W3.5: the in-flight assistant row is discarded via CancellationToken.None, not orphaned.
        Assert.Equal(1, grimoire.DiscardCallCount);

    }

    [Fact]
    public async Task LoopContract_StreamingFailureWithPartial_FinalizesPartial_NoOrphan()
    {

        Guid sessionId = Guid.NewGuid();

        FakeGrimoireRepository grimoire = new() { FixedSessionId = sessionId };

        ScriptingChatClient chat = new();

        chat.EnqueueStreamFailure(new InvalidOperationException("stream broke mid-flight"));

        WizardIntelligenceProvider wizard = CreateWizard(chat, grimoire: grimoire);

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with
            {
                Prompt = "partial then fail",
                SessionId = sessionId,
                SkipSpellRouting = true,
                DisableMcpTools = true,
            });

        Assert.Contains(events, static e => e.Type == IntelligenceEventType.Error);

        // EnqueueStreamFailure yields "partial" before throwing; that partial is finalized via
        // CancellationToken.None (not discarded), so no orphaned in-flight row remains.
        Assert.Equal(1, grimoire.FinalizeCallCount);

        Assert.Equal(0, grimoire.DiscardCallCount);

    }

    // --- RAG Phase 3 — semantic context injection scenarios ---

    [Fact]
    public async Task ScenarioRag01_CodebaseRetrievalEnabled_InjectsSemanticContextIntoSystemPrompt()
    {
        string dbPath = Path.Combine(_workspace.Root, $"rag-{Guid.NewGuid():N}.db");

        await using ArcanumDbContext db = CreateWorkspaceChunksDbContext(dbPath);

        await SeedWorkspaceFileChunkAsync(db, _workspace.Root, "src/Foo.cs", chunkId: "chunk-1", content: "public class Foo {}");

        FakeRagWeaveService weave = new() { Available = true };

        FakeRagDivinationService divination = new()
        {
            Results = [new DivinationResult("chunk-1", 0.95f, EmptyDivinationMetadata)],
        };

        FakeRagWorkspaceIndexingService indexing = new();

        ArcanumSettings settings = DefaultSettings() with
        {
            Embeddings = new EmbeddingSettings { Enabled = true, CodebaseRetrievalEnabled = true },
        };

        ScriptingChatClient chat = new();

        chat.EnqueueText("buffered answer");

        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            settings,
            weaveService: weave,
            divinationService: divination,
            workspaceIndexingService: indexing,
            db: db);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "how does Foo work?", SkipSpellRouting = true, DisableMcpTools = true, WorkingDirectory = _workspace.Root },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        string systemPrompt = ExtractSystemPromptText(chat.LastBufferedMessages);

        Assert.Contains("### Semantic Context (Retrieved Codebase)", systemPrompt, StringComparison.Ordinal);

        Assert.Contains("src/Foo.cs", systemPrompt, StringComparison.Ordinal);

        Assert.Contains("public class Foo {}", systemPrompt, StringComparison.Ordinal);

        Assert.Contains(_workspace.Root, indexing.RegisteredPaths);

    }

    [Fact]
    public async Task ScenarioRag02_EmbeddingFailure_DegradesGracefully_NoSemanticContext()
    {
        FakeRagWeaveService weave = new() { Available = true, FailEmbed = true };

        ArcanumSettings settings = DefaultSettings() with
        {
            Embeddings = new EmbeddingSettings { Enabled = true, CodebaseRetrievalEnabled = true },
        };

        ScriptingChatClient chat = new();

        chat.EnqueueText("buffered answer");

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, weaveService: weave);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "hello", SkipSpellRouting = true, DisableMcpTools = true, WorkingDirectory = _workspace.Root },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        string systemPrompt = ExtractSystemPromptText(chat.LastBufferedMessages);

        Assert.DoesNotContain("Semantic Context", systemPrompt, StringComparison.Ordinal);

    }

    [Fact]
    public async Task ScenarioRag03_Disabled_NeverRegistersWorkspace_NoSemanticContext()
    {
        FakeRagWorkspaceIndexingService indexing = new();

        ScriptingChatClient chat = new();

        chat.EnqueueText("buffered answer");

        // DefaultSettings() leaves Embeddings at its all-false default.
        WizardIntelligenceProvider wizard = CreateWizard(chat, workspaceIndexingService: indexing);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "hello", SkipSpellRouting = true, DisableMcpTools = true, WorkingDirectory = _workspace.Root },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        string systemPrompt = ExtractSystemPromptText(chat.LastBufferedMessages);

        Assert.DoesNotContain("Semantic Context", systemPrompt, StringComparison.Ordinal);

        Assert.Empty(indexing.RegisteredPaths);

    }

    [Fact]
    public async Task ScenarioRag04_EmptyWorkingDirectory_NoSemanticContext_EvenWhenEnabled()
    {
        FakeRagWorkspaceIndexingService indexing = new();

        ArcanumSettings settings = DefaultSettings() with
        {
            Embeddings = new EmbeddingSettings { Enabled = true, CodebaseRetrievalEnabled = true },
        };

        ScriptingChatClient chat = new();

        chat.EnqueueText("buffered answer");

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, workspaceIndexingService: indexing);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "hello", SkipSpellRouting = true, DisableMcpTools = true, WorkingDirectory = string.Empty },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        string systemPrompt = ExtractSystemPromptText(chat.LastBufferedMessages);

        Assert.DoesNotContain("Semantic Context", systemPrompt, StringComparison.Ordinal);

        Assert.Empty(indexing.RegisteredPaths);

    }

    [Fact]
    public async Task ScenarioSaga01_SagaEnabled_InjectsSagaMemoriesIntoSystemPrompt()
    {
        FakeRagWeaveService weave = new() { Available = true };

        FakeRagDivinationService divination = new()
        {
            Results = [new DivinationResult("memory-1", 0.88f, EmptyDivinationMetadata)],
        };

        FakeSagaMemoryStore store = new();

        store.Memories["memory-1"] = new SagaMemoryDto(
            "memory-1",
            "The operator prefers dark mode.",
            new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
            SessionId: null,
            Tags: null,
            Source: "extraction");

        ArcanumSettings settings = DefaultSettings() with
        {
            Embeddings = new EmbeddingSettings { Enabled = true, SagaEnabled = true },
        };

        ScriptingChatClient chat = new();

        chat.EnqueueText("buffered answer");

        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            settings,
            weaveService: weave,
            divinationService: divination,
            sagaMemoryStore: store);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "what theme do I like?", SkipSpellRouting = true, DisableMcpTools = true, WorkingDirectory = _workspace.Root },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        string systemPrompt = ExtractSystemPromptText(chat.LastBufferedMessages);

        Assert.Contains("### Saga (Associative Memory)", systemPrompt, StringComparison.Ordinal);

        Assert.Contains("The operator prefers dark mode.", systemPrompt, StringComparison.Ordinal);

    }

    [Fact]
    public async Task ScenarioSaga02_EmbeddingFailure_DegradesGracefully_NoSagaMemories()
    {
        FakeRagWeaveService weave = new() { Available = true, FailEmbed = true };

        ArcanumSettings settings = DefaultSettings() with
        {
            Embeddings = new EmbeddingSettings { Enabled = true, SagaEnabled = true },
        };

        ScriptingChatClient chat = new();

        chat.EnqueueText("buffered answer");

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, weaveService: weave);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "hello", SkipSpellRouting = true, DisableMcpTools = true, WorkingDirectory = _workspace.Root },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        string systemPrompt = ExtractSystemPromptText(chat.LastBufferedMessages);

        Assert.DoesNotContain("Saga (Associative Memory)", systemPrompt, StringComparison.Ordinal);

    }

    [Fact]
    public async Task ScenarioSaga03_Disabled_NoSagaMemories_NoEmbedCall()
    {
        FakeRagWeaveService weave = new() { Available = true };

        ScriptingChatClient chat = new();

        chat.EnqueueText("buffered answer");

        // DefaultSettings() leaves Embeddings at its all-false default (Saga disabled too).
        WizardIntelligenceProvider wizard = CreateWizard(chat, weaveService: weave);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "hello", SkipSpellRouting = true, DisableMcpTools = true, WorkingDirectory = _workspace.Root },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        string systemPrompt = ExtractSystemPromptText(chat.LastBufferedMessages);

        Assert.DoesNotContain("Saga (Associative Memory)", systemPrompt, StringComparison.Ordinal);

        Assert.Equal(0, weave.EmbedCallCount);

    }

    [Fact]
    public async Task ScenarioSaga04_CodebaseAndSagaBothEnabled_EmbedsQueryOnceNotTwice()
    {
        string dbPath = Path.Combine(_workspace.Root, $"rag-{Guid.NewGuid():N}.db");

        await using ArcanumDbContext db = CreateWorkspaceChunksDbContext(dbPath);

        await SeedWorkspaceFileChunkAsync(db, _workspace.Root, "src/Foo.cs", chunkId: "chunk-1", content: "public class Foo {}");

        FakeRagWeaveService weave = new() { Available = true };

        FakeRagDivinationService divination = new()
        {
            Results = [new DivinationResult("chunk-1", 0.95f, EmptyDivinationMetadata)],
        };

        FakeSagaMemoryStore store = new();

        ArcanumSettings settings = DefaultSettings() with
        {
            Embeddings = new EmbeddingSettings { Enabled = true, CodebaseRetrievalEnabled = true, SagaEnabled = true },
        };

        ScriptingChatClient chat = new();

        chat.EnqueueText("buffered answer");

        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            settings,
            weaveService: weave,
            divinationService: divination,
            sagaMemoryStore: store,
            db: db);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "how does Foo work?", SkipSpellRouting = true, DisableMcpTools = true, WorkingDirectory = _workspace.Root },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        // Both RetrieveSemanticContextAsync (Phase 3) and RetrieveSagaMemoriesAsync (Phase 4) need the
        // same query embedding this turn — ResolveRagQueryEmbeddingAsync/EmbedQueryAsync must compute
        // it exactly once and share it, never embedding the same prompt twice.
        Assert.Equal(1, weave.EmbedCallCount);

    }

    [Fact]
    public async Task ScenarioSpell01_PureEmbeddingRouting_SelectsSpellWithoutLlmRouterCall()
    {
        await CreateSpellWithDescriptionAsync("alpha", "Alpha", "Alpha spell for testing dark mode preferences", body: "alpha body");

        await CreateSpellWithDescriptionAsync("beta", "Beta", "Beta spell unrelated to anything", body: "beta body");

        FakeRagWeaveService weave = new() { Available = true, QueryVector = [1f, 0f, 0f] };

        weave.BatchVectorsByText["Alpha spell for testing dark mode preferences"] = [1f, 0f, 0f];

        weave.BatchVectorsByText["Beta spell unrelated to anything"] = [0f, 1f, 0f];

        ArcanumSettings settings = DefaultSettings() with
        {
            Embeddings = new EmbeddingSettings
            {
                Enabled = true,
                SemanticSpellRoutingEnabled = true,
                SpellRoutingHybridMode = false,
                SimilarityThreshold = 0.5f,
            },
        };

        ScriptingChatClient chat = new();

        // Only the final answer is scripted: if SemanticSpellRouter fell back to the LLM router, that
        // fallback call would consume this response and the real inference call would fail with "No
        // scripted buffered response remaining" instead of succeeding.
        chat.EnqueueText("alpha answer");

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, weaveService: weave);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "what theme do I like?",
                WorkingDirectory = _workspace.Root,
                SkipSpellRouting = false,
                DisableMcpTools = true,
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        string systemPrompt = ExtractSystemPromptText(chat.LastBufferedMessages);

        Assert.Contains("### Active Operational Spell (Alpha)", systemPrompt, StringComparison.Ordinal);

        Assert.Contains("alpha body", systemPrompt, StringComparison.Ordinal);

        Assert.DoesNotContain("Beta", systemPrompt, StringComparison.Ordinal);

    }

    // === Scrying (vision/multimodality) capability gate ===

    [Fact]
    public async Task ScenarioScrying01_NonVisionModel_RejectsWithVisionNotSupported()
    {

        WizardIntelligenceProvider wizard = CreateWizard(new ScriptingChatClient());

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "describe this",
                SkipSpellRouting = true,
                DisableMcpTools = true,
                ScryingFoci = [new ScryingFocusDto(Convert.ToBase64String([1, 2, 3]), "image/png")],
            },
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Scrying.VisionNotSupported, result.Error.Code);

    }

    [Fact]
    public async Task ScenarioScrying02_VisionCapableModel_AcceptsImageAndSucceeds()
    {

        ArcanumSettings settings = DefaultSettings() with
        {
            Providers = [DefaultProvider() with { Models = [new ModelEntry(ModelName, SupportsVision: true)] }],
        };

        ScriptingChatClient chat = new();

        chat.EnqueueText("I see a red square");

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "describe this",
                SkipSpellRouting = true,
                DisableMcpTools = true,
                ScryingFoci = [new ScryingFocusDto(Convert.ToBase64String([1, 2, 3]), "image/png")],
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal("I see a red square", result.Value!.Text);

    }

    [Fact]
    public async Task ScenarioScrying03_FeatureDisabled_RejectsEvenForVisionCapableModel()
    {

        ArcanumSettings settings = DefaultSettings() with
        {
            Providers = [DefaultProvider() with { Models = [new ModelEntry(ModelName, SupportsVision: true)] }],
            Scrying = new ScryingSettings { Enabled = false },
        };

        WizardIntelligenceProvider wizard = CreateWizard(new ScriptingChatClient(), settings);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "describe this",
                SkipSpellRouting = true,
                DisableMcpTools = true,
                ScryingFoci = [new ScryingFocusDto(Convert.ToBase64String([1, 2, 3]), "image/png")],
            },
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Scrying.FeatureDisabled, result.Error.Code);

    }

    [Fact]
    public async Task ScenarioScrying04_TooManyImages_ReturnsValidationError()
    {

        ArcanumSettings settings = DefaultSettings() with
        {
            Providers = [DefaultProvider() with { Models = [new ModelEntry(ModelName, SupportsVision: true)] }],
            Scrying = new ScryingSettings { MaxImagesPerRequest = 1 },
        };

        WizardIntelligenceProvider wizard = CreateWizard(new ScriptingChatClient(), settings);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "describe these",
                SkipSpellRouting = true,
                DisableMcpTools = true,
                ScryingFoci =
                [
                    new ScryingFocusDto(Convert.ToBase64String([1, 2, 3]), "image/png"),
                    new ScryingFocusDto(Convert.ToBase64String([4, 5, 6]), "image/png"),
                ],
            },
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Scrying.TooManyImages, result.Error.Code);

    }

    [Fact]
    public async Task ScenarioScrying05_ImageTooLarge_ReturnsValidationError()
    {

        ArcanumSettings settings = DefaultSettings() with
        {
            Providers = [DefaultProvider() with { Models = [new ModelEntry(ModelName, SupportsVision: true)] }],
            Scrying = new ScryingSettings { MaxImageBytes = 1024 },
        };

        WizardIntelligenceProvider wizard = CreateWizard(new ScriptingChatClient(), settings);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "describe this",
                SkipSpellRouting = true,
                DisableMcpTools = true,
                ScryingFoci = [new ScryingFocusDto(new string('A', 4000), "image/png")],
            },
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Scrying.ImageTooLarge, result.Error.Code);

    }

    [Fact]
    public async Task ScenarioScrying06_UnsupportedMimeType_ReturnsValidationError()
    {

        ArcanumSettings settings = DefaultSettings() with
        {
            Providers = [DefaultProvider() with { Models = [new ModelEntry(ModelName, SupportsVision: true)] }],
        };

        WizardIntelligenceProvider wizard = CreateWizard(new ScriptingChatClient(), settings);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "describe this",
                SkipSpellRouting = true,
                DisableMcpTools = true,
                ScryingFoci = [new ScryingFocusDto(Convert.ToBase64String([1, 2, 3]), "image/tiff")],
            },
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Scrying.UnsupportedMimeType, result.Error.Code);

    }

    [Fact]
    public async Task ScenarioScrying07_StreamNonVisionModel_EmitsErrorEvent()
    {

        WizardIntelligenceProvider wizard = CreateWizard(new ScriptingChatClient());

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with
            {
                Prompt = "describe this",
                SkipSpellRouting = true,
                DisableMcpTools = true,
                ScryingFoci = [new ScryingFocusDto(Convert.ToBase64String([1, 2, 3]), "image/png")],
            });

        IntelligenceEvent errorEvent = Assert.Single(events, static e => e.Type == IntelligenceEventType.Error);

        Assert.Contains("vision", errorEvent.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task ScenarioScrying08_NoImages_SkipsGateEntirely()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueText("no images here");

        WizardIntelligenceProvider wizard = CreateWizard(chat);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "hello", SkipSpellRouting = true, DisableMcpTools = true },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

    }

    private static readonly IReadOnlyDictionary<string, string> EmptyDivinationMetadata = new Dictionary<string, string>(0);

    private static string ExtractSystemPromptText(IReadOnlyList<MeAiChatMessage> messages)
    {
        MeAiChatMessage? systemMessage = messages.FirstOrDefault(static m => m.Role == ChatRole.System);

        Assert.NotNull(systemMessage);

        return systemMessage.Text;
    }

    private static ArcanumDbContext CreateWorkspaceChunksDbContext(string dbPath)
    {
        DbContextOptions<ArcanumDbContext> options = new DbContextOptionsBuilder<ArcanumDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .UseModel(ArcanumDbContextModel.Instance)
            .Options;

        return new ArcanumDbContext(options, new UnusedSecretStore(), new UnusedPassphraseSource());
    }

    private static async Task SeedWorkspaceFileChunkAsync(
        ArcanumDbContext db,
        string workspacePath,
        string relativePath,
        string chunkId,
        string content)
    {
        DbConnection connection = db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using DbCommand createCmd = connection.CreateCommand();

        createCmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS workspace_file_chunks (
                ChunkId TEXT PRIMARY KEY,
                WorkspacePath TEXT NOT NULL,
                RelativePath TEXT NOT NULL,
                ChunkIndex INTEGER NOT NULL,
                Content TEXT NOT NULL,
                CharOffset INTEGER NOT NULL,
                CharLength INTEGER NOT NULL,
                FileLastWriteTime TEXT NOT NULL,
                IndexedAt TEXT NOT NULL
            );
            """;

        _ = await createCmd.ExecuteNonQueryAsync();

        await using DbCommand insertCmd = connection.CreateCommand();

        insertCmd.CommandText =
            """
            INSERT INTO workspace_file_chunks
                (ChunkId, WorkspacePath, RelativePath, ChunkIndex, Content, CharOffset, CharLength, FileLastWriteTime, IndexedAt)
            VALUES
                (@chunkId, @workspacePath, @relativePath, 0, @content, 0, @charLength, @now, @now)
            """;

        DateTimeOffset now = DateTimeOffset.UtcNow;

        AddParameter(insertCmd, "@chunkId", chunkId);

        AddParameter(insertCmd, "@workspacePath", workspacePath);

        AddParameter(insertCmd, "@relativePath", relativePath);

        AddParameter(insertCmd, "@content", content);

        AddParameter(insertCmd, "@charLength", content.Length);

        AddParameter(insertCmd, "@now", now.ToString("o"));

        _ = await insertCmd.ExecuteNonQueryAsync();

    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {
        DbParameter parameter = cmd.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        cmd.Parameters.Add(parameter);
    }

    private sealed class FakeRagWeaveService : IWeaveService
    {

        public bool Available { get; set; } = true;

        public bool FailEmbed { get; set; }

        public float[] QueryVector { get; set; } = [1f, 0f, 0f];

        public int EmbedCallCount { get; private set; }

        public bool IsAvailable => Available;

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken)
        {
            EmbedCallCount++;

            if (FailEmbed)
            {
                return Task.FromResult(Result<Embedding<float>>.Failure(
                    new Error(ErrorCodes.Embeddings.ProviderUnavailable, "Simulated embedding failure.")));
            }

            return Task.FromResult(Result<Embedding<float>>.Success(new Embedding<float>(QueryVector)));
        }

        /// <summary>RAG Phase 5 — per-description vectors for <see cref="SpellWeaveCache"/> scenarios; falls back to <see cref="QueryVector"/> for any text not registered here.</summary>
        public Dictionary<string, float[]> BatchVectorsByText { get; } = new(StringComparer.Ordinal);

        public bool FailEmbedBatch { get; set; }

        public int EmbedBatchCallCount { get; private set; }

        public Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {
            EmbedBatchCallCount++;

            if (FailEmbedBatch)
            {
                return Task.FromResult(Result<Embedding<float>[]>.Failure(
                    new Error(ErrorCodes.Embeddings.ProviderUnavailable, "Simulated batch embedding failure.")));
            }

            Embedding<float>[] result = new Embedding<float>[texts.Count];

            for (int i = 0; i < texts.Count; i++)
            {
                float[] vector = BatchVectorsByText.TryGetValue(texts[i], out float[]? registered) ? registered : QueryVector;

                result[i] = new Embedding<float>(vector);
            }

            return Task.FromResult(Result<Embedding<float>[]>.Success(result));
        }

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(string text, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by WizardIntelligenceProvider's semantic context retrieval.");

    }

    private sealed class FakeRagDivinationService : IDivinationService
    {

        public DivinationResult[] Results { get; set; } = [];

        public bool Fail { get; set; }

        public Task<Result<DivinationResult[]>> SearchAsync(
            string tableName,
            string primaryKeyColumn,
            string embeddingColumn,
            Embedding<float> queryEmbedding,
            int maxResults,
            float similarityThreshold,
            CancellationToken cancellationToken)
        {

            if (Fail)
            {
                return Task.FromResult(Result<DivinationResult[]>.Failure(
                    new Error(ErrorCodes.Embeddings.ProviderUnavailable, "Simulated search failure.")));
            }

            return Task.FromResult(Result<DivinationResult[]>.Success(Results));

        }

        public Task<Result<DivinationResult[]>> SearchScopedAsync(
            string tableName,
            string primaryKeyColumn,
            string embeddingColumn,
            string scopeTableName,
            string scopeJoinColumn,
            string scopeFilterColumn,
            string scopeFilterValue,
            Embedding<float> queryEmbedding,
            int maxResults,
            float similarityThreshold,
            CancellationToken cancellationToken) =>
            SearchAsync(tableName, primaryKeyColumn, embeddingColumn, queryEmbedding, maxResults, similarityThreshold, cancellationToken);

    }

    private sealed class FakeRagWorkspaceIndexingService : IWorkspaceIndexingService
    {

        public List<string> RegisteredPaths { get; } = [];

        public void RegisterWorkspace(string workspacePath)
        {

            RegisteredPaths.Add(workspacePath);

        }

        public Task IndexNowAsync(string workspacePath, CancellationToken cancellationToken) => Task.CompletedTask;

    }

    /// <summary>RAG Phase 4 — in-memory <see cref="ISagaMemoryStore"/> fake; no raw SQL needed for hub-level scenario tests.</summary>
    private sealed class FakeSagaMemoryStore : ISagaMemoryStore
    {

        public Dictionary<string, SagaMemoryDto> Memories { get; } = new(StringComparer.Ordinal);

        public Task InsertAsync(
            string id,
            string content,
            DateTimeOffset createdAt,
            Guid? sessionId,
            string? tags,
            string? source,
            float[] embedding,
            CancellationToken cancellationToken)
        {

            Memories[id] = new SagaMemoryDto(id, content, createdAt, sessionId, tags, source);

            return Task.CompletedTask;

        }

        public Task<int> CountAsync(CancellationToken cancellationToken) => Task.FromResult(Memories.Count);

        public Task<int> CountBySessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
            Task.FromResult(Memories.Values.Count(m => m.SessionId == sessionId));

        public Task<SagaMemoryDto[]> ListAsync(string? query, Guid? sessionId, int limit, int offset, CancellationToken cancellationToken) =>
            Task.FromResult(Memories.Values.Skip(offset).Take(limit).ToArray());

        public Task<IReadOnlyDictionary<string, SagaMemoryDto>> GetByIdsAsync(
            IReadOnlyList<string> ids,
            CancellationToken cancellationToken)
        {

            Dictionary<string, SagaMemoryDto> result = new(StringComparer.Ordinal);

            foreach (string id in ids)
            {

                if (Memories.TryGetValue(id, out SagaMemoryDto? memory))
                {

                    result[id] = memory;

                }

            }

            return Task.FromResult((IReadOnlyDictionary<string, SagaMemoryDto>)result);

        }

        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken) => Task.FromResult(Memories.Remove(id));

        public Task DeleteAllAsync(CancellationToken cancellationToken)
        {

            Memories.Clear();

            return Task.CompletedTask;

        }

        public Task<SagaStats> GetStatsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SagaStats(Memories.Count, Memories.Values.Select(static m => m.SessionId).Distinct().Count(), null, null));

        public Task<DateTimeOffset?> GetWatermarkAsync(Guid sessionId, CancellationToken cancellationToken) =>
            Task.FromResult<DateTimeOffset?>(null);

        public Task SetWatermarkAsync(Guid sessionId, DateTimeOffset lastExtractedEntryCreatedAt, CancellationToken cancellationToken) =>
            Task.CompletedTask;

    }

    private sealed class UnusedSecretStore : ISecretStore
    {

        public Task<string?> GetApiKeyAsync() => throw new NotSupportedException("Unused in RAG-disabled scenarios.");

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() => throw new NotSupportedException("Unused in RAG-disabled scenarios.");

        public Task SaveApiKeyAsync(string apiKey) => throw new NotSupportedException("Unused in RAG-disabled scenarios.");

        public Task<string?> GetGrimoireEncryptionSecretAsync() => throw new NotSupportedException("Unused in RAG-disabled scenarios.");

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => throw new NotSupportedException("Unused in RAG-disabled scenarios.");

    }

    private sealed class UnusedPassphraseSource : IGrimoireDbPassphraseSource
    {

        public string Passphrase => throw new NotSupportedException("Unused: DbContextOptions is pre-configured so OnConfiguring never reads this.");

        public void SetPassphrase(string passphrase) =>
            throw new NotSupportedException("Unused: DbContextOptions is pre-configured so OnConfiguring never reads this.");

    }

    [Theory]
    [InlineData(ReasoningOutputMode.None, ReasoningOutput.None)]
    [InlineData(ReasoningOutputMode.Summary, ReasoningOutput.Summary)]
    [InlineData(ReasoningOutputMode.Full, ReasoningOutput.Full)]
    public async Task ReasoningMapping_BufferedStandardDialect_MapsEffortAndOutput(
        ReasoningOutputMode requestedOutput,
        ReasoningOutput expectedOutput)
    {
        ReasoningCapabilities capabilities = new()
        {
            ControlSupport = ReasoningControlSupport.Effort,
            SupportsSummary = true,
            SupportsFull = true,
            AllowsClientOutput = true,
            WireDialect = ReasoningWireDialect.Standard,
        };

        ArcanumSettings settings = DefaultSettings() with
        {
            Providers =
            [
                DefaultProvider() with
                {
                    Models = [new ModelEntry(ModelName, Reasoning: capabilities)],
                },
            ],
        };

        ScriptingChatClient chat = new();

        chat.EnqueueText("normal answer");

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "reason",
                SkipSpellRouting = true,
                DisableMcpTools = true,
                Reasoning = new ReasoningRequestOptions(
                    Effort: ReasoningEffortLevel.High,
                    Output: requestedOutput),
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("normal answer", result.Value.Text);
        Assert.Equal(ReasoningEffort.High, chat.LastChatOptions?.Reasoning?.Effort);
        Assert.Equal(expectedOutput, chat.LastChatOptions?.Reasoning?.Output);
    }

    [Fact]
    public async Task ReasoningMapping_StreamingStandardDialect_MapsTypedOptions()
    {
        ReasoningCapabilities capabilities = new()
        {
            ControlSupport = ReasoningControlSupport.Effort,
            SupportsSummary = true,
            SupportsStreaming = true,
            AllowsClientOutput = true,
            WireDialect = ReasoningWireDialect.Standard,
        };

        ArcanumSettings settings = DefaultSettings() with
        {
            Providers =
            [
                DefaultProvider() with
                {
                    Models = [new ModelEntry(ModelName, Reasoning: capabilities)],
                },
            ],
        };

        ScriptingChatClient chat = new();

        chat.EnqueueStreamTokens("normal ", "answer");

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings);

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with
            {
                Prompt = "reason",
                SkipSpellRouting = true,
                DisableMcpTools = true,
                Reasoning = new ReasoningRequestOptions(
                    Effort: ReasoningEffortLevel.Medium,
                    Output: ReasoningOutputMode.Summary),
            });

        Assert.Contains(events, static e => e.Type == IntelligenceEventType.Result);
        Assert.Equal(ReasoningEffort.Medium, chat.LastChatOptions?.Reasoning?.Effort);
        Assert.Equal(ReasoningOutput.Summary, chat.LastChatOptions?.Reasoning?.Output);
    }

    [Fact]
    public async Task ReasoningMapping_DefaultRequest_DoesNotAddProviderOptions()
    {
        ScriptingChatClient chat = new();

        chat.EnqueueText("normal answer");

        WizardIntelligenceProvider wizard = CreateWizard(chat);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "plain",
                SkipSpellRouting = true,
                DisableMcpTools = true,
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(chat.LastChatOptions?.Reasoning);
        Assert.Null(chat.LastChatOptions?.RawRepresentationFactory);
    }

    [Fact]
    public async Task Reasoning_BufferedProjection_ExposesClientSafeSummarySeparately()
    {
        TextReasoningContent reasoning = new("client-safe summary")
        {
            ProtectedData = "opaque-provider-state",
        };
        ScriptingChatClient chat = new();
        chat.EnqueueResponse(new ChatResponse(new MeAiChatMessage(
            ChatRole.Assistant,
            [
                reasoning,
                new TextContent("answer only"),
            ])));
        FakeGrimoireRepository grimoire = new();
        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            SettingsWithReasoning(),
            grimoire: grimoire);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "reason",
                SkipSpellRouting = true,
                DisableMcpTools = true,
                Reasoning = new ReasoningRequestOptions(Output: ReasoningOutputMode.Summary),
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        ReasoningContentSegment projected = Assert.Single(result.Value.Reasoning);
        Assert.Equal("client-safe summary", projected.Text);
        Assert.Equal(ReasoningOutputMode.Summary, projected.Output);
        Assert.Equal("answer only", result.Value.Text);
        Assert.Equal("answer only", grimoire.LastFinalizedContent);
        Assert.DoesNotContain("opaque-provider-state", projected.Text, StringComparison.Ordinal);
        string json = JsonSerializer.Serialize(
            result.Value,
            ArcanumJsonContext.Default.PromptTurnResult);
        Assert.Contains("client-safe summary", json, StringComparison.Ordinal);
        Assert.DoesNotContain("opaque-provider-state", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Reasoning_UnspecifiedOutput_RequiresActualModelClientOutputCapability(
        bool allowsClientOutput,
        bool expectReasoning)
    {
        ReasoningCapabilities capabilities = new()
        {
            SupportsFull = true,
            AllowsClientOutput = allowsClientOutput,
        };
        ArcanumSettings settings = DefaultSettings() with
        {
            Providers =
            [
                DefaultProvider() with
                {
                    Models = [new ModelEntry(ModelName, Reasoning: capabilities)],
                },
            ],
        };
        ScriptingChatClient chat = new();
        chat.EnqueueResponse(new ChatResponse(new MeAiChatMessage(
            ChatRole.Assistant,
            [
                new TextReasoningContent("provider-default reasoning"),
                new TextContent("answer"),
            ])));
        WizardIntelligenceProvider wizard = CreateWizard(chat, settings);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "reason",
                SkipSpellRouting = true,
                DisableMcpTools = true,
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(expectReasoning, result.Value.Reasoning.Count > 0);
        if (expectReasoning)
        {
            ReasoningContentSegment projected = Assert.Single(result.Value.Reasoning);
            Assert.Equal("provider-default reasoning", projected.Text);
            Assert.Equal(ReasoningOutputMode.Full, projected.Output);
        }
        Assert.Equal("answer", result.Value.Text);
    }

    [Fact]
    public async Task Reasoning_UnspecifiedOutput_SummaryOnlyModelDefaultsToSummary()
    {
        ReasoningCapabilities capabilities = new()
        {
            SupportsSummary = true,
            SupportsFull = false,
            AllowsClientOutput = true,
        };
        ArcanumSettings settings = DefaultSettings() with
        {
            Providers =
            [
                DefaultProvider() with
                {
                    Models = [new ModelEntry(ModelName, Reasoning: capabilities)],
                },
            ],
        };
        ScriptingChatClient chat = new();
        chat.EnqueueResponse(new ChatResponse(new MeAiChatMessage(
            ChatRole.Assistant,
            [
                new TextReasoningContent("provider-default summary"),
                new TextContent("answer"),
            ])));
        WizardIntelligenceProvider wizard = CreateWizard(chat, settings);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "reason",
                SkipSpellRouting = true,
                DisableMcpTools = true,
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        ReasoningContentSegment projected = Assert.Single(result.Value.Reasoning);
        Assert.Equal("provider-default summary", projected.Text);
        Assert.Equal(ReasoningOutputMode.Summary, projected.Output);
    }

    [Fact]
    public async Task Reasoning_StreamingModelWithoutStreamingSupport_SuppressesReasoningFrames()
    {
        ReasoningCapabilities capabilities = new()
        {
            SupportsSummary = true,
            SupportsStreaming = false,
            AllowsClientOutput = true,
        };
        ArcanumSettings settings = DefaultSettings() with
        {
            Providers =
            [
                DefaultProvider() with
                {
                    Models = [new ModelEntry(ModelName, Reasoning: capabilities)],
                },
            ],
        };
        ScriptingChatClient chat = new();
        chat.EnqueueStreamUpdates(new ChatResponseUpdate(
            ChatRole.Assistant,
            [
                new TextReasoningContent("must not stream"),
                new TextContent("answer"),
            ]));
        WizardIntelligenceProvider wizard = CreateWizard(chat, settings);

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with
            {
                Prompt = "reason",
                SkipSpellRouting = true,
                DisableMcpTools = true,
                Reasoning = new ReasoningRequestOptions(Output: ReasoningOutputMode.Summary),
            });

        Assert.DoesNotContain(events, static evt => evt.Type == IntelligenceEventType.Reasoning);
        Assert.Equal(
            "answer",
            string.Concat(
                events
                    .Where(static evt => evt.Type == IntelligenceEventType.Token)
                    .Select(static evt => evt.Data)));
        Assert.Contains(events, static evt => evt.Type == IntelligenceEventType.Result);
    }

    [Fact]
    public async Task Reasoning_BufferedProviderIgnoringDisabledOutput_DoesNotContaminateAnswerOrGrimoire()
    {
        TextReasoningContent reasoning = new("provider reasoning must stay separate")
        {
            ProtectedData = "opaque-provider-state",
        };
        ScriptingChatClient chat = new();
        chat.EnqueueResponse(new ChatResponse(new MeAiChatMessage(
            ChatRole.Assistant,
            [
                reasoning,
                new TextContent("answer only"),
            ])));
        FakeGrimoireRepository grimoire = new();
        WizardIntelligenceProvider wizard = CreateWizard(chat, grimoire: grimoire);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "reason",
                SkipSpellRouting = true,
                DisableMcpTools = true,
                Reasoning = new ReasoningRequestOptions(Output: ReasoningOutputMode.None),
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("answer only", result.Value.Text);
        Assert.Equal(0, result.Value.Usage?.TotalTokens);
        Assert.Equal("answer only", grimoire.LastFinalizedContent);
        Assert.Empty(result.Value.Reasoning);
        Assert.DoesNotContain("provider reasoning", result.Value.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("provider reasoning", grimoire.LastFinalizedContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reasoning_StreamingInterleaving_DoesNotContaminateTokensOrGrimoire()
    {
        ScriptingChatClient chat = new();
        chat.EnqueueStreamUpdates(
            new ChatResponseUpdate(
                ChatRole.Assistant,
                [
                    new TextReasoningContent("think one"),
                    new TextContent("answer "),
                ]),
            new ChatResponseUpdate(
                ChatRole.Assistant,
                [
                    new TextContent("only"),
                    new TextReasoningContent("think two"),
                ]));
        FakeGrimoireRepository grimoire = new();
        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            SettingsWithReasoning(),
            grimoire: grimoire);

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with
            {
                Prompt = "reason",
                SkipSpellRouting = true,
                DisableMcpTools = true,
                Reasoning = new ReasoningRequestOptions(Output: ReasoningOutputMode.Summary),
            });

        string tokenText = string.Concat(
            events
                .Where(static evt => evt.Type == IntelligenceEventType.Token)
                .Select(static evt => evt.Data));
        Assert.Equal("answer only", tokenText);
        Assert.Equal("answer only", grimoire.LastFinalizedContent);
        Assert.DoesNotContain("think", tokenText, StringComparison.Ordinal);
        Assert.DoesNotContain("think", grimoire.LastFinalizedContent, StringComparison.Ordinal);

        IntelligenceEvent[] projected = events
            .Where(static evt => evt.Type is IntelligenceEventType.Reasoning or IntelligenceEventType.Token)
            .ToArray();
        Assert.Collection(
            projected,
            evt =>
            {
                Assert.Equal(IntelligenceEventType.Reasoning, evt.Type);
                Assert.Equal(
                    new ReasoningContentSegment("think one", ReasoningOutputMode.Summary),
                    evt.Reasoning);
                Assert.Null(evt.Data);
            },
            evt =>
            {
                Assert.Equal(IntelligenceEventType.Token, evt.Type);
                Assert.Equal("answer ", evt.Data);
            },
            evt =>
            {
                Assert.Equal(IntelligenceEventType.Token, evt.Type);
                Assert.Equal("only", evt.Data);
            },
            evt =>
            {
                Assert.Equal(IntelligenceEventType.Reasoning, evt.Type);
                Assert.Equal(
                    new ReasoningContentSegment("think two", ReasoningOutputMode.Summary),
                    evt.Reasoning);
                Assert.Null(evt.Data);
            });
        Assert.Contains(events, static evt => evt.Type == IntelligenceEventType.Result);
    }

    [Fact]
    public async Task Reasoning_GuardrailBufferedStreaming_BlocksUnsafeReasoningBeforeVisibility()
    {
        ScriptingChatClient chat = new();
        chat.EnqueueStreamUpdates(new ChatResponseUpdate(
            ChatRole.Assistant,
            [
                new TextReasoningContent("contains bad-word"),
                new TextContent("safe answer"),
            ]));
        ArcanumSettings settings = SettingsWithReasoning() with
        {
            Guardrails = new GuardrailsSettings
            {
                Enabled = true,
                DetectPii = false,
                BlockToxicity = true,
                ToxicityBlocklist = ["bad-word"],
                StreamingMode = GuardrailsStreamingMode.Buffered,
            },
        };
        FakeGrimoireRepository grimoire = new();
        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            settings,
            grimoire: grimoire,
            guardrailsPipeline: CreateGuardrailsPipeline(settings));

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with
            {
                Prompt = "reason safely",
                SkipSpellRouting = true,
                DisableMcpTools = true,
                Reasoning = new ReasoningRequestOptions(Output: ReasoningOutputMode.Summary),
            });

        Assert.Contains(
            events,
            static evt => evt.Type == IntelligenceEventType.Error
                && evt.Data == ErrorCodes.Guardrails.Blocked);
        Assert.DoesNotContain(
            events,
            static evt => evt.Type is IntelligenceEventType.Reasoning or IntelligenceEventType.Token);
        Assert.DoesNotContain(events, static evt => evt.Type == IntelligenceEventType.Result);
        Assert.NotEqual("safe answer", grimoire.LastFinalizedContent);
    }

    [Fact]
    public async Task Reasoning_StrictStructuredStreaming_ReleasesMixedFramesAfterValidation()
    {
        ScriptingChatClient chat = new();
        chat.EnqueueStreamUpdates(
            new ChatResponseUpdate(
                ChatRole.Assistant,
                [new TextReasoningContent("validated summary")]),
            new ChatResponseUpdate(
                ChatRole.Assistant,
                [new TextContent("""{"name":"answer"}""")]));
        ArcanumSettings settings = SettingsWithReasoning() with
        {
            StructuredOutput = new StructuredOutputSettings
            {
                Enabled = true,
                StrictMode = true,
                MaxValidationRetries = 0,
            },
        };
        JsonElement schema = JsonSerializer.Deserialize<JsonElement>("""
            {
              "type": "object",
              "properties": { "name": { "type": "string" } },
              "required": ["name"],
              "additionalProperties": false
            }
            """);
        WizardIntelligenceProvider wizard = CreateWizard(chat, settings);

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with
            {
                Prompt = "return structured output",
                ResponseFormat = "json_schema",
                ResponseFormatJsonSchema = schema,
                SkipSpellRouting = true,
                DisableMcpTools = true,
                Reasoning = new ReasoningRequestOptions(Output: ReasoningOutputMode.Summary),
            });

        IntelligenceEvent[] outputFrames = events
            .Where(static evt => evt.Type is IntelligenceEventType.Reasoning or IntelligenceEventType.Token)
            .ToArray();
        Assert.Collection(
            outputFrames,
            frame =>
            {
                Assert.Equal(IntelligenceEventType.Reasoning, frame.Type);
                Assert.Equal("validated summary", frame.Reasoning?.Text);
            },
            frame =>
            {
                Assert.Equal(IntelligenceEventType.Token, frame.Type);
                Assert.Equal("""{"name":"answer"}""", frame.Data);
            });
        Assert.Contains(events, static evt => evt.Type == IntelligenceEventType.Result);
        Assert.DoesNotContain(events, static evt => evt.Type == IntelligenceEventType.Error);
    }

    [Fact]
    public async Task Reasoning_StrictStructuredStreaming_CoalescesAdjacentBufferedOutputRuns()
    {
        ScriptingChatClient chat = new();
        chat.EnqueueStreamUpdates(
            new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("reason-1")]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("+reason-2")]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("{\"name\":\"")]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("answer\"")]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("reason-3")]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("+reason-4")]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("}")]));
        ArcanumSettings settings = SettingsWithReasoning() with
        {
            StructuredOutput = new StructuredOutputSettings
            {
                Enabled = true,
                StrictMode = true,
                MaxValidationRetries = 0,
            },
        };
        JsonElement schema = JsonSerializer.Deserialize<JsonElement>("""
            {
              "type": "object",
              "properties": { "name": { "type": "string" } },
              "required": ["name"],
              "additionalProperties": false
            }
            """);
        WizardIntelligenceProvider wizard = CreateWizard(chat, settings);

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with
            {
                Prompt = "return structured output",
                ResponseFormat = "json_schema",
                ResponseFormatJsonSchema = schema,
                SkipSpellRouting = true,
                DisableMcpTools = true,
                Reasoning = new ReasoningRequestOptions(Output: ReasoningOutputMode.Summary),
            });

        IntelligenceEvent[] outputFrames = events
            .Where(static evt => evt.Type is IntelligenceEventType.Reasoning or IntelligenceEventType.Token)
            .ToArray();
        Assert.Collection(
            outputFrames,
            frame => Assert.Equal("reason-1+reason-2", frame.Reasoning?.Text),
            frame => Assert.Equal("{\"name\":\"answer\"", frame.Data),
            frame => Assert.Equal("reason-3+reason-4", frame.Reasoning?.Text),
            frame => Assert.Equal("}", frame.Data));
        Assert.Contains(events, static evt => evt.Type == IntelligenceEventType.Result);
    }

    [Fact]
    public async Task Reasoning_StrictStructuredStreamingRetry_RebuildsReleaseFromSafeReplacement()
    {
        const string initialReasoning = "stale bad-word reasoning";
        const string initialAnswer = "stale invalid bad-word answer";
        const string replacementReasoning = "safe replacement reasoning";
        const string replacementAnswerStart = "{\"name\":\"";
        const string replacementAnswerEnd = "safe replacement\"}";
        const string replacementAnswer = replacementAnswerStart + replacementAnswerEnd;
        ScriptingChatClient chat = new();
        chat.EnqueueStreamUpdates(new ChatResponseUpdate(
            ChatRole.Assistant,
            [
                new TextReasoningContent(initialReasoning),
                new TextContent(initialAnswer),
            ]));
        chat.EnqueueResponse(new ChatResponse(new MeAiChatMessage(
            ChatRole.Assistant,
            [
                new TextContent(replacementAnswerStart),
                new TextReasoningContent(replacementReasoning),
                new TextContent(replacementAnswerEnd),
            ])));
        ArcanumSettings settings = SettingsWithReasoning() with
        {
            StructuredOutput = new StructuredOutputSettings
            {
                Enabled = true,
                StrictMode = true,
                MaxValidationRetries = 1,
            },
            Guardrails = new GuardrailsSettings
            {
                Enabled = true,
                DetectPii = false,
                BlockToxicity = true,
                ToxicityBlocklist = ["bad-word"],
                StreamingMode = GuardrailsStreamingMode.Buffered,
            },
        };
        JsonElement schema = JsonSerializer.Deserialize<JsonElement>("""
            {
              "type": "object",
              "properties": { "name": { "type": "string" } },
              "required": ["name"],
              "additionalProperties": false
            }
            """);
        FakeGrimoireRepository grimoire = new();
        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            settings,
            grimoire: grimoire,
            guardrailsPipeline: CreateGuardrailsPipeline(settings));

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with
            {
                Prompt = "return structured output",
                ResponseFormat = "json_schema",
                ResponseFormatJsonSchema = schema,
                SkipSpellRouting = true,
                DisableMcpTools = true,
                Reasoning = new ReasoningRequestOptions(Output: ReasoningOutputMode.Summary),
            });

        IntelligenceEvent[] released = events
            .Where(static evt => evt.Type is IntelligenceEventType.Reasoning or IntelligenceEventType.Token)
            .ToArray();
        Assert.Collection(
            released,
            frame => Assert.Equal(replacementAnswerStart, frame.Data),
            frame => Assert.Equal(replacementReasoning, frame.Reasoning?.Text),
            frame => Assert.Equal(replacementAnswerEnd, frame.Data));
        Assert.Contains(events, static evt => evt.Type == IntelligenceEventType.Result);
        Assert.DoesNotContain(events, static evt => evt.Type == IntelligenceEventType.Error);
        Assert.DoesNotContain(
            events,
            frame => frame.Message.Contains(initialReasoning, StringComparison.Ordinal)
                || frame.Data?.Contains(initialAnswer, StringComparison.Ordinal) == true);
        Assert.Equal(replacementAnswer, grimoire.LastFinalizedContent);
    }

    [Fact]
    public async Task Reasoning_StrictStructuredStreamingRetry_SafetyInspectsReplacementInReleaseOrder()
    {
        const string replacementAnswerStart = "{\"name\":\"";
        const string replacementReasoning = "ordered marker";
        const string replacementAnswerEnd = "safe replacement\"}";
        ScriptingChatClient chat = new();
        chat.EnqueueStreamTokens("invalid answer");
        chat.EnqueueResponse(new ChatResponse(new MeAiChatMessage(
            ChatRole.Assistant,
            [
                new TextContent(replacementAnswerStart),
                new TextReasoningContent(replacementReasoning),
                new TextContent(replacementAnswerEnd),
            ])));
        ArcanumSettings settings = SettingsWithReasoning() with
        {
            StructuredOutput = new StructuredOutputSettings
            {
                Enabled = true,
                StrictMode = true,
                MaxValidationRetries = 1,
            },
            Guardrails = new GuardrailsSettings
            {
                Enabled = true,
                DetectPii = false,
                BlockToxicity = false,
                BlockedTopics = ["(?s)name.*ordered marker.*safe replacement"],
                StreamingMode = GuardrailsStreamingMode.Buffered,
            },
        };
        JsonElement schema = JsonSerializer.Deserialize<JsonElement>("""
            {
              "type": "object",
              "properties": { "name": { "type": "string" } },
              "required": ["name"],
              "additionalProperties": false
            }
            """);
        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            settings,
            guardrailsPipeline: CreateGuardrailsPipeline(settings));

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with
            {
                Prompt = "return structured output",
                ResponseFormat = "json_schema",
                ResponseFormatJsonSchema = schema,
                SkipSpellRouting = true,
                DisableMcpTools = true,
                Reasoning = new ReasoningRequestOptions(Output: ReasoningOutputMode.Summary),
            });

        Assert.Contains(
            events,
            static frame => frame.Type == IntelligenceEventType.Error
                && frame.Data == ErrorCodes.Guardrails.Blocked);
        Assert.DoesNotContain(
            events,
            static frame => frame.Type is IntelligenceEventType.Reasoning
                or IntelligenceEventType.Token);
        Assert.DoesNotContain(events, static frame => frame.Type == IntelligenceEventType.Result);
    }

    [Fact]
    public async Task Reasoning_StrictStructuredStreaming_DropsReasoningWhenValidationFails()
    {
        ScriptingChatClient chat = new();
        chat.EnqueueStreamUpdates(new ChatResponseUpdate(
            ChatRole.Assistant,
            [
                new TextReasoningContent("""{"name":"reasoning-is-not-answer"}"""),
                new TextContent("invalid answer"),
            ]));
        ArcanumSettings settings = SettingsWithReasoning() with
        {
            StructuredOutput = new StructuredOutputSettings
            {
                Enabled = true,
                StrictMode = true,
                MaxValidationRetries = 0,
            },
        };
        JsonElement schema = JsonSerializer.Deserialize<JsonElement>("""
            {
              "type": "object",
              "properties": { "name": { "type": "string" } },
              "required": ["name"],
              "additionalProperties": false
            }
            """);
        WizardIntelligenceProvider wizard = CreateWizard(chat, settings);

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with
            {
                Prompt = "return structured output",
                ResponseFormat = "json_schema",
                ResponseFormatJsonSchema = schema,
                SkipSpellRouting = true,
                DisableMcpTools = true,
                Reasoning = new ReasoningRequestOptions(Output: ReasoningOutputMode.Summary),
            });

        Assert.Contains(
            events,
            static evt => evt.Type == IntelligenceEventType.Error
                && evt.Data == ErrorCodes.StructuredOutput.ValidationFailed);
        Assert.DoesNotContain(
            events,
            static evt => evt.Type is IntelligenceEventType.Reasoning or IntelligenceEventType.Token);
        Assert.DoesNotContain(events, static evt => evt.Type == IntelligenceEventType.Result);
    }

    [Fact]
    public async Task Reasoning_StrictStructuredRetry_ExposesOnlyReplacementReasoningAndAnswer()
    {
        ScriptingChatClient chat = new();
        chat.EnqueueResponse(new ChatResponse(new MeAiChatMessage(
            ChatRole.Assistant,
            [
                new TextReasoningContent("discarded initial reasoning"),
                new TextContent("invalid answer"),
            ])));
        chat.EnqueueResponse(new ChatResponse(new MeAiChatMessage(
            ChatRole.Assistant,
            [
                new TextReasoningContent("replacement reasoning"),
                new TextContent("""{"name":"fixed"}"""),
            ])));
        ArcanumSettings settings = SettingsWithReasoning() with
        {
            StructuredOutput = new StructuredOutputSettings
            {
                Enabled = true,
                StrictMode = true,
                MaxValidationRetries = 1,
            },
        };
        JsonElement schema = JsonSerializer.Deserialize<JsonElement>("""
            {
              "type": "object",
              "properties": { "name": { "type": "string" } },
              "required": ["name"],
              "additionalProperties": false
            }
            """);
        WizardIntelligenceProvider wizard = CreateWizard(chat, settings);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "return structured output",
                ResponseFormat = "json_schema",
                ResponseFormatJsonSchema = schema,
                SkipSpellRouting = true,
                DisableMcpTools = true,
                Reasoning = new ReasoningRequestOptions(Output: ReasoningOutputMode.Summary),
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("""{"name":"fixed"}""", result.Value.Text);
        ReasoningContentSegment reasoning = Assert.Single(result.Value.Reasoning);
        Assert.Equal("replacement reasoning", reasoning.Text);
        Assert.DoesNotContain("discarded initial", reasoning.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reasoning_StrictStructuredRetry_GuardrailsInspectReplacementReasoningBeforeVisibility()
    {
        ScriptingChatClient chat = new();
        chat.EnqueueResponse(new ChatResponse(new MeAiChatMessage(
            ChatRole.Assistant,
            [new TextContent("invalid answer")])));
        chat.EnqueueResponse(new ChatResponse(new MeAiChatMessage(
            ChatRole.Assistant,
            [
                new TextReasoningContent("replacement contains bad-word"),
                new TextContent("""{"name":"fixed"}"""),
            ])));
        ArcanumSettings settings = SettingsWithReasoning() with
        {
            StructuredOutput = new StructuredOutputSettings
            {
                Enabled = true,
                StrictMode = true,
                MaxValidationRetries = 1,
            },
            Guardrails = new GuardrailsSettings
            {
                Enabled = true,
                DetectPii = false,
                BlockToxicity = true,
                ToxicityBlocklist = ["bad-word"],
                StreamingMode = GuardrailsStreamingMode.Buffered,
            },
        };
        JsonElement schema = JsonSerializer.Deserialize<JsonElement>("""
            {
              "type": "object",
              "properties": { "name": { "type": "string" } },
              "required": ["name"],
              "additionalProperties": false
            }
            """);
        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            settings,
            guardrailsPipeline: CreateGuardrailsPipeline(settings));

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "return structured output",
                ResponseFormat = "json_schema",
                ResponseFormatJsonSchema = schema,
                SkipSpellRouting = true,
                DisableMcpTools = true,
                Reasoning = new ReasoningRequestOptions(Output: ReasoningOutputMode.Summary),
            },
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Guardrails.Blocked, result.Error.Code);
        Assert.DoesNotContain(
            "replacement contains bad-word",
            result.Error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reasoning_ProtectedContent_PersistsOnlyForSameProviderToolContinuation()
    {
        TextReasoningContent protectedReasoning = new(string.Empty)
        {
            ProtectedData = "opaque-provider-state",
        };
        ScriptingChatClient chat = new();
        chat.EnqueueResponse(new ChatResponse(new MeAiChatMessage(
            ChatRole.Assistant,
            [
                protectedReasoning,
                new FunctionCallContent(
                    ArcanumLocalTimeTool.ToolName,
                    ArcanumLocalTimeTool.ToolName,
                    new Dictionary<string, object?>()),
            ])));
        chat.EnqueueText("final answer");
        WizardIntelligenceProvider wizard = CreateWizard(chat);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "what time is it?",
                SkipSpellRouting = true,
                DisableMcpTools = true,
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("final answer", result.Value.Text);
        Assert.Equal(2, chat.AllBufferedCalls.Count);
        TextReasoningContent continued = Assert.Single(
            chat.AllBufferedCalls[1]
                .SelectMany(static message => message.Contents)
                .OfType<TextReasoningContent>());
        Assert.Same(protectedReasoning, continued);
        Assert.Equal("opaque-provider-state", continued.ProtectedData);
    }

    [Fact]
    public async Task Reasoning_StreamingProtectedContent_PersistsForSameProviderToolContinuation()
    {
        TextReasoningContent protectedReasoning = new(string.Empty)
        {
            ProtectedData = "opaque-stream-provider-state",
        };
        ScriptingChatClient chat = new();
        chat.EnqueueStreamUpdates(new ChatResponseUpdate(
            ChatRole.Assistant,
            [
                protectedReasoning,
                new FunctionCallContent(
                    ArcanumLocalTimeTool.ToolName,
                    ArcanumLocalTimeTool.ToolName,
                    new Dictionary<string, object?>()),
            ]));
        chat.EnqueueStreamTokens("final answer");
        WizardIntelligenceProvider wizard = CreateWizard(chat);

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with
            {
                Prompt = "what time is it?",
                SkipSpellRouting = true,
                DisableMcpTools = true,
            });

        Assert.Contains(events, static evt => evt.Type == IntelligenceEventType.Result);
        Assert.Equal(2, chat.AllStreamingCalls.Count);
        TextReasoningContent continued = Assert.Single(
            chat.AllStreamingCalls[1]
                .SelectMany(static message => message.Contents)
                .OfType<TextReasoningContent>());
        Assert.Same(protectedReasoning, continued);
        Assert.Equal("opaque-stream-provider-state", continued.ProtectedData);
    }

    [Fact]
    public async Task Reasoning_ProtectedContent_PersistsAcrossMultipleToolContinuationRounds()
    {
        TextReasoningContent firstReasoning = new(string.Empty)
        {
            ProtectedData = "opaque-round-one",
        };
        TextReasoningContent secondReasoning = new(string.Empty)
        {
            ProtectedData = "opaque-round-two",
        };
        ScriptingChatClient chat = new();
        chat.EnqueueResponse(new ChatResponse(new MeAiChatMessage(
            ChatRole.Assistant,
            [
                firstReasoning,
                new FunctionCallContent(
                    "call-one",
                    ArcanumLocalTimeTool.ToolName,
                    new Dictionary<string, object?>()),
            ])));
        chat.EnqueueResponse(new ChatResponse(new MeAiChatMessage(
            ChatRole.Assistant,
            [
                secondReasoning,
                new FunctionCallContent(
                    "call-two",
                    ArcanumLocalTimeTool.ToolName,
                    new Dictionary<string, object?>()),
            ])));
        chat.EnqueueText("final answer");
        WizardIntelligenceProvider wizard = CreateWizard(chat);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "check the time twice",
                SkipSpellRouting = true,
                DisableMcpTools = true,
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("final answer", result.Value.Text);
        Assert.Equal(3, chat.AllBufferedCalls.Count);
        Assert.Same(
            firstReasoning,
            Assert.Single(
                chat.AllBufferedCalls[1]
                    .SelectMany(static message => message.Contents)
                    .OfType<TextReasoningContent>()));
        TextReasoningContent[] finalContinuationReasoning = chat.AllBufferedCalls[2]
            .SelectMany(static message => message.Contents)
            .OfType<TextReasoningContent>()
            .ToArray();
        Assert.Equal([firstReasoning, secondReasoning], finalContinuationReasoning);
    }

    [Fact]
    public async Task Reasoning_GuardrailBufferedStreaming_StillCommitsBeforeFailure()
    {
        TextReasoningContent protectedReasoning = new(string.Empty)
        {
            ProtectedData = "opaque-buffered-provider-state",
        };
        ScriptingChatClient chat = new();
        chat.EnqueueUpdatesThenStreamFailure(
            [
                new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [protectedReasoning]),
                new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [new TextContent("withheld answer")]),
            ],
            new InvalidOperationException("model does not support tools"));
        chat.EnqueueStreamTokens("must not restart");
        ArcanumSettings settings = SettingsWithReasoning() with
        {
            Guardrails = new GuardrailsSettings
            {
                Enabled = true,
                DetectPii = false,
                BlockToxicity = true,
                ToxicityBlocklist = ["bad-word"],
                StreamingMode = GuardrailsStreamingMode.Buffered,
            },
        };
        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            settings,
            guardrailsPipeline: CreateGuardrailsPipeline(settings));

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with
            {
                Prompt = "reason before failing",
                SkipSpellRouting = true,
                DisableMcpTools = true,
                Reasoning = new ReasoningRequestOptions(Output: ReasoningOutputMode.None),
            });

        Assert.Equal(1, chat.StreamingCallCount);
        Assert.DoesNotContain(events, static evt => evt.Type == IntelligenceEventType.Token);
        Assert.DoesNotContain(
            events,
            static evt => string.Equals(evt.Data, "withheld answer", StringComparison.Ordinal));
        Assert.Contains(events, static evt => evt.Type == IntelligenceEventType.Error);
        Assert.DoesNotContain(events, static evt => evt.Type == IntelligenceEventType.Result);
        Assert.DoesNotContain(
            events,
            static evt => evt.Type == IntelligenceEventType.Status
                && evt.Message.Contains("continuing without local tools", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Reasoning_StrictStructuredOutputValidation_NeverConsumesReasoningAsAnswer()
    {
        ScriptingChatClient chat = new();
        chat.EnqueueResponse(new ChatResponse(new MeAiChatMessage(
            ChatRole.Assistant,
            [
                new TextReasoningContent("""{"name":"reasoning-only"}"""),
                new TextContent("not json"),
            ])));
        ArcanumSettings settings = SettingsWithReasoning() with
        {
            StructuredOutput = new StructuredOutputSettings
            {
                Enabled = true,
                StrictMode = true,
                MaxValidationRetries = 0,
            },
        };
        JsonElement schema = JsonSerializer.Deserialize<JsonElement>("""
            {
              "type": "object",
              "properties": { "name": { "type": "string" } },
              "required": ["name"],
              "additionalProperties": false
            }
            """);
        WizardIntelligenceProvider wizard = CreateWizard(chat, settings);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "return structured output",
                ResponseFormat = "json_schema",
                ResponseFormatJsonSchema = schema,
                SkipSpellRouting = true,
                DisableMcpTools = true,
                Reasoning = new ReasoningRequestOptions(Output: ReasoningOutputMode.Summary),
            },
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.StructuredOutput.ValidationFailed, result.Error.Code);
        Assert.DoesNotContain("reasoning-only", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reasoning_CommitProhibitsNoToolsCompatibilityRestart()
    {
        TextReasoningContent protectedReasoning = new(string.Empty)
        {
            ProtectedData = "opaque-provider-state",
        };
        ScriptingChatClient chat = new();
        chat.EnqueueReasoningThenStreamFailure(
            protectedReasoning,
            new InvalidOperationException("model does not support tools"));
        chat.EnqueueStreamTokens("must not restart");
        WizardIntelligenceProvider wizard = CreateWizard(chat);

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with
            {
                Prompt = "reason before failing",
                SkipSpellRouting = true,
                DisableMcpTools = true,
            });

        Assert.Equal(1, chat.StreamingCallCount);
        Assert.Contains(events, static evt => evt.Type == IntelligenceEventType.Error);
        Assert.DoesNotContain(events, static evt => evt.Type == IntelligenceEventType.Result);
        Assert.DoesNotContain(
            events,
            static evt => evt.Type == IntelligenceEventType.Status
                && evt.Message.Contains("continuing without local tools", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResolveCallId_EmptyCallId_GeneratesStableFallbackId()
    {

        ArcanumSettings settings = DefaultSettings();

        ToolExecutionPipeline pipeline = new(
            new TestOptionsSnapshot<ArcanumSettings>(settings),
            new FakeWard(),
            new ConfigurableSanctumGuard(),
            new NoOpSessionAttachmentStore(),
            NullLogger<ToolExecutionPipeline>.Instance);

        FunctionCallContent fcc = new(callId: string.Empty, name: "test_tool", arguments: null);

        string id1 = pipeline.ResolveCallId(fcc);

        Assert.False(string.IsNullOrEmpty(id1));

        Assert.NotEqual("test_tool", id1);

        string id2 = pipeline.ResolveCallId(fcc);

        Assert.Equal(id1, id2);

    }

    [Fact]
    public void ResolveCallId_NonEmptyCallId_ReturnsOriginal()
    {

        ArcanumSettings settings = DefaultSettings();

        ToolExecutionPipeline pipeline = new(
            new TestOptionsSnapshot<ArcanumSettings>(settings),
            new FakeWard(),
            new ConfigurableSanctumGuard(),
            new NoOpSessionAttachmentStore(),
            NullLogger<ToolExecutionPipeline>.Instance);

        FunctionCallContent fcc = new(callId: "call_abc123", name: "test_tool", arguments: null);

        string id = pipeline.ResolveCallId(fcc);

        Assert.Equal("call_abc123", id);

    }

    private static int CountContextTokens(
        ArcanumSettings settings,
        IReadOnlyList<MeAiChatMessage> messages)
    {
        TestOptionsMonitor<ArcanumSettings> options = new(settings);
        InferenceTokenizerResolver resolver =
            new(NullLogger<InferenceTokenizerResolver>.Instance);

        return new ManaPreflight(options).CountTokens(
            messages,
            resolver.ResolveTokenizer(settings.Intelligence.TokenizerEncoding),
            ArcanumSettingClamps.PerMessageTemplateOverheadTokens(
                settings.Intelligence.PerMessageTemplateOverheadTokens),
            settings.Intelligence.TokenizerEncoding);
    }

    private static Result InvokeEnsureContextBudget(
        WizardIntelligenceProvider wizard,
        List<MeAiChatMessage> messages,
        ChatClientLease lease,
        PingRequest request)
    {
        System.Reflection.MethodInfo method = typeof(WizardIntelligenceProvider).GetMethod(
            "EnsureContextBudget",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        return Assert.IsType<Result>(
            method.Invoke(wizard, [messages, new ChatOptions(), lease, request]));
    }

    private WizardIntelligenceProvider CreateWizard(
        ScriptingChatClient chatClient,
        ArcanumSettings? settings = null,
        FakeGrimoireRepository? grimoire = null,
        FakeWard? ward = null,
        FakeMcpConnectionManager? mcp = null,
        IChatClientFactory? factory = null,
        ICampaignRepository? campaignRepository = null,
        ISanctumGuard? sanctumGuard = null,
        IWeaveService? weaveService = null,
        IDivinationService? divinationService = null,
        IWorkspaceIndexingService? workspaceIndexingService = null,
        ISagaMemoryStore? sagaMemoryStore = null,
        SagaExtractionService? sagaExtractionService = null,
        SemanticSpellRouter? semanticSpellRouter = null,
        ILexiconService? lexiconService = null,
        ArcanumDbContext? db = null,
        IInferenceAuditLogger? auditLogger = null,
        GuardrailsPipeline? guardrailsPipeline = null,
        BudgetMonitor? budgetMonitor = null,
        ITurnRunWriter? turnRunWriter = null,
        IBudgetReservationService? budgetReservationService = null)
    {
        settings ??= DefaultSettings();

        auditLogger ??= new FakeInferenceAuditLogger();

        grimoire ??= new FakeGrimoireRepository();

        ward ??= new FakeWard();

        mcp ??= new FakeMcpConnectionManager();

        factory ??= new FakeChatClientFactory(
            chatClient,
            settings.Providers is { Length: > 0 } ? settings.Providers[0] : DefaultProvider());

        campaignRepository ??= new FakeCampaignRepository();

        sanctumGuard ??= new ConfigurableSanctumGuard();

        // RAG Phase 3 — most scenarios keep Embeddings.Enabled/CodebaseRetrievalEnabled false (the
        // DefaultSettings() default), so RetrieveSemanticContextAsync short-circuits before ever
        // touching weaveService/divinationService/workspaceIndexingService/db; these fakes exist only
        // so the constructor can be satisfied, and RAG-specific scenarios supply their own.
        weaveService ??= new FakeRagWeaveService { Available = false };

        divinationService ??= new FakeRagDivinationService();

        workspaceIndexingService ??= new FakeRagWorkspaceIndexingService();

        sagaMemoryStore ??= new FakeSagaMemoryStore();

        // RAG Phase 4 — SagaExtractionService and SemanticSpellRouter are concrete classes (not
        // interfaces); the hub only ever calls EnqueueExtraction/ResolveAsync on them, so a real
        // instance backed by an empty scope factory and the same settings is sufficient here.
        sagaExtractionService ??= new SagaExtractionService(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new TestOptionsMonitor<ArcanumSettings>(settings),
            NullLogger<SagaExtractionService>.Instance);

        semanticSpellRouter ??= new SemanticSpellRouter(
            new SpellWeaveCache(weaveService, new TestOptionsMonitor<ArcanumSettings>(settings), NullLogger<SpellWeaveCache>.Instance),
            weaveService,
            new TestOptionsSnapshot<ArcanumSettings>(settings),
            NullLogger<SemanticSpellRouter>.Instance);

        lexiconService ??= new FakeLexiconService();

        db ??= CreateUnusedDbContext();

        budgetMonitor ??= CreateBudgetMonitor(settings);

        return new WizardIntelligenceProvider(
            factory,
            new TestOptionsSnapshot<ArcanumSettings>(settings),
            NullLogger<WizardIntelligenceProvider>.Instance,
            new FakeHttpClientFactory(),
            grimoire,
            mcp,
            campaignRepository,
            new ToolExecutionPipeline(
                new TestOptionsSnapshot<ArcanumSettings>(settings),
                ward,
                sanctumGuard,
                new NoOpSessionAttachmentStore(),
                NullLogger<ToolExecutionPipeline>.Instance),
            new GrimoireTurnWriter(
                grimoire,
                new SessionEventHub(new TestOptionsMonitor<ArcanumSettings>(settings), NullLogger<SessionEventHub>.Instance),
                NullLogger<GrimoireTurnWriter>.Instance),
            CreateInferenceContextBuilder(grimoire, settings),
            sanctumGuard,
            new ProcessResourceLimiter(),
            weaveService,
            divinationService,
            workspaceIndexingService,
            sagaMemoryStore,
            sagaExtractionService,
            semanticSpellRouter,
            lexiconService,
            db,
            auditLogger,
            new StructuredOutputValidator(),
            new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance),
            budgetMonitor,
            new NoOpSessionAttachmentStore(),
            new HumanPromptRegistry(),
            new ManaPreflight(new TestOptionsMonitor<ArcanumSettings>(settings)),
            healthTracker: null,
            guardrailsPipeline: guardrailsPipeline,
            turnRunWriter: turnRunWriter,
            budgetReservationService: budgetReservationService);
    }

    private static GuardrailsPipeline CreateGuardrailsPipeline(ArcanumSettings settings, FakeGuardrailAuditLogger? audit = null) =>
        new(
            new TestOptionsMonitor<ArcanumSettings>(settings),
            audit ?? new FakeGuardrailAuditLogger(),
            NullLogger<GuardrailsPipeline>.Instance);

    /// <summary>
    /// A syntactically-valid but never-opened <see cref="ArcanumDbContext"/> for scenarios where RAG
    /// is disabled (the default) and the db dependency is never touched. Pre-configuring
    /// <c>DbContextOptions</c> means <c>OnConfiguring</c> returns immediately without needing a real
    /// secret store or passphrase source (see <c>ArcanumDbContext.OnConfiguring</c>).
    /// </summary>
    private static ArcanumDbContext CreateUnusedDbContext()
    {
        DbContextOptions<ArcanumDbContext> options = new DbContextOptionsBuilder<ArcanumDbContext>()
            .UseSqlite("Data Source=:memory:")
            .UseModel(ArcanumDbContextModel.Instance)
            .Options;

        return new ArcanumDbContext(options, new UnusedSecretStore(), new UnusedPassphraseSource());
    }

    private static InferenceContextBuilder CreateInferenceContextBuilder(
        IGrimoireRepository grimoire,
        ArcanumSettings settings)
    {

        ManaPreflight manaPreflight = new(new TestOptionsMonitor<ArcanumSettings>(settings));

        InferenceTokenizerResolver tokenizerResolver = new(NullLogger<InferenceTokenizerResolver>.Instance);

        IContextCompressionService compression = new ContextCompressionService(
            grimoire,
            new TestOptionsSnapshot<ArcanumSettings>(settings),
            manaPreflight,
            tokenizerResolver,
            NullLogger<ContextCompressionService>.Instance);

        return new InferenceContextBuilder(
            grimoire,
            new TestOptionsSnapshot<ArcanumSettings>(settings),
            NullLogger<InferenceContextBuilder>.Instance,
            manaPreflight,
            compression);

    }

    private static BudgetMonitor CreateBudgetMonitor(ArcanumSettings? settings = null)
    {

        ArcanumSettings effective = settings ?? new ArcanumSettings();

        IGrimoireRepository grimoire = new FakeGrimoireRepository();

        IBudgetAlertRepository budgetAlerts = new FakeBudgetAlertRepository();

        ICommLinkDispatcher commLink = new FakeCommLinkDispatcher();

        IServiceScopeFactory scopeFactory = CreateBudgetMonitorScopeFactory(grimoire, budgetAlerts);

        return new BudgetMonitor(
            scopeFactory,
            commLink,
            new TestOptionsMonitor<ArcanumSettings>(effective),
            NullLogger<BudgetMonitor>.Instance);

    }

    private static IServiceScopeFactory CreateBudgetMonitorScopeFactory(
        IGrimoireRepository grimoire,
        IBudgetAlertRepository budgetAlerts)
    {

        ServiceCollection services = new();

        services.AddScoped(_ => grimoire);

        services.AddScoped(_ => budgetAlerts);

        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    }

    private static PingRequest BaseRequest() =>
        new(Prompt: string.Empty, Model: ModelName, WorkingDirectory: string.Empty);

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }

        return condition();
    }

    private static ArcanumSettings DefaultSettings() =>
        new()
        {
            DefaultModel = ModelName,
            Providers = [DefaultProvider()],
            Ward = new WardSettings
            {
                Enabled = true,
                ForbiddenArts = ["execute_command"],
                AutoDenyInUnattendedMode = true,
            },
            Intelligence = new IntelligenceSettings
            {
                // Lexicon retrieval is off by default in hub scenario tests so the fallback
                // LexiconEntityExtractor does not fire an extra LLM call against the scripted
                // ScriptingChatClient. Production defaults EnableLexiconSystem to true (Option A);
                // Lexicon-specific scenarios enable it explicitly.
                EnableLexiconSystem = false,
            },
        };

    private static ArcanumSettings SettingsWithReasoning()
    {
        ReasoningCapabilities capabilities = new()
        {
            ControlSupport = ReasoningControlSupport.EffortAndBudget,
            SupportsSummary = true,
            SupportsFull = true,
            SupportsStreaming = true,
            AllowsClientOutput = true,
            WireDialect = ReasoningWireDialect.Standard,
        };

        return DefaultSettings() with
        {
            Providers =
            [
                DefaultProvider() with
                {
                    Models = [new ModelEntry(ModelName, Reasoning: capabilities)],
                },
            ],
        };
    }

    private static ProviderSettings DefaultProvider() =>
        new()
        {
            Name = "test",
            Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://example.test/v1",
            Models = [ModelName],
            ContextWindowLimit = 8192,
        };

    private static Campaign BuildSanctumCampaign(string workspaceRoot, bool enabled, SanctumMode mode)
    {

        SanctumConfig sanctum = new()
        {
            Enabled = enabled,
            Mode = mode,
        };

        return new Campaign
        {
            Id = Guid.NewGuid(),
            Name = "sanctum-campaign",
            NameLower = "sanctum-campaign",
            Path = workspaceRoot,
            Type = WorkspaceType.Custom,
            Settings = CampaignRepository.SerializeSettings(new CampaignSettings(
                DefaultModel: null,
                ModelMap: null,
                McpServerProfiles: null,
                SpellRoots: null,
                LoreNamespace: null,
                AllowedTools: null,
                RequireWardForForbiddenArts: true)),
            SanctumConfigJson = CampaignRepository.SerializeSanctumConfig(sanctum),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    }

    private static Session BuildHeavySession(Guid sessionId)
    {
        DateTime watermark = DateTime.UtcNow.AddHours(-2);

        List<Entry> entries = [];

        for (int i = 0; i < 40; i++)
        {
            entries.Add(new Entry
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                Role = i % 2 == 0 ? MessageRole.User : MessageRole.Assistant,
                Content = new string('x', 2000) + i,
                CreatedAt = watermark.AddMinutes(i),
            });
        }

        return new Session
        {
            Id = sessionId,
            Summary = "rolled-up campaign memory",
            // Watermark after most entries so compression can drop the heavy prefix and still
            // leave a few recent turns — required once EnsureContextBudget runs post-compression.
            LastSummarizedMessageAt = watermark.AddMinutes(35),
            Entries = entries,
        };
    }

    private static Session BuildHeavySessionWithoutSummary(Guid sessionId)
    {

        DateTime watermark = DateTime.UtcNow.AddHours(-2);

        List<Entry> entries = [];

        for (int i = 0; i < 40; i++)
        {
            entries.Add(new Entry
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                Role = i % 2 == 0 ? MessageRole.User : MessageRole.Assistant,
                Content = new string('x', 2000) + i,
                CreatedAt = watermark.AddMinutes(i),
            });
        }

        return new Session
        {
            Id = sessionId,
            Summary = null,
            LastSummarizedMessageAt = null,
            Entries = entries,
        };

    }

    private async Task CreateSpellAsync(
        string folderName,
        string spellName,
        string[]? dependencies,
        string? body = null)
    {
        string dir = Path.Combine(_workspace.Root, folderName);

        Directory.CreateDirectory(dir);

        string spellBody = body ?? $"{spellName} body";

        string spellMd = $"---\nname: {spellName}\ndescription: test\n---\n{spellBody}\n";

        await File.WriteAllTextAsync(Path.Combine(dir, "SPELL.md"), spellMd);

        string dependenciesJson = JsonSerializer.Serialize(dependencies ?? Array.Empty<string>());

        string skillJson = $$"""

            {
              "name": "{{spellName}}",
              "version": "1.0.0",
              "description": "test",
              "tags": [],
              "declaredTools": [],
              "dependencies": {{dependenciesJson}}
            }

            """.Trim();

        await File.WriteAllTextAsync(Path.Combine(dir, "SKILL.json"), skillJson);
    }

    /// <summary>RAG Phase 5 — like <see cref="CreateSpellAsync"/> but with a caller-supplied description, needed for embedding-similarity-based spell routing scenarios (the shared helper hardcodes description "test" for every spell).</summary>
    private async Task CreateSpellWithDescriptionAsync(
        string folderName,
        string spellName,
        string description,
        string body)
    {
        string dir = Path.Combine(_workspace.Root, folderName);

        Directory.CreateDirectory(dir);

        string spellMd = $"---\nname: {spellName}\ndescription: {description}\n---\n{body}\n";

        await File.WriteAllTextAsync(Path.Combine(dir, "SPELL.md"), spellMd);

        string skillJson = $$"""

            {
              "name": "{{spellName}}",
              "version": "1.0.0",
              "description": "{{description}}",
              "tags": [],
              "declaredTools": [],
              "dependencies": []
            }

            """.Trim();

        await File.WriteAllTextAsync(Path.Combine(dir, "SKILL.json"), skillJson);
    }

    private async Task CreateSpellWithDeclaredToolsAsync(string folderName, string[] declaredTools)
    {
        string dir = Path.Combine(_workspace.Root, folderName);

        Directory.CreateDirectory(dir);

        string spellMd = $"---\nname: {folderName}\ndescription: test\n---\nbody\n";

        await File.WriteAllTextAsync(Path.Combine(dir, "SPELL.md"), spellMd);

        string toolsJson = JsonSerializer.Serialize(declaredTools);

        string skillJson = $$"""

            {
              "name": "{{folderName}}",
              "version": "1.0.0",
              "description": "test",
              "tags": [],
              "declaredTools": {{toolsJson}},
              "dependencies": []
            }

            """.Trim();

        await File.WriteAllTextAsync(Path.Combine(dir, "SKILL.json"), skillJson);
    }

    private static AIFunction CreateMcpTool(string name) =>
        AIFunctionFactory.Create(() => "ok", name, "mcp tool");

    private static AIFunction CreateThrowingMcpTool(string name) =>
        AIFunctionFactory.Create(ThrowingToolDelegate, name, "throws");

    private static string ThrowingToolDelegate() =>
        throw new InvalidOperationException("tool boom");

    private static HashSet<string> ToolNames(ChatOptions? options) =>
        options?.Tools?
            .OfType<AIFunction>()
            .Select(static t => t.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
        ?? [];

    private static string GetMessageText(MeAiChatMessage message)
    {

        if (!string.IsNullOrEmpty(message.Text))
        {

            return message.Text;

        }

        foreach (AIContent content in message.Contents)
        {

            if (content is FunctionResultContent result)
            {

                return result.Result?.ToString() ?? string.Empty;

            }

        }

        return string.Empty;

    }

    private static async Task<List<IntelligenceEvent>> CollectStreamAsync(
        WizardIntelligenceProvider wizard,
        PingRequest request,
        InferenceAuditContext? auditContext = null)
    {
        List<IntelligenceEvent> events = [];

        await foreach (IntelligenceEvent evt in wizard.StreamPromptAsync(request, CancellationToken.None, auditContext))
        {
            events.Add(evt);
        }

        return events;
    }

    private sealed class ScriptingChatClient : IChatClient
    {

        private readonly Queue<Func<CancellationToken, Task<ChatResponse>>> _buffered = new();

        private readonly Queue<Func<CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>> _streaming = new();

        public List<IReadOnlyList<MeAiChatMessage>> AllBufferedCalls { get; } = [];

        public List<IReadOnlyList<MeAiChatMessage>> AllStreamingCalls { get; } = [];

        public int BufferedCallCount { get; private set; }

        public int StreamingCallCount { get; private set; }

        public IReadOnlyList<MeAiChatMessage> LastBufferedMessages { get; private set; } = [];

        public ChatOptions? LastChatOptions { get; private set; }

        public int? UsageTotalTokens { get; set; }

        public int? UsageCachedInputTokens { get; set; }

        public void EnqueueText(string text) =>
            _buffered.Enqueue(_ => Task.FromResult(ResponseText(text)));

        public void EnqueueResponse(ChatResponse response) =>
            _buffered.Enqueue(_ => Task.FromResult(response));

        public void EnqueueToolCall(string toolName, string? callId = null, Dictionary<string, object?>? arguments = null)
        {
            callId ??= toolName;

            Dictionary<string, object?> args = arguments ?? new Dictionary<string, object?>();

            _buffered.Enqueue(_ => Task.FromResult(ResponseTool(toolName, callId, args)));
        }

        public void EnqueueException(Exception ex) =>
            _buffered.Enqueue(_ => throw ex);

        public void EnqueueStreamTokens(params string[] tokens) =>
            _streaming.Enqueue(_ => StreamTokens(tokens));

        public void EnqueueStreamUpdates(params ChatResponseUpdate[] updates) =>
            _streaming.Enqueue(_ => StreamUpdates(updates));

        public void EnqueueReasoningThenStreamFailure(
            TextReasoningContent reasoning,
            Exception exception) =>
            _streaming.Enqueue(_ => ReasoningThenFail(reasoning, exception));

        public void EnqueueUpdatesThenStreamFailure(
            IReadOnlyList<ChatResponseUpdate> updates,
            Exception exception) =>
            _streaming.Enqueue(_ => UpdatesThenFail(updates, exception));

        public void EnqueueStreamToolCall(string toolName, string? callId = null, Dictionary<string, object?>? arguments = null)
        {
            callId ??= toolName;

            Dictionary<string, object?> args = arguments ?? new Dictionary<string, object?>();

            _streaming.Enqueue(_ => StreamToolCall(toolName, callId, args));
        }

        public void EnqueueStreamToolCallOnly(string toolName, string? callId = null, Dictionary<string, object?>? arguments = null)
        {
            callId ??= toolName;

            Dictionary<string, object?> args = arguments ?? new Dictionary<string, object?>();

            _streaming.Enqueue(_ => StreamToolCallOnly(toolName, callId, args));
        }

        public void EnqueueStreamFailure(Exception ex) =>
            _streaming.Enqueue(_ => FailingStream(ex));

        public void EnqueueImmediateStreamFailure(Exception ex) =>
            _streaming.Enqueue(_ => ImmediateFailingStream(ex));

        public void EnqueueSlowStream(TimeSpan delay, string token) =>
            _streaming.Enqueue(ct => SlowStream(delay, token, ct));

        public void EnqueueSlowBuffered(TimeSpan delay, string text) =>
            _buffered.Enqueue(async ct =>
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);

                return ResponseText(text);

            });

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<MeAiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            BufferedCallCount++;

            LastBufferedMessages = messages.ToList();

            AllBufferedCalls.Add(LastBufferedMessages);

            LastChatOptions = options;

            if (_buffered.Count == 0)
            {
                throw new InvalidOperationException("No scripted buffered response remaining.");
            }

            return _buffered.Dequeue()(cancellationToken);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MeAiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            StreamingCallCount++;

            LastBufferedMessages = messages.ToList();
            AllStreamingCalls.Add(LastBufferedMessages);

            LastChatOptions = options;

            if (_streaming.Count == 0)
            {
                throw new InvalidOperationException("No scripted streaming response remaining.");
            }

            return _streaming.Dequeue()(cancellationToken);
        }

        private ChatResponse ResponseText(string text)
        {

            ChatResponse response = new(new MeAiChatMessage(ChatRole.Assistant, text));

            if (UsageTotalTokens is { } total)
            {

                response.Usage = new UsageDetails
                {
                    InputTokenCount = total / 2,
                    OutputTokenCount = total - (total / 2),
                };

            }

            if (UsageCachedInputTokens is { } cached)
            {

                response.Usage ??= new UsageDetails();

                response.Usage.CachedInputTokenCount = cached;

            }

            return response;

        }

        private ChatResponse ResponseTool(
            string toolName,
            string callId,
            Dictionary<string, object?> arguments)
        {

            ChatResponse response = new(new MeAiChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent(callId, toolName, arguments),
            ]));

            if (UsageTotalTokens is { } total)
            {

                response.Usage = new UsageDetails
                {
                    InputTokenCount = total / 2,
                    OutputTokenCount = total - (total / 2),
                };

            }

            return response;

        }

        private static async IAsyncEnumerable<ChatResponseUpdate> StreamTokens(
            IEnumerable<string> tokens,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (string token in tokens)
            {
                cancellationToken.ThrowIfCancellationRequested();

                yield return new ChatResponseUpdate(ChatRole.Assistant, token);

                await Task.Yield();
            }
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> StreamUpdates(
            IEnumerable<ChatResponseUpdate> updates,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (ChatResponseUpdate update in updates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                yield return update;

                await Task.Yield();
            }
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> ReasoningThenFail(
            TextReasoningContent reasoning,
            Exception exception,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            yield return new ChatResponseUpdate(ChatRole.Assistant, [reasoning]);

            await Task.Yield();

            throw exception;
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> UpdatesThenFail(
            IEnumerable<ChatResponseUpdate> updates,
            Exception exception,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (ChatResponseUpdate update in updates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
                await Task.Yield();
            }

            throw exception;
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> StreamToolCall(
            string toolName,
            string callId,
            Dictionary<string, object?> arguments,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, [
                new FunctionCallContent(callId, toolName, arguments),
            ]);

            await Task.Yield();
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> StreamToolCallOnly(
            string toolName,
            string callId,
            Dictionary<string, object?> arguments,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {

            yield return new ChatResponseUpdate(ChatRole.Assistant, [
                new FunctionCallContent(callId, toolName, arguments),
            ]);

            await Task.Yield();

        }

        private static async IAsyncEnumerable<ChatResponseUpdate> FailingStream(
            Exception ex,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "partial");

            await Task.Yield();

            throw ex;
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> ImmediateFailingStream(
            Exception ex,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {

            cancellationToken.ThrowIfCancellationRequested();

            throw ex;

#pragma warning disable CS0162 // yield break is required by the async-iterator shape (CS8419) but unreachable after the throw above.
            yield break;
#pragma warning restore CS0162

        }

        private static async IAsyncEnumerable<ChatResponseUpdate> SlowStream(
            TimeSpan delay,
            string token,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

            yield return new ChatResponseUpdate(ChatRole.Assistant, token);
        }

    }

    private sealed class FakeChatClientFactory(ScriptingChatClient client, ProviderSettings provider) : IChatClientFactory
    {

        public Task<ChatClientLease> ResolveClientAsync(string? targetModel, CancellationToken cancellationToken)
        {
            ChatClientLease lease = new(
                client,
                provider,
                provider.Models[0].Name,
                ownedHttpClient: null);

            return Task.FromResult(lease);
        }

        public Task<ChatClientLease> ResolveClientAsync(ProviderSettings resolvedProvider, string resolvedModel, CancellationToken cancellationToken) =>
            ResolveClientAsync(resolvedModel, cancellationToken);

    }

    private sealed class ThrowingChatClientFactory : IChatClientFactory
    {

        public Task<ChatClientLease> ResolveClientAsync(string? targetModel, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No AI model could be resolved.");

        public Task<ChatClientLease> ResolveClientAsync(ProviderSettings provider, string resolvedModel, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No AI model could be resolved.");

    }

    private sealed class FakeGrimoireRepository : IGrimoireRepository
    {

        public Session? Session { get; init; }

        public bool ThrowOnBegin { get; init; }

        public bool ThrowOnFinalize { get; init; }

        public Guid? FixedSessionId { get; init; }

        public Guid? LastIncrementedSessionId { get; private set; }

        public long LastIncrementedTokens { get; private set; }

        public decimal LastIncrementedCostUsd { get; private set; }

        public int DiscardCallCount { get; private set; }

        public int FinalizeCallCount { get; private set; }

        public string LastFinalizedContent { get; private set; } = string.Empty;

        public Task<Session?> GetSessionAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Session?.Id == id ? Session : null);

        public Task<Session?> GetSessionHeaderAsync(Guid id, CancellationToken cancellationToken = default) =>
            GetSessionAsync(id, cancellationToken);

        public Task<(Guid SessionId, Guid AssistantEntryId)> BeginAssistantReplyAsync(
            Guid? sessionId,
            string prompt,
            string model,
            CancellationToken cancellationToken = default)
        {

            if (ThrowOnBegin)
            {
                throw new InvalidOperationException("begin failed");
            }

            return Task.FromResult((FixedSessionId ?? sessionId ?? Guid.NewGuid(), Guid.NewGuid()));

        }

        public Task FinalizeAssistantEntryAsync(Guid assistantEntryId, string fullContent, CancellationToken cancellationToken = default)
        {

            FinalizeCallCount++;
            LastFinalizedContent = fullContent;

            if (ThrowOnFinalize)
            {
                throw new InvalidOperationException("finalize failed");
            }

            return Task.CompletedTask;

        }

        public Task DiscardAssistantEntryAsync(Guid assistantEntryId, CancellationToken cancellationToken = default)
        {

            DiscardCallCount++;

            return Task.CompletedTask;

        }

        public Task AppendToolInteractionAsync(
            Guid sessionId,
            string toolName,
            string arguments,
            string result,
            string modelUsed,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SaveCompletedExchangeAsync(string userPrompt, string assistantText, string modelUsed, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<int> PurgeSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<List<GrimoireEntryDto>?> GetSessionEntriesAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<List<GrimoireEntryDto>?>(null);

        public Task<List<GrimoireEntryDto>?> GetRecentSessionEntriesAsync(Guid sessionId, int takeLast, CancellationToken cancellationToken = default) =>
            Task.FromResult<List<GrimoireEntryDto>?>(null);

        public Task<GrimoireEntryDto?> GetEntryByIdAsync(Guid sessionId, Guid entryId, CancellationToken cancellationToken = default) =>
            Task.FromResult<GrimoireEntryDto?>(null);

        public Task<bool> DeleteEntryAsync(Guid sessionId, Guid entryId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> SetEntryPinnedAsync(Guid sessionId, Guid entryId, bool pinned, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<int> GetPinnedEntryCountAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<List<Guid>> GetSessionsNeedingSummarizationAsync(int threshold, DateTime idleCutoff, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<Guid>());

        public Task<List<Entry>> GetUnsummarizedEntriesAsync(Guid sessionId, DateTime watermark, int batchSize, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<Entry>());

        public Task<bool> SessionExistsAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task IncrementSessionTokensAsync(Guid sessionId, long totalTokens, CancellationToken cancellationToken = default)
        {

            LastIncrementedSessionId = sessionId;

            LastIncrementedTokens = totalTokens;

            return Task.CompletedTask;

        }

        public Task IncrementSessionTokensAndCostAsync(Guid sessionId, long totalTokens, decimal costUsd, CancellationToken cancellationToken = default)
        {

            LastIncrementedSessionId = sessionId;

            LastIncrementedTokens = totalTokens;

            LastIncrementedCostUsd = costUsd;

            return Task.CompletedTask;

        }

        public decimal TodaySpend { get; set; }

        public Task<decimal> GetTodaySpendAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(TodaySpend);

        public Task AdvanceCampaignLogWatermarkAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UpdateSessionCampaignRollupAsync(Guid sessionId, string summary, DateTime lastSummarizedMessageAt, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string?> ReadLoreAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<LoreDto> ScribeLoreAsync(string key, string value, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LoreDto(key, value, DateTime.UtcNow));

        public Task<bool> DeleteLoreAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<ListPageResult<LoreDto>> ListLoreAsync(int? limit = null, int offset = 0, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListPageResult<LoreDto>([], false));

        public Task<LoreDto?> GetLoreAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<LoreDto?>(null);

        public Task<string> SearchArchivesAsync(string query, int maxResults, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task RecordWorkspaceContextAsync(WorkspaceContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<WorkspaceContext?> GetLatestWorkspaceContextAsync(string workspacePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<WorkspaceContext?>(null);

    }

    private sealed class FakeWard : IWard
    {

        public WardResolution NextResolution { get; init; } =
            new(true, null, DateTimeOffset.UtcNow);

        /// <summary>
        /// When set, invoked instead of returning <see cref="NextResolution"/> immediately.
        /// </summary>
        public Func<string, Task<WardResolution>>? WardHandler { get; set; }

        public int WardCallCount { get; private set; }

        public string? LastWardId { get; private set; }

        public Task<WardResolution> WardAsync(
            string wardId,
            string toolName,
            JsonDocument? arguments,
            string? sessionId,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            WardCallCount++;
            LastWardId = wardId;

            if (WardHandler is not null)
            {
                return WardHandler(wardId);
            }

            return Task.FromResult(NextResolution);
        }

        public ResolveStatus Resolve(string wardId, bool allow, string? reason) =>
            ResolveStatus.Success;

        public IReadOnlyList<ActiveWard> GetActiveWards() => [];

    }

    private sealed class FakeMcpConnectionManager : IMcpConnectionManager
    {

        public List<AITool> Tools { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<Result> StartAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> StopAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> RestartAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<McpServerInfo?> GetStatusAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult<McpServerInfo?>(null);

        public Task<McpServerInfo[]> GetAllStatusesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Array.Empty<McpServerInfo>());

        public Task<IReadOnlyList<AITool>> GetAvailableToolsAsync(string? workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AITool>>(Tools);

        public Task<AIFunction?> GetToolAsync(
            string serverName,
            string toolName,
            string? workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AIFunction?>(null);

        public Task<List<McpServerStatusDto>> GetServerStatusesAsync(string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<McpServerStatusDto>());

        public Task ReloadAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<Result> TrustWorkspaceAsync(string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

    }

    private sealed class FakeCampaignRepository : ICampaignRepository
    {

        private readonly Campaign? _campaign;

        public FakeCampaignRepository(Campaign? campaign = null)
        {

            _campaign = campaign;

        }

        public Task<Campaign?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_campaign?.Id == id ? _campaign : null);

        public Task<Campaign?> GetByPathAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(
                _campaign is not null
                    && string.Equals(path.Trim(), _campaign.Path.Trim(), StringComparison.OrdinalIgnoreCase)
                    ? _campaign
                    : null);

        public Task<Campaign?> GetByNameAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<ListPageResult<Campaign>> ListAsync(
            Core.Workspaces.WorkspaceType? typeFilter,
            int? limit = null,
            int offset = 0,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListPageResult<Campaign>([], false));

        public Task<Campaign> AddAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            Task.FromResult(campaign);

        public Task<Campaign> UpdateAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            Task.FromResult(campaign);

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

    }

    private sealed class RecordingTurnRunWriter : ITurnRunWriter
    {
        private readonly Guid _runId = Guid.NewGuid();

        private readonly ConcurrentQueue<BillableOperationRecord> _operations = new();

        public BillableOperationRecord? LastOperation => _operations.LastOrDefault();

        public IReadOnlyList<BillableOperationRecord> Operations => [.. _operations];

        public Exception? RecordException { get; init; }

        public CancellationTokenSource? CancelBeforeRecord { get; init; }

        public Task<Guid> StartRunAsync(
            InferenceRunStart start,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_runId);

        public Task CompleteRunAsync(
            Guid runId,
            InferenceRunStatus status,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<Guid> RecordBillableOperationAsync(
            BillableOperationRecord operation,
            CancellationToken cancellationToken = default)
        {
            if (CancelBeforeRecord is not null)
            {
                CancelBeforeRecord.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (RecordException is not null)
            {
                return Task.FromException<Guid>(RecordException);
            }

            _operations.Enqueue(operation);
            return Task.FromResult(Guid.NewGuid());
        }
    }

    private sealed class RecordingBudgetReservationService : IBudgetReservationService
    {
        public BudgetReservationRequest? LastRequest { get; private set; }

        public decimal? ReconciledUsd { get; private set; }

        public int ReconcileCount { get; private set; }

        public bool WasReleased { get; private set; }

        public Task<Result<BudgetReservation>> ReserveAsync(
            BudgetReservationRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(Result<BudgetReservation>.Success(new BudgetReservation(
                Guid.NewGuid(),
                request.RunId,
                request.BudgetPeriod,
                request.ReservedUsd,
                0m,
                BudgetReservationStatus.Reserved,
                request.ExpiresAt,
                DateTimeOffset.UtcNow)));
        }

        public Task ReconcileAsync(
            Guid reservationId,
            decimal actualCostUsd,
            CancellationToken cancellationToken = default)
        {
            ReconciledUsd = actualCostUsd;
            ReconcileCount++;
            return Task.CompletedTask;
        }

        public Task ReleaseAsync(
            Guid reservationId,
            CancellationToken cancellationToken = default)
        {
            WasReleased = true;
            return Task.CompletedTask;
        }

        public Task<decimal> GetTodayCommittedSpendAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0m);

        public Task<decimal> GetTodayOutstandingReservationsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0m);

        public Task<int> SweepExpiredAsync(
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    private sealed class ConfigurableSanctumGuard : ISanctumGuard
    {

        public Func<string, string, string, string, CancellationToken, Task<SanctumResult>>? PathValidator { get; init; }

        public Func<string, string, string, CancellationToken, Task<SanctumResult>>? NetworkValidator { get; init; }

        public Func<string, string, CancellationToken, Task<SanctumResult>>? ToolValidator { get; init; }

        public Task<SanctumResult> ValidatePathAsync(
            string campaignId,
            string requestedPath,
            string operationType,
            string toolName,
            CancellationToken ct = default) =>
            PathValidator is not null
                ? PathValidator(campaignId, requestedPath, operationType, toolName, ct)
                : Task.FromResult(new SanctumResult { Allowed = true });

        public Task<SanctumResult> ValidateNetworkAsync(
            string campaignId,
            string url,
            string toolName,
            CancellationToken ct = default) =>
            NetworkValidator is not null
                ? NetworkValidator(campaignId, url, toolName, ct)
                : Task.FromResult(new SanctumResult { Allowed = true });

        public Task<SanctumResult> ValidateToolAsync(string campaignId, string toolName, CancellationToken ct = default) =>
            ToolValidator is not null
                ? ToolValidator(campaignId, toolName, ct)
                : Task.FromResult(new SanctumResult { Allowed = true });

        public Task<ResourceLimits> GetEffectiveResourceLimitsForWorkspaceAsync(string? workspaceRoot, CancellationToken ct = default) =>
            Task.FromResult(new ResourceLimits());

        
        public Task<SanctumChildProcessBoundary?> GetChildProcessBoundaryForWorkspaceAsync(
            string? workspaceRoot,
            CancellationToken ct = default) =>
            Task.FromResult<SanctumChildProcessBoundary?>(null);

public Task RecordResourceLimitBreachAsync(
            string? workspaceRoot,
            string toolName,
            Core.Platform.ResourceLimitKind resource,
            string limitValue,
            string? actualValue,
            CancellationToken ct = default) =>
            Task.CompletedTask;

    }

}
