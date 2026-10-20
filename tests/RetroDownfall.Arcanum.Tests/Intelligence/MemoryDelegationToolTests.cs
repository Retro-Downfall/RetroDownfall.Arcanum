using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence.Subagents;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
using RetroDownfall.Arcanum.Api.Intelligence.MemoryDelegation;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Tests.Fixtures;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence.MemoryDelegation;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class MemoryDelegationToolTests
{
    [SkippableFact]
    public async Task Exact_executed_task_digest_binds_prompt_model_file_attachment_and_budgets()
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();

        Guid first = Guid.NewGuid(), second = Guid.NewGuid();

        using var attachments = AttachmentMemoryGateAmbient.BeginTurn();

        foreach (Guid id in new[] { first, second })
            AttachmentMemoryGateAmbient.RegisterMaterialized(new(Guid.NewGuid(), id, "file", 1, "hash", DateTimeOffset.UtcNow,
                "SessionAttachment", AttachmentSourceAvailability.Available));

        async Task<CovenantDigest> Bind(string prompt = "  actual task  ", string model = "model", string path = "file.txt",
            string content = "file data", Guid? attachment = null, long tokens = 100, decimal cost = 1m)
        {
            Guid rootId = Guid.NewGuid();

            await using var root = await h.PrepareAsync(rootId);

            var parent = new PingRequest("parent");

            var binding = new MemoryDelegationRootBinding(parent, h.Source.Invocation, rootId, root, h.Coordinator);

            CapturingRunner runner = new();

            var args = Arguments("memory");

            args["prompt"] = prompt;

            args["max_tokens"] = tokens;

            args["max_cost_usd"] = cost;

            args["files"] = JsonSerializer.SerializeToElement(new[] { new { path, content, attachment_id = attachment ?? first } }, AdHocJson.Options);

            await new ArcanumDelegateTaskTool(runner, model, binding).InvokeAsync(args);

            Assert.Equal(prompt.Trim(), runner.Request!.Prompt);

            return runner.Request.MemoryDelegation!.Child.TaskDigest;
        }

        var baseline = await Bind();

        Assert.Equal(baseline, await Bind(prompt: "actual task", cost: 1.00m));

        Assert.NotEqual(baseline, await Bind(prompt: "other task"));

        Assert.NotEqual(baseline, await Bind(model: "other model"));

        Assert.NotEqual(baseline, await Bind(path: "other.txt"));

        Assert.NotEqual(baseline, await Bind(content: "other data"));

        Assert.NotEqual(baseline, await Bind(attachment: second));

        Assert.NotEqual(baseline, await Bind(tokens: 101));

        Assert.NotEqual(baseline, await Bind(cost: 2m));

        Assert.NotEqual(baseline, await Bind(cost: 0.0000000000000000000000000001m));
    }

    [SkippableFact]
    public async Task Capsule_result_preserves_trusted_metadata_before_stringification()
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();
        await using var root = await h.PrepareAsync();
        var parent = new PingRequest("parent");
        var binding = new MemoryDelegationRootBinding(parent, h.Source.Invocation, h.RootId, root, h.Coordinator);
        var output = await new ArcanumDelegateTaskTool(new CapturingRunner(), "model", binding).InvokeAsync(Arguments("memory"));
        Assert.NotNull(output);
        Assert.IsNotType<string>(output);
        Assert.Equal("summary", output.ToString());
    }

    [SkippableFact]
    public async Task Later_ordinary_child_retains_output_guard_without_another_read_grant()
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync(sensitive: true);
        await using var root = await h.PrepareAsync();
        var binding = new MemoryDelegationRootBinding(new PingRequest("parent"), h.Source.Invocation, h.RootId, root, h.Coordinator);
        var runner = new CapturingRunner();
        var tool = new ArcanumDelegateTaskTool(runner, "model", binding);
        await tool.InvokeAsync(Arguments("memory"));
        await runner.Request!.MemoryDelegation!.Child.DisposeAsync();
        var args = Arguments(null);
        args["prompt"] = "derived explicit prompt";
        var output = await tool.InvokeAsync(args);
        Assert.Equal("derived explicit prompt", runner.Request!.Prompt);
        Assert.Null(runner.Request.MemoryDelegation);
        var protection = Assert.IsType<MemoryDelegationProtection>(runner.Request.InputProtection);
        Assert.Equal(ContentSensitivity.CovenantDerived, protection.Sensitivity.Level);
        Assert.Equal(root.Sensitivity.Provenance, protection.Sensitivity.Provenance);
        Assert.IsType<MemoryDelegationToolResult>(output);
        await using (await h.Coordinator.BeginMutationAsync()) { }
        Assert.True((await protection.AdmitAsync(default)).IsFailure);
    }

    [SkippableFact]
    public async Task Parallel_ordinary_input_is_frozen_before_later_consumption()
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();
        await using var root = await h.PrepareAsync();
        var binding = new MemoryDelegationRootBinding(new PingRequest("parent"), h.Source.Invocation, h.RootId, root, h.Coordinator);
        var runner = new QueuedRunner();
        var tool = new ArcanumDelegateTaskTool(runner, "model", binding);
        var args = Arguments(null);
        var queued = tool.InvokeAsync(args).AsTask();
        await runner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await tool.InvokeAsync(Arguments("memory"));
        args["prompt"] = "later derived text";
        runner.Release.TrySetResult();
        await queued;
        Assert.Equal("actual task", runner.First!.Prompt);
        Assert.Null(runner.First.InputProtection);
        Assert.Null(runner.First.MemoryDelegation);
    }

    [SkippableFact]
    public async Task Primary_input_sensitivity_is_frozen_before_async_consumption()
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();
        await using var root = await h.PrepareAsync();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        async ValueTask<RetroDownfall.Arcanum.Core.Primitives.Result> Revalidate(CancellationToken ct)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return RetroDownfall.Arcanum.Core.Primitives.Result.Success();
        }

        var binding = new MemoryDelegationRootBinding(new PingRequest("parent"), h.Source.Invocation, h.RootId, root, h.Coordinator, Revalidate);
        Guid initial = Guid.NewGuid(), later = Guid.NewGuid();
        binding.RecordDerived(ProviderCallSensitivities.Create(ContentSensitivity.CovenantDerived, GenerationProvenance.CreateExact([initial])));
        var runner = new CapturingRunner();
        var tool = new ArcanumDelegateTaskTool(runner, "model", binding);
        var args = Arguments("memory");
        var pending = tool.InvokeAsync(args).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        binding.RecordDerived(ProviderCallSensitivities.Create(ContentSensitivity.CovenantDerived, GenerationProvenance.CreateExact([later])));
        args["prompt"] = "changed later";
        release.TrySetResult();
        var result = Assert.IsType<MemoryDelegationToolResult>(await pending);
        Assert.Equal("actual task", runner.Request!.Prompt);
        Assert.Contains(initial, result.Sensitivity.Provenance.ExactGenerationIds);
        Assert.DoesNotContain(later, result.Sensitivity.Provenance.ExactGenerationIds);
    }

    private sealed class QueuedRunner : ISubagentRunner
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal SubagentRunRequest? First { get; private set; }
        public async Task<SubagentRunResult> RunAsync(SubagentRunRequest request, CancellationToken cancellationToken)
        {
            if (First is null)
            {
                First = request;
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            return new(true, "summary", Guid.NewGuid(), new(1, 0m, 1, false), null);
        }
    }

    [Fact]
    public void Capability_and_internal_execution_state_have_no_generated_wire_path()
    {
        foreach (var type in new[] { typeof(SubagentRunRequest), typeof(MemoryDelegationExecution), typeof(MemoryDelegationRootBinding), typeof(MemoryDelegationRoot), typeof(MemoryDelegationChild), typeof(MemoryDelegationEffect), typeof(MemoryDelegationProtection), typeof(MemoryDelegationPublication), typeof(MemoryDelegationToolResult), typeof(MemoryDelegationUsageCommit) })
            Assert.Null(ArcanumJsonContext.Default.GetTypeInfo(type));
    }

    [SkippableFact]
    public async Task Only_bound_root_advertises_selector_and_consumption_is_once()
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();

        await using var root = await h.PrepareAsync();

        var parent = new PingRequest("parent", "model", "");

        var binding = new MemoryDelegationRootBinding(parent, h.Source.Invocation, h.RootId, root, h.Coordinator);

        CapturingRunner runner = new();

        var tool = new ArcanumDelegateTaskTool(runner, "model", binding);

        Assert.Equal("memory", tool.JsonSchema.GetProperty("properties").GetProperty("memory_grant").GetProperty("enum")[0].GetString());

        await tool.InvokeAsync(Arguments("memory"));

        Assert.True(root.IsConsumed);

        Assert.NotNull(runner.Request!.MemoryDelegation);

        Assert.Equal(runner.Request.MemoryDelegation.RunId, runner.Request.MemoryDelegation.Child.ChildId);

        Assert.Equal("actual task", runner.Request.Prompt);

        await tool.InvokeAsync(Arguments("memory"));

        Assert.Equal(1, runner.Calls);
    }

    [SkippableFact]
    public async Task Missing_selector_is_ordinary_child_and_prompt_is_unchanged()
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();

        await using var root = await h.PrepareAsync();

        var parent = new PingRequest("parent", "model", "");

        var binding = new MemoryDelegationRootBinding(parent, h.Source.Invocation, h.RootId, root, h.Coordinator);

        CapturingRunner runner = new();

        await new ArcanumDelegateTaskTool(runner, "model", binding).InvokeAsync(Arguments(null));

        Assert.False(root.IsConsumed);

        Assert.Null(runner.Request!.MemoryDelegation);

        Assert.Equal("actual task", runner.Request.Prompt);
    }

    [SkippableFact]
    public async Task Root_capture_requires_exact_request_and_invocation_once_and_child_masks_root()
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();

        await using var root = await h.PrepareAsync();

        var parent = new PingRequest("parent", "model", "");

        var binding = new MemoryDelegationRootBinding(parent, h.Source.Invocation, h.RootId, root, h.Coordinator);

        using var scope = MemoryDelegationRootBinding.Enter(binding);

        Assert.Null(MemoryDelegationRootBinding.TryCapture(parent with { }, h.Source.Invocation));

        Assert.Null(MemoryDelegationRootBinding.TryCapture(parent, ArcanumInvocationContext.None));

        using (SubagentExecutionAmbient.EnterChild(new(10, null)))
        {
            Assert.Null(MemoryDelegationRootBinding.TryCapture(parent, h.Source.Invocation));
        }

        Assert.Same(binding, MemoryDelegationRootBinding.TryCapture(parent, h.Source.Invocation));

        Assert.Null(MemoryDelegationRootBinding.TryCapture(parent, h.Source.Invocation));
    }

    [SkippableFact]
    public async Task Iterator_yield_cannot_swap_root_binding()
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();

        await using var root = await h.PrepareAsync();

        var parent = new PingRequest("parent", "model", "");

        var binding = new MemoryDelegationRootBinding(parent, h.Source.Invocation, h.RootId, root, h.Coordinator);

        using var scope = MemoryDelegationRootBinding.Enter(binding);

        var captured = MemoryDelegationRootBinding.TryCapture(parent, h.Source.Invocation);

        CapturingRunner runner = new();

        async IAsyncEnumerable<ArcanumDelegateTaskTool> Tools()
        {
            using var changed = MemoryDelegationRootBinding.Enter(null);

            await Task.Yield();

            yield return new(runner, "model", captured);
        }

        await foreach (var tool in Tools()) await tool.InvokeAsync(Arguments("memory"));

        Assert.True(root.IsConsumed);

        Assert.NotNull(runner.Request!.MemoryDelegation);
    }

    private static AIFunctionArguments Arguments(string? selector)
    {
        var values = new Dictionary<string, object?> { ["prompt"] = "  actual task  ", ["max_tokens"] = 100 };

        if (selector is not null) values["memory_grant"] = selector;

        return new(values);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("other")]
    public async Task Unbound_tool_cannot_select_any_grant(string selector)
    {
        CapturingRunner runner = new();

        var tool = new ArcanumDelegateTaskTool(runner, "model");

        await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["prompt"] = "ordinary task",
            ["max_tokens"] = 100,
            ["memory_grant"] = selector,
        }));

        Assert.Equal(0, runner.Calls);

        Assert.False(tool.JsonSchema.GetProperty("properties").TryGetProperty("memory_grant", out _));
    }

    private sealed class CapturingRunner : ISubagentRunner
    {
        public int Calls { get; private set; }

        public SubagentRunRequest? Request { get; private set; }

        public Task<SubagentRunResult> RunAsync(SubagentRunRequest request, CancellationToken cancellationToken)
        {
            Calls++;

            Request = request;

            return Task.FromResult(new SubagentRunResult(true, "summary", Guid.NewGuid(), new(1, 0m, 1, false), null));
        }
    }
}
