using System.Data;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The central connection owner's drain, which every Covenant maintenance path runs before it takes
/// an exclusive lock.
/// </summary>
[Collection(RetroDownfall.Arcanum.Tests.Collections.SqliteConnectionPoolCollection.Name)]
public sealed class CovenantConnectionDrainTests
{

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public async Task A_registered_handle_is_closed_by_the_drain()
    {

        await using CovenantSchemaScratchDatabase database = await CovenantSchemaScratchDatabase.CreateAsync(Token);

        CovenantConnectionDrain drain = new();

        using IDisposable enrolment = drain.Register(database.Connection);

        Assert.Equal(ConnectionState.Open, database.Connection.State);

        Result drained = await drain.DrainAsync(Token);

        Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

        Assert.Equal(ConnectionState.Closed, database.Connection.State);

    }

    [Fact]
    public async Task A_handle_whose_registration_was_disposed_is_left_alone()
    {

        await using CovenantSchemaScratchDatabase database = await CovenantSchemaScratchDatabase.CreateAsync(Token);

        CovenantConnectionDrain drain = new();

        drain.Register(database.Connection).Dispose();

        Result drained = await drain.DrainAsync(Token);

        Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

        // Enrolment is what makes a handle this owner's to close. A drain that closed everything it
        // had ever seen would take down a component that had already finished with its own handle and
        // handed it to somebody else.
        Assert.Equal(ConnectionState.Open, database.Connection.State);

    }

    /// <summary>
    /// A handle two components enrolled stays this owner's to close until the second one releases it.
    /// </summary>
    /// <remarks>
    /// The connection Entity Framework opens is enrolled twice — once at the open itself, and again
    /// by the Covenant connection source in the scopes that ask for one — and the two are released at
    /// different moments. Without a count the first release would cancel the second component's
    /// registration, leaving a handle that is still open and still held outside the drain: the exact
    /// shape an exclusive erasure fails on and cannot name.
    /// </remarks>
    [Fact]
    public async Task A_handle_two_components_enrolled_is_released_only_by_the_last_of_them()
    {

        await using CovenantSchemaScratchDatabase database = await CovenantSchemaScratchDatabase.CreateAsync(Token);

        CovenantConnectionDrain drain = new();

        IDisposable first = drain.Register(database.Connection);

        using IDisposable second = drain.Register(database.Connection);

        first.Dispose();

        Result drained = await drain.DrainAsync(Token);

        Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

        Assert.Equal(ConnectionState.Closed, database.Connection.State);

    }

    [Fact]
    public async Task An_EF_close_releases_only_the_interceptors_reference_counted_enrolment()
    {

        await using CovenantSchemaScratchDatabase database = await CovenantSchemaScratchDatabase.CreateAsync(Token);

        CountingDrain drain = new(new CovenantConnectionDrain());

        GrimoireConnectionAdmissionGate admission = new(TimeProvider.System, drain);

        // A closed handle of its own, not the scratch database's already-open one. Handed an open
        // external connection, EF's open and close never reach the physical handle, so no interceptor
        // callback fires and the enrolment this test is named for never exists at all.
        await using SqliteConnection serving = new(database.Connection.ConnectionString);

        using IDisposable otherHolder = drain.Register(serving);

        DbContextOptions<DrainProbeDbContext> options =
            new DbContextOptionsBuilder<DrainProbeDbContext>()
                .UseSqlite(serving, contextOwnsConnection: false)
                .AddInterceptors(
                    new CovenantConnectionEnrolmentInterceptor(
                        new GrimoireOrdinaryConnectionLifecycle(admission, drain),
                        drain,
                        CovenantSqliteConnectionInitializer.Instance))
                .Options;

        await using DrainProbeDbContext context = new(options);

        await context.Database.OpenConnectionAsync(Token);

        await context.Database.CloseConnectionAsync();

        // Counted, not inferred. The state assertions below hold whether or not the connection was
        // ever enrolled - the other holder's own registration keeps the count at one and the drain
        // closes the handle either way - so this is the only place the enrolment the test is named
        // for is actually observed: two enrolments in, exactly one paid back by the EF close.
        Assert.Equal(2, drain.RegisterCount);

        Assert.Equal(1, drain.ReleaseCount);

        await serving.OpenAsync(Token);

        Result drained = await drain.DrainAsync(Token);

        Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

        // The interceptor paid back only its own enrolment. The other logical holder still owns the
        // physical handle, so the reference-counted drain must retain and close it.
        Assert.Equal(ConnectionState.Closed, serving.State);

    }

