using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using Microsoft.Extensions.Options;

using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Hosting;

using Microsoft.Data.Sqlite;

using System.Net.Http.Json;

using System.Net;

using System.Runtime.ExceptionServices;

using System.Runtime.CompilerServices;

using RetroDownfall.Arcanum.Core.Conclave;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Api.Intelligence;

using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Api.Security;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Secrets.Security;

using RetroDownfall.Arcanum.Core.Events;

using RetroDownfall.Arcanum.Core.Storage.Entities;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Tower;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Operations;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.Logging;

using RetroDownfall.Arcanum.Infrastructure.Repositories;

using RetroDownfall.Arcanum.Infrastructure.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Core.Weave;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

public enum GrimoireTransitionEntryPoint : byte
{
    DirectCovenantReset = 1,

    StandaloneFactoryReset = 2,
}

internal sealed class GrimoireMaintenanceAdmissionHarness : IAsyncDisposable
{
    private static readonly ConditionalWeakTable<RestartableArcanumProfileFixture, ParticipantSeedRegistration> ParticipantSeeds = new();

    private readonly RestartableArcanumProfileFixture _profile;

    private readonly bool _ownsProfile;

    private readonly CancellationTokenSource _shutdown = new();

    private readonly List<MaintenanceSseResponse> _streams = [];

    private HttpClient? _client;

