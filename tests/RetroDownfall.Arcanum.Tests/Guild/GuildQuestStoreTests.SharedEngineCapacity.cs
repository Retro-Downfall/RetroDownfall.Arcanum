using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Guild_shared_engine_parallel_delegate_wait_yields_only_after_every_active_provider_turn_blocks()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var row = await ReceiptRootAsync(f);

        var input = LocalInput(row) with { Mode = GuildLocalExecutionMode.Apprentice };

        var binding = await CreateLocalAsync(f, row, input);

        f.UseLocalAdmission(new LocalAdmission());

        var driver = await f.DriverAsync(row);

        var dispatch = await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(), ReceiptDigest("parallel capacity"), row.Quest.RunId, false, default);

        Assert.True(dispatch.IsSuccess, dispatch.Error.Message);

        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();

        DefaultApprenticeExecutionCapacity capacity = new();

        EngineOptions options = new();

        options.CurrentValue.Execution.MaxConcurrentApprentices = 1;

        options.CurrentValue.Execution.MaxConcurrentApprenticeBranches = 2;

        using ApprenticeService service = new(services.GetRequiredService<IServiceScopeFactory>(), options, new(),
            NullLogger<ApprenticeService>.Instance, new GrimoireConnectionAdmissionGate(TimeProvider.System), capacity);

        ParallelCapacityRuntime runtime = new();

        Task<Result<PromptTurnResult>> running = service.ExecuteGuildAsync(binding, input, new(f.Store, driver, dispatch.Value), runtime,
            new TestGuildEngineWait(static (_, _, _) => throw new InvalidOperationException("Unexpected wait")), default);

        try
        {
            await runtime.FirstSuspended.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, capacity.RunningCount);

            Assert.False(capacity.TryAcquire(1, out _));

            runtime.SuspendSecond.TrySetResult();

            await runtime.BothSuspended.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0, capacity.RunningCount);

            Assert.True(capacity.TryAcquire(1, out AdmissionGate.Lease? childSlot));

            using (childSlot)
            {
                runtime.ReturnChildren.TrySetResult();

                await runtime.ResumeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.Equal(0, runtime.Resumed);

                Assert.False(running.IsCompleted);
            }

            var result = await running.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(result.IsSuccess, result.Error.Message);

            Assert.Equal(2, runtime.Resumed);

            Assert.Equal(0, capacity.RunningCount);
        }
        finally
        {
            runtime.SuspendSecond.TrySetResult();

            runtime.ReturnChildren.TrySetResult();
        }
    }

    private sealed class ParallelCapacityRuntime : IGuildLocalInferenceRuntime
    {
        private int _calls;

        internal int Resumed;

        private readonly TaskCompletionSource _secondEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource FirstSuspended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource SuspendSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource BothSuspended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReturnChildren { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ResumeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Result<PromptTurnResult>> ExecuteAsync(GuildLocalInferenceExecution execution, GuildLocalExecutionInput input, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Expected the shared planner and unit engine");

        public async Task<Result<PromptTurnResult>> ExecuteTurnAsync(GuildLocalInferenceExecution execution, GuildLocalExecutionInput input,
            GuildLocalInferenceTurn turn, CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref _calls);

            if (call == 1)
                return Result<PromptTurnResult>.Success(new("[{\"index\":1,\"description\":\"First branch\",\"isParallel\":true},{\"index\":2,\"description\":\"Second branch\",\"isParallel\":true}]", null));

            if (call is 2 or 3)
            {
                if (call == 2) await _secondEntered.Task.WaitAsync(cancellationToken);
                else
                {
                    _secondEntered.TrySetResult();

                    await SuspendSecond.Task.WaitAsync(cancellationToken);
                }

                IAsyncDisposable wait = await execution.SuspendForChildAsync(cancellationToken);

                if (call == 2) FirstSuspended.TrySetResult();
                else BothSuspended.TrySetResult();

                await ReturnChildren.Task.WaitAsync(cancellationToken);

                ValueTask resume = wait.DisposeAsync();

                ResumeStarted.TrySetResult();

                await resume;

                Interlocked.Increment(ref Resumed);
            }

            return Result<PromptTurnResult>.Success(new("public branch result", null));
        }
    }
}
