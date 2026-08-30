using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence.Guardrails;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.CommLink;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.WebResearch;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Generated;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.WebResearch;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
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
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal("buffered answer", result.Value!.Text);
    }

    [Fact]
    public async Task Scenario01b_CatalogPromptCaching_AppliesProviderBoundOptionsForEligiblePrefix()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_workspace.Root, "CODEX.md"),
            new string('x', 8_192));
        ArcanumSettings settings = DefaultSettings() with
        {
            DefaultModel = "gpt-5",
            FastModel = "gpt-5",
            Providers =
            [
                DefaultProvider() with
                {
                    Endpoint = "https://api.openai.com/v1",
                    Models = ["gpt-5"],
                },
            ],
        };
        ScriptingChatClient chat = new();
        chat.EnqueueText("cached answer");
        WizardIntelligenceProvider wizard = CreateWizard(chat, settings);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Model = "gpt-5",
                Prompt = "hello",
                SkipSpellRouting = true,
                DisableMcpTools = true,
            },
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(chat.LastChatOptions?.RawRepresentationFactory);
        Assert.Equal(2, chat.LastBufferedMessages.Count);
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

        Assert.Contains(
            events,
            static e => e.Type == IntelligenceEventType.Context
                && e.ContextBreakdown is { InputTokens: > 0 });

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
            new ChronicleHub(),
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
            [wizard, apprentice, "Complete this step.", linkedCts, apprentice.Id, false]));
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
            Cost = new CostSettings
            {
                Budget = new BudgetPolicySettings { Enabled = true, DailyLimitUsd = 10m },
            },
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
    public async Task StreamPromptAsync_StructuredOutputBestEffort_InvalidJson_WarningsOnResult()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueStreamTokens("not", " ", "json");

        ArcanumSettings settings = DefaultSettings();

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
    public async Task StructuredOutput_ChangingInvalidEvidenceBeyondFormerRetryLimit_ReachesValidResponse()
    {
        const int changingInvalidResponseCount = 4;
        const string validResponse = """{"name":"accepted"}""";
        ScriptingChatClient chat = new();

        for (int attempt = 0; attempt < changingInvalidResponseCount; attempt++)
        {
            chat.EnqueueText($$"""{"attempt":{{attempt}}}""");
        }

        chat.EnqueueText(validResponse);
        JsonElement schema = JsonSerializer.Deserialize<JsonElement>("""
            {
              "type": "object",
              "properties": { "name": { "type": "string" } },
              "required": ["name"],
              "additionalProperties": false
            }
            """);
        WizardIntelligenceProvider wizard = CreateWizard(chat);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "return structured output",
                ResponseFormat = "json_schema",
                ResponseFormatJsonSchema = schema,
                SkipSpellRouting = true,
                DisableMcpTools = true,
            },
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(changingInvalidResponseCount + 1, chat.BufferedCallCount);
        Assert.Equal(validResponse, result.Value.Text);
    }

    [Fact]
    public async Task Guardrails_PiiInInput_BlocksBeforeInference()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueText("should not be reached");

        ArcanumSettings settings = ConfigureGuardrails(
            DefaultSettings(),
            enabled: true,
            detectPii: true);

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, guardrailsPipeline: CreateGuardrailsPipeline(settings));

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "Email me at alice@example.com", SkipSpellRouting = true, DisableMcpTools = true },
            InvocationContexts.AttendedSession(),
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

        ArcanumSettings settings = ConfigureGuardrails(
            DefaultSettings(),
            enabled: true,
            detectPii: false,
            blockToxicity: true,
            toxicityBlocklist: ["bad-word"]);

        FakeGrimoireRepository grimoire = new();

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, grimoire: grimoire, guardrailsPipeline: CreateGuardrailsPipeline(settings));

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "say something", SkipSpellRouting = true, DisableMcpTools = true },
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Guardrails.Blocked, result.Error.Code);

    }

    [Fact]
    public async Task Guardrails_Disabled_PassesThrough()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueText("alice@example.com is fine when guardrails are off");

        ArcanumSettings settings = ConfigureGuardrails(
            DefaultSettings(),
            enabled: false,
            detectPii: true);

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "alice@example.com", SkipSpellRouting = true, DisableMcpTools = true },
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Contains("alice@example.com", result.Value!.Text);

    }

    [Fact]
    public async Task Guardrails_PiiInStatelessInput_BlocksBeforeInference()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueText("should not be reached");

        ArcanumSettings settings = ConfigureGuardrails(
            DefaultSettings(),
            enabled: true,
            detectPii: true);

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, guardrailsPipeline: CreateGuardrailsPipeline(settings));

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = string.Empty,
                StatelessMessages = [new CoreChatMessage("user", "My SSN is 123-45-6789")],
                SkipSpellRouting = true,
                DisableMcpTools = true,
            },
            InvocationContexts.AttendedSession(),
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

        ArcanumSettings settings = ConfigureGuardrails(
            DefaultSettings(),
            enabled: true,
            detectPii: false,
            blockToxicity: true,
            toxicityBlocklist: ["bad-word"]);

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, guardrailsPipeline: CreateGuardrailsPipeline(settings));

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with { Prompt = "stream something", SkipSpellRouting = true, DisableMcpTools = true });

        Assert.DoesNotContain(events, e => e.Type == IntelligenceEventType.Token);

        IntelligenceEvent error = Assert.Single(events, e => e.Type == IntelligenceEventType.Error);

        // Output-stage rejection: the message must blame the model's response, not the prompt.
        Assert.Contains("Response blocked", error.Message, StringComparison.Ordinal);

        Assert.Contains("matched a guardrail policy", error.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task Guardrails_Streaming_BufferedToxicity_DiscardsAssistantEntry()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueStreamTokens("the model says ", "bad-word here");

        ArcanumSettings settings = ConfigureGuardrails(
            DefaultSettings(),
            enabled: true,
            detectPii: false,
            blockToxicity: true,
            toxicityBlocklist: ["bad-word"]);

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

        // Output-stage rejection: the message must blame the model's response, not the prompt.
        Assert.Contains("Response blocked", error.Message, StringComparison.Ordinal);

        Assert.Contains("matched a guardrail policy", error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(1, grimoire.DiscardCallCount);

        Assert.Equal(0, grimoire.FinalizeCallCount);

    }

    [Fact]
    public async Task Guardrails_Streaming_CodeOwnedBufferedPolicy_WithholdsTokensThenError()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueStreamTokens("the model says ", "bad-word here");

        ArcanumSettings settings = ConfigureGuardrails(
            DefaultSettings(),
            enabled: true,
            detectPii: false,
            blockToxicity: true,
            toxicityBlocklist: ["bad-word"]);

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, guardrailsPipeline: CreateGuardrailsPipeline(settings));

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with { Prompt = "stream something", SkipSpellRouting = true, DisableMcpTools = true });

        Assert.DoesNotContain(events, e => e.Type == IntelligenceEventType.Token);

        IntelligenceEvent error = Assert.Single(events, e => e.Type == IntelligenceEventType.Error);

        // Output-stage rejection: the message must blame the model's response, not the prompt.
        Assert.Contains("Response blocked", error.Message, StringComparison.Ordinal);

        Assert.Contains("matched a guardrail policy", error.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task Guardrails_Streaming_BufferedMode_DisabledGuardrails_Passthrough()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueStreamTokens("the model says ", "bad-word here");

        ArcanumSettings settings = ConfigureGuardrails(
            DefaultSettings(),
            enabled: false,
            blockToxicity: true,
            toxicityBlocklist: ["bad-word"]);

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
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal("time retrieved", result.Value!.Text);

        Assert.NotNull(result.Value.ToolCalls);

        Assert.Single(result.Value.ToolCalls!);
    }

    [Fact]
    public async Task Scenario04_BufferedToolLoop_ChangingEvidenceBeyondFormerLimits_Completes()
    {
        const int toolRoundCount = 40;
        const string progressToolName = "record_progress";
        ScriptingChatClient chat = new();
        FakeMcpConnectionManager mcp = new();
        mcp.Tools.Add(CreateProgressMcpTool(progressToolName));

        for (int round = 1; round <= toolRoundCount; round++)
        {
            chat.EnqueueToolCall(
                progressToolName,
                $"progress-{round}",
                new Dictionary<string, object?> { ["evidence"] = round });
        }

        chat.EnqueueText("completed after changing evidence");
        WizardIntelligenceProvider wizard = CreateWizard(chat, mcp: mcp);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "make progress", SkipSpellRouting = true },
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(toolRoundCount + 1, chat.BufferedCallCount);
        Assert.Equal("completed after changing evidence", result.Value.Text);
        Assert.Equal(toolRoundCount, result.Value.ToolCalls?.Count);
        for (int round = 1; round <= toolRoundCount; round++)
        {
            string expectedEvidence = $"evidence-{round}";
            Assert.Contains(
                chat.LastBufferedMessages,
                message => message.Role == ChatRole.Tool
                    && GetMessageText(message).Contains(
                        expectedEvidence,
                        StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task BufferedToolLoop_RepeatedIdenticalRound_ReturnsTypedNoProgressFailure()
    {

        const string progressToolName = "record_progress";

        ScriptingChatClient chat = new();

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(CreateProgressMcpTool(progressToolName));

        Dictionary<string, object?> arguments = new()
        {

            ["evidence"] = 1,

        };

        chat.EnqueueToolCall(progressToolName, "repeat-1", arguments);

        chat.EnqueueToolCall(progressToolName, "repeat-2", arguments);

        chat.EnqueueText("must not be reached");

        WizardIntelligenceProvider wizard = CreateWizard(chat, mcp: mcp);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "detect no progress", SkipSpellRouting = true },
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Hub.NoProgressDetected, result.Error.Code);

        Assert.Equal(2, chat.BufferedCallCount);

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
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal("no tools needed", result.Value!.Text);

        Assert.Equal(2, chat.BufferedCallCount);
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
                DefaultProvider() with { ContextWindowLimit = 32_768 },
            ],
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
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        bool compressedPrompt = chat.AllBufferedCalls
            .SelectMany(static batch => batch)
            .Any(static m => m.Role == ChatRole.System
                && m.Text.Contains("### Campaign Summary (compressed context)", StringComparison.Ordinal));

        Assert.True(compressedPrompt);
    }

    [Fact]
    public async Task Scenario11_ContextCompression_SkippedBelowThreshold()
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
                DefaultProvider() with { ContextWindowLimit = 262_144 },
            ],
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
            InvocationContexts.AttendedSession(),
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
            InvocationContexts.AttendedSession(),
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
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        HashSet<string> toolNames = ToolNames(chat.LastChatOptions);

        Assert.Contains("alpha_tool", toolNames);

        Assert.Contains("beta_tool", toolNames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Attunement_NonEmptyDeclaredToolsWithoutWebTools_OmitsWebTools(
        bool disableMcpTools)
    {
        await CreateSpellWithDeclaredToolsAsync("browse-restricted", ["allowed_tool"]);

        ScriptingChatClient chat = new();

        chat.EnqueueText("attuned");

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(CreateMcpTool("allowed_tool"));

        ArcanumSettings baseline = DefaultSettings();

        ArcanumSettings settings = baseline with
        {
            Features = baseline.Features with { WebBrowsing = true },
        };

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, mcp: mcp);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "cast",
                WorkingDirectory = _workspace.Root,
                OverrideSpellName = "browse-restricted",
                DisableMcpTools = disableMcpTools,
            },
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        HashSet<string> toolNames = ToolNames(chat.LastChatOptions);

        Assert.DoesNotContain(ArcanumBrowseWebTool.ToolName, toolNames);
        Assert.DoesNotContain(ArcanumBuiltInToolNames.WebSearch, toolNames);
        Assert.DoesNotContain(ArcanumBuiltInToolNames.ReadUrl, toolNames);
        Assert.Contains(ArcanumLocalTimeTool.ToolName, toolNames);
        Assert.Contains(ArcanumSystemInfoTool.ToolName, toolNames);
        Assert.Equal(!disableMcpTools, toolNames.Contains("allowed_tool"));
    }

    [Theory]
    [InlineData("browse-declared", true, false)]
    [InlineData("browse-open", false, true)]
    public async Task Attunement_LegacyBrowseOrUnrestricted_AdvertisesCanonicalWebTools(
        string folderName,
        bool declareBrowseWeb,
        bool expectWebSearch)
    {
        await CreateSpellWithDeclaredToolsAsync(
            folderName,
            declareBrowseWeb ? [ArcanumBrowseWebTool.ToolName] : []);

        ScriptingChatClient chat = new();

        chat.EnqueueText("browse");

        ArcanumSettings baseline = DefaultSettings();

        ArcanumSettings settings = baseline with
        {
            Features = baseline.Features with { WebBrowsing = true },
        };

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "cast",
                WorkingDirectory = _workspace.Root,
                OverrideSpellName = folderName,
                DisableMcpTools = true,
            },
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        HashSet<string> toolNames = ToolNames(chat.LastChatOptions);

        Assert.DoesNotContain(ArcanumBrowseWebTool.ToolName, toolNames);
        Assert.Contains(ArcanumBuiltInToolNames.ReadUrl, toolNames);
        Assert.Equal(
            expectWebSearch,
            toolNames.Contains(ArcanumBuiltInToolNames.WebSearch));
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
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        MeAiChatMessage system = chat.LastBufferedMessages.First(static m => m.Role == ChatRole.System);

        Assert.Contains("Resonant Spells (Dependencies)", system.Text, StringComparison.Ordinal);

        Assert.Contains("dependency body", system.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scenario15_ModelNotFound_ReturnsHubModelError()
    {
        const string canary = "CANARY_MODEL_RESOLUTION_CREDENTIAL";
        TestCapturingLogger<WizardIntelligenceProvider> logger = new();
        WizardIntelligenceProvider wizard = CreateWizard(
            new ScriptingChatClient(),
            factory: new ThrowingChatClientFactory { FailureMessage = canary },
            logger: logger);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "hello", Model = "missing", SkipSpellRouting = true },
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Hub.Model", result.Error.Code);

        Assert.Equal(
            "The requested model is not configured. Check Arcanum:Providers and Arcanum:DefaultModel.",
            result.Error.Message);

        TestLogEntry log = Assert.Single(
            logger.Entries,
            static entry => entry.Message.Contains(
                "model resolution",
                StringComparison.OrdinalIgnoreCase));
        Assert.Null(log.Exception);
        Assert.DoesNotContain(canary, log.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), log.Message, StringComparison.Ordinal);
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
                InvocationContexts.AttendedSession(),
                cts.Token))
            {
            }
        });
    }

    [Fact]
    public async Task Scenario17_EmptyPrompt_ReturnsValidationError()
    {

        WizardIntelligenceProvider wizard = CreateWizard(new ScriptingChatClient());

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "   ", SkipSpellRouting = true, DisableMcpTools = true },
            InvocationContexts.AttendedSession(),
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
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Hub.Error", result.Error.Code);

    }

    [Fact]
    public async Task Scenario19_StreamingToolLoop_ChangingEvidenceBeyondFormerLimits_Completes()
    {
        const int toolRoundCount = 12;
        const string progressToolName = "record_progress";
        ScriptingChatClient chat = new();
        FakeMcpConnectionManager mcp = new();
        mcp.Tools.Add(CreateProgressMcpTool(progressToolName));

        for (int round = 1; round <= toolRoundCount; round++)
        {
            chat.EnqueueStreamToolCall(
                progressToolName,
                $"progress-{round}",
                new Dictionary<string, object?> { ["evidence"] = round });
        }

        chat.EnqueueStreamTokens("completed ", "after changing evidence");
        WizardIntelligenceProvider wizard = CreateWizard(chat, mcp: mcp);

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with { Prompt = "make progress", SkipSpellRouting = true });

        Assert.DoesNotContain(events, static e => e.Type == IntelligenceEventType.Error);
        Assert.Equal(toolRoundCount + 1, chat.StreamingCallCount);
        Assert.Contains(events, static e => e.Type == IntelligenceEventType.Result);
        Assert.Contains(
            events,
            static e => e.Type == IntelligenceEventType.Token
                && e.Data == "after changing evidence");
        for (int round = 1; round <= toolRoundCount; round++)
        {
            string expectedEvidence = $"evidence-{round}";
            Assert.Contains(
                events,
                e => e.Type == IntelligenceEventType.ToolResult
                    && e.Data?.Contains(expectedEvidence, StringComparison.Ordinal) == true);
        }

    }

    [Fact]
    public async Task Issue220_Multi_tool_stream_keeps_live_and_durable_tool_records()
    {
        const string toolName = "record_progress";
        Guid sessionId = Guid.Parse("22000000-0000-0000-0000-000000000001");
        FakeGrimoireRepository grimoire = new() { FixedSessionId = sessionId };
        ScriptingChatClient chat = new();
        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(CreateProgressMcpTool(toolName));
        chat.EnqueueStreamToolCall(
            toolName,
            "progress-1",
            new Dictionary<string, object?> { ["evidence"] = 1 });
        chat.EnqueueStreamToolCall(
            toolName,
            "progress-2",
            new Dictionary<string, object?> { ["evidence"] = 2 });
        chat.EnqueueStreamTokens("completed");

        WizardIntelligenceProvider wizard = CreateWizard(chat, grimoire: grimoire, mcp: mcp);

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with
            {
                Prompt = "record changing progress",
                SessionId = sessionId,
                SkipSpellRouting = true,
            });

        List<IntelligenceEvent> toolCalls = events
            .Where(static evt => evt.Type == IntelligenceEventType.ToolCall)
            .ToList();
        List<IntelligenceEvent> toolResults = events
            .Where(static evt => evt.Type == IntelligenceEventType.ToolResult)
            .ToList();
        List<IntelligenceEvent> toolEvents = events
            .Where(static evt => evt.Type is IntelligenceEventType.ToolCall or IntelligenceEventType.ToolResult)
            .ToList();

        void AssertToolEvent(
            IntelligenceEvent evt,
            IntelligenceEventType expectedType,
            string expectedCallId)
        {
            Assert.Equal(expectedType, evt.Type);
            Assert.NotNull(evt.ToolCall);
            Assert.Equal(expectedCallId, evt.ToolCall!.CallId);
            Assert.Equal(toolName, evt.ToolCall.Name);
        }

        Assert.Collection(
            toolEvents,
            evt => AssertToolEvent(evt, IntelligenceEventType.ToolCall, "progress-1"),
            evt => AssertToolEvent(evt, IntelligenceEventType.ToolResult, "progress-1"),
            evt => AssertToolEvent(evt, IntelligenceEventType.ToolCall, "progress-2"),
            evt => AssertToolEvent(evt, IntelligenceEventType.ToolResult, "progress-2"));

        Assert.Equal(2, toolCalls.Count);
        Assert.Equal(2, toolResults.Count);
        Assert.Equal(2, grimoire.ToolInteractions.Count);
        Assert.DoesNotContain(events, static evt => evt.Type == IntelligenceEventType.Error);

        for (int index = 0; index < toolCalls.Count; index++)
        {
            IntelligenceEvent toolCall = toolCalls[index];
            IntelligenceEvent toolResult = toolResults[index];
            FakeGrimoireRepository.RecordedToolInteraction persisted =
                grimoire.ToolInteractions[index];

            Assert.NotNull(toolCall.ToolCall);
            Assert.NotNull(toolResult.ToolCall);
            Assert.True(
                events.IndexOf(toolResult) > events.IndexOf(toolCall),
                "Each ToolResult must follow its corresponding ToolCall.");
            Assert.Equal(toolCall.ToolCall!.CallId, toolResult.ToolCall!.CallId);
            Assert.Equal(toolName, toolCall.ToolCall.Name);
            Assert.Equal(toolName, toolResult.ToolCall.Name);
            Assert.Equal(toolCall.ToolCall.Name, toolResult.ToolCall.Name);
            Assert.Equal(toolCall.ToolCall.Name, persisted.ToolName);
            Assert.Equal(
                System.Text.Encoding.UTF8.GetBytes(toolCall.ToolCall.ArgumentsJson),
                System.Text.Encoding.UTF8.GetBytes(persisted.Arguments));
            Assert.Equal(
                System.Text.Encoding.UTF8.GetBytes(toolResult.Data!),
                System.Text.Encoding.UTF8.GetBytes(persisted.Result));
            Assert.Equal(sessionId, persisted.SessionId);
            Assert.Equal(ModelName, persisted.ModelUsed);
        }

        Assert.Equal("progress-1", toolCalls[0].ToolCall!.CallId);
        Assert.Equal("progress-2", toolCalls[1].ToolCall!.CallId);
        Assert.Equal("evidence-1", toolResults[0].Data);
        Assert.Equal("evidence-2", toolResults[1].Data);

    }

    [Fact]
    public async Task StreamingToolLoop_RepeatedIdenticalRound_EmitsTypedNoProgressError()
    {

        const string progressToolName = "record_progress";

        ScriptingChatClient chat = new();

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(CreateProgressMcpTool(progressToolName));

        Dictionary<string, object?> arguments = new()
        {

            ["evidence"] = 1,

        };

        chat.EnqueueStreamToolCall(progressToolName, "repeat-1", arguments);

        chat.EnqueueStreamToolCall(progressToolName, "repeat-2", arguments);

        chat.EnqueueStreamTokens("must not be reached");

        WizardIntelligenceProvider wizard = CreateWizard(chat, mcp: mcp);

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with { Prompt = "detect no progress", SkipSpellRouting = true });

        IntelligenceEvent error = Assert.Single(
            events,
            static e => e.Type == IntelligenceEventType.Error);

        Assert.Equal(ErrorCodes.Hub.NoProgressDetected, error.Data);

        Assert.DoesNotContain(
            events,
            static e => e.Type == IntelligenceEventType.Result);

        Assert.Equal(2, chat.StreamingCallCount);

    }

    [Fact]
    public async Task Scenario20_AttachedFilesBeyondFormerCountCeiling_AreAccepted()
    {
        const int fileCount = 33;

        ScriptingChatClient chat = new();

        chat.EnqueueText("accepted");

        WizardIntelligenceProvider wizard = CreateWizard(chat);
        List<AttachedFileDto> files = Enumerable.Range(1, fileCount)
            .Select(static index => new AttachedFileDto($"file-{index}.txt", "content"))
            .ToList();

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "files",
                SkipSpellRouting = true,
                DisableMcpTools = true,
                AttachedFiles = files,
            },
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal("accepted", result.Value!.Text);

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
            InvocationContexts.AttendedSession(),
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
            InvocationContexts.AttendedSession(),
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
            InvocationContexts.AttendedSession(),
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
            InvocationContexts.AttendedSession(),
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
            InvocationContexts.AttendedSession(),
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
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Validation.AttachedFiles", result.Error.Code);

    }

    [Fact]
    public async Task Scenario27_AttachedFileOversizedContent_ReturnsValidationError()
    {
        int oversizedLength = checked((int)ArcanumSettingClamps.MaxAttachFileSizeBytes(
            ArcanumRuntimeDefaults.CliMaxAttachFileSizeBytes) + 1);
        WizardIntelligenceProvider wizard = CreateWizard(new ScriptingChatClient());

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "files",
                SkipSpellRouting = true,
                DisableMcpTools = true,
                AttachedFiles = [new("big.txt", new string('x', oversizedLength))],
            },
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Validation.AttachedFiles", result.Error.Code);

    }

    [Fact]
    public async Task Scenario28_StreamAttachedFilesBeyondFormerCountCeiling_Completes()
    {
        const int fileCount = 33;

        ScriptingChatClient chat = new();

        chat.EnqueueStreamTokens("accepted");

        WizardIntelligenceProvider wizard = CreateWizard(chat);

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with
            {
                Prompt = "files",
                SkipSpellRouting = true,
                DisableMcpTools = true,
                AttachedFiles = Enumerable.Range(1, fileCount)
                    .Select(static index => new AttachedFileDto($"file-{index}.txt", "content"))
                    .ToList(),
            });

        Assert.Contains(events, static e => e.Type == IntelligenceEventType.Result);

        Assert.DoesNotContain(events, static e => e.Type == IntelligenceEventType.Error);

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
                DefaultProvider() with { ContextWindowLimit = 262_144 },
            ],
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
            InvocationContexts.AttendedSession(),
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
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal("stateless ok", result.Value!.Text);

    }

    [Fact]
    public async Task Scenario33_SessionBeginFailure_AbortsBeforeProviderDispatch()
    {

        // Inverted by issue #83. A begin failure used to be caught into an empty handle and the turn
        // continued, so a deleted Campaign, a missing Session, or a binding mismatch all produced a
        // normal-looking answer attached to nothing durable (§10.12).
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
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.False(result.IsSuccess);

        // The buffered projection reports in-turn aborts as Hub.Error; the begin failure's own message
        // survives, and carrying the typed storage code all the way out is a turn-result change that
        // belongs with the turn-publication slice.
        Assert.Equal(ErrorCodes.Hub.Error, result.Error.Code);

        // The provider was never dialled, so its scripted answer is still queued.
        Assert.Equal(0, chat.BufferedCallCount);

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
            InvocationContexts.AttendedSession(),
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

        ArcanumSettings settings = DefaultSettings();

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, grimoire);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "hello",
                SessionId = sessionId,
                SkipSpellRouting = true,
                DisableMcpTools = true,
            },
            InvocationContexts.AttendedSession(),
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
        ArcanumSettings settings = DefaultSettings();
        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, grimoire);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "hello",
                SessionId = sessionId,
                SkipSpellRouting = true,
                DisableMcpTools = true,
            },
            InvocationContexts.AttendedSession(),
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
            Cost = new CostSettings
            {
                Pricing = new PricingSettings
                {
                    ModelPricing = new Dictionary<string, ModelPricingEntry>(StringComparer.OrdinalIgnoreCase)
                    {
                        [ModelName] = new() { InputPer1M = 10.00m, OutputPer1M = 30.00m },
                    },
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
            InvocationContexts.AttendedSession(),
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
            DefaultModel = "gpt-5",
            FastModel = "gpt-5",
            Providers =
            [
                DefaultProvider() with
                {
                    Endpoint = "https://api.openai.com/v1",
                    Models = ["gpt-5"],
                },
            ],
        };

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, grimoire);

        string marker = settings.Providers[0].Name;

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
                Model = "gpt-5",
                Prompt = "hello",
                SessionId = sessionId,
                SkipSpellRouting = true,
                DisableMcpTools = true,
            },
            InvocationContexts.AttendedSession(),
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
            InvocationContexts.AttendedSession(),
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

        const string canary = "CANARY_TOOL_SECRET_FILE_CONTENT";
        ScriptingChatClient chat = new();

        chat.EnqueueStreamToolCall("failing_tool");

        chat.EnqueueStreamTokens("after tool");

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(CreateThrowingMcpTool("failing_tool", canary));

        TestCapturingLogger<ToolExecutionPipeline> toolLogger = new();
        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            mcp: mcp,
            toolLogger: toolLogger);

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

        string serialized = string.Join(
            '\n',
            events.Select(static frame => JsonSerializer.Serialize(
                frame,
                ArcanumJsonContext.Default.IntelligenceEvent)));
        Assert.DoesNotContain(canary, serialized, StringComparison.Ordinal);

        TestLogEntry log = Assert.Single(toolLogger.Entries);
        Assert.Null(log.Exception);
        Assert.DoesNotContain(canary, log.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), log.Message, StringComparison.Ordinal);
        Assert.Contains("failing_tool", log.Message, StringComparison.Ordinal);

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

        mcp.Tools.Add(CreateMcpTool("search_workspace"));

        mcp.Tools.Add(CreateMcpTool("write_file"));

        mcp.Tools.Add(CreateMcpTool("apply_patch"));

        mcp.Tools.Add(CreateMcpTool("workspace_check"));

        mcp.Tools.Add(CreateMcpTool("use_commlink"));

        WizardIntelligenceProvider wizard = CreateWizard(chat, mcp: mcp);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "list",
                SkipSpellRouting = true,
                ToolPolicy = ToolPolicy.ReadOnlyTools,
            },
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        HashSet<string> toolNames = ToolNames(chat.LastChatOptions);

        Assert.Contains("read_file_chunk", toolNames);

        Assert.Contains("search_workspace", toolNames);

        Assert.DoesNotContain("write_file", toolNames);

        Assert.DoesNotContain("apply_patch", toolNames);

        Assert.DoesNotContain("workspace_check", toolNames);
        Assert.DoesNotContain("use_commlink", toolNames);

    }

    /// <summary>
    /// <c>disableMcpTools</c> and a filtering <c>toolPolicy</c> are both narrowing instructions, so the
    /// answer to both is their intersection, not the policy alone. Dropping the flag for
    /// <c>readOnlyTools</c>/<c>noForbiddenArts</c> advertised MCP tools to a caller that asked for none.
    /// </summary>
    [Theory]
    [InlineData(ToolPolicy.ReadOnlyTools)]
    [InlineData(ToolPolicy.NoForbiddenArts)]
    public async Task A_filtering_tool_policy_still_honours_disable_mcp_tools(ToolPolicy policy)
    {

        ScriptingChatClient chat = new();

        chat.EnqueueText("filtered");

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(CreateMcpTool("read_file_chunk"));

        mcp.Tools.Add(CreateMcpTool("search_workspace"));

        WizardIntelligenceProvider wizard = CreateWizard(chat, mcp: mcp);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "list",
                SkipSpellRouting = true,
                ToolPolicy = policy,
                DisableMcpTools = true,
            },
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        HashSet<string> toolNames = ToolNames(chat.LastChatOptions);

        Assert.DoesNotContain("read_file_chunk", toolNames);

        Assert.DoesNotContain("search_workspace", toolNames);

    }

    /// <summary>
    /// The wire converter refuses an undefined policy, so such a value can only arrive from in-process
    /// construction — but both consumers treated "unrecognized" as the permissive arm, which is the one
    /// direction an unknown restriction must never take.
    /// </summary>
    [Fact]
    public async Task An_undefined_tool_policy_advertises_nothing_rather_than_everything()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueText("undefined");

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(CreateMcpTool("read_file_chunk"));

        mcp.Tools.Add(CreateMcpTool("write_file"));

        WizardIntelligenceProvider wizard = CreateWizard(chat, mcp: mcp);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "list",
                SkipSpellRouting = true,
                ToolPolicy = (ToolPolicy)99,
            },
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Empty(ToolNames(chat.LastChatOptions));

    }

    [Fact]
    public async Task Stateless_turn_does_not_advertise_apply_patch()
    {

        ScriptingChatClient chat = new();
        chat.EnqueueText("stateless");
        FakeMcpConnectionManager mcp = new();
        mcp.Tools.Add(CreateMcpTool("read_file_chunk"));
        mcp.Tools.Add(CreateMcpTool("write_file"));
        mcp.Tools.Add(CreateMcpTool("replace_text_block"));
        mcp.Tools.Add(CreateMcpTool(ToolRiskClassifier.ApplyPatchToolName));
        WizardIntelligenceProvider wizard = CreateWizard(chat, mcp: mcp);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = string.Empty,
                StatelessMessages = [new CoreChatMessage("user", "inspect the workspace")],
                SkipSpellRouting = true,
            },
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        HashSet<string> toolNames = ToolNames(chat.LastChatOptions);
        Assert.Contains("read_file_chunk", toolNames);
        Assert.DoesNotContain("write_file", toolNames);
        Assert.DoesNotContain("replace_text_block", toolNames);
        Assert.DoesNotContain(ToolRiskClassifier.ApplyPatchToolName, toolNames);

    }

    [Fact]
    public async Task Unattended_write_file_executes_when_campaign_settings_are_missing()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueToolCall(
            "write_file",
            "unattended-write-call",
            new Dictionary<string, object?>
            {
                ["relativePath"] = "unattended.txt",
                ["content"] = "write",
            });

        chat.EnqueueText("done");

        Campaign campaign = BuildSanctumCampaign(
            _workspace.Root,
            enabled: false,
            SanctumMode.Strict);

        // The former campaign Ward setting treated this deserialized-null value as the warded
        // default. Keeping the persisted absence fixture here proves that fallback cannot re-gate.
        campaign.Settings = "null";

        FakeWard ward = new();

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(CreateMcpTool("write_file"));

        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            ward: ward,
            campaignRepository: new FakeCampaignRepository(campaign),
            mcp: mcp);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "write",
                WorkingDirectory = _workspace.Root,
                SkipSpellRouting = true,
                UnattendedMode = true,
            },
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Single(result.Value.ToolCalls!);

        Assert.Contains(
            chat.AllBufferedCalls.SelectMany(static batch => batch),
            static message => message.Role == ChatRole.Tool
                && string.Equals(GetMessageText(message), "ok", StringComparison.Ordinal));

        Assert.Equal(0, ward.WardCallCount);

        Assert.Equal([WardResolutionOrigin.Ungated], ward.AutomaticResolutionOrigins);

    }

    [Fact]
    public async Task Apply_patch_executes_when_listed_as_a_forbidden_art_in_an_unattended_turn()
    {

        const string relativePath = "unattended-production-patch.txt";

        const string replacement = "unattended patch executed";

        ArcanumSettings settings = DefaultSettings();

        settings.Security.Ward.ForbiddenArts = [ToolRiskClassifier.ApplyPatchToolName];

        FakeGrimoireRepository grimoire = new()
        {
            FixedSessionId = Guid.NewGuid(),
            MandatoryAppendOutcome = MandatoryToolInteractionAppendOutcome.NewlyCommitted,
        };

        ScriptingChatClient chat = new();

        chat.EnqueueToolCall(
            ToolRiskClassifier.ApplyPatchToolName,
            "unattended-patch-call",
            new Dictionary<string, object?>
            {
                ["dryRun"] = false,
                ["patch"] =
                    $"--- /dev/null\n+++ b/{relativePath}\n@@ -0,0 +1 @@\n+{replacement}\n",
            });

        chat.EnqueueText("patched");

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(CreateProductionApplyPatchTool(settings));

        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            settings,
            grimoire: grimoire,
            mcp: mcp);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "patch",
                WorkingDirectory = _workspace.Root,
                SkipSpellRouting = true,
                UnattendedMode = true,
            },
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        PromptToolCall toolCall = Assert.Single(result.Value.ToolCalls!);

        Assert.Equal(ToolRiskClassifier.ApplyPatchToolName, toolCall.Name);

        MandatoryToolInteraction persisted = Assert.Single(grimoire.MandatoryInteractions);

        Assert.Equal(ToolRiskClassifier.ApplyPatchToolName, persisted.ToolName);

        Assert.Equal(
            replacement + "\n",
            await File.ReadAllTextAsync(Path.Combine(_workspace.Root, relativePath)));

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_patch_binds_persisted_turn_and_reuses_exact_call_and_result_snapshots(
        bool providerCallIdMissing)
    {

        const string relativePath = "buffered-production-patch.txt";
        const string replacement = "buffered exact result";
        Guid sessionId = Guid.NewGuid();
        ArcanumSettings settings = DefaultSettings();
        FakeGrimoireRepository grimoire = new()
        {
            FixedSessionId = sessionId,
            MandatoryAppendOutcome =
                MandatoryToolInteractionAppendOutcome.NewlyCommitted,
        };
        ScriptingChatClient chat = new();
        Dictionary<string, object?> arguments = new()
        {
            ["dryRun"] = false,
            ["patch"] =
                $"--- /dev/null\n+++ b/{relativePath}\n@@ -0,0 +1 @@\n+{replacement}\n",
        };
        if (providerCallIdMissing)
        {
            chat.EnqueueToolCallWithMissingId(
                ToolRiskClassifier.ApplyPatchToolName,
                arguments);
        }
        else
        {
            chat.EnqueueToolCall(
                ToolRiskClassifier.ApplyPatchToolName,
                "provider-patch-call",
                arguments);
        }
        chat.EnqueueText("patched");
        FakeMcpConnectionManager mcp = new();
        mcp.Tools.Add(CreateProductionApplyPatchTool(settings));
        FakeInferenceAuditLogger auditLogger = new();
        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            settings,
            grimoire: grimoire,
            mcp: mcp,
            auditLogger: auditLogger);
        InferenceAuditContext auditContext = new()
        {
            RequestType = "unit6-buffered-patch",
        };
        TurnExecutionRequest request = new(
            BaseRequest() with
            {
                Prompt = "patch",
                WorkingDirectory = _workspace.Root,
                SkipSpellRouting = true,
            },
            InvocationContexts.AttendedSession(),
            TurnResponseMode.Buffered,
            TurnPurpose.Interactive,
            HumanInteractionAvailable: false,
            HasIdempotencyKey: false,
            AccountingHandle: null);
        TurnEngine engine = new(wizard);
        List<TurnEvent> events = [];

        await foreach (TurnEvent evt in engine.RunTurnAsync(
            request,
            auditContext,
            CancellationToken.None))
        {
            events.Add(evt);
        }

        RunCompleted completed = Assert.Single(events.OfType<RunCompleted>());
        ToolInvocationCompleted toolResult =
            Assert.Single(events.OfType<ToolInvocationCompleted>());
        MandatoryToolInteraction persisted =
            Assert.Single(grimoire.MandatoryInteractions);
        PromptToolCall observed = Assert.Single(completed.ToolCalls!);
        Guid assistantEntryId = Assert.IsType<Guid>(
            grimoire.LastAssistantEntryId);
        string? providerCallId =
            providerCallIdMissing ? null : "provider-patch-call";
        ToolInteractionReceipt expectedReceipt =
            ToolInteractionReceiptDerivation.Derive(
                new ToolInvocationIdentity(
                    assistantEntryId.ToString("D"),
                    providerCallId,
                    ToolRoundOrdinal: 0,
                    CallOrdinal: 0,
                    ToolRiskClassifier.ApplyPatchToolName));

        Assert.Equal(sessionId, persisted.SessionId);
        Assert.Equal(expectedReceipt, persisted.Receipt);
        Assert.Equal(
            providerCallId,
            persisted.ToolCallId);
        Assert.Equal(observed.ArgumentsJson, persisted.Arguments);
        Assert.Equal(persisted.Arguments, toolResult.ArgumentsJson);
        Assert.Equal(
            System.Text.Encoding.UTF8.GetBytes(persisted.Result),
            System.Text.Encoding.UTF8.GetBytes(toolResult.ResultText));
        Assert.False(string.IsNullOrWhiteSpace(observed.CallId));
        Assert.Equal(
            System.Text.Encoding.UTF8.GetBytes(persisted.Result),
            System.Text.Encoding.UTF8.GetBytes(
                GetMessageText(
                    chat.AllBufferedCalls[1].Single(
                        static message => message.Role == ChatRole.Tool))));
        Assert.Equal(0, grimoire.AppendToolInteractionCallCount);
        Assert.Equal(
            replacement + "\n",
            await File.ReadAllTextAsync(
                Path.Combine(_workspace.Root, relativePath)));
        Assert.Equal(
            [ToolRiskClassifier.ApplyPatchToolName],
            auditContext.ToolNames);
        Assert.Equal([persisted.Arguments], auditContext.ToolArgumentsJson);
        InferenceAuditRecord audit = Assert.Single(auditLogger.Records);
        Assert.Equal(1, audit.ToolCalls);
        Assert.Equal(
            [ToolRiskClassifier.ApplyPatchToolName],
            audit.ToolNames);
        Assert.Null(audit.ToolArgumentsJson);

    }

    [Fact]
    public async Task Apply_patch_multi_call_rounds_keep_stable_receipt_ordinals()
    {

        ArcanumSettings settings = DefaultSettings();
        FakeGrimoireRepository grimoire = new()
        {
            FixedSessionId = Guid.NewGuid(),
            MandatoryAppendOutcome =
                MandatoryToolInteractionAppendOutcome.NewlyCommitted,
        };
        ScriptingChatClient chat = new();
        chat.EnqueueResponse(
            new ChatResponse(
                new MeAiChatMessage(
                    ChatRole.Assistant,
                    [
                        new FunctionCallContent(
                            "round-0-call-0",
                            ToolRiskClassifier.ApplyPatchToolName,
                            CreateFilePatchArguments(
                                "round-0-call-0.txt",
                                "first")),
                        new FunctionCallContent(
                            string.Empty,
                            ToolRiskClassifier.ApplyPatchToolName,
                            CreateFilePatchArguments(
                                "round-0-call-1.txt",
                                "second")),
                    ])));
        chat.EnqueueToolCall(
            ToolRiskClassifier.ApplyPatchToolName,
            "round-1-call-0",
            CreateFilePatchArguments(
                "round-1-call-0.txt",
                "third"));
        chat.EnqueueText("all patched");
        FakeMcpConnectionManager mcp = new();
        mcp.Tools.Add(CreateProductionApplyPatchTool(settings));
        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            settings,
            grimoire,
            mcp: mcp);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "three patches",
                WorkingDirectory = _workspace.Root,
                SkipSpellRouting = true,
            },
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value.ToolCalls?.Count);
        Assert.Equal(3, grimoire.MandatoryInteractions.Count);
        Assert.Equal(0, grimoire.AppendToolInteractionCallCount);
        Guid assistantEntryId = Assert.IsType<Guid>(
            grimoire.LastAssistantEntryId);
        (string? ProviderCallId, int Round, int Call)[] expectedIdentity =
        [
            ("round-0-call-0", 0, 0),
            (null, 0, 1),
            ("round-1-call-0", 1, 0),
        ];

        for (int index = 0; index < expectedIdentity.Length; index++)
        {
            (string? providerCallId, int round, int call) =
                expectedIdentity[index];
            MandatoryToolInteraction interaction =
                grimoire.MandatoryInteractions[index];
            ToolInteractionReceipt expectedReceipt =
                ToolInteractionReceiptDerivation.Derive(
                    new ToolInvocationIdentity(
                        assistantEntryId.ToString("D"),
                        providerCallId,
                        round,
                        call,
                        ToolRiskClassifier.ApplyPatchToolName));

            Assert.Equal(expectedReceipt, interaction.Receipt);
            Assert.Equal(providerCallId, interaction.ToolCallId);
        }

        static Dictionary<string, object?> CreateFilePatchArguments(
            string path,
            string content) =>
            new()
            {
                ["patch"] =
                    $"--- /dev/null\n+++ b/{path}\n@@ -0,0 +1 @@\n+{content}\n",
                ["dryRun"] = false,
            };

    }

    [Fact]
    public async Task Apply_patch_streaming_recovered_receipt_reuses_exact_result_once()
    {

        const string relativePath = "streaming-production-patch.txt";
        const string replacement = "streaming exact result";
        const string providerCallId = "provider-stream-patch";
        Guid sessionId = Guid.NewGuid();
        ArcanumSettings settings = DefaultSettings();
        FakeGrimoireRepository grimoire = new()
        {
            FixedSessionId = sessionId,
            MandatoryAppendOutcome =
                MandatoryToolInteractionAppendOutcome.RecoveredCommitted,
        };
        ScriptingChatClient chat = new();
        chat.EnqueueStreamToolCall(
            ToolRiskClassifier.ApplyPatchToolName,
            providerCallId,
            new Dictionary<string, object?>
            {
                ["patch"] =
                    $"--- /dev/null\n+++ b/{relativePath}\n@@ -0,0 +1 @@\n+{replacement}\n",
                ["dryRun"] = false,
            });
        chat.EnqueueStreamTokens("patched");
        FakeMcpConnectionManager mcp = new();
        mcp.Tools.Add(CreateProductionApplyPatchTool(settings));
        FakeInferenceAuditLogger auditLogger = new();
        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            settings,
            grimoire,
            mcp: mcp,
            auditLogger: auditLogger);
        InferenceAuditContext auditContext = new()
        {
            RequestType = "unit6-streaming-patch",
        };

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with
            {
                Prompt = "patch",
                WorkingDirectory = _workspace.Root,
                SkipSpellRouting = true,
            },
            auditContext);

        IntelligenceEvent toolResult = Assert.Single(
            events,
            static evt => evt.Type == IntelligenceEventType.ToolResult);
        MandatoryToolInteraction persisted =
            Assert.Single(grimoire.MandatoryInteractions);
        Guid assistantEntryId = Assert.IsType<Guid>(
            grimoire.LastAssistantEntryId);
        ToolInteractionReceipt expectedReceipt =
            ToolInteractionReceiptDerivation.Derive(
                new ToolInvocationIdentity(
                    assistantEntryId.ToString("D"),
                    providerCallId,
                    ToolRoundOrdinal: 0,
                    CallOrdinal: 0,
                    ToolRiskClassifier.ApplyPatchToolName));

        Assert.Equal(expectedReceipt, persisted.Receipt);
        Assert.Equal(providerCallId, persisted.ToolCallId);
        Assert.Equal(
            System.Text.Encoding.UTF8.GetBytes(persisted.Result),
            System.Text.Encoding.UTF8.GetBytes(toolResult.Data!));
        Assert.Equal(persisted.Arguments, toolResult.ToolCall?.ArgumentsJson);
        Assert.Equal(
            System.Text.Encoding.UTF8.GetBytes(persisted.Result),
            System.Text.Encoding.UTF8.GetBytes(
                GetMessageText(
                    chat.AllStreamingCalls[1].Single(
                        static message => message.Role == ChatRole.Tool))));
        Assert.Equal(0, grimoire.AppendToolInteractionCallCount);
        Assert.Equal(
            replacement + "\n",
            await File.ReadAllTextAsync(
                Path.Combine(_workspace.Root, relativePath)));
        Assert.Equal(
            [ToolRiskClassifier.ApplyPatchToolName],
            auditContext.ToolNames);
        Assert.Equal([persisted.Arguments], auditContext.ToolArgumentsJson);
        InferenceAuditRecord audit = Assert.Single(auditLogger.Records);
        Assert.Null(audit.ToolArgumentsJson);

    }

    [Fact]
    public async Task Apply_patch_failed_receipt_rolls_back_and_continues_with_failure_result()
    {

        const string relativePath = "failed-production-patch.txt";
        _workspace.WriteFile(relativePath, "before\n");
        ArcanumSettings settings = DefaultSettings();
        FakeGrimoireRepository grimoire = new()
        {
            FixedSessionId = Guid.NewGuid(),
            MandatoryAppendOutcome =
                MandatoryToolInteractionAppendOutcome.Failed,
        };
        ScriptingChatClient chat = new();
        chat.EnqueueToolCall(
            ToolRiskClassifier.ApplyPatchToolName,
            "failed-provider-call",
            new Dictionary<string, object?>
            {
                ["patch"] =
                    $"--- a/{relativePath}\n+++ b/{relativePath}\n@@ -1 +1 @@\n-before\n+after\n",
                ["dryRun"] = false,
            });
        chat.EnqueueText("continued after receipt failure");
        FakeMcpConnectionManager mcp = new();
        mcp.Tools.Add(CreateProductionApplyPatchTool(settings));
        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            settings,
            grimoire,
            mcp: mcp);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "patch",
                WorkingDirectory = _workspace.Root,
                SkipSpellRouting = true,
            },
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(grimoire.MandatoryInteractions);
        Assert.Equal(1, grimoire.AppendToolInteractionCallCount);
        Assert.Equal(
            "before\n",
            await File.ReadAllTextAsync(
                Path.Combine(_workspace.Root, relativePath)));
        string modelResult = GetMessageText(
            chat.AllBufferedCalls[1].Single(
                static message => message.Role == ChatRole.Tool));
        using JsonDocument payload = JsonDocument.Parse(modelResult);
        Assert.Equal(
            "conflict",
            payload.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "receipt_failed",
            payload.RootElement.GetProperty("code").GetString());

    }

    [Fact]
    public async Task Apply_patch_ambiguous_receipt_is_never_tolerated_as_model_continuation()
    {

        const string relativePath = "ambiguous-production-patch.txt";
        _workspace.WriteFile(relativePath, "before\n");
        ArcanumSettings settings = DefaultSettings();
        FakeGrimoireRepository grimoire = new()
        {
            FixedSessionId = Guid.NewGuid(),
            MandatoryAppendOutcome =
                MandatoryToolInteractionAppendOutcome.Ambiguous,
        };
        ScriptingChatClient chat = new();
        chat.EnqueueToolCall(
            ToolRiskClassifier.ApplyPatchToolName,
            "fatal-patch",
            new Dictionary<string, object?>
            {
                ["patch"] =
                    $"--- a/{relativePath}\n+++ b/{relativePath}\n@@ -1 +1 @@\n-before\n+after\n",
                ["dryRun"] = false,
            });
        chat.EnqueueText("must not continue");
        FakeMcpConnectionManager mcp = new();
        mcp.Tools.Add(CreateProductionApplyPatchTool(settings));
        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            settings,
            grimoire,
            mcp: mcp);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "patch",
                WorkingDirectory = _workspace.Root,
                SkipSpellRouting = true,
            },
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Hub.Error, result.Error.Code);
        Assert.Equal(1, chat.BufferedCallCount);
        Assert.Single(grimoire.MandatoryInteractions);
        Assert.Equal(0, grimoire.AppendToolInteractionCallCount);
        Assert.Equal(
            "after\n",
            await File.ReadAllTextAsync(
                Path.Combine(_workspace.Root, relativePath)));

    }

    [Fact]
    public async Task Apply_patch_post_dispatch_transport_failure_is_never_tolerated_as_model_continuation()
    {

        ArcanumSettings settings = DefaultSettings();
        FakeGrimoireRepository grimoire = new()
        {
            FixedSessionId = Guid.NewGuid(),
        };
        ScriptingChatClient chat = new();
        chat.EnqueueToolCall(
            ToolRiskClassifier.ApplyPatchToolName,
            "post-dispatch-timeout",
            new Dictionary<string, object?>
            {
                ["patch"] =
                    "--- /dev/null\n"
                    + "+++ b/must-not-run-twice.txt\n"
                    + "@@ -0,0 +1 @@\n"
                    + "+value\n",
                ["dryRun"] = false,
            });
        chat.EnqueueText("must not continue");
        FakeMcpConnectionManager mcp = new();
        mcp.Tools.Add(
            CreatePostDispatchFailingApplyPatchTool());
        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            settings,
            grimoire,
            mcp: mcp);

        Result<PromptTurnResult> result =
            await wizard.ExecutePromptAsync(
                BaseRequest() with
                {
                    Prompt = "patch",
                    WorkingDirectory = _workspace.Root,
                    SkipSpellRouting = true,
                },
                InvocationContexts.AttendedSession(),
                CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Hub.Error, result.Error.Code);
        Assert.Equal(1, chat.BufferedCallCount);
        Assert.Empty(grimoire.MandatoryInteractions);
        Assert.Equal(
            0,
            grimoire.AppendToolInteractionCallCount);

    }

    [Fact]
    public async Task Apply_patch_observation_is_not_persisted_when_continuation_fails()
    {

        const string relativePath = "audit-boundary-patch.txt";
        ArcanumSettings settings = DefaultSettings();
        FakeGrimoireRepository grimoire = new()
        {
            FixedSessionId = Guid.NewGuid(),
            MandatoryAppendOutcome =
                MandatoryToolInteractionAppendOutcome.NewlyCommitted,
        };
        ScriptingChatClient chat = new();
        chat.EnqueueToolCall(
            ToolRiskClassifier.ApplyPatchToolName,
            "audit-boundary-call",
            new Dictionary<string, object?>
            {
                ["patch"] =
                    $"--- /dev/null\n+++ b/{relativePath}\n@@ -0,0 +1 @@\n+committed\n",
                ["dryRun"] = false,
            });
        chat.EnqueueException(new InvalidOperationException(
            "model continuation failed"));
        FakeMcpConnectionManager mcp = new();
        mcp.Tools.Add(CreateProductionApplyPatchTool(settings));
        FakeInferenceAuditLogger auditLogger = new();
        InferenceAuditContext auditContext = new()
        {
            RequestType = "unit6-audit-boundary",
        };
        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            settings,
            grimoire,
            mcp: mcp,
            auditLogger: auditLogger);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "patch then fail",
                WorkingDirectory = _workspace.Root,
                SkipSpellRouting = true,
            },
            InvocationContexts.AttendedSession(),
            CancellationToken.None,
            auditContext);

        Assert.True(result.IsFailure);
        Assert.Single(grimoire.MandatoryInteractions);
        Assert.Equal(0, grimoire.AppendToolInteractionCallCount);
        Assert.Equal(
            [ToolRiskClassifier.ApplyPatchToolName],
            auditContext.ToolNames);
        Assert.Single(auditContext.ToolArgumentsJson);
        Assert.Empty(auditLogger.Records);
        Assert.Equal(
            "committed\n",
            await File.ReadAllTextAsync(
                Path.Combine(_workspace.Root, relativePath)));

    }

    [Fact]
    public async Task Apply_patch_cancellation_after_handoff_propagates_after_cleanup()
    {

        const string relativePath = "cancel-after-handoff.txt";
        ArcanumSettings settings = DefaultSettings();
        FakeGrimoireRepository grimoire = new()
        {
            FixedSessionId = Guid.NewGuid(),
            MandatoryAppendOutcome =
                MandatoryToolInteractionAppendOutcome.NewlyCommitted,
        };
        ScriptingChatClient chat = new();
        chat.EnqueueToolCall(
            ToolRiskClassifier.ApplyPatchToolName,
            "cancel-after-handoff-call",
            new Dictionary<string, object?>
            {
                ["patch"] =
                    $"--- /dev/null\n+++ b/{relativePath}\n@@ -0,0 +1 @@\n+committed\n",
                ["dryRun"] = false,
            });
        using CancellationTokenSource cancellation = new();
        FakeMcpConnectionManager mcp = new();
        mcp.Tools.Add(
            CreateProductionApplyPatchTool(
                settings,
                sink => new CancelAfterHandoffSink(
                    sink,
                    cancellation)));
        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            settings,
            grimoire,
            mcp: mcp);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => wizard.ExecutePromptAsync(
                BaseRequest() with
                {
                    Prompt = "patch then cancel",
                    WorkingDirectory = _workspace.Root,
                    SkipSpellRouting = true,
                },
                InvocationContexts.AttendedSession(),
                cancellation.Token));

        Assert.Single(grimoire.MandatoryInteractions);
        Assert.Equal(0, grimoire.AppendToolInteractionCallCount);
        Assert.Equal(
            "committed\n",
            await File.ReadAllTextAsync(
                Path.Combine(_workspace.Root, relativePath)));
        Assert.Empty(Directory.GetFiles(
            _workspace.Root,
            "*.arcanum-*",
            SearchOption.AllDirectories));

    }

    [Fact]
    public async Task Apply_patch_snapshot_security_denial_precedes_rebuilt_handler_mutation()
    {

        const string relativePath = "reload-race.txt";
        _workspace.WriteFile(relativePath, "before\n");
        string replacement = new('x', 1536);
        string patch =
            $"--- a/{relativePath}\n"
            + $"+++ b/{relativePath}\n"
            + "@@ -1 +1 @@\n"
            + "-before\n"
            + $"+{replacement}\n";
        bool handlerInvoked = false;
        ArcanumSettings snapshotSettings = DefaultSettings();
        Campaign campaign = BuildSanctumCampaign(
            _workspace.Root,
            enabled: true,
            SanctumMode.Strict);
        ConfigurableSanctumGuard sanctum = new()
        {
            PathValidator = (_, _, _, _, _) =>
                Task.FromResult(
                    new SanctumResult
                    {
                        Allowed = false,
                        DenyReason = "strict patch path denial",
                        Breach = new SanctumBreach
                        {
                            BreachType = "PathViolation",
                        },
                    }),
        };
        ScriptingChatClient chat = new();
        chat.EnqueueToolCall(
            ToolRiskClassifier.ApplyPatchToolName,
            "reload-race-call",
            new Dictionary<string, object?>
            {
                ["patch"] = patch,
                ["dryRun"] = false,
            });
        chat.EnqueueText("blocked");
        FakeMcpConnectionManager mcp = new();
        mcp.Tools.Add(
            AIFunctionFactory.Create(
                ApplyWithRebuiltSettings,
                ToolRiskClassifier.ApplyPatchToolName,
                "simulated rebuilt apply_patch handler"));
        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            snapshotSettings,
            campaignRepository: new FakeCampaignRepository(campaign),
            sanctumGuard: sanctum,
            mcp: mcp);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "apply the large patch",
                WorkingDirectory = _workspace.Root,
                SkipSpellRouting = true,
            },
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(handlerInvoked);
        Assert.Equal("before\n", await File.ReadAllTextAsync(
            Path.Combine(_workspace.Root, relativePath)));
        Assert.Contains(
            chat.AllBufferedCalls.SelectMany(static batch => batch),
            static message => message.Role == ChatRole.Tool
                && GetMessageText(message).Contains(
                    "Sanctum Guard has blocked",
                    StringComparison.Ordinal));

        string ApplyWithRebuiltSettings()
        {
            handlerInvoked = true;
            _workspace.WriteFile(relativePath, replacement + "\n");
            ApplyPatchInvocationAmbient.Current?.RecordHandoffOutcome(
                MandatoryToolInteractionAppendOutcome.NewlyCommitted);

            return "{\"status\":\"ok\"}";
        }
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
            InvocationContexts.AttendedSession(),
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
            InvocationContexts.AttendedSession(),
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
            InvocationContexts.AttendedSession(),
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
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.DoesNotContain(
            chat.AllBufferedCalls.SelectMany(static batch => batch),
            static m => m.Role == ChatRole.Tool
                && GetMessageText(m).Contains("Sanctum Guard has blocked", StringComparison.Ordinal));

    }

    [Fact]
    public async Task Scenario53_NoForbiddenArtsPolicy_ExcludesOperatorConfiguredTools()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueText("no forbidden");

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(CreateMcpTool("read_file_chunk"));

        mcp.Tools.Add(CreateMcpTool("execute_command"));

        mcp.Tools.Add(CreateMcpTool("apply_patch"));

        mcp.Tools.Add(CreateMcpTool("workspace_check"));

        mcp.Tools.Add(CreateMcpTool("search_workspace"));

        ArcanumSettings settings = DefaultSettings();

        settings.Security.Ward.ForbiddenArts =
        [
            "execute_command",
            "apply_patch",
            "workspace_check",
        ];

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, mcp: mcp);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "tools",
                SkipSpellRouting = true,
                ToolPolicy = ToolPolicy.NoForbiddenArts,
            },
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        HashSet<string> toolNames = ToolNames(chat.LastChatOptions);

        Assert.Contains("read_file_chunk", toolNames);

        Assert.DoesNotContain("execute_command", toolNames);

        Assert.DoesNotContain("apply_patch", toolNames);

        Assert.DoesNotContain("workspace_check", toolNames);

        Assert.Contains("search_workspace", toolNames);

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
            InvocationContexts.AttendedSession(),
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
            InvocationContexts.AttendedSession(),
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
    public async Task StreamingAskHuman_WithoutLiveChannel_KeepsDenialTextOutOfArgumentsJson()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueStreamToolCall(
            "ask_human",
            callId: "call_ask_denied",
            arguments: new Dictionary<string, object?>
            {
                ["question"] = "What is the passphrase?",
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
                UnattendedMode = true,
            });

        // toolCall.argumentsJson is the serialized call arguments on every frame — the denial text
        // is the human-readable failure and belongs on Data, not in the arguments position.
        IntelligenceEvent toolError = Assert.Single(
            events,
            static e => e.Type == IntelligenceEventType.ToolError);

        Assert.NotNull(toolError.ToolCall);

        Assert.Contains(
            "What is the passphrase?",
            toolError.ToolCall.ArgumentsJson,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "only available during attended",
            toolError.ToolCall.ArgumentsJson,
            StringComparison.Ordinal);

        Assert.Contains(
            "only available during attended",
            toolError.Data ?? string.Empty,
            StringComparison.Ordinal);

        IntelligenceEvent toolResult = Assert.Single(
            events,
            static e => e.Type == IntelligenceEventType.ToolResult);

        Assert.NotNull(toolResult.ToolCall);

        Assert.Contains(
            "What is the passphrase?",
            toolResult.ToolCall.ArgumentsJson,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "only available during attended",
            toolResult.ToolCall.ArgumentsJson,
            StringComparison.Ordinal);

        Assert.Contains(
            "only available during attended",
            toolResult.Data ?? string.Empty,
            StringComparison.Ordinal);

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
    public async Task Scenario56_AttunementExecuteCommand_IsAdvertisedExecutesAndRecordsUngated()
    {
        string? previousAllow = global::System.Environment.GetEnvironmentVariable(HostProcessToolPolicy.AllowHostProcessToolsEnvVar);

        string? previousEdition = global::System.Environment.GetEnvironmentVariable("ARCANUM_EDITION");

        try
        {
            global::System.Environment.SetEnvironmentVariable(HostProcessToolPolicy.AllowHostProcessToolsEnvVar, "1");

            global::System.Environment.SetEnvironmentVariable("ARCANUM_EDITION", "development");

            await CreateSpellWithDeclaredToolsAsync("exec-spell", ["execute_command"]);

            ScriptingChatClient chat = new();

            chat.EnqueueStreamToolCall("execute_command");

            chat.EnqueueStreamTokens("done");

            FakeWard ward = new();

            FakeMcpConnectionManager mcp = new();

            mcp.Tools.Add(CreateMcpTool("execute_command"));

            ArcanumSettings settings = DefaultSettings() with { Edition = ArcanumEdition.Development };

            WizardIntelligenceProvider wizard = CreateWizard(chat, settings, ward: ward, mcp: mcp);

            List<IntelligenceEvent> events = await CollectStreamAsync(
                wizard,
                BaseRequest() with
                {
                    Prompt = "run",
                    WorkingDirectory = _workspace.Root,
                    OverrideSpellName = "exec-spell",
                    SkipSpellRouting = false,
                    UnattendedMode = false,
                });

            HashSet<string> toolNames = ToolNames(chat.LastChatOptions);

            Assert.Contains("execute_command", toolNames);

            IntelligenceEvent warded = Assert.Single(
                events,
                static evt => evt.Type == IntelligenceEventType.Warded);

            IntelligenceEvent resolved = Assert.Single(
                events,
                static evt => evt.Type == IntelligenceEventType.WardResolved);

            IntelligenceEvent toolResult = Assert.Single(
                events,
                static evt => evt.Type == IntelligenceEventType.ToolResult);

            Assert.Equal(WardResolutionOrigin.Ungated, warded.WardOrigin);

            Assert.Equal(WardResolutionOrigin.Ungated, resolved.WardOrigin);

            Assert.Equal(warded.WardId, resolved.WardId);

            Assert.True(resolved.WardAllowed);

            Assert.False(toolResult.ToolDenied);

            Assert.Equal("ok", toolResult.Data);

            Assert.Equal(0, ward.WardCallCount);

            Assert.Equal([WardResolutionOrigin.Ungated], ward.AutomaticResolutionOrigins);
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
    public async Task Streaming_tool_interaction_is_persisted_before_tool_result_can_be_disposed()
    {

        Guid sessionId = Guid.NewGuid();
        TaskCompletionSource appendStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseAppend = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        FakeGrimoireRepository grimoire = new()
        {
            FixedSessionId = sessionId,
            AppendToolInteractionHandler = async cancellationToken =>
            {
                appendStarted.TrySetResult();
                await releaseAppend.Task.WaitAsync(
                    cancellationToken);
            },
        };
        ScriptingChatClient chat = new();
        chat.EnqueueStreamToolCall(
            ArcanumLocalTimeTool.ToolName);
        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            grimoire: grimoire);
        IAsyncEnumerator<IntelligenceEvent> enumerator = wizard
            .StreamPromptAsync(
                BaseRequest() with
                {
                    Prompt = "what time?",
                    SessionId = sessionId,
                    SkipSpellRouting = true,
                    DisableMcpTools = true,
                },
                InvocationContexts.AttendedSession(),
                CancellationToken.None)
            .GetAsyncEnumerator();
        Task<IntelligenceEvent> toolResult = ReadUntilToolResultAsync(
            enumerator);

        try
        {
            await appendStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(5));
            await Task.Delay(50);
            Assert.False(toolResult.IsCompleted);

            releaseAppend.TrySetResult();
            IntelligenceEvent observed =
                await toolResult.WaitAsync(
                    TimeSpan.FromSeconds(5));
            Assert.Equal(
                IntelligenceEventType.ToolResult,
                observed.Type);
        }
        finally
        {
            releaseAppend.TrySetResult();
            await enumerator.DisposeAsync();
        }

        Assert.Equal(
            1,
            grimoire.AppendToolInteractionCallCount);

        static async Task<IntelligenceEvent> ReadUntilToolResultAsync(
            IAsyncEnumerator<IntelligenceEvent> source)
        {
            while (await source.MoveNextAsync())
            {
                if (source.Current.Type
                    == IntelligenceEventType.ToolResult)
                {
                    return source.Current;
                }
            }

            throw new InvalidOperationException(
                "The stream ended before a tool result.");
        }

    }

    [Fact]
    public async Task LoopContract_BufferedToolInvocationFailure_DefaultTolerates_ReturnsSyntheticResultAndContinues()
    {

        // Buffered tool failures are automatically tolerated: a throwing tool is caught and
        // synthesized into a tool result the model can see and react to, matching streaming.
        ScriptingChatClient chat = new();

        chat.EnqueueToolCall("failing_tool");

        chat.EnqueueText("recovered after tool failure");

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(CreateThrowingMcpTool("failing_tool"));

        WizardIntelligenceProvider wizard = CreateWizard(chat, mcp: mcp);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "tool fail", SkipSpellRouting = true },
            InvocationContexts.AttendedSession(),
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
            InvocationContexts.AttendedSession(),
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
            InvocationContexts.AttendedSession(),
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
            InvocationContexts.AttendedSession(),
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
            InvocationContexts.AttendedSession(),
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

        ArcanumSettings settings = DefaultSettings();

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, grimoire);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "time then answer",
                SessionId = sessionId,
                SkipSpellRouting = true,
                DisableMcpTools = true,
            },
            InvocationContexts.AttendedSession(),
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
            InvocationContexts.AttendedSession(),
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
            InvocationContexts.AttendedSession(),
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
            InvocationContexts.AttendedSession(),
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
            InvocationContexts.AttendedSession(),
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
            Cost = new CostSettings
            {
                Pricing = new PricingSettings
                {
                    DefaultPricing = new ModelPricingEntry
                    {
                        OutputPer1M = 20m,
                        ReasoningPer1M = 80m,
                    },
                },
            },
        };
        settings.Providers[0].Models[0].Reasoning = new ModelReasoningSettings
        {
            WireDialect = ReasoningWireDialect.OpenRouter,
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
                Prompt = "budget",
                SkipSpellRouting = true,
                DisableMcpTools = true,
                MaxOutputTokens = 1_000,
                Reasoning = new ReasoningRequestOptions(BudgetTokens: 600),
            },
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        BudgetReservationRequest request = Assert.IsType<BudgetReservationRequest>(reservations.LastRequest);
        Assert.Equal(
            BudgetReservationService.EstimateWorstCaseTurnUsd(
                settings.Cost.Pricing.DefaultPricing,
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
            DefaultProvider() with
            {
                ContextWindowLimit = messageTokens + maxOutputTokens + reasoningBudgetTokens,
            },
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
            DefaultProvider() with
            {
                ContextWindowLimit = messageTokens + maxOutputTokens + reasoningBudgetTokens - 1,
            },
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

    public async Task ExplicitAttachmentReferences_StopMaterializingAtResolvedProviderContextBoundary()
    {

        Guid sessionId = Guid.NewGuid();

        string payload = string.Concat(
            Enumerable.Repeat(
                "alpha beta gamma delta epsilon zeta eta theta ",
                300));

        Guid[] attachmentIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];

        Dictionary<Guid, SessionAttachmentRecord> records = attachmentIds.ToDictionary(
            static id => id,
            id => new SessionAttachmentRecord(
                id,
                sessionId,
                EntryId: null,
                PendingTurnId: null,
                SessionAttachmentState.Bound,
                $"notes-{Array.IndexOf(attachmentIds, id) + 1}",
                $"notes-{Array.IndexOf(attachmentIds, id) + 1}.txt",
                Version: 1,
                RelativePath: $"noop/{id:N}",
                ContentSha256: id.ToString("N"),
                MimeType: "text/plain",
                ByteLength: System.Text.Encoding.UTF8.GetByteCount(payload),
                SessionAttachmentKind.Text,
                DateTimeOffset.UtcNow));

        int materializedReferences = 0;

        NoOpSessionAttachmentStore store = new(
            records: records,
            readBytes: (_, _) =>
            {

                materializedReferences++;

                return Task.FromResult<ReadOnlyMemory<byte>>(
                    System.Text.Encoding.UTF8.GetBytes(payload));

            },
            openRead: (_, _) =>
            {

                materializedReferences++;

                return Task.FromResult<Stream>(new MemoryStream(
                    System.Text.Encoding.UTF8.GetBytes(payload),
                    writable: false));

            });

        ArcanumSettings settings = DefaultSettings() with
        {

            Providers =
            [

                DefaultProvider() with { ContextWindowLimit = 4_096 },

            ],

        };

        ScriptingChatClient chat = new();

        chat.EnqueueText("must not be called");

        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            settings,
            grimoire: new FakeGrimoireRepository
            {

                Session = new Session { Id = sessionId, Entries = [] },

            },
            sessionAttachmentStore: store);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {

                Prompt = "Compare the referenced notes.",

                SessionId = sessionId,

                AttachmentReferences = [.. attachmentIds],

                SkipSpellRouting = true,

                DisableMcpTools = true,

                MaxOutputTokens = 128,

            },
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Hub.ContextBudgetExceeded, result.Error.Code);

        Assert.InRange(materializedReferences, 1, attachmentIds.Length - 1);

        Assert.Equal(0, chat.BufferedCallCount);

    }

    [Fact]

    public async Task ExplicitAttachmentReferences_CancellationStopsLaterMaterializationAndProviderCall()
    {

        Guid sessionId = Guid.NewGuid();

        Guid[] attachmentIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];

        byte[] payload = System.Text.Encoding.UTF8.GetBytes("small attachment body");

        Dictionary<Guid, SessionAttachmentRecord> records = attachmentIds.ToDictionary(
            static id => id,
            id => new SessionAttachmentRecord(
                id,
                sessionId,
                EntryId: null,
                PendingTurnId: null,
                SessionAttachmentState.Bound,
                $"notes-{Array.IndexOf(attachmentIds, id) + 1}",
                $"notes-{Array.IndexOf(attachmentIds, id) + 1}.txt",
                Version: 1,
                RelativePath: $"noop/{id:N}",
                ContentSha256: id.ToString("N"),
                MimeType: "text/plain",
                ByteLength: payload.Length,
                SessionAttachmentKind.Text,
                DateTimeOffset.UtcNow));

        using CancellationTokenSource cancellation = new();

        List<Guid> openedAttachmentIds = [];

        NoOpSessionAttachmentStore store = new(
            records: records,
            openRead: (record, cancellationToken) =>
            {

                openedAttachmentIds.Add(record.Id);

                if (record.Id == attachmentIds[1])
                {

                    cancellation.Cancel();

                    cancellationToken.ThrowIfCancellationRequested();

                }

                return Task.FromResult<Stream>(new MemoryStream(payload, writable: false));

            });

        ScriptingChatClient chat = new();

        chat.EnqueueText("must not be called");

        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            grimoire: new FakeGrimoireRepository
            {

                Session = new Session { Id = sessionId, Entries = [] },

            },
            sessionAttachmentStore: store);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            wizard.ExecutePromptAsync(
                BaseRequest() with
                {

                    Prompt = "Compare the referenced notes.",

                    SessionId = sessionId,

                    AttachmentReferences = [.. attachmentIds],

                    SkipSpellRouting = true,

                    DisableMcpTools = true,

                },
                InvocationContexts.AttendedSession(),
                cancellation.Token));

        Assert.Equal(attachmentIds[..2], openedAttachmentIds);

        Assert.Equal(0, chat.BufferedCallCount);

    }

    [Fact]

    public async Task ExplicitAttachmentReferences_MaterializeInRequestOrderWithProvenance()
    {

        Guid sessionId = Guid.NewGuid();

        Guid[] attachmentIds = [Guid.NewGuid(), Guid.NewGuid()];

        Dictionary<Guid, byte[]> payloads = new()
        {

            [attachmentIds[0]] = System.Text.Encoding.UTF8.GetBytes("first attachment body"),

            [attachmentIds[1]] = System.Text.Encoding.UTF8.GetBytes("second attachment body"),

        };

        Dictionary<Guid, SessionAttachmentRecord> records = attachmentIds.ToDictionary(
            static id => id,
            id => new SessionAttachmentRecord(
                id,
                sessionId,
                EntryId: null,
                PendingTurnId: null,
                SessionAttachmentState.Bound,
                $"notes-{Array.IndexOf(attachmentIds, id) + 1}",
                $"notes-{Array.IndexOf(attachmentIds, id) + 1}.txt",
                Version: 1,
                RelativePath: $"noop/{id:N}",
                ContentSha256: id.ToString("N"),
                MimeType: "text/plain",
                ByteLength: payloads[id].Length,
                SessionAttachmentKind.Text,
                DateTimeOffset.UtcNow));

        List<Guid> openedAttachmentIds = [];

        NoOpSessionAttachmentStore store = new(
            records: records,
            openRead: (record, _) =>
            {

                openedAttachmentIds.Add(record.Id);

                return Task.FromResult<Stream>(new MemoryStream(
                    payloads[record.Id],
                    writable: false));

            });

        ScriptingChatClient chat = new();

        chat.EnqueueText("compared");

        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            grimoire: new FakeGrimoireRepository
            {

                Session = new Session { Id = sessionId, Entries = [] },

            },
            sessionAttachmentStore: store);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {

                Prompt = "Compare the referenced notes.",

                SessionId = sessionId,

                AttachmentReferences = [.. attachmentIds],

                SkipSpellRouting = true,

                DisableMcpTools = true,

            },
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(attachmentIds, openedAttachmentIds);

        MeAiChatMessage userMessage = chat.LastBufferedMessages.Last(
            static message => message.Role == ChatRole.User);

        TextContent[] explicitContents = userMessage.Contents
            .OfType<TextContent>()
            .Where(static content => content.AdditionalProperties is not null
                && content.AdditionalProperties.TryGetValue(
                    "arcanum.context_source",
                    out object? source)
                && string.Equals(source as string, "explicitAttachment", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(2, explicitContents.Length);

        Assert.Contains("first attachment body", explicitContents[0].Text, StringComparison.Ordinal);

        Assert.Contains("second attachment body", explicitContents[1].Text, StringComparison.Ordinal);

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
            Cost = new CostSettings
            {
                Pricing = new PricingSettings
                {
                    DefaultPricing = new ModelPricingEntry
                    {
                        OutputPer1M = 20m,
                        ReasoningPer1M = 80m,
                    },
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
            InvocationContexts.AttendedSession(),
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
    public async Task AccountingReconciliation_PersistsExplicitAllZeroUsage()
    {
        ChatResponse response = new(new MeAiChatMessage(ChatRole.Assistant, "answer"))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 0,
                OutputTokenCount = 0,
                TotalTokenCount = 0,
                CachedInputTokenCount = 0,
                ReasoningTokenCount = 0,
            },
        };
        ScriptingChatClient chat = new();
        chat.EnqueueResponse(response);
        RecordingTurnRunWriter turnRuns = new();
        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            turnRunWriter: turnRuns);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "zero usage",
                SkipSpellRouting = true,
                DisableMcpTools = true,
            },
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        BillableOperationRecord operation = Assert.Single(turnRuns.Operations);
        Assert.Equal(0, operation.InputTokens);
        Assert.Equal(0, operation.OutputTokens);
        Assert.Equal(0, operation.CachedTokens);
        Assert.Equal(0m, operation.ActualCostUsd);
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
        ArcanumSettings settings = ConfigureGuardrails(
            DefaultSettings(),
            enabled: true,
            detectPii: false,
            blockToxicity: true,
            toxicityBlocklist: ["bad-word"]) with
        {
            Cost = new CostSettings
            {
                Pricing = new PricingSettings
                {
                    DefaultPricing = new ModelPricingEntry { InputPer1M = 1m, OutputPer1M = 2m },
                },
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
            InvocationContexts.AttendedSession(),
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
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Collection(
            turnRuns.Operations,
            first => Assert.Equal((10L, 2L), (first.InputTokens, first.OutputTokens)),
            second => Assert.Equal((15L, 3L), (second.InputTokens, second.OutputTokens)));
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
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        BillableOperationRecord operation = Assert.Single(turnRuns.Operations);
        Assert.Equal((10L, 5L), (operation.InputTokens, operation.OutputTokens));
        Assert.Equal(1, reservations.ReconcileCount);
        Assert.Equal(operation.ActualCostUsd, reservations.ReconciledUsd);
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
            InvocationContexts.AttendedSession(),
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
            Cost = new CostSettings
            {
                Pricing = new PricingSettings
                {
                    DefaultPricing = new ModelPricingEntry { InputPer1M = 1m, OutputPer1M = 2m },
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
                Prompt = "route this",
                WorkingDirectory = _workspace.Root,
                SkipSpellRouting = false,
                DisableMcpTools = true,
            },
            InvocationContexts.AttendedSession(),
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
            Cost = new CostSettings
            {
                Pricing = new PricingSettings
                {
                    DefaultPricing = new ModelPricingEntry { InputPer1M = 1m, OutputPer1M = 2m },
                },
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
                InvocationContexts.AttendedSession(),
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
            Features = DefaultSettings().Features with { Lexicon = true },
            Cost = new CostSettings
            {
                Pricing = new PricingSettings
                {
                    DefaultPricing = new ModelPricingEntry { InputPer1M = 1m, OutputPer1M = 2m },
                },
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
                InvocationContexts.AttendedSession(),
                callerCancellation.Token));

        BillableOperationRecord operation = Assert.Single(turnRuns.Operations);
        Assert.True(await WaitUntilAsync(() => reservations.ReconcileCount == 1, TimeSpan.FromSeconds(5)));
        Assert.Equal(BillableOperationType.Extraction, operation.OperationType);
        Assert.Equal(operation.ActualCostUsd, reservations.ReconciledUsd);
        Assert.Equal(1, reservations.ReconcileCount);
        Assert.False(reservations.WasReleased);
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
            Features = DefaultSettings().Features with
            {
                Embeddings = true,
                CodebaseRetrieval = true,
            },
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
            InvocationContexts.AttendedSession(),
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
            Features = DefaultSettings().Features with
            {
                Embeddings = true,
                CodebaseRetrieval = true,
            },
        };

        ScriptingChatClient chat = new();

        chat.EnqueueText("buffered answer");

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, weaveService: weave);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "hello", SkipSpellRouting = true, DisableMcpTools = true, WorkingDirectory = _workspace.Root },
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        string systemPrompt = ExtractSystemPromptText(chat.LastBufferedMessages);

        Assert.DoesNotContain("Semantic Context", systemPrompt, StringComparison.Ordinal);

    }

    [Fact]

    public async Task AttachmentRag_BufferedAndStreaming_UseSameSessionScopedMaterialization()
    {

        Guid sessionId = Guid.NewGuid();

        Guid attachmentId = Guid.NewGuid();

        SessionAttachmentRetrievedChunk chunk = new(
            "chunk-1",
            sessionId,
            attachmentId,
            "notes",
            1,
            "notes.md",
            "text/markdown",
            "whole-hash",
            0,
            0,
            24,
            1,
            2,
            "attachment semantic fact",
            0.94f);

        FakeSessionAttachmentRetrieval retrieval = new([chunk]);

        FakeRagWeaveService weave = new() { Available = true };

        ArcanumSettings settings = DefaultSettings() with
        {

            Features = DefaultSettings().Features with
            {

                Embeddings = true,

                AttachmentRetrieval = true,

            },

        };

        ScriptingChatClient bufferedChat = new();

        bufferedChat.EnqueueText("buffered answer");

        Result<PromptTurnResult> buffered = await CreateWizard(
                bufferedChat,
                settings,
                weaveService: weave,
                sessionAttachmentRetrieval: retrieval)
            .ExecutePromptAsync(
                BaseRequest() with
                {

                    SessionId = sessionId,

                    Prompt = "find the note",

                    SkipSpellRouting = true,

                    DisableMcpTools = true,

                },
                InvocationContexts.AttendedSession(),
                CancellationToken.None);

        ScriptingChatClient streamingChat = new();

        streamingChat.EnqueueStreamTokens("streamed answer");

        List<IntelligenceEvent> streamed = await CollectStreamAsync(
            CreateWizard(
                streamingChat,
                settings,
                weaveService: weave,
                sessionAttachmentRetrieval: retrieval),
            BaseRequest() with
            {

                SessionId = sessionId,

                Prompt = "find the note",

                SkipSpellRouting = true,

                DisableMcpTools = true,

            });

        Assert.True(buffered.IsSuccess);

        Assert.Contains(streamed, static item => item.Type == IntelligenceEventType.Result);

        string bufferedSystem = ExtractSystemPromptText(bufferedChat.LastBufferedMessages);

        string streamingSystem = ExtractSystemPromptText(streamingChat.LastBufferedMessages);

        Assert.Equal(bufferedSystem, streamingSystem);

        Assert.Contains("### Retrieved Session Attachment Context", bufferedSystem, StringComparison.Ordinal);

        Assert.Contains("attachment semantic fact", bufferedSystem, StringComparison.Ordinal);

        Assert.Equal([sessionId, sessionId], retrieval.SessionIds);

        Assert.Equal(2, weave.EmbedCallCount);

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
            InvocationContexts.AttendedSession(),
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
            Features = DefaultSettings().Features with
            {
                Embeddings = true,
                CodebaseRetrieval = true,
            },
        };

        ScriptingChatClient chat = new();

        chat.EnqueueText("buffered answer");

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, workspaceIndexingService: indexing);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "hello", SkipSpellRouting = true, DisableMcpTools = true, WorkingDirectory = string.Empty },
            InvocationContexts.AttendedSession(),
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
            Features = DefaultSettings().Features with
            {
                Embeddings = true,
                Saga = true,
            },
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
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        string systemPrompt = ExtractSystemPromptText(chat.LastBufferedMessages);

        Assert.Contains("### Saga (Associative Memory)", systemPrompt, StringComparison.Ordinal);

        Assert.Contains("The operator prefers dark mode.", systemPrompt, StringComparison.Ordinal);

    }

    /// <summary>
    /// The gate's default is the guarantee: with it unset, a turn asks for the same unscoped candidate
    /// set it always has, and the same memory reaches the prompt.
    /// </summary>
    [Fact]
    public async Task ScenarioSaga05_CampaignScopingOff_LeavesTheCandidateSetUnscoped()
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
            Features = DefaultSettings().Features with
            {
                Embeddings = true,
                Saga = true,
            },
        };

        Assert.False(settings.Features.CampaignScopedMemory);

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
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Null(divination.LastCampaignScope);

        Assert.Contains(
            "The operator prefers dark mode.",
            ExtractSystemPromptText(chat.LastBufferedMessages),
            StringComparison.Ordinal);

    }

    /// <summary>
    /// With the gate on, the scope is the Campaign the turn already resolved — never anything the
    /// request carried.
    /// </summary>
    [Fact]
    public async Task ScenarioSaga06_CampaignScopingOn_ScopesToTheTurnsResolvedCampaign()
    {
        Guid campaign = new("3B7C1E90-2A44-4D18-8F65-9C0E1D2A3B4C");

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
            Features = DefaultSettings().Features with
            {
                Embeddings = true,
                Saga = true,
                CampaignScopedMemory = true,
            },
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
            InvocationContexts.AttendedSession(campaign),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.NotNull(divination.LastCampaignScope);

        Assert.Equal(campaign, divination.LastCampaignScope!.CampaignId);

        Assert.Equal(SagaStorageKeys.MemoryTable, divination.LastCampaignScope.OwnerTableName);

    }

    /// <summary>
    /// A turn that resolved to no Campaign asks for the installation-scoped memories alone, rather than
    /// falling back to every memory on the installation.
    /// </summary>
    [Fact]
    public async Task ScenarioSaga07_CampaignScopingOn_WithNoResolvedCampaign_ScopesToGlobalOnly()
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
            Features = DefaultSettings().Features with
            {
                Embeddings = true,
                Saga = true,
                CampaignScopedMemory = true,
            },
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
            InvocationContexts.AttendedGlobalOnlySession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.NotNull(divination.LastCampaignScope);

        Assert.Null(divination.LastCampaignScope!.CampaignId);

    }

    [Fact]
    public async Task ScenarioSaga02_EmbeddingFailure_DegradesGracefully_NoSagaMemories()
    {
        FakeRagWeaveService weave = new() { Available = true, FailEmbed = true };

        ArcanumSettings settings = DefaultSettings() with
        {
            Features = DefaultSettings().Features with
            {
                Embeddings = true,
                Saga = true,
            },
        };

        ScriptingChatClient chat = new();

        chat.EnqueueText("buffered answer");

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, weaveService: weave);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "hello", SkipSpellRouting = true, DisableMcpTools = true, WorkingDirectory = _workspace.Root },
            InvocationContexts.AttendedSession(),
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
            InvocationContexts.AttendedSession(),
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
            Features = DefaultSettings().Features with
            {
                Embeddings = true,
                CodebaseRetrieval = true,
                Saga = true,
            },
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
            InvocationContexts.AttendedSession(),
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
            Features = DefaultSettings().Features with
            {
                Embeddings = true,
                SemanticSpellRouting = true,
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
            InvocationContexts.AttendedSession(),
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
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Scrying.VisionNotSupported, result.Error.Code);

    }

    [Fact]

    public async Task CurrentTurnAttachedFileParts_WithIdenticalBytes_AllReachTheModel()
    {

        ScriptingChatClient chat = new();

        chat.EnqueueText("complete");

        WizardIntelligenceProvider wizard = CreateWizard(chat);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {

                Prompt = "inspect every part",

                SkipSpellRouting = true,

                DisableMcpTools = true,

                AttachedFiles =
                [

                    new AttachedFileDto("repeat.part-0001.txt", "identical chunk"),

                    new AttachedFileDto("repeat.part-0002.txt", "identical chunk"),

                ],

            },
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        string systemPrompt = ExtractSystemPromptText(chat.LastBufferedMessages);

        Assert.Contains("repeat.part-0001.txt", systemPrompt, StringComparison.Ordinal);

        Assert.Contains("repeat.part-0002.txt", systemPrompt, StringComparison.Ordinal);

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
            InvocationContexts.AttendedSession(),
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
            Features = DefaultSettings().Features with { Scrying = false },
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
            InvocationContexts.AttendedSession(),
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
        };
        int maxImages = ArcanumSettingClamps.ScryingMaxImagesPerRequest(
            ArcanumRuntimeDefaults.Scrying.MaxImagesPerRequest);

        WizardIntelligenceProvider wizard = CreateWizard(new ScriptingChatClient(), settings);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "describe these",
                SkipSpellRouting = true,
                DisableMcpTools = true,
                ScryingFoci = Enumerable
                    .Range(0, maxImages + 1)
                    .Select(static index => new ScryingFocusDto(
                        Convert.ToBase64String(new byte[] { (byte)index }),
                        "image/png"))
                    .ToList(),
            },
            InvocationContexts.AttendedSession(),
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
        };
        long maxImageBytes = ArcanumSettingClamps.ScryingMaxImageBytes(
            ArcanumRuntimeDefaults.Scrying.MaxImageBytes);

        WizardIntelligenceProvider wizard = CreateWizard(new ScriptingChatClient(), settings);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "describe this",
                SkipSpellRouting = true,
                DisableMcpTools = true,
                ScryingFoci =
                [
                    new ScryingFocusDto(
                        Convert.ToBase64String(new byte[checked((int)maxImageBytes + 1)]),
                        "image/png"),
                ],
            },
            InvocationContexts.AttendedSession(),
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
            InvocationContexts.AttendedSession(),
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
            InvocationContexts.AttendedSession(),
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

    private sealed class FakeSessionAttachmentRetrieval(
        SessionAttachmentRetrievedChunk[] chunks) : ISessionAttachmentRetrievalService
    {

        public List<Guid> SessionIds { get; } = [];

        public Task<SessionAttachmentRetrievedChunk[]> SearchAsync(
            Guid sessionId,
            Embedding<float> queryEmbedding,
            bool includeHistorical,
            CancellationToken cancellationToken)
        {

            SessionIds.Add(sessionId);

            return Task.FromResult(chunks);

        }

        public Task<IReadOnlyDictionary<Guid, SessionAttachmentIndexStatus>> GetStatusesAsync(
            IReadOnlyList<Guid> attachmentIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, SessionAttachmentIndexStatus>>(
                attachmentIds.ToDictionary(
                    static id => id,
                    static _ => SessionAttachmentIndexStatus.Indexed));

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

        /// <summary>The scope the turn asked for, or null when the turn used the unscoped search.</summary>
        public DivinationCampaignScope? LastCampaignScope { get; private set; }

        public Task<Result<DivinationResult[]>> SearchCampaignScopedAsync(
            string tableName,
            string primaryKeyColumn,
            string embeddingColumn,
            DivinationCampaignScope scope,
            Embedding<float> queryEmbedding,
            int maxResults,
            float similarityThreshold,
            CancellationToken cancellationToken)
        {

            LastCampaignScope = scope;

            return SearchAsync(tableName, primaryKeyColumn, embeddingColumn, queryEmbedding, maxResults, similarityThreshold, cancellationToken);

        }

    }

    private sealed class FakeRagWorkspaceIndexingService : IWorkspaceIndexingService
    {

        public List<string> RegisteredPaths { get; } = [];

        public void RegisterWorkspace(string workspacePath)
        {

            RegisteredPaths.Add(workspacePath);

        }

        public void UnregisterWorkspace(string workspacePath)
        {
        }

        public Task IndexNowAsync(string workspacePath, CancellationToken cancellationToken) => Task.CompletedTask;

    }

    /// <summary>RAG Phase 4 — in-memory <see cref="ISagaMemoryStore"/> fake; no raw SQL needed for hub-level scenario tests.</summary>
    private sealed class FakeSagaMemoryStore : ISagaMemoryStore
    {

        public Dictionary<string, SagaMemoryDto> Memories { get; } = new(StringComparer.Ordinal);

        // Retirement removes the embedding and reinstatement restores it; ReadCurationRowAsync has to
        // answer HasEmbedding from this rather than a hardcoded true, or a memory retired through this
        // fake would read back as retired and still embedded -- a state the real store can never
        // produce, and the opposite of what retirement is for.
        private readonly HashSet<string> _embeddedIds = new(StringComparer.Ordinal);

        // Mirrors the real store's suppression binding -- scope-and-content, not memory identity -- so
        // a fake that always returned Written could not mask the defect this fake exists to catch.
        private readonly HashSet<(SagaMemoryScopeKind ScopeKind, Guid? CampaignId, string Content)> _suppressed = [];

        public Task<SagaMemoryWriteOutcome> InsertAsync(
            string id,
            string content,
            DateTimeOffset createdAt,
            Guid? sessionId,
            string? tags,
            string? source,
            float[] embedding,
            CancellationToken cancellationToken)
        {

            const SagaMemoryScopeKind ScopeKind = SagaMemoryScopeKind.Unclassified;

            if (_suppressed.Contains((ScopeKind, null, content)))
            {

                return Task.FromResult(SagaMemoryWriteOutcome.Suppressed);

            }

            Memories[id] = new SagaMemoryDto(id, content, createdAt, sessionId, tags, source);

            _embeddedIds.Add(id);

            return Task.FromResult(SagaMemoryWriteOutcome.Written);

        }

        public Task<int> CountAsync(CancellationToken cancellationToken) => Task.FromResult(Memories.Count);

        public Task<int> CountBySessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
            Task.FromResult(Memories.Values.Count(m => m.SessionId == sessionId));

        public Task<SagaMemoryDto[]> ListAsync(string? query, Guid? sessionId, MemoryScope scope, int limit, int offset, CancellationToken cancellationToken) =>
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

        public Task<SagaMemoryCurationRow?> ReadCurationRowAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(Memories.TryGetValue(id, out SagaMemoryDto? memory)
                ? new SagaMemoryCurationRow(
                    memory, new SagaMemoryLifecycle(memory.RetiredAtUtc, memory.PinnedAtUtc), _embeddedIds.Contains(id))
                : null);

        public Task<SagaCurationOutcome> RetireAsync(
            string id, byte[] expectedContentDigest, DateTimeOffset retiredAt, CancellationToken cancellationToken)
        {

            if (!Memories.TryGetValue(id, out SagaMemoryDto? memory))
            {

                return Task.FromResult(new SagaCurationOutcome(SagaCurationOutcomeKind.NotFound, null));

            }

            if (memory.RetiredAtUtc is not null)
            {

                return Task.FromResult(new SagaCurationOutcome(SagaCurationOutcomeKind.AlreadyRetired, null));

            }

            byte[] currentDigest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(memory.Content));

            if (!currentDigest.AsSpan().SequenceEqual(expectedContentDigest))
            {

                return Task.FromResult(new SagaCurationOutcome(SagaCurationOutcomeKind.StaleContent, null));

            }

            Memories[id] = memory with { RetiredAtUtc = retiredAt };

            _embeddedIds.Remove(id);

            _suppressed.Add((memory.ScopeKind, memory.ScopeCampaignId, memory.Content));

            return Task.FromResult(
                new SagaCurationOutcome(SagaCurationOutcomeKind.Applied, new SagaMemoryLifecycle(retiredAt, memory.PinnedAtUtc)));

        }

        public Task<SagaCurationOutcome> ReinstateAsync(
            string id,
            byte[] expectedContentDigest,
            float[] embedding,
            DateTimeOffset reinstatedAt,
            CancellationToken cancellationToken)
        {

            if (!Memories.TryGetValue(id, out SagaMemoryDto? memory))
            {

                return Task.FromResult(new SagaCurationOutcome(SagaCurationOutcomeKind.NotFound, null));

            }

            if (memory.RetiredAtUtc is null)
            {

                return Task.FromResult(new SagaCurationOutcome(SagaCurationOutcomeKind.NotRetired, null));

            }

            byte[] currentDigest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(memory.Content));

            if (!currentDigest.AsSpan().SequenceEqual(expectedContentDigest))
            {

                return Task.FromResult(new SagaCurationOutcome(SagaCurationOutcomeKind.StaleContent, null));

            }

            Memories[id] = memory with { RetiredAtUtc = null };

            _embeddedIds.Add(id);

            _suppressed.Remove((memory.ScopeKind, memory.ScopeCampaignId, memory.Content));

            return Task.FromResult(
                new SagaCurationOutcome(SagaCurationOutcomeKind.Applied, new SagaMemoryLifecycle(null, memory.PinnedAtUtc)));

        }

        public Task<SagaCurationOutcome> CorrectAsync(
            string id,
            byte[] expectedContentDigest,
            string content,
            float[] embedding,
            DateTimeOffset correctedAt,
            CancellationToken cancellationToken)
        {

            if (!Memories.TryGetValue(id, out SagaMemoryDto? memory))
            {

                return Task.FromResult(new SagaCurationOutcome(SagaCurationOutcomeKind.NotFound, null));

            }

            if (memory.RetiredAtUtc is not null)
            {

                return Task.FromResult(new SagaCurationOutcome(SagaCurationOutcomeKind.AlreadyRetired, null));

            }

            byte[] currentDigest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(memory.Content));

            if (!currentDigest.AsSpan().SequenceEqual(expectedContentDigest))
            {

                return Task.FromResult(new SagaCurationOutcome(SagaCurationOutcomeKind.StaleContent, null));

            }

            byte[] newDigest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content));

            if (newDigest.AsSpan().SequenceEqual(currentDigest))
            {

                return Task.FromResult(new SagaCurationOutcome(SagaCurationOutcomeKind.Unchanged, null));

            }

            Memories[id] = memory with { Content = content };

            return Task.FromResult(
                new SagaCurationOutcome(SagaCurationOutcomeKind.Applied, new SagaMemoryLifecycle(null, memory.PinnedAtUtc)));

        }

        public Task<SagaCurationOutcome> SetPinAsync(
            string id, bool pinned, DateTimeOffset changedAt, CancellationToken cancellationToken)
        {

            if (!Memories.TryGetValue(id, out SagaMemoryDto? memory))
            {

                return Task.FromResult(new SagaCurationOutcome(SagaCurationOutcomeKind.NotFound, null));

            }

            DateTimeOffset? pinnedAtUtc = pinned ? changedAt : null;

            Memories[id] = memory with { PinnedAtUtc = pinnedAtUtc };

            return Task.FromResult(
                new SagaCurationOutcome(SagaCurationOutcomeKind.Applied, new SagaMemoryLifecycle(memory.RetiredAtUtc, pinnedAtUtc)));

        }

        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken)
        {

            _embeddedIds.Remove(id);

            return Task.FromResult(Memories.Remove(id));

        }

        public Task DeleteAllAsync(CancellationToken cancellationToken)
        {

            Memories.Clear();

            _embeddedIds.Clear();

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

        ArcanumSettings settings = DefaultSettings() with
        {
            Providers =
            [
                DefaultProvider() with
                {
                    Models =
                    [
                        new ModelEntry(
                            ModelName,
                            Reasoning: new ModelReasoningSettings
                            {
                                WireDialect = ReasoningWireDialect.OpenRouter,
                            }),
                    ],
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
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("normal answer", result.Value.Text);
        Assert.Equal(ReasoningEffort.High, chat.LastChatOptions?.Reasoning?.Effort);
        Assert.Equal(expectedOutput, chat.LastChatOptions?.Reasoning?.Output);
    }

    [Fact]
    public async Task ReasoningMapping_StreamingStandardDialect_MapsTypedOptions()
    {

        ArcanumSettings settings = DefaultSettings() with
        {
            Providers =
            [
                DefaultProvider() with
                {
                    Models =
                    [
                        new ModelEntry(
                            ModelName,
                            Reasoning: new ModelReasoningSettings
                            {
                                WireDialect = ReasoningWireDialect.OpenRouter,
                            }),
                    ],
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
            InvocationContexts.AttendedSession(),
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
            InvocationContexts.AttendedSession(),
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
    public async Task Reasoning_UnspecifiedOutput_RequiresReasoningSummariesEnabled(
        bool reasoningSummariesEnabled,
        bool expectReasoning)
    {
        ArcanumSettings settings = SettingsWithReasoning() with
        {
            Features = DefaultSettings().Features with
            {
                Reasoning = true,
                ReasoningSummaries = reasoningSummariesEnabled,
            },
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
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(expectReasoning, result.Value.Reasoning.Count > 0);
        if (expectReasoning)
        {
            ReasoningContentSegment projected = Assert.Single(result.Value.Reasoning);
            Assert.Equal("provider-default reasoning", projected.Text);
            Assert.Equal(ReasoningOutputMode.Summary, projected.Output);
        }
        Assert.Equal("answer", result.Value.Text);
    }

    [Fact]
    public async Task Reasoning_UnspecifiedOutput_DefaultsToSummaryWhenReasoningSummariesEnabled()
    {
        ArcanumSettings settings = SettingsWithReasoning();
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
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        ReasoningContentSegment projected = Assert.Single(result.Value.Reasoning);
        Assert.Equal("provider-default summary", projected.Text);
        Assert.Equal(ReasoningOutputMode.Summary, projected.Output);
    }

    [Fact]
    public async Task Reasoning_StreamingWithSummariesDisabled_SuppressesReasoningFrames()
    {

        // DefaultSettings leaves Features.ReasoningSummaries off, so client-safe reasoning is not
        // projected even though the model declares reasoning and the request asks for a summary.
        ArcanumSettings settings = DefaultSettings() with
        {
            Providers =
            [
                DefaultProvider() with
                {
                    Models =
                    [
                        new ModelEntry(
                            ModelName,
                            Reasoning: new ModelReasoningSettings
                            {
                                WireDialect = ReasoningWireDialect.OpenRouter,
                            }),
                    ],
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
            InvocationContexts.AttendedSession(),
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
        ArcanumSettings settings = ConfigureGuardrails(
            SettingsWithReasoning(),
            enabled: true,
            detectPii: false,
            blockToxicity: true,
            toxicityBlocklist: ["bad-word"]);
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
        ArcanumSettings settings = SettingsWithReasoning();
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
                ResponseFormatJsonSchema = StrictJsonSchema(schema),
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
        ArcanumSettings settings = SettingsWithReasoning();
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
                ResponseFormatJsonSchema = StrictJsonSchema(schema),
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
        ArcanumSettings settings = ConfigureGuardrails(
            SettingsWithReasoning(),
            enabled: true,
            detectPii: false,
            blockToxicity: true,
            toxicityBlocklist: ["bad-word"]);
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
                ResponseFormatJsonSchema = StrictJsonSchema(schema),
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
        ArcanumSettings settings = ConfigureGuardrails(
            SettingsWithReasoning(),
            enabled: true,
            detectPii: false,
            blockedTopics: ["(?s)name.*ordered marker.*safe replacement"]);
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
                ResponseFormatJsonSchema = StrictJsonSchema(schema),
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
        chat.EnqueueText("invalid answer");
        ArcanumSettings settings = SettingsWithReasoning();
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
                ResponseFormatJsonSchema = StrictJsonSchema(schema),
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
        ArcanumSettings settings = SettingsWithReasoning();
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
                ResponseFormatJsonSchema = StrictJsonSchema(schema),
                SkipSpellRouting = true,
                DisableMcpTools = true,
                Reasoning = new ReasoningRequestOptions(Output: ReasoningOutputMode.Summary),
            },
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("""{"name":"fixed"}""", result.Value.Text);
        ReasoningContentSegment reasoning = Assert.Single(result.Value.Reasoning);
        Assert.Equal("replacement reasoning", reasoning.Text);
        Assert.DoesNotContain("discarded initial", reasoning.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StructuredRetry_ReservationFailure_PreservesBudgetError()
    {
        ScriptingChatClient chat = new();
        chat.EnqueueText("invalid answer");
        chat.EnqueueText("""{"name":"unused"}""");
        RecordingBudgetReservationService reservations = new()
        {
            ReservedUsdOverride = 0m,
        };
        reservations.AdjustResults.Enqueue(Result.Success());
        reservations.AdjustResults.Enqueue(Result.Failure(
            new Error(ErrorCodes.Budget.Exceeded, "retry reservation exceeded")));
        ArcanumSettings defaults = DefaultSettings();
        ArcanumSettings settings = defaults with
        {
            Providers =
            [
                DefaultProvider() with { ContextWindowLimit = 262_144 },
            ],
            Cost = defaults.Cost with
            {
                Pricing = new PricingSettings
                {
                    DefaultPricing = new ModelPricingEntry
                    {
                        InputPer1M = 1m,
                        OutputPer1M = 1m,
                    },
                },
            },
        };
        JsonElement schema = JsonSerializer.Deserialize<JsonElement>(
            """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""");
        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            settings,
            turnRunWriter: new RecordingTurnRunWriter(),
            budgetReservationService: reservations);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "return structured output",
                ResponseFormat = "json_schema",
                ResponseFormatJsonSchema = StrictJsonSchema(schema),
                MaxOutputTokens = 128_000,
                SkipSpellRouting = true,
                DisableMcpTools = true,
            },
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Budget.Exceeded, result.Error.Code);
        Assert.Equal(1, chat.BufferedCallCount);
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
        ArcanumSettings settings = ConfigureGuardrails(
            SettingsWithReasoning(),
            enabled: true,
            detectPii: false,
            blockToxicity: true,
            toxicityBlocklist: ["bad-word"]);
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
                ResponseFormatJsonSchema = StrictJsonSchema(schema),
                SkipSpellRouting = true,
                DisableMcpTools = true,
                Reasoning = new ReasoningRequestOptions(Output: ReasoningOutputMode.Summary),
            },
            InvocationContexts.AttendedSession(),
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
            InvocationContexts.AttendedSession(),
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
            InvocationContexts.AttendedSession(),
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
        ArcanumSettings settings = ConfigureGuardrails(
            SettingsWithReasoning(),
            enabled: true,
            detectPii: false,
            blockToxicity: true,
            toxicityBlocklist: ["bad-word"]);
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
        chat.EnqueueText("not json");
        ArcanumSettings settings = SettingsWithReasoning();
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
                ResponseFormatJsonSchema = StrictJsonSchema(schema),
                SkipSpellRouting = true,
                DisableMcpTools = true,
                Reasoning = new ReasoningRequestOptions(Output: ReasoningOutputMode.Summary),
            },
            InvocationContexts.AttendedSession(),
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

        ProviderSettings provider = settings.Providers[0];
        string model = provider.Models[0].Name;
        return new ModelTokenEstimator(resolver)
            .EstimateContext(new ModelTokenizationRequest(
                provider,
                model,
                messages,
                new ChatOptions(),
                ReservedAnswerTokens: 0,
                ReservedReasoningTokens: 0))
            .InputTokens;
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

        object?[] arguments = [messages, new ChatOptions(), lease, request, null];
        return Assert.IsType<Result>(method.Invoke(wizard, arguments));
    }

    [Fact]

    public async Task ContextPreview_NoRetrieval_UsesProductionAssemblyWithoutModelCallOrContent()

    {

        ScriptingChatClient chat = new();

        WizardIntelligenceProvider wizard = CreateWizard(chat);

        Result<ContextPreviewResult> preview = await wizard.PreviewContextAsync(

            new ContextPreviewRequest(

                Prompt: "inspect this turn",

                Model: ModelName,

                NoRetrieval: true),

            InvocationContexts.AttendedSession(),

            CancellationToken.None);

        Assert.True(preview.IsSuccess);

        Assert.Equal(0, chat.BufferedCallCount);

        Assert.Equal(0, chat.StreamingCallCount);

        Assert.Equal("disabledByNoRetrieval", preview.Value.RoutingMode);

        Assert.Null(preview.Value.Content);

        Assert.True(preview.Value.Tokens.TotalTokens > 0);

        Assert.Contains(

            preview.Value.Sources,

            static source => source.Source == ContextTokenSource.WorkspaceRag

                && !source.Included

                && source.Reason.Contains("noRetrieval", StringComparison.Ordinal));

    }

    [Fact]

    public async Task ContextPreview_ExplicitSpellAndTransientAttachments_AreAssembledWithoutInferenceOrPersistence()

    {

        await CreateSpellAsync(

            "preview-explicit",

            "PreviewExplicit",

            dependencies: null,

            body: "Follow the explicit preview Spell.");

        ArcanumSettings settings = DefaultSettings() with

        {

            Providers =

            [

                DefaultProvider() with

                {

                    Models = [new ModelEntry(ModelName, SupportsVision: true)],

                },

            ],

        };

        ScriptingChatClient chat = new();

        NoOpSessionAttachmentStore attachments = new();

        FakeGrimoireRepository grimoire = new();

        WizardIntelligenceProvider wizard = CreateWizard(

            chat,

            settings,

            grimoire,

            sessionAttachmentStore: attachments);

        Result<ContextPreviewResult> preview = await wizard.PreviewContextAsync(

            new ContextPreviewRequest(

                Prompt: "inspect explicit context",

                Model: ModelName,

                WorkingDirectory: _workspace.Root,

                ShowContent: true,

                NoRetrieval: true,

                OverrideSpellName: "PreviewExplicit",

                AttachedFiles:

                [

                    new AttachedFileDto(

                        "notes.txt",

                        "operator-provided preview notes"),

                ],

                ScryingFoci:

                [

                    new ScryingFocusDto(

                        Convert.ToBase64String([1, 2, 3]),

                        "image/png"),

                ],

                DisableAllTools: true,

                AdditionalSystemPrompt: "Use research synthesis policy.",

                MaxOutputTokens: 1_200),

            InvocationContexts.AttendedSession(),

            CancellationToken.None);

        Assert.True(preview.IsSuccess);

        Assert.Equal("PreviewExplicit", preview.Value.SelectedSpell);

        Assert.Equal("explicitOverride", preview.Value.RoutingMode);

        Assert.Equal(0, chat.BufferedCallCount);

        Assert.Equal(0, chat.StreamingCallCount);

        Assert.Equal(0, attachments.PersistNewCallCount);

        Assert.Null(grimoire.LastAssistantEntryId);

        Assert.NotNull(preview.Value.Content);

        Assert.Contains(

            "operator-provided preview notes",

            preview.Value.Content.SystemPrompt,

            StringComparison.Ordinal);

        Assert.Contains(

            "Use research synthesis policy.",

            preview.Value.Content.SystemPrompt,

            StringComparison.Ordinal);

        ContextPreviewSource explicitAttachments = Assert.Single(

            preview.Value.Sources,

            static source => source.Source == ContextTokenSource.ExplicitAttachments);

        Assert.True(explicitAttachments.Included);

        Assert.Equal(

            TokenEstimateClassification.Unknown,

            explicitAttachments.Classification);

        Assert.Equal(1_200, preview.Value.Tokens.ReservedOutputTokens);

        Assert.DoesNotContain(

            preview.Value.Tools,

            static tool => tool.Included);

    }

    [Fact]

    public async Task ContextPreview_ShowContent_ReturnsExactAssembledPromptOnlyWhenRequested()

    {

        ScriptingChatClient chat = new();

        WizardIntelligenceProvider wizard = CreateWizard(chat);

        Result<ContextPreviewResult> preview = await wizard.PreviewContextAsync(

            new ContextPreviewRequest(

                Prompt: "visible prompt",

                Model: ModelName,

                ShowContent: true,

                NoRetrieval: true),

            InvocationContexts.AttendedSession(),

            CancellationToken.None);

        Assert.True(preview.IsSuccess);

        Assert.NotNull(preview.Value.Content);

        Assert.Contains("## DATA", preview.Value.Content.SystemPrompt, StringComparison.Ordinal);

        Assert.Contains(

            preview.Value.Content.Messages,

            static message => message.Role == "user"

                && message.Content == "visible prompt");

    }

    [Fact]

    public async Task ContextPreview_AccountsAuxiliaryRoutingAndExplainsAttunementExclusions()

    {

        await CreateSpellWithDeclaredToolsAsync("preview-spell", ["allowed_tool"]);

        ScriptingChatClient chat = new()

        {

            UsageTotalTokens = 30,

        };

        chat.EnqueueText("""{"spellName":"preview-spell","entities":[]}""");

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(CreateMcpTool("allowed_tool"));

        mcp.Tools.Add(CreateMcpTool("blocked_tool"));

        WizardIntelligenceProvider wizard = CreateWizard(chat, mcp: mcp);

        Result<ContextPreviewResult> preview = await wizard.PreviewContextAsync(

            new ContextPreviewRequest(

                Prompt: "inspect routing",

                Model: ModelName,

                WorkingDirectory: _workspace.Root),

            InvocationContexts.AttendedSession(),

            CancellationToken.None);

        Assert.True(preview.IsSuccess);

        Assert.Equal(1, chat.BufferedCallCount);

        Assert.Equal("preview-spell", preview.Value.SelectedSpell);

        Assert.Contains("selected", preview.Value.SpellReason, StringComparison.OrdinalIgnoreCase);

        ContextPreviewAuxiliaryCall routing = Assert.Single(

            preview.Value.AuxiliaryCalls,

            static call => call.Purpose == "routing");

        Assert.Equal(30, routing.Tokens);

        Assert.Equal(TokenEstimateClassification.ProviderReported, routing.Classification);

        Assert.Contains(

            preview.Value.Tools,

            static tool => tool.Name == "blocked_tool"

                && !tool.Included

                && tool.Reason.Contains("attunement", StringComparison.OrdinalIgnoreCase));

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
        IBudgetReservationService? budgetReservationService = null,
        ILogger<WizardIntelligenceProvider>? logger = null,
        ILogger<ToolExecutionPipeline>? toolLogger = null,
        ISessionAttachmentRetrievalService? sessionAttachmentRetrieval = null,
        ISessionAttachmentStore? sessionAttachmentStore = null)
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

        sessionAttachmentStore ??= new NoOpSessionAttachmentStore();

        GrimoireTurnWriter grimoireTurnWriter = new(
            grimoire,
            grimoire as ISessionTurnBeginStore ?? new FakeSessionTurnBeginStore(),
            new SessionEventHub(NullLogger<SessionEventHub>.Instance),
            NullLogger<GrimoireTurnWriter>.Instance);

        return new WizardIntelligenceProvider(
            factory,
            new TestOptionsSnapshot<ArcanumSettings>(settings),
            logger ?? NullLogger<WizardIntelligenceProvider>.Instance,
            new FakeHttpClientFactory(),
            grimoire,
            mcp,
            campaignRepository,
            new ToolExecutionPipeline(
                new TestOptionsSnapshot<ArcanumSettings>(settings),
                ward,
                sanctumGuard,
                sessionAttachmentStore,
                toolLogger ?? NullLogger<ToolExecutionPipeline>.Instance,
                grimoireTurnWriter: grimoireTurnWriter),
            grimoireTurnWriter,
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
            sessionAttachmentStore,
            new HumanPromptRegistry(),
            healthTracker: null,
            guardrailsPipeline: guardrailsPipeline,
            turnRunWriter: turnRunWriter,
            budgetReservationService: budgetReservationService,
            webResearchProviderCatalog: new WebResearchProviderCatalog([]),
            sessionAttachmentRetrieval: sessionAttachmentRetrieval);
    }

    private static GuardrailsPipeline CreateGuardrailsPipeline(ArcanumSettings settings, FakeGuardrailAuditLogger? audit = null) =>
        new(
            new TestOptionsMonitor<ArcanumSettings>(settings),
            audit ?? new FakeGuardrailAuditLogger(),
            NullLogger<GuardrailsPipeline>.Instance);

    private static ArcanumSettings ConfigureGuardrails(
        ArcanumSettings settings,
        bool enabled,
        bool detectPii = true,
        bool blockToxicity = false,
        string[]? toxicityBlocklist = null,
        string[]? blockedTopics = null) =>
        settings with
        {
            Features = settings.Features with { Guardrails = enabled },
            Security = settings.Security with
            {
                Guardrails = new GuardrailsPolicySettings
                {
                    DetectPii = detectPii,
                    BlockToxicity = blockToxicity,
                    ToxicityBlocklist = toxicityBlocklist ?? [],
                    BlockedTopics = blockedTopics ?? [],
                },
            },
        };

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

        InferenceTokenizerResolver tokenizerResolver = new(NullLogger<InferenceTokenizerResolver>.Instance);

        IContextCompressionService compression = new ContextCompressionService(
            grimoire,
            new TestOptionsSnapshot<ArcanumSettings>(settings),
            tokenizerResolver,
            NullLogger<ContextCompressionService>.Instance);

        return new InferenceContextBuilder(
            grimoire,
            new TestOptionsSnapshot<ArcanumSettings>(settings),
            NullLogger<InferenceContextBuilder>.Instance,
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

    private static JsonElement StrictJsonSchema(JsonElement schema)
    {
        using JsonDocument wrapper = JsonDocument.Parse($$"""
            {
              "strict": true,
              "schema": {{schema.GetRawText()}}
            }
            """);
        return wrapper.RootElement.Clone();
    }

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
            Security = new SecuritySettings
            {
                Ward = new WardPolicySettings
                {
                    ForbiddenArts = ["execute_command"],
                },
            },
            Features = new FeatureSettings
            {
                // Lexicon retrieval is off by default in hub scenario tests so the fallback
                // LexiconEntityExtractor does not fire an extra LLM call against the scripted
                // ScriptingChatClient. Production defaults EnableLexiconSystem to true (Option A);
                // Lexicon-specific scenarios enable it explicitly.
                Lexicon = false,
            },
        };

    private static ArcanumSettings SettingsWithReasoning()
    {

        return DefaultSettings() with
        {
            Providers =
            [
                DefaultProvider() with
                {
                    Models =
                    [
                        new ModelEntry(
                            ModelName,
                            Reasoning: new ModelReasoningSettings
                            {
                                WireDialect = ReasoningWireDialect.OpenRouter,
                            }),
                    ],
                },
            ],
            Features = DefaultSettings().Features with
            {
                Reasoning = true,
                ReasoningSummaries = true,
            },
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
                AllowedTools: null)),
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
                Sequence = i + 1,
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
                Sequence = i + 1,
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

    private static AIFunction CreateProgressMcpTool(string name) =>
        AIFunctionFactory.Create(
            (int evidence) => $"evidence-{evidence}",
            name,
            "returns changing progress evidence");

    private AIFunction CreateProductionApplyPatchTool(
        ArcanumSettings settings,
        Func<IApplyPatchPendingReceiptSink, IApplyPatchPendingReceiptSink>?
            decorateSink = null)
    {

        async Task<string> ApplyPatchAsync(
            string patch,
            bool dryRun,
            CancellationToken cancellationToken)
        {

            ApplyPatchInvocationContext ambient =
                Assert.IsType<ApplyPatchInvocationContext>(
                    ApplyPatchInvocationAmbient.Current);
            ApplyPatchInvocationContext executionContext =
                decorateSink is null
                    ? ambient
                    : ambient with
                    {
                        Sink = decorateSink(ambient.Sink),
                    };
            WorkspacePatchSettings patchSettings =
                settings.ResolveCodingTools().Patch;
            ApplyPatchToolExecutionResponse response =
                await new ApplyPatchToolExecutionService(
                        _workspace.Root,
                        patchSettings,
                        outputBudgetBytes: 1024 * 1024,
                        McpJsonSerializerContext.Default)
                    .ExecuteAsync(
                        new ApplyPatchParams(patch, dryRun),
                        executionContext,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (!ReferenceEquals(ambient, executionContext)
                && executionContext.HandoffOutcome is { } outcome)
            {
                if (executionContext.CancellationClassified)
                {
                    ambient.RecordCancellationOutcome(outcome);
                }
                else
                {
                    ambient.RecordHandoffOutcome(outcome);
                }
            }

            return response.SerializedResult;

        }

        return AIFunctionFactory.Create(
            ApplyPatchAsync,
            ToolRiskClassifier.ApplyPatchToolName,
            "production apply_patch executor");

    }

    private static AIFunction
        CreatePostDispatchFailingApplyPatchTool() =>
        AIFunctionFactory.Create(
            PostDispatchFailingApplyPatch,
            ToolRiskClassifier.ApplyPatchToolName,
            "post-dispatch failure seam");

    private static string PostDispatchFailingApplyPatch()
    {
        ApplyPatchInvocationContext context =
            Assert.IsType<ApplyPatchInvocationContext>(
                ApplyPatchInvocationAmbient.Current);
        context.MarkDispatched();

        throw new McpTransportUnavailableException(
            "channel completed after dispatch",
            McpRequestDispatchState.DispatchedOrUnknown);
    }

    private static AIFunction CreateThrowingMcpTool(
        string name,
        string message = "tool boom") =>
        AIFunctionFactory.Create(
            () => ThrowingToolDelegate(message),
            name,
            "throws");

    private static string ThrowingToolDelegate(string message) =>
        throw new InvalidOperationException(message);

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

        await foreach (IntelligenceEvent evt in wizard.StreamPromptAsync(request, InvocationContexts.AttendedSession(), CancellationToken.None, auditContext))
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

        public void EnqueueToolCallWithMissingId(
            string toolName,
            Dictionary<string, object?>? arguments = null)
        {
            Dictionary<string, object?> args =
                arguments ?? new Dictionary<string, object?>();

            _buffered.Enqueue(
                _ => Task.FromResult(
                    ResponseTool(toolName, string.Empty, args)));
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
        public string FailureMessage { get; init; } =
            "No AI model could be resolved.";

        public Task<ChatClientLease> ResolveClientAsync(string? targetModel, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(FailureMessage);

        public Task<ChatClientLease> ResolveClientAsync(ProviderSettings provider, string resolvedModel, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(FailureMessage);

    }

    private sealed class FakeGrimoireRepository : IGrimoireRepository, ISessionTurnBeginStore
    {

        public sealed record RecordedToolInteraction(
            Guid SessionId,
            string ToolName,
            string Arguments,
            string Result,
            string ModelUsed);

        public Session? Session { get; init; }

        public List<RecordedToolInteraction> ToolInteractions { get; } = [];

        public bool ThrowOnBegin { get; init; }

        public bool ThrowOnFinalize { get; init; }

        public Guid? FixedSessionId { get; init; }

        public Guid? LastAssistantEntryId { get; private set; }

        public MandatoryToolInteractionAppendOutcome MandatoryAppendOutcome
        {
            get;
            init;
        } = MandatoryToolInteractionAppendOutcome.NewlyCommitted;

        public Func<MandatoryToolInteraction, CancellationToken, Task>?
            MandatoryAppendHandler { get; init; }

        public List<MandatoryToolInteraction> MandatoryInteractions
        {
            get;
        } = [];

        public Guid? LastIncrementedSessionId { get; private set; }

        public long LastIncrementedTokens { get; private set; }

        public decimal LastIncrementedCostUsd { get; private set; }

        public int DiscardCallCount { get; private set; }

        public int FinalizeCallCount { get; private set; }

        public int AppendToolInteractionCallCount { get; private set; }

        public Func<CancellationToken, Task>?
            AppendToolInteractionHandler { get; init; }

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

            LastAssistantEntryId = Guid.NewGuid();

            return Task.FromResult((
                FixedSessionId ?? sessionId ?? Guid.NewGuid(),
                LastAssistantEntryId.Value));

        }

        public ValueTask<Result<Guid>> CreateBoundSessionAsync(
            CanonicalCampaignContext campaign,
            string title,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<Guid>.Success(FixedSessionId ?? Guid.NewGuid()));

        public async ValueTask<Result<AssistantReplyBeginReceipt>> BeginAssistantReplyAsync(
            Guid existingSessionId,
            CanonicalCampaignContext campaign,
            string prompt,
            string model,
            CancellationToken cancellationToken)
        {

            if (ThrowOnBegin)
            {
                // The port reports a failed begin, it does not throw: the whole point of the narrow
                // contract is that a caller cannot mistake the failure for an ordinary turn.
                return new Error(ErrorCodes.Grimoire.WriteFailed, "begin failed");
            }

            (Guid sessionId, Guid assistantEntryId) = await BeginAssistantReplyAsync(
                existingSessionId,
                prompt,
                model,
                cancellationToken);

            return Result<AssistantReplyBeginReceipt>.Success(
                new AssistantReplyBeginReceipt(
                    sessionId,
                    Guid.NewGuid(),
                    assistantEntryId,
                    new SessionTurnInputPreflight(sessionId, campaign.Binding, 0, 0)));

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

        public async Task AppendToolInteractionAsync(
            Guid sessionId,
            string toolName,
            string arguments,
            string result,
            string modelUsed,
            CancellationToken cancellationToken = default)
        {
            ToolInteractions.Add(new RecordedToolInteraction(
                sessionId,
                toolName,
                arguments,
                result,
                modelUsed));

            AppendToolInteractionCallCount++;

            if (AppendToolInteractionHandler is not null)
            {
                await AppendToolInteractionHandler(
                    cancellationToken);
            }
        }

        public Task<MandatoryToolInteractionProbeResult>
            ProbeMandatoryToolInteractionAsync(
                MandatoryToolInteractionProbe probe,
                CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new MandatoryToolInteractionProbeResult(
                    MandatoryToolInteractionProbeOutcome.NotFound,
                    Result: null));

        public Task<MandatoryToolInteractionPreflightResult>
            PreflightMandatoryToolInteractionAsync(
                MandatoryToolInteraction interaction,
                CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new MandatoryToolInteractionPreflightResult(
                    MandatoryToolInteractionPreflightOutcome.Admitted,
                    Result: null));

        public async Task<MandatoryToolInteractionAppendResult>
            AppendMandatoryToolInteractionAsync(
                MandatoryToolInteraction interaction,
                CancellationToken cancellationToken = default)
        {

            MandatoryInteractions.Add(interaction);
            if (MandatoryAppendHandler is not null)
            {
                await MandatoryAppendHandler(
                    interaction,
                    cancellationToken).ConfigureAwait(false);
            }

            return new MandatoryToolInteractionAppendResult(
                MandatoryAppendOutcome,
                interaction.Receipt);

        }

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

    private sealed class CancelAfterHandoffSink(
        IApplyPatchPendingReceiptSink inner,
        CancellationTokenSource cancellation)
        : IApplyPatchPendingReceiptSink
    {
        public ValueTask<ApplyPatchReceiptProbeResult> ProbeAsync(
            ApplyPatchReceiptProbe probe,
            CancellationToken cancellationToken) =>
            inner.ProbeAsync(probe, cancellationToken);

        public ValueTask<ApplyPatchReceiptPreflightResult> PreflightAsync(
            ApplyPatchReceiptPreflight preflight,
            CancellationToken cancellationToken) =>
            inner.PreflightAsync(preflight, cancellationToken);

        public ValueTask<MandatoryToolInteractionAppendOutcome>
            PersistRecoveryReceiptAsync(
                ApplyPatchRecoveryReceipt receipt,
                CancellationToken cancellationToken) =>
            inner.PersistRecoveryReceiptAsync(
                receipt,
                cancellationToken);

        public async ValueTask<ApplyPatchPendingReceiptHandoffResult>
            HandoffAsync(
                PendingApplyPatchReceipt receipt,
                CancellationToken cancellationToken)
        {

            ApplyPatchPendingReceiptHandoffResult result =
                await inner.HandoffAsync(
                    receipt,
                    CancellationToken.None).ConfigureAwait(false);
            cancellation.Cancel();

            return result;

        }

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

        public List<WardResolutionOrigin> AutomaticResolutionOrigins { get; } = [];

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

        public WardResolution RecordAutomaticResolution(
            string wardId,
            bool allowed,
            string? reason,
            WardResolutionOrigin origin)
        {
            AutomaticResolutionOrigins.Add(origin);

            return new(allowed, reason, DateTimeOffset.UtcNow, origin);
        }

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

        public Task<Result<Campaign>> AddAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<Campaign>.Success(campaign));

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

        public Task<bool> TryAbandonRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

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

        public decimal? ReservedUsdOverride { get; init; }

        public decimal? ReconciledUsd { get; private set; }

        public int ReconcileCount { get; private set; }

        public bool WasReleased { get; private set; }

        public Queue<Result> AdjustResults { get; } = new();

        public Task<Result<BudgetReservation>> ReserveAsync(
            BudgetReservationRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(Result<BudgetReservation>.Success(new BudgetReservation(
                Guid.NewGuid(),
                request.RunId,
                request.BudgetPeriod,
                ReservedUsdOverride ?? request.ReservedUsd,
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

        public Task<Result> AdjustAsync(
            Guid reservationId,
            decimal reservedUsd,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                AdjustResults.Count > 0
                    ? AdjustResults.Dequeue()
                    : Result.Success());

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
