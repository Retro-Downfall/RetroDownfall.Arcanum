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

        using ScopedConsumerPause pause = new("GrimoireLivenessProbe.ExecuteProbeAsync");

        Task<(bool Ok, string Detail)> probing = probe.ProbeAsync(CancellationToken.None);

        await pause.WaitUntilEnteredAsync();

        Assert.Equal(1, connections.LiveLeaseCountFor(CovenantSqliteConnectionMode.ReadOnly));

        pause.Release();

        (bool ok, _) = await probing;

        Assert.True(ok);

        Assert.Equal([CovenantSqliteConnectionMode.ReadOnly], connections.Modes);

        Assert.Equal(0, connections.LiveLeaseCountFor(CovenantSqliteConnectionMode.ReadOnly));

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

        using ScopedConsumerPause pause = new("CovenantCampaignScopeProbe.HasDeletionEventAsync");

        Task<Result<CovenantCampaignScopeState>> resolving = probe.ResolveAsync(
            Guid.NewGuid(),
            CancellationToken.None).AsTask();

        await pause.WaitUntilEnteredAsync();

        Assert.Equal(1, connections.LiveLeaseCountFor(CovenantSqliteConnectionMode.ReadOnly));

        pause.Release();

        Result<CovenantCampaignScopeState> result = await resolving;

        Assert.True(result.IsSuccess);

        Assert.Equal(CovenantCampaignScopeState.Unknown, result.Value);

        Assert.Equal([CovenantSqliteConnectionMode.ReadOnly], connections.Modes);

        Assert.Equal(0, connections.LiveLeaseCountFor(CovenantSqliteConnectionMode.ReadOnly));

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

        await db.Database.CloseConnectionAsync();

        ConstructorInfo? constructor = typeof(CovenantConnectionSource)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .SingleOrDefault(candidate =>
                candidate.GetParameters() is [_, { ParameterType: var parameterType }]
                && parameterType == typeof(IGrimoireOrdinaryConnectionFactory));

        Assert.NotNull(constructor);

        using CovenantConnectionSource source = (CovenantConnectionSource)constructor.Invoke([db, connections]);

        SqliteConnection connection = await source.GetOpenCoreConnectionAsync(CancellationToken.None);

        Assert.Equal(1, connections.LiveLeaseCount);

        Assert.Equal(1, connections.LiveOwnerLeaseCount);

        Assert.Equal(0, connections.LiveBorrowLeaseCount);

        Result<IGrimoireOrdinaryConnectionLease> borrowed = await connections.AcquireScopedAsync(
            connection,
            CovenantSqliteConnectionMode.ReadWrite,
            CancellationToken.None);

        Assert.True(borrowed.IsSuccess);

        borrowed.Value.Dispose();

        Assert.Equal(1, connections.LiveLeaseCount);

        Assert.Equal(1, connections.LiveOwnerLeaseCount);

        Assert.Equal(0, connections.LiveBorrowLeaseCount);

        source.Dispose();

        Assert.Equal(0, connections.LiveLeaseCount);

        Assert.Equal(0, connections.LiveOwnerLeaseCount);

    }

}

internal sealed class RecordingScopedOrdinaryConnectionFactory : IGrimoireOrdinaryConnectionFactory
{

    private int _liveLeaseCount;

    private int _liveOwnerLeaseCount;

    private int _liveBorrowLeaseCount;

    private int _liveReadOnlyLeaseCount;

    private int _liveReadWriteLeaseCount;

    internal List<CovenantSqliteConnectionMode> Modes { get; } = [];

    internal int LiveLeaseCount => Volatile.Read(ref _liveLeaseCount);

    internal int LiveOwnerLeaseCount => Volatile.Read(ref _liveOwnerLeaseCount);

    internal int LiveBorrowLeaseCount => Volatile.Read(ref _liveBorrowLeaseCount);

    internal int LiveLeaseCountFor(CovenantSqliteConnectionMode mode) => mode switch
    {
        CovenantSqliteConnectionMode.ReadOnly => Volatile.Read(ref _liveReadOnlyLeaseCount),
        CovenantSqliteConnectionMode.ReadWrite => Volatile.Read(ref _liveReadWriteLeaseCount),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public async Task<Result<IGrimoireOrdinaryConnectionLease>> AcquireScopedAsync(
        SqliteConnection connection,
        CovenantSqliteConnectionMode mode,
        CancellationToken cancellationToken)
    {

        Modes.Add(mode);

        bool ownsPhysicalOpen = connection.State != System.Data.ConnectionState.Open;

        if (ownsPhysicalOpen)
        {

            await connection.OpenAsync(cancellationToken);

        }

        _ = Interlocked.Increment(ref _liveLeaseCount);

        if (mode is CovenantSqliteConnectionMode.ReadOnly)
        {

            _ = Interlocked.Increment(ref _liveReadOnlyLeaseCount);

        }
        else
        {

            _ = Interlocked.Increment(ref _liveReadWriteLeaseCount);

        }

        if (ownsPhysicalOpen)
        {

            _ = Interlocked.Increment(ref _liveOwnerLeaseCount);

        }
        else
        {

            _ = Interlocked.Increment(ref _liveBorrowLeaseCount);

        }

        return Result<IGrimoireOrdinaryConnectionLease>.Success(
            new RecordingLease(
                connection,
                ownsPhysicalOpen,
                () =>
                {

                    _ = Interlocked.Decrement(ref _liveLeaseCount);

                    if (mode is CovenantSqliteConnectionMode.ReadOnly)
                    {

                        _ = Interlocked.Decrement(ref _liveReadOnlyLeaseCount);

                    }
                    else
                    {

                        _ = Interlocked.Decrement(ref _liveReadWriteLeaseCount);

                    }

                    if (ownsPhysicalOpen)
                    {

                        _ = Interlocked.Decrement(ref _liveOwnerLeaseCount);

                    }
                    else
                    {

                        _ = Interlocked.Decrement(ref _liveBorrowLeaseCount);

                    }

                }));

    }

    public Task<Result<IGrimoireOrdinaryConnectionLease>> OpenFreshAsync(
        GrimoireOrdinaryFreshConnectionKind kind,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    private sealed class RecordingLease : IGrimoireOrdinaryConnectionLease
    {

        private int _disposed;

        private readonly bool _ownsPhysicalOpen;

        private readonly Action _onDispose;

        internal RecordingLease(
            SqliteConnection connection,
            bool ownsPhysicalOpen,
            Action onDispose)
        {

            Connection = connection;

            _ownsPhysicalOpen = ownsPhysicalOpen;

            _onDispose = onDispose;

        }

        public SqliteConnection Connection { get; }

        public void Dispose()
        {

            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {

                if (_ownsPhysicalOpen)
                {

                    Connection.Close();

                }

                _onDispose();

            }

        }

        public ValueTask DisposeAsync()
        {

            Dispose();

            return ValueTask.CompletedTask;

        }

    }

}

internal sealed class ScopedConsumerPause : IDisposable
{

    private readonly TaskCompletionSource _entered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource _released =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly IDisposable _override;

    internal ScopedConsumerPause(string checkpoint)
    {

        _override = GrimoireScopedConsumerTestSeam.Override(
            checkpoint,
            cancellationToken =>
            {

                _entered.TrySetResult();

                return new ValueTask(_released.Task.WaitAsync(cancellationToken));

            });

    }

    internal Task WaitUntilEnteredAsync() =>
        _entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

    internal void Release() => _released.TrySetResult();

    public void Dispose()
    {

        Release();

        _override.Dispose();

    }

}
