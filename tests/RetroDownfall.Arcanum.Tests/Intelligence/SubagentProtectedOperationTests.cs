using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence.MemoryDelegation;
using RetroDownfall.Arcanum.Api.Intelligence.Subagents;
using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Tests.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

[Collection("Grimoire")]
public sealed class SubagentProtectedOperationTests(GrimoireFixture fixture)
{
    [SkippableTheory]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    [InlineData(false, "owner")]
    [InlineData(false, "heartbeat-false")]
    [InlineData(false, "heartbeat-throw")]
    [InlineData(false, "complete-race")]
    [InlineData(false, "caller")]
    [InlineData(false, "root")]
    public async Task Operation_cancel_or_lost_owner_stops_protected_provider_and_retains_usage_through_drain(bool derivedOnly, string stop)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        string path = fixture.CopyDatabase();
        await using var db = fixture.CreateContext(path);
        var clock = new ManualWallClockTimeProvider();
        var store = new LongRunningOperationStore(db, TestOrdinaryConnectionFactory.For(db));
        var operations = new HookedCoordinator(new LongRunningOperationCoordinator(store, clock));
        bool lostOwner = stop == "owner";
        if (stop.StartsWith("heartbeat-", StringComparison.Ordinal)) operations.Heartbeat = () => stop == "heartbeat-false" ? Task.FromResult(false) : Task.FromException<bool>(new IOException("private heartbeat detail"));
        if (stop == "complete-race") operations.BeforeComplete = async id =>
        {
            var current = (await store.GetAsync(id))!;
            Assert.True(await store.RequestCancellationAsync(id, current.Revision, clock.GetUtcNow()));
        };
        TaskCompletionSource observerEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource observerCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        var services = new ServiceCollection();
        services.AddScoped(_ => fixture.CreateContext(path));
        services.AddScoped<ILongRunningOperationStore>(sp => new ObservedStore(new LongRunningOperationStore(sp.GetRequiredService<ArcanumDbContext>(), TestOrdinaryConnectionFactory.For(sp.GetRequiredService<ArcanumDbContext>())))
        {
            BeforeRead = async ct =>
            {
                if (Interlocked.Increment(ref reads) > 1 && stop is "caller" or "root")
                {
                    observerEntered.TrySetResult();
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                    finally { observerCancelled.TrySetResult(); }
                }
            }
        });
        await using var serviceProvider = services.BuildServiceProvider();
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync(sensitive: true);
        await using var root = await h.PrepareAsync();
        var childId = Guid.NewGuid();
        var child = (await h.Coordinator.ConsumeAsync(root, h.RootId, childId, h.Digest)).Value;
        var binding = new MemoryDelegationRootBinding(new PingRequest("parent"), h.Source.Invocation, h.RootId, root, h.Coordinator);
        var request = new SubagentRunRequest("explicit input", "model", [], 100, null)
        {
            MemoryDelegation = derivedOnly ? null : new(childId, child),
            InputProtection = derivedOnly ? binding.CaptureOutputProtection() : null,
        };
        if (derivedOnly) await child.DisposeAsync();
        var writer = new TurnRunWriter(db);
        var facade = new PhysicalFacade(writer);
        var logger = new TestCapturingLogger<SubagentRunner>();
        var runner = new SubagentRunner(new(() => facade), operations, clock, logger, serviceProvider.GetRequiredService<IServiceScopeFactory>());
        using var caller = new CancellationTokenSource();
        var run = runner.RunAsync(request, caller.Token);
        await Task.WhenAny(facade.Chat.Entered.Task, run).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(run.IsCompleted, string.Join(";", logger.Entries.Select(e => e.Message)) + "; " + facade.Failure);
        var operation = Assert.Single(await store.ListAsync(new(Kind: LongRunningOperationKinds.Subagent)));
        if (lostOwner)
        {
            Assert.True(await store.TryTransitionAsync(operation.Id, operation.Revision, operation.LeaseOwner, LongRunningOperationState.Failed, clock.GetUtcNow()));
            operation = (await store.GetAsync(operation.Id))!;
            Assert.True(await store.ResetForRetryAsync(operation.Id, operation.Revision, clock.GetUtcNow()));
            Assert.True((await store.TryAcquireLeaseAsync(operation.Id, "replacement", clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(15))).Acquired);
        }
        else if (stop == "cancel") Assert.True(await store.RequestCancellationAsync(operation.Id, operation.Revision, clock.GetUtcNow()));
        try
        {
            if (stop is "caller" or "root")
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                await observerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (stop == "caller") await caller.CancelAsync();
                else h.Time.Advance(TimeSpan.FromMinutes(5));
                await observerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            else clock.Advance(stop.StartsWith("heartbeat-", StringComparison.Ordinal) ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(1));
            if (stop != "complete-race") await facade.Chat.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(run.IsCompleted);
            if (!derivedOnly) Assert.NotNull(child.Sources);
            facade.Chat.Release.TrySetResult();
            await facade.AccountingEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(run.IsCompleted);
            if (!derivedOnly) Assert.NotNull(child.Sources);
            facade.AccountingRelease.TrySetResult();
            if (stop is "caller" or "root") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
            else
            {
                var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.False(result.Success);
                Assert.Empty(result.Summary);
                Assert.Equal(18, result.Usage.Tokens);
                Assert.Equal(root.Sensitivity.Digest, result.Sensitivity!.Digest);
            }

            var terminal = (await store.GetAsync(operation.Id))!;
            Assert.Equal(lostOwner || stop is "caller" or "root" ? LongRunningOperationState.Running : LongRunningOperationState.Failed, terminal.State);
            if (lostOwner) Assert.Equal("replacement", terminal.LeaseOwner);
            else if (stop is not ("caller" or "root")) Assert.Equal(stop.StartsWith("heartbeat-", StringComparison.Ordinal) ? SubagentFailureCodes.ChildFailed : SubagentFailureCodes.Cancelled, terminal.TerminalErrorCode);
            if (stop != "root")
            {
                await using var parentEffect = (await h.Coordinator.AdmitEffectAsync(root)).Value;
                Assert.False(root.Revocation.IsCancellationRequested);
            }
            else Assert.True(root.Revocation.IsCancellationRequested);
            Assert.Throws<InvalidOperationException>(() => child.Sources);
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT InputTokens,OutputTokens FROM BillableOperations";
            await using var rows = await command.ExecuteReaderAsync();
            Assert.True(await rows.ReadAsync());
            Assert.Equal((11L, 7L), (rows.GetInt64(0), rows.GetInt64(1)));
            Assert.False(await rows.ReadAsync());
        }
        finally
        {
            facade.Chat.Release.TrySetResult();
            facade.AccountingRelease.TrySetResult();
            try
            {
                await run.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (OperationCanceledException) when (stop is "caller" or "root")
            {
                // These cases assert cancellation above; cleanup still joins after releasing both barriers.
            }
        }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Blocked_observer_read_is_cancelled_by_stop_or_outer_teardown(bool outerCancellation)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        string path = fixture.CopyDatabase();
        var clock = new ManualWallClockTimeProvider();
        TaskCompletionSource readEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource readCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddScoped(_ => fixture.CreateContext(path));
        services.AddScoped<ILongRunningOperationStore>(sp => new ObservedStore(new LongRunningOperationStore(sp.GetRequiredService<ArcanumDbContext>(), TestOrdinaryConnectionFactory.For(sp.GetRequiredService<ArcanumDbContext>())))
        {
            BeforeRead = async ct =>
            {
                readEntered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                finally { readCancelled.TrySetResult(); }
            }
        });
        await using var provider = services.BuildServiceProvider();
        using var outer = new CancellationTokenSource();
        await using var lifetime = new SubagentOperationLifetime(provider.GetRequiredService<IServiceScopeFactory>(), Guid.NewGuid(), "owner", clock, outer.Token);
        clock.Advance(TimeSpan.FromSeconds(1));
        await readEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (outerCancellation) await outer.CancelAsync();
        await lifetime.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await readCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(outerCancellation, lifetime.Token.IsCancellationRequested);
    }