    private int _disposed;

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private GrimoireMaintenanceAdmissionHarness(RestartableArcanumProfileFixture profile, bool ownsProfile,
        MaintenanceAdoptionObservation? adoption, CovenantErasureFaultSeam? faultSeam,
        GrimoireOfflineTransitionTerminalSuffixFaultSeam? terminalSuffixFaultSeam,
        RecoveryHostStartupObservation? startupObservation,
        bool failCompletedOperationTransitions)
    {
        _profile = profile;

        _ownsProfile = ownsProfile;

        Adoption = adoption ?? new MaintenanceAdoptionObservation();

        OrdinaryMutations = startupObservation?.OrdinaryMutations ?? new OrdinaryMutationObservations();

        Operations = startupObservation?.Operations ?? new MaintenanceOperationObservations();

        Operations.FailCompletedTransitions = failCompletedOperationTransitions;

        Journal = startupObservation?.Journal ?? new MaintenanceJournalObservations();

        HostLogs = startupObservation?.HostLogs ?? new MaintenanceHostLogCapture();

        Factory = _profile.CreateFactory();

        Stats = new PausingStatsConnectionInterceptor(StatsProbe);

        Factory.AdditionalDbContextInterceptors = [Stats, EfOpen];

        SseProbes =
        [
            new("/api/events/daemon"),

            new("/api/events/mcp"),

            new("/api/events/logs"),

            new(() => $"/api/sessions/{SessionId}/stream"),

            new(() => $"/api/apprentices/{ApprenticeId}/chronicle"),
        ];

        EndpointProbes =
        [
            StatsProbe,

            new("/v1/models"),

            .. SseProbes,

            new("/v1/chat/completions"),

            new(() => $"/api/sessions/{SessionId}/attachments/{DownloadAttachment.Id}/content"),
        ];

        // All probe instances exist before the host. Dynamic paths resolve only after seeding
        // (or retained-identity restoration) and always match one exact concrete route.
        Factory.BeforeEndpoint = (context, next) =>
        {
            PostAdmissionEndpointProbe? selected = EndpointProbes.SingleOrDefault(probe =>
                context.Request.Headers[PostAdmissionEndpointProbe.Header] == probe.RequestId);

            return selected is null ? next(context) : selected.InvokeAsync(context, next);
        };

        Factory.SettingsOverride = settings => settings with
        {
            Host = settings.Host with { AuditLog = settings.Host.AuditLog with { Enabled = true } },

            Features = settings.Features with { AttachmentRetrieval = true },

            Integrations = settings.Integrations with
            {
                Embeddings = settings.Integrations.Embeddings with
                {
                    Provider = "test",

                    Model = "mistral:latest",

                    Dimensions = 64,
                },
            },

            Execution = settings.Execution with { MaxSseConnections = 8, MaxSseConnectionsPerType = 8 },
        };

        Factory.ServiceOverrides = services =>
        {
            if (startupObservation is not null)
            {
                services.RemoveAll<GrimoireDbReadiness>();

                services.RemoveAll<IGrimoireDbReadiness>();

                services.AddSingleton(startupObservation.Readiness);

                services.AddSingleton<IGrimoireDbReadiness>(startupObservation.Readiness);

                ServiceDescriptor authority = services.Single(descriptor =>
                    descriptor.ServiceType == typeof(ICovenantRecoveryAuthorityBootstrapper));

                services.RemoveAll<ICovenantRecoveryAuthorityBootstrapper>();

                services.AddSingleton<ICovenantRecoveryAuthorityBootstrapper>(sp =>
                {
                    IHostProcessToolsRuntimePolicy actualHostTools = sp
                        .GetRequiredService<IHostProcessToolsRuntimePolicy>();

                    startupObservation.ActualHostToolsPolicy = actualHostTools;

                    startupObservation.CovenantPermitted = actualHostTools.CovenantPermitted;

                    return new ObservingCovenantRecoveryAuthorityBootstrapper(
                        (ICovenantRecoveryAuthorityBootstrapper)authority.ImplementationFactory!(sp),
                        startupObservation);
                });

                ServiceDescriptor dispatch = services.Single(descriptor =>
                    descriptor.ServiceType == typeof(IGrimoireOfflineTransitionHandlerDispatch));

                services.RemoveAll<IGrimoireOfflineTransitionHandlerDispatch>();

                services.AddSingleton<IGrimoireOfflineTransitionHandlerDispatch>(sp =>
                    new ObservingTerminalRecoveryDispatch(
                        (IGrimoireOfflineTransitionHandlerDispatch)dispatch.ImplementationFactory!(sp),
                        startupObservation));

                services.RemoveAll<IGrimoireOfflineTransitionTerminalSuffixFinisher>();

                services.AddSingleton<IGrimoireOfflineTransitionTerminalSuffixFinisher>(sp =>
                {
                    CovenantOperationGate covenant = sp.GetRequiredService<CovenantOperationGate>();

                    GrimoireConnectionAdmissionGate grimoire = sp
                        .GetRequiredService<GrimoireConnectionAdmissionGate>();

                    startupObservation.PrepareTerminalFreshnessMutation(covenant, grimoire);

                    return new ObservingTerminalSuffixFinisher(
                        new GrimoireOfflineTransitionTerminalSuffixFinisher(
                            sp.GetRequiredService<GrimoireOfflineTransitionLifecycleStore>(),
                            sp.GetRequiredService<IServiceScopeFactory>(),
                            covenant,
                            grimoire,
                            startupObservation.ObserveTerminalSuffixAsync),
                        startupObservation);
                });
            }

            // The production Serilog factory does not forward added providers. Match the existing
            // host-test logging pattern so every normal ILogger category reaches this capture.
            services.RemoveAll<ILoggerFactory>();

            services.AddSingleton<ILoggerFactory>(_ => new LoggerFactory([HostLogs]));

            services.AddScoped<ICovenantErasureTransition>(sp => new ObservingMaintenanceTransition(
                sp.GetRequiredService<CovenantErasureTransition>(), sp.GetRequiredService<CovenantRuntimeGenerationProvider>(), Journal));

            ServiceDescriptor parentResolver = services.Last(descriptor =>
                descriptor.ServiceType == typeof(IGrimoireOfflineTransitionParentReceiptResolver));

            services.RemoveAll<IGrimoireOfflineTransitionParentReceiptResolver>();

            services.AddScoped<IGrimoireOfflineTransitionParentReceiptResolver>(sp =>
                new ObservingMaintenanceParentReceiptResolver(
                    (IGrimoireOfflineTransitionParentReceiptResolver)parentResolver.ImplementationFactory!(sp), Journal));

            // Preserve the factory's seeded API/Grimoire secret path while retaining blob keys
            // through the real file-secret implementation over this profile's fake OS store.
            ISecretStore seededSecrets = (ISecretStore)services.Single(descriptor => descriptor.ServiceType == typeof(ISecretStore)).ImplementationInstance!;

            services.RemoveAll<ISecretStore>();

            services.AddSingleton(sp => new OsKeychainSecretStore(
                _profile.CredentialStore, sp.GetRequiredService<DataProtectionSecretStore>(),
                sp.GetRequiredService<IApiKeyDigestCache>(), sp.GetService<Microsoft.Extensions.Logging.ILogger<OsKeychainSecretStore>>()));

            services.AddSingleton<ISecretStore>(sp => new ProfileFileEncryptionSecretStore(seededSecrets, sp.GetRequiredService<OsKeychainSecretStore>()));

            services.AddScoped<MaintenanceScopeSentinel>();

            services.RemoveAll<ISessionAttachmentIndexWriter>();

            services.AddScoped<ISessionAttachmentIndexWriter>(sp => new ObservingSessionAttachmentIndexWriter(
                sp.GetRequiredService<SessionAttachmentIndexRepository>(),
                OrdinaryMutations,
                () => sp.GetRequiredService<IGrimoireConnectionAdmissionGate>().CurrentGeneration));

            services.RemoveAll<ITurnRunWriter>();

            services.AddScoped<TurnRunWriter>();

            services.AddScoped<ITurnRunWriter>(sp => new ObservingTurnRunWriter(
                sp.GetRequiredService<TurnRunWriter>(), OrdinaryMutations));

            services.RemoveAll<IUnseenServantWatermarkStore>();

            services.AddScoped<UnseenServantWatermarkStore>();

            services.AddScoped<IUnseenServantWatermarkStore>(sp => new ObservingUnseenServantWatermarkStore(
                sp.GetRequiredService<UnseenServantWatermarkStore>(), OrdinaryMutations));

            services.AddSingleton<CovenantConnectionDrain>();

            services.AddSingleton<ObservingCovenantConnectionDrain>();

            services.RemoveAll<ICovenantConnectionDrain>();

            services.AddSingleton<ICovenantConnectionDrain>(sp => sp.GetRequiredService<ObservingCovenantConnectionDrain>());

            services.RemoveAll<IGrimoireOrdinaryConnectionFactoryTestSeam>();

            services.AddSingleton<IGrimoireOrdinaryConnectionFactoryTestSeam>(RawOpen);

            services.AddSingleton(Operations);

            services.RemoveAll<IGrimoireOfflineTransitionJournalStore>();

            services.AddSingleton<IGrimoireOfflineTransitionJournalStore>(sp => new ObservingMaintenanceJournal(
                _profile.CredentialStore,
                (GrimoireMaintenanceAdmissionObserver)sp.GetRequiredService<IGrimoireConnectionAdmissionGate>(),
                sp.GetRequiredService<CovenantRuntimeGenerationProvider>(), Journal, Operations));

            services.RemoveAll<ILongRunningOperationStore>();

            services.AddScoped<ILongRunningOperationStore, RecordingLongRunningOperationStore>();

            if (faultSeam is not null || terminalSuffixFaultSeam is not null)
            {
                // The production registration constructs this concrete coordinator directly. A
                // faulted host therefore replaces that concrete resolution with the same production
                // dependency graph plus the requested one-shot seam; adding the delegate alone would
                // not affect any caller.
                services.AddScoped(sp => new CovenantErasureCoordinator(
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

            services.AddSingleton(Adoption);

            services.RemoveAll<ILongRunningOperationMaintenanceLeaseAdoption>();

            services.AddScoped<ILongRunningOperationMaintenanceLeaseAdoption, PausingMaintenanceLeaseAdoption>();

            services.RemoveAll<IGrimoireConnectionAdmissionGate>();

            services.AddSingleton<IGrimoireConnectionAdmissionGate>(sp =>
            {
                GrimoireMaintenanceAdmissionObserver admission = new(
                    sp.GetRequiredService<GrimoireConnectionAdmissionGate>());

                return startupObservation?.ObserveAdmission(admission) ?? admission;
            });

            if (startupObservation is not null)
            {
                services.RemoveAll<ICovenantOperationGate>();

                services.AddSingleton<ICovenantOperationGate>(sp => startupObservation.ObserveCovenant(
                    sp.GetRequiredService<CovenantOperationGate>()));
            }

            services.AddSingleton(sp => new ControlledEventBus(sp.GetRequiredService<InMemoryEventBus>(), _shutdown.Token));

            services.RemoveAll<IEventBus>();

            services.AddSingleton<IEventBus>(sp => sp.GetRequiredService<ControlledEventBus>());

            services.AddSingleton(new ControlledLogQueryService(_shutdown.Token));

            services.RemoveAll<ILogQueryService>();

            services.AddSingleton<ILogQueryService>(sp => sp.GetRequiredService<ControlledLogQueryService>());

            services.AddSingleton<EncryptedBlobStore>();

            services.AddSingleton<ControlledEncryptedBlobStore>();

            services.RemoveAll<IEncryptedBlobStore>();

            services.AddSingleton<IEncryptedBlobStore>(sp => sp.GetRequiredService<ControlledEncryptedBlobStore>());

            services.RemoveAll<IArcanumIntelligenceProvider>();

            services.AddScoped<IArcanumIntelligenceProvider>(sp => sp.GetRequiredService<WizardIntelligenceProvider>());

            services.RemoveAll<IContextPreviewService>();

            services.AddScoped<IContextPreviewService>(sp => sp.GetRequiredService<WizardIntelligenceProvider>());

            services.AddSingleton(_ => startupObservation?.ObserveChat(new ControlledChatClientFactory())
                ?? new ControlledChatClientFactory());

            services.RemoveAll<IChatClientFactory>();

            services.AddSingleton<IChatClientFactory>(sp => sp.GetRequiredService<ControlledChatClientFactory>());

            services.AddSingleton<WeaveService>();

            services.AddSingleton(sp => startupObservation?.ObserveWeave(new ControlledWeaveService(
                    sp.GetRequiredService<WeaveService>()))
                ?? new ControlledWeaveService(sp.GetRequiredService<WeaveService>()));

            services.RemoveAll<IWeaveService>();

            services.AddSingleton<IWeaveService>(sp => sp.GetRequiredService<ControlledWeaveService>());

            services.AddSingleton<ObservingIndexingLogger>();

            services.AddSingleton<Microsoft.Extensions.Logging.ILogger<SessionAttachmentIndexingService>>(sp => sp.GetRequiredService<ObservingIndexingLogger>());

            services.AddSingleton(sp => startupObservation?.ObserveWorkers(new ObservingWorkerScopeFactory(
                    sp.GetRequiredService<IServiceScopeFactory>()))
                ?? new ObservingWorkerScopeFactory(sp.GetRequiredService<IServiceScopeFactory>()));

            // Preserve the production worker and every hosted-service/queue alias. Only its
            // injected scope factory observes construction and completed disposal independently.
            services.RemoveAll<SessionAttachmentIndexingService>();

            services.AddSingleton(sp => new SessionAttachmentIndexingService(
                sp.GetRequiredService<ObservingWorkerScopeFactory>(),
                sp.GetRequiredService<IOptionsMonitor<ArcanumSettings>>(),
                sp.GetRequiredService<IGrimoireConnectionAdmissionGate>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SessionAttachmentIndexingService>>()));

            if (startupObservation is not null)
            {
                services.RemoveAll<CovenantDisclosureWriter>();

                services.AddSingleton(provider => new CovenantDisclosureWriter(
                    provider.GetRequiredService<IGrimoireOrdinaryConnectionFactory>(),
                    provider.GetRequiredService<ICovenantAvailability>(),
                    provider.GetRequiredService<ICovenantDisclosureTransactionWriter>(),
                    startupObservation.ObserveDisclosureWriterReopen));

                if (startupObservation.FailDisclosureWriterRestore)
                {
                    services.RemoveAll<ICovenantDisclosureWriterLifecycle>();

                    services.AddSingleton<ICovenantDisclosureWriterLifecycle>(provider =>
                        new ForwardingDisclosureWriterLifecycle(
                            provider.GetRequiredService<CovenantDisclosureWriter>()));
                }

                services.AddSingleton<IHostedService>(new RecoveryFinalHostedServiceSentinel(startupObservation));
            }
        };
    }

    internal ArcanumWebApplicationFactory Factory { get; }

    internal PostAdmissionEndpointProbe StatsProbe { get; } = new("/api/grimoire/stats");

    internal IReadOnlyList<PostAdmissionEndpointProbe> EndpointProbes { get; }

    internal IReadOnlyList<PostAdmissionEndpointProbe> SseProbes { get; }

    internal PostAdmissionEndpointProbe ProbeForPath(string path) => EndpointProbes.Single(probe => probe.Path == path);

    internal HttpRequestMessage CreateObservedRequest(HttpMethod method, string path)
    {
        HttpRequestMessage request = new(method, path);

        request.Headers.Add(PostAdmissionEndpointProbe.Header, ProbeForPath(path).RequestId);

        return request;
    }

    internal PausingStatsConnectionInterceptor Stats { get; }

    internal PausingEfOpenInterceptor EfOpen { get; } = new();

    internal OrdinaryMutationObservations OrdinaryMutations { get; }

    internal PausingOrdinaryConnectionFactoryTestSeam RawOpen { get; } = new();

    internal MaintenanceOperationObservations Operations { get; }

    internal MaintenanceJournalObservations Journal { get; }

    internal MaintenanceHostLogCapture HostLogs { get; }

    internal MaintenanceAdoptionObservation Adoption { get; }

    internal ObservingCovenantConnectionDrain Drain => Factory.Services.GetRequiredService<ObservingCovenantConnectionDrain>();

    internal HttpClient Client => _client ?? throw new InvalidOperationException("The harness host has not started.");

    internal Guid SessionId { get; private set; }

    internal Guid ApprenticeId { get; private set; }

    internal Guid EntryId { get; private set; }

    internal GrimoireMaintenanceAdmissionObserver Admission => (GrimoireMaintenanceAdmissionObserver)Factory.Services.GetRequiredService<IGrimoireConnectionAdmissionGate>();

    internal ControlledEventBus Events => Factory.Services.GetRequiredService<ControlledEventBus>();

    internal ControlledLogQueryService Logs => Factory.Services.GetRequiredService<ControlledLogQueryService>();

    internal ControlledEncryptedBlobStore Blobs => Factory.Services.GetRequiredService<ControlledEncryptedBlobStore>();

    internal ControlledChatClientFactory Chat => Factory.Services.GetRequiredService<ControlledChatClientFactory>();

    internal ControlledWeaveService Weave => Factory.Services.GetRequiredService<ControlledWeaveService>();

    internal ObservingIndexingLogger IndexingLogs => Factory.Services.GetRequiredService<ObservingIndexingLogger>();

    internal ObservingWorkerScopeFactory WorkerScopes => Factory.Services.GetRequiredService<ObservingWorkerScopeFactory>();

    internal SessionAttachmentIndexingService Indexing => (SessionAttachmentIndexingService)Factory.Services.GetRequiredService<ISessionAttachmentIndexQueue>();

    internal SessionAttachmentRecord DownloadAttachment { get; private set; } = null!;

    internal SessionAttachmentRecord IndexingAttachmentA { get; private set; } = null!;

    internal SessionAttachmentRecord IndexingAttachmentB { get; private set; } = null!;

    internal byte[] DownloadBytes { get; } = "controlled-download-prefix-and-complete-encrypted-payload"u8.ToArray();

    internal static Task<GrimoireMaintenanceAdmissionHarness> StartAsync(
        RestartableArcanumProfileFixture? profile = null,
        MaintenanceAdoptionObservation? adoption = null,
        CovenantErasureFaultSeam? faultSeam = null,
        GrimoireOfflineTransitionTerminalSuffixFaultSeam? terminalSuffixFaultSeam = null,
        bool failCompletedOperationTransitions = false)
    {
        bool ownsProfile = profile is null;

        profile ??= new RestartableArcanumProfileFixture();

        ParticipantSeedRegistration registration = ParticipantSeeds.GetValue(profile, static _ => new());

        if (!registration.TryClaim())
        {
            throw new InvalidOperationException("This profile's participant seed is one-shot. Use StartRecoveryAsync to reopen its retained identities.");
        }

        return StartHostAsync(profile, ownsProfile, registration, seedParticipants: true, adoption, faultSeam,
            terminalSuffixFaultSeam,
            startupObservation: null,
            failCompletedOperationTransitions);
    }

    internal static Task<GrimoireMaintenanceAdmissionHarness> StartRecoveryAsync(
        RestartableArcanumProfileFixture profile,
        MaintenanceAdoptionObservation? adoption = null,
        RecoveryHostStartupObservation? startupObservation = null)
    {
        if (!ParticipantSeeds.TryGetValue(profile, out ParticipantSeedRegistration? registration)
            || registration.Seed is null)
        {
            throw new InvalidOperationException("Recovery requires this profile's completed first-host participant seed.");
        }

        return StartHostAsync(profile, ownsProfile: false, registration, seedParticipants: false, adoption,
            faultSeam: null, terminalSuffixFaultSeam: null, startupObservation,
            failCompletedOperationTransitions: false);
    }

    private static async Task<GrimoireMaintenanceAdmissionHarness> StartHostAsync(
        RestartableArcanumProfileFixture profile,
        bool ownsProfile,
        ParticipantSeedRegistration registration,
        bool seedParticipants,
        MaintenanceAdoptionObservation? adoption,
        CovenantErasureFaultSeam? faultSeam,
        GrimoireOfflineTransitionTerminalSuffixFaultSeam? terminalSuffixFaultSeam,
        RecoveryHostStartupObservation? startupObservation,
        bool failCompletedOperationTransitions)
    {
        GrimoireMaintenanceAdmissionHarness harness = new(profile, ownsProfile, adoption, faultSeam,
            terminalSuffixFaultSeam,
            startupObservation,
            failCompletedOperationTransitions);

        startupObservation?.ObserveHarness(harness);

        try
        {
            harness._client = new HttpClient(harness.Factory.Server.CreateHandler(context =>
                context.Connection.RemoteIpAddress = IPAddress.Loopback))
            {
                BaseAddress = new Uri("http://localhost"),
            };

            harness._client.DefaultRequestHeaders.Add(ArcanumApiHeaders.ApiKey, ArcanumWebApplicationFactory.TestApiKey);

            if (seedParticipants)
            {
                // The real worker initially reconciles before waiting on its queue. Finish
                // that empty-profile scope before exposing A/B, so it cannot claim their
                // identities as automatic Attempt=0 work ahead of the test-owned requests.
                await harness.WorkerScopes.WaitUntilInitialScopeDisposedAsync();

                await harness.SeedAsync();

                registration.Seed = new(harness.SessionId, harness.ApprenticeId, harness.EntryId,
                    harness.DownloadAttachment, harness.IndexingAttachmentA, harness.IndexingAttachmentB);
            }
            else
            {
                ParticipantSeed seed = registration.Seed!;

                harness.SessionId = seed.SessionId;

                harness.ApprenticeId = seed.ApprenticeId;

                harness.EntryId = seed.EntryId;

                harness.DownloadAttachment = seed.Download;

                harness.IndexingAttachmentA = seed.IndexingA;

                harness.IndexingAttachmentB = seed.IndexingB;

                harness.Blobs.DownloadPath = Path.GetFullPath(Path.Combine(ArcanumPaths.AttachmentsDirectory, seed.Download.RelativePath));
            }

            return harness;
        }
        catch
        {
            await harness.DisposeAsync();

            throw;
        }
    }

    private async Task SeedAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();

        ISessionRepository sessions = scope.ServiceProvider.GetRequiredService<ISessionRepository>();

        Session session = await sessions.CreateAsync(null, "Maintenance observation", _shutdown.Token);

        SessionId = session.Id;

        EntryId = Guid.NewGuid();

        var entry = await sessions.AddEntryAsync(SessionId, new Entry
        {
            Id = EntryId,

            SessionId = SessionId,

            Role = MessageRole.User,

            Content = "controlled-entry",

            CreatedAt = DateTimeOffset.UtcNow,
        }, _shutdown.Token);

        if (entry.IsFailure)
        {
            throw new InvalidOperationException(entry.Error.Message);
        }

        Apprentice apprentice = await scope.ServiceProvider.GetRequiredService<IApprenticeRepository>().AddAsync(new Apprentice
        {
            Id = Guid.NewGuid(),

            Name = "Maintenance observer",

            Goal = "Observe the production chronicle",

            SessionId = SessionId,

            Plan = ApprenticeRepository.SerializePlan([new PlanStep { Index = 1, Description = "controlled-plan" }]),

            Status = ApprenticeStatus.Idle.ToString(),

            CreatedAt = DateTimeOffset.UtcNow,

            UpdatedAt = DateTimeOffset.UtcNow,
        }, _shutdown.Token);

        ApprenticeId = apprentice.Id;

        // Seed through the production store without scheduling work. The real hosted queue is
        // populated explicitly by each scenario after its observation barriers are installed.
        SessionAttachmentStore attachments = new(
            scope.ServiceProvider.GetRequiredService<ArcanumDbContext>(),
            scope.ServiceProvider.GetRequiredService<IOptions<ArcanumSettings>>(),
            blobStore: Blobs);

        DownloadAttachment = await attachments.PersistNewAsync(SessionId, null, null,
            "download.bin", "download.bin", DownloadBytes, "application/octet-stream", SessionAttachmentKind.Binary, _shutdown.Token);

        IndexingAttachmentA = await attachments.PersistNewAsync(SessionId, null, null,
            "indexing-a.txt", "indexing-a.txt", "controlled indexing request A"u8.ToArray(), "text/plain", SessionAttachmentKind.Text, _shutdown.Token);

        IndexingAttachmentB = await attachments.PersistNewAsync(SessionId, null, null,
            "indexing-b.txt", "indexing-b.txt", "controlled indexing request B"u8.ToArray(), "text/plain", SessionAttachmentKind.Text, _shutdown.Token);

        Blobs.DownloadPath = Path.GetFullPath(Path.Combine(ArcanumPaths.AttachmentsDirectory, DownloadAttachment.RelativePath));
    }

    internal async Task<DataRetentionPlan> PlanAsync(GrimoireTransitionEntryPoint entryPoint)
    {
        using HttpRequestMessage request = entryPoint switch
        {
            GrimoireTransitionEntryPoint.DirectCovenantReset => new(HttpMethod.Post, "/api/data/memory/reset/plan")
            {
                Content = JsonContent.Create(new MemoryResetRequest(MemoryResetScope.Covenant), ArcanumJsonContext.Default.MemoryResetRequest),
            },

            GrimoireTransitionEntryPoint.StandaloneFactoryReset => new(HttpMethod.Post, "/api/data/factory-reset/plan")
            {
                Content = JsonContent.Create(new InstallationResetDataPlanRequest(InstallationResetDataScope.Global), ArcanumJsonContext.Default.InstallationResetDataPlanRequest),
            },

            _ => throw new ArgumentOutOfRangeException(nameof(entryPoint)),
        };

        using HttpResponseMessage response = await Client.SendAsync(request, _shutdown.Token).WaitAsync(TimeSpan.FromSeconds(10));

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseDataRetentionPlan, _shutdown.Token);

        return body is { IsSuccess: true, Data: { } plan }
            ? plan
            : throw new InvalidOperationException("The authenticated transition plan did not return a success envelope.");
    }

    // The direct memory-reset wire contract has no caller-supplied operation ID. Its actual
    // durable identity comes from the result; only standalone factory reset accepts this ID.
    internal HttpRequestMessage CreateApplyRequest(GrimoireTransitionEntryPoint entryPoint, string planId, Guid operationId) => entryPoint switch
    {
        GrimoireTransitionEntryPoint.DirectCovenantReset => new(HttpMethod.Post, "/api/data/memory/reset")
        {
            Content = JsonContent.Create(new MemoryResetRequest(MemoryResetScope.Covenant, planId), ArcanumJsonContext.Default.MemoryResetRequest),
        },

        GrimoireTransitionEntryPoint.StandaloneFactoryReset => new(HttpMethod.Post, "/api/data/factory-reset")
        {
            Content = JsonContent.Create(FactoryApply(planId, operationId), ArcanumJsonContext.Default.FactoryResetRequest),
        },

        _ => throw new ArgumentOutOfRangeException(nameof(entryPoint)),
    };

    private static FactoryResetRequest FactoryApply(string planId, Guid operationId) =>
        new("factory-reset", ExpectedPlanId: planId, RequestedOperationId: operationId, InstallationResetHandoff: null);

    internal async Task<HttpResponseMessage> StartStatsAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken);

