using System.Data.Common;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Tests.Fixtures;
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
    public static TheoryData<string> SelfAdmittingEntries => new() { "resume", "recover" };

    public static TheoryData<string> AdmittedEntries => new() { "resume-admitted", "maintenance-admitted" };

    public static TheoryData<string> AllEntries => new() { "resume", "recover", "resume-admitted", "maintenance-admitted" };

    public static TheoryData<string, object> WrongKinds
    {
        get
        {
            TheoryData<string, object> rows = new();

            foreach (string entry in new[] { "resume-admitted", "maintenance-admitted" })
            {
                foreach (GrimoireWorkKind kind in Enum.GetValues<GrimoireWorkKind>())
                {
                    if (kind != GrimoireWorkKind.LongRestSweep) rows.Add(entry, kind);
                }
            }

            return rows;
        }
    }

    [Theory]
    [MemberData(nameof(SelfAdmittingEntries))]
    public async Task Self_admitting_entry_keeps_its_own_lease_until_the_admitted_work_has_returned(string entryPoint)
    {
        Harness h = new(entryPoint);

        TaskCompletionSource disposing = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        h.Gate.Lease.BeforeDisposal = async () =>
        {
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
    }

    [Theory]
    [MemberData(nameof(AdmittedEntries))]
    public async Task Admitted_entry_borrows_the_callers_lease_and_never_consults_the_gate_or_disposes_it(string entryPoint)
    {
        Harness h = new(entryPoint);

        ProbeLease borrowed = new();

        await h.InvokeExpectedOutcomeAsync(borrowed);

        Assert.Empty(h.Gate.Requested);

        Assert.Equal(0, borrowed.Disposals);

        Assert.Equal(0, h.Gate.Lease.Disposals);

        await borrowed.DisposeAsync();

        Assert.Equal(1, borrowed.Disposals);
    }

    [Theory]
    [MemberData(nameof(AllEntries))]
    public async Task Caller_cancellation_precedes_both_borrowing_and_acquisition(string entryPoint)
    {
        Harness h = new(entryPoint);

        ProbeLease borrowed = new();

        using CancellationTokenSource caller = new();

        caller.Cancel();

        OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.InvokeAsync(caller.Token, borrowed));

        Assert.Equal(caller.Token, failure.CancellationToken);

        Assert.Empty(h.Gate.Requested);

        Assert.Equal(0, h.Configuration.Calls);

        Assert.Equal(0, borrowed.Disposals);

        Assert.Equal(0, h.Gate.Lease.Disposals);
    }

    [Theory]
    [InlineData("resume", "refused")]
    [InlineData("recover", "refused")]
    [InlineData("resume", "cancelled-refusal")]
    [InlineData("recover", "cancelled-refusal")]
    [InlineData("resume", "null-success")]
    [InlineData("recover", "null-success")]
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
    }

    [Fact]
    public async Task The_Session_shell_refuses_a_closing_gate_without_writing_the_operation()
    {
        // A real gate in Closing refuses every new work lease: the shell must defer before any store is touched.
        GrimoireConnectionAdmissionGate real = new(TimeProvider.System);

        await using IGrimoireClosingOwner closing = LongRestSweepHarness.Require(real.BeginOrResumeExclusive(
            new(Guid.NewGuid(), CovenantExclusiveOperation.CovenantReset, new CovenantDigest(new byte[32]))));

        Harness h = new("resume", real);

        LongRunningOperation[] before = [.. h.Operations.Operations];

        LongRestMaintenanceDeferredException failure = await Assert.ThrowsAsync<LongRestMaintenanceDeferredException>(() => h.InvokeAsync(CancellationToken.None));

        Assert.Equal(CancellationToken.None, failure.CancellationToken);

        Assert.Equal(0, h.Configuration.Calls);

        Assert.Empty(h.Operations.LeaseAcquisitions);

        Assert.Equal(before, h.Operations.Operations.ToArray());
    }

    [Theory]
    [MemberData(nameof(WrongKinds))]
    public async Task A_lease_that_is_not_a_Long_Rest_sweep_lease_is_refused_at_both_admitted_entries(string entryPoint, object value)
    {
        GrimoireWorkKind kind = Assert.IsType<GrimoireWorkKind>(value);

        Harness h = new(entryPoint);

        ProbeLease wrong = new() { Kind = kind };

        await Assert.ThrowsAsync<ArgumentException>(() => h.InvokeAsync(CancellationToken.None, wrong));

        // Refused before any work: no configuration, no store, no gate, and the caller's lease is not touched.
        Assert.Equal(0, h.Configuration.Calls);

        Assert.Empty(h.Operations.LeaseAcquisitions);

        Assert.Empty(h.Gate.Requested);

        Assert.Equal(0, wrong.Disposals);
    }

    [Theory]
    [MemberData(nameof(AdmittedEntries))]
    public async Task A_missing_lease_is_refused_at_both_admitted_entries(string entryPoint)
    {
        Harness h = new(entryPoint);

        await Assert.ThrowsAsync<ArgumentNullException>(() => h.InvokeAsync(CancellationToken.None, null));

        Assert.Equal(0, h.Configuration.Calls);

        Assert.Empty(h.Gate.Requested);
    }

    private sealed class Harness
    {
        private readonly string _entryPoint;

        private readonly LongRestMaintenance _maintenance;

        private readonly LongRestSweepCoordinator _coordinator;

        private readonly LongRunningOperation _operation;

        internal ProbeGate Gate { get; } = new();

        internal ProbeConfiguration Configuration { get; } = new();

        internal FakeLongRunningOperationStore Operations { get; } = new(TimeProvider.System);

        internal Harness(string entryPoint, IGrimoireConnectionAdmissionGate? leaseGate = null)
        {
            _entryPoint = entryPoint;

            LongRestConfigurationEpoch epoch = new(new TestOptionsMonitor<ArcanumSettings>(new()), Guid.NewGuid());

            _maintenance = new(epoch, null!, null!, null!, null!, null!, null!, null!, null!, null!, TimeProvider.System);

            _operation = Operations.Seed(LongRunningOperationKinds.LongRestSweep, LongRunningOperationRecoveryPolicy.ResumeFromCheckpoint);

            LongRunningOperationOwnership ownership = new();

            Assert.True(ownership.TryClaim(_operation.Id, out _));

            _coordinator = new(Operations, null!, null!, Configuration, null!, _maintenance, null!, ownership,
                null!, null!, TimeProvider.System, leaseGate ?? Gate);
        }

        /// <param name="lease">The caller's lease for the admitted entries; the self-admitting entries mint their own.</param>
        internal async Task InvokeAsync(CancellationToken ct, IGrimoireWorkLease? lease = null)
        {
            if (_entryPoint == "recover")
            {
                LongRunningOperationRecoveryResult result = await _coordinator.RecoverAsync(_operation, ct);

                Assert.Equal("long_rest.worker_unavailable", result.ErrorCode);
            }
            else if (_entryPoint == "maintenance-admitted")
            {
                Result<LongRestSweepProgress> result = await _maintenance.ProcessAdmittedAsync(_operation.Id, "unused", null, lease!, ct);

                Assert.True(result.IsFailure);

                Assert.Equal(ErrorCodes.LongRest.StaleInput, result.Error.Code);
            }
            else if (_entryPoint == "resume-admitted")
            {
                await ((ILongRestAdmittedSweep)_coordinator).ResumeAdmittedAsync(_operation.Id, lease!, ct);
            }
            else
            {
                await _coordinator.ResumeAsync(_operation.Id, null, ct);
            }
        }

        internal async Task InvokeExpectedOutcomeAsync(IGrimoireWorkLease? lease = null)
        {
            if (_entryPoint is "recover" or "maintenance-admitted") await InvokeAsync(CancellationToken.None, lease);
            else await Assert.ThrowsAsync<ConfigurationProbeException>(() => InvokeAsync(CancellationToken.None, lease));
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

        public bool MaintenanceRevocationPending => MaintenanceRevocation.IsCancellationRequested;

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

        public bool AttestDrainedMaintenance(IGrimoireWorkLease lease, long previousGeneration, long currentGeneration) => false;

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
