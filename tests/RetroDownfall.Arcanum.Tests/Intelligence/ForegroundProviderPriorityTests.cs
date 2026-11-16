using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence.Subagents;
using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    // Removing turn-lifetime backend ownership must let the Tapestry send overtake the
    // foreground continuation, even though its configured provider/model names differ.
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, true)]
    public async Task Foreground_provider_priority_preserves_the_tool_gap_against_a_Tapestry_turn(
        bool streaming, bool independentBackend, bool delegateChild, bool holdBeforeFirstSend)
    {
        const string tool = "wait_for_operator_result";

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));

        TaskCompletionSource toolEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource releaseTool = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource foregroundSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource backgroundWaiting = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource backgroundDispatched = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource primaryResolved = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource releasePrimaryResolution = new(TaskCreationOptions.RunContinuationsAsynchronously);

        AsyncLocal<bool> observingBackground = new();

        using MeterListener listener = new();

        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (instrument.Meter.Name == "RetroDownfall.Arcanum.ProviderWork"
                && instrument.Name == "arcanum.provider_work.waiting")
            {
                observer.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            if (!observingBackground.Value) return;

            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "kind" && Equals(tag.Value, "background")) backgroundWaiting.TrySetResult();
            }
        });

        listener.Start();

        ConcurrentQueue<string> sends = new();

        ProviderSettings primary = DefaultProvider() with
        {
            Name = "interactive-local",
            Endpoint = "http://127.0.0.1:11434/v1/",
            LocalRuntimeId = "primary-runtime",
        };

        ProviderSettings maintenance = primary with
        {
            Name = "maintenance-local",
            Endpoint = independentBackend ? "http://127.0.0.1:11435/v1/" : primary.Endpoint,
            LocalRuntimeId = independentBackend ? "independent-runtime" : primary.LocalRuntimeId,
            Models = ["small-summary-model"],
        };

        ArcanumSettings settings = DefaultSettings() with
        {
            Providers = [primary, maintenance],
            FastModel = "small-summary-model",
            LocalRuntimes =
            [
                new() { Id = "primary-runtime", Adapter = LocalRuntimeAdapterCatalog.Ollama, ManagementEndpoint = "http://127.0.0.1:11434" },
                new() { Id = "independent-runtime", Adapter = LocalRuntimeAdapterCatalog.Ollama, ManagementEndpoint = "http://127.0.0.1:11435" },
            ],
        };

        ModelTokenEstimator estimator = new(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance));

        using ProviderWorkCoordinator coordinator = new();

        ModelCallExecutor executor = new(estimator, new TestOptionsMonitor<ArcanumSettings>(settings), coordinator);

        ScriptingChatClient childChat = new();

        childChat.BeforeCall = () => sends.Enqueue("child");

        childChat.EnqueueText("child completed");

        WizardIntelligenceProvider childWizard = CreateWizard(childChat, settings, executor: executor);

        using ServiceProvider operationServices = new ServiceCollection().BuildServiceProvider();

        SubagentRunner childRunner = new(
            new Lazy<ITurnExecutionFacade>(() => new ChildWizardTurnFacade(childWizard, () => { })),
            new AcquiringOperationCoordinator(), TimeProvider.System, NullLogger<SubagentRunner>.Instance,
            operationServices.GetRequiredService<IServiceScopeFactory>());

        ScriptingChatClient foregroundChat = new();

        int round = 0;

        foregroundChat.BeforeCall = () => sends.Enqueue($"foreground-{Interlocked.Increment(ref round)}");

        if (streaming)
        {
            foregroundChat.EnqueueStreamToolCall(tool, "held-tool");

            foregroundChat.EnqueueStreamTokens("foreground completed");
        }
        else
        {
            foregroundChat.EnqueueToolCall(tool, "held-tool");

            foregroundChat.EnqueueText("foreground completed");
        }

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(AIFunctionFactory.Create(async () =>
        {
            toolEntered.TrySetResult();

            await releaseTool.Task.WaitAsync(deadline.Token);

            if (delegateChild)
            {
                SubagentRunResult child = await childRunner.RunAsync(
                    new SubagentRunRequest("Complete the child task.", ModelName, [], MaxTokens: 1_000, MaxCostUsd: null),
                    deadline.Token);

                Assert.True(child.Success, child.FailureCode);

                Assert.Equal("child completed", child.Summary);
            }

            return "operator result";
        }, tool, "A controlled tool whose result has not arrived yet."));

        WizardIntelligenceProvider foreground = CreateWizard(foregroundChat, settings, mcp: mcp, executor: executor,
            factory: holdBeforeFirstSend
                ? new HeldPriorityClientFactory(foregroundChat, primary, primaryResolved, releasePrimaryResolution)
                : null);

        ScriptingChatClient backgroundChat = new();

        backgroundChat.EnqueueBufferedResponder(async cancellationToken =>
        {
            sends.Enqueue("maintenance");

            backgroundDispatched.TrySetResult();

            await foregroundSettled.Task.WaitAsync(cancellationToken);

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "a corpus summary"));
        });

        WizardIntelligenceProvider background = CreateWizard(backgroundChat, settings,
            factory: new FakeChatClientFactory(backgroundChat, maintenance), executor: executor);

        TapestrySummarizer summarizer = new(background, estimator,
            new TestOptionsMonitor<ArcanumSettings>(settings), NullLogger<TapestrySummarizer>.Instance);

        async Task RunForegroundAsync()
        {
            try
            {
                PingRequest request = BaseRequest() with { Prompt = "Use the operator result.", SkipSpellRouting = true };

                if (streaming)
                {
                    List<IntelligenceEvent> events = [];

                    await foreach (IntelligenceEvent item in foreground.StreamPromptAsync(request,
                        InvocationContexts.AttendedSession(), deadline.Token))
                    {
                        events.Add(item);
                    }

                    Assert.Contains(events, item => item.Type == IntelligenceEventType.Result);
                }
                else
                {
                    Result<PromptTurnResult> result = await foreground.ExecutePromptAsync(request,
                        InvocationContexts.AttendedSession(), deadline.Token);

                    Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

                    Assert.Equal("foreground completed", result.Value.Text);
                }
            }
            finally
            {
                foregroundSettled.TrySetResult();
            }
        }

        Task foregroundRun = RunForegroundAsync();

        Task<Result<string>>? backgroundRun = null;

        try
        {
            await (holdBeforeFirstSend ? primaryResolved.Task : toolEntered.Task).WaitAsync(deadline.Token);

            observingBackground.Value = true;

            try
            {
                backgroundRun = summarizer.SummarizeAsync(
                    new TapestrySummaryRequest(TapestryScopeKind.Workspace, "independent corpus", 1, ["A source paragraph."]),
                    deadline.Token);
            }
            finally
            {
                observingBackground.Value = false;
            }

            if (independentBackend)
            {
                await backgroundDispatched.Task.WaitAsync(deadline.Token);

                Assert.Equal(["foreground-1", "maintenance"], sends.ToArray());

                Assert.False(backgroundWaiting.Task.IsCompleted);
            }
            else
            {
                Task first = await Task.WhenAny(backgroundWaiting.Task, backgroundDispatched.Task).WaitAsync(deadline.Token);

                Assert.Same(backgroundWaiting.Task, first);

                Assert.Equal(holdBeforeFirstSend ? [] : new[] { "foreground-1" }, sends.ToArray());
            }

            releasePrimaryResolution.TrySetResult();

            await toolEntered.Task.WaitAsync(deadline.Token);

            releaseTool.TrySetResult();

            await foregroundRun.WaitAsync(deadline.Token);

            Result<string> summary = await backgroundRun.WaitAsync(deadline.Token);

            Assert.True(summary.IsSuccess, summary.IsFailure ? summary.Error.Message : null);

            Assert.Equal("a corpus summary", summary.Value);

            string[] expected = independentBackend
                ? ["foreground-1", "maintenance", "foreground-2"]
                : delegateChild ? ["foreground-1", "child", "foreground-2", "maintenance"]
                : ["foreground-1", "foreground-2", "maintenance"];

            Assert.Equal(expected, sends.ToArray());
        }
        finally
        {
            releaseTool.TrySetResult();

            releasePrimaryResolution.TrySetResult();

            foregroundSettled.TrySetResult();

            await deadline.CancelAsync();

            try
            {
                await foregroundRun;
            }
            catch (OperationCanceledException)
            {
            }

            if (backgroundRun is not null)
            {
                try
                {
                    await backgroundRun;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    private sealed class HeldPriorityClientFactory(
        IChatClient client, ProviderSettings provider, TaskCompletionSource entered, TaskCompletionSource release) : IChatClientFactory
    {
        public async Task<ChatClientLease> ResolveClientAsync(string? targetModel, CancellationToken cancellationToken)
        {
            entered.TrySetResult();

            await release.Task.WaitAsync(cancellationToken);

            return new ChatClientLease(client, provider, provider.Models[0].Name, ownedHttpClient: null);
        }

        public Task<ChatClientLease> ResolveClientAsync(ProviderSettings selected, string model, CancellationToken cancellationToken) =>
            ResolveClientAsync(model, cancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Foreground_provider_priority_waits_for_an_active_background_send_without_canceling_it(bool cancelForeground)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));

        using CancellationTokenSource foregroundCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);

        using ProviderWorkCoordinator coordinator = new();

        TaskCompletionSource backgroundEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource releaseBackground = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource foregroundWaiting = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource foregroundDispatched = new(TaskCreationOptions.RunContinuationsAsynchronously);

        AsyncLocal<bool> observingForeground = new();

        using MeterListener listener = new();

        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (instrument.Meter.Name == "RetroDownfall.Arcanum.ProviderWork"
                && instrument.Name == "arcanum.provider_work.waiting") observer.EnableMeasurementEvents(instrument);
        };

        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            if (!observingForeground.Value) return;

            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "kind" && Equals(tag.Value, "foreground")) foregroundWaiting.TrySetResult();
            }
        });

        listener.Start();

        ProviderSettings primary = DefaultProvider() with
        {
            Endpoint = "http://127.0.0.1:11434/v1/",
            LocalRuntimeId = "primary-runtime",
        };

        ProviderSettings maintenance = primary with { Name = "maintenance", Models = ["small-summary-model"] };

        ArcanumSettings settings = DefaultSettings() with
        {
            Providers = [primary, maintenance],
            FastModel = "small-summary-model",
            LocalRuntimes =
            [
                new() { Id = "primary-runtime", Adapter = LocalRuntimeAdapterCatalog.Ollama, ManagementEndpoint = "http://127.0.0.1:11434" },
            ],
        };

        ModelTokenEstimator estimator = new(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance));

        ModelCallExecutor executor = new(estimator, new TestOptionsMonitor<ArcanumSettings>(settings), coordinator);

        ScriptingChatClient backgroundChat = new();

        CancellationToken backgroundToken = default;

        backgroundChat.EnqueueBufferedResponder(async token =>
        {
            backgroundToken = token;

            backgroundEntered.TrySetResult();

            await releaseBackground.Task.WaitAsync(token);

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "settled summary"));
        });

        WizardIntelligenceProvider background = CreateWizard(backgroundChat, settings,
            factory: new FakeChatClientFactory(backgroundChat, maintenance), executor: executor);

        TapestrySummarizer summarizer = new(background, estimator,
            new TestOptionsMonitor<ArcanumSettings>(settings), NullLogger<TapestrySummarizer>.Instance);

        ScriptingChatClient foregroundChat = new();

        foregroundChat.BeforeCall = () => foregroundDispatched.TrySetResult();

        foregroundChat.EnqueueStreamTokens("foreground completed");

        WizardIntelligenceProvider foreground = CreateWizard(foregroundChat, settings, executor: executor);

        List<IntelligenceEvent> events = [];

        async Task RunForegroundAsync()
        {
            await foreach (IntelligenceEvent item in foreground.StreamPromptAsync(
                BaseRequest() with { Prompt = "Answer the operator.", SkipSpellRouting = true },
                InvocationContexts.AttendedSession(), foregroundCancellation.Token))
            {
                events.Add(item);
            }
        }

        Task<Result<string>> backgroundRun = summarizer.SummarizeAsync(
            new TapestrySummaryRequest(TapestryScopeKind.Workspace, "prior corpus", 1, ["A source paragraph."]), deadline.Token);

        Task? foregroundRun = null;

        try
        {
            await backgroundEntered.Task.WaitAsync(deadline.Token);

            observingForeground.Value = true;

            try
            {
                foregroundRun = RunForegroundAsync();
            }
            finally
            {
                observingForeground.Value = false;
            }

            Task first = await Task.WhenAny(foregroundWaiting.Task, foregroundDispatched.Task).WaitAsync(deadline.Token);

            Assert.Same(foregroundWaiting.Task, first);

            Assert.Equal(0, foregroundChat.StreamingCallCount);

            Assert.False(backgroundToken.IsCancellationRequested);

            if (cancelForeground)
            {
                await foregroundCancellation.CancelAsync();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => foregroundRun.WaitAsync(deadline.Token));

                Assert.Equal(0, foregroundChat.StreamingCallCount);

                Assert.False(backgroundToken.IsCancellationRequested);

                Assert.False(backgroundRun.IsCompleted);
            }

            releaseBackground.TrySetResult();

            Result<string> summary = await backgroundRun.WaitAsync(deadline.Token);

            Assert.True(summary.IsSuccess, summary.IsFailure ? summary.Error.Message : null);

            Assert.Equal("settled summary", summary.Value);

            if (!cancelForeground)
            {
                await foregroundRun.WaitAsync(deadline.Token);

                Assert.Equal(1, foregroundChat.StreamingCallCount);

                Assert.Contains(events, item => item.Type == IntelligenceEventType.Status
                    && item.Message == "Waiting for background model work to finish.");

                Assert.Contains(events, item => item.Type == IntelligenceEventType.Result);
            }
        }
        finally
        {
            releaseBackground.TrySetResult();

            await deadline.CancelAsync();

            if (foregroundRun is not null)
            {
                try { await foregroundRun; }
                catch (OperationCanceledException) { }
            }

            try { await backgroundRun; }
            catch (OperationCanceledException) { }
        }
    }
}