        using HttpRequestMessage request = new(HttpMethod.Get, "/api/grimoire/stats");

        request.Headers.Add(PostAdmissionEndpointProbe.Header, StatsProbe.RequestId);

        return await Client.SendAsync(request, linked.Token);
    }

    internal async Task<MaintenanceSseResponse> OpenSseAsync(string path)
    {
        using HttpRequestMessage request = CreateObservedRequest(HttpMethod.Get, path);

        HttpResponseMessage response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _shutdown.Token).WaitAsync(TimeSpan.FromSeconds(10));

        try
        {
            response.EnsureSuccessStatusCode();

            MaintenanceSseResponse stream = new(response, await response.Content.ReadAsStreamAsync(_shutdown.Token));

            _streams.Add(stream);

            return stream;
        }
        catch
        {
            response.Dispose();

            throw;
        }
    }

    internal async Task<MaintenanceSseResponse> OpenChatAsync()
    {
        OpenAiChatRequest payload = new("mistral:latest",
            [new OpenAiChatMessage("user", OpenAiMessageContent.FromText("Give one controlled answer."))], Stream: true,
            StreamOptions: new OpenAiStreamOptions(IncludeUsage: true));

        using HttpRequestMessage request = CreateObservedRequest(HttpMethod.Post, "/v1/chat/completions");

        request.Content = JsonContent.Create(payload, ArcanumJsonContext.Default.OpenAiChatRequest);

        HttpResponseMessage response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _shutdown.Token).WaitAsync(TimeSpan.FromSeconds(10));

        try
        {
            response.EnsureSuccessStatusCode();

            MaintenanceSseResponse stream = new(response, await response.Content.ReadAsStreamAsync(_shutdown.Token));

            _streams.Add(stream);

            return stream;
        }
        catch
        {
            response.Dispose();

            throw;
        }
    }

    internal void ReleaseStreamProducers()
    {
        Events.Daemon.Checkpoint.Release();

        Events.Mcp.Checkpoint.Release();

        Logs.Frames.Checkpoint.Release();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _shutdown.Cancel();

            Stats.Checkpoint.Release();

            EfOpen.Checkpoint.Release();

            RawOpen.Checkpoint.Release();

            Adoption.Checkpoint.Release();

            if (_client is not null)
            {
                Admission.StageOne.AfterCompletion.Release();

                Admission.StageTwo.AfterCompletion.Release();

                Blobs.Checkpoint.Release();

                Chat.Checkpoint.Release();

                Weave.Checkpoint.Release();
            }

            for (int index = _streams.Count - 1; index >= 0; index--)
            {
                await _streams[index].DisposeAsync();
            }

            _client?.Dispose();
        }
        finally
        {
            try
            {
                await Factory.DisposeAsync();
            }
            finally
            {
                _shutdown.Dispose();

                if (_ownsProfile)
                {
                    await _profile.DisposeAsync();
                }
            }
        }
    }

    private sealed record ParticipantSeed(Guid SessionId, Guid ApprenticeId, Guid EntryId,
        SessionAttachmentRecord Download, SessionAttachmentRecord IndexingA, SessionAttachmentRecord IndexingB);

    private sealed class ParticipantSeedRegistration
    {
        private int _claimed;

        internal ParticipantSeed? Seed { get; set; }

        internal bool TryClaim() => Interlocked.CompareExchange(ref _claimed, 1, 0) == 0;
    }
}

