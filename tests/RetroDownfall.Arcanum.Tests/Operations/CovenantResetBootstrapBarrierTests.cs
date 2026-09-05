using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

/// <summary>
/// Issue #118 — nothing ordinary and durable runs ahead of an active Covenant reset launch.
/// </summary>
/// <remarks>
/// An offline-transition launch row means a reset was interrupted somewhere between canonical
/// erasure and the verified reopen, so the state root it is about to replace or roll back is
/// exactly the tree an ordinary writer would append to. The ordering is enforced in two places and
/// both are asserted here: the descriptor's
/// <see cref="LongRunningOperationStartupPriority.BeforeStateWrites"/> phase inside one
/// reconciliation pass, and the hosted-service registration order that puts the whole pass ahead of
/// every durable workload, optional initializer, worker, and ready-state publication (§10.20.3).
/// </remarks>
public sealed class CovenantResetBootstrapBarrierTests
{

    /// <summary>
    /// Every hosted workload that may write to the state root, initialize an optional tier, run a
    /// background worker, or publish readiness. Each must be registered after the reconciliation
    /// pass, because hosted services start sequentially in registration order.
    /// </summary>
    private static readonly string[] GatedHostedServices =
    [
        nameof(SessionAttachmentPendingGcHostedService),
        nameof(EntryWeavingService),
        nameof(SessionAttachmentIndexingService),
        nameof(WorkspaceIndexingService),
        nameof(FileEncryptionKeyBootstrapHostedService),
        nameof(DataRetentionSweepHostedService),
    ];

    [Fact]
    public void The_data_retention_mutation_kind_recovers_before_ordinary_state_writes()
    {

        LongRunningOperationRecoveryDescriptor descriptor =
            LongRunningOperationRecoveryRegistry.Descriptors[
                LongRunningOperationKinds.DataRetentionMutation];

        Assert.Equal(LongRunningOperationStartupPriority.BeforeStateWrites, descriptor.StartupPriority);

    }

    /// <summary>
    /// The reconciliation pass is registered after the Grimoire bootstrap that gives it a database
    /// and before every workload that could append to the tree an interrupted reset is replacing.
    /// </summary>
    [Fact]
    public void The_reconciliation_pass_starts_after_the_grimoire_and_before_every_durable_workload()
    {

        IConfiguration configuration = new ConfigurationBuilder().Build();

        ServiceCollection services = [];

        _ = services.AddArcanumApiServices(configuration);

        List<string> hosted =
        [
            .. services
                .Where(static descriptor => descriptor.ServiceType == typeof(IHostedService))
                .Select(static descriptor =>
                    descriptor.ImplementationType?.Name
                    ?? descriptor.ImplementationInstance?.GetType().Name
                    ?? HostedFactoryName(descriptor)),
        ];

        int reconciler = hosted.IndexOf(nameof(LongRunningOperationStartupHostedService));

        Assert.True(reconciler >= 0, "The startup reconciliation pass is not registered at all.");

        int grimoire = hosted.IndexOf(nameof(GrimoireDatabaseHostedService));

        Assert.InRange(grimoire, 0, reconciler - 1);

        foreach (string gated in GatedHostedServices)
        {

            int index = hosted.IndexOf(gated);

            Assert.True(index >= 0, $"{gated} is not registered as a hosted service.");

            Assert.True(
                index > reconciler,
                $"{gated} starts at {index}, ahead of durable-operation recovery at {reconciler}.");

        }

    }

    [Fact]
    public void The_lock_first_startup_graph_is_resolvable_and_the_hosted_alias_uses_the_same_singleton()
    {

        IConfiguration configuration = new ConfigurationBuilder().Build();

        ServiceCollection services = [];

        _ = services.AddArcanumApiServices(configuration);

        using ServiceProvider provider = services.BuildServiceProvider();

        InstallationResetMaintenanceLockAccessor accessor = provider
            .GetRequiredService<InstallationResetMaintenanceLockAccessor>();

        Assert.Same(
            accessor,
            provider.GetRequiredService<IInstallationResetMaintenanceLockAccessor>());

        Assert.IsType<InstallationResetStartupRecovery>(
            provider.GetRequiredService<IInstallationResetStartupRecovery>());

        GrimoireDatabaseHostedService host = provider
            .GetRequiredService<GrimoireDatabaseHostedService>();

        ServiceDescriptor hostedAlias = Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(IHostedService)
                && string.Equals(
                    HostedFactoryName(descriptor),
                    nameof(GrimoireDatabaseHostedService),
                    StringComparison.Ordinal));

