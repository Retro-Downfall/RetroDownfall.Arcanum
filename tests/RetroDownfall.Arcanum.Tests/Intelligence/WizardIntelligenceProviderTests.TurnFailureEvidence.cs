using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Native_turn_explains_that_malformed_tool_arguments_never_dispatched(bool streaming, bool validFirst)
    {
        ArcanumSettings settings = DefaultSettings();

        const string canary = "untrusted-malformed-argument-secret";

        string incomplete = "{\"" + canary + "\":";

        string body = validFirst ? SyntheticMixedToolResponse(streaming, incomplete) : streaming ? SyntheticToolSse(incomplete, "length") : SyntheticToolBuffered(incomplete, "length");

        using SyntheticOutputHttpFactory http = new(streaming, body);

        ChatClientFactory factory = new(http, new TestOptionsMonitor<ArcanumSettings>(settings), new OutputLimitCredentialResolver());

        TurnExecutionCoordinator coordinator = new(new TurnEngine(CreateWizard(new(), settings, factory: factory)));

        var request = OutputLimitRequest() with { DisableAllTools = false, DisableMcpTools = true };

        JsonElement wire;

        if (streaming)
        {
            List<IntelligenceEvent> events = [];

            await foreach (var frame in coordinator.ExecuteIntelligenceStreamAsync(request, InvocationContexts.AttendedSession(), CancellationToken.None))
                events.Add(frame);

            Assert.DoesNotContain(events, item => item.Type is IntelligenceEventType.ToolCall or IntelligenceEventType.ToolResult);

            IntelligenceEvent terminal = Assert.Single(events, item => item.Type == IntelligenceEventType.Error);

            Assert.Equal(ErrorCodes.Hub.ProviderToolCallInvalid, terminal.Data);

            wire = JsonSerializer.SerializeToElement(terminal, ArcanumJsonContext.Default.IntelligenceEvent);
        }
        else
        {
            Result<PromptTurnResult> result = await coordinator.ExecuteBufferedAsync(request, InvocationContexts.AttendedSession(), CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(ErrorCodes.Hub.ProviderToolCallInvalid, result.Error.Code);

            var envelope = ApiResponse<PromptResponseDto>.FromResult(Result<PromptResponseDto>.Failure(result.Error));

            wire = JsonSerializer.SerializeToElement(envelope, ArcanumJsonContext.Default.ApiResponsePromptResponseDto).GetProperty("error");
        }

        Assert.DoesNotContain(canary, wire.GetRawText(), StringComparison.Ordinal);

        Assert.True(wire.TryGetProperty("failureEvidence", out JsonElement evidence), "The caller needs the dispatch outcome for this tool failure.");

        Assert.Equal("toolArguments", evidence.GetProperty("phase").GetString());

        Assert.Equal("notDispatched", evidence.GetProperty("effect").GetString());

        Assert.Equal("correctArguments", evidence.GetProperty("nextAction").GetString());

        Assert.Equal(validFirst ? 1 : 0, evidence.GetProperty("toolIndex").GetInt32());

        Assert.Equal(1, http.Sends);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Native_tool_failure_facts_survive_a_successful_followup_without_copying_arguments(bool streaming, bool denied)
    {
        using ArcanumTestHomeScope home = new("native-tool-failure");

        const string privateName = "private-file-name-53c2.txt";

        string path = denied ? Path.Combine(Path.GetDirectoryName(_workspace.Root)!, privateName) : privateName;

        await using McpConnectionManager manager = McpConnectionManagerHarness.Create();

        FakeMcpConnectionManager mcp = await WorkspaceOutcomeToolsAsync(manager, _workspace.Root);

        ScriptingChatClient chat = new();

        JsonElement evidence;

        if (streaming)
        {
            chat.EnqueueStreamToolCall("read_file", "read-with-failure", WorkspaceReadArguments(path));

            chat.EnqueueStreamTokens("finished");

            var events = await CollectStreamAsync(CreateWizard(chat, mcp: mcp), WorkspaceOutcomeRequest(_workspace.Root));

            var tool = Assert.Single(events, item => item.Type == IntelligenceEventType.ToolResult);

            Assert.Equal("finished", Assert.Single(events, item => item.Type == IntelligenceEventType.Result).Message);

            var json = JsonSerializer.SerializeToElement(tool, ArcanumJsonContext.Default.IntelligenceEvent);

            Assert.True(json.TryGetProperty("failureEvidence", out evidence), "The streaming tool result lost its failure evidence.");
        }
        else
        {
            chat.EnqueueToolCall("read_file", "read-with-failure", WorkspaceReadArguments(path));

            chat.EnqueueText("finished");

            var result = await CreateWizard(chat, mcp: mcp).ExecutePromptAsync(WorkspaceOutcomeRequest(_workspace.Root),
                InvocationContexts.AttendedSession(), CancellationToken.None);

            Assert.True(result.IsSuccess);

            Assert.Equal("finished", result.Value.Text);

            var json = JsonSerializer.SerializeToElement(PromptResponseDto.From(result.Value), ArcanumJsonContext.Default.PromptResponseDto);

            Assert.True(json.TryGetProperty("toolFailures", out var failures), "A successful buffered answer lost its earlier tool failure.");

            evidence = Assert.Single(failures.EnumerateArray());
        }

        Assert.Equal(denied ? "toolAuthorization" : "toolExecution", evidence.GetProperty("phase").GetString());

        Assert.Equal(denied ? "notDispatched" : "completed", evidence.GetProperty("effect").GetString());

        Assert.Equal(denied ? "useAuthorizedScope" : "inspectWorkspace", evidence.GetProperty("nextAction").GetString());

        Assert.Equal("read_file", evidence.GetProperty("toolName").GetString());

        Assert.DoesNotContain(privateName, evidence.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Native_turn_reports_where_a_transport_exception_was_observed(bool streaming, bool localPreparation)
    {
        const string canary = "private-failure-detail";

        var settings = DefaultSettings() with
        {
            Providers = [DefaultProvider() with { Name = "native-first", Endpoint = "https://native-first.invalid" }],
        };

        using OriginHttpFactory http = new(true, streaming, canary);

        var clients = new ChatClientFactory(http, new TestOptionsMonitor<ArcanumSettings>(settings), new OriginCredentialResolver(false, canary));

        FakeGrimoireRepository grimoire = new()
        {
            BeforeBegin = _ => localPreparation ? Task.FromException(OriginTransportFailure(canary)) : Task.CompletedTask,
        };

        var wizard = CreateWizard(new(), settings, factory: clients, grimoire: grimoire);

        var request = BaseRequest() with { Model = ModelName, SkipSpellRouting = true, DisableAllTools = true, Prompt = "Start a turn." };

        JsonElement error;

        if (streaming)
        {
            var frames = await CollectStreamAsync(wizard, request);

            error = JsonSerializer.SerializeToElement(Assert.Single(frames, item => item.Type == IntelligenceEventType.Error),
                ArcanumJsonContext.Default.IntelligenceEvent);
        }
        else
        {
            var result = await wizard.ExecutePromptAsync(request, InvocationContexts.AttendedSession(), CancellationToken.None);

            Assert.True(result.IsFailure);

            var envelope = ApiResponse<PromptResponseDto>.FromResult(Result<PromptResponseDto>.Failure(result.Error));

            error = JsonSerializer.SerializeToElement(envelope, ArcanumJsonContext.Default.ApiResponsePromptResponseDto).GetProperty("error");
        }

        Assert.DoesNotContain(canary, error.GetRawText(), StringComparison.Ordinal);

        Assert.Equal(localPreparation ? 0 : 1, http.Sends);

        Assert.True(error.TryGetProperty("failureEvidence", out JsonElement evidence), "The terminal error lost its observed origin.");

        Assert.Equal(localPreparation ? "hostPreparation" : "providerRequest", evidence.GetProperty("phase").GetString());

        Assert.Equal(localPreparation ? "notDispatched" : "unknown", evidence.GetProperty("effect").GetString());

        Assert.Equal(localPreparation ? "inspectSession" : "checkProvider", evidence.GetProperty("nextAction").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelling_the_caller_during_a_tool_exposes_the_unknown_effect_and_does_not_run_it_again(bool streaming)
    {
        using CancellationTokenSource caller = new();

        int invocations = 0;

        async Task<string> CancelInToolAsync(CancellationToken token)
        {
            Interlocked.Increment(ref invocations);

            await caller.CancelAsync();

            token.ThrowIfCancellationRequested();

            return "completed";
        }

        FakeMcpConnectionManager tools = new();

        tools.Tools.Add(AIFunctionFactory.Create(CancelInToolAsync, "test_effect"));

        ScriptingChatClient chat = new();

        if (streaming) chat.EnqueueStreamToolCall("test_effect");
        else chat.EnqueueToolCall("test_effect");

        var wizard = CreateWizard(chat, mcp: tools);

        var request = BaseRequest() with { Prompt = "Use the test tool.", SkipSpellRouting = true };

        OperationCanceledException cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (streaming)
            {
                await foreach (var _ in wizard.StreamPromptAsync(request, InvocationContexts.AttendedSession(), caller.Token)) { }
            }
            else
            {
                await wizard.ExecutePromptAsync(request, InvocationContexts.AttendedSession(), caller.Token);
            }
        });

        Assert.Equal(1, invocations);

        Error failure = Assert.IsType<TurnCanceledException>(cancelled).Failure;

        Assert.Equal("callerCancellation", JsonSerializer.SerializeToElement(
            ApiResponse<PromptResponseDto>.FromResult(Result<PromptResponseDto>.Failure(failure)),
            ArcanumJsonContext.Default.ApiResponsePromptResponseDto).GetProperty("error").GetProperty("failureEvidence").GetProperty("phase").GetString());

        Assert.Equal(TurnEffectState.Unknown, failure.FailureEvidence?.Effect);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_turn_retains_an_owner_observed_patch_conflict(bool streaming)
    {
        FakeMcpConnectionManager mcp = new();

        const string name = "apply_patch";

        var conflict = new TrustedStructuredToolResult(TrustedStructuredToolResultKind.WorkspacePatch,
            """{"status":"conflict","code":"hunk_mismatch"}""")
        {
            FailureEvidence = new(TurnFailurePhase.ToolPrecondition, TurnEffectState.NotDispatched, TurnRecoveryAction.InspectWorkspace),
        };

        mcp.Tools.Add(new NativePatchObservationFunction(conflict));

        ScriptingChatClient chat = new();

        var grimoire = new FakeGrimoireRepository();

        var wizard = CreateWizard(chat, mcp: mcp, grimoire: grimoire);

        var request = BaseRequest() with { SessionId = Guid.NewGuid(), Prompt = "Edit the file.", SkipSpellRouting = true };

        JsonElement evidence;

        if (streaming)
        {
            chat.EnqueueStreamToolCall(name, "patch-id", new() { ["patch"] = "test" });

            chat.EnqueueStreamTokens("done");

            var frames = await CollectStreamAsync(wizard, request);

            var result = Assert.Single(frames, f => f.Type == IntelligenceEventType.ToolResult);

            Assert.True(JsonSerializer.SerializeToElement(result, ArcanumJsonContext.Default.IntelligenceEvent)
                .TryGetProperty("failureEvidence", out evidence), "The patch owner's conflict evidence was lost.");
        }
        else
        {
            chat.EnqueueToolCall(name, "patch-id", new() { ["patch"] = "test" });

            chat.EnqueueText("done");

            var result = await wizard.ExecutePromptAsync(request,
                InvocationContexts.AttendedSession(), CancellationToken.None);

            Assert.True(result.IsSuccess);

            var wire = JsonSerializer.SerializeToElement(PromptResponseDto.From(result.Value), ArcanumJsonContext.Default.PromptResponseDto);

            Assert.True(wire.TryGetProperty("toolFailures", out var failures), "The buffered patch owner's conflict evidence was lost.");

            evidence = Assert.Single(failures.EnumerateArray());
        }

        Assert.Equal("toolPrecondition", evidence.GetProperty("phase").GetString());

        Assert.Equal("notDispatched", evidence.GetProperty("effect").GetString());

        Assert.Equal(0, evidence.GetProperty("toolIndex").GetInt32());
    }

    private sealed class NativePatchObservationFunction(TrustedStructuredToolResult result) : AIFunction
    {
        public override string Name => "apply_patch";

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            ValueTask.FromResult<object?>(result);
    }
}
