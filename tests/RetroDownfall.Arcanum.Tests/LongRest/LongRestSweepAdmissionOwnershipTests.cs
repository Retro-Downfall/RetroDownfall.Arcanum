using System.Data.Common;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Tests.Support;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;
using RetroDownfall.Arcanum.Tests.Operations;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed class LongRestSweepAdmissionOwnershipTests
{
    [Theory]
    [InlineData("resume")]
    [InlineData("recover")]
    [InlineData("maintenance")]
    public async Task Owned_entry_clears_the_binding_before_awaited_lease_cleanup(string entryPoint)
    {
        Harness h = new(entryPoint);

        TaskCompletionSource disposing = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        h.Gate.Lease.BeforeDisposal = async () =>
        {
            Assert.Throws<InvalidOperationException>(() => h.Holder.Lease);

            disposing.TrySetResult();

            await release.Task;
        };

        Task invocation = h.InvokeExpectedOutcomeAsync();

        try
        {
            await disposing.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(invocation.IsCompleted);

            Assert.Equal([GrimoireWorkKind.LongRestSweep], h.Gate.Requested);

            Assert.Equal(1, h.Gate.Lease.Disposals);
        }
        finally
        {
            release.TrySetResult();

            await invocation;
        }

        Assert.Throws<InvalidOperationException>(() => h.Holder.Lease);
    }

    [Theory]
    [InlineData("resume")]
    [InlineData("recover")]
    [InlineData("maintenance")]
    public async Task Borrowed_entry_keeps_the_exact_callers_binding_and_lease_alive(string entryPoint)
    {
        Harness h = new(entryPoint);

        ProbeLease borrowed = new() { Kind = GrimoireWorkKind.LongRunningOperationRecovery };

        using IDisposable binding = h.Holder.Bind(borrowed);

        await h.InvokeExpectedOutcomeAsync();

        Assert.Empty(h.Gate.Requested);

        Assert.Same(borrowed, h.Holder.Lease);

        Assert.Equal(0, borrowed.Disposals);

        Assert.Equal(0, h.Gate.Lease.Disposals);

        binding.Dispose();

        await borrowed.DisposeAsync();

        Assert.Equal(1, borrowed.Disposals);
    }

    [Theory]
    [InlineData("resume", false)]
    [InlineData("resume", true)]
    [InlineData("recover", false)]
    [InlineData("recover", true)]
    [InlineData("maintenance", false)]
    [InlineData("maintenance", true)]
    public async Task Caller_cancellation_precedes_both_borrowing_and_acquisition(string entryPoint, bool bound)
    {
        Harness h = new(entryPoint);

        ProbeLease borrowed = new() { Kind = GrimoireWorkKind.LongRunningOperationRecovery };

        using IDisposable? binding = bound ? h.Holder.Bind(borrowed) : null;

        using CancellationTokenSource caller = new();

        caller.Cancel();

        OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.InvokeAsync(caller.Token));

        Assert.Equal(caller.Token, failure.CancellationToken);

        Assert.Empty(h.Gate.Requested);

        Assert.Equal(0, h.Configuration.Calls);

        Assert.Equal(0, borrowed.Disposals);

        Assert.Equal(0, h.Gate.Lease.Disposals);

        if (bound) Assert.Same(borrowed, h.Holder.Lease);
    }

    [Theory]
    [InlineData("resume", "refused")]
    [InlineData("recover", "refused")]
    [InlineData("maintenance", "refused")]
    [InlineData("resume", "cancelled-refusal")]
    [InlineData("recover", "cancelled-refusal")]
    [InlineData("maintenance", "cancelled-refusal")]
    [InlineData("resume", "null-success")]
    [InlineData("recover", "null-success")]
    [InlineData("maintenance", "null-success")]
    public async Task Acquisition_refusals_stop_before_admitted_work(string entryPoint, string mode)
    {
        Harness h = new(entryPoint);

        using CancellationTokenSource caller = new();

        h.Gate.Mode = mode;

        if (mode == "cancelled-refusal") h.Gate.DuringAcquisition = caller.Cancel;

        if (mode == "null-success")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => h.InvokeAsync(caller.Token));
        }
        else if (mode == "cancelled-refusal")
        {
            OperationCanceledException failure = await Assert.ThrowsAsync<OperationCanceledException>(() => h.InvokeAsync(caller.Token));

            Assert.Equal(caller.Token, failure.CancellationToken);
        }
        else
        {
            LongRestMaintenanceDeferredException failure = await Assert.ThrowsAsync<LongRestMaintenanceDeferredException>(() => h.InvokeAsync(caller.Token));

            Assert.Equal(CancellationToken.None, failure.CancellationToken);
        }

        Assert.Equal([GrimoireWorkKind.LongRestSweep], h.Gate.Requested);

        Assert.Equal(0, h.Configuration.Calls);

        Assert.Equal(0, h.Gate.Lease.Disposals);

        Assert.Throws<InvalidOperationException>(() => h.Holder.Lease);
    }

    [Theory]
    [InlineData("resume")]
    [InlineData("recover")]
    [InlineData("maintenance")]
    public async Task Binding_refusal_disposes_only_the_newly_acquired_lease(string entryPoint)
    {
        Harness h = new(entryPoint);

        ProbeLease existing = new() { Kind = GrimoireWorkKind.LongRunningOperationRecovery };

        IDisposable? installed = null;

        // The real holder is changed reentrantly at the gate call's return boundary.
        // This deliberately exercises Bind's refusal; it is not a claim that normal
        // scoped serving composition concurrently shares this holder.
        h.Gate.DuringAcquisition = () => installed = h.Holder.Bind(existing);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => h.InvokeAsync(CancellationToken.None));

            Assert.Equal([GrimoireWorkKind.LongRestSweep], h.Gate.Requested);

            Assert.Equal(1, h.Gate.Lease.Disposals);

            Assert.Equal(0, existing.Disposals);

            Assert.Equal(0, h.Configuration.Calls);

            Assert.Same(existing, h.Holder.Lease);
        }
        finally
        {
            installed?.Dispose();

            await existing.DisposeAsync();
        }
    }

    private sealed class Harness
    {
        private readonly string _entryPoint;

        private readonly LongRestMaintenance _maintenance;

        private readonly LongRestSweepCoordinator _coordinator;

        private readonly LongRunningOperation _operation;

        internal ProbeGate Gate { get; } = new();

        internal ProbeConfiguration Configuration { get; } = new();

        internal LongRestWorkAdmission Holder { get; }

        internal Harness(string entryPoint)
        {
            _entryPoint = entryPoint;

            Holder = new(Gate);

            LongRestConfigurationEpoch epoch = new(new TestOptionsMonitor<ArcanumSettings>(new()), Guid.NewGuid());

            _maintenance = new(epoch, null!, null!, null!, null!, null!, null!, null!, null!, Holder, null!, TimeProvider.System);

            _operation = new FakeLongRunningOperationStore(TimeProvider.System)
                .Seed(LongRunningOperationKinds.LongRestSweep, LongRunningOperationRecoveryPolicy.ResumeFromCheckpoint);

            LongRunningOperationOwnership ownership = new();

            Assert.True(ownership.TryClaim(_operation.Id, out _));

            _coordinator = new(null!, null!, null!, Configuration, null!, null!, null!, ownership,
                null!, null!, TimeProvider.System, Holder);
        }

        internal async Task InvokeAsync(CancellationToken ct)
        {
            if (_entryPoint == "recover")
            {
                LongRunningOperationRecoveryResult result = await _coordinator.RecoverAsync(_operation, ct);

                Assert.Equal("long_rest.worker_unavailable", result.ErrorCode);
            }
            else if (_entryPoint == "maintenance")
            {
                Result<LongRestSweepProgress> result = await _maintenance.ProcessAsync(_operation.Id, "unused", null, ct);

                Assert.True(result.IsFailure);

                Assert.Equal(ErrorCodes.LongRest.StaleInput, result.Error.Code);
            }
            else
            {
                await _coordinator.ResumeAsync(_operation.Id, null, ct);
            }
        }

        internal async Task InvokeExpectedOutcomeAsync()
        {
            if (_entryPoint != "resume") await InvokeAsync(CancellationToken.None);
            else await Assert.ThrowsAsync<ConfigurationProbeException>(() => InvokeAsync(CancellationToken.None));
        }
    }

    private sealed class ConfigurationProbeException : Exception
    {
    }

    private sealed class ProbeConfiguration : ILongRestConfigurationProvider
    {
        internal int Calls { get; private set; }

        public LongRestSweepConfiguration Capture()
        {
            Calls++;

            throw new ConfigurationProbeException();
        }

        public bool IsCurrent(LongRestConfigurationStamp stamp) => throw new NotSupportedException();
    }

    private sealed class ProbeLease : IGrimoireWorkLease
    {
        public GrimoireWorkKind Kind { get; init; } = GrimoireWorkKind.LongRestSweep;

        public long Generation => 1;

        public CancellationToken MaintenanceRevocation => CancellationToken.None;

        internal int Disposals { get; private set; }

        internal Func<ValueTask>? BeforeDisposal { get; set; }

        public bool TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? effectGroup) => throw new NotSupportedException();

        public async ValueTask DisposeAsync()
        {
            Disposals++;

            if (BeforeDisposal is { } before) await before();
        }
    }

    private sealed class ProbeGate : IGrimoireConnectionAdmissionGate
    {
        internal ProbeLease Lease { get; } = new();

        internal List<GrimoireWorkKind> Requested { get; } = [];

        internal string Mode { get; set; } = "owned";

        internal Action? DuringAcquisition { get; set; }

        public long CurrentGeneration => 1;

        public bool TryAcquireWorkLease(GrimoireWorkKind kind, out IGrimoireWorkLease? lease)
        {
            Requested.Add(kind);

            DuringAcquisition?.Invoke();

            lease = Mode == "owned" ? Lease : null;

            return Mode is "owned" or "null-success";
        }

        public bool TryAcquireRequestLease(GrimoireRequestKind kind, out IGrimoireRequestLease? lease) => throw new NotSupportedException();

        public IGrimoireConnectionOpenTicket AcquireOrdinaryOpen(DbConnection connection) => throw new NotSupportedException();

        public Result<IGrimoireClosingOwner> BeginOrResumeExclusive(CovenantExclusiveRecoveryOwner owner,
            IGrimoireRequestLease? initiatingRequest = null, DbConnection? scopedConnection = null) => throw new NotSupportedException();

        public ValueTask<Result> DrainRequestAndWorkAsync(IGrimoireClosingOwner closingOwner, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<Result<IGrimoireExclusiveClosedLease>> CloseConnectionAdmissionAsync(IGrimoireClosingOwner closingOwner,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<Result> AbortClosingAsync(IGrimoireClosingOwner closingOwner,
            Func<CancellationToken, ValueTask<bool>> proveNoDestructiveEffectAsync, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<long> WaitForNextOpenGenerationAsync(long observedGeneration, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<Result<IGrimoireExpiredLeaseAdoptionInterlock>> AcquireExpiredLeaseAdoptionInterlockAsync(
            CovenantExclusiveRecoveryOwner candidateOwner,
            Func<CovenantExclusiveRecoveryOwner, CancellationToken, ValueTask<bool>> revalidateDurableOwnerAsync,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