        Assert.Same(
            host,
            hostedAlias.ImplementationFactory!(provider));

    }

    [Fact]
    public void The_Covenant_feature_publisher_starts_immediately_after_lock_first_identity_verification()
    {

        IConfiguration configuration = new ConfigurationBuilder().Build();

        ServiceCollection services = [];

        _ = services.AddArcanumApiServices(configuration);

        List<string> hosted =
        [
            .. services
                .Where(static descriptor => descriptor.ServiceType == typeof(IHostedService))
                .Select(static descriptor =>
                    descriptor.ImplementationType?.Name
                    ?? descriptor.ImplementationInstance?.GetType().Name
                    ?? HostedFactoryName(descriptor)),
        ];

        int grimoire = hosted.IndexOf(nameof(GrimoireDatabaseHostedService));

        int publisher = hosted.IndexOf(nameof(CovenantFeatureConfigurationPublisher));

        Assert.True(grimoire >= 0, "The lock-first Grimoire host is not registered.");

        Assert.Equal(grimoire + 1, publisher);

    }

    [Fact]
    public async Task Production_hosted_order_keeps_Covenant_default_closed_when_lock_first_admission_fails()
    {

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Arcanum:Features:Covenant"] = bool.TrueString,
            })
            .Build();

        string testRoot = Path.Combine(
            Path.GetTempPath(),
            "arcanum-tests",
            $"feature-order-{Guid.NewGuid():N}");

        string guardedRoot = Path.Combine(testRoot, "arcanum");

        Directory.CreateDirectory(testRoot);

        try
        {

            ServiceCollection services = [];

            _ = services.AddArcanumApiServices(configuration);

            services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(
                new FixedArcanumSettingsMonitor(
                    new ArcanumSettings
                    {
                        Features = new FeatureSettings
                        {
                            Covenant = true,
                        },
                    }));

            services.AddSingleton(
                sp => new GrimoireDatabaseHostedService(
                    sp.GetRequiredService<IServiceScopeFactory>(),
                    sp.GetRequiredService<ISecretStore>(),
                    sp.GetRequiredService<IGrimoireDbPassphraseSource>(),
                    guardedRoot,
                    new InstallationResetMaintenanceLockAccessor(),
                    new RejectingStartupRecovery()));

            using ServiceProvider provider = services.BuildServiceProvider();

            List<string> relevantOrder =
            [
                .. services
                    .Where(static descriptor => descriptor.ServiceType == typeof(IHostedService))
                    .Select(static descriptor =>
                        descriptor.ImplementationType?.Name
                        ?? descriptor.ImplementationInstance?.GetType().Name
                        ?? HostedFactoryName(descriptor))
                    .Where(static name => name is
                        nameof(GrimoireDatabaseHostedService)
                        or nameof(CovenantFeatureConfigurationPublisher)),
            ];

            Exception? startupFailure = null;

            foreach (string serviceName in relevantOrder)
            {

                try
                {

                    if (serviceName is nameof(GrimoireDatabaseHostedService))
                    {

                        await provider
                            .GetRequiredService<GrimoireDatabaseHostedService>()
                            .StartAsync(CancellationToken.None);

                    }
                    else
                    {

                        await provider
                            .GetRequiredService<CovenantFeatureConfigurationPublisher>()
                            .StartAsync(CancellationToken.None);

                    }

                }
                catch (Exception exception)
                {

                    startupFailure = exception;

                    break;

                }

            }

            Assert.IsType<InvalidOperationException>(startupFailure);

            CovenantAvailability availability = provider
                .GetRequiredService<CovenantAvailability>();

            Assert.False(availability.Current.FeatureEnabled);

        }
        finally
        {

            if (Directory.Exists(testRoot))
            {

                Directory.Delete(testRoot, recursive: true);

            }

        }

    }

    [Fact]
    public void The_pid_file_starts_immediately_after_verified_Covenant_feature_publication()
    {

        IConfiguration configuration = new ConfigurationBuilder().Build();

        ServiceCollection services = [];

        _ = services.AddArcanumApiServices(configuration);

        List<string> hosted =
        [
            .. services
                .Where(static descriptor => descriptor.ServiceType == typeof(IHostedService))
                .Select(static descriptor =>
                    descriptor.ImplementationType?.Name
                    ?? descriptor.ImplementationInstance?.GetType().Name
                    ?? HostedFactoryName(descriptor)),
        ];

        int publisher = hosted.IndexOf(nameof(CovenantFeatureConfigurationPublisher));

        int pidFile = hosted.IndexOf(nameof(PidFileService));

        Assert.True(publisher >= 0, "The verified Covenant feature publisher is not registered.");

        Assert.Equal(publisher + 1, pidFile);

    }

    [Fact]
    public void Every_application_hosted_service_after_pid_is_recovery_aware()
    {

        IConfiguration configuration = new ConfigurationBuilder().Build();

        ServiceCollection services = [];

        _ = services.AddArcanumApiServices(configuration);

        ServiceDescriptor[] hosted =
        [
            .. services.Where(static descriptor =>
                descriptor.ServiceType == typeof(IHostedService)),
        ];

        int pid = Array.FindIndex(hosted, static descriptor =>
            string.Equals(
                descriptor.ImplementationType?.Name
                ?? descriptor.ImplementationInstance?.GetType().Name
                ?? HostedFactoryName(descriptor),
                nameof(PidFileService),
                StringComparison.Ordinal));

        Assert.True(pid >= 0, "The PID hosted service is not registered.");

        foreach (ServiceDescriptor descriptor in hosted[(pid + 1)..])
        {

            Type? implementation = descriptor.ImplementationFactory?
                .GetType()
                .GenericTypeArguments
                .ElementAtOrDefault(1);

            Assert.NotNull(implementation);

            Assert.True(
                implementation!.IsGenericType
                && implementation.GetGenericTypeDefinition()
                    == typeof(InstallationResetRecoveryAwareHostedService<>),
                $"{implementation.Name} is not recovery-aware.");

        }

    }

    [Fact]
    public async Task Recovery_aware_hosted_service_never_starts_or_stops_a_known_writer_in_recovery_mode()
    {

        InstallationResetApiAdmission admission = new();

        admission.PublishRecovery(new ActiveInstallationReset(
            InstallationResetScope.Global,
            WorkspaceRoot: null,
            PlanId: "background-plan",
            OperationId: Guid.Parse("59595959-5959-4959-8959-595959595959"),
            Phase: InstallationResetPhase.Prepared,
            DataHandoff: InstallationResetDataHandoff.HostFactoryErasure,
            OnlineDataCompletionDurable: false));

        RecordingHostedWriter writer = new();

        InstallationResetRecoveryAwareHostedService<RecordingHostedWriter> guarded =
            new(writer, admission);

        await guarded.StartAsync(CancellationToken.None);

        await guarded.StopAsync(CancellationToken.None);

        Assert.Equal(0, writer.StartCalls);

        Assert.Equal(0, writer.StopCalls);

    }

    [Fact]
    public async Task Recovery_aware_hosted_service_preserves_normal_start_and_stop_lifetime()
    {

        RecordingHostedWriter writer = new();

        InstallationResetRecoveryAwareHostedService<RecordingHostedWriter> guarded =
            new(writer, admission: null);

        await guarded.StartAsync(CancellationToken.None);

        await guarded.StopAsync(CancellationToken.None);

        Assert.Equal(1, writer.StartCalls);

        Assert.Equal(1, writer.StopCalls);

    }

    [Fact]
    public async Task Recovery_aware_hosted_service_remains_stoppable_when_container_disposal_precedes_host_stop()
    {

        RecordingHostedWriter writer = new();

        InstallationResetRecoveryAwareHostedService<RecordingHostedWriter> guarded =
            new(writer, admission: null);

        await guarded.StartAsync(CancellationToken.None);

        guarded.Dispose();

        await guarded.StopAsync(CancellationToken.None);

        Assert.Equal(1, writer.StartCalls);

        Assert.Equal(1, writer.StopCalls);

    }

    [Fact]
    public void Serve_defers_every_guarded_root_write_to_the_lock_first_post_topology_lifecycle()
    {

        IReadOnlyList<ProductionSource> sources = ProductionSourceInventory.Sources();

        ProductionSource serve = Assert.Single(
            sources,
            static source => source.IsExactOwner(
                "src/RetroDownfall.Arcanum.Cli/Commands/ServeCommand.cs"));

        int hostStart = serve.Text.IndexOf(
            "await app.StartAsync(",
            StringComparison.Ordinal);

        Assert.True(hostStart >= 0, "Serve no longer starts the composed host.");

        string beforeHostStart = serve.Text[..hostStart];

        Assert.DoesNotContain(
            "ArcanumMasterKeyBootstrapper.EnsureMasterApiKeyExistsAsync(",
            beforeHostStart,
            StringComparison.Ordinal);

        int configuredAction = beforeHostStart.IndexOf(
            "ConfigurePostTopologyStartupAction(() =>",
            StringComparison.Ordinal);

        int deferredRedirect = beforeHostStart.IndexOf(
            "RedirectConsoleToBootstrapLog()",
            StringComparison.Ordinal);

        int deferredAcknowledgement = beforeHostStart.IndexOf(
            "ListenAnySecurityPolicy.PersistAcknowledgement()",
            StringComparison.Ordinal);

        Assert.InRange(configuredAction, 0, deferredRedirect - 1);

        Assert.InRange(deferredRedirect, configuredAction + 1, deferredAcknowledgement - 1);

        ProductionSource bootstrapComposition = Assert.Single(
            sources,
            static source => source.Names(
                "ArcanumMasterKeyBootstrapper.EnsureMasterApiKeyExistsAsync"));

        Assert.True(
            bootstrapComposition.IsExactOwner(
                "src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs"),
            bootstrapComposition.RelativePath);

    }

    /// <summary>
    /// Within one pass, an active Covenant reset launch is claimed before any ordinary
    /// readiness-phase operation, even when the ordinary work was discovered first.
    /// </summary>
    [Fact]
    public async Task An_active_offline_transition_launch_is_settled_before_any_ordinary_operation()
    {

        FakeTimeProvider clock = new();

        FakeLongRunningOperationStore store = new(clock);

        // The ordinary operation is deliberately the older row, so the expiry query returns it
        // first. Without the BeforeStateWrites priority the reconciler would therefore settle it
        // first and this test would fail — the ordering it asserts cannot come from discovery order.
        LongRunningOperation ordinary = store.Seed(
            LongRunningOperationKinds.WorkspaceIndex,
            LongRunningOperationRecoveryPolicy.RestartIdempotently,
            leaseExpiresAt: clock.GetUtcNow().AddMinutes(-5));

        clock.Advance(TimeSpan.FromMinutes(1));

        LongRunningOperation reset = store.Seed(
            LongRunningOperationKinds.DataRetentionMutation,
            LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
            checkpointVersion: CovenantOfflineTransitionLaunchV4.CurrentVersion,
            leaseExpiresAt: clock.GetUtcNow().AddMinutes(-5));

        List<string> settled = [];

        RecordingRecoveryHandler ordinaryHandler = new(
            LongRunningOperationKinds.WorkspaceIndex,
            supportedCheckpointVersion: 0,
            _ =>
            {

                settled.Add(LongRunningOperationKinds.WorkspaceIndex);

                return LongRunningOperationRecoveryResult.Completed();

            });

        RecordingRecoveryHandler resetHandler = new(
            LongRunningOperationKinds.DataRetentionMutation,
            CovenantOfflineTransitionLaunchV4.CurrentVersion,
            _ =>
            {

                settled.Add(LongRunningOperationKinds.DataRetentionMutation);

                return LongRunningOperationRecoveryResult.Completed();

            });

        LongRunningOperationReconciler reconciler = new(
            store,
            [ordinaryHandler, resetHandler],
            clock,
            NullLogger<LongRunningOperationReconciler>.Instance,
            new LongRunningOperationOwnership());

        _ = await reconciler.ReconcileNowAsync("barrier", maxConcurrency: 1);

        Assert.Equal(
            [
                LongRunningOperationKinds.DataRetentionMutation,
                LongRunningOperationKinds.WorkspaceIndex,
            ],
            settled);

        // Discovery order really was the other way round, so the assertion above is about the
        // startup phase rather than about which row the expiry query happened to return.
        Assert.True(ordinary.CreatedAt < reset.CreatedAt);

        Assert.Equal(
            [reset.Id],
            resetHandler.Invocations);

        Assert.Equal(
            [ordinary.Id],
            ordinaryHandler.Invocations);

    }

    /// <summary>
    /// A launch payload is inside its kind's declared window, so the reconciler hands it to the
    /// owning handler rather than stranding it as <c>operation.checkpoint_version_unsupported</c>.
    /// </summary>
    /// <remarks>
    /// The window and the payload shape are raised in two separate files, so a build that moved the
    /// reset to an offline-transition launch without widening the registry would still write rows
    /// the reconciler then refused to admit. That refusal is silent to the writer and permanent to
    /// the row: an interrupted reset that closed admission would sit unrecoverable behind a version
    /// number, which is the one failure this barrier exists to prevent. Asserting both ends against
    /// the launch constants themselves is what keeps a future version bump from splitting them
    /// again — a literal here would let the window drift while the test kept passing.
    /// </remarks>
    [Fact]
    public void The_launch_window_admits_the_binding_the_reset_writes()
    {

        LongRunningOperationRecoveryDescriptor mutation =
            LongRunningOperationRecoveryRegistry.Descriptors[
                LongRunningOperationKinds.DataRetentionMutation];

        Assert.Equal(0, mutation.MinCheckpointVersion);

        Assert.Equal(CovenantOfflineTransitionLaunchV4.CurrentVersion, mutation.MaxCheckpointVersion);

        Assert.Equal(
            CovenantOfflineTransitionLaunchV4.CurrentVersion,
            new DataRetentionMutationRecoveryHandler(null!).SupportedCheckpointVersion);

        LongRunningOperationRecoveryDescriptor factory =
            LongRunningOperationRecoveryRegistry.Descriptors[
                LongRunningOperationKinds.DataRetentionFactoryReset];

        Assert.Equal(0, factory.MinCheckpointVersion);

        Assert.Equal(DataRetentionFactoryTransitionLaunchV2.CurrentVersion, factory.MaxCheckpointVersion);

        Assert.Equal(
            DataRetentionFactoryTransitionLaunchV2.CurrentVersion,
            new DataRetentionFactoryResetRecoveryHandler(null!).SupportedCheckpointVersion);

    }

    /// <summary>
    /// A factory registration keeps its concrete type in the delegate's own generic arguments, so a
    /// hosted service registered as <c>sp =&gt; sp.GetRequiredService&lt;T&gt;()</c> is still named
    /// rather than reported as unknown and silently skipped by the ordering assertions.
    /// </summary>
    private static string HostedFactoryName(ServiceDescriptor descriptor)
    {

        if (descriptor.ImplementationFactory?.GetType().GenericTypeArguments is not [_, Type implementation])
        {

            return "<unknown>";

        }

        return implementation.IsGenericType
            && implementation.GetGenericTypeDefinition()
                == typeof(InstallationResetRecoveryAwareHostedService<>)
            ? implementation.GetGenericArguments()[0].Name
            : implementation.Name;

    }

    private sealed class RejectingStartupRecovery : IInstallationResetStartupRecovery
    {

        public Task<Result<InstallationResetStartupRecoveryState>> RecoverBeforeBootstrapAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<InstallationResetStartupRecoveryState>.Success(
                new InstallationResetStartupRecoveryState(
                    new ActiveInstallationReset(
                        InstallationResetScope.Workspace,
                        WorkspaceRoot: "/blocked",
                        PlanId: "blocked-plan"),
                    ExpectedInstallationId: null,
                    IsLegacyV1: false)));

    }

    private sealed class FixedArcanumSettingsMonitor(
        ArcanumSettings settings)
        : IOptionsMonitor<ArcanumSettings>
    {

        public ArcanumSettings CurrentValue => settings;

        public ArcanumSettings Get(string? name) => settings;

        public IDisposable OnChange(
            Action<ArcanumSettings, string?> listener) =>
            NoopDisposable.Instance;

        private sealed class NoopDisposable : IDisposable
        {

            internal static NoopDisposable Instance { get; } = new();

            public void Dispose()
            {

            }

        }

    }

    private sealed class RecordingHostedWriter : IHostedService
    {

        public int StartCalls { get; private set; }

        public int StopCalls { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {

            StartCalls++;

            return Task.CompletedTask;

        }

        public Task StopAsync(CancellationToken cancellationToken)
        {

            StopCalls++;

            return Task.CompletedTask;

        }

    }

}
