using System.Net;
using System.Globalization;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestMaintenanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Clean_worker_callback_fault_joins_original_heartbeat_before_caller_and_admission_exit(bool campaign) =>
        ObserveHeartbeatShutdownAsync(claim: false, campaign, "callback-fault");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Protected_claim_callback_fault_joins_original_heartbeat_before_caller_and_effect_exit(bool campaign) =>
        ObserveHeartbeatShutdownAsync(claim: true, campaign, "callback-fault");

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task Normal_cancellation_joins_original_heartbeat_before_caller_and_authority_exit(bool claim, bool campaign) =>
        ObserveHeartbeatShutdownAsync(claim, campaign, "cancellation");

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task Real_heartbeat_task_failure_is_joined_before_caller_and_authority_exit(bool claim, bool campaign) =>
        ObserveHeartbeatShutdownAsync(claim, campaign, "store-fault");

    private static async Task ObserveHeartbeatShutdownAsync(bool claim, bool campaign, string scenario)
    {
        ManualTimerTimeProvider clock = new();

        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: claim, clock: clock,
            campaign: campaign, protectedSources: claim);

        LongRestMaintenanceCapability? capability = claim ? await CaptureAsync(f) : null;

        HeartbeatHold held = new(scenario);

        TaskCompletionSource sdkEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource sdkRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource dispatchCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        CancellationTokenRegistration dispatchRegistration = default;

        f.DispatchObserved = token => dispatchRegistration = token.Register(() =>
        {
            dispatchCancelled.TrySetResult();

            if (scenario == "store-fault") throw new InvalidOperationException("fixture heartbeat dispatch callback");
        });

        f.Handler.InsideSend = async () =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            if (f.Handler.SendCount == 1)
            {
                sdkEntered.TrySetResult();

                await sdkRelease.Task;
            }
        };

        RecordingGrimoireWorkAdmissionGate outer = new(f.Admission);

        outer.BeforeWorkLeaseDisposalAsync = () => { held.Record("outer-admission-disposed"); return ValueTask.CompletedTask; };

        if (claim)
        {
            f.RecordedAdmission.BeforeWorkLeaseDisposalAsync = () => { held.Record("paid-admission-disposed"); return ValueTask.CompletedTask; };

            f.RecordedAdmission.BeforeEffectGroupDisposalAsync = () => { held.Record("paid-effect-disposed"); return ValueTask.CompletedTask; };
        }

        using CancellationTokenSource callerCancellation = new();

        Exception? failure = null;

        LongRestDiscoveryGeneration? beforeGeneration = null;

        LongRunningOperation? beforeOperation = null;

        LongRestDispatchAttempt? sentAttempt = null;

        long beforeDisposition = 0;

        async Task InvokeAsync()
        {
            try
            {
                if (claim)
                {
                    await f.ProcessAsync(f.Operation, "fixture-worker", capability, callerCancellation.Token);
                }
                else
                {
                    Assert.True(outer.TryAcquireWorkLease(GrimoireWorkKind.LongRestSweep, out IGrimoireWorkLease? lease));

                    await using IGrimoireWorkLease owned = lease!;

                    await f.Coordinator.ResumeAsync(f.Operation, null, callerCancellation.Token);
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                held.Record("caller-settled");
            }
        }

        Task run = InvokeAsync();

        try
        {
            await sdkEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            if (claim && scenario == "callback-fault")
            {
                beforeGeneration = LongRestSweepHarness.Require(await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None));

                beforeOperation = (await f.Operations.GetAsync(f.Operation))!;

                (sentAttempt, beforeDisposition) = await ReadHeartbeatAttemptAsync(f, Assert.Single(f.Payloads).Work);

                Assert.Equal(LongRestDispatchAttemptState.Sent, sentAttempt.State);
            }

            f.IndependentScopeDisposed = held.ScopeFinished;

            if (claim) f.DecorateIndependentAttempts = store => new HeldClaimStore(store, held);
            else f.DecorateIndependentOperations = store => new HeldWorkerStore(store, held);

            Assert.Equal(1, clock.ActiveTimers);

            clock.Advance(TimeSpan.FromMinutes(1));

            await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(run.IsCompleted);

            Assert.False(held.ScopeDisposed.Task.IsCompleted);

            Assert.Equal(LongRunningOperationState.Running, (await f.Operations.GetAsync(f.Operation))!.State);

            if (scenario == "store-fault")
            {
                held.Release();

                await dispatchCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            else if (scenario == "cancellation")
            {
                await callerCancellation.CancelAsync();
            }

            sdkRelease.TrySetResult();

            if (scenario != "store-fault")
            {
                await held.StopCallback.Task.WaitAsync(TimeSpan.FromSeconds(10));

                // This bounded window only guarantees release on both old and repaired code.
                // The verdict below uses recorded resource/caller ordering, never elapsed time.
                await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(1)));

                Assert.False(held.ScopeDisposed.Task.IsCompleted);

                Assert.Equal(LongRunningOperationState.Running, (await f.Operations.GetAsync(f.Operation))!.State);
            }
        }
        finally
        {
            sdkRelease.TrySetResult();

            held.Release();

            await run.WaitAsync(TimeSpan.FromSeconds(10));

            await held.ScopeDisposed.Task.WaitAsync(TimeSpan.FromSeconds(10));

            dispatchRegistration.Dispose();
        }

        string[] events = held.Events;

        int scopeDisposed = Array.IndexOf(events, "heartbeat-scope-disposed");

        string order = string.Join(" -> ", events);

        Assert.True(scopeDisposed >= 0 && scopeDisposed < Array.IndexOf(events, "caller-settled"), order);

        Assert.True(scopeDisposed < Array.IndexOf(events, claim ? "paid-effect-disposed" : "outer-admission-disposed"), order);

        if (claim) Assert.True(scopeDisposed < Array.IndexOf(events, "paid-admission-disposed"), order);

        Assert.Equal(0, clock.ActiveTimers);

        Assert.Equal(scenario == "callback-fault" ? (long)f.Handler.SendCount : 0L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        if (scenario == "callback-fault")
        {
            AggregateException callbackFailure = Assert.IsType<AggregateException>(failure);

            Assert.Contains(callbackFailure.InnerExceptions, exception => exception.Message == "fixture heartbeat stop callback");

            Assert.Equal(LongRunningOperationState.Running, (await f.Operations.GetAsync(f.Operation))!.State);

            if (claim)
            {
                Assert.Equal(1, f.Handler.SendCount);

                Assert.NotNull(beforeGeneration);

                Assert.NotNull(beforeOperation);

                Assert.NotNull(sentAttempt);

                await AssertHeartbeatAcceptedSummaryAsync(f, sentAttempt, beforeDisposition, beforeGeneration, beforeOperation);
            }
        }
        else
        {
            if (scenario == "store-fault")
            {
                AggregateException heartbeatFailure = Assert.IsType<AggregateException>(failure);

                Assert.Contains(heartbeatFailure.Flatten().InnerExceptions, exception => exception.Message == "fixture heartbeat dispatch callback");
            }

            Assert.Equal(1, f.Handler.SendCount);

            Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects WHERE StateCode = 3 AND CompletionJson IS NULL;"));

            Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_accepted_summaries;"));
        }
    }

    private static async Task<(LongRestDispatchAttempt Attempt, long Disposition)> ReadHeartbeatAttemptAsync(
        MaintenanceFixture f, LongRestWorkIdentity work)
    {
        await using SqliteCommand command = f.H.Connection.CreateCommand();

        command.CommandText = """
            SELECT effect.AttemptId, effect.PayloadKindCode, effect.PhysicalOrdinal, effect.Revision, effect.StateCode,
                effect.PayloadDigest, effect.DestinationDigest, effect.DisclosureReceiptDigest, unit.DispositionCode
            FROM long_rest_provider_effects effect JOIN long_rest_work_items unit ON unit.WorkItemId = effect.WorkItemId
            WHERE effect.WorkDigest = $work AND unit.WorkDigest = $work;
            """;

        command.Parameters.AddWithValue("$work", LongRestSweepHarness.Require(LongRestDiscoveryHash.Work(work)).Bytes);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());

        LongRestDispatchAttempt attempt = new(Guid.Parse(reader.GetString(0)), work, (LongRestProviderPayloadKind)reader.GetInt32(1),
            checked((ulong)reader.GetInt64(2)), reader.GetInt64(3), (LongRestDispatchAttemptState)reader.GetInt32(4),
            new CovenantDigest((byte[])reader[5]), new CovenantDigest((byte[])reader[6]),
            reader.IsDBNull(7) ? null : new CovenantDigest((byte[])reader[7]));

        long disposition = reader.GetInt64(8);

        Assert.False(await reader.ReadAsync());

        return (attempt, disposition);
    }

    private static async Task AssertHeartbeatAcceptedSummaryAsync(MaintenanceFixture f, LongRestDispatchAttempt sent,
        long beforeDisposition, LongRestDiscoveryGeneration beforeGeneration, LongRunningOperation beforeOperation)
    {
        LongRestProviderPayload payload = Assert.Single(f.Payloads);

        Assert.Equal(LongRestProviderPayloadKind.Summary, payload.Kind);

        Assert.Equal(payload.Work, sent.Work);

        LongRestProviderResult output = LongRestSweepHarness.Require(f.ProviderResult!);

        Assert.Equal(LongRestProviderResultDisposition.Valid, output.Disposition);

        LongRestProviderUsage usage = new(17, 3, 20, 0, 0);

        Assert.Equal(usage, output.Usage);

        LongRestProviderSelection selected = LongRestSweepHarness.Require(f.Epoch.Capture().ResolveChat());

        Assert.Equal(LongRestSweepHarness.Require(LongRestProviderCallFreezer.PayloadDigest(payload, selected.Provider, selected.Model, selected.Dimensions)), sent.PayloadDigest);

        Assert.Equal(beforeGeneration.Recipe.SummaryDestinationDigest, sent.DestinationDigest);

        Assert.NotNull(sent.DisclosureReceiptDigest);

        decimal cost = CostCalculator.CalculateCost(usage.InputTokens, usage.OutputTokens, usage.CachedInputTokens, usage.ReasoningTokens, selected.Pricing);

        Assert.Equal(0.000043m, cost);

        LongRestDispatchCompletion expectedCompletion = new(LongRestDispatchAttemptState.Accepted, usage, cost, null);

        string completionJson = Assert.IsType<string>(await f.H.ScalarAsync("SELECT CompletionJson FROM long_rest_provider_effects;"));

        Assert.Equal(expectedCompletion, JsonSerializer.Deserialize(completionJson, LongRestJsonContext.Default.LongRestDispatchCompletion));

        Assert.Equal(JsonSerializer.Serialize(expectedCompletion, LongRestJsonContext.Default.LongRestDispatchCompletion), completionJson);

        LongRestDispatchAttempt expectedAttempt = sent with { State = LongRestDispatchAttemptState.Accepted, Revision = checked(sent.Revision + 1) };

        (LongRestDispatchAttempt retained, long disposition) = await ReadHeartbeatAttemptAsync(f, payload.Work);

        Assert.Equal(expectedAttempt, retained);

        // Summary staging retains pending work; only a later embedding fold publication disposes it.
        Assert.Equal(beforeDisposition, disposition);

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects;"));

        Assert.Equal(1L, await f.H.ScalarAsync("""
            SELECT count(*) FROM long_rest_provider_effects
            WHERE StateCode = 4 AND SentCount = 1 AND PaidCount = 1 AND InvalidCount = 0 AND FailureCount = 0
                AND InputTokens = 17 AND OutputTokens = 3 AND TotalTokens = 20 AND CachedInputTokens = 0 AND ReasoningTokens = 0
                AND CompletionJson IS NOT NULL AND FailureCode IS NULL;
            """));

        Assert.Equal(cost, decimal.Parse(Assert.IsType<string>(await f.H.ScalarAsync("SELECT CostUsd FROM long_rest_provider_effects;")), CultureInfo.InvariantCulture));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_accepted_summaries;"));

        string summaryJson = Assert.IsType<string>(await f.H.ScalarAsync("SELECT EvidenceJson FROM long_rest_accepted_summaries;"));

        LongRestAcceptedHierarchySummary summary = JsonSerializer.Deserialize(summaryJson, LongRestJsonContext.Default.LongRestStoredSummary)!.ToDomain();

        Assert.Equal(new LongRestPaidEvidence(expectedAttempt, usage.InputTokens, usage.OutputTokens, cost), summary.PaidEvidence);

        Assert.Equal(payload.Work, summary.Fold.Work);

        Assert.Equal(payload.Work.GenerationId, summary.GenerationId);

        Assert.NotEqual(Guid.Empty, summary.NodeId);

        Assert.Equal(payload.Fold!.Layer, summary.Fold.Layer);

        Assert.Equal(payload.Fold.Group, summary.Fold.Group);

        Assert.Equal(payload.Fold.MembershipDigest, summary.Fold.MembershipDigest);

        Assert.Equal(payload.Work.SourceDigest, LongRestSweepHarness.Require(LongRestDiscoveryHash.HierarchyFold(summary.Fold)));

        Assert.Equal(payload.Work.RecipeDigest, summary.RecipeDigest);

        Assert.Equal(sent.PayloadDigest, summary.PayloadDigest);

        Assert.Equal(output.Summary, summary.Summary);

        Assert.Equal(DerivedArtifactContentDigest.ForText(output.Summary!), summary.SummaryDigest);

        Assert.Equal(JsonSerializer.Serialize(LongRestStoredSummary.From(summary), LongRestJsonContext.Default.LongRestStoredSummary), summaryJson);

        await using (SqliteCommand artifact = f.H.Connection.CreateCommand())
        {
            artifact.CommandText = """
                SELECT count(*) FROM long_rest_accepted_summaries accepted
                JOIN long_rest_work_items unit ON unit.WorkItemId = accepted.WorkItemId
                JOIN long_rest_provider_effects effect ON effect.WorkItemId = accepted.WorkItemId
                WHERE accepted.WorkDigest = $work AND unit.WorkDigest = $work AND effect.WorkDigest = $work
                    AND accepted.GenerationId = unit.GenerationId AND accepted.GenerationId = $generation
                    AND unit.OperationId = $operation AND unit.ItemId = $item AND unit.KindCode = $kind
                    AND accepted.NodeId = $node AND accepted.MembershipDigest = $membership AND accepted.SourceDigest = $source
                    AND unit.SourceDigest = $source AND accepted.RecipeDigest = $recipe AND unit.RecipeDigest = $recipe
                    AND accepted.PayloadDigest = effect.PayloadDigest AND accepted.PayloadDigest = $payload
                    AND accepted.Summary = $summary AND accepted.SummaryDigest = $digest AND accepted.EvidenceJson = $evidence;
                """;

            artifact.Parameters.AddWithValue("$work", LongRestSweepHarness.Require(LongRestDiscoveryHash.Work(payload.Work)).Bytes);

            artifact.Parameters.AddWithValue("$generation", summary.GenerationId.ToString("D").ToUpperInvariant());

            artifact.Parameters.AddWithValue("$operation", payload.Work.OperationId.ToString("N"));

            artifact.Parameters.AddWithValue("$item", payload.Work.ItemId);

            artifact.Parameters.AddWithValue("$kind", (int)payload.Work.Kind);

            artifact.Parameters.AddWithValue("$node", summary.NodeId.ToString("D").ToUpperInvariant());

            artifact.Parameters.AddWithValue("$membership", summary.Fold.MembershipDigest.Bytes);

            artifact.Parameters.AddWithValue("$source", payload.Work.SourceDigest.Bytes);

            artifact.Parameters.AddWithValue("$recipe", payload.Work.RecipeDigest.Bytes);

            artifact.Parameters.AddWithValue("$payload", sent.PayloadDigest.Bytes);

            artifact.Parameters.AddWithValue("$summary", output.Summary!);

            artifact.Parameters.AddWithValue("$digest", summary.SummaryDigest.Bytes);

            artifact.Parameters.AddWithValue("$evidence", summaryJson);

            Assert.Equal(1L, await artifact.ExecuteScalarAsync());
        }

        await using (SqliteCommand bill = f.H.Connection.CreateCommand())
        {
            bill.CommandText = """
                SELECT Provider, Model, Purpose, InputTokens, OutputTokens, CachedTokens, ReasoningTokens, ActualCostUsd, Status
                FROM BillableOperations;
                """;

            await using SqliteDataReader reader = await bill.ExecuteReaderAsync();

            Assert.True(await reader.ReadAsync());

            Assert.Equal(selected.Provider.Name, reader.GetString(0));

            Assert.Equal(selected.Model, reader.GetString(1));

            Assert.Equal("long-rest-summary", reader.GetString(2));

            Assert.Equal(usage.InputTokens, reader.GetInt64(3));

            Assert.Equal(usage.OutputTokens, reader.GetInt64(4));

            Assert.Equal(usage.CachedInputTokens, reader.GetInt64(5));

            Assert.Equal(usage.ReasoningTokens, reader.GetInt64(6));

            Assert.Equal(cost, decimal.Parse(reader.GetString(7), CultureInfo.InvariantCulture));

            Assert.Equal((int)BillableOperationStatus.Completed, reader.GetInt32(8));

            Assert.False(await reader.ReadAsync());
        }

        Assert.Equal(beforeGeneration, LongRestSweepHarness.Require(await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None)));

        LongRunningOperation latest = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(beforeOperation.CheckpointVersion, latest.CheckpointVersion);

        Assert.Equal(beforeOperation.CheckpointPayload, latest.CheckpointPayload);

        Assert.Equal(beforeOperation.CheckpointReference, latest.CheckpointReference);

        Assert.Null(latest.TerminalErrorCode);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_embeddings;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_nodes WHERE Layer > 0;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_edges;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_adjudications;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_receipts;"));
    }

    private sealed class HeartbeatHold(string scenario)
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _events = new();

        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _released;

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource StopCallback { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ScopeDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal string[] Events => _events.ToArray();

        internal void Record(string value) => _events.Enqueue(value);

        internal void ScopeFinished()
        {
            Record("heartbeat-scope-disposed");

            ScopeDisposed.TrySetResult();
        }

        internal void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;

            Record("held-operation-released");

            _release.TrySetResult();
        }

        internal async Task HoldAsync(CancellationToken stop)
        {
            using CancellationTokenRegistration registration = stop.Register(() =>
            {
                Record("stop-callback");

                StopCallback.TrySetResult();

                if (scenario == "callback-fault") throw new InvalidOperationException("fixture heartbeat stop callback");
            });

            Record("heartbeat-entered");

            Entered.TrySetResult();

            await _release.Task;

            Record("held-operation-returned");

            if (scenario == "store-fault") throw new InvalidOperationException("fixture independent heartbeat store");
        }
    }

    private sealed class HeldClaimStore(ILongRestAttemptStore inner, HeartbeatHold held) : ILongRestAttemptStore
    {
        public async Task<Result<SessionTurnClaimLease>> RenewClaimAsync(SessionTurnClaimLease claim, CanonicalCampaignContext scope, CancellationToken ct)
        {
            Result<SessionTurnClaimLease> result = await inner.RenewClaimAsync(claim, scope, ct);

            await held.HoldAsync(ct);

            return result;
        }

        public Task<Result<LongRestDispatchAttempt>> PrepareAttemptAsync(LongRestWorkIdentity work, LongRestProviderPayloadKind kind, CovenantDigest payload, CovenantDigest destination, CancellationToken ct) => inner.PrepareAttemptAsync(work, kind, payload, destination, ct);

        public Task<Result<LongRestDispatchAttempt>> RecordDisclosureAsync(LongRestDispatchAttempt attempt, CovenantDigest receipt, CancellationToken ct) => inner.RecordDisclosureAsync(attempt, receipt, ct);

        public Task<Result<LongRestDispatchAttempt>> MarkSentAsync(LongRestDispatchAttempt attempt, CancellationToken ct) => inner.MarkSentAsync(attempt, ct);

        public Task<Result<LongRestDispatchAttempt>> CompleteAttemptAsync(LongRestDispatchAttempt attempt, LongRestDispatchCompletion completion, CancellationToken ct) => inner.CompleteAttemptAsync(attempt, completion, ct);

        public Task<Result<LongRestDispatchAttempt>> SettleUnresolvedSendAsync(LongRestDispatchAttempt attempt, LongRestUnresolvedSend outcome, CancellationToken ct) => inner.SettleUnresolvedSendAsync(attempt, outcome, ct);

        public Task<Result> ReleaseUnsentAttemptAsync(LongRestDispatchAttempt attempt, CancellationToken ct) => inner.ReleaseUnsentAttemptAsync(attempt, ct);

        public Task<Result> ValidateAttemptAsync(LongRestDispatchAttempt attempt, CancellationToken ct) => inner.ValidateAttemptAsync(attempt, ct);
    }

    private sealed class HeldWorkerStore(ILongRunningOperationStore inner, HeartbeatHold held) : ILongRunningOperationStore
    {
        public async Task<LongRunningOperation?> GetAsync(Guid id, CancellationToken ct = default)
        {
            LongRunningOperation? result = await inner.GetAsync(id, ct);

            await held.HoldAsync(ct);

            return result;
        }

        public Task<LongRunningOperation> CreateAsync(LongRunningOperationCreateRequest request, CancellationToken ct = default) => inner.CreateAsync(request, ct);

        public Task<LongRunningOperationRequestIdentityResult> ResolveOrCreateAsync(LongRunningOperationCreateRequest request, LongRunningOperationRequestIdentity identity, CancellationToken ct = default) => inner.ResolveOrCreateAsync(request, identity, ct);

        public Task<LongRunningOperation?> TryStartSingleFlightAsync(LongRunningOperationCreateRequest request, string owner, DateTimeOffset now, DateTimeOffset expires, CancellationToken ct = default) => inner.TryStartSingleFlightAsync(request, owner, now, expires, ct);

        public Task<LongRunningOperationRequestIdentity?> FindRequestIdentityAsync(Guid id, CancellationToken ct = default) => inner.FindRequestIdentityAsync(id, ct);

        public Task<LongRunningOperationRequestIdentityMatch?> FindByRequestedOperationIdAsync(Guid id, CancellationToken ct = default) => inner.FindByRequestedOperationIdAsync(id, ct);

        public Task<IReadOnlyList<LongRunningOperation>> ListAsync(LongRunningOperationQuery query, CancellationToken ct = default) => inner.ListAsync(query, ct);

        public Task<IReadOnlyList<LongRunningOperation>> FindExpiredAsync(DateTimeOffset now, int limit, CancellationToken ct = default) => inner.FindExpiredAsync(now, limit, ct);

        public Task<LongRunningOperationLeaseResult> TryAcquireLeaseAsync(Guid id, string owner, DateTimeOffset now, DateTimeOffset expires, CancellationToken ct = default) => inner.TryAcquireLeaseAsync(id, owner, now, expires, ct);

        public Task<bool> HeartbeatAsync(Guid id, string owner, DateTimeOffset now, DateTimeOffset expires, CancellationToken ct = default) => inner.HeartbeatAsync(id, owner, now, expires, ct);

        public Task<bool> RenewLeaseAsync(Guid id, string owner, DateTimeOffset now, DateTimeOffset expires, CancellationToken ct = default) => inner.RenewLeaseAsync(id, owner, now, expires, ct);

        public Task<bool> SaveCheckpointAsync(Guid id, string owner, int expected, int version, byte[]? payload, string? reference, string summary, DateTimeOffset now, CancellationToken ct = default) => inner.SaveCheckpointAsync(id, owner, expected, version, payload, reference, summary, now, ct);

        public Task<bool> TryTransitionAsync(Guid id, long revision, string? owner, LongRunningOperationState state, DateTimeOffset now, string? code = null, CancellationToken ct = default) => inner.TryTransitionAsync(id, revision, owner, state, now, code, ct);

        public Task<bool> RequestCancellationAsync(Guid id, long revision, DateTimeOffset now, CancellationToken ct = default) => inner.RequestCancellationAsync(id, revision, now, ct);

        public Task<bool> ResetForRetryAsync(Guid id, long revision, DateTimeOffset now, CancellationToken ct = default) => inner.ResetForRetryAsync(id, revision, now, ct);

        public Task<IReadOnlyList<LongRunningOperationCount>> GetCountsAsync(CancellationToken ct = default) => inner.GetCountsAsync(ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lagging_partial_group_checkpoint_resumes_the_exact_retained_coordinate_pair_after_boot_change(bool protectedSources)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: protectedSources, sourceCount: 24);

        f.Handler.InsideSend = () => { f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!); return Task.CompletedTask; };

        await f.H.ExecuteAsync("CREATE TEMP TRIGGER fixture_partial_checkpoint BEFORE UPDATE OF CheckpointPayload ON LongRunningOperations WHEN (SELECT count(*) FROM long_rest_work_items WHERE KindCode = 3 AND DispositionCode = 2) >= 4 BEGIN SELECT RAISE(ABORT, 'fixture partial checkpoint interruption'); END;");

        LongRestMaintenanceCapability? capability = protectedSources ? await FreshAsync(f) : null;

        await Record.ExceptionAsync(() => f.Coordinator.ResumeAsync(f.Operation, capability, CancellationToken.None));

        Assert.Equal(4L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_work_items WHERE KindCode = 3 AND DispositionCode = 2;"));

        var prior = (await f.Operations.GetAsync(f.Operation))!;

        var checkpoint = LongRestSweepHarness.Require(RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.LongRestSweepCheckpointCodec.Decode(f.Operation, prior.CheckpointPayload!));

        Assert.NotNull(checkpoint.CommittedItemCursor);

        Assert.NotNull(checkpoint.CommittedWorkDigest);

        Assert.True(checkpoint.Revision < (long)(await f.H.ScalarAsync("SELECT Revision FROM long_rest_sweeps;"))!);

        await f.H.ExecuteAsync("DROP TRIGGER fixture_partial_checkpoint;");

        await f.RebootAsync();

        var resumed = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, protectedSources ? await FreshAsync(f) : null, CancellationToken.None));

        Assert.True(resumed.Phase == LongRestSweepPhase.Completed, resumed.DeferredReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Insertion_above_original_highwater_allows_current_progress_and_remains_next_generation_debt(bool protectedSources)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: protectedSources);

        Guid added = Guid.NewGuid();

        f.Handler.InsideSend = async () =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            if (f.Handler.SendCount == 1)
            {
                await f.H.SeedAsync(added.ToString("D"), "New later observation.", f.SourceSession);

                if (protectedSources) await f.H.ProtectAsync(added, f.Provenance);
            }
        };

        var result = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation,
            protectedSources ? await FreshAsync(f) : null, CancellationToken.None));

        Assert.True(result.Phase == LongRestSweepPhase.Completed, result.DeferredReason);

        Assert.Equal(2L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_leaves;"));

        using CancellationTokenSource bounded = new(TimeSpan.FromSeconds(1));

        LongRunningOperationDto? next = null;

        Exception? requestFailure = await Record.ExceptionAsync(async () => next = LongRestSweepHarness.Require(
            await f.Coordinator.RequestAsync(f.Scope, f.Partition, bounded.Token)));

        Assert.Null(requestFailure);

        Assert.NotNull(next);

        Assert.NotEqual(f.Operation, next.Id);

        LongRestMaintenanceCapability? fresh = protectedSources ? LongRestSweepHarness.Require(await f.Maintenance.CreateCapabilityAsync(next.Id,
            f.Invocation, f.Claim, f.Scope, f.Authority, CancellationToken.None)) : null;

        Assert.Equal(LongRestSweepPhase.Completed, LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(next.Id, fresh, CancellationToken.None)).Phase);

        await using var retained = f.H.Connection.CreateCommand();

        retained.CommandText = "SELECT count(*) FROM long_rest_discovery_leaves WHERE MemoryId = $memory;";

        retained.Parameters.AddWithValue("$memory", added.ToString("D"));

        Assert.Equal(1L, await retained.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Completed_exact_protected_partition_replays_without_writes_or_billing_while_clean_debt_remains()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false);

        f.Handler.InsideSend = () => { f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!); return Task.CompletedTask; };

        Assert.Equal(LongRestSweepPhase.Completed, LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, await FreshAsync(f), CancellationToken.None)).Phase);

        await f.H.SeedAsync(Guid.NewGuid().ToString("D"), "Unrelated clean debt.", f.SourceSession);

        long writes = (long)(await f.H.ScalarAsync("SELECT total_changes();"))!;

        int sends = f.Handler.SendCount;

        Assert.Equal(f.Operation, LongRestSweepHarness.Require(await f.Coordinator.RequestAsync(f.Scope, f.Partition, CancellationToken.None)).Id);

        Assert.Equal(writes, await f.H.ScalarAsync("SELECT total_changes();"));

        Assert.Equal(sends, f.Handler.SendCount);

        Assert.NotEqual(f.Operation, LongRestSweepHarness.Require(await f.Coordinator.RequestAsync(f.Scope, CancellationToken.None)).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Multiple_frozen_groups_checkpoint_each_committed_unit_before_advancing_the_anchor(bool protectedSources)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: protectedSources, sourceCount: 24);

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            return Task.CompletedTask;
        };

        var result = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation,
            protectedSources ? await FreshAsync(f) : null, CancellationToken.None));

        string? persistedCursor = await f.H.ScalarAsync("SELECT CommittedItemCursor FROM long_rest_sweeps;") as string;

        string? digestAnchor = await f.H.ScalarAsync("SELECT input.MemoryId FROM long_rest_sweeps sweep JOIN long_rest_work_items work ON work.WorkDigest = sweep.CommittedWorkDigest JOIN long_rest_work_inputs input ON input.WorkItemId = work.WorkItemId AND input.Ordinal = 1;") as string;

        Assert.True(result.Phase == LongRestSweepPhase.Completed, $"{result.DeferredReason}; persisted cursor {persistedCursor}; digest unit anchor {digestAnchor}.");

        Assert.True((long)(await f.H.ScalarAsync("SELECT count(*) FROM long_rest_work_items WHERE KindCode = 3 AND DispositionCode = 2;"))! > 1);

        Assert.Equal(LongRunningOperationState.Completed, (await f.Operations.GetAsync(f.Operation))!.State);
    }

    [Fact]
    public async Task Worker_and_claim_heartbeats_use_independent_contexts_refresh_authority_and_join_before_latest_terminal_CAS()
    {
        RetroDownfall.Arcanum.Tests.Support.ManualTimerTimeProvider clock = new();

        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, clock: clock);

        LongRestMaintenanceCapability capability = await FreshAsync(f);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        f.Handler.InsideSend = async () =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            if (f.Handler.SendCount == 1)
            {
                entered.TrySetResult();

                await release.Task;
            }
        };

        Task<RetroDownfall.Arcanum.Core.Primitives.Result<LongRestSweepProgress>> run = f.Coordinator.ResumeAsync(f.Operation, capability, CancellationToken.None);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        long revision = (await f.Operations.GetAsync(f.Operation))!.Revision;

        DateTimeOffset deadline = Assert.IsType<DateTimeOffset>(capability.Claim.LeaseDeadlineUtc);

        try
        {
            Assert.Equal(2, clock.ActiveTimers);

            clock.Advance(TimeSpan.FromMinutes(1));

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

            while (capability.Claim.LeaseDeadlineUtc <= deadline || (await f.Operations.GetAsync(f.Operation))!.Revision <= revision)
            {
                await Task.Delay(10, timeout.Token);
            }

            Assert.True(f.IndependentFactsResolved > 0, "The claim heartbeat must validate source and worker proof through its own scoped reader.");

            Assert.True(f.IndependentContexts.Distinct().Count() >= 2);

            Assert.DoesNotContain(f.H.Saga.Context, f.IndependentContexts);
        }
        finally
        {
            release.TrySetResult();

            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }

        var completed = await run;

        Assert.True(completed.IsSuccess, completed.IsFailure ? completed.Error.Code : "");

        Assert.Equal(LongRunningOperationState.Completed, (await f.Operations.GetAsync(f.Operation))!.State);

        Assert.Equal(0, clock.ActiveTimers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_new_boot_reuses_original_paid_summary_with_fresh_embedding_and_request(bool campaign)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(campaign: campaign);

        LongRestMaintenanceCapability oldCapability = await CaptureAsync(f);

        List<string> bodies = [];

        f.Handler.InsideSend = () =>
        {
            bodies.Add(f.Handler.LastBody!);

            f.Handler.Status = f.Handler.LastBody!.Contains("long_rest_summary", StringComparison.Ordinal) ? HttpStatusCode.OK : HttpStatusCode.BadRequest;

            return Task.CompletedTask;
        };

        Assert.True((await f.ProcessAsync(f.Operation, "fixture-worker", oldCapability, CancellationToken.None)).IsFailure);

        string original = (string)(await f.H.ScalarAsync("SELECT EvidenceJson FROM long_rest_accepted_summaries;"))!;

        string originalWork = (string)(await f.H.ScalarAsync("SELECT hex(WorkDigest) FROM long_rest_accepted_summaries;"))!;

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        LongRestWorkIdentity historicalWork = f.Payloads[0].Work;

        f.Payloads.Clear();

        Guid oldBoot = f.Epoch.Capture().Stamp.BootId;

        await f.RebootAsync();

        Assert.NotEqual(oldBoot, f.Epoch.Capture().Stamp.BootId);

        LongRestMaintenanceCapability fresh = await FreshAsync(f);

        f.Handler.Status = HttpStatusCode.OK;

        f.Handler.InsideSend = () =>
        {
            bodies.Add(f.Handler.LastBody!);

            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            return Task.CompletedTask;
        };

        var result = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, fresh, CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Completed, result.Phase);

        Assert.Equal(original, await f.H.ScalarAsync("SELECT EvidenceJson FROM long_rest_accepted_summaries;"));

        Assert.Equal(originalWork, await f.H.ScalarAsync("SELECT hex(WorkDigest) FROM long_rest_accepted_summaries;"));

        Assert.Single(bodies, body => body.Contains("long_rest_summary", StringComparison.Ordinal));

        Assert.True((await oldCapability.ValidateAsync(f.Epoch.Capture().Stamp, CancellationToken.None)).IsFailure);

        Assert.True((await fresh.ValidateWorkAsync(historicalWork, f.Epoch.Capture().Stamp, CancellationToken.None)).IsFailure);

        Assert.Equal(LongRestProviderPayloadKind.Embedding, f.Payloads[0].Kind);

        Assert.Equal(historicalWork.ItemId, f.Payloads[0].Work.ItemId);

        Assert.NotEqual(LongRestDiscoveryHash.Work(historicalWork).Value, LongRestDiscoveryHash.Work(f.Payloads[0].Work).Value);

        Assert.Equal(f.Epoch.Capture().Stamp, LongRestSweepHarness.Require(await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None))!.Fence.Configuration);
    }

    [Fact]
    public async Task Accepted_adjudication_survives_actual_boot_change_without_repeating_its_paid_payload()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync();

        LongRestMaintenanceCapability capability = await CaptureAsync(f);

        List<string> bodies = [];

        f.Handler.InsideSend = () =>
        {
            bodies.Add(f.Handler.LastBody!);

            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            return Task.CompletedTask;
        };

        LongRestSweepHarness.Require(await f.ProcessAsync(f.Operation, "fixture-worker", capability, CancellationToken.None));

        LongRestSweepHarness.Require(await f.ProcessAsync(f.Operation, "fixture-worker", capability, CancellationToken.None));

        await f.H.ExecuteAsync("CREATE TEMP TRIGGER fixture_stop_decision BEFORE UPDATE OF DispositionCode ON long_rest_work_items WHEN OLD.KindCode = 3 AND NEW.DispositionCode = 2 BEGIN SELECT RAISE(ABORT, 'fixture interrupted after accepted decision'); END;");

        Assert.True((await f.ProcessAsync(f.Operation, "fixture-worker", capability, CancellationToken.None)).IsFailure);

        string original = (string)(await f.H.ScalarAsync("SELECT EvidenceJson FROM long_rest_adjudications;") )!;

        string payload = Assert.Single(bodies, body => body.Contains("long_rest_adjudication", StringComparison.Ordinal));

        await f.H.ExecuteAsync("DROP TRIGGER fixture_stop_decision;");

        await f.RebootAsync();

        var result = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, await FreshAsync(f), CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Completed, result.Phase);

        Assert.Equal(1, bodies.Count(body => body == payload));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_adjudications WHERE EvidenceJson = '" + original.Replace("'", "''", StringComparison.Ordinal) + "';"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pure_exact_duplicate_production_sweep_has_no_adjudication_send_or_paid_record(bool protectedSources)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: protectedSources, exact: true);

        LongRestMaintenanceCapability? capability = protectedSources ? await FreshAsync(f) : null;

        var result = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, capability, CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Completed, result.Phase);

        Assert.Equal(2, f.Handler.SendCount);

        Assert.Equal(2L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_adjudications;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects WHERE PayloadKindCode = 2;"));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_receipts;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Canonical_commit_before_operation_checkpoint_replays_without_repeating_paid_or_canonical_effects(bool protectedSources)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: protectedSources, exact: true);

        await f.H.ExecuteAsync("CREATE TEMP TRIGGER fixture_stop_checkpoint BEFORE UPDATE OF CheckpointPayload ON LongRunningOperations WHEN EXISTS (SELECT 1 FROM long_rest_receipts) BEGIN SELECT RAISE(ABORT, 'fixture interrupted before operation checkpoint'); END;");

        LongRestMaintenanceCapability? initial = protectedSources ? await FreshAsync(f) : null;

        _ = await Record.ExceptionAsync(() => f.Coordinator.ResumeAsync(f.Operation, initial, CancellationToken.None));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_receipts;"));

        int sends = f.Handler.SendCount;

        string receipt = (string)(await f.H.ScalarAsync("SELECT ReceiptId FROM long_rest_receipts;"))!;

        await f.H.ExecuteAsync("DROP TRIGGER fixture_stop_checkpoint;");

        await f.RebootAsync();

        var result = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation,
            protectedSources ? await FreshAsync(f) : null, CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Completed, result.Phase);

        Assert.Equal(sends, f.Handler.SendCount);

        Assert.Equal(receipt, await f.H.ScalarAsync("SELECT ReceiptId FROM long_rest_receipts;"));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_receipts;"));

        Assert.Equal(LongRunningOperationState.Completed, (await f.Operations.GetAsync(f.Operation))!.State);
    }

    private static Task<RetroDownfall.Arcanum.Core.Primitives.Result<LongRestMaintenanceCapability>> IssueAsync(MaintenanceFixture f) =>
        f.Maintenance.CreateCapabilityAsync(f.Operation, f.Invocation, f.Claim, f.Scope, f.Authority, CancellationToken.None);

    private static async Task<LongRestMaintenanceCapability> FreshAsync(MaintenanceFixture f) => LongRestSweepHarness.Require(await IssueAsync(f));

    private static string DecisionOrSummary(string body) => body.Contains("long_rest_adjudication", StringComparison.Ordinal)
        ? "{\"kind\":\"NoChange\",\"survivorVersionId\":null}" : "{\"summary\":\"Accepted summary.\"}";
}
