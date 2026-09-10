using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>
/// The scoped carrier that holds one request's admission for the whole of that request's scope.
/// </summary>
/// <remarks>
/// Its two hard properties are both absences. It takes nothing until a request asks, because a
/// holder that acquired in its constructor would mint an HTTP request lease from every background
/// child scope that happened to resolve it; and it releases only on disposal, because the request
/// scope is disposed after the pooled context has gone back and after every response-completed
/// writer has run.
/// </remarks>
public sealed class GrimoireRequestAdmissionScopeTests
{

    private static readonly TimeSpan OpeningTimeout = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task A_new_scope_has_taken_nothing_until_a_request_asks()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        await using GrimoireRequestAdmissionScope scope = new(gate);

        Assert.Null(scope.Lease);

        await using IGrimoireClosingOwner closing = Begin(gate);

        Result drained = await gate.DrainRequestAndWorkAsync(closing, CancellationToken.None);

        Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

    }

    [Fact]
    public async Task An_ordinary_gate_admits_the_request_and_the_scope_keeps_its_lease()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        await using GrimoireRequestAdmissionScope scope = new(gate);

        Assert.True(scope.TryAdmit(GrimoireRequestKind.Finite));

        Assert.NotNull(scope.Lease);

        Assert.Equal(GrimoireRequestKind.Finite, scope.Lease.Kind);

        Assert.Equal(gate.CurrentGeneration, scope.Lease.Generation);

    }

    [Fact]
    public async Task A_second_admission_on_the_same_scope_reuses_the_first_lease()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        await using GrimoireRequestAdmissionScope scope = new(gate);

        Assert.True(scope.TryAdmit(GrimoireRequestKind.Finite));

        IGrimoireRequestLease? first = scope.Lease;

        Assert.True(scope.TryAdmit(GrimoireRequestKind.Finite));

        Assert.Same(first, scope.Lease);

    }

    [Fact]
    public async Task A_closing_gate_refuses_the_request_and_the_scope_keeps_nothing()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        await using IGrimoireClosingOwner closing = Begin(gate);

        await using GrimoireRequestAdmissionScope scope = new(gate);

        Assert.False(scope.TryAdmit(GrimoireRequestKind.Finite));

        Assert.Null(scope.Lease);

    }

    /// <summary>
    /// The drain finishes only once the scope is disposed, never once the request returns.
    /// </summary>
    [Fact]
    public async Task A_drain_waits_for_the_scope_and_finishes_when_it_is_disposed()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        GrimoireRequestAdmissionScope scope = new(gate);

        Assert.True(scope.TryAdmit(GrimoireRequestKind.Finite));

        await using IGrimoireClosingOwner closing = Begin(gate);

        Task<Result> drain = gate
            .DrainRequestAndWorkAsync(closing, CancellationToken.None)
            .AsTask();

        Assert.False(drain.IsCompleted);

        await scope.DisposeAsync();

        Result drained = await drain;

        Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

    }

    [Fact]
    public async Task Disposal_releases_exactly_once_and_a_later_request_is_still_admitted()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        GrimoireRequestAdmissionScope first = new(gate);

        Assert.True(first.TryAdmit(GrimoireRequestKind.Finite));

        await first.DisposeAsync();

        await first.DisposeAsync();

        await using GrimoireRequestAdmissionScope second = new(gate);

        Assert.True(second.TryAdmit(GrimoireRequestKind.Finite));

        await second.DisposeAsync();

        await using IGrimoireClosingOwner closing = Begin(gate);

        Result drained = await gate.DrainRequestAndWorkAsync(closing, CancellationToken.None);

        Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

    }

    [Fact]
    public async Task A_scope_that_never_asked_disposes_without_touching_the_gate()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        GrimoireRequestAdmissionScope scope = new(gate);

        await scope.DisposeAsync();

        await using GrimoireRequestAdmissionScope later = new(gate);

        Assert.True(later.TryAdmit(GrimoireRequestKind.Finite));

    }

    /// <summary>
    /// A scope disposed before it was admitted cannot then admit.
    /// </summary>
    /// <remarks>
    /// The order is impossible in the request pipeline and possible in a fault: a scope disposed by
    /// an unwinding container, then reached by a callback that outlived it. Admitting there would
    /// take a lease nothing will ever release, and stage one would wait out its whole checkpoint on
    /// a request that no longer exists.
    /// </remarks>
    [Fact]
    public async Task A_disposed_scope_admits_nothing()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        GrimoireRequestAdmissionScope scope = new(gate);

        await scope.DisposeAsync();

        Assert.False(scope.TryAdmit(GrimoireRequestKind.Finite));

        Assert.Null(scope.Lease);

    }

    /// <summary>
    /// A synchronously disposed container scope releases the lease rather than throwing.
    /// </summary>
    /// <remarks>
    /// The request scope is disposed asynchronously, so this path is not the one production takes. It
    /// exists because a service that implements only <c>IAsyncDisposable</c> makes a synchronous scope
    /// disposal throw outright, and eleven synchronous scopes exist under <c>src</c> today — any of
    /// which a later change could give a transitive dependency on this holder.
    /// </remarks>
    [Fact]
    public async Task A_synchronously_disposed_scope_still_releases_the_lease()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        using (GrimoireRequestAdmissionScope scope = new(gate))
        {

            Assert.True(scope.TryAdmit(GrimoireRequestKind.Finite));

        }

        await using IGrimoireClosingOwner closing = Begin(gate);

        Result drained = await gate.DrainRequestAndWorkAsync(closing, CancellationToken.None);

        Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

    }

    private static GrimoireConnectionAdmissionGate CreateGate() =>
        new(
            TimeProvider.System,
            new NoOpConnectionDrain(),
            OpeningTimeout);

    private static IGrimoireClosingOwner Begin(GrimoireConnectionAdmissionGate gate)
    {

        Result<IGrimoireClosingOwner> begun = gate.BeginOrResumeExclusive(Owner);

        Assert.True(begun.IsSuccess, begun.IsFailure ? begun.Error.Message : null);

        return begun.Value;

    }

    private static CovenantExclusiveRecoveryOwner Owner =>
        new(
            Guid.Parse("00000000-0000-0000-0000-000000000251"),
            CovenantExclusiveOperation.CovenantReset,
            new CovenantDigest([.. Enumerable.Repeat<byte>(251, 32)]));

    private sealed class NoOpConnectionDrain : ICovenantConnectionDrain
    {

        public IDisposable Register(SqliteConnection connection) => new Registration();

        public Result ClearExactPoolAfterClose(SqliteConnection connection) => Result.Success();

        public Task<Result> DrainAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        private sealed class Registration : IDisposable
        {

            public void Dispose()
            {
            }

        }

    }

}
