using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.Operations;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The launch a crash left with no journal, finished before readiness rather than after it.
/// </summary>
/// <remarks>
/// The scan that finds the row is the adopter's and is unchanged. What is new is where its answer is
/// spent: inside the bootstrap, before the signal every pool, worker and endpoint waits on, instead of
/// in a periodic pass that runs afterwards on a ten-second budget while the host is already serving.
/// </remarks>
[Collection("WorkspacePathPolicy")]
public sealed class CovenantOfflineTransitionLaunchGapResumptionTests : IAsyncLifetime
{

    private static readonly CancellationToken Token = CancellationToken.None;

    private static readonly CovenantExclusiveRecoveryOwner Adopted = new(
        Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
        CovenantExclusiveOperation.CovenantReset,
        new CovenantDigest(Convert.FromHexString(new string('a', 64))));

    private readonly TempWorkspace _workspace = new();

    public Task InitializeAsync() => _workspace.InitializeAsync();

    public Task DisposeAsync() => _workspace.DisposeAsync();

    [Theory]

    [InlineData(LongRunningOperationSettlementOutcome.Completed)]

    [InlineData(LongRunningOperationSettlementOutcome.Failed)]

    [InlineData(LongRunningOperationSettlementOutcome.Abandoned)]
    public async Task Every_durable_verdict_lets_readiness_proceed(
        LongRunningOperationSettlementOutcome verdict)
    {

        using Held held = Hold("verdict-" + verdict);

        RecordingDispatch dispatch = new(
            Result<LongRunningOperationSettlementOutcome>.Success(verdict));

        Result resumed = await CovenantOfflineTransitionLaunchGapResumption
            .ResumeBeforeReadinessAsync(dispatch, held.Lock, held.Root, Adopted, Token);

        Assert.True(resumed.IsSuccess, resumed.IsFailure ? resumed.Error.Message : null);

        Assert.Equal(Adopted.OperationId, dispatch.Dispatched);

    }

    [Theory]

    [InlineData(LongRunningOperationSettlementOutcome.RequiresAttention)]

    [InlineData(LongRunningOperationSettlementOutcome.ConcurrencyLost)]

    [InlineData(LongRunningOperationSettlementOutcome.OwnedInProcess)]

    [InlineData(LongRunningOperationSettlementOutcome.NotFound)]
    public async Task A_verdict_short_of_terminal_refuses_readiness(
        LongRunningOperationSettlementOutcome verdict)
    {

        using Held held = Hold("short-" + verdict);

        RecordingDispatch dispatch = new(
            Result<LongRunningOperationSettlementOutcome>.Success(verdict));

        Result resumed = await CovenantOfflineTransitionLaunchGapResumption
            .ResumeBeforeReadinessAsync(dispatch, held.Lock, held.Root, Adopted, Token);

        Assert.True(resumed.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, resumed.Error.Code);

    }

    /// <summary>
    /// Nothing adopted is nothing to do, and that is the ordinary case rather than an error.
    /// </summary>
    /// <remarks>
    /// It is also what the adopter answers for an ordinary retention mutation, which closed no
    /// admission and has no exclusive scope to resume. Dispatching one here would run an erasure
    /// handler over a row that never launched a transition.
    /// </remarks>
    [Fact]
    public async Task No_adopted_owner_dispatches_nothing()
    {

        using Held held = Hold("none");

        RecordingDispatch dispatch = new(
            Result<LongRunningOperationSettlementOutcome>.Success(
                LongRunningOperationSettlementOutcome.Completed));

        Result resumed = await CovenantOfflineTransitionLaunchGapResumption
            .ResumeBeforeReadinessAsync(dispatch, held.Lock, held.Root, adopted: null, Token);

        Assert.True(resumed.IsSuccess, resumed.IsFailure ? resumed.Error.Message : null);

        Assert.Null(dispatch.Dispatched);

    }

    [Fact]
    public async Task A_dispatch_refusal_travels_out_unchanged()
    {

        using Held held = Hold("refused");

        RecordingDispatch dispatch = new(
            Result<LongRunningOperationSettlementOutcome>.Failure(
                new Error(ErrorCodes.Covenant.ManualRecoveryRequired, "no lease")));

        Result resumed = await CovenantOfflineTransitionLaunchGapResumption
            .ResumeBeforeReadinessAsync(dispatch, held.Lock, held.Root, Adopted, Token);

        Assert.True(resumed.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, resumed.Error.Code);

    }

    [Fact]
    public async Task A_lock_held_for_another_root_refuses()
    {

        using Held held = Hold("foreign");

        string elsewhere = _workspace.CreateSubdir("launch-gap-elsewhere");

        using ArcanumMaintenanceLock foreign = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(elsewhere));

        await Assert.ThrowsAnyAsync<Exception>(
            async () => await CovenantOfflineTransitionLaunchGapResumption
                .ResumeBeforeReadinessAsync(
                    new RecordingDispatch(
                        Result<LongRunningOperationSettlementOutcome>.Success(
                            LongRunningOperationSettlementOutcome.Completed)),
                    foreign,
                    held.Root,
                    Adopted,
                    Token));

    }

    private Held Hold(string name)
    {

        string root = _workspace.CreateSubdir("launch-gap-" + name);

        return new Held(
            Assert.IsType<ArcanumMaintenanceLock>(ArcanumMaintenanceLock.TryAcquire(root)),
            root);

    }

    private sealed record Held(ArcanumMaintenanceLock Lock, string Root) : IDisposable
    {

        public void Dispose() => Lock.Dispose();

    }

    private sealed class RecordingDispatch(Result<LongRunningOperationSettlementOutcome> answer)
        : IGrimoireOfflineTransitionHandlerDispatch
    {

        internal Guid? Dispatched { get; private set; }

        public Task<Result<LongRunningOperationSettlementOutcome>> DispatchAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            string guardedDirectory,
            Guid operationId,
            CancellationToken cancellationToken)
        {

            Dispatched = operationId;

            return Task.FromResult(answer);

        }

    }

}
