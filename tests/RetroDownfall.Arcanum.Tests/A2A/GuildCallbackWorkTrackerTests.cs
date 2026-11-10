using System.Net;
using RetroDownfall.Arcanum.Infrastructure.A2A;

namespace RetroDownfall.Arcanum.Tests.A2A;

public sealed class GuildCallbackWorkTrackerTests
{
    [Fact]
    public async Task Disposal_joins_replaced_callback_until_its_physical_HTTP_send_finishes()
    {
        await using GuildCallbackWorkTracker tracker = new();

        using HeldCallbackHandler handler = new();

        using HttpClient client = new(handler);

        Task old = Assert.IsAssignableFrom<Task>(tracker.Start("task", new object(), async ct =>
        {
            try
            {
                using HttpResponseMessage response = await client.GetAsync("https://callback.example/", ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }));

        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Task replacement = Assert.IsAssignableFrom<Task>(tracker.Start("task", new object(), _ => Task.CompletedTask));

        await replacement;

        Task stopping = tracker.DisposeAsync().AsTask();

        Task firstCompleted = await Task.WhenAny(stopping, Task.Delay(TimeSpan.FromMilliseconds(100)));

        bool escapedOldSend = ReferenceEquals(firstCompleted, stopping);

        int lateCalls = 0;

        Task? late = tracker.Start("late-task", new object(), _ =>
        {
            Interlocked.Increment(ref lateCalls);

            return Task.CompletedTask;
        });

        handler.Release.TrySetResult(new(HttpStatusCode.OK));

        await old;

        await stopping;

        if (late is not null) await late;

        Assert.False(escapedOldSend);

        Assert.Null(late);

        Assert.Equal(0, lateCalls);

        Assert.Null(tracker.Start("after-stop", new object(), _ => Task.CompletedTask));
    }

    [Fact]
    public async Task Long_lived_callback_work_does_not_capture_the_ended_HTTP_request_context()
    {
        await using GuildCallbackWorkTracker tracker = new();

        AsyncLocal<string?> finiteRequest = new() { Value = "request-owned-admission" };

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        string? observed = "not-run";

        Task background = Assert.IsAssignableFrom<Task>(tracker.Start("task", new object(), async _ =>
        {
            await release.Task;

            observed = finiteRequest.Value;
        }));

        finiteRequest.Value = null;

        release.TrySetResult();

        await background;

        Assert.Null(observed);
    }

    [Fact]
    public async Task Same_peer_and_task_coalesce_one_physical_watcher()
    {
        await using GuildCallbackWorkTracker tracker = new();

        object peer = new();

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        int calls = 0;

        Task Work(CancellationToken _)
        {
            Interlocked.Increment(ref calls);

            return release.Task;
        }

        Task first = Assert.IsAssignableFrom<Task>(tracker.Start("task", peer, Work));

        Task second = Assert.IsAssignableFrom<Task>(tracker.Start("task", peer, Work));

        release.TrySetResult();

        await Task.WhenAll(first, second);

        Assert.Same(first, second);

        Assert.Equal(1, calls);
    }

    private sealed class HeldCallbackHandler : HttpMessageHandler
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<HttpResponseMessage> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();

            return Release.Task;
        }
    }
}