    [SkippableTheory]
    [InlineData("expired")]
    [InlineData("read-failure")]
    [InlineData("missing")]
    public async Task Observer_refuses_expired_missing_lease_or_failed_read(string refusal)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        string path = fixture.CopyDatabase();
        await using var db = fixture.CreateContext(path);
        var clock = new ManualWallClockTimeProvider();
        var store = new LongRunningOperationStore(db, TestOrdinaryConnectionFactory.For(db));
        var operations = new LongRunningOperationCoordinator(store, clock);
        var lease = await operations.StartAsync(new(LongRunningOperationKinds.Subagent, LongRunningOperationRecoveryPolicy.AbandonSafely, "child", clock.GetUtcNow()), "owner", TimeSpan.FromSeconds(5));
        Assert.True(lease.Acquired);
        var services = new ServiceCollection();
        services.AddScoped(_ => fixture.CreateContext(path));
        services.AddScoped<ILongRunningOperationStore>(sp => new ObservedStore(new LongRunningOperationStore(sp.GetRequiredService<ArcanumDbContext>(), TestOrdinaryConnectionFactory.For(sp.GetRequiredService<ArcanumDbContext>())))
        {
            BeforeRead = refusal == "read-failure" ? _ => throw new IOException("private read detail") : null
        });
        await using var provider = services.BuildServiceProvider();
        await using var lifetime = new SubagentOperationLifetime(provider.GetRequiredService<IServiceScopeFactory>(), refusal == "missing" ? Guid.NewGuid() : lease.Operation.Id, "owner", clock, default);
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.Token.Register(() => cancelled.TrySetResult());
        clock.Advance(TimeSpan.FromSeconds(refusal == "expired" ? 6 : 1));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SubagentFailureCodes.ChildFailed, lifetime.FailureCode);
        Assert.Equal(SubagentFailureCodes.ChildFailed, await lifetime.SettleAsync(operations, true, SubagentFailureCodes.ChildFailed));
        Assert.Equal(LongRunningOperationState.Running, (await store.GetAsync(lease.Operation.Id))!.State);
    }

    private sealed class ObservedStore(ILongRunningOperationStore inner) : ILongRunningOperationStore
    {
        internal Func<CancellationToken, Task>? BeforeRead;

        public async Task<LongRunningOperation?> GetAsync(Guid id, CancellationToken ct = default)
        {
            if (BeforeRead is { } hook) await hook(ct);
            return await inner.GetAsync(id, ct);
        }

        public Task<LongRunningOperationLeaseResult> TryAcquireLeaseAsync(Guid id, string owner, DateTimeOffset now, DateTimeOffset expires, CancellationToken ct = default) => inner.TryAcquireLeaseAsync(id, owner, now, expires, ct);
        public Task<bool> SaveCheckpointAsync(Guid id, string owner, int expected, int version, byte[]? payload, string? reference, string summary, DateTimeOffset now, CancellationToken ct = default) => inner.SaveCheckpointAsync(id, owner, expected, version, payload, reference, summary, now, ct);

        public Task<LongRunningOperation> CreateAsync(LongRunningOperationCreateRequest request, CancellationToken ct = default) => inner.CreateAsync(request, ct);

        public Task<LongRunningOperationRequestIdentityResult> ResolveOrCreateAsync(LongRunningOperationCreateRequest request, LongRunningOperationRequestIdentity identity, CancellationToken ct = default) => inner.ResolveOrCreateAsync(request, identity, ct);

        public Task<LongRunningOperation?> TryStartSingleFlightAsync(LongRunningOperationCreateRequest request, string owner, DateTimeOffset now, DateTimeOffset expires, CancellationToken ct = default) => inner.TryStartSingleFlightAsync(request, owner, now, expires, ct);

        public Task<LongRunningOperationRequestIdentity?> FindRequestIdentityAsync(Guid id, CancellationToken ct = default) => inner.FindRequestIdentityAsync(id, ct);

        public Task<LongRunningOperationRequestIdentityMatch?> FindByRequestedOperationIdAsync(Guid id, CancellationToken ct = default) => inner.FindByRequestedOperationIdAsync(id, ct);

        public Task<IReadOnlyList<LongRunningOperation>> ListAsync(LongRunningOperationQuery query, CancellationToken ct = default) => inner.ListAsync(query, ct);

        public Task<IReadOnlyList<LongRunningOperation>> FindExpiredAsync(DateTimeOffset now, int limit, CancellationToken ct = default) => inner.FindExpiredAsync(now, limit, ct);

        public Task<bool> HeartbeatAsync(Guid id, string owner, DateTimeOffset now, DateTimeOffset expires, CancellationToken ct = default) => inner.HeartbeatAsync(id, owner, now, expires, ct);

        public Task<bool> RenewLeaseAsync(Guid id, string owner, DateTimeOffset now, DateTimeOffset expires, CancellationToken ct = default) => inner.RenewLeaseAsync(id, owner, now, expires, ct);

        public Task<bool> TryTransitionAsync(Guid id, long revision, string? owner, LongRunningOperationState state, DateTimeOffset now, string? code = null, CancellationToken ct = default) => inner.TryTransitionAsync(id, revision, owner, state, now, code, ct);

        public Task<bool> RequestCancellationAsync(Guid id, long revision, DateTimeOffset now, CancellationToken ct = default) => inner.RequestCancellationAsync(id, revision, now, ct);

        public Task<bool> ResetForRetryAsync(Guid id, long revision, DateTimeOffset now, CancellationToken ct = default) => inner.ResetForRetryAsync(id, revision, now, ct);

        public Task<IReadOnlyList<LongRunningOperationCount>> GetCountsAsync(CancellationToken ct = default) => inner.GetCountsAsync(ct);
    }

    private sealed class HookedCoordinator(ILongRunningOperationCoordinator inner) : ILongRunningOperationCoordinator
    {
        internal Func<Task<bool>>? Heartbeat;

        internal Func<Guid, Task>? BeforeComplete;
        public Task<LongRunningOperationLeaseResult> StartAsync(LongRunningOperationCreateRequest request, string ownerId, TimeSpan duration, CancellationToken ct = default) => inner.StartAsync(request, ownerId, duration, ct);
        public Task<Result<LongRunningOperationRequestIdentityResult>> StartWithRequestIdentityAsync(LongRunningOperationCreateRequest request, LongRunningOperationRequestIdentity identity, string ownerId, TimeSpan duration, CancellationToken ct = default) => inner.StartWithRequestIdentityAsync(request, identity, ownerId, duration, ct);
        public Task<bool> HeartbeatAsync(Guid id, string owner, TimeSpan duration, CancellationToken ct = default) => Heartbeat is { } hook ? hook() : inner.HeartbeatAsync(id, owner, duration, ct);
        public Task<bool> CheckpointAsync(Guid id, string owner, int expected, int version, byte[]? payload, string? reference, string summary, CancellationToken ct = default) => inner.CheckpointAsync(id, owner, expected, version, payload, reference, summary, ct);
        public async Task<bool> CompleteAsync(Guid id, string owner, long revision, CancellationToken ct = default)
        {
            if (BeforeComplete is { } hook) await hook(id);
            return await inner.CompleteAsync(id, owner, revision, ct);
        }

        public Task<bool> FailAsync(Guid id, string owner, long revision, string code, CancellationToken ct = default) => inner.FailAsync(id, owner, revision, code, ct);
    }

    private sealed class PhysicalFacade(ITurnRunWriter writer) : ITurnExecutionFacade
    {
        internal string? Failure;
        internal BlockingChat Chat { get; } = new();
        internal TaskCompletionSource AccountingEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AccountingRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<Result<PromptTurnResult>> ExecuteBufferedAsync(PingRequest request, ArcanumInvocationContext invocationContext,
            CancellationToken executionToken, InferenceAuditContext? auditContext = null)
        {
            Assert.Same(ArcanumInvocationContext.None, invocationContext);
            var runId = await writer.StartRunAsync(new("child", null, "test", "chat", null, DateTimeOffset.UtcNow));
            var protection = SubagentExecutionAmbient.Protection!.WithUsageCommit(new(async usage =>
            {
                AccountingEntered.TrySetResult();
                await AccountingRelease.Task;
                await writer.RecordBillableOperationAsync(new(runId, BillableOperationType.Chat, "provider", "model", "chat", DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow, usage.InputTokenCount ?? 0, usage.OutputTokenCount ?? 0, 0, 0, "{}", 0m, BillableOperationStatus.Completed, null), CancellationToken.None);
            }));
            var outcome = await new ModelCallExecutor(new ModelTokenEstimator(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance))).ExecuteBufferedAsync(Chat, [new(ChatRole.User, request.Prompt)], new(), UnrestrictedTurnBudget.Instance,
                ModelCallPurpose.MainInference, executionToken, new(new ProviderSettings { Name = "provider" }, "model", 0, 0, ProtectedBinding: protection));
            if (outcome.IsFailure) Failure = outcome.Error.ToString();
            return outcome.IsSuccess ? new PromptTurnResult(outcome.Value.Response.Text, null) : outcome.Error;
        }

        public IAsyncEnumerable<IntelligenceEvent> ExecuteIntelligenceStreamAsync(PingRequest request, ArcanumInvocationContext invocationContext, CancellationToken executionToken, InferenceAuditContext? auditContext = null) => throw new NotSupportedException();
    }

    private sealed class BlockingChat : IChatClient
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            using var registration = cancellationToken.Register(() => Cancelled.TrySetResult());
            Entered.TrySetResult();
            await Release.Task;
            return new(new ChatMessage(ChatRole.Assistant, "private late answer")) { Usage = new() { InputTokenCount = 11, OutputTokenCount = 7, TotalTokenCount = 18 } };
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