internal enum TerminalFreshnessMutation : byte
{
    None = 0,

    CovenantReadiness = 1,

    GrimoireRequestLease = 2,
}

// This pre-created observation surface is safe to inspect while TestServer startup is blocked.
// It never resolves Factory.Services; DI factories publish only the real host-2 objects that
// production startup has already requested.
internal sealed class RecoveryHostStartupObservation
{
    private GrimoireMaintenanceAdmissionObserver? _admission;

    private ICovenantOperationGate? _covenant;

    private ControlledChatClientFactory? _chat;

    private ControlledWeaveService? _weave;

    private ObservingWorkerScopeFactory? _workers;

    private long _initialOpenGeneration = -1;

    private readonly TaskCompletionSource _finalHostedServiceStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource<GrimoireMaintenanceAdmissionHarness> _harnessCreated =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource _terminalSuffixInvoked =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource<GrimoireOfflineTransitionTerminalSuffixBoundary>
        _terminalSuffixReached = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource _releaseTerminalSuffix =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal GrimoireDbReadiness Readiness { get; } = new();

    internal MaintenanceJournalObservations Journal { get; } = new();

    internal MaintenanceOperationObservations Operations { get; } = new();

    internal OrdinaryMutationObservations OrdinaryMutations { get; } = new();

