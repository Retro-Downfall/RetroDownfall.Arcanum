namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

public sealed partial class TapestryRecursiveScaleProbe
{
    [Fact]
    public async Task Resource_sampling_excludes_concurrent_reader_access()
    {
        using ManualResetEventSlim firstEntered = new();

        using ManualResetEventSlim releaseFirst = new();

        using ManualResetEventSlim secondInvoked = new();

        using ManualResetEventSlim secondRead = new();

        int reads = 0;

        int active = 0;

        int maximumActive = 0;

        using ConsumerBudget budget = new(30, 1024, CancellationToken.None, new ControlledResourceReader(() =>
        {
            int ordinal = Interlocked.Increment(ref reads);

            if (ordinal == 1) return 128;

            int concurrent = Interlocked.Increment(ref active);

            Interlocked.Exchange(ref maximumActive, Math.Max(maximumActive, concurrent));

            try
            {
                if (ordinal == 2)
                {
                    firstEntered.Set();

                    Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(5)));
                }
                else secondRead.Set();

                return 256;
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }, () => { }), false);

        Task first = Task.Run(budget.SampleResources);

        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(5)));

        Task second = Task.Run(() => { secondInvoked.Set(); budget.SampleResources(); });

        bool overlapped;

        try
        {
            Assert.True(secondInvoked.Wait(TimeSpan.FromSeconds(5)));

            // This is a negative observation window after both callers started, never a positive completion deadline.
            overlapped = secondRead.Wait(TimeSpan.FromMilliseconds(200));
        }
        finally
        {
            releaseFirst.Set();

            await Task.WhenAll(first, second);
        }

        Assert.False(overlapped, "Two callbacks accessed the same stateful resource reader concurrently.");

        Assert.Equal(1, maximumActive);

        Assert.Equal(256, budget.PeakWorkingSet);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("caller")]
    [InlineData("time")]
    [InlineData("memory")]
    public void Resource_sampling_failure_is_fail_closed_and_preserves_first_cause(string firstCause)
    {
        using CancellationTokenSource caller = new();

        int reads = 0;

        using ConsumerBudget budget = new(30, 1024, caller.Token, new ControlledResourceReader(() =>
        {
            if (Interlocked.Increment(ref reads) == 1) return 128;

            throw new InvalidOperationException("private-resource-failure-canary");
        }, () => { }), false);

        if (firstCause == "caller") caller.Cancel();

        else if (firstCause == "time") budget.ObserveResources(128, 30);

        else if (firstCause == "memory") budget.ObserveResources(1025, 0);

        Exception? escaped = Record.Exception(budget.SampleResources);

        Assert.Null(escaped);

        Assert.True(budget.Token.IsCancellationRequested);

        Assert.Equal(firstCause == "none" ? "resource-unavailable" : firstCause, budget.Reason);

        Assert.Equal(128, budget.PeakWorkingSet);
    }

    [Fact]
    public void Resource_reader_is_disposed_when_initial_observation_fails()
    {
        int disposals = 0;

        ControlledResourceReader reader = new(() => throw new InvalidOperationException("initial-private-canary"),
            () => disposals++);

        _ = Assert.Throws<InvalidOperationException>(() => new ConsumerBudget(30, 1024, CancellationToken.None, reader, false));

        Assert.Equal(1, disposals);
    }

    [Fact]
    public async Task Resource_reader_outlives_an_inflight_timer_callback_and_is_disposed_once()
    {
        using ManualResetEventSlim entered = new();

        using ManualResetEventSlim release = new();

        using ManualResetEventSlim disposing = new();

        int reads = 0;

        int disposed = 0;

        ConsumerBudget budget = new(30, 1024, CancellationToken.None, new ControlledResourceReader(() =>
        {
            if (Interlocked.Increment(ref reads) == 1) return 128;

            entered.Set();

            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));

            Assert.Equal(0, Volatile.Read(ref disposed));

            return 256;
        }, () => Interlocked.Increment(ref disposed)), true);

        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        Task disposal = Task.Run(() => { disposing.Set(); budget.Dispose(); });

        try
        {
            Assert.True(disposing.Wait(TimeSpan.FromSeconds(5)));

            Assert.Equal(0, Volatile.Read(ref disposed));
        }
        finally
        {
            release.Set();

            await disposal;
        }

        Assert.Equal(1, disposed);
    }

    [Fact]
    public void Resource_reader_samples_the_actual_current_process()
    {
        using ConsumerBudget budget = new(30, 6L * 1024 * 1024 * 1024, CancellationToken.None);

        Assert.True(budget.StartWorkingSet > 0);

        budget.SampleResources();

        Assert.True(budget.PeakWorkingSet >= budget.StartWorkingSet);

        Assert.False(budget.Token.IsCancellationRequested);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("time")]
    [InlineData("caller")]
    public void Resource_budget_cancellation_callback_fault_does_not_escape_or_erase_containment(string cause)
    {
        using CancellationTokenSource caller = new();

        using ConsumerBudget budget = new(30, 1024, caller.Token,
            new ControlledResourceReader(() => 128, () => { }), false);

        using CancellationTokenRegistration registration = budget.Token.Register(() =>
            throw new InvalidOperationException("private-cancellation-callback-canary"));

        Exception? escaped = Record.Exception(() =>
        {
            if (cause == "memory") budget.ObserveResources(1025, 0);

            else if (cause == "time") budget.ObserveResources(128, 30);

            else caller.Cancel();
        });

        Assert.Null(escaped);

        Assert.True(budget.Token.IsCancellationRequested);

        Assert.Equal(cause, budget.Reason);

        Assert.True(budget.CallbackFailed);
    }

    [Fact]
    public async Task Resource_sampling_keeps_the_deadline_active_while_another_read_is_held()
    {
        using ManualResetEventSlim entered = new();

        using ManualResetEventSlim release = new();

        int reads = 0;

        using ConsumerBudget budget = new(0.02, 1024, CancellationToken.None, new ControlledResourceReader(() =>
        {
            if (Interlocked.Increment(ref reads) == 2)
            {
                entered.Set();

                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            }

            return 128;
        }, () => { }), false);

        Task first = Task.Run(budget.SampleResources);

        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        Task second = Task.Run(async () => { await Task.Delay(50); budget.SampleResources(); });

        try
        {
            Assert.True(budget.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(2)), "A held resource read cannot block the clock deadline.");

            Assert.Equal("time", budget.Reason);
        }
        finally
        {
            release.Set();

            await Task.WhenAll(first, second);
        }
    }

    [Fact]
    public async Task Resource_sampling_retains_an_actual_overage_after_a_skipped_overlapping_tick()
    {
        using ManualResetEventSlim entered = new();

        using ManualResetEventSlim release = new();

        int reads = 0;

        using ConsumerBudget budget = new(30, 1024, CancellationToken.None, new ControlledResourceReader(() =>
        {
            if (Interlocked.Increment(ref reads) == 1) return 128;

            entered.Set();

            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));

            return 1025;
        }, () => { }), false);

        Task first = Task.Run(budget.SampleResources);

        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        Task second = Task.Run(budget.SampleResources);

        try
        {
            await second.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(2, Volatile.Read(ref reads));
        }
        finally
        {
            release.Set();

            await Task.WhenAll(first, second);
        }

        Assert.True(budget.Token.IsCancellationRequested);

        Assert.Equal("memory", budget.Reason);

        Assert.Equal(1025, budget.PeakWorkingSet);
    }

    [Fact]
    public async Task Resource_sampling_failure_produces_an_incomplete_recursive_receipt_without_private_error_text()
    {
        using StringWriter receipt = new(System.Globalization.CultureInfo.InvariantCulture);

        int budgets = 0;

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunRecursiveAsync(24,
            new ConsumerLimits(1, 1, 1, 3, 1024), receipt, (seconds, ceiling, caller) =>
            {
                int reads = 0;

                ConsumerBudget budget = new(seconds, ceiling, caller, new ControlledResourceReader(() =>
                {
                    if (Interlocked.Increment(ref reads) == 1) return 128;

                    throw new InvalidOperationException("private-recursive-resource-canary");
                }, () => { }), false);

                if (Interlocked.Increment(ref budgets) == 1) budget.SampleResources();

                return budget;
            }));

        Assert.Contains("outcome=incomplete reason=resource-unavailable", receipt.ToString(), StringComparison.Ordinal);

        Assert.DoesNotContain("outcome=accepted", receipt.ToString(), StringComparison.Ordinal);

        Assert.DoesNotContain("private-recursive-resource-canary", receipt.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, _summary.Calls);

        Assert.Equal(0, _embedding.BatchCalls);

        Assert.Null(await _store.GetCurrentGenerationAsync(Scope, CancellationToken.None));
    }

    private sealed class ControlledResourceReader(Func<long> read, Action dispose) : IConsumerResourceReader
    {
        public long ReadWorkingSet() => read();

        public void Dispose() => dispose();
    }
}
