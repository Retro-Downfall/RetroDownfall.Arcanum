using System.Data.Common;

using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Security;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Benchmarks;

[Collection("ProcessEnvironment")]
public sealed class GrimoireAdmissionBenchmarkSmokeTests
{

    [Fact]
    public async Task Direct_gate_operations_finish_with_zero_live_state_and_clean_reopen()
    {

        string repositoryRoot = FindRepositoryRoot();

        Assert.True(
            File.Exists(Path.Combine(
                repositoryRoot,
                "tests",
                "RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks",
                "GrimoireAdmissionWorkloadBed.cs")),
            "The dedicated direct-gate workload bed is missing.");

        CovenantConnectionDrain drain = new();

        GrimoireConnectionAdmissionGate gate = new(
            TimeProvider.System,
            drain,
            new Paths(Path.Combine(Path.GetTempPath(), "arcanum-admission-smoke.db")));

        DirectState state = new(gate, 2);

        long callbackBaseline = gate.MaterializedTerminalCallbacks;

        using (PersistentWorkerHarness harness = new(2))
        {

            AdmissionBenchmarkPhaseResult result = harness.Run(
                new(
                    AdmissionBenchmarkPhaseKind.Throughput,
                    28,
                    1,
                    state.Execute),
                CancellationToken.None);

            Assert.Equal(56, result.OperationCount);

            Assert.Equal(56, result.SuccessCount);

            Assert.Equal(0, result.FailureCount);

        }

        Assert.Equal(callbackBaseline, gate.MaterializedTerminalCallbacks);

        Assert.Equal(0, state.LiveRequests);

        Assert.Equal(0, state.LiveWork);

        Assert.Equal(0, state.LiveEffects);

        Assert.Equal(0, state.LiveOpens);

        Result<IGrimoireClosingOwner> begun = gate.BeginOrResumeExclusive(Owner());

        Assert.True(begun.IsSuccess, begun.IsFailure ? begun.Error.Message : null);

        await using IGrimoireClosingOwner closing = begun.Value;

        Result drained = await gate.DrainRequestAndWorkAsync(closing, CancellationToken.None);

        Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

        Result<IGrimoireExclusiveClosedLease> closedResult = await gate.CloseConnectionAdmissionAsync(
            closing,
            CancellationToken.None);

        Assert.True(closedResult.IsSuccess, closedResult.IsFailure ? closedResult.Error.Message : null);

        await using IGrimoireExclusiveClosedLease closed = closedResult.Value;

        Result reopened = await closed.CompleteAsync(
            CovenantExclusiveLeaseDisposition.RollbackAndReopen,
            CancellationToken.None);

        Assert.True(reopened.IsSuccess, reopened.IsFailure ? reopened.Error.Message : null);

        Assert.True(gate.TryAcquireRequestLease(GrimoireRequestKind.Finite, out IGrimoireRequestLease? request));

        await request!.DisposeAsync();

        using SqliteConnection connection = new("Data Source=:memory:;Pooling=False");

        using IGrimoireConnectionOpenTicket open = gate.AcquireOrdinaryOpen(connection);

        Assert.True(open.RevalidateAfterNativeOpen().IsSuccess);

        Assert.True(open.MarkOpened().IsSuccess);

    }

