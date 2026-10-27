using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class GrimoireConnectionAdmissionGateTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Aborted_promoted_request_remains_counted_until_its_tail_disposes(bool timeoutFirst, bool stream)
    {
        ManualTimeProvider clock = new();

        GrimoireConnectionAdmissionGate gate = CreateGate(clock);

        Assert.True(gate.TryAcquireRequestLease(stream ? GrimoireRequestKind.QuiesceableStream : GrimoireRequestKind.Finite, out IGrimoireRequestLease? acquired));

        await using IGrimoireRequestLease request = acquired!;

        await using IAsyncDisposable? blocker = timeoutFirst ? AcquireLifetime(gate, work: true) : null;

        using SqliteConnection exact = new();

        await using IGrimoireClosingOwner first = Begin(gate, Owner(91), request, exact);

        if (timeoutFirst)
        {
            await TimeoutAndAbort(gate, clock, first);

            await blocker!.DisposeAsync();
        }
        else
        {
            Assert.True((await gate.DrainRequestAndWorkAsync(first, CancellationToken.None)).IsSuccess);

            Assert.True((await gate.AbortClosingAsync(first, static _ => ValueTask.FromResult(true), CancellationToken.None)).IsSuccess);
        }

        Assert.Equal(1, request.Generation);

        Assert.Equal(2, gate.CurrentGeneration);

        await using IGrimoireClosingOwner second = Begin(gate, Owner(92));

        using CancellationTokenSource wait = new();

        Task<Result> draining = gate.DrainRequestAndWorkAsync(second, wait.Token).AsTask();

        try
        {
            Assert.False(draining.IsCompleted);

            Assert.Equal(stream, request.MaintenanceRevocation.IsCancellationRequested);

            var premature = await gate.CloseConnectionAdmissionAsync(second, CancellationToken.None);

            Assert.True(premature.IsFailure);

            Assert.Equal("Grimoire.AdmissionLifecycleConflict", premature.Error.Code);

            await request.DisposeAsync();

            Assert.True((await draining.WaitAsync(BoundedWait)).IsSuccess);

            var closed = await gate.CloseConnectionAdmissionAsync(second, CancellationToken.None);

            Assert.True(closed.IsSuccess, closed.Error.Message);

            await closed.Value.DisposeAsync();
        }
        finally
        {
            await wait.CancelAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Aborted_promoted_survivor_prevents_drained_interval_attestation(bool disposedBeforeAbort)
    {
        GrimoireConnectionAdmissionGate gate = CreateGate();

        Assert.True(gate.TryAcquireRequestLease(GrimoireRequestKind.Finite, out IGrimoireRequestLease? acquired));

        await using IGrimoireRequestLease request = acquired!;

        using SqliteConnection exact = new();

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(93), request, exact);

        Assert.True((await gate.DrainRequestAndWorkAsync(closing, CancellationToken.None)).IsSuccess);

        if (disposedBeforeAbort) await request.DisposeAsync();

        Assert.True((await gate.AbortClosingAsync(closing, static _ => ValueTask.FromResult(true), CancellationToken.None)).IsSuccess);

        Assert.True(gate.TryAcquireWorkLease(GrimoireWorkKind.LongRestSweep, out IGrimoireWorkLease? fresh));

        await using IGrimoireWorkLease work = fresh!;

        Assert.Equal(disposedBeforeAbort, gate.AttestDrainedMaintenance(work, 1, 2));
    }

    [Fact]
    public async Task Promoted_request_released_during_abort_proof_is_not_resurrected()
    {
        GrimoireConnectionAdmissionGate gate = CreateGate();

        Assert.True(gate.TryAcquireRequestLease(GrimoireRequestKind.Finite, out IGrimoireRequestLease? acquired));

        await using IGrimoireRequestLease request = acquired!;

        using SqliteConnection exact = new();

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(94), request, exact);

        Assert.True((await gate.DrainRequestAndWorkAsync(closing, CancellationToken.None)).IsSuccess);

        TaskCompletionSource<bool> proof = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<Result> aborting = gate.AbortClosingAsync(closing, _ => new ValueTask<bool>(proof.Task), CancellationToken.None).AsTask();

        Assert.False(aborting.IsCompleted);

        await request.DisposeAsync();

        proof.SetResult(true);

        Assert.True((await aborting.WaitAsync(BoundedWait)).IsSuccess);

        await using IGrimoireClosingOwner next = Begin(gate, Owner(95));

        Assert.True((await gate.DrainRequestAndWorkAsync(next, CancellationToken.None)).IsSuccess);
    }
}
