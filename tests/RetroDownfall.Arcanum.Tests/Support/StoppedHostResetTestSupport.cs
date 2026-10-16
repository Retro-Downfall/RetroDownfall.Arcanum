using System.Globalization;

using System.Reflection;

using System.Runtime.ExceptionServices;

using System.Security.Cryptography;

using System.Text;

using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Storage.Entities;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Secrets.Security;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// Seams for observing, and interrupting, the protected erasure a stopped-host global reset runs, over
/// the same command composition the executable uses.
/// </summary>
/// <remarks>
/// Everything here substitutes a collaborator at its own seam and nothing else: the coordinator's
/// constructor takes its fault seams as optional trailing arguments, and the offline cleanup is a
/// registered port. The rest of the composition (the lock, the active store, the journal, the real
/// SQLCipher catalog) is production.
/// </remarks>
internal static class StoppedHostResetTestSupport
{
    /// <summary>
    /// Replaces the coordinator registration with one that carries the given fault seams, built from
    /// exactly the arguments the production registration passes.
    /// </summary>
    internal static void UseFaultSeams(
        IServiceCollection services,
        CovenantErasureFaultSeam? faultSeam,
        GrimoireOfflineTransitionTerminalSuffixFaultSeam? terminalSuffixFaultSeam = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.RemoveAll<CovenantErasureCoordinator>();

        services.AddScoped(
            sp => new CovenantErasureCoordinator(
                sp.GetRequiredService<ILongRunningOperationCoordinator>(),
                sp.GetRequiredService<ILongRunningOperationStore>(),
                sp.GetRequiredService<ICovenantOperationGate>(),
                sp.GetRequiredService<ICovenantProtectedArtifactErasureKernel>(),
                sp.GetRequiredService<ICovenantManagedFileErasureKernel>(),
                sp.GetRequiredService<ICovenantErasureInventorySource>(),
                sp.GetRequiredService<ICovenantErasureTransition>(),
                sp.GetRequiredService<ICovenantDisclosureWriterLifecycle>(),
                sp.GetRequiredService<IGrimoireOfflineTransitionPhaseAuthority>(),
                sp.GetRequiredService<GrimoireOfflineTransitionEffectHandlerRegistry>(),
                sp.GetRequiredService<IGrimoireConnectionAdmissionGate>(),
                sp.GetRequiredService<IGrimoireMaintenanceConnectionFactory>(),
                sp.GetRequiredService<IGrimoireMaintenancePathAuthority>(),
                sp.GetRequiredService<IGrimoireDbPassphraseSource>(),
                sp.GetRequiredService<ICovenantClosedPeriodLedgerConnection>(),
                sp.GetRequiredService<GrimoireRequestAdmissionScope>(),
                sp.GetRequiredService<ICovenantConnectionDrain>(),
                sp.GetRequiredService<GrimoireOfflineTransitionDatabaseReconciler>(),
                sp.GetRequiredService<LongRunningOperationOwnership>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<CovenantErasureCoordinator>>(),
                faultSeam,
                terminalSuffixFaultSeam));
    }

    /// <summary>
    /// Wraps the offline cleanup so a callback sees the Grimoire exactly as the data phase left it,
    /// immediately before the cleanup deletes it.
    /// </summary>
    internal static void BeforeOfflineCleanup(
        IServiceCollection services,
        Func<Task> observe)
    {
        ArgumentNullException.ThrowIfNull(services);

        ArgumentNullException.ThrowIfNull(observe);

        services.RemoveAll<IInstallationResetOfflineCleanup>();

        services.AddScoped<IInstallationResetOfflineCleanup>(
            sp => new ObservingOfflineCleanup(
                ActivatorUtilities.CreateInstance<InstallationResetOfflineCleanup>(sp),
                observe));
    }

    /// <summary>The passphrase the fixture's template database is keyed from.</summary>
    internal static string FixturePassphrase { get; } =
        GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecret(
            GrimoireFixture.TestGrimoireSecret,
            [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]);