    [Fact]
    public async Task Pooled_EF_cell_uses_isolated_SQLCipher_serving_composition_and_drains()
    {

        string repositoryRoot = FindRepositoryRoot();

        Assert.True(
            File.Exists(Path.Combine(
                repositoryRoot,
                "tests",
                "RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks",
                "BenchmarkComposition.cs")),
            "The dedicated pooled-EF composition is missing.");

        string home = Path.Combine(Path.GetTempPath(), "arcanum-admission-ef-" + Guid.NewGuid().ToString("N"));

        string? oldEnvironment = global::System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

        string? oldHome = global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME");

        global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Testing");

        global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", home);

        try
        {

            SqliteNativeRuntime.Instance.Initialize();

            Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

            GrimoireDbPassphraseSource passphrase = new();

            passphrase.SetPassphrase("admission-benchmark-fixed-passphrase");

            CovenantConnectionDrain drain = new();

            GrimoireConnectionAdmissionGate gate = new(
                TimeProvider.System,
                drain,
                new Paths(ArcanumPaths.GrimoireDatabaseFile));

            GrimoireOrdinaryConnectionLifecycle lifecycle = new(gate, drain);

            CovenantSqliteConnectionInitializer initializer = new(SqliteNativeRuntime.Instance);

            ServiceCollection services = new();

            services.AddSingleton<ISecretStore>(NoIoSecretStore.Instance);

            services.AddSingleton<IGrimoireDbPassphraseSource>(passphrase);

            services.AddSingleton<ICovenantConnectionDrain>(drain);

            services.AddSingleton<IGrimoireConnectionAdmissionGate>(gate);

            services.AddSingleton<IGrimoireOrdinaryConnectionLifecycle>(lifecycle);

            services.AddSingleton<ISqliteNativeRuntime>(SqliteNativeRuntime.Instance);

            services.AddSingleton<ICovenantSqliteConnectionInitializer>(initializer);

            services.AddDbContextPool<ArcanumDbContext>(
                (provider, options) => ArcanumDbContextOptionsConfigurator.Configure(
                    options,
                    provider.GetRequiredService<IGrimoireDbPassphraseSource>(),
                    provider.GetRequiredService<IGrimoireOrdinaryConnectionLifecycle>(),
                    provider.GetRequiredService<ICovenantConnectionDrain>(),
                    provider.GetRequiredService<ICovenantSqliteConnectionInitializer>()),
                poolSize: 32);

            await using ServiceProvider provider = services.BuildServiceProvider();

            await using (AsyncServiceScope scope = provider.CreateAsyncScope())
            {

                ArcanumDbContext context = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

                context.Database.OpenConnection();

                await using DbCommand scalar = context.Database.GetDbConnection().CreateCommand();

                scalar.CommandText = "SELECT 1";

                Assert.Equal(1L, Convert.ToInt64(scalar.ExecuteScalar()));

                await using DbCommand cipher = context.Database.GetDbConnection().CreateCommand();

                cipher.CommandText = "PRAGMA cipher_version";

                Assert.False(string.IsNullOrWhiteSpace(Convert.ToString(cipher.ExecuteScalar())));

                context.Database.CloseConnection();

            }

            Result drained = await drain.DrainAsync(CancellationToken.None);

            Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

        }
        finally
        {

            SqliteConnection.ClearAllPools();

            global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", oldHome);

            global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", oldEnvironment);

            if (Directory.Exists(home))
            {

                Directory.Delete(home, recursive: true);

            }

        }

    }

    private static CovenantExclusiveRecoveryOwner Owner() =>
        new(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            CovenantExclusiveOperation.CovenantReset,
            new CovenantDigest(Enumerable.Repeat((byte)1, 32).ToArray()));

    private static string FindRepositoryRoot()
    {

        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {

            if (File.Exists(Path.Combine(directory.FullName, "RetroDownfall.Arcanum.slnx")))
            {

                return directory.FullName;

            }

            directory = directory.Parent;

        }

        throw new InvalidOperationException("Could not locate the repository root.");

    }

