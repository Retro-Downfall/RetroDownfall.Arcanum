using System.Diagnostics;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Memory;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Memory;

/// <summary>
/// The post-commit WAL scrub: one checked truncating checkpoint on its own unpooled read-write
/// connection, with a short wait so a busy log is reported rather than waited out.
/// </summary>
[Collection("Grimoire")]
public sealed class MemoryErasureScrubberTests(GrimoireFixture fixture) : IAsyncLifetime
{
    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    private static CancellationToken Token => CancellationToken.None;

    private SqliteConnection Connection => (SqliteConnection)_db!.Database.GetDbConnection();

    public Task InitializeAsync()
    {
        _dbPath = fixture.CopyDatabase();

        _db = fixture.CreateContext(_dbPath);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }

        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [SkippableFact]
    public async Task Checkpoint_truncates_a_quiet_database_on_an_unpooled_read_write_connection()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FixtureOrdinaryConnectionFactory factory = FixtureOrdinaryConnectionFactory.For(_db!);

        await WriteAsync("CREATE TABLE scrub_probe (Value INTEGER); INSERT INTO scrub_probe VALUES (1);");

        MemoryErasureWalCheckpointAttempt attempt = await new MemoryErasureScrubber(factory).CheckpointAsync(Token);

        Assert.Equal(MemoryErasureWalCheckpointAttempt.Truncated, attempt);

        Assert.Equal([GrimoireOrdinaryFreshConnectionKind.ReadWrite], factory.Kinds);

        Assert.Equal(0, factory.LiveFreshLeaseCount);
    }

    /// <summary>
    /// A reader holding a snapshot older than the log's tail keeps the checkpoint from resetting the
    /// log. The connection's ordinary wait is five seconds; the scrubber's is a quarter of one, so the
    /// answer is Busy well inside three.
    /// </summary>
    [SkippableFact]
    public async Task Checkpoint_reports_busy_within_the_short_wait_while_a_reader_holds_an_older_snapshot()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FixtureOrdinaryConnectionFactory factory = FixtureOrdinaryConnectionFactory.For(_db!);

        await WriteAsync("CREATE TABLE scrub_probe (Value INTEGER);");

        Result<IGrimoireOrdinaryConnectionLease> reader = await factory.OpenFreshAsync(
            GrimoireOrdinaryFreshConnectionKind.ReadOnly,
            Token);

        Assert.True(reader.IsSuccess, reader.IsFailure ? reader.Error.Message : string.Empty);

        await using IGrimoireOrdinaryConnectionLease lease = reader.Value;

        await using (SqliteCommand snapshot = lease.Connection.CreateCommand())
        {
            snapshot.CommandText = "BEGIN; SELECT count(*) FROM grimoire_feature_schemas;";

            _ = await snapshot.ExecuteScalarAsync(Token);
        }

        await WriteAsync("INSERT INTO scrub_probe VALUES (1);");

        Stopwatch stopwatch = Stopwatch.StartNew();

        MemoryErasureWalCheckpointAttempt attempt = await new MemoryErasureScrubber(factory).CheckpointAsync(Token);

        stopwatch.Stop();

        Assert.Equal(MemoryErasureWalCheckpointAttempt.Busy, attempt);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"The checkpoint waited {stopwatch.Elapsed}.");

        await using (SqliteCommand end = lease.Connection.CreateCommand())
        {
            end.CommandText = "COMMIT;";

            _ = await end.ExecuteNonQueryAsync(Token);
        }

        // With the old snapshot released, the same scrub truncates.
        Assert.Equal(MemoryErasureWalCheckpointAttempt.Truncated, await new MemoryErasureScrubber(factory).CheckpointAsync(Token));
    }

    [Fact]
    public async Task Checkpoint_reports_unavailable_when_the_fresh_open_is_refused()
    {
        RefusingFactory factory = new();

        MemoryErasureWalCheckpointAttempt attempt = await new MemoryErasureScrubber(factory).CheckpointAsync(Token);

        Assert.Equal(MemoryErasureWalCheckpointAttempt.Unavailable, attempt);

        Assert.Equal([GrimoireOrdinaryFreshConnectionKind.ReadWrite], factory.Requested);
    }

    [Fact]
    public async Task Checkpoint_reports_unavailable_when_the_fresh_open_throws()
    {
        RefusingFactory factory = new() { Throw = true };

        Assert.Equal(
            MemoryErasureWalCheckpointAttempt.Unavailable,
            await new MemoryErasureScrubber(factory).CheckpointAsync(Token));
    }

    [Fact]
    public async Task Host_composition_registers_one_scrubber_over_the_ordinary_connection_factory()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();

        builder.Services.AddSingleton<IOsCredentialStore>(new InMemoryOsCredentialStore());

        builder.Services.AddArcanumInfrastructure(new ConfigurationBuilder().Build());

        ServiceDescriptor descriptor = Assert.Single(
            builder.Services,
            static candidate => candidate.ServiceType == typeof(MemoryErasureScrubber));

        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);

        await using ServiceProvider provider = builder.Services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<MemoryErasureScrubber>(), provider.GetRequiredService<MemoryErasureScrubber>());
    }

    private async Task WriteAsync(string sql)
    {
        await Connection.OpenAsync(Token);

        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(Token);
    }

    private sealed class RefusingFactory : IGrimoireOrdinaryConnectionFactory
    {
        internal List<GrimoireOrdinaryFreshConnectionKind> Requested { get; } = [];

        internal bool Throw { get; init; }

        public Task<Result<IGrimoireOrdinaryConnectionLease>> AcquireScopedAsync(
            SqliteConnection connection,
            CovenantSqliteConnectionMode mode,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The scrubber never borrows a scoped connection.");

        public Task<Result<IGrimoireOrdinaryConnectionLease>> OpenFreshAsync(
            GrimoireOrdinaryFreshConnectionKind kind,
            CancellationToken cancellationToken)
        {
            Requested.Add(kind);

            if (Throw)
            {
                throw new SqliteException("The database file could not be opened.", 14);
            }

            return Task.FromResult(Result<IGrimoireOrdinaryConnectionLease>.Failure(
                new Error(ErrorCodes.Grimoire.MaintenanceUnavailable, "Admission is closed.")));
        }
    }
}