    [Fact]
    public async Task Every_registered_handle_is_closed_even_when_one_was_closed_already()
    {

        await using CovenantSchemaScratchDatabase database = await CovenantSchemaScratchDatabase.CreateAsync(Token);

        SqliteConnection second = await database.OpenAdditionalConnectionAsync(Token);

        await using (second)
        {

            CovenantConnectionDrain drain = new();

            using IDisposable first = drain.Register(database.Connection);

            using IDisposable other = drain.Register(second);

            await database.Connection.CloseAsync();

            Result drained = await drain.DrainAsync(Token);

            // A resumed erasure re-enters a drain whose handles a previous pass already closed, so an
            // already-closed handle is the ordinary case rather than a failure.
            Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

            Assert.Equal(ConnectionState.Closed, second.State);

        }

    }

    [Fact]
    public async Task Every_enrolled_handle_is_observed_physically_closed_before_all_pools_clear()
    {

        await using CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(Token);

        await using SqliteConnection pooled = new(
            new SqliteConnectionStringBuilder(database.Connection.ConnectionString)
            {

                Pooling = true,

            }.ToString());

        await pooled.OpenAsync(Token);

        await pooled.CloseAsync();

        await using ObservedCloseConnection first = new(database.Connection.ConnectionString);

        await using ObservedCloseConnection second = new(database.Connection.ConnectionString);

        await first.OpenAsync(Token);

        await second.OpenAsync(Token);

        await database.Connection.CloseAsync();

        CovenantConnectionDrain drain = new();

        using IDisposable firstEnrolment = drain.Register(first);

        using IDisposable secondEnrolment = drain.Register(second);

        Task<Result> draining = Task.Run(() => drain.DrainAsync(Token), Token);

        Task firstEntered = await Task.WhenAny(first.Entered, second.Entered);

        ObservedCloseConnection firstClosing = ReferenceEquals(firstEntered, first.Entered)
            ? first
            : second;

        ObservedCloseConnection secondClosing = ReferenceEquals(firstClosing, first)
            ? second
            : first;

        firstClosing.AllowPhysicalClose();

        await firstClosing.PhysicallyClosed;

        Assert.Contains(
            CovenantResidualArtifactClass.WriteAheadLog,
            CovenantResidualArtifacts.Survivors(database.DatabasePath));

        firstClosing.AllowCloseReturn();

        await secondClosing.Entered;

        secondClosing.AllowPhysicalClose();

        await secondClosing.PhysicallyClosed;

        Assert.Contains(
            CovenantResidualArtifactClass.WriteAheadLog,
            CovenantResidualArtifacts.Survivors(database.DatabasePath));

        secondClosing.AllowCloseReturn();

        Result drained = await draining;

        Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

        Assert.Empty(CovenantResidualArtifacts.Survivors(database.DatabasePath));

    }

