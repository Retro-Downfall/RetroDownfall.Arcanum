using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Api.Streaming;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Tests.Api.Streaming;

/// <summary>
/// The one rule that keeps a quiesced stream from ending mid-frame: which token carries revocation.
/// </summary>
/// <remarks>
/// Every one of the five routes hands a single token to both its producer and its frame writer today.
/// Linking maintenance revocation into that token would cancel the body write and flush inside the
/// frame writer, leaving a <c>data:</c> line with no terminating blank line on the wire — a protocol
/// error rather than a short stream, and the exact partial frame this child exists to prevent. The
/// split is asserted here rather than left to each route's own tests because it is one rule with five
/// call sites, and a rule proved only at its call sites is one a sixth call site can miss.
/// </remarks>
public sealed class GrimoireStreamQuiescenceTests
{

    private static readonly TimeSpan OpeningTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A request with no admission holder never quiesces, the way every stage answers an absent service.
    /// </summary>
    /// <remarks>
    /// A host that maps these routes without the Arcanum infrastructure stack has no Grimoire, so
    /// there is no lease to revoke and nothing for quiescence to protect. Answering with a token that
    /// can never fire is what lets the writer take the same path in both hosts instead of branching.
    /// </remarks>
    [Fact]
    public void A_request_with_no_admission_holder_never_quiesces()
    {

        DefaultHttpContext context = new()
        {
            RequestServices = new ServiceCollection().BuildServiceProvider(),
        };

        GrimoireStreamQuiescence quiescence = GrimoireStreamQuiescence.For(context);

        Assert.False(quiescence.IsQuiescing);

        Assert.False(quiescence.Revocation.CanBeCanceled);

    }

    /// <summary>
    /// A quiesceable lease publishes the gate's own revocation token.
    /// </summary>
    [Fact]
    public void A_quiesceable_lease_publishes_the_gates_revocation()
    {

        using GateFixture fixture = new();

        GrimoireStreamQuiescence quiescence = fixture.Admit(GrimoireRequestKind.QuiesceableStream);

        Assert.True(quiescence.Revocation.CanBeCanceled);

        Assert.False(quiescence.IsQuiescing);

        fixture.BeginClosing();

        Assert.True(quiescence.IsQuiescing);

    }

    /// <summary>
    /// A finite lease reports a token that stays unsignalled even once a transition has begun.
    /// </summary>
    /// <remarks>
    /// The gate already declines to revoke a finite lease, so this is belt and braces — but it is the
    /// belt that matters. A billable stream cut halfway would bill for an answer nobody received, and
    /// the parent design forbids retrying provider work because maintenance began. Reading the kind
    /// here rather than trusting the gate's selection means a future change to that selection cannot
    /// silently start cutting the streams that must never be cut.
    /// </remarks>
    [Fact]
    public void A_finite_lease_never_reports_quiescence()
    {

        using GateFixture fixture = new();

        GrimoireStreamQuiescence quiescence = fixture.Admit(GrimoireRequestKind.Finite);

        fixture.BeginClosing();

        Assert.False(quiescence.IsQuiescing);

        Assert.False(quiescence.Revocation.CanBeCanceled);

    }

    /// <summary>
    /// The linked producer source is cancelled by the caller's own token and by revocation.
    /// </summary>
    [Fact]
    public void The_producer_link_answers_to_both_the_caller_and_maintenance()
    {

        using GateFixture fixture = new();

        GrimoireStreamQuiescence quiescence = fixture.Admit(GrimoireRequestKind.QuiesceableStream);

        using CancellationTokenSource caller = new();

        using CancellationTokenSource producer = quiescence.LinkProducer(caller.Token);

        Assert.False(producer.IsCancellationRequested);

        caller.Cancel();

        Assert.True(producer.IsCancellationRequested);

        using CancellationTokenSource second = new();

        using CancellationTokenSource viaMaintenance = quiescence.LinkProducer(second.Token);

        fixture.BeginClosing();

        Assert.True(viaMaintenance.IsCancellationRequested);

    }

    /// <summary>
    /// Revocation reaches a producer link and never the token a frame is written on.
    /// </summary>
    /// <remarks>
    /// The negative half is the load-bearing one. A frame token that answered to revocation would
    /// abort <c>Response.Body.WriteAsync</c> between the <c>data:</c> prefix and the terminating blank
    /// line, which no client can parse and no later frame can repair.
    /// </remarks>
    [Fact]
    public void Revocation_reaches_the_producer_and_never_the_frame_writer()
    {

        using GateFixture fixture = new();

        GrimoireStreamQuiescence quiescence = fixture.Admit(GrimoireRequestKind.QuiesceableStream);

        using CancellationTokenSource request = new();

        using CancellationTokenSource producer = quiescence.LinkProducer(request.Token);

        CancellationToken frame = request.Token;

        fixture.BeginClosing();

        Assert.True(producer.IsCancellationRequested);

        Assert.False(frame.IsCancellationRequested);

    }

    /// <summary>
    /// A real gate, a real lease, and a real closing transition — no test double for the rule under test.
    /// </summary>
    private sealed class GateFixture : IDisposable
    {

        private readonly GrimoireConnectionAdmissionGate _gate =
            new(TimeProvider.System, new NoOpConnectionDrain(), OpeningTimeout);

        private readonly ServiceProvider _root;

        private IServiceScope? _scope;

        private IGrimoireClosingOwner? _closing;

        internal GateFixture()
        {

            ServiceCollection services = new();

            services.AddSingleton<IGrimoireConnectionAdmissionGate>(_gate);

            services.AddScoped(
                static sp => new GrimoireRequestAdmissionScope(
                    sp.GetRequiredService<IGrimoireConnectionAdmissionGate>()));

            _root = services.BuildServiceProvider();

        }

        internal GrimoireStreamQuiescence Admit(GrimoireRequestKind kind)
        {

            _scope = _root.CreateScope();

            GrimoireRequestAdmissionScope admission =
                _scope.ServiceProvider.GetRequiredService<GrimoireRequestAdmissionScope>();

            Assert.True(admission.TryAdmit(kind));

            DefaultHttpContext context = new()
            {
                RequestServices = _scope.ServiceProvider,
            };

            return GrimoireStreamQuiescence.For(context);

        }

        internal void BeginClosing()
        {

            Result<IGrimoireClosingOwner> begun = _gate.BeginOrResumeExclusive(
                new CovenantExclusiveRecoveryOwner(
                    Guid.Parse("00000000-0000-0000-0000-000000000252"),
                    CovenantExclusiveOperation.CovenantReset,
                    new CovenantDigest([.. Enumerable.Repeat<byte>(252, 32)])));

            Assert.True(begun.IsSuccess, begun.IsFailure ? begun.Error.Message : null);

            _closing = begun.Value;

        }

        public void Dispose()
        {

            _closing?.DisposeAsync().AsTask().GetAwaiter().GetResult();

            _scope?.Dispose();

            _root.Dispose();

        }

    }

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
