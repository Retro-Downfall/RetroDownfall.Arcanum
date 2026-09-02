using System.Text.Json;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Tests.Covenant;
using ArcanumJsonRpcRequest = RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol.JsonRpcRequest;

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

    // SweepExpired() (below) is the only registry-side reclaim, and it has never had a production
    // caller — only a failed-send unbind that was already wired for every other binding store
    // (SessionAttachmentToolAmbient, ApplyPatchInvocationBinding, PersistedToolInvocationBinding,
    // ApprenticeToolInvocationBinding) but stopped short of this registry. TryRegister is TryAdd-only,
    // so a request id whose frame never reached the wire stayed permanently stranded on a live
    // connection.
    [Fact]
    public void A_failed_send_releases_its_registration_so_a_retry_can_register()
    {

        CovenantToolCapabilityRegistry registry = new();

        using IDisposable staging = CovenantToolStagingAmbient.Push(Staging(registry));

        ArcanumJsonRpcRequest first = ToolsCall("7", CovenantToolNames.ProposeCovenant);

        _ = SessionAttachmentAmbientSend.ApplyAmbientBinding(Connection, first);

        Assert.Equal(1, registry.CountForTests);

        // Mirrors InProcessMcpTransport.WriteRequestAsync's catch block: the frame never reached the
        // wire, so no handler could ever have called TryTake for this id.
        SessionAttachmentAmbientSend.UnbindFailedToolsCall(Connection, first);

        Assert.Equal(0, registry.CountForTests);

        ArcanumJsonRpcRequest retry = ToolsCall("7", CovenantToolNames.ProposeCovenant);

        _ = SessionAttachmentAmbientSend.ApplyAmbientBinding(Connection, retry);

        Result<CovenantToolCapabilityGrant> taken = registry.TryTake(Connection, "7");

        Assert.True(taken.IsSuccess, taken.IsFailure ? taken.Error.Message : string.Empty);

    }

    [Fact]
    public void A_taken_registration_survives_a_late_unbind_on_the_same_id()
    {

        CovenantToolCapabilityRegistry registry = new();

        using IDisposable staging = CovenantToolStagingAmbient.Push(Staging(registry));

        ArcanumJsonRpcRequest request = ToolsCall("7", CovenantToolNames.ProposeCovenant);

        _ = SessionAttachmentAmbientSend.ApplyAmbientBinding(Connection, request);

        Result<CovenantToolCapabilityGrant> taken = registry.TryTake(Connection, "7");

        Assert.True(taken.IsSuccess, taken.IsFailure ? taken.Error.Message : string.Empty);

        // The send actually succeeded (a handler already took the capability); an unbind reaching
        // here late must not pull the id out from under the handler that is still draining it.
        SessionAttachmentAmbientSend.UnbindFailedToolsCall(Connection, request);

        Assert.Equal(CovenantToolCapabilityState.Taken, taken.Value.Capability.State);

    }

    private static CovenantToolStagingContext Staging(CovenantToolCapabilityRegistry registry)
    {

        CovenantTurnPlan plan = CovenantTask6Fixture.IntegrationPlan();

        return new CovenantToolStagingContext(
            new CovenantMutationCollector(
                Guid.NewGuid(),
                plan.Digest,
                CovenantTask6Fixture.BranchId),
            CovenantCapabilityFixtures.Campaign(),
            CovenantCapabilityFixtures.Admission(plan),
            CovenantCapabilityFixtures.Materialization(),
            new CovenantCapabilityFixtures.StubHeadProbe(),
            true,
            registry,
            CancellationToken.None);

    }

    private static ArcanumJsonRpcRequest ToolsCall(string id, string toolName) =>
        new()
        {
            Method = "tools/call",
            Id = JsonDocument.Parse($"\"{id}\"").RootElement.Clone(),
            Params = JsonDocument
                .Parse($$$"""{"name":"{{{toolName}}}","arguments":{}}""")
                .RootElement.Clone(),
        };

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
            CancellationToken.None);
    }

    private sealed class FakeClock
    {

        private long _ticks = DateTime.UnixEpoch.Ticks;

        public long Read() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan amount) => Interlocked.Add(ref _ticks, amount.Ticks);

    }

}
