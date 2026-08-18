using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

/// <summary>
/// Issue #118 — nothing ordinary and durable runs ahead of an active V3 Covenant reset checkpoint.
/// </summary>
/// <remarks>
/// A V3 checkpoint means a reset was interrupted somewhere between canonical erasure and the
/// verified reopen, so the state root it is about to replace or roll back is exactly the tree an
/// ordinary writer would append to. The ordering is enforced in two places and both are asserted
/// here: the descriptor's <see cref="LongRunningOperationStartupPriority.BeforeStateWrites"/> phase
/// inside one reconciliation pass, and the hosted-service registration order that puts the whole
/// pass ahead of every durable workload, optional initializer, worker, and ready-state publication
/// (§10.20.3).
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

    /// <summary>
    /// Within one pass, an active V3 Covenant reset is claimed before any ordinary readiness-phase
    /// operation, even when the ordinary work was discovered first.
    /// </summary>
    [Fact]
    public async Task An_active_v3_checkpoint_is_settled_before_any_ordinary_operation()
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
            checkpointVersion: DataRetentionMutationCheckpointV3.CurrentVersion,
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
            DataRetentionMutationCheckpointV3.CurrentVersion,
            _ =>
            {

                settled.Add(LongRunningOperationKinds.DataRetentionMutation);

                return LongRunningOperationRecoveryResult.Completed();

            });

        LongRunningOperationReconciler reconciler = new(
            store,
            [ordinaryHandler, resetHandler],
            clock,
            NullLogger<LongRunningOperationReconciler>.Instance);

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
    /// A V3 payload is inside the kind's declared window, so the reconciler hands it to the owning
    /// handler rather than stranding it as <c>operation.checkpoint_version_unsupported</c>.
    /// </summary>
    [Fact]
    public void The_v3_window_admits_the_checkpoint_the_reset_writes()
    {

        LongRunningOperationRecoveryDescriptor mutation =
            LongRunningOperationRecoveryRegistry.Descriptors[
                LongRunningOperationKinds.DataRetentionMutation];

        Assert.Equal(0, mutation.MinCheckpointVersion);

        Assert.Equal(DataRetentionMutationCheckpointV3.CurrentVersion, mutation.MaxCheckpointVersion);

        Assert.Equal(
            DataRetentionMutationCheckpointV3.CurrentVersion,
            new DataRetentionMutationRecoveryHandler(null!).SupportedCheckpointVersion);

        LongRunningOperationRecoveryDescriptor factory =
            LongRunningOperationRecoveryRegistry.Descriptors[
                LongRunningOperationKinds.DataRetentionFactoryReset];

        Assert.Equal(0, factory.MinCheckpointVersion);

        Assert.Equal(DataRetentionFactoryResetCheckpointV1.CurrentVersion, factory.MaxCheckpointVersion);

        Assert.Equal(
            DataRetentionFactoryResetCheckpointV1.CurrentVersion,
            new DataRetentionFactoryResetRecoveryHandler(null!).SupportedCheckpointVersion);

    }

    /// <summary>
    /// A factory registration keeps its concrete type in the delegate's own generic arguments, so a
    /// hosted service registered as <c>sp =&gt; sp.GetRequiredService&lt;T&gt;()</c> is still named
    /// rather than reported as unknown and silently skipped by the ordering assertions.
    /// </summary>
    private static string HostedFactoryName(ServiceDescriptor descriptor) =>
        descriptor.ImplementationFactory?.GetType().GenericTypeArguments is [_, Type implementation]
            ? implementation.Name
            : "<unknown>";

}
