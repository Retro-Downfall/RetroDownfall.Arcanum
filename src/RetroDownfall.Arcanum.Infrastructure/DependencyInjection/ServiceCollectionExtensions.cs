using A2A;

using System.Diagnostics.CodeAnalysis;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.CommLink;
using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Core.Chronosync;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Configuration.Presets;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Events;
using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Intelligence.WebResearch;
using RetroDownfall.Arcanum.Core.Pattern;
using RetroDownfall.Arcanum.Core.Resilience;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Annals;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.CommLink;
using RetroDownfall.Arcanum.Infrastructure.Chronosync;
using RetroDownfall.Arcanum.Infrastructure.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Coordination;
using RetroDownfall.Arcanum.Infrastructure.Daemons;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Logging;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Infrastructure.Pattern;
using RetroDownfall.Arcanum.Infrastructure.Platform;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Resilience;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Storage;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Infrastructure.Telemetry;
using RetroDownfall.Arcanum.Infrastructure.Theme;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.WebResearch;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;
using RetroDownfall.Arcanum.Infrastructure.Lexicon;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;
using RetroDownfall.Arcanum.Infrastructure.Tower;

namespace RetroDownfall.Arcanum.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    internal static IServiceCollection AddInstallationResetRecoveryAwareHostedService<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TService>(
        this IServiceCollection services)
        where TService : class, IHostedService
    {

        services.TryAddSingleton<TService>();

        services.AddHostedService(static sp =>
            new InstallationResetRecoveryAwareHostedService<TService>(
                sp.GetRequiredService<TService>(),
                sp.GetService<InstallationResetApiAdmission>()));

        return services;

    }

    /// <summary>
    /// Registers <see cref="IEyeOfTheWorld"/> without pulling the full infrastructure stack (for example the CLI <c>look</c> command).
    /// </summary>
    public static IServiceCollection AddArcanumEyeOfTheWorld(this IServiceCollection services)
    {
        services.AddSingleton<IEyeOfTheWorld, EyeOfTheWorldService>();
        return services;
    }

    /// <summary>
    /// Registers OS-level light/dark preference detection for CLI theming (narrow registration; no Spectre).
    /// </summary>
    public static IServiceCollection AddArcanumThemeDetection(this IServiceCollection services)
    {
        services.AddSingleton<IThemeDetector, ThemeDetector>();

        return services;
    }

    /// <summary>
    /// Registers the retained client-mutation mutex and read-only maintenance-evidence admission
    /// used by first-party clients that write installation-local state.
    /// </summary>
    public static IServiceCollection AddArcanumClientMutationCoordination(
        this IServiceCollection services)
    {

        services.TryAddSingleton<IOsCredentialStore>(static _ =>
            new OsCredentialStore());

        services.TryAddSingleton<IInstallationStartupProbe>(static sp =>
            new InstallationStartupProbe(
                ArcanumPaths.GrimoireDirectory,
                ArcanumPaths.ConfigurationFile,
                ArcanumPaths.GrimoireDatabaseFile,
                ArcanumPaths.ApiKeyStoreFile,
                sp.GetRequiredService<IOsCredentialStore>()));

        services.TryAddSingleton(static _ =>
            new ClientMutationBlockerStore(ArcanumPaths.GrimoireDirectory));

        services.TryAddSingleton<IClientMutationResetEvidenceProbe>(static sp =>
            new InstallationResetClientMutationEvidenceProbe(
                sp.GetRequiredService<IInstallationStartupProbe>()));

        services.TryAddSingleton<IClientMutationRestoreEvidenceProbe>(static sp =>
            new BackupRestoreClientMutationEvidenceProbe(
                ArcanumPaths.GrimoireDirectory,
                sp.GetRequiredService<IOsCredentialStore>()));

        services.TryAddSingleton<IClientMutationEvidenceProbe>(static sp =>
            new CompositeClientMutationEvidenceProbe(
                sp.GetRequiredService<ClientMutationBlockerStore>(),
                sp.GetRequiredService<IClientMutationResetEvidenceProbe>(),
                sp.GetRequiredService<IClientMutationRestoreEvidenceProbe>()));

        services.TryAddSingleton(static sp =>
            new InstallationMaintenanceCoordination(
                ArcanumPaths.GrimoireDirectory,
                sp.GetRequiredService<ClientMutationBlockerStore>(),
                sp.GetRequiredService<IClientMutationResetEvidenceProbe>(),
                sp.GetRequiredService<IClientMutationRestoreEvidenceProbe>()));

        services.TryAddSingleton(static sp =>
            new ArcanumClientMutationBoundary(
                ArcanumPaths.GrimoireDirectory,
                sp.GetRequiredService<IClientMutationEvidenceProbe>()));

        services.TryAddSingleton<IArcanumClientMutationBoundary>(static sp =>
            sp.GetRequiredService<ArcanumClientMutationBoundary>());

        return services;

    }

    /// <summary>
    /// Registers the shared preset catalog, planner orchestration, complete-candidate validation,
    /// secure credential-readiness probe, and atomic file persistence used by the CLI and
    /// Compendium.
    /// </summary>
    public static IServiceCollection AddArcanumConfigurationPresets(
        this IServiceCollection services,
        Func<
            IServiceProvider,
            IConfigurationPresetService,
            IConfigurationPresetService>? decorate = null)
    {

        services.AddDataProtection()
            .SetApplicationName("ArcanumCore")
            .PersistKeysToFileSystem(new DirectoryInfo(DataProtectionKeyPaths.Directory));

        services.TryAddSingleton<IOsCredentialStore>(static _ => new OsCredentialStore());

        services.TryAddSingleton<IWebResearchCredentialStore, WebResearchCredentialStore>();

        services.TryAddSingleton<IProviderCredentialStore, ProviderCredentialStore>();

        services.TryAddSingleton<IProviderApiKeyResolver, ProviderApiKeyResolver>();

        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<ConfigurationValidator>();

        services.TryAddSingleton<ConfigurationWriter>();

        services.TryAddSingleton<ConfigurationPresetPersistenceHooks>();

        services.TryAddSingleton<
            IConfigurationPresetCandidateValidator,
            ConfigurationPresetCandidateValidator>();

        services.TryAddSingleton<
            IConfigurationPresetPersistence,
            FileConfigurationPresetPersistence>();

        services.TryAddSingleton<ConfigurationPresetService>();

        services.TryAddSingleton<IConfigurationPresetService>(sp =>
        {

            IConfigurationPresetService inner =
                sp.GetRequiredService<ConfigurationPresetService>();

            return decorate is null
                ? inner
                : decorate(sp, inner);

        });

        return services;

    }

    /// <summary>
    /// Registers <see cref="IDaemonManager"/> for Windows Service (<c>sc.exe</c>), macOS launchd, or Linux systemd user units (narrow registration; does not pull EF Core, Serilog file logging, or Grimoire).
    /// </summary>
    public static IServiceCollection AddArcanumDaemonManagement(this IServiceCollection services)
    {
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<IDaemonManager, WindowsDaemonManager>();
        }
        else if (OperatingSystem.IsMacOS())
        {
            services.AddSingleton<IDaemonManager, MacOsDaemonManager>();
        }
        else if (OperatingSystem.IsLinux())
        {
            services.AddSingleton<IDaemonManager, LinuxDaemonManager>();
        }
        else
        {
            throw new PlatformNotSupportedException("Arcanum daemon management is only supported on Windows, macOS, and Linux.");
        }

        return services;
    }

    /// <summary>
    /// Registers Grimoire EF Core, scoped repository and Chronosync engine, and a one-shot CLI bootstrap gate (no Serilog file logging, MCP, or campaign logger).
    /// </summary>
    public static IServiceCollection AddArcanumGrimoireForCli(this IServiceCollection services)
    {
        services.AddArcanumClientMutationCoordination();

        services.AddSingleton<IGrimoireDbPassphraseSource, GrimoireDbPassphraseSource>();

        services.AddSingleton<IGrimoireDbReadiness, GrimoireDbReadiness>();

        services.AddSingleton(TimeProvider.System);

        // Divination's managed cosine fallback is the only search path, and its availability flag is
        // read on the CLI's own turn route. Its absence here was a real bug: schema installation
        // resolved it with GetRequiredService and the resulting throw silently forced the default
        // embedding dimension on every CLI bootstrap.
        services.AddSingleton<WeaveIndexAvailability>();

        services.AddSingleton<ICovenantSqliteConnectionInitializer>(
            static _ => CovenantSqliteConnectionInitializer.Instance);

        services.AddSingleton<IGrimoireSchemaDataInitializer, CoreGrimoireSchemaDataInitializer>();

        services.AddSingleton<IGrimoireSchemaDataInitializer, CovenantCanonicalSchemaDataInitializer>();

        services.AddSingleton<IGrimoireSchemaDataInitializer, CovenantAcceleratorSchemaDataInitializer>();

        services.AddSingleton<GrimoireSchemaDataInitializers>();

        services.AddSingleton(static _ => GrimoireSchemaTierOwnershipRegistry.CreateDefault());

        services.AddSingleton<GrimoireSchemaManifestInspector>();

        // The chains say which versions each tier has had and how to reach the one this binary
        // declares. Injected rather than read statically so the installer has exactly one source of
        // that answer, and so a suite can drive a longer chain through the same entry point.
        services.AddSingleton(static _ => GrimoireSchemaVersionChains.Default);

        services.AddSingleton<GrimoireSchemaInstaller>();

        services.AddSingleton<GrimoireSchemaBackfillRunner>();

        services.AddScoped<GrimoireSchemaTransitionCoordinator>();

        // One table for the whole host: a Covenant capability is registered by the API pipeline and
        // taken by the in-process MCP server, and those two run on different tasks.
        services.AddSingleton<CovenantToolCapabilityRegistry>();

        services.AddCovenantAuthority();

        services.AddCampaignPathIdentity();

        services.AddCovenantPersistence();

        services.AddDbContext<ArcanumDbContext>((sp, options) =>
            ArcanumDbContextOptionsConfigurator.Configure(
                options,
                sp.GetRequiredService<IGrimoireDbPassphraseSource>(),
                sp.GetRequiredService<IGrimoireOrdinaryConnectionLifecycle>(),
                sp.GetRequiredService<ICovenantConnectionDrain>(),
                sp.GetRequiredService<ICovenantSqliteConnectionInitializer>()));

        // GrimoireRepository requires the attachment store (session fork/purge hooks). Register it
        // here for the deliberately offline CLI maintenance operations.
        services.AddScoped<ISessionAttachmentStore, SessionAttachmentStore>();
        services.AddScoped<ISessionContextPinStore, SessionContextPinStore>();
        services.AddScoped<IAttachmentSourceResolver, AttachmentSourceResolver>();

        // AttachmentSourceResolver requires the host workspace context when offline maintenance
        // resolves repository services from the CLI container.
        services.TryAddSingleton<IHostWorkspaceContext, HostWorkspaceContext>();

        // An explicit factory rather than a type registration: the Covenant mutation kernel is
        // internal, so the composed constructor cannot be reached by a reflective activator.
        services.AddScoped<IGrimoireRepository>(
            static sp => new GrimoireRepository(
                sp.GetRequiredService<ArcanumDbContext>(),
                sp.GetRequiredService<ISessionAttachmentStore>(),
                sp.GetRequiredService<ILogger<GrimoireRepository>>(),
                sp.GetRequiredService<IOptionsSnapshot<ArcanumSettings>>(),
                sp.GetService<ISessionAttachmentIndexMaintenance>(),
                sp.GetService<CovenantMutationKernel>(),
                sp.GetRequiredService<IGrimoireOrdinaryConnectionFactory>()));

        // The narrow turn-begin port is deliberately a separate registration over the same scoped
        // instance. Resolving it through IGrimoireRepository would let any holder of the broad
        // interface reach Campaign-binding writes it has no business performing (§10.12).
        services.AddScoped<ISessionTurnBeginStore>(
            static sp => (GrimoireRepository)sp.GetRequiredService<IGrimoireRepository>());

        // The batch-aware finalizer is the same scoped instance again, exposed as the narrow
        // publication port so a caller cannot reach entry writes through it (§10.13).
        services.AddScoped<IGrimoireTurnCommitter>(
            static sp => (GrimoireRepository)sp.GetRequiredService<IGrimoireRepository>());

        services.AddSingleton<IGrimoireCliInitialization, GrimoireCliInitialization>();
        services.AddScoped<ILongRunningOperationStore, LongRunningOperationStore>();
        services.AddScoped<ILongRunningOperationCoordinator, LongRunningOperationCoordinator>();
        services.AddScoped<IBlobEncryptionMetadataStore, BlobEncryptionMetadataStore>();
        services.AddScoped<BlobEncryptionFileProcessor>();
        services.AddScoped<BlobEncryptionLifecycleService>();
        services.AddScoped<IBlobEncryptionLifecycleService>(
            static sp => sp.GetRequiredService<BlobEncryptionLifecycleService>());

        return services;
    }

    /// <summary>
    /// Registers the OS keychain–backed master API key store with Data Protection fallback/mirror,
    /// plus the concrete <see cref="DataProtectionSecretStore"/> used for Grimoire encryption secrets.
    /// </summary>
    public static IServiceCollection AddArcanumSecretStore(this IServiceCollection services)
    {

        // Must use the parameterless-ctor factory, not TryAddSingleton<IOsCredentialStore, OsCredentialStore>():
        // the generic overload lets the container pick OsCredentialStore's test-seam constructor
        // (OsCredentialStore(IOsCredentialStore inner)), which requests IOsCredentialStore again and
        // self-cycles at resolution time.
        services.TryAddSingleton<IOsCredentialStore>(static _ => new OsCredentialStore());

        services.AddSingleton<DataProtectionSecretStore>();

        services.AddSingleton<IWebResearchCredentialStore, WebResearchCredentialStore>();

        services.AddSingleton<IProviderCredentialStore, ProviderCredentialStore>();

        services.AddSingleton<IProviderApiKeyResolver, ProviderApiKeyResolver>();

        services.AddSingleton<ISecretStore>(static sp => new OsKeychainSecretStore(
            sp.GetRequiredService<IOsCredentialStore>(),
            sp.GetRequiredService<DataProtectionSecretStore>(),
            sp.GetRequiredService<IApiKeyDigestCache>(),
            sp.GetService<ILogger<OsKeychainSecretStore>>()));

        services.AddSingleton<FileEncryptionKeyProvider>();
        services.AddSingleton<IFileEncryptionKeyProvider>(
            static sp => sp.GetRequiredService<FileEncryptionKeyProvider>());
        services.AddSingleton<IFileEncryptionKeyRing>(
            static sp => sp.GetRequiredService<FileEncryptionKeyProvider>());

        services.AddSingleton<IEncryptedBlobStore, EncryptedBlobStore>();

        services.AddSingleton<IEncryptedBlobDiagnostics, EncryptedBlobDiagnostics>();

        return services;

    }

    /// <summary>
    /// W6.4: the minimal secret/grimoire stack the CLI shares with the API host — Data Protection,
    /// the API-key digest cache, the OS-keychain-backed secret store, and the CLI Grimoire. Owned
    /// here (next to <see cref="AddArcanumInfrastructure"/>) so the CLI wiring cannot silently drift
    /// out of sync with the host (see DX5).
    /// </summary>
    public static IServiceCollection AddArcanumCliClientStack(this IServiceCollection services)
    {
        services.AddDataProtection()
            .SetApplicationName("ArcanumCore")
            .PersistKeysToFileSystem(new DirectoryInfo(DataProtectionKeyPaths.Directory));

        services.AddSingleton<IApiKeyDigestCache, ApiKeyDigestCache>();

        services.AddArcanumSecretStore();

        services.AddArcanumGrimoireForCli();

        services.AddArcanumBackup();

        return services;
    }

    public static IServiceCollection AddArcanumInstallationReset(
        this IServiceCollection services,
        ArcanumSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        services.TryAddScoped<InstallationResetExistingGrimoire>(provider =>
            new InstallationResetExistingGrimoire(
                provider.GetRequiredService<DataProtectionSecretStore>(),
                settings,
                provider.GetService<TimeProvider>() ?? TimeProvider.System,
                provider.GetService<ILoggerFactory>()
                    ?? LoggerFactory.Create(static _ => { }),
                provider.GetRequiredService<IGrimoireOrdinaryConnectionFactory>()));

        services.TryAddScoped<IInstallationResetDataService>(provider =>
            provider.GetRequiredService<InstallationResetExistingGrimoire>());

        services.TryAddScoped<IInstallationResetWorkspaceResolver>(provider =>
            provider.GetRequiredService<InstallationResetExistingGrimoire>());

        services.TryAddScoped<InstallationResetActiveStore>(provider =>
            new InstallationResetActiveStore(
                ArcanumPaths.GrimoireDirectory,
                provider.GetRequiredService<IOsCredentialStore>()));

        services.TryAddScoped<IInstallationResetActiveStore>(provider =>
            provider.GetRequiredService<InstallationResetActiveStore>());

        services.TryAddScoped<IInstallationResetDatabaseIdentityReader>(provider =>
            provider.GetRequiredService<InstallationResetExistingGrimoire>());

        services.TryAddScoped<
            IInstallationResetHostProcessToolsDatabaseEvidenceReader>(provider =>
            provider.GetRequiredService<InstallationResetExistingGrimoire>());

        services.TryAddScoped<
            IInstallationResetHostProcessToolsPairReader,
            InstallationResetHostProcessToolsPairReader>();

        services.TryAddScoped<IInstallationResetCredentialService>(provider =>
            new InstallationResetCredentialCatalog(
                provider.GetRequiredService<IOsCredentialStore>(),
                settings,
                ArcanumPaths.SecretStoreDirectory));

        services.TryAddScoped<IInstallationResetOfflineCleanup, InstallationResetOfflineCleanup>();

        services.TryAddScoped<IInstallationResetStateRoots>(static _ =>
            InstallationResetStateRoots.Default);

        services.TryAddScoped<IInstallationResetPreDataMutation, InstallationResetDaemonMutation>();

        services.TryAddSingleton<
            IFullInstallationResetRemediationTrustRootProvider,
            FullInstallationResetRemediationTrustRootAdapter>();

        services.TryAddSingleton<IFullInstallationResetRemediationAttestationVerifier>(provider =>
            new FullInstallationResetRemediationAttestationVerifier(
                provider.GetRequiredService<
                    IFullInstallationResetRemediationTrustRootProvider>(),
                provider.GetService<TimeProvider>() ?? TimeProvider.System));

        // The marker-pair reset boundary, one production implementation per port. Registered here
        // rather than beside the Covenant tier because this is the only composition that has a
        // full-reset entry at all, and a port with two implementations is a coordinator that can be
        // handed a second opinion about which marker it is deleting.
        //
        // The gate is a singleton because it is the process-wide exclusion the taint transition and
        // this reset share; two instances exclude nothing from each other. The operating-system
        // adapter is a singleton for the same structural reason: it refuses a capability minted by
        // any other instance, so a second adapter would be a second authority over the same slot.
        services.TryAddSingleton<HostProcessToolsMarkerMutationGate>();

        services.TryAddSingleton<IHostToolsMarkerPairResetOsPort>(provider =>
            new HostProcessToolsMarkerResetAdapter(
                new HostProcessToolsMarkerCredentialCapabilitySource(),
                provider.GetRequiredService<HostProcessToolsMarkerMutationGate>()));

        services.TryAddSingleton<IHostToolsMarkerPairResetDatabase>(provider =>
            new HostToolsMarkerPairResetDatabase(
                provider.GetRequiredService<ICovenantMaintenanceConnectionFactory>(),
                provider.GetRequiredService<ICovenantSqliteConnectionInitializer>()));

        services.TryAddSingleton<IFullInstallationResetCampaignSchemaReadiness>(provider =>
            new FullInstallationResetCampaignSchemaReadiness(
                provider.GetRequiredService<GrimoireSchemaManifestInspector>()));

        // Scoped, because the active store and the Campaign marker lifecycle it authenticates
        // against are scoped: a singleton coordinator would outlive the connection its lifecycle
        // writes through and keep an authority bound to a journal nobody can still read.
        services.TryAddScoped<IHostToolsMarkerPairResetCoordinator>(provider =>
            new HostToolsMarkerPairResetCoordinator(
                provider.GetRequiredService<IInstallationResetActiveStore>(),
                provider.GetRequiredService<IHostToolsMarkerPairResetDatabase>(),
                provider.GetRequiredService<IFullInstallationResetCampaignSchemaReadiness>(),
                provider.GetRequiredService<IHostProcessToolsMarkerPairJoiner>(),
                provider.GetRequiredService<
                    IFullInstallationResetRemediationAttestationVerifier>(),
                provider.GetRequiredService<ICampaignPathMarkerLifecycle>(),
                provider.GetRequiredService<IHostToolsMarkerPairResetOsPort>(),
                provider.GetRequiredService<IFullInstallationResetManagedFileReconciler>()));

        // Scoped for the same reason the coordinator is: the active store it authenticates against and
        // the erasure kernel it routes through are scoped, and a singleton would outlive the connection
        // both of them write through.
        services.TryAddScoped<IFullInstallationResetManagedFileReconciler>(provider =>
            new FullInstallationResetManagedFileReconciler(
                provider.GetRequiredService<IInstallationResetActiveStore>(),
                provider.GetRequiredService<CovenantManagedFileErasureKernel>(),
                provider.GetRequiredService<ManagedFileWriteIntentRecoveryService>()));

        services.TryAddScoped(provider =>
            new InstallationResetRestoreCredentialCleanup(
                provider.GetRequiredService<IOsCredentialStore>()));

        // Scoped alongside the active store it authenticates against. It is handed the database path
        // rather than deriving one, because the whole point of the step is to observe that exact file
        // is gone before the last restore credentials are removed.
        services.TryAddScoped<IFullInstallationResetTerminalContinuation>(provider =>
            new FullInstallationResetTerminalContinuation(
                provider.GetRequiredService<IInstallationResetActiveStore>(),
                new Backup.BackupRestoreJournalAnchorStore(
                    provider.GetRequiredService<IOsCredentialStore>(),
                    new Backup.BackupRestoreJournalKeyProvider(
                        provider.GetRequiredService<IOsCredentialStore>()),
                    new Backup.BackupRestoreJournalInstallationIdentityProvider(
                        provider.GetRequiredService<IOsCredentialStore>())),
                provider.GetRequiredService<InstallationResetRestoreCredentialCleanup>(),
                provider.GetRequiredService<IOsCredentialStore>(),
                ArcanumPaths.GrimoireDatabaseFile));

        // A deferred resolution for the same reason the marker-pair coordinator is one: planning and
        // the ordinary reset paths must keep working on an installation this graph cannot serve.
        services.TryAddScoped<Func<IFullInstallationResetTerminalContinuation>>(provider =>
            provider.GetRequiredService<IFullInstallationResetTerminalContinuation>);

        // A deferred resolution, not a constructor dependency. The reset service must resolve and
        // plan on an installation whose Grimoire is absent or locked, and the coordinator's graph
        // reaches the encrypted database; binding it eagerly would make every restricted path
        // require what only the full-reset path actually needs.
        services.TryAddScoped<Func<IHostToolsMarkerPairResetCoordinator>>(provider =>
            provider.GetRequiredService<IHostToolsMarkerPairResetCoordinator>);

        services.TryAddScoped(static _ =>
            new InstallationResetControlPaths(ArcanumPaths.GrimoireDirectory));

        services.TryAddScoped<InstallationResetService>();

        services.TryAddScoped<IInstallationResetService>(provider =>
            provider.GetRequiredService<InstallationResetService>());

        services.TryAddScoped<IInstallationResetOnlineDataHandoff>(provider =>
            provider.GetRequiredService<InstallationResetService>());

        services.TryAddScoped<IInstallationResetLockedService>(provider =>
            provider.GetRequiredService<InstallationResetService>());

        return services;
    }

    /// <summary>
    /// Registers the host-coordinated encrypted backup planner, snapshotter, archive codec, and service.
    /// </summary>
    public static IServiceCollection AddArcanumBackup(this IServiceCollection services)
    {

        services.AddArcanumClientMutationCoordination();

        services.TryAddSingleton(BackupStatePaths.Default);

        services.TryAddSingleton<BackupInventoryPlanner>();

        services.TryAddSingleton<BackupDatabaseSnapshotter>();

        services.TryAddSingleton<BackupArchiveCodec>(
            static _ => new BackupArchiveCodec());

        services.TryAddSingleton<IBackupSecretSnapshotReader>(serviceProvider =>
            new BackupSecretSnapshotReader(
                serviceProvider.GetRequiredService<IOsCredentialStore>(),
                serviceProvider.GetRequiredService<DataProtectionSecretStore>()));

        services.TryAddScoped<IBackupService>(serviceProvider =>
            new BackupService(
                serviceProvider.GetRequiredService<BackupStatePaths>(),
                serviceProvider.GetRequiredService<BackupInventoryPlanner>(),
                serviceProvider.GetRequiredService<BackupDatabaseSnapshotter>(),
                serviceProvider.GetRequiredService<BackupArchiveCodec>(),
                serviceProvider.GetRequiredService<IBackupSecretSnapshotReader>(),
                serviceProvider.GetRequiredService<TimeProvider>(),
                serviceProvider.GetRequiredService<IGrimoireDbPassphraseSource>(),
                new DeferredBackupOperationCoordinator(serviceProvider),
                new DeferredBackupOperationStore(serviceProvider),
                ResolveCovenantBackupServices(serviceProvider)));

        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                ILongRunningOperationRecoveryHandler,
                BackupCreateRecoveryHandler>());

        services.TryAddScoped<IBackupRestoreService>(serviceProvider =>
            new BackupRestoreService(
                serviceProvider.GetRequiredService<BackupStatePaths>(),
                serviceProvider.GetRequiredService<BackupArchiveCodec>(),
                serviceProvider.GetRequiredService<ISecretStore>(),
                serviceProvider.GetRequiredService<IBackupService>,
                serviceProvider.GetRequiredService<TimeProvider>(),
                serviceProvider.GetRequiredService<GrimoireSchemaInstaller>(),
                new BackupRestoreServiceOptions
                {

                    EmbeddingDimensions = ArcanumSettingClamps.EmbeddingsDimensions(
                        serviceProvider
                            .GetService<IOptionsMonitor<ArcanumSettings>>()?
                            .CurrentValue.ResolveEmbeddings().Dimensions
                        ?? new EmbeddingSettings().Dimensions),

                    // Present only while the gate is on. Absent is not a degraded protected import,
                    // it is the pre-Covenant plaintext path that installations without the feature
                    // have always used.
                    SelectiveImport = ResolveSelectiveImport(serviceProvider),

                    // Same rule for the full-restore arm: absent means this installation has never
                    // held protected state, so there is nothing to reconcile and nothing to close.
                    RestoreStaging = ResolveRestoreStaging(serviceProvider),

                },
                serviceProvider.GetRequiredService<InstallationMaintenanceCoordination>()));

        return services;

    }

    /// <summary>
    /// Resolves the disclosure and lease capabilities a protected physical backup runs under.
    /// </summary>
    /// <remarks>
    /// Null when the gate is off, which is the pre-Covenant backup this installation has always
    /// taken. A partially resolved set is deliberately not possible: a disclosure boundary without a
    /// lease would account for a read nothing fenced.
    /// </remarks>
    private static CovenantBackupServices? ResolveCovenantBackupServices(IServiceProvider serviceProvider)
    {

        bool enabled = serviceProvider
            .GetService<IOptionsMonitor<ArcanumSettings>>()?
            .CurrentValue.Features.Covenant
            ?? false;

        if (!enabled)
        {

            return null;

        }

        ICovenantOperationGate? gate = serviceProvider.GetService<ICovenantOperationGate>();

        ICovenantDisclosureJournal? journal = serviceProvider.GetService<ICovenantDisclosureJournal>();

        return gate is null || journal is null
            ? null
            : new CovenantBackupServices(
                gate,
                new CovenantBackupDisclosureBoundary(
                    journal,
                    () => Guid.TryParse(
                        serviceProvider
                            .GetRequiredService<CovenantAuthoritySnapshotProvider>()
                            .Current?.InstallationIdentity,
                        out Guid installationId)
                        ? installationId
                        : Guid.Empty,
                    serviceProvider.GetRequiredService<TimeProvider>()));

    }

    /// <summary>
    /// Resolves the protected selective-import path, or reports that this installation has none.
    /// </summary>
    /// <remarks>
    /// Null is a real answer rather than a failure. With <c>Arcanum:Features:Covenant</c> off there is
    /// no operation gate to drain against and no protected state to fence, and a restore that demanded
    /// them anyway would refuse an import that has always been allowed.
    /// </remarks>
    private static CovenantSelectiveImportServices? ResolveSelectiveImport(IServiceProvider serviceProvider)
    {

        bool enabled = serviceProvider
            .GetService<IOptionsMonitor<ArcanumSettings>>()?
            .CurrentValue.Features.Covenant
            ?? false;

        if (!enabled)
        {

            return null;

        }

        ICovenantOperationGate? gate = serviceProvider.GetService<ICovenantOperationGate>();

        return gate is null
            ? null
            : new CovenantSelectiveImportServices(
                gate,
                new ProtectedArtifactTransferStore(
                    CovenantSqliteConnectionInitializer.Instance,
                    serviceProvider.GetRequiredService<TimeProvider>()));

    }

    /// <summary>
    /// Resolves the staged protected-state reconciliation path for a full restore.
    /// </summary>
    /// <remarks>
    /// All five capabilities or none. A restore that held the gate but had no marker lifecycle would
    /// close admission and then be unable to prove what it owed the Campaign roots it displaced, and a
    /// restore that could publish an authenticated journal without an owner to put in it would name an
    /// operation nothing holds. The anchor store and the identity provider are constructed here rather
    /// than registered, for the same reason the startup recovery composes its own: the credential
    /// accounts they own are namespaced to one profile root, and a container-wide singleton bound to
    /// <c>ArcanumPaths</c> could not honour a bootstrap that was handed a different one.
    /// </remarks>
    private static CovenantRestoreStagingServices? ResolveRestoreStaging(IServiceProvider serviceProvider)
    {

        bool enabled = serviceProvider
            .GetService<IOptionsMonitor<ArcanumSettings>>()?
            .CurrentValue.Features.Covenant
            ?? false;

        if (!enabled)
        {

            return null;

        }

        ICovenantOperationGate? gate = serviceProvider.GetService<ICovenantOperationGate>();

        ICampaignPathMarkerLifecycle? markers = serviceProvider.GetService<ICampaignPathMarkerLifecycle>();

        IOsCredentialStore? credentials = serviceProvider.GetService<IOsCredentialStore>();

        if (gate is null || markers is null || credentials is null)
        {

            return null;

        }

        BackupRestoreJournalInstallationIdentityProvider identities = new(credentials);

        BackupRestoreJournalKeyProvider keys = new(credentials);

        return new CovenantRestoreStagingServices(
            gate,
            markers,
            new BackupRestoreJournalAnchorStore(credentials, keys, identities),
            identities,
            keys,
            new BackupRestoreEffectDigestCalculator());

    }

    /// <summary>
    /// Registers daemon job registry, execution history, runner, config-backed <see cref="IDaemonJob"/> instances, and the Unseen Servant scheduler.
    /// </summary>
    public static IServiceCollection AddArcanumDaemonServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<InMemoryDaemonExecutionRepository>();

        services.AddSingleton<IDaemonExecutionRepository>(static services =>
            services.GetRequiredService<InMemoryDaemonExecutionRepository>());

        services.AddSingleton<IDaemonExecutionMutationGate>(static services =>
            services.GetRequiredService<InMemoryDaemonExecutionRepository>());

        services.AddSingleton<DaemonJobRegistry>();

        services.AddSingleton<IDaemonRegistry>(static sp => sp.GetRequiredService<DaemonJobRegistry>());

        services.AddSingleton<IDaemonRunner, DaemonRunner>();

        services.AddSingleton<UnseenServantJobTracker>();

        services.AddSingleton<IUnseenServantJobTracker>(static sp => sp.GetRequiredService<UnseenServantJobTracker>());

        List<UnseenServantJob> jobs =
            ConfigurationBootstrapper.LoadArcanumSettings(
                () => configuration.GetSection("Arcanum").Get<ArcanumSettings>()
                    ?? new ArcanumSettings()).Daemon.Jobs;

        foreach (UnseenServantJob job in jobs)
        {
            UnseenServantJob captured = job;

            services.AddSingleton<IDaemonJob>(sp => new UnseenServantDaemonJob(captured, sp));
        }

        services.AddInstallationResetRecoveryAwareHostedService<UnseenServantService>();

        return services;
    }

    /// <summary>
    /// Registers Serilog file logging, Data Protection, the secret store, encrypted Grimoire database, and workspace scanning.
    /// </summary>
    public static IServiceCollection AddArcanumInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArcanumSettings settingsSnapshot =
            ConfigurationBootstrapper.LoadArcanumSettings(
                () => configuration.GetSection("Arcanum").Get<ArcanumSettings>()
                    ?? new ArcanumSettings());
        services.Configure<ArcanumSettings>(settings =>
            ConfigurationBootstrapper.CopySettings(settingsSnapshot, settings));

        services.AddSingleton<ConfigurationWriter>();

        services.AddSingleton<IDataRetentionPolicyStore, DataRetentionPolicyStore>();

        services.AddSingleton<WorkspaceCheckCapabilityReporter>();

        services.AddSingleton<IWorkspaceCheckCapabilityReporter>(
            static sp => sp.GetRequiredService<WorkspaceCheckCapabilityReporter>());

        services.AddSingleton<IWorkspaceCheckAdvertisementEligibility>(
            static sp => sp.GetRequiredService<WorkspaceCheckCapabilityReporter>());

        services.AddSingleton<ConfigurationValidator>();

        services.AddArcanumEyeOfTheWorld();

        services.AddSingleton<InMemoryLogRingBuffer>();

        services.AddSingleton<ILogRingBuffer>(static sp => sp.GetRequiredService<InMemoryLogRingBuffer>());

        services.AddSingleton<ILogQueryService, LogQueryService>();

        services.AddSingleton<IDaemonLogAttacher, DaemonLogAttacher>();

        services.AddSingleton<SerilogLogRingBufferSink>();

        // Register the ring-buffer sink before AddArcanumSerilog so deferred first-emit resolution succeeds.
        services.AddArcanumSerilog();

        services.AddSingleton<ManagedLogMutationGate>();

        services.AddSingleton<IManagedLogMutationGate>(static services =>
            services.GetRequiredService<ManagedLogMutationGate>());

        services.AddSingleton<IInferenceAuditLogger>(static services =>
            new InferenceAuditLogger(
                services.GetRequiredService<IOptionsMonitor<ArcanumSettings>>(),
                services.GetRequiredService<ILogger<InferenceAuditLogger>>(),
                filePathOverride: null,
                managedLogMutationGate:
                    services.GetRequiredService<IManagedLogMutationGate>()));

        services.AddSingleton<IGuardrailAuditLogger>(static services =>
            new GuardrailAuditLogger(
                services.GetRequiredService<IOptionsMonitor<ArcanumSettings>>(),
                services.GetRequiredService<ILogger<GuardrailAuditLogger>>(),
                filePathOverride: null,
                managedLogMutationGate:
                    services.GetRequiredService<IManagedLogMutationGate>()));

        services.AddDataProtection()
            .SetApplicationName("ArcanumCore")
            .PersistKeysToFileSystem(new DirectoryInfo(DataProtectionKeyPaths.Directory));

        services.AddSingleton<IApiKeyDigestCache, ApiKeyDigestCache>();

        services.AddArcanumSecretStore();

        services.AddArcanumBackup();

        services.AddSingleton<IWebResearchApiKeyResolver, WebResearchApiKeyResolver>();

        services.AddSingleton<WebPageContentExtractor>();

        services.AddSingleton<IWebResearchProvider, PerplexityWebProvider>();

        services.AddSingleton<IWebResearchProvider, LocalHttpWebProvider>();

        services.AddSingleton<IWebResearchProviderCatalog, WebResearchProviderCatalog>();

        services.AddHttpClient(
                WebResearchConstants.PerplexityHttpClientName,
                static client => client.Timeout = Timeout.InfiniteTimeSpan)
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(static () =>
            {
                SocketsHttpHandler handler =
                    OutboundUrlGuard.CreateUntrustedEgressHandler();
                handler.AutomaticDecompression =
                    global::System.Net.DecompressionMethods.All;
                return handler;
            });

        services.AddHttpClient(
                WebResearchConstants.LocalHttpClientName,
                static client => client.Timeout = Timeout.InfiniteTimeSpan)
            // Direct-read URLs can contain secret query data. Provider-owned logs
            // intentionally report only bounded outcome codes and never raw URLs.
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(static () =>
            {
                SocketsHttpHandler handler =
                    OutboundUrlGuard.CreateUntrustedEgressHandler();
                handler.AutomaticDecompression =
                    global::System.Net.DecompressionMethods.All;
                return handler;
            });

        services.AddSingleton<IWard, WardGate>();
        services.AddScoped<ISanctumGuard, SanctumGuard>();
        services.AddSingleton<IProcessResourceLimiter, ProcessResourceLimiter>();
        services.AddSingleton<IGrimoireDbPassphraseSource, GrimoireDbPassphraseSource>();
        services.AddSingleton<IGrimoireDbReadiness, GrimoireDbReadiness>();
        services.AddSingleton<WeaveIndexAvailability>();

        services.AddSingleton<ICovenantSqliteConnectionInitializer>(
            static _ => CovenantSqliteConnectionInitializer.Instance);

        services.AddSingleton<IGrimoireSchemaDataInitializer, CoreGrimoireSchemaDataInitializer>();

        services.AddSingleton<IGrimoireSchemaDataInitializer, CovenantCanonicalSchemaDataInitializer>();

        services.AddSingleton<IGrimoireSchemaDataInitializer, CovenantAcceleratorSchemaDataInitializer>();

        services.AddSingleton<GrimoireSchemaDataInitializers>();

        services.AddSingleton(static _ => GrimoireSchemaTierOwnershipRegistry.CreateDefault());

        services.AddSingleton<GrimoireSchemaManifestInspector>();

        // The chains say which versions each tier has had and how to reach the one this binary
        // declares. Injected rather than read statically so the installer has exactly one source of
        // that answer, and so a suite can drive a longer chain through the same entry point.
        services.AddSingleton(static _ => GrimoireSchemaVersionChains.Default);

        services.AddSingleton<GrimoireSchemaInstaller>();

        services.AddSingleton<GrimoireSchemaBackfillRunner>();

        services.AddScoped<GrimoireSchemaTransitionCoordinator>();

        // One table for the whole host: a Covenant capability is registered by the API pipeline and
        // taken by the in-process MCP server, and those two run on different tasks.
        services.AddSingleton<CovenantToolCapabilityRegistry>();

        // The one bridge from Arcanum:Features:Covenant to the in-memory gate every Covenant path
        // reads. Registered as a singleton and started as a hosted service so the same instance owns
        // the subscription it later disposes; two instances would leave one publishing after
        // shutdown, and a disable is the one publication that must not be lost.
        services.AddSingleton<CovenantFeatureConfigurationPublisher>();

        services.AddCovenantAuthority();

        services.AddCampaignPathIdentity();

        services.AddCovenantPersistence();

        services.AddScoped<IDivinationService, DivinationService>();
        services.AddScoped(
            static sp => new EmbeddingsResetService(
                sp.GetRequiredService<ArcanumDbContext>(),
                sp.GetRequiredService<WeaveIndexAvailability>(),
                sp,
                sp.GetRequiredService<ICovenantSensitiveArtifactPurger>()));
        services.AddScoped<ITapestryStore, TapestryStore>();
        services.AddScoped<SessionAttachmentIndexRepository>();
        services.AddScoped<ISessionAttachmentIndexMaintenance>(
            static sp => sp.GetRequiredService<SessionAttachmentIndexRepository>());
        services.AddScoped<SessionAttachmentIndexProcessor>();
        services.AddScoped<ISessionAttachmentRetrievalService, SessionAttachmentRetrievalService>();
        // Phase 7 — read-only RAG / The Weave inspector over the existing workspace chunk tables. Scoped
        // because it depends on the scoped ArcanumDbContext; never triggers indexing or mutates state.
        services.AddScoped<IWorkspaceIndexInspectorService, WorkspaceIndexInspectorService>();
        services.AddSingleton<SpellWeaveCache>();
        services.AddSingleton<LongRunningOperationReconciliationStatus>();

        services.AddSingleton<InstallationResetMaintenanceLockAccessor>();

        services.AddSingleton<InstallationResetApiAdmission>();

        services.AddSingleton<IInstallationResetMaintenanceLockAccessor>(
            static sp => sp.GetRequiredService<InstallationResetMaintenanceLockAccessor>());

        services.TryAddScoped(
            static sp => new InstallationResetActiveStore(
                ArcanumPaths.GrimoireDirectory,
                sp.GetRequiredService<IOsCredentialStore>()));

        services.AddScoped<IInstallationResetDatabaseIdentityReader,
            InstallationResetDatabaseIdentityReader>();

        services.AddScoped<IInstallationResetHostHandoffCoordinator,
            InstallationResetHostHandoffCoordinator>();

        services.AddSingleton<IInstallationResetStartupRecovery>(
            static sp => new InstallationResetStartupRecovery(
                ArcanumPaths.GrimoireDirectory,
                new InstallationResetActiveStore(
                    ArcanumPaths.GrimoireDirectory,
                    sp.GetRequiredService<IOsCredentialStore>())));

        services.AddSingleton(
            static sp => new GrimoireDatabaseHostedService(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<ISecretStore>(),
                sp.GetRequiredService<IGrimoireDbPassphraseSource>(),
                ArcanumPaths.GrimoireDirectory,
                sp.GetRequiredService<InstallationResetMaintenanceLockAccessor>(),
                sp.GetRequiredService<IInstallationResetStartupRecovery>(),
                sp.GetRequiredService<HostLockSerilogFileSink>(),
                ArcanumMasterKeyBootstrapper.EnsureMasterApiKeyExistsAsync,
                sp.GetRequiredService<InstallationResetApiAdmission>(),
                startupCoordination:
                    sp.GetRequiredService<InstallationMaintenanceCoordination>()));

        services.AddHostedService(
            static sp => sp.GetRequiredService<GrimoireDatabaseHostedService>());

        // Keep the singleton resolvable above for snapshot composition, but start its subscription
        // only after the lock-first host has authenticated the active installation identity and
        // published the converged schema. A configured true value must never become visible from a
        // database whose expected identity has not passed verification.
        services.AddHostedService(
            static sp => sp.GetRequiredService<CovenantFeatureConfigurationPublisher>());

        services.AddHostedService<PidFileService>();

        // Must run after the encrypted Grimoire is migrated and before durable workloads below
        // begin accepting potentially conflicting work.
        services.AddInstallationResetRecoveryAwareHostedService<LongRunningOperationStartupHostedService>();

        // One-shot pending attachment GC; runs after Grimoire schema bootstrap above.
        services.AddInstallationResetRecoveryAwareHostedService<SessionAttachmentPendingGcHostedService>();

        // Reset-aware rather than plain, so a pass cannot open a transaction against a dataset the
        // installation is in the middle of replacing. Registered on the server host alone: the CLI
        // composition is short-lived, and a sweep that started with a command and died with it would
        // drain a backlog only for whoever happened to run one.
        services.AddInstallationResetRecoveryAwareHostedService<CovenantMaintenanceHostedService>();

        // Journal-gated rather than availability-gated, which is the opposite of the sweep above and
        // deliberately so. A tier with a version run in flight is not healthy by design, so a driver
        // that waited for health could never run the sweep that restores it; for Core, whose run
        // stands its dependents down, that would leave the installation unrepairable by the only
        // process able to repair it.
        services.AddInstallationResetRecoveryAwareHostedService<GrimoireSchemaTransitionHostedService>();

        // RAG Phase 2/3 — Entry Weaving and Workspace Indexing both idle (no-op) until
        // Arcanum:Features:SessionSearch / CodebaseRetrieval are enabled, so registering them
        // unconditionally is safe on the hot path. Registered after
        // GrimoireDatabaseHostedService so the Grimoire (and The Weave's schema) is guaranteed ready
        // before either service's first tick can run any query.
        services.AddInstallationResetRecoveryAwareHostedService<EntryWeavingService>();

        services.AddSingleton<SessionAttachmentIndexingService>();
        services.AddSingleton<ISessionAttachmentIndexQueue>(
            static sp => sp.GetRequiredService<SessionAttachmentIndexingService>());
        services.AddInstallationResetRecoveryAwareHostedService<SessionAttachmentIndexingService>();

        services.AddSingleton<IWorkspaceFileWatcherFactory, WorkspaceFileWatcherFactory>();
        services.AddSingleton<WorkspaceIndexingService>();
        services.AddSingleton<IWorkspaceIndexingService>(static sp => sp.GetRequiredService<WorkspaceIndexingService>());
        services.AddSingleton<IWorkspaceIndexRuntimeStatusProvider>(static sp => sp.GetRequiredService<WorkspaceIndexingService>());
        services.AddInstallationResetRecoveryAwareHostedService<WorkspaceIndexingService>();

        // RAG Phase 4 — Saga extraction is event-driven (enqueued by WizardIntelligenceProvider after a
        // successful turn), not polling, so registering it unconditionally is safe on the hot path.
        // Registered as a singleton (not just a hosted service) so the hub can resolve it directly to
        // call EnqueueExtraction, mirroring WorkspaceIndexingService's singleton+hosted-factory pattern.
        services.AddSingleton<SagaExtractionService>();
        services.AddInstallationResetRecoveryAwareHostedService<SagaExtractionService>();

        // The Tapestry (§21.11) — idles unless Arcanum:Features:Tapestry is enabled, so registering it
        // unconditionally is free on the hot path. Registered after GrimoireDatabaseHostedService so
        // The Weave's schema is guaranteed ready before the first sweep.
        services.AddScoped<ITapestrySummarizer, TapestrySummarizer>();
        services.AddScoped<TapestryWeaver>();
        services.AddInstallationResetRecoveryAwareHostedService<TapestryWeavingService>();

        services.AddInstallationResetRecoveryAwareHostedService<ArcanumSettingsClampStartupLogger>();

        services.AddInstallationResetRecoveryAwareHostedService<ArcanumSecurityStartupChecks>();

        services.AddInstallationResetRecoveryAwareHostedService<FileEncryptionKeyBootstrapHostedService>();

        // Options must be fully configured here — pooled contexts reject OnConfiguring mutations
        // (including AddInterceptors). Passphrase is set by GrimoireDatabaseHostedService before
        // the first request resolves a context from the pool.
        services.AddDbContextPool<ArcanumDbContext>(
            (sp, options) => ArcanumDbContextOptionsConfigurator.Configure(
                options,
                sp.GetRequiredService<IGrimoireDbPassphraseSource>(),
                sp.GetRequiredService<IGrimoireOrdinaryConnectionLifecycle>(),
                sp.GetRequiredService<ICovenantConnectionDrain>(),
                sp.GetRequiredService<ICovenantSqliteConnectionInitializer>()),
            poolSize: 32);

        services.AddScoped<IUnseenServantWatermarkStore, UnseenServantWatermarkStore>();

        services.AddScoped<IIdempotencyStore, IdempotencyStore>();

        services.AddScoped<IIdempotencyClaimStore, IdempotencyClaimStore>();

        services.AddScoped<ITurnRunWriter, TurnRunWriter>();

        services.AddScoped<IBudgetReservationService, BudgetReservationService>();

        services.AddScoped<ILongRunningOperationStore, LongRunningOperationStore>();

        services.AddScoped<ILongRunningOperationCoordinator, LongRunningOperationCoordinator>();

        services.AddScoped<DataRetentionService>();

        services.AddScoped<IDataRetentionService>(
            static provider => provider.GetRequiredService<DataRetentionService>());

        services.AddScoped<ILongRunningOperationRecoveryHandler, DataRetentionRecoveryHandler>();

        services.AddScoped<ILongRunningOperationRecoveryHandler, DataRetentionMutationRecoveryHandler>();

        services.AddScoped<ILongRunningOperationRecoveryHandler, DataRetentionFactoryResetRecoveryHandler>();

        // One host-only producer of a Covenant erasure's effect digest, and one seam that makes
        // exclusive-gate acquisition unreachable before the InventoryPrepared checkpoint commits.
        // The shared production coordinator consumes the resulting checkpoint on both the direct
        // reset route and durable recovery.
        services.AddSingleton<ICovenantErasureEffectDigestCalculator, CovenantErasureEffectDigestCalculator>();

        services.AddSingleton<
            ICovenantFactoryErasureApplyRequestDigestCalculator,
            CovenantFactoryErasureApplyRequestDigestCalculator>();

        services.AddScoped<CovenantResetCheckpointInitiator>();

        services.AddInstallationResetRecoveryAwareHostedService<DataRetentionSweepHostedService>();

        services.AddScoped<LongRunningOperationReconciler>();
        services.AddScoped<IDurableOperationDiagnostics, DurableOperationDiagnostics>();
        services.AddScoped<ILongRunningOperationRecoveryHandler, BudgetReservationRecoveryHandler>();

        // Issue #40: every kind in LongRunningOperationRecoveryRegistry owns a handler, so a stranded
        // operation reaches explicit recovery instead of falling through to "handler missing".
        services.AddScoped<ILongRunningOperationRecoveryHandler, InferenceRunRecoveryHandler>();
        services.AddScoped<ILongRunningOperationRecoveryHandler, SubagentRecoveryHandler>();
        services.AddScoped<ILongRunningOperationRecoveryHandler, IdempotencyClaimRecoveryHandler>();
        services.AddScoped<ILongRunningOperationRecoveryHandler, ApprenticeRecoveryHandler>();
        services.AddScoped<ILongRunningOperationRecoveryHandler, AttachmentPromotionRecoveryHandler>();
        services.AddScoped<ILongRunningOperationRecoveryHandler, WorkspaceIndexRecoveryHandler>();

        services.AddScoped<IBlobEncryptionMetadataStore, BlobEncryptionMetadataStore>();
        services.AddScoped<BlobEncryptionFileProcessor>();
        services.AddScoped<BlobEncryptionLifecycleService>();
        services.AddScoped<IBlobEncryptionLifecycleService>(
            static sp => sp.GetRequiredService<BlobEncryptionLifecycleService>());
        services.AddScoped<ILongRunningOperationRecoveryHandler, BlobEncryptionMigrationRecoveryHandler>();
        services.AddScoped<ILongRunningOperationRecoveryHandler, BlobEncryptionKeyRotationRecoveryHandler>();

        services.AddScoped<IUploadedFileRepository, UploadedFileRepository>();

        services.AddScoped<ISessionAttachmentStore, SessionAttachmentStore>();
        services.AddScoped<ISessionContextPinStore, SessionContextPinStore>();
        services.AddScoped<IAttachmentSourceResolver, AttachmentSourceResolver>();

        services.AddScoped<IBatchRepository, BatchRepository>();

        services.AddScoped<ISanctumBreachRepository, SanctumBreachRepository>();

        services.AddScoped<IBudgetAlertRepository, BudgetAlertRepository>();

        services.AddScoped<ISagaMemoryStore, SagaMemoryStore>();

        // Same lifetime as ISagaMemoryStore: this wraps that store's DbContext-backed calls directly,
        // and a service scoped any looser would hold that DbContext across a boundary the store itself
        // does not.
        services.AddScoped<ISagaCurationService, SagaCurationService>();

        services.AddScoped<IAttachmentMemoryProvenanceStore, AttachmentMemoryProvenanceStore>();

        services.AddScoped<ILexiconService, LexiconService>();

        services.AddScoped<IAnnalsStore, AnnalsStore>();

        // One owner for the Campaign-scoped-memory gate, so retrieval and every inspection surface
        // cannot disagree about which scope a turn draws from.
        services.AddScoped<IMemoryScopeResolver, MemoryScopeResolver>();

        services.AddSingleton(TimeProvider.System);
        // An explicit factory rather than a type registration: the Covenant mutation kernel is
        // internal, so the composed constructor cannot be reached by a reflective activator.
        services.AddScoped<IGrimoireRepository>(
            static sp => new GrimoireRepository(
                sp.GetRequiredService<ArcanumDbContext>(),
                sp.GetRequiredService<ISessionAttachmentStore>(),
                sp.GetRequiredService<ILogger<GrimoireRepository>>(),
                sp.GetRequiredService<IOptionsSnapshot<ArcanumSettings>>(),
                sp.GetService<ISessionAttachmentIndexMaintenance>(),
                sp.GetService<CovenantMutationKernel>(),
                sp.GetRequiredService<IGrimoireOrdinaryConnectionFactory>()));

        // The narrow turn-begin port is deliberately a separate registration over the same scoped
        // instance. Resolving it through IGrimoireRepository would let any holder of the broad
        // interface reach Campaign-binding writes it has no business performing (§10.12).
        services.AddScoped<ISessionTurnBeginStore>(
            static sp => (GrimoireRepository)sp.GetRequiredService<IGrimoireRepository>());

        // The batch-aware finalizer is the same scoped instance again, exposed as the narrow
        // publication port so a caller cannot reach entry writes through it (§10.13).
        services.AddScoped<IGrimoireTurnCommitter>(
            static sp => (GrimoireRepository)sp.GetRequiredService<IGrimoireRepository>());
        services.AddScoped<ICampaignRepository, CampaignRepository>();
        services.AddScoped<IPromptRepository, PromptRepository>();
        services.AddScoped<IApprenticeRepository, ApprenticeRepository>();
        services.AddScoped<IConclaveArchmage, ConclaveArchmage>();
        // A Sending blocks until the remote agent reaches a terminal state, and remote work can run far
        // longer than HttpClient's 100-second default. Per issue #55 the bound is on establishing the
        // connection, not on the operation as a whole; caller/host cancellation ends the work.
        services.AddHttpClient(
                A2AClientService.OutboundHttpClientName,
                static client => client.Timeout = Timeout.InfiniteTimeSpan)
            // A peer's agent-card URL can carry a token in a path segment, and default
            // IHttpClientFactory logging writes that URI at Information. Only A2AClientService's
            // own host-only diagnostics are permitted for this named client.
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(static () =>
                OutboundUrlGuard.CreateUntrustedEgressHandler(A2AClientService.OutboundConnectTimeout));
        services.AddSingleton<IA2AClientService, A2AClientService>();
        services.AddSingleton<ArcanumA2AAgentHandler>();
        // Durable record of in-flight A2A correspondences, so a restart can cancel an orphaned remote
        // task and a peer's tasks/cancel still reaches the real Apprentice (issue #62).
        services.AddScoped<IA2ASendingLedger, A2ASendingLedger>();
        // A Sending has no deadline, so its ledger lease has to be renewed while this process holds it.
        // Unrenewed, background reconciliation reclaimed every Sending that ran past 15 minutes and
        // cancelled the peer's task out from under the call that was still awaiting it.
        services.AddSingleton<A2ASendingLeaseRenewer>();
        services.AddInstallationResetRecoveryAwareHostedService<A2ASendingLeaseRenewer>();
        // Delegated spend is read from those same durable records, so the day's external cost survives
        // the process that spent it and is never confused with local spend (issue #69).
        services.AddScoped<IExternalSpendLedger, A2AExternalSpendLedger>();
        services.AddScoped<ILongRunningOperationRecoveryHandler, A2AInboundSendingRecoveryHandler>();
        services.AddScoped<ILongRunningOperationRecoveryHandler, A2AOutboundSendingRecoveryHandler>();
        // Push notifications, off unless an operator turns them on: inbound, a peer's callback is honoured
        // and validated like any other egress; outbound, a Sending can stop holding a concurrency slot
        // while the remote works (issue #67).
        services.AddSingleton<A2APushNotificationRegistry>();
        services.AddSingleton<A2APushNotificationDispatcher>();
        services.AddSingleton<A2ASendingCallbackRegistry>();
        // The SDK resolves the task before it ever calls the handler, so a purely in-memory store made
        // every post-restart continuation and peer cancel unreachable however durable the Apprentice
        // underneath was (issue #68).
        services.AddSingleton<A2AServer>(static sp => new ArcanumA2AServer(
            sp.GetRequiredService<ArcanumA2AAgentHandler>(),
            new ArcanumA2ATaskStore(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<ILogger<ArcanumA2ATaskStore>>()),
            new ChannelEventNotifier(),
            sp.GetRequiredService<A2APushNotificationRegistry>(),
            sp.GetRequiredService<ILogger<A2AServer>>(),
            new A2AServerOptions { AutoAppendHistory = true }));
        services.AddScoped<IChronosyncEngine, ChronosyncEngine>();
        services.AddSingleton<CampaignLoggerQueue>();
        services.AddSingleton<ICampaignLoggerQueue>(sp => sp.GetRequiredService<CampaignLoggerQueue>());
        services.AddInstallationResetRecoveryAwareHostedService<Loremaster>();
        services.AddSingleton<ChronicleHub>();
        services.AddSingleton<ApprenticeService>();
        services.AddSingleton<IApprenticeRuntime>(static sp => sp.GetRequiredService<ApprenticeService>());
        services.AddInstallationResetRecoveryAwareHostedService<ApprenticeService>();
        services.AddSingleton<IWorkspaceScanner, PhysicalWorkspaceScanner>();
        services.AddSingleton<IUnseenServantPacer, UnseenServantPacer>();
        services.AddSingleton<InMemoryEventBus>();
        services.AddSingleton<IEventBus>(static sp => sp.GetRequiredService<InMemoryEventBus>());
        services.AddSingleton<SseConnectionCounter>();
        services.AddSingleton<SseConnectionGate>();
        services.AddSingleton<PrometheusMetricsExporter>();

        services.AddHttpClient(
            WebhookCommLinkDispatcher.HttpClientName,
            (sp, client) =>
            {
                IOptionsMonitor<ArcanumSettings> opts = sp.GetRequiredService<IOptionsMonitor<ArcanumSettings>>();

                int timeoutSeconds = ArcanumSettingClamps.WebhookTimeoutSeconds(
                    opts.CurrentValue.ResolveCommLink().WebhookTimeoutSeconds);

                client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            })
            // Default IHttpClientFactory logging includes the request URI. CommLink URLs can carry
            // credentials in path/query data, so only WebhookCommLinkDispatcher's host-only logs
            // are permitted for this named client.
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(static () => OutboundUrlGuard.CreateUntrustedEgressHandler());

        services.AddHttpClient(
            ArcanumBrowseWebConstants.HttpClientName,
            // Infinite by design, not by omission: HttpClient.Timeout bounds the whole request and
            // would cut a slow but still-progressing download, so ArcanumBrowseWebTool imposes an
            // IDLE deadline of its own (WebBrowsingIdleTimeoutSeconds) around the read instead.
            static client => client.Timeout = Timeout.InfiniteTimeSpan)
            // Browsed URLs may carry credentials in path/query data, so suppress
            // IHttpClientFactory's URI logging; the tool logs the host only.
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(static () => OutboundUrlGuard.CreateUntrustedEgressHandler());

        services.AddSingleton<WebhookCommLinkDispatcher>();

        services.AddSingleton<ICommLinkDispatcher>(static sp =>
        {
            WebhookCommLinkDispatcher webhook = sp.GetRequiredService<WebhookCommLinkDispatcher>();

            IReadOnlyList<ICommLinkDispatcher> sinks = [webhook];

            ILogger<CommLinkMultiplexer> logger = sp.GetRequiredService<ILogger<CommLinkMultiplexer>>();

            return new CommLinkMultiplexer(sinks, logger);
        });

        services.AddSingleton<ITrustedMcpWorkspaceStore, TrustedMcpWorkspaceStore>();

        services.AddHttpClient(
            McpConnectionManager.McpHttpClientName,
            static client => client.Timeout = Timeout.InfiniteTimeSpan)
            // Hosted MCP endpoints commonly embed their bearer token in a path segment, which
            // .NET's URI redaction does not strip. Default IHttpClientFactory logging would copy
            // that token into the rolling log and the ring buffer behind GET /api/logs.
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                IOptionsMonitor<ArcanumSettings> opts = sp.GetRequiredService<IOptionsMonitor<ArcanumSettings>>();

                int connectTimeoutSeconds = ArcanumSettingClamps.McpHttpConnectTimeoutSeconds(
                    opts.CurrentValue.ResolveMcp().HttpConnectTimeoutSeconds);

                SocketsHttpHandler handler =
                    OutboundUrlGuard.CreateUntrustedEgressHandler();

                handler.ConnectTimeout = TimeSpan.FromSeconds(connectTimeoutSeconds);

                return handler;
            });

        services.AddSingleton<McpConnectionManager>();

        services.AddSingleton<IMcpConnectionManager>(static sp => sp.GetRequiredService<McpConnectionManager>());

        services.AddInstallationResetRecoveryAwareHostedService<McpServerBootstrapHostedService>();

        services.AddSingleton<IHostWorkspaceContext, HostWorkspaceContext>();

        services.AddSingleton<IWorkspaceRegistry>(sp => new CampaignBackedWorkspaceRegistry(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IGrimoireDbReadiness>(),
            sp.GetRequiredService<IOptionsMonitor<ArcanumSettings>>()));

        services.AddScoped<ISessionRepository>(
            static sp => new SessionRepository(
                sp.GetRequiredService<ArcanumDbContext>(),
                sp.GetRequiredService<ISessionAttachmentStore>(),
                sp.GetRequiredService<IOptionsMonitor<ArcanumSettings>>(),
                sp.GetService<ISessionAttachmentIndexQueue>(),
                sp.GetRequiredService<IGrimoireOrdinaryConnectionFactory>()));

        services.AddSingleton<SessionEventHub>();

        services.AddSingleton<IFileSystemBrowser, PhysicalFileSystemBrowser>();

        services.AddScoped<IFileSystemWriter, PhysicalFileSystemWriter>();

        services.AddSingleton<ISpellRepository, SpellRepository>();

        services.AddSingleton<IArcanumSpellCatalog, SpellCatalogService>();

        services.AddSingleton<ISpellCastPreviewService, SpellCastPreviewService>();

        services.AddArcanumResilience();

        return services;
    }

    /// <summary>
    /// Registers the provider resilience layer: the in-memory health tracker, the connectivity probe,
    /// the periodic probe scheduler, and a dedicated <c>"ProviderHealthProbe"</c> named <see cref="HttpClient"/>
    /// (short timeout, no connection pooling — never reuses the long-lived inference clients).
    /// Provider health and fallback mechanics are code-owned and active whenever providers exist.
    /// </summary>
    private static IServiceCollection AddArcanumResilience(this IServiceCollection services)
    {
        services.TryAddSingleton<IProviderHealthTracker, ProviderHealthTracker>();

        services.TryAddSingleton<IProviderHealthProbe, ProviderHealthProbe>();

        // Familiars: one runner for every spawn, and the status probe that reads each CLI's own
        // health surface. Both are inert until an operator configures a Familiar provider.
        services.TryAddSingleton<Familiars.IFamiliarProcessRunner, Familiars.FamiliarProcessRunner>();

        services.TryAddSingleton<Familiars.IFamiliarProbe, Familiars.FamiliarProbe>();

        services.AddHttpClient(
            ProviderHealthProbe.HttpClientName,
            (sp, client) =>
            {
                IOptionsMonitor<ArcanumSettings> opts = sp.GetRequiredService<IOptionsMonitor<ArcanumSettings>>();

                int timeoutSeconds = ArcanumSettingClamps.HealthProbeTimeoutSeconds(
                    ArcanumRuntimeDefaults.Resilience.HealthProbeTimeoutSeconds);

                client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            })
            // Provider base URLs are operator-supplied and may carry credentials in path/query
            // data, so suppress IHttpClientFactory URI logging here too.
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(static () =>
            {
                SocketsHttpHandler handler = OutboundUrlGuard.CreateProviderEgressHandler();

                handler.PooledConnectionLifetime = TimeSpan.Zero;

                return handler;
            });

        services.AddInstallationResetRecoveryAwareHostedService<ProviderHealthProbeService>();

        return services;
    }

    /// <summary>
    /// The Covenant authority boundary: one authority issuer, one envelope key generation, one codec,
    /// one diagnostic tagger, and the single publisher that swaps them all at once.
    /// </summary>
    /// <remarks>
    /// Every one of these is a singleton holding process-wide key material or a process-wide authority
    /// view. A scoped codec would derive its own counters per request, and two counter sequences under
    /// one key is a repeated nonce.
    ///
    /// <para>The issuer lives in Core so it can reach the internal factories of the values it mints.
    /// Registering it here rather than there keeps composition in one place without letting any other
    /// assembly construct an operator authority context.</para>
    /// </remarks>
    private static IServiceCollection AddCovenantAuthority(this IServiceCollection services)
    {

        services.AddSingleton<IOperatorAuthorityContextIssuer>(
            static sp => new OperatorAuthorityContextIssuer(
                sp.GetRequiredService<ICovenantAuthoritySnapshotProvider>()));

        services.AddSingleton(
            static sp => new CovenantEnvelopeMasterKeyProvider(
                sp.GetRequiredService<CovenantRuntimeGenerationProvider>()));

        services.AddSingleton<ICovenantEnvelopeMasterKeyProvider>(
            static sp => sp.GetRequiredService<CovenantEnvelopeMasterKeyProvider>());

        services.AddSingleton<ICovenantEnvelopeCodec>(
            static sp => new CovenantEnvelopeCodec(
                sp.GetRequiredService<ICovenantEnvelopeMasterKeyProvider>(),
                sp.GetService<TimeProvider>() ?? TimeProvider.System));

        services.AddSingleton<ICovenantDiagnosticKeySource>(
            static sp => sp.GetRequiredService<CovenantEnvelopeMasterKeyProvider>());

        services.AddSingleton<ICovenantDiagnosticTagger>(
            static sp => new CovenantDiagnosticTagger(
                sp.GetRequiredService<ICovenantDiagnosticKeySource>()));

        services.AddSingleton(
            static sp => new CovenantAuthorityTransitionPublisher(
                sp.GetRequiredService<CovenantRuntimeGenerationProvider>(),
                sp.GetRequiredService<CovenantEnvelopeMasterKeyProvider>(),
                sp.GetRequiredService<CovenantAvailability>()));

        services.AddSingleton<ICovenantAuthorityTransitionPublisher>(
            static sp => sp.GetRequiredService<CovenantAuthorityTransitionPublisher>());

        services.AddSingleton<ICovenantCommittedTransitionPublisher>(
            static sp => sp.GetRequiredService<CovenantAuthorityTransitionPublisher>());

        return services.AddHostProcessToolsAuthority();

    }

    /// <summary>
    /// The host-process-tools taint boundary: one marker slot, one trusted environment probe, one
    /// pure joiner, and the process-wide policy the startup gate publishes into.
    /// </summary>
    /// <remarks>
    /// The policy is a singleton and is deliberately registered under both its concrete and interface
    /// types: the startup gate needs the publisher, every consumer needs only the read side, and both
    /// have to be the same instance or a consumer would read an unpublished default forever.
    ///
    /// <para>No transition service is registered. Enabling the escape hatch requires a stopped host,
    /// so it is composed by its own offline entry point against a connection that process owns, and a
    /// resolvable singleton would invite a running host to call it.</para>
    /// </remarks>
    private static IServiceCollection AddHostProcessToolsAuthority(this IServiceCollection services)
    {

        services.AddSingleton<HostProcessToolsRuntimePolicy>();

        services.AddSingleton<IHostProcessToolsRuntimePolicy>(
            static sp => sp.GetRequiredService<HostProcessToolsRuntimePolicy>());

        services.AddSingleton<IHostProcessToolsMarkerPairJoiner, HostProcessToolsMarkerPairJoiner>();

        services.AddSingleton<IHostProcessToolsMarkerStore>(
            static sp => new HostProcessToolsMarkerStore(sp.GetRequiredService<IOsCredentialStore>()));

        // An explicit factory with an optional options resolution: this probe runs during bootstrap
        // in containers that never bound the options pipeline, and a required resolution there would
        // turn a missing registration into a startup failure rather than a hardened default.
        services.AddSingleton<IHostProcessToolsEnvironmentProbe>(
            static sp => new HostProcessToolsEnvironmentProbe(
                sp.GetService<IOptions<ArcanumSettings>>()));

        return services;

    }

    /// <summary>
    /// Canonical Campaign identity: one root-identity key, one physical opener, and the two readers
    /// that turn a supplied directory into a registered Campaign.
    /// </summary>
    /// <remarks>
    /// The key provider and the opener are singletons because the key is process-wide and its OS
    /// credential read must happen once, not once per turn. The readers are scoped because they run
    /// through the scoped Grimoire connection.
    ///
    /// <para>The resolver itself lives in Api and is registered there, beside the endpoints that are its
    /// only callers. Registering it here would put a Campaign-authority decision in the container that
    /// Infrastructure composes for the CLI bootstrap, which has no HTTP boundary to establish it.</para>
    /// </remarks>
    private static IServiceCollection AddCampaignPathIdentity(this IServiceCollection services)
    {

        services.AddSingleton<CampaignRootIdentityKeyProvider>();

        services.AddSingleton<ICampaignRootIdentityKeyProvider>(
            static sp => sp.GetRequiredService<CampaignRootIdentityKeyProvider>());

        services.AddSingleton<ICampaignRootIdentityRecoveryKeyProvider>(
            static sp => sp.GetRequiredService<CampaignRootIdentityKeyProvider>());

        services.AddSingleton(
            static sp => new PhysicalCampaignRootOpener(
                sp.GetRequiredService<ICampaignRootIdentityKeyProvider>()));

        services.AddScoped<ISessionCampaignBindingReader>(
            static sp => new SessionCampaignBindingReader(
                sp.GetRequiredService<ICovenantConnectionSource>()));

        services.AddScoped<ICampaignPathIdentityReader>(
            static sp => new CampaignPathIdentityReader(
                sp.GetRequiredService<ICovenantConnectionSource>(),
                sp.GetRequiredService<PhysicalCampaignRootOpener>()));

        services.AddScoped<ICampaignAvailabilityReader>(
            static sp => new CampaignAvailabilityReader(
                sp.GetRequiredService<ICovenantConnectionSource>()));

        // One codec, registered once. Registration, cleanup, restore cleanup, and compare-delete all
        // resolve this instance, because two implementations that agree today drift tomorrow and the
        // first divergence is a cleanup that declines to recognise the marker registration wrote.
        services.AddSingleton<ICampaignPathMarkerCodec>(
            static sp => new CampaignPathMarkerCodec(
                sp.GetRequiredService<ICampaignRootIdentityKeyProvider>()));

        services.AddScoped<ICampaignPathMarkerLifecycle>(
            static sp => new CampaignPathMarkerLifecycle(
                sp.GetRequiredService<ICampaignPathMarkerCodec>(),
                sp.GetRequiredService<PhysicalCampaignRootOpener>(),
                sp.GetRequiredService<ICovenantConnectionSource>(),
                CovenantSqliteConnectionInitializer.Instance,
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ICampaignRootIdentityRecoveryKeyProvider>()));

        return services;

    }

    /// <summary>
    /// The Covenant persistence boundary: one gate, one store, one mutation kernel, one quota guard,
    /// one search index, and the single-writer workers behind them.
    /// </summary>
    /// <remarks>
    /// Registered from one place so both host compositions get exactly the same set. Two lists that
    /// had to be kept in step by hand is precisely how one host ends up with a second operation gate
    /// and loses the drain guarantee the whole tier depends on.
    ///
    /// <para>The gate, compiler, and linker are singletons because their state is process-wide. The
    /// store, kernel, guard, index, and workers are scoped, because each one writes through the
    /// scoped Grimoire connection.</para>
    /// </remarks>
    private static IServiceCollection AddCovenantPersistence(this IServiceCollection services)
    {

        // Explicit factories rather than type registrations: these components declare internal
        // constructors on purpose, so the container is handed exactly the dependency graph the tier
        // intends instead of whatever a reflective activator can reach.
        services.AddSingleton<CovenantRuntimeGenerationProvider>();

        services.AddSingleton<ICovenantRuntimeGenerationProvider>(
            static sp => sp.GetRequiredService<CovenantRuntimeGenerationProvider>());

        services.AddSingleton(
            static sp => new CovenantAvailability(
                sp.GetRequiredService<CovenantRuntimeGenerationProvider>()));

        services.AddSingleton<ICovenantAvailability>(
            static sp => sp.GetRequiredService<CovenantAvailability>());

        services.AddSingleton(
            static sp => new CovenantAuthoritySnapshotProvider(
                sp.GetRequiredService<CovenantRuntimeGenerationProvider>()));

        services.AddSingleton<ICovenantAuthoritySnapshotProvider>(
            static sp => sp.GetRequiredService<CovenantAuthoritySnapshotProvider>());

        services.AddSingleton(
            static sp => new GrimoireConnectionAdmissionGate(
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ICovenantConnectionDrain>()));

        services.AddSingleton<IGrimoireConnectionAdmissionGate>(
            static sp => sp.GetRequiredService<GrimoireConnectionAdmissionGate>());

        // Its enrolment set is the proof a drain owns. Every direct Covenant handle in the process
        // therefore has to meet the same instance, whichever request scope opened it.
        services.AddSingleton<ICovenantConnectionDrain, CovenantConnectionDrain>();

        services.AddSingleton<IGrimoireOrdinaryConnectionLifecycle>(static sp =>
            new GrimoireOrdinaryConnectionLifecycle(
                sp.GetRequiredService<IGrimoireConnectionAdmissionGate>(),
                sp.GetRequiredService<ICovenantConnectionDrain>()));

        services.AddSingleton<ISqliteNativeRuntime>(SqliteNativeRuntime.Instance);

        services.AddSingleton<IGrimoireOrdinaryConnectionFactoryTestSeam>(static _ =>
            new NoOpGrimoireOrdinaryConnectionFactoryTestSeam());

        services.AddSingleton<IGrimoireOrdinaryConnectionFactory, GrimoireOrdinaryConnectionFactory>();

        services.AddSingleton<ICovenantCampaignScopeProbe>(
            static sp => new CovenantCampaignScopeProbe(sp.GetRequiredService<IServiceScopeFactory>()));

        services.AddSingleton(
            static sp => new CovenantOperationGate(
                sp.GetRequiredService<CovenantRuntimeGenerationProvider>(),
                sp.GetRequiredService<ICovenantCampaignScopeProbe>()));

        services.AddSingleton<ICovenantOperationGate>(
            static sp => sp.GetRequiredService<CovenantOperationGate>());

        services.AddSingleton<ICovenantCompiler, CovenantCompiler>();

        services.AddSingleton<ICovenantLinker, CovenantLinker>();

        services.AddSingleton(static _ => new CovenantSearchQueryCompiler());

        services.AddScoped<ICovenantConnectionSource>(
            static sp => new CovenantConnectionSource(
                sp.GetRequiredService<ArcanumDbContext>(),
                sp.GetRequiredService<IGrimoireOrdinaryConnectionFactory>()));

        services.AddScoped<ICovenantStore>(
            static sp => new CovenantStore(sp.GetRequiredService<ICovenantConnectionSource>()));

        // Registered unconditionally, because the policy itself is what decides whether this
        // installation has a Covenant arm at all. A conditional registration would make "the feature
        // is off" and "this host never wired the policy" the same absence, and the two must not be:
        // one is a decision the export routes can act on, the other is a gap (§10.19.11).
        services.AddScoped<ICovenantExportPolicy>(
            static sp => new CovenantExportPolicy(
                sp.GetRequiredService<ICovenantAvailability>(),
                sp.GetRequiredService<ICovenantOperationGate>(),
                sp.GetRequiredService<ICovenantConnectionSource>()));

        // The turn-plan seam. Scoped because it reads through the scoped store and hands back a
        // lease the turn owns for its whole lifetime (§10.13).
        // The operator's read path. Scoped for the same reason as the write path below: it answers one
        // request under one caller-owned lease.
        services.AddScoped<ICovenantManagementService>(static sp => new CovenantManagementService(
            sp.GetRequiredService<ICovenantStore>(),
            sp.GetRequiredService<ICovenantLinker>(),
            sp.GetRequiredService<ICovenantOperationGate>(),
            sp.GetRequiredService<ICovenantAvailability>(),
            sp.GetRequiredService<ICovenantEnvelopeCodec>(),
            sp.GetRequiredService<ICampaignAvailabilityReader>()));

        // The operator's write path. Scoped because it borrows the caller's own connection and lease
        // for the life of one request; a singleton would outlive both.
        services.AddScoped<ICovenantMutationService>(static sp => new CovenantMutationService(
            sp.GetRequiredService<ICovenantStore>(),
            sp.GetRequiredService<ICovenantCompiler>(),
            sp.GetRequiredService<ICovenantEnvelopeCodec>(),
            sp.GetRequiredService<ICovenantConnectionSource>(),
            sp.GetRequiredService<CovenantMutationKernel>(),
            sp.GetRequiredService<CovenantCurationKernel>(),
            sp.GetRequiredService<ICovenantAuthoritySnapshotProvider>(),
            sp.GetRequiredService<TimeProvider>()));

        services.AddScoped<ICovenantContextProvider>(
            static sp => new CovenantContextProvider(
                sp.GetRequiredService<ICovenantAvailability>(),
                sp.GetRequiredService<ICovenantOperationGate>(),
                sp.GetRequiredService<ICovenantStore>(),
                sp.GetRequiredService<ICovenantLinker>()));

        services.AddScoped<ICovenantSearchIndex>(
            static sp => new CovenantSearchIndex(sp.GetRequiredService<ICovenantConnectionSource>()));

        // The information-flow ledger and the derived-output producers that route through it. All
        // scoped, because each writes its label inside the transaction its caller already owns on the
        // scoped Grimoire connection; a singleton would have to open a second one and lose exactly
        // the atomicity the label exists for (§10.12).
        services.AddScoped<IArtifactSensitivityLedger>(
            static sp => new ArtifactSensitivityLedger(sp.GetRequiredService<ICovenantConnectionSource>()));

        services.AddScoped(
            static sp => new SessionDerivedArtifactStore(
                sp.GetRequiredService<ICovenantConnectionSource>(),
                sp.GetRequiredService<ICovenantSqliteConnectionInitializer>()));

        services.AddScoped<ISessionSummaryArtifactStore>(
            static sp => sp.GetRequiredService<SessionDerivedArtifactStore>());

        services.AddScoped<ISessionTitleArtifactStore>(
            static sp => sp.GetRequiredService<SessionDerivedArtifactStore>());

        services.AddScoped<IProtectedAssistantArtifactReader>(
            static sp => new ProtectedAssistantArtifactReader(
                sp.GetRequiredService<ICovenantConnectionSource>()));

        // One boot identity for the whole process. A disclosure subject records which boot created
        // it so startup can tell an adoptable orphan from a turn that is still live, and a per-scope
        // identity would make every subject look like it belonged to a boot that had already ended.
        services.AddSingleton<CovenantProcessBootIdentity>();

        services.AddSingleton<ICovenantMaintenanceConnectionFactory, CovenantMaintenanceConnectionFactory>();

        services.AddSingleton(
            static sp => new CovenantHealthyCatalogErasureGuard(
                sp.GetRequiredService<IGrimoireOrdinaryConnectionFactory>(),
                sp.GetRequiredService<GrimoireSchemaManifestInspector>()));

        services.AddSingleton(static _ => new CovenantManagedFileErasureRequestReader());

        services.AddSingleton(static _ => new CovenantDisclosureExposureReader());

        services.AddSingleton(
            static sp => new CovenantErasureStartupRecoveryOwnerAdopter(
                sp.GetRequiredService<CovenantOperationGate>()));

        services.AddSingleton(
            static sp => new CovenantCanonicalErasureTransaction(
                sp.GetRequiredService<ICovenantMaintenanceConnectionFactory>(),
                sp.GetRequiredService<ICovenantSqliteConnectionInitializer>(),
                sp.GetRequiredService<ICovenantConnectionDrain>(),
                sp.GetRequiredService<TimeProvider>()));

        services.AddSingleton<ICovenantCanonicalErasure>(
            static sp => sp.GetRequiredService<CovenantCanonicalErasureTransaction>());

        services.AddSingleton(
            static sp => new CovenantLocalErasureStorageHealth(
                sp.GetRequiredService<ICovenantMaintenanceConnectionFactory>(),
                sp.GetRequiredService<ICovenantSqliteConnectionInitializer>(),
                sp.GetRequiredService<ICovenantConnectionDrain>(),
                sp.GetRequiredService<TimeProvider>()));

        services.AddSingleton<ICovenantLocalErasureStorageHealth>(
            static sp => sp.GetRequiredService<CovenantLocalErasureStorageHealth>());

        services.AddSingleton<ICovenantDisclosureTransactionWriter>(
            static sp => new CovenantDisclosureTransactionWriter(
                sp.GetRequiredService<CovenantProcessBootIdentity>().BootId));

        // One process-wide warm owner behind both ports. A second instance would carry a second
        // direct connection and could continue acknowledging while the lifecycle port believed the
        // writer it had quiesced was the only one.
        services.AddSingleton(
            static sp => new CovenantDisclosureWriter(
                sp.GetRequiredService<IGrimoireOrdinaryConnectionFactory>(),
                sp.GetRequiredService<ICovenantAvailability>(),
                sp.GetRequiredService<ICovenantDisclosureTransactionWriter>()));

        services.AddSingleton<ICovenantDisclosureJournal>(
            static sp => sp.GetRequiredService<CovenantDisclosureWriter>());

        services.AddSingleton<ICovenantDisclosureWriterLifecycle>(
            static sp => sp.GetRequiredService<CovenantDisclosureWriter>());

        // The guard remains scoped to its effect boundary and delegates to the one process-wide
        // writer whose lifecycle destructive maintenance owns.
        services.AddScoped(
            static sp => new CovenantToolEgressGuard(
                sp.GetRequiredService<ICovenantDisclosureJournal>()));

        services.AddScoped(
            static sp => new CovenantQuotaGuard(sp.GetRequiredService<ICovenantSqliteConnectionInitializer>()));

        // Scoped beside the quota guard it reserves through, and given the same process boot identity
        // the disclosure journal uses. Startup tells an adoptable prior-boot claim from one this
        // process still owns by comparing that identity; a per-scope boot ID would make every live
        // claim look abandoned.
        services.AddScoped<ISessionTurnClaimCoordinator>(
            static sp => new SessionTurnClaimStore(
                sp.GetRequiredService<ICovenantConnectionSource>(),
                sp.GetRequiredService<CovenantQuotaGuard>(),
                sp.GetRequiredService<CovenantProcessBootIdentity>().BootId));

        services.AddScoped(
            static sp => new CovenantMutationKernel(sp.GetRequiredService<CovenantQuotaGuard>()));

        // No quota guard: a curation change appends no compiled content and joins no Section, so there
        // is no capacity for it to consume and nothing for a guard to measure.
        services.AddScoped(static _ => new CovenantCurationKernel());

        services.AddScoped(
            static sp => new CovenantTurnReceiptCompactor(
                sp.GetRequiredService<ICovenantSqliteConnectionInitializer>()));

        services.AddScoped(static _ => new CovenantOwnerDeletionReader());

        services.AddScoped(
            static sp => new CovenantCleanupWorker(
                sp.GetRequiredService<ICovenantSqliteConnectionInitializer>(),
                sp.GetRequiredService<CovenantOwnerDeletionReader>()));

        services.AddScoped(
            static sp => new CovenantSearchOutboxWorker(
                sp.GetRequiredService<ICovenantSqliteConnectionInitializer>()));

        services.AddScoped(
            static sp => new CovenantIndexRebuilder(
                sp.GetRequiredService<ICovenantConnectionSource>(),
                sp.GetRequiredService<ICovenantSqliteConnectionInitializer>()));

        return services.AddCovenantErasureAndMaintenance();

    }

    /// <summary>
    /// The shared erasure kernels, their pre-readiness recovery, and the maintenance coordinators.
    /// </summary>
    /// <remarks>
    /// Each kernel is registered exactly once. A second registration would be a second managed-file
    /// open, identity, compare-delete, or label-removal implementation, and only one of them could be
    /// right about which file on disk belongs to Arcanum (§10.17).
    ///
    /// <para>Neither kernel resolves <c>ICovenantOperationGate</c>. They receive a caller-owned
    /// authority instead, so an exclusive caller cannot self-deadlock by having a kernel try to
    /// acquire an ordinary lease during its own drain.</para>
    /// </remarks>
    private static IServiceCollection AddCovenantErasureAndMaintenance(this IServiceCollection services)
    {

        services.AddSingleton<IManagedFileCapabilityOpener>(static _ => new ManagedFileCapabilityOpener());

        services.AddSingleton<IManagedFileOwnershipVerifier>(static _ => new ManagedFileOwnershipVerifier());

        services.AddScoped(
            static sp => new ManagedFileErasureStateMachine(
                sp.GetRequiredService<ICovenantSqliteConnectionInitializer>(),
                sp.GetRequiredService<IManagedFileCapabilityOpener>(),
                sp.GetRequiredService<IManagedFileOwnershipVerifier>(),
                sp.GetRequiredService<TimeProvider>()));

        // One scope per request, so the object a filter publishes into is the same one the purger reads
        // and there is nothing process-wide two requests could race on.
        services.AddScoped<ICovenantLabeledArtifactGuard>(
            static sp => new CovenantLabeledArtifactGuard(
                sp.GetRequiredService<IArtifactSensitivityLedger>(),
                sp.GetRequiredService<ICovenantConnectionSource>()));

        services.AddScoped<CovenantSensitivePurgeAuthorityScope>();

        services.AddScoped<ICovenantSensitiveArtifactPurger>(
            static sp => new CovenantSensitiveRetentionPurgeCoordinator(
                sp.GetRequiredService<IArtifactSensitivityLedger>(),
                sp.GetRequiredService<ICovenantConnectionSource>(),
                sp.GetRequiredService<CovenantManagedFileErasureRequestReader>(),
                sp.GetRequiredService<ICovenantOperationGate>(),
                sp.GetRequiredService<IOperatorAuthorityContextIssuer>(),
                sp.GetRequiredService<ICovenantAvailability>(),
                sp.GetRequiredService<ICovenantProtectedArtifactErasureKernel>(),
                sp.GetRequiredService<ICovenantManagedFileErasureKernel>(),
                sp.GetRequiredService<CovenantSensitivePurgeAuthorityScope>()));

        services.AddScoped<ICovenantProtectedArtifactErasureKernel>(
            static sp => new CovenantProtectedArtifactErasureKernel(
                sp.GetRequiredService<ICovenantConnectionSource>(),
                sp.GetRequiredService<ICovenantSqliteConnectionInitializer>(),
                sp.GetRequiredService<TimeProvider>()));

        // The concrete kernel is registered as well as its port, because the two stopped-host overloads
        // are deliberately not on the port: a full reset reaches them through the concrete type, and
        // nothing that resolves the port can name them at all.
        services.AddScoped(
            static sp => new CovenantManagedFileErasureKernel(
                sp.GetRequiredService<ICovenantConnectionSource>(),
                sp.GetRequiredService<ICovenantSqliteConnectionInitializer>(),
                sp.GetRequiredService<ManagedFileErasureStateMachine>(),
                sp.GetRequiredService<TimeProvider>()));

        services.AddScoped<ICovenantManagedFileErasureKernel>(
            static sp => sp.GetRequiredService<CovenantManagedFileErasureKernel>());

        services.AddScoped(
            static sp => new ManagedFileWriteIntentRecoveryService(
                sp.GetRequiredService<ICovenantSqliteConnectionInitializer>(),
                sp.GetRequiredService<IManagedFileCapabilityOpener>(),
                sp.GetRequiredService<IManagedFileOwnershipVerifier>(),
                sp.GetRequiredService<TimeProvider>()));

        services.AddScoped(
            static sp => new CovenantErasureInventorySource(
                sp.GetRequiredService<IGrimoireOrdinaryConnectionFactory>(),
                sp.GetRequiredService<CovenantHealthyCatalogErasureGuard>(),
                sp.GetRequiredService<CovenantManagedFileErasureRequestReader>(),
                sp.GetRequiredService<CovenantDisclosureExposureReader>()));

        services.AddScoped<ICovenantErasureInventorySource>(
            static sp => sp.GetRequiredService<CovenantErasureInventorySource>());

        services.AddScoped(
            static sp => new CovenantErasureTransition(
                sp.GetRequiredService<ICovenantCanonicalErasure>(),
                sp.GetRequiredService<ICovenantLocalErasureStorageHealth>(),
                sp.GetRequiredService<CovenantRuntimeGenerationProvider>(),
                sp.GetRequiredService<ICovenantCommittedTransitionPublisher>()));

        services.AddScoped<ICovenantErasureTransition>(
            static sp => sp.GetRequiredService<CovenantErasureTransition>());

        services.AddScoped(
            static sp => new CovenantErasureCoordinator(
                sp.GetRequiredService<ILongRunningOperationCoordinator>(),
                sp.GetRequiredService<ILongRunningOperationStore>(),
                sp.GetRequiredService<ICovenantOperationGate>(),
                sp.GetRequiredService<ICovenantProtectedArtifactErasureKernel>(),
                sp.GetRequiredService<ICovenantManagedFileErasureKernel>(),
                sp.GetRequiredService<ICovenantErasureInventorySource>(),
                sp.GetRequiredService<ICovenantErasureTransition>(),
                sp.GetRequiredService<ICovenantDisclosureWriterLifecycle>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<CovenantErasureCoordinator>>()));

        services.AddScoped<ICovenantLocalErasureStartupRecovery>(
            static sp => new CovenantLocalErasureStartupRecovery(
                sp.GetRequiredService<ManagedFileErasureStateMachine>()));

        services.AddScoped(
            static sp => new CovenantProtectedInventoryService(
                sp.GetRequiredService<ICovenantConnectionSource>()));

        services.AddScoped(
            static sp => new CovenantRequestedOperationStarter(
                sp.GetRequiredService<ILongRunningOperationCoordinator>()));

        // The three maintenance sweeps and their drivers. Each was registered and exercised by its own
        // suite for the whole of this feature's life with nothing under src calling it, which meant
        // owner deletions were journalled and never applied and the canonical outbox only ever grew.
        // The compactor cost nothing by being idle, because nothing writes a turn-receipt detail row
        // yet; it is driven on the same terms so a producer arrives to a sweep already proven.
        services.AddScoped(
            static sp => new CovenantOwnerCleanupCoordinator(
                sp.GetRequiredService<ICovenantOperationGate>(),
                sp.GetRequiredService<ICovenantConnectionSource>(),
                sp.GetRequiredService<CovenantCleanupWorker>()));

        services.AddScoped(
            static sp => new CovenantSearchOutboxCoordinator(
                sp.GetRequiredService<ICovenantOperationGate>(),
                sp.GetRequiredService<ICovenantConnectionSource>(),
                sp.GetRequiredService<CovenantSearchOutboxWorker>()));

        services.AddScoped(
            static sp => new CovenantTurnReceiptCompactionCoordinator(
                sp.GetRequiredService<ICovenantOperationGate>(),
                sp.GetRequiredService<ICovenantConnectionSource>(),
                sp.GetRequiredService<CovenantTurnReceiptCompactor>()));

        services.AddScoped(
            static sp => new CovenantIndexRebuildCoordinator(
                sp.GetRequiredService<ILongRunningOperationCoordinator>(),
                sp.GetRequiredService<ILongRunningOperationStore>(),
                sp.GetRequiredService<ICovenantOperationGate>(),
                sp.GetRequiredService<CovenantIndexRebuilder>(),
                sp.GetRequiredService<TimeProvider>()));

        // The two recovery handlers the registry requires for the kinds this slice adds. Registering
        // a kind without its handler is the exact drift the coverage suite fails on (#40).
        services.AddScoped<ILongRunningOperationRecoveryHandler, CovenantIndexRebuildRecoveryHandler>();

        services.AddScoped<ILongRunningOperationRecoveryHandler, CovenantFamilyReinitializeRecoveryHandler>();

        return services;

    }

}