    /// <summary>
    /// A pooled handle survives its own disposal, and only the pool clear releases the sidecars.
    /// </summary>
    /// <remarks>
    /// The fact the whole ordering here rests on, written down because it is invisible at every call
    /// site: disposing a pooled connection does not close the database. Its native handle goes back
    /// into the pool with the file still open, so the write-ahead log and the wal-index stay on disk
    /// after the caller believes it has let go — and every proof of absence a Covenant erasure makes
    /// is a statement about exactly those two files. That is why enrolment alone would not be enough
    /// and why the drain clears the pools after it, rather than instead of it.
    ///
    /// <para>It is also the boundary of what the drain can promise. The proof it enables is true at
    /// the instant it is taken and about nothing later: a caller that opens a pooled connection after
    /// this returns puts both files straight back.</para>
    /// </remarks>
    [Fact]
    public async Task A_disposed_pooled_handle_keeps_the_sidecars_until_the_drain_clears_the_pools()
    {

        await using CovenantSchemaScratchDatabase database = await CovenantSchemaScratchDatabase.CreateAsync(Token);

        CovenantConnectionDrain drain = new();

        SqliteConnection pooled = new(
            new SqliteConnectionStringBuilder(database.Connection.ConnectionString)
            {
                Pooling = true,
            }.ToString());

        await using (pooled.ConfigureAwait(false))
        {

            await pooled.OpenAsync(Token);

        }

        // The scratch handle is unpooled and closes for real, so what is left holding the database is
        // the pooled handle this test disposed a statement ago.
        await database.Connection.CloseAsync();

        // Asserted on every platform again. This was briefly guarded to POSIX after it redded on
        // Windows, on the reading that the pool behaves differently there -- but the class is also
        // order-dependent, and the same assertion fails on macOS under a wide filter and passes when
        // the class runs alone, because ClearAllPools() is process-global and another test's drain
        // empties the pool this one is measuring. The class is serialized now, which removes that
        // interference. If Windows still reds here, the platform difference is real and this guard
        // earns its place; guarding first would have hidden the race behind a plausible story.
        Assert.Contains(
            CovenantResidualArtifactClass.WriteAheadLog,
            CovenantResidualArtifacts.Survivors(database.DatabasePath));

        Result drained = await drain.DrainAsync(Token);

        Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

        Assert.Empty(CovenantResidualArtifacts.Survivors(database.DatabasePath));

    }

    [Fact]
    public async Task Exact_pool_clear_releases_one_closed_pooled_handle_and_observes_closure()
    {

        await using CovenantSchemaScratchDatabase database = await CovenantSchemaScratchDatabase.CreateAsync(Token);

        CovenantConnectionDrain drain = new();

        await using SqliteConnection pooled = new(
            new SqliteConnectionStringBuilder(database.Connection.ConnectionString)
            {

                Pooling = true,

            }.ToString());

        await pooled.OpenAsync(Token);

        await pooled.CloseAsync();

        await database.Connection.CloseAsync();

        Assert.Equal(ConnectionState.Closed, pooled.State);

        Assert.Contains(
            CovenantResidualArtifactClass.WriteAheadLog,
            CovenantResidualArtifacts.Survivors(database.DatabasePath));

        Result cleared = drain.ClearExactPoolAfterClose(pooled);

        Assert.True(cleared.IsSuccess, cleared.IsFailure ? cleared.Error.Message : null);

        Assert.Equal(ConnectionState.Closed, pooled.State);

        Assert.Empty(CovenantResidualArtifacts.Survivors(database.DatabasePath));

    }

    /// <summary>
    /// A scope that opens the Grimoire through the Covenant connection source is closed by the drain.
    /// </summary>
    /// <remarks>
    /// <c>CovenantConnectionSource</c> opens the scope's connection and never closes it, so it stays
    /// open for the life of the scope rather than for the life of a statement. That is the shape the
    /// drain exists for, and it is the shape the erasure cannot survive: a second handle holding this
    /// database open costs the exclusive maintenance connection one busy timeout per wal-index lock it
    /// has to take, which is tens of seconds of waiting followed by <c>database is locked</c>.
    ///
    /// <para>Driven through the real host rather than a scratch database because enrolment is a
    /// composition property, not a connection property: what decides whether a held handle is drained
    /// is which services the scope happens to resolve, and only the production registrations say
    /// that.</para>
    /// </remarks>
    [SkippableFact]
    public async Task A_scope_holding_the_Grimoire_open_for_Covenant_is_closed_by_the_drain()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new()
        {

            SettingsOverride = static settings => settings with
            {

                Features = settings.Features with { Covenant = true },

            },

        };

        // The maintenance sweeps resolve exactly this and nothing that enrols a handle, so the scope
        // below is the one the sweep driver builds, minus the sweeps. Reading Services is what starts
        // the host, so the registrations under test are the ones the server composes.
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        SqliteConnection held = await scope.ServiceProvider
            .GetRequiredService<ICovenantConnectionSource>()
            .GetOpenCoreConnectionAsync(Token);