    /// <summary>Reads one scalar from the Grimoire on its own read-only connection.</summary>
    internal static async Task<long> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = ArcanumPaths.GrimoireDatabaseFile,
            Password = FixturePassphrase,
            Pooling = false,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());

        await connection.OpenAsync(CancellationToken.None);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {
            _ = command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(CancellationToken.None),
            CultureInfo.InvariantCulture);
    }

    /// <summary>Adds one ordinary Session, which only the ordinary cleanup of a factory reset deletes.</summary>
    internal static async Task<Guid> SeedOrdinarySessionAsync(GrimoireFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        await using ArcanumDbContext context = fixture.CreateContext(ArcanumPaths.GrimoireDatabaseFile);

        Guid sessionId = Guid.NewGuid();

        context.Sessions.Add(new Session
        {
            Id = sessionId,
            Status = "archived",
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        });

        await context.SaveChangesAsync();

        await context.Database.CloseConnectionAsync();

        return sessionId;
    }

    /// <summary>
    /// Puts the Grimoire in write-ahead logging, the journal mode every installation really has, so a
    /// case about sidecars is about the shape of a real database.
    /// </summary>
    internal static async Task UseWriteAheadLoggingAsync(GrimoireFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        await using (ArcanumDbContext context = fixture.CreateContext(ArcanumPaths.GrimoireDatabaseFile))
        {
            _ = await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");

            await context.Database.CloseConnectionAsync();
        }

        SqliteConnection.ClearAllPools();

        File.Delete(ArcanumPaths.GrimoireDatabaseFile + "-wal");

        File.Delete(ArcanumPaths.GrimoireDatabaseFile + "-shm");
    }

    /// <summary>Runs one statement against the Grimoire on its own read-write connection.</summary>
    internal static async Task<int> ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = ArcanumPaths.GrimoireDatabaseFile,
            Password = FixturePassphrase,
            Pooling = false,
            Mode = SqliteOpenMode.ReadWrite,
        }.ToString());

        await connection.OpenAsync(CancellationToken.None);

        // The schema's guard triggers call functions a connection has to register, whether or not the
        // statement would be allowed.
        CovenantSqliteConnectionInitializer.Instance.EnsureAuthorizationFunctions(connection);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {
            _ = command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    /// <summary>
    /// Whether anything in this process still holds the Grimoire open: opens it once on a connection
    /// of its own and closes it, and reports whether SQLite then cleaned its sidecars up.
    /// </summary>
    /// <remarks>
    /// SQLite removes the write-ahead log and its shared-memory index only when the connection that
    /// closes is the last one with the database open, so their absence afterwards is a portable proof
    /// that no pooled handle was left behind. On Windows the same handle would also have kept the file
    /// from being deleted.
    /// </remarks>
    internal static async Task<bool> NoHandleRemainsOpenAsync()
    {
        string log = ArcanumPaths.GrimoireDatabaseFile + "-wal";

        string index = ArcanumPaths.GrimoireDatabaseFile + "-shm";

        await using (SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = ArcanumPaths.GrimoireDatabaseFile,
            Password = FixturePassphrase,
            Pooling = false,
            Mode = SqliteOpenMode.ReadWrite,
        }.ToString()))
        {
            await connection.OpenAsync(CancellationToken.None);

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = "PRAGMA journal_mode;";

            _ = await command.ExecuteScalarAsync(CancellationToken.None);
        }

        return !File.Exists(log) && !File.Exists(index);
    }

    /// <summary>
    /// Every credential the in-memory store holds, by service and account, with its value's digest, so
    /// a case can say that a refusal changed none of them without printing a value.
    /// </summary>
    internal static Dictionary<string, string> CredentialDigests(InMemoryOsCredentialStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        FieldInfo field = typeof(InMemoryOsCredentialStore).GetField(
            "_secrets",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The in-memory credential store keeps no secrets field.");

        IEnumerable<KeyValuePair<string, string>> secrets =
            (IEnumerable<KeyValuePair<string, string>>)field.GetValue(store)!;

        return secrets.ToDictionary(
            static pair => pair.Key,
            static pair => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pair.Value))),
            StringComparer.Ordinal);
    }

    /// <summary>Replaces the data service with one that wraps the registered service.</summary>
    internal static void InterceptDataService(
        IServiceCollection services,
        Func<IInstallationResetStoppedHostDataService, IInstallationResetStoppedHostDataService> wrap)
    {
        ArgumentNullException.ThrowIfNull(services);

        ArgumentNullException.ThrowIfNull(wrap);

        services.RemoveAll<IInstallationResetStoppedHostDataService>();

        services.AddScoped<IInstallationResetStoppedHostDataService>(
            sp => wrap(sp.GetRequiredService<InstallationResetExistingGrimoire>()));
    }

    /// <summary>
    /// Replaces the active-record store with one that ends the process before a chosen write.
    /// </summary>
    internal static void DieBeforeActiveWrite(
        IServiceCollection services,
        Func<InstallationResetActivePublication, InstallationResetActiveRecord, bool> dieBefore)
    {
        ArgumentNullException.ThrowIfNull(services);

        ArgumentNullException.ThrowIfNull(dieBefore);

        services.RemoveAll<IInstallationResetActiveStore>();

        services.AddScoped<IInstallationResetActiveStore>(
            sp => new DyingActiveStore(sp.GetRequiredService<InstallationResetActiveStore>(), dieBefore));
    }

    /// <summary>
    /// Replaces the operation ledger store with one that ends the process on either side of a chosen
    /// checkpoint commit.
    /// </summary>
    internal static void DieAroundCheckpoint(
        IServiceCollection services,
        Func<int, int, bool> dieBefore,
        Func<int, int, bool> dieAfter)
    {
        ArgumentNullException.ThrowIfNull(services);

        ServiceDescriptor original = services.Last(
            static descriptor => descriptor.ServiceType == typeof(ILongRunningOperationStore));

        Func<IServiceProvider, object> factory = original.ImplementationFactory
            ?? throw new InvalidOperationException("The operation store is not registered by a factory.");

        services.RemoveAll<ILongRunningOperationStore>();

        services.AddScoped<ILongRunningOperationStore>(
            sp => new DyingOperationStore((ILongRunningOperationStore)factory(sp), dieBefore, dieAfter));
    }

    /// <summary>Replaces the secret store with one whose pure read of the master key throws.</summary>
    internal static void MasterKeyReadThrows(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.RemoveAll<ISecretStore>();

        services.AddSingleton<ISecretStore>(
            sp => RefusingSecretStore.Wrap(
                sp.GetRequiredService<DataProtectionSecretStore>(),
                nameof(ISecretStore.PeekApiKeyReadResultAsync)));
    }

    /// <summary>Replaces the secret store with one that refuses every read of the master key.</summary>
    internal static void RefuseTheMasterKeyRead(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.RemoveAll<ISecretStore>();

        services.AddSingleton<ISecretStore>(
            sp => RefusingSecretStore.Wrap(
                sp.GetRequiredService<DataProtectionSecretStore>(),
                nameof(ISecretStore.GetApiKeyAsync),
                nameof(ISecretStore.GetApiKeyReadResultAsync),
                nameof(ISecretStore.PeekApiKeyReadResultAsync)));
    }

    /// <summary>Makes construction of a scoped service fail, so a path that builds it is caught.</summary>
    internal static void RefuseToConstruct<T>(IServiceCollection services)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(services);

        services.RemoveAll<T>();

        services.AddScoped<T>(
            static _ => throw new InvalidOperationException(typeof(T).Name + " was constructed."));
    }

    /// <summary>Replaces a singleton with a stand-in that fails on any call, so a path that uses it is caught.</summary>
    internal static void RefuseEveryCall<T>(IServiceCollection services)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(services);

        services.RemoveAll<T>();

        services.AddSingleton<T>(static _ => DispatchProxy.Create<T, ThrowingStandIn>());
    }

    /// <summary>
    /// Gives the command the production startup probe's answer about an active reset, which the CLI
    /// harness otherwise replaces with one that never finds any.
    /// </summary>
    /// <remarks>
    /// A type of this assembly, so the harness leaves it in place. Only the active-reset read is the
    /// production one: whether the installation is fresh is a question about the machine, and the
    /// answer a test wants is that it is not.
    /// </remarks>
    internal static void UseTheProductionActiveResetRead(
        IServiceCollection services,
        IOsCredentialStore credentials)
    {
        ArgumentNullException.ThrowIfNull(services);

        ArgumentNullException.ThrowIfNull(credentials);

        services.RemoveAll<IInstallationStartupProbe>();

        // An instance, because the harness recognizes a probe the caller owns by the type of its
        // instance, and would replace one registered through a factory.
        services.AddSingleton<IInstallationStartupProbe>(
            new ProductionActiveResetProbe(
                new InstallationStartupProbe(
                    ArcanumPaths.GrimoireDirectory,
                    ArcanumPaths.ConfigurationFile,
                    ArcanumPaths.GrimoireDatabaseFile,
                    ArcanumPaths.ApiKeyStoreFile,
                    credentials)));
    }

    private sealed class ProductionActiveResetProbe(IInstallationStartupProbe inner) : IInstallationStartupProbe
    {
        public Task<Result<ActiveInstallationReset?>> ReadActiveResetAsync(
            CancellationToken cancellationToken = default) =>
            inner.ReadActiveResetAsync(cancellationToken);

        public Result<bool> IsFreshInstallation() => Result<bool>.Success(false);
    }

    /// <summary>The end of a process, thrown from a seam so nothing after it in that process runs.</summary>
    internal sealed class SimulatedProcessDeathException(string point)
        : Exception("The process was ended at " + point + ".");

    /// <summary>A daemon mutation that counts its calls and changes nothing.</summary>
    internal sealed class RecordingPreDataMutation : IInstallationResetPreDataMutation
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<Result> ExecuteAsync(CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _calls);

            return Task.FromResult(Result.Success());
        }
    }

    /// <summary>The stopped-host data service with hooks at the three places a case needs one.</summary>
    internal sealed class InterceptingStoppedHostDataService(IInstallationResetStoppedHostDataService inner)
        : IInstallationResetStoppedHostDataService
    {
        public Func<Result<DataRetentionPlan>, Result<DataRetentionPlan>>? MapPlan { get; init; }

        public Func<Result<DataRetentionPlan>, Task>? AfterPlanAsync { get; init; }

        public Func<DataRetentionApplyRequest, CancellationToken, Task>? BeforeApply { get; init; }

        public Action<Result<DataRetentionApplyResult>>? AfterApply { get; init; }

        public async Task<Result<DataRetentionPlan>> PlanUnderStoppedHostAuthorityAsync(
            InstallationResetDataPlanRequest request,
            IStoppedHostGrimoireAuthorityIssuer issuer,
            CancellationToken cancellationToken)
        {
            Result<DataRetentionPlan> plan = await inner
                .PlanUnderStoppedHostAuthorityAsync(request, issuer, cancellationToken)
                .ConfigureAwait(false);

            if (AfterPlanAsync is not null)
            {
                await AfterPlanAsync(plan).ConfigureAwait(false);
            }

            return MapPlan is null ? plan : MapPlan(plan);
        }

        public Task<Result<InstallationResetWorkspaceResolution>> ResolveWorkspaceUnderStoppedHostAuthorityAsync(
            string invocationDirectory,
            IStoppedHostGrimoireAuthorityIssuer issuer,
            CancellationToken cancellationToken) =>
            inner.ResolveWorkspaceUnderStoppedHostAuthorityAsync(invocationDirectory, issuer, cancellationToken);

        public Task<Result<Guid>> ReadIdentityUnderStoppedHostAuthorityAsync(
            IStoppedHostGrimoireAuthorityIssuer issuer,
            CancellationToken cancellationToken) =>
            inner.ReadIdentityUnderStoppedHostAuthorityAsync(issuer, cancellationToken);

        public Task<Result<HostProcessToolsDatabaseMarkerEvidence>> ReadHostToolsEvidenceUnderStoppedHostAuthorityAsync(
            IStoppedHostGrimoireAuthorityIssuer issuer,
            CancellationToken cancellationToken) =>
            inner.ReadHostToolsEvidenceUnderStoppedHostAuthorityAsync(issuer, cancellationToken);

        public async Task<Result<DataRetentionApplyResult>> ApplyUnderStoppedHostAuthorityAsync(
            DataRetentionApplyRequest request,
            IStoppedHostGrimoireAuthorityIssuer issuer,
            CancellationToken cancellationToken)
        {
            if (BeforeApply is not null)
            {
                await BeforeApply(request, cancellationToken).ConfigureAwait(false);
            }

            Result<DataRetentionApplyResult> applied = await inner
                .ApplyUnderStoppedHostAuthorityAsync(request, issuer, cancellationToken)
                .ConfigureAwait(false);

            AfterApply?.Invoke(applied);

            return applied;
        }

        public Task<Result<StoppedHostNestedLaunchState>> ReadNestedLaunchStateUnderStoppedHostAuthorityAsync(
            Guid nestedOperationId,
            IStoppedHostGrimoireAuthorityIssuer issuer,
            CancellationToken cancellationToken) =>
            inner.ReadNestedLaunchStateUnderStoppedHostAuthorityAsync(nestedOperationId, issuer, cancellationToken);
    }

    private sealed class DyingActiveStore(
        IInstallationResetActiveStore inner,
        Func<InstallationResetActivePublication, InstallationResetActiveRecord, bool> dieBefore)
        : IInstallationResetActiveStore
    {
        public string GuardedRoot => inner.GuardedRoot;

        public Task<Result<InstallationResetActiveRecoveryState>> RecoverAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            CancellationToken cancellationToken = default) =>
            inner.RecoverAsync(heldInstallationLock, cancellationToken);

        public Task<Result<InstallationResetActivePublication>> BeginAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            Guid installationId,
            InstallationResetActiveRecord record,
            CancellationToken cancellationToken = default) =>
            inner.BeginAsync(heldInstallationLock, installationId, record, cancellationToken);

        public Task<Result<InstallationResetActivePublication>> AdvanceAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            InstallationResetActivePublication current,
            InstallationResetActiveRecord next,
            CancellationToken cancellationToken = default)
        {
            if (dieBefore(current, next))
            {
                throw new SimulatedProcessDeathException(
                    "the write of phase " + next.Phase + " with receipt " + next.NestedTransitionReceipt?.Phase);
            }

            return inner.AdvanceAsync(heldInstallationLock, current, next, cancellationToken);
        }

        public Task<Result<InstallationResetActiveRecoveryState>> InspectAsync(
            CancellationToken cancellationToken = default) =>
            inner.InspectAsync(cancellationToken);

        public Task<Result<InstallationResetActivePublication>> MigrateLegacyV1Async(
            ArcanumMaintenanceLock heldInstallationLock,
            Guid installationId,
            InstallationResetActiveRecord expectedRecord,
            FileHandleIdentity expectedIdentity,
            CancellationToken cancellationToken = default) =>
            inner.MigrateLegacyV1Async(
                heldInstallationLock,
                installationId,
                expectedRecord,
                expectedIdentity,
                cancellationToken);

        public Task<Result> RetireAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            inner.RetireAsync(heldInstallationLock, operationId, cancellationToken);

        public Task<Result> CompleteStartupCleanupAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            CancellationToken cancellationToken = default) =>
            inner.CompleteStartupCleanupAsync(heldInstallationLock, cancellationToken);
    }

    private sealed class DyingOperationStore(
        ILongRunningOperationStore inner,
        Func<int, int, bool> dieBefore,
        Func<int, int, bool> dieAfter) : ILongRunningOperationStore
    {
        public Task<LongRunningOperation> CreateAsync(
            LongRunningOperationCreateRequest request,
            CancellationToken cancellationToken = default) =>
            inner.CreateAsync(request, cancellationToken);

        public Task<LongRunningOperationRequestIdentityResult> ResolveOrCreateAsync(
            LongRunningOperationCreateRequest request,
            LongRunningOperationRequestIdentity identity,
            CancellationToken cancellationToken = default) =>
            inner.ResolveOrCreateAsync(request, identity, cancellationToken);

        public Task<LongRunningOperation?> TryStartSingleFlightAsync(
            LongRunningOperationCreateRequest request,
            string ownerId,
            DateTimeOffset utcNow,
            DateTimeOffset leaseExpiresAt,
            CancellationToken cancellationToken = default) =>
            inner.TryStartSingleFlightAsync(request, ownerId, utcNow, leaseExpiresAt, cancellationToken);

        public Task<LongRunningOperation?> GetAsync(
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            inner.GetAsync(operationId, cancellationToken);

        public Task<LongRunningOperationRequestIdentity?> FindRequestIdentityAsync(
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            inner.FindRequestIdentityAsync(operationId, cancellationToken);

        public Task<LongRunningOperationRequestIdentityMatch?> FindByRequestedOperationIdAsync(
            Guid requestedOperationId,
            CancellationToken cancellationToken = default) =>
            inner.FindByRequestedOperationIdAsync(requestedOperationId, cancellationToken);

        public Task<IReadOnlyList<LongRunningOperation>> ListAsync(
            LongRunningOperationQuery query,
            CancellationToken cancellationToken = default) =>
            inner.ListAsync(query, cancellationToken);

        public Task<IReadOnlyList<LongRunningOperation>> FindExpiredAsync(
            DateTimeOffset utcNow,
            int limit,
            CancellationToken cancellationToken = default) =>
            inner.FindExpiredAsync(utcNow, limit, cancellationToken);

        public Task<LongRunningOperationLeaseResult> TryAcquireLeaseAsync(
            Guid operationId,
            string ownerId,
            DateTimeOffset utcNow,
            DateTimeOffset leaseExpiresAt,
            CancellationToken cancellationToken = default) =>
            inner.TryAcquireLeaseAsync(operationId, ownerId, utcNow, leaseExpiresAt, cancellationToken);

        public Task<bool> HeartbeatAsync(
            Guid operationId,
            string ownerId,
            DateTimeOffset utcNow,
            DateTimeOffset leaseExpiresAt,
            CancellationToken cancellationToken = default) =>
            inner.HeartbeatAsync(operationId, ownerId, utcNow, leaseExpiresAt, cancellationToken);

        public Task<bool> RenewLeaseAsync(
            Guid operationId,
            string ownerId,
            DateTimeOffset utcNow,
            DateTimeOffset leaseExpiresAt,
            CancellationToken cancellationToken = default) =>
            inner.RenewLeaseAsync(operationId, ownerId, utcNow, leaseExpiresAt, cancellationToken);

        public async Task<bool> SaveCheckpointAsync(
            Guid operationId,
            string ownerId,
            int expectedCheckpointVersion,
            int checkpointVersion,
            byte[]? checkpointPayload,
            string? checkpointReference,
            string publicSummary,
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default)
        {
            if (dieBefore(expectedCheckpointVersion, checkpointVersion))
            {
                throw new SimulatedProcessDeathException(
                    "before the checkpoint " + expectedCheckpointVersion + " to " + checkpointVersion);
            }

            bool saved = await inner.SaveCheckpointAsync(
                operationId,
                ownerId,
                expectedCheckpointVersion,
                checkpointVersion,
                checkpointPayload,
                checkpointReference,
                publicSummary,
                utcNow,
                cancellationToken).ConfigureAwait(false);

            if (saved && dieAfter(expectedCheckpointVersion, checkpointVersion))
            {
                throw new SimulatedProcessDeathException(
                    "after the checkpoint " + expectedCheckpointVersion + " to " + checkpointVersion);
            }

            return saved;
        }

        public Task<bool> TryTransitionAsync(
            Guid operationId,
            long expectedRevision,
            string? ownerId,
            LongRunningOperationState state,
            DateTimeOffset utcNow,
            string? terminalErrorCode = null,
            CancellationToken cancellationToken = default) =>
            inner.TryTransitionAsync(
                operationId,
                expectedRevision,
                ownerId,
                state,
                utcNow,
                terminalErrorCode,
                cancellationToken);

        public Task<bool> RequestCancellationAsync(
            Guid operationId,
            long expectedRevision,
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default) =>
            inner.RequestCancellationAsync(operationId, expectedRevision, utcNow, cancellationToken);

        public Task<bool> ResetForRetryAsync(
            Guid operationId,
            long expectedRevision,
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default) =>
            inner.ResetForRetryAsync(operationId, expectedRevision, utcNow, cancellationToken);

        public Task<IReadOnlyList<LongRunningOperationCount>> GetCountsAsync(
            CancellationToken cancellationToken = default) =>
            inner.GetCountsAsync(cancellationToken);
    }

    /// <summary>
    /// A secret store that refuses named reads as an operating-system credential store that cannot
    /// answer does, and forwards everything else.
    /// </summary>
    private class RefusingSecretStore : DispatchProxy
    {
        private ISecretStore _inner = null!;

        private HashSet<string> _refused = [];

        internal static ISecretStore Wrap(ISecretStore inner, params string[] refused)
        {
            ISecretStore proxy = Create<ISecretStore, RefusingSecretStore>();

            RefusingSecretStore self = (RefusingSecretStore)(object)proxy;

            self._inner = inner;

            self._refused = [.. refused];

            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);

            if (_refused.Contains(targetMethod.Name))
            {
                throw new InvalidOperationException("The operating-system credential store did not answer.");
            }

            try
            {
                return targetMethod.Invoke(_inner, args);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();

                throw;
            }
        }
    }

    private class ThrowingStandIn : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException(
                (targetMethod?.DeclaringType?.Name ?? "A stand-in") + "." + targetMethod?.Name + " was called.");
    }

    private sealed class ObservingOfflineCleanup(
        IInstallationResetOfflineCleanup inner,
        Func<Task> observe) : IInstallationResetOfflineCleanup
    {
        public Task<Result<InstallationResetFileSystemInventory>> PlanAsync(
            string[] selectedRoots,
            string[] excludedRoots,
            CancellationToken cancellationToken) =>
            inner.PlanAsync(selectedRoots, excludedRoots, cancellationToken);

        public async Task<Result<InstallationResetOfflineCleanupResult>> ExecuteAsync(
            InstallationResetPlan plan,
            CancellationToken cancellationToken)
        {
            await observe().ConfigureAwait(false);

            return await inner.ExecuteAsync(plan, cancellationToken).ConfigureAwait(false);
        }
    }
}