    internal MaintenanceHostLogCapture HostLogs { get; } = new();

    internal Result<ICovenantClosedRecoveryHandoff>? AuthorityLoad { get; set; }

    internal Result? AuthorityConsume { get; set; }

    internal int DispatchCalls;

    internal int TerminalSuffixCalls;

    private int _disclosureWriterReopenCalls;

    private IGrimoireRequestLease? _terminalFreshnessRequestLease;

    private int _terminalFreshnessPrepared;

    internal bool? CovenantPermitted { get; set; }

    internal IHostProcessToolsRuntimePolicy? ActualHostToolsPolicy { get; set; }

    internal bool FailDisclosureWriterRestore { get; init; }

    internal TerminalFreshnessMutation TerminalFreshnessMutation { get; init; }

    internal bool SuppressTerminalSuffixReached { get; init; }

    internal int DisclosureWriterReopenCalls => Volatile.Read(ref _disclosureWriterReopenCalls);

    internal Task FinalHostedServiceStarted => _finalHostedServiceStarted.Task;

    internal Task<GrimoireMaintenanceAdmissionHarness> HarnessCreated => _harnessCreated.Task;

    internal Task TerminalSuffixInvoked => _terminalSuffixInvoked.Task;

    internal Task<GrimoireOfflineTransitionTerminalSuffixBoundary> TerminalSuffixReached =>
        _terminalSuffixReached.Task;

