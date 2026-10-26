using Microsoft.Extensions.Logging.Abstractions;

using Microsoft.AspNetCore.Http;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence.MemoryRecall;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class MemoryRecallResponseLifetimeTests
{
    [Fact]
    public void Missing_authority_is_rejected_before_a_response_can_bind()
    {
        Assert.Throws<ArgumentNullException>("authority", () => new MemoryRecallResponseLifetime(null!));
    }

    [Fact]
    public async Task Original_turn_lease_stays_owned_until_the_complete_response_write_finishes()
    {
        var invocation = InvocationContexts.AttendedGlobalOnlySession();

        var registration = new Registration();

        var turn = Turn(registration);

        await using var lifetime = new MemoryRecallResponseLifetime(new Authority(invocation.ReadAuthorityEpoch!));

        lifetime.Bind(invocation);

        Assert.True(lifetime.TryRetain(invocation, turn));

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var context = new DefaultHttpContext();

        context.Response.Body = new MemoryStream();

        Task writing = lifetime.Wrap(new CallbackResult(async http =>
        {
            entered.SetResult();

            await finish.Task;

            await http.Response.Body.WriteAsync("protected-result"u8.ToArray());
        })).ExecuteAsync(context);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, registration.Releases);

        finish.SetResult();

        await writing;

        Assert.Equal(1, registration.Releases);

        Assert.Equal("no-store, private", context.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task Stale_original_epoch_refuses_before_inner_response_or_protected_bytes()
    {
        var invocation = InvocationContexts.AttendedGlobalOnlySession();

        var authority = new Authority(invocation.ReadAuthorityEpoch!);

        var registration = new Registration();

        await using var lifetime = new MemoryRecallResponseLifetime(authority);

        lifetime.Bind(invocation);

        Assert.True(lifetime.TryRetain(invocation, Turn(registration)));

        authority.Current = authority.Current! with { AuthorityEpoch = authority.Current.AuthorityEpoch + 1 };

        var context = new DefaultHttpContext();

        context.Response.Body = new MemoryStream();

        bool invoked = false;

        await lifetime.Wrap(new CallbackResult(_ =>
        {
            invoked = true;

            return Task.CompletedTask;
        })).ExecuteAsync(context);

        Assert.False(invoked);

        Assert.Equal(1, registration.Releases);

        Assert.Equal("no-store, private", context.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task Another_invocation_cannot_transfer_a_scope_and_abandoned_response_releases_once()
    {
        var invocation = InvocationContexts.AttendedGlobalOnlySession();

        var registration = new Registration();

        var turn = Turn(registration);

        var lifetime = new MemoryRecallResponseLifetime(new Authority(invocation.ReadAuthorityEpoch!));

        lifetime.Bind(invocation);

        Assert.False(lifetime.TryRetain(InvocationContexts.AttendedGlobalOnlySession(), turn));

        Assert.True(lifetime.TryRetain(invocation, turn));

        await lifetime.DisposeAsync();

        await lifetime.DisposeAsync();

        Assert.Equal(1, registration.Releases);
    }

    [Fact]
    public async Task Abandoned_response_cannot_write_after_its_original_lease_was_released()
    {
        var invocation = InvocationContexts.AttendedGlobalOnlySession();

        var registration = new Registration();

        var lifetime = new MemoryRecallResponseLifetime(new Authority(invocation.ReadAuthorityEpoch!));

        lifetime.Bind(invocation);

        Assert.True(lifetime.TryRetain(invocation, Turn(registration)));

        await lifetime.DisposeAsync();

        var context = new DefaultHttpContext();

        context.Response.Body = new MemoryStream();

        bool invoked = false;

        await lifetime.Wrap(new CallbackResult(_ =>
        {
            invoked = true;

            return Task.CompletedTask;
        })).ExecuteAsync(context);

        Assert.False(invoked);

        Assert.Equal(1, registration.Releases);
    }

    private static CovenantTurnScope Turn(Registration registration)
    {
        var plan = new CovenantLinker().Link(CovenantTask6Fixture.Snapshot(null,
            CovenantTask6Fixture.GlobalConfirmed("recall.key", CovenantTask6Fixture.G1, CovenantTask6Fixture.G2, 1, 1))).Value;

        return new CovenantTurnScope(CovenantTurnContext.ForPlan(plan, new CovenantTurnLease(registration), null, Guid.NewGuid()),
            Guid.NewGuid(), null, false, NullLogger.Instance);
    }

    private sealed class CallbackResult(Func<HttpContext, Task> execute) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) => execute(httpContext);
    }

    private sealed class Authority(CovenantReadAuthorityEpoch epoch) : ICovenantAuthoritySnapshotProvider
    {
        public CovenantAuthoritySnapshot? Current { get; set; } = new(epoch.RuntimeAuthorityGeneration,
            epoch.InstallationIdentity, epoch.AuthorityEpoch, 1, 1, CovenantHostToolsState.Clean, null);
    }

    private sealed class Registration : ICovenantLeaseRegistration
    {
        internal int Releases;

        public CovenantOperationLeaseSnapshot Snapshot { get; } = new(Guid.NewGuid(), 1, CovenantLeaseKind.Turn,
            CovenantLeaseCoverage.Scoped, CovenantOperationScope.Global, CovenantTask6Fixture.DatasetGeneration,
            1, 11, 0, 1, null, null, null, null, false);

        public CancellationToken Revocation => CancellationToken.None;

        public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

        public ValueTask ReleaseAsync()
        {
            Releases++;

            return ValueTask.CompletedTask;
        }
    }
}
