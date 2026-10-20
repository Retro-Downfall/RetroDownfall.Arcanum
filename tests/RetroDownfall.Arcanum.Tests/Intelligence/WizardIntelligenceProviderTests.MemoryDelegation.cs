using Microsoft.Extensions.AI;
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
