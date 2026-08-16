using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Mcp;

/// <summary>
/// The connection-and-request-id table that carries one Covenant capability across the in-process
/// MCP task boundary (§10.14).
/// </summary>
/// <remarks>
/// Request ids repeat over a long-lived connection, so almost every assertion here is about the ABA
/// window that creates: a duplicate id must never overwrite a live registration, and a late cleanup
/// carrying an earlier request's nonce must never remove the registration that replaced it.
/// </remarks>
public sealed class CovenantToolCapabilityRegistryTests
{

    private const string Connection = "connection-a";

    [Fact]
    public async Task A_registered_capability_is_taken_exactly_once()
    {
        CovenantToolCapabilityRegistry registry = NewRegistry(out _);

        await using CovenantToolInvocationContext capability = Capability(out CovenantToolCapabilityNonce nonce);

        Assert.True(registry.TryRegister(Connection, "1", capability, nonce));

        Result<CovenantToolCapabilityGrant> first = registry.TryTake(Connection, "1");
        Result<CovenantToolCapabilityGrant> replay = registry.TryTake(Connection, "1");

        Assert.True(first.IsSuccess, first.Error.Message);
        Assert.Same(capability, first.Value.Capability);
        Assert.Equal(CovenantToolCapabilityState.Taken, capability.State);
        Assert.Equal(ErrorCodes.Covenant.LifecycleConflict, replay.Error.Code);
    }

    [Fact]
    public async Task A_duplicate_request_id_can_never_overwrite_a_live_registration()
    {
        CovenantToolCapabilityRegistry registry = NewRegistry(out _);

        await using CovenantToolInvocationContext first = Capability(out CovenantToolCapabilityNonce firstNonce);
        await using CovenantToolInvocationContext second = Capability(out CovenantToolCapabilityNonce secondNonce);

        Assert.True(registry.TryRegister(Connection, "1", first, firstNonce));
        Assert.False(registry.TryRegister(Connection, "1", second, secondNonce));

        Result<CovenantToolCapabilityGrant> taken = registry.TryTake(Connection, "1");

        Assert.Same(first, taken.Value.Capability);
        Assert.Equal(CovenantToolCapabilityState.Registered, second.State);
    }

    [Fact]
    public async Task The_grant_carries_the_nonce_the_capability_will_accept()
    {
        CovenantToolCapabilityRegistry registry = NewRegistry(out _);

        await using CovenantToolInvocationContext capability = Capability(out CovenantToolCapabilityNonce nonce);

        _ = registry.TryRegister(Connection, "1", capability, nonce);

        CovenantToolCapabilityGrant grant = registry.TryTake(Connection, "1").Value;

        Assert.True(grant.Nonce.Equals(nonce));

        Result<IDisposable> lease = capability.TryAcquireUse(grant.Nonce);

        Assert.True(lease.IsSuccess, lease.Error.Message);

        lease.Value.Dispose();
    }

    [Fact]
    public async Task A_late_cleanup_carrying_an_earlier_nonce_removes_nothing()
    {
        CovenantToolCapabilityRegistry registry = NewRegistry(out _);

        await using CovenantToolInvocationContext earlier = Capability(out CovenantToolCapabilityNonce earlierNonce);
        await using CovenantToolInvocationContext later = Capability(out CovenantToolCapabilityNonce laterNonce);

        _ = registry.TryRegister(Connection, "1", earlier, earlierNonce);

        Assert.True(registry.Remove(Connection, "1", earlierNonce));

        _ = registry.TryRegister(Connection, "1", later, laterNonce);

        // The earlier request's finally block runs now, one id reuse too late.
        Assert.False(registry.Remove(Connection, "1", earlierNonce));
        Assert.Same(later, registry.TryTake(Connection, "1").Value.Capability);
    }

    [Fact]
    public async Task Removal_is_the_only_thing_that_frees_a_reserved_request_id()
    {
        CovenantToolCapabilityRegistry registry = NewRegistry(out _);

        await using CovenantToolInvocationContext capability = Capability(out CovenantToolCapabilityNonce nonce);

        _ = registry.TryRegister(Connection, "1", capability, nonce);

        _ = registry.TryTake(Connection, "1");

        await using CovenantToolInvocationContext replacement = Capability(out CovenantToolCapabilityNonce replacementNonce);

        // The handler is still running: the id stays reserved even though nothing is Registered.
        Assert.False(registry.TryRegister(Connection, "1", replacement, replacementNonce));

        Assert.True(registry.Remove(Connection, "1", nonce));
        Assert.True(registry.TryRegister(Connection, "1", replacement, replacementNonce));
    }

