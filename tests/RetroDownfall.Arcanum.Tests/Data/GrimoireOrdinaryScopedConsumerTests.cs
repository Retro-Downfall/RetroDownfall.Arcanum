using System.Reflection;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Api.Health;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("Grimoire")]
public sealed class GrimoireLivenessProbeTests(GrimoireFixture fixture)
{

    [SkippableFact]
    public async Task Probe_retains_read_only_admission_through_its_database_command()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string path = fixture.CopyDatabase();

        await using ArcanumDbContext db = fixture.CreateContext(path);

        RecordingScopedOrdinaryConnectionFactory connections = new();

        ServiceCollection services = new();

        services.AddSingleton(db);

        services.AddSingleton<IGrimoireOrdinaryConnectionFactory>(connections);

        await using ServiceProvider provider = services.BuildServiceProvider();

        GrimoireLivenessProbe probe = new(provider.GetRequiredService<IServiceScopeFactory>());

        (bool ok, _) = await probe.ProbeAsync(CancellationToken.None);

        Assert.True(ok);

        Assert.Equal([CovenantSqliteConnectionMode.ReadOnly], connections.Modes);

        Assert.Equal(0, connections.LiveLeaseCount);

    }

}

[Collection("Grimoire")]
public sealed class CovenantCampaignScopeProbeTests(GrimoireFixture fixture)
{

    [SkippableFact]
    public async Task Deletion_probe_retains_read_only_admission_through_its_database_command()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string path = fixture.CopyDatabase();

        await using ArcanumDbContext db = fixture.CreateContext(path);

        RecordingScopedOrdinaryConnectionFactory connections = new();

        ServiceCollection services = new();

        services.AddSingleton(db);

        services.AddSingleton<IGrimoireOrdinaryConnectionFactory>(connections);

        await using ServiceProvider provider = services.BuildServiceProvider();

        CovenantCampaignScopeProbe probe = new(provider.GetRequiredService<IServiceScopeFactory>());

        Result<CovenantCampaignScopeState> result = await probe.ResolveAsync(
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(CovenantCampaignScopeState.Unknown, result.Value);

        Assert.Equal([CovenantSqliteConnectionMode.ReadOnly], connections.Modes);

        Assert.Equal(0, connections.LiveLeaseCount);

    }

}

[Collection("Grimoire")]
public sealed class CovenantConnectionSourceTests(GrimoireFixture fixture)
{

    [SkippableFact]
    public async Task Source_retains_its_lease_after_an_independent_borrow_until_source_disposal()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string path = fixture.CopyDatabase();

        await using ArcanumDbContext db = fixture.CreateContext(path);

        RecordingScopedOrdinaryConnectionFactory connections = new();

        ConstructorInfo? constructor = typeof(CovenantConnectionSource)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .SingleOrDefault(candidate =>
                candidate.GetParameters() is [_, { ParameterType: var parameterType }]
                && parameterType == typeof(IGrimoireOrdinaryConnectionFactory));

        Assert.NotNull(constructor);

        using CovenantConnectionSource source = (CovenantConnectionSource)constructor.Invoke([db, connections]);

        SqliteConnection connection = await source.GetOpenCoreConnectionAsync(CancellationToken.None);

        Assert.Equal(1, connections.LiveLeaseCount);

        Result<IGrimoireOrdinaryConnectionLease> borrowed = await connections.AcquireScopedAsync(
            connection,
            CovenantSqliteConnectionMode.ReadWrite,
            CancellationToken.None);

        Assert.True(borrowed.IsSuccess);

        borrowed.Value.Dispose();

        Assert.Equal(1, connections.LiveLeaseCount);

        source.Dispose();

        Assert.Equal(0, connections.LiveLeaseCount);

    }

}

internal sealed class RecordingScopedOrdinaryConnectionFactory : IGrimoireOrdinaryConnectionFactory
{

    private int _liveLeaseCount;

    internal List<CovenantSqliteConnectionMode> Modes { get; } = [];

    internal int LiveLeaseCount => Volatile.Read(ref _liveLeaseCount);

    public async Task<Result<IGrimoireOrdinaryConnectionLease>> AcquireScopedAsync(
        SqliteConnection connection,
        CovenantSqliteConnectionMode mode,
        CancellationToken cancellationToken)
    {

        Modes.Add(mode);

        if (connection.State != System.Data.ConnectionState.Open)
        {

            await connection.OpenAsync(cancellationToken);

        }

        _ = Interlocked.Increment(ref _liveLeaseCount);

        return Result<IGrimoireOrdinaryConnectionLease>.Success(
            new RecordingLease(connection, () => Interlocked.Decrement(ref _liveLeaseCount)));

    }

    public Task<Result<IGrimoireOrdinaryConnectionLease>> OpenFreshAsync(
        GrimoireOrdinaryFreshConnectionKind kind,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    private sealed class RecordingLease(
        SqliteConnection connection,
        Action onDispose) : IGrimoireOrdinaryConnectionLease
    {

        private int _disposed;

        public SqliteConnection Connection { get; } = connection;

        public void Dispose()
        {

            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {

                onDispose();

            }

        }

        public ValueTask DisposeAsync()
        {

            Dispose();

            return ValueTask.CompletedTask;

        }

    }

}
