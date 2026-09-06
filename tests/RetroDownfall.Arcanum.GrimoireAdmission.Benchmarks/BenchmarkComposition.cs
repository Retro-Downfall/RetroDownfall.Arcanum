using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks;

internal sealed class BenchmarkComposition : IAsyncDisposable
{

    private readonly ServiceProvider _provider;

    private BenchmarkComposition(
        ServiceProvider provider,
        GrimoireConnectionAdmissionGate gate,
        CovenantConnectionDrain drain,
        GrimoireOrdinaryConnectionLifecycle lifecycle,
        CovenantSqliteConnectionInitializer initializer)
    {

        _provider = provider;

        Gate = gate;

        Drain = drain;

        Lifecycle = lifecycle;

        Initializer = initializer;

    }

    internal GrimoireConnectionAdmissionGate Gate { get; }

    internal CovenantConnectionDrain Drain { get; }

    internal GrimoireOrdinaryConnectionLifecycle Lifecycle { get; }

    internal CovenantSqliteConnectionInitializer Initializer { get; }

    internal IServiceProvider Services => _provider;

    internal static BenchmarkComposition Create()
    {

        SqliteNativeRuntime.Instance.Initialize();

        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        GrimoireDbPassphraseSource passphrase = new();

        passphrase.SetPassphrase("arcanum-grimoire-admission-benchmark-v1");

        CovenantConnectionDrain drain = new();

        GrimoireConnectionAdmissionGate gate = new(
            TimeProvider.System,
            drain,
            new BenchmarkMaintenancePaths(ArcanumPaths.GrimoireDatabaseFile));

        GrimoireOrdinaryConnectionLifecycle lifecycle = new(gate, drain);

        CovenantSqliteConnectionInitializer initializer = new(SqliteNativeRuntime.Instance);

        ServiceCollection services = new();

        services.AddSingleton<ISecretStore>(BenchmarkSecretStore.Instance);

        services.AddSingleton<IGrimoireDbPassphraseSource>(passphrase);

        services.AddSingleton<ICovenantConnectionDrain>(drain);

        services.AddSingleton(gate);

        services.AddSingleton<IGrimoireConnectionAdmissionGate>(gate);

        services.AddSingleton(lifecycle);

        services.AddSingleton<IGrimoireOrdinaryConnectionLifecycle>(lifecycle);

        services.AddSingleton<ISqliteNativeRuntime>(SqliteNativeRuntime.Instance);

        services.AddSingleton(initializer);

        services.AddSingleton<ICovenantSqliteConnectionInitializer>(initializer);

        services.AddDbContextPool<ArcanumDbContext>(
            (provider, options) => ArcanumDbContextOptionsConfigurator.Configure(
                options,
                provider.GetRequiredService<IGrimoireDbPassphraseSource>(),
                provider.GetRequiredService<IGrimoireOrdinaryConnectionLifecycle>(),
                provider.GetRequiredService<ICovenantConnectionDrain>(),
                provider.GetRequiredService<ICovenantSqliteConnectionInitializer>()),
            poolSize: 32);

        return new(
            services.BuildServiceProvider(),
            gate,
            drain,
            lifecycle,
            initializer);

    }

    public async ValueTask DisposeAsync()
    {

        await Drain.DrainAsync(CancellationToken.None).ConfigureAwait(false);

        await _provider.DisposeAsync().ConfigureAwait(false);

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    }

    private sealed class BenchmarkMaintenancePaths(string canonicalPath) : IGrimoireMaintenancePathAuthority
    {

        public string CanonicalDatabasePath => canonicalPath;

        public string ExportStagingDatabasePath(Guid operationId) =>
            canonicalPath + "." + operationId.ToString("N") + ".candidate";

    }

    private sealed class BenchmarkSecretStore : ISecretStore
    {

        internal static BenchmarkSecretStore Instance { get; } = new();

        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>(null);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Missing());

        public Task SaveApiKeyAsync(string apiKey) => throw new NotSupportedException();

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) =>
            throw new NotSupportedException();

    }

}
