using System.Collections.Concurrent;

using Microsoft.Extensions.AI;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api.Intelligence;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Events;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Lexicon;

using RetroDownfall.Arcanum.Core.Mcp;

using RetroDownfall.Arcanum.Core.Platform;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Sanctum;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Weave;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

using RetroDownfall.Arcanum.Infrastructure.Mcp;

using RetroDownfall.Arcanum.Infrastructure.Platform;

using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class McpConnectionManagerBootstrapIdempotencyTests : IAsyncLifetime
{
    private const string BootstrapProbeServer = "bootstrap-idempotency-probe";

    private McpConnectionManager _manager = null!;

    private MutableOptionsMonitor _settings = null!;

    private RecordingEventBus _events = null!;

    private FakeLexiconService _lexicon = null!;

    private RecordingGrimoireWorkAdmissionGate _admission = null!;

    private GrimoireConnectionAdmissionGate _innerAdmission = null!;

    public Task InitializeAsync()
    {
        _settings = new MutableOptionsMonitor(
            new ArcanumSettings
            {
                Security = new SecuritySettings
                {
                    AllowUnsandboxedToolChildren = true,
                },
            });

        ServiceCollection services = new();

        services.AddSingleton<ISanctumGuard, PermissiveSanctumGuard>();

        services.AddSingleton<IMemoryScopeResolver, FakeMemoryScopeResolver>();

        services.AddSingleton<IProcessResourceLimiter, ProcessResourceLimiter>();

        _lexicon = new FakeLexiconService();

        services.AddSingleton<ILexiconService>(_lexicon);

        services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<ArcanumSettings>>(
            _settings);

        IServiceScopeFactory scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        IHumanPromptRegistry humanPrompts = new HumanPromptRegistry();

        IUnseenServantPacer pacer = new UnseenServantPacer(
            new FakeEventBus(),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
            scopeFactory,
            NullLogger<UnseenServantPacer>.Instance);

        _events = new RecordingEventBus();

        _manager = new McpConnectionManager(
            NullLogger<McpConnectionManager>.Instance,
            humanPrompts,
            scopeFactory,
            pacer,
            _events,
            new AlwaysTrustedWorkspaceStore(),
            new FakeHttpClientFactory(),
            _settings);

        _innerAdmission = new GrimoireConnectionAdmissionGate(TimeProvider.System);

        _admission = new RecordingGrimoireWorkAdmissionGate(_innerAdmission);

        _manager.ConfigureGlobalAdmission(_admission);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _manager.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentInitializeAsync_StartsEachGlobalServerOnce_WithoutThrowing()
    {
        // An SSE probe is the cheapest observable bootstrap: StartAsync refuses the transport
        // in-process (no child process, no socket) and publishes exactly one server event per
        // attempt, so the published event count *is* the bootstrap count.
        await _manager.RegisterFromConfigAsync(
            new McpConfig
            {
                McpServers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
                {
                    [BootstrapProbeServer] = new()
                    {
                        Type = "sse",
                        Url = "https://mcp.invalid/rpc",
                    },
                },
            },
            scopeWorkingDirectory: null,
            CancellationToken.None);

        using ManualResetEventSlim parked = new(initialState: false);

        _events.ParkEventsFor(BootstrapProbeServer, parked);

        // Task.Run because the parked bootstrap blocks the thread that publishes the event; the
        // block is what keeps the shared operation genuinely in flight for the second caller.
        Task first = Task.Run(() => _manager.InitializeAsync());

        await _events.ParkedEventObserved.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Task second = Task.Run(() => _manager.InitializeAsync());

        // A caller that bootstraps instead of joining the in-flight operation reaches its own start
        // attempt here; the loop only bounds how long we look for it, the assertion below is what fails.
        for (int attempt = 0;
            attempt < 30 && _events.CountEventsFor(BootstrapProbeServer) < 2;
            attempt++)
        {
            await Task.Delay(10);
        }

        parked.Set();

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));

        await _manager.InitializeAsync();

        IReadOnlyList<AITool> tools = await _manager.GetAvailableToolsAsync(workingDirectory: null);

        Assert.Equal(1, _events.CountEventsFor(BootstrapProbeServer));

        Assert.Equal(
            tools.Count,
            tools.Select(static tool => tool.Name)
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Fact]
    public async Task CancelledWaiterDoesNotReleaseSharedInitializerAuthority()
    {
        await RegisterBootstrapProbeAsync();

        using ManualResetEventSlim parked = new(initialState: false);

        _events.ParkEventsFor(BootstrapProbeServer, parked);

        using CancellationTokenSource caller = new();

        Task waiter = StartLongRunningInitializer(caller.Token);

        await _events.ParkedEventObserved.Task.WaitAsync(TimeSpan.FromSeconds(30));

        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => waiter.WaitAsync(TimeSpan.FromSeconds(30)));

        Task stop = _manager.StopAllAsync();

        await Task.Delay(50);

        Assert.False(stop.IsCompleted);

        parked.Set();

        await stop.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task StopCancelsAndObservesActualGlobalInitBeforeStopAll()
    {
        await RegisterBootstrapProbeAsync();

        using ManualResetEventSlim parked = new(initialState: false);

        _events.ParkEventsFor(BootstrapProbeServer, parked);

        Task initializer = StartLongRunningInitializer(CancellationToken.None);

        await _events.ParkedEventObserved.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Task stop = _manager.StopAllAsync();

        await Task.Delay(50);

        Assert.False(stop.IsCompleted);

        parked.Set();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => initializer.WaitAsync(TimeSpan.FromSeconds(30)));

        await stop.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task ConcurrentCallersJoinOneAuthorityBearingTask()
    {
        await RegisterBootstrapProbeAsync();

        using ManualResetEventSlim parked = new(initialState: false);

        _events.ParkEventsFor(BootstrapProbeServer, parked);

        Task ordinary = StartLongRunningInitializer(CancellationToken.None);

        await _events.ParkedEventObserved.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Task actual = GetPrivateField<Task>(_manager, "_globalInitOperation");

        Task preReadinessJoin = ((IMcpGlobalInitializationCoordinator)_manager)
            .InitializeGlobalAsync(
                McpGlobalInitializationAuthority.PreReadinessStartup,
                CancellationToken.None);

        Assert.Same(actual, GetPrivateField<Task>(_manager, "_globalInitOperation"));

        Assert.Equal(
            McpGlobalInitializationAuthority.OrdinaryHostedWork,
            GetPrivateField<McpGlobalInitializationAuthority?>(
                _manager,
                "_globalInitAuthority"));

        parked.Set();

        await Task.WhenAll(ordinary, preReadinessJoin).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(
            [GrimoireWorkKind.McpServerBootstrap],
            _admission.RequestedWorkKinds);

        Assert.Equal(1, _admission.EffectGroupAttempts);
    }

    [Fact]
    public async Task FirstStarterFixesSharedAuthority()
    {
        await RegisterBootstrapProbeAsync();

        using ManualResetEventSlim parked = new(initialState: false);

        _events.ParkEventsFor(BootstrapProbeServer, parked);

        Task preReadiness = Task.Factory.StartNew(
                () => ((IMcpGlobalInitializationCoordinator)_manager)
                    .InitializeGlobalAsync(
                        McpGlobalInitializationAuthority.PreReadinessStartup,
                        CancellationToken.None),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .Unwrap();

        await _events.ParkedEventObserved.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Task actual = GetPrivateField<Task>(_manager, "_globalInitOperation");

        Task ordinaryJoin = _manager.InitializeAsync();

        Assert.Same(actual, GetPrivateField<Task>(_manager, "_globalInitOperation"));

        Assert.Equal(
            McpGlobalInitializationAuthority.PreReadinessStartup,
            GetPrivateField<McpGlobalInitializationAuthority?>(
                _manager,
                "_globalInitAuthority"));

        Assert.Empty(_admission.RequestedWorkKinds);

        parked.Set();

        await Task.WhenAll(preReadiness, ordinaryJoin).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Empty(_admission.RequestedWorkKinds);
    }

    [Fact]
    public async Task GlobalCacheInvalidationDoesNotClearInFlightInitializationIdentity()
    {
        await RegisterBootstrapProbeAsync();

        TrackingMcpClient invalidatedClient = new();

        AddRunningRegistryEntry(_manager, "cache-invalidator", invalidatedClient);

        using ManualResetEventSlim parked = new(initialState: false);

        _events.ParkEventsFor(BootstrapProbeServer, parked);

        Task initializer = StartLongRunningInitializer(CancellationToken.None);

        await _events.ParkedEventObserved.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Task actual = GetPrivateField<Task>(_manager, "_globalInitOperation");

        Result stopped = await _manager.StopAsync(
            "cache-invalidator",
            workingDirectory: null,
            CancellationToken.None);

        Assert.True(stopped.IsSuccess, stopped.IsFailure ? stopped.Error.Message : string.Empty);

        Assert.Same(actual, GetPrivateField<Task>(_manager, "_globalInitOperation"));

        Assert.False(actual.IsCompleted);

        parked.Set();

        await initializer.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task JoinedCallerRechecksPublicationAfterInitializerCompletes()
    {
        TaskCompletionSource disposalReached = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource releaseDisposal = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        int disposalCount = 0;

        _admission.BeforeEffectGroupDisposalAsync = async () =>
        {
            if (Interlocked.Increment(ref disposalCount) == 1)
            {
                disposalReached.TrySetResult();

                await releaseDisposal.Task;
            }
        };

        Task first = _manager.InitializeAsync();

        await disposalReached.Task.WaitAsync(TimeSpan.FromSeconds(30));

        TrackingMcpClient invalidatedClient = new();

        AddRunningRegistryEntry(_manager, "late-invalidator", invalidatedClient);

        Result stopped = await _manager.StopAsync(
            "late-invalidator",
            workingDirectory: null,
            CancellationToken.None);

        Assert.True(stopped.IsSuccess, stopped.IsFailure ? stopped.Error.Message : string.Empty);

        Task actual = GetPrivateField<Task>(_manager, "_globalInitOperation");

        Task joined = _manager.InitializeAsync();

        Assert.Same(
            actual,
            GetPrivateField<Task>(_manager, "_globalInitOperation"));

        releaseDisposal.TrySetResult();

        await Task.WhenAll(first, joined).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, _admission.EffectGroupAttempts);

        Assert.Equal(
            GetPrivateField<long>(_manager, "_toolSurfaceGeneration"),
            GetPrivateField<long>(_manager, "_globalSurfaceRevision"));
    }

    [Fact]
    public async Task DeniedInitializerWaitsWithoutStartingProcessOrNetwork()
    {
        IGrimoireExclusiveClosedLease closed = await CloseAdmissionAsync(_innerAdmission);

        await RegisterBootstrapProbeAsync();

        Task initializer = _manager.InitializeAsync();

        await WaitUntilAsync(
            () => _admission.ActiveGenerationWaiters == 1,
            TimeSpan.FromSeconds(30));

        Assert.False(initializer.IsCompleted);

        Assert.Equal(0, _events.CountEventsFor(BootstrapProbeServer));

        Assert.Equal(0, _admission.EffectGroupAttempts);

        await ReopenAdmissionAsync(closed);

        await initializer.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, _events.CountEventsFor(BootstrapProbeServer));

        Assert.Equal(1, _admission.EffectGroupAttempts);
    }

    [Fact]
    public async Task InvalidationDuringProjectionCannotPublishStaleSurface()
    {
        TrackingMcpClient blockedClient = new();

        TrackingMcpClient invalidatedClient = new();

        ManagedMcpServerEntry blocked = AddRunningRegistryEntry(
            _manager,
            "projection-blocked",
            blockedClient);

        _ = AddRunningRegistryEntry(
            _manager,
            "projection-invalidator",
            invalidatedClient);

        await blocked.Gate.WaitAsync(CancellationToken.None);

        Task initializer = _manager.InitializeAsync();

        await Task.Delay(50);

        Assert.False(initializer.IsCompleted);

        Result stopped = await _manager.StopAsync(
            "projection-invalidator",
            workingDirectory: null,
            CancellationToken.None);

        Assert.True(stopped.IsSuccess, stopped.IsFailure ? stopped.Error.Message : string.Empty);

        long invalidatedGeneration = GetPrivateField<long>(
            _manager,
            "_toolSurfaceGeneration");

        blocked.Gate.Release();

        await initializer.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(
            invalidatedGeneration,
            GetPrivateField<long>(_manager, "_globalSurfaceRevision"));
    }

    [Fact]
    public async Task AlwaysOnRegistrationDuringInitializationIsStartedBeforePublication()
    {
        const string lateServer = "late-always-on-probe";

        await RegisterBootstrapProbeAsync();

        using ManualResetEventSlim parked = new(initialState: false);

        _events.ParkEventsFor(BootstrapProbeServer, parked);

        Task initializer = StartLongRunningInitializer(CancellationToken.None);

        await _events.ParkedEventObserved.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await _manager.RegisterFromConfigAsync(
            new McpConfig
            {
                McpServers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
                {
                    [lateServer] = new()
                    {
                        Type = "sse",
                        Url = "https://mcp.invalid/late",
                    },
                },
            },
            scopeWorkingDirectory: null,
            CancellationToken.None);

        parked.Set();

        await initializer.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, _events.CountEventsFor(lateServer));

        McpServerInfo status = Assert.IsType<McpServerInfo>(
            await _manager.GetStatusAsync(
                lateServer,
                workingDirectory: null,
                CancellationToken.None));

        Assert.Equal(McpServerState.Error, status.State);
    }

    [Fact]
    public async Task AlwaysOnRegistrationAfterPublicationInvalidatesAndRebuildsSurface()
    {
        const string lateServer = "late-projection-always-on-probe";

        await _manager.InitializeAsync();

        await _manager.RegisterFromConfigAsync(
            new McpConfig
            {
                McpServers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
                {
                    [lateServer] = new()
                    {
                        Type = "sse",
                        Url = "https://mcp.invalid/late-projection",
                    },
                },
            },
            scopeWorkingDirectory: null,
            CancellationToken.None);

        await _manager.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, _events.CountEventsFor(lateServer));

        McpServerInfo status = Assert.IsType<McpServerInfo>(
            await _manager.GetStatusAsync(
                lateServer,
                workingDirectory: null,
                CancellationToken.None));

        Assert.Equal(McpServerState.Error, status.State);
    }

    [Fact]
    public async Task ReloadWaitsForInFlightInitializerBeforeInvalidatingRegistry()
    {
        await RegisterBootstrapProbeAsync();

        using ManualResetEventSlim parked = new(initialState: false);

        _events.ParkEventsFor(BootstrapProbeServer, parked);

        Task initializer = StartLongRunningInitializer(CancellationToken.None);

        await _events.ParkedEventObserved.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Task reload = _manager.ReloadAsync(workingDirectory: string.Empty);

        await Task.Delay(50);

        Assert.False(reload.IsCompleted);

        Assert.NotNull(await _manager.GetStatusAsync(
            BootstrapProbeServer,
            workingDirectory: null,
            CancellationToken.None));

        parked.Set();

        await Task.WhenAll(initializer, reload).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(await _manager.GetStatusAsync(
            BootstrapProbeServer,
            workingDirectory: null,
            CancellationToken.None));
    }

    [Fact]
    public async Task ReloadRegistryCutoverWaitsForRegistrationLock()
    {
        SignalOnDisposeMcpClient client = new();

        ManagedMcpServerEntry entry = AddRunningRegistryEntry(
            _manager,
            "reload-registry-cutover",
            client);

        SemaphoreSlim registryLock = GetPrivateField<SemaphoreSlim>(
            _manager,
            "_registryLock");

        await registryLock.WaitAsync(CancellationToken.None);

        await entry.Gate.WaitAsync(CancellationToken.None);

        Task reload = _manager.ReloadAsync(workingDirectory: string.Empty);

        bool entryGateReleased = false;

        try
        {
            entry.Gate.Release();

            entryGateReleased = true;

            Task timeout = Task.Delay(TimeSpan.FromMilliseconds(250));

            Task winner = await Task.WhenAny(client.DisposeStarted.Task, timeout);

            Assert.Same(timeout, winner);
        }
        finally
        {
            if (!entryGateReleased)
            {
                entry.Gate.Release();
            }

            registryLock.Release();

            await reload.WaitAsync(TimeSpan.FromSeconds(30));
        }

        Assert.Equal(1, client.DisposeCount);
    }

    [Fact]
    public async Task DirectStartRacingStopCannotPublishAfterTeardown()
    {
        await RegisterBootstrapProbeAsync();

        using ManualResetEventSlim parked = new(initialState: false);

        _events.ParkEventsFor(BootstrapProbeServer, parked);

        Task<Result> start = Task.Factory.StartNew(
                () => _manager.StartAsync(
                    BootstrapProbeServer,
                    workingDirectory: null,
                    CancellationToken.None),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .Unwrap();

        await _events.ParkedEventObserved.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Task stop = _manager.StopAllAsync();

        await Task.Delay(50);

        Assert.False(stop.IsCompleted);

        parked.Set();

        Assert.True((await start.WaitAsync(TimeSpan.FromSeconds(30))).IsFailure);

        await stop.WaitAsync(TimeSpan.FromSeconds(30));

        Result afterShutdown = await _manager.StartAsync(
            BootstrapProbeServer,
            workingDirectory: null,
            CancellationToken.None);

        Assert.Equal(ErrorCodes.Mcp.ServerNotRunning, afterShutdown.Error.Code);
    }

    [Fact]
    public async Task DirectRestartRacingStopCannotPublishAfterTeardown()
    {
        await RegisterBootstrapProbeAsync();

        using ManualResetEventSlim parked = new(initialState: false);

        _events.ParkEventsFor(BootstrapProbeServer, parked);

        Task<Result> restart = Task.Factory.StartNew(
                () => _manager.RestartAsync(
                    BootstrapProbeServer,
                    workingDirectory: null,
                    CancellationToken.None),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .Unwrap();

        await _events.ParkedEventObserved.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Task stop = _manager.StopAllAsync();

        await Task.Delay(50);

        Assert.False(stop.IsCompleted);

        parked.Set();

        Assert.True((await restart.WaitAsync(TimeSpan.FromSeconds(30))).IsFailure);

        await stop.WaitAsync(TimeSpan.FromSeconds(30));

        Result afterShutdown = await _manager.RestartAsync(
            BootstrapProbeServer,
            workingDirectory: null,
            CancellationToken.None);

        Assert.Equal(ErrorCodes.Mcp.ServerNotRunning, afterShutdown.Error.Code);
    }

    [Fact]
    public async Task ReloadRacingStopCannotPublishAfterTeardown()
    {
        await RegisterBootstrapProbeAsync();

        using ManualResetEventSlim parked = new(initialState: false);

        _events.ParkEventsFor(BootstrapProbeServer, parked);

        Task initializer = StartLongRunningInitializer(CancellationToken.None);

        await _events.ParkedEventObserved.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Task reload = _manager.ReloadAsync(workingDirectory: string.Empty);

        Task stop = _manager.StopAllAsync();

        await Task.Delay(50);

        Assert.False(stop.IsCompleted);

        parked.Set();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => initializer.WaitAsync(TimeSpan.FromSeconds(30)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reload.WaitAsync(TimeSpan.FromSeconds(30)));

        await stop.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(
            -1,
            GetPrivateField<long>(_manager, "_globalSurfaceRevision"));
    }

    [Fact]
    public async Task StopCancelsDirectStartBeforeWaitingForReloadGlobalLock()
    {
        string command = OperatingSystem.IsWindows()
            ? Path.Combine(
                global::System.Environment.SystemDirectory,
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe")
            : "/bin/sleep";

        string[] arguments = OperatingSystem.IsWindows()
            ? ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"]
            : ["30"];

        Dictionary<string, string>? environment = OperatingSystem.IsWindows()
            ? new Dictionary<string, string>
            {
                ["SystemRoot"] = global::System.Environment.GetEnvironmentVariable("SystemRoot")!,
            }
            : null;

        await _manager.RegisterFromConfigAsync(
            new McpConfig
            {
                McpServers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
                {
                    ["lifecycle-lock-probe"] = new()
                    {
                        Command = command,
                        Args = arguments,
                        Env = environment,
                    },
                },
            },
            scopeWorkingDirectory: null,
            CancellationToken.None);

        Task<Result> start = _manager.StartAsync(
            "lifecycle-lock-probe",
            workingDirectory: null,
            CancellationToken.None);

        await WaitUntilAsync(
            () => GetRegistryEntry(_manager, "lifecycle-lock-probe").State
                is McpServerState.Starting,
            TimeSpan.FromSeconds(30));

        Task reload = _manager.ReloadAsync(workingDirectory: string.Empty);

        await WaitUntilAsync(
            () => string.Equals(
                GetPrivateField<object>(_manager, "_globalLifecycleState").ToString(),
                "Reloading",
                StringComparison.Ordinal),
            TimeSpan.FromSeconds(30));

        Task stop = _manager.StopAllAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => start.WaitAsync(TimeSpan.FromSeconds(30)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reload.WaitAsync(TimeSpan.FromSeconds(30)));

        await stop.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(GetRegistryEntry(_manager, "lifecycle-lock-probe").Client);
    }

    [Fact]
    public async Task StopAllWaitsForActualInitializerBeforeStoppingRegisteredServers()
    {
        await RegisterBootstrapProbeAsync();

        TrackingMcpClient existingClient = new();

        _ = AddRunningRegistryEntry(_manager, "existing-client", existingClient);

        using ManualResetEventSlim parked = new(initialState: false);

        _events.ParkEventsFor(BootstrapProbeServer, parked);

        Task initializer = StartLongRunningInitializer(CancellationToken.None);

        await _events.ParkedEventObserved.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Task stop = _manager.StopAllAsync();

        await Task.Delay(50);

        Assert.False(stop.IsCompleted);

        Assert.Equal(0, existingClient.DisposeCount);

        parked.Set();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => initializer.WaitAsync(TimeSpan.FromSeconds(30)));

        await stop.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, existingClient.DisposeCount);
    }

    [Fact]
    public Task DisposeCancelsAndObservesInitializerBeforeDisposingSynchronization() =>
        AssertDisposeWaitsForInitializerAsync();

    [Fact]
    public Task DisposeWaitsForActualInitializerBeforeDisposingManagerLocks() =>
        AssertDisposeWaitsForInitializerAsync();

    [Fact]
    public async Task InitializeAfterShutdownCannotCreateAnotherOperation()
    {
        await _manager.StopAllAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => _manager.InitializeAsync());
    }

    [Fact]
    public async Task StartAfterShutdownCreatesNoClient()
    {
        await RegisterBootstrapProbeAsync();

        await _manager.StopAllAsync();

        Result result = await _manager.StartAsync(
            BootstrapProbeServer,
            workingDirectory: null,
            CancellationToken.None);

        Assert.Equal(ErrorCodes.Mcp.ServerNotRunning, result.Error.Code);

        Assert.Equal(0, _events.CountEventsFor(BootstrapProbeServer));
    }

    [Fact]
    public async Task RestartAfterShutdownCreatesNoClient()
    {
        await RegisterBootstrapProbeAsync();

        await _manager.StopAllAsync();

        Result result = await _manager.RestartAsync(
            BootstrapProbeServer,
            workingDirectory: null,
            CancellationToken.None);

        Assert.Equal(ErrorCodes.Mcp.ServerNotRunning, result.Error.Code);

        Assert.Equal(0, _events.CountEventsFor(BootstrapProbeServer));
    }

    [Fact]
    public async Task ReloadAfterShutdownCreatesNoClient()
    {
        await _manager.StopAllAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => _manager.ReloadAsync(
                workingDirectory: string.Empty,
                CancellationToken.None));
    }

    [Fact]
    public async Task OrdinaryInitializationFailsClosedWithoutConfiguredAdmission()
    {
        await using McpConnectionManager manager = CreateUnconfiguredManager(_settings);

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.InitializeAsync());

        Assert.Contains(
            "requires configured Grimoire admission",
            failure.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalInitializationAuthorityHasExactlyTwoExplicitValues()
    {
        Assert.Equal(
            [
                McpGlobalInitializationAuthority.PreReadinessStartup,
                McpGlobalInitializationAuthority.OrdinaryHostedWork,
            ],
            Enum.GetValues<McpGlobalInitializationAuthority>());

        Assert.Equal(1, (byte)McpGlobalInitializationAuthority.PreReadinessStartup);

        Assert.Equal(2, (byte)McpGlobalInitializationAuthority.OrdinaryHostedWork);
    }

    [Fact]
    public void GlobalAdmissionCanOnlyBeConfiguredOnce()
    {
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => _manager.ConfigureGlobalAdmission(
                new GrimoireConnectionAdmissionGate(TimeProvider.System)));

        Assert.Contains(
            "already been configured",
            failure.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReentrantShutdownCancellationJoinsStableStopOperation()
    {
        CancellationTokenSource initializationLifetime =
            GetPrivateField<CancellationTokenSource>(
                _manager,
                "_globalInitializationLifetime");

        Task? reentrantStop = null;

        Task? operationObservedByCallback = null;

        using CancellationTokenRegistration registration =
            initializationLifetime.Token.Register(
                () =>
                {
                    reentrantStop = _manager.StopAllAsync();

                    operationObservedByCallback =
                        GetPrivateField<Task>(_manager, "_stopOperation");
                });

        Task stop = _manager.StopAllAsync();

        await stop.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(reentrantStop);

        await reentrantStop!.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Same(
            operationObservedByCallback,
            GetPrivateField<Task>(_manager, "_stopOperation"));
    }

    [Fact]
    public async Task ThrowingShutdownCallbackCannotSkipServerTeardown()
    {
        TrackingMcpClient client = new();

        AddRunningRegistryEntry(
            _manager,
            "throwing-shutdown-callback",
            client);

        CancellationTokenSource initializationLifetime =
            GetPrivateField<CancellationTokenSource>(
                _manager,
                "_globalInitializationLifetime");

        using CancellationTokenRegistration registration =
            initializationLifetime.Token.Register(
                static () => throw new InvalidOperationException(
                    "synthetic cancellation callback failure"));

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(
            () => _manager.StopAllAsync());

        Assert.Contains(
            failure.InnerExceptions,
            static inner => inner is InvalidOperationException
                { Message: "synthetic cancellation callback failure" });

        Assert.Equal(1, client.DisposeCount);

        Result start = await _manager.StartAsync(
            "throwing-shutdown-callback",
            workingDirectory: null,
            CancellationToken.None);

        Assert.Equal(ErrorCodes.Mcp.ServerNotRunning, start.Error.Code);
    }

    [Fact]
    public async Task EffectFrontierRefusalWaitsForDirectReopenBeforeRetrying()
    {
        await RegisterBootstrapProbeAsync();

        IGrimoireClosingOwner? closing = null;

        int admissionAttempts = 0;

        _admission.BeforeEffectGroupAdmission = () =>
        {
            if (Interlocked.Increment(ref admissionAttempts) == 1)
            {
                closing = BeginClosing(_innerAdmission);
            }
        };

        Task initializer = _manager.InitializeAsync();

        await WaitUntilAsync(
            () => _admission.ActiveGenerationWaiters == 1,
            TimeSpan.FromSeconds(30));

        Assert.NotNull(closing);

        Assert.False(initializer.IsCompleted);

        Assert.Equal(0, _events.CountEventsFor(BootstrapProbeServer));

        Assert.Equal(1, _admission.EffectGroupAttempts);

        Result drained = await _innerAdmission.DrainRequestAndWorkAsync(
            closing!,
            CancellationToken.None);

        Assert.True(drained.IsSuccess, drained.Error.Message);

        Result<IGrimoireExclusiveClosedLease> closed =
            await _innerAdmission.CloseConnectionAdmissionAsync(
                closing!,
                CancellationToken.None);

        Assert.True(closed.IsSuccess, closed.Error.Message);

        await ReopenAdmissionAsync(closed.Value);

        await initializer.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, _events.CountEventsFor(BootstrapProbeServer));

        Assert.Equal(2, _admission.EffectGroupAttempts);
    }

    [Fact]
    public async Task LifecycleAdmissionCloseIsStableAndDrainsEveryLease()
    {
        using CancellationTokenSource lifetime = new();

        McpLifecycleAdmission admission = new(lifetime);

        Assert.True(admission.TryEnter(out IAsyncDisposable? first));

        Assert.True(admission.TryEnter(out IAsyncDisposable? second));

        Task drain = admission.CloseAndCancel();

        Assert.Same(drain, admission.CloseAndCancel());

        Assert.True(lifetime.IsCancellationRequested);

        Assert.False(drain.IsCompleted);

        await first!.DisposeAsync();

        await first.DisposeAsync();

        Assert.False(drain.IsCompleted);

        await second!.DisposeAsync();

        await drain.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(admission.TryEnter(out _));

        admission.Dispose();
    }

    [Fact]
    public async Task OrdinaryInitializerDrainsThroughEffectThenWorkDisposal()
    {
        TaskCompletionSource effectReached = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource releaseEffect = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource workReached = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource releaseWork = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        List<string> phases = [];

        _admission.BeforeEffectGroupDisposalAsync = async () =>
        {
            phases.Add("effect-dispose");

            effectReached.TrySetResult();

            await releaseEffect.Task;
        };

        _admission.BeforeWorkLeaseDisposalAsync = async () =>
        {
            phases.Add("work-dispose");

            workReached.TrySetResult();

            await releaseWork.Task;
        };

        Task initializer = _manager.InitializeAsync();

        await effectReached.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(
            GetPrivateField<long>(_manager, "_toolSurfaceGeneration"),
            GetPrivateField<long>(_manager, "_globalSurfaceRevision"));

        IGrimoireClosingOwner closingOwner = BeginClosing(_innerAdmission);

        Task<Result> drain = _innerAdmission.DrainRequestAndWorkAsync(
            closingOwner,
            CancellationToken.None).AsTask();

        Assert.False(drain.IsCompleted);

        releaseEffect.TrySetResult();

        await workReached.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(drain.IsCompleted);

        releaseWork.TrySetResult();

        await initializer.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True((await drain.WaitAsync(TimeSpan.FromSeconds(30))).IsSuccess);

        Assert.Equal(["effect-dispose", "work-dispose"], phases);

        Result<IGrimoireExclusiveClosedLease> closed =
            await _innerAdmission.CloseConnectionAdmissionAsync(
                closingOwner,
                CancellationToken.None);

        Assert.True(closed.IsSuccess, closed.Error.Message);

        await ReopenAdmissionAsync(closed.Value);
    }

    [Fact]
    public async Task StopCancelsDeniedInitializerWithoutWaitingForReopen()
    {
        IGrimoireExclusiveClosedLease closed = await CloseAdmissionAsync(_innerAdmission);

        Task initializer = _manager.InitializeAsync();

        await WaitUntilAsync(
            () => _admission.ActiveGenerationWaiters == 1,
            TimeSpan.FromSeconds(30));

        await _manager.StopAllAsync().WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => initializer.WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Equal(0, _admission.EffectGroupAttempts);

        await ReopenAdmissionAsync(closed);
    }

    [Fact]
    public async Task Initial_settings_fingerprint_does_not_retire_bootstrapped_global_partition()
    {
        await _manager.InitializeAsync();
        TrackingMcpClient bootstrappedClient = new();
        AddClientToPrivatePartition(
            _manager,
            "__arcanum_mcp_global__",
            bootstrappedClient);

        _ = await _manager.GetAvailableToolsAsync(
            workingDirectory: null);

        Assert.Equal(0, bootstrappedClient.DisposeCount);
    }

    [Fact]
    public async Task Equivalent_settings_keep_generation_and_workspace_check_change_rebuilds_surface()
    {
        await using TempWorkspace workspace = new();
        await workspace.InitializeAsync();

        AIFunction before = await GetToolAsync(
            workspace.Root,
            ToolRiskClassifier.SearchWorkspaceToolName);

        WorkspaceCheckIntegrationSettings current =
            _settings.CurrentValue.Integrations.WorkspaceChecks;
        _settings.CurrentValue = _settings.CurrentValue with
        {
            Integrations = _settings.CurrentValue.Integrations with
            {
                WorkspaceChecks = current with { },
            },
        };

        AIFunction equivalent = await GetToolAsync(
            workspace.Root,
            ToolRiskClassifier.SearchWorkspaceToolName);
        Assert.Same(before, equivalent);

        _settings.CurrentValue = _settings.CurrentValue with
        {
            Integrations = _settings.CurrentValue.Integrations with
            {
                WorkspaceChecks = current with
                {
                    ExecutableCatalog = current.ExecutableCatalog with
                    {
                        DotNet = current.ExecutableCatalog.DotNet with
                        {
                            Path = "dotnet-test",
                        },
                    },
                },
            },
        };

        AIFunction changed = await GetToolAsync(
            workspace.Root,
            ToolRiskClassifier.SearchWorkspaceToolName);

        Assert.NotSame(equivalent, changed);
    }

    [Fact]
    public async Task Active_apply_patch_survives_settings_generation_reload()
    {
        await using TempWorkspace workspace = new();
        await workspace.InitializeAsync();
        workspace.WriteFile("active-reload.txt", "before\n");

        AIFunction applyPatch = await GetToolAsync(
            workspace.Root,
            ToolRiskClassifier.ApplyPatchToolName);
        BlockingCommittedReceiptSink sink = new();
        ApplyPatchInvocationContext context = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new ToolInvocationIdentity(
                Guid.NewGuid().ToString("N"),
                "active-reload-call",
                ToolRoundOrdinal: 0,
                CallOrdinal: 0,
                ToolRiskClassifier.ApplyPatchToolName),
            "{\"patch\":\"active-reload\"}",
            "test-model",
            DateTimeOffset.UtcNow,
            sink);

        using IDisposable scope =
            ApplyPatchInvocationAmbient.Begin(context);
        Task<object?> invocation = applyPatch.InvokeAsync(
            new AIFunctionArguments(
                new Dictionary<string, object?>
                {
                    ["patch"] =
                        "--- a/active-reload.txt\n"
                        + "+++ b/active-reload.txt\n"
                        + "@@ -1 +1 @@\n"
                        + "-before\n"
                        + "+after\n",
                    ["dryRun"] = false,
                }),
            CancellationToken.None).AsTask();

        await sink.HandoffStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(30));

        _settings.CurrentValue = _settings.CurrentValue with
        {
            Features = _settings.CurrentValue.Features with
            {
                WorkspaceChecks = false,
            },
        };

        _ = await _manager.GetAvailableToolsAsync(
            workspace.Root);

        Assert.False(invocation.IsCompleted);

        sink.ReleaseHandoff();
        TrustedStructuredToolResult result =
            Assert.IsType<TrustedStructuredToolResult>(
                await invocation.WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Contains(
            "\"status\":\"ok\"",
            result.Text,
            StringComparison.Ordinal);
        Assert.True(context.ReceiptHandled);
        Assert.Equal(
            "after\n",
            await File.ReadAllTextAsync(
                Path.Combine(
                    workspace.Root,
                    "active-reload.txt")));
    }

    [Fact]
    public async Task Explicit_reload_drains_active_apply_patch_before_generation_disposal()
    {
        await using TempWorkspace workspace = new();
        await workspace.InitializeAsync();
        workspace.WriteFile("explicit-reload.txt", "before\n");

        AIFunction applyPatch = await GetToolAsync(
            workspace.Root,
            ToolRiskClassifier.ApplyPatchToolName);
        BlockingCommittedReceiptSink sink = new();
        ApplyPatchInvocationContext context = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new ToolInvocationIdentity(
                Guid.NewGuid().ToString("N"),
                "explicit-reload-call",
                ToolRoundOrdinal: 0,
                CallOrdinal: 0,
                ToolRiskClassifier.ApplyPatchToolName),
            "{\"patch\":\"explicit-reload\"}",
            "test-model",
            DateTimeOffset.UtcNow,
            sink);

        using IDisposable scope =
            ApplyPatchInvocationAmbient.Begin(context);
        Task<object?> invocation = applyPatch.InvokeAsync(
            new AIFunctionArguments(
                new Dictionary<string, object?>
                {
                    ["patch"] =
                        "--- a/explicit-reload.txt\n"
                        + "+++ b/explicit-reload.txt\n"
                        + "@@ -1 +1 @@\n"
                        + "-before\n"
                        + "+after\n",
                    ["dryRun"] = false,
                }),
            CancellationToken.None).AsTask();

        await sink.HandoffStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        Task reload = _manager.ReloadAsync(
            workspace.Root);
        await Task.Delay(50);

        Assert.False(reload.IsCompleted);
        Assert.False(invocation.IsCompleted);

        sink.ReleaseHandoff();
        TrustedStructuredToolResult result =
            Assert.IsType<TrustedStructuredToolResult>(
                await invocation.WaitAsync(
                    TimeSpan.FromSeconds(5)));
        await reload.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(
            "\"status\":\"ok\"",
            result.Text,
            StringComparison.Ordinal);
        Assert.True(context.ReceiptHandled);
        Assert.Equal(
            "after\n",
            await File.ReadAllTextAsync(
                Path.Combine(
                    workspace.Root,
                    "explicit-reload.txt")));
    }

    [Fact]
    public async Task Explicit_reload_serves_a_live_tool_surface_while_the_retired_generation_drains()
    {
        await using TempWorkspace workspace = new();
        await workspace.InitializeAsync();
        workspace.WriteFile("draining-reload.txt", "before\n");
        workspace.WriteFile("draining-probe.txt", "probe-content\n");

        AIFunction applyPatch = await GetToolAsync(
            workspace.Root,
            ToolRiskClassifier.ApplyPatchToolName);
        BlockingCommittedReceiptSink sink = new();
        ApplyPatchInvocationContext context = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new ToolInvocationIdentity(
                Guid.NewGuid().ToString("N"),
                "draining-reload-call",
                ToolRoundOrdinal: 0,
                CallOrdinal: 0,
                ToolRiskClassifier.ApplyPatchToolName),
            "{\"patch\":\"draining-reload\"}",
            "test-model",
            DateTimeOffset.UtcNow,
            sink);

        using IDisposable scope =
            ApplyPatchInvocationAmbient.Begin(context);
        Task<object?> invocation = applyPatch.InvokeAsync(
            new AIFunctionArguments(
                new Dictionary<string, object?>
                {
                    ["patch"] =
                        "--- a/draining-reload.txt\n"
                        + "+++ b/draining-reload.txt\n"
                        + "@@ -1 +1 @@\n"
                        + "-before\n"
                        + "+after\n",
                    ["dryRun"] = false,
                }),
            CancellationToken.None).AsTask();

        await sink.HandoffStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        Task reload = _manager.ReloadAsync(
            workspace.Root);
        await Task.Delay(50);

        Assert.False(reload.IsCompleted);

        // The retired generation is still draining, so every turn that rebuilds its surface in this
        // window must be handed a fresh partition. Reload therefore has to publish the cleared state
        // before it waits on the drain, not after it.
        AIFunction readFileChunk = await GetToolAsync(
            workspace.Root,
            "read_file_chunk").WaitAsync(TimeSpan.FromSeconds(10));
        object? readResult = await readFileChunk.InvokeAsync(
            new AIFunctionArguments(
                new Dictionary<string, object?>
                {
                    ["relativePath"] = "draining-probe.txt",
                    ["startLine"] = 1,
                    ["endLine"] = 1,
                }),
            CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(
            "probe-content",
            readResult?.ToString() ?? string.Empty,
            StringComparison.Ordinal);

        sink.ReleaseHandoff();
        _ = await invocation.WaitAsync(TimeSpan.FromSeconds(5));
        await reload.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Dispose_stops_registry_client_that_was_never_attached_to_a_partition()
    {
        TrackingMcpClient orphanedClient = new();

        AddRunningRegistryEntry(
            _manager,
            "never-attached",
            orphanedClient);

        await _manager.DisposeAsync();

        Assert.Equal(1, orphanedClient.DisposeCount);
    }

    [Fact]
    public async Task Unattended_forbidden_write_file_runs_through_the_registered_MCP_bridge_and_records_an_ungated_audit()
    {
        await using TempWorkspace workspace = new();
        await workspace.InitializeAsync();
        RecordingWard ward = new();
        const string content = "The Ward only records this write.\n";

        ToolExecutionPipeline.ProcessedToolCall processed = await ProcessRegisteredToolAsync(
            workspace.Root,
            "write_file",
            new Dictionary<string, object?>
            {
                ["relativePath"] = "ward-effect.txt",
                ["content"] = content,
            },
            ward,
            persistedAssistantTurn: true);

        Assert.False(processed.Failed);

        Assert.False(processed.Denied);

        Assert.Equal(
            content,
            await File.ReadAllTextAsync(Path.Combine(workspace.Root, "ward-effect.txt")));

        AssertUngatedAudit(processed, "write_file", ward);
    }

    [Fact]
    public async Task Unattended_forbidden_scribe_lexicon_runs_through_the_registered_MCP_bridge_and_records_an_ungated_audit()
    {
        await using TempWorkspace workspace = new();
        await workspace.InitializeAsync();
        RecordingWard ward = new();
        const string entityName = "Wardless Atlas";
        const string fact = "The registered tool reached the Global Lexicon.";

        ToolExecutionPipeline.ProcessedToolCall processed = await ProcessRegisteredToolAsync(
            workspace.Root,
            "scribe_lexicon",
            new Dictionary<string, object?>
            {
                ["name"] = entityName,
                ["type"] = "Artifact",
                ["facts"] = new[] { fact },
            },
            ward,
            persistedAssistantTurn: false);

        Assert.False(processed.Failed);

        Assert.False(processed.Denied);

        Result<LexiconEntryDto?> persisted = await _lexicon.GetByNameInScopeAsync(
            entityName,
            LexiconScope.Global,
            CancellationToken.None);
        LexiconEntryDto entry = Assert.IsType<LexiconEntryDto>(persisted.Value);

        Assert.Equal("Artifact", entry.Type);

        Assert.Equal([fact], entry.Facts);

        Assert.Null(entry.ScopeCampaignId);

        AssertUngatedAudit(processed, "scribe_lexicon", ward);
    }

    private async Task<ToolExecutionPipeline.ProcessedToolCall> ProcessRegisteredToolAsync(
        string workspaceRoot,
        string toolName,
        IDictionary<string, object?> arguments,
        RecordingWard ward,
        bool persistedAssistantTurn)
    {
        _settings.CurrentValue = _settings.CurrentValue with
        {
            Security = _settings.CurrentValue.Security with
            {
                Ward = new WardPolicySettings
                {
                    ForbiddenArts = [toolName],
                    UnattendedMode = true,
                },
            },
        };

        AIFunction tool = await GetToolAsync(workspaceRoot, toolName);
        ToolExecutionPipeline pipeline = new(
            new TestOptionsSnapshot<ArcanumSettings>(_settings.CurrentValue),
            ward,
            new PermissiveSanctumGuard(),
            new NoOpSessionAttachmentStore(),
            NullLogger<ToolExecutionPipeline>.Instance);
        Guid sessionId = Guid.NewGuid();

        return await pipeline.ProcessSingleToolCallAsync(
            new FunctionCallContent(
                Guid.NewGuid().ToString("N"),
                toolName,
                arguments),
            new PingRequest(
                "Execute the registered tool.",
                WorkingDirectory: workspaceRoot,
                UnattendedMode: true),
            new ChatOptions { Tools = [tool] },
            activeSpell: null,
            sessionId: sessionId.ToString("D"),
            new ToolExecutionPipeline.TurnContext
            {
                WorkspaceRoot = workspaceRoot,
                PersistedSessionId = persistedAssistantTurn ? sessionId : null,
                AssistantEntryId = persistedAssistantTurn ? Guid.NewGuid() : null,
            },
            suppressInvocationFailures: false,
            CancellationToken.None);
    }

    private static void AssertUngatedAudit(
        ToolExecutionPipeline.ProcessedToolCall processed,
        string toolName,
        RecordingWard ward)
    {
        Assert.Equal(0, ward.WardAsyncCount);

        Assert.Equal(1, ward.AutomaticResolutionCount);

        Assert.Equal(WardResolutionOrigin.Ungated, ward.AutomaticOrigin);

        Assert.Equal(
            [IntelligenceEventType.Warded, IntelligenceEventType.WardResolved],
            processed.WardEvents.Select(static evt => evt.Type));

        IntelligenceEvent warded = processed.WardEvents[0];
        IntelligenceEvent resolved = processed.WardEvents[1];

        Assert.False(string.IsNullOrWhiteSpace(warded.WardId));

        Assert.Equal(warded.WardId, resolved.WardId);

        Assert.Equal(toolName, warded.WardToolName);

        Assert.Equal(toolName, resolved.WardToolName);

        Assert.Equal(WardResolutionOrigin.Ungated, warded.WardOrigin);

        Assert.Equal(WardResolutionOrigin.Ungated, resolved.WardOrigin);

        Assert.True(resolved.WardAllowed);
    }

    private async Task<AIFunction> GetToolAsync(
        string workspaceRoot,
        string toolName)
    {
        IReadOnlyList<AITool> tools =
            await _manager.GetAvailableToolsAsync(workspaceRoot);

        return Assert.IsAssignableFrom<AIFunction>(
            tools.Single(
                tool => string.Equals(
                    tool.Name,
                    toolName,
                    StringComparison.Ordinal)));
    }

    private Task RegisterBootstrapProbeAsync() =>
        _manager.RegisterFromConfigAsync(
            new McpConfig
            {
                McpServers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
                {
                    [BootstrapProbeServer] = new()
                    {
                        Type = "sse",
                        Url = "https://mcp.invalid/rpc",
                    },
                },
            },
            scopeWorkingDirectory: null,
            CancellationToken.None);

    private Task StartLongRunningInitializer(CancellationToken cancellationToken) =>
        Task.Factory.StartNew(
                () => _manager.InitializeAsync(cancellationToken),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .Unwrap();

    private async Task AssertDisposeWaitsForInitializerAsync()
    {
        await RegisterBootstrapProbeAsync();

        using ManualResetEventSlim parked = new(initialState: false);

        _events.ParkEventsFor(BootstrapProbeServer, parked);

        Task initializer = StartLongRunningInitializer(CancellationToken.None);

        await _events.ParkedEventObserved.Task.WaitAsync(TimeSpan.FromSeconds(30));

        SemaphoreSlim globalLock = GetPrivateField<SemaphoreSlim>(
            _manager,
            "_globalInitLock");

        CancellationTokenSource initializationLifetime =
            GetPrivateField<CancellationTokenSource>(
                _manager,
                "_globalInitializationLifetime");

        Task disposal = _manager.DisposeAsync().AsTask();

        await Task.Delay(50);

        Assert.False(disposal.IsCompleted);

        Assert.True(await globalLock.WaitAsync(TimeSpan.FromSeconds(30)));

        globalLock.Release();

        _ = initializationLifetime.Token;

        parked.Set();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => initializer.WaitAsync(TimeSpan.FromSeconds(30)));

        await disposal.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Throws<ObjectDisposedException>(() => globalLock.Wait(0));

        Assert.Throws<ObjectDisposedException>(() => initializationLifetime.Token);
    }

    private static T GetPrivateField<T>(
        McpConnectionManager manager,
        string fieldName)
    {
        System.Reflection.FieldInfo? field = typeof(McpConnectionManager).GetField(
            fieldName,
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(field);

        object? value = field!.GetValue(manager);

        Assert.NotNull(value);

        return (T)value!;
    }

    private static ManagedMcpServerEntry GetRegistryEntry(
        McpConnectionManager manager,
        string name)
    {
        ConcurrentDictionary<(string Name, string? WorkingDirectory), ManagedMcpServerEntry> registry =
            GetPrivateField<ConcurrentDictionary<(string Name, string? WorkingDirectory), ManagedMcpServerEntry>>(
                manager,
                "_registry");

        return registry[(name, null)];
    }

    private static McpConnectionManager CreateUnconfiguredManager(
        Microsoft.Extensions.Options.IOptionsMonitor<ArcanumSettings> settings)
    {
        IServiceScopeFactory scopeFactory = new ServiceCollection()
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        IUnseenServantPacer pacer = new UnseenServantPacer(
            new FakeEventBus(),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
            scopeFactory,
            NullLogger<UnseenServantPacer>.Instance);

        return new McpConnectionManager(
            NullLogger<McpConnectionManager>.Instance,
            new HumanPromptRegistry(),
            scopeFactory,
            pacer,
            new FakeEventBus(),
            new AlwaysTrustedWorkspaceStore(),
            new FakeHttpClientFactory(),
            settings);
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected MCP test condition was not reached.");
            }

            await Task.Delay(10);
        }
    }

    private static async Task<IGrimoireExclusiveClosedLease> CloseAdmissionAsync(
        GrimoireConnectionAdmissionGate gate)
    {
        IGrimoireClosingOwner closingOwner = BeginClosing(gate);

        Result drained = await gate.DrainRequestAndWorkAsync(
            closingOwner,
            CancellationToken.None);

        Assert.True(drained.IsSuccess, drained.Error.Message);

        Result<IGrimoireExclusiveClosedLease> closed =
            await gate.CloseConnectionAdmissionAsync(
                closingOwner,
                CancellationToken.None);

        Assert.True(closed.IsSuccess, closed.Error.Message);

        return closed.Value;
    }

    private static IGrimoireClosingOwner BeginClosing(
        GrimoireConnectionAdmissionGate gate)
    {
        CovenantExclusiveRecoveryOwner owner = new(
            Guid.NewGuid(),
            CovenantExclusiveOperation.CovenantReset,
            new CovenantDigest(new byte[32]));

        Result<IGrimoireClosingOwner> beginning = gate.BeginOrResumeExclusive(owner);

        Assert.True(beginning.IsSuccess, beginning.Error.Message);

        return beginning.Value;
    }

    private static async Task ReopenAdmissionAsync(
        IGrimoireExclusiveClosedLease closed)
    {
        Result reopened = await closed.CompleteAsync(
            CovenantExclusiveLeaseDisposition.RollbackAndReopen,
            CancellationToken.None);

        Assert.True(reopened.IsSuccess, reopened.Error.Message);

        await closed.DisposeAsync();
    }

    /// <summary>
    /// Mirrors what <c>StartAsync</c> leaves behind for a global <c>alwaysOn: false</c> server: a
    /// running registry entry holding a live client that no surface build has attached to a
    /// partition yet.
    /// </summary>
    private static ManagedMcpServerEntry AddRunningRegistryEntry(
        McpConnectionManager manager,
        string name,
        IMcpClient client)
    {
        System.Reflection.FieldInfo? field =
            typeof(McpConnectionManager).GetField(
                "_registry",
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);

        ConcurrentDictionary<(string Name, string? WorkingDirectory), ManagedMcpServerEntry> registry =
            Assert.IsType<ConcurrentDictionary<(string Name, string? WorkingDirectory), ManagedMcpServerEntry>>(
                field!.GetValue(manager));

        ManagedMcpServerEntry entry = new(
            name,
            scopeWorkingDirectory: null,
            new McpServerConfig { Command = "never-attached-server", AlwaysOn = false },
            McpServerTransport.Stdio,
            alwaysOn: false,
            sourceDigest: null)
        {
            State = McpServerState.Running,
            Client = client,
        };

        registry[(name, null)] = entry;

        return entry;
    }

    private static void AddClientToPrivatePartition(
        McpConnectionManager manager,
        string partitionKey,
        IMcpClient client)
    {
        System.Reflection.FieldInfo? field =
            typeof(McpConnectionManager).GetField(
                "_partitionClients",
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        object? partitions = field!.GetValue(manager);
        Assert.NotNull(partitions);
        System.Reflection.PropertyInfo? indexer =
            partitions!.GetType().GetProperty("Item");
        Assert.NotNull(indexer);
        object? lazy = indexer!.GetValue(
            partitions,
            [partitionKey]);
        Assert.NotNull(lazy);
        object? partition =
            lazy!.GetType().GetProperty("Value")?.GetValue(lazy);
        Assert.NotNull(partition);

        // The partition's client list is private behind its own gate — three disjoint locks used to
        // mutate a bare List<T> that the status surface enumerated unlocked — so tests go through the
        // same synchronized entry point production code does.
        System.Reflection.MethodInfo? add =
            partition!.GetType().GetMethod("AddClientIfAbsent");
        Assert.NotNull(add);

        _ = add!.Invoke(partition, [client]);
    }

    private sealed class AlwaysTrustedWorkspaceStore : ITrustedMcpWorkspaceStore
    {
        public Task<bool> IsTrustedAsync(string workspaceRootPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> IsTrustedAsync(
            string workspaceRootPath,
            string sourceDigest,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> IsApprovedDigestAsync(
            string workspaceRootPath,
            string sourceDigest,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<TrustedMcpWorkspaceSnapshot> GetSnapshotAsync(
            string workspaceRootPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TrustedMcpWorkspaceSnapshot(null, IsApproved: true));

        public Task TrustAsync(string workspaceRootPath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class MutableOptionsMonitor(ArcanumSettings current)
        : Microsoft.Extensions.Options.IOptionsMonitor<ArcanumSettings>
    {
        public ArcanumSettings CurrentValue { get; set; } = current;

        public ArcanumSettings Get(string? name) => CurrentValue;

        public IDisposable OnChange(
            Action<ArcanumSettings, string?> listener) =>
            new NoopDisposable();
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class RecordingWard : IWard
    {
        public int WardAsyncCount { get; private set; }

        public int AutomaticResolutionCount { get; private set; }

        public WardResolutionOrigin? AutomaticOrigin { get; private set; }

        public Task<WardResolution> WardAsync(
            string wardId,
            string toolName,
            System.Text.Json.JsonDocument? arguments,
            string? sessionId,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            WardAsyncCount++;

            return Task.FromResult(
                new WardResolution(
                    Allowed: false,
                    Reason: "Unexpected interactive Ward invocation.",
                    ResolvedAt: DateTimeOffset.UtcNow,
                    Origin: WardResolutionOrigin.Human));
        }

        public ResolveStatus Resolve(string wardId, bool allow, string? reason) =>
            ResolveStatus.Success;

        public WardResolution RecordAutomaticResolution(
            string wardId,
            bool allowed,
            string? reason,
            WardResolutionOrigin origin)
        {
            AutomaticResolutionCount++;

            AutomaticOrigin = origin;

            return new WardResolution(
                allowed,
                reason,
                DateTimeOffset.UtcNow,
                origin);
        }

        public IReadOnlyList<ActiveWard> GetActiveWards() => [];
    }

    private sealed class BlockingCommittedReceiptSink
        : IApplyPatchPendingReceiptSink
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource HandoffStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ApplyPatchReceiptProbeResult> ProbeAsync(
            ApplyPatchReceiptProbe probe,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                new ApplyPatchReceiptProbeResult(
                    ApplyPatchReceiptProbeOutcome.NotFound,
                    SerializedResult: null));

        public ValueTask<ApplyPatchReceiptPreflightResult> PreflightAsync(
            ApplyPatchReceiptPreflight preflight,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                new ApplyPatchReceiptPreflightResult(
                    ApplyPatchReceiptPreflightOutcome.Admitted,
                    SerializedResult: null));

        public ValueTask<MandatoryToolInteractionAppendOutcome>
            PersistRecoveryReceiptAsync(
                ApplyPatchRecoveryReceipt receipt,
                CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                MandatoryToolInteractionAppendOutcome.NewlyCommitted);

        public async ValueTask<ApplyPatchPendingReceiptHandoffResult>
            HandoffAsync(
                PendingApplyPatchReceipt receipt,
                CancellationToken cancellationToken)
        {
            HandoffStarted.TrySetResult();
            await _release.Task.WaitAsync(
                cancellationToken);
            WorkspaceArtifactCleanupResult cleanup =
                await receipt.MarkIrreversibleAsync(
                    CancellationToken.None);

            return new ApplyPatchPendingReceiptHandoffResult(
                MandatoryToolInteractionAppendOutcome.NewlyCommitted,
                cleanup,
                Rollback: null);
        }

        public void ReleaseHandoff() =>
            _release.TrySetResult();
    }

    private sealed class TrackingMcpClient : IMcpClient
    {
        public int DisposeCount { get; private set; }

        public Task InitializeAsync(
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<McpBridgeTool>> GetToolsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<McpBridgeTool>>([]);

        public Task<ModelContextProtocol.Protocol.CallToolResult>
            CallToolAsync(
                string toolName,
                IReadOnlyDictionary<string, object?> arguments,
                TimeSpan? requestTimeout = null,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SignalOnDisposeMcpClient : IMcpClient
    {
        public TaskCompletionSource DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeCount { get; private set; }

        public Task InitializeAsync(
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<McpBridgeTool>> GetToolsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<McpBridgeTool>>([]);

        public Task<ModelContextProtocol.Protocol.CallToolResult>
            CallToolAsync(
                string toolName,
                IReadOnlyDictionary<string, object?> arguments,
                TimeSpan? requestTimeout = null,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            DisposeCount++;

            DisposeStarted.TrySetResult();

            return ValueTask.CompletedTask;
        }
    }

    private sealed class PermissiveSanctumGuard : ISanctumGuard
    {
        public Task<SanctumResult> ValidatePathAsync(
            string campaignId,
            string requestedPath,
            string operationType,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<SanctumResult> ValidateNetworkAsync(
            string campaignId,
            string url,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<SanctumResult> ValidateToolAsync(string campaignId, string toolName, CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<ResourceLimits> GetEffectiveResourceLimitsForWorkspaceAsync(
            string? workspaceRoot,
            CancellationToken ct = default) =>
            Task.FromResult(new ResourceLimits());

        public Task<SanctumChildProcessBoundary?> GetChildProcessBoundaryForWorkspaceAsync(
            string? workspaceRoot,
            CancellationToken ct = default) =>
            Task.FromResult<SanctumChildProcessBoundary?>(null);

public Task RecordResourceLimitBreachAsync(
            string? workspaceRoot,
            string toolName,
            Core.Platform.ResourceLimitKind resource,
            string limitValue,
            string? actualValue,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeEventBus : IEventBus
    {
        public void Publish<T>(T @event) where T : notnull
        {
        }

        public IAsyncEnumerable<T> Subscribe<T>(CancellationToken cancellationToken) where T : notnull =>
            AsyncEnumerable.Empty<T>();
    }

    /// <summary>
    /// Counts published <see cref="McpServerEvent"/>s per server so a test can prove how many
    /// bootstrap start attempts actually happened, and can park the attempts for one probe server so
    /// a second caller arrives while the shared global bootstrap is still in flight.
    /// </summary>
    private sealed class RecordingEventBus : IEventBus
    {
        private readonly object _gate = new();

        private readonly List<McpServerEvent> _serverEvents = [];

        private string? _parkedServerName;

        private ManualResetEventSlim? _parked;

        public TaskCompletionSource ParkedEventObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ParkEventsFor(string serverName, ManualResetEventSlim parked)
        {
            _parkedServerName = serverName;

            _parked = parked;
        }

        public int CountEventsFor(string serverName)
        {
            lock (_gate)
            {
                return _serverEvents.Count(
                    ev => string.Equals(
                        ev.ServerName,
                        serverName,
                        StringComparison.Ordinal));
            }
        }

        public void Publish<T>(T @event) where T : notnull
        {
            if (@event is not McpServerEvent serverEvent)
            {
                return;
            }

            lock (_gate)
            {
                _serverEvents.Add(serverEvent);
            }

            if (!string.Equals(
                    serverEvent.ServerName,
                    _parkedServerName,
                    StringComparison.Ordinal))
            {
                return;
            }

            _ = ParkedEventObserved.TrySetResult();

            _ = _parked?.Wait(TimeSpan.FromSeconds(30));
        }

        public IAsyncEnumerable<T> Subscribe<T>(CancellationToken cancellationToken) where T : notnull =>
            AsyncEnumerable.Empty<T>();
    }
}
