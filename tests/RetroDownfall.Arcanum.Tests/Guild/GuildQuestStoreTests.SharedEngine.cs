using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    private sealed class TestGuildEngineWait(Func<GuildLocalInferenceExecution, GuildLocalWaitCheckpointWrite,
        CancellationToken, Task<Result<GuildLocalEngineContinuation>>> callback) : IGuildLocalEngineWait
    {
        public Task<Result<GuildLocalEngineContinuation>> WaitAsync(GuildLocalInferenceExecution execution,
            GuildLocalWaitCheckpointWrite checkpoint, CancellationToken cancellationToken) =>
            callback(execution, checkpoint, cancellationToken);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Guild_shared_engine_awaits_existing_child_and_preserves_content_free_skeleton(bool sterile, bool qualifiedTools)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = await ReceiptRootAsync(f);

        GuildLocalExecutionInput input = LocalInput(row) with { Mode = sterile ? GuildLocalExecutionMode.Sterile : GuildLocalExecutionMode.Apprentice };

        GuildLocalExecutionBinding binding = await CreateLocalAsync(f, row, input);

        f.UseLocalAdmission(new LocalAdmission());

        GuildQuestDriver driver = await f.DriverAsync(row);

        GuildQuestDispatchRecord dispatch = (await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(), ReceiptDigest("shared engine"), row.Quest.RunId, false, default)).Value;

        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();

        ChronicleHub hub = new();

        using ApprenticeService service = new(services.GetRequiredService<IServiceScopeFactory>(), new EngineOptions(), hub,
            NullLogger<ApprenticeService>.Instance, new GrimoireConnectionAdmissionGate(TimeProvider.System));

        EngineRuntime runtime = new() { AssertQualifiedTools = qualifiedTools };

        using CancellationTokenSource subscription = new();

        await using var events = hub.SubscribeAsync(binding.ApprenticeId, subscription.Token).GetAsyncEnumerator();

        Task<bool> nextEvent = events.MoveNextAsync().AsTask();

        var result = await service.ExecuteGuildAsync(binding, input, new(f.Store, driver, dispatch), runtime,
            new TestGuildEngineWait(static (_, _, _) => throw new InvalidOperationException("Unexpected input wait")), default);

        hub.Publish(binding.ApprenticeId, new ApprenticeEvent { ApprenticeId = binding.ApprenticeId, Summary = "outside the Guild scope" });

        Assert.True(await nextEvent.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal("outside the Guild scope", events.Current.Summary);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal("public final answer", result.Value.Text);

        Assert.Equal(sterile ? 1 : 0, runtime.SterileCalls);

        Assert.Equal(sterile ? 0 : 3, runtime.TurnCalls);

        Assert.Equal(1L, await f.CountAsync("Apprentices"));

        Assert.Equal(1L, await f.CountAsync("LongRunningOperations"));

        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT Goal,Plan,CheckpointData FROM Apprentices";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());

        Assert.Equal("Guild-owned execution.", reader.GetString(0));

        Assert.Equal("[]", reader.GetString(1));

        Assert.DoesNotContain("public final answer", reader.GetString(2), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Guild_shared_engine_does_not_retry_uncertain_planner_failure(int failAt)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = await ReceiptRootAsync(f);

        GuildLocalExecutionInput input = LocalInput(row) with { Mode = GuildLocalExecutionMode.Apprentice };

        GuildLocalExecutionBinding binding = await CreateLocalAsync(f, row, input);

        f.UseLocalAdmission(new LocalAdmission());

        GuildQuestDriver driver = await f.DriverAsync(row);

        GuildQuestDispatchRecord dispatch = (await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(), ReceiptDigest("uncertain engine"), row.Quest.RunId, false, default)).Value;

        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();

        using ApprenticeService service = new(services.GetRequiredService<IServiceScopeFactory>(), new EngineOptions(), new(),
            NullLogger<ApprenticeService>.Instance, new GrimoireConnectionAdmissionGate(TimeProvider.System));

        EngineRuntime runtime = new() { FailAt = failAt };

        var result = await service.ExecuteGuildAsync(binding, input, new(f.Store, driver, dispatch), runtime,
            new TestGuildEngineWait(static (_, _, _) => throw new InvalidOperationException("Unexpected input wait")), default);

        Assert.True(result.IsFailure);

        Assert.Equal("fixture.uncertain", result.Error.Code);

        Assert.Equal(failAt, runtime.TurnCalls);
    }

    [Fact]
    public async Task Guild_shared_engine_refuses_prepared_dispatch_without_its_committed_child_association()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = await ReceiptRootAsync(f);

        GuildLocalExecutionInput input = LocalInput(row);

        GuildLocalExecutionBinding binding = await CreateLocalAsync(f, row, input);

        GuildQuestDriver driver = await f.DriverAsync(row);

        GuildQuestDispatchRecord dispatch = (await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(), ReceiptDigest("unassociated engine"), row.Quest.RunId, false, default)).Value;

        Assert.Null(dispatch.LocalChildId);

        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();

        using ApprenticeService service = new(services.GetRequiredService<IServiceScopeFactory>(), new EngineOptions(), new(),
            NullLogger<ApprenticeService>.Instance, new GrimoireConnectionAdmissionGate(TimeProvider.System));

        EngineRuntime runtime = new();

        var result = await service.ExecuteGuildAsync(binding, input, new(f.Store, driver, dispatch), runtime,
            new TestGuildEngineWait(static (_, _, _) => throw new InvalidOperationException("Unexpected input wait")), default);

        Assert.True(result.IsFailure);

        Assert.Equal(0, runtime.SterileCalls);
    }

    [Fact]
    public async Task Guild_shared_engine_refuses_sterile_continuation_without_replaying_initial_provider_call()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = await ReceiptRootAsync(f);

        GuildLocalExecutionInput input = LocalInput(row);

        GuildLocalExecutionBinding binding = await CreateLocalAsync(f, row, input);

        f.UseLocalAdmission(new LocalAdmission());

        GuildQuestDriver driver = await f.DriverAsync(row);

        Guid dispatchId = Guid.NewGuid(), continuation = Guid.NewGuid();

        var initial = await f.Store.PrepareDispatchAsync(driver, dispatchId, binding.Input.Digest, row.Quest.RunId, false, default);

        Assert.True(initial.IsSuccess, initial.Error.Message);

        var waiting = await f.Store.WaitForInputAsync(driver, Guid.NewGuid(), continuation, new(dispatchId, WaitSnapshot(binding)), default);

        Assert.True(waiting.IsSuccess, waiting.Error.Message);

        var accepted = await f.Store.AcceptContinuationAsync(f.Caller, row.Quest.Id,
            new(Guid.NewGuid(), waiting.Value.Quest.Revision, continuation, "text/plain", "Accepted answer"), default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        driver = driver with { OperationRevision = driver.OperationRevision + 2 };

        var resumed = await f.Store.PrepareDispatchAsync(driver, continuation, ReceiptDigest("sterile continuation"), row.Quest.RunId, true, default);

        Assert.True(resumed.IsSuccess, resumed.Error.Message);

        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();

        using ApprenticeService service = new(services.GetRequiredService<IServiceScopeFactory>(), new EngineOptions(), new(),
            NullLogger<ApprenticeService>.Instance, new GrimoireConnectionAdmissionGate(TimeProvider.System));

        EngineRuntime runtime = new();

        var result = await service.ExecuteGuildAsync(binding, input, new(f.Store, driver, resumed.Value), runtime,
            new TestGuildEngineWait(static (_, _, _) => throw new InvalidOperationException("Sterile continuation cannot restore a planner")), default);

        Assert.True(result.IsFailure);

        Assert.Equal(0, runtime.SterileCalls);
    }

    [Fact]
    public async Task Guild_shared_engine_shutdown_drains_the_awaited_provider_before_releasing_shared_capacity()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = await ReceiptRootAsync(f);

        GuildLocalExecutionInput input = LocalInput(row);

        GuildLocalExecutionBinding binding = await CreateLocalAsync(f, row, input);

        f.UseLocalAdmission(new LocalAdmission());

        GuildQuestDriver driver = await f.DriverAsync(row);

        GuildQuestDispatchRecord dispatch = (await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(), ReceiptDigest("drain engine"), row.Quest.RunId, false, default)).Value;

        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();

        DefaultApprenticeExecutionCapacity capacity = new();

        using ApprenticeService service = new(services.GetRequiredService<IServiceScopeFactory>(), new EngineOptions(), new(),
            NullLogger<ApprenticeService>.Instance, new GrimoireConnectionAdmissionGate(TimeProvider.System), capacity);

        EngineRuntime runtime = new() { Block = true };

        Task<Result<PromptTurnResult>> running = service.ExecuteGuildAsync(binding, input, new(f.Store, driver, dispatch), runtime,
            new TestGuildEngineWait(static (_, _, _) => throw new InvalidOperationException("Unexpected input wait")), default);

        await runtime.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task stopped = service.StopAsync(default);

        Assert.False(stopped.IsCompleted);

        Assert.Equal(1, capacity.RunningCount);

        runtime.Release.TrySetResult();

        _ = await running.WaitAsync(TimeSpan.FromSeconds(5));

        await stopped.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, capacity.RunningCount);
    }

    [Fact]
    public async Task Guild_shared_engine_expired_capacity_callback_cannot_reacquire_a_completed_execution_slot()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = await ReceiptRootAsync(f);

        GuildLocalExecutionInput input = LocalInput(row) with { Mode = GuildLocalExecutionMode.Apprentice };

        GuildLocalExecutionBinding binding = await CreateLocalAsync(f, row, input);

        f.UseLocalAdmission(new LocalAdmission());

        GuildQuestDriver driver = await f.DriverAsync(row);

        var dispatch = await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(), ReceiptDigest("expired capacity"), row.Quest.RunId, false, default);

        Assert.True(dispatch.IsSuccess, dispatch.Error.Message);

        GuildLocalInferenceExecution execution = new(f.Store, driver, dispatch.Value);

        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();

        DefaultApprenticeExecutionCapacity capacity = new();

        using ApprenticeService service = new(services.GetRequiredService<IServiceScopeFactory>(), new EngineOptions(), new(),
            NullLogger<ApprenticeService>.Instance, new GrimoireConnectionAdmissionGate(TimeProvider.System), capacity);

        var result = await service.ExecuteGuildAsync(binding, input, execution, new EngineRuntime(),
            new TestGuildEngineWait(static (_, _, _) => throw new InvalidOperationException("Unexpected input wait")), default);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(0, capacity.RunningCount);

        await Assert.ThrowsAsync<OperationCanceledException>(async () => { _ = await execution.SuspendForChildAsync(default); });

        Assert.Equal(0, capacity.RunningCount);
    }

    private sealed class EngineRuntime : IGuildLocalInferenceRuntime
    {
        internal int SterileCalls;

        internal int TurnCalls;

        internal int FailAt;

        internal bool Block;

        internal bool AssertQualifiedTools;

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<Result<PromptTurnResult>> ExecuteAsync(GuildLocalInferenceExecution execution, GuildLocalExecutionInput input, CancellationToken cancellationToken)
        {
            SterileCalls++;

            Entered.TrySetResult();

            if (Block) await Release.Task;

            return Result<PromptTurnResult>.Success(new("public final answer", null));
        }

        public Task<Result<PromptTurnResult>> ExecuteTurnAsync(GuildLocalInferenceExecution execution, GuildLocalExecutionInput input, GuildLocalInferenceTurn turn, CancellationToken cancellationToken)
        {
            TurnCalls++;

            if (AssertQualifiedTools) Assert.Equal(TurnCalls != 2, turn.DisableAllTools);

            return Task.FromResult(TurnCalls == FailAt ? Result<PromptTurnResult>.Failure(new("fixture.uncertain", "Paid reply was lost"))
                : Result<PromptTurnResult>.Success(new(TurnCalls == 1 ? "[{\"index\":1,\"description\":\"Produce the answer\"}]" : "public final answer", null)));
        }
    }

    private sealed class EngineOptions : IOptionsMonitor<ArcanumSettings>
    {
        public ArcanumSettings CurrentValue { get; } = new() { Features = new() { Guild = true, Apprentices = true } };

        public ArcanumSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ArcanumSettings, string?> listener) => null;
    }
}