    internal long InitialOpenGeneration => Interlocked.Read(ref _initialOpenGeneration);

    internal GrimoireMaintenanceAdmissionObserver Admission => Volatile.Read(ref _admission)
        ?? throw new InvalidOperationException("The recovery host has not resolved Grimoire admission yet.");

    internal GrimoireMaintenanceAdmissionObserver? AdmissionOrNull => Volatile.Read(ref _admission);

    internal ICovenantOperationGate Covenant => Volatile.Read(ref _covenant)
        ?? throw new InvalidOperationException("The recovery host has not resolved Covenant admission yet.");

    internal ControlledChatClientFactory? Chat => Volatile.Read(ref _chat);

    internal ControlledWeaveService? Weave => Volatile.Read(ref _weave);

    internal ObservingWorkerScopeFactory? Workers => Volatile.Read(ref _workers);

    internal GrimoireMaintenanceAdmissionObserver ObserveAdmission(GrimoireMaintenanceAdmissionObserver admission)
    {
        Interlocked.CompareExchange(ref _initialOpenGeneration, admission.CurrentGeneration, -1);

        Interlocked.CompareExchange(ref _admission, admission, null);

        return admission;
    }

    internal ICovenantOperationGate ObserveCovenant(ICovenantOperationGate covenant)
    {
        Interlocked.CompareExchange(ref _covenant, covenant, null);

        return covenant;
    }

