using A2A;

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
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.TheForge;
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
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.CommLink;
using RetroDownfall.Arcanum.Infrastructure.Chronosync;
using RetroDownfall.Arcanum.Infrastructure.Configuration;
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
using RetroDownfall.Arcanum.Infrastructure.TheForge;

namespace RetroDownfall.Arcanum.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
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
    /// Registers the shared preset catalog, planner orchestration, complete-candidate validation,
    /// secure credential-readiness probe, and atomic file persistence used by the CLI and
    /// Compendium.
    /// </summary>
    public static IServiceCollection AddArcanumConfigurationPresets(
        this IServiceCollection services)
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

        services.TryAddSingleton<
            IConfigurationPresetService,
            ConfigurationPresetService>();

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

        services.AddSingleton<GrimoireSchemaInstaller>();

        // One instance behind both names. Two would let a reader observe an availability generation
        // the publisher never advanced.
        services.AddSingleton<CovenantAvailability>();

        services.AddSingleton<ICovenantAvailability>(
            static sp => sp.GetRequiredService<CovenantAvailability>());

        services.AddSingleton<CovenantAuthoritySnapshotProvider>();

        services.AddSingleton<ICovenantAuthoritySnapshotProvider>(
            static sp => sp.GetRequiredService<CovenantAuthoritySnapshotProvider>());

        services.AddCovenantAuthority();

        services.AddCampaignPathIdentity();

        services.AddCovenantPersistence();

        services.AddDbContext<ArcanumDbContext>((sp, options) =>
            ArcanumDbContextOptionsConfigurator.Configure(
                options,
                sp.GetRequiredService<IGrimoireDbPassphraseSource>()));

        // GrimoireRepository requires the attachment store (session fork/purge hooks). Register it
        // here so the CLI chronosync path cannot drift from AddArcanumInfrastructure.
        services.AddScoped<ISessionAttachmentStore, SessionAttachmentStore>();
        services.AddScoped<ISessionContextPinStore, SessionContextPinStore>();
        services.AddScoped<IAttachmentSourceResolver, AttachmentSourceResolver>();

        // AttachmentSourceResolver requires the host workspace context; without it the whole
        // chronosync graph (IChronosyncEngine, used by the ordinary `arcanum run` route) fails to
        // activate in the CLI container.
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
                sp.GetService<CovenantMutationKernel>()));

        // The narrow turn-begin port is deliberately a separate registration over the same scoped
        // instance. Resolving it through IGrimoireRepository would let any holder of the broad
        // interface reach Campaign-binding writes it has no business performing (§10.12).
        services.AddScoped<ISessionTurnBeginStore>(
            static sp => (GrimoireRepository)sp.GetRequiredService<IGrimoireRepository>());

        // The batch-aware finalizer is the same scoped instance again, exposed as the narrow
        // publication port so a caller cannot reach entry writes through it (§10.13).
        services.AddScoped<IGrimoireTurnCommitter>(
            static sp => (GrimoireRepository)sp.GetRequiredService<IGrimoireRepository>());

        services.AddScoped<IChronosyncEngine, ChronosyncEngine>();

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
                    ?? LoggerFactory.Create(static _ => { })));

        services.TryAddScoped<IInstallationResetDataService>(provider =>
            provider.GetRequiredService<InstallationResetExistingGrimoire>());

        services.TryAddScoped<IInstallationResetWorkspaceResolver>(provider =>
            provider.GetRequiredService<InstallationResetExistingGrimoire>());

        services.TryAddScoped<IInstallationResetActiveStore>(static _ =>
            new InstallationResetActiveStore(ArcanumPaths.GrimoireDirectory));

        services.TryAddScoped<IInstallationResetCredentialService>(provider =>
            new InstallationResetCredentialCatalog(
                provider.GetRequiredService<IOsCredentialStore>(),
                settings,
                ArcanumPaths.SecretStoreDirectory));

        services.TryAddScoped<IInstallationResetOfflineCleanup, InstallationResetOfflineCleanup>();

        services.TryAddScoped<IInstallationResetStateRoots>(static _ =>
            InstallationResetStateRoots.Default);

        services.TryAddScoped<IInstallationResetPreDataMutation, InstallationResetDaemonMutation>();

        services.TryAddScoped(static _ =>
            new InstallationResetControlPaths(ArcanumPaths.GrimoireDirectory));

        services.TryAddScoped<IInstallationResetService, InstallationResetService>();

        return services;
    }

    /// <summary>
    /// Registers the host-coordinated encrypted backup planner, snapshotter, archive codec, and service.
    /// </summary>
    public static IServiceCollection AddArcanumBackup(this IServiceCollection services)
    {

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
                new DeferredBackupOperationStore(serviceProvider)));

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

                }));

        return services;

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

        services.AddHostedService<UnseenServantService>();

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

        services.AddHostedService<PidFileService>();

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

        services.AddSingleton<GrimoireSchemaInstaller>();

        // One instance behind both names. Two would let a reader observe an availability generation
        // the publisher never advanced.
        services.AddSingleton<CovenantAvailability>();

        services.AddSingleton<ICovenantAvailability>(
            static sp => sp.GetRequiredService<CovenantAvailability>());

        services.AddSingleton<CovenantAuthoritySnapshotProvider>();

        services.AddSingleton<ICovenantAuthoritySnapshotProvider>(
            static sp => sp.GetRequiredService<CovenantAuthoritySnapshotProvider>());

        services.AddCovenantAuthority();

        services.AddCampaignPathIdentity();

        services.AddCovenantPersistence();

        services.AddScoped<IDivinationService, DivinationService>();
        services.AddScoped<EmbeddingsResetService>();
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
        services.AddHostedService<GrimoireDatabaseHostedService>();

        // Must run after the encrypted Grimoire is migrated and before durable workloads below
        // begin accepting potentially conflicting work.
        services.AddHostedService<LongRunningOperationStartupHostedService>();

        // One-shot pending attachment GC; runs after Grimoire schema bootstrap above.
        services.AddHostedService<SessionAttachmentPendingGcHostedService>();

        // RAG Phase 2/3 — Entry Weaving and Workspace Indexing both idle (no-op) until
        // Arcanum:Features:SessionSearch / CodebaseRetrieval are enabled, so registering them
        // unconditionally is safe on the hot path. Registered after
        // GrimoireDatabaseHostedService so the Grimoire (and The Weave's schema) is guaranteed ready
        // before either service's first tick can run any query.
        services.AddHostedService<EntryWeavingService>();

        services.AddSingleton<SessionAttachmentIndexingService>();
        services.AddSingleton<ISessionAttachmentIndexQueue>(
            static sp => sp.GetRequiredService<SessionAttachmentIndexingService>());
        services.AddHostedService(static sp => sp.GetRequiredService<SessionAttachmentIndexingService>());

        services.AddSingleton<IWorkspaceFileWatcherFactory, WorkspaceFileWatcherFactory>();
        services.AddSingleton<WorkspaceIndexingService>();
        services.AddSingleton<IWorkspaceIndexingService>(static sp => sp.GetRequiredService<WorkspaceIndexingService>());
        services.AddSingleton<IWorkspaceIndexRuntimeStatusProvider>(static sp => sp.GetRequiredService<WorkspaceIndexingService>());
        services.AddHostedService(static sp => sp.GetRequiredService<WorkspaceIndexingService>());

        // RAG Phase 4 — Saga extraction is event-driven (enqueued by WizardIntelligenceProvider after a
        // successful turn), not polling, so registering it unconditionally is safe on the hot path.
        // Registered as a singleton (not just a hosted service) so the hub can resolve it directly to
        // call EnqueueExtraction, mirroring WorkspaceIndexingService's singleton+hosted-factory pattern.
        services.AddSingleton<SagaExtractionService>();
        services.AddHostedService(static sp => sp.GetRequiredService<SagaExtractionService>());

        // The Tapestry (§21.11) — idles unless Arcanum:Features:Tapestry is enabled, so registering it
        // unconditionally is free on the hot path. Registered after GrimoireDatabaseHostedService so
        // The Weave's schema is guaranteed ready before the first sweep.
        services.AddScoped<ITapestrySummarizer, TapestrySummarizer>();
        services.AddScoped<TapestryWeaver>();
        services.AddHostedService<TapestryWeavingService>();

        services.AddHostedService<ArcanumSettingsClampStartupLogger>();

        services.AddHostedService<ArcanumSecurityStartupChecks>();

        services.AddHostedService<FileEncryptionKeyBootstrapHostedService>();

        // Options must be fully configured here — pooled contexts reject OnConfiguring mutations
        // (including AddInterceptors). Passphrase is set by GrimoireDatabaseHostedService before
        // the first request resolves a context from the pool.
        services.AddDbContextPool<ArcanumDbContext>(
            (sp, options) => ArcanumDbContextOptionsConfigurator.Configure(
                options,
                sp.GetRequiredService<IGrimoireDbPassphraseSource>()),
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

        services.AddHostedService<DataRetentionSweepHostedService>();

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

        services.AddScoped<IAttachmentMemoryProvenanceStore, AttachmentMemoryProvenanceStore>();

        services.AddScoped<ILexiconService, LexiconService>();

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
                sp.GetService<CovenantMutationKernel>()));

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
        services.AddHostedService(static sp => sp.GetRequiredService<A2ASendingLeaseRenewer>());
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
        services.AddHostedService<Loremaster>();
        services.AddSingleton<ChronicleHub>();
        services.AddSingleton<ApprenticeService>();
        services.AddSingleton<IApprenticeRuntime>(static sp => sp.GetRequiredService<ApprenticeService>());
        services.AddHostedService(static sp => sp.GetRequiredService<ApprenticeService>());
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
            static client => client.Timeout = Timeout.InfiniteTimeSpan)
            // Legacy embedder fallback only. URLs may contain credentials in
            // path/query data, so suppress IHttpClientFactory URI logging.
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

        services.AddHostedService<McpServerBootstrapHostedService>();

        services.AddSingleton<IHostWorkspaceContext, HostWorkspaceContext>();

        services.AddSingleton<IWorkspaceRegistry>(sp => new CampaignBackedWorkspaceRegistry(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IGrimoireDbReadiness>(),
            sp.GetRequiredService<IOptionsMonitor<ArcanumSettings>>()));

        services.AddScoped<ISessionRepository, SessionRepository>();

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
            .ConfigurePrimaryHttpMessageHandler(static () =>
            {
                SocketsHttpHandler handler = OutboundUrlGuard.CreateProviderEgressHandler();

                handler.PooledConnectionLifetime = TimeSpan.Zero;

                return handler;
            });

        services.AddHostedService<ProviderHealthProbeService>();

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

        services.AddSingleton<CovenantEnvelopeMasterKeyProvider>();

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

        services.AddSingleton<ICovenantAuthorityTransitionPublisher>(
            static sp => new CovenantAuthorityTransitionPublisher(
                sp.GetRequiredService<CovenantEnvelopeMasterKeyProvider>(),
                sp.GetRequiredService<CovenantAuthoritySnapshotProvider>()));

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
        services.AddSingleton<ICovenantCampaignScopeProbe>(
            static sp => new CovenantCampaignScopeProbe(sp.GetRequiredService<IServiceScopeFactory>()));

        services.AddSingleton(
            static sp => new CovenantOperationGate(
                sp.GetRequiredService<ICovenantAvailability>(),
                sp.GetRequiredService<ICovenantAuthoritySnapshotProvider>(),
                sp.GetRequiredService<ICovenantCampaignScopeProbe>()));

        services.AddSingleton<ICovenantOperationGate>(
            static sp => sp.GetRequiredService<CovenantOperationGate>());

        services.AddSingleton<ICovenantCompiler, CovenantCompiler>();

        services.AddSingleton<ICovenantLinker, CovenantLinker>();

        services.AddSingleton(static _ => new CovenantSearchQueryCompiler());

        services.AddScoped<ICovenantConnectionSource>(
            static sp => new CovenantConnectionSource(sp.GetRequiredService<ArcanumDbContext>()));

        services.AddScoped<ICovenantStore>(
            static sp => new CovenantStore(sp.GetRequiredService<ICovenantConnectionSource>()));

        // The turn-plan seam. Scoped because it reads through the scoped store and hands back a
        // lease the turn owns for its whole lifetime (§10.13).
        services.AddScoped<ICovenantContextProvider>(
            static sp => new CovenantContextProvider(
                sp.GetRequiredService<ICovenantAvailability>(),
                sp.GetRequiredService<ICovenantOperationGate>(),
                sp.GetRequiredService<ICovenantStore>(),
                sp.GetRequiredService<ICovenantLinker>()));

        services.AddScoped<ICovenantSearchIndex>(
            static sp => new CovenantSearchIndex(sp.GetRequiredService<ICovenantConnectionSource>()));

        // One boot identity for the whole process. A disclosure subject records which boot created
        // it so startup can tell an adoptable orphan from a turn that is still live, and a per-scope
        // identity would make every subject look like it belonged to a boot that had already ended.
        services.AddSingleton<CovenantProcessBootIdentity>();

        services.AddScoped<ICovenantDisclosureJournal>(
            static sp => new CovenantDisclosureJournal(
                sp.GetRequiredService<ICovenantConnectionSource>(),
                sp.GetRequiredService<CovenantProcessBootIdentity>().BootId));

        services.AddScoped(
            static sp => new CovenantQuotaGuard(sp.GetRequiredService<ICovenantSqliteConnectionInitializer>()));

        services.AddScoped(
            static sp => new CovenantMutationKernel(sp.GetRequiredService<CovenantQuotaGuard>()));

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

        // The long-running-operation adapter, its checkpoint envelope, and its recovery handler
        // belong to a later slice. Only the base batch algorithm is registered here.
        services.AddScoped(
            static sp => new CovenantIndexRebuilder(
                sp.GetRequiredService<ICovenantConnectionSource>(),
                sp.GetRequiredService<ICovenantSqliteConnectionInitializer>()));

        return services;

    }

}