    [Fact]
    public async Task The_ttl_sweep_reclaims_an_abandoned_registration_and_leaves_a_taken_one()
    {
        CovenantToolCapabilityRegistry registry = NewRegistry(out FakeClock clock);

        await using CovenantToolInvocationContext abandoned = Capability(out CovenantToolCapabilityNonce abandonedNonce);
        await using CovenantToolInvocationContext running = Capability(out CovenantToolCapabilityNonce runningNonce);

        _ = registry.TryRegister(Connection, "1", abandoned, abandonedNonce);
        _ = registry.TryRegister(Connection, "2", running, runningNonce);
        _ = registry.TryTake(Connection, "2");

        clock.Advance(TimeSpan.FromMinutes(11));

        IReadOnlyList<CovenantToolInvocationContext> reclaimed = registry.SweepExpired();

        Assert.Same(abandoned, Assert.Single(reclaimed));
        Assert.Equal(ErrorCodes.Covenant.LifecycleConflict, registry.TryTake(Connection, "1").Error.Code);

        // The running handler still owns its reservation: only its own removal frees the id.
        Assert.Equal(CovenantToolCapabilityState.Taken, running.State);
        Assert.True(registry.Remove(Connection, "2", runningNonce));
    }

    [Fact]
    public async Task Two_connections_keep_isolated_registrations_for_one_request_id()
    {
        CovenantToolCapabilityRegistry registry = NewRegistry(out _);

        await using CovenantToolInvocationContext first = Capability(out CovenantToolCapabilityNonce firstNonce);
        await using CovenantToolInvocationContext second = Capability(out CovenantToolCapabilityNonce secondNonce);

        Assert.True(registry.TryRegister("connection-a", "1", first, firstNonce));
        Assert.True(registry.TryRegister("connection-b", "1", second, secondNonce));

        Assert.Same(first, registry.TryTake("connection-a", "1").Value.Capability);
        Assert.Same(second, registry.TryTake("connection-b", "1").Value.Capability);
    }

    [Fact]
    public async Task Concurrent_registration_of_one_request_id_admits_exactly_one_capability()
    {
        CovenantToolCapabilityRegistry registry = NewRegistry(out _);

        (CovenantToolInvocationContext Capability, CovenantToolCapabilityNonce Nonce)[] candidates =
        [
            .. Enumerable.Range(0, 16).Select(static _ =>
            {
                CovenantToolInvocationContext capability = Capability(out CovenantToolCapabilityNonce nonce);

                return (capability, nonce);
            }),
        ];

        bool[] outcomes = await Task.WhenAll(
            candidates.Select(candidate => Task.Run(() =>
                registry.TryRegister(Connection, "1", candidate.Capability, candidate.Nonce))));

        Assert.Single(outcomes, static won => won);

        foreach ((CovenantToolInvocationContext capability, _) in candidates)
        {
            await capability.DisposeAsync();
        }
    }

    [Fact]
    public async Task Concurrent_takes_of_one_registration_admit_exactly_one_handler()
    {
        CovenantToolCapabilityRegistry registry = NewRegistry(out _);

        await using CovenantToolInvocationContext capability = Capability(out CovenantToolCapabilityNonce nonce);

        _ = registry.TryRegister(Connection, "1", capability, nonce);

        Result<CovenantToolCapabilityGrant>[] outcomes = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ => Task.Run(() => registry.TryTake(Connection, "1"))));

        Assert.Single(outcomes, static grant => grant.IsSuccess);
    }

    private static CovenantToolCapabilityRegistry NewRegistry(out FakeClock clock)
    {
        FakeClock created = new();

        clock = created;

        return new CovenantToolCapabilityRegistry(
            TimeSpan.FromMinutes(10),
            created.Read);
    }

    private static CovenantToolInvocationContext Capability(out CovenantToolCapabilityNonce nonce)
    {
        CovenantTurnPlan plan = CovenantTask6Fixture.IntegrationPlan();

        CovenantMutationCollector collector = new(
            Guid.NewGuid(),
            plan.Digest,
            CovenantTask6Fixture.BranchId);

        nonce = CovenantToolCapabilityNonce.Create();

        return new CovenantToolInvocationContext(
            collector,
            CovenantCapabilityFixtures.Campaign(),
            CovenantCapabilityFixtures.Admission(plan),
            CovenantCapabilityFixtures.Materialization(),
            new CovenantCapabilityFixtures.StubHeadProbe(),
            nonce,
            CovenantToolNames.ProposeCovenant,
            "call-1",
            retirementPreflight: null,
            wardReceipt: null,
            CancellationToken.None);
    }

    private sealed class FakeClock
    {

        private long _ticks = DateTime.UnixEpoch.Ticks;

        public long Read() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan amount) => Interlocked.Add(ref _ticks, amount.Ticks);

    }

}