    internal ControlledChatClientFactory ObserveChat(ControlledChatClientFactory chat)
    {
        Interlocked.CompareExchange(ref _chat, chat, null);

        return chat;
    }

    internal ControlledWeaveService ObserveWeave(ControlledWeaveService weave)
    {
        Interlocked.CompareExchange(ref _weave, weave, null);

        return weave;
    }

    internal ObservingWorkerScopeFactory ObserveWorkers(ObservingWorkerScopeFactory workers)
    {
        Interlocked.CompareExchange(ref _workers, workers, null);

        return workers;
    }

    internal void MarkFinalHostedServiceStarted() => _finalHostedServiceStarted.TrySetResult();

    internal void ObserveHarness(GrimoireMaintenanceAdmissionHarness harness) =>
        _harnessCreated.TrySetResult(harness);

    internal void ObserveTerminalSuffixInvocation()
    {
        _ = Interlocked.Increment(ref TerminalSuffixCalls);

        _ = _terminalSuffixInvoked.TrySetResult();
    }

    internal async Task<Result> ObserveTerminalSuffixAsync(
        GrimoireOfflineTransitionTerminalSuffixBoundary boundary,
        CancellationToken cancellationToken)
    {
        if (!SuppressTerminalSuffixReached)
        {
            _ = _terminalSuffixReached.TrySetResult(boundary);
        }

        await _releaseTerminalSuffix.Task.WaitAsync(cancellationToken);

        return Result.Success();
    }

    internal void ReleaseTerminalSuffix() => _releaseTerminalSuffix.TrySetResult();

    internal void ObserveDisclosureWriterReopen() =>
        _ = Interlocked.Increment(ref _disclosureWriterReopenCalls);

    internal void PrepareTerminalFreshnessMutation(
        CovenantOperationGate covenant,
        GrimoireConnectionAdmissionGate grimoire)
    {
        if (Interlocked.Exchange(ref _terminalFreshnessPrepared, 1) != 0)
        {
            return;
        }

        if (TerminalFreshnessMutation is TerminalFreshnessMutation.CovenantReadiness)
        {
            covenant.PublishReadiness();
        }
        else if (TerminalFreshnessMutation is TerminalFreshnessMutation.GrimoireRequestLease)
        {
            if (!grimoire.TryAcquireRequestLease(
                GrimoireRequestKind.Finite,
                out IGrimoireRequestLease? admitted))
            {
                throw new InvalidOperationException(
                    "The terminal freshness test could not acquire its real request lease.");
            }

            _terminalFreshnessRequestLease = admitted;
        }
    }

    internal async ValueTask ReleaseTerminalFreshnessMutationAsync()
    {
        IGrimoireRequestLease? held = Interlocked.Exchange(
            ref _terminalFreshnessRequestLease,
            null);

        if (held is not null)
        {
            await held.DisposeAsync();
        }
    }

    internal static async Task DisposeAndObserveStartupAsync(
        Task<GrimoireMaintenanceAdmissionHarness> starting,
        RecoveryHostStartupObservation observation)
    {
        using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(10));

        Task ownerBoundary = await Task.WhenAny(observation.HarnessCreated, starting)
            .WaitAsync(cleanup.Token);

        GrimoireMaintenanceAdmissionHarness? owned = null;

        if (ReferenceEquals(ownerBoundary, observation.HarnessCreated))
        {
            owned = await observation.HarnessCreated.WaitAsync(cleanup.Token);
        }
        else if (starting.IsCompletedSuccessfully)
        {
            owned = await starting;
        }

        Exception? disposalFailure = null;

        if (owned is not null)
        {
            try
            {
                await owned.DisposeAsync().AsTask().WaitAsync(cleanup.Token);
            }
            catch (Exception ex)
            {
                disposalFailure = ex;
            }
        }

        try
        {
            _ = await starting.WaitAsync(cleanup.Token);
        }
        catch (Exception) when (!cleanup.IsCancellationRequested)
        {
            // The owned host was disposed and its terminal startup result was observed.
        }

        if (disposalFailure is not null)
        {
            ExceptionDispatchInfo.Capture(disposalFailure).Throw();
        }
    }
}

