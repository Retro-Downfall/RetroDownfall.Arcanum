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

        using HttpClient client = new(handler) { Timeout = Timeout.InfiniteTimeSpan };

        Task old = Assert.IsAssignableFrom<Task>(tracker.Start("task", new object(), async ct =>
        {
            try
            {
                using HttpResponseMessage response = await client.GetAsync("https://callback.example/", ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }));

        Task? replacement = null;

        Task? stopping = null;

        Task? late = null;

        bool escapedOldSend = false;

        int lateCalls = 0;

        try
        {
            await handler.Entered.Task;

            replacement = Assert.IsAssignableFrom<Task>(tracker.Start("task", new object(), _ => Task.CompletedTask));

            await replacement;

            stopping = tracker.DisposeAsync().AsTask();

            await handler.CancellationObserved.Task;

            escapedOldSend = stopping.IsCompleted;

            late = tracker.Start("late-task", new object(), _ =>
            {
                Interlocked.Increment(ref lateCalls);

                return Task.CompletedTask;
            });
        }
        finally
        {
            handler.Release.TrySetResult(new(HttpStatusCode.OK));

            List<Task> owned = [old];

            if (replacement is not null) owned.Add(replacement);

            if (stopping is not null) owned.Add(stopping);

            if (late is not null) owned.Add(late);

            await Task.WhenAll(owned);
        }

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

        try
        {
            finiteRequest.Value = null;
        }
        finally
        {
            release.TrySetResult();

            await background;
        }

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

        Task? second = null;

        try
        {
            second = Assert.IsAssignableFrom<Task>(tracker.Start("task", peer, Work));
        }
        finally
        {
            release.TrySetResult();

            List<Task> owned = [first];

            if (second is not null) owned.Add(second);

            await Task.WhenAll(owned);
        }

        Assert.Same(first, second);

        Assert.Equal(1, calls);
    }

    private sealed class HeldCallbackHandler : HttpMessageHandler
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<HttpResponseMessage> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using CancellationTokenRegistration registration = cancellationToken.Register(() => CancellationObserved.TrySetResult());

            Entered.TrySetResult();

            return await Release.Task;
        }
    }
}
