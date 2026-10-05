using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// Linearizability of acquire against close, and the absence of leaked registrations afterwards.
/// </summary>
/// <remarks>
/// The schedules use a fixed seed. A concurrency bug that only reproduces one run in fifty is worth
/// nothing as a regression test, so the interleaving is randomized but reproducible.
/// </remarks>
public sealed class CovenantOperationGateConcurrencyTests
{
    private const int Seed = 20260815;

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public async Task Thirty_two_readers_and_eight_writers_never_overlap_a_close()
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(
            drainTimeout: TimeSpan.FromSeconds(30));

        Random random = new(Seed);

        // How many scheduler turns each worker spends between steps. Counted in yields rather than
        // milliseconds so the schedule does not depend on how loaded the machine is: the shared fake clock
        // has no timers, and a wall-clock delay would make the interleaving a property of the host.
        int[] turns = [.. Enumerable.Range(0, 40).Select(_ => 1 + random.Next(0, 5) * 8)];

        // Readers that held a live lease while an exclusive operation held its closure. The close drains
        // every holder first, so this must stay zero; it is asserted at the end.
        int liveDuringClose = 0;

        int exclusivesHeld = 0;

        int closesObserved = 0;

        using CancellationTokenSource stop = new();

        List<Task> workers = [];

        for (int index = 0; index < 32; index++)
        {
            int slot = index;

            workers.Add(Task.Run(
                async () =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        Result<CovenantReadLease> acquired = await gate.AcquireReadAsync(
                            slot % 2 == 0
                                ? CovenantOperationScope.Global
                                : CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignOne),
                            Token);

                        if (acquired.IsFailure)
                        {
                            await YieldAsync(turns[slot]);

                            continue;
                        }

                        await using CovenantReadLease lease = acquired.Value;

                        // Checked while this lease is live: an exclusive holder cannot exist yet, because
                        // its drain is waiting for exactly this registration to release.
                        if (Volatile.Read(ref exclusivesHeld) > 0)
                        {
                            _ = Interlocked.Increment(ref liveDuringClose);
                        }

                        await YieldAsync(turns[slot]);

                        if (Volatile.Read(ref exclusivesHeld) > 0)
                        {
                            _ = Interlocked.Increment(ref liveDuringClose);
                        }
                    }
                },
                Token));
        }

        for (int index = 0; index < 8; index++)
        {
            int slot = 32 + index;

            workers.Add(Task.Run(
                async () =>
                {
                    for (int round = 0; round < 4; round++)
                    {
                        await YieldAsync(turns[slot]);

                        Result<CovenantExclusiveLease> exclusive = await gate.AcquireExclusiveAsync(
                            CovenantOperationGateFixture.Owner(
                                CovenantExclusiveOperation.CovenantReset,
                                operationId: Guid.NewGuid()),
                            Token);

                        if (exclusive.IsFailure)
                        {
                            continue;
                        }

                        _ = Interlocked.Increment(ref closesObserved);

                        _ = Interlocked.Increment(ref exclusivesHeld);

                        // Nothing else may hold a lease at this instant: the close drained them all.
                        Assert.Equal(0, gate.LiveRegistrationCount);

                        await YieldAsync(turns[slot]);

                        // Released before the disposition reopens admission, so a reader that acquires the
                        // moment it reopens is never mistaken for one that overlapped the close.
                        _ = Interlocked.Decrement(ref exclusivesHeld);

                        _ = await exclusive.Value.CompleteAsync(
                            CovenantExclusiveLeaseDisposition.CommitAndReopen,
                            Token);

                        await exclusive.Value.DisposeAsync();
                    }
                },
                Token));
        }

        await Task.WhenAll(workers.Skip(32));

        await stop.CancelAsync();

        await Task.WhenAll(workers.Take(32));

        Assert.True(Volatile.Read(ref closesObserved) > 0, "No exclusive close ever won the race.");

        Assert.Equal(0, Volatile.Read(ref liveDuringClose));

        Assert.Equal(0, gate.LiveRegistrationCount);
    }

    private static async Task YieldAsync(int turns)
    {
        for (int turn = 0; turn < turns; turn++)
        {
            await Task.Yield();
        }
    }

    [Fact]
    public async Task A_campaign_close_leaves_unrelated_scopes_running()
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(
            drainTimeout: TimeSpan.FromSeconds(30));

        await using CovenantReadLease unrelated = (await gate.AcquireReadAsync(
            CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignTwo),
            Token)).Value;

        await using CovenantCampaignExclusiveLease exclusive = (await gate.AcquireCampaignExclusiveAsync(
            CovenantOperationGateFixture.CampaignOne,
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CampaignDelete),
            Token)).Value;

        Assert.False(unrelated.Revocation.IsCancellationRequested);

        Assert.True((await unrelated.RevalidateAsync(Token)).IsSuccess);
    }

    [Fact]
    public async Task Two_campaign_closes_do_not_deadlock_each_other()
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(
            drainTimeout: TimeSpan.FromSeconds(30));

        Task<Result<CovenantCampaignExclusiveLease>> first = gate.AcquireCampaignExclusiveAsync(
            CovenantOperationGateFixture.CampaignOne,
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CampaignDelete),
            Token).AsTask();

        Task<Result<CovenantCampaignExclusiveLease>> second = gate.AcquireCampaignExclusiveAsync(
            CovenantOperationGateFixture.CampaignTwo,
            CovenantOperationGateFixture.Owner(
                CovenantExclusiveOperation.CampaignDelete,
                operationId: Guid.NewGuid()),
            Token).AsTask();

        Result<CovenantCampaignExclusiveLease>[] results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.True(result.IsSuccess));

        foreach (Result<CovenantCampaignExclusiveLease> result in results)
        {
            _ = await result.Value.CompleteAsync(CovenantExclusiveLeaseDisposition.RollbackAndReopen, Token);

            await result.Value.DisposeAsync();
        }

        Assert.Equal(0, gate.LiveRegistrationCount);
    }

    [Fact]
    public async Task Concurrent_disposal_releases_a_registration_exactly_once()
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        CovenantReadLease lease = (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Value;

        Task[] disposals = [.. Enumerable.Range(0, 16).Select(_ => lease.DisposeAsync().AsTask())];

        await Task.WhenAll(disposals);

        Assert.Equal(0, gate.LiveRegistrationCount);
    }
}
