using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using Microsoft.Extensions.Options;

using Microsoft.Extensions.Logging;

using System.Net.Http.Json;

using System.Net;

using System.Runtime.CompilerServices;

using RetroDownfall.Arcanum.Core.Conclave;

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

using RetroDownfall.Arcanum.Core.Operations;

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

internal enum GrimoireTransitionEntryPoint : byte
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

    private GrimoireMaintenanceAdmissionHarness(RestartableArcanumProfileFixture profile, bool ownsProfile, MaintenanceAdoptionObservation? adoption)
    {
        _profile = profile;

        _ownsProfile = ownsProfile;

        Adoption = adoption ?? new MaintenanceAdoptionObservation();

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
            // The production Serilog factory does not forward added providers. Match the existing
            // host-test logging pattern so every normal ILogger category reaches this capture.
            services.RemoveAll<ILoggerFactory>();

            services.AddSingleton<ILoggerFactory>(_ => new LoggerFactory([HostLogs]));

            services.AddScoped<ICovenantErasureTransition>(sp => new ObservingMaintenanceTransition(
                sp.GetRequiredService<CovenantErasureTransition>(), sp.GetRequiredService<CovenantRuntimeGenerationProvider>(), Journal));

            // Preserve the factory's seeded API/Grimoire secret path while retaining blob keys
            // through the real file-secret implementation over this profile's fake OS store.
            ISecretStore seededSecrets = (ISecretStore)services.Single(descriptor => descriptor.ServiceType == typeof(ISecretStore)).ImplementationInstance!;

            services.RemoveAll<ISecretStore>();

            services.AddSingleton(sp => new OsKeychainSecretStore(
                _profile.CredentialStore, sp.GetRequiredService<DataProtectionSecretStore>(),
                sp.GetRequiredService<IApiKeyDigestCache>(), sp.GetService<Microsoft.Extensions.Logging.ILogger<OsKeychainSecretStore>>()));

            services.AddSingleton<ISecretStore>(sp => new ProfileFileEncryptionSecretStore(seededSecrets, sp.GetRequiredService<OsKeychainSecretStore>()));

            services.AddScoped<MaintenanceScopeSentinel>();

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
                sp.GetRequiredService<CovenantRuntimeGenerationProvider>(), Journal));

            services.RemoveAll<ILongRunningOperationStore>();

            services.AddScoped<ILongRunningOperationStore, RecordingLongRunningOperationStore>();

            services.AddSingleton(Adoption);

            services.RemoveAll<ILongRunningOperationMaintenanceLeaseAdoption>();

            services.AddScoped<ILongRunningOperationMaintenanceLeaseAdoption, PausingMaintenanceLeaseAdoption>();

            services.RemoveAll<IGrimoireConnectionAdmissionGate>();

            services.AddSingleton<IGrimoireConnectionAdmissionGate>(sp =>
                new GrimoireMaintenanceAdmissionObserver(sp.GetRequiredService<GrimoireConnectionAdmissionGate>()));

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

            services.AddSingleton<ControlledChatClientFactory>();

            services.RemoveAll<IChatClientFactory>();

            services.AddSingleton<IChatClientFactory>(sp => sp.GetRequiredService<ControlledChatClientFactory>());

            services.AddSingleton<WeaveService>();

            services.AddSingleton<ControlledWeaveService>();

            services.RemoveAll<IWeaveService>();

            services.AddSingleton<IWeaveService>(sp => sp.GetRequiredService<ControlledWeaveService>());

            services.AddSingleton<ObservingIndexingLogger>();

            services.AddSingleton<Microsoft.Extensions.Logging.ILogger<SessionAttachmentIndexingService>>(sp => sp.GetRequiredService<ObservingIndexingLogger>());

            services.AddSingleton<ObservingWorkerScopeFactory>();

            // Preserve the production worker and every hosted-service/queue alias. Only its
            // injected scope factory observes construction and completed disposal independently.
            services.RemoveAll<SessionAttachmentIndexingService>();

            services.AddSingleton(sp => new SessionAttachmentIndexingService(
                sp.GetRequiredService<ObservingWorkerScopeFactory>(),
                sp.GetRequiredService<IOptionsMonitor<ArcanumSettings>>(),
                sp.GetRequiredService<IGrimoireConnectionAdmissionGate>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SessionAttachmentIndexingService>>()));
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

    internal PausingOrdinaryConnectionFactoryTestSeam RawOpen { get; } = new();

    internal MaintenanceOperationObservations Operations { get; } = new();

    internal MaintenanceJournalObservations Journal { get; } = new();

    internal MaintenanceHostLogCapture HostLogs { get; } = new();

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
        MaintenanceAdoptionObservation? adoption = null)
    {
        bool ownsProfile = profile is null;

        profile ??= new RestartableArcanumProfileFixture();

        ParticipantSeedRegistration registration = ParticipantSeeds.GetValue(profile, static _ => new());

        if (!registration.TryClaim())
        {
            throw new InvalidOperationException("This profile's participant seed is one-shot. Use StartRecoveryAsync to reopen its retained identities.");
        }

        return StartHostAsync(profile, ownsProfile, registration, seedParticipants: true, adoption);
    }

    internal static Task<GrimoireMaintenanceAdmissionHarness> StartRecoveryAsync(
        RestartableArcanumProfileFixture profile,
        MaintenanceAdoptionObservation? adoption = null)
    {
        if (!ParticipantSeeds.TryGetValue(profile, out ParticipantSeedRegistration? registration)
            || registration.Seed is null)
        {
            throw new InvalidOperationException("Recovery requires this profile's completed first-host participant seed.");
        }

        return StartHostAsync(profile, ownsProfile: false, registration, seedParticipants: false, adoption);
    }

    private static async Task<GrimoireMaintenanceAdmissionHarness> StartHostAsync(
        RestartableArcanumProfileFixture profile,
        bool ownsProfile,
        ParticipantSeedRegistration registration,
        bool seedParticipants,
        MaintenanceAdoptionObservation? adoption)
    {
        GrimoireMaintenanceAdmissionHarness harness = new(profile, ownsProfile, adoption);

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
