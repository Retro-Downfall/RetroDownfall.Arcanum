using Microsoft.Extensions.AI;
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

        var request = BaseRequest() with { Prompt = "parent", SkipSpellRouting = true, DisableMcpTools = true };

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

        var wizard = CreateWizard(chat, subagentRunner: runner);

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