        Assert.Equal(ConnectionState.Open, held.State);

        Result drained = await factory.Services
            .GetRequiredService<ICovenantConnectionDrain>()
            .DrainAsync(Token);

        Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

        Assert.Equal(ConnectionState.Closed, held.State);

    }

    /// <summary>
    /// A handle a live component reopens after the drain has closed it does not fail the drain.
    /// </summary>
    /// <remarks>
    /// The condition enrolling every Entity Framework connection introduced. Before that, the
    /// enrolled set held handles a component opened once and held, and nothing reopened them; it now
    /// holds the connections a running host opens and closes constantly, and a background
    /// reconciliation pass or a maintenance sweep can legitimately reopen one between its close here
    /// and the pass that reads it back.
    ///
    /// <para>The reopen is driven rather than waited for. Each handle blocks in its own close until
    /// this test releases it, so the reopen lands inside the drain's window on every run and on every
    /// platform, where waiting for a background pass to collide with a drain reproduces on a slow
    /// machine and never on a fast one. Which handle the drain reaches first is not this test's to
    /// decide, so it takes them in whichever order they arrive.</para>
    ///
    /// <para>What makes this the right answer rather than a weakened one is that the same reopen a
    /// microsecond after <c>DrainAsync</c> returns is unpreventable and already the exclusive
    /// acquisition's to meet. A refusal here would buy the erasure nothing and cost it a verdict that
    /// depends on when a background pass ran.</para>
    /// </remarks>
    [Fact]
    public async Task A_handle_reopened_after_its_close_does_not_fail_the_drain()
    {

        await using CovenantSchemaScratchDatabase database = await CovenantSchemaScratchDatabase.CreateAsync(Token);

        await using GatedConnection first = new(database.Connection.ConnectionString);

        await using GatedConnection second = new(database.Connection.ConnectionString);

        await first.OpenAsync(Token);

        await second.OpenAsync(Token);

        CovenantConnectionDrain drain = new();

        using IDisposable firstEnrolment = drain.Register(first);

        using IDisposable secondEnrolment = drain.Register(second);

        Task<Result> draining = Task.Run(() => drain.DrainAsync(Token), Token);

        Task entered = await Task.WhenAny(first.Entered, second.Entered);

        GatedConnection reopened = ReferenceEquals(entered, first.Entered) ? first : second;

        GatedConnection holding = ReferenceEquals(reopened, first) ? second : first;

        reopened.ReleaseClose();

        // The drain has read the first handle back and moved on, so the reopen below is a reopen
        // rather than a close that never landed.
        await holding.Entered;

        Assert.Equal(ConnectionState.Closed, reopened.State);

        await reopened.OpenAsync(Token);

        holding.ReleaseClose();

        Result drained = await draining;

        Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

        Assert.Equal(ConnectionState.Open, reopened.State);

        Assert.Equal(ConnectionState.Closed, holding.State);

    }

    /// <summary>
    /// A handle that does not close is still refused, whatever else the drain lets through.
    /// </summary>
    /// <remarks>
    /// The other side of the distinction above, and the guarantee an erasure rests on. A handle
    /// nothing can close holds the database through the exclusive lock that follows, which costs the
    /// maintenance connection one busy timeout per wal-index lock and ends in <c>database is
    /// locked</c> with no way for that caller to name the holder. The drain reports it here, where
    /// the holder is still known.
    /// </remarks>
    [Fact]
    public async Task A_handle_that_does_not_close_is_still_refused_by_the_drain()
    {

        await using CovenantSchemaScratchDatabase database = await CovenantSchemaScratchDatabase.CreateAsync(Token);

        UnclosableConnection stuck = new(database.Connection.ConnectionString);

        try
        {

            await stuck.OpenAsync(Token);

            CovenantConnectionDrain drain = new();

            using IDisposable enrolment = drain.Register(stuck);

            Result drained = await drain.DrainAsync(Token);

            Assert.True(drained.IsFailure);

            Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, drained.Error.Code);

            Assert.Contains("did not close", drained.Error.Message);

        }
        finally
        {

            stuck.ForceClose();

            await stuck.DisposeAsync();

        }

    }

    [Fact]
    public async Task A_drain_with_nothing_registered_still_clears_the_pools()
    {

        CovenantConnectionDrain drain = new();

        Result drained = await drain.DrainAsync(Token);

        // The idle pools belong to no component, so there is never nothing to do. A drain that
        // short-circuited on an empty enrolment set would leave every pooled handle behind.
        Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

    }

    [Fact]
    public void No_production_file_outside_the_drain_clears_a_connection_pool()
    {

        List<string> offenders =
        [
            .. ProductionSourceInventory.Sources()
                .Where(static source => !source.Is("CovenantConnectionDrain.cs"))
                .Where(static source => !source.Is("SqliteNativeRuntimeValidator.cs"))
                .Where(static source => source.Names("ClearAllPools") || source.Names("ClearPool("))
                .Select(static source => source.RelativePath),
        ];

        // One owner, one order. Clearing the pools is only half a drain, and the half that is easy to
        // remember: a call site that open-coded it would empty the idle pools while the handle
        // actually holding the database open stayed exactly where it was. The runtime validator is
        // exempt because it clears pools to release a rejected native library, not to free a database.
        Assert.Empty(offenders);

    }

    /// <summary>
    /// Every production file that composes a connection string or constructs a connection either
    /// turns pooling off or is named here with a reason.
    /// </summary>
    /// <remarks>
    /// A pooled handle a component opened for itself is the one kind this owner cannot close.
    /// Enrolment covers the handles components hand it, and the pool clear covers the ones they have
    /// let go of — but disposal does not close a pooled connection, so a component that has already
    /// returned is still holding the database with nobody left to ask. That is not an abstract
    /// hazard: it is what a Covenant erasure's proof of absence reads as a live handle and refuses
    /// on, and the write-ahead log and wal-index it refuses over survive until somebody clears a pool
    /// they never opened.
    ///
    /// <para>The string is inventoried alongside the connection because the string is what decides
    /// pooling; a component that composes one and hands it to EF has made the same choice as one that
    /// opens a handle itself. File-level rather than site-level, like the pool-clear inventory above
    /// it, so a file that already turns pooling off for one connection can add a second without this
    /// noticing. The rule it does enforce is the one that matters for a new component: an author
    /// reaching for SQLite in a file that never has before either says <c>Pooling = false</c> or
    /// comes here and says why not.</para>
    /// </remarks>
    [Fact]
    public void Every_production_opener_is_unpooled_or_named_here()
    {

        List<string> offenders =
        [
            .. ProductionSourceInventory.Sources()
                .Where(static source => source.Names("new SqliteConnection(")
                    || source.Names("SqliteConnection connection = new(")
                    || source.Names("new SqliteConnectionStringBuilder"))
                .Where(static source => !source.Names("Pooling = false"))
                .Where(static source => !PooledByDesign(source))
                .Select(static source => source.RelativePath),
        ];

        Assert.Empty(offenders);

    }

    /// <summary>
    /// The three that are deliberately left pooled, and what makes each of them safe.
    /// </summary>
    private static bool PooledByDesign(ProductionSource source) =>

        // The one connection string EF composes for the workload. Its handle is the drain's ordinary
        // case: enrolled while a scope holds it open, released by the pool clear once it is idle. It
        // is also the path pooling exists for — EF opens and closes per operation, and unpooled that
        // would repeat SQLCipher key derivation on every read the product performs.
        source.Is("ArcanumDbContextOptionsConfigurator.cs")

        // The design-time factory. It runs under `dotnet ef` against a scratch file in the temp root
        // and is never constructed by the shipped host, so no drain will ever be asked about it.
        || source.Is("ArcanumDbContextFactory.cs")

        // Two per-turn readbacks over the workload's own connection string. Their handles are idle in
        // a pool by the time any maintenance runs, so the drain's pool clear releases them, and an
        // overlap with a live proof is what the absence proof's bounded retry covers. Unpooling them
        // would repeat key derivation on the per-turn path, which is a latency change to measure
        // rather than to assume.
        || source.Is("SessionEntryPersistence.cs");

    /// <summary>
    /// Counts enrolments and their releases while the real drain does all of the work.
    /// </summary>
    /// <remarks>
    /// A decorator rather than a stand-in, because the surrounding test still needs the drain to
    /// close the handle for real; what it could not see before is how many enrolments existed and
    /// how many were paid back.
    /// </remarks>
    private sealed class CountingDrain(ICovenantConnectionDrain inner) : ICovenantConnectionDrain
    {

        internal int RegisterCount { get; private set; }

        internal int ReleaseCount { get; private set; }

        public IDisposable Register(SqliteConnection connection)
        {

            RegisterCount++;

            return new CountedEnrolment(this, inner.Register(connection));

        }

        public IDisposable Register(SqliteConnection connection, Action afterPhysicalClose)
        {

            RegisterCount++;

            return new CountedEnrolment(this, inner.Register(connection, afterPhysicalClose));

        }

        public Result ClearExactPoolAfterClose(SqliteConnection connection) =>
            inner.ClearExactPoolAfterClose(connection);

        public Task<Result> DrainAsync(CancellationToken cancellationToken) =>
            inner.DrainAsync(cancellationToken);

        private sealed class CountedEnrolment(CountingDrain owner, IDisposable enrolment) : IDisposable
        {

            private int _released;

            public void Dispose()
            {

                if (Interlocked.Exchange(ref _released, 1) == 0)
                {

                    owner.ReleaseCount++;

                }

                enrolment.Dispose();

            }

        }

    }

    /// <summary>
    /// A real handle to the scratch database whose close this test holds open until it says so.
    /// </summary>
    /// <remarks>
    /// A real connection rather than a stand-in for one, because what is under test is the state the
    /// drain reads back off a handle it has just closed. Only the asynchronous close is gated: the
    /// drain closes through that one, while disposal closes through the synchronous one and must not
    /// block on a release nobody is left to give.
    /// </remarks>
    private sealed class GatedConnection(string connectionString) : SqliteConnection(connectionString)
    {

        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the drain has entered this handle's close.</summary>
        internal Task Entered => _entered.Task;

        /// <summary>Lets the close this handle is holding run through.</summary>
        internal void ReleaseClose() =>
            _released.TrySetResult();

        public override async Task CloseAsync()
        {

            _ = _entered.TrySetResult();

            await _released.Task;

            await base.CloseAsync();

        }

    }

    private sealed class ObservedCloseConnection(string connectionString)
        : SqliteConnection(connectionString)
    {

        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _allowPhysicalClose =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _physicallyClosed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _allowCloseReturn =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => _entered.Task;

        internal Task PhysicallyClosed => _physicallyClosed.Task;

        internal void AllowPhysicalClose() => _allowPhysicalClose.TrySetResult();

        internal void AllowCloseReturn() => _allowCloseReturn.TrySetResult();

        public override async Task CloseAsync()
        {

            _entered.TrySetResult();

            await _allowPhysicalClose.Task;

            await base.CloseAsync();

            _physicallyClosed.TrySetResult();

            await _allowCloseReturn.Task;

        }

    }

    private sealed class DrainProbeDbContext(DbContextOptions<DrainProbeDbContext> options)
        : DbContext(options)
    {
    }

    /// <summary>
    /// A handle that will not close, which is the shape an exclusive erasure cannot survive.
    /// </summary>
    /// <remarks>
    /// Both closes are overridden. The base type's asynchronous close is the synchronous one, so a
    /// stand-in that refused only one of them would still close through the other and prove nothing.
    /// </remarks>
    private sealed class UnclosableConnection(string connectionString) : SqliteConnection(connectionString)
    {

        public override void Close()
        {
        }

        public override Task CloseAsync() =>
            Task.CompletedTask;

        /// <summary>Closes it for real, so a test that made its point still releases the file.</summary>
        internal void ForceClose() =>
            base.Close();

    }

}