    private sealed class DirectState(
        GrimoireConnectionAdmissionGate gate,
        int workerCount)
    {

        private readonly SqliteConnection[] _connections = Enumerable.Range(0, workerCount)
            .Select(static _ => new SqliteConnection("Data Source=:memory:;Pooling=False"))
            .ToArray();

        private long _liveRequests;

        private long _liveWork;

        private long _liveEffects;

        private long _liveOpens;

        internal long LiveRequests => Interlocked.Read(ref _liveRequests);

        internal long LiveWork => Interlocked.Read(ref _liveWork);

        internal long LiveEffects => Interlocked.Read(ref _liveEffects);

        internal long LiveOpens => Interlocked.Read(ref _liveOpens);

        internal bool Execute(
            int worker,
            int iteration,
            CancellationToken cancellationToken,
            ref long checksum)
        {

            cancellationToken.ThrowIfCancellationRequested();

            switch ((worker + iteration) % 7)
            {
                case 0:
                    Request(GrimoireRequestKind.Finite, readRevocation: false, ref checksum);
                    break;
                case 1:
                    Request(GrimoireRequestKind.QuiesceableStream, readRevocation: true, ref checksum);
                    break;
                case 2:
                    Work(withEffect: false, ref checksum);
                    break;
                case 3:
                    Work(withEffect: true, ref checksum);
                    break;
                case 4:
                    FailedOpen(worker, ref checksum);
                    break;
                case 5:
                    RequestOpen(worker, ref checksum);
                    break;
                default:
                    checksum = checked(checksum + gate.CurrentGeneration);
                    break;
            }

            return true;

        }

        private void Request(
            GrimoireRequestKind kind,
            bool readRevocation,
            ref long checksum)
        {

            if (!gate.TryAcquireRequestLease(kind, out IGrimoireRequestLease? lease))
            {

                throw new InvalidOperationException("Ordinary request admission was refused.");

            }

            Interlocked.Increment(ref _liveRequests);

            try
            {

                checksum = checked(checksum + lease!.Generation);

                if (readRevocation)
                {

                    checksum = checked(checksum + (lease.MaintenanceRevocation.CanBeCanceled ? 1 : 0));

                }

                lease.DisposeAsync().AsTask().GetAwaiter().GetResult();

            }
            finally
            {

                Interlocked.Decrement(ref _liveRequests);

            }

        }

        private void Work(bool withEffect, ref long checksum)
        {

            if (!gate.TryAcquireWorkLease(GrimoireWorkKind.BatchProcessing, out IGrimoireWorkLease? lease))
            {

                throw new InvalidOperationException("Ordinary work admission was refused.");

            }

            Interlocked.Increment(ref _liveWork);

            try
            {

                checksum = checked(checksum + lease!.Generation);

                if (withEffect)
                {

                    if (!lease.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? effect))
                    {

                        throw new InvalidOperationException("External effect admission was refused.");

                    }

                    Interlocked.Increment(ref _liveEffects);

                    try
                    {

                        effect!.DisposeAsync().AsTask().GetAwaiter().GetResult();

                    }
                    finally
                    {

                        Interlocked.Decrement(ref _liveEffects);

                    }

                }

                lease.DisposeAsync().AsTask().GetAwaiter().GetResult();

            }
            finally
            {

                Interlocked.Decrement(ref _liveWork);

            }

        }

        private void FailedOpen(int worker, ref long checksum)
        {

            Interlocked.Increment(ref _liveOpens);

            try
            {

                using IGrimoireConnectionOpenTicket ticket = gate.AcquireOrdinaryOpen(_connections[worker]);

                checksum = checked(checksum + ticket.Generation);

                ticket.MarkFailed();

            }
            finally
            {

                Interlocked.Decrement(ref _liveOpens);

            }

        }

        private void RequestOpen(int worker, ref long checksum)
        {

            if (!gate.TryAcquireRequestLease(GrimoireRequestKind.Finite, out IGrimoireRequestLease? request))
            {

                throw new InvalidOperationException("Nested request admission was refused.");

            }

            Interlocked.Increment(ref _liveRequests);

            try
            {

                Interlocked.Increment(ref _liveOpens);

                try
                {

                    using IGrimoireConnectionOpenTicket ticket = gate.AcquireOrdinaryOpen(_connections[worker]);

                    Result revalidated = ticket.RevalidateAfterNativeOpen();

                    Result opened = revalidated.IsSuccess ? ticket.MarkOpened() : revalidated;

                    if (opened.IsFailure)
                    {

                        throw new InvalidOperationException(opened.Error.Message);

                    }

                    checksum = checked(checksum + request!.Generation + ticket.Generation);

                }
                finally
                {

                    Interlocked.Decrement(ref _liveOpens);

                }

                request!.DisposeAsync().AsTask().GetAwaiter().GetResult();

            }
            finally
            {

                Interlocked.Decrement(ref _liveRequests);

            }

        }

    }

    private sealed class Paths(string canonicalPath) : IGrimoireMaintenancePathAuthority
    {

        public string CanonicalDatabasePath => canonicalPath;

        public string ExportStagingDatabasePath(Guid operationId) => canonicalPath + "." + operationId.ToString("N") + ".candidate";

    }

    private sealed class NoIoSecretStore : ISecretStore
    {

        internal static NoIoSecretStore Instance { get; } = new();

        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>(null);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() => Task.FromResult(SecretStoreReadResult.Missing());

        public Task SaveApiKeyAsync(string apiKey) => throw new NotSupportedException();

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => throw new NotSupportedException();

    }

}
