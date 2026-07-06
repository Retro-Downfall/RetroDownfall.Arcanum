using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
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
            Intelligence = new IntelligenceSettings { InferenceTimeoutSeconds = 1 },
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
            Intelligence = new IntelligenceSettings { InferenceTimeoutSeconds = 1 },
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
                && e.Message == "Tool invocation limit reached.");

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
    public async Task Scenario34_SessionFinalizeFailure_StillReturnsInference()
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

        Assert.True(result.IsSuccess);

        Assert.Equal("finalize failure ok", result.Value!.Text);

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

        await CreateSpellWithDeclaredToolsAsync("exec-spell", ["execute_command"]);

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
    public async Task LoopContract_BufferedTimeout_DiscardsInFlightRow_NoOrphan()
    {

        FakeGrimoireRepository grimoire = new();

        ScriptingChatClient chat = new();

        chat.EnqueueSlowBuffered(TimeSpan.FromSeconds(30), "late");

        ArcanumSettings settings = DefaultSettings() with
        {
            Intelligence = new IntelligenceSettings { InferenceTimeoutSeconds = 1 },
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
        ArcanumDbContext? db = null,
        IInferenceAuditLogger? auditLogger = null)
    {
        settings ??= DefaultSettings();

        auditLogger ??= new FakeInferenceAuditLogger();

        grimoire ??= new FakeGrimoireRepository();

        ward ??= new FakeWard();

        mcp ??= new FakeMcpConnectionManager();

        factory ??= new FakeChatClientFactory(chatClient, DefaultProvider());

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

        db ??= CreateUnusedDbContext();

        return new WizardIntelligenceProvider(
            factory,
            new TestOptionsSnapshot<ArcanumSettings>(settings),
            NullLogger<WizardIntelligenceProvider>.Instance,
            grimoire,
            mcp,
            campaignRepository,
            new ToolExecutionPipeline(
                new TestOptionsSnapshot<ArcanumSettings>(settings),
                ward,
                sanctumGuard,
                NullLogger<ToolExecutionPipeline>.Instance),
            new GrimoireTurnWriter(
                grimoire,
                new SessionEventHub(new TestOptionsMonitor<ArcanumSettings>(settings), NullLogger<SessionEventHub>.Instance),
                NullLogger<GrimoireTurnWriter>.Instance),
            new InferenceContextBuilder(
                grimoire,
                new TestOptionsSnapshot<ArcanumSettings>(settings),
                NullLogger<InferenceContextBuilder>.Instance,
                new ManaPreflight(new TestOptionsMonitor<ArcanumSettings>(settings)),
                new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance)),
            sanctumGuard,
            new ProcessResourceLimiter(),
            weaveService,
            divinationService,
            workspaceIndexingService,
            sagaMemoryStore,
            sagaExtractionService,
            semanticSpellRouter,
            db,
            auditLogger);
    }

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

    private static PingRequest BaseRequest() =>
        new(Prompt: string.Empty, Model: ModelName, WorkingDirectory: string.Empty);

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
        };

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
            LastSummarizedMessageAt = watermark,
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

        public int BufferedCallCount { get; private set; }

        public IReadOnlyList<MeAiChatMessage> LastBufferedMessages { get; private set; } = [];

        public ChatOptions? LastChatOptions { get; private set; }

        public int? UsageTotalTokens { get; set; }

        public void EnqueueText(string text) =>
            _buffered.Enqueue(_ => Task.FromResult(ResponseText(text)));

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
            LastBufferedMessages = messages.ToList();

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

        public int DiscardCallCount { get; private set; }

        public int FinalizeCallCount { get; private set; }

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

        public int WardCallCount { get; private set; }

        public Task<WardResolution> WardAsync(
            string wardId,
            string toolName,
            JsonDocument? arguments,
            string? sessionId,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            WardCallCount++;

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