internal sealed class ObservingTerminalSuffixFinisher(
    IGrimoireOfflineTransitionTerminalSuffixFinisher inner,
    RecoveryHostStartupObservation observation)
    : IGrimoireOfflineTransitionTerminalSuffixFinisher
{
    public Task<Result<GrimoireOfflineTransitionTerminalSuffixOutcome>> FinishAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        SqliteConnection recoveryConnection,
        GrimoireOfflineTransitionRecoveryEvidence evidence,
        CancellationToken cancellationToken)
    {
        observation.ObserveTerminalSuffixInvocation();

        return inner.FinishAsync(
            heldInstallationLock,
            guardedDirectory,
            recoveryConnection,
            evidence,
            cancellationToken);
    }
}

internal sealed class ObservingTerminalRecoveryDispatch(
    IGrimoireOfflineTransitionHandlerDispatch inner,
    RecoveryHostStartupObservation observation) : IGrimoireOfflineTransitionHandlerDispatch
{
    public Task<Result<LongRunningOperationSettlementOutcome>> DispatchAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        LongRunningRecoveryOwnerEvidence ownerEvidence,
        CancellationToken cancellationToken)
    {
        _ = Interlocked.Increment(ref observation.DispatchCalls);

        return inner.DispatchAsync(
            heldInstallationLock,
            guardedDirectory,
            ownerEvidence,
            cancellationToken);
    }
}

internal sealed class ForwardingDisclosureWriterLifecycle(
    ICovenantDisclosureWriterLifecycle inner) : ICovenantDisclosureWriterLifecycle
{
    public ValueTask<Result> QuiesceAsync(CancellationToken cancellationToken) =>
        inner.QuiesceAsync(cancellationToken);

    public ValueTask<Result> ReopenAsync(CancellationToken cancellationToken) =>
        inner.ReopenAsync(cancellationToken);
}

internal sealed class ObservingCovenantRecoveryAuthorityBootstrapper(
    ICovenantRecoveryAuthorityBootstrapper inner,
    RecoveryHostStartupObservation observation) : ICovenantRecoveryAuthorityBootstrapper
{
    public async Task<Result<ICovenantClosedRecoveryHandoff>> LoadAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        SqliteConnection recoveryConnection,
        GrimoireOfflineTransitionRecoveryEvidence evidence,
        IHostProcessToolsRuntimePolicy provisionalHostToolsPolicy,
        CancellationToken cancellationToken)
    {
        Result<ICovenantClosedRecoveryHandoff> result = await inner.LoadAsync(
            heldInstallationLock, guardedDirectory, recoveryConnection, evidence,
            provisionalHostToolsPolicy, cancellationToken);

        observation.AuthorityLoad = result;

        return result.IsSuccess
            ? Result<ICovenantClosedRecoveryHandoff>.Success(
                new ObservingCovenantClosedRecoveryHandoff(result.Value, observation))
            : result;
    }
}

internal sealed class ObservingCovenantClosedRecoveryHandoff(
    ICovenantClosedRecoveryHandoff inner,
    RecoveryHostStartupObservation observation) : ICovenantClosedRecoveryHandoff
{
    public Guid OperationId => inner.OperationId;

    public CovenantExclusiveRecoveryOwner Owner => inner.Owner;

    public LongRunningOperationRecoveryFingerprint ExpectedOperation => inner.ExpectedOperation;

    public async Task<Result> ConsumeAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        GrimoireOfflineTransitionRecoveryEvidence evidence,
        SqliteConnection recoveryConnection,
        IHostProcessToolsRuntimePolicy provisionalHostToolsPolicy,
        CancellationToken cancellationToken)
    {
        Result result = await inner.ConsumeAsync(
            heldInstallationLock, guardedDirectory, evidence, recoveryConnection,
            provisionalHostToolsPolicy, cancellationToken);

        observation.AuthorityConsume = result;

        return result;
    }
}

internal sealed class RecoveryFinalHostedServiceSentinel(RecoveryHostStartupObservation observation) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        observation.MarkFinalHostedServiceStarted();

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class MaintenanceSseResponse(HttpResponseMessage response, Stream stream) : IAsyncDisposable
{
    private readonly StreamReader _reader = new(stream);

    private readonly List<string> _frames = [];

    internal IReadOnlyList<string> Frames => _frames.ToArray();

    private int _disposed;

    internal async Task<string> ReadDataFrameAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        while (await ReadFrameAsync(timeout.Token) is { } frame)
        {
            if (frame.Contains("data:", StringComparison.Ordinal))
            {
                return frame;
            }
        }

        throw new EndOfStreamException("The SSE response ended before its data frame.");
    }

    internal async Task<int> ReadTerminalCountToEndAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        int count = 0;

        while (await ReadFrameAsync(timeout.Token) is { } frame)
        {
            if (frame == "data: [DONE]")
            {
                count++;
            }
        }

        return count;
    }

    internal async Task<int> ReadRevocationTerminalToEndAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        int count = 0;

        while (await ReadFrameAsync(timeout.Token) is { } frame)
        {
            if (frame != "data: [DONE]" || count != 0)
            {
                throw new InvalidDataException("The revoked SSE response emitted an additional complete frame.");
            }

            count++;
        }

        if (count != 1)
        {
            throw new InvalidDataException("The revoked SSE response ended without its complete terminal frame.");
        }

        return count;
    }

    private async Task<string?> ReadFrameAsync(CancellationToken cancellationToken)
    {
        List<string> lines = [];

        while (await _reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (lines.Count > 0)
                {
                    string frame = string.Join("\n", lines);

                    _frames.Add(frame);

                    return frame;
                }
            }
            else
            {
                lines.Add(line);
            }
        }

        if (lines.Count != 0)
        {
            throw new InvalidDataException("The SSE response ended within a frame.");
        }

        return null;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _reader.Dispose();

            response.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
