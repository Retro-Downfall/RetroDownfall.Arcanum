using System.Data;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The central connection owner's drain, which every Covenant maintenance path runs before it takes
/// an exclusive lock.
/// </summary>
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

        Assert.Contains(
            CovenantResidualArtifactClass.WriteAheadLog,
            CovenantResidualArtifacts.Survivors(database.DatabasePath));

        Result drained = await drain.DrainAsync(Token);

        Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

        Assert.Empty(CovenantResidualArtifacts.Survivors(database.DatabasePath));

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

}
