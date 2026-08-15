using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>
/// The one place a test composes <see cref="GrimoireSchemaInstaller"/> and the installation context
/// it needs.
/// </summary>
/// <remarks>
/// The installer stopped being static when it grew a manifest inspector, a per-tier initializer set,
/// and an injected logger, so every test that installs a schema now has to build the same small
/// object graph. Building it here rather than in each suite keeps a change to that graph from
/// rippling through a dozen unrelated files, and keeps every suite installing through the exact
/// composition the host uses.
///
/// <para>The context is fixed rather than derived from a clock or a secret store, so two runs of the
/// same suite seed byte-identical rows and any difference an assertion sees is a real one.</para>
/// </remarks>
internal static class GrimoireSchemaTestInstaller
{

    /// <summary>A stable identity: tests compare rows across runs, so a fresh GUID would defeat them.</summary>
    private const string InstallationIdentity = "3D6A1F42-77B5-4C0E-9E31-8A2B4D5F6C71";

    /// <summary>
    /// The SQLite provider has to be installed before the first connection is constructed. Doing it
    /// here means any suite that reaches the schema through this helper is safe in a filtered run,
    /// instead of only passing when some earlier test happened to initialize it.
    /// </summary>
    static GrimoireSchemaTestInstaller() => SqliteNativeRuntime.Instance.Initialize();

    /// <summary>
    /// Opens a connection and applies Covenant connection policy to it.
    /// </summary>
    /// <remarks>
    /// The initializer is what registers the <c>arcanum_*_authorized</c> scalar functions the schema's
    /// guard triggers call. A raw connection that skipped it fails any guarded write with
    /// "no such function" rather than denying it, so a test would fail, or pass, for the wrong reason.
    /// </remarks>
    internal static async Task<SqliteConnection> OpenAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {

        SqliteConnection connection = new(connectionString);

        try
        {

            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await CovenantSqliteConnectionInitializer.Instance
                .InitializeAsync(connection, CovenantSqliteConnectionMode.ReadWrite, cancellationToken)
                .ConfigureAwait(false);

            return connection;

        }
        catch
        {

            await connection.DisposeAsync().ConfigureAwait(false);

            throw;

        }

    }

    /// <summary>
    /// Composes the installer exactly as the container does, minus the logger, which every suite can
    /// supply when it wants to assert on a warning.
    /// </summary>
    internal static GrimoireSchemaInstaller Create() =>
        new(
            new GrimoireSchemaManifestInspector(GrimoireSchemaTierOwnershipRegistry.CreateDefault()),
            new GrimoireSchemaDataInitializers(
            [
                new CoreGrimoireSchemaDataInitializer(),
                new CovenantCanonicalSchemaDataInitializer(),
                new CovenantAcceleratorSchemaDataInitializer(),
            ]));

    /// <summary>
    /// The installation-local facts the three tier initializers run against.
    /// </summary>
    internal static GrimoireSchemaInitializationContext CreateContext() =>
        new(
            InstallationIdentity,
            AuthorityEpoch: 1,
            MasterKeyVersion: 1,
            MasterKeyFingerprint: CreateFingerprint(),
            RecoveryEnvelopeEpoch: 1,
            InstalledAtUtc: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>
    /// Installs all three tiers on an already-open connection, the way the host bootstrap does.
    /// </summary>
    internal static Task<GrimoireSchemaInstallResult> InstallAsync(
        SqliteConnection connection,
        int embeddingDimensions,
        CancellationToken cancellationToken) =>
        Create().InstallAsync(connection, embeddingDimensions, CreateContext(), cancellationToken);

    /// <summary>
    /// Registers the schema-installation graph the bootstrapper resolves out of its scope.
    /// </summary>
    /// <remarks>
    /// A test container that omits any of these fails at bootstrap rather than at compile time, which
    /// is the failure mode this helper exists to remove.
    /// </remarks>
    internal static IServiceCollection AddGrimoireSchemaInstallation(this IServiceCollection services)
    {

        services.AddSingleton<WeaveIndexAvailability>();

        services.AddSingleton<ICovenantSqliteConnectionInitializer>(
            static _ => CovenantSqliteConnectionInitializer.Instance);

        services.AddSingleton<IGrimoireSchemaDataInitializer, CoreGrimoireSchemaDataInitializer>();

        services.AddSingleton<IGrimoireSchemaDataInitializer, CovenantCanonicalSchemaDataInitializer>();

        services.AddSingleton<IGrimoireSchemaDataInitializer, CovenantAcceleratorSchemaDataInitializer>();

        services.AddSingleton<GrimoireSchemaDataInitializers>();

        services.AddSingleton(static _ => GrimoireSchemaTierOwnershipRegistry.CreateDefault());

        services.AddSingleton<GrimoireSchemaManifestInspector>();

        services.AddSingleton<GrimoireSchemaInstaller>();

        services.AddSingleton<CovenantAvailability>();

        services.AddSingleton<ICovenantAvailability>(
            static sp => sp.GetRequiredService<CovenantAvailability>());

        services.AddSingleton<CovenantAuthoritySnapshotProvider>();

        services.AddSingleton<ICovenantAuthoritySnapshotProvider>(
            static sp => sp.GetRequiredService<CovenantAuthoritySnapshotProvider>());

        return services;

    }

    /// <summary>
    /// A content-free stand-in for the real digest. The schema checks only the length, so the bytes
    /// simply have to be stable and the right count.
    /// </summary>
    private static byte[] CreateFingerprint()
    {

        byte[] fingerprint = new byte[32];

        for (int index = 0; index < fingerprint.Length; index++)
        {

            fingerprint[index] = (byte)(index + 1);

        }

        return fingerprint;

    }

}
