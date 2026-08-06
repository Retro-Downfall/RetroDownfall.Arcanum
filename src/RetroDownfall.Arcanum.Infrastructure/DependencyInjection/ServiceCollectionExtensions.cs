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
using RetroDownfall.Arcanum.Infrastructure.Lexicon;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

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
            .PersistKeysToFileSystem(DataProtectionKeyPaths.EnsureDirectory());

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

        services.AddDbContext<ArcanumDbContext>((sp, options) =>
            ArcanumDbContextOptionsConfigurator.Configure(
                options,
                sp.GetRequiredService<IGrimoireDbPassphraseSource>()));

        // GrimoireRepository requires the attachment store (session fork/purge hooks). Register it
        // here so the CLI chronosync path cannot drift from AddArcanumInfrastructure.
        services.AddScoped<ISessionAttachmentStore, SessionAttachmentStore>();
        services.AddScoped<ISessionContextPinStore, SessionContextPinStore>();
        services.AddScoped<IAttachmentSourceResolver, AttachmentSourceResolver>();

        services.AddScoped<IGrimoireRepository, GrimoireRepository>();

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
            .PersistKeysToFileSystem(DataProtectionKeyPaths.EnsureDirectory());

        services.AddSingleton<IApiKeyDigestCache, ApiKeyDigestCache>();

        services.AddArcanumSecretStore();

        services.AddArcanumGrimoireForCli();

        services.AddArcanumBackup();

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
            .PersistKeysToFileSystem(DataProtectionKeyPaths.EnsureDirectory());

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
        services.AddScoped<ILongRunningOperationRecoveryHandler, BudgetReservationRecoveryHandler>();
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
        services.AddScoped<IGrimoireRepository, GrimoireRepository>();
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
        services.AddScoped<ILongRunningOperationRecoveryHandler, A2AInboundSendingRecoveryHandler>();
        services.AddScoped<ILongRunningOperationRecoveryHandler, A2AOutboundSendingRecoveryHandler>();
        services.AddSingleton(static sp => new A2AServer(
            sp.GetRequiredService<ArcanumA2AAgentHandler>(),
            new InMemoryTaskStore(),
            new ChannelEventNotifier(),
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
}
